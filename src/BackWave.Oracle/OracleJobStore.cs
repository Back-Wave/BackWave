using System.Collections.Concurrent;
using System.Text.Json;
using BackWave.Core;
using BackWave.Diagnostics;
using BackWave.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Oracle.ManagedDataAccess.Client;
using Oracle.ManagedDataAccess.Types;

namespace BackWave.Oracle;

/// <summary>
/// The Oracle-backed job store. Construct one with a connection string and register it as your
/// BackWave storage; it persists every job, schedule, and workflow in Oracle. Claim contention is
/// resolved inside the database with FOR UPDATE SKIP LOCKED, so many worker processes can share one
/// database safely, and each multi-step state change runs in a single transaction, so a crash partway
/// through never leaves a half-applied result. The database schema must already exist - apply it as
/// part of your deployment, or set the options to create it on first use.
/// </summary>
/// <param name="options">
/// Connection string and behavior settings for the store. The connection string is required; the
/// remaining settings default to safe production values.
/// </param>
/// <example>
/// <code>
/// services.AddBackWave(backwave =&gt;
/// {
///     backwave.UseStore(new OracleJobStore(new OracleStoreOptions
///     {
///         ConnectionString = connectionString,
///     }));
/// });
/// </code>
/// </example>
public sealed class OracleJobStore(OracleStoreOptions options) : IJobStore, IStoreFaultClassifier, IWakeUpHintSource
{
    // The EFFECTIVE Job History Policy: the configured rung with the top one downgraded by the
    // Failure Detail env kill-switch. Resolved once - env is an input to the run.
    private readonly JobHistoryPolicy _historyPolicy = JobHistoryPolicyResolver.Resolve(options.HistoryPolicy);

    // One logger for the whole store, resolved from the configured factory (a no-op logger when none is
    // supplied). Used for the rare Wake-Up Hint channel-fault warning.
    private readonly ILogger _logger =
        options.LoggerFactory?.CreateLogger(OracleDiagnostics.SourceName) ?? NullLogger.Instance;

    private bool _publishFaultLogged;

    // Swaps the canonical 'backwave' schema qualifier for the configured SchemaName in every query
    // and DDL script. The default schema is a zero-cost passthrough.
    private readonly SchemaRewriter _schema = new(options.SchemaName);
    private readonly SemaphoreSlim _readyGate = new(1, 1);
    private bool _ready;

    // The one place an Oracle command is built, so the configured schema is swapped into every query.
    // Every command binds by name (:param), never by position, so a name repeated in the SQL is bound
    // once and reused. Positional (sql, connection[, transaction]) keeps call sites terse.
    private OracleCommand Cmd(string sql, OracleConnection connection, OracleTransaction? transaction = null)
    {
        var command = new OracleCommand(_schema.Rewrite(sql), connection) { BindByName = true };
        if (transaction is not null)
        {
            command.Transaction = transaction;
        }
        return command;
    }

    // Tags-in-use signal. Under the no-tags configuration the job_tags table is empty, so a claim must
    // not pay an unconditional tag-hydration round-trip. Once any Tag is seen - or written on THIS
    // process - the signal latches true and every later claim hydrates; while false, a single cheap
    // EXISTS probe runs at most once per TagsProbeRefreshMs, amortized across every claim in the
    // window. A stale true merely restores the old unconditional round-trip, so the latch never has to
    // be cleared.
    private const long TagsProbeRefreshMs = 5_000;
    private volatile bool _tagsInUse;
    private long _tagsProbeTicks;

    // Per-Queue unlimited-Queue cache. The Concurrency Limit and Paused flag share one queue_limits
    // row, mutated only by rare operator actions; a claim on a limited or paused Queue must lock and
    // read that row, but the common unlimited, unpaused Queue need not pay the round-trip at all. A
    // claim that observes a Queue unlimited AND unpaused stamps it here with the config generation seen
    // BEFORE the read and the wall-clock tick; a later claim skips the round-trip while that stamp's
    // generation is still current AND it is younger than QueueLimitRefreshMs. The generation fence
    // closes a race the row lock cannot: locking a not-yet-existent queue_limits row locks nothing, so
    // it does not serialize against the FIRST pause/limit insert.
    private const long QueueLimitRefreshMs = 5_000;
    private long _queueConfigGeneration;
    private readonly ConcurrentDictionary<string, QueueConfigStamp> _unlimitedQueues = new();

    // One immutable stamp per cached Queue, read as a single atomic reference: the config generation
    // observed before the read, and the tick the stamp was taken.
    private sealed record QueueConfigStamp(long Generation, long Ticks);

    /// <inheritdoc/>
    public bool SupportsTransactionalEnqueue => true;

    /// <inheritdoc/>
    public JobHistoryPolicy HistoryPolicy => _historyPolicy;

    /// <inheritdoc/>
    public StoreBounds Bounds => options.Bounds;

