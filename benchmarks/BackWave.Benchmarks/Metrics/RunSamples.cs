namespace BackWave.Benchmarks.Metrics;

/// <summary>
/// The raw, per-run measurements the harness collects around one execution of an <c>IBenchmarkTarget</c>,
/// fed verbatim into the pure <see cref="MetricsAggregator"/>. Throughput comes from <see cref="JobCount"/>
/// over <see cref="Window"/>; the latency lists are per-job durations the aggregator turns into percentiles.
/// </summary>
/// <param name="JobCount">Number of jobs that completed in the measured window.</param>
/// <param name="Window">Wall-clock measurement window (drain time, or the sustained steady-state window).</param>
/// <param name="EndToEndLatencies">Per-job enqueue→terminal durations.</param>
/// <param name="EnqueueLatencies">Per-job enqueue call→committed durations.</param>
public readonly record struct RunSamples(
    int JobCount,
    TimeSpan Window,
    IReadOnlyList<TimeSpan> EndToEndLatencies,
    IReadOnlyList<TimeSpan> EnqueueLatencies);
