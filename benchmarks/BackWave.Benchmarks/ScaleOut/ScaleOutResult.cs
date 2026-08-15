using BackWave.Benchmarks.Environment;
using BackWave.Benchmarks.Workload;

namespace BackWave.Benchmarks.ScaleOut;

/// <summary>
/// The machine-readable result of one scale-out sweep (bench-0141): the workload, the environment manifest,
/// one aggregate-throughput point per Node count, and the derived plateau / DB-saturation knee. BackWave-only
/// — it demonstrates the database-authoritative stateless-peer scale-out (ADR 0006), not a competitor
/// comparison. Serialized to JSON so the curve can be charted without re-running.
/// </summary>
public sealed record ScaleOutResult
{
    /// <summary>The system+adapter that produced this curve, e.g. "BackWave/Postgres".</summary>
    public required string Target { get; init; }

    /// <summary>The storage engine under test.</summary>
    public required string Engine { get; init; }

    /// <summary>The workload each point ran.</summary>
    public required WorkloadSummary Workload { get; init; }

    /// <summary>The environment stamp + publishable flag.</summary>
    public required EnvironmentManifest Manifest { get; init; }

    /// <summary>One aggregate-throughput point per swept Node count, in sweep order.</summary>
    public required IReadOnlyList<ScaleOutPoint> Points { get; init; }

    /// <summary>The highest aggregate throughput (jobs/sec) observed across the swept Node counts.</summary>
    public required double PeakThroughputJobsPerSecond { get; init; }

    /// <summary>The Node count at which <see cref="PeakThroughputJobsPerSecond"/> was observed.</summary>
    public required int PeakAtNodeCount { get; init; }

    /// <summary>
    /// The Node count identified as the throughput plateau / DB-saturation knee: the first count beyond which
    /// adding Nodes raised aggregate throughput by less than 10%. Equals the last swept count when no plateau
    /// was reached within the swept range (see <see cref="SaturationReached"/>).
    /// </summary>
    public required int KneeAtNodeCount { get; init; }

    /// <summary>
    /// True when a plateau was detected within the swept range (throughput stopped rising meaningfully);
    /// false when throughput was still climbing at the last swept Node count, so the knee lies beyond it.
    /// </summary>
    public required bool SaturationReached { get; init; }

    /// <summary>Whether this curve may be published (mirrors <see cref="EnvironmentManifest.Publishable"/>).</summary>
    public bool Publishable => Manifest.Publishable;

    // The plateau heuristic: a per-step throughput gain below this fraction marks the saturation knee.
    private const double PlateauGainThreshold = 0.10;

    /// <summary>
    /// Assembles a result from the measured points, deriving the peak and the saturation knee.
    /// </summary>
    /// <param name="target">The system+adapter name.</param>
    /// <param name="engine">The storage engine name.</param>
    /// <param name="spec">The workload each point ran.</param>
    /// <param name="manifest">The captured environment stamp.</param>
    /// <param name="points">The measured points, in sweep order; must be non-empty.</param>
    /// <returns>The assembled <see cref="ScaleOutResult"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="points"/> is empty.</exception>
    public static ScaleOutResult From(
        string target,
        string engine,
        WorkloadSpec spec,
        EnvironmentManifest manifest,
        IReadOnlyList<ScaleOutPoint> points)
    {
        if (points.Count == 0)
        {
            throw new ArgumentException("A scale-out curve needs at least one measured point.", nameof(points));
        }

        var peak = points[0];
        foreach (var point in points)
        {
            if (point.ThroughputJobsPerSecond > peak.ThroughputJobsPerSecond)
            {
                peak = point;
            }
        }

        var kneeNodeCount = points[^1].NodeCount;
        var saturationReached = false;
        for (var i = 0; i < points.Count - 1; i++)
        {
            var current = points[i].ThroughputJobsPerSecond;
            var next = points[i + 1].ThroughputJobsPerSecond;
            var gain = current > 0 ? (next - current) / current : double.PositiveInfinity;
            if (gain < PlateauGainThreshold)
            {
                kneeNodeCount = points[i].NodeCount;
                saturationReached = true;
                break;
            }
        }

        return new ScaleOutResult
        {
            Target = target,
            Engine = engine,
            Workload = WorkloadSummary.From(spec),
            Manifest = manifest,
            Points = points,
            PeakThroughputJobsPerSecond = peak.ThroughputJobsPerSecond,
            PeakAtNodeCount = peak.NodeCount,
            KneeAtNodeCount = kneeNodeCount,
            SaturationReached = saturationReached,
        };
    }
}

/// <summary>One point on the scale-out curve: the aggregate throughput a given number of Node processes
/// reached draining the shared backlog.</summary>
public sealed record ScaleOutPoint
{
    /// <summary>The number of Node processes that ran concurrently for this point.</summary>
    public required int NodeCount { get; init; }

    /// <summary>The preloaded backlog size all the Nodes drained together.</summary>
    public required int JobCount { get; init; }

    /// <summary>
    /// The number of jobs cleared during the timed window: the backlog still pending when the window opened,
    /// after the readiness barrier. Fast Nodes may drain part of <see cref="JobCount"/> while slower Nodes are
    /// still starting, so this — not <see cref="JobCount"/> — is the throughput numerator.
    /// </summary>
    public required int ProcessedInWindow { get; init; }

    /// <summary>Wall-clock seconds from every Node being ready (claiming) to the shared backlog draining empty.</summary>
    public required double WindowSeconds { get; init; }

    /// <summary>Aggregate throughput (<see cref="ProcessedInWindow"/> / <see cref="WindowSeconds"/>), jobs/sec.</summary>
    public required double ThroughputJobsPerSecond { get; init; }
}
