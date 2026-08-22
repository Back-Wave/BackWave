using System.Text.Json;
using System.Text.Json.Serialization;
using BackWave.Benchmarks;
using BackWave.Benchmarks.Environment;
using BackWave.Benchmarks.Latency;
using BackWave.Benchmarks.ScaleOut;
using BackWave.Benchmarks.Targets;
using BackWave.Benchmarks.Workload;

// The Benchmark Harness console (ADR 0027). It drives the real Shell pump against a real Storage Adapter
// under wall-clock time and writes a self-labelled JSON result with a `publishable` flag. It is NOT a test
// and is never run by `dotnet test`.
//
// Usage:
//   dotnet run -c Release --project benchmarks/BackWave.Benchmarks -- [options]
//
//   --target  postgres|sqlserver|oracle|hangfire-postgres|hangfire-sqlserver
//                                  system under test                    (default: postgres)
//   --mode    local|official       run mode; only official+native-x64 is publishable (default: local)
//   --jobs    N                    number of jobs in the stream         (default: 10000)
//   --arrival drain|sustained      workload arrival shape               (default: drain)
//   --delay-ms M                   per-job handler delay (0 = noop ceiling, ~10 = realistic anchor)
//   --rate    R                    sustained enqueue rate, jobs/sec     (default: 0 = unpaced)
//   --producers N                  concurrent producer tasks            (default: 1; raise for sustained)
//   --pool-size N                  per-node max concurrent jobs         (default: target's built-in tuning)
//   --worker-groups N              in-process pumps on the same queue   (default: 1)
//   --payload-bytes B              fixed payload size band              (default: 128)
//   --warmup  W                    warmup runs discarded                (default: 1)
//   --runs    N                    measured runs reported as min/median/max distribution (default: 3)
//   --out     path.json            write result JSON to a file          (default: stdout only)
//   --rtt-ms  D                    latency profile: add D ms to every database round trip
//                                  (default: 0 = off; DIAGNOSTIC only, refused in official mode)

// Scale-out curve + per-Node subprocess (bench-0141). Both use flags the single-run parser does not know,
// so they are dispatched before BenchmarkOptions.Parse: a `--node` process runs only a pump and drains the
// shared backlog; `--scale-out 1,2,4,8` is the parent that spawns those Node processes and charts aggregate
// throughput as the Node count rises, to the DB-saturation knee.
if (Array.IndexOf(args, "--node") >= 0)
{
    return await NodeRunner.RunAsync(args, CancellationToken.None);
}

if (Array.IndexOf(args, "--scale-out") >= 0)
{
    return await ScaleOutCommand.RunAsync(args, CancellationToken.None);
}

var options = BenchmarkOptions.Parse(args);

// Official mode refuses up front on a non-native-x86-64 host (e.g. Apple Silicon / Rosetta) rather than
// running to a silently-unpublishable number (ADR 0027 §8). Local mode is never gated. The refusal is a
// clean stderr message + non-zero exit, not a stack trace — it is expected operator feedback.
try
{
    OfficialModeGuard.Assert(options.Mode, options.Latency);
}
catch (OfficialModeNotSupportedException refusal)
{
    Console.Error.WriteLine($"official mode refused: {refusal.Message}");
    return 2;
}

var spec = new WorkloadSpec
{
    JobCount = options.JobCount,
    HandlerDelay = TimeSpan.FromMilliseconds(options.DelayMs),
    Arrival = options.Arrival,
    SustainedRatePerSecond = options.Rate,
    ProducerCount = options.Producers,
    PumpPoolSize = options.PoolSize,
    WorkerGroupCount = options.WorkerGroups,
    PayloadSizeBytes = options.PayloadBytes,
};

// The latency profile (bench-0265). Off by default: with the dial at 0 nothing is started and no
// connection string is touched, so the official path is byte-for-byte the path it always was. Engaged, it
// stands a loopback proxy in front of the database and rewrites the target's DSN to point at it, BEFORE the
// target is constructed and reads that DSN. The delay lands on the wire, so it prices round TRIPS - the
// thing that differs by adapter - rather than store calls.
LatencyProxy? installed;
try
{
    installed = LatencyProfileInstaller.Install(options.Target, options.Latency);
}
catch (InvalidOperationException refusal)
{
    // Expected operator feedback, like the official-mode refusal above: a message and an exit code rather
    // than a stack trace.
    Console.Error.WriteLine($"latency profile refused: {refusal.Message}");
    return 2;
}

await using var latencyProxy = installed;
if (options.Latency.IsEngaged)
{
    Console.Error.WriteLine(
        $"LATENCY PROFILE ENGAGED: +{options.Latency.RoundTripMs:0}ms on every database round trip. " +
        "This is a DIAGNOSTIC run. Its numbers are never published and never enter the result matrix.");
}

await using var target = BenchmarkTargetRegistry.Create(options.Target);

Console.Error.WriteLine(
    $"Running {target.Name} | {spec.Arrival} | {spec.JobCount} jobs | {spec.DelayMs}ms handler | " +
    $"{options.WarmupRuns} warmup + {options.MeasuredRuns} runs | mode={options.Mode} | " +
    $"rtt-delay={options.Latency.RoundTripMs:0}ms");

var orchestrator = new RunOrchestrator(target, options.Mode, options.Latency);
var result = await orchestrator.RunAsync(spec, options.WarmupRuns, options.MeasuredRuns, CancellationToken.None);

var json = JsonSerializer.Serialize(result, JsonOptions());
Console.WriteLine(json);
if (options.OutPath is { } path)
{
    await File.WriteAllTextAsync(path, json);
    Console.Error.WriteLine($"Wrote result to {path}");
}

Console.Error.WriteLine(
    $"throughput jobs/sec  min={result.ThroughputJobsPerSecond.Min:N0}  " +
    $"median={result.ThroughputJobsPerSecond.Median:N0}  max={result.ThroughputJobsPerSecond.Max:N0}  " +
    $"publishable={result.Publishable}  diagnostic={result.Diagnostic}");

if (latencyProxy is not null)
{
    // The task timer overshoots a short wait, so report what the proxy actually held segments for rather
    // than leaving the reader to assume the setting was delivered exactly.
    Console.Error.WriteLine(
        $"latency profile  requested={options.Latency.RoundTripMs:0}ms  " +
        $"achieved-mean={latencyProxy.MeanHold.TotalMilliseconds:N3}ms  " +
        $"held-segments={latencyProxy.HeldSegments:N0}");
}

Console.Error.WriteLine(
    "tuning dials  " + string.Join("  ", result.TuningDials.Select(d => $"{d.Key}={d.Value}")));

Console.Error.WriteLine(
    $"resources  peak-connections={result.Resources.PeakConnections}  " +
    $"cpu={result.Resources.CpuPercent:N1}%  " +
    $"alloc/job={result.Resources.Internal.AllocatedBytesPerJob:N0}B (internal-only)  " +
    $"gc-gen0/1/2={result.Resources.Internal.Gen0Collections}/" +
    $"{result.Resources.Internal.Gen1Collections}/{result.Resources.Internal.Gen2Collections}");

return 0;

static JsonSerializerOptions JsonOptions() => new()
{
    WriteIndented = true,
    Converters = { new JsonStringEnumConverter() },
};
