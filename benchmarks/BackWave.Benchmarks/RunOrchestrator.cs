using System.Diagnostics;
using BackWave.Benchmarks.Environment;
using BackWave.Benchmarks.Metrics;
using BackWave.Benchmarks.Targets;
using BackWave.Benchmarks.Workload;

namespace BackWave.Benchmarks;

/// <summary>
/// Drives one target through warmup + N measured runs and assembles the <see cref="BenchmarkResult"/>.
/// The timed window — the one number that must be measured identically for every system — is taken here,
/// outside the target, with a single <see cref="Stopwatch"/> around <see cref="IBenchmarkTarget.ExecuteAsync"/>
/// (ADR 0027 §5). Warm/cooldown bracket that stopwatch so host spin-up and graceful shutdown stay out of the
/// denominator (ADR 0027 §2).
/// </summary>
public sealed class RunOrchestrator
{
    private readonly IBenchmarkTarget _target;
    private readonly RunMode _mode;

    /// <summary>Creates an orchestrator for one target in one run mode.</summary>
    public RunOrchestrator(IBenchmarkTarget target, RunMode mode)
    {
        _target = target;
        _mode = mode;
    }

    /// <summary>
    /// Runs the full battery: setup once, then <paramref name="warmupRuns"/> discarded runs followed by
    /// <paramref name="measuredRuns"/> measured ones, and reports the cross-run distributions.
    /// </summary>
    public async Task<BenchmarkResult> RunAsync(
        WorkloadSpec spec, int warmupRuns, int measuredRuns, CancellationToken cancellationToken)
    {
        var totalRuns = warmupRuns + measuredRuns;
        var version = await _target.SetupAsync(cancellationToken).ConfigureAwait(false);
        var manifest = EnvironmentManifest.Capture(_mode, _target.Engine, version);

        var perRun = new List<RunOutcome>(totalRuns);
        try
        {
            for (var run = 0; run < totalRuns; run++)
            {
                perRun.Add(await RunOnceAsync(spec, cancellationToken).ConfigureAwait(false));
            }
        }
        finally
        {
            await _target.TeardownAsync(cancellationToken).ConfigureAwait(false);
        }

        var measured = MetricsAggregator.DiscardWarmup(perRun, warmupRuns);
        var metrics = measured.Select(o => o.Metrics).ToArray();

        return new BenchmarkResult
        {
            Target = _target.Name,
            Engine = _target.Engine,
            Workload = WorkloadSummary.From(spec),
            Manifest = manifest,
            TuningDials = _target.TuningDials,
            WarmupRuns = warmupRuns,
            MeasuredRuns = measured.Count,
            ThroughputJobsPerSecond = MetricsAggregator.Distribute(Project(metrics, m => m.ThroughputJobsPerSecond)),
            EndToEndP50Ms = MetricsAggregator.Distribute(Project(metrics, m => m.EndToEndP50.TotalMilliseconds)),
            EndToEndP99Ms = MetricsAggregator.Distribute(Project(metrics, m => m.EndToEndP99.TotalMilliseconds)),
            EnqueueP50Ms = MetricsAggregator.Distribute(Project(metrics, m => m.EnqueueP50.TotalMilliseconds)),
            EnqueueP99Ms = MetricsAggregator.Distribute(Project(metrics, m => m.EnqueueP99.TotalMilliseconds)),
            Resources = AggregateResources(measured.Select(o => o.Resources).ToArray()),
        };
    }

    private async Task<RunOutcome> RunOnceAsync(WorkloadSpec spec, CancellationToken cancellationToken)
    {
        await _target.ResetAsync(cancellationToken).ConfigureAwait(false);
        await _target.PreloadAsync(spec, cancellationToken).ConfigureAwait(false);
        await _target.WarmAsync(spec, cancellationToken).ConfigureAwait(false);

        var stopwatch = Stopwatch.StartNew();
        await _target.ExecuteAsync(spec, cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();

        await _target.CooldownAsync(cancellationToken).ConfigureAwait(false);

        var samples = await _target.CollectSamplesAsync(cancellationToken).ConfigureAwait(false);
        var metrics = MetricsAggregator.Aggregate(new RunSamples(
            spec.JobCount, stopwatch.Elapsed, samples.EndToEndLatencies, samples.EnqueueLatencies));
        return new RunOutcome(metrics, samples.Resources);
    }

    private static IReadOnlyList<double> Project(IReadOnlyList<RunMetrics> runs, Func<RunMetrics, double> select)
        => runs.Select(select).ToArray();

    /// <summary>
    /// Collapses the per-run resource metrics into one representative figure. Peak connections takes the
    /// max (a peak across runs is still a peak); CPU and the internal-only allocation/GC counters take the
    /// median, the same warmup-discarded summary the throughput distribution uses.
    /// </summary>
    private static ResourceMetrics AggregateResources(IReadOnlyList<ResourceMetrics> runs)
    {
        if (runs.Count == 0)
        {
            return ResourceMetrics.None;
        }

        var peakConnections = runs.Max(r => r.PeakConnections);
        var cpuPercent = MetricsAggregator.Distribute(runs.Select(r => r.CpuPercent).ToArray()).Median;
        var allocatedPerJob = (long)MetricsAggregator.Distribute(
            runs.Select(r => (double)r.Internal.AllocatedBytesPerJob).ToArray()).Median;
        var gen0 = (int)MetricsAggregator.Distribute(runs.Select(r => (double)r.Internal.Gen0Collections).ToArray()).Median;
        var gen1 = (int)MetricsAggregator.Distribute(runs.Select(r => (double)r.Internal.Gen1Collections).ToArray()).Median;
        var gen2 = (int)MetricsAggregator.Distribute(runs.Select(r => (double)r.Internal.Gen2Collections).ToArray()).Median;

        return new ResourceMetrics(
            peakConnections, cpuPercent, new InternalAllocationMetrics(allocatedPerJob, gen0, gen1, gen2));
    }

    /// <summary>One run's aggregated latency/throughput metrics plus the resource costs measured alongside it.</summary>
    private readonly record struct RunOutcome(RunMetrics Metrics, ResourceMetrics Resources);
}
