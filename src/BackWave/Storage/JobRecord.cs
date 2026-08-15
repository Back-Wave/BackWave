namespace BackWave.Storage;

/// <summary>An immutable snapshot of a stored job, as the storage layer exposes it to readers.</summary>
public sealed record JobRecord
{
    /// <summary>The job's stable, unique identifier, assigned at enqueue and never reused.</summary>
    public required Guid JobId { get; init; }

    /// <summary>The serialization-stable name of the job's payload type, used to route and deserialize it.</summary>
    public required string WireName { get; init; }

    /// <summary>The serialized payload bytes the handler will deserialize and run against.</summary>
    public required ReadOnlyMemory<byte> Payload { get; init; }

    /// <summary>Opaque trace-correlation context captured at enqueue; parents the execution span. Null when none was supplied.</summary>
    public string? TraceContext { get; init; }

    /// <summary>The queue the job belongs to. Claiming, concurrency limits, and pausing are all per-queue.</summary>
    public required string Queue { get; init; }

    /// <summary>The job's current lifecycle state.</summary>
    public required JobState State { get; init; }

    /// <summary>The UTC instant the job becomes eligible to run; meaningful while the job is Scheduled.</summary>
    public required DateTimeOffset DueTime { get; init; }

    /// <summary>How many times execution has been started. A claim increments this, because claiming is the start of an attempt.</summary>
    public int Attempt { get; init; }

    /// <summary>The worker that currently holds the lease, or null when the job is not leased.</summary>
    public string? LeaseOwner { get; init; }

    /// <summary>When the current lease lapses; once past, the job may be reclaimed. Null when not leased.</summary>
    public DateTimeOffset? LeaseExpiry { get; init; }

    /// <summary>Cooperative cancellation flag: set when a leased job is asked to cancel, and observed by the worker through its heartbeat.</summary>
    public bool CancelRequested { get; init; }

    /// <summary>When the job reached a terminal state, or null while it is still live.</summary>
    public DateTimeOffset? TerminalAt { get; init; }

    /// <summary>A short human-readable reason for the terminal state (the failure error, cancel actor, or unroutable reason), or null while live.</summary>
    public string? TerminalCause { get; init; }

    /// <summary>The id of the recurring schedule that minted this instance, or null for a directly enqueued job.</summary>
    public string? ScheduleId { get; init; }

    /// <summary>The countdown of parents not yet terminal that still gate this job; meaningful while it is awaiting a parent. Reaches zero when the last parent resolves.</summary>
    public int ParentsRemaining { get; init; }

    /// <summary>
    /// A store-assigned counter that strictly increases with insertion order. It serves as the
    /// within-queue tiebreak when ordering claims of equal due time, and as the stable cursor for
    /// paging listings.
    /// </summary>
    public long Sequence { get; init; }

    /// <summary>How the job's dependency on its parents is interpreted (run on parent success, or run regardless). Defaults to running only when every parent succeeds.</summary>
    public DependencyMode Mode { get; init; } = DependencyMode.OnSuccess;

    /// <summary>
    /// The workflow this job is a member of, or null for a non-workflow job. An immutable identifier
    /// set once when the workflow is enqueued (at most one per job); it is observational metadata the
    /// scheduling core never reads.
    /// </summary>
    public Guid? WorkflowId { get; init; }

    /// <summary>
    /// The job's tags: an observational set of string annotations attached at enqueue and accumulated
    /// at runtime. Used for search, filtering, and grouping; the scheduling core never reads them.
    /// </summary>
    public JobTags Tags { get; init; } = JobTags.Empty;

    /// <summary>
    /// The job's output: the single opaque blob a handler emitted, written to this row only when the
    /// job succeeds and on no other outcome. It is functional data a dependent descendant may read,
    /// not diagnostics, so it survives regardless of how much history the store keeps. Null when the
    /// job produced no output. It is fetched on demand through the dedicated output read, not carried
    /// in listing snapshots.
    /// </summary>
    public ReadOnlyMemory<byte>? Output { get; init; }
}
