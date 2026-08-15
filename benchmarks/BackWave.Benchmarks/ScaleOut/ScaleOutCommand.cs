using System.Text.Json;
using System.Text.Json.Serialization;
using BackWave.Benchmarks.Environment;
using BackWave.Benchmarks.Targets;
using BackWave.Benchmarks.Workload;

namespace BackWave.Benchmarks.ScaleOut;

/// <summary>
/// The <c>--scale-out</c> entry point (bench-0141): parses the sweep flags, runs the curve against the shared
/// Postgres database, writes the JSON result, and prints a stderr summary table with the saturation knee. Kept
/// separate from the single-run console so the harness's main option parser stays untouched.
/// </summary>
internal static class ScaleOutCommand
{
    private const string DefaultConnectionString =
        "Host=localhost;Port=5499;Username=backwave;Password=backwave;Database=backwave_test";

    /// <summary>Parses the scale-out flags, runs the sweep, emits the result, and returns an exit code.</summary>
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        var (nodeCounts, jobCount, delayMs, payloadBytes, mode, outPath) = ParseArgs(args);

        var spec = new WorkloadSpec
        {
            JobCount = jobCount,
            HandlerDelay = TimeSpan.FromMilliseconds(delayMs),
            // Scale-out is a drain story: a fixed backlog is preloaded, then the cluster empties it.
            Arrival = ArrivalMode.Drain,
            PayloadSizeBytes = payloadBytes,
        };

        var connectionString =
            System.Environment.GetEnvironmentVariable(PostgresBenchmarkTarget.ConnectionStringEnvVar)
            ?? DefaultConnectionString;

        Console.Error.WriteLine(
            $"Scale-out sweep {string.Join("→", nodeCounts)} nodes | {spec.JobCount} jobs | " +
            $"{spec.DelayMs}ms handler | mode={mode}");

        var orchestrator = new ScaleOutOrchestrator(connectionString, spec, nodeCounts, mode);
        var result = await orchestrator.RunAsync(cancellationToken).ConfigureAwait(false);

        var json = JsonSerializer.Serialize(result, JsonOptions());
        Console.WriteLine(json);
        if (outPath is not null)
        {
            await File.WriteAllTextAsync(outPath, json, cancellationToken).ConfigureAwait(false);
            Console.Error.WriteLine($"Wrote result to {outPath}");
        }

        Console.Error.WriteLine(
            $"peak {result.PeakThroughputJobsPerSecond:N0} jobs/sec at {result.PeakAtNodeCount} node(s)  |  " +
            $"knee at {result.KneeAtNodeCount} node(s)  |  " +
            $"saturated={result.SaturationReached}  |  publishable={result.Publishable}");

        return 0;
    }

    private static (IReadOnlyList<int> NodeCounts, int JobCount, int DelayMs, int PayloadBytes, RunMode Mode, string? OutPath)
        ParseArgs(string[] args)
    {
        IReadOnlyList<int>? nodeCounts = null;
        var jobCount = 10_000;
        var delayMs = 0;
        var payloadBytes = 128;
        var mode = RunMode.Local;
        string? outPath = null;

        for (var i = 0; i < args.Length - 1; i += 2)
        {
            var key = args[i];
            var value = args[i + 1];
            switch (key)
            {
                case "--scale-out":
                    nodeCounts = NodeCountList.Parse(value);
                    break;
                case "--jobs":
                    jobCount = int.Parse(value);
                    break;
                case "--delay-ms":
                    delayMs = int.Parse(value);
                    break;
                case "--payload-bytes":
                    payloadBytes = int.Parse(value);
                    break;
                case "--mode":
                    mode = ParseMode(value);
                    break;
                case "--out":
                    outPath = value;
                    break;
                default:
                    throw new ArgumentException($"Unknown scale-out option '{key}'.");
            }
        }

        if (nodeCounts is null)
        {
            throw new ArgumentException("Scale-out requires --scale-out with a node-count list like '1,2,4,8'.");
        }

        return (nodeCounts, jobCount, delayMs, payloadBytes, mode, outPath);
    }

    private static RunMode ParseMode(string value) => value.ToLowerInvariant() switch
    {
        "local" => RunMode.Local,
        "official" => RunMode.Official,
        _ => throw new ArgumentException($"Unknown mode '{value}'. Expected 'local' or 'official'."),
    };

    private static JsonSerializerOptions JsonOptions() => new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };
}
