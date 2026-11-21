using BackWave.Benchmarks.Workload;

namespace BackWave.Benchmarks.Targets;

/// <summary>
/// The raw per-run samples a target hands back after one execution, fed into the pure aggregator. Latency
/// is inherently target-sourced (each system records its own per-job timestamps); the <em>measurement</em>
/// — the wall-clock window and all the math — lives outside the target so it is provably identical across
/// systems (ADR 0027 §4–5).
/// </summary>
/// <param name="EnqueueLatencies">Per-job enqueue call→committed durations observed this run.</param>
/// <param name="EndToEndLatencies">Per-job enqueue→terminal durations observed this run.</param>
/// <param name="Resources">The resource-cost metrics captured around this run's timed window.</param>
/// <param name="Work">What the target can prove about the work itself; see <see cref="WorkEvidence"/>.</param>
public readonly record struct TargetSamples(
    IReadOnlyList<TimeSpan> EnqueueLatencies,
    IReadOnlyList<TimeSpan> EndToEndLatencies,
    ResourceMetrics Resources,
    WorkEvidence Work = default);

/// <summary>
/// The target's evidence that the work happened: how many handler executions it counted, the first and last
/// completion instants on the host clock, and how long the enqueue side took. The orchestrator checks it
/// against the job count and the timed window. A null field is one the target cannot observe.
/// </summary>
/// <param name="Executions">Handler executions counted this run; equal to the job count when each job ran exactly once.</param>
/// <param name="FirstCompletion">The earliest completion instant among the end-to-end samples.</param>
/// <param name="LastCompletion">The latest completion instant among the end-to-end samples.</param>
/// <param name="ProducerElapsed">First enqueue to last enqueue of this run's producers.</param>
public readonly record struct WorkEvidence(
    int? Executions,
    DateTimeOffset? FirstCompletion,
    DateTimeOffset? LastCompletion,
    TimeSpan? ProducerElapsed);

/// <summary>
/// The fairness seam (ADR 0027 §5). Each system under test — BackWave on each adapter, and later Hangfire
/// and MassTransit — plugs in here, and the <em>identical</em> measurement and metrics code runs around it.
/// The lifecycle is: <see cref="SetupAsync"/> once, then per run
/// <see cref="ResetAsync"/> → <see cref="PreloadAsync"/> → <see cref="WarmAsync"/> → <see cref="ExecuteAsync"/>
/// (the only timed call) → <see cref="CooldownAsync"/> → <see cref="CollectSamplesAsync"/>, and finally
/// <see cref="TeardownAsync"/>. Warm/cooldown bracket the timed window so host build, worker startup, and
/// graceful shutdown never land in the throughput denominator (ADR 0027 §2).
/// </summary>
public interface IBenchmarkTarget : IAsyncDisposable
{
    /// <summary>Display name of the system+adapter under test, e.g. "BackWave/Postgres".</summary>
    string Name { get; }

    /// <summary>The storage engine under test, e.g. "PostgreSQL" or "SQL Server".</summary>
    string Engine { get; }

    /// <summary>
    /// The tuned configuration dials this target runs under, recorded into the result so a third party can
    /// reproduce and challenge any number (ADR 0027 §5). Two kinds live here side by side: the
    /// <em>neutralized</em> config (worker/pool count, poll interval, connection-pool size, retry policy —
    /// matched cross-system so the comparison is honest) and the <em>surfaced</em> architectural choices
    /// (claim strategy, serialization — these are the product, won fairly, not hidden). Keyed dial → value.
    /// </summary>
    IReadOnlyDictionary<string, string> TuningDials { get; }

    /// <summary>One-time setup: connect, migrate schema, build the host. Returns the engine version string.</summary>
    Task<string> SetupAsync(CancellationToken cancellationToken);

    /// <summary>Empties all benchmark state so the next run starts from a clean store.</summary>
    Task ResetAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Drain mode: enqueue the whole backlog before the timed window opens. Sustained mode: a no-op —
    /// producers run concurrently inside <see cref="ExecuteAsync"/>.
    /// </summary>
    Task PreloadAsync(WorkloadSpec spec, CancellationToken cancellationToken);

    /// <summary>
    /// Starts the host/server outside the timed window so it is already claiming when the window opens. Keeps
    /// fixed spin-up (host build, worker-group startup) out of the throughput denominator (ADR 0027 §2).
    /// </summary>
    Task WarmAsync(WorkloadSpec spec, CancellationToken cancellationToken);

    /// <summary>
    /// The timed phase, and the only call the orchestrator times. Drain: run until the cluster is empty.
    /// Sustained: run producers and consumers together for the steady-state window. The host is already
    /// running (<see cref="WarmAsync"/>) and is torn down afterward (<see cref="CooldownAsync"/>).
    /// </summary>
    Task ExecuteAsync(WorkloadSpec spec, CancellationToken cancellationToken);

    /// <summary>
    /// Stops and disposes the host/server after the window closes. Drain has already confirmed every job
    /// terminal, so graceful shutdown is dead time that must not inflate the throughput denominator.
    /// </summary>
    Task CooldownAsync(CancellationToken cancellationToken);

    /// <summary>Returns the per-job latency samples gathered during the last <see cref="ExecuteAsync"/>.</summary>
    Task<TargetSamples> CollectSamplesAsync(CancellationToken cancellationToken);

    /// <summary>Releases the host and storage resources held since <see cref="SetupAsync"/>.</summary>
    Task TeardownAsync(CancellationToken cancellationToken);
}
