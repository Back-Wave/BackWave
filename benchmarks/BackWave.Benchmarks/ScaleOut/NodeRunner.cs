using BackWave.Benchmarks.Targets;
using BackWave.Generated;
using BackWave.Hosting;
using BackWave.Jobs;
using BackWave.Postgres;
using BackWave.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BackWave.Benchmarks.ScaleOut;

/// <summary>
/// The per-Node worker subprocess of the scale-out curve (bench-0141). One Node = one OS process running a
/// single real <c>WorkerGroupService</c> pump against the shared database, coordinating with its sibling
/// Nodes only through the adapter — the database-authoritative stateless-peer model (ADR 0006). It enqueues
/// nothing: it claims and drains the preloaded backlog, then exits once the shared queue is empty, or when
/// the parent orchestrator terminates it. The shared DSN arrives in the same environment variable the
/// Postgres target reads, inherited from the parent.
/// </summary>
internal static class NodeRunner
{
    private static readonly TimeSpan DrainPollInterval = TimeSpan.FromMilliseconds(25);

    /// <summary>Runs the pump until the shared backlog drains, then returns a process exit code.</summary>
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        // The node takes no flags beyond `--node`; the workload lives in the already-loaded backlog.
        _ = args;

        var connectionString =
            System.Environment.GetEnvironmentVariable(PostgresBenchmarkTarget.ConnectionStringEnvVar)
            ?? throw new InvalidOperationException(
                $"A node subprocess needs the shared DSN in ${PostgresBenchmarkTarget.ConnectionStringEnvVar}.");

        // One store instance shared by the pump and the drain poll: the host's DI container owns it and
        // disposes it when the host is torn down, so the process must not dispose it a second time.
        var store = CreateStore(connectionString);
        using var host = BuildPumpHost(store);

        await host.StartAsync(cancellationToken).ConfigureAwait(false);
        SignalReady();
        try
        {
            await WaitForDrainAsync(store, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await host.StopAsync(cancellationToken).ConfigureAwait(false);
        }

        return 0;
    }

    private static void SignalReady()
    {
        // The pump has started and is about to claim. Drop a file the parent counts so it can open the timed
        // window only once every Node is up, keeping per-Node process startup out of the window (ADR 0027 §2).
        // Absent the env var (a Node run by hand, not under the orchestrator) this is a harmless no-op.
        var dir = System.Environment.GetEnvironmentVariable(ScaleOutOrchestrator.ReadinessDirEnvVar);
        if (string.IsNullOrEmpty(dir))
        {
            return;
        }

        File.WriteAllText(Path.Combine(dir, $"{System.Environment.ProcessId}.ready"), string.Empty);
    }

    private static IHost BuildPumpHost(IJobStore store)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        builder.Services.AddBackWave(backwave =>
        {
            backwave
                .UseStore(_ => store)
                .UseJobs(BackWaveJobs.Module)
                .AddWorkerGroup(BackWaveTarget.WorkerOptions());
        });

        return builder.Build();
    }

    private static IJobStore CreateStore(string connectionString)
        => new PostgresJobStore(new PostgresStoreOptions { ConnectionString = connectionString });

    private static async Task WaitForDrainAsync(IJobStore store, CancellationToken cancellationToken)
    {
        // pending counts every non-terminal job (Available, Leased, Running, …), so a queue that is merely
        // all-in-flight on sibling Nodes never reads as drained — only a fully terminal backlog does.
        while (true)
        {
            var counts = await store.CountJobsAsync(cancellationToken).ConfigureAwait(false);
            var pending = counts.Where(c => !c.State.IsTerminal()).Sum(c => c.Count);
            if (pending == 0)
            {
                return;
            }

            await Task.Delay(DrainPollInterval, cancellationToken).ConfigureAwait(false);
        }
    }
}
