namespace BackWave.Benchmarks.Metrics;

/// <summary>
/// The aggregated metrics for a single run, derived purely from one <see cref="RunSamples"/>. Latency is
/// always a percentile pair (p50/p99), never a bare average (ADR 0027 §4).
/// </summary>
public sealed record RunMetrics
{
    /// <summary>Throughput in jobs/second: completed jobs over the measured window.</summary>
    public required double ThroughputJobsPerSecond { get; init; }

    /// <summary>Median end-to-end latency (enqueue→terminal).</summary>
    public required TimeSpan EndToEndP50 { get; init; }

    /// <summary>99th-percentile end-to-end latency (enqueue→terminal) — the tail.</summary>
    public required TimeSpan EndToEndP99 { get; init; }

    /// <summary>Median enqueue latency (call→committed).</summary>
    public required TimeSpan EnqueueP50 { get; init; }

    /// <summary>99th-percentile enqueue latency (call→committed) — the tail.</summary>
    public required TimeSpan EnqueueP99 { get; init; }
}

/// <summary>
/// A min/median/max distribution across repeated runs (warmup already discarded). A single hero run is
/// never the published figure (ADR 0027 §4); the distribution is.
/// </summary>
/// <param name="Min">The smallest value across the runs.</param>
/// <param name="Median">The median value across the runs.</param>
/// <param name="Max">The largest value across the runs.</param>
public readonly record struct Distribution(double Min, double Median, double Max);
