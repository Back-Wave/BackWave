using BackWave.Benchmarks.Metrics;

namespace BackWave.Benchmarks.Tests;

/// <summary>
/// The highest-value tests in the harness: a silent percentile or throughput bug would corrupt every
/// published number (ADR 0027 §4). These exercise the pure math against known fixtures and edge cases.
/// </summary>
public sealed class MetricsAggregatorTests
{
    [Fact]
    public void Throughput_is_jobs_over_window_seconds()
    {
        Assert.Equal(1000d, MetricsAggregator.Throughput(2000, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void Throughput_is_zero_for_a_nonpositive_window()
    {
        Assert.Equal(0d, MetricsAggregator.Throughput(2000, TimeSpan.Zero));
        Assert.Equal(0d, MetricsAggregator.Throughput(2000, TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void Percentile_of_1_to_100_uses_nearest_rank()
    {
        var samples = Enumerable.Range(1, 100).Select(n => TimeSpan.FromMilliseconds(n)).ToArray();
        Assert.Equal(TimeSpan.FromMilliseconds(50), MetricsAggregator.Percentile(samples, 50));
        Assert.Equal(TimeSpan.FromMilliseconds(99), MetricsAggregator.Percentile(samples, 99));
        Assert.Equal(TimeSpan.FromMilliseconds(1), MetricsAggregator.Percentile(samples, 1));
        Assert.Equal(TimeSpan.FromMilliseconds(100), MetricsAggregator.Percentile(samples, 100));
    }

    [Fact]
    public void Percentile_does_not_depend_on_input_order()
    {
        var ascending = new[] { 1, 2, 3, 4, 5 }.Select(n => TimeSpan.FromMilliseconds(n)).ToArray();
        var shuffled = new[] { 4, 1, 5, 3, 2 }.Select(n => TimeSpan.FromMilliseconds(n)).ToArray();
        Assert.Equal(
            MetricsAggregator.Percentile(ascending, 50),
            MetricsAggregator.Percentile(shuffled, 50));
    }

    [Fact]
    public void Percentile_of_empty_set_is_zero()
    {
        Assert.Equal(TimeSpan.Zero, MetricsAggregator.Percentile([], 50));
        Assert.Equal(TimeSpan.Zero, MetricsAggregator.Percentile([], 99));
    }

    [Fact]
    public void Percentile_of_single_sample_is_that_sample()
    {
        var single = new[] { TimeSpan.FromMilliseconds(42) };
        Assert.Equal(TimeSpan.FromMilliseconds(42), MetricsAggregator.Percentile(single, 50));
        Assert.Equal(TimeSpan.FromMilliseconds(42), MetricsAggregator.Percentile(single, 99));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(101)]
    public void Percentile_rejects_out_of_range(double percentile)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => MetricsAggregator.Percentile([TimeSpan.FromMilliseconds(1)], percentile));
    }

    [Fact]
    public void Distribute_reports_min_median_max_for_odd_count()
    {
        var dist = MetricsAggregator.Distribute([30d, 10d, 20d]);
        Assert.Equal(10d, dist.Min);
        Assert.Equal(20d, dist.Median);
        Assert.Equal(30d, dist.Max);
    }

    [Fact]
    public void Distribute_averages_the_two_middle_values_for_even_count()
    {
        var dist = MetricsAggregator.Distribute([10d, 20d, 30d, 40d]);
        Assert.Equal(10d, dist.Min);
        Assert.Equal(25d, dist.Median);
        Assert.Equal(40d, dist.Max);
    }

    [Fact]
    public void Distribute_of_empty_is_all_zero()
    {
        var dist = MetricsAggregator.Distribute([]);
        Assert.Equal(default, dist);
    }

    [Fact]
    public void DiscardWarmup_drops_the_leading_runs()
    {
        IReadOnlyList<int> runs = [1, 2, 3, 4, 5];
        Assert.Equal([3, 4, 5], MetricsAggregator.DiscardWarmup(runs, 2));
    }

    [Fact]
    public void DiscardWarmup_returns_empty_when_warmup_covers_every_run()
    {
        IReadOnlyList<int> runs = [1, 2];
        Assert.Empty(MetricsAggregator.DiscardWarmup(runs, 5));
    }

    [Fact]
    public void Aggregate_produces_throughput_and_percentile_pairs()
    {
        var e2e = Enumerable.Range(1, 100).Select(n => TimeSpan.FromMilliseconds(n)).ToArray();
        var enqueue = Enumerable.Range(1, 100).Select(n => TimeSpan.FromMilliseconds(n / 10d)).ToArray();
        var samples = new RunSamples(1000, TimeSpan.FromSeconds(1), e2e, enqueue);

        var metrics = MetricsAggregator.Aggregate(samples);

        Assert.Equal(1000d, metrics.ThroughputJobsPerSecond);
        Assert.Equal(TimeSpan.FromMilliseconds(50), metrics.EndToEndP50);
        Assert.Equal(TimeSpan.FromMilliseconds(99), metrics.EndToEndP99);
    }
}
