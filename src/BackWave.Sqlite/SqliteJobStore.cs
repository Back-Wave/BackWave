using System.Data.Common;
using System.Text.Json;
using BackWave.Core;
using BackWave.Diagnostics;
using BackWave.Sqlite.Internal;
using BackWave.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace BackWave.Sqlite;

/// <summary>
/// BackWave's SQLite job store: a self-contained, single-host backend that keeps its tables in an
/// ordinary SQLite database file — no server, no Docker, nothing to provision beyond a file path.
/// Construct one from a <see cref="SqliteStoreOptions"/> and hand it to your BackWave registration's
/// <c>UseStore(...)</c>.
/// <para>
/// Because SQLite has a single writer, this store is for a <b>single host</b>: many threads and
/// pumps in <em>one</em> process share it safely, but you cannot point two separate machines at the
/// same file and expect coordinated job processing. Within that host it is fully consistent — every
/// write runs in a serialized transaction, so a job is claimed by exactly one worker and your
/// crash-recovery and dependency guarantees hold.
/// </para>
/// <para>
/// Two deployments, chosen entirely by which file the connection string points at:
/// <list type="bullet">
/// <item><b>Co-resident</b> — point it at your application's own database file. BackWave's tables
///   live alongside your business tables, so you can enqueue a job in the <em>same</em> transaction
///   as your own writes: either both commit or neither does, with no outbox.</item>
/// <item><b>Dedicated</b> — give BackWave its own file, separate from your business data. Simpler to
///   reason about, but a job and a business write then live in different files and cannot share a
///   transaction, so transactional enqueue is unavailable in that shape.</item>
/// </list>
/// </para>
/// </summary>
/// <example>
/// <code>
/// var store = new SqliteJobStore(new SqliteStoreOptions
/// {
///     ConnectionString = "Data Source=app.db",
///     AutoMigrate = true,
/// });
/// builder.Services.AddBackWave(b => b.UseStore(store));
/// </code>
/// </example>
public sealed class SqliteJobStore : IJobStore, IWakeUpHintSource, IStoreFaultClassifier, IAsyncDisposable
{
    // SQLITE_BUSY (5) and SQLITE_LOCKED (6): residual write-lock contention that survived the
    // busy-timeout. Transient, never an invariant violation. ADR 0007 amendment, ADR 0019, issue 0098.
    private const int SqliteBusy = 5;
    private const int SqliteLocked = 6;

