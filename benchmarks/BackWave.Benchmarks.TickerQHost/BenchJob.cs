namespace BackWave.Benchmarks.Workload;

/// <summary>The benchmark job payload, serialized into each ticker row as the TickerQ request.</summary>
/// <param name="Payload">The fixed-size filler string.</param>
/// <param name="DelayMs">The handler delay in whole milliseconds.</param>
public sealed record BenchJob(string Payload, int DelayMs);