    /// <summary>Migrate (if opted in) and verify the schema version exactly once.</summary>
    private async ValueTask EnsureReadyAsync(CancellationToken cancellationToken)
    {
        if (_ready)
        {
            return;
        }
        await _readyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_ready)
            {
                return;
            }
            if (options.AutoMigrate)
            {
                await OracleMigrator.MigrateAsync(options.ConnectionString, options.SchemaName, options.CoordinateMigration, cancellationToken).ConfigureAwait(false);
                BackWaveLog.MigrationApplied(_logger, "oracle");
            }
            await OracleMigrator.VerifySchemaVersionAsync(options.ConnectionString, options.SchemaName, cancellationToken).ConfigureAwait(false);
            _ready = true;
        }
        finally
        {
            _readyGate.Release();
        }
    }

    private async ValueTask<OracleConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new OracleConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async ValueTask<OracleTransaction> BeginAsync(
        OracleConnection connection, CancellationToken cancellationToken)
        => (OracleTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

    // Test-only failpoint: a no-op in production (hook null), but a test arms OracleStoreOptions.FaultHook
    // to throw at a named point between the effects of a multi-effect operation, proving the surrounding
    // transaction makes them all-or-nothing.
    private Task FailpointAsync(string name, CancellationToken cancellationToken)
        => options.FaultHook?.Invoke(name, cancellationToken) ?? Task.CompletedTask;

    // The effective jobs-table name for the db.collection.name span attribute, honoring a custom
    // SchemaName through the same rewrite choke point every query goes through (identity by default).
    private string JobsCollection => _schema.Rewrite("backwave.jobs");

    // Classifies a store fault for the backwave.store.faults metric tag, mirroring the host's own
    // transient/terminal split: a bare TimeoutException plus the shared connectivity/timeout set and the
    // ORA-00060 deadlock victim are the whole transient set. Emit-only - the host still makes the real
    // retry/fail-stop decision from the rethrown exception.
    private static bool IsTransientStoreFault(Exception exception)
        => exception is TimeoutException
           || (exception is OracleException o
               && (OracleFaultCodes.IsConnectivityFault(o.Number) || o.Number == 60));

    /// <inheritdoc/>
    public bool IsTransientFault(Exception exception) => IsTransientStoreFault(exception);

    // ODP.NET raises ORA-00001 for a unique/primary-key violation - the dialect's duplicate arbiter,
    // used wherever an unlocked NOT EXISTS guard races a concurrent insert of the same key.
    private static bool IsDuplicate(OracleException exception) => exception.Number == 1;

    // ── §5.1 Enqueue ────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async ValueTask<EnqueueResult> EnqueueAsync(
        NewJob job, DateTimeOffset now, System.Data.Common.DbTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        using var activity = OracleDiagnostics.StartStore("enqueue", JobsCollection);
        try
        {
            return await EnqueueUntracedAsync(job, now, transaction, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            OracleDiagnostics.RecordStoreFault(activity, exception, IsTransientStoreFault(exception));
            throw;
        }
    }

    private async ValueTask<EnqueueResult> EnqueueUntracedAsync(
        NewJob job, DateTimeOffset now, System.Data.Common.DbTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        // The parent set is a set: duplicate ids collapse before any rule applies.
        if (job.Parents.Count > 1)
        {
            job = job with { Parents = job.Parents.Distinct().ToArray() };
        }

        if (job.Payload.Length > options.Bounds.MaxPayloadBytes)
        {
            return EnqueueResult.PayloadTooLarge;
        }
        if (job.WireName.Length > options.Bounds.MaxWireNameLength)
        {
            return EnqueueResult.WireNameTooLong;
        }
        if (job.Parents.Count > options.Bounds.MaxParentsPerJob)
        {
            return EnqueueResult.TooManyParents;
        }

        if (transaction is not null)
        {
            // Transactional Enqueue: enlist in the caller's ADO.NET transaction. Their rollback means
            // the job never existed; their commit publishes it atomically.
            if (transaction is not OracleTransaction { Connection: { } callerConnection } oracleTransaction)
            {
                throw new ArgumentException(
                    "The Oracle adapter enlists in OracleTransaction instances only.", nameof(transaction));
            }
            return await EnqueueCoreAsync(callerConnection, oracleTransaction, job, now, cancellationToken)
                .ConfigureAwait(false);
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var ownTransaction = await BeginAsync(connection, cancellationToken).ConfigureAwait(false);
        var result = await EnqueueCoreAsync(connection, ownTransaction, job, now, cancellationToken).ConfigureAwait(false);
        await ownTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async ValueTask<EnqueueResult> EnqueueCoreAsync(
        OracleConnection connection, OracleTransaction transaction, NewJob job, DateTimeOffset now,
        CancellationToken cancellationToken, Guid? workflowId = null)
    {
        // Lock the parents (if any) so a concurrent terminal transition cannot race the latch we are
        // about to record.
        var pendingParents = new List<Guid>();
        var cancelledByParent = (JobState?)null;
        if (job.Parents.Count > 0)
        {
            // Lock the parent rows one at a time in a single deterministic id order - the same order
            // latch resolution locks child sets - so an enqueue and a concurrent terminal outcome over
            // overlapping rows can never deadlock (sorted-id lock ordering).
            var states = new Dictionary<Guid, JobState>();
            var distinctParents = job.Parents.Distinct().ToArray();
            Array.Sort(distinctParents);
            foreach (var parentId in distinctParents)
            {
                await using var parent = Cmd(
                    "SELECT state FROM backwave.jobs WHERE job_id = :id FOR UPDATE", connection, transaction);
                parent.Parameters.Add(Raw("id", parentId));
                await using var reader = (OracleDataReader)await parent.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    states[parentId] = (JobState)reader.GetInt32(0);
                }
            }
            if (states.Count != distinctParents.Length)
            {
                return EnqueueResult.UnknownParent;
            }
            foreach (var parentId in job.Parents)
            {
                var parentState = states[parentId];
                if (!parentState.IsTerminal())
                {
                    pendingParents.Add(parentId);
                }
                else if (job.Mode == DependencyMode.OnSuccess && parentState != JobState.Succeeded)
                {
                    cancelledByParent = parentState;
                }
            }
        }

        var state = cancelledByParent is not null ? JobState.Cancelled
            : pendingParents.Count > 0 ? JobState.AwaitingParent
            : JobState.Scheduled;

        await using var insert = Cmd(
            """
            INSERT INTO backwave.jobs
                (job_id, wire_name, payload, trace_context, queue, state, due_time, parents_remaining, job_mode,
                 terminal_at, terminal_cause, workflow_id)
            SELECT :id, :wire, :payload, :trace, :queue, :state, :due, :remaining, :jobMode, :terminalAt, :terminalCause, :workflowId
            FROM dual
            WHERE NOT EXISTS (SELECT 1 FROM backwave.jobs WHERE job_id = :id)
            """,
            connection, transaction);
        insert.Parameters.Add(Raw("id", job.JobId));
        insert.Parameters.Add(Str("wire", job.WireName));
        insert.Parameters.Add(Blob("payload", job.Payload));
        insert.Parameters.Add(StrN("trace", job.TraceContext));
        insert.Parameters.Add(Str("queue", job.Queue));
        insert.Parameters.Add(Int("state", (int)state));
        insert.Parameters.Add(Tstz("due", job.DueTime));
        insert.Parameters.Add(Int("remaining", pendingParents.Count));
        insert.Parameters.Add(Int("jobMode", (int)job.Mode));
        insert.Parameters.Add(TstzN("terminalAt", cancelledByParent is not null ? now : null));
        insert.Parameters.Add(Clob("terminalCause",
            cancelledByParent is not null ? ParentFailureCause(cancelledByParent.Value) : null));
        // Workflow membership: the immutable scalar, stamped once here at enqueue; null for an ordinary
        // job. The Core never reads it - it lives entirely above the determinism boundary.
        insert.Parameters.Add(RawN("workflowId", workflowId));

        try
        {
            if (await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
            {
                return EnqueueResult.Duplicate;
            }
        }
        catch (OracleException exception) when (IsDuplicate(exception))
        {
            // Two concurrent inserts of the same JobId raced past the NOT EXISTS guard; the primary key
            // is the arbiter (the dialect's ON CONFLICT DO NOTHING).
            return EnqueueResult.Duplicate;
        }

        // Crash between the job row and its parent edges: rollback must leave neither.
        await FailpointAsync("enqueue", cancellationToken).ConfigureAwait(false);

        foreach (var parentId in pendingParents)
        {
            await using var edge = Cmd(
                "INSERT INTO backwave.job_parents (parent_id, child_id) VALUES (:parent, :child)",
                connection, transaction);
            edge.Parameters.Add(Raw("parent", parentId));
            edge.Parameters.Add(Raw("child", job.JobId));
            await edge.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // Job Tags: the enqueue-time set, in this same transaction so they are visible exactly when the
        // job is - and rolled back with it under Transactional Enqueue.
        await InsertTagsAsync(connection, transaction, job.JobId, job.Tags, cancellationToken).ConfigureAwait(false);

        // Transition Log: the actual resulting state - Scheduled, AwaitingParent, or Cancelled - at
        // Attempt 0, in this same transaction (atomic with the job row, even under Transactional Enqueue).
        await RecordTransitionAsync(connection, transaction, job.JobId, state, attempt: 0, now, cancellationToken)
            .ConfigureAwait(false);

        if (state == JobState.Scheduled && job.DueTime <= now)
        {
            // Wake-Up Hint (§8): DBMS_ALERT.SIGNAL is transactional, so the hint fires on commit -
            // including the caller's own commit under Transactional Enqueue - or never.
            await PublishHintAsync(connection, transaction, job.Queue, cancellationToken).ConfigureAwait(false);
        }
        return EnqueueResult.Ok;
    }

    // Fires a Wake-Up Hint on the Queue through DBMS_ALERT within the caller's transaction. A no-op unless
    // EnableWakeUpHints is on; then it needs an EXECUTE grant on SYS.DBMS_ALERT. The alert name is the
    // channel and the Queue rides in the message, mirroring Postgres pg_notify. SIGNAL takes effect only on
    // commit, so a rolled-back enqueue fires no hint (a hint is an optimization, never truth).
    private async ValueTask PublishHintAsync(
        OracleConnection connection, OracleTransaction transaction, string queue, CancellationToken cancellationToken)
    {
        if (!options.EnableWakeUpHints)
        {
            return;
        }
        try
        {
            await using var signal = Cmd("BEGIN DBMS_ALERT.SIGNAL(:name, :msg); END;", connection, transaction);
            signal.Parameters.Add(Str("name", _schema.HintAlertName));
            signal.Parameters.Add(Str("msg", queue));
            await signal.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            if (!_publishFaultLogged)
            {
                _publishFaultLogged = true;
                BackWaveLog.WakeHintChannelUnavailable(_logger, "oracle", exception);
            }
        }
    }
    // ── §5.2 Claim ──────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<JobRecord>> ClaimAsync(
        ClaimRequest request, CancellationToken cancellationToken = default)
    {
        using var activity = OracleDiagnostics.StartStore("claim", JobsCollection);
        try
        {
            var (jobs, _) = await ClaimUntracedAsync(request, computeNextDue: false, cancellationToken).ConfigureAwait(false);
            return jobs;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            OracleDiagnostics.RecordStoreFault(activity, exception, IsTransientStoreFault(exception));
            throw;
        }
    }

    /// <inheritdoc/>
    public async ValueTask<ClaimResult> ClaimBatchAsync(
        ClaimRequest request, CancellationToken cancellationToken = default)
    {
        using var activity = OracleDiagnostics.StartStore("claim", JobsCollection);
        try
        {
            // Idle-poll next-due: computed on the SAME connection right after the per-queue claims commit,
            // so it reads the post-claim committed snapshot. With Wake-Up Hints off (the default) this
            // value is the sole latency mechanism for an idle backed-off fleet on this adapter; with them
            // on, a DBMS_ALERT signal wakes the pump sooner, but this next-due still bounds the no-hint case.
            var (jobs, nextDue) = await ClaimUntracedAsync(request, computeNextDue: true, cancellationToken).ConfigureAwait(false);
            return new ClaimResult(jobs, nextDue);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            OracleDiagnostics.RecordStoreFault(activity, exception, IsTransientStoreFault(exception));
            throw;
        }
    }

    private async ValueTask<(IReadOnlyList<JobRecord> Jobs, DateTimeOffset? NextDue)> ClaimUntracedAsync(
        ClaimRequest request, bool computeNextDue, CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        var maxJobs = Math.Min(request.MaxJobs, options.Bounds.MaxClaimBatch);
        var claimed = new List<JobRecord>();

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        foreach (var queue in request.Queues)
        {
            if (claimed.Count >= maxJobs)
            {
                break;
            }

            await using var transaction = await BeginAsync(connection, cancellationToken).ConfigureAwait(false);

            // Concurrency Limit and Paused flag live in one row: lock it so concurrent claimers of a
            // limited Queue serialize on the slot count and a concurrent Pause is observed atomically. A
            // Queue recently observed unlimited AND unpaused skips this round-trip and the lock entirely -
            // the common case pays nothing; see _unlimitedQueues.
            var slots = int.MaxValue;
            var paused = false;
            int? configured = null;
            if (!IsCachedUnlimited(queue))
            {
                // Serialize claim-vs-first-config on the row lock (best-effort while no row exists yet):
                // an existing queue_limits row is locked FOR UPDATE so a claim and a concurrent limit/pause
                // change serialize. Scoped to the read path: a cached unlimited, unpaused Queue skips this
                // block and pays nothing.
                await AcquireQueueConfigLockAsync(connection, transaction, queue, cancellationToken).ConfigureAwait(false);
                // Capture the generation BEFORE the read so a concurrent operator change (which bumps it)
                // is detected when we go to publish the stamp below.
                var generation = Interlocked.Read(ref _queueConfigGeneration);
                await using (var limit = Cmd(
                    "SELECT max_concurrent, paused FROM backwave.queue_limits WHERE queue = :queue FOR UPDATE",
                    connection, transaction))
                {
                    limit.Parameters.Add(Str("queue", queue));
                    await using var reader = await limit.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                    if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        configured = reader.IsDBNull(0) ? null : reader.GetInt32(0);
                        paused = reader.GetInt32(1) != 0;
                    }
                }
                CacheQueueConfig(queue, configured, paused, generation);
            }
            if (paused)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                continue; // a Paused Queue yields nothing to Claim
            }
            if (configured is { } limitValue)
            {
                await using var leased = Cmd(
                    "SELECT count(*) FROM backwave.jobs WHERE queue = :queue AND state = 2",
                    connection, transaction);
                leased.Parameters.Add(Str("queue", queue));
                var inUse = Convert.ToInt32(await leased.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
                slots = limitValue - inUse;
            }
            if (slots <= 0)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            var take = Math.Min(maxJobs - claimed.Count, slots);

            // The single contended operation. Oracle forbids ROWNUM/FETCH together with FOR UPDATE in one
            // query level, and multi-row RETURNING is awkward, so this is a two-step lock-then-update: the
            // top-N candidate set is chosen in a ROWNUM-bounded inner subquery, then locked FOR UPDATE SKIP
            // LOCKED (the dialect's skip-locked) in the outer select, and finally leased by a plain UPDATE
            // over exactly those locked ids. All in one transaction, so a crash before commit un-leases them.
            var queueClaims = new List<JobRecord>();
            await using (var claim = Cmd(
                $"""
                SELECT {JobColumns} FROM backwave.jobs
                WHERE job_id IN (
                    SELECT job_id FROM (
                        SELECT job_id FROM backwave.jobs
                        WHERE queue = :queue AND state = 0 AND due_time <= :now
                        ORDER BY due_time, sequence
                    ) WHERE ROWNUM <= :take
                )
                ORDER BY due_time, sequence
                FOR UPDATE SKIP LOCKED
                """,
                connection, transaction))
            {
                claim.Parameters.Add(Str("queue", queue));
                claim.Parameters.Add(Tstz("now", request.Now));
                claim.Parameters.Add(Int("take", take));
                await using var reader = (OracleDataReader)await claim.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    queueClaims.Add(ReadJob(reader));
                }
            }

            if (queueClaims.Count > 0)
            {
                var expiry = request.Now + request.LeaseDuration;
                await using (var update = Cmd(
                    $"""
                    UPDATE backwave.jobs
                    SET state = 2, attempt = attempt + 1, lease_owner = :worker, lease_expiry = :expiry
                    WHERE job_id IN ({ParameterList("j", queueClaims.Count)})
                    """,
                    connection, transaction))
                {
                    update.Parameters.Add(Str("worker", request.WorkerId));
                    update.Parameters.Add(Tstz("expiry", expiry));
                    AddIdList(update, "j", [.. queueClaims.Select(j => j.JobId)]);
                    await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
                // The read saw the pre-lease row; reflect the lease in memory to match the committed state.
                queueClaims = [.. queueClaims.Select(j => j with
                {
                    State = JobState.Leased,
                    Attempt = j.Attempt + 1,
                    LeaseOwner = request.WorkerId,
                    LeaseExpiry = expiry,
                })];
            }

            // Crash after the lease write, before commit: rollback must un-lease every row.
            await FailpointAsync("claim", cancellationToken).ConfigureAwait(false);
            // Transition Log: one Leased entry per claimed job at its post-claim Attempt, in this same
            // transaction (atomic with the lease write).
            await RecordTransitionsBatchAsync(
                connection, transaction,
                [.. queueClaims.Select(j => (j.JobId, JobState.Leased, j.Attempt, (string?)null))],
                request.Now, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            // The contract's per-Queue (DueTime, enqueue order) ordering is guaranteed here; never re-sort
            // across Queues - the Dispatch Policy's queue order is already final.
            claimed.AddRange(queueClaims.OrderBy(j => j.DueTime).ThenBy(j => j.Sequence));
        }

        // Tags hydrate in one batched round-trip - but only when tags are actually in use. Under the
        // no-tags configuration the job_tags table is empty, the gate skips the round-trip entirely, and
        // the claim hot path pays nothing. See TagsInUseAsync for the cheap presence signal.
        var tagged = claimed.Count == 0 || !await TagsInUseAsync(connection, cancellationToken).ConfigureAwait(false)
            ? claimed
            : await WithTagsAsync(connection, claimed, cancellationToken).ConfigureAwait(false);
        var nextDue = computeNextDue
            ? await NextDueAsync(connection, request, cancellationToken).ConfigureAwait(false)
            : null;
        return (tagged, nextDue);
    }

    // The earliest future instant a currently-empty claim could begin returning work through time alone,
    // for idle-poll backoff. Read on the connection the per-queue claims just committed on. A served,
    // non-paused queue that still holds a due-now Scheduled job reports Now; otherwise the earliest future
    // Scheduled due time across served, non-paused queues, or null when none is scheduled. Advisory only.
    private async ValueTask<DateTimeOffset?> NextDueAsync(
        OracleConnection connection, ClaimRequest request, CancellationToken cancellationToken)
    {
        if (request.Queues.Count == 0)
        {
            return null;
        }
        var queueParams = string.Join(", ", request.Queues.Select((_, i) => $":q{i}"));
        await using var cmd = Cmd(
            $"""
            SELECT j.due_time
            FROM backwave.jobs j
            LEFT JOIN backwave.queue_limits ql ON ql.queue = j.queue
            WHERE j.state = 0 AND j.queue IN ({queueParams}) AND NVL(ql.paused, 0) = 0
            ORDER BY j.due_time
            FETCH FIRST 1 ROW ONLY
            """,
            connection);
        for (var i = 0; i < request.Queues.Count; i++)
        {
            cmd.Parameters.Add(Str($"q{i}", request.Queues[i]));
        }
        await using var reader = (OracleDataReader)await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null; // nothing scheduled in any served, non-paused queue
        }
        var earliest = ReadTstz(reader, 0);
        // Due now but withheld (concurrency limit or batch cap): clamp to Now so the poller does not back off.
        return earliest <= request.Now ? request.Now : earliest;
    }

    // Reports whether any Tag is in use, gating the post-commit hydration round-trip so the no-tags
    // configuration pays nothing. The signal latches true the first time a Tag is seen or written; while
    // false it runs one EXISTS probe at most once per TagsProbeRefreshMs. A Tag first written on ANOTHER
    // process is reflected within TagsProbeRefreshMs; a Tag written on THIS process latches at once.
    private async ValueTask<bool> TagsInUseAsync(OracleConnection connection, CancellationToken cancellationToken)
    {
        if (_tagsInUse)
        {
            return true;
        }
        var lastProbe = Interlocked.Read(ref _tagsProbeTicks);
        if (lastProbe != 0 && Environment.TickCount64 - lastProbe < TagsProbeRefreshMs)
        {
            return false; // recently probed empty - skip the round-trip
        }
        Interlocked.Exchange(ref _tagsProbeTicks, Environment.TickCount64);
        await using var probe = Cmd("SELECT 1 FROM backwave.job_tags FETCH FIRST 1 ROW ONLY", connection);
        var present = await probe.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
        if (present)
        {
            _tagsInUse = true;
        }
        return present;
    }

    // True when a claim recently observed this Queue unlimited AND unpaused under the CURRENT config
    // generation, so the queue_limits round-trip + row lock may be skipped. A stamp from a superseded
    // generation, an aged-out stamp, or no stamp all fall through to a fresh read.
    private bool IsCachedUnlimited(string queue)
        => _unlimitedQueues.TryGetValue(queue, out var stamp)
           && stamp.Generation == Interlocked.Read(ref _queueConfigGeneration)
           && Environment.TickCount64 - stamp.Ticks < QueueLimitRefreshMs;

    // Publishes an "unlimited, unpaused" stamp tagged with the generation observed before the read; a
    // limited or paused Queue is simply not stamped, so it keeps re-reading under lock. If an operator
    // change committed on THIS process since that generation was captured, the stamp is born stale and
    // IsCachedUnlimited ignores it - no removal, hence no removal race.
    private void CacheQueueConfig(string queue, int? configured, bool paused, long observedGeneration)
    {
        if (configured is null && !paused)
        {
            _unlimitedQueues[queue] = new QueueConfigStamp(observedGeneration, Environment.TickCount64);
        }
    }

    // An operator pause/resume or limit change on THIS process bumps the config generation, immediately
    // staling every unlimited stamp - including one an in-flight claim publishes afterward from the old
    // state - so the next claim re-reads the row under lock.
    private void InvalidateQueueConfig() => Interlocked.Increment(ref _queueConfigGeneration);

    // Best-effort serialization of claim-vs-config on the queue_limits row. An existing row is locked FOR
    // UPDATE so a claim and a concurrent limit/pause change serialize; while no row exists yet there is
    // nothing to lock, so the generation fence in the cache is the backstop. Drains the reader to release
    // the cursor while keeping the lock to end of transaction.
    private async Task AcquireQueueConfigLockAsync(
        OracleConnection connection, OracleTransaction transaction, string queue, CancellationToken cancellationToken)
    {
        // FOR UPDATE can only lock a row that exists, so a claim and a first-ever pause/limit on the same
        // queue would otherwise lock nothing and race. Materialize a per-queue anchor in the dedicated
        // queue_locks table (a concurrent insert loses the primary key and is a benign no-op) so both
        // paths converge on a single lockable row - without seeding a phantom into operator-owned
        // queue_limits, whose listing must show only queues an operator actually touched.
        await using (var ensure = Cmd(
            """
            INSERT INTO backwave.queue_locks (queue)
            SELECT :queue FROM dual
            WHERE NOT EXISTS (SELECT 1 FROM backwave.queue_locks WHERE queue = :queue)
            """,
            connection, transaction))
        {
            ensure.Parameters.Add(Str("queue", queue));
            try
            {
                await ensure.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OracleException exception) when (IsDuplicate(exception))
            {
                // Another party inserted the anchor first; the FOR UPDATE below locks their row.
            }
        }

        await using var applock = Cmd(
            "SELECT queue FROM backwave.queue_locks WHERE queue = :queue FOR UPDATE", connection, transaction);
        applock.Parameters.Add(Str("queue", queue));
        await using var reader = await applock.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
        }
    }
    // ── §5.6 ReportOutcome ──────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async ValueTask<OutcomeResult> ReportOutcomeAsync(
        Guid jobId, string workerId, int attempt, JobOutcome outcome, DateTimeOffset now,
        string? failureDetail = null,
        JobTags? addedTags = null,
        ReadOnlyMemory<byte>? output = null,
        CancellationToken cancellationToken = default)
    {
        var operation = outcome switch
        {
            JobOutcome.Success => "complete",
            JobOutcome.Failure => "fail",
            _ => "report_outcome",
        };
        using var activity = OracleDiagnostics.StartStore(operation, JobsCollection);
        try
        {
            return await ReportOutcomeUntracedAsync(
                jobId, workerId, attempt, outcome, now, failureDetail, addedTags, output, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            OracleDiagnostics.RecordStoreFault(activity, exception, IsTransientStoreFault(exception));
            throw;
        }
    }

    private async ValueTask<OutcomeResult> ReportOutcomeUntracedAsync(
        Guid jobId, string workerId, int attempt, JobOutcome outcome, DateTimeOffset now,
        string? failureDetail = null,
        JobTags? addedTags = null,
        ReadOnlyMemory<byte>? output = null,
        CancellationToken cancellationToken = default)
    {
        // Job Output rides the same fence but persists ONLY on a Success outcome and is independent of Job
        // History Policy. Over MaxOutputBytes it is REJECTED loudly, never truncated. The check precedes
        // any write, so an over-limit write leaves the store untouched (Effect-Once). On a fenced-out
        // outcome the SET clause never runs, so the buffered blob is discarded with the rest of the write.
        var writeOutput = outcome is JobOutcome.Success && output is not null;
        if (writeOutput && output!.Value.Length > options.Bounds.MaxOutputBytes)
        {
            throw new JobOutputTooLargeException(jobId, output.Value.Length, options.Bounds.MaxOutputBytes);
        }
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        var (sql, configure, newStateValue) = outcome switch
        {
            JobOutcome.Success =>
                ("state = 3, lease_owner = NULL, lease_expiry = NULL, terminal_at = :now"
                 + (writeOutput ? ", output = :output" : string.Empty),
                    (Action<OracleCommand>)(command =>
                    {
                        if (writeOutput)
                        {
                            command.Parameters.Add(Blob("output", output!.Value));
                        }
                    }), 3),
            JobOutcome.Failure { NextDueTime: { } retryAt } =>
                ("state = 0, due_time = :retryAt, lease_owner = NULL, lease_expiry = NULL",
                    command => command.Parameters.Add(Tstz("retryAt", retryAt)), 0),
            JobOutcome.Failure failure =>
                ("state = 5, lease_owner = NULL, lease_expiry = NULL, terminal_at = :now, terminal_cause = :cause",
                    command => command.Parameters.Add(Clob("cause", failure.Error)), 5),
            JobOutcome.Cancelled cancelled =>
                ("state = 4, lease_owner = NULL, lease_expiry = NULL, cancel_requested = 0, " +
                 "terminal_at = :now, terminal_cause = :cause",
                    command => command.Parameters.Add(Clob("cause", cancelled.Cause)), 4),
            JobOutcome.Unroutable unroutable =>
                ("state = 6, lease_owner = NULL, lease_expiry = NULL, terminal_at = :now, terminal_cause = :cause",
                    command => command.Parameters.Add(Clob("cause", unroutable.Reason)), 6),
            _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
        };

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await BeginAsync(connection, cancellationToken).ConfigureAwait(false);

        // Fenced UPDATE: Oracle returns no OUTPUT, but the target state is already known, so the affected
        // rowcount is the whole verdict - 0 means the (workerId, attempt, live-lease) fence failed.
        await using var update = Cmd(
            $"""
            UPDATE backwave.jobs SET {sql}
            WHERE job_id = :id AND state = 2 AND lease_owner = :worker AND attempt = :attempt
              AND lease_expiry > :now
            """,
            connection, transaction);
        update.Parameters.Add(Raw("id", jobId));
        update.Parameters.Add(Str("worker", workerId));
        update.Parameters.Add(Int("attempt", attempt));
        update.Parameters.Add(Tstz("now", now));
        configure(update);

        if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return OutcomeResult.StaleLease; // the (workerId, attempt) fence
        }
        var newState = (JobState)newStateValue;

        // Job Tags delta: the runtime Tags the handler buffered ride the SAME fenced transaction - applied
        // only because the fence held. Effect-Once; set semantics make re-adding an identical Tag a no-op.
        if (addedTags is { Count: > 0 })
        {
            await InsertTagsAsync(connection, transaction, jobId, addedTags, cancellationToken).ConfigureAwait(false);
        }

        // Transition Log: the resulting state at this Attempt, atomic with the outcome write. Failure
        // Detail rides only the failing transition; every other outcome records null.
        await RecordTransitionAsync(
            connection, transaction, jobId, newState, attempt, now, cancellationToken,
            failureDetail: outcome is JobOutcome.Failure ? failureDetail : null)
            .ConfigureAwait(false);

        if (newState.IsTerminal())
        {
            // Crash after the terminal write, before the latch cascade: rollback must leave the parent
            // non-terminal and every child latch un-decremented.
            await FailpointAsync("report-outcome", cancellationToken).ConfigureAwait(false);
            await ResolveChildLatchesAsync(connection, transaction, jobId, newState, now, cancellationToken)
                .ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return OutcomeResult.Applied;
    }

    // ── §5.6b ReportOutcomes (batch) ─────────────────────────────────────────────

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<OutcomeReportResult>> ReportOutcomesAsync(
        IReadOnlyList<OutcomeReport> batch, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        using var activity = OracleDiagnostics.StartStore("report_outcomes", JobsCollection);
        try
        {
            return await ReportOutcomesUntracedAsync(batch, now, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            OracleDiagnostics.RecordStoreFault(activity, exception, IsTransientStoreFault(exception));
            throw;
        }
    }

    private async ValueTask<IReadOnlyList<OutcomeReportResult>> ReportOutcomesUntracedAsync(
        IReadOnlyList<OutcomeReport> batch, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        // Empty batch applies nothing - no transaction needed.
        if (batch.Count == 0)
        {
            return [];
        }

        // Job Output: over MaxOutputBytes is REJECTED loudly, never truncated. The check spans the WHOLE
        // batch and precedes ANY write, so an over-limit row leaves the store untouched (Effect-Once).
        foreach (var row in batch)
        {
            if (row.Outcome is JobOutcome.Success && row.Output is { } blob
                && blob.Length > options.Bounds.MaxOutputBytes)
            {
                throw new JobOutputTooLargeException(row.JobId, blob.Length, options.Bounds.MaxOutputBytes);
            }
        }

        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await BeginAsync(connection, cancellationToken).ConfigureAwait(false);

        // Oracle has no OPENJSON, so the batch is applied as a per-row fenced UPDATE loop inside ONE
        // transaction: each row carries its computed target state and per-state columns, and the WHERE
        // applies the per-(worker, attempt, live-lease) Effect-Once fence to that row alone. A row whose
        // lease is no longer live changes nothing (StaleLease); a matched row (rowcount > 0) is recorded
        // by job id. due_time moves only for a retry row (COALESCE keeps it otherwise); cancel_requested
        // clears only for a Cancelled row (CASE); terminal_at/terminal_cause carry per-row (null for retry).
        var matched = new Dictionary<Guid, int>();
        foreach (var report in batch)
        {
            // Success -> Succeeded (3, terminal now); Failure with a retry instant -> Scheduled (0, due
            // then, NOT terminal); Failure at the ceiling -> Dead-Lettered (5); Cancelled -> 4; Unroutable
            // -> Quarantined (6). The cause rides terminal failures/cancel/unroutable; due rides retry.
            (int State, string? Cause, DateTimeOffset? Due, DateTimeOffset? TerminalAt) target = report.Outcome switch
            {
                JobOutcome.Success => (3, null, null, now),
                JobOutcome.Failure { NextDueTime: { } retryAt } => (0, null, retryAt, null),
                JobOutcome.Failure failure => (5, failure.Error, null, now),
                JobOutcome.Cancelled cancelled => (4, cancelled.Cause, null, now),
                JobOutcome.Unroutable unroutable => (6, unroutable.Reason, null, now),
                _ => throw new ArgumentOutOfRangeException(nameof(batch)),
            };
            await using var update = Cmd(
                """
                UPDATE backwave.jobs
                SET state = :state,
                    lease_owner = NULL,
                    lease_expiry = NULL,
                    terminal_at = :terminalAt,
                    terminal_cause = :cause,
                    due_time = COALESCE(:due, due_time),
                    cancel_requested = CASE WHEN :state = 4 THEN 0 ELSE cancel_requested END
                WHERE job_id = :id AND state = 2 AND lease_owner = :worker AND attempt = :attempt
                  AND lease_expiry > :now
                """,
                connection, transaction);
            update.Parameters.Add(Raw("id", report.JobId));
            update.Parameters.Add(Str("worker", report.WorkerId));
            update.Parameters.Add(Int("attempt", report.Attempt));
            update.Parameters.Add(Int("state", target.State));
            update.Parameters.Add(Clob("cause", target.Cause));
            update.Parameters.Add(TstzN("due", target.Due));
            update.Parameters.Add(TstzN("terminalAt", target.TerminalAt));
            update.Parameters.Add(Tstz("now", now));
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0)
            {
                matched[report.JobId] = target.State;
            }
        }

        // Output and Tag deltas land ONLY for matched rows. Output persists only on a Success outcome;
        // Tags union onto the job's existing tags (set semantics). Both ride this same fenced transaction.
        foreach (var row in batch)
        {
            if (!matched.ContainsKey(row.JobId))
            {
                continue;
            }
            if (row.Outcome is JobOutcome.Success && row.Output is { } blob)
            {
                await using var setOutput = Cmd(
                    "UPDATE backwave.jobs SET output = :output WHERE job_id = :id", connection, transaction);
                setOutput.Parameters.Add(Raw("id", row.JobId));
                setOutput.Parameters.Add(Blob("output", blob));
                await setOutput.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            if (row.AddedTags is { Count: > 0 } addedTags)
            {
                await InsertTagsAsync(connection, transaction, row.JobId, addedTags, cancellationToken).ConfigureAwait(false);
            }
        }

        // Transition Log: one entry per matched row for its resulting state at this Attempt. Failure
        // Detail rides only a failing transition; every other outcome records null. Honors the history
        // policy (Off appends nothing), so the noop-drain hot path adds no transition statements.
        var transitionRows = new List<(Guid JobId, JobState State, int Attempt, string? FailureDetail)>(matched.Count);
        foreach (var row in batch)
        {
            if (matched.TryGetValue(row.JobId, out var newState))
            {
                transitionRows.Add((row.JobId, (JobState)newState, row.Attempt,
                    row.Outcome is JobOutcome.Failure ? row.FailureDetail : null));
            }
        }
        await RecordTransitionsBatchAsync(connection, transaction, transitionRows, now, cancellationToken)
            .ConfigureAwait(false);

        // First-level child-latch resolution for the matched TERMINAL ids only (a retry row gates nothing).
        var terminalIds = new List<Guid>();
        foreach (var (jobId, state) in matched)
        {
            if (((JobState)state).IsTerminal())
            {
                terminalIds.Add(jobId);
            }
        }
        if (terminalIds.Count > 0)
        {
            // Crash after the terminal write, before the latch cascade: rollback must leave every parent
            // non-terminal and every child latch un-resolved. A separate statement so the failpoint seam
            // survives at BATCH granularity.
            await FailpointAsync("report-outcome", cancellationToken).ConfigureAwait(false);

            var parents = new List<Guid>();
            await using (var withChildren = Cmd(
                $"SELECT DISTINCT parent_id FROM backwave.job_parents WHERE parent_id IN ({ParameterList("p", terminalIds.Count)})",
                connection, transaction))
            {
                AddIdList(withChildren, "p", terminalIds);
                await using var reader = (OracleDataReader)await withChildren.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    parents.Add(ReadGuid(reader, 0));
                }
            }
            parents.Sort(); // deterministic lock order, as everywhere else
            foreach (var parentId in parents)
            {
                await ResolveChildLatchesAsync(
                    connection, transaction, parentId, (JobState)matched[parentId], now, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        // One result per input row, in input order, keyed by job id: matched => Applied, else StaleLease.
        var count = batch.Count;
        var results = new OutcomeReportResult[count];
        for (var i = 0; i < count; i++)
        {
            results[i] = new OutcomeReportResult(
                batch[i].JobId,
                matched.ContainsKey(batch[i].JobId) ? OutcomeResult.Applied : OutcomeResult.StaleLease);
        }
        return results;
    }

    /// <summary>
    /// The latch, inside the same transaction as the terminal transition. Deleting the edge claims it:
    /// each parent-child edge resolves exactly once.
    /// </summary>
    private async Task ResolveChildLatchesAsync(
        OracleConnection connection, OracleTransaction transaction,
        Guid parentId, JobState parentState, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var work = new Stack<(Guid ParentId, JobState ParentState)>();
        work.Push((parentId, parentState));

        while (work.Count > 0)
        {
            var (currentParent, currentState) = work.Pop();

            // Oracle has no DELETE ... RETURNING for a row set, so read the edges then delete them. The
            // parent row is already locked by this transaction's terminal write, so no concurrent enqueue
            // can add an edge in between.
            var children = new List<Guid>();
            await using (var edges = Cmd(
                "SELECT child_id FROM backwave.job_parents WHERE parent_id = :parent", connection, transaction))
            {
                edges.Parameters.Add(Raw("parent", currentParent));
                await using var reader = (OracleDataReader)await edges.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    children.Add(ReadGuid(reader, 0));
                }
            }
            await using (var delete = Cmd(
                "DELETE FROM backwave.job_parents WHERE parent_id = :parent", connection, transaction))
            {
                delete.Parameters.Add(Raw("parent", currentParent));
                await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            // Lock the child rows in a single deterministic id order - the same order the enqueue path
            // locks parent sets - so two transactions over overlapping rows can never deadlock.
            children.Sort();

            foreach (var childId in children)
            {
                int childState, remaining, mode, childAttempt;
                await using (var child = Cmd(
                    "SELECT state, parents_remaining, job_mode, attempt FROM backwave.jobs WHERE job_id = :id FOR UPDATE",
                    connection, transaction))
                {
                    child.Parameters.Add(Raw("id", childId));
                    await using var reader = await child.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                    if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        continue;
                    }
                    (childState, remaining, mode, childAttempt) =
                        (reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3));
                }
                if ((JobState)childState != JobState.AwaitingParent)
                {
                    continue; // already cancelled via another failed parent
                }

                if ((DependencyMode)mode == DependencyMode.OnSuccess && currentState != JobState.Succeeded)
                {
                    await using var cancel = Cmd(
                        """
                        UPDATE backwave.jobs
                        SET state = 4, parents_remaining = 0, terminal_at = :now, terminal_cause = :cause
                        WHERE job_id = :id
                        """,
                        connection, transaction);
                    cancel.Parameters.Add(Raw("id", childId));
                    cancel.Parameters.Add(Tstz("now", now));
                    cancel.Parameters.Add(Clob("cause", ParentFailureCause(currentState)));
                    await cancel.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    await RecordTransitionAsync(connection, transaction, childId, JobState.Cancelled, childAttempt, now, cancellationToken)
                        .ConfigureAwait(false);
                    work.Push((childId, JobState.Cancelled)); // cascade
                    continue;
                }

                await using var resolve = Cmd(
                    remaining - 1 > 0
                        ? "UPDATE backwave.jobs SET parents_remaining = parents_remaining - 1 WHERE job_id = :id"
                        : """
                          UPDATE backwave.jobs
                          SET state = 0, parents_remaining = 0,
                              due_time = CASE WHEN due_time > :now THEN due_time ELSE :now END
                          WHERE job_id = :id
                          """,
                    connection, transaction);
                resolve.Parameters.Add(Raw("id", childId));
                if (remaining - 1 <= 0)
                {
                    resolve.Parameters.Add(Tstz("now", now));
                }
                await resolve.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                // Only the latch RELEASE (last parent terminal -> Scheduled) is a state change worth a
                // transition; a mere decrement keeps the child in AwaitingParent.
                if (remaining - 1 <= 0)
                {
                    await RecordTransitionAsync(connection, transaction, childId, JobState.Scheduled, childAttempt, now, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }
    }

    private static string ParentFailureCause(JobState parentState) => $"parent-failure:{parentState}";

    // ── §5.4 Heartbeat ──────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<HeartbeatResult>> HeartbeatAsync(
        string workerId, IReadOnlyList<Guid> jobIds, TimeSpan leaseDuration, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        if (jobIds.Count == 0)
        {
            return [];
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await BeginAsync(connection, cancellationToken).ConfigureAwait(false);

        // Oracle returns no OUTPUT, so lock the still-live leases this worker holds, capture each job's
        // cancel_requested flag, then extend exactly those in one UPDATE. A job whose lease lapsed or was
        // stolen simply fails the fence and is reported not renewed.
        var renewed = new Dictionary<Guid, bool>();
        await using (var select = Cmd(
            $"""
            SELECT job_id, cancel_requested FROM backwave.jobs
            WHERE job_id IN ({ParameterList("p", jobIds.Count)})
              AND state = 2 AND lease_owner = :worker AND lease_expiry > :now
            FOR UPDATE
            """,
            connection, transaction))
        {
            AddIdList(select, "p", jobIds);
            select.Parameters.Add(Str("worker", workerId));
            select.Parameters.Add(Tstz("now", now));
            await using var reader = (OracleDataReader)await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                renewed[ReadGuid(reader, 0)] = reader.GetInt32(1) != 0;
            }
        }

        if (renewed.Count > 0)
        {
            await using var extend = Cmd(
                $"UPDATE backwave.jobs SET lease_expiry = :expiry WHERE job_id IN ({ParameterList("r", renewed.Count)})",
                connection, transaction);
            extend.Parameters.Add(Tstz("expiry", now + leaseDuration));
            AddIdList(extend, "r", [.. renewed.Keys]);
            await extend.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return [.. jobIds.Select(id => renewed.TryGetValue(id, out var cancelRequested)
            ? new HeartbeatResult(id, Renewed: true, cancelRequested)
            : new HeartbeatResult(id, Renewed: false, CancelRequested: false))];
    }

    // ── §5.5 ExpireLeases ───────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async ValueTask<int> ExpireLeasesAsync(
        DateTimeOffset now, int maxJobs, IReadOnlyList<string> queues, RetryDisposition disposition,
        CancellationToken cancellationToken = default)
    {
        using var activity = OracleDiagnostics.StartStore("expire_leases", JobsCollection);
        try
        {
            return await ExpireLeasesUntracedAsync(now, maxJobs, queues, disposition, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            OracleDiagnostics.RecordStoreFault(activity, exception, IsTransientStoreFault(exception));
            throw;
        }
    }

    private async ValueTask<int> ExpireLeasesUntracedAsync(
        DateTimeOffset now, int maxJobs, IReadOnlyList<string> queues, RetryDisposition disposition,
        CancellationToken cancellationToken = default)
    {
        if (queues.Count == 0)
        {
            return 0;
        }

        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await BeginAsync(connection, cancellationToken).ConfigureAwait(false);

        // FOR UPDATE SKIP LOCKED makes concurrent sweeps dispose disjoint sets: exactly-once disposal.
        // Scoped to the caller's served Queues so each group applies its own policy. Oracle forbids
        // ROWNUM together with FOR UPDATE at one query level, so the top-N candidate set is bounded in an
        // inner subquery and locked in the outer select.
        var queueParams = string.Join(", ", queues.Select((_, i) => $":q{i}"));
        var expired = new List<(Guid JobId, int Attempt)>();
        await using (var select = Cmd(
            $"""
            SELECT job_id, attempt FROM backwave.jobs
            WHERE job_id IN (
                SELECT job_id FROM (
                    SELECT job_id FROM backwave.jobs
                    WHERE state = 2 AND lease_expiry <= :now AND queue IN ({queueParams})
                    ORDER BY lease_expiry
                ) WHERE ROWNUM <= :max
            )
            ORDER BY lease_expiry
            FOR UPDATE SKIP LOCKED
            """,
            connection, transaction))
        {
            select.Parameters.Add(Tstz("now", now));
            select.Parameters.Add(Int("max", maxJobs));
            for (var i = 0; i < queues.Count; i++)
            {
                select.Parameters.Add(Str($"q{i}", queues[i]));
            }
            await using var reader = (OracleDataReader)await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                expired.Add((ReadGuid(reader, 0), reader.GetInt32(1)));
            }
        }

        // Partition by the disposition (pure data): retry at a backoff instant, or dead-letter at the
        // ceiling. The claim already counted the Attempt, so expiry just disposes it.
        var retries = new List<(Guid JobId, DateTimeOffset Due)>();
        var deadLettered = new List<(Guid JobId, string Cause)>();
        foreach (var (jobId, attempt) in expired)
        {
            if (disposition.NextAttemptAt(attempt, now) is { } retryAt)
            {
                retries.Add((jobId, retryAt));
            }
            else
            {
                deadLettered.Add((jobId, $"Lease expired on attempt {attempt} (attempt ceiling reached)."));
            }
        }

        // Oracle has no VALUES table constructor, so each disposition is a bounded per-row UPDATE loop
        // inside this one transaction (maxJobs caps the count).
        foreach (var (jobId, due) in retries)
        {
            await using var reschedule = Cmd(
                "UPDATE backwave.jobs SET state = 0, due_time = :due, lease_owner = NULL, lease_expiry = NULL WHERE job_id = :id",
                connection, transaction);
            reschedule.Parameters.Add(Raw("id", jobId));
            reschedule.Parameters.Add(Tstz("due", due));
            await reschedule.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (deadLettered.Count > 0)
        {
            foreach (var (jobId, cause) in deadLettered)
            {
                await using var deadLetter = Cmd(
                    """
                    UPDATE backwave.jobs
                    SET state = 5, lease_owner = NULL, lease_expiry = NULL, terminal_at = :now, terminal_cause = :cause
                    WHERE job_id = :id
                    """,
                    connection, transaction);
                deadLetter.Parameters.Add(Raw("id", jobId));
                deadLetter.Parameters.Add(Tstz("now", now));
                deadLetter.Parameters.Add(Clob("cause", cause));
                await deadLetter.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            // Crash after the dead-letter write, before the latch cascade: rollback must leave the parent
            // leased and every child latch un-resolved.
            await FailpointAsync("lease-expiry", cancellationToken).ConfigureAwait(false);

            // Latch resolution touches only dead-lettered jobs that actually parent a Dependency - one
            // lookup for the whole set, then cascade just those. The common no-children sweep adds zero
            // per-job statements.
            var deadIds = deadLettered.Select(d => d.JobId).ToList();
            var parents = new List<Guid>();
            await using (var withChildren = Cmd(
                $"SELECT DISTINCT parent_id FROM backwave.job_parents WHERE parent_id IN ({ParameterList("p", deadIds.Count)})",
                connection, transaction))
            {
                AddIdList(withChildren, "p", deadIds);
                await using var reader = (OracleDataReader)await withChildren.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    parents.Add(ReadGuid(reader, 0));
                }
            }
            parents.Sort(); // deterministic lock order, as everywhere else
            foreach (var parentId in parents)
            {
                await ResolveChildLatchesAsync(connection, transaction, parentId, JobState.DeadLettered, now, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        // Transition Log: one entry per expired job for its resulting state - Scheduled (rescheduled) or
        // DeadLettered (ceiling) - at its post-claim Attempt, atomic with the disposition writes.
        foreach (var (jobId, attempt) in expired)
        {
            var resulting = disposition.NextAttemptAt(attempt, now) is not null
                ? JobState.Scheduled
                : JobState.DeadLettered;
            await RecordTransitionAsync(connection, transaction, jobId, resulting, attempt, now, cancellationToken)
                .ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return expired.Count;
    }

    // ── §5.8 Cancel ─────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async ValueTask<CancelResult> CancelJobAsync(
        Guid jobId, string actor, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await BeginAsync(connection, cancellationToken).ConfigureAwait(false);

        int state, attempt;
        await using (var current = Cmd(
            "SELECT state, attempt FROM backwave.jobs WHERE job_id = :id FOR UPDATE", connection, transaction))
        {
            current.Parameters.Add(Raw("id", jobId));
            await using var reader = await current.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return CancelResult.NotCancellable;
            }
            (state, attempt) = (reader.GetInt32(0), reader.GetInt32(1));
        }

        switch ((JobState)state)
        {
            case JobState.Scheduled or JobState.AwaitingParent:
                await using (var cancel = Cmd(
                    "UPDATE backwave.jobs SET state = 4, terminal_at = :now, terminal_cause = :actor WHERE job_id = :id",
                    connection, transaction))
                {
                    cancel.Parameters.Add(Raw("id", jobId));
                    cancel.Parameters.Add(Tstz("now", now));
                    cancel.Parameters.Add(Clob("actor", actor));
                    await cancel.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
                // Transition Log: the immediate Cancelled state, atomic with the cancel.
                await RecordTransitionAsync(connection, transaction, jobId, JobState.Cancelled, attempt, now, cancellationToken)
                    .ConfigureAwait(false);
                await ResolveChildLatchesAsync(connection, transaction, jobId, JobState.Cancelled, now, cancellationToken)
                    .ConfigureAwait(false);
                await AppendAuditAsync(connection, transaction, actor, OperatorAction.Cancel, jobId.ToString(), now, cancellationToken)
                    .ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return CancelResult.CancelledImmediately;

            case JobState.Leased:
                await using (var request = Cmd(
                    "UPDATE backwave.jobs SET cancel_requested = 1 WHERE job_id = :id", connection, transaction))
                {
                    request.Parameters.Add(Raw("id", jobId));
                    await request.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
                await AppendAuditAsync(connection, transaction, actor, OperatorAction.Cancel, jobId.ToString(), now, cancellationToken)
                    .ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return CancelResult.CancellationRequested;

            default:
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return CancelResult.NotCancellable;
        }
    }
    // ── §5.8 Operator Actions (Requeue, Pause/Resume, TriggerScheduleNow, audit) ──

    /// <inheritdoc/>
    public async ValueTask<RequeueResult> RequeueAsync(
        Guid jobId, string actor, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await BeginAsync(connection, cancellationToken).ConfigureAwait(false);

        // Only Dead-Lettered (5) or Quarantined (6) recover; the state guard rejects anything else without
        // effect. Attempt resets to 0, due now.
        await using var update = Cmd(
            """
            UPDATE backwave.jobs
            SET state = 0, attempt = 0, due_time = :now, lease_owner = NULL, lease_expiry = NULL,
                cancel_requested = 0, terminal_at = NULL, terminal_cause = NULL
            WHERE job_id = :id AND state IN (5, 6)
            """,
            connection, transaction);
        update.Parameters.Add(Raw("id", jobId));
        update.Parameters.Add(Tstz("now", now));
        if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return RequeueResult.NotRequeueable;
        }

        // Transition Log: back to Scheduled at Attempt 0 (the requeue resets the budget).
        await RecordTransitionAsync(connection, transaction, jobId, JobState.Scheduled, attempt: 0, now, cancellationToken)
            .ConfigureAwait(false);
        await AppendAuditAsync(connection, transaction, actor, OperatorAction.Requeue, jobId.ToString(), now, cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return RequeueResult.Requeued;
    }

    /// <inheritdoc/>
    public ValueTask PauseQueueAsync(
        string queue, string actor, DateTimeOffset now, CancellationToken cancellationToken = default)
        => SetPausedAsync(queue, paused: true, actor, OperatorAction.PauseQueue, now, cancellationToken);

    /// <inheritdoc/>
    public ValueTask ResumeQueueAsync(
        string queue, string actor, DateTimeOffset now, CancellationToken cancellationToken = default)
        => SetPausedAsync(queue, paused: false, actor, OperatorAction.ResumeQueue, now, cancellationToken);

    private async ValueTask SetPausedAsync(
        string queue, bool paused, string actor, OperatorAction action, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await BeginAsync(connection, cancellationToken).ConfigureAwait(false);
        // Best-effort serialization against an in-flight claim's first-config read: take the same row
        // lock the claim read path takes. Released at commit.
        await AcquireQueueConfigLockAsync(connection, transaction, queue, cancellationToken).ConfigureAwait(false);
        await using (var upsert = Cmd(
            """
            MERGE INTO backwave.queue_limits t
            USING (SELECT :queue AS queue FROM dual) s ON (t.queue = s.queue)
            WHEN MATCHED THEN UPDATE SET paused = :paused
            WHEN NOT MATCHED THEN INSERT (queue, paused) VALUES (:queue, :paused)
            """,
            connection, transaction))
        {
            upsert.Parameters.Add(Str("queue", queue));
            upsert.Parameters.Add(Int("paused", paused ? 1 : 0));
            await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await AppendAuditAsync(connection, transaction, actor, action, queue, now, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        InvalidateQueueConfig(); // pause/resume on this process is honored on the next claim
    }

    /// <inheritdoc/>
    public async ValueTask<TriggerScheduleResult> TriggerScheduleNowAsync(
        string scheduleId, string actor, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await BeginAsync(connection, cancellationToken).ConfigureAwait(false);

        (string WireName, byte[] Payload, string Queue)? schedule = null;
        await using (var select = Cmd(
            "SELECT wire_name, payload, queue FROM backwave.schedules WHERE schedule_id = :id", connection, transaction))
        {
            select.Parameters.Add(Str("id", scheduleId));
            await using var reader = (OracleDataReader)await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                schedule = (reader.GetString(0), ReadBytes(reader, 1), reader.GetString(2));
            }
        }
        if (schedule is null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return TriggerScheduleResult.ScheduleNotFound;
        }

        // One instance due now; the Cursor is never touched, so future ticks are unaffected. The id is
        // deterministic per (schedule, instant), so a retried trigger collapses.
        var mintedId = JobIds.ForMintedTick(scheduleId, now);
        await using (var insert = Cmd(
            """
            INSERT INTO backwave.jobs (job_id, wire_name, payload, queue, state, due_time, schedule_id)
            SELECT :id, :wire, :payload, :queue, 0, :due, :scheduleId FROM dual
            WHERE NOT EXISTS (SELECT 1 FROM backwave.jobs WHERE job_id = :id)
            """,
            connection, transaction))
        {
            insert.Parameters.Add(Raw("id", mintedId));
            insert.Parameters.Add(Str("wire", schedule.Value.WireName));
            insert.Parameters.Add(Blob("payload", schedule.Value.Payload));
            insert.Parameters.Add(Str("queue", schedule.Value.Queue));
            insert.Parameters.Add(Tstz("due", now));
            insert.Parameters.Add(Str("scheduleId", scheduleId));
            if (await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0)
            {
                // Transition Log: the minted instance's first Scheduled state, at Attempt 0.
                await RecordTransitionAsync(connection, transaction, mintedId,
                    JobState.Scheduled, attempt: 0, now, cancellationToken).ConfigureAwait(false);
            }
        }
        await AppendAuditAsync(connection, transaction, actor, OperatorAction.TriggerScheduleNow, scheduleId, now, cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return TriggerScheduleResult.Triggered;
    }

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<OperatorAuditRecord>> ListAuditRecordsAsync(
        string target, CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Cmd(
            "SELECT actor, action, target, recorded_at FROM backwave.operator_audit WHERE target = :target ORDER BY sequence",
            connection);
        command.Parameters.Add(Str("target", target));

        var records = new List<OperatorAuditRecord>();
        await using var reader = (OracleDataReader)await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            records.Add(new OperatorAuditRecord(
                reader.GetString(0), (OperatorAction)reader.GetInt32(1), reader.GetString(2),
                ReadTstz(reader, 3)));
        }
        return records;
    }

    // Appends one Operator audit record inside the action's transaction.
    private async Task AppendAuditAsync(
        OracleConnection connection, OracleTransaction transaction, string actor, OperatorAction action,
        string target, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var audit = Cmd(
            """
            INSERT INTO backwave.operator_audit (actor, action, target, recorded_at)
            VALUES (:actor, :action, :target, :now)
            """,
            connection, transaction);
        audit.Parameters.Add(Str("actor", actor));
        audit.Parameters.Add(Int("action", (int)action));
        audit.Parameters.Add(Str("target", target));
        audit.Parameters.Add(Tstz("now", now));
        await audit.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    // Appends one Transition Log entry for a job's resulting state, inside the SAME transaction as the
    // state change it records - a crash leaves neither or both. The ordinal is the per-job max + 1 (a
    // sub-select against the same table), so it climbs even as oldest rows age out. The trailing bounded
    // delete enforces MaxTransitionsPerJob. `now` is always the caller's clock. `failureDetail` is the
    // Shell-captured exception text, written only on a failing transition and clamped to
    // MaxFailureDetailBytes; null on every other transition.
    private async Task RecordTransitionAsync(
        OracleConnection connection, OracleTransaction transaction, Guid jobId, JobState state,
        int attempt, DateTimeOffset now, CancellationToken cancellationToken, string? failureDetail = null)
    {
        // Job History Policy gates writes, not schema. Off appends no row at all; Transitions appends the
        // row but never the detail; the full rung keeps the clamped detail. The table always exists -
        // flipping the policy is config, never a migration.
        if (_historyPolicy == JobHistoryPolicy.Off)
        {
            return;
        }
        if (_historyPolicy == JobHistoryPolicy.Transitions)
        {
            failureDetail = null; // record the transition, but never the detail it would have carried
        }

        await using (var insert = Cmd(
            """
            INSERT INTO backwave.job_transitions (job_id, ordinal, recorded_at, state, attempt, failure_detail)
            SELECT :id, COALESCE(MAX(ordinal) + 1, 0), :now, :state, :attempt, :detail
            FROM backwave.job_transitions WHERE job_id = :id
            """,
            connection, transaction))
        {
            insert.Parameters.Add(Raw("id", jobId));
            insert.Parameters.Add(Tstz("now", now));
            insert.Parameters.Add(Int("state", (int)state));
            insert.Parameters.Add(Int("attempt", attempt));
            insert.Parameters.Add(Clob("detail", options.Bounds.ClampFailureDetail(failureDetail)));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // Per-job-life cap: keep only the newest MaxTransitionsPerJob entries, dropping oldest.
        await using var prune = Cmd(
            """
            DELETE FROM backwave.job_transitions
            WHERE job_id = :id AND ordinal <= (
                SELECT MAX(ordinal) FROM backwave.job_transitions WHERE job_id = :id
            ) - :cap
            """,
            connection, transaction);
        prune.Parameters.Add(Raw("id", jobId));
        prune.Parameters.Add(Int("cap", options.Bounds.MaxTransitionsPerJob));
        await prune.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    // Appends a BATCH of Transition Log entries. Oracle has no OPENJSON, and each job appears exactly once
    // per batch, so this is a straight per-row loop over the single-row recorder - each entry's ordinal is
    // still the per-job MAX(ordinal)+1, and every write rides the caller's transaction, so the whole batch
    // is atomic with the lease/outcome write. Honors the history policy (Off writes nothing) via the
    // per-row recorder.
    private async Task RecordTransitionsBatchAsync(
        OracleConnection connection, OracleTransaction transaction,
        IReadOnlyList<(Guid JobId, JobState State, int Attempt, string? FailureDetail)> rows,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (rows.Count == 0 || _historyPolicy == JobHistoryPolicy.Off)
        {
            return;
        }
        foreach (var (jobId, state, attempt, failureDetail) in rows)
        {
            await RecordTransitionAsync(connection, transaction, jobId, state, attempt, now, cancellationToken, failureDetail)
                .ConfigureAwait(false);
        }
    }
    // ── §5.7 Schedules & minting ─────────────────────────────────────────────────

    /// <inheritdoc/>
    public async ValueTask UpsertScheduleAsync(ScheduleRecord schedule, CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        // Redefining a schedule keeps its Cursor: ticks already resolved never replay. The transaction
        // serializes concurrent first-upserts of the same new id - the MERGE's row probe plus PK are the
        // arbiter, so the loser converges rather than duplicating.
        await using var transaction = await BeginAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var command = Cmd(
            """
            MERGE INTO backwave.schedules t
            USING (SELECT :id AS schedule_id FROM dual) s ON (t.schedule_id = s.schedule_id)
            WHEN MATCHED THEN UPDATE
                SET cron = :cron, wire_name = :wire, payload = :payload, queue = :queue,
                    time_zone_id = :zone, catch_up = :catchUp, no_overlap = :noOverlap
            WHEN NOT MATCHED THEN
                INSERT (schedule_id, cron, wire_name, payload, queue, cursor, time_zone_id, catch_up, no_overlap)
                VALUES (:id, :cron, :wire, :payload, :queue, :cursor, :zone, :catchUp, :noOverlap)
            """,
            connection, transaction);
        command.Parameters.Add(Str("id", schedule.ScheduleId));
        command.Parameters.Add(Str("cron", schedule.Cron));
        command.Parameters.Add(Str("wire", schedule.WireName));
        command.Parameters.Add(Blob("payload", schedule.Payload));
        command.Parameters.Add(Str("queue", schedule.Queue));
        command.Parameters.Add(StrN("zone", schedule.TimeZoneId));
        command.Parameters.Add(Int("catchUp", (int)schedule.CatchUp));
        command.Parameters.Add(Int("noOverlap", schedule.NoOverlap ? 1 : 0));
        command.Parameters.Add(Tstz("cursor", schedule.Cursor));
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                break;
            }
            catch (OracleException exception) when (IsDuplicate(exception) && attempt < 5)
            {
                // A concurrent first-upsert of the same new id committed between this MERGE's not-matched
                // probe and its insert. The row now exists, so a retry converges on the matched-update
                // branch rather than duplicating.
            }
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask RemoveScheduleAsync(string scheduleId, CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await BeginAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var command = Cmd(
            "DELETE FROM backwave.schedules WHERE schedule_id = :id", connection, transaction);
        command.Parameters.Add(Str("id", scheduleId));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<ScheduleSnapshot>> ListSchedulesAsync(CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        // Payload is deliberately omitted from this hot-path listing: the mint planner never reads it, and
        // MintDue re-reads it from the row, so the per-poll load carries no blobs. A correlated NVL(...,0)
        // stands in for the illegal-in-a-select-list CASE WHEN EXISTS: ROWNUM = 1 short-circuits at the
        // first live member.
        await using var command = Cmd(
            """
            SELECT s.schedule_id, s.cron, s.wire_name, s.queue, s.cursor,
                   s.time_zone_id, s.catch_up, s.no_overlap, s.skipped_ticks,
                   NVL((SELECT 1 FROM backwave.jobs j
                        WHERE j.schedule_id = s.schedule_id AND j.state IN (0, 1, 2) AND ROWNUM = 1), 0) AS has_live
            FROM backwave.schedules s
            ORDER BY s.schedule_id
            """,
            connection);

        var snapshots = new List<ScheduleSnapshot>();
        await using var reader = (OracleDataReader)await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            snapshots.Add(new ScheduleSnapshot(
                new ScheduleRecord
                {
                    ScheduleId = reader.GetString(0),
                    Cron = reader.GetString(1),
                    WireName = reader.GetString(2),
                    Payload = ReadOnlyMemory<byte>.Empty,
                    Queue = reader.GetString(3),
                    Cursor = ReadTstz(reader, 4),
                    TimeZoneId = reader.IsDBNull(5) ? null : reader.GetString(5),
                    CatchUp = (CatchUpPolicy)reader.GetInt32(6),
                    NoOverlap = reader.GetInt32(7) != 0,
                    SkippedTicks = ParseSkippedTicks(reader.GetString(8)),
                },
                HasLiveInstance: reader.GetInt32(9) == 1));
        }
        return snapshots;
    }

    /// <inheritdoc/>
    public async ValueTask<int> MintDueAsync(
        IReadOnlyList<MintDecision> decisions, CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        var minted = 0;

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        foreach (var decision in decisions)
        {
            await using var transaction = await BeginAsync(connection, cancellationToken).ConfigureAwait(false);

            // Cursor fencing: advancing the cursor claims the decision's ticks whole. Oracle has no OUTPUT,
            // so the fence UPDATE reports a rowcount and, when it won (1), a follow-up SELECT re-reads the
            // row this transaction now owns.
            int fenced;
            await using (var fence = Cmd(
                "UPDATE backwave.schedules SET cursor = :newCursor WHERE schedule_id = :id AND cursor = :expected",
                connection, transaction))
            {
                fence.Parameters.Add(Str("id", decision.ScheduleId));
                fence.Parameters.Add(Tstz("newCursor", decision.NewCursor));
                fence.Parameters.Add(Tstz("expected", decision.ExpectedCursor));
                fenced = await fence.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            if (fenced == 0)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                continue; // another node already minted these ticks
            }

            (string WireName, byte[] Payload, string Queue, string SkippedTicks) schedule;
            await using (var select = Cmd(
                "SELECT wire_name, payload, queue, skipped_ticks FROM backwave.schedules WHERE schedule_id = :id",
                connection, transaction))
            {
                select.Parameters.Add(Str("id", decision.ScheduleId));
                await using var reader = (OracleDataReader)await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                schedule = (reader.GetString(0), ReadBytes(reader, 1), reader.GetString(2), reader.GetString(3));
            }

            if (decision.SkippedTicks.Count > 0)
            {
                var combined = ParseSkippedTicks(schedule.SkippedTicks)
                    .Concat(decision.SkippedTicks)
                    .TakeLast(options.Bounds.MaxRecordedSkippedTicks)
                    .ToList();
                await using var record = Cmd(
                    "UPDATE backwave.schedules SET skipped_ticks = :ticks WHERE schedule_id = :id",
                    connection, transaction);
                record.Parameters.Add(Str("id", decision.ScheduleId));
                record.Parameters.Add(Clob("ticks", RenderSkippedTicks(combined)));
                await record.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            // Crash after the cursor advanced, before the instances are minted: rollback must restore the
            // cursor so the ticks are minted, never silently lost.
            await FailpointAsync("mint-due", cancellationToken).ConfigureAwait(false);

            var mintedForDecision = 0;
            foreach (var tick in decision.Ticks)
            {
                var mintedId = JobIds.ForMintedTick(decision.ScheduleId, tick);
                await using var insert = Cmd(
                    """
                    INSERT INTO backwave.jobs (job_id, wire_name, payload, queue, state, due_time, schedule_id)
                    SELECT :id, :wire, :payload, :queue, 0, :due, :scheduleId FROM dual
                    WHERE NOT EXISTS (SELECT 1 FROM backwave.jobs WHERE job_id = :id)
                    """,
                    connection, transaction);
                insert.Parameters.Add(Raw("id", mintedId));
                insert.Parameters.Add(Str("wire", schedule.WireName));
                insert.Parameters.Add(Blob("payload", schedule.Payload));
                insert.Parameters.Add(Str("queue", schedule.Queue));
                insert.Parameters.Add(Tstz("due", tick));
                insert.Parameters.Add(Str("scheduleId", decision.ScheduleId));
                if (await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0)
                {
                    mintedForDecision++;
                    // MintDue carries no `now`; the tick (the instance's due instant) is the deterministic
                    // timestamp for its first Scheduled transition.
                    await RecordTransitionAsync(connection, transaction, mintedId,
                        JobState.Scheduled, attempt: 0, tick, cancellationToken).ConfigureAwait(false);
                }
            }
            if (mintedForDecision > 0)
            {
                // Minted ticks are due by construction - hint the cluster (§8) in this same transaction.
                await PublishHintAsync(connection, transaction, schedule.Queue, cancellationToken).ConfigureAwait(false);
            }
            minted += mintedForDecision;
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return minted;
    }

    // ── §5.10 Queue configuration ────────────────────────────────────────────────

    /// <inheritdoc/>
    public async ValueTask SetConcurrencyLimitAsync(
        string queue, int? limit, string actor, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await BeginAsync(connection, cancellationToken).ConfigureAwait(false);
        // Serialize against an in-flight claim's first-config read: take the same row lock the claim read
        // path takes, so this first-ever limit serializes with a claim even before any queue_limits row
        // exists. Released at commit.
        await AcquireQueueConfigLockAsync(connection, transaction, queue, cancellationToken).ConfigureAwait(false);
        await using (var command = Cmd(
            """
            MERGE INTO backwave.queue_limits t
            USING (SELECT :queue AS queue FROM dual) s ON (t.queue = s.queue)
            WHEN MATCHED THEN UPDATE SET max_concurrent = :limit
            WHEN NOT MATCHED THEN INSERT (queue, max_concurrent) VALUES (:queue, :limit)
            """,
            connection, transaction))
        {
            command.Parameters.Add(Str("queue", queue));
            command.Parameters.Add(IntN("limit", limit));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await AppendAuditAsync(
            connection, transaction, actor, OperatorAction.SetConcurrencyLimit, queue, now, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        InvalidateQueueConfig(); // a limit set/cleared on this process is honored on the next claim
    }
    // ── §5.9 Monitor reads ───────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async ValueTask<JobRecord?> GetJobAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Cmd($"SELECT {JobColumns} FROM backwave.jobs WHERE job_id = :id", connection);
        command.Parameters.Add(Raw("id", jobId));
        JobRecord? record;
        await using (var reader = (OracleDataReader)await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            record = await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadJob(reader) : null;
        }
        if (record is null)
        {
            return null;
        }
        var tags = await HydrateTagsAsync(connection, [record.JobId], cancellationToken).ConfigureAwait(false);
        return record with { Tags = tags.TryGetValue(record.JobId, out var set) ? set : JobTags.Empty };
    }

    /// <inheritdoc/>
    public async ValueTask<ReadOnlyMemory<byte>?> GetJobOutputAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        // Reads ONLY the output column, so a large blob never rides the listing/claim path. Null for an
        // unknown job or one that never set output; deleted with the job row under retention for free.
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Cmd("SELECT output FROM backwave.jobs WHERE job_id = :id", connection);
        command.Parameters.Add(Raw("id", jobId));
        await using var reader = (OracleDataReader)await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.IsDBNull(0))
        {
            return null;
        }
        return new ReadOnlyMemory<byte>(ReadBytes(reader, 0));
    }

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<JobTransition>> GetJobHistoryAsync(
        Guid jobId, CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        // The Transition Log, oldest first. Rows are deleted with the job via FK cascade, so an absent or
        // purged job simply yields an empty timeline.
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Cmd(
            """
            SELECT ordinal, recorded_at, state, attempt, failure_detail
            FROM backwave.job_transitions WHERE job_id = :id ORDER BY ordinal
            """,
            connection);
        command.Parameters.Add(Raw("id", jobId));

        var transitions = new List<JobTransition>();
        await using var reader = (OracleDataReader)await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            transitions.Add(new JobTransition(
                reader.GetInt64(0),
                ReadTstz(reader, 1),
                (JobState)reader.GetInt32(2),
                reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }
        return transitions;
    }

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<JobRecord>> ListJobsAsync(
        JobQuery query, CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        // Only the filters a query actually uses become predicates: the catch-all (:x IS NULL OR col = :x)
        // form defeats index seeks.
        await using var command = new OracleCommand { Connection = connection, BindByName = true };
        var conditions = new List<string>();
        AppendScopeConditions(query, conditions, command);
        var newestFirst = query.SortDirection == JobSortDirection.NewestFirst;
        if (query.AfterSequence is { } after)
        {
            // The cursor is direction-relative: newest-first continues toward OLDER jobs.
            conditions.Add(newestFirst ? "sequence < :after" : "sequence > :after");
            command.Parameters.Add(Long("after", after));
        }
        var where = conditions.Count > 0 ? $"WHERE {string.Join(" AND ", conditions)}" : string.Empty;
        var order = newestFirst ? "ORDER BY sequence DESC" : "ORDER BY sequence";
        command.CommandText = _schema.Rewrite(
            $"SELECT {JobColumns} FROM backwave.jobs {where} {order} FETCH FIRST :take ROWS ONLY");
        command.Parameters.Add(Int("take", Math.Min(query.MaxResults, options.Bounds.MaxMonitorPageSize)));

        var jobs = new List<JobRecord>();
        await using (var reader = (OracleDataReader)await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                jobs.Add(ReadJob(reader));
            }
        }
        return await WithTagsAsync(connection, jobs, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<QueueStateCount>> CountJobsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Cmd(
            "SELECT queue, state, count(*) FROM backwave.jobs GROUP BY queue, state ORDER BY queue, state",
            connection);

        var counts = new List<QueueStateCount>();
        await using var reader = (OracleDataReader)await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            counts.Add(new QueueStateCount(
                reader.GetString(0), (JobState)reader.GetInt32(1), reader.GetInt32(2)));
        }
        return counts;
    }

    // Builds the §5.9 scope conditions shared by ListJobsAsync and FacetAsync - the scalar filters plus
    // the AND-ed tag predicates, each an EXISTS over job_tags correlated to the job row (has-key-any-value
    // omits the value condition). Everything is parameterized onto `command`. Pagination is NOT a scope
    // condition - the caller adds it. Empty key/value encode to the CHR(1) sentinel so a Label (empty key)
    // matches the stored form; key/value are plain (unquoted) column names.
    private static void AppendScopeConditions(JobQuery query, List<string> conditions, OracleCommand command)
    {
        if (query.State is { } state)
        {
            conditions.Add("state = :state");
            command.Parameters.Add(Int("state", (int)state));
        }
        if (query.Queue is { } queue)
        {
            conditions.Add("queue = :queue");
            command.Parameters.Add(Str("queue", queue));
        }
        if (query.WireName is { } wire)
        {
            conditions.Add("wire_name = :wire");
            command.Parameters.Add(Str("wire", wire));
        }
        if (query.ScheduleId is { } scheduleId)
        {
            conditions.Add("schedule_id = :scheduleId");
            command.Parameters.Add(Str("scheduleId", scheduleId));
        }
        for (var i = 0; i < query.TagPredicates.Count; i++)
        {
            var predicate = query.TagPredicates[i];
            var keyParam = $"tagKey{i}";
            if (predicate.Value is { } value)
            {
                var valueParam = $"tagValue{i}";
                conditions.Add(
                    $"EXISTS (SELECT 1 FROM backwave.job_tags t WHERE t.job_id = jobs.job_id "
                    + $"AND t.key = :{keyParam} AND t.value = :{valueParam})");
                command.Parameters.Add(Str(keyParam, EncodeTag(predicate.Key)));
                command.Parameters.Add(Str(valueParam, EncodeTag(value)));
            }
            else
            {
                conditions.Add(
                    $"EXISTS (SELECT 1 FROM backwave.job_tags t WHERE t.job_id = jobs.job_id AND t.key = :{keyParam})");
                command.Parameters.Add(Str(keyParam, EncodeTag(predicate.Key)));
            }
        }
    }

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<TagFacet>> FacetAsync(
        string key, JobQuery? baseQuery = null, int maxResults = int.MaxValue, CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new OracleCommand { Connection = connection, BindByName = true };
        command.Parameters.Add(Str("key", EncodeTag(key)));
        command.Parameters.Add(Int("max", Math.Max(0, maxResults)));

        // COUNT(DISTINCT job_id) is distinct-JOB counting, so a job carrying the same Tag once is never
        // double-counted; a multi-value key counts the job under each value. A baseQuery scopes the
        // population FIRST with the same predicates ListJobs uses, as an IN (<scoped job ids>) subquery.
        // ORDER BY count DESC, then value under NLSSORT BINARY - the byte-ordinal tiebreak that keeps the
        // FETCH FIRST cap picking the same buckets as the reference store, independent of session NLS.
        var scope = string.Empty;
        if (baseQuery is not null)
        {
            var conditions = new List<string>();
            AppendScopeConditions(baseQuery, conditions, command);
            var where = conditions.Count > 0 ? $"WHERE {string.Join(" AND ", conditions)}" : string.Empty;
            scope = $"AND job_id IN (SELECT job_id FROM backwave.jobs {where})";
        }
        command.CommandText = _schema.Rewrite(
            $"SELECT value, count(DISTINCT job_id) FROM backwave.job_tags WHERE key = :key {scope} "
            + "GROUP BY value ORDER BY count(DISTINCT job_id) DESC, NLSSORT(value, 'NLS_SORT=BINARY') "
            + "FETCH FIRST :max ROWS ONLY");

        var facets = new List<TagFacet>();
        await using var reader = (OracleDataReader)await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            facets.Add(new TagFacet(DecodeTag(reader.GetString(0)), reader.GetInt32(1)));
        }
        return facets;
    }

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<TagSuggestion>> SuggestTagsAsync(
        TagSuggestQuery query, CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        var limit = Math.Clamp(query.MaxResults, 1, TagSuggestQuery.MaxSuggestResults);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new OracleCommand { Connection = connection, BindByName = true };
        command.Parameters.Add(Int("limit", limit));
        // The prefix is escaped for LIKE (\, %, _) and folded by LOWER(). The persisted key_lower/
        // value_lower virtual columns (and their index) materialize the fold. Oracle's default binary
        // NLS_COMP makes DISTINCT/=/LIKE case-sensitive, and NLSSORT BINARY makes the ordering byte-
        // ordinal, so the ASCII-CI + lexicographic promises hold identically to the reference store.
        // Oracle has no row-value comparison, so each keyset > is expanded to the lead-strict-or-equal-
        // then-next form. LOWER('') is NULL and NULL || '%' is '%', so an empty prefix matches all.
        command.Parameters.Add(Str("prefix", EscapeLike(query.Prefix)));

        var suggestions = new List<TagSuggestion>();
        if (query.Key is not null)
        {
            // Stage two: distinct values under one key (key="" => Labels via the CHR(1) sentinel), keyset-
            // paged by value.
            command.Parameters.Add(Str("key", EncodeTag(query.Key)));
            var cursor = string.Empty;
            if (query.After is { } after)
            {
                command.Parameters.Add(Str("av", after.Value));
                cursor = "AND (value_lower > LOWER(:av) "
                    + "OR (value_lower = LOWER(:av) AND value > :av)) ";
            }
            command.CommandText = _schema.Rewrite(
                "SELECT value FROM ("
                + "SELECT DISTINCT value, value_lower FROM backwave.job_tags "
                + "WHERE key = :key AND value_lower LIKE LOWER(:prefix) || '%' ESCAPE '\\' "
                + cursor
                + ") ORDER BY NLSSORT(value_lower, 'NLS_SORT=BINARY'), NLSSORT(value, 'NLS_SORT=BINARY') "
                + "FETCH FIRST :limit ROWS ONLY");

            await using var reader = (OracleDataReader)await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                suggestions.Add(new TagSuggestion(query.Key, DecodeTag(reader.GetString(0))));
            }
            return suggestions;
        }

        // Stage one: Labels (section 0) then keys (section 1), one keyset order across both blocks. The
        // fold-prefix predicate is pushed INTO each DISTINCT subquery on the persisted key_lower/value_lower
        // columns, so each branch is a bounded range seek. Labels are the CHR(1)-keyed rows; keys are all
        // others.
        var stageOneCursor = string.Empty;
        if (query.After is { } cursorItem)
        {
            var section = cursorItem.IsLabel ? 0 : 1;
            var name = cursorItem.IsLabel ? cursorItem.Value : cursorItem.Key;
            command.Parameters.Add(Int("sec", section));
            command.Parameters.Add(Str("an", name));
            stageOneCursor = "WHERE (section > :sec OR (section = :sec AND ("
                + "NLSSORT(LOWER(name), 'NLS_SORT=BINARY') > NLSSORT(LOWER(:an), 'NLS_SORT=BINARY') "
                + "OR (NLSSORT(LOWER(name), 'NLS_SORT=BINARY') = NLSSORT(LOWER(:an), 'NLS_SORT=BINARY') "
                + "AND NLSSORT(name, 'NLS_SORT=BINARY') > NLSSORT(:an, 'NLS_SORT=BINARY'))))) ";
        }
        command.Parameters.Add(Str("emptyKey", EncodeTag(string.Empty)));
        command.CommandText = _schema.Rewrite(
            "WITH tokens AS ("
            + "SELECT 0 AS section, name FROM (SELECT DISTINCT value AS name FROM backwave.job_tags "
            + "WHERE key = :emptyKey AND value_lower LIKE LOWER(:prefix) || '%' ESCAPE '\\') "
            + "UNION ALL "
            + "SELECT 1 AS section, name FROM (SELECT DISTINCT key AS name FROM backwave.job_tags "
            + "WHERE key <> :emptyKey AND key_lower LIKE LOWER(:prefix) || '%' ESCAPE '\\')) "
            + "SELECT section, name FROM tokens "
            + stageOneCursor
            + "ORDER BY section, NLSSORT(LOWER(name), 'NLS_SORT=BINARY'), NLSSORT(name, 'NLS_SORT=BINARY') "
            + "FETCH FIRST :limit ROWS ONLY");

        await using var stageOneReader = (OracleDataReader)await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await stageOneReader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var name = DecodeTag(stageOneReader.GetString(1));
            suggestions.Add(stageOneReader.GetInt32(0) == 0
                ? new TagSuggestion(string.Empty, name)
                : new TagSuggestion(name, string.Empty));
        }
        return suggestions;
    }

    // Escape the LIKE metacharacters (backslash first, then % and _) so a typed prefix is matched
    // literally; the caller appends the '%' wildcard and uses ESCAPE '\'. Oracle LIKE has no character-
    // class opener, so '[' needs no escape (unlike T-SQL).
    private static string EscapeLike(string prefix)
        => prefix.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<QueueSettings>> ListQueueSettingsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        // The Pause flag and Concurrency Limit share the one queue_limits row the claim path already
        // reads, so the operational settings read is a single scan of it.
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Cmd(
            "SELECT queue, paused, max_concurrent FROM backwave.queue_limits ORDER BY queue", connection);

        var settings = new List<QueueSettings>();
        await using var reader = (OracleDataReader)await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            settings.Add(new QueueSettings(
                reader.GetString(0), reader.GetInt32(1) != 0, reader.IsDBNull(2) ? null : reader.GetInt32(2)));
        }
        return settings;
    }
    // ── Workflows ─────────────────────────────────────────────────────────────────
    //
    // The full Networked-Adapter surface, byte-for-byte equivalent to the In-Memory reference. The whole
    // graph commits in ONE transaction - all-or-nothing - and under Transactional Enqueue it rides the
    // CALLER's transaction, so the co-resident whole-Workflow guarantee falls out.

    /// <inheritdoc/>
    public async ValueTask<WorkflowEnqueueResult> EnqueueWorkflowAsync(
        WorkflowDefinition workflow, DateTimeOffset now, System.Data.Common.DbTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        if (transaction is not null)
        {
            if (transaction is not OracleTransaction { Connection: { } callerConnection } oracleTransaction)
            {
                throw new ArgumentException(
                    "The Oracle adapter enlists in OracleTransaction instances only.", nameof(transaction));
            }
            return await EnqueueWorkflowCoreAsync(callerConnection, oracleTransaction, workflow, now, cancellationToken)
                .ConfigureAwait(false);
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var ownTransaction = await BeginAsync(connection, cancellationToken).ConfigureAwait(false);
        var result = await EnqueueWorkflowCoreAsync(connection, ownTransaction, workflow, now, cancellationToken)
            .ConfigureAwait(false);
        // All-or-nothing: a non-Ok validation leaves nothing inserted, so only Ok commits.
        if (result == WorkflowEnqueueResult.Ok)
        {
            await ownTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await ownTransaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        }
        return result;
    }

    private async ValueTask<WorkflowEnqueueResult> EnqueueWorkflowCoreAsync(
        OracleConnection connection, OracleTransaction transaction, WorkflowDefinition workflow,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Admission rules for the whole graph, validated BEFORE any insert - a single bad member rejects
        // the whole batch and (because every write is in this transaction) leaves the store untouched.
        if (workflow.Members.Count == 0)
        {
            return WorkflowEnqueueResult.EmptyWorkflow;
        }

        var workflowExists = await WorkflowExistsAsync(connection, transaction, workflow.WorkflowId, cancellationToken)
            .ConfigureAwait(false);
        if (workflow.IsAppend)
        {
            if (!workflowExists)
            {
                return WorkflowEnqueueResult.WorkflowNotFound; // nothing to append to
            }
        }
        else if (workflowExists)
        {
            return WorkflowEnqueueResult.DuplicateWorkflow;
        }

        var newMemberIds = new HashSet<Guid>();
        foreach (var member in workflow.Members)
        {
            if (!newMemberIds.Add(member.JobId))
            {
                return WorkflowEnqueueResult.DuplicateMember; // the same JobId twice in one batch
            }
        }
        // Allowed parents: new members plus (on append) the existing members of this Workflow.
        var allowedParents = new HashSet<Guid>(newMemberIds);
        if (workflow.IsAppend)
        {
            allowedParents.UnionWith(
                await MembersOfAsync(connection, transaction, workflow.WorkflowId, cancellationToken)
                    .ConfigureAwait(false));
        }

        foreach (var member in workflow.Members)
        {
            if (await JobExistsAsync(connection, transaction, member.JobId, cancellationToken).ConfigureAwait(false))
            {
                return WorkflowEnqueueResult.DuplicateMember;
            }
            if (member.Payload.Length > options.Bounds.MaxPayloadBytes)
            {
                return WorkflowEnqueueResult.PayloadTooLarge;
            }
            if (member.WireName.Length > options.Bounds.MaxWireNameLength)
            {
                return WorkflowEnqueueResult.WireNameTooLong;
            }
            var parents = member.Parents.Distinct().ToArray();
            if (parents.Length > options.Bounds.MaxParentsPerJob)
            {
                return WorkflowEnqueueResult.TooManyParents;
            }
            if (parents.Any(p => !allowedParents.Contains(p)))
            {
                return WorkflowEnqueueResult.ContainmentViolation;
            }
        }

        // The existence check above is an unlocked read, so two concurrent creates of the same id can both
        // pass it and then race the writes below. The primary keys are the arbiter - the ORA-00001 catch
        // on each insert maps the loser to a defined result, never a raw PK violation. This failpoint parks
        // a create PAST every check so a test can pin that race; a no-op in production.
        await FailpointAsync("workflow-apply", cancellationToken).ConfigureAwait(false);

        // Apply. Append leaves the existing Workflows row untouched; only a creation writes the row.
        if (!workflow.IsAppend)
        {
            await using var insertRow = Cmd(
                """
                INSERT INTO backwave.workflows (workflow_id, name, created_at, retention, restarted_from)
                VALUES (:id, :name, :createdAt, :retention, :restartedFrom)
                """,
                connection, transaction);
            insertRow.Parameters.Add(Raw("id", workflow.WorkflowId));
            insertRow.Parameters.Add(Clob("name", workflow.Name));
            insertRow.Parameters.Add(Tstz("createdAt", now));
            insertRow.Parameters.Add(Int("retention", (int)workflow.Retention));
            insertRow.Parameters.Add(RawN("restartedFrom", workflow.RestartedFrom));
            try
            {
                await insertRow.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OracleException exception) when (IsDuplicate(exception))
            {
                // A concurrent create won this id first. Nothing else is written yet, so the whole graph
                // rolls back and the caller gets the defined duplicate result.
                return WorkflowEnqueueResult.DuplicateWorkflow;
            }
        }

        // Members in dependency order (parents before children), each stamped with the WorkflowId.
        // EnqueueCoreAsync re-runs the §5.1 insert path so already-terminal in-workflow parents resolve
        // the latch identically (the ordering makes that a no-op here - parents are still live when their
        // children insert).
        foreach (var member in TopologicallyOrdered(workflow.Members))
        {
            // The parent set is a set: duplicate ids collapse before any edge is written.
            var deduped = member;
            if (deduped.Parents.Count > 1)
            {
                deduped = deduped with { Parents = deduped.Parents.Distinct().ToArray() };
            }

            var applied = await EnqueueCoreAsync(connection, transaction, deduped, now, cancellationToken, workflow.WorkflowId)
                .ConfigureAwait(false);
            // A concurrent create can insert a member with the same JobId after this batch's existence
            // check passed; the member insert's NOT EXISTS guard / ORA-00001 catch reports it as Duplicate.
            if (applied == EnqueueResult.Duplicate)
            {
                return WorkflowEnqueueResult.DuplicateMember;
            }
            if (applied != EnqueueResult.Ok) // always-on assertion: everything else validated above
            {
                throw new InvalidOperationException(
                    $"Workflow enqueue commit failed for member {member.JobId}: {applied}.");
            }
        }

        // Structural edges: immutable, recorded once, so the graph view stays total even after the live
        // gating edges (job_parents) resolve away. Append adds its new edges to the set.
        foreach (var member in workflow.Members)
        {
            foreach (var parent in member.Parents.Distinct())
            {
                await using var edge = Cmd(
                    """
                    INSERT INTO backwave.workflow_edges (workflow_id, parent_id, child_id)
                    SELECT :workflowId, :parent, :child FROM dual
                    WHERE NOT EXISTS (
                        SELECT 1 FROM backwave.workflow_edges
                        WHERE workflow_id = :workflowId AND parent_id = :parent AND child_id = :child)
                    """,
                    connection, transaction);
                edge.Parameters.Add(Raw("workflowId", workflow.WorkflowId));
                edge.Parameters.Add(Raw("parent", parent));
                edge.Parameters.Add(Raw("child", member.JobId));
                try
                {
                    await edge.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OracleException exception) when (IsDuplicate(exception))
                {
                    // Structural edges are immutable and recorded once, so a duplicate is a no-op - but the
                    // unlocked NOT EXISTS does not serialize two concurrent same-workflow appends of the
                    // same edge; the loser hits the primary key. Swallow it so the edge converges
                    // idempotently.
                }
            }
        }

        return WorkflowEnqueueResult.Ok;
    }

    private async ValueTask<bool> WorkflowExistsAsync(
        OracleConnection connection, OracleTransaction transaction, Guid workflowId, CancellationToken cancellationToken)
    {
        await using var command = Cmd(
            "SELECT 1 FROM backwave.workflows WHERE workflow_id = :id", connection, transaction);
        command.Parameters.Add(Raw("id", workflowId));
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    private async ValueTask<bool> JobExistsAsync(
        OracleConnection connection, OracleTransaction transaction, Guid jobId, CancellationToken cancellationToken)
    {
        await using var command = Cmd(
            "SELECT 1 FROM backwave.jobs WHERE job_id = :id", connection, transaction);
        command.Parameters.Add(Raw("id", jobId));
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    private async ValueTask<HashSet<Guid>> MembersOfAsync(
        OracleConnection connection, OracleTransaction transaction, Guid workflowId, CancellationToken cancellationToken)
    {
        var members = new HashSet<Guid>();
        await using var command = Cmd(
            "SELECT job_id FROM backwave.jobs WHERE workflow_id = :id", connection, transaction);
        command.Parameters.Add(Raw("id", workflowId));
        await using var reader = (OracleDataReader)await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            members.Add(ReadGuid(reader, 0));
        }
        return members;
    }

    /// <summary>
    /// Orders members so every member follows its in-batch parents (Kahn's algorithm); mirrors
    /// <c>InMemoryJobStore.TopologicallyOrdered</c>. Parents NOT in this batch (an append's existing
    /// members) are already inserted, so they impose no ordering. Insertion order breaks ties.
    /// </summary>
    private static IReadOnlyList<NewJob> TopologicallyOrdered(IReadOnlyList<NewJob> members)
    {
        var byId = members.ToDictionary(m => m.JobId);
        var indegree = members.ToDictionary(m => m.JobId, m => m.Parents.Distinct().Count(byId.ContainsKey));
        var ready = new Queue<NewJob>(members.Where(m => indegree[m.JobId] == 0));
        var children = new Dictionary<Guid, List<Guid>>();
        foreach (var m in members)
        {
            foreach (var p in m.Parents.Distinct().Where(byId.ContainsKey))
            {
                (children.TryGetValue(p, out var list) ? list : children[p] = []).Add(m.JobId);
            }
        }

        var ordered = new List<NewJob>(members.Count);
        while (ready.Count > 0)
        {
            var m = ready.Dequeue();
            ordered.Add(m);
            if (children.TryGetValue(m.JobId, out var kids))
            {
                foreach (var kid in kids)
                {
                    if (--indegree[kid] == 0)
                    {
                        ready.Enqueue(byId[kid]);
                    }
                }
            }
        }
        return ordered.Count == members.Count ? ordered : members;
    }

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<WorkflowSnapshot>> ListWorkflowsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        // Each Workflow's status is a projection of its members' states. One pass over the member states
        // grouped by workflow_id, joined to the Workflows rows. Ordered by created_at (oldest first),
        // workflow_id as the stable tiebreak.
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var statesByWorkflow = new Dictionary<Guid, List<JobState>>();
        await using (var members = Cmd(
            "SELECT workflow_id, state FROM backwave.jobs WHERE workflow_id IS NOT NULL", connection))
        {
            await using var reader = (OracleDataReader)await members.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var wf = ReadGuid(reader, 0);
                (statesByWorkflow.TryGetValue(wf, out var list) ? list : statesByWorkflow[wf] = [])
                    .Add((JobState)reader.GetInt32(1));
            }
        }

        var snapshots = new List<WorkflowSnapshot>();
        await using (var workflows = Cmd(
            "SELECT workflow_id, name, created_at, restarted_from FROM backwave.workflows " +
            "ORDER BY created_at, workflow_id", connection))
        {
            await using var reader = (OracleDataReader)await workflows.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var workflowId = ReadGuid(reader, 0);
                var states = statesByWorkflow.GetValueOrDefault(workflowId) ?? [];
                snapshots.Add(new WorkflowSnapshot
                {
                    WorkflowId = workflowId,
                    Name = reader.IsDBNull(1) ? null : reader.GetString(1),
                    CreatedAt = ReadTstz(reader, 2),
                    Status = WorkflowStatusProjection.Project(states),
                    MemberCount = states.Count,
                    RestartedFrom = reader.IsDBNull(3) ? null : ReadGuid(reader, 3),
                });
            }
        }
        return snapshots;
    }

    /// <inheritdoc/>
    public async ValueTask<WorkflowGraph?> GetWorkflowAsync(Guid workflowId, CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        string? name;
        DateTimeOffset createdAt;
        Guid? restartedFrom;
        await using (var row = Cmd(
            "SELECT name, created_at, restarted_from FROM backwave.workflows WHERE workflow_id = :id", connection))
        {
            row.Parameters.Add(Raw("id", workflowId));
            await using var reader = (OracleDataReader)await row.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }
            name = reader.IsDBNull(0) ? null : reader.GetString(0);
            createdAt = ReadTstz(reader, 1);
            restartedFrom = reader.IsDBNull(2) ? null : ReadGuid(reader, 2);
        }

        // Members in enqueue order (sequence); the graph stays total because the structural edges are never
        // deleted (unlike job_parents). Hydrate Tags so a member's full JobRecord matches reads.
        var members = new List<JobRecord>();
        await using (var memberRows = Cmd(
            $"SELECT {JobColumns} FROM backwave.jobs WHERE workflow_id = :id ORDER BY sequence", connection))
        {
            memberRows.Parameters.Add(Raw("id", workflowId));
            await using var reader = (OracleDataReader)await memberRows.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                members.Add(ReadJob(reader));
            }
        }
        var hydrated = await WithTagsAsync(connection, members, cancellationToken).ConfigureAwait(false);

        var edges = new List<WorkflowEdge>();
        await using (var edgeRows = Cmd(
            "SELECT parent_id, child_id FROM backwave.workflow_edges WHERE workflow_id = :id " +
            "ORDER BY parent_id, child_id", connection))
        {
            edgeRows.Parameters.Add(Raw("id", workflowId));
            await using var reader = (OracleDataReader)await edgeRows.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                edges.Add(new WorkflowEdge(ReadGuid(reader, 0), ReadGuid(reader, 1)));
            }
        }

        return new WorkflowGraph
        {
            WorkflowId = workflowId,
            Name = name,
            CreatedAt = createdAt,
            Status = WorkflowStatusProjection.Project(hydrated.Select(m => m.State)),
            Members = hydrated,
            Edges = edges,
            RestartedFrom = restartedFrom,
        };
    }

    /// <inheritdoc/>
    public async ValueTask<DependencyEdges> GetDependencyEdgesAsync(
        Guid jobId, CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        // Edges are deleted as each parent terminates (§5.6 latch cascade), so a child's surviving
        // parent_id rows are exactly its still-gating parents - never the full original set.
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var gatingParents = new List<Guid>();
        await using (var parents = Cmd(
            "SELECT parent_id FROM backwave.job_parents WHERE child_id = :id ORDER BY parent_id", connection))
        {
            parents.Parameters.Add(Raw("id", jobId));
            await using var reader = (OracleDataReader)await parents.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                gatingParents.Add(ReadGuid(reader, 0));
            }
        }

        var children = new List<Guid>();
        await using (var childRows = Cmd(
            "SELECT child_id FROM backwave.job_parents WHERE parent_id = :id ORDER BY child_id", connection))
        {
            childRows.Parameters.Add(Raw("id", jobId));
            await using var reader = (OracleDataReader)await childRows.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                children.Add(ReadGuid(reader, 0));
            }
        }
        return new DependencyEdges(gatingParents, children);
    }
    // ── §5.11 Retention sweep ────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async ValueTask<int> PurgeTerminalAsync(
        TerminalStateClass stateClass, DateTimeOffset terminalBefore, int maxJobs,
        CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        // Workflow-aware retention, byte-equivalent to InMemoryJobStore: a NON-workflow job keeps the
        // per-job rule (terminal_at <= :before); a Workflow member is eligible only once the WHOLE Workflow
        // has drained (no member still non-terminal) AND the DRAIN instant - max member terminal_at - is
        // <= :before, so the window starts at the drain point and the graph stays coherent for the
        // Workflow's whole life. The drained CTE folds both: a non-NULL drain_at means drained, NULL means
        // a live member exists. Oracle has no WITH before DELETE, so the CTE lives inside the IN subquery,
        // and the ORDER BY-then-ROWNUM inline view caps the batch after the ordering is applied.
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await BeginAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var command = Cmd(
            """
            DELETE FROM backwave.jobs
            WHERE job_id IN (
                WITH drained AS (
                    SELECT workflow_id,
                           CASE WHEN MIN(CASE WHEN state IN (3, 4, 5, 6) THEN 1 ELSE 0 END) = 1
                                THEN MAX(terminal_at) END AS drain_at
                    FROM backwave.jobs
                    WHERE workflow_id IS NOT NULL
                    GROUP BY workflow_id
                )
                SELECT job_id FROM (
                    SELECT j.job_id
                    FROM backwave.jobs j
                    LEFT JOIN drained d ON d.workflow_id = j.workflow_id
                    WHERE j.state IN (:stateA, :stateB)
                      AND ((j.workflow_id IS NULL AND j.terminal_at <= :before)
                           OR (j.workflow_id IS NOT NULL AND d.drain_at IS NOT NULL AND d.drain_at <= :before))
                    ORDER BY j.terminal_at, j.sequence
                ) WHERE ROWNUM <= :max
            )
            """,
            connection, transaction);
        var (stateA, stateB) = stateClass == TerminalStateClass.SucceededOrCancelled
            ? (JobState.Succeeded, JobState.Cancelled)
            : (JobState.DeadLettered, JobState.Quarantined);
        command.Parameters.Add(Int("stateA", (int)stateA));
        command.Parameters.Add(Int("stateB", (int)stateB));
        command.Parameters.Add(Tstz("before", terminalBefore));
        command.Parameters.Add(Int("max", Math.Min(maxJobs, options.Bounds.MaxPurgeBatch)));
        var purged = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        // When a Workflow's last member is purged, drop its now-orphaned identity row (structural edges
        // cascade via FK) so the tables never leak rows for Workflows with no surviving jobs.
        await using (var prune = Cmd(
            """
            DELETE FROM backwave.workflows w
            WHERE NOT EXISTS (SELECT 1 FROM backwave.jobs j WHERE j.workflow_id = w.workflow_id)
            """,
            connection, transaction))
        {
            await prune.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return purged;
    }
    // ── §5.13 Observer-delivery capability ────────────────────────────────────────
    //
    // The leaderless, at-least-once walk of the Transition Log, mirroring the In-Memory reference. The
    // same claim/lease spine as job claiming: the Observer's row in backwave.observers is held under FOR
    // UPDATE for the whole claim/report transaction, so exactly one node advances a given Observer's
    // cursor at a time. The global Position lives on job_transitions; the per-(Observer, Position) attempt/
    // resolution bookkeeping lives in backwave.observer_deliveries.

    /// <inheritdoc/>
    public async ValueTask<ObserverClaim> ClaimObserverDeliveriesAsync(
        ObserverClaimRequest request, CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await BeginAsync(connection, cancellationToken).ConfigureAwait(false);

        // Ensure the row exists, then lock it: the lock is what gives single delivery while staying
        // leaderless - concurrent claimers of one Observer serialize on FOR UPDATE, not a leader election.
        await using (var ensure = Cmd(
            "INSERT INTO backwave.observers (observer_id) SELECT :id FROM dual " +
            "WHERE NOT EXISTS (SELECT 1 FROM backwave.observers WHERE observer_id = :id)",
            connection, transaction))
        {
            ensure.Parameters.Add(Str("id", request.ObserverId));
            try
            {
                await ensure.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OracleException exception) when (IsDuplicate(exception))
            {
                // Two claimers of a brand-new Observer race the unlocked NOT EXISTS; the loser hits the PK.
                // The row now exists, so swallow and fall through to lock it.
            }
        }

        long cursor;
        string? leaseOwner;
        DateTimeOffset? leaseExpiry;
        await using (var locked = Cmd(
            "SELECT cursor_pos, lease_owner, lease_expiry FROM backwave.observers WHERE observer_id = :id FOR UPDATE",
            connection, transaction))
        {
            locked.Parameters.Add(Str("id", request.ObserverId));
            await using var reader = (OracleDataReader)await locked.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            cursor = reader.GetInt64(0);
            leaseOwner = reader.IsDBNull(1) ? null : reader.GetString(1);
            leaseExpiry = reader.IsDBNull(2) ? null : ReadTstz(reader, 2);
        }

        // Remember the subscription so cursor advance (on report) can tell matching rows from the ones this
        // Observer ignores. Run config - set every claim, never changes within a run. An empty state set
        // renders to '' which Oracle stores as NULL; the read maps it back.
        var states = request.States.Select(s => (int)s).ToArray();
        await using (var sub = Cmd(
            "UPDATE backwave.observers SET sub_states = :states, sub_wire_name = :wire, sub_queue = :queue " +
            "WHERE observer_id = :id",
            connection, transaction))
        {
            sub.Parameters.Add(Str("id", request.ObserverId));
            sub.Parameters.Add(StrN("states", string.Join(',', states)));
            sub.Parameters.Add(StrN("wire", request.WireName));
            sub.Parameters.Add(StrN("queue", request.Queue));
            await sub.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // A live Lease held by a different worker means that node is delivering - back off.
        if (leaseOwner is { } held
            && !string.Equals(held, request.WorkerId, StringComparison.Ordinal)
            && leaseExpiry > request.Now)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return ObserverClaim.None(request.ObserverId);
        }

        // Matching rows after the cursor, in Position order, not yet resolved. The head-of-line block (a
        // Pending row still in its backoff window) is detected in the loop below, not in SQL.
        var candidates = new List<ObserverClaimedDelivery>();
        await using (var scan = Cmd(
            $"""
            SELECT t.position, t.job_id, t.ordinal, j.wire_name, j.queue, t.state, t.attempt,
                   t.recorded_at, t.failure_detail, d.delivery_attempt, d.next_attempt_at
            FROM backwave.job_transitions t
            JOIN backwave.jobs j ON j.job_id = t.job_id
            LEFT JOIN backwave.observer_deliveries d ON d.observer_id = :id AND d.position = t.position
            WHERE t.position > :cursor
              AND t.state IN ({StatesInClause(states)})
              AND (:wire IS NULL OR j.wire_name = :wire)
              AND (:queue IS NULL OR j.queue = :queue)
              AND (d.resolution IS NULL OR d.resolution = 0)
            ORDER BY t.position
            FETCH FIRST :take ROWS ONLY
            """,
            connection, transaction))
        {
            scan.Parameters.Add(Str("id", request.ObserverId));
            scan.Parameters.Add(Long("cursor", cursor));
            scan.Parameters.Add(StrN("wire", request.WireName));
            scan.Parameters.Add(StrN("queue", request.Queue));
            scan.Parameters.Add(Int("take", Math.Max(0, request.MaxRows)));
            await using var reader = (OracleDataReader)await scan.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var nextAttemptAt = reader.IsDBNull(10) ? (DateTimeOffset?)null : ReadTstz(reader, 10);
                // Head-of-line: a row still in its backoff window holds the cursor - claim nothing past it,
                // so in-order-per-Observer falls out of the single moving cursor.
                if (nextAttemptAt is { } next && next > request.Now)
                {
                    break;
                }
                var priorAttempt = reader.IsDBNull(9) ? 0 : reader.GetInt32(9);
                candidates.Add(new ObserverClaimedDelivery(
                    reader.GetInt64(0), ReadGuid(reader, 1), reader.GetInt64(2), reader.GetString(3), reader.GetString(4),
                    (JobState)reader.GetInt32(5), reader.GetInt32(6), ReadTstz(reader, 7),
                    reader.IsDBNull(8) ? null : reader.GetString(8), priorAttempt + 1)); // the claim starts a delivery Attempt
            }
        }

        if (candidates.Count == 0)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return ObserverClaim.None(request.ObserverId);
        }

        foreach (var delivery in candidates)
        {
            await using var upsert = Cmd(
                """
                MERGE INTO backwave.observer_deliveries t
                USING (SELECT :id AS observer_id, :pos AS position FROM dual) s
                ON (t.observer_id = s.observer_id AND t.position = s.position)
                WHEN MATCHED THEN UPDATE SET delivery_attempt = :attempt, resolution = 0, next_attempt_at = NULL
                WHEN NOT MATCHED THEN INSERT (observer_id, position, delivery_attempt, resolution, next_attempt_at)
                    VALUES (:id, :pos, :attempt, 0, NULL)
                """,
                connection, transaction);
            upsert.Parameters.Add(Str("id", request.ObserverId));
            upsert.Parameters.Add(Long("pos", delivery.Position));
            upsert.Parameters.Add(Int("attempt", delivery.DeliveryAttempt));
            await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var lease = Cmd(
            "UPDATE backwave.observers SET lease_owner = :worker, lease_expiry = :expiry WHERE observer_id = :id",
            connection, transaction))
        {
            lease.Parameters.Add(Str("id", request.ObserverId));
            lease.Parameters.Add(Str("worker", request.WorkerId));
            lease.Parameters.Add(Tstz("expiry", request.Now + request.LeaseDuration));
            await lease.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ObserverClaim(request.ObserverId, Acquired: true, candidates);
    }

    /// <inheritdoc/>
    public async ValueTask ReportObserverDeliveriesAsync(
        ObserverDeliveryReport report, CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await BeginAsync(connection, cancellationToken).ConfigureAwait(false);

        long cursor = 0;
        string? leaseOwner = null;
        DateTimeOffset? leaseExpiry = null;
        int[] states = [];
        string? wireName = null;
        string? queue = null;
        bool found;
        await using (var locked = Cmd(
            "SELECT cursor_pos, lease_owner, lease_expiry, sub_states, sub_wire_name, sub_queue " +
            "FROM backwave.observers WHERE observer_id = :id FOR UPDATE",
            connection, transaction))
        {
            locked.Parameters.Add(Str("id", report.ObserverId));
            await using var reader = (OracleDataReader)await locked.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            found = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (found)
            {
                cursor = reader.GetInt64(0);
                leaseOwner = reader.IsDBNull(1) ? null : reader.GetString(1);
                leaseExpiry = reader.IsDBNull(2) ? null : ReadTstz(reader, 2);
                states = ParseStates(reader.IsDBNull(3) ? string.Empty : reader.GetString(3));
                wireName = reader.IsDBNull(4) ? null : reader.GetString(4);
                queue = reader.IsDBNull(5) ? null : reader.GetString(5);
            }
        }

        if (!found)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return; // unknown Observer - nothing claimed, nothing to resolve
        }

        // Fence: only the live claim-Lease holder may resolve deliveries and advance the cursor. A stale
        // survivor of a lapsed claim reports into the void - at-least-once intact.
        if (!string.Equals(leaseOwner, report.WorkerId, StringComparison.Ordinal) || leaseExpiry <= report.Now)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        foreach (var outcome in report.Outcomes)
        {
            var resolution = outcome.Disposition switch
            {
                ObserverDeliveryDisposition.Delivered => 1,
                ObserverDeliveryDisposition.DeadLettered => 2,
                _ => 0, // Retry: held, the cursor will stall on it
            };
            await using var resolve = Cmd(
                "UPDATE backwave.observer_deliveries SET resolution = :resolution, next_attempt_at = :next " +
                "WHERE observer_id = :id AND position = :pos",
                connection, transaction);
            resolve.Parameters.Add(Str("id", report.ObserverId));
            resolve.Parameters.Add(Long("pos", outcome.Position));
            resolve.Parameters.Add(Int("resolution", resolution));
            resolve.Parameters.Add(TstzN("next",
                outcome.Disposition == ObserverDeliveryDisposition.Retry ? outcome.NextAttemptAt : null));
            await resolve.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await AdvanceObserverCursorAsync(
            connection, transaction, report.ObserverId, cursor, states, wireName, queue, report.Now, cancellationToken)
            .ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sweeps the cursor forward over the contiguous prefix of resolved matching rows - and over every
    /// non-matching row, which needs no delivery - stopping at the first matching row still Pending (the
    /// head-of-line block). A dead-lettered row is recorded loudly as the cursor passes it. The set-based
    /// analogue of the In-Memory reference's row-by-row sweep; the caller holds the row lock.
    /// </summary>
    private async Task AdvanceObserverCursorAsync(
        OracleConnection connection, OracleTransaction transaction, string observerId, long cursor,
        int[] states, string? wireName, string? queue, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // The first matching row after the cursor still unresolved - the cursor cannot pass it.
        long? block;
        await using (var blockCommand = Cmd(
            $"""
            SELECT MIN(t.position)
            FROM backwave.job_transitions t
            JOIN backwave.jobs j ON j.job_id = t.job_id
            LEFT JOIN backwave.observer_deliveries d ON d.observer_id = :id AND d.position = t.position
            WHERE t.position > :cursor
              AND t.state IN ({StatesInClause(states)})
              AND (:wire IS NULL OR j.wire_name = :wire)
              AND (:queue IS NULL OR j.queue = :queue)
              AND (d.resolution IS NULL OR d.resolution = 0)
            """,
            connection, transaction))
        {
            blockCommand.Parameters.Add(Str("id", observerId));
            blockCommand.Parameters.Add(Long("cursor", cursor));
            blockCommand.Parameters.Add(StrN("wire", wireName));
            blockCommand.Parameters.Add(StrN("queue", queue));
            var result = await blockCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            block = result is null or DBNull ? null : Convert.ToInt64(result);
        }

        // The cursor sweeps to the last Position before the block (or to the end if nothing blocks).
        long? newCursor;
        await using (var advance = Cmd(
            "SELECT MAX(position) FROM backwave.job_transitions WHERE position > :cursor AND (:block IS NULL OR position < :block)",
            connection, transaction))
        {
            advance.Parameters.Add(Long("cursor", cursor));
            advance.Parameters.Add(LongN("block", block));
            var result = await advance.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            newCursor = result is null or DBNull ? null : Convert.ToInt64(result);
        }

        if (newCursor is not { } target || target <= cursor)
        {
            return; // nothing to sweep - the block (or the absence of new rows) holds the cursor
        }

        // Record dead-lettered rows the cursor is about to pass - loudly, never silently dropped.
        await using (var deadLetter = Cmd(
            """
            INSERT INTO backwave.observer_dead_letters
                (observer_id, position, job_id, ordinal, state, attempt, delivery_attempts, dead_lettered_at)
            SELECT :id, t.position, t.job_id, t.ordinal, t.state, t.attempt, d.delivery_attempt, :now
            FROM backwave.job_transitions t
            JOIN backwave.observer_deliveries d ON d.observer_id = :id AND d.position = t.position
            WHERE t.position > :cursor AND t.position <= :target AND d.resolution = 2
              AND NOT EXISTS (
                  SELECT 1 FROM backwave.observer_dead_letters x WHERE x.observer_id = :id AND x.position = t.position)
            """,
            connection, transaction))
        {
            deadLetter.Parameters.Add(Str("id", observerId));
            deadLetter.Parameters.Add(Long("cursor", cursor));
            deadLetter.Parameters.Add(Long("target", target));
            deadLetter.Parameters.Add(Tstz("now", now));
            await deadLetter.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // The swept rows are all resolved now - drop their in-flight bookkeeping.
        await using (var sweep = Cmd(
            "DELETE FROM backwave.observer_deliveries WHERE observer_id = :id AND position <= :target",
            connection, transaction))
        {
            sweep.Parameters.Add(Str("id", observerId));
            sweep.Parameters.Add(Long("target", target));
            await sweep.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var move = Cmd(
            "UPDATE backwave.observers SET cursor_pos = :target WHERE observer_id = :id",
            connection, transaction))
        {
            move.Parameters.Add(Str("id", observerId));
            move.Parameters.Add(Long("target", target));
            await move.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Renders a JobState int list as a SQL IN body; an empty subscription matches nothing.</summary>
    private static string StatesInClause(int[] states) => states.Length == 0 ? "NULL" : string.Join(',', states);

    private static int[] ParseStates(string states) => states.Length == 0
        ? []
        : states.Split(',').Select(int.Parse).ToArray();

    /// <inheritdoc/>
    public async ValueTask<long> GetObserverCursorAsync(string observerId, CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Cmd(
            "SELECT cursor_pos FROM backwave.observers WHERE observer_id = :id", connection);
        command.Parameters.Add(Str("id", observerId));
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is null or DBNull ? -1L : Convert.ToInt64(result);
    }

    /// <inheritdoc/>
    public async ValueTask<ObserverLag> GetObserverLagAsync(
        ObserverLagRequest request, CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        var states = request.States.Select(s => (int)s).ToArray();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        // Subscription-aware backlog: matching transitions the durable cursor has not advanced past. The
        // cursor is -1 when the observer has never delivered, so every matching row counts. The cur CTE
        // needs a FROM dual for the scalar-subquery projection.
        await using var command = Cmd(
            $"""
            WITH cur AS (
                SELECT NVL((SELECT cursor_pos FROM backwave.observers WHERE observer_id = :id), -1) AS pos FROM dual
            )
            SELECT c.pos, agg.cnt, agg.oldest
            FROM cur c
            CROSS JOIN (
                SELECT COUNT(t.position) AS cnt, MIN(t.recorded_at) AS oldest
                FROM backwave.job_transitions t
                JOIN backwave.jobs j ON j.job_id = t.job_id
                WHERE t.position > (SELECT pos FROM cur)
                  AND t.state IN ({StatesInClause(states)})
                  AND (:wire IS NULL OR j.wire_name = :wire)
                  AND (:queue IS NULL OR j.queue = :queue)
            ) agg
            """,
            connection);
        command.Parameters.Add(Str("id", request.ObserverId));
        command.Parameters.Add(StrN("wire", request.WireName));
        command.Parameters.Add(StrN("queue", request.Queue));

        await using var reader = (OracleDataReader)await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        var oldest = reader.IsDBNull(2) ? (DateTimeOffset?)null : ReadTstz(reader, 2);
        return new ObserverLag(reader.GetInt64(0), (int)reader.GetInt64(1), oldest);
    }

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<ObserverDeadLetterRecord>> ListObserverDeadLettersAsync(
        string observerId, CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Cmd(
            """
            SELECT position, job_id, ordinal, state, attempt, delivery_attempts, dead_lettered_at
            FROM backwave.observer_dead_letters WHERE observer_id = :id ORDER BY position
            """,
            connection);
        command.Parameters.Add(Str("id", observerId));

        var records = new List<ObserverDeadLetterRecord>();
        await using var reader = (OracleDataReader)await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            records.Add(new ObserverDeadLetterRecord(
                reader.GetInt64(0), ReadGuid(reader, 1), reader.GetInt64(2), (JobState)reader.GetInt32(3),
                reader.GetInt32(4), reader.GetInt32(5), ReadTstz(reader, 6)));
        }
        return records;
    }
    // ── row mapping ───────────────────────────────────────────────────────────────

    // The jobs columns in reader order. job_mode and sequence are plain identifiers here (SqlServer needs
    // [sequence]/mode); Oracle has no reserved-word clash for these, and the schema names the mode column
    // job_mode to steer clear of MODE.
    private const string JobColumns =
        "job_id, wire_name, payload, queue, state, due_time, attempt, lease_owner, lease_expiry, " +
        "cancel_requested, terminal_at, terminal_cause, schedule_id, parents_remaining, job_mode, trace_context, " +
        "sequence, workflow_id";

    private static JobRecord ReadJob(OracleDataReader reader) => new()
    {
        JobId = ReadGuid(reader, 0),
        WireName = reader.GetString(1),
        Payload = ReadBytes(reader, 2),
        Queue = reader.GetString(3),
        State = (JobState)reader.GetInt32(4),
        DueTime = ReadTstz(reader, 5),
        Attempt = reader.GetInt32(6),
        LeaseOwner = reader.IsDBNull(7) ? null : reader.GetString(7),
        LeaseExpiry = reader.IsDBNull(8) ? null : ReadTstz(reader, 8),
        CancelRequested = reader.GetInt32(9) != 0,
        TerminalAt = reader.IsDBNull(10) ? null : ReadTstz(reader, 10),
        TerminalCause = reader.IsDBNull(11) ? null : reader.GetString(11),
        ScheduleId = reader.IsDBNull(12) ? null : reader.GetString(12),
        ParentsRemaining = reader.GetInt32(13),
        Mode = (DependencyMode)reader.GetInt32(14),
        TraceContext = reader.IsDBNull(15) ? null : reader.GetString(15),
        Sequence = reader.GetInt64(16),
        WorkflowId = reader.IsDBNull(17) ? null : ReadGuid(reader, 17),
    };

    // ── Job Tags ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Inserts a Tag set into job_tags within the caller's transaction. Tags are already a set upstream
    /// (JobTags collapses duplicates), so the insert is idempotent-by-construction: a duplicate
    /// (job_id, key, value) converges to the existing row rather than throwing. A Label's key is the
    /// empty-string sentinel; Oracle folds an empty string to NULL, and key/value are NOT NULL primary-key
    /// columns, so an empty key or value is encoded to a CHR(1) sentinel on write and decoded back on read.
    /// </summary>
    private async Task InsertTagsAsync(
        OracleConnection connection, OracleTransaction transaction, Guid jobId, JobTags tags,
        CancellationToken cancellationToken)
    {
        foreach (var tag in tags)
        {
            // A Tag is being written on THIS process, so latch the tags-in-use signal: every later claim
            // now hydrates without waiting for the periodic probe to notice.
            _tagsInUse = true;
            await using var insert = Cmd(
                """
                INSERT INTO backwave.job_tags (job_id, key, value)
                SELECT :id, :key, :value FROM dual
                WHERE NOT EXISTS (
                    SELECT 1 FROM backwave.job_tags WHERE job_id = :id AND key = :key AND value = :value)
                """,
                connection, transaction);
            insert.Parameters.Add(Raw("id", jobId));
            insert.Parameters.Add(Str("key", EncodeTag(tag.Key)));
            insert.Parameters.Add(Str("value", EncodeTag(tag.Value)));
            try
            {
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OracleException exception) when (IsDuplicate(exception))
            {
                // The unlocked NOT EXISTS does not serialize two concurrent writers of the same
                // (job_id, key, value): both can pass it and the loser then hits the primary key. The key
                // is the arbiter - swallowing it converges idempotently, exactly as the jobs-insert catch does.
            }
        }
    }

    // Reads the Tags for a batch of jobs in one round-trip (job_id IN (...)) - never N+1. Reconstructs each
    // set with the empty-key => Label discriminator, decoding the CHR(1) sentinel back to empty. Jobs with
    // no Tags are simply absent from the map.
    private async Task<Dictionary<Guid, JobTags>> HydrateTagsAsync(
        OracleConnection connection, IReadOnlyList<Guid> jobIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, JobTags>();
        if (jobIds.Count == 0)
        {
            return result;
        }
        await using var command = Cmd(
            $"SELECT job_id, key, value FROM backwave.job_tags WHERE job_id IN ({ParameterList("id", jobIds.Count)})",
            connection);
        AddIdList(command, "id", jobIds);
        await using var reader = (OracleDataReader)await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var jobId = ReadGuid(reader, 0);
            var key = DecodeTag(reader.GetString(1));
            var value = DecodeTag(reader.GetString(2));
            var tag = key.Length == 0 ? JobTag.Label(value) : JobTag.Keyed(key, value);
            result[jobId] = (result.TryGetValue(jobId, out var existing) ? existing : JobTags.Empty).With(tag);
        }
        return result;
    }

    /// <summary>Returns the jobs with their Tags hydrated in one batched read (never N+1).</summary>
    private async Task<IReadOnlyList<JobRecord>> WithTagsAsync(
        OracleConnection connection, IReadOnlyList<JobRecord> jobs, CancellationToken cancellationToken)
    {
        if (jobs.Count == 0)
        {
            return jobs;
        }
        var tags = await HydrateTagsAsync(connection, [.. jobs.Select(j => j.JobId)], cancellationToken)
            .ConfigureAwait(false);
        return [.. jobs.Select(j => j with { Tags = tags.TryGetValue(j.JobId, out var set) ? set : JobTags.Empty })];
    }

    // Oracle stores an empty string as NULL, but key/value are NOT NULL PK columns, so an empty Tag key or
    // value (a Label carries an empty key) is stored as the CHR(1) control character and decoded back.
    private static string EncodeTag(string value) => value.Length == 0 ? "" : value;

    private static string DecodeTag(string value) => value == "" ? string.Empty : value;

    // ":p0, :p1, ..." - ODP.NET has no array parameters; the lists are bounded.
    private static string ParameterList(string prefix, int count)
        => string.Join(", ", Enumerable.Range(0, count).Select(i => $":{prefix}{i}"));

    private static void AddIdList(OracleCommand command, string prefix, IReadOnlyList<Guid> ids)
    {
        for (var i = 0; i < ids.Count; i++)
        {
            command.Parameters.Add(Raw($"{prefix}{i}", ids[i]));
        }
    }

    private static IReadOnlyList<DateTimeOffset> ParseSkippedTicks(string json)
    {
        using var document = JsonDocument.Parse(json);
        return [.. document.RootElement.EnumerateArray().Select(e => e.GetDateTimeOffset())];
    }

    private static string RenderSkippedTicks(IReadOnlyList<DateTimeOffset> ticks)
        => "[" + string.Join(",", ticks.Select(t => $"\"{t.ToUniversalTime():O}\"")) + "]";

    // ── §8 Wake-Up Hints ────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task<IAsyncDisposable> SubscribeAsync(
        Action<string> onHint, CancellationToken cancellationToken = default)
    {
        if (!options.EnableWakeUpHints)
        {
            // Feature off: hand back a no-op so the pump subscribes to nothing and stays on the poll
            // interval, exactly as an adapter that does not implement IWakeUpHintSource would.
            return Task.FromResult<IAsyncDisposable>(NoopSubscription.Instance);
        }
        var subscription = new HintSubscription(options.ConnectionString, _schema.HintAlertName, onHint, _logger);
        subscription.Start();
        return Task.FromResult<IAsyncDisposable>(subscription);
    }

    private sealed class NoopSubscription : IAsyncDisposable
    {
        public static readonly NoopSubscription Instance = new();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // A dedicated DBMS_ALERT waiting session, the Oracle analog to the Postgres LISTEN connection. Channel
    // loss is a latency event, never a correctness event: the loop reconnects forever until
    // disposed, and while it is down polling carries everything at the poll interval. It logs the first
    // fault after a healthy registration, so a missing EXECUTE grant is visible without flooding the log.
    private sealed class HintSubscription(
        string connectionString, string alertName, Action<string> onHint, ILogger logger) : IAsyncDisposable
    {
        // Named bound: how long a dead hint channel waits before it redials.
        private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);

        // The dispose bound for the waiter. A cancellation token does NOT break a parked DBMS_ALERT.WAITONE:
        // ODP.NET surfaces ORA-01013 only after the server-side wait ends, so this timeout, not the token, is
        // what lets DisposeAsync return. Keep it small - one idle round-trip per second per waiting pump is
        // the price of a prompt shutdown.
        private const int WaitTimeoutSeconds = 1;

        private readonly CancellationTokenSource _stop = new();
        private Task _loop = Task.CompletedTask;
        private bool _faultLogged;

        public void Start() => _loop = RunAsync(_stop.Token);

        private async Task RunAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                OracleConnection? connection = null;
                try
                {
                    connection = new OracleConnection(connectionString);
                    await connection.OpenAsync(token).ConfigureAwait(false);
                    await RegisterAsync(connection, token).ConfigureAwait(false);
                    // A healthy registration re-arms the one-shot fault log for the next outage.
                    _faultLogged = false;
                    while (!token.IsCancellationRequested)
                    {
                        var (status, message) = await WaitOneAsync(connection, token).ConfigureAwait(false);
                        // status 0 is an alert (message is the Queue); status 1 is the WAITONE timeout.
                        if (status == 0 && !string.IsNullOrEmpty(message))
                        {
                            onHint(message);
                        }
                    }
                }
                catch (Exception exception)
                {
                    // Cancellation can surface as OperationCanceledException or as an Oracle break
                    // (ORA-01013) on the parked WAITONE; either way, a requested stop ends the loop.
                    if (token.IsCancellationRequested)
                    {
                        return;
                    }
                    if (!_faultLogged)
                    {
                        _faultLogged = true;
                        BackWaveLog.WakeHintChannelUnavailable(logger, "oracle", exception);
                    }
                    try
                    {
                        await Task.Delay(ReconnectDelay, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
                finally
                {
                    if (connection is not null)
                    {
                        try
                        {
                            await connection.DisposeAsync().ConfigureAwait(false);
                        }
                        catch
                        {
                            // A dead connection on the way down is nothing to act on.
                        }
                    }
                }
            }
        }

        private async Task RegisterAsync(OracleConnection connection, CancellationToken token)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "BEGIN DBMS_ALERT.REGISTER(:name); END;";
            command.BindByName = true;
            command.Parameters.Add(new OracleParameter("name", OracleDbType.Varchar2) { Value = alertName });
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }

        private async Task<(int Status, string Message)> WaitOneAsync(
            OracleConnection connection, CancellationToken token)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "BEGIN DBMS_ALERT.WAITONE(:name, :msg, :status, :timeout); END;";
            command.BindByName = true;
            command.Parameters.Add(new OracleParameter("name", OracleDbType.Varchar2) { Value = alertName });
            var message = new OracleParameter("msg", OracleDbType.Varchar2, 1800)
            {
                Direction = System.Data.ParameterDirection.Output,
            };
            command.Parameters.Add(message);
            var status = new OracleParameter("status", OracleDbType.Int32)
            {
                Direction = System.Data.ParameterDirection.Output,
            };
            command.Parameters.Add(status);
            command.Parameters.Add(new OracleParameter("timeout", OracleDbType.Int32) { Value = WaitTimeoutSeconds });
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);

            var statusCode = ((OracleDecimal)status.Value).ToInt32();
            var payload = message.Value is OracleString text && !text.IsNull ? text.Value : string.Empty;
            return (statusCode, payload);
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync().ConfigureAwait(false);
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch
            {
                // The loop ends on cancellation; any exception on the way down is part of shutdown.
            }
            _stop.Dispose();
        }
    }

    // ── parameter and reader helpers ──────────────────────────────────────────────
    //
    // Every parameter is bound by name (Cmd sets BindByName), so a distinct :name that recurs in a
    // statement is added once and reused. Guids map to RAW(16) via ToByteArray; DateTimeOffset maps to
    // TIMESTAMP WITH TIME ZONE normalized to UTC (stored with a +00:00 zone), read back with
    // GetOracleTimeStampTZ so the offset survives - ExecuteScalar/GetDateTime would drop it.

    private static OracleParameter Raw(string name, Guid value)
        => new(name, OracleDbType.Raw) { Size = 16, Value = value.ToByteArray() };

    private static OracleParameter RawN(string name, Guid? value)
        => new(name, OracleDbType.Raw) { Size = 16, Value = value.HasValue ? value.Value.ToByteArray() : (object)DBNull.Value };

    private static OracleParameter Str(string name, string value)
        => new(name, OracleDbType.Varchar2) { Value = value };

    private static OracleParameter StrN(string name, string? value)
        => new(name, OracleDbType.Varchar2) { Value = (object?)value ?? DBNull.Value };

    private static OracleParameter Clob(string name, string? value)
        => new(name, OracleDbType.Clob) { Value = (object?)value ?? DBNull.Value };

    private static OracleParameter Blob(string name, ReadOnlyMemory<byte> value)
        => new(name, OracleDbType.Blob) { Value = value.ToArray() };

    private static OracleParameter Int(string name, int value)
        => new(name, OracleDbType.Int32) { Value = value };

    private static OracleParameter IntN(string name, int? value)
        => new(name, OracleDbType.Int32) { Value = (object?)value ?? DBNull.Value };

    private static OracleParameter Long(string name, long value)
        => new(name, OracleDbType.Int64) { Value = value };

    private static OracleParameter LongN(string name, long? value)
        => new(name, OracleDbType.Int64) { Value = (object?)value ?? DBNull.Value };

    private static OracleParameter Tstz(string name, DateTimeOffset value)
        => new(name, OracleDbType.TimeStampTZ) { Value = ToTstz(value) };

    private static OracleParameter TstzN(string name, DateTimeOffset? value)
        => new(name, OracleDbType.TimeStampTZ) { Value = value is { } instant ? ToTstz(instant) : (object)DBNull.Value };

    // Normalize to UTC and hand ODP.NET an Unspecified-kind DateTime with an explicit +00:00 zone; the
    // constructor rejects a UTC-kind DateTime, and DateTimeOffset.DateTime is always Unspecified.
    private static OracleTimeStampTZ ToTstz(DateTimeOffset value)
        => new(value.ToUniversalTime().DateTime, "+00:00");

    private static Guid ReadGuid(OracleDataReader reader, int ordinal)
        => new(reader.GetOracleBinary(ordinal).Value);

    private static DateTimeOffset ReadTstz(OracleDataReader reader, int ordinal)
    {
        var timestamp = reader.GetOracleTimeStampTZ(ordinal);
        return new DateTimeOffset(timestamp.Value, timestamp.GetTimeZoneOffset());
    }

    private static byte[] ReadBytes(OracleDataReader reader, int ordinal)
    {
        using var blob = reader.GetOracleBlob(ordinal);
        return blob.Value;
    }
}