    /// <inheritdoc/>
    public bool IsTransientFault(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqliteException { SqliteErrorCode: SqliteBusy or SqliteLocked })
            {
                return true;
            }
        }
        return false;
    }

    // The effective jobs-table name for the db.collection.name span attribute, honoring a custom
    // TablePrefix through the same rewrite choke point every query goes through (identity by default).
    private string JobsCollection => _schema.Rewrite("backwave_jobs");

    // Classifies a store fault for the backwave.store.faults metric tag, mirroring the host's own
    // transient/terminal split: a provider-transient DbException or a bare TimeoutException, plus this
    // adapter's own SQLITE_BUSY/SQLITE_LOCKED recognition (which the generic IsTransient flag misses).
    // Emit-only - the host still makes the real retry/fail-stop decision from the rethrown exception.
    private bool IsTransientStoreFault(Exception exception)
        => exception is DbException { IsTransient: true } or TimeoutException || IsTransientFault(exception);

    private readonly SqliteStoreOptions _options;
    private readonly string _connectionString;

    // Swaps the canonical 'backwave' table-name prefix for the configured TablePrefix in every query
    // and DDL script (ADR 0040). The default prefix is a zero-cost passthrough.
    private readonly SchemaRewriter _schema;
    private readonly JobHistoryPolicy _historyPolicy;
    private readonly SqliteSameFileGuard _sameFileGuard;
    private readonly WakeUpHintHub? _hintHub;
    private readonly SemaphoreSlim _readyGate = new(1, 1);
    private bool _ready;

    /// <summary>
    /// Creates a store over the database file named by <paramref name="options"/>. The file is opened
    /// lazily on first use; the schema is applied then if you opted in to auto-migration, and the
    /// engine version and schema version are verified before any job work runs.
    /// </summary>
    /// <param name="options">
    /// The connection string and behaviour for this store. The connection string decides whether you
    /// are running co-resident (BackWave's tables in your application's own file) or dedicated
    /// (BackWave on its own file).
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <c>null</c>.</exception>
    /// <example>
    /// <code>
    /// var store = new SqliteJobStore(new SqliteStoreOptions
    /// {
    ///     ConnectionString = "Data Source=app.db",
    ///     AutoMigrate = true,
    /// });
    /// builder.Services.AddBackWave(b => b.UseStore(store));
    /// </code>
    /// </example>
    public SqliteJobStore(SqliteStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _schema = new SchemaRewriter(options.TablePrefix);
        _connectionString = SqliteConnectionStringNormalizer.Normalize(options.ConnectionString, options.BusyTimeout);
        _historyPolicy = JobHistoryPolicyResolver.Resolve(options.HistoryPolicy);
        _sameFileGuard = new SqliteSameFileGuard(options.ConnectionString);
        // The hint hub is best-effort and scoped to this store; absent when opted out. ADR 0005.
        _hintHub = options.EnableInProcessHints ? new WakeUpHintHub() : null;
    }

    /// <inheritdoc/>
    public bool SupportsTransactionalEnqueue => true;

    /// <inheritdoc/>
    public JobHistoryPolicy HistoryPolicy => _historyPolicy;

    /// <inheritdoc/>
    public StoreBounds Bounds => _options.Bounds;

    /// <summary>Disposes the store, releasing its in-process resources. Safe to call more than once.</summary>
    /// <returns>A task that completes when the store has been disposed.</returns>
    public async ValueTask DisposeAsync()
    {
        if (_hintHub is not null)
        {
            await _hintHub.DisposeAsync().ConfigureAwait(false);
        }
        _readyGate.Dispose();
    }

    // The one place a SQLite command is built, so the configured table prefix is swapped into every
    // query (ADR 0040). Positional (sql, connection[, transaction]) mirrors SqliteCommand's own
    // constructor, so command construction reads unchanged at every call site.
    private SqliteCommand Cmd(string sql, SqliteConnection connection, SqliteTransaction? transaction = null)
        => new(_schema.Rewrite(sql), connection, transaction);

    // ── §5.1 Enqueue ────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async ValueTask<EnqueueResult> EnqueueAsync(
        NewJob job, DateTimeOffset now, DbTransaction? transaction = null, CancellationToken cancellationToken = default)
    {
        using var activity = SqliteDiagnostics.StartStore("enqueue", JobsCollection);
        try
        {
            return await EnqueueUntracedAsync(job, now, transaction, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SqliteDiagnostics.RecordStoreFault(activity, exception, IsTransientStoreFault(exception));
            throw;
        }
    }

    private async ValueTask<EnqueueResult> EnqueueUntracedAsync(
        NewJob job, DateTimeOffset now, DbTransaction? transaction = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
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
            // Transactional Enqueue (co-resident): enlist in the caller's transaction; the caller
            // owns the commit, so a rolled-back business transaction leaves no job behind. The
            // same-file guard fires here (issue 0095) and the Wake-Up Hint stays silent — ADO.NET
            // exposes no commit callback, so a hint before the user's commit would wake the pump to
            // an invisible row (issue 0097).
            if (transaction is not SqliteTransaction { Connection: { } callerConnection } sqliteTransaction)
            {
                throw new ArgumentException(
                    "The SQLite adapter enlists in SqliteTransaction instances only.", nameof(transaction));
            }
            _sameFileGuard.EnsureSameFile(callerConnection);
            var (result, _) = await EnqueueCoreAsync(callerConnection, sqliteTransaction, job, now, cancellationToken)
                .ConfigureAwait(false);
            return result;
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var ownTransaction = (SqliteTransaction)connection.BeginTransaction(deferred: false);
        var (ownResult, hintQueue) = await EnqueueCoreAsync(connection, ownTransaction, job, now, cancellationToken)
            .ConfigureAwait(false);
        await ownTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        // §8: the hint fires only AFTER the adapter's own commit — or never (issue 0097).
        if (hintQueue is not null)
        {
            _hintHub?.Publish(hintQueue);
        }
        return ownResult;
    }

    // The shared §5.1 insert path. Returns the result plus the Queue to hint once this transaction
    // commits (null when nothing should be hinted — not Scheduled, not yet due, or not Ok). The
    // caller publishes the hint after its commit on the adapter-owned path and ignores it on the
    // Transactional Enqueue path.
    private async ValueTask<(EnqueueResult Result, string? HintQueue)> EnqueueCoreAsync(
        SqliteConnection connection, SqliteTransaction transaction, NewJob job, DateTimeOffset now,
        CancellationToken cancellationToken, Guid? workflowId = null)
    {
        // Resolve parents (if any) so we record the right latch (invariant I2). Under whole-writer
        // serialization the single write lock is already held, so no row locks / sorted lock order
        // are needed (unlike the Networked Adapters).
        var pendingParents = new List<Guid>();
        var cancelledByParent = (JobState?)null;
        if (job.Parents.Count > 0)
        {
            var states = new Dictionary<Guid, JobState>();
            foreach (var parentId in job.Parents)
            {
                await using var parent = Cmd(
                    "SELECT state FROM backwave_jobs WHERE job_id = $id", connection, transaction);
                parent.Parameters.AddWithValue("$id", SqliteValueCodec.ToText(parentId));
                if (await parent.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long parentState)
                {
                    states[parentId] = SqliteValueCodec.ToEnum<JobState>(parentState);
                }
            }
            if (states.Count != job.Parents.Count)
            {
                return (EnqueueResult.UnknownParent, null);
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

        await using (var insert = Cmd(
            """
            INSERT INTO backwave_jobs
                (job_id, wire_name, payload, trace_context, queue, state, due_time, parents_remaining, mode,
                 terminal_at, terminal_cause, workflow_id)
            VALUES ($id, $wire, $payload, $trace, $queue, $state, $due, $remaining, $mode, $terminalAt, $terminalCause, $workflowId)
            ON CONFLICT (job_id) DO NOTHING
            """,
            connection, transaction))
        {
            insert.Parameters.AddWithValue("$id", SqliteValueCodec.ToText(job.JobId));
            insert.Parameters.AddWithValue("$wire", job.WireName);
            insert.Parameters.AddWithValue("$payload", job.Payload.ToArray());
            insert.Parameters.AddWithValue("$trace", (object?)job.TraceContext ?? DBNull.Value);
            insert.Parameters.AddWithValue("$queue", job.Queue);
            insert.Parameters.AddWithValue("$state", (int)state);
            insert.Parameters.AddWithValue("$due", SqliteValueCodec.ToTicks(job.DueTime));
            insert.Parameters.AddWithValue("$remaining", pendingParents.Count);
            insert.Parameters.AddWithValue("$mode", SqliteValueCodec.ToInt(job.Mode));
            insert.Parameters.AddWithValue("$terminalAt",
                cancelledByParent is not null ? SqliteValueCodec.ToTicks(now) : DBNull.Value);
            insert.Parameters.AddWithValue("$terminalCause",
                cancelledByParent is not null ? ParentFailureCause(cancelledByParent.Value) : DBNull.Value);
            insert.Parameters.AddWithValue("$workflowId",
                workflowId is { } wf ? SqliteValueCodec.ToText(wf) : DBNull.Value);
            if (await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
            {
                return (EnqueueResult.Duplicate, null);
            }
        }

        // Crash between the job row and its parent edges: rollback must leave neither (issue 0034).
        await FailpointAsync("enqueue", cancellationToken).ConfigureAwait(false);

        foreach (var parentId in pendingParents)
        {
            await using var edge = Cmd(
                "INSERT INTO backwave_job_parents (parent_id, child_id) VALUES ($parent, $child)",
                connection, transaction);
            edge.Parameters.AddWithValue("$parent", SqliteValueCodec.ToText(parentId));
            edge.Parameters.AddWithValue("$child", SqliteValueCodec.ToText(job.JobId));
            await edge.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // Job Tags (ADR 0022): the enqueue-time set, in this same transaction so they are visible
        // exactly when the job is — and rolled back with it under Transactional Enqueue.
        await InsertTagsAsync(connection, transaction, job.JobId, job.Tags, cancellationToken).ConfigureAwait(false);

        // Transition Log (§5.12): the actual resulting state at Attempt 0, atomic with the job row.
        await RecordTransitionAsync(connection, transaction, job.JobId, state, attempt: 0, now, cancellationToken)
            .ConfigureAwait(false);

        var hintQueue = state == JobState.Scheduled && job.DueTime <= now ? job.Queue : null;
        return (EnqueueResult.Ok, hintQueue);
    }

    // ── §5.2 Claim ──────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<JobRecord>> ClaimAsync(
        ClaimRequest request, CancellationToken cancellationToken = default)
    {
        using var activity = SqliteDiagnostics.StartStore("claim", JobsCollection);
        try
        {
            return await ClaimUntracedAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SqliteDiagnostics.RecordStoreFault(activity, exception, IsTransientStoreFault(exception));
            throw;
        }
    }

    private async ValueTask<IReadOnlyList<JobRecord>> ClaimUntracedAsync(
        ClaimRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        var maxJobs = Math.Min(request.MaxJobs, _options.Bounds.MaxClaimBatch);
        var claimed = new List<JobRecord>();
        if (maxJobs <= 0 || request.Queues.Count == 0)
        {
            return claimed;
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        foreach (var queue in request.Queues)
        {
            if (claimed.Count >= maxJobs)
            {
                break;
            }

            // Peek for COMMITTED due candidates with a lock-free WAL read before reaching for the
            // single write lock. This is what lets a co-resident caller hold an open write
            // transaction (its own business work, or a Transactional Enqueue not yet committed)
            // without this claim blocking on it: an uncommitted row is not in the committed snapshot,
            // so there is nothing to claim and we never contend for the writer. Committed work always
            // shows up here (and, when it does, no caller is holding the lock against it).
            await using (var peek = Cmd(
                $"""
                SELECT 1 FROM backwave_jobs
                WHERE queue = $queue AND state = {(int)JobState.Scheduled} AND due_time <= $now
                LIMIT 1
                """,
                connection))
            {
                peek.Parameters.AddWithValue("$queue", queue);
                peek.Parameters.AddWithValue("$now", SqliteValueCodec.ToTicks(request.Now));
                if (await peek.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
                {
                    continue; // nothing committed and due in this Queue — no write lock taken
                }
            }

            await using var transaction = (SqliteTransaction)connection.BeginTransaction(deferred: false);

            // Concurrency Limit (I3) and Paused flag (§5.8) live in one row; whole-writer
            // serialization makes the read-then-claim atomic without an explicit row lock.
            var slots = int.MaxValue;
            var paused = false;
            int? configured = null;
            await using (var limit = Cmd(
                "SELECT max_concurrent, paused FROM backwave_queue_limits WHERE queue = $queue",
                connection, transaction))
            {
                limit.Parameters.AddWithValue("$queue", queue);
                await using var reader = await limit.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    configured = reader.IsDBNull(0) ? null : reader.GetInt32(0);
                    paused = reader.GetBoolean(1);
                }
            }
            if (paused)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                continue; // a Paused Queue yields nothing to Claim (§5.8)
            }
            if (configured is { } limitValue)
            {
                await using var leased = Cmd(
                    $"SELECT count(*) FROM backwave_jobs WHERE queue = $queue AND state = {(int)JobState.Leased}",
                    connection, transaction);
                leased.Parameters.AddWithValue("$queue", queue);
                var inUse = (long)(await leased.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
                slots = limitValue - (int)inUse;
            }
            if (slots <= 0)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            await using var claim = Cmd(
                $"""
                UPDATE backwave_jobs
                SET state = {(int)JobState.Leased}, attempt = attempt + 1, lease_owner = $owner, lease_expiry = $expiry
                WHERE sequence IN (
                    SELECT sequence FROM backwave_jobs
                    WHERE queue = $queue AND state = {(int)JobState.Scheduled} AND due_time <= $now
                    ORDER BY due_time, sequence
                    LIMIT $take
                )
                RETURNING {JobColumns}
                """,
                connection, transaction);
            claim.Parameters.AddWithValue("$owner", request.WorkerId);
            claim.Parameters.AddWithValue("$expiry", SqliteValueCodec.ToTicks(request.Now + request.LeaseDuration));
            claim.Parameters.AddWithValue("$queue", queue);
            claim.Parameters.AddWithValue("$now", SqliteValueCodec.ToTicks(request.Now));
            claim.Parameters.AddWithValue("$take", Math.Min(maxJobs - claimed.Count, slots));

            var queueClaims = new List<JobRecord>();
            await using (var reader = await claim.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    queueClaims.Add(ReadJob(reader));
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

            // RETURNING does not guarantee order; the contract's per-Queue (DueTime, enqueue order) does (§5.2).
            claimed.AddRange(queueClaims.OrderBy(j => j.DueTime).ThenBy(j => j.Sequence));
        }

        return await WithTagsAsync(connection, claimed, cancellationToken).ConfigureAwait(false);
    }

    // ── §5.6 ReportOutcome ──────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async ValueTask<OutcomeResult> ReportOutcomeAsync(
        Guid jobId, string workerId, int attempt, JobOutcome outcome, DateTimeOffset now,
        string? failureDetail = null, JobTags? addedTags = null, ReadOnlyMemory<byte>? output = null,
        CancellationToken cancellationToken = default)
    {
        var operation = outcome switch
        {
            JobOutcome.Success => "complete",
            JobOutcome.Failure => "fail",
            _ => "report_outcome",
        };
        using var activity = SqliteDiagnostics.StartStore(operation, JobsCollection);
        try
        {
            return await ReportOutcomeUntracedAsync(
                jobId, workerId, attempt, outcome, now, failureDetail, addedTags, output, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SqliteDiagnostics.RecordStoreFault(activity, exception, IsTransientStoreFault(exception));
            throw;
        }
    }

    private async ValueTask<OutcomeResult> ReportOutcomeUntracedAsync(
        Guid jobId, string workerId, int attempt, JobOutcome outcome, DateTimeOffset now,
        string? failureDetail = null, JobTags? addedTags = null, ReadOnlyMemory<byte>? output = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        // Job Output (ADR 0026) rides the fence but persists ONLY on Success; over MaxOutputBytes it
        // is REJECTED loudly before any write (Effect-Once — an over-limit write leaves the store
        // untouched). A fenced-out outcome never runs the SET clause, so the buffered blob is discarded.
        var writeOutput = outcome is JobOutcome.Success && output is { } blob;
        if (writeOutput && output!.Value.Length > _options.Bounds.MaxOutputBytes)
        {
            throw new JobOutputTooLargeException(jobId, output.Value.Length, _options.Bounds.MaxOutputBytes);
        }
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        var (sql, configure) = BuildOutcomeUpdate(outcome, output);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)connection.BeginTransaction(deferred: false);

        int newState;
        await using (var update = Cmd(
            $"""
            UPDATE backwave_jobs SET {sql}
            WHERE job_id = $id AND state = {(int)JobState.Leased} AND lease_owner = $worker AND attempt = $attempt
              AND lease_expiry > $now
            RETURNING state
            """,
            connection, transaction))
        {
            update.Parameters.AddWithValue("$id", SqliteValueCodec.ToText(jobId));
            update.Parameters.AddWithValue("$worker", workerId);
            update.Parameters.AddWithValue("$attempt", attempt);
            update.Parameters.AddWithValue("$now", SqliteValueCodec.ToTicks(now));
            configure(update);

            var scalar = await update.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (scalar is not long resultState)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return OutcomeResult.StaleLease; // the (workerId, attempt) fence
            }
            newState = (int)resultState;
        }

        // Job Tags delta (ADR 0022): the runtime Tags the handler buffered ride the SAME fenced
        // transaction — applied only because the fence held. Set semantics: re-adding is a no-op.
        if (addedTags is { Count: > 0 })
        {
            await InsertTagsAsync(connection, transaction, jobId, addedTags, cancellationToken).ConfigureAwait(false);
        }

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

    // The per-row fenced SET clause and its parameter binding, shared by the singular path above and
    // the batch loop below so neither can drift from the §5.6 semantics: Success → Succeeded (output
    // folded into the SET only on a Success carrying a blob); a Failure with a retry instant →
    // Scheduled at that instant; a Failure at the ceiling → Dead-Lettered; Cancelled clears the
    // cancellation flag; Unroutable → Quarantined. Output over the cap is rejected by the caller
    // BEFORE any write, so the SET never persists a clipped blob.
    private static (string Sql, Action<SqliteCommand> Configure) BuildOutcomeUpdate(
        JobOutcome outcome, ReadOnlyMemory<byte>? output)
    {
        var writeOutput = outcome is JobOutcome.Success && output is { };
        return outcome switch
        {
            JobOutcome.Success =>
                ($"state = {(int)JobState.Succeeded}, lease_owner = NULL, lease_expiry = NULL, terminal_at = $now"
                 + (writeOutput ? ", output = $output" : string.Empty),
                    (Action<SqliteCommand>)(command =>
                    {
                        if (writeOutput)
                        {
                            command.Parameters.AddWithValue("$output", output!.Value.ToArray());
                        }
                    })),
            JobOutcome.Failure { NextDueTime: { } retryAt } =>
                ($"state = {(int)JobState.Scheduled}, due_time = $retryAt, lease_owner = NULL, lease_expiry = NULL",
                    command => command.Parameters.AddWithValue("$retryAt", SqliteValueCodec.ToTicks(retryAt))),
            JobOutcome.Failure failure =>
                ($"state = {(int)JobState.DeadLettered}, lease_owner = NULL, lease_expiry = NULL, terminal_at = $now, terminal_cause = $cause",
                    command => command.Parameters.AddWithValue("$cause", failure.Error)),
            JobOutcome.Cancelled cancelled =>
                ($"state = {(int)JobState.Cancelled}, lease_owner = NULL, lease_expiry = NULL, cancel_requested = 0, "
                 + "terminal_at = $now, terminal_cause = $cause",
                    command => command.Parameters.AddWithValue("$cause", cancelled.Cause)),
            JobOutcome.Unroutable unroutable =>
                ($"state = {(int)JobState.Quarantined}, lease_owner = NULL, lease_expiry = NULL, terminal_at = $now, terminal_cause = $cause",
                    command => command.Parameters.AddWithValue("$cause", unroutable.Reason)),
            _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
        };
    }

    // ── §5.6b ReportOutcomes (batch) ─────────────────────────────────────────────

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<OutcomeReportResult>> ReportOutcomesAsync(
        IReadOnlyList<OutcomeReport> batch, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        using var activity = SqliteDiagnostics.StartStore("report_outcomes", JobsCollection);
        try
        {
            return await ReportOutcomesUntracedAsync(batch, now, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SqliteDiagnostics.RecordStoreFault(activity, exception, IsTransientStoreFault(exception));
            throw;
        }
    }

    private async ValueTask<IReadOnlyList<OutcomeReportResult>> ReportOutcomesUntracedAsync(
        IReadOnlyList<OutcomeReport> batch, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        // SQLite is whole-writer-serialized and in-process (ADR 0019: WAL + BEGIN IMMEDIATE), so there
        // are NO round-trips to amortize — this method buys atomicity, not throughput. The deliverable
        // is the contract: the whole batch applies the per-(worker, attempt) fence in ONE write
        // transaction, all-or-nothing across the failpoint seam, collapsing N single-job transactions
        // into one. It is the singular fence/update/transition logic in a tight loop inside one
        // BEGIN IMMEDIATE — no vectorized SQL, which would buy SQLite nothing.

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

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)connection.BeginTransaction(deferred: false);

        // Per-row fence/update/tags/transition, all inside the one transaction. A row whose lease is no
        // longer live changes nothing (StaleLease); a matched row applies. Latch cascades for matched
        // TERMINAL rows are deferred past the failpoint seam below, so the seam stays batch-granular.
        var matched = new Dictionary<Guid, int>();
        foreach (var row in batch)
        {
            var (sql, configure) = BuildOutcomeUpdate(row.Outcome, row.Output);
            int newState;
            await using (var update = Cmd(
                $"""
                UPDATE backwave_jobs SET {sql}
                WHERE job_id = $id AND state = {(int)JobState.Leased} AND lease_owner = $worker AND attempt = $attempt
                  AND lease_expiry > $now
                RETURNING state
                """,
                connection, transaction))
            {
                update.Parameters.AddWithValue("$id", SqliteValueCodec.ToText(row.JobId));
                update.Parameters.AddWithValue("$worker", row.WorkerId);
                update.Parameters.AddWithValue("$attempt", row.Attempt);
                update.Parameters.AddWithValue("$now", SqliteValueCodec.ToTicks(now));
                configure(update);

                if (await update.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not long resultState)
                {
                    continue; // the (workerId, attempt) fence — StaleLease, nothing buffered survives
                }
                newState = (int)resultState;
            }
            matched[row.JobId] = newState;

            // Tag delta unions onto the job's existing tags (set semantics) only because the fence held.
            if (row.AddedTags is { Count: > 0 } addedTags)
            {
                await InsertTagsAsync(connection, transaction, row.JobId, addedTags, cancellationToken).ConfigureAwait(false);
            }
        }

        // Transition Log (§5.12): one entry per matched row for its resulting state at this Attempt,
        // in ONE set-based INSERT atomic with the outcome writes above. Failure Detail rides only a
        // failing transition; Off appends nothing, so the noop-drain hot path adds no rows.
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

        // Latch cascade for the matched TERMINAL rows only (a retry row stays non-terminal and gates
        // nothing), deferred to a single seam past the failpoint so the whole batch is all-or-nothing.
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
            // Crash after the terminal writes, before the latch cascade: rollback must leave every
            // parent non-terminal and every child latch un-decremented (issue 0034, invariant I2). One
            // seam for the whole batch keeps I2 all-or-nothing at batch granularity.
            await FailpointAsync("report-outcome", cancellationToken).ConfigureAwait(false);
            terminalIds.Sort(); // deterministic lock order, as everywhere else (issue 0032)
            foreach (var parentId in terminalIds)
            {
                await ResolveChildLatchesAsync(
                    connection, transaction, parentId, (JobState)matched[parentId], now, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        // One result per input row, in input order, keyed by job id: matched ⇒ Applied, else StaleLease.
        var results = new OutcomeReportResult[batch.Count];
        for (var i = 0; i < batch.Count; i++)
        {
            results[i] = new OutcomeReportResult(
                batch[i].JobId,
                matched.ContainsKey(batch[i].JobId) ? OutcomeResult.Applied : OutcomeResult.StaleLease);
        }
        return results;
    }

    // The latch (invariant I2), inside the same transaction as the terminal transition. Deleting
    // the edge claims it: each parent-child edge resolves exactly once.
    private async Task ResolveChildLatchesAsync(
        SqliteConnection connection, SqliteTransaction transaction,
        Guid parentId, JobState parentState, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var work = new Stack<(Guid ParentId, JobState ParentState)>();
        work.Push((parentId, parentState));

        while (work.Count > 0)
        {
            var (currentParent, currentState) = work.Pop();

            var children = new List<Guid>();
            await using (var edges = Cmd(
                "DELETE FROM backwave_job_parents WHERE parent_id = $parent RETURNING child_id",
                connection, transaction))
            {
                edges.Parameters.AddWithValue("$parent", SqliteValueCodec.ToText(currentParent));
                await using var reader = await edges.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    children.Add(SqliteValueCodec.ToGuid(reader.GetString(0)));
                }
            }

            foreach (var childId in children)
            {
                int childState, remaining, mode, childAttempt;
                await using (var child = Cmd(
                    "SELECT state, parents_remaining, mode, attempt FROM backwave_jobs WHERE job_id = $id",
                    connection, transaction))
                {
                    child.Parameters.AddWithValue("$id", SqliteValueCodec.ToText(childId));
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
                        $"""
                        UPDATE backwave_jobs
                        SET state = {(int)JobState.Cancelled}, parents_remaining = 0, terminal_at = $now, terminal_cause = $cause
                        WHERE job_id = $id
                        """,
                        connection, transaction);
                    cancel.Parameters.AddWithValue("$id", SqliteValueCodec.ToText(childId));
                    cancel.Parameters.AddWithValue("$now", SqliteValueCodec.ToTicks(now));
                    cancel.Parameters.AddWithValue("$cause", ParentFailureCause(currentState));
                    await cancel.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    await RecordTransitionAsync(connection, transaction, childId, JobState.Cancelled, childAttempt, now, cancellationToken)
                        .ConfigureAwait(false);
                    work.Push((childId, JobState.Cancelled)); // cascade
                    continue;
                }

                await using var resolve = Cmd(
                    remaining - 1 > 0
                        ? "UPDATE backwave_jobs SET parents_remaining = parents_remaining - 1 WHERE job_id = $id"
                        : $"""
                          UPDATE backwave_jobs
                          SET state = {(int)JobState.Scheduled}, parents_remaining = 0, due_time = MAX(due_time, $now)
                          WHERE job_id = $id
                          """,
                    connection, transaction);
                resolve.Parameters.AddWithValue("$id", SqliteValueCodec.ToText(childId));
                if (remaining - 1 <= 0)
                {
                    resolve.Parameters.AddWithValue("$now", SqliteValueCodec.ToTicks(now));
                }
                await resolve.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                // Only the latch RELEASE (last parent terminal → Scheduled) is a transition worth
                // recording; a mere decrement keeps the child in AwaitingParent (§5.12).
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
        await using var transaction = (SqliteTransaction)connection.BeginTransaction(deferred: false);

        var renewed = new Dictionary<Guid, bool>();
        var (placeholders, ids) = IdPlaceholders(jobIds, "$j");
        await using (var command = Cmd(
            $"""
            UPDATE backwave_jobs
            SET lease_expiry = $expiry
            WHERE job_id IN ({placeholders}) AND state = {(int)JobState.Leased}
              AND lease_owner = $worker AND lease_expiry > $now
            RETURNING job_id, cancel_requested
            """,
            connection, transaction))
        {
            command.Parameters.AddWithValue("$expiry", SqliteValueCodec.ToTicks(now + leaseDuration));
            command.Parameters.AddWithValue("$worker", workerId);
            command.Parameters.AddWithValue("$now", SqliteValueCodec.ToTicks(now));
            foreach (var (name, value) in ids)
            {
                command.Parameters.AddWithValue(name, value);
            }
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                renewed[SqliteValueCodec.ToGuid(reader.GetString(0))] = reader.GetBoolean(1);
            }
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
        using var activity = SqliteDiagnostics.StartStore("expire_leases", JobsCollection);
        try
        {
            return await ExpireLeasesUntracedAsync(now, maxJobs, queues, disposition, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SqliteDiagnostics.RecordStoreFault(activity, exception, IsTransientStoreFault(exception));
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
        await using var transaction = (SqliteTransaction)connection.BeginTransaction(deferred: false);

        // Whole-writer serialization disposes the expired set whole; scoped to the caller's served
        // Queues so each group applies its own policy (§5.5).
        var expired = new List<(Guid JobId, int Attempt)>();
        var (queuePlaceholders, queueParams) = ValuePlaceholders(queues, "$q");
        await using (var select = Cmd(
            $"""
            SELECT job_id, attempt FROM backwave_jobs
            WHERE state = {(int)JobState.Leased} AND lease_expiry <= $now AND queue IN ({queuePlaceholders})
            ORDER BY lease_expiry
            LIMIT $max
            """,
            connection, transaction))
        {
            select.Parameters.AddWithValue("$now", SqliteValueCodec.ToTicks(now));
            select.Parameters.AddWithValue("$max", maxJobs);
            foreach (var (name, value) in queueParams)
            {
                select.Parameters.AddWithValue(name, value);
            }
            await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                expired.Add((SqliteValueCodec.ToGuid(reader.GetString(0)), reader.GetInt32(1)));
            }
        }

        var deadLetteredParents = new List<Guid>();
        foreach (var (jobId, attempt) in expired)
        {
            if (disposition.NextAttemptAt(attempt, now) is { } retryAt)
            {
                await using var reschedule = Cmd(
                    $"""
                    UPDATE backwave_jobs
                    SET state = {(int)JobState.Scheduled}, due_time = $due, lease_owner = NULL, lease_expiry = NULL
                    WHERE job_id = $id
                    """,
                    connection, transaction);
                reschedule.Parameters.AddWithValue("$due", SqliteValueCodec.ToTicks(retryAt));
                reschedule.Parameters.AddWithValue("$id", SqliteValueCodec.ToText(jobId));
                await reschedule.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await using var deadLetter = Cmd(
                    $"""
                    UPDATE backwave_jobs
                    SET state = {(int)JobState.DeadLettered}, lease_owner = NULL, lease_expiry = NULL,
                        terminal_at = $now, terminal_cause = $cause
                    WHERE job_id = $id
                    """,
                    connection, transaction);
                deadLetter.Parameters.AddWithValue("$now", SqliteValueCodec.ToTicks(now));
                deadLetter.Parameters.AddWithValue("$cause", $"Lease expired on attempt {attempt} (attempt ceiling reached).");
                deadLetter.Parameters.AddWithValue("$id", SqliteValueCodec.ToText(jobId));
                await deadLetter.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                deadLetteredParents.Add(jobId);
            }
        }

        if (deadLetteredParents.Count > 0)
        {
            // Crash after the dead-letter writes, before the latch cascade: rollback must leave the
            // parent leased and every child latch un-resolved (issue 0034, invariant I2).
            await FailpointAsync("lease-expiry", cancellationToken).ConfigureAwait(false);
            foreach (var parentId in deadLetteredParents)
            {
                await ResolveChildLatchesAsync(connection, transaction, parentId, JobState.DeadLettered, now, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        // Transition Log (§5.12): one entry per expired job for its resulting state — at its
        // post-claim Attempt — atomic with the disposition writes.
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
        await using var transaction = (SqliteTransaction)connection.BeginTransaction(deferred: false);

        int state, attempt;
        await using (var current = Cmd(
            "SELECT state, attempt FROM backwave_jobs WHERE job_id = $id", connection, transaction))
        {
            current.Parameters.AddWithValue("$id", SqliteValueCodec.ToText(jobId));
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
                    $"UPDATE backwave_jobs SET state = {(int)JobState.Cancelled}, terminal_at = $now, terminal_cause = $actor WHERE job_id = $id",
                    connection, transaction))
                {
                    cancel.Parameters.AddWithValue("$id", SqliteValueCodec.ToText(jobId));
                    cancel.Parameters.AddWithValue("$now", SqliteValueCodec.ToTicks(now));
                    cancel.Parameters.AddWithValue("$actor", actor);
                    await cancel.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
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
                    "UPDATE backwave_jobs SET cancel_requested = 1 WHERE job_id = $id", connection, transaction))
                {
                    request.Parameters.AddWithValue("$id", SqliteValueCodec.ToText(jobId));
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
        await using var transaction = (SqliteTransaction)connection.BeginTransaction(deferred: false);

        // Only Dead-Lettered or Quarantined recover; the state guard rejects anything else without
        // effect (§5.8). Attempt resets to 0, due now (§3).
        await using (var update = Cmd(
            $"""
            UPDATE backwave_jobs
            SET state = {(int)JobState.Scheduled}, attempt = 0, due_time = $now, lease_owner = NULL, lease_expiry = NULL,
                cancel_requested = 0, terminal_at = NULL, terminal_cause = NULL
            WHERE job_id = $id AND state IN ({(int)JobState.DeadLettered}, {(int)JobState.Quarantined})
            RETURNING job_id
            """,
            connection, transaction))
        {
            update.Parameters.AddWithValue("$id", SqliteValueCodec.ToText(jobId));
            update.Parameters.AddWithValue("$now", SqliteValueCodec.ToTicks(now));
            if (await update.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return RequeueResult.NotRequeueable;
            }
        }

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
        await using var transaction = (SqliteTransaction)connection.BeginTransaction(deferred: false);

        // Upsert the flag, preserving any Concurrency Limit already on the row.
        await using (var upsert = Cmd(
            """
            INSERT INTO backwave_queue_limits (queue, paused) VALUES ($queue, $paused)
            ON CONFLICT (queue) DO UPDATE SET paused = $paused
            """,
            connection, transaction))
        {
            upsert.Parameters.AddWithValue("$queue", queue);
            upsert.Parameters.AddWithValue("$paused", paused ? 1 : 0);
            await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await AppendAuditAsync(connection, transaction, actor, action, queue, now, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask<TriggerScheduleResult> TriggerScheduleNowAsync(
        string scheduleId, string actor, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)connection.BeginTransaction(deferred: false);

        (string WireName, byte[] Payload, string Queue)? schedule = null;
        await using (var select = Cmd(
            "SELECT wire_name, payload, queue FROM backwave_schedules WHERE schedule_id = $id", connection, transaction))
        {
            select.Parameters.AddWithValue("$id", scheduleId);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                schedule = (reader.GetString(0), (byte[])reader[1], reader.GetString(2));
            }
        }
        if (schedule is null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return TriggerScheduleResult.ScheduleNotFound;
        }

        var mintedId = JobIds.ForMintedTick(scheduleId, now);
        var triggered = false;
        await using (var insert = Cmd(
            $"""
            INSERT INTO backwave_jobs (job_id, wire_name, payload, queue, state, due_time, schedule_id)
            VALUES ($id, $wire, $payload, $queue, {(int)JobState.Scheduled}, $due, $scheduleId)
            ON CONFLICT (job_id) DO NOTHING
            """,
            connection, transaction))
        {
            insert.Parameters.AddWithValue("$id", SqliteValueCodec.ToText(mintedId));
            insert.Parameters.AddWithValue("$wire", schedule.Value.WireName);
            insert.Parameters.AddWithValue("$payload", schedule.Value.Payload);
            insert.Parameters.AddWithValue("$queue", schedule.Value.Queue);
            insert.Parameters.AddWithValue("$due", SqliteValueCodec.ToTicks(now));
            insert.Parameters.AddWithValue("$scheduleId", scheduleId);
            if (await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0)
            {
                triggered = true;
                await RecordTransitionAsync(connection, transaction, mintedId, JobState.Scheduled, attempt: 0, now, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        await AppendAuditAsync(connection, transaction, actor, OperatorAction.TriggerScheduleNow, scheduleId, now, cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        if (triggered)
        {
            _hintHub?.Publish(schedule.Value.Queue);
        }
        return TriggerScheduleResult.Triggered;
    }

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<OperatorAuditRecord>> ListAuditRecordsAsync(
        string target, CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Cmd(
            "SELECT actor, action, target, recorded_at FROM backwave_operator_audit WHERE target = $target ORDER BY sequence",
            connection);
        command.Parameters.AddWithValue("$target", target);

        var records = new List<OperatorAuditRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            records.Add(new OperatorAuditRecord(
                reader.GetString(0), (OperatorAction)reader.GetInt32(1), reader.GetString(2),
                SqliteValueCodec.FromTicks(reader.GetInt64(3))));
        }
        return records;
    }

    private async Task AppendAuditAsync(
        SqliteConnection connection, SqliteTransaction transaction, string actor, OperatorAction action,
        string target, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var audit = Cmd(
            """
            INSERT INTO backwave_operator_audit (actor, action, target, recorded_at)
            VALUES ($actor, $action, $target, $now)
            """,
            connection, transaction);
        audit.Parameters.AddWithValue("$actor", actor);
        audit.Parameters.AddWithValue("$action", (int)action);
        audit.Parameters.AddWithValue("$target", target);
        audit.Parameters.AddWithValue("$now", SqliteValueCodec.ToTicks(now));
        await audit.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    // Appends one Transition Log entry (§5.12) for a job's resulting state, inside the SAME
    // transaction as the state change it records — a crash leaves neither or both (§4). The ordinal
    // is the per-job max + 1; the global position (which Postgres carries on a SEQUENCE) is
    // assigned here as MAX(position)+1 over the whole table, race-free under whole-writer
    // serialization (ADR 0019). `now` is always the caller's clock. The trailing bounded delete
    // enforces MaxTransitionsPerJob (§7). Job History Policy gates writes, not schema: Off appends
    // nothing; Transitions appends the row but never the detail.
    private async Task RecordTransitionAsync(
        SqliteConnection connection, SqliteTransaction transaction, Guid jobId, JobState state,
        int attempt, DateTimeOffset now, CancellationToken cancellationToken, string? failureDetail = null)
    {
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
            INSERT INTO backwave_job_transitions (job_id, ordinal, recorded_at, state, attempt, failure_detail, position)
            SELECT $id, COALESCE(MAX(ordinal) + 1, 0), $now, $state, $attempt, $detail,
                   (SELECT COALESCE(MAX(position), 0) + 1 FROM backwave_job_transitions)
            FROM backwave_job_transitions WHERE job_id = $id
            """,
            connection, transaction))
        {
            insert.Parameters.AddWithValue("$id", SqliteValueCodec.ToText(jobId));
            insert.Parameters.AddWithValue("$now", SqliteValueCodec.ToTicks(now));
            insert.Parameters.AddWithValue("$state", (int)state);
            insert.Parameters.AddWithValue("$attempt", attempt);
            insert.Parameters.AddWithValue(
                "$detail", (object?)_options.Bounds.ClampFailureDetail(failureDetail) ?? DBNull.Value);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // Per-job-life cap: keep only the newest MaxTransitionsPerJob entries, dropping oldest.
        await using var prune = Cmd(
            """
            DELETE FROM backwave_job_transitions
            WHERE job_id = $id AND ordinal <= (
                SELECT MAX(ordinal) FROM backwave_job_transitions WHERE job_id = $id
            ) - $cap
            """,
            connection, transaction);
        prune.Parameters.AddWithValue("$id", SqliteValueCodec.ToText(jobId));
        prune.Parameters.AddWithValue("$cap", _options.Bounds.MaxTransitionsPerJob);
        await prune.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    // Appends a BATCH of Transition Log entries (§5.12) in ONE set-based INSERT — the per-row
    // recorder amortized for the claim and batched-report paths, where each job appears exactly once
    // per batch so its ordinal (the per-job MAX(ordinal)+1) is well-defined. The rows ride a JSON
    // parameter unpacked by json_each; the LEFT JOIN supplies each job's current MAX(ordinal). The
    // global position (Postgres carries it on a SEQUENCE) is assigned as the pre-batch MAX plus a
    // per-row offset, race-free under whole-writer serialization. Runs inside the caller's
    // transaction, so the whole batch is atomic with the lease/outcome write. One set-based DELETE
    // prunes the batch to MaxTransitionsPerJob (§7). Honors the history policy: Off writes nothing;
    // Transitions writes the rows but never the detail; the full rung keeps the clamped detail.
    private async Task RecordTransitionsBatchAsync(
        SqliteConnection connection, SqliteTransaction transaction,
        IReadOnlyList<(Guid JobId, JobState State, int Attempt, string? FailureDetail)> rows,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (rows.Count == 0 || _historyPolicy == JobHistoryPolicy.Off)
        {
            return;
        }

        var payloadRows = new TransitionRow[rows.Count];
        for (var i = 0; i < rows.Count; i++)
        {
            // Transitions records the row but never the detail; the full rung clamps and keeps it.
            var detail = _historyPolicy == JobHistoryPolicy.Transitions
                ? null
                : _options.Bounds.ClampFailureDetail(rows[i].FailureDetail);
            payloadRows[i] = new TransitionRow(
                SqliteValueCodec.ToText(rows[i].JobId), (int)rows[i].State, rows[i].Attempt, detail);
        }
        var payload = JsonSerializer.Serialize(payloadRows);

        // The pre-batch global MAX(position); each row gets it plus its 1-based order in the batch.
        long basePosition;
        await using (var maxPosition = Cmd(
            "SELECT COALESCE(MAX(position), 0) FROM backwave_job_transitions", connection, transaction))
        {
            basePosition = (long)(await maxPosition.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }

        // RETURNING the assigned ordinals lets the prune be skipped entirely (below) when no job in
        // the batch has reached the cap — the common 2-transition job pays no DELETE.
        var maxNewOrdinal = -1L;
        await using (var insert = Cmd(
            """
            INSERT INTO backwave_job_transitions (job_id, ordinal, recorded_at, state, attempt, failure_detail, position)
            SELECT u.id, COALESCE(t.maxord, -1) + 1, $now, u.state, u.attempt, u.detail,
                   $basePos + ROW_NUMBER() OVER (ORDER BY u.idx)
            FROM (
                SELECT je.key AS idx,
                       json_extract(je.value, '$.JobId') AS id,
                       json_extract(je.value, '$.State') AS state,
                       json_extract(je.value, '$.Attempt') AS attempt,
                       json_extract(je.value, '$.Detail') AS detail
                FROM json_each($payload) je
            ) u
            LEFT JOIN (
                SELECT job_id, MAX(ordinal) AS maxord FROM backwave_job_transitions
                WHERE job_id IN (SELECT json_extract(value, '$.JobId') FROM json_each($payload))
                GROUP BY job_id
            ) t ON t.job_id = u.id
            RETURNING ordinal
            """,
            connection, transaction))
        {
            insert.Parameters.AddWithValue("$now", SqliteValueCodec.ToTicks(now));
            insert.Parameters.AddWithValue("$basePos", basePosition);
            insert.Parameters.AddWithValue("$payload", payload);
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
            DELETE FROM backwave_job_transitions
            WHERE job_id IN (SELECT json_extract(value, '$.JobId') FROM json_each($payload))
              AND ordinal <= (
                  SELECT MAX(ordinal) FROM backwave_job_transitions x WHERE x.job_id = backwave_job_transitions.job_id
              ) - $cap
            """,
            connection, transaction);
        prune.Parameters.AddWithValue("$payload", payload);
        prune.Parameters.AddWithValue("$cap", _options.Bounds.MaxTransitionsPerJob);
        await prune.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    // The set-valued transition row for the batch INSERT, serialized to JSON and unpacked by
    // json_each; the property names are the json_extract '$.X' paths above.
    private sealed record TransitionRow(string JobId, int State, int Attempt, string? Detail);

    // ── §5.7 Schedules & minting ────────────────────────────────────────────────

    /// <inheritdoc/>
    public async ValueTask UpsertScheduleAsync(ScheduleRecord schedule, CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        // Redefining a schedule keeps its Cursor: ticks already resolved never replay.
        await using var command = Cmd(
            """
            INSERT INTO backwave_schedules
                (schedule_id, cron, wire_name, payload, queue, cursor, time_zone_id, catch_up, no_overlap)
            VALUES ($id, $cron, $wire, $payload, $queue, $cursor, $zone, $catchUp, $noOverlap)
            ON CONFLICT (schedule_id) DO UPDATE
            SET cron = $cron, wire_name = $wire, payload = $payload, queue = $queue,
                time_zone_id = $zone, catch_up = $catchUp, no_overlap = $noOverlap
            """,
            connection);
        command.Parameters.AddWithValue("$id", schedule.ScheduleId);
        command.Parameters.AddWithValue("$cron", schedule.Cron);
        command.Parameters.AddWithValue("$wire", schedule.WireName);
        command.Parameters.AddWithValue("$payload", schedule.Payload.ToArray());
        command.Parameters.AddWithValue("$queue", schedule.Queue);
        command.Parameters.AddWithValue("$cursor", SqliteValueCodec.ToTicks(schedule.Cursor));
        command.Parameters.AddWithValue("$zone", (object?)schedule.TimeZoneId ?? DBNull.Value);
        command.Parameters.AddWithValue("$catchUp", (int)schedule.CatchUp);
        command.Parameters.AddWithValue("$noOverlap", schedule.NoOverlap ? 1 : 0);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask RemoveScheduleAsync(string scheduleId, CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Cmd(
            "DELETE FROM backwave_schedules WHERE schedule_id = $id", connection);
        command.Parameters.AddWithValue("$id", scheduleId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<ScheduleSnapshot>> ListSchedulesAsync(CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        // Payload is deliberately omitted from this hot-path listing (§5.7): the mint planner never
        // reads it, and MintDue re-reads it from the row, so the per-poll load carries no blobs.
        await using var command = Cmd(
            $"""
            SELECT s.schedule_id, s.cron, s.wire_name, s.queue, s.cursor,
                   s.time_zone_id, s.catch_up, s.no_overlap, s.skipped_ticks,
                   EXISTS (SELECT 1 FROM backwave_jobs j
                           WHERE j.schedule_id = s.schedule_id
                             AND j.state IN ({(int)JobState.Scheduled}, {(int)JobState.AwaitingParent}, {(int)JobState.Leased})) AS has_live
            FROM backwave_schedules s
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
                    Cursor = SqliteValueCodec.FromTicks(reader.GetInt64(4)),
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
        var hintedQueues = new List<string>();

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        foreach (var decision in decisions)
        {
            await using var transaction = (SqliteTransaction)connection.BeginTransaction(deferred: false);

            // Cursor fencing: advancing the cursor claims the decision's ticks whole.
            (string WireName, byte[] Payload, string Queue, string SkippedTicks)? schedule = null;
            await using (var fence = Cmd(
                """
                UPDATE backwave_schedules
                SET cursor = $newCursor
                WHERE schedule_id = $id AND cursor = $expected
                RETURNING wire_name, payload, queue, skipped_ticks
                """,
                connection, transaction))
            {
                fence.Parameters.AddWithValue("$id", decision.ScheduleId);
                fence.Parameters.AddWithValue("$expected", SqliteValueCodec.ToTicks(decision.ExpectedCursor));
                fence.Parameters.AddWithValue("$newCursor", SqliteValueCodec.ToTicks(decision.NewCursor));
                await using var reader = await fence.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    schedule = (reader.GetString(0), (byte[])reader[1], reader.GetString(2), reader.GetString(3));
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
                    "UPDATE backwave_schedules SET skipped_ticks = $ticks WHERE schedule_id = $id",
                    connection, transaction);
                record.Parameters.AddWithValue("$id", decision.ScheduleId);
                record.Parameters.AddWithValue("$ticks", RenderSkippedTicks(combined));
                await record.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            // Crash after the cursor advanced, before the instances are minted: rollback must restore
            // the cursor so the ticks are minted, never silently lost (issue 0034).
            await FailpointAsync("mint-due", cancellationToken).ConfigureAwait(false);

            var mintedForDecision = 0;
            foreach (var tick in decision.Ticks)
            {
                var tickId = JobIds.ForMintedTick(decision.ScheduleId, tick);
                await using var insert = Cmd(
                    $"""
                    INSERT INTO backwave_jobs (job_id, wire_name, payload, queue, state, due_time, schedule_id)
                    VALUES ($id, $wire, $payload, $queue, {(int)JobState.Scheduled}, $due, $scheduleId)
                    ON CONFLICT (job_id) DO NOTHING
                    """,
                    connection, transaction);
                insert.Parameters.AddWithValue("$id", SqliteValueCodec.ToText(tickId));
                insert.Parameters.AddWithValue("$wire", schedule.Value.WireName);
                insert.Parameters.AddWithValue("$payload", schedule.Value.Payload);
                insert.Parameters.AddWithValue("$queue", schedule.Value.Queue);
                insert.Parameters.AddWithValue("$due", SqliteValueCodec.ToTicks(tick));
                insert.Parameters.AddWithValue("$scheduleId", decision.ScheduleId);
                if (await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0)
                {
                    mintedForDecision++;
                    // MintDue carries no `now`; the tick (the instance's due instant) is the
                    // deterministic timestamp for its first Scheduled transition (§5.12).
                    await RecordTransitionAsync(connection, transaction, tickId, JobState.Scheduled, attempt: 0, tick, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            if (mintedForDecision > 0)
            {
                hintedQueues.Add(schedule.Value.Queue);
            }
            minted += mintedForDecision;
        }

        // §8: minted ticks are due by construction — hint after the adapter's own commit (issue 0097).
        foreach (var queue in hintedQueues)
        {
            _hintHub?.Publish(queue);
        }
        return minted;
    }

    // ── §5.10 Queue configuration ───────────────────────────────────────────────

    /// <inheritdoc/>
    public async ValueTask SetConcurrencyLimitAsync(
        string queue, int? limit, string actor, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        // A transaction so the upsert and its audit record commit atomically, as SetPausedAsync does.
        await using var transaction = (SqliteTransaction)connection.BeginTransaction(deferred: false);
        await using (var command = Cmd(
            """
            INSERT INTO backwave_queue_limits (queue, max_concurrent) VALUES ($queue, $limit)
            ON CONFLICT (queue) DO UPDATE SET max_concurrent = $limit
            """,
            connection, transaction))
        {
            command.Parameters.AddWithValue("$queue", queue);
            command.Parameters.AddWithValue("$limit", (object?)limit ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await AppendAuditAsync(connection, transaction, actor, OperatorAction.SetConcurrencyLimit, queue, now, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    // ── §5.9 Monitor reads ──────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async ValueTask<JobRecord?> GetJobAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Cmd(
            $"SELECT {JobColumns} FROM backwave_jobs WHERE job_id = $id", connection);
        command.Parameters.AddWithValue("$id", SqliteValueCodec.ToText(jobId));
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

        // Reads ONLY the output column (ADR 0026), so a large blob never rides the listing/claim path.
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Cmd(
            "SELECT output FROM backwave_jobs WHERE job_id = $id", connection);
        command.Parameters.AddWithValue("$id", SqliteValueCodec.ToText(jobId));
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        // byte[] has an implicit conversion to ReadOnlyMemory<byte>, so an unqualified `: null` would
        // be the empty `default` memory (HasValue) rather than no value — cast explicitly.
        return result is byte[] bytes ? new ReadOnlyMemory<byte>(bytes) : (ReadOnlyMemory<byte>?)null;
    }

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<JobTransition>> GetJobHistoryAsync(
        Guid jobId, CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Cmd(
            """
            SELECT ordinal, recorded_at, state, attempt, failure_detail
            FROM backwave_job_transitions WHERE job_id = $id ORDER BY ordinal
            """,
            connection);
        command.Parameters.AddWithValue("$id", SqliteValueCodec.ToText(jobId));

        var transitions = new List<JobTransition>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            transitions.Add(new JobTransition(
                reader.GetInt64(0),
                SqliteValueCodec.FromTicks(reader.GetInt64(1)),
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

        await using var command = connection.CreateCommand();
        var conditions = new List<string>();
        AppendScopeConditions(query, conditions, command);
        var newestFirst = query.SortDirection == JobSortDirection.NewestFirst;
        if (query.AfterSequence is { } after)
        {
            // The cursor is direction-relative: newest-first continues toward OLDER jobs.
            conditions.Add(newestFirst ? "sequence < $after" : "sequence > $after");
            command.Parameters.AddWithValue("$after", after);
        }
        var where = conditions.Count > 0 ? $"WHERE {string.Join(" AND ", conditions)}" : string.Empty;
        var order = newestFirst ? "ORDER BY sequence DESC" : "ORDER BY sequence";
        command.CommandText = _schema.Rewrite($"SELECT {JobColumns} FROM backwave_jobs {where} {order} LIMIT $take");
        command.Parameters.AddWithValue("$take", Math.Min(query.MaxResults, _options.Bounds.MaxMonitorPageSize));

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

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Cmd(
            "SELECT queue, state, count(*) FROM backwave_jobs GROUP BY queue, state ORDER BY queue, state",
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

    // Builds the §5.9 scope conditions shared by ListJobsAsync and FacetAsync — the scalar filters
    // plus the AND-ed tag predicates (ADR 0022), each an EXISTS over backwave_job_tags correlated to
    // the job row. Pagination is NOT a scope condition — the caller adds it. The empty-string key
    // sentinel carries Labels.
    private static void AppendScopeConditions(JobQuery query, List<string> conditions, SqliteCommand command)
    {
        if (query.State is { } state)
        {
            conditions.Add("state = $state");
            command.Parameters.AddWithValue("$state", (int)state);
        }
        if (query.Queue is { } queue)
        {
            conditions.Add("queue = $queue");
            command.Parameters.AddWithValue("$queue", queue);
        }
        if (query.WireName is { } wire)
        {
            conditions.Add("wire_name = $wire");
            command.Parameters.AddWithValue("$wire", wire);
        }
        if (query.ScheduleId is { } scheduleId)
        {
            conditions.Add("schedule_id = $scheduleId");
            command.Parameters.AddWithValue("$scheduleId", scheduleId);
        }
        for (var i = 0; i < query.TagPredicates.Count; i++)
        {
            var predicate = query.TagPredicates[i];
            var keyParam = $"$tagKey{i}";
            if (predicate.Value is { } value)
            {
                var valueParam = $"$tagValue{i}";
                conditions.Add(
                    $"EXISTS (SELECT 1 FROM backwave_job_tags t WHERE t.job_id = backwave_jobs.job_id "
                    + $"AND t.key = {keyParam} AND t.value = {valueParam})");
                command.Parameters.AddWithValue(keyParam, predicate.Key);
                command.Parameters.AddWithValue(valueParam, value);
            }
            else
            {
                conditions.Add(
                    $"EXISTS (SELECT 1 FROM backwave_job_tags t WHERE t.job_id = backwave_jobs.job_id AND t.key = {keyParam})");
                command.Parameters.AddWithValue(keyParam, predicate.Key);
            }
        }
    }

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<TagFacet>> FacetAsync(
        string key, JobQuery? baseQuery = null, int maxResults = int.MaxValue, CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$max", Math.Max(0, maxResults));

        // COUNT(DISTINCT job_id) is distinct-JOB counting (ADR 0022). A baseQuery scopes the
        // population FIRST with the same predicates ListJobs uses. ORDER BY count DESC, value ASC
        // (the In-Memory tiebreak) for a deterministic, adapter-identical order, then LIMIT to the top
        // buckets (ADR 0042) — the cap applies after the group-count.
        var scope = string.Empty;
        if (baseQuery is not null)
        {
            var conditions = new List<string>();
            AppendScopeConditions(baseQuery, conditions, command);
            var where = conditions.Count > 0 ? $"WHERE {string.Join(" AND ", conditions)}" : string.Empty;
            scope = $"AND job_id IN (SELECT job_id FROM backwave_jobs {where})";
        }
        command.CommandText = _schema.Rewrite(
            $"SELECT value, count(DISTINCT job_id) FROM backwave_job_tags WHERE key = $key {scope} "
            + "GROUP BY value ORDER BY count(DISTINCT job_id) DESC, value LIMIT $max");

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

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Parameters.AddWithValue("$limit", limit);
        // The prefix is escaped for LIKE (\, %, _) and folded by lower(). SQLite's built-in lower()
        // folds ASCII only and its default BINARY collation is already byte-ordinal, so both the fold
        // match and the ordering are byte-ordinal — the ASCII-CI + lexicographic promises hold
        // identically to the reference store with no COLLATE clause. The v2 lower() expression index
        // serves the range scan.
        command.Parameters.AddWithValue("$prefix", EscapeLike(query.Prefix));

        var suggestions = new List<TagSuggestion>();
        if (query.Key is not null)
        {
            // Stage two: distinct values under one key (key="" ⇒ Labels), keyset-paged by value. The
            // cursor uses the expanded (lower, canonical) comparison rather than a row-value tuple so
            // it reads identically on every supported engine.
            command.Parameters.AddWithValue("$key", query.Key);
            var cursor = string.Empty;
            if (query.After is { } after)
            {
                command.Parameters.AddWithValue("$av", after.Value);
                cursor = "AND (lower(value) > lower($av) "
                    + "OR (lower(value) = lower($av) AND value > $av)) ";
            }
            // GROUP BY (not SELECT DISTINCT) to mirror the reference store and keep the lower(value)
            // ORDER BY expression unconstrained by a DISTINCT select list.
            command.CommandText = _schema.Rewrite(
                "SELECT value FROM backwave_job_tags "
                + "WHERE key = $key AND lower(value) LIKE lower($prefix) || '%' ESCAPE '\\' "
                + cursor
                + "GROUP BY value ORDER BY lower(value), value LIMIT $limit");

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                suggestions.Add(new TagSuggestion(query.Key, reader.GetString(0)));
            }
            return suggestions;
        }

        // Stage one: Labels (section 0) then keys (section 1), one keyset order across both blocks.
        // The fold-prefix predicate is pushed INTO each DISTINCT subquery, on lower(key)/lower(value) —
        // the leading columns of the v2 (lower(key), lower(value)) index — so each branch is a bounded
        // range seek over the typed prefix, not a full scan-and-aggregate of every keyed/labelled tag
        // row that the outer filter would then prune. The outer clause carries only the keyset cursor.
        var stageOneCursor = string.Empty;
        if (query.After is { } cursorItem)
        {
            var section = cursorItem.IsLabel ? 0 : 1;
            var name = cursorItem.IsLabel ? cursorItem.Value : cursorItem.Key;
            command.Parameters.AddWithValue("$sec", section);
            command.Parameters.AddWithValue("$an", name);
            stageOneCursor = "WHERE (section > $sec "
                + "OR (section = $sec AND lower(name) > lower($an)) "
                + "OR (section = $sec AND lower(name) = lower($an) AND name > $an)) ";
        }
        command.CommandText = _schema.Rewrite(
            "WITH tokens AS ("
            + "SELECT 0 AS section, value AS name FROM (SELECT DISTINCT value FROM backwave_job_tags "
            + "WHERE lower(key) = '' AND lower(value) LIKE lower($prefix) || '%' ESCAPE '\\') l "
            + "UNION ALL "
            + "SELECT 1 AS section, key AS name FROM (SELECT DISTINCT key FROM backwave_job_tags "
            + "WHERE lower(key) <> '' AND lower(key) LIKE lower($prefix) || '%' ESCAPE '\\') k) "
            + "SELECT section, name FROM tokens "
            + stageOneCursor
            + "ORDER BY section, lower(name), name LIMIT $limit");

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

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Cmd(
            "SELECT queue, paused, max_concurrent FROM backwave_queue_limits ORDER BY queue", connection);

        var settings = new List<QueueSettings>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            settings.Add(new QueueSettings(
                reader.GetString(0), reader.GetBoolean(1), reader.IsDBNull(2) ? null : reader.GetInt32(2)));
        }
        return settings;
    }

    /// <inheritdoc/>
    public async ValueTask<DependencyEdges> GetDependencyEdgesAsync(
        Guid jobId, CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        // Edges are deleted as each parent terminates (§5.6 latch cascade), so a child's surviving
        // parent_id rows are exactly its still-gating parents — never the full original set (ADR 0009).
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var gatingParents = new List<Guid>();
        await using (var parents = Cmd(
            "SELECT parent_id FROM backwave_job_parents WHERE child_id = $id ORDER BY parent_id", connection))
        {
            parents.Parameters.AddWithValue("$id", SqliteValueCodec.ToText(jobId));
            await using var reader = await parents.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                gatingParents.Add(SqliteValueCodec.ToGuid(reader.GetString(0)));
            }
        }

        var children = new List<Guid>();
        await using (var childRows = Cmd(
            "SELECT child_id FROM backwave_job_parents WHERE parent_id = $id ORDER BY child_id", connection))
        {
            childRows.Parameters.AddWithValue("$id", SqliteValueCodec.ToText(jobId));
            await using var reader = await childRows.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                children.Add(SqliteValueCodec.ToGuid(reader.GetString(0)));
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
        // job keeps the per-job rule (terminal_at <= before); a Workflow member is eligible only once the
        // WHOLE Workflow has drained AND the drain instant — max member terminal_at — is <= before. The
        // drained CTE folds both: a non-NULL drain_at means drained. Postgres's bool_and becomes
        // MIN(state IN terminal) here.
        var (states, stateParams) = StatePlaceholders(stateClass == TerminalStateClass.SucceededOrCancelled
            ? [JobState.Succeeded, JobState.Cancelled]
            : [JobState.DeadLettered, JobState.Quarantined]);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (var command = Cmd(
            $"""
            WITH drained AS (
                SELECT workflow_id,
                       CASE WHEN MIN(CASE WHEN state IN ({(int)JobState.Succeeded}, {(int)JobState.Cancelled}, {(int)JobState.DeadLettered}, {(int)JobState.Quarantined}) THEN 1 ELSE 0 END) = 1
                            THEN MAX(terminal_at) END AS drain_at
                FROM backwave_jobs
                WHERE workflow_id IS NOT NULL
                GROUP BY workflow_id
            )
            DELETE FROM backwave_jobs
            WHERE job_id IN (
                SELECT j.job_id FROM backwave_jobs j
                LEFT JOIN drained d ON d.workflow_id = j.workflow_id
                WHERE j.state IN ({states})
                  AND CASE
                        WHEN j.workflow_id IS NULL THEN j.terminal_at <= $before
                        ELSE d.drain_at IS NOT NULL AND d.drain_at <= $before
                      END
                ORDER BY j.terminal_at, j.sequence
                LIMIT $max
            )
            """,
            connection))
        {
            command.Parameters.AddWithValue("$before", SqliteValueCodec.ToTicks(terminalBefore));
            command.Parameters.AddWithValue("$max", Math.Min(maxJobs, _options.Bounds.MaxPurgeBatch));
            foreach (var (name, value) in stateParams)
            {
                command.Parameters.AddWithValue(name, value);
            }
            var purged = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            // When a Workflow's last member is purged, drop its now-orphaned identity row (structural
            // edges cascade via FK) so the tables never leak rows for Workflows with no surviving jobs.
            await using (var prune = Cmd(
                "DELETE FROM backwave_workflows WHERE workflow_id NOT IN " +
                "(SELECT workflow_id FROM backwave_jobs WHERE workflow_id IS NOT NULL)",
                connection))
            {
                await prune.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            return purged;
        }
    }

    // ── Workflows (ADR 0023) ─────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async ValueTask<WorkflowEnqueueResult> EnqueueWorkflowAsync(
        WorkflowDefinition workflow, DateTimeOffset now, DbTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        if (transaction is not null)
        {
            if (transaction is not SqliteTransaction { Connection: { } callerConnection } sqliteTransaction)
            {
                throw new ArgumentException(
                    "The SQLite adapter enlists in SqliteTransaction instances only.", nameof(transaction));
            }
            _sameFileGuard.EnsureSameFile(callerConnection);
            var (enlisted, _) = await EnqueueWorkflowCoreAsync(callerConnection, sqliteTransaction, workflow, now, cancellationToken)
                .ConfigureAwait(false);
            return enlisted;
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var ownTransaction = (SqliteTransaction)connection.BeginTransaction(deferred: false);
        var (result, hintQueues) = await EnqueueWorkflowCoreAsync(connection, ownTransaction, workflow, now, cancellationToken)
            .ConfigureAwait(false);
        // All-or-nothing: a non-Ok validation leaves nothing inserted, so only Ok commits.
        if (result == WorkflowEnqueueResult.Ok)
        {
            await ownTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            foreach (var queue in hintQueues)
            {
                _hintHub?.Publish(queue);
            }
        }
        else
        {
            await ownTransaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        }
        return result;
    }

    private async ValueTask<(WorkflowEnqueueResult Result, List<string> HintQueues)> EnqueueWorkflowCoreAsync(
        SqliteConnection connection, SqliteTransaction transaction, WorkflowDefinition workflow,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var hintQueues = new List<string>();
        if (workflow.Members.Count == 0)
        {
            return (WorkflowEnqueueResult.EmptyWorkflow, hintQueues);
        }

        var workflowExists = await WorkflowExistsAsync(connection, transaction, workflow.WorkflowId, cancellationToken)
            .ConfigureAwait(false);
        if (workflow.IsAppend)
        {
            if (!workflowExists)
            {
                return (WorkflowEnqueueResult.WorkflowNotFound, hintQueues); // nothing to append to
            }
        }
        else if (workflowExists)
        {
            return (WorkflowEnqueueResult.DuplicateWorkflow, hintQueues);
        }

        var newMemberIds = new HashSet<Guid>();
        foreach (var member in workflow.Members)
        {
            if (!newMemberIds.Add(member.JobId))
            {
                return (WorkflowEnqueueResult.DuplicateMember, hintQueues); // the same JobId twice in one batch
            }
        }
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
                return (WorkflowEnqueueResult.DuplicateMember, hintQueues);
            }
            if (member.Payload.Length > _options.Bounds.MaxPayloadBytes)
            {
                return (WorkflowEnqueueResult.PayloadTooLarge, hintQueues);
            }
            if (member.WireName.Length > _options.Bounds.MaxWireNameLength)
            {
                return (WorkflowEnqueueResult.WireNameTooLong, hintQueues);
            }
            var parents = member.Parents.Distinct().ToArray();
            if (parents.Length > _options.Bounds.MaxParentsPerJob)
            {
                return (WorkflowEnqueueResult.TooManyParents, hintQueues);
            }
            if (parents.Any(p => !allowedParents.Contains(p)))
            {
                return (WorkflowEnqueueResult.ContainmentViolation, hintQueues);
            }
        }

        if (!workflow.IsAppend)
        {
            await using var insertRow = Cmd(
                """
                INSERT INTO backwave_workflows (workflow_id, name, created_at, retention, restarted_from)
                VALUES ($id, $name, $createdAt, $retention, $restartedFrom)
                """,
                connection, transaction);
            insertRow.Parameters.AddWithValue("$id", SqliteValueCodec.ToText(workflow.WorkflowId));
            insertRow.Parameters.AddWithValue("$name", (object?)workflow.Name ?? DBNull.Value);
            insertRow.Parameters.AddWithValue("$createdAt", SqliteValueCodec.ToTicks(now));
            insertRow.Parameters.AddWithValue("$retention", (int)workflow.Retention);
            insertRow.Parameters.AddWithValue("$restartedFrom",
                workflow.RestartedFrom is { } from ? SqliteValueCodec.ToText(from) : DBNull.Value);
            await insertRow.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // Members in dependency order (parents before children), each stamped with the WorkflowId.
        foreach (var member in TopologicallyOrdered(workflow.Members))
        {
            var (applied, hint) = await EnqueueCoreAsync(connection, transaction, member, now, cancellationToken, workflow.WorkflowId)
                .ConfigureAwait(false);
            if (applied != EnqueueResult.Ok) // always-on assertion: validated above
            {
                throw new InvalidOperationException(
                    $"Workflow enqueue commit failed for member {member.JobId}: {applied}.");
            }
            if (hint is not null)
            {
                hintQueues.Add(hint);
            }
        }

        // Structural edges (ADR 0023): immutable, recorded once, so the graph view stays total even
        // after the live gating edges (job_parents) resolve away.
        foreach (var member in workflow.Members)
        {
            foreach (var parent in member.Parents.Distinct())
            {
                await using var edge = Cmd(
                    """
                    INSERT INTO backwave_workflow_edges (workflow_id, parent_id, child_id)
                    VALUES ($workflowId, $parent, $child)
                    ON CONFLICT (workflow_id, parent_id, child_id) DO NOTHING
                    """,
                    connection, transaction);
                edge.Parameters.AddWithValue("$workflowId", SqliteValueCodec.ToText(workflow.WorkflowId));
                edge.Parameters.AddWithValue("$parent", SqliteValueCodec.ToText(parent));
                edge.Parameters.AddWithValue("$child", SqliteValueCodec.ToText(member.JobId));
                await edge.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        return (WorkflowEnqueueResult.Ok, hintQueues);
    }

    private async ValueTask<bool> WorkflowExistsAsync(
        SqliteConnection connection, SqliteTransaction transaction, Guid workflowId, CancellationToken cancellationToken)
    {
        await using var command = Cmd(
            "SELECT 1 FROM backwave_workflows WHERE workflow_id = $id", connection, transaction);
        command.Parameters.AddWithValue("$id", SqliteValueCodec.ToText(workflowId));
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    private async ValueTask<bool> JobExistsAsync(
        SqliteConnection connection, SqliteTransaction transaction, Guid jobId, CancellationToken cancellationToken)
    {
        await using var command = Cmd(
            "SELECT 1 FROM backwave_jobs WHERE job_id = $id", connection, transaction);
        command.Parameters.AddWithValue("$id", SqliteValueCodec.ToText(jobId));
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    private async ValueTask<HashSet<Guid>> MembersOfAsync(
        SqliteConnection connection, SqliteTransaction transaction, Guid workflowId, CancellationToken cancellationToken)
    {
        var members = new HashSet<Guid>();
        await using var command = Cmd(
            "SELECT job_id FROM backwave_jobs WHERE workflow_id = $id", connection, transaction);
        command.Parameters.AddWithValue("$id", SqliteValueCodec.ToText(workflowId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            members.Add(SqliteValueCodec.ToGuid(reader.GetString(0)));
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

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var statesByWorkflow = new Dictionary<Guid, List<JobState>>();
        await using (var members = Cmd(
            "SELECT workflow_id, state FROM backwave_jobs WHERE workflow_id IS NOT NULL", connection))
        {
            await using var reader = await members.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var wf = SqliteValueCodec.ToGuid(reader.GetString(0));
                (statesByWorkflow.TryGetValue(wf, out var list) ? list : statesByWorkflow[wf] = [])
                    .Add((JobState)reader.GetInt32(1));
            }
        }

        var snapshots = new List<WorkflowSnapshot>();
        await using (var workflows = Cmd(
            "SELECT workflow_id, name, created_at, restarted_from FROM backwave_workflows " +
            "ORDER BY created_at, workflow_id", connection))
        {
            await using var reader = await workflows.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var workflowId = SqliteValueCodec.ToGuid(reader.GetString(0));
                var states = statesByWorkflow.GetValueOrDefault(workflowId) ?? [];
                snapshots.Add(new WorkflowSnapshot
                {
                    WorkflowId = workflowId,
                    Name = reader.IsDBNull(1) ? null : reader.GetString(1),
                    CreatedAt = SqliteValueCodec.FromTicks(reader.GetInt64(2)),
                    Status = WorkflowStatusProjection.Project(states),
                    MemberCount = states.Count,
                    RestartedFrom = reader.IsDBNull(3) ? null : SqliteValueCodec.ToGuid(reader.GetString(3)),
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
            "SELECT name, created_at, restarted_from FROM backwave_workflows WHERE workflow_id = $id", connection))
        {
            row.Parameters.AddWithValue("$id", SqliteValueCodec.ToText(workflowId));
            await using var reader = await row.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }
            name = reader.IsDBNull(0) ? null : reader.GetString(0);
            createdAt = SqliteValueCodec.FromTicks(reader.GetInt64(1));
            restartedFrom = reader.IsDBNull(2) ? null : SqliteValueCodec.ToGuid(reader.GetString(2));
        }

        var members = new List<JobRecord>();
        await using (var memberRows = Cmd(
            $"SELECT {JobColumns} FROM backwave_jobs WHERE workflow_id = $id ORDER BY sequence", connection))
        {
            memberRows.Parameters.AddWithValue("$id", SqliteValueCodec.ToText(workflowId));
            await using var reader = await memberRows.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                members.Add(ReadJob(reader));
            }
        }
        var hydrated = await WithTagsAsync(connection, members, cancellationToken).ConfigureAwait(false);

        var edges = new List<WorkflowEdge>();
        await using (var edgeRows = Cmd(
            "SELECT parent_id, child_id FROM backwave_workflow_edges WHERE workflow_id = $id " +
            "ORDER BY parent_id, child_id", connection))
        {
            edgeRows.Parameters.AddWithValue("$id", SqliteValueCodec.ToText(workflowId));
            await using var reader = await edgeRows.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                edges.Add(new WorkflowEdge(
                    SqliteValueCodec.ToGuid(reader.GetString(0)), SqliteValueCodec.ToGuid(reader.GetString(1))));
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

    // ── §5.13 Observer-delivery capability (ADR 0017) ────────────────────────────
    //
    // The leaderless, at-least-once walk of the Transition Log, mirroring the In-Memory reference.
    // Whole-writer serialization plays the role Postgres's FOR UPDATE row lock does: exactly one node
    // advances a given Observer's cursor at a time. The global Position lives on job_transitions; the
    // per-(Observer, Position) attempt/resolution bookkeeping lives in backwave_observer_deliveries.

    /// <inheritdoc/>
    public async ValueTask<ObserverClaim> ClaimObserverDeliveriesAsync(
        ObserverClaimRequest request, CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)connection.BeginTransaction(deferred: false);

        await using (var ensure = Cmd(
            "INSERT INTO backwave_observers (observer_id) VALUES ($id) ON CONFLICT (observer_id) DO NOTHING",
            connection, transaction))
        {
            ensure.Parameters.AddWithValue("$id", request.ObserverId);
            await ensure.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        long cursor;
        string? leaseOwner;
        DateTimeOffset? leaseExpiry;
        await using (var locked = Cmd(
            "SELECT cursor_pos, lease_owner, lease_expiry FROM backwave_observers WHERE observer_id = $id",
            connection, transaction))
        {
            locked.Parameters.AddWithValue("$id", request.ObserverId);
            await using var reader = await locked.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            cursor = reader.GetInt64(0);
            leaseOwner = reader.IsDBNull(1) ? null : reader.GetString(1);
            leaseExpiry = reader.IsDBNull(2) ? null : SqliteValueCodec.FromTicks(reader.GetInt64(2));
        }

        // Remember the subscription so cursor advance (on report) can tell matching rows from the ones
        // this Observer ignores. Run config — set every claim, never changes within a run.
        var states = request.States.Select(s => (int)s).ToArray();
        await using (var sub = Cmd(
            "UPDATE backwave_observers SET sub_states = $states, sub_wire_name = $wire, sub_queue = $queue WHERE observer_id = $id",
            connection, transaction))
        {
            sub.Parameters.AddWithValue("$id", request.ObserverId);
            sub.Parameters.AddWithValue("$states", string.Join(',', states));
            sub.Parameters.AddWithValue("$wire", (object?)request.WireName ?? DBNull.Value);
            sub.Parameters.AddWithValue("$queue", (object?)request.Queue ?? DBNull.Value);
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

        var (statePlaceholders, stateParams) = StatePlaceholders(request.States);
        var candidates = new List<ObserverClaimedDelivery>();
        await using (var scan = Cmd(
            $"""
            SELECT t.position, t.job_id, t.ordinal, j.wire_name, j.queue, t.state, t.attempt,
                   t.recorded_at, t.failure_detail, d.delivery_attempt, d.next_attempt_at
            FROM backwave_job_transitions t
            JOIN backwave_jobs j ON j.job_id = t.job_id
            LEFT JOIN backwave_observer_deliveries d ON d.observer_id = $id AND d.position = t.position
            WHERE t.position > $cursor
              AND t.state IN ({statePlaceholders})
              AND ($wire IS NULL OR j.wire_name = $wire)
              AND ($queue IS NULL OR j.queue = $queue)
              AND (d.resolution IS NULL OR d.resolution = 0)
            ORDER BY t.position
            LIMIT $take
            """,
            connection, transaction))
        {
            scan.Parameters.AddWithValue("$id", request.ObserverId);
            scan.Parameters.AddWithValue("$cursor", cursor);
            scan.Parameters.AddWithValue("$wire", (object?)request.WireName ?? DBNull.Value);
            scan.Parameters.AddWithValue("$queue", (object?)request.Queue ?? DBNull.Value);
            scan.Parameters.AddWithValue("$take", Math.Max(0, request.MaxRows));
            foreach (var (name, value) in stateParams)
            {
                scan.Parameters.AddWithValue(name, value);
            }
            await using var reader = await scan.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var nextAttemptAt = reader.IsDBNull(10) ? (DateTimeOffset?)null : SqliteValueCodec.FromTicks(reader.GetInt64(10));
                // Head-of-line (§0077): a row still in its backoff window holds the cursor — claim
                // nothing past it, so in-order-per-Observer falls out of the single moving cursor.
                if (nextAttemptAt is { } next && next > request.Now)
                {
                    break;
                }
                var priorAttempt = reader.IsDBNull(9) ? 0 : reader.GetInt32(9);
                candidates.Add(new ObserverClaimedDelivery(
                    reader.GetInt64(0), SqliteValueCodec.ToGuid(reader.GetString(1)), reader.GetInt64(2),
                    reader.GetString(3), reader.GetString(4), (JobState)reader.GetInt32(5), reader.GetInt32(6),
                    SqliteValueCodec.FromTicks(reader.GetInt64(7)),
                    reader.IsDBNull(8) ? null : reader.GetString(8), priorAttempt + 1));
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
                INSERT INTO backwave_observer_deliveries (observer_id, position, delivery_attempt, resolution, next_attempt_at)
                VALUES ($id, $pos, $attempt, 0, NULL)
                ON CONFLICT (observer_id, position)
                DO UPDATE SET delivery_attempt = $attempt, resolution = 0, next_attempt_at = NULL
                """,
                connection, transaction);
            upsert.Parameters.AddWithValue("$id", request.ObserverId);
            upsert.Parameters.AddWithValue("$pos", delivery.Position);
            upsert.Parameters.AddWithValue("$attempt", delivery.DeliveryAttempt);
            await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var lease = Cmd(
            "UPDATE backwave_observers SET lease_owner = $worker, lease_expiry = $expiry WHERE observer_id = $id",
            connection, transaction))
        {
            lease.Parameters.AddWithValue("$id", request.ObserverId);
            lease.Parameters.AddWithValue("$worker", request.WorkerId);
            lease.Parameters.AddWithValue("$expiry", SqliteValueCodec.ToTicks(request.Now + request.LeaseDuration));
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
        await using var transaction = (SqliteTransaction)connection.BeginTransaction(deferred: false);

        long cursor;
        string? leaseOwner;
        DateTimeOffset? leaseExpiry;
        int[] states;
        string? wireName;
        string? queue;
        await using (var locked = Cmd(
            "SELECT cursor_pos, lease_owner, lease_expiry, sub_states, sub_wire_name, sub_queue " +
            "FROM backwave_observers WHERE observer_id = $id",
            connection, transaction))
        {
            locked.Parameters.AddWithValue("$id", report.ObserverId);
            await using var reader = await locked.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return; // unknown Observer — nothing claimed, nothing to resolve
            }
            cursor = reader.GetInt64(0);
            leaseOwner = reader.IsDBNull(1) ? null : reader.GetString(1);
            leaseExpiry = reader.IsDBNull(2) ? null : SqliteValueCodec.FromTicks(reader.GetInt64(2));
            states = ParseStates(reader.GetString(3));
            wireName = reader.IsDBNull(4) ? null : reader.GetString(4);
            queue = reader.IsDBNull(5) ? null : reader.GetString(5);
        }

        // Fence (§5.13): only the live claim-Lease holder may resolve deliveries and advance the cursor.
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
                "UPDATE backwave_observer_deliveries SET resolution = $resolution, next_attempt_at = $next " +
                "WHERE observer_id = $id AND position = $pos",
                connection, transaction);
            resolve.Parameters.AddWithValue("$id", report.ObserverId);
            resolve.Parameters.AddWithValue("$pos", outcome.Position);
            resolve.Parameters.AddWithValue("$resolution", resolution);
            resolve.Parameters.AddWithValue(
                "$next", outcome.Disposition == ObserverDeliveryDisposition.Retry && outcome.NextAttemptAt is { } at
                    ? SqliteValueCodec.ToTicks(at) : (object)DBNull.Value);
            await resolve.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await AdvanceObserverCursorAsync(
            connection, transaction, report.ObserverId, cursor, states, wireName, queue, report.Now, cancellationToken)
            .ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sweeps the cursor forward over the contiguous prefix of resolved matching rows — and over every
    /// non-matching row, which needs no delivery — stopping at the first matching row still Pending (the
    /// head-of-line block). A dead-lettered row is recorded loudly as the cursor passes it.
    /// </summary>
    private async Task AdvanceObserverCursorAsync(
        SqliteConnection connection, SqliteTransaction transaction, string observerId, long cursor,
        int[] states, string? wireName, string? queue, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var (statePlaceholders, stateParams) = StatePlaceholders(states.Select(s => (JobState)s).ToArray());

        long? block;
        await using (var blockCommand = Cmd(
            $"""
            SELECT MIN(t.position)
            FROM backwave_job_transitions t
            JOIN backwave_jobs j ON j.job_id = t.job_id
            LEFT JOIN backwave_observer_deliveries d ON d.observer_id = $id AND d.position = t.position
            WHERE t.position > $cursor
              AND t.state IN ({statePlaceholders})
              AND ($wire IS NULL OR j.wire_name = $wire)
              AND ($queue IS NULL OR j.queue = $queue)
              AND (d.resolution IS NULL OR d.resolution = 0)
            """,
            connection, transaction))
        {
            blockCommand.Parameters.AddWithValue("$id", observerId);
            blockCommand.Parameters.AddWithValue("$cursor", cursor);
            blockCommand.Parameters.AddWithValue("$wire", (object?)wireName ?? DBNull.Value);
            blockCommand.Parameters.AddWithValue("$queue", (object?)queue ?? DBNull.Value);
            foreach (var (name, value) in stateParams)
            {
                blockCommand.Parameters.AddWithValue(name, value);
            }
            var result = await blockCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            block = result is DBNull or null ? null : (long)result;
        }

        long? newCursor;
        await using (var advance = Cmd(
            "SELECT MAX(position) FROM backwave_job_transitions WHERE position > $cursor AND ($block IS NULL OR position < $block)",
            connection, transaction))
        {
            advance.Parameters.AddWithValue("$cursor", cursor);
            advance.Parameters.AddWithValue("$block", (object?)block ?? DBNull.Value);
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
            INSERT INTO backwave_observer_dead_letters
                (observer_id, position, job_id, ordinal, state, attempt, delivery_attempts, dead_lettered_at)
            SELECT $id, t.position, t.job_id, t.ordinal, t.state, t.attempt, d.delivery_attempt, $now
            FROM backwave_job_transitions t
            JOIN backwave_observer_deliveries d ON d.observer_id = $id AND d.position = t.position
            WHERE t.position > $cursor AND t.position <= $target AND d.resolution = 2
            ON CONFLICT (observer_id, position) DO NOTHING
            """,
            connection, transaction))
        {
            deadLetter.Parameters.AddWithValue("$id", observerId);
            deadLetter.Parameters.AddWithValue("$cursor", cursor);
            deadLetter.Parameters.AddWithValue("$target", target);
            deadLetter.Parameters.AddWithValue("$now", SqliteValueCodec.ToTicks(now));
            await deadLetter.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // The swept rows are all resolved now — drop their in-flight bookkeeping.
        await using (var sweep = Cmd(
            "DELETE FROM backwave_observer_deliveries WHERE observer_id = $id AND position <= $target",
            connection, transaction))
        {
            sweep.Parameters.AddWithValue("$id", observerId);
            sweep.Parameters.AddWithValue("$target", target);
            await sweep.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var move = Cmd(
            "UPDATE backwave_observers SET cursor_pos = $target WHERE observer_id = $id",
            connection, transaction))
        {
            move.Parameters.AddWithValue("$id", observerId);
            move.Parameters.AddWithValue("$target", target);
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

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Cmd(
            "SELECT cursor_pos FROM backwave_observers WHERE observer_id = $id", connection);
        command.Parameters.AddWithValue("$id", observerId);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is DBNull or null ? -1L : (long)result;
    }

    /// <inheritdoc/>
    public async ValueTask<ObserverLag> GetObserverLagAsync(
        ObserverLagRequest request, CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        var (statePlaceholders, stateParams) = StatePlaceholders(request.States);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        // Subscription-aware backlog: matching transitions the durable cursor has not advanced past.
        // The cursor is -1 when the observer has never delivered, so every matching row counts.
        await using var command = Cmd(
            $"""
            WITH cur AS (
                SELECT COALESCE((SELECT cursor_pos FROM backwave_observers WHERE observer_id = $id), -1) AS pos
            )
            SELECT (SELECT pos FROM cur), COUNT(t.position), MIN(t.recorded_at)
            FROM backwave_job_transitions t
            JOIN backwave_jobs j ON j.job_id = t.job_id
            WHERE t.position > (SELECT pos FROM cur)
              AND t.state IN ({statePlaceholders})
              AND ($wire IS NULL OR j.wire_name = $wire)
              AND ($queue IS NULL OR j.queue = $queue)
            """,
            connection);
        command.Parameters.AddWithValue("$id", request.ObserverId);
        command.Parameters.AddWithValue("$wire", (object?)request.WireName ?? DBNull.Value);
        command.Parameters.AddWithValue("$queue", (object?)request.Queue ?? DBNull.Value);
        foreach (var (name, value) in stateParams)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        var oldest = reader.IsDBNull(2) ? (DateTimeOffset?)null : SqliteValueCodec.FromTicks(reader.GetInt64(2));
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
            FROM backwave_observer_dead_letters WHERE observer_id = $id ORDER BY position
            """,
            connection);
        command.Parameters.AddWithValue("$id", observerId);

        var records = new List<ObserverDeadLetterRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            records.Add(new ObserverDeadLetterRecord(
                reader.GetInt64(0), SqliteValueCodec.ToGuid(reader.GetString(1)), reader.GetInt64(2),
                (JobState)reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5),
                SqliteValueCodec.FromTicks(reader.GetInt64(6))));
        }
        return records;
    }

    // ── §8 Wake-Up Hints (in-process hub, issue 0097) ────────────────────────────

    /// <inheritdoc/>
    public Task<IAsyncDisposable> SubscribeAsync(
        Action<string> onHint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onHint);
        // Opted out (or already disposed): hand back a no-op handle. Polling carries everything.
        IAsyncDisposable subscription = _hintHub is null ? NoopSubscription.Instance : _hintHub.Subscribe(onHint);
        return Task.FromResult(subscription);
    }

    private sealed class NoopSubscription : IAsyncDisposable
    {
        public static readonly NoopSubscription Instance = new();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // ── row mapping ─────────────────────────────────────────────────────────────

    /// <summary>The hot-read column list (output is deliberately excluded — it never rides a hot read).</summary>
    private const string JobColumns =
        "sequence, job_id, wire_name, payload, trace_context, queue, state, due_time, attempt, " +
        "lease_owner, lease_expiry, cancel_requested, terminal_at, terminal_cause, schedule_id, " +
        "parents_remaining, mode, workflow_id";

    private static JobRecord ReadJob(SqliteDataReader reader) => new()
    {
        Sequence = reader.GetInt64(0),
        JobId = SqliteValueCodec.ToGuid(reader.GetString(1)),
        WireName = reader.GetString(2),
        Payload = (byte[])reader[3],
        TraceContext = reader.IsDBNull(4) ? null : reader.GetString(4),
        Queue = reader.GetString(5),
        State = SqliteValueCodec.ToEnum<JobState>(reader.GetInt64(6)),
        DueTime = SqliteValueCodec.FromTicks(reader.GetInt64(7)),
        Attempt = reader.GetInt32(8),
        LeaseOwner = reader.IsDBNull(9) ? null : reader.GetString(9),
        LeaseExpiry = reader.IsDBNull(10) ? null : SqliteValueCodec.FromTicks(reader.GetInt64(10)),
        CancelRequested = reader.GetInt64(11) != 0,
        TerminalAt = reader.IsDBNull(12) ? null : SqliteValueCodec.FromTicks(reader.GetInt64(12)),
        TerminalCause = reader.IsDBNull(13) ? null : reader.GetString(13),
        ScheduleId = reader.IsDBNull(14) ? null : reader.GetString(14),
        ParentsRemaining = reader.GetInt32(15),
        Mode = SqliteValueCodec.ToEnum<DependencyMode>(reader.GetInt64(16)),
        WorkflowId = reader.IsDBNull(17) ? null : SqliteValueCodec.ToGuid(reader.GetString(17)),
    };

    // ── Job Tags (ADR 0022) ─────────────────────────────────────────────────────

    private async Task InsertTagsAsync(
        SqliteConnection connection, SqliteTransaction transaction, Guid jobId, JobTags tags,
        CancellationToken cancellationToken)
    {
        foreach (var tag in tags)
        {
            await using var insert = Cmd(
                """
                INSERT INTO backwave_job_tags (job_id, key, value)
                VALUES ($id, $key, $value)
                ON CONFLICT (job_id, key, value) DO NOTHING
                """,
                connection, transaction);
            insert.Parameters.AddWithValue("$id", SqliteValueCodec.ToText(jobId));
            insert.Parameters.AddWithValue("$key", tag.Key);
            insert.Parameters.AddWithValue("$value", tag.Value);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<Dictionary<Guid, JobTags>> HydrateTagsAsync(
        SqliteConnection connection, IReadOnlyList<Guid> jobIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, JobTags>();
        if (jobIds.Count == 0)
        {
            return result;
        }
        var (placeholders, ids) = IdPlaceholders(jobIds, "$j");
        await using var command = Cmd(
            $"SELECT job_id, key, value FROM backwave_job_tags WHERE job_id IN ({placeholders})", connection);
        foreach (var (name, value) in ids)
        {
            command.Parameters.AddWithValue(name, value);
        }
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var jobId = SqliteValueCodec.ToGuid(reader.GetString(0));
            var key = reader.GetString(1);
            var value = reader.GetString(2);
            var tag = key.Length == 0 ? JobTag.Label(value) : JobTag.Keyed(key, value);
            result[jobId] = (result.TryGetValue(jobId, out var existing) ? existing : JobTags.Empty).With(tag);
        }
        return result;
    }

    private async Task<IReadOnlyList<JobRecord>> WithTagsAsync(
        SqliteConnection connection, IReadOnlyList<JobRecord> jobs, CancellationToken cancellationToken)
    {
        if (jobs.Count == 0)
        {
            return jobs;
        }
        var tags = await HydrateTagsAsync(connection, [.. jobs.Select(j => j.JobId)], cancellationToken)
            .ConfigureAwait(false);
        return [.. jobs.Select(j => j with { Tags = tags.TryGetValue(j.JobId, out var set) ? set : JobTags.Empty })];
    }

    // ── helpers ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds an <c>IN (…)</c> placeholder list for a Guid id set plus the matching ($name, text)
    /// parameter pairs — SQLite has no array binding, so a variadic <c>IN</c> stands in for
    /// Postgres's <c>= ANY(@ids)</c>.
    /// </summary>
    private static (string Placeholders, List<(string Name, object Value)> Parameters) IdPlaceholders(
        IReadOnlyList<Guid> ids, string prefix)
    {
        var names = new List<string>(ids.Count);
        var parameters = new List<(string, object)>(ids.Count);
        for (var i = 0; i < ids.Count; i++)
        {
            var name = $"{prefix}{i}";
            names.Add(name);
            parameters.Add((name, SqliteValueCodec.ToText(ids[i])));
        }
        return (string.Join(", ", names), parameters);
    }

    /// <summary>Variadic <c>IN (…)</c> placeholders for a string set.</summary>
    private static (string Placeholders, List<(string Name, object Value)> Parameters) ValuePlaceholders(
        IReadOnlyList<string> values, string prefix)
    {
        var names = new List<string>(values.Count);
        var parameters = new List<(string, object)>(values.Count);
        for (var i = 0; i < values.Count; i++)
        {
            var name = $"{prefix}{i}";
            names.Add(name);
            parameters.Add((name, values[i]));
        }
        return (string.Join(", ", names), parameters);
    }

    /// <summary>Variadic <c>IN (…)</c> placeholders for a state set (the int code of each).</summary>
    private static (string Placeholders, List<(string Name, object Value)> Parameters) StatePlaceholders(
        IReadOnlyList<JobState> states)
    {
        var names = new List<string>(states.Count);
        var parameters = new List<(string, object)>(states.Count);
        for (var i = 0; i < states.Count; i++)
        {
            var name = $"$st{i}";
            names.Add(name);
            parameters.Add((name, (int)states[i]));
        }
        // An empty IN () is a SQL syntax error; a sentinel that can never match keeps it valid.
        return (states.Count == 0 ? "-1" : string.Join(", ", names), parameters);
    }

    private static IReadOnlyList<DateTimeOffset> ParseSkippedTicks(string json)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        return [.. document.RootElement.EnumerateArray().Select(e => e.GetDateTimeOffset())];
    }

    /// <summary>ISO-8601 instants need no JSON escaping, so the array renders directly.</summary>
    private static string RenderSkippedTicks(IReadOnlyList<DateTimeOffset> ticks)
        => "[" + string.Join(",", ticks.Select(t => $"\"{t.ToUniversalTime():O}\"")) + "]";

    // ── connection & lifecycle ───────────────────────────────────────────────────

    private Task FailpointAsync(string name, CancellationToken cancellationToken)
        => _options.FaultHook?.Invoke(name, cancellationToken) ?? Task.CompletedTask;

    private async ValueTask<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

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
            await SqliteMigrator.EnsureEngineVersionAsync(_connectionString, cancellationToken).ConfigureAwait(false);
            if (_options.AutoMigrate)
            {
                await SqliteMigrator.MigrateAsync(_connectionString, _options.TablePrefix, _options.CoordinateMigration, cancellationToken).ConfigureAwait(false);
                BackWaveLog.MigrationApplied(
                    _options.LoggerFactory?.CreateLogger(SqliteDiagnostics.SourceName) ?? NullLogger.Instance, "sqlite");
            }
            await SqliteMigrator.VerifySchemaVersionAsync(_connectionString, _options.TablePrefix, cancellationToken).ConfigureAwait(false);
            _ready = true;
        }
        finally
        {
            _readyGate.Release();
        }
    }
}
