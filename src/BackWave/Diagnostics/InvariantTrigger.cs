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

    /// <summary>The store's identity fence refused an outcome write while the reporting pump still believed
    /// its Lease was live, by more than the clock skew a fleet can carry. A Lease that simply lapsed is the
    /// ordinary end of an Attempt: another node inherits the job, and it raises nothing here. Only the
    /// contradiction is logged and counted, and neither one is ever halted on.</summary>
    OutcomeFenceRejected,

    // ── Node Driver ──────────────────────────────────────────────────────────────────────────────

    /// <summary>A claim returned more jobs than the batch bound the Driver put on it.</summary>
    ClaimBatchOverrun,

    // ── Dependency resolution and gating ─────────────────────────────────────────────────────────

    /// <summary>A live workflow member names a workflow whose row is absent.</summary>
    WorkflowMemberWithoutWorkflow,

    // ── Storage adapters ─────────────────────────────────────────────────────────────────────────

    /// <summary>A claimed row came back that is not Leased to the claiming worker.</summary>
    ClaimedRowNotLeasedToWorker,

    /// <summary>A claim's candidate row came back in a state the claim's own SQL predicate excludes.</summary>
    ClaimedRowNotEligible,

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

    /// <summary>An observer delivery report was refused by the claim-lease fence while the REPORTER still
    /// believed its own Lease on that claim was live, so two workers believed they held one observer claim
    /// at once. A Lease that simply lapsed is the ordinary end of a delivery attempt: at-least-once
    /// redelivers it, and it raises nothing here - including when a peer has already reclaimed the observer
    /// and the refused report reads that peer's future expiry.</summary>
    ObserverReportFenceRejected,

    /// <summary>A workflow's members hold a dependency cycle, so no insertion order puts every member
    /// after its own parents. The builder rejects a cycle before this point, so reaching it means the
    /// graph was never validated.</summary>
    WorkflowMemberCycle,
}
