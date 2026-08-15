namespace BackWave.Benchmarks.Targets;

/// <summary>
/// The resource-cost metrics captured around one timed window, so no throughput number is ever published
/// naked (ADR 0027 §4). <see cref="PeakConnections"/> and <see cref="CpuPercent"/> are measured identically
/// for any target, so they are fair cross-system comparisons; <see cref="Internal"/> holds BackWave-only
/// allocation/GC counters that are process-wide and must never be charted against a competitor.
/// </summary>
/// <param name="PeakConnections">Peak concurrent DB connections held by the process while the window was open.</param>
/// <param name="CpuPercent">CPU utilization over the window: process CPU time / (wall window × core count), as a percentage.</param>
/// <param name="Internal">BackWave-only allocation/GC counters; never a cross-system comparison cell.</param>
public readonly record struct ResourceMetrics(
    int PeakConnections,
    double CpuPercent,
    InternalAllocationMetrics Internal)
{
    /// <summary>The all-zero metrics, used before any run has measured a window.</summary>
    public static ResourceMetrics None => new(0, 0d, default);
}

/// <summary>
/// Allocation and GC counters for a single run. These are <strong>internal-only</strong>: GC statistics are
/// process-wide and a head-to-head allocation chart against a reflection-JSON competitor is apples-to-oranges
/// (ADR 0027 §4), so they must never be emitted as a cross-system comparison cell. The <see cref="Scope"/>
/// marker rides along in the JSON to make that unmistakable.
/// </summary>
/// <param name="AllocatedBytesPerJob">Managed bytes allocated during the window, divided by the job count.</param>
/// <param name="Gen0Collections">Gen-0 GC collections during the window.</param>
/// <param name="Gen1Collections">Gen-1 GC collections during the window.</param>
/// <param name="Gen2Collections">Gen-2 GC collections during the window.</param>
public readonly record struct InternalAllocationMetrics(
    long AllocatedBytesPerJob,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections)
{
    /// <summary>Always "internal-only" — marks these counters as not comparable across systems.</summary>
    public string Scope => "internal-only";
}
