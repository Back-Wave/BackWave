using System.Collections;
using Microsoft.Extensions.Logging;

namespace BackWave.Diagnostics;

// The third telemetry pillar: a source-generated [LoggerMessage] catalog for the job lifecycle, a
// sibling to the traces/metrics surface in BackWaveDiagnostics. The [LoggerMessage] generator emits an
// IsEnabled guard ahead of every format, so a call on a disabled level (or a NullLogger) never formats
// its message and allocates nothing. The Shells hold the ILogger and call these; the Core takes only an
// optional ILoggerFactory (null → a NullLogger that no-ops), so a log is never a decision input and a
// simulated Core stays byte-identical.
//
// Internal, so no XML-doc gate applies (the messages ARE the documentation) and no logging vocabulary
// leaks into the shipped public surface. The [LoggerMessage] generator matches a template placeholder to
// the method parameter of the SAME name, and the emitted structured key IS that name - so the parameters
// are deliberately snake_case (job_id, wire_name, ...) to match the messaging/db semantic-convention keys
// the trace/metric surface uses and the scope below stamps.
internal static partial class BackWaveLog
{
    // ── Job-context scope ────────────────────────────────────────────────────────────────────────
    // Wraps the claim/execute/settle path so every event under it carries job_id / wire_name / attempt /
    // queue without repeating them per message. Passed as a readonly struct to the generic BeginScope so
    // a NullLogger never boxes it (it returns a shared no-op scope and ignores the state) - zero heap
    // allocation on the silent path - while a real provider enumerates it as structured key/value pairs.

    /// <summary>Opens a job-context log scope stamping job_id, wire_name, attempt, and queue.</summary>
    internal static IDisposable? BeginJobScope(ILogger logger, Guid jobId, string wireName, int attempt, string queue)
        => logger.BeginScope(new JobLogScope(jobId, wireName, attempt, queue));

    // A struct scope state: four fixed structured fields, enumerable as key/value pairs the way the BCL's
    // own FormattedLogValues scope is, so a capturing provider records them by name.
    private readonly struct JobLogScope(Guid jobId, string wireName, int attempt, string queue)
        : IReadOnlyList<KeyValuePair<string, object?>>
    {
        public int Count => 4;

        public KeyValuePair<string, object?> this[int index] => index switch
        {
            0 => new("job_id", jobId),
            1 => new("wire_name", wireName),
            2 => new("attempt", attempt),
            3 => new("queue", queue),
            _ => throw new ArgumentOutOfRangeException(nameof(index)),
        };

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
        {
            for (var i = 0; i < Count; i++)
            {
                yield return this[i];
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public override string ToString()
            => $"job_id={jobId} wire_name={wireName} attempt={attempt} queue={queue}";
    }

    // ── Lifecycle events ─────────────────────────────────────────────────────────────────────────
    // EventIds are stable and grouped by phase (10xx enqueue/claim, 11xx execution, 12xx settlement,
    // 13xx store/schema, 14xx observer, 15xx wake-up hints) so a consumer can filter on them. Parameter names are snake_case
    // so the [LoggerMessage] generator maps them to the same-named template placeholders (the emitted
    // structured keys).

    [LoggerMessage(EventId = 1001, Level = LogLevel.Debug,
        Message = "Job {job_id} ({wire_name}) enqueued to queue {queue}.")]
    internal static partial void JobEnqueued(ILogger logger, Guid job_id, string wire_name, string queue);

    [LoggerMessage(EventId = 1101, Level = LogLevel.Trace,
        Message = "Lease acquired for job {job_id} ({wire_name}) attempt {attempt} on queue {queue}.")]
    internal static partial void LeaseAcquired(ILogger logger, Guid job_id, string wire_name, int attempt, string queue);

    [LoggerMessage(EventId = 1102, Level = LogLevel.Debug, Message = "Job execution started.")]
    internal static partial void ExecutionStarted(ILogger logger);

    [LoggerMessage(EventId = 1103, Level = LogLevel.Debug, Message = "Job execution completed.")]
    internal static partial void ExecutionCompleted(ILogger logger);

    [LoggerMessage(EventId = 1201, Level = LogLevel.Information,
        Message = "Retry scheduled after a failed attempt; next due at {next_due:o}.")]
    internal static partial void RetryScheduled(ILogger logger, DateTimeOffset next_due);

    [LoggerMessage(EventId = 1202, Level = LogLevel.Warning,
        Message = "Lease lost; the attempt was abandoned and its work will be reclaimed by another worker.")]
    internal static partial void LeaseLost(ILogger logger);

    [LoggerMessage(EventId = 1203, Level = LogLevel.Error,
        Message = "Job dead-lettered after exhausting its retry ceiling.")]
    internal static partial void DeadLettered(ILogger logger);

    // The reclaiming side of a lost lease: 1202 above logs the losing worker, this logs the sweep that
    // reclaims the expired leases store-side for redelivery. Emitted once per sweep and only when it
    // reclaimed at least one lease, so a quiet sweep (the common case) stays silent.
    [LoggerMessage(EventId = 1204, Level = LogLevel.Information,
        Message = "Worker group '{worker_group}' reclaimed {reclaimed_count} expired lease(s) for redelivery.")]
    internal static partial void LeasesReclaimed(ILogger logger, string worker_group, int reclaimed_count);

    [LoggerMessage(EventId = 1301, Level = LogLevel.Warning,
        Message = "Worker group '{worker_group}' hit a transient store fault; retrying on the next tick.")]
    internal static partial void StoreFaultTransientRetry(ILogger logger, string worker_group, Exception exception);

    [LoggerMessage(EventId = 1302, Level = LogLevel.Information,
        Message = "BackWave schema migration applied for {db_system}.")]
    internal static partial void MigrationApplied(ILogger logger, string db_system);

    [LoggerMessage(EventId = 1401, Level = LogLevel.Warning,
        Message = "Observer '{observer_id}' delivery dead-lettered after exhausting its retry ceiling.")]
    internal static partial void ObserverDeliveryDeadLettered(ILogger logger, string observer_id);

    [LoggerMessage(EventId = 1501, Level = LogLevel.Warning,
        Message = "Wake-Up Hint channel for {db_system} is unavailable; falling back to polling until it recovers.")]
    internal static partial void WakeHintChannelUnavailable(ILogger logger, string db_system, Exception exception);
}
