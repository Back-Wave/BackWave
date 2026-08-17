using System.Collections.Concurrent;
using System.Data.Common;
using System.Text.Json;
using BackWave.Core;
using BackWave.Diagnostics;
using BackWave.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace BackWave.Postgres;

/// <summary>
/// The Postgres Storage Adapter. Claim contention is delegated to the database via
/// set-based FOR UPDATE SKIP LOCKED; every multi-row transition runs in one transaction,
/// so crash-mid-write exposes nothing partial. Time is always an input — the database
/// clock is never consulted for semantics.
/// </summary>
// The claim's row-level SKIP LOCKED needs no lock table. The one exception is a per-Queue advisory
// lock (pg_advisory_xact_lock) taken by the claim's config-read path and the operator config setters:
// a row lock on a not-yet-existent queue_limits row locks nothing, so it cannot serialize a claim
// against the FIRST pause/limit — the advisory key exists before the row does (issue 0193).
public sealed class PostgresJobStore : IJobStore, IWakeUpHintSource, IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly PostgresStoreOptions _options;

    // Swaps the canonical 'backwave' schema qualifier for the configured SchemaName in every query
    // and DDL script (ADR 0040). The default schema is a zero-cost passthrough.
    private readonly SchemaRewriter _schema;

    // The EFFECTIVE Job History Policy (§5.12, ADR 0011): the configured rung with the top one
    // downgraded by the Failure Detail env kill-switch. Resolved once — env is an input to the run.
    private readonly JobHistoryPolicy _historyPolicy;
    private readonly SemaphoreSlim _readyGate = new(1, 1);
    private bool _ready;

    // Per-Queue unlimited-Queue cache (issue 0170). The Concurrency Limit (I3) and Paused flag (§5.8)
    // share one queue_limits row, mutated only by rare operator actions; a claim on a limited or paused
    // Queue must lock and read that row, but the common unlimited, unpaused Queue need not pay the
    // round-trip + FOR UPDATE at all. A claim that observes a Queue unlimited AND unpaused stamps it here
    // with the config generation seen BEFORE the read and the wall-clock tick; a later claim skips the
    // round-trip while that stamp's generation is still current AND it is younger than QueueLimitRefreshMs.
    // The generation fence closes a race the row lock cannot: a FOR UPDATE on a not-yet-existent
    // queue_limits row locks nothing, so it does not serialize against the FIRST pause/limit insert — an
    // in-flight claim could otherwise publish a pre-change "unlimited" stamp after that write committed.
    // STALENESS BOUND: a pause/limit set on ANOTHER process is honored within QueueLimitRefreshMs; one set
    // on THIS process bumps the generation (InvalidateQueueConfig), immediately staling every stamp — even
    // one written afterward by a claim that read the old state — so the next claim re-reads under lock.
    // Keys come only from the claim's polled-Queue set (request.Queues), so the map is bounded by the
    // node's configuration, not by the (unbounded) space of Queue names jobs may target.
    private const long QueueLimitRefreshMs = 5_000;
    private long _queueConfigGeneration;
    private readonly ConcurrentDictionary<string, QueueConfigStamp> _unlimitedQueues = new();

    // One immutable stamp per cached Queue, read as a single atomic reference (never a torn multi-field
    // struct read): the config generation observed before the read, and the tick the stamp was taken.
    private sealed record QueueConfigStamp(long Generation, long Ticks);

    /// <summary>
    /// Creates the Postgres-backed job store from its options. The store opens its own connection
    /// pool from <see cref="PostgresStoreOptions.ConnectionString"/> and verifies the database
    /// schema once, on first use. Pass the instance to BackWave's <c>UseStore</c> at registration.
    /// </summary>
    /// <param name="options">
    /// Connection string, optional auto-migrate, storage size bounds, and the job-history policy.
    /// See <see cref="PostgresStoreOptions"/> for each setting and its default.
    /// </param>
    /// <example>
    /// <code>
    /// services.AddBackWave(b => b
    ///     .UseStore(new PostgresJobStore(new PostgresStoreOptions
    ///     {
    ///         ConnectionString = "Host=localhost;Database=app;Username=app;Password=secret",
    ///         AutoMigrate = true, // create/upgrade the schema on first use (dev convenience)
    ///     }))
    ///     .UseJobs(BackWaveJobs.Module));
    /// </code>
    /// </example>
    public PostgresJobStore(PostgresStoreOptions options)
    {
        _options = options;
        _schema = new SchemaRewriter(options.SchemaName);
        _historyPolicy = JobHistoryPolicyResolver.Resolve(options.HistoryPolicy);
        _dataSource = NpgsqlDataSource.Create(options.ConnectionString);
    }

    // The one place a Postgres command is built, so the configured schema is swapped into every query
    // (ADR 0040). Positional (sql, connection[, transaction]) mirrors NpgsqlCommand's own constructor,
    // so command construction reads unchanged at every call site.
    private NpgsqlCommand Cmd(string sql, NpgsqlConnection connection, NpgsqlTransaction? transaction = null)
        => new(_schema.Rewrite(sql), connection, transaction);

    /// <inheritdoc/>
    public bool SupportsTransactionalEnqueue => true;

    /// <inheritdoc/>
    public JobHistoryPolicy HistoryPolicy => _historyPolicy;

    /// <inheritdoc/>
    public StoreBounds Bounds => _options.Bounds;

    /// <summary>Disposes the underlying connection pool. Safe to call more than once.</summary>
    /// <returns>A task that completes once the pool is disposed.</returns>
    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();

    // Migrate (if opted in) and verify the schema version exactly once.
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
            if (_options.AutoMigrate)
            {
                await PostgresMigrator.MigrateAsync(
                    _dataSource, _options.SchemaName, _options.CoordinateMigration, cancellationToken).ConfigureAwait(false);
                BackWaveLog.MigrationApplied(
                    _options.LoggerFactory?.CreateLogger(PostgresDiagnostics.SourceName) ?? NullLogger.Instance, "postgresql");
            }
            await PostgresMigrator.VerifySchemaVersionAsync(_dataSource, _options.SchemaName, cancellationToken).ConfigureAwait(false);
            _ready = true;
        }
        finally
        {
            _readyGate.Release();
        }
    }

    // Test-only failpoint (issue 0034): a no-op in production (hook null), but a test arms
    // PostgresStoreOptions.FaultHook to throw at a named point between the effects of a
    // multi-effect operation, proving the surrounding transaction makes them all-or-nothing.
    private Task FailpointAsync(string name, CancellationToken cancellationToken)
        => _options.FaultHook?.Invoke(name, cancellationToken) ?? Task.CompletedTask;

    // The effective jobs-table name for the db.collection.name span attribute, honoring a custom
    // SchemaName through the same rewrite choke point every query goes through (identity by default).
    private string JobsCollection => _schema.Rewrite("backwave.jobs");

    // Classifies a store fault for the backwave.store.faults metric tag, mirroring the host's own
    // transient/terminal split: Npgsql surfaces its transient conditions (connection reset, failover,
    // deadlock victim, command timeout) through DbException.IsTransient, so that flag plus a bare
    // TimeoutException is the whole transient set. This is emit-only - the host still makes the real
    // retry/fail-stop decision from the rethrown exception.
    private static bool IsTransientStoreFault(Exception exception)
        => exception is DbException { IsTransient: true } or TimeoutException;

    // ── §5.1 Enqueue ────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async ValueTask<EnqueueResult> EnqueueAsync(
        NewJob job, DateTimeOffset now, DbTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        using var activity = PostgresDiagnostics.StartStore("enqueue", JobsCollection);
        try
        {
            return await EnqueueUntracedAsync(job, now, transaction, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            PostgresDiagnostics.RecordStoreFault(activity, exception, IsTransientStoreFault(exception));
            throw;
        }
    }

    private async ValueTask<EnqueueResult> EnqueueUntracedAsync(
        NewJob job, DateTimeOffset now, DbTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        // The parent set is a set (§5.1): duplicate ids collapse before any rule applies.
        if (job.Parents.Count > 1)
        {
            job = job with { Parents = job.Parents.Distinct().ToArray() };
        }

        if (job.Payload.Length > _options.Bounds.MaxPayloadBytes)
        {
            return EnqueueResult.PayloadTooLarge;
        }
        if (job.WireName.Length > _options.Bounds.MaxWireNameLength)
        {
            return EnqueueResult.WireNameTooLong;
        }
        if (job.Parents.Count > _options.Bounds.MaxParentsPerJob)
        {
            return EnqueueResult.TooManyParents;
        }

        if (transaction is not null)
        {
            // Transactional Enqueue: enlist in the caller's ADO.NET transaction. Their
            // rollback means the job never existed; their commit publishes it atomically.
            if (transaction is not NpgsqlTransaction { Connection: { } callerConnection } npgsqlTransaction)
            {
                throw new ArgumentException(
                    "The Postgres adapter enlists in NpgsqlTransaction instances only.", nameof(transaction));
            }
            return await EnqueueCoreAsync(callerConnection, npgsqlTransaction, job, now, cancellationToken)
                .ConfigureAwait(false);
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var ownTransaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var result = await EnqueueCoreAsync(connection, ownTransaction, job, now, cancellationToken).ConfigureAwait(false);
        await ownTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async ValueTask<EnqueueResult> EnqueueCoreAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, NewJob job, DateTimeOffset now,
        CancellationToken cancellationToken, Guid? workflowId = null)
    {
        // Lock the parents (if any) so a concurrent terminal transition cannot race the
        // latch we are about to record (invariant I2).
        var pendingParents = new List<Guid>();
        var cancelledByParent = (JobState?)null;
        if (job.Parents.Count > 0)
        {
            // Lock the parent rows one at a time in a single deterministic id order — the same
            // order latch resolution locks child sets — so an enqueue and a concurrent terminal
            // outcome over overlapping rows can never deadlock (sorted-id lock ordering, issue 0032).
            var states = new Dictionary<Guid, JobState>();
            var lockOrder = job.Parents.ToArray();
            Array.Sort(lockOrder);
            foreach (var parentId in lockOrder)
            {
                await using var parent = Cmd(
                    "SELECT state FROM backwave.jobs WHERE job_id = @id FOR UPDATE", connection, transaction);
                parent.Parameters.AddWithValue("id", parentId);
                if (await parent.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is int parentState)
                {
                    states[parentId] = (JobState)parentState;
                }
            }
            if (states.Count != job.Parents.Count)
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
                (job_id, wire_name, payload, trace_context, queue, state, due_time, parents_remaining, mode,
                 terminal_at, terminal_cause, workflow_id)
            VALUES (@id, @wire, @payload, @trace::text, @queue, @state, @due, @remaining, @mode, @terminalAt, @terminalCause, @workflowId)
            ON CONFLICT (job_id) DO NOTHING
            """,
            connection, transaction);
        insert.Parameters.AddWithValue("id", job.JobId);
        insert.Parameters.AddWithValue("wire", job.WireName);
        insert.Parameters.AddWithValue("payload", job.Payload.ToArray());
        insert.Parameters.AddWithValue("trace", (object?)job.TraceContext ?? DBNull.Value);
        insert.Parameters.AddWithValue("queue", job.Queue);
        insert.Parameters.AddWithValue("state", (int)state);
        insert.Parameters.AddWithValue("due", job.DueTime.ToUniversalTime());
        insert.Parameters.AddWithValue("remaining", pendingParents.Count);
        insert.Parameters.AddWithValue("mode", (int)job.Mode);
        insert.Parameters.AddWithValue("terminalAt",
            cancelledByParent is not null ? now.ToUniversalTime() : DBNull.Value);
        insert.Parameters.AddWithValue("terminalCause",
            cancelledByParent is not null ? ParentFailureCause(cancelledByParent.Value) : DBNull.Value);
        // Workflow membership (ADR 0023): the immutable scalar, stamped once here at enqueue; null for
        // an ordinary job. The Core never reads it — it lives entirely above the determinism boundary.
        insert.Parameters.AddWithValue("workflowId", (object?)workflowId ?? DBNull.Value);
        if (await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
        {
            return EnqueueResult.Duplicate;
        }

        // Crash between the job row and its parent edges: rollback must leave neither (issue 0034).
        await FailpointAsync("enqueue", cancellationToken).ConfigureAwait(false);

        foreach (var parentId in pendingParents)
        {
            await using var edge = Cmd(
                "INSERT INTO backwave.job_parents (parent_id, child_id) VALUES (@parent, @child)",
                connection, transaction);
            edge.Parameters.AddWithValue("parent", parentId);
            edge.Parameters.AddWithValue("child", job.JobId);
            await edge.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // Job Tags (ADR 0022): the enqueue-time set, in this same transaction so they are visible
        // exactly when the job is — and rolled back with it under Transactional Enqueue.
        await InsertTagsAsync(connection, transaction, job.JobId, job.Tags, cancellationToken).ConfigureAwait(false);

        // Transition Log (§5.12): the actual resulting state — Scheduled, AwaitingParent, or
        // Cancelled (an already-terminal parent resolved against an on-success child) — at Attempt 0,
        // in this same transaction (atomic with the job row, even under Transactional Enqueue).
        await RecordTransitionAsync(connection, transaction, job.JobId, state, attempt: 0, now, cancellationToken)
            .ConfigureAwait(false);

        if (state == JobState.Scheduled && job.DueTime <= now)
        {
            // Wake-Up Hint (§8): NOTIFY is transactional, so the hint fires on commit —
            // including the caller's own commit under Transactional Enqueue — or never.
            await PublishHintAsync(connection, transaction, job.Queue, cancellationToken).ConfigureAwait(false);
        }
        return EnqueueResult.Ok;
    }

    private async ValueTask PublishHintAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string queue,
        CancellationToken cancellationToken)
    {
        await using var notify = Cmd(
            $"SELECT pg_notify('{_schema.HintChannel}', @queue)", connection, transaction);
        notify.Parameters.AddWithValue("queue", queue);
        await notify.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    // ── §5.2 Claim ──────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<JobRecord>> ClaimAsync(
        ClaimRequest request, CancellationToken cancellationToken = default)
    {
        using var activity = PostgresDiagnostics.StartStore("claim", JobsCollection);
        try
        {
            var (jobs, _) = await ClaimUntracedAsync(request, computeNextDue: false, cancellationToken).ConfigureAwait(false);
            return jobs;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            PostgresDiagnostics.RecordStoreFault(activity, exception, IsTransientStoreFault(exception));
            throw;
        }
    }

    /// <inheritdoc/>
    public async ValueTask<ClaimResult> ClaimBatchAsync(
        ClaimRequest request, CancellationToken cancellationToken = default)
    {
        using var activity = PostgresDiagnostics.StartStore("claim", JobsCollection);
        try
        {
            // Idle-poll next-due: computed on the SAME connection after every per-Queue claim
            // transaction commits, so it reads the post-claim committed snapshot. Advisory only: an
            // enqueue also fires a Wake-Up Hint (NOTIFY), so this value only bounds the rare no-hint case.
            var (jobs, nextDue) = await ClaimUntracedAsync(request, computeNextDue: true, cancellationToken).ConfigureAwait(false);
            return new ClaimResult(jobs, nextDue);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            PostgresDiagnostics.RecordStoreFault(activity, exception, IsTransientStoreFault(exception));
            throw;
        }
    }

    private async ValueTask<(IReadOnlyList<JobRecord> Jobs, DateTimeOffset? NextDue)> ClaimUntracedAsync(
        ClaimRequest request, bool computeNextDue, CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        var maxJobs = Math.Min(request.MaxJobs, _options.Bounds.MaxClaimBatch);
        var claimed = new List<JobRecord>();

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var queue in request.Queues)
        {
            if (claimed.Count >= maxJobs)
            {
                break;
            }

            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            // Concurrency Limit (I3) and Paused flag (§5.8) live in one row: lock it so concurrent
            // claimers of a limited Queue serialize on the slot count and a concurrent Pause is observed
            // atomically. A Queue recently observed unlimited AND unpaused skips this round-trip and the
            // lock entirely (issue 0170) — the common case pays nothing; see _unlimitedQueues.
            var slots = int.MaxValue;
            var paused = false;
            int? configured = null;
            if (!IsCachedUnlimited(queue))
            {
                // Serialize claim-vs-first-config on a key that exists BEFORE the row does (issue 0193):
                // the FOR UPDATE below locks nothing when no queue_limits row exists yet, so it does not
                // serialize against the FIRST pause/limit's INSERT — an in-flight claim could over-claim
                // past a first-ever limit or slip past a first-ever pause. The advisory lock (below) is
                // taken by both this read path and the operator setters, so they serialize with no row
                // present. Scoped to the read path: a cached unlimited, unpaused Queue (issue 0170) skips
                // this block entirely and still pays nothing.
                await AcquireQueueConfigLockAsync(connection, transaction, queue, cancellationToken).ConfigureAwait(false);
                // Capture the generation BEFORE the read so a concurrent operator change (which bumps it)
                // is detected when we go to publish the stamp below (issue 0170).
                var generation = Interlocked.Read(ref _queueConfigGeneration);
                await using (var limit = Cmd(
                    "SELECT max_concurrent, paused FROM backwave.queue_limits WHERE queue = @queue FOR UPDATE",
                    connection, transaction))
                {
                    limit.Parameters.AddWithValue("queue", queue);
                    await using var reader = await limit.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                    if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        configured = reader.IsDBNull(0) ? null : reader.GetInt32(0);
                        paused = reader.GetBoolean(1);
                    }
                }
                CacheQueueConfig(queue, configured, paused, generation);
            }
            if (paused)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                continue; // a Paused Queue yields nothing to Claim (§5.8)
            }
            if (configured is { } limitValue)
            {
                await using var leased = Cmd(
                    "SELECT count(*) FROM backwave.jobs WHERE queue = @queue AND state = 2",
                    connection, transaction);
                leased.Parameters.AddWithValue("queue", queue);
                var inUse = (long)(await leased.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
                slots = limitValue - (int)inUse;
            }
            if (slots <= 0)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            // The single contended operation: set-based FOR UPDATE SKIP LOCKED.
            await using var claim = Cmd(
                """
                WITH candidates AS (
                    SELECT job_id FROM backwave.jobs
                    WHERE queue = @queue AND state = 0 AND due_time <= @now
                    ORDER BY due_time, sequence
                    LIMIT @take
                    FOR UPDATE SKIP LOCKED
                )
                UPDATE backwave.jobs j
                SET state = 2, attempt = j.attempt + 1, lease_owner = @worker, lease_expiry = @expiry
                FROM candidates c
                WHERE j.job_id = c.job_id
                RETURNING j.job_id, j.wire_name, j.payload, j.queue, j.state, j.due_time, j.attempt,
                          j.lease_owner, j.lease_expiry, j.cancel_requested, j.terminal_at,
                          j.terminal_cause, j.schedule_id, j.parents_remaining, j.mode, j.trace_context,
                          j.sequence, j.workflow_id,
                          -- Job Tags (ADR 0022) ride back with the claim as a correlated aggregate in
                          -- THIS round-trip — never a second SELECT — so the no-tags hot path pays only a
                          -- PK-indexed empty lookup (NULL) and is never N+1. The empty-string-key => Label
                          -- discriminator is reconstructed below. Always correct, no gating staleness.
                          (SELECT json_agg(json_build_object('k', t.key, 'v', t.value))
                           FROM backwave.job_tags t WHERE t.job_id = j.job_id)::text
                """,
                connection, transaction);
            claim.Parameters.AddWithValue("queue", queue);
            claim.Parameters.AddWithValue("now", request.Now.ToUniversalTime());
            claim.Parameters.AddWithValue("take", Math.Min(maxJobs - claimed.Count, slots));
            claim.Parameters.AddWithValue("worker", request.WorkerId);
            claim.Parameters.AddWithValue("expiry", (request.Now + request.LeaseDuration).ToUniversalTime());
            var queueClaims = new List<JobRecord>();
            await using (var reader = await claim.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var job = ReadJob(reader);
                    // Column 18 is the correlated tag aggregate (json or NULL when the job has none).
                    if (!reader.IsDBNull(18))
                    {
                        job = job with { Tags = ParseTagsJson(reader.GetString(18)) };
                    }
                    queueClaims.Add(job);
                }
            }
            // Crash after the lease write, before commit: rollback must un-lease every row (issue 0034).
            await FailpointAsync("claim", cancellationToken).ConfigureAwait(false);
            // Transition Log (§5.12): one Leased entry per claimed job at its post-claim Attempt, in
            // ONE set-based INSERT in this same transaction (atomic with the lease write).
            await RecordTransitionsBatchAsync(
                connection, transaction,
                [.. queueClaims.Select(j => (j.JobId, JobState.Leased, j.Attempt, (string?)null))],
                request.Now, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            // RETURNING does not guarantee order; the contract's per-Queue
            // (DueTime, enqueue order) does (§5.2).
            claimed.AddRange(queueClaims.OrderBy(j => j.DueTime).ThenBy(j => j.Sequence));
        }

        // Tags already rode back with the claim (the correlated aggregate above), so no separate
        // hydration round-trip — even when tags are present they are batch-hydrated, never N+1.
        var nextDue = computeNextDue
            ? await NextDueAsync(connection, request, cancellationToken).ConfigureAwait(false)
            : null;
        return (claimed, nextDue);
    }

    // The earliest future instant a currently-empty claim could begin returning work through time alone,
    // for idle-poll backoff. Read on the connection the claim just committed on, after every per-Queue
    // transaction commits. A served, non-paused queue that still holds a due-now Scheduled job (withheld
    // by a concurrency limit or the batch cap) reports Now, so the caller keeps the floor cadence;
    // otherwise the earliest future Scheduled due time across served, non-paused queues, or null when none
    // is scheduled. Advisory only.
    private async ValueTask<DateTimeOffset?> NextDueAsync(
        NpgsqlConnection connection, ClaimRequest request, CancellationToken cancellationToken)
    {
        // A queue is paused only when its queue_limits row sets paused = true; absent row = not paused.
        await using var cmd = Cmd(
            """
            SELECT j.due_time
            FROM backwave.jobs j
            LEFT JOIN backwave.queue_limits ql ON ql.queue = j.queue
            WHERE j.state = 0
                AND j.queue = ANY(@queues)
                AND COALESCE(ql.paused, false) = false
            ORDER BY j.due_time
            LIMIT 1
            """,
            connection);
        cmd.Parameters.AddWithValue("queues", request.Queues.ToArray());
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null; // nothing scheduled in any served, non-paused queue
        }
        var earliest = reader.GetFieldValue<DateTimeOffset>(0);
        // Due now but withheld (concurrency limit or batch cap): clamp to Now so the poller does not back off.
        return earliest <= request.Now ? request.Now : earliest;
    }

    // True when a claim recently observed this Queue unlimited AND unpaused under the CURRENT config
    // generation, so the queue_limits round-trip + FOR UPDATE may be skipped (issue 0170). A stamp from
    // a superseded generation, an aged-out stamp, or no stamp all fall through to a fresh read.
    private bool IsCachedUnlimited(string queue)
        => _unlimitedQueues.TryGetValue(queue, out var stamp)
           && stamp.Generation == Interlocked.Read(ref _queueConfigGeneration)
           && Environment.TickCount64 - stamp.Ticks < QueueLimitRefreshMs;

    // Publishes an "unlimited, unpaused" stamp tagged with the generation observed before the read; a
    // limited or paused Queue is simply not stamped, so it keeps re-reading under lock. If an operator
    // change committed on THIS process since that generation was captured, the stamp is born stale and
    // IsCachedUnlimited ignores it — no removal, hence no removal race (issue 0170).
    private void CacheQueueConfig(string queue, int? configured, bool paused, long observedGeneration)
    {
        if (configured is null && !paused)
        {
            _unlimitedQueues[queue] = new QueueConfigStamp(observedGeneration, Environment.TickCount64);
        }
    }

    // An operator pause/resume or limit change on THIS process bumps the config generation, immediately
    // staling every unlimited stamp — including one an in-flight claim publishes afterward from the old
    // state — so the next claim re-reads the row under lock (issue 0170).
    private void InvalidateQueueConfig() => Interlocked.Increment(ref _queueConfigGeneration);

    // Serializes claim-vs-first-config on a per-Queue advisory key that exists before the queue_limits
    // row does (issue 0193). hashtext maps the Queue name into the int lock space — a collision merely
    // over-serializes two unrelated Queues (correct, at worst a little contention), never under-locks.
    // Held to end of transaction, so both the claim read path and the operator setters that take it
    // serialize even with no row present, closing the window a row lock cannot (a lock on a missing row
    // locks nothing).
    private async Task AcquireQueueConfigLockAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string queue, CancellationToken cancellationToken)
    {
        await using var advisory = Cmd(
            "SELECT pg_advisory_xact_lock(hashtext(@queue))", connection, transaction);
        advisory.Parameters.AddWithValue("queue", queue);
        await advisory.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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
        using var activity = PostgresDiagnostics.StartStore(operation, JobsCollection);
        try
        {
            return await ReportOutcomeUntracedAsync(
                jobId, workerId, attempt, outcome, now, failureDetail, addedTags, output, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            PostgresDiagnostics.RecordStoreFault(activity, exception, IsTransientStoreFault(exception));
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
        // Job Output (ADR 0026) rides the same fence but persists ONLY on a Success outcome (every
        // other outcome — including a graceful Failure — writes none) and is independent of Job
        // History Policy. Over MaxOutputBytes it is REJECTED loudly, never truncated (a clipped blob
        // is undeserializable). The check precedes any write, so an over-limit write leaves the store
        // untouched (Effect-Once). On a fenced-out outcome the SET clause simply never runs, so the
        // buffered blob is discarded with the rest of the write — no split-brain from a stale node.
        var writeOutput = outcome is JobOutcome.Success && output is { } blob;
        if (writeOutput && output!.Value.Length > _options.Bounds.MaxOutputBytes)
        {
            throw new JobOutputTooLargeException(jobId, output.Value.Length, _options.Bounds.MaxOutputBytes);
        }
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        var (sql, configure) = outcome switch
        {
            JobOutcome.Success =>
                ("state = 3, lease_owner = NULL, lease_expiry = NULL, terminal_at = @now"
                 + (writeOutput ? ", output = @output" : string.Empty),
                    (Action<NpgsqlCommand>)(command =>
                    {
                        if (writeOutput)
                        {
                            command.Parameters.AddWithValue("output", output!.Value.ToArray());
                        }
                    })),
            JobOutcome.Failure { NextDueTime: { } retryAt } =>
                ("state = 0, due_time = @retryAt, lease_owner = NULL, lease_expiry = NULL",
                    command => command.Parameters.AddWithValue("retryAt", retryAt.ToUniversalTime())),
            JobOutcome.Failure failure =>
                ("state = 5, lease_owner = NULL, lease_expiry = NULL, terminal_at = @now, terminal_cause = @cause",
                    command => command.Parameters.AddWithValue("cause", failure.Error)),
            JobOutcome.Cancelled cancelled =>
                ("state = 4, lease_owner = NULL, lease_expiry = NULL, cancel_requested = false, " +
                 "terminal_at = @now, terminal_cause = @cause",
                    command => command.Parameters.AddWithValue("cause", cancelled.Cause)),
            JobOutcome.Unroutable unroutable =>
                ("state = 6, lease_owner = NULL, lease_expiry = NULL, terminal_at = @now, terminal_cause = @cause",
                    command => command.Parameters.AddWithValue("cause", unroutable.Reason)),
            _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
        };

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using var update = Cmd(
            $"""
            UPDATE backwave.jobs SET {sql}
            WHERE job_id = @id AND state = 2 AND lease_owner = @worker AND attempt = @attempt
              AND lease_expiry > @now
            RETURNING state
            """,
            connection, transaction);
        update.Parameters.AddWithValue("id", jobId);
        update.Parameters.AddWithValue("worker", workerId);
        update.Parameters.AddWithValue("attempt", attempt);
        update.Parameters.AddWithValue("now", now.ToUniversalTime());
        configure(update);

        if (await update.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not int newState)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return OutcomeResult.StaleLease; // the (workerId, attempt) fence
        }

        // Job Tags delta (ADR 0022): the runtime Tags the handler buffered ride the SAME fenced
        // transaction — applied only because the fence held, discarded with a fenced-out (StaleLease)
        // outcome above. Effect-Once; set semantics make re-adding an identical Tag a no-op.
        if (addedTags is { Count: > 0 })
        {
            await InsertTagsAsync(connection, transaction, jobId, addedTags, cancellationToken).ConfigureAwait(false);
        }

        // Transition Log (§5.12): the resulting state at this Attempt (the claim already counted
        // it, so the outcome does not change it), atomic with the outcome write. Failure Detail
        // rides only the failing transition; every other outcome records null.
        await RecordTransitionAsync(
            connection, transaction, jobId, (JobState)newState, attempt, now, cancellationToken,
            failureDetail: outcome is JobOutcome.Failure ? failureDetail : null)
            .ConfigureAwait(false);

        if (((JobState)newState).IsTerminal())
        {
            // Crash after the terminal write, before the latch cascade: rollback must leave the
            // parent non-terminal and every child latch un-decremented (issue 0034, invariant I2).
            await FailpointAsync("report-outcome", cancellationToken).ConfigureAwait(false);
            await ResolveChildLatchesAsync(connection, transaction, jobId, (JobState)newState, now, cancellationToken)
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
        using var activity = PostgresDiagnostics.StartStore("report_outcomes", JobsCollection);
        try
        {
            return await ReportOutcomesUntracedAsync(batch, now, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            PostgresDiagnostics.RecordStoreFault(activity, exception, IsTransientStoreFault(exception));
            throw;
        }
    }

    private async ValueTask<IReadOnlyList<OutcomeReportResult>> ReportOutcomesUntracedAsync(
        IReadOnlyList<OutcomeReport> batch, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        // Empty batch applies nothing — no transaction needed.
        if (batch.Count == 0)
        {
            return [];
        }

        // Job Output (ADR 0026): over MaxOutputBytes is REJECTED loudly, never truncated. The check
        // spans the WHOLE batch and precedes ANY write, so an over-limit row leaves the store
        // untouched (Effect-Once). Output rides the fence only on a Success outcome.
        foreach (var row in batch)
        {
            if (row.Outcome is JobOutcome.Success && row.Output is { } blob
                && blob.Length > _options.Bounds.MaxOutputBytes)
            {
                throw new JobOutputTooLargeException(row.JobId, blob.Length, _options.Bounds.MaxOutputBytes);
            }
        }

        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        var nowUtc = now.ToUniversalTime();

        // The set-valued payload: each row's fence keys (worker, attempt) plus its COMPUTED target
        // state and the per-state columns (terminal instant, cause, retry due-time). Output blobs and
        // Tag deltas stay OUT of this vector — the few rows carrying them get a per-row write below,
        // inside this same transaction, so the common pure-drain row rides the batch UPDATE alone.
        var count = batch.Count;
        var ids = new Guid[count];
        var workers = new string[count];
        var attempts = new int[count];
        var states = new int[count];
        var causes = new string?[count];
        var dues = new DateTimeOffset?[count];
        var terminalAts = new DateTimeOffset?[count];
        for (var i = 0; i < count; i++)
        {
            var row = batch[i];
            ids[i] = row.JobId;
            workers[i] = row.WorkerId;
            attempts[i] = row.Attempt;
            // Success → Succeeded (3, terminal now); Failure with a retry instant → Scheduled (0, due
            // then, NOT terminal); Failure at the ceiling → Dead-Lettered (5); Cancelled → 4; Unroutable
            // → Quarantined (6). The cause rides terminal failures/cancel/unroutable; due rides retry.
            (int State, string? Cause, DateTimeOffset? Due, DateTimeOffset? TerminalAt) target = row.Outcome switch
            {
                JobOutcome.Success => (3, null, null, nowUtc),
                JobOutcome.Failure { NextDueTime: { } retryAt } => (0, null, retryAt.ToUniversalTime(), null),
                JobOutcome.Failure failure => (5, failure.Error, null, nowUtc),
                JobOutcome.Cancelled cancelled => (4, cancelled.Cause, null, nowUtc),
                JobOutcome.Unroutable unroutable => (6, unroutable.Reason, null, nowUtc),
                _ => throw new ArgumentOutOfRangeException(nameof(batch)),
            };
            (states[i], causes[i], dues[i], terminalAts[i]) = target;
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // One fenced multi-row UPDATE: the WHERE applies the per-(worker, attempt) Effect-Once fence to
        // every row INDEPENDENTLY. A row whose lease is no longer live simply fails to join and changes
        // nothing (StaleLease); a matched row applies and is returned via RETURNING, keyed by job id.
        // due_time moves only for a retry row (COALESCE keeps it for everyone else); cancel_requested
        // clears only for a Cancelled row. terminal_at/terminal_cause carry per-row (null for retry).
        var matched = new Dictionary<Guid, int>();
        await using (var update = Cmd(
            """
            UPDATE backwave.jobs j
            SET state = d.state,
                lease_owner = NULL,
                lease_expiry = NULL,
                terminal_at = d.terminal_at,
                terminal_cause = d.cause,
                due_time = COALESCE(d.due, j.due_time),
                cancel_requested = CASE WHEN d.state = 4 THEN false ELSE j.cancel_requested END
            FROM unnest(@ids::uuid[], @workers::text[], @attempts::int[], @states::int[],
                        @causes::text[], @dues::timestamptz[], @terminalAts::timestamptz[])
                 AS d(job_id, worker, attempt, state, cause, due, terminal_at)
            WHERE j.job_id = d.job_id AND j.state = 2 AND j.lease_owner = d.worker
              AND j.attempt = d.attempt AND j.lease_expiry > @now
            RETURNING j.job_id, j.state
            """,
            connection, transaction))
        {
            update.Parameters.AddWithValue("ids", ids);
            update.Parameters.AddWithValue("workers", workers);
            update.Parameters.AddWithValue("attempts", attempts);
            update.Parameters.AddWithValue("states", states);
            update.Parameters.AddWithValue("causes", causes);
            update.Parameters.AddWithValue("dues", dues);
            update.Parameters.AddWithValue("terminalAts", terminalAts);
            update.Parameters.AddWithValue("now", nowUtc);
            await using var reader = await update.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                matched[reader.GetGuid(0)] = reader.GetInt32(1);
            }
        }

        // Output and Tag deltas land ONLY for matched rows — a fenced-out (StaleLease) row leaves
        // nothing a stale node buffered. Output persists only on a Success outcome; Tags union onto
        // the job's existing tags (set semantics, so an identical tag is a no-op). Both ride this
        // same fenced transaction, keeping the common drain row a pure batch UPDATE.
        foreach (var row in batch)
        {
            if (!matched.ContainsKey(row.JobId))
            {
                continue;
            }
            if (row.Outcome is JobOutcome.Success && row.Output is { } blob)
            {
                await using var setOutput = Cmd(
                    "UPDATE backwave.jobs SET output = @output WHERE job_id = @id", connection, transaction);
                setOutput.Parameters.AddWithValue("id", row.JobId);
                setOutput.Parameters.AddWithValue("output", blob.ToArray());
                await setOutput.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            if (row.AddedTags is { Count: > 0 } addedTags)
            {
                await InsertTagsAsync(connection, transaction, row.JobId, addedTags, cancellationToken).ConfigureAwait(false);
            }
        }

        // Transition Log (§5.12): one entry per matched row for its resulting state at this Attempt,
        // in ONE set-based INSERT atomic with the outcome write. Failure Detail rides only a failing
        // transition; every other outcome records null. The batch honors the history policy (Off
        // appends nothing), so the noop-drain hot path adds no transition statements at all.
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

        // First-level child-latch resolution for the matched TERMINAL ids only (a retry row stays
        // non-terminal and gates nothing). One lookup finds the few terminal parents that actually
        // gate a Dependency; the existing per-parent cascade then resolves each recursively, so a deep
        // dependency graph stays correct. The common no-children batch adds zero per-job statements.
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
            // Crash after the terminal write, before the latch cascade: rollback must leave every
            // parent non-terminal and every child latch un-resolved (issue 0034, invariant I2). Kept a
            // separate statement from the UPDATE so the failpoint seam survives at BATCH granularity.
            await FailpointAsync("report-outcome", cancellationToken).ConfigureAwait(false);

            var parents = new List<Guid>();
            await using (var withChildren = Cmd(
                "SELECT DISTINCT parent_id FROM backwave.job_parents WHERE parent_id = ANY(@ids)",
                connection, transaction))
            {
                withChildren.Parameters.AddWithValue("ids", terminalIds.ToArray());
                await using var reader = await withChildren.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    parents.Add(reader.GetGuid(0));
                }
            }
            parents.Sort(); // deterministic lock order, as everywhere else (issue 0032)
            foreach (var parentId in parents)
            {
                await ResolveChildLatchesAsync(
                    connection, transaction, parentId, (JobState)matched[parentId], now, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        // One result per input row, in input order, keyed by job id: matched ⇒ Applied, else StaleLease.
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
    /// The latch (invariant I2), inside the same transaction as the terminal transition.
    /// Deleting the edge claims it: each parent-child edge resolves exactly once.
    /// </summary>
    private async Task ResolveChildLatchesAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid parentId, JobState parentState, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var work = new Stack<(Guid ParentId, JobState ParentState)>();
        work.Push((parentId, parentState));

        while (work.Count > 0)
        {
            var (currentParent, currentState) = work.Pop();

            var children = new List<Guid>();
            await using (var edges = Cmd(
                "DELETE FROM backwave.job_parents WHERE parent_id = @parent RETURNING child_id",
                connection, transaction))
            {
                edges.Parameters.AddWithValue("parent", currentParent);
                await using var reader = await edges.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    children.Add(reader.GetGuid(0));
                }
            }

            // Lock the child rows in a single deterministic id order — the same order the
            // enqueue path locks parent sets — so two transactions over overlapping rows can
            // never acquire locks in opposite orders and deadlock (issue 0032). Ordering only;
            // the latch-cascade semantics below are unchanged.
            children.Sort();

            foreach (var childId in children)
            {
                int childState, remaining, mode, childAttempt;
                await using (var child = Cmd(
                    "SELECT state, parents_remaining, mode, attempt FROM backwave.jobs WHERE job_id = @id FOR UPDATE",
                    connection, transaction))
                {
                    child.Parameters.AddWithValue("id", childId);
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
                        SET state = 4, parents_remaining = 0, terminal_at = @now, terminal_cause = @cause
                        WHERE job_id = @id
                        """,
                        connection, transaction);
                    cancel.Parameters.AddWithValue("id", childId);
                    cancel.Parameters.AddWithValue("now", now.ToUniversalTime());
                    cancel.Parameters.AddWithValue("cause", ParentFailureCause(currentState));
                    await cancel.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    await RecordTransitionAsync(connection, transaction, childId, JobState.Cancelled, childAttempt, now, cancellationToken)
                        .ConfigureAwait(false);
                    work.Push((childId, JobState.Cancelled)); // cascade
                    continue;
                }

                await using var resolve = Cmd(
                    remaining - 1 > 0
                        ? "UPDATE backwave.jobs SET parents_remaining = parents_remaining - 1 WHERE job_id = @id"
                        : """
                          UPDATE backwave.jobs
                          SET state = 0, parents_remaining = 0, due_time = GREATEST(due_time, @now)
                          WHERE job_id = @id
                          """,
                    connection, transaction);
                resolve.Parameters.AddWithValue("id", childId);
                if (remaining - 1 <= 0)
                {
                    resolve.Parameters.AddWithValue("now", now.ToUniversalTime());
                }
                await resolve.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                // Only the latch RELEASE (last parent terminal → Scheduled) is a state change worth
                // a transition; a mere decrement keeps the child in AwaitingParent (§5.12).
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

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Cmd(
            """
            UPDATE backwave.jobs
            SET lease_expiry = @expiry
            WHERE job_id = ANY(@ids) AND state = 2 AND lease_owner = @worker AND lease_expiry > @now
            RETURNING job_id, cancel_requested
            """,
            connection);
        command.Parameters.AddWithValue("expiry", (now + leaseDuration).ToUniversalTime());
        command.Parameters.AddWithValue("ids", jobIds.ToArray());
        command.Parameters.AddWithValue("worker", workerId);
        command.Parameters.AddWithValue("now", now.ToUniversalTime());

        var renewed = new Dictionary<Guid, bool>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                renewed[reader.GetGuid(0)] = reader.GetBoolean(1);
            }
        }

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
        using var activity = PostgresDiagnostics.StartStore("expire_leases", JobsCollection);
        try
        {
            return await ExpireLeasesUntracedAsync(now, maxJobs, queues, disposition, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            PostgresDiagnostics.RecordStoreFault(activity, exception, IsTransientStoreFault(exception));
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

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // SKIP LOCKED makes concurrent sweeps dispose disjoint sets: exactly-once disposal.
        // Scoped to the caller's served Queues so each group applies its own policy (§5.5).
        var expired = new List<(Guid JobId, int Attempt)>();
        await using (var select = Cmd(
            """
            SELECT job_id, attempt FROM backwave.jobs
            WHERE state = 2 AND lease_expiry <= @now AND queue = ANY(@queues)
            ORDER BY lease_expiry
            LIMIT @max
            FOR UPDATE SKIP LOCKED
            """,
            connection, transaction))
        {
            select.Parameters.AddWithValue("now", now.ToUniversalTime());
            select.Parameters.AddWithValue("queues", queues.ToArray());
            select.Parameters.AddWithValue("max", maxJobs);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                expired.Add((reader.GetGuid(0), reader.GetInt32(1)));
            }
        }

        // Partition by the disposition (pure data): retry at a backoff instant, or dead-letter
        // at the ceiling. The claim already counted the Attempt, so expiry just disposes it.
        var retryIds = new List<Guid>();
        var retryDueTimes = new List<DateTimeOffset>();
        var deadIds = new List<Guid>();
        var deadCauses = new List<string>();
        foreach (var (jobId, attempt) in expired)
        {
            if (disposition.NextAttemptAt(attempt, now) is { } retryAt)
            {
                retryIds.Add(jobId);
                retryDueTimes.Add(retryAt.ToUniversalTime());
            }
            else
            {
                deadIds.Add(jobId);
                deadCauses.Add($"Lease expired on attempt {attempt} (attempt ceiling reached).");
            }
        }

        // Set-based disposition: one UPDATE for the whole retry set, one for the dead-letter set
        // — O(1) statements, not one per job. The per-row due_time/cause ride in as arrays.
        if (retryIds.Count > 0)
        {
            await using var reschedule = Cmd(
                """
                UPDATE backwave.jobs j
                SET state = 0, due_time = d.due, lease_owner = NULL, lease_expiry = NULL
                FROM unnest(@ids::uuid[], @dues::timestamptz[]) AS d(job_id, due)
                WHERE j.job_id = d.job_id
                """,
                connection, transaction);
            reschedule.Parameters.AddWithValue("ids", retryIds.ToArray());
            reschedule.Parameters.AddWithValue("dues", retryDueTimes.ToArray());
            await reschedule.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (deadIds.Count > 0)
        {
            await using var deadLetter = Cmd(
                """
                UPDATE backwave.jobs j
                SET state = 5, lease_owner = NULL, lease_expiry = NULL, terminal_at = @now, terminal_cause = d.cause
                FROM unnest(@ids::uuid[], @causes::text[]) AS d(job_id, cause)
                WHERE j.job_id = d.job_id
                """,
                connection, transaction);
            deadLetter.Parameters.AddWithValue("now", now.ToUniversalTime());
            deadLetter.Parameters.AddWithValue("ids", deadIds.ToArray());
            deadLetter.Parameters.AddWithValue("causes", deadCauses.ToArray());
            await deadLetter.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            // Crash after the dead-letter write, before the latch cascade: rollback must leave the
            // parent leased and every child latch un-resolved (issue 0034, invariant I2).
            await FailpointAsync("lease-expiry", cancellationToken).ConfigureAwait(false);

            // Latch resolution touches only dead-lettered jobs that actually parent a
            // Dependency — one lookup for the whole set, then cascade just those (§5.6, I2).
            // The common no-children sweep adds zero per-job statements.
            var parents = new List<Guid>();
            await using (var withChildren = Cmd(
                "SELECT DISTINCT parent_id FROM backwave.job_parents WHERE parent_id = ANY(@ids)",
                connection, transaction))
            {
                withChildren.Parameters.AddWithValue("ids", deadIds.ToArray());
                await using var reader = await withChildren.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    parents.Add(reader.GetGuid(0));
                }
            }
            parents.Sort(); // deterministic lock order, as everywhere else (issue 0032)
            foreach (var parentId in parents)
            {
                await ResolveChildLatchesAsync(connection, transaction, parentId, JobState.DeadLettered, now, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        // Transition Log (§5.12): one entry per expired job for its resulting state —
        // Scheduled (rescheduled) or DeadLettered (ceiling) — at its post-claim Attempt
        // (expiry counts as the already-claimed Attempt), atomic with the disposition writes.
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

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        int state, attempt;
        await using (var current = Cmd(
            "SELECT state, attempt FROM backwave.jobs WHERE job_id = @id FOR UPDATE", connection, transaction))
        {
            current.Parameters.AddWithValue("id", jobId);
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
                    "UPDATE backwave.jobs SET state = 4, terminal_at = @now, terminal_cause = @actor WHERE job_id = @id",
                    connection, transaction))
                {
                    cancel.Parameters.AddWithValue("id", jobId);
                    cancel.Parameters.AddWithValue("now", now.ToUniversalTime());
                    cancel.Parameters.AddWithValue("actor", actor);
                    await cancel.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
                // Transition Log (§5.12): the immediate Cancelled state, atomic with the cancel.
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
                    "UPDATE backwave.jobs SET cancel_requested = true WHERE job_id = @id", connection, transaction))
                {
                    request.Parameters.AddWithValue("id", jobId);
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

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // Only Dead-Lettered (5) or Quarantined (6) recover; the state guard rejects anything
        // else without effect (§5.8). Attempt resets to 0, due now (§3).
        await using var update = Cmd(
            """
            UPDATE backwave.jobs
            SET state = 0, attempt = 0, due_time = @now, lease_owner = NULL, lease_expiry = NULL,
                cancel_requested = false, terminal_at = NULL, terminal_cause = NULL
            WHERE job_id = @id AND state IN (5, 6)
            RETURNING job_id
            """,
            connection, transaction);
        update.Parameters.AddWithValue("id", jobId);
        update.Parameters.AddWithValue("now", now.ToUniversalTime());
        if (await update.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return RequeueResult.NotRequeueable;
        }

        // Transition Log (§5.12): back to Scheduled at Attempt 0 (the requeue resets the budget, §3).
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

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // Serialize against an in-flight claim's first-config read (issue 0193): take the same advisory
        // key the claim read path takes, so this pause serializes with a claim even before any
        // queue_limits row exists (a row lock on a missing row would lock nothing). Released at commit.
        await AcquireQueueConfigLockAsync(connection, transaction, queue, cancellationToken).ConfigureAwait(false);

        // Upsert the flag, preserving any Concurrency Limit already on the row.
        await using (var upsert = Cmd(
            """
            INSERT INTO backwave.queue_limits (queue, paused) VALUES (@queue, @paused)
            ON CONFLICT (queue) DO UPDATE SET paused = @paused
            """,
            connection, transaction))
        {
            upsert.Parameters.AddWithValue("queue", queue);
            upsert.Parameters.AddWithValue("paused", paused);
            await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await AppendAuditAsync(connection, transaction, actor, action, queue, now, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        InvalidateQueueConfig(); // pause/resume on this process is honored on the next claim (0170)
    }

    /// <inheritdoc/>
    public async ValueTask<TriggerScheduleResult> TriggerScheduleNowAsync(
        string scheduleId, string actor, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        (string WireName, byte[] Payload, string Queue)? schedule = null;
        await using (var select = Cmd(
            "SELECT wire_name, payload, queue FROM backwave.schedules WHERE schedule_id = @id", connection, transaction))
        {
            select.Parameters.AddWithValue("id", scheduleId);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                schedule = (reader.GetString(0), reader.GetFieldValue<byte[]>(1), reader.GetString(2));
            }
        }
        if (schedule is null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return TriggerScheduleResult.ScheduleNotFound;
        }

        // One instance due now; the Cursor is never touched, so future ticks are unaffected.
        // The id is deterministic per (schedule, instant), so a retried trigger collapses.
        await using (var insert = Cmd(
            """
            INSERT INTO backwave.jobs (job_id, wire_name, payload, queue, state, due_time, schedule_id)
            VALUES (@id, @wire, @payload, @queue, 0, @due, @scheduleId)
            ON CONFLICT (job_id) DO NOTHING
            """,
            connection, transaction))
        {
            insert.Parameters.AddWithValue("id", JobIds.ForMintedTick(scheduleId, now));
            insert.Parameters.AddWithValue("wire", schedule.Value.WireName);
            insert.Parameters.AddWithValue("payload", schedule.Value.Payload);
            insert.Parameters.AddWithValue("queue", schedule.Value.Queue);
            insert.Parameters.AddWithValue("due", now.ToUniversalTime());
            insert.Parameters.AddWithValue("scheduleId", scheduleId);
            if (await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0)
            {
                // Transition Log (§5.12): the minted instance's first Scheduled state, at Attempt 0.
                await RecordTransitionAsync(connection, transaction, JobIds.ForMintedTick(scheduleId, now),
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

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Cmd(
            "SELECT actor, action, target, recorded_at FROM backwave.operator_audit WHERE target = @target ORDER BY sequence",
            connection);
        command.Parameters.AddWithValue("target", target);

        var records = new List<OperatorAuditRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            records.Add(new OperatorAuditRecord(
                reader.GetString(0), (OperatorAction)reader.GetInt32(1), reader.GetString(2),
                reader.GetFieldValue<DateTimeOffset>(3)));
        }
        return records;
    }

    // Appends one Operator audit record (spec §5.8) inside the action's transaction.
    private async Task AppendAuditAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string actor, OperatorAction action,
        string target, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var audit = Cmd(
            """
            INSERT INTO backwave.operator_audit (actor, action, target, recorded_at)
            VALUES (@actor, @action, @target, @now)
            """,
            connection, transaction);
        audit.Parameters.AddWithValue("actor", actor);
        audit.Parameters.AddWithValue("action", (int)action);
        audit.Parameters.AddWithValue("target", target);
        audit.Parameters.AddWithValue("now", now.ToUniversalTime());
        await audit.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    // Appends one Transition Log entry (spec §5.12) for a job's resulting state, inside the SAME
    // transaction as the state change it records — a crash leaves neither or both (§4). The
    // ordinal is the per-job max + 1 (a sub-select against the same table), so it climbs even as
    // oldest rows age out. The trailing bounded delete enforces MaxTransitionsPerJob (§7): once the
    // cap is exceeded, the oldest entry is dropped. `now` is always the caller's clock — the
    // database clock is never consulted (Virtual Time stays meaningful in the Conformance Suite).
    // `failureDetail` is the Shell-captured exception text, written only on a failing transition
    // (§5.12) and clamped to MaxFailureDetailBytes; null on every other transition.
    private async Task RecordTransitionAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid jobId, JobState state,
        int attempt, DateTimeOffset now, CancellationToken cancellationToken, string? failureDetail = null)
    {
        // Job History Policy (§5.12, ADR 0011) gates writes, not schema. Off appends no row at all;
        // Transitions appends the row but never the detail; the full rung keeps the clamped detail.
        // The table always exists — flipping the policy is config, never a migration.
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
            SELECT @id, COALESCE(MAX(ordinal) + 1, 0), @now, @state, @attempt, @detail
            FROM backwave.job_transitions WHERE job_id = @id
            """,
            connection, transaction))
        {
            insert.Parameters.AddWithValue("id", jobId);
            insert.Parameters.AddWithValue("now", now.ToUniversalTime());
            insert.Parameters.AddWithValue("state", (int)state);
            insert.Parameters.AddWithValue("attempt", attempt);
            insert.Parameters.AddWithValue(
                "detail", (object?)_options.Bounds.ClampFailureDetail(failureDetail) ?? DBNull.Value);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // Per-job-life cap: keep only the newest MaxTransitionsPerJob entries, dropping oldest.
        await using var prune = Cmd(
            """
            DELETE FROM backwave.job_transitions
            WHERE job_id = @id AND ordinal <= (
                SELECT MAX(ordinal) FROM backwave.job_transitions WHERE job_id = @id
            ) - @cap
            """,
            connection, transaction);
        prune.Parameters.AddWithValue("id", jobId);
        prune.Parameters.AddWithValue("cap", _options.Bounds.MaxTransitionsPerJob);
        await prune.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    // Appends a BATCH of Transition Log entries (§5.12) in ONE set-based INSERT — the per-row
    // recorder amortized for the claim and batched-report paths, where each job appears exactly
    // once per batch so its ordinal (the per-job MAX(ordinal)+1) is well-defined. Runs inside the
    // caller's transaction, so the whole batch is atomic with the lease/outcome write, exactly as
    // the per-row calls were. The global Position rides the job_transitions SEQUENCE default, one
    // nextval per row. One set-based DELETE prunes the batch to MaxTransitionsPerJob (§7). Honors
    // the history policy: Off writes nothing; Transitions writes the rows but never the detail; the
    // full rung keeps the clamped detail. `now` is always the caller's clock.
    private async Task RecordTransitionsBatchAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        IReadOnlyList<(Guid JobId, JobState State, int Attempt, string? FailureDetail)> rows,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (rows.Count == 0 || _historyPolicy == JobHistoryPolicy.Off)
        {
            return;
        }

        var ids = new Guid[rows.Count];
        var states = new int[rows.Count];
        var attempts = new int[rows.Count];
        var details = new string?[rows.Count];
        for (var i = 0; i < rows.Count; i++)
        {
            ids[i] = rows[i].JobId;
            states[i] = (int)rows[i].State;
            attempts[i] = rows[i].Attempt;
            // Transitions records the row but never the detail; the full rung clamps and keeps it.
            details[i] = _historyPolicy == JobHistoryPolicy.Transitions
                ? null
                : _options.Bounds.ClampFailureDetail(rows[i].FailureDetail);
        }

        // RETURNING the assigned ordinals lets the prune be skipped entirely (below) when no job in
        // the batch has reached the cap — the common 2-transition job pays no DELETE round-trip.
        var maxNewOrdinal = -1L;
        await using (var insert = Cmd(
            """
            INSERT INTO backwave.job_transitions (job_id, ordinal, recorded_at, state, attempt, failure_detail)
            SELECT u.id, COALESCE(t.maxord, -1) + 1, @now, u.state, u.attempt, u.detail
            FROM unnest(@ids::uuid[], @states::int[], @attempts::int[], @details::text[])
                 WITH ORDINALITY AS u(id, state, attempt, detail, ord)
            LEFT JOIN (
                SELECT job_id, MAX(ordinal) AS maxord FROM backwave.job_transitions
                WHERE job_id = ANY(@ids) GROUP BY job_id
            ) t ON t.job_id = u.id
            ORDER BY u.ord
            RETURNING ordinal
            """,
            connection, transaction))
        {
            insert.Parameters.AddWithValue("ids", ids);
            insert.Parameters.AddWithValue("states", states);
            insert.Parameters.AddWithValue("attempts", attempts);
            insert.Parameters.AddWithValue("details", details);
            insert.Parameters.AddWithValue("now", now.ToUniversalTime());
            await using var reader = await insert.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var ordinal = reader.GetInt64(0);
                if (ordinal > maxNewOrdinal)
                {
                    maxNewOrdinal = ordinal;
                }
            }
        }

        // Per-job-life cap (§7): skip the prune entirely unless some job's new ordinal reached the
        // cap — a job nowhere near MaxTransitionsPerJob never pays the DELETE. When some job did
        // reach it, one set-based DELETE keeps only the newest MaxTransitionsPerJob per job (the
        // correlated MAX no-ops for the jobs still under the cap).
        if (maxNewOrdinal < _options.Bounds.MaxTransitionsPerJob)
        {
            return;
        }
        await using var prune = Cmd(
            """
            DELETE FROM backwave.job_transitions jt
            WHERE jt.job_id = ANY(@ids)
              AND jt.ordinal <= (SELECT MAX(ordinal) FROM backwave.job_transitions x WHERE x.job_id = jt.job_id) - @cap
            """,
            connection, transaction);
        prune.Parameters.AddWithValue("ids", ids);
        prune.Parameters.AddWithValue("cap", _options.Bounds.MaxTransitionsPerJob);
        await prune.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    // ── §5.7 Schedules & minting ────────────────────────────────────────────────

    /// <inheritdoc/>
    public async ValueTask UpsertScheduleAsync(ScheduleRecord schedule, CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        // Redefining a schedule keeps its Cursor: ticks already resolved never replay.
        await using var command = Cmd(
            """
            INSERT INTO backwave.schedules
                (schedule_id, cron, wire_name, payload, queue, cursor, time_zone_id, catch_up, no_overlap)
            VALUES (@id, @cron, @wire, @payload, @queue, @cursor, @zone, @catchUp, @noOverlap)
            ON CONFLICT (schedule_id) DO UPDATE
            SET cron = @cron, wire_name = @wire, payload = @payload, queue = @queue,
                time_zone_id = @zone, catch_up = @catchUp, no_overlap = @noOverlap
            """,
            connection);
        command.Parameters.AddWithValue("id", schedule.ScheduleId);
        command.Parameters.AddWithValue("cron", schedule.Cron);
        command.Parameters.AddWithValue("wire", schedule.WireName);
        command.Parameters.AddWithValue("payload", schedule.Payload.ToArray());
        command.Parameters.AddWithValue("queue", schedule.Queue);
        command.Parameters.AddWithValue("cursor", schedule.Cursor.ToUniversalTime());
        command.Parameters.AddWithValue("zone", (object?)schedule.TimeZoneId ?? DBNull.Value);
        command.Parameters.AddWithValue("catchUp", (int)schedule.CatchUp);
        command.Parameters.AddWithValue("noOverlap", schedule.NoOverlap);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask RemoveScheduleAsync(string scheduleId, CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Cmd(
            "DELETE FROM backwave.schedules WHERE schedule_id = @id", connection);
        command.Parameters.AddWithValue("id", scheduleId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<ScheduleSnapshot>> ListSchedulesAsync(CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        // Payload is deliberately omitted from this hot-path listing (§5.7, issue 0039): the
        // mint planner never reads it, and MintDue re-reads it from the row, so the per-poll
        // load carries no blobs.
        await using var command = Cmd(
            """
            SELECT s.schedule_id, s.cron, s.wire_name, s.queue, s.cursor,
                   s.time_zone_id, s.catch_up, s.no_overlap, s.skipped_ticks,
                   EXISTS (SELECT 1 FROM backwave.jobs j
                           WHERE j.schedule_id = s.schedule_id AND j.state IN (0, 1, 2)) AS has_live
            FROM backwave.schedules s
            ORDER BY s.schedule_id
            """,
            connection);

        var snapshots = new List<ScheduleSnapshot>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
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
                    Cursor = reader.GetFieldValue<DateTimeOffset>(4),
                    TimeZoneId = reader.IsDBNull(5) ? null : reader.GetString(5),
                    CatchUp = (CatchUpPolicy)reader.GetInt32(6),
                    NoOverlap = reader.GetBoolean(7),
                    SkippedTicks = ParseSkippedTicks(reader.GetString(8)),
                },
                HasLiveInstance: reader.GetBoolean(9)));
        }
        return snapshots;
    }

    /// <inheritdoc/>
    public async ValueTask<int> MintDueAsync(
        IReadOnlyList<MintDecision> decisions, CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        var minted = 0;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var decision in decisions)
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            // Cursor fencing: advancing the cursor claims the decision's ticks whole.
            (string WireName, byte[] Payload, string Queue, string SkippedTicks)? schedule = null;
            await using (var fence = Cmd(
                """
                UPDATE backwave.schedules
                SET cursor = @newCursor
                WHERE schedule_id = @id AND cursor = @expected
                RETURNING wire_name, payload, queue, skipped_ticks
                """,
                connection, transaction))
            {
                fence.Parameters.AddWithValue("id", decision.ScheduleId);
                fence.Parameters.AddWithValue("expected", decision.ExpectedCursor.ToUniversalTime());
                fence.Parameters.AddWithValue("newCursor", decision.NewCursor.ToUniversalTime());
                await using var reader = await fence.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    schedule = (reader.GetString(0), reader.GetFieldValue<byte[]>(1),
                        reader.GetString(2), reader.GetString(3));
                }
            }
            if (schedule is null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                continue; // another node already minted these ticks
            }

            if (decision.SkippedTicks.Count > 0)
            {
                var combined = ParseSkippedTicks(schedule.Value.SkippedTicks)
                    .Concat(decision.SkippedTicks)
                    .TakeLast(_options.Bounds.MaxRecordedSkippedTicks)
                    .ToList();
                await using var record = Cmd(
                    "UPDATE backwave.schedules SET skipped_ticks = @ticks::jsonb WHERE schedule_id = @id",
                    connection, transaction);
                record.Parameters.AddWithValue("id", decision.ScheduleId);
                record.Parameters.AddWithValue("ticks", RenderSkippedTicks(combined));
                await record.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            // Crash after the cursor advanced, before the instances are minted: rollback must
            // restore the cursor so the ticks are minted, never silently lost (issue 0034).
            await FailpointAsync("mint-due", cancellationToken).ConfigureAwait(false);

            var mintedForDecision = 0;
            foreach (var tick in decision.Ticks)
            {
                await using var insert = Cmd(
                    """
                    INSERT INTO backwave.jobs (job_id, wire_name, payload, queue, state, due_time, schedule_id)
                    VALUES (@id, @wire, @payload, @queue, 0, @due, @scheduleId)
                    ON CONFLICT (job_id) DO NOTHING
                    """,
                    connection, transaction);
                insert.Parameters.AddWithValue("id", JobIds.ForMintedTick(decision.ScheduleId, tick));
                insert.Parameters.AddWithValue("wire", schedule.Value.WireName);
                insert.Parameters.AddWithValue("payload", schedule.Value.Payload);
                insert.Parameters.AddWithValue("queue", schedule.Value.Queue);
                insert.Parameters.AddWithValue("due", tick.ToUniversalTime());
                insert.Parameters.AddWithValue("scheduleId", decision.ScheduleId);
                if (await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0)
                {
                    mintedForDecision++;
                    // MintDue carries no `now`; the tick (the instance's due instant) is the
                    // deterministic timestamp for its first Scheduled transition (§5.12).
                    await RecordTransitionAsync(connection, transaction, JobIds.ForMintedTick(decision.ScheduleId, tick),
                        JobState.Scheduled, attempt: 0, tick, cancellationToken).ConfigureAwait(false);
                }
            }
            if (mintedForDecision > 0)
            {
                // Minted ticks are due by construction — hint the cluster (§8). pg_notify
                // dedups identical notifications within a transaction.
                await PublishHintAsync(connection, transaction, schedule.Value.Queue, cancellationToken)
                    .ConfigureAwait(false);
            }
            minted += mintedForDecision;
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return minted;
    }

    // ── §5.10 Queue configuration ───────────────────────────────────────────────

    /// <inheritdoc/>
    public async ValueTask SetConcurrencyLimitAsync(
        string queue, int? limit, string actor, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        // A transaction so the advisory lock is held across the upsert (issue 0193): it serializes this
        // first-ever limit against an in-flight claim even before any queue_limits row exists — a FOR
        // UPDATE on the missing row would lock nothing, letting a parked claim over-claim past the limit.
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await AcquireQueueConfigLockAsync(connection, transaction, queue, cancellationToken).ConfigureAwait(false);
        await using (var command = Cmd(
            """
            INSERT INTO backwave.queue_limits (queue, max_concurrent) VALUES (@queue, @limit)
            ON CONFLICT (queue) DO UPDATE SET max_concurrent = @limit
            """,
            connection, transaction))
        {
            command.Parameters.AddWithValue("queue", queue);
            command.Parameters.AddWithValue("limit", (object?)limit ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await AppendAuditAsync(
            connection, transaction, actor, OperatorAction.SetConcurrencyLimit, queue, now, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        InvalidateQueueConfig(); // a limit set/cleared on this process is honored on the next claim (0170)
    }

    // ── §5.9 Monitor reads ──────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async ValueTask<JobRecord?> GetJobAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Cmd(
            $"SELECT {JobColumns} FROM backwave.jobs WHERE job_id = @id", connection);
        command.Parameters.AddWithValue("id", jobId);
        JobRecord? record;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
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

        // Reads ONLY the output column (ADR 0026), so a large blob never rides the listing/claim
        // path. Null for an unknown job or one that never set output; deleted with the job row under
        // retention for free. The Core never calls this — no determinism-boundary surface.
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Cmd(
            "SELECT output FROM backwave.jobs WHERE job_id = @id", connection);
        command.Parameters.AddWithValue("id", jobId);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        // Explicit nullable cast: byte[] has an implicit conversion to ReadOnlyMemory<byte>, so an
        // unqualified `: null` here would be the empty `default` memory (HasValue) rather than no value.
        return result is byte[] bytes ? new ReadOnlyMemory<byte>(bytes) : (ReadOnlyMemory<byte>?)null;
    }

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<JobTransition>> GetJobHistoryAsync(
        Guid jobId, CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        // The Transition Log, oldest first (§5.12). Rows are deleted with the job via FK cascade
        // (§5.11), so an absent or purged job simply yields an empty timeline.
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Cmd(
            """
            SELECT ordinal, recorded_at, state, attempt, failure_detail
            FROM backwave.job_transitions WHERE job_id = @id ORDER BY ordinal
            """,
            connection);
        command.Parameters.AddWithValue("id", jobId);

        var transitions = new List<JobTransition>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            transitions.Add(new JobTransition(
                reader.GetInt64(0),
                reader.GetFieldValue<DateTimeOffset>(1),
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

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        // Only the filters a query actually uses become predicates: the catch-all
        // (@x IS NULL OR col = @x) form defeats index seeks on both databases (§5.9).
        await using var command = new NpgsqlCommand { Connection = connection };
        var conditions = new List<string>();
        AppendScopeConditions(query, conditions, command);
        var newestFirst = query.SortDirection == JobSortDirection.NewestFirst;
        if (query.AfterSequence is { } after)
        {
            // The cursor is direction-relative: newest-first continues toward OLDER jobs.
            conditions.Add(newestFirst ? "sequence < @after" : "sequence > @after");
            command.Parameters.AddWithValue("after", after);
        }
        var where = conditions.Count > 0 ? $"WHERE {string.Join(" AND ", conditions)}" : string.Empty;
        var order = newestFirst ? "ORDER BY sequence DESC" : "ORDER BY sequence";
        command.CommandText = _schema.Rewrite($"SELECT {JobColumns} FROM backwave.jobs {where} {order} LIMIT @take");
        command.Parameters.AddWithValue("take", Math.Min(query.MaxResults, _options.Bounds.MaxMonitorPageSize));

        var jobs = new List<JobRecord>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
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

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Cmd(
            "SELECT queue, state, count(*) FROM backwave.jobs GROUP BY queue, state ORDER BY queue, state",
            connection);

        var counts = new List<QueueStateCount>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            counts.Add(new QueueStateCount(
                reader.GetString(0), (JobState)reader.GetInt32(1), (int)reader.GetInt64(2)));
        }
        return counts;
    }

    // Builds the spec §5.9 scope conditions shared by ListJobsAsync and FacetAsync — the scalar
    // filters plus the AND-ed Job Tag predicates (ADR 0022), each an EXISTS over job_tags correlated
    // to the job row (has-key-any-value omits the value condition). Everything is parameterized onto
    // `command`. Pagination is NOT a scope condition — the caller adds it. The empty-string key
    // sentinel carries Labels.
    private static void AppendScopeConditions(JobQuery query, List<string> conditions, NpgsqlCommand command)
    {
        if (query.State is { } state)
        {
            conditions.Add("state = @state");
            command.Parameters.AddWithValue("state", (int)state);
        }
        if (query.Queue is { } queue)
        {
            conditions.Add("queue = @queue");
            command.Parameters.AddWithValue("queue", queue);
        }
        if (query.WireName is { } wire)
        {
            conditions.Add("wire_name = @wire");
            command.Parameters.AddWithValue("wire", wire);
        }
        if (query.ScheduleId is { } scheduleId)
        {
            conditions.Add("schedule_id = @scheduleId");
            command.Parameters.AddWithValue("scheduleId", scheduleId);
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
                    + $"AND t.key = @{keyParam} AND t.value = @{valueParam})");
                command.Parameters.AddWithValue(keyParam, predicate.Key);
                command.Parameters.AddWithValue(valueParam, value);
            }
            else
            {
                conditions.Add(
                    $"EXISTS (SELECT 1 FROM backwave.job_tags t WHERE t.job_id = jobs.job_id AND t.key = @{keyParam})");
                command.Parameters.AddWithValue(keyParam, predicate.Key);
            }
        }
    }

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<TagFacet>> FacetAsync(
        string key, JobQuery? baseQuery = null, int maxResults = int.MaxValue, CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand { Connection = connection };
        command.Parameters.AddWithValue("key", key);
        command.Parameters.AddWithValue("max", Math.Max(0, maxResults));

        // COUNT(DISTINCT job_id) is distinct-JOB counting (ADR 0022), so a job carrying the same Tag
        // once is never double-counted; a multi-value key counts the job under each value. The
        // unscoped case is served by the (key, value) index; a baseQuery scopes the population FIRST
        // with the same predicates ListJobs uses, as an `IN (<scoped job ids>)` subquery. ORDER BY
        // count DESC, value ASC under COLLATE "C" — the byte-ordinal In-Memory tiebreak — then LIMIT to
        // the top maxResults buckets (ADR 0042); the cap applies after the group-count. The explicit "C"
        // collation matters now the cap decides membership: under the database locale collation two
        // buckets tied on count could rank the other way, so the cap would keep a different bucket than
        // the reference store and the other adapters.
        var scope = string.Empty;
        if (baseQuery is not null)
        {
            var conditions = new List<string>();
            AppendScopeConditions(baseQuery, conditions, command);
            var where = conditions.Count > 0 ? $"WHERE {string.Join(" AND ", conditions)}" : string.Empty;
            scope = $"AND job_id IN (SELECT job_id FROM backwave.jobs {where})";
        }
        command.CommandText = _schema.Rewrite(
            $"SELECT value, count(DISTINCT job_id) FROM backwave.job_tags WHERE key = @key {scope} "
            + "GROUP BY value ORDER BY count(DISTINCT job_id) DESC, value COLLATE \"C\" LIMIT @max");

        var facets = new List<TagFacet>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            facets.Add(new TagFacet(reader.GetString(0), (int)reader.GetInt64(1)));
        }
        return facets;
    }

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<TagSuggestion>> SuggestTagsAsync(
        TagSuggestQuery query, CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        var limit = Math.Clamp(query.MaxResults, 1, TagSuggestQuery.MaxSuggestResults);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand { Connection = connection };
        command.Parameters.AddWithValue("limit", limit);
        // The prefix is escaped for LIKE (\, %, _) and folded by lower(); COLLATE "C" makes both the
        // fold match and the ordering byte-ordinal, so the ASCII-CI + lexicographic promises hold
        // identically to the reference store. The v9 lower() expression index serves the range scan.
        command.Parameters.AddWithValue("prefix", EscapeLike(query.Prefix));

        var suggestions = new List<TagSuggestion>();
        if (query.Key is not null)
        {
            // Stage two: distinct values under one key (key="" ⇒ Labels), keyset-paged by value.
            command.Parameters.AddWithValue("key", query.Key);
            var cursor = string.Empty;
            if (query.After is { } after)
            {
                command.Parameters.AddWithValue("av", after.Value);
                cursor = "AND (lower(value) COLLATE \"C\", value COLLATE \"C\") "
                    + "> (lower(@av) COLLATE \"C\", @av COLLATE \"C\")";
            }
            // DISTINCT is taken with an explicit "C" collation INSIDE a derived table so two values
            // differing only in case (ACME vs Acme) stay separate rows — under a non-deterministic
            // database default collation a bare DISTINCT/GROUP BY would collapse them, dropping below
            // the reference store's count. The derived table also keeps the lower(value) ORDER BY legal
            // (it references the derived v); a bare SELECT DISTINCT would reject a non-select ORDER BY.
            command.CommandText = _schema.Rewrite(
                "SELECT v FROM ("
                + "SELECT DISTINCT value COLLATE \"C\" AS v FROM backwave.job_tags "
                + "WHERE key = @key AND lower(value) COLLATE \"C\" LIKE lower(@prefix) COLLATE \"C\" || '%' ESCAPE '\\' "
                + cursor
                + ") d ORDER BY lower(v) COLLATE \"C\", v COLLATE \"C\" LIMIT @limit");

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                suggestions.Add(new TagSuggestion(query.Key, reader.GetString(0)));
            }
            return suggestions;
        }

        // Stage one: Labels (section 0) then keys (section 1), one keyset order across both blocks.
        // The fold-prefix predicate is pushed INTO each DISTINCT subquery, on lower(key)/lower(value) —
        // the leading columns of the v9 (lower(key), lower(value)) index — so each branch is a bounded
        // range seek over the typed prefix, not a full scan-and-aggregate of every keyed/labelled tag
        // row that the outer filter would then prune. The outer clause carries only the keyset cursor.
        var stageOneCursor = string.Empty;
        if (query.After is { } cursorItem)
        {
            var section = cursorItem.IsLabel ? 0 : 1;
            var name = cursorItem.IsLabel ? cursorItem.Value : cursorItem.Key;
            command.Parameters.AddWithValue("sec", section);
            command.Parameters.AddWithValue("an", name);
            stageOneCursor = "WHERE (section, lower(name) COLLATE \"C\", name COLLATE \"C\") "
                + "> (@sec, lower(@an) COLLATE \"C\", @an COLLATE \"C\") ";
        }
        command.CommandText = _schema.Rewrite(
            "WITH tokens AS ("
            + "SELECT 0 AS section, name FROM (SELECT DISTINCT value COLLATE \"C\" AS name FROM backwave.job_tags "
            + "WHERE lower(key) COLLATE \"C\" = '' "
            + "AND lower(value) COLLATE \"C\" LIKE lower(@prefix) COLLATE \"C\" || '%' ESCAPE '\\') l "
            + "UNION ALL "
            + "SELECT 1 AS section, name FROM (SELECT DISTINCT key COLLATE \"C\" AS name FROM backwave.job_tags "
            + "WHERE lower(key) COLLATE \"C\" <> '' "
            + "AND lower(key) COLLATE \"C\" LIKE lower(@prefix) COLLATE \"C\" || '%' ESCAPE '\\') k) "
            + "SELECT section, name FROM tokens "
            + stageOneCursor
            + "ORDER BY section, lower(name) COLLATE \"C\", name COLLATE \"C\" LIMIT @limit");

        await using var stageOneReader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await stageOneReader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var name = stageOneReader.GetString(1);
            suggestions.Add(stageOneReader.GetInt32(0) == 0
                ? new TagSuggestion(string.Empty, name)
                : new TagSuggestion(name, string.Empty));
        }
        return suggestions;
    }

    // Escape the LIKE metacharacters (backslash first, then % and _) so a typed prefix is matched
    // literally; the caller appends the '%' wildcard and uses ESCAPE '\'.
    private static string EscapeLike(string prefix)
        => prefix.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<QueueSettings>> ListQueueSettingsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        // The Pause flag (§5.8) and Concurrency Limit (§5.10) share the one queue_limits row the
        // claim path already reads, so the operational settings read is a single scan of it.
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Cmd(
            "SELECT queue, paused, max_concurrent FROM backwave.queue_limits ORDER BY queue", connection);

        var settings = new List<QueueSettings>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            settings.Add(new QueueSettings(
                reader.GetString(0), reader.GetBoolean(1), reader.IsDBNull(2) ? null : reader.GetInt32(2)));
        }
        return settings;
    }

    // ── Workflows (ADR 0023, issue 0120) ─────────────────────────────────────────
    //
    // The full Networked-Adapter surface, byte-for-byte equivalent to the In-Memory reference
    // (InMemoryJobStore.EnqueueWorkflowAsync/ValidateWorkflowLocked/ApplyWorkflowLocked). The whole
    // graph commits in ONE transaction — all-or-nothing — and under Transactional Enqueue it rides
    // the CALLER's transaction, so the co-resident whole-Workflow guarantee falls out: the graph
    // commits with their business write and rolls back with it (the outbox eliminated, ADR 0023).

    /// <inheritdoc/>
    public async ValueTask<WorkflowEnqueueResult> EnqueueWorkflowAsync(
        WorkflowDefinition workflow, DateTimeOffset now, DbTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        if (transaction is not null)
        {
            if (transaction is not NpgsqlTransaction { Connection: { } callerConnection } npgsqlTransaction)
            {
                throw new ArgumentException(
                    "The Postgres adapter enlists in NpgsqlTransaction instances only.", nameof(transaction));
            }
            return await EnqueueWorkflowCoreAsync(callerConnection, npgsqlTransaction, workflow, now, cancellationToken)
                .ConfigureAwait(false);
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var ownTransaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
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
        NpgsqlConnection connection, NpgsqlTransaction transaction, WorkflowDefinition workflow,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        // §5.1 / ADR 0023 admission rules for the whole graph, validated BEFORE any insert — a single
        // bad member rejects the whole batch and (because every write is in this transaction) leaves
        // the store untouched. Containment: a member's gating parent must be a member of THIS
        // Workflow (a new member, or — on append — an already-existing one).
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
        // Allowed parents: new members ∪ (on append) the existing members of this Workflow.
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
            if (member.Payload.Length > _options.Bounds.MaxPayloadBytes)
            {
                return WorkflowEnqueueResult.PayloadTooLarge;
            }
            if (member.WireName.Length > _options.Bounds.MaxWireNameLength)
            {
                return WorkflowEnqueueResult.WireNameTooLong;
            }
            var parents = member.Parents.Distinct().ToArray();
            if (parents.Length > _options.Bounds.MaxParentsPerJob)
            {
                return WorkflowEnqueueResult.TooManyParents;
            }
            if (parents.Any(p => !allowedParents.Contains(p)))
            {
                return WorkflowEnqueueResult.ContainmentViolation;
            }
        }

        // The existence check above is an unlocked read, so two concurrent creates of the same id can
        // both pass it (neither insert is committed yet) and then race the writes below (issue 0194).
        // The primary keys are the arbiter — ON CONFLICT on the row here, a Duplicate from each member
        // insert — so the loser gets a defined result, never a raw PK violation (23505). This failpoint
        // parks a create PAST every check so a test can pin that race deterministically; a no-op in production.
        await FailpointAsync("workflow-apply", cancellationToken).ConfigureAwait(false);

        // Apply. Append leaves the existing Workflows row untouched (its CreatedAt/name/lineage stand);
        // only a creation writes the row.
        if (!workflow.IsAppend)
        {
            await using var insertRow = Cmd(
                """
                INSERT INTO backwave.workflows (workflow_id, name, created_at, retention, restarted_from)
                VALUES (@id, @name, @createdAt, @retention, @restartedFrom)
                ON CONFLICT (workflow_id) DO NOTHING
                """,
                connection, transaction);
            insertRow.Parameters.AddWithValue("id", workflow.WorkflowId);
            insertRow.Parameters.AddWithValue("name", (object?)workflow.Name ?? DBNull.Value);
            insertRow.Parameters.AddWithValue("createdAt", now.ToUniversalTime());
            insertRow.Parameters.AddWithValue("retention", (int)workflow.Retention);
            insertRow.Parameters.AddWithValue("restartedFrom", (object?)workflow.RestartedFrom ?? DBNull.Value);
            // Zero rows: a concurrent create won this id first. Nothing else is written yet, so the whole
            // graph rolls back and the caller gets the defined duplicate result, not a thrown 23505.
            if (await insertRow.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
            {
                return WorkflowEnqueueResult.DuplicateWorkflow;
            }
        }

        // Members in dependency order (parents before children — possible because the builder
        // guarantees acyclicity), each stamped with the WorkflowId. EnqueueCoreAsync re-runs the §5.1
        // insert path so already-terminal in-workflow parents resolve the latch identically (the
        // ordering makes that a no-op here — parents are still live when their children insert).
        foreach (var member in TopologicallyOrdered(workflow.Members))
        {
            var applied = await EnqueueCoreAsync(connection, transaction, member, now, cancellationToken, workflow.WorkflowId)
                .ConfigureAwait(false);
            // A concurrent create can insert a member with the same JobId after this batch's existence
            // check passed (issue 0194); the member insert's ON CONFLICT reports it as Duplicate. Map it
            // to the defined result — the graph rolls back whole — rather than re-throwing.
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

        // Structural edges (ADR 0023): immutable, recorded once, so the graph view stays total even
        // after the live gating edges (job_parents) resolve away. Append adds its new edges to the set.
        foreach (var member in workflow.Members)
        {
            foreach (var parent in member.Parents.Distinct())
            {
                await using var edge = Cmd(
                    """
                    INSERT INTO backwave.workflow_edges (workflow_id, parent_id, child_id)
                    VALUES (@workflowId, @parent, @child)
                    ON CONFLICT (workflow_id, parent_id, child_id) DO NOTHING
                    """,
                    connection, transaction);
                edge.Parameters.AddWithValue("workflowId", workflow.WorkflowId);
                edge.Parameters.AddWithValue("parent", parent);
                edge.Parameters.AddWithValue("child", member.JobId);
                await edge.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        return WorkflowEnqueueResult.Ok;
    }

    private async ValueTask<bool> WorkflowExistsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid workflowId, CancellationToken cancellationToken)
    {
        await using var command = Cmd(
            "SELECT 1 FROM backwave.workflows WHERE workflow_id = @id", connection, transaction);
        command.Parameters.AddWithValue("id", workflowId);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    private async ValueTask<bool> JobExistsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid jobId, CancellationToken cancellationToken)
    {
        await using var command = Cmd(
            "SELECT 1 FROM backwave.jobs WHERE job_id = @id", connection, transaction);
        command.Parameters.AddWithValue("id", jobId);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    private async ValueTask<HashSet<Guid>> MembersOfAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid workflowId, CancellationToken cancellationToken)
    {
        var members = new HashSet<Guid>();
        await using var command = Cmd(
            "SELECT job_id FROM backwave.jobs WHERE workflow_id = @id", connection, transaction);
        command.Parameters.AddWithValue("id", workflowId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            members.Add(reader.GetGuid(0));
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

        // Each Workflow's status is a projection of its members' states (ADR 0023). One pass over the
        // member states grouped by workflow_id, joined to the Workflows rows. Ordered by CreatedAt
        // (oldest first), WorkflowId as the stable tiebreak — identical to the In-Memory listing.
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var statesByWorkflow = new Dictionary<Guid, List<JobState>>();
        await using (var members = Cmd(
            "SELECT workflow_id, state FROM backwave.jobs WHERE workflow_id IS NOT NULL", connection))
        {
            await using var reader = await members.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var wf = reader.GetGuid(0);
                (statesByWorkflow.TryGetValue(wf, out var list) ? list : statesByWorkflow[wf] = [])
                    .Add((JobState)reader.GetInt32(1));
            }
        }

        var snapshots = new List<WorkflowSnapshot>();
        await using (var workflows = Cmd(
            "SELECT workflow_id, name, created_at, restarted_from FROM backwave.workflows " +
            "ORDER BY created_at, workflow_id", connection))
        {
            await using var reader = await workflows.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var workflowId = reader.GetGuid(0);
                var states = statesByWorkflow.GetValueOrDefault(workflowId) ?? [];
                snapshots.Add(new WorkflowSnapshot
                {
                    WorkflowId = workflowId,
                    Name = reader.IsDBNull(1) ? null : reader.GetString(1),
                    CreatedAt = reader.GetFieldValue<DateTimeOffset>(2),
                    Status = WorkflowStatusProjection.Project(states),
                    MemberCount = states.Count,
                    RestartedFrom = reader.IsDBNull(3) ? null : reader.GetGuid(3),
                });
            }
        }
        return snapshots;
    }

    /// <inheritdoc/>
    public async ValueTask<WorkflowGraph?> GetWorkflowAsync(Guid workflowId, CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        string? name;
        DateTimeOffset createdAt;
        Guid? restartedFrom;
        await using (var row = Cmd(
            "SELECT name, created_at, restarted_from FROM backwave.workflows WHERE workflow_id = @id", connection))
        {
            row.Parameters.AddWithValue("id", workflowId);
            await using var reader = await row.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }
            name = reader.IsDBNull(0) ? null : reader.GetString(0);
            createdAt = reader.GetFieldValue<DateTimeOffset>(1);
            restartedFrom = reader.IsDBNull(2) ? null : reader.GetGuid(2);
        }

        // Members in enqueue order (sequence); the graph stays total because the structural edges are
        // never deleted (unlike job_parents). Hydrate Tags so a member's full JobRecord matches reads.
        var members = new List<JobRecord>();
        await using (var memberRows = Cmd(
            $"SELECT {JobColumns} FROM backwave.jobs WHERE workflow_id = @id ORDER BY sequence", connection))
        {
            memberRows.Parameters.AddWithValue("id", workflowId);
            await using var reader = await memberRows.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                members.Add(ReadJob(reader));
            }
        }
        var hydrated = await WithTagsAsync(connection, members, cancellationToken).ConfigureAwait(false);

        var edges = new List<WorkflowEdge>();
        await using (var edgeRows = Cmd(
            "SELECT parent_id, child_id FROM backwave.workflow_edges WHERE workflow_id = @id " +
            "ORDER BY parent_id, child_id", connection))
        {
            edgeRows.Parameters.AddWithValue("id", workflowId);
            await using var reader = await edgeRows.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                edges.Add(new WorkflowEdge(reader.GetGuid(0), reader.GetGuid(1)));
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
        // parent_id rows are exactly its still-gating parents — never the full original set (ADR 0009).
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var gatingParents = new List<Guid>();
        await using (var parents = Cmd(
            "SELECT parent_id FROM backwave.job_parents WHERE child_id = @id ORDER BY parent_id", connection))
        {
            parents.Parameters.AddWithValue("id", jobId);
            await using var reader = await parents.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                gatingParents.Add(reader.GetGuid(0));
            }
        }

        var children = new List<Guid>();
        await using (var childRows = Cmd(
            "SELECT child_id FROM backwave.job_parents WHERE parent_id = @id ORDER BY child_id", connection))
        {
            childRows.Parameters.AddWithValue("id", jobId);
            await using var reader = await childRows.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                children.Add(reader.GetGuid(0));
            }
        }
        return new DependencyEdges(gatingParents, children);
    }

    // ── §5.11 Retention sweep ───────────────────────────────────────────────────

    /// <inheritdoc/>
    public async ValueTask<int> PurgeTerminalAsync(
        TerminalStateClass stateClass, DateTimeOffset terminalBefore, int maxJobs,
        CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        // Workflow-aware retention (ADR 0023, §5.11), byte-equivalent to InMemoryJobStore: a NON-workflow
        // job keeps the per-job rule (terminal_at <= @before); a Workflow member is eligible only once the
        // WHOLE Workflow has drained (no member still non-terminal) AND the DRAIN instant — max member
        // terminal_at — is <= @before, so the window starts at the drain point and the graph stays coherent
        // (and materialized for Restart) for the Workflow's whole life. workflow_id is read here at
        // retention time only, never the scheduling hot path. The drained-workflows CTE folds both: a
        // non-NULL drain_at means drained, NULL means a live member still exists.
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Cmd(
            """
            WITH drained AS (
                SELECT workflow_id,
                       CASE WHEN bool_and(state IN (3, 4, 5, 6)) THEN max(terminal_at) END AS drain_at
                FROM backwave.jobs
                WHERE workflow_id IS NOT NULL
                GROUP BY workflow_id
            )
            DELETE FROM backwave.jobs
            WHERE job_id IN (
                SELECT j.job_id FROM backwave.jobs j
                LEFT JOIN drained d ON d.workflow_id = j.workflow_id
                WHERE j.state = ANY(@states)
                  AND CASE
                        WHEN j.workflow_id IS NULL THEN j.terminal_at <= @before
                        ELSE d.drain_at IS NOT NULL AND d.drain_at <= @before
                      END
                ORDER BY j.terminal_at, j.sequence
                LIMIT @max
                FOR UPDATE OF j SKIP LOCKED
            )
            """,
            connection);
        command.Parameters.AddWithValue("states", stateClass == TerminalStateClass.SucceededOrCancelled
            ? new[] { (int)JobState.Succeeded, (int)JobState.Cancelled }
            : new[] { (int)JobState.DeadLettered, (int)JobState.Quarantined });
        command.Parameters.AddWithValue("before", terminalBefore.ToUniversalTime());
        command.Parameters.AddWithValue("max", Math.Min(maxJobs, _options.Bounds.MaxPurgeBatch));
        var purged = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        // When a Workflow's last member is purged, drop its now-orphaned identity row (structural edges
        // cascade via FK) so the tables never leak rows for Workflows with no surviving jobs.
        await using var prune = Cmd(
            """
            DELETE FROM backwave.workflows w
            WHERE NOT EXISTS (SELECT 1 FROM backwave.jobs j WHERE j.workflow_id = w.workflow_id)
            """,
            connection);
        await prune.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return purged;
    }

    // ── §5.13 Observer-delivery capability (ADR 0017) ────────────────────────────
    //
    // The leaderless, at-least-once walk of the Transition Log, mirroring the In-Memory reference
    // (InMemoryJobStore). The same claim/lease spine as job claiming: the Observer's row in
    // backwave.observers is locked FOR UPDATE for the whole claim/report transaction, so exactly one
    // node advances a given Observer's cursor at a time — single delivery in the happy path, leaderless
    // redelivery on a lapsed Lease (ADR 0006). The global Position lives on job_transitions; the
    // per-(Observer, Position) attempt/resolution bookkeeping lives in backwave.observer_deliveries.

    /// <inheritdoc/>
    public async ValueTask<ObserverClaim> ClaimObserverDeliveriesAsync(
        ObserverClaimRequest request, CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // Ensure the row exists, then lock it: the lock is what gives single delivery while staying
        // leaderless — concurrent claimers of one Observer serialize here, not on a leader election.
        await using (var ensure = Cmd(
            "INSERT INTO backwave.observers (observer_id) VALUES (@id) ON CONFLICT (observer_id) DO NOTHING",
            connection, transaction))
        {
            ensure.Parameters.AddWithValue("id", request.ObserverId);
            await ensure.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        long cursor;
        string? leaseOwner;
        DateTimeOffset? leaseExpiry;
        await using (var locked = Cmd(
            "SELECT cursor_pos, lease_owner, lease_expiry FROM backwave.observers WHERE observer_id = @id FOR UPDATE",
            connection, transaction))
        {
            locked.Parameters.AddWithValue("id", request.ObserverId);
            await using var reader = await locked.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            cursor = reader.GetInt64(0);
            leaseOwner = reader.IsDBNull(1) ? null : reader.GetString(1);
            leaseExpiry = reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2);
        }

        // Remember the subscription so cursor advance (on report) can tell matching rows from the ones
        // this Observer ignores. Run config — set every claim, never changes within a run.
        var states = request.States.Select(s => (int)s).ToArray();
        await using (var sub = Cmd(
            "UPDATE backwave.observers SET sub_states = @states, sub_wire_name = @wire, sub_queue = @queue WHERE observer_id = @id",
            connection, transaction))
        {
            sub.Parameters.AddWithValue("id", request.ObserverId);
            sub.Parameters.AddWithValue("states", string.Join(',', states));
            sub.Parameters.AddWithValue("wire", (object?)request.WireName ?? DBNull.Value);
            sub.Parameters.AddWithValue("queue", (object?)request.Queue ?? DBNull.Value);
            await sub.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // A live Lease held by a different worker means that node is delivering — back off (§5.13).
        if (leaseOwner is { } held
            && !string.Equals(held, request.WorkerId, StringComparison.Ordinal)
            && leaseExpiry > request.Now)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return ObserverClaim.None(request.ObserverId);
        }

        // Matching rows after the cursor, in Position order, that are not yet resolved. The head-of-line
        // block (a Pending row still in its backoff window) is detected in the loop below, not in SQL.
        var candidates = new List<ObserverClaimedDelivery>();
        await using (var scan = Cmd(
            """
            SELECT t.position, t.job_id, t.ordinal, j.wire_name, j.queue, t.state, t.attempt,
                   t.recorded_at, t.failure_detail, d.delivery_attempt, d.next_attempt_at
            FROM backwave.job_transitions t
            JOIN backwave.jobs j ON j.job_id = t.job_id
            LEFT JOIN backwave.observer_deliveries d ON d.observer_id = @id AND d.position = t.position
            WHERE t.position > @cursor
              AND t.state = ANY(@states)
              AND (@wire::text IS NULL OR j.wire_name = @wire::text)
              AND (@queue::text IS NULL OR j.queue = @queue::text)
              AND (d.resolution IS NULL OR d.resolution = 0)
            ORDER BY t.position
            LIMIT @take
            """,
            connection, transaction))
        {
            scan.Parameters.AddWithValue("id", request.ObserverId);
            scan.Parameters.AddWithValue("cursor", cursor);
            scan.Parameters.AddWithValue("states", states);
            scan.Parameters.AddWithValue("wire", (object?)request.WireName ?? DBNull.Value);
            scan.Parameters.AddWithValue("queue", (object?)request.Queue ?? DBNull.Value);
            scan.Parameters.AddWithValue("take", Math.Max(0, request.MaxRows));
            await using var reader = await scan.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var nextAttemptAt = reader.IsDBNull(10) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(10);
                // Head-of-line (§0077): a row still in its backoff window holds the cursor — claim
                // nothing past it, so in-order-per-Observer falls out of the single moving cursor.
                if (nextAttemptAt is { } next && next > request.Now)
                {
                    break;
                }
                var priorAttempt = reader.IsDBNull(9) ? 0 : reader.GetInt32(9);
                candidates.Add(new ObserverClaimedDelivery(
                    reader.GetInt64(0), reader.GetGuid(1), reader.GetInt64(2), reader.GetString(3), reader.GetString(4),
                    (JobState)reader.GetInt32(5), reader.GetInt32(6), reader.GetFieldValue<DateTimeOffset>(7),
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
                INSERT INTO backwave.observer_deliveries (observer_id, position, delivery_attempt, resolution, next_attempt_at)
                VALUES (@id, @pos, @attempt, 0, NULL)
                ON CONFLICT (observer_id, position)
                DO UPDATE SET delivery_attempt = @attempt, resolution = 0, next_attempt_at = NULL
                """,
                connection, transaction);
            upsert.Parameters.AddWithValue("id", request.ObserverId);
            upsert.Parameters.AddWithValue("pos", delivery.Position);
            upsert.Parameters.AddWithValue("attempt", delivery.DeliveryAttempt);
            await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var lease = Cmd(
            "UPDATE backwave.observers SET lease_owner = @worker, lease_expiry = @expiry WHERE observer_id = @id",
            connection, transaction))
        {
            lease.Parameters.AddWithValue("id", request.ObserverId);
            lease.Parameters.AddWithValue("worker", request.WorkerId);
            lease.Parameters.AddWithValue("expiry", (request.Now + request.LeaseDuration).ToUniversalTime());
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

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        long cursor = 0;
        string? leaseOwner = null;
        DateTimeOffset? leaseExpiry = null;
        int[] states = [];
        string? wireName = null;
        string? queue = null;
        bool found;
        await using (var locked = Cmd(
            "SELECT cursor_pos, lease_owner, lease_expiry, sub_states, sub_wire_name, sub_queue " +
            "FROM backwave.observers WHERE observer_id = @id FOR UPDATE",
            connection, transaction))
        {
            locked.Parameters.AddWithValue("id", report.ObserverId);
            await using var reader = await locked.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            found = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (found)
            {
                cursor = reader.GetInt64(0);
                leaseOwner = reader.IsDBNull(1) ? null : reader.GetString(1);
                leaseExpiry = reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2);
                states = ParseStates(reader.GetString(3));
                wireName = reader.IsDBNull(4) ? null : reader.GetString(4);
                queue = reader.IsDBNull(5) ? null : reader.GetString(5);
            }
        }

        // Commit the read lock outside the reader's scope: an open reader forbids CommitAsync on the
        // same connection.
        if (!found)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return; // unknown Observer — nothing claimed, nothing to resolve
        }

        // Fence (§5.13): only the live claim-Lease holder may resolve deliveries and advance the
        // cursor. A stale survivor of a lapsed claim reports into the void — at-least-once intact.
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
                "UPDATE backwave.observer_deliveries SET resolution = @resolution, next_attempt_at = @next " +
                "WHERE observer_id = @id AND position = @pos",
                connection, transaction);
            resolve.Parameters.AddWithValue("id", report.ObserverId);
            resolve.Parameters.AddWithValue("pos", outcome.Position);
            resolve.Parameters.AddWithValue("resolution", resolution);
            resolve.Parameters.AddWithValue(
                "next", outcome.Disposition == ObserverDeliveryDisposition.Retry && outcome.NextAttemptAt is { } at
                    ? at.ToUniversalTime() : (object)DBNull.Value);
            await resolve.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await AdvanceObserverCursorAsync(
            connection, transaction, report.ObserverId, cursor, states, wireName, queue, report.Now, cancellationToken)
            .ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sweeps the cursor forward over the contiguous prefix of resolved matching rows — and over every
    /// non-matching row, which needs no delivery — stopping at the first matching row still Pending
    /// (the head-of-line block). A dead-lettered row is recorded loudly as the cursor passes it. The
    /// set-based analogue of the In-Memory reference's row-by-row sweep; the caller holds the row lock.
    /// </summary>
    private async Task AdvanceObserverCursorAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string observerId, long cursor,
        int[] states, string? wireName, string? queue, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // The first matching row after the cursor still unresolved — the cursor cannot pass it.
        long? block;
        await using (var blockCommand = Cmd(
            """
            SELECT MIN(t.position)
            FROM backwave.job_transitions t
            JOIN backwave.jobs j ON j.job_id = t.job_id
            LEFT JOIN backwave.observer_deliveries d ON d.observer_id = @id AND d.position = t.position
            WHERE t.position > @cursor
              AND t.state = ANY(@states)
              AND (@wire::text IS NULL OR j.wire_name = @wire::text)
              AND (@queue::text IS NULL OR j.queue = @queue::text)
              AND (d.resolution IS NULL OR d.resolution = 0)
            """,
            connection, transaction))
        {
            blockCommand.Parameters.AddWithValue("id", observerId);
            blockCommand.Parameters.AddWithValue("cursor", cursor);
            blockCommand.Parameters.AddWithValue("states", states);
            blockCommand.Parameters.AddWithValue("wire", (object?)wireName ?? DBNull.Value);
            blockCommand.Parameters.AddWithValue("queue", (object?)queue ?? DBNull.Value);
            var result = await blockCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            block = result is DBNull or null ? null : (long)result;
        }

        // The cursor sweeps to the last Position before the block (or to the end if nothing blocks).
        long? newCursor;
        await using (var advance = Cmd(
            "SELECT MAX(position) FROM backwave.job_transitions WHERE position > @cursor AND (@block::bigint IS NULL OR position < @block::bigint)",
            connection, transaction))
        {
            advance.Parameters.AddWithValue("cursor", cursor);
            advance.Parameters.AddWithValue("block", (object?)block ?? DBNull.Value);
            var result = await advance.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            newCursor = result is DBNull or null ? null : (long)result;
        }

        if (newCursor is not { } target || target <= cursor)
        {
            return; // nothing to sweep — the block (or the absence of new rows) holds the cursor
        }

        // Record dead-lettered rows the cursor is about to pass — loudly, never silently dropped.
        await using (var deadLetter = Cmd(
            """
            INSERT INTO backwave.observer_dead_letters
                (observer_id, position, job_id, ordinal, state, attempt, delivery_attempts, dead_lettered_at)
            SELECT @id, t.position, t.job_id, t.ordinal, t.state, t.attempt, d.delivery_attempt, @now
            FROM backwave.job_transitions t
            JOIN backwave.observer_deliveries d ON d.observer_id = @id AND d.position = t.position
            WHERE t.position > @cursor AND t.position <= @target AND d.resolution = 2
            ON CONFLICT (observer_id, position) DO NOTHING
            """,
            connection, transaction))
        {
            deadLetter.Parameters.AddWithValue("id", observerId);
            deadLetter.Parameters.AddWithValue("cursor", cursor);
            deadLetter.Parameters.AddWithValue("target", target);
            deadLetter.Parameters.AddWithValue("now", now.ToUniversalTime());
            await deadLetter.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // The swept rows are all resolved now — drop their in-flight bookkeeping.
        await using (var sweep = Cmd(
            "DELETE FROM backwave.observer_deliveries WHERE observer_id = @id AND position <= @target",
            connection, transaction))
        {
            sweep.Parameters.AddWithValue("id", observerId);
            sweep.Parameters.AddWithValue("target", target);
            await sweep.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var move = Cmd(
            "UPDATE backwave.observers SET cursor_pos = @target WHERE observer_id = @id",
            connection, transaction))
        {
            move.Parameters.AddWithValue("id", observerId);
            move.Parameters.AddWithValue("target", target);
            await move.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static int[] ParseStates(string states) => states.Length == 0
        ? []
        : states.Split(',').Select(int.Parse).ToArray();

    /// <inheritdoc/>
    public async ValueTask<long> GetObserverCursorAsync(string observerId, CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Cmd(
            "SELECT cursor_pos FROM backwave.observers WHERE observer_id = @id", connection);
        command.Parameters.AddWithValue("id", observerId);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is DBNull or null ? -1L : (long)result;
    }

    /// <inheritdoc/>
    public async ValueTask<ObserverLag> GetObserverLagAsync(
        ObserverLagRequest request, CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        var states = request.States.Select(s => (int)s).ToArray();
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        // Subscription-aware backlog: matching transitions the durable cursor has not advanced past.
        // The cursor is -1 when the observer has never delivered, so every matching row counts.
        await using var command = Cmd(
            """
            WITH cur AS (
                SELECT COALESCE((SELECT cursor_pos FROM backwave.observers WHERE observer_id = @id), -1) AS pos
            )
            SELECT (SELECT pos FROM cur), COUNT(t.position), MIN(t.recorded_at)
            FROM backwave.job_transitions t
            JOIN backwave.jobs j ON j.job_id = t.job_id
            WHERE t.position > (SELECT pos FROM cur)
              AND t.state = ANY(@states)
              AND (@wire::text IS NULL OR j.wire_name = @wire::text)
              AND (@queue::text IS NULL OR j.queue = @queue::text)
            """,
            connection);
        command.Parameters.AddWithValue("id", request.ObserverId);
        command.Parameters.AddWithValue("states", states);
        command.Parameters.AddWithValue("wire", (object?)request.WireName ?? DBNull.Value);
        command.Parameters.AddWithValue("queue", (object?)request.Queue ?? DBNull.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        var oldest = reader.IsDBNull(2) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(2);
        return new ObserverLag(reader.GetInt64(0), (int)reader.GetInt64(1), oldest);
    }

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<ObserverDeadLetterRecord>> ListObserverDeadLettersAsync(
        string observerId, CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Cmd(
            """
            SELECT position, job_id, ordinal, state, attempt, delivery_attempts, dead_lettered_at
            FROM backwave.observer_dead_letters WHERE observer_id = @id ORDER BY position
            """,
            connection);
        command.Parameters.AddWithValue("id", observerId);

        var records = new List<ObserverDeadLetterRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            records.Add(new ObserverDeadLetterRecord(
                reader.GetInt64(0), reader.GetGuid(1), reader.GetInt64(2), (JobState)reader.GetInt32(3),
                reader.GetInt32(4), reader.GetInt32(5), reader.GetFieldValue<DateTimeOffset>(6)));
        }
        return records;
    }

    // ── §8 Wake-Up Hints ────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task<IAsyncDisposable> SubscribeAsync(
        Action<string> onHint, CancellationToken cancellationToken = default)
    {
        var subscription = new HintSubscription(_options.ConnectionString, _schema.HintChannel, onHint);
        subscription.Start();
        return Task.FromResult<IAsyncDisposable>(subscription);
    }

    // A dedicated LISTEN connection. Channel loss is a latency event, never a correctness event
    // (ADR-0005): the loop reconnects forever until disposed, and while it is down polling carries
    // everything at the poll interval.
    private sealed class HintSubscription(string connectionString, string channel, Action<string> onHint) : IAsyncDisposable
    {
        // Named bound: how long a dead hint channel waits before redialing.
        private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);

        private readonly CancellationTokenSource _stop = new();
        private Task _loop = Task.CompletedTask;

        public void Start() => _loop = RunAsync(_stop.Token);

        private async Task RunAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await using var connection = new NpgsqlConnection(connectionString);
                    await connection.OpenAsync(token).ConfigureAwait(false);
                    connection.Notification += (_, args) => onHint(args.Payload);
                    await using (var listen = new NpgsqlCommand($"LISTEN {channel}", connection))
                    {
                        await listen.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    }
                    while (!token.IsCancellationRequested)
                    {
                        await connection.WaitAsync(token).ConfigureAwait(false);
                    }
                }
                catch (Exception) when (!token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(ReconnectDelay, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync().ConfigureAwait(false);
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown path.
            }
            _stop.Dispose();
        }
    }

    // ── row mapping ─────────────────────────────────────────────────────────────

    private const string JobColumns =
        "job_id, wire_name, payload, queue, state, due_time, attempt, lease_owner, lease_expiry, " +
        "cancel_requested, terminal_at, terminal_cause, schedule_id, parents_remaining, mode, trace_context, " +
        "sequence, workflow_id";

    private static JobRecord ReadJob(NpgsqlDataReader reader) => new()
    {
        JobId = reader.GetGuid(0),
        WireName = reader.GetString(1),
        Payload = reader.GetFieldValue<byte[]>(2),
        Queue = reader.GetString(3),
        State = (JobState)reader.GetInt32(4),
        DueTime = reader.GetFieldValue<DateTimeOffset>(5),
        Attempt = reader.GetInt32(6),
        LeaseOwner = reader.IsDBNull(7) ? null : reader.GetString(7),
        LeaseExpiry = reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8),
        CancelRequested = reader.GetBoolean(9),
        TerminalAt = reader.IsDBNull(10) ? null : reader.GetFieldValue<DateTimeOffset>(10),
        TerminalCause = reader.IsDBNull(11) ? null : reader.GetString(11),
        ScheduleId = reader.IsDBNull(12) ? null : reader.GetString(12),
        ParentsRemaining = reader.GetInt32(13),
        Mode = (DependencyMode)reader.GetInt32(14),
        TraceContext = reader.IsDBNull(15) ? null : reader.GetString(15),
        Sequence = reader.GetInt64(16),
        WorkflowId = reader.IsDBNull(17) ? null : reader.GetGuid(17),
    };

    // ── Job Tags (ADR 0022) ─────────────────────────────────────────────────────

    /// <summary>
    /// Inserts a Tag set into job_tags within the caller's transaction. Tags are already a set
    /// upstream (JobTags collapses duplicates), so ON CONFLICT DO NOTHING is purely defensive
    /// against runtime re-tagging across Attempts. A Label's key is the empty-string sentinel.
    /// </summary>
    private async Task InsertTagsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid jobId, JobTags tags,
        CancellationToken cancellationToken)
    {
        foreach (var tag in tags)
        {
            await using var insert = Cmd(
                """
                INSERT INTO backwave.job_tags (job_id, key, value)
                VALUES (@id, @key, @value)
                ON CONFLICT (job_id, key, value) DO NOTHING
                """,
                connection, transaction);
            insert.Parameters.AddWithValue("id", jobId);
            insert.Parameters.AddWithValue("key", tag.Key);
            insert.Parameters.AddWithValue("value", tag.Value);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    // Reads the Tags for a batch of jobs in one round-trip (key = ANY) — never N+1. Reconstructs
    // each set with the empty-string-key => Label discriminator (Job Tags, ADR 0022). Jobs with no
    // Tags are simply absent from the map.
    private async Task<Dictionary<Guid, JobTags>> HydrateTagsAsync(
        NpgsqlConnection connection, IReadOnlyList<Guid> jobIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, JobTags>();
        if (jobIds.Count == 0)
        {
            return result;
        }
        await using var command = Cmd(
            "SELECT job_id, key, value FROM backwave.job_tags WHERE job_id = ANY(@ids)", connection);
        command.Parameters.AddWithValue("ids", jobIds.ToArray());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var jobId = reader.GetGuid(0);
            var key = reader.GetString(1);
            var value = reader.GetString(2);
            var tag = key.Length == 0 ? JobTag.Label(value) : JobTag.Keyed(key, value);
            result[jobId] = (result.TryGetValue(jobId, out var existing) ? existing : JobTags.Empty).With(tag);
        }
        return result;
    }

    // Rebuilds a Tag set from the claim's correlated json_agg ([{"k":key,"v":value}, …]).
    // Reconstructs each set with the empty-string-key => Label discriminator (ADR 0022).
    private static JobTags ParseTagsJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        var tags = JobTags.Empty;
        foreach (var element in document.RootElement.EnumerateArray())
        {
            var key = element.GetProperty("k").GetString()!;
            var value = element.GetProperty("v").GetString()!;
            tags = tags.With(key.Length == 0 ? JobTag.Label(value) : JobTag.Keyed(key, value));
        }
        return tags;
    }

    /// <summary>Returns the jobs with their Tags hydrated in one batched read (never N+1).</summary>
    private async Task<IReadOnlyList<JobRecord>> WithTagsAsync(
        NpgsqlConnection connection, IReadOnlyList<JobRecord> jobs, CancellationToken cancellationToken)
    {
        if (jobs.Count == 0)
        {
            return jobs;
        }
        var tags = await HydrateTagsAsync(connection, [.. jobs.Select(j => j.JobId)], cancellationToken)
            .ConfigureAwait(false);
        return [.. jobs.Select(j => j with { Tags = tags.TryGetValue(j.JobId, out var set) ? set : JobTags.Empty })];
    }

    private static IReadOnlyList<DateTimeOffset> ParseSkippedTicks(string json)
    {
        using var document = JsonDocument.Parse(json);
        return [.. document.RootElement.EnumerateArray().Select(e => e.GetDateTimeOffset())];
    }

    /// <summary>ISO-8601 instants need no JSON escaping, so the array renders directly.</summary>
    private static string RenderSkippedTicks(IReadOnlyList<DateTimeOffset> ticks)
        => "[" + string.Join(",", ticks.Select(t => $"\"{t.ToUniversalTime():O}\"")) + "]";
}
