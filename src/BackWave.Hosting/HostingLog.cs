using Microsoft.Extensions.Logging;

namespace BackWave.Hosting;

// The Hosting Shell's own source-generated [LoggerMessage] catalog for its operational fault sites - the
// worker-group fail-stop and the observer-pump fault paths that were previously ad-hoc ILogger calls.
// Folding them here (rather than into the Core BackWave lifecycle catalog) keeps the shared lifecycle
// vocabulary in Core and the Hosting-only operational messages next to the pumps that raise them. The
// [LoggerMessage] generator guards each call on IsEnabled, so a disabled level formats nothing; parameter
// names are snake_case so the generator maps them to the same-named template placeholders.
internal static partial class HostingLog
{
    [LoggerMessage(EventId = 2001, Level = LogLevel.Critical,
        Message = "BackWave Worker Group '{worker_group}' fail-stopped on an invariant violation; the group is halted.")]
    internal static partial void WorkerGroupFailStopped(ILogger logger, string worker_group, Exception exception);

    [LoggerMessage(EventId = 2101, Level = LogLevel.Error,
        Message = "BackWave Observer dispatch pump faulted on a non-cancellation error; the pump is stopping. "
            + "The cursor Lease will lapse and another node re-claims; the host keeps serving.")]
    internal static partial void ObserverPumpFaulted(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 2102, Level = LogLevel.Warning,
        Message = "BackWave Observer '{observer_id}' claim faulted; releasing and re-claiming next poll.")]
    internal static partial void ObserverClaimFaulted(ILogger logger, string observer_id, Exception exception);

    [LoggerMessage(EventId = 2103, Level = LogLevel.Warning,
        Message = "BackWave Observer '{observer_id}' report faulted; cursor stands, redelivers on the next claim.")]
    internal static partial void ObserverReportFaulted(ILogger logger, string observer_id, Exception exception);

    [LoggerMessage(EventId = 2104, Level = LogLevel.Warning,
        Message = "BackWave Observer '{observer_id}' callback faulted; recording the delivery as failed.")]
    internal static partial void ObserverCallbackFaulted(ILogger logger, string observer_id, Exception exception);

    [LoggerMessage(EventId = 2105, Level = LogLevel.Error,
        Message = "BackWave Observer '{observer_id}' callback did not complete within the {delivery_timeout} delivery "
            + "timeout and ignored its cancellation token; recording the delivery as failed and proceeding. The "
            + "callback task is leaked (it will be observed in the background) - make the observer honor its "
            + "CancellationToken.")]
    internal static partial void ObserverCallbackTimedOut(ILogger logger, string observer_id, TimeSpan delivery_timeout);

    [LoggerMessage(EventId = 2106, Level = LogLevel.Error,
        Message = "BackWave Observer '{observer_id}' leaked (timed-out) callback later faulted; the delivery was already "
            + "recorded failed. Swallowed to keep it from surfacing as an unobserved task exception.")]
    internal static partial void ObserverLeakedCallbackFaulted(ILogger logger, string observer_id, Exception exception);
}
