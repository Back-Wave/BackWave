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
    // invariant_trigger is the InvariantTrigger member name when a named check raised the halt, and
    // UnclassifiedTrigger when the group stopped on a fault no check named (the negative catch-all). It
    // is the same stable id BackWaveHealth's HaltState carries and the backwave.invariant.trigger metric
    // tag emits, so the halt log and the health report never disagree about why a group stopped.
    [LoggerMessage(EventId = 2001, Level = LogLevel.Critical,
        Message = "BackWave Worker Group '{worker_group}' fail-stopped on an invariant violation "
            + "({invariant_trigger}); the group is halted.")]
    internal static partial void WorkerGroupFailStopped(
        ILogger logger, string worker_group, string invariant_trigger, Exception exception);

    // The value 2001 carries when the halting fault matched no named check: today's negative catch-all
    // still halts, and it needs a trigger value that cannot be mistaken for an InvariantTrigger member.
    internal const string UnclassifiedTrigger = "unclassified";

    // The Degrade counterpart of 2001, at group altitude: a named check tripped, the site took its
    // benign branch, and the group keeps claiming and executing. Warning, not Critical - nothing stopped.
    // "Degraded" here is the invariant sense and NOT BackWaveHealth's: that one means a transient store
    // fault, it is reported only from the pump's transient catch, and it is what turns the health probe
    // amber. This log never touches the probe, so a group that emits 2003 still reads Healthy.
    [LoggerMessage(EventId = 2003, Level = LogLevel.Warning,
        Message = "BackWave Worker Group '{worker_group}' tripped invariant '{invariant_trigger}': {detail}. "
            + "The group keeps running, degraded.")]
    internal static partial void WorkerGroupDegradedByInvariant(
        ILogger logger, string worker_group, string invariant_trigger, string detail);

    [LoggerMessage(EventId = 2002, Level = LogLevel.Error,
        Message = "BackWave Worker Group '{worker_group}' job {job_id} produced a Job Output of {actual_bytes} bytes, "
            + "which exceeds the store's {max_output_bytes}-byte bound; the job is dead-lettered and the group keeps "
            + "running. Store a reference (id, blob key) instead of the data itself.")]
    internal static partial void JobOutputRejected(
        ILogger logger, string worker_group, Guid job_id, int actual_bytes, int max_output_bytes);

    [LoggerMessage(EventId = 2004, Level = LogLevel.Error,
        Message = "BackWave Worker Group '{worker_group}' job {job_id} added a Job Tag with a {key_length}-character key "
            + "and a {value_length}-character value, which exceeds the store's MaxTagKeyLength {max_tag_key_length} / "
            + "MaxTagValueLength {max_tag_value_length} bound; the job is dead-lettered and the group keeps running. "
            + "Tags are rejected, never truncated.")]
    internal static partial void JobTagRejected(
        ILogger logger, string worker_group, Guid job_id, int key_length, int value_length,
        int max_tag_key_length, int max_tag_value_length);

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
