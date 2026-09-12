using System.Collections.Concurrent;
using System.Data;
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

    // The prefetch size for a LOB column the store puts no explicit byte cap on: a Workflow name, a
    // schedule's skipped-tick list. Borrowed from the failure-detail cap, the largest bound the store
    // does enforce on a text column, so an uncapped column still gets a bounded buffer. A longer value
    // reads correctly through the locator at the cost of one trip.
    private int UncappedTextPrefetchBytes => options.Bounds.MaxFailureDetailBytes;

    // Runs a read whose result set carries LOB columns, with both halves of the LOB prefetch set
    // together. Left at its default of 0, InitialLOBFetchSize makes the driver hand back a locator per
    // LOB value, and following it costs a round trip per value - the cost that made a 200-row job page
    // 400 round trips. Setting it alone does not help: FetchSize is a BYTE budget, a prefetched row is
    // orders of magnitude larger, and the fetch array collapses to a single row per trip, trading LOB
    // trips for fetch trips one for one. So the two are set in one place and no call site can get one
    // half right and the other wrong.
    //
    // `prefetchBytes` is the size cap the store already enforces on the largest LOB column the statement
    // selects - bytes for a BLOB, characters for a CLOB. A value over it still reads correctly: the
    // driver falls back to the locator and pays one trip, so this is a latency knob and never a
    // correctness one.
    //
    // `rows` is how many rows the window should hold. RowSize is the driver's own per-row buffer size for
    // this statement, and it cannot exceed the LOB columns at their caps plus the scalars, so the bytes
    // one read command holds in flight are bounded by the row SHAPE and this count - never by how many
    // rows the query returns. That is what keeps a 200-row monitor page from asking for a 26 MB buffer:
    // the page arrives in windows of LobFetchWindowRows rows instead. A read that can match at most one
    // row passes SingleRow, so a primary-key lookup buys no buffer it cannot fill.
    private async Task<OracleDataReader> ExecuteLobReaderAsync(
        OracleCommand command, int prefetchBytes, int rows, CancellationToken cancellationToken)
    {
        command.InitialLOBFetchSize = prefetchBytes;
        var reader = (OracleDataReader)await command.ExecuteReaderCountedAsync(cancellationToken).ConfigureAwait(false);
        // Never shrink the driver's own default; a row too small to reach it fetches as widely as before.
        reader.FetchSize = Math.Max(
            reader.FetchSize, Math.Min((long)reader.RowSize * rows, MaxFetchWindowBytes));
        OracleRoundTrips.RecordFetchWindow(reader.FetchSize);
        return reader;
    }

    // How many rows a page read buffers per fetch trip. Borrowed from the claim batch, the size class the
    // store already treats as one unit of work.
    private int LobFetchWindowRows => options.Bounds.MaxClaimBatch;

    // A read that can match at most one row - a primary-key lookup - wants no more window than that.
    private const int SingleRow = 1;

    // The hard ceiling on one read command's fetch window, whatever the row shape works out to. The
    // window above is derived from RowSize and the store's own size bounds, and every one of those bounds
    // is operator-settable with no validation, so the derivation alone cannot promise a bounded buffer:
    // raising MaxPayloadBytes to 64 MB would have a claim ask the driver to hold 4 GB. This is the
    // backstop that binds regardless. 8 MB sits well above the default job page (about 4.5 MB), so no
    // default configuration ever reaches it, and well below a size that would matter to a host.
    internal const long MaxFetchWindowBytes = 8L * 1024 * 1024;

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
                await using var reader = (OracleDataReader)await parent.ExecuteReaderCountedAsync(cancellationToken).ConfigureAwait(false);
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
            if (await insert.ExecuteNonQueryCountedAsync(cancellationToken).ConfigureAwait(false) == 0)
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
            await edge.ExecuteNonQueryCountedAsync(cancellationToken).ConfigureAwait(false);
        }

        // Job Tags: the enqueue-time set, in this same transaction so they are visible exactly when the
        // job is - and rolled back with it under Transactional Enqueue.
        await InsertTagsAsync(connection, transaction, [(job.JobId, job.Tags)], cancellationToken)
            .ConfigureAwait(false);

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
            await signal.ExecuteNonQueryCountedAsync(cancellationToken).ConfigureAwait(false);
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
                    await using var reader = await limit.ExecuteReaderCountedAsync(cancellationToken).ConfigureAwait(false);
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
                var leasedCount = await leased.ExecuteScalarCountedAsync(cancellationToken).ConfigureAwait(false);
                if (leasedCount is null or DBNull)
                {
                    throw new InvariantViolationException(
                        InvariantTrigger.LeasedCountAggregateNull,
                        $"The leased-count aggregate for queue '{queue}' returned no value; COUNT(*) always returns one.");
                }
                slots = limitValue - Convert.ToInt32(leasedCount);
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
                await using var reader = await ExecuteLobReaderAsync(claim, options.Bounds.MaxPayloadBytes, LobFetchWindowRows, cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var job = ReadJob(reader);
                    // Oracle has no multi-row RETURNING, so the claim reads the row BEFORE it leases it.
                    // The post-lease state is therefore synthesized in memory and cannot be checked; what
                    // can be is that the row honored the statement's own state = 0 predicate, which
                    // FOR UPDATE re-evaluates after it takes the lock.
                    if (job.State != JobState.Scheduled)
                    {
                        throw new InvariantViolationException(
                            InvariantTrigger.ClaimedRowNotEligible,
                            $"Claim locked job {job.JobId} in state {job.State}; the statement selects only state Scheduled.");
                    }
                    queueClaims.Add(job);
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
                    var leasedRows = await update.ExecuteNonQueryCountedAsync(cancellationToken).ConfigureAwait(false);
                    if (leasedRows != queueClaims.Count)
                    {
                        throw new InvariantViolationException(
                            InvariantTrigger.UnexpectedAffectedRowCount,
                            $"The lease write affected {leasedRows} rows for {queueClaims.Count} ids this transaction already holds under FOR UPDATE.");
                    }
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
        await using var reader = (OracleDataReader)await cmd.ExecuteReaderCountedAsync(cancellationToken).ConfigureAwait(false);
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
        var present = await probe.ExecuteScalarCountedAsync(cancellationToken).ConfigureAwait(false) is not null;
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
                await ensure.ExecuteNonQueryCountedAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OracleException exception) when (IsDuplicate(exception))
            {
                // Another party inserted the anchor first; the FOR UPDATE below locks their row.
            }
        }

        await using var applock = Cmd(
            "SELECT queue FROM backwave.queue_locks WHERE queue = :queue FOR UPDATE", connection, transaction);
        applock.Parameters.Add(Str("queue", queue));
        await using var reader = await applock.ExecuteReaderCountedAsync(cancellationToken).ConfigureAwait(false);
        var anchored = false;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            anchored = true;
        }
        if (!anchored)
        {
            throw new InvariantViolationException(
                InvariantTrigger.QueueConfigLockNotAcquired,
                $"The queue-config anchor for '{queue}' returned no row, so FOR UPDATE locked nothing and the lock was never taken.");
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

        if (await update.ExecuteNonQueryCountedAsync(cancellationToken).ConfigureAwait(false) == 0)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return OutcomeResult.StaleLease; // the (workerId, attempt) fence
        }
        var newState = (JobState)newStateValue;

        // Job Tags delta: the runtime Tags the handler buffered ride the SAME fenced transaction - applied
        // only because the fence held. Effect-Once; set semantics make re-adding an identical Tag a no-op.
        if (addedTags is { Count: > 0 })
        {
            await InsertTagsAsync(connection, transaction, [(jobId, addedTags)], cancellationToken)
                .ConfigureAwait(false);
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

        // The per-row target the fence applies if it holds. Success -> Succeeded (3, terminal now);
        // Failure with a retry instant -> Scheduled (0, due then, NOT terminal); Failure at the ceiling
        // -> Dead-Lettered (5); Cancelled -> 4; Unroutable -> Quarantined (6). The cause rides terminal
        // failures/cancel/unroutable; due rides retry.
        var count = batch.Count;
        var targets = new (int State, string? Cause, DateTimeOffset? Due, DateTimeOffset? TerminalAt)[count];
        for (var i = 0; i < count; i++)
        {
            targets[i] = batch[i].Outcome switch
            {
                JobOutcome.Success => (3, null, null, now),
                JobOutcome.Failure { NextDueTime: { } retryAt } => (0, null, retryAt, null),
                JobOutcome.Failure failure => (5, failure.Error, null, now),
                JobOutcome.Cancelled cancelled => (4, cancelled.Cause, null, now),
                JobOutcome.Unroutable unroutable => (6, unroutable.Reason, null, now),
                _ => throw new ArgumentOutOfRangeException(nameof(batch)),
            };
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await BeginAsync(connection, cancellationToken).ConfigureAwait(false);

        // Effect-Once, split into a read and a write that a row lock joins. Oracle hands back no per-row
        // verdict for a multi-row write - there is no OUTPUT, and RETURNING carries no set - so the fence
        // is READ first and applied second. That read is the one round trip this path ADDS; it replaces
        // one UPDATE per row.
        //
        // FOR UPDATE takes the row locks the per-row UPDATE took and holds them to commit, so state,
        // lease owner, attempt, and lease expiry cannot move between the verdict and the write. A lease
        // expiring concurrently either lands before this read, in which case the row does not come back
        // and the outcome reports StaleLease, or waits behind the lock until this transaction ends. The
        // batch-wide half of the fence - Leased, lease still live at :now - sits in the read's WHERE, so
        // a row that is already stale is never locked at all. The per-row half - worker and attempt -
        // is compared below against the values the locked row returned, because Oracle rejects FOR
        // UPDATE on any query that mentions JSON_TABLE (ORA-01786) and the two vary per row.
        //
        // Moving that per-row half out of the WHERE widens what this read locks. A job still leased to a
        // DIFFERENT worker satisfies the batch-wide half, so it is locked here and held to commit, where
        // the old per-row UPDATE matched no row and took no lock at all. The verdict below still refuses
        // the write, so the only cost is contention - but two nodes reporting overlapping batches can now
        // block each other, which is why the ids are sorted. A fixed order across nodes turns what would
        // be a deadlock into a wait.
        var ids = (IReadOnlyList<Guid>)[.. batch.Select(row => row.JobId).Distinct().Order()];
        var live = new Dictionary<Guid, (string? Owner, int Attempt)>(ids.Count);
        await using (var fence = Cmd(
            $"""
            SELECT job_id, lease_owner, attempt FROM backwave.jobs
            WHERE job_id IN ({ParameterList("j", ids.Count)}) AND state = 2 AND lease_expiry > :now
            FOR UPDATE
            """,
            connection, transaction))
        {
            AddIdList(fence, "j", ids);
            fence.Parameters.Add(Tstz("now", now));
            await using var reader = (OracleDataReader)await fence
                .ExecuteReaderCountedAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                live[ReadGuid(reader, 0)] = (reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetInt32(2));
            }
        }

        // The verdict, per batch row, against the locked values. A job named twice in one batch resolves
        // as the per-row loop resolved it: the FIRST matching row wins the write, and every row naming
        // that job reports Applied.
        var matched = new Dictionary<Guid, int>(ids.Count);
        var writes = new List<OutcomeRow>(ids.Count);
        for (var i = 0; i < count; i++)
        {
            var report = batch[i];
            if (!live.TryGetValue(report.JobId, out var leased)
                || !string.Equals(leased.Owner, report.WorkerId, StringComparison.Ordinal)
                || leased.Attempt != report.Attempt
                || !matched.TryAdd(report.JobId, targets[i].State))
            {
                continue;
            }
            writes.Add(new OutcomeRow(
                Convert.ToHexString(report.JobId.ToByteArray()), report.WorkerId, report.Attempt,
                targets[i].State, targets[i].Cause, Iso(targets[i].Due), Iso(targets[i].TerminalAt)));
        }

        // One set-based write over the matched rows. MERGE rather than an updatable join, because Oracle
        // cannot key-preserve a join to JSON_TABLE; the USING set carries each row's target state and
        // per-state columns. The fence is repeated in the WHEN MATCHED filter, so the database still
        // authorizes every write - the verdict above only decides what the caller is told. due_time
        // moves only for a retry row (COALESCE keeps it otherwise); cancel_requested clears only for a
        // Cancelled row (CASE); terminal_at and terminal_cause carry per row and are null for a retry.
        // Both instants travel as ISO text under an explicit format. A JSON_TABLE column declared
        // TIMESTAMP WITH TIME ZONE takes second precision 6 and rounds away the seventh digit, which is
        // a digit this store hands back; TO_TIMESTAMP_TZ over the text keeps all of them.
        if (writes.Count > 0)
        {
            await using var apply = Cmd(
                """
                MERGE INTO backwave.jobs j
                USING (SELECT HEXTORAW(d.job_hex) AS job_id, d.worker, d.attempt, d.state, d.cause,
                              TO_TIMESTAMP_TZ(d.due, :fmt) AS due,
                              TO_TIMESTAMP_TZ(d.terminal_at, :fmt) AS terminal_at
                       FROM JSON_TABLE(:payload, '$[*]' COLUMNS (
                                job_hex VARCHAR2(32) PATH '$.JobHex',
                                worker VARCHAR2(4000) PATH '$.WorkerId',
                                attempt NUMBER PATH '$.Attempt',
                                state NUMBER PATH '$.State',
                                cause CLOB PATH '$.Cause',
                                due VARCHAR2(40) PATH '$.Due',
                                terminal_at VARCHAR2(40) PATH '$.TerminalAt')) d) d
                ON (j.job_id = d.job_id)
                WHEN MATCHED THEN UPDATE SET
                    j.state = d.state,
                    j.lease_owner = NULL,
                    j.lease_expiry = NULL,
                    j.terminal_at = d.terminal_at,
                    j.terminal_cause = d.cause,
                    j.due_time = COALESCE(d.due, j.due_time),
                    j.cancel_requested = CASE WHEN d.state = 4 THEN 0 ELSE j.cancel_requested END
                WHERE j.state = 2 AND j.lease_owner = d.worker AND j.attempt = d.attempt
                  AND j.lease_expiry > :now
                """,
                connection, transaction);
            apply.Parameters.Add(Clob("payload", JsonSerializer.Serialize(writes)));
            apply.Parameters.Add(Str("fmt", IsoTimestampFormat));
            apply.Parameters.Add(Tstz("now", now));
            await apply.ExecuteNonQueryCountedAsync(cancellationToken).ConfigureAwait(false);
        }

        // Output and Tag deltas land ONLY for matched rows. Output persists only on a Success outcome;
        // Tags union onto the job's existing tags (set semantics). Both ride this same fenced
        // transaction, and both cost one statement for the whole batch.
        //
        // Output cannot batch the way every other set-based write here does: a blob has no place in the
        // JSON payload - JSON carries no binary type, and HEXTORAW caps at 32767 bytes, below
        // MaxOutputBytes - so there is no set for a MERGE to read. Array binding is the other batching
        // mechanism the driver offers and it has no such limit: the ids and the blobs travel as two
        // parallel arrays, the driver sends them in ONE round trip, and the server runs the UPDATE once
        // per element. A job named twice in one batch keeps the per-row loop's outcome, because the
        // elements execute in array order and the last write wins either way.
        var outputIds = new List<Guid>();
        var outputBlobs = new List<ReadOnlyMemory<byte>>();
        var tagRows = new List<(Guid JobId, JobTags Tags)>();
        foreach (var row in batch)
        {
            if (!matched.ContainsKey(row.JobId))
            {
                continue;
            }
            if (row.Outcome is JobOutcome.Success && row.Output is { } blob)
            {
                outputIds.Add(row.JobId);
                outputBlobs.Add(blob);
            }
            if (row.AddedTags is { Count: > 0 } addedTags)
            {
                tagRows.Add((row.JobId, addedTags));
            }
        }
        if (outputIds.Count > 0)
        {
            await using var setOutput = Cmd(
                "UPDATE backwave.jobs SET output = :output WHERE job_id = :id", connection, transaction);
            setOutput.ArrayBindCount = outputIds.Count;
            setOutput.Parameters.Add(RawArray("id", outputIds));
            setOutput.Parameters.Add(BlobArray("output", outputBlobs));
            await setOutput.ExecuteNonQueryCountedAsync(cancellationToken).ConfigureAwait(false);
        }
        await InsertTagsAsync(connection, transaction, tagRows, cancellationToken).ConfigureAwait(false);

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
                await using var reader = (OracleDataReader)await withChildren.ExecuteReaderCountedAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    parents.Add(ReadGuid(reader, 0));
                }
            }
            parents.Sort(); // deterministic lock order, as everywhere else
            foreach (var parentId in parents)
            {
                if (!matched.TryGetValue(parentId, out var parentState))
                {
                    throw new InvariantViolationException(
                        InvariantTrigger.ParentJobMissingFromBatch,
                        $"Parent job {parentId} came back from a lookup restricted to this batch's own terminal ids, yet it is absent from that batch.");
                }
                await ResolveChildLatchesAsync(
                    connection, transaction, parentId, (JobState)parentState, now, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        // One result per input row, in input order, keyed by job id: matched => Applied, else StaleLease.
        var results = new OutcomeReportResult[count];
        for (var i = 0; i < count; i++)
        {
            results[i] = new OutcomeReportResult(
                batch[i].JobId,
                matched.ContainsKey(batch[i].JobId) ? OutcomeResult.Applied : OutcomeResult.StaleLease);
        }
        return results;
    }

    // The set-valued outcome row for the batched MERGE, serialized to JSON and unpacked by JSON_TABLE.
    // JobHex is the job id in the same byte order every other bind uses (Guid.ToByteArray), because JSON
    // has no RAW literal. Due and TerminalAt are round-trip ISO text, which the statement converts under
    // an explicit format rather than as a JSON timestamp.
    private sealed record OutcomeRow(
        string JobHex, string WorkerId, int Attempt, int State, string? Cause, string? Due, string? TerminalAt);

    // The Oracle picture for the round-trip ("O") format DateTimeOffset renders, normalized to UTC.
    private const string IsoTimestampFormat = "YYYY-MM-DD\"T\"HH24:MI:SS.FFTZH:TZM";

    private static string? Iso(DateTimeOffset? instant)
        => instant is { } value ? $"{value.ToUniversalTime():O}" : null;

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
                await using var reader = (OracleDataReader)await edges.ExecuteReaderCountedAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    children.Add(ReadGuid(reader, 0));
                }
            }
            await using (var delete = Cmd(
                "DELETE FROM backwave.job_parents WHERE parent_id = :parent", connection, transaction))
            {
                delete.Parameters.Add(Raw("parent", currentParent));
                await delete.ExecuteNonQueryCountedAsync(cancellationToken).ConfigureAwait(false);
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
                    await using var reader = await child.ExecuteReaderCountedAsync(cancellationToken).ConfigureAwait(false);
                    if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        throw new InvariantViolationException(
                            InvariantTrigger.DanglingGatingEdge,
                            $"Gating edge {currentParent} -> {childId} named a child job row that does not exist; the job_parents foreign key forbids it.");
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
                    var cancelled = await cancel.ExecuteNonQueryCountedAsync(cancellationToken).ConfigureAwait(false);
                    if (cancelled != 1)
                    {
                        throw new InvariantViolationException(
                            InvariantTrigger.UnexpectedAffectedRowCount,
                            $"Cancelling gated child {childId} affected {cancelled} rows; the row is locked FOR UPDATE by this transaction, so exactly 1 is the only possible count.");
                    }
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
                var resolved = await resolve.ExecuteNonQueryCountedAsync(cancellationToken).ConfigureAwait(false);
                if (resolved != 1)
                {
                    throw new InvariantViolationException(
                        InvariantTrigger.UnexpectedAffectedRowCount,
                        $"Resolving the latch on gated child {childId} affected {resolved} rows; the row is locked FOR UPDATE by this transaction, so exactly 1 is the only possible count.");
                }
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
            await using var reader = (OracleDataReader)await select.ExecuteReaderCountedAsync(cancellationToken).ConfigureAwait(false);
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
            await extend.ExecuteNonQueryCountedAsync(cancellationToken).ConfigureAwait(false);
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
            await using var reader = (OracleDataReader)await select.ExecuteReaderCountedAsync(cancellationToken).ConfigureAwait(false);
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

        // One set-based write per disposition, whatever maxJobs is. Oracle has no VALUES table
        // constructor, so the set arrives as a JSON payload and JSON_TABLE unpacks it - the same shape the
        // outcome write uses, and for the same reason: a sweep of 500 expired leases is 500 round trips as
        // a per-row loop and two as this. Every row here is already locked by the FOR UPDATE SKIP LOCKED
        // above and the ids are distinct, so the MERGE needs no fence of its own. The due instant travels
        // as ISO text under an explicit format, because a JSON_TABLE column declared TIMESTAMP WITH TIME
        // ZONE rounds away the seventh fractional digit that this store hands back.
        if (retries.Count > 0)
        {
            await using var reschedule = Cmd(
                """
                MERGE INTO backwave.jobs j
                USING (SELECT HEXTORAW(d.job_hex) AS job_id, TO_TIMESTAMP_TZ(d.due, :fmt) AS due
                       FROM JSON_TABLE(:payload, '$[*]' COLUMNS (
                                job_hex VARCHAR2(32) PATH '$.JobHex',
                                due VARCHAR2(40) PATH '$.Due')) d) d
                ON (j.job_id = d.job_id)
                WHEN MATCHED THEN UPDATE SET
                    j.state = 0, j.due_time = d.due, j.lease_owner = NULL, j.lease_expiry = NULL
                """,
                connection, transaction);
            reschedule.Parameters.Add(Clob("payload", JsonSerializer.Serialize(
                retries.Select(r => new RescheduleRow(Convert.ToHexString(r.JobId.ToByteArray()), Iso(r.Due))))));
            reschedule.Parameters.Add(Str("fmt", IsoTimestampFormat));
            await reschedule.ExecuteNonQueryCountedAsync(cancellationToken).ConfigureAwait(false);
        }

        if (deadLettered.Count > 0)
        {
            await using (var deadLetter = Cmd(
                """
                MERGE INTO backwave.jobs j
                USING (SELECT HEXTORAW(d.job_hex) AS job_id, d.cause
                       FROM JSON_TABLE(:payload, '$[*]' COLUMNS (
                                job_hex VARCHAR2(32) PATH '$.JobHex',
                                cause CLOB PATH '$.Cause')) d) d
                ON (j.job_id = d.job_id)
                WHEN MATCHED THEN UPDATE SET
                    j.state = 5, j.lease_owner = NULL, j.lease_expiry = NULL,
                    j.terminal_at = :now, j.terminal_cause = d.cause
                """,
                connection, transaction))
            {
                deadLetter.Parameters.Add(Clob("payload", JsonSerializer.Serialize(
                    deadLettered.Select(d => new DeadLetterRow(Convert.ToHexString(d.JobId.ToByteArray()), d.Cause)))));
                deadLetter.Parameters.Add(Tstz("now", now));
                await deadLetter.ExecuteNonQueryCountedAsync(cancellationToken).ConfigureAwait(false);
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
                await using var reader = (OracleDataReader)await withChildren.ExecuteReaderCountedAsync(cancellationToken).ConfigureAwait(false);
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
        // DeadLettered (ceiling) - at its post-claim Attempt, atomic with the disposition writes. Batched,
        // so a wide sweep does not undo the two statements above with one insert per job.
        var transitions = new List<(Guid JobId, JobState State, int Attempt, string? FailureDetail)>(expired.Count);
        foreach (var (jobId, attempt) in expired)
        {
            var resulting = disposition.NextAttemptAt(attempt, now) is not null
                ? JobState.Scheduled
                : JobState.DeadLettered;
            transitions.Add((jobId, resulting, attempt, null));
        }
        await RecordTransitionsBatchAsync(connection, transaction, transitions, now, cancellationToken)
            .ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return expired.Count;
    }

    /// <inheritdoc/>
    public async ValueTask<int> RelinquishLeasesAsync(
        string workerId, DateTimeOffset now, RetryDisposition disposition,
        CancellationToken cancellationToken = default)
    {
        using var activity = OracleDiagnostics.StartStore("relinquish_leases", JobsCollection);
        try
        {
            return await RelinquishLeasesUntracedAsync(workerId, now, disposition, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            OracleDiagnostics.RecordStoreFault(activity, exception, IsTransientStoreFault(exception));
            throw;
        }
    }

    private async ValueTask<int> RelinquishLeasesUntracedAsync(
        string workerId, DateTimeOffset now, RetryDisposition disposition,
        CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await BeginAsync(connection, cancellationToken).ConfigureAwait(false);

        // Fenced on the lease itself: only rows this worker still holds, still Leased, so a job that
        // already reported an outcome is never revived.
        var held = new List<(Guid JobId, int Attempt)>();
        await using (var select = Cmd(
            """
            SELECT job_id, attempt FROM backwave.jobs
            WHERE state = 2 AND lease_owner = :owner
            ORDER BY job_id
            FOR UPDATE
            """,
            connection, transaction))
        {
            select.Parameters.Add(Str("owner", workerId));
            await using var reader = (OracleDataReader)await select.ExecuteReaderCountedAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                held.Add((ReadGuid(reader, 0), reader.GetInt32(1)));
            }
        }

        if (held.Count == 0)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return 0;
        }

        // The claim already counted the Attempt, so the hand-back leaves it alone: a clean stop skips
        // the backoff but not the ceiling.
        //
        // Transition Log: one entry per relinquished job for its resulting state, at its unchanged
        // Attempt, atomic with the state writes. Built in this same pass, because the ceiling is the
        // policy's answer and asking it twice for one job invites two answers.
        var ready = new List<Guid>();
        var deadLettered = new List<(Guid JobId, string Cause)>();
        var transitions = new List<(Guid JobId, JobState State, int Attempt, string? FailureDetail)>(held.Count);
        foreach (var (jobId, attempt) in held)
        {
            if (disposition.NextAttemptAt(attempt, now) is not null)
            {
                ready.Add(jobId);
                transitions.Add((jobId, JobState.Scheduled, attempt, null));
            }
            else
            {
                deadLettered.Add((jobId, $"Lease relinquished on attempt {attempt} (attempt ceiling reached)."));
                transitions.Add((jobId, JobState.DeadLettered, attempt, null));
            }
        }

        // One set-based write per branch, whatever the hand-back size, through the same JSON_TABLE shape
        // the expiry path uses. Every row is already locked by the FOR UPDATE above and the ids are
        // distinct, so the MERGE needs no fence of its own.
        if (ready.Count > 0)
        {
            await using var restore = Cmd(
                """
                MERGE INTO backwave.jobs j
                USING (SELECT HEXTORAW(d.job_hex) AS job_id, TO_TIMESTAMP_TZ(d.due, :fmt) AS due
                       FROM JSON_TABLE(:payload, '$[*]' COLUMNS (
                                job_hex VARCHAR2(32) PATH '$.JobHex',
                                due VARCHAR2(40) PATH '$.Due')) d) d
                ON (j.job_id = d.job_id)
                WHEN MATCHED THEN UPDATE SET
                    j.state = 0, j.due_time = d.due, j.lease_owner = NULL, j.lease_expiry = NULL
                """,
                connection, transaction);
            restore.Parameters.Add(Clob("payload", JsonSerializer.Serialize(
                ready.Select(id => new RescheduleRow(Convert.ToHexString(id.ToByteArray()), Iso(now))))));
            restore.Parameters.Add(Str("fmt", IsoTimestampFormat));
            await restore.ExecuteNonQueryCountedAsync(cancellationToken).ConfigureAwait(false);
        }

        if (deadLettered.Count > 0)
        {
            await using (var deadLetter = Cmd(
                """
                MERGE INTO backwave.jobs j
                USING (SELECT HEXTORAW(d.job_hex) AS job_id, d.cause
                       FROM JSON_TABLE(:payload, '$[*]' COLUMNS (
                                job_hex VARCHAR2(32) PATH '$.JobHex',
                                cause CLOB PATH '$.Cause')) d) d
                ON (j.job_id = d.job_id)
                WHEN MATCHED THEN UPDATE SET
                    j.state = 5, j.lease_owner = NULL, j.lease_expiry = NULL,
                    j.terminal_at = :now, j.terminal_cause = d.cause
                """,
                connection, transaction))
            {
                deadLetter.Parameters.Add(Clob("payload", JsonSerializer.Serialize(
                    deadLettered.Select(d => new DeadLetterRow(Convert.ToHexString(d.JobId.ToByteArray()), d.Cause)))));
                deadLetter.Parameters.Add(Tstz("now", now));
                await deadLetter.ExecuteNonQueryCountedAsync(cancellationToken).ConfigureAwait(false);
            }

            // Latch resolution touches only dead-lettered jobs that actually parent a Dependency, exactly
            // as the expiry path does.
            var deadIds = deadLettered.Select(d => d.JobId).ToList();
            var parents = new List<Guid>();
            await using (var withChildren = Cmd(
                $"SELECT DISTINCT parent_id FROM backwave.job_parents WHERE parent_id IN ({ParameterList("p", deadIds.Count)})",
                connection, transaction))
            {
                AddIdList(withChildren, "p", deadIds);
                await using var reader = (OracleDataReader)await withChildren.ExecuteReaderCountedAsync(cancellationToken).ConfigureAwait(false);
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

        // The entries go in batched, for the same reason the expiry path batches.
        await RecordTransitionsBatchAsync(connection, transaction, transitions, now, cancellationToken)
            .ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return held.Count;
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
            await using var reader = await current.ExecuteReaderCountedAsync(cancellationToken).ConfigureAwait(false);
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
                    await cancel.ExecuteNonQueryCountedAsync(cancellationToken).ConfigureAwait(false);
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
                    await request.ExecuteNonQueryCountedAsync(cancellationToken).ConfigureAwait(false);
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
        if (await update.ExecuteNonQueryCountedAsync(cancellationToken).ConfigureAwait(false) == 0)
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
            await upsert.ExecuteNonQueryCountedAsync(cancellationToken).ConfigureAwait(false);
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
            await using var reader = await ExecuteLobReaderAsync(select, options.Bounds.MaxPayloadBytes, SingleRow, cancellationToken).ConfigureAwait(false);
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
            if (await insert.ExecuteNonQueryCountedAsync(cancellationToken).ConfigureAwait(false) > 0)
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
        await using var reader = (OracleDataReader)await command.ExecuteReaderCountedAsync(cancellationToken).ConfigureAwait(false);
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
        await audit.ExecuteNonQueryCountedAsync(cancellationToken).ConfigureAwait(false);
    }

    // The ordinal a recorder reports when it wrote nothing - the Job History Policy suppressed the row,
    // or the batch read back no maximum at all. Below every real ordinal and below every cap, so a
    // caller's "did anything reach the cap?" test reads false without a special case.
    private const long NoTransitionRecorded = -1;

    // Appends one Transition Log entry for a job's resulting state, inside the SAME transaction as the
    // state change it records - a crash leaves neither or both. The ordinal is the per-job max + 1 (a
    // scalar sub-select against the same table), so it climbs even as oldest rows age out. The trailing
    // bounded delete enforces MaxTransitionsPerJob, and runs only when this entry put the cap in play.
    // `now` is always the caller's clock. `failureDetail` is the Shell-captured exception text, written
    // only on a failing transition and clamped to MaxFailureDetailBytes; null on every other transition.
    private async Task RecordTransitionAsync(
        OracleConnection connection, OracleTransaction transaction, Guid jobId, JobState state,
        int attempt, DateTimeOffset now, CancellationToken cancellationToken, string? failureDetail = null)
    {
        var ordinal = await AppendTransitionAsync(
            connection, transaction, jobId, state, attempt, now, cancellationToken, failureDetail).ConfigureAwait(false);

        // Per-job-life cap: skip the DELETE entirely unless the entry just written reached the cap. Under
        // the cap the delete can only be a no-op - its bound is MAX(ordinal) - cap, which is negative
        // while the newest ordinal is below the cap, and no ordinal is negative - so a job nowhere near
        // MaxTransitionsPerJob pays no round trip for it.
        if (ordinal < options.Bounds.MaxTransitionsPerJob)
        {
            return;
        }
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
        await prune.ExecuteNonQueryCountedAsync(cancellationToken).ConfigureAwait(false);
    }

    // Appends a BATCH of Transition Log entries in ONE set-based INSERT - the per-row recorder amortized
    // for the claim, batched-report, and lease-sweep paths. JSON_TABLE unpacks the payload into a set,
    // exactly as the SQL Server adapter uses OPENJSON; a job id travels as its ToByteArray hex, because
    // JSON carries no RAW, and HEXTORAW turns it back. Each entry's ordinal is still the per-job
    // MAX(ordinal)+1: a correlated scalar sub-query supplies the job's current max (read consistency keeps
    // this statement's own rows out of it), and ROW_NUMBER over the payload order adds one per repeat, so a
    // job appearing twice in one batch gets two consecutive ordinals rather than one duplicate. The whole
    // insert rides the caller's transaction, so it stays atomic with the lease/outcome write.
    //
    // Oracle rejects RETURNING on an INSERT ... SELECT, so the highest ordinal the batch assigned comes
    // back on a following read: after the insert, every job in the batch has its new entry as its own
    // maximum, so one MAX over the batch's ids IS that number. It buys the prune skip - a batch where no
    // job reached the cap, the common shape since most jobs live two transitions, issues no DELETE at all.
    // When one did reach it, a single set-based DELETE covers the whole batch; its correlated MAX no-ops
    // for the jobs still under the cap. Honors the history policy (Off writes nothing).
    private async Task RecordTransitionsBatchAsync(
        OracleConnection connection, OracleTransaction transaction,
        IReadOnlyList<(Guid JobId, JobState State, int Attempt, string? FailureDetail)> rows,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (rows.Count == 0 || _historyPolicy == JobHistoryPolicy.Off)
        {
            return;
        }

        // One row costs LESS through the single-row recorder: it gets its ordinal back on the insert's own
        // RETURNING, where the batch form has to read MAX(ordinal) afterwards because Oracle rejects
        // RETURNING on an INSERT ... SELECT. At a batch of one there is no second row to amortize that read
        // over, so the batch form would be a round trip worse than the code it replaced. A batch of one is
        // not a corner case either - the driver flushes outcomes as soon as its executing set empties, so
        // it is the ordinary shape for a lightly loaded worker.
        if (rows.Count == 1)
        {
            var (jobId, state, attempt, detail) = rows[0];
            await RecordTransitionAsync(
                connection, transaction, jobId, state, attempt, now, cancellationToken, detail).ConfigureAwait(false);
            return;
        }

        var payloadRows = new TransitionRow[rows.Count];
        for (var i = 0; i < rows.Count; i++)
        {
            // Transitions records the row but never the detail; the full rung clamps and keeps it.
            var detail = _historyPolicy == JobHistoryPolicy.Transitions
                ? null
                : options.Bounds.ClampFailureDetail(rows[i].FailureDetail);
            payloadRows[i] = new TransitionRow(
                Convert.ToHexString(rows[i].JobId.ToByteArray()), (int)rows[i].State, rows[i].Attempt, detail);
        }
        var payload = JsonSerializer.Serialize(payloadRows);

        // position reads its DEFAULT from a sequence, one draw per row as the row is inserted, so the
        // row order decides the Positions an Observer walks. ORDER BY the payload order states that
        // order outright instead of leaving it to fall out of the window function's own sort.
        await using (var insert = Cmd(
            """
            INSERT INTO backwave.job_transitions (job_id, ordinal, recorded_at, state, attempt, failure_detail)
            SELECT HEXTORAW(d.job_hex),
                   COALESCE((SELECT MAX(t.ordinal) FROM backwave.job_transitions t
                             WHERE t.job_id = HEXTORAW(d.job_hex)), -1)
                     + ROW_NUMBER() OVER (PARTITION BY d.job_hex ORDER BY d.seq),
                   :now, d.state, d.attempt, d.detail
            FROM JSON_TABLE(:payload, '$[*]' COLUMNS (
                     seq FOR ORDINALITY,
                     job_hex VARCHAR2(32) PATH '$.JobHex',
                     state NUMBER PATH '$.State',
                     attempt NUMBER PATH '$.Attempt',
                     detail CLOB PATH '$.Detail')) d
            ORDER BY d.seq
            """,
            connection, transaction))
        {
            insert.Parameters.Add(Clob("payload", payload));
            insert.Parameters.Add(Tstz("now", now));
            await insert.ExecuteNonQueryCountedAsync(cancellationToken).ConfigureAwait(false);
        }

        // Distinct, because a batch may name one job more than once and both statements below want the
        // job once. The same bind list serves the read and the prune.
        var ids = (IReadOnlyList<Guid>)[.. rows.Select(row => row.JobId).Distinct()];
        var idList = ParameterList("j", ids.Count);
        long maxNewOrdinal;
        await using (var highest = Cmd(
            $"SELECT MAX(ordinal) FROM backwave.job_transitions WHERE job_id IN ({idList})",
            connection, transaction))
        {
            AddIdList(highest, "j", ids);
            var value = await highest.ExecuteScalarCountedAsync(cancellationToken).ConfigureAwait(false);
            maxNewOrdinal = value is null or DBNull ? NoTransitionRecorded : Convert.ToInt64(value);
        }

        // Per-job-life cap: skip the DELETE entirely unless some job's new ordinal reached the cap. Under
        // the cap the delete can only be a no-op - its bound is MAX(ordinal) - cap, which is negative while
        // the newest ordinal is below the cap, and no ordinal is negative.
        if (maxNewOrdinal < options.Bounds.MaxTransitionsPerJob)
        {
            return;
        }
        await using var prune = Cmd(
            $"""
            DELETE FROM backwave.job_transitions jt
            WHERE jt.job_id IN ({idList})
              AND jt.ordinal <= (
                  SELECT MAX(ordinal) FROM backwave.job_transitions x WHERE x.job_id = jt.job_id
              ) - :cap
            """,
            connection, transaction);
        AddIdList(prune, "j", ids);
        prune.Parameters.Add(Int("cap", options.Bounds.MaxTransitionsPerJob));
        await prune.ExecuteNonQueryCountedAsync(cancellationToken).ConfigureAwait(false);
    }

    // The set-valued transition row for the batch INSERT, serialized to JSON and unpacked by JSON_TABLE.
    // JobHex is the job id in the same byte order every other bind uses (Guid.ToByteArray), because JSON
    // has no RAW literal.
    private sealed record TransitionRow(string JobHex, int State, int Attempt, string? Detail);

    // The insert half of recording ONE transition. Returns the ordinal the row was assigned, so the
    // caller can decide whether the per-job-life cap is even in play, or NoTransitionRecorded when the
    // Job History Policy suppressed the write. The ordinal comes back on the insert's own round trip:
    // Oracle rejects RETURNING on an INSERT ... SELECT, so the new ordinal is a scalar sub-query in the
    // VALUES list and RETURNING reads back what it evaluated to. The batch recorder cannot use that trick
    // - RETURNING carries no multi-row result - and pays a read of its own instead.
    private async Task<long> AppendTransitionAsync(
        OracleConnection connection, OracleTransaction transaction, Guid jobId, JobState state,
        int attempt, DateTimeOffset now, CancellationToken cancellationToken, string? failureDetail)
    {
        // Job History Policy gates writes, not schema. Off appends no row at all; Transitions appends the
        // row but never the detail; the full rung keeps the clamped detail. The table always exists -
        // flipping the policy is config, never a migration.
        if (_historyPolicy == JobHistoryPolicy.Off)
        {
            return NoTransitionRecorded;
        }
        if (_historyPolicy == JobHistoryPolicy.Transitions)
        {
            failureDetail = null; // record the transition, but never the detail it would have carried
        }

        await using var insert = Cmd(
            """
            INSERT INTO backwave.job_transitions (job_id, ordinal, recorded_at, state, attempt, failure_detail)
            VALUES (
                :id,
                (SELECT COALESCE(MAX(ordinal) + 1, 0) FROM backwave.job_transitions WHERE job_id = :id),
                :now, :state, :attempt, :detail)
            RETURNING ordinal INTO :ordinal
            """,
            connection, transaction);
        insert.Parameters.Add(Raw("id", jobId));
        insert.Parameters.Add(Tstz("now", now));
        insert.Parameters.Add(Int("state", (int)state));
        insert.Parameters.Add(Int("attempt", attempt));
        insert.Parameters.Add(Clob("detail", options.Bounds.ClampFailureDetail(failureDetail)));
        var assigned = OutLong("ordinal");
        insert.Parameters.Add(assigned);
        await insert.ExecuteNonQueryCountedAsync(cancellationToken).ConfigureAwait(false);
        return ((OracleDecimal)assigned.Value).ToInt64();
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
                await command.ExecuteNonQueryCountedAsync(cancellationToken).ConfigureAwait(false);
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
        await command.ExecuteNonQueryCountedAsync(cancellationToken).ConfigureAwait(false);
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
        await using var reader = await ExecuteLobReaderAsync(command, UncappedTextPrefetchBytes, LobFetchWindowRows, cancellationToken).ConfigureAwait(false);
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
                    SkippedTicks = ParseSkippedTicks(ReadText(reader, 8)),
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
                fenced = await fence.ExecuteNonQueryCountedAsync(cancellationToken).ConfigureAwait(false);
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
                await using var reader = await ExecuteLobReaderAsync(select, options.Bounds.MaxPayloadBytes, SingleRow, cancellationToken).ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    throw new InvariantViolationException(
                        InvariantTrigger.GuaranteedRowAbsent,
                        $"Schedule '{decision.ScheduleId}' has no row, yet this transaction just won its cursor fence on that row.");
                }
                schedule = (reader.GetString(0), ReadBytes(reader, 1), reader.GetString(2), ReadText(reader, 3));
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
                await record.ExecuteNonQueryCountedAsync(cancellationToken).ConfigureAwait(false);
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
                if (await insert.ExecuteNonQueryCountedAsync(cancellationToken).ConfigureAwait(false) > 0)
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
            await command.ExecuteNonQueryCountedAsync(cancellationToken).ConfigureAwait(false);
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
        await using (var reader = await ExecuteLobReaderAsync(command, options.Bounds.MaxPayloadBytes, SingleRow, cancellationToken).ConfigureAwait(false))
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
        await using var reader = await ExecuteLobReaderAsync(command, options.Bounds.MaxOutputBytes, SingleRow, cancellationToken).ConfigureAwait(false);
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
        await using var reader = await ExecuteLobReaderAsync(command, options.Bounds.MaxFailureDetailBytes, LobFetchWindowRows, cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            transitions.Add(new JobTransition(
                reader.GetInt64(0),
                ReadTstz(reader, 1),
                (JobState)reader.GetInt32(2),
                reader.GetInt32(3),
                ReadTextOrNull(reader, 4)));
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
        await using (var reader = await ExecuteLobReaderAsync(command, options.Bounds.MaxPayloadBytes, LobFetchWindowRows, cancellationToken).ConfigureAwait(false))
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
        await using var reader = (OracleDataReader)await command.ExecuteReaderCountedAsync(cancellationToken).ConfigureAwait(false);
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
        await using var reader = (OracleDataReader)await command.ExecuteReaderCountedAsync(cancellationToken).ConfigureAwait(false);
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

            await using var reader = (OracleDataReader)await command.ExecuteReaderCountedAsync(cancellationToken).ConfigureAwait(false);
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

        await using var stageOneReader = (OracleDataReader)await command.ExecuteReaderCountedAsync(cancellationToken).ConfigureAwait(false);
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
        await using var reader = (OracleDataReader)await command.ExecuteReaderCountedAsync(cancellationToken).ConfigureAwait(false);
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
                await insertRow.ExecuteNonQueryCountedAsync(cancellationToken).ConfigureAwait(false);
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
                throw new InvariantViolationException(
                    InvariantTrigger.WorkflowMemberEnqueueRejected,
                    $"Workflow enqueue rejected member {member.JobId} with {applied} inside the transaction that had already validated it.");
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
                    await edge.ExecuteNonQueryCountedAsync(cancellationToken).ConfigureAwait(false);
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
        return await command.ExecuteScalarCountedAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    private async ValueTask<bool> JobExistsAsync(
        OracleConnection connection, OracleTransaction transaction, Guid jobId, CancellationToken cancellationToken)
    {
        await using var command = Cmd(
            "SELECT 1 FROM backwave.jobs WHERE job_id = :id", connection, transaction);
        command.Parameters.Add(Raw("id", jobId));
        return await command.ExecuteScalarCountedAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    private async ValueTask<HashSet<Guid>> MembersOfAsync(
        OracleConnection connection, OracleTransaction transaction, Guid workflowId, CancellationToken cancellationToken)
    {
        var members = new HashSet<Guid>();
        await using var command = Cmd(
            "SELECT job_id FROM backwave.jobs WHERE workflow_id = :id", connection, transaction);
        command.Parameters.Add(Raw("id", workflowId));
        await using var reader = (OracleDataReader)await command.ExecuteReaderCountedAsync(cancellationToken).ConfigureAwait(false);
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
        if (ordered.Count != members.Count)
        {
            // Kahn's algorithm drains every member unless a cycle holds some of them back, and the
            // builder rejects a cycle long before this point, so a shortfall means the graph reached the
            // store unvalidated. The old fallback inserted in the caller's order instead, which put a
            // member ahead of its own parent and had the insert refused a few lines below - naming the
            // member as the problem rather than the cycle that is the actual cause. Raise here, at the
            // only point that can still tell the two apart, and before any row is written.
            throw new InvariantViolationException(
                InvariantTrigger.WorkflowMemberCycle,
                $"A workflow of {members.Count} member(s) ordered only {ordered.Count} of them, " +
                "so its in-batch dependency edges hold a cycle.");
        }

        return ordered;
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
            await using var reader = (OracleDataReader)await members.ExecuteReaderCountedAsync(cancellationToken).ConfigureAwait(false);
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
            await using var reader = await ExecuteLobReaderAsync(workflows, UncappedTextPrefetchBytes, LobFetchWindowRows, cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var workflowId = ReadGuid(reader, 0);
                var states = statesByWorkflow.GetValueOrDefault(workflowId) ?? [];
                snapshots.Add(new WorkflowSnapshot
                {
                    WorkflowId = workflowId,
                    Name = ReadTextOrNull(reader, 1),
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
            await using var reader = await ExecuteLobReaderAsync(row, UncappedTextPrefetchBytes, SingleRow, cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }
            name = ReadTextOrNull(reader, 0);
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
            await using var reader = await ExecuteLobReaderAsync(memberRows, options.Bounds.MaxPayloadBytes, LobFetchWindowRows, cancellationToken).ConfigureAwait(false);
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
            await using var reader = (OracleDataReader)await edgeRows.ExecuteReaderCountedAsync(cancellationToken).ConfigureAwait(false);
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
            await using var reader = (OracleDataReader)await parents.ExecuteReaderCountedAsync(cancellationToken).ConfigureAwait(false);
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
            await using var reader = (OracleDataReader)await childRows.ExecuteReaderCountedAsync(cancellationToken).ConfigureAwait(false);
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
        var purged = await command.ExecuteNonQueryCountedAsync(cancellationToken).ConfigureAwait(false);

        // When a Workflow's last member is purged, drop its now-orphaned identity row (structural edges
        // cascade via FK) so the tables never leak rows for Workflows with no surviving jobs.
        await using (var prune = Cmd(
            """
            DELETE FROM backwave.workflows w
            WHERE NOT EXISTS (SELECT 1 FROM backwave.jobs j WHERE j.workflow_id = w.workflow_id)
            """,
            connection, transaction))
        {
            await prune.ExecuteNonQueryCountedAsync(cancellationToken).ConfigureAwait(false);
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
                await ensure.ExecuteNonQueryCountedAsync(cancellationToken).ConfigureAwait(false);
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
            await using var reader = (OracleDataReader)await locked.ExecuteReaderCountedAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvariantViolationException(
                    InvariantTrigger.GuaranteedRowAbsent,
                    $"Observer '{request.ObserverId}' has no row immediately after this transaction ensured one.");
            }
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
            await sub.ExecuteNonQueryCountedAsync(cancellationToken).ConfigureAwait(false);
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
            await using var reader = await ExecuteLobReaderAsync(scan, options.Bounds.MaxFailureDetailBytes, LobFetchWindowRows, cancellationToken).ConfigureAwait(false);
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
                    ReadTextOrNull(reader, 8), priorAttempt + 1)); // the claim starts a delivery Attempt
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
            await upsert.ExecuteNonQueryCountedAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var lease = Cmd(
            "UPDATE backwave.observers SET lease_owner = :worker, lease_expiry = :expiry WHERE observer_id = :id",
            connection, transaction))
        {
            lease.Parameters.Add(Str("id", request.ObserverId));
            lease.Parameters.Add(Str("worker", request.WorkerId));
            lease.Parameters.Add(Tstz("expiry", request.Now + request.LeaseDuration));
            await lease.ExecuteNonQueryCountedAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ObserverClaim(request.ObserverId, Acquired: true, candidates);
    }

    /// <inheritdoc/>
    public async ValueTask ReportObserverDeliveriesAsync(
        ObserverDeliveryReport report, CancellationToken cancellationToken = default)
        => await TryReportObserverDeliveriesAsync(report, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc/>
    public async ValueTask<ObserverReportOutcome> TryReportObserverDeliveriesAsync(
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
            await using var reader = (OracleDataReader)await locked.ExecuteReaderCountedAsync(cancellationToken).ConfigureAwait(false);
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
            return ObserverReportOutcome.UnknownObserver; // nothing claimed, nothing to resolve
        }

        // Fence: only the live claim-Lease holder may resolve deliveries and advance the
        // cursor. Two shapes are refused here and only one of them is a broken invariant.
        //
        // A stale survivor of a lapsed claim reports into the void - its claim Lease simply ran out
        // before the report arrived, so the report changes nothing and at-least-once stays intact.
        // That race is ordinary, so it is counted nowhere: the invariant ledger is the surface a
        // promotion rule reads FOR ZEROS, and a healthy fleet that increments it makes the trigger
        // meaningless.
        //
        // What no legal race produces is a report from a worker that is not the owner while that worker
        // STILL BELIEVED its own claim Lease was live, so two workers believed they held the same
        // observer claim at once. Only that contradiction is counted, and ObserverFence owns the test -
        // the row's own expiry cannot answer it, because a peer that reclaimed this observer after the
        // lapse leaves ITS future expiry here for the old owner's late report to read.
        if (!string.Equals(leaseOwner, report.WorkerId, StringComparison.Ordinal) || leaseExpiry <= report.Now)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            if (ObserverFence.IsContradiction(report))
            {
                Invariant.Degrade(
                    _logger, InvariantTrigger.ObserverReportFenceRejected,
                    ObserverFence.Detail(report, leaseOwner));
            }
            return ObserverReportOutcome.FenceRejected;
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
            await resolve.ExecuteNonQueryCountedAsync(cancellationToken).ConfigureAwait(false);
        }

        await AdvanceObserverCursorAsync(
            connection, transaction, report.ObserverId, cursor, states, wireName, queue, report.Now, cancellationToken)
            .ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ObserverReportOutcome.Applied;
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
            var result = await blockCommand.ExecuteScalarCountedAsync(cancellationToken).ConfigureAwait(false);
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
            var result = await advance.ExecuteScalarCountedAsync(cancellationToken).ConfigureAwait(false);
            newCursor = result is null or DBNull ? null : Convert.ToInt64(result);
        }

        if (newCursor is { } regressed && regressed < cursor)
        {
            throw new InvariantViolationException(
                InvariantTrigger.ObserverCursorRegressed,
                $"Observer '{observerId}' would move its cursor from {cursor} back to {regressed}; the advance selects MAX(position) strictly greater than the cursor.");
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
            await deadLetter.ExecuteNonQueryCountedAsync(cancellationToken).ConfigureAwait(false);
        }

        // The swept rows are all resolved now - drop their in-flight bookkeeping.
        await using (var sweep = Cmd(
            "DELETE FROM backwave.observer_deliveries WHERE observer_id = :id AND position <= :target",
            connection, transaction))
        {
            sweep.Parameters.Add(Str("id", observerId));
            sweep.Parameters.Add(Long("target", target));
            await sweep.ExecuteNonQueryCountedAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var move = Cmd(
            "UPDATE backwave.observers SET cursor_pos = :target WHERE observer_id = :id",
            connection, transaction))
        {
            move.Parameters.Add(Str("id", observerId));
            move.Parameters.Add(Long("target", target));
            var moved = await move.ExecuteNonQueryCountedAsync(cancellationToken).ConfigureAwait(false);
            if (moved != 1)
            {
                throw new InvariantViolationException(
                    InvariantTrigger.UnexpectedAffectedRowCount,
                    $"Moving the cursor of observer '{observerId}' affected {moved} rows; the row is locked FOR UPDATE by this transaction, so exactly 1 is the only possible count.");
            }
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
        var result = await command.ExecuteScalarCountedAsync(cancellationToken).ConfigureAwait(false);
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

        await using var reader = (OracleDataReader)await command.ExecuteReaderCountedAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvariantViolationException(
                InvariantTrigger.GuaranteedRowAbsent,
                $"The lag aggregate for observer '{request.ObserverId}' returned no row; an ungrouped aggregate always returns exactly one.");
        }
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
        await using var reader = (OracleDataReader)await command.ExecuteReaderCountedAsync(cancellationToken).ConfigureAwait(false);
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

    private static JobRecord ReadJob(OracleDataReader reader)
    {
        var storedState = reader.GetInt32(4);
        if (!Enum.IsDefined((JobState)storedState))
        {
            throw new InvariantViolationException(
                InvariantTrigger.UndefinedEnumValueStored,
                $"Job {ReadGuid(reader, 0)} stores state {storedState}, which is not a defined JobState.");
        }
        var storedMode = reader.GetInt32(14);
        if (!Enum.IsDefined((DependencyMode)storedMode))
        {
            throw new InvariantViolationException(
                InvariantTrigger.UndefinedEnumValueStored,
                $"Job {ReadGuid(reader, 0)} stores dependency mode {storedMode}, which is not a defined DependencyMode.");
        }
        return new()
        {
            JobId = ReadGuid(reader, 0),
            WireName = reader.GetString(1),
            Payload = ReadBytes(reader, 2),
            Queue = reader.GetString(3),
            State = (JobState)storedState,
            DueTime = ReadTstz(reader, 5),
            Attempt = reader.GetInt32(6),
            LeaseOwner = reader.IsDBNull(7) ? null : reader.GetString(7),
            LeaseExpiry = reader.IsDBNull(8) ? null : ReadTstz(reader, 8),
            CancelRequested = reader.GetInt32(9) != 0,
            TerminalAt = reader.IsDBNull(10) ? null : ReadTstz(reader, 10),
            TerminalCause = ReadTextOrNull(reader, 11),
            ScheduleId = reader.IsDBNull(12) ? null : reader.GetString(12),
            ParentsRemaining = reader.GetInt32(13),
            Mode = (DependencyMode)storedMode,
            TraceContext = reader.IsDBNull(15) ? null : reader.GetString(15),
            Sequence = reader.GetInt64(16),
            WorkflowId = reader.IsDBNull(17) ? null : ReadGuid(reader, 17),
        };
    }

    // ── Job Tags ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Inserts Tag sets into job_tags within the caller's transaction, one statement for the whole batch.
    /// Tags are already a set upstream (JobTags collapses duplicates), and the insert is
    /// idempotent-by-construction: IGNORE_ROW_ON_DUPKEY_INDEX skips a row whose (job_id, key, value)
    /// already exists instead of raising, so a duplicate converges to the existing row - including
    /// against a concurrent writer holding that key uncommitted, which the insert waits behind and then
    /// converges on. A Label's key is the empty-string sentinel; Oracle folds an empty string to NULL,
    /// and key/value are NOT NULL primary-key columns, so an empty key or value is encoded to a CHR(1)
    /// sentinel on write and decoded back on read.
    /// </summary>
    private async Task InsertTagsAsync(
        OracleConnection connection, OracleTransaction transaction,
        IReadOnlyList<(Guid JobId, JobTags Tags)> rows, CancellationToken cancellationToken)
    {
        // Distinct across the WHOLE payload, not just within one job's set. A report batch may name the
        // same job twice, and both entries contribute their tag delta, so one (job_id, key, value) can
        // reach this list more than once. The anti-join below cannot catch that: the insert reads the
        // table as it stood before the statement, so a repeat inside the payload is invisible to it and
        // would land on the primary key.
        var payloadRows = new List<TagRow>();
        var seen = new HashSet<TagRow>();
        foreach (var (jobId, tags) in rows)
        {
            var jobHex = Convert.ToHexString(jobId.ToByteArray());
            foreach (var tag in tags)
            {
                var candidate = new TagRow(jobHex, EncodeTag(tag.Key), EncodeTag(tag.Value));
                if (seen.Add(candidate))
                {
                    payloadRows.Add(candidate);
                }
            }
        }
        if (payloadRows.Count == 0)
        {
            return;
        }

        // A Tag is being written on THIS process, so latch the tags-in-use signal: every later claim
        // now hydrates without waiting for the periodic probe to notice.
        _tagsInUse = true;

        // Two filters, because neither alone is enough. NOT EXISTS removes every duplicate this
        // transaction can SEE, which is the ordinary case - re-reporting a tag a job already carries -
        // and it removes it without depending on a hint. The hint is left as the arbiter for the one
        // case the anti-join cannot serialize: a concurrent writer holding the same key uncommitted,
        // which both statements pass and one then loses on. Restoring the anti-join matters more here
        // than it did per row, because a raised ORA-00001 now fails the WHOLE batch rather than one tag,
        // and a hint that fails to resolve is dropped by Oracle in silence.
        //
        // The hint names the table unqualified: Oracle resolves a hint against the table name in the
        // statement, not against its owner, so this holds under a custom schema too.
        await using var insert = Cmd(
            """
            INSERT /*+ IGNORE_ROW_ON_DUPKEY_INDEX(job_tags (job_id, key, value)) */
            INTO backwave.job_tags (job_id, key, value)
            SELECT HEXTORAW(d.job_hex), d.tag_key, d.tag_value
            FROM JSON_TABLE(:payload, '$[*]' COLUMNS (
                     job_hex VARCHAR2(32) PATH '$.JobHex',
                     tag_key VARCHAR2(1024) PATH '$.Key',
                     tag_value VARCHAR2(1024) PATH '$.Value')) d
            WHERE NOT EXISTS (
                SELECT 1 FROM backwave.job_tags t
                WHERE t.job_id = HEXTORAW(d.job_hex) AND t.key = d.tag_key AND t.value = d.tag_value)
            """,
            connection, transaction);
        insert.Parameters.Add(Clob("payload", JsonSerializer.Serialize(payloadRows)));
        await insert.ExecuteNonQueryCountedAsync(cancellationToken).ConfigureAwait(false);
    }

    // The set-valued disposition rows for the lease sweep's two MERGE statements. JobHex is the job id in
    // the same byte order every other bind uses (Guid.ToByteArray), because JSON has no RAW literal.
    private sealed record RescheduleRow(string JobHex, string? Due);

    private sealed record DeadLetterRow(string JobHex, string Cause);

    // The set-valued tag row for the batch INSERT, serialized to JSON and unpacked by JSON_TABLE. Key
    // and Value are already encoded, so the empty-string sentinel never reaches Oracle as a NULL.
    private sealed record TagRow(string JobHex, string Key, string Value);

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
        await using var reader = (OracleDataReader)await command.ExecuteReaderCountedAsync(cancellationToken).ConfigureAwait(false);
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
    private static string EncodeTag(string value) => value.Length == 0 ? "\u0001" : value;

    private static string DecodeTag(string value) => value == "\u0001" ? string.Empty : value;

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
            // uncounted round trip: the pump owns this session for the process lifetime and registers on
            // it once, on its own task. It is nobody's operation, so counting it would charge whichever
            // operation happened to be in flight for a statement it never asked for.
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
            // uncounted round trip: this statement parks on DBMS_ALERT.WAITONE for the whole wait window
            // rather than making a trip and returning. It is a blocked session, not a cost any operation
            // pays, and it belongs to the pump's task rather than to any operation being measured.
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

    // The array-bound forms of the two binds above, for a statement run under ArrayBindCount. Size is the
    // cap on ONE element rather than on the array, which is why the RAW form still declares 16.
    private static OracleParameter RawArray(string name, IReadOnlyList<Guid> values)
        => new(name, OracleDbType.Raw) { Size = 16, Value = values.Select(value => value.ToByteArray()).ToArray() };

    private static OracleParameter BlobArray(string name, IReadOnlyList<ReadOnlyMemory<byte>> values)
        => new(name, OracleDbType.Blob) { Value = values.Select(value => value.ToArray()).ToArray() };

    private static OracleParameter Int(string name, int value)
        => new(name, OracleDbType.Int32) { Value = value };

    private static OracleParameter IntN(string name, int? value)
        => new(name, OracleDbType.Int32) { Value = (object?)value ?? DBNull.Value };

    private static OracleParameter Long(string name, long value)
        => new(name, OracleDbType.Int64) { Value = value };

    private static OracleParameter LongN(string name, long? value)
        => new(name, OracleDbType.Int64) { Value = (object?)value ?? DBNull.Value };

    // An OUT bind for a RETURNING clause. ODP.NET hands the value back as an OracleDecimal.
    private static OracleParameter OutLong(string name)
        => new(name, OracleDbType.Int64) { Direction = ParameterDirection.Output };

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

    // The two LOB read helpers, and the only places the adapter pulls a LOB value across the wire. Both
    // are counted, and both count only what the wire cost: a value the reader's command prefetched came
    // in the row and is free, while one over the prefetch size left a locator behind and costs a further
    // round trip. Route every BLOB and CLOB column through these - a bare GetString on a CLOB column
    // reads the same locator without being counted, and the count would lie.
    private static byte[] ReadBytes(OracleDataReader reader, int ordinal)
    {
        using var blob = reader.GetOracleBlob(ordinal);
        var value = blob.Value;
        OracleRoundTrips.CountLobRead(reader, value.Length);
        return value;
    }

    private static string ReadText(OracleDataReader reader, int ordinal)
    {
        var value = reader.GetString(ordinal);
        OracleRoundTrips.CountLobRead(reader, value.Length);
        return value;
    }

    // A NULL CLOB costs nothing: there is no locator to follow, so the null branch is not a LOB read.
    private static string? ReadTextOrNull(OracleDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : ReadText(reader, ordinal);
}
