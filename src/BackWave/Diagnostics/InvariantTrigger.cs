namespace BackWave.Diagnostics;

/// <summary>
/// The single registry of every impossible-state condition BackWave detects at runtime. Each member is
/// a <i>stable trigger id</i>: the identity an operator's alert rule, a log filter, and a metric query
/// match a fail-stop on - never on message text, a job id, or a worker-group name, all of which vary
/// between one occurrence and the next. <see cref="InvariantViolationException"/> carries the tripped
/// member, the halt log names it, and the <c>backwave.invariant.violations</c> counter tags with it.
/// <para>
/// This enum IS the registry: a check site cannot compile without naming one, so there is no unnamed
/// detection. Members are a stable wire identity (surfaced by name, never by number): the ordinal is
/// deliberately unpinned, so members may be reordered freely, while a rename is an operator-visible
/// break that silently retires an alert rule rather than failing a build.
/// </para>
/// </summary>
public enum InvariantTrigger
{
    // ── Worker Group pump ────────────────────────────────────────────────────────────────────────

    /// <summary>A batched outcome report came back with a different row count than it was given.</summary>
    OutcomeBatchCountMismatch,

    /// <summary>A batched heartbeat came back with a different row count than it was given.</summary>
    HeartbeatBatchCountMismatch,

    /// <summary>A claim handed back a job that is already in a terminal state, so it must not execute.</summary>
    ClaimedJobTerminal,

    // ── Node Driver ──────────────────────────────────────────────────────────────────────────────

    /// <summary>A claim returned more jobs than the batch bound the Driver put on it.</summary>
    ClaimBatchOverrun,

    /// <summary>A claim handed back a job the Driver already believes it is executing.</summary>
    DuplicateExecutingJob,

    // ── Dependency resolution and gating ─────────────────────────────────────────────────────────

    /// <summary>A live workflow member names a workflow whose row is absent.</summary>
    WorkflowMemberWithoutWorkflow,

    /// <summary>A gate read an ancestor still AwaitingParent, so it evaluated before that ancestor ran.</summary>
    GateAncestorNotRun,

    // ── Storage adapters ─────────────────────────────────────────────────────────────────────────

    /// <summary>A claimed row came back that is not Leased to the claiming worker.</summary>
    ClaimedRowNotLeasedToWorker,

    /// <summary>A workflow member's enqueue was rejected inside the transaction that validated it.</summary>
    WorkflowMemberEnqueueRejected,

    /// <summary>A gating edge names a child job row that does not exist, which a foreign key forbids.</summary>
    DanglingGatingEdge,

    /// <summary>A write that must affect exactly one already-locked row affected a different number.</summary>
    UnexpectedAffectedRowCount,

    /// <summary>A reader returned no row where the same transaction had just created or ensured one.</summary>
    GuaranteedRowAbsent,

    /// <summary>A parent job read under lock was absent from the set built by that same read.</summary>
    ParentJobMissingFromBatch,

    /// <summary>A stored column holds an integer that is not a defined value of the enum it maps to.</summary>
    UndefinedEnumValueStored,

    /// <summary>An observer cursor was asked to move strictly backward, which its monotonicity forbids.</summary>
    ObserverCursorRegressed,

    /// <summary>A serializing application lock reported that it was not acquired.</summary>
    ApplicationLockNotAcquired,

    /// <summary>A queue-config lock returned no anchor row, so the lock was never taken.</summary>
    QueueConfigLockNotAcquired,

    /// <summary>A leased-count aggregate came back NULL, which a COUNT never returns.</summary>
    LeasedCountAggregateNull,
}
