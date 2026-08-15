using System.Diagnostics;
using BackWave.Benchmarks.Environment;
using BackWave.Benchmarks.Targets;
using BackWave.Benchmarks.Workload;
using BackWave.Jobs;
using BackWave.Postgres;
using BackWave.Storage;

namespace BackWave.Benchmarks.ScaleOut;

/// <summary>
/// Drives the BackWave-only scale-out curve (bench-0141). For each swept Node count it resets and preloads
/// the shared backlog, spawns that many Node subprocesses (each a pump coordinating only through the shared
/// adapter, ADR 0006), waits until every Node's pump is up and claiming, then times the parent
/// <see cref="Stopwatch"/> from that readiness barrier to the backlog draining empty, and terminates the
/// children. Timing only the post-readiness drain keeps per-Node process startup (JIT, host build, connect)
/// out of the window (ADR 0027 §2) — otherwise fixed startup would grow as a fraction of a shrinking window
/// at high N and trip the plateau heuristic into a falsely early saturation knee. The window is timed in the
/// parent — outside the Nodes — so the one number that must be measured identically across the curve is taken
/// in one place (ADR 0027 §5). No competitor is involved: this charts BackWave's stateless-peer scale-out.
/// </summary>
internal sealed class ScaleOutOrchestrator
{
    /// <summary>Environment variable carrying the directory each Node drops its readiness file into.</summary>
    internal const string ReadinessDirEnvVar = "BACKWAVE_BENCH_READY_DIR";

    private static readonly TimeSpan DrainPollInterval = TimeSpan.FromMilliseconds(25);
    private static readonly TimeSpan ChildExitTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReadinessPollInterval = TimeSpan.FromMilliseconds(25);
    private static readonly TimeSpan ReadinessTimeout = TimeSpan.FromSeconds(60);

    private readonly string _connectionString;
    private readonly WorkloadSpec _spec;
    private readonly IReadOnlyList<int> _nodeCounts;
    private readonly RunMode _mode;

    /// <summary>Creates the orchestrator for one sweep against one shared database.</summary>
    public ScaleOutOrchestrator(
        string connectionString, WorkloadSpec spec, IReadOnlyList<int> nodeCounts, RunMode mode)
    {
        _connectionString = connectionString;
        _spec = spec;
        _nodeCounts = nodeCounts;
        _mode = mode;
    }

    /// <summary>Runs the full sweep and returns the assembled curve.</summary>
    public async Task<ScaleOutResult> RunAsync(CancellationToken cancellationToken)
    {
        // The target owns reset + preload (the same enqueue/truncate the single-run headline uses); a
        // separate store handles the parent's drain poll so it never shares a connection with the pumps.
        await using var target = new PostgresBenchmarkTarget(_connectionString);
        var version = await target.SetupAsync(cancellationToken).ConfigureAwait(false);
        var manifest = EnvironmentManifest.Capture(_mode, target.Engine, version);

        await using var pollStore = new PostgresJobStore(
            new PostgresStoreOptions { ConnectionString = _connectionString });

        var points = new List<ScaleOutPoint>(_nodeCounts.Count);
        foreach (var nodeCount in _nodeCounts)
        {
            points.Add(await MeasurePointAsync(target, pollStore, nodeCount, cancellationToken).ConfigureAwait(false));
        }

        return ScaleOutResult.From(target.Name, target.Engine, _spec, manifest, points);
    }

