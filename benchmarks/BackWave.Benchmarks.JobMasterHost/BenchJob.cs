namespace BackWave.Benchmarks.Workload;

/// <summary>
/// The benchmark job shape. In the harness this record is emitted by BackWave's source generator from the
/// <c>bench</c> job handler; the host has no BackWave reference, so it declares the same shape by hand for
/// the linked <see cref="WorkloadSpec"/> and <see cref="ParallelProducer"/> sources.
/// </summary>
/// <param name="Payload">The fixed-size filler string.</param>
/// <param name="DelayMs">The handler delay in whole milliseconds.</param>
public sealed record BenchJob(string Payload, int DelayMs);
