using BackWave.Benchmarks.Environment;
using BackWave.Benchmarks.Metrics;
using BackWave.Benchmarks.Targets;
using BackWave.Benchmarks.Workload;

namespace BackWave.Benchmarks;

/// <summary>
/// The machine-readable result of one benchmark invocation: the workload, the environment manifest, the
/// per-measured-run metrics, and the cross-run distributions (warmup discarded). Serialized to JSON so
/// trends can be charted or fed to a regression job without re-running (ADR 0027 §4, PRD output).
/// </summary>
public sealed record BenchmarkResult
{
    /// <summary>The system+adapter that produced this result, e.g. "BackWave/Postgres".</summary>
    public required string Target { get; init; }

    /// <summary>The storage engine under test.</summary>
    public required string Engine { get; init; }

    /// <summary>The workload that was run.</summary>
    public required WorkloadSummary Workload { get; init; }

    /// <summary>The environment stamp + publishable flag.</summary>
    public required EnvironmentManifest Manifest { get; init; }

    /// <summary>
    /// The tuned configuration dials this number was produced under — the neutralized config matched
    /// cross-system and the surfaced architectural choices — recorded so a third party can reproduce and
    /// challenge any number (ADR 0027 §5).
    /// </summary>
    public required IReadOnlyDictionary<string, string> TuningDials { get; init; }

    /// <summary>Number of warmup runs discarded before measurement.</summary>
    public required int WarmupRuns { get; init; }

    /// <summary>Number of measured runs that fed the distributions.</summary>
    public required int MeasuredRuns { get; init; }

    /// <summary>Throughput distribution (jobs/sec) across the measured runs.</summary>
    public required Distribution ThroughputJobsPerSecond { get; init; }

    /// <summary>End-to-end latency p50 distribution, in milliseconds, across the measured runs.</summary>
    public required Distribution EndToEndP50Ms { get; init; }

    /// <summary>End-to-end latency p99 distribution, in milliseconds, across the measured runs.</summary>
    public required Distribution EndToEndP99Ms { get; init; }

    /// <summary>Enqueue latency p50 distribution, in milliseconds, across the measured runs.</summary>
    public required Distribution EnqueueP50Ms { get; init; }

    /// <summary>Enqueue latency p99 distribution, in milliseconds, across the measured runs.</summary>
    public required Distribution EnqueueP99Ms { get; init; }

    /// <summary>
    /// The resource costs that travel with the throughput above, so no number is published naked: peak DB
    /// connections held and CPU at throughput (both fair cross-system metrics), plus BackWave-only,
    /// internal-only allocation/GC counters that must never be charted against a competitor.
    /// </summary>
    public required ResourceMetrics Resources { get; init; }

    /// <summary>Whether this number may be published (mirrors <see cref="EnvironmentManifest.Publishable"/>).</summary>
    public bool Publishable => Manifest.Publishable;
}

/// <summary>A flat, JSON-friendly snapshot of the <see cref="WorkloadSpec"/> that produced a result.</summary>
public sealed record WorkloadSummary
{
    /// <summary>Total jobs in the stream.</summary>
    public required int JobCount { get; init; }

    /// <summary>Per-job handler delay in milliseconds (0 = noop ceiling).</summary>
    public required int HandlerDelayMs { get; init; }

    /// <summary>Arrival shape (Drain or Sustained).</summary>
    public required string Arrival { get; init; }

    /// <summary>Target enqueue rate for sustained mode (0 for drain / unpaced).</summary>
    public required double SustainedRatePerSecond { get; init; }

    /// <summary>Concurrent producer tasks that enqueued the stream.</summary>
    public required int ProducerCount { get; init; }

    /// <summary>Fixed payload size band, in bytes.</summary>
    public required int PayloadSizeBytes { get; init; }

    /// <summary>Captures the JSON-friendly summary from a spec.</summary>
    public static WorkloadSummary From(WorkloadSpec spec) => new()
    {
        JobCount = spec.JobCount,
        HandlerDelayMs = spec.DelayMs,
        Arrival = spec.Arrival.ToString(),
        SustainedRatePerSecond = spec.SustainedRatePerSecond,
        ProducerCount = spec.ProducerCount,
        PayloadSizeBytes = spec.PayloadSizeBytes,
    };
}
