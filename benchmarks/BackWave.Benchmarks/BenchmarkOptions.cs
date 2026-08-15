using BackWave.Benchmarks.Environment;
using BackWave.Benchmarks.Workload;

namespace BackWave.Benchmarks;

/// <summary>Parsed command-line options for one benchmark invocation.</summary>
public sealed record BenchmarkOptions
{
    /// <summary>Storage adapter under test ("postgres", "sqlserver").</summary>
    public string Target { get; init; } = "postgres";

    /// <summary>Run mode; only official on native-x64 yields a publishable number.</summary>
    public RunMode Mode { get; init; } = RunMode.Local;

    /// <summary>Number of jobs in the stream.</summary>
    public int JobCount { get; init; } = 10_000;

    /// <summary>Workload arrival shape.</summary>
    public ArrivalMode Arrival { get; init; } = ArrivalMode.Drain;

    /// <summary>Per-job handler delay in milliseconds (0 = noop ceiling).</summary>
    public int DelayMs { get; init; }

    /// <summary>Sustained enqueue rate, jobs/sec (0 = unpaced).</summary>
    public double Rate { get; init; }

    /// <summary>
    /// Concurrent producer tasks enqueuing the stream. 1 (default) keeps drain preload a clean isolated
    /// enqueue-latency measurement; sustained mode wants several so the producer never caps throughput.
    /// </summary>
    public int Producers { get; init; } = 1;

    /// <summary>
    /// Per-node worker pool size (max concurrent jobs). When null the BackWave target uses its built-in
    /// aggressive default; set it to sweep the dial and find where throughput plateaus for a given handler.
    /// </summary>
    public int? PoolSize { get; init; }

    /// <summary>
    /// Number of independent worker-group pumps to run in the single benchmark process, all serving the
    /// same queue. Defaults to 1; raise it to measure whether in-process pump fan-out scales throughput the
    /// way multi-process scale-out does.
    /// </summary>
    public int WorkerGroups { get; init; } = 1;

    /// <summary>Fixed payload size band, in bytes.</summary>
    public int PayloadBytes { get; init; } = 128;

    /// <summary>Warmup runs discarded before measurement.</summary>
    public int WarmupRuns { get; init; } = 1;

    /// <summary>Measured runs reported as a distribution.</summary>
    public int MeasuredRuns { get; init; } = 3;

    /// <summary>Optional path to write the result JSON to.</summary>
    public string? OutPath { get; init; }

    /// <summary>Parses harness options from <c>--key value</c> arguments, applying defaults for omitted keys.</summary>
    public static BenchmarkOptions Parse(string[] args)
    {
        var result = new BenchmarkOptions();
        for (var i = 0; i < args.Length - 1; i += 2)
        {
            var key = args[i];
            var value = args[i + 1];
            result = key switch
            {
                "--target" => result with { Target = value.ToLowerInvariant() },
                "--mode" => result with { Mode = ParseMode(value) },
                "--jobs" => result with { JobCount = int.Parse(value) },
                "--arrival" => result with { Arrival = ParseArrival(value) },
                "--delay-ms" => result with { DelayMs = int.Parse(value) },
                "--rate" => result with { Rate = double.Parse(value) },
                "--producers" => result with { Producers = int.Parse(value) },
                "--pool-size" => result with { PoolSize = int.Parse(value) },
                "--worker-groups" => result with { WorkerGroups = int.Parse(value) },
                "--payload-bytes" => result with { PayloadBytes = int.Parse(value) },
                "--warmup" => result with { WarmupRuns = int.Parse(value) },
                "--runs" => result with { MeasuredRuns = int.Parse(value) },
                "--out" => result with { OutPath = value },
                _ => throw new ArgumentException($"Unknown option '{key}'."),
            };
        }

        return result;
    }

    private static RunMode ParseMode(string value) => value.ToLowerInvariant() switch
    {
        "local" => RunMode.Local,
        "official" => RunMode.Official,
        _ => throw new ArgumentException($"Unknown mode '{value}'. Expected 'local' or 'official'."),
    };

    private static ArrivalMode ParseArrival(string value) => value.ToLowerInvariant() switch
    {
        "drain" => ArrivalMode.Drain,
        "sustained" => ArrivalMode.Sustained,
        _ => throw new ArgumentException($"Unknown arrival '{value}'. Expected 'drain' or 'sustained'."),
    };
}