    private async Task<ScaleOutPoint> MeasurePointAsync(
        PostgresBenchmarkTarget target, IJobStore pollStore, int nodeCount, CancellationToken cancellationToken)
    {
        await target.ResetAsync(cancellationToken).ConfigureAwait(false);
        await target.PreloadAsync(_spec, cancellationToken).ConfigureAwait(false);

        var readinessDir = CreateReadinessDir(nodeCount);
        var children = new List<Process>(nodeCount);
        var stopwatch = new Stopwatch();
        int processedInWindow;
        try
        {
            for (var i = 0; i < nodeCount; i++)
            {
                children.Add(SpawnNode(readinessDir));
            }

            // Readiness barrier: hold the clock until every Node's pump is up and about to claim, so per-Node
            // process startup never lands in the window (ADR 0027 §2).
            await WaitForNodesReadyAsync(readinessDir, nodeCount, cancellationToken).ConfigureAwait(false);

            // Fast Nodes may have cleared part of the backlog while slower ones were still starting, so the
            // window measures only what is left to drain: throughput = (jobs cleared in window) / window.
            processedInWindow = await CountPendingAsync(pollStore, cancellationToken).ConfigureAwait(false);
            stopwatch.Start();
            await WaitForDrainAsync(pollStore, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
        }
        finally
        {
            stopwatch.Stop();
            // The children always die here — drain is already complete (every job terminal), so killing is
            // safe, and a hard kill guarantees no pump is ever leaked against the shared database on error.
            await TerminateChildrenAsync(children).ConfigureAwait(false);
            TryDeleteReadinessDir(readinessDir);
        }

        var window = stopwatch.Elapsed.TotalSeconds;
        var throughput = window > 0 ? processedInWindow / window : 0d;
        Console.Error.WriteLine(
            $"  nodes={nodeCount,2}  window={window,8:N2}s  processed={processedInWindow,8}  " +
            $"throughput={throughput,10:N0} jobs/sec");

        return new ScaleOutPoint
        {
            NodeCount = nodeCount,
            JobCount = _spec.JobCount,
            ProcessedInWindow = processedInWindow,
            WindowSeconds = window,
            ThroughputJobsPerSecond = throughput,
        };
    }

    private static string CreateReadinessDir(int nodeCount)
    {
        // One fresh directory per point (keyed by the parent PID + node count) so a prior point's readiness
        // files can never be miscounted; the Nodes drop their files here and the parent polls the count.
        var dir = Path.Combine(
            Path.GetTempPath(), $"backwave-scaleout-{System.Environment.ProcessId}-{nodeCount}");
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }

        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDeleteReadinessDir(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup of a temp directory — a leftover never affects a measurement.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static async Task WaitForNodesReadyAsync(
        string readinessDir, int nodeCount, CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        while (Directory.GetFiles(readinessDir, "*.ready").Length < nodeCount)
        {
            if (elapsed.Elapsed > ReadinessTimeout)
            {
                var ready = Directory.GetFiles(readinessDir, "*.ready").Length;
                throw new TimeoutException(
                    $"Only {ready}/{nodeCount} nodes signalled readiness within " +
                    $"{ReadinessTimeout.TotalSeconds:N0}s — aborting the scale-out point.");
            }

            await Task.Delay(ReadinessPollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private Process SpawnNode(string readinessDir)
    {
        var entryDll = System.Reflection.Assembly.GetEntryAssembly()?.Location
            ?? throw new InvalidOperationException("Cannot locate the benchmark entry assembly to spawn a node.");
        var processPath = System.Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot locate the host executable (Environment.ProcessPath is null).");

        var startInfo = new ProcessStartInfo
        {
            FileName = processPath,
            UseShellExecute = false,
        };

        // When the running process is the dotnet muxer we must hand it the managed entry DLL; when it is the
        // app's native apphost, that executable IS this program, so `--node` alone re-launches it as a Node.
        var isDotnetHost = string.Equals(
            Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase);
        if (isDotnetHost)
        {
            startInfo.ArgumentList.Add(entryDll);
        }

        startInfo.ArgumentList.Add("--node");
        startInfo.Environment[PostgresBenchmarkTarget.ConnectionStringEnvVar] = _connectionString;
        startInfo.Environment[ReadinessDirEnvVar] = readinessDir;

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start a node subprocess.");
    }

    private static async Task TerminateChildrenAsync(IReadOnlyList<Process> children)
    {
        foreach (var child in children)
        {
            try
            {
                if (!child.HasExited)
                {
                    child.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // The child exited between the check and the kill — nothing to terminate.
            }
        }

        foreach (var child in children)
        {
            try
            {
                using var timeout = new CancellationTokenSource(ChildExitTimeout);
                await child.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Gave the child a generous window to exit; move on rather than hang the sweep.
            }
            finally
            {
                child.Dispose();
            }
        }
    }

    private static async Task WaitForDrainAsync(IJobStore store, CancellationToken cancellationToken)
    {
        while (await CountPendingAsync(store, cancellationToken).ConfigureAwait(false) > 0)
        {
            await Task.Delay(DrainPollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<int> CountPendingAsync(IJobStore store, CancellationToken cancellationToken)
    {
        var counts = await store.CountJobsAsync(cancellationToken).ConfigureAwait(false);
        return counts.Where(c => !c.State.IsTerminal()).Sum(c => c.Count);
    }
}
