using BackWave.Storage;

namespace BackWave.Driver;

/// <summary>
/// Inputs to the Node Driver. The Shell tells the Driver what happened; the Driver
/// never finds anything out on its own.
/// </summary>
internal abstract record NodeEvent
{
    private NodeEvent() { }

    /// <summary>The poll tick: time to look for due work.</summary>
    public sealed record PollDue(DateTimeOffset Now) : NodeEvent;

    /// <summary>The store returned the Recurring Schedules for mint planning.</summary>
    public sealed record SchedulesLoaded(IReadOnlyList<ScheduleSnapshot> Schedules, DateTimeOffset Now) : NodeEvent;

    /// <summary>A Claim the Driver asked for has completed.</summary>
    public sealed record ClaimCompleted(IReadOnlyList<JobRecord> Jobs, DateTimeOffset Now) : NodeEvent;

    /// <summary>The Shell ran a leased job's handler to successful completion.</summary>
    public sealed record ExecutionSucceeded(JobRecord Job, DateTimeOffset Now) : NodeEvent;

    /// <summary>
    /// The Shell caught a handler failure at the execution boundary. Raw fact only —
    /// the retry decision belongs to the Driver's Core policy, not the Shell.
    /// </summary>
    public sealed record ExecutionFailed(JobRecord Job, string Error, DateTimeOffset Now) : NodeEvent;

    /// <summary>The Shell caught the handler observing cooperative cancellation.</summary>
    public sealed record ExecutionCancelled(JobRecord Job, string Cause, DateTimeOffset Now) : NodeEvent;

    /// <summary>
    /// The Shell could not route the job: unregistered Wire Name or undecodable payload.
    /// Deploy drift becomes an observable event, never a retry storm.
    /// </summary>
    public sealed record ExecutionUnroutable(JobRecord Job, string Reason, DateTimeOffset Now) : NodeEvent;

    /// <summary>A retention sweep finished; a full batch means more may remain.</summary>
    public sealed record PurgeCompleted(Command.PurgeTerminal Sweep, int Purged, DateTimeOffset Now) : NodeEvent;

    /// <summary>The heartbeat tick: time to renew Leases on everything executing.</summary>
    public sealed record HeartbeatDue(DateTimeOffset Now) : NodeEvent;

    /// <summary>The store answered a heartbeat.</summary>
    public sealed record HeartbeatCompleted(IReadOnlyList<HeartbeatResult> Results, DateTimeOffset Now) : NodeEvent;

    /// <summary>The store acknowledged (or rejected as stale) a reported outcome.</summary>
    public sealed record OutcomeReported(Guid JobId, OutcomeResult Result, DateTimeOffset Now) : NodeEvent;

    /// <summary>The store applied the Core's mint decisions; carries how many instances it minted.</summary>
    public sealed record MintCompleted(int Minted, DateTimeOffset Now) : NodeEvent;
}
