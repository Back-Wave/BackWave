namespace BackWave.Benchmarks.Metrics;

/// <summary>
/// Pure math over the raw run samples — the highest-value module to test, because a silent percentile or
/// throughput bug would corrupt every published number (ADR 0027 §4). No I/O, no clock, no randomness.
/// </summary>
public static class MetricsAggregator
{
    /// <summary>
    /// Aggregates one run's samples into throughput and the p50/p99 latency pairs. An empty latency list
    /// yields <see cref="TimeSpan.Zero"/> percentiles; a zero/negative window yields zero throughput
    /// (degenerate, but never a divide-by-zero).
    /// </summary>
    public static RunMetrics Aggregate(RunSamples samples)
        => new()
        {
            ThroughputJobsPerSecond = Throughput(samples.JobCount, samples.Window),
            EndToEndP50 = Percentile(samples.EndToEndLatencies, 50),
            EndToEndP99 = Percentile(samples.EndToEndLatencies, 99),
            EnqueueP50 = Percentile(samples.EnqueueLatencies, 50),
            EnqueueP99 = Percentile(samples.EnqueueLatencies, 99),
        };

    /// <summary>Jobs/second over a window. Returns 0 when the window is non-positive.</summary>
    public static double Throughput(int jobCount, TimeSpan window)
        => window.TotalSeconds > 0 ? jobCount / window.TotalSeconds : 0d;

    /// <summary>
    /// The <paramref name="percentile"/>-th percentile of a duration sample, by the nearest-rank method on
    /// a sorted copy: rank = ceil(p/100 · N), clamped to [1, N]. p50 of 1..100 is 50; p99 is 99. An empty
    /// sample returns <see cref="TimeSpan.Zero"/>; a single sample returns that sample for any percentile.
    /// </summary>
    /// <param name="samples">The unsorted duration sample (not mutated).</param>
    /// <param name="percentile">A percentile in (0, 100].</param>
    public static TimeSpan Percentile(IReadOnlyList<TimeSpan> samples, double percentile)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (percentile is <= 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(percentile), percentile, "Percentile must be in the interval (0, 100].");
        }

        if (samples.Count == 0)
        {
            return TimeSpan.Zero;
        }

        var sorted = samples.ToArray();
        Array.Sort(sorted);

        // Nearest-rank: 1-based rank = ceil(p/100 · N), clamped into the array.
        var rank = (int)Math.Ceiling(percentile / 100d * sorted.Length);
        rank = Math.Clamp(rank, 1, sorted.Length);
        return sorted[rank - 1];
    }

    /// <summary>
    /// The min/median/max distribution across a set of per-run values (e.g. each run's throughput), with
    /// warmup already discarded by the caller. Median uses the average of the two middle values for an
    /// even count. An empty input yields an all-zero distribution.
    /// </summary>
    public static Distribution Distribute(IReadOnlyList<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0)
        {
            return default;
        }

        var sorted = values.ToArray();
        Array.Sort(sorted);

        var n = sorted.Length;
        var median = n % 2 == 1
            ? sorted[n / 2]
            : (sorted[(n / 2) - 1] + sorted[n / 2]) / 2d;

        return new Distribution(sorted[0], median, sorted[n - 1]);
    }

    /// <summary>
    /// Discards the first <paramref name="warmupRuns"/> entries (JIT, connection-pool fill, DB cache warm)
    /// and returns the rest as the measured set. If warmup ≥ count, returns an empty set (nothing measured).
    /// </summary>
    public static IReadOnlyList<T> DiscardWarmup<T>(IReadOnlyList<T> runs, int warmupRuns)
    {
        ArgumentNullException.ThrowIfNull(runs);
        if (warmupRuns <= 0)
        {
            return runs;
        }

        return warmupRuns >= runs.Count ? [] : runs.Skip(warmupRuns).ToArray();
    }
}
