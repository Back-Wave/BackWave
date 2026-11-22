namespace BackWave.Benchmarks.Workload;

/// <summary>The benchmark job shape that the linked <see cref="WorkloadSpec"/> and <see cref="ParallelProducer"/> sources use.</summary>
/// <param name="Payload">The fixed-size filler string.</param>
/// <param name="DelayMs">The handler delay in whole milliseconds.</param>
public sealed record BenchJob(string Payload, int DelayMs);
