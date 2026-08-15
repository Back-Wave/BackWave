namespace BackWave.Tests.Simulation;

/// <summary>
/// The single registry of every oracle the Simulator asserts (issue 0085, ADR 0018). Each member is a
/// <i>stable invariant ID</i>: the key the Seed Minimizer (0088) and the VOPR Runner's dedup (0087) match
/// "same failure" on by identity — never by message text, Seed, virtual time, step count, or an embedded
/// jobId, all of which vary across candidates. <see cref="SimulationInvariantException"/> carries the
/// tripped member and a <see cref="FailureStamp"/> persists it.
///
/// This enum IS the registry: <c>Simulator.Invariant</c> takes an <see cref="InvariantId"/> with no default,
/// so a new assertion site cannot compile without naming one — there is no silent fallback. Members are a
/// stable wire identity (serialized by name); rename only with a fixture migration.
/// </summary>
internal enum InvariantId
{
    /// <summary>Event-loop guard: the run must converge well within a generous step ceiling.</summary>
    RunawayEventLoop,

    // ── Existing core oracles (§3, §5.7, §5.8; ADR 0011/0013/0016) ──────────────────────────────

    /// <summary>Work-conservation / stuck-job: every job reaches a terminal state by the drain window's end.</summary>
    DrainLiveness,

    /// <summary>At-least-once-execute: a ran-to-completion terminal is only reachable by having executed.</summary>
    ExecuteLiveness,

    /// <summary>A Quarantined job was unroutable and never dispatched, so it must never have executed.</summary>
    QuarantineNotExecuted,

    /// <summary>Audit-log completeness: every Operator Action is recorded exactly once per target.</summary>
    AuditCompleteness,

    /// <summary>A job's first recorded state is a legal initial state.</summary>
    LegalInitialState,

    /// <summary>Legal-transition: every recorded edge is a legal state-machine move.</summary>
    LegalTransition,

    /// <summary>Attempt never regresses across an edge (bar the Requeue reset).</summary>
    AttemptMonotonic,

    /// <summary>No-Overlap (§5.7): at most one instance of a No-Overlap schedule is non-terminal at once.</summary>
    NoOverlap,

    /// <summary>Pause (§5.8): no job in a Queue the oracle believes Paused is freshly Leased.</summary>
    PausedClaim,

    /// <summary>I1: at most one node executes a job whose live Lease the store still attributes to it.</summary>
    NoDoubleExecution,

    /// <summary>A Leased job carries both a Lease owner and an expiry.</summary>
    LeaseOwnerPresent,

    /// <summary>A non-Leased job carries no Lease owner.</summary>
    LeaseOwnerCleared,

    /// <summary>A job never exceeds the configured attempt ceiling.</summary>
    AttemptCeiling,

    /// <summary>I2: no AwaitingParent orphan — a terminal parent means the latch has already fired.</summary>
    NoAwaitingParentOrphan,

    /// <summary>Cancellation provenance (§5.8): every Cancelled job has an operator or failed-parent cause.</summary>
    CancelProvenance,

    /// <summary>A terminal job never changes state.</summary>
    TerminalStable,

    /// <summary>A terminal job retains its TerminalAt stamp.</summary>
    TerminalTimestamp,

    /// <summary>Outcome-Provenance (ADR 0013): Effect-Once at the Storage Contract boundary — no stale write.</summary>
    OutcomeProvenance,

    /// <summary>Migration-Liveness (issue 0069): a survivor sweeps an isolated owner's lapsed Lease within bound.</summary>
    MigrationLiveness,

    // ── Phase-2 additions (issues 0071–0075) ───────────────────────────────────────────────────

    /// <summary>Served-set containment (0071): a node only Leases Queues in its declared served set.</summary>
    ServedSetContainment,

    /// <summary>Per-node cap (0072): a node's real in-flight executions never exceed its PoolSize.</summary>
    PerNodeCap,

    /// <summary>I3 (0074): a limited Queue's concurrent Leases never exceed its Concurrency Limit.</summary>
    ConcurrencyLimit,

    /// <summary>Slot-double-release (0074): an Attempt's Concurrency slot frees at most once (Effect-Once).</summary>
    SlotDoubleRelease,

    // ── Transition Observer (§0076–0078; ADR 0017): at-least-once delivery + bounded poison ─────

    /// <summary>First delivery of each transition arrives in ascending log order per job (duplicates tolerated).</summary>
    ObserverDeliveryOrder,

    /// <summary>At-least-once-delivery liveness: every watched transition is delivered ≥1 or dead-lettered.</summary>
    ObserverDeliveryLiveness,

    /// <summary>Bounded poison: a dead-lettered delivery exhausted the attempt ceiling first.</summary>
    ObserverPoisonBounded,

    /// <summary>A delivered transition never also dead-letters (delivered XOR dead-lettered).</summary>
    ObserverDeliveredXorDeadLettered,
}
