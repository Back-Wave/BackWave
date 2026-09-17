using BackWave.Storage;

namespace BackWave.Conformance;

/// <summary>
/// The optional capabilities an adapter's certification class declares through
/// <see cref="ConformanceSuite.Capabilities"/>. Each flag names a hook or store feature the suite
/// cannot assume, and the clauses that need it are gated on the declaration: a declared capability
/// whose hook still returns its default fails those clauses, so a lapsed hook can never pass in
/// silence, and an undeclared capability makes them return early with a "skipped" line on the test
/// output. Declare exactly the set your subclass provides.
/// </summary>
[Flags]
public enum ConformanceCapabilities
{
    /// <summary>No optional capability; only the mandatory spine runs.</summary>
    None = 0,

    /// <summary>
    /// The store overrides <see cref="IJobStore.ClaimBatchAsync"/> to report a real
    /// <see cref="ClaimResult.NextDue"/>. Declaring it proves the next-due hint is exact; without it
    /// the next-due clauses certify only the documented <c>null</c> fallback.
    /// </summary>
    NextDue = 1 << 0,

    /// <summary>
    /// The store implements the optional hand-back <see cref="IJobStore.RelinquishLeasesAsync"/>.
    /// Declaring it proves a stopping worker returns its leases at once; without it the hand-back clauses
    /// certify only the interface default of relinquishing nothing.
    /// </summary>
    LeaseRelinquish = 1 << 1,

    /// <summary>
    /// The store applies a batched outcome report as one all-or-nothing unit. Declaring it proves an
    /// over-limit row aborts the whole batch instead of leaving earlier rows applied.
    /// </summary>
    AtomicBatchOutcomes = 1 << 2,

    /// <summary>
    /// The subclass overrides <see cref="ConformanceSuite.CreateFaultArmedStoreAsync"/>. Declaring it proves
    /// every multi-effect operation lands all-or-nothing when interrupted before commit.
    /// </summary>
    FaultInjection = 1 << 3,

    /// <summary>
    /// The subclass overrides <see cref="ConformanceSuite.CreateInterleavingStoreAsync"/>. Declaring it
    /// proves the read-then-write races the suite pins deterministically (a claim against a queue's first
    /// config write, two creates of one workflow) resolve without a raw database error.
    /// </summary>
    ForcedInterleaving = 1 << 4,

    /// <summary>
    /// The subclass overrides <see cref="ConformanceSuite.HoldQueueConfigLockAsync"/>. Declaring it proves
    /// the per-queue lock the claim read path and the operator config setters share really serializes a
    /// claim against a queue's first-ever settings write.
    /// </summary>
    QueueConfigLock = 1 << 5,

    /// <summary>
    /// The subclass overrides <see cref="ConformanceSuite.HoldTagRowAsync"/>. Declaring it proves a tag
    /// insert that meets a concurrently committed duplicate converges idempotently instead of surfacing a
    /// duplicate-key error.
    /// </summary>
    ConcurrentTagInsert = 1 << 6,

    /// <summary>
    /// The subclass overrides <see cref="ConformanceSuite.HoldEdgeRowAsync"/>. Declaring it proves the same
    /// convergence for a workflow's structural-edge insert.
    /// </summary>
    ConcurrentEdgeInsert = 1 << 7,

    /// <summary>
    /// The subclass overrides <see cref="ConformanceSuite.TryStoreUndefinedJobStateAsync"/>. Declaring it
    /// proves a job state written out of band, outside <see cref="JobState"/>, is met with a named
    /// invariant violation instead of a raw cast.
    /// </summary>
    OutOfBandStateWrite = 1 << 8,

    /// <summary>
    /// The store overrides <see cref="IJobStore.TryReportObserverDeliveriesAsync"/> to name its
    /// refusals. Declaring it proves an unknown observer and a fenced-out worker are each reported as such;
    /// without it the clauses certify only that the refused write changed nothing.
    /// </summary>
    ObserverReportOutcomes = 1 << 9,
}
