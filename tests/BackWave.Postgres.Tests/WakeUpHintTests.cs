using System.Text.Json.Serialization;
using BackWave.Core;
using BackWave.Hosting;
using BackWave.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace BackWave.Postgres.Tests;

public sealed record HintProbe(string Name);

public sealed class HintProbeHandler(HintRecorder recorder) : IJobHandler<HintProbe>
{
    public Task HandleAsync(HintProbe job, JobContext context, CancellationToken cancellationToken)
    {
        recorder.Handled.TrySetResult(DateTimeOffset.UtcNow);
        return Task.CompletedTask;
    }
}

public sealed class HintRecorder
{
    public TaskCompletionSource<DateTimeOffset> Handled { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

[JsonSerializable(typeof(HintProbe))]
internal sealed partial class HintJsonContext : JsonSerializerContext;

/// <summary>
/// Real-clock integration tests for LISTEN/NOTIFY Wake-Up Hints (issue 0021): the poll
/// interval is deliberately long, so anything fast got there via a hint — and killing the
/// hint channel must change latency only, never outcomes (ADR-0005).
/// </summary>
[Collection("postgres")]
public class WakeUpHintTests
{
    private static (WorkerGroupService Service, BackWaveClient Client, HintRecorder Recorder) Build(
        PostgresJobStore store, TimeSpan pollInterval)
    {
        var recorder = new HintRecorder();
        var provider = new ServiceCollection()
            .AddSingleton(recorder)
            .AddTransient<IJobHandler<HintProbe>, HintProbeHandler>()
            .BuildServiceProvider();
        var registry = new JobRegistry(
        [
            JobRegistration.Create<HintProbe, HintProbeHandler>("hint-probe", HintJsonContext.Default.HintProbe),
        ]);
        var service = new WorkerGroupService(
            new WorkerGroupOptions
            {
                Name = "hint-workers",
                Policy = new DispatchPolicy.Strict(["default"]),
                PollInterval = pollInterval,
            },
            store,
            registry,
            provider.GetRequiredService<IServiceScopeFactory>(),
            new BackWaveHealth(),
            NullLogger<WorkerGroupService>.Instance);
        return (service, new BackWaveClient(store, registry), recorder);
    }

    /// <summary>The pump subscribes asynchronously; wait until the LISTEN backend is live.</summary>
    private static async Task WaitForListenerAsync()
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(15);
        await using var dataSource = NpgsqlDataSource.Create(PostgresTestDatabase.ConnectionString);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var command = dataSource.CreateCommand(
                "SELECT count(*) FROM pg_stat_activity WHERE query ILIKE 'LISTEN%'");
            if ((long)(await command.ExecuteScalarAsync())! > 0)
            {
                return;
            }
            await Task.Delay(50);
        }
        Assert.Fail("the LISTEN connection never appeared in pg_stat_activity");
    }

    [Fact]
    public async Task EnqueueToStart_IsMilliseconds_NotThePollInterval()
    {
        await using var store = await PostgresTestDatabase.CreateFreshStoreAsync();
        var (service, client, recorder) = Build(store, pollInterval: TimeSpan.FromSeconds(10));
        await service.StartAsync(CancellationToken.None);
        try
        {
            await WaitForListenerAsync();

            var enqueuedAt = DateTimeOffset.UtcNow;
            await client.EnqueueAsync(new HintProbe("fast-path"), enqueuedAt);
            var startedAt = await recorder.Handled.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // The first poll tick is 10 s out; only a hint can explain this latency.
            var latency = startedAt - enqueuedAt;
            Assert.True(latency < TimeSpan.FromSeconds(3), $"enqueue-to-start took {latency}");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task KillingTheListenConnection_DegradesLatencyToThePollInterval_NothingElse()
    {
        await using var store = await PostgresTestDatabase.CreateFreshStoreAsync();
        var (service, client, recorder) = Build(store, pollInterval: TimeSpan.FromSeconds(2));
        await service.StartAsync(CancellationToken.None);
        try
        {
            await WaitForListenerAsync();

            await using var dataSource = NpgsqlDataSource.Create(PostgresTestDatabase.ConnectionString);
            await using (var kill = dataSource.CreateCommand(
                "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE query ILIKE 'LISTEN%'"))
            {
                await kill.ExecuteScalarAsync();
            }

            // Polling is the sole correctness mechanism: the job still runs, within the
            // poll interval instead of milliseconds. Nothing fails, nothing is lost.
            await client.EnqueueAsync(new HintProbe("slow-path"), DateTimeOffset.UtcNow);
            await recorder.Handled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }
}
