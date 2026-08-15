using BackWave.Storage;

namespace BackWave.Monitor;

/// <summary>
/// A read-only view of one job as the Monitor surfaces it: the displayable facts only, never the
/// payload bytes (use the Monitor's payload read for those).
/// </summary>
public sealed record JobSnapshot
{
    /// <summary>The job's unique id.</summary>
    public required Guid JobId { get; init; }

    /// <summary>The wire name of the job's type: the stable string identity it was registered under.</summary>
    public required string WireName { get; init; }

    /// <summary>The queue this job runs on.</summary>
    public required string Queue { get; init; }

    /// <summary>The job's current lifecycle state.</summary>
    public required JobState State { get; init; }

    /// <summary>Execution tries so far; claiming a job to run starts an attempt.</summary>
    public required int Attempt { get; init; }

    /// <summary>When the job becomes (or became) eligible to run.</summary>
    public required DateTimeOffset DueTime { get; init; }

    /// <summary>The worker currently holding this job; set while it is leased to a worker, null otherwise — the "executing now" view.</summary>
    public string? LeaseOwner { get; init; }

    /// <summary>When the current lease expires if not renewed by a heartbeat; set while leased, null otherwise.</summary>
    public DateTimeOffset? LeaseExpiry { get; init; }

    /// <summary>Whether cancellation has been requested for this job. The running attempt observes it cooperatively.</summary>
    public bool CancelRequested { get; init; }

    /// <summary>When the job reached a terminal state (succeeded, dead-lettered, or cancelled); null while still active.</summary>
    public DateTimeOffset? TerminalAt { get; init; }

    /// <summary>A short reason for the terminal outcome (for example why it was dead-lettered); null while still active.</summary>
    public string? TerminalCause { get; init; }

    /// <summary>The recurring schedule that minted this instance; null for a directly enqueued job.</summary>
    public string? ScheduleId { get; init; }

    /// <summary>
    /// A monotonic ordering value used for paging. To fetch the next page, pass the last row's value as
    /// the job query's after-sequence cursor.
    /// </summary>
    public long Sequence { get; init; }

    /// <summary>The workflow this job belongs to, or null for a job that is not part of a workflow.</summary>
    public Guid? WorkflowId { get; init; }

    /// <summary>
    /// The job's tags: an observational set of strings for search, filtering, and grouping (the
    /// dashboard renders them as pills). Empty for an untagged job.
    /// </summary>
    public IReadOnlyList<JobTag> Tags { get; init; } = JobTags.Empty;
}

/// <summary>A read-only view of one recurring schedule as the Monitor surfaces it.</summary>
public sealed record ScheduleStatus
{
    /// <summary>The schedule's unique id.</summary>
    public required string ScheduleId { get; init; }

    /// <summary>The schedule's cron expression, in canonical 6-field form.</summary>
    public required string Cron { get; init; }

    /// <summary>The wire name of the job type this schedule mints.</summary>
    public required string WireName { get; init; }

    /// <summary>The queue the minted jobs run on.</summary>
    public required string Queue { get; init; }

    /// <summary>The IANA time-zone id the cron is evaluated in; null means UTC.</summary>
    public string? TimeZoneId { get; init; }

    /// <summary>Whether ticks missed while the host was down are run on recovery, or skipped.</summary>
    public CatchUpPolicy CatchUp { get; init; }

    /// <summary>Whether a new tick is skipped while a previous instance of this schedule is still running.</summary>
    public bool NoOverlap { get; init; }

    /// <summary>The instant up to which due ticks have been resolved (inclusive).</summary>
    public required DateTimeOffset Cursor { get; init; }

    /// <summary>The next tick that will mint, or null if the cron has no future occurrence.</summary>
    public DateTimeOffset? NextDue { get; init; }

    /// <summary>Whether a minted instance is currently non-terminal (what No-Overlap watches).</summary>
    public bool HasLiveInstance { get; init; }

    /// <summary>Recently skipped ticks (No-Overlap or Catch-Up), newest last, bounded.</summary>
    public IReadOnlyList<DateTimeOffset> SkippedTicks { get; init; } = [];

    /// <summary>
    /// Non-null when the schedule cannot be resolved on this host (unparseable cron, or an
    /// IANA zone id absent here): minting skips it and the Monitor shows it quarantined,
    /// never silent. Null for a healthy schedule.
    /// </summary>
    public string? Error { get; init; }
}
