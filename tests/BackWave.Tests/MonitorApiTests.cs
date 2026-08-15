using System.Text.Json.Serialization;
using BackWave.Core;
using BackWave.Driver;
using BackWave.Jobs;
using BackWave.Monitor;
using BackWave.Storage;
using BackWave.Testing;
using BackWave.Storage.InMemory;
using Microsoft.Extensions.DependencyInjection;

namespace BackWave.Tests;

public sealed record InventorySync(string Region);

public sealed class InventorySyncHandler : IJobHandler<InventorySync>
{
    public Task HandleAsync(InventorySync job, JobContext context, CancellationToken cancellationToken)
        => Task.CompletedTask;
}

[JsonSerializable(typeof(InventorySync))]
internal sealed partial class MonitorJsonContext : JsonSerializerContext;

public class MonitorApiTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(
        BackWaveClient Client,
        BackWaveMonitor Monitor,
        DeterministicPump Pump,
        InMemoryJobStore Store);

    private static Fixture CreateFixture(StoreBounds? bounds = null)
    {
        var services = new ServiceCollection()
            .AddTransient<IJobHandler<InventorySync>, InventorySyncHandler>()
            .BuildServiceProvider();

        var registry = new JobRegistry(
        [
            JobRegistration.Create<InventorySync, InventorySyncHandler>(
                "inventory-sync", MonitorJsonContext.Default.InventorySync),
        ]);

        var store = new InMemoryJobStore(bounds);
        var driver = new NodeDriver(new NodeOptions
        {
            WorkerId = "node-1",
            Policy = new DispatchPolicy.Strict(["default", "reports"]),
            RetryPolicy = RetryPolicy.Default,
        });
        var pump = new DeterministicPump(driver, store, registry, services);

        return new Fixture(new BackWaveClient(store, registry), new BackWaveMonitor(store), pump, store);
    }

    [Fact]
    public async Task JobLifecycle_ObservablePurelyThroughTheMonitorApi()
    {
        var fixture = CreateFixture();

        var jobId = await fixture.Client.EnqueueAsync(new InventorySync("eu"), dueTime: T0);

        var pending = await fixture.Monitor.GetJobAsync(jobId);
        Assert.NotNull(pending);
        Assert.Equal(JobState.Scheduled, pending.State);
        Assert.Equal("inventory-sync", pending.WireName);
        Assert.Equal("default", pending.Queue);
        Assert.Equal(0, pending.Attempt);
        Assert.Equal(T0, pending.DueTime);

        await fixture.Pump.PumpAsync(T0);

        var done = await fixture.Monitor.GetJobAsync(jobId);
        Assert.NotNull(done);
        Assert.Equal(JobState.Succeeded, done.State);
        Assert.Equal(1, done.Attempt);
        Assert.Equal(T0, done.TerminalAt);
    }

    [Fact]
    public async Task ListJobs_FiltersByStateQueueAndWireName()
    {
        var fixture = CreateFixture();

        var defaultJob = await fixture.Client.EnqueueAsync(new InventorySync("us"), dueTime: T0);
        var reportsJob = await fixture.Client.EnqueueAsync(new InventorySync("apac"), dueTime: T0.AddHours(1), queue: "reports");

        await fixture.Pump.PumpAsync(T0); // runs only the default-queue job

        var succeeded = await fixture.Monitor.ListJobsAsync(new JobQuery { State = JobState.Succeeded });
        Assert.Equal([defaultJob], succeeded.Select(j => j.JobId));

        var stillScheduled = await fixture.Monitor.ListJobsAsync(new JobQuery { State = JobState.Scheduled, Queue = "reports" });
        Assert.Equal([reportsJob], stillScheduled.Select(j => j.JobId));

        var byWireName = await fixture.Monitor.ListJobsAsync(new JobQuery { WireName = "inventory-sync" });
        Assert.Equal(2, byWireName.Count);

        Assert.Empty(await fixture.Monitor.ListJobsAsync(new JobQuery { WireName = "no-such-wire-name" }));
    }

    [Fact]
    public async Task QueueDepths_CountByQueueAndState()
    {
        var fixture = CreateFixture();

        await fixture.Client.EnqueueAsync(new InventorySync("a"), dueTime: T0);
        await fixture.Client.EnqueueAsync(new InventorySync("b"), dueTime: T0.AddDays(1));
        await fixture.Client.EnqueueAsync(new InventorySync("c"), dueTime: T0, queue: "reports");

        await fixture.Pump.PumpAsync(T0); // job "a" and "c": Succeeded; "b": still Scheduled

        var depths = await fixture.Monitor.GetQueueDepthsAsync();
        Assert.Equal(
        [
            new QueueStateCount("default", JobState.Scheduled, 1),
            new QueueStateCount("default", JobState.Succeeded, 1),
            new QueueStateCount("reports", JobState.Succeeded, 1),
        ], depths);
    }

    [Fact]
    public async Task ScheduleStatus_ShowsNextDueAndMintedInstances()
    {
        var fixture = CreateFixture();

        await fixture.Client.UpsertRecurringAsync(
            "hourly-sync", Cron.Hourly(atMinute: 0), new InventorySync("eu"), now: T0);

        await fixture.Pump.PumpAsync(T0.AddHours(1)); // first tick mints and runs

        var status = Assert.Single(await fixture.Monitor.ListSchedulesAsync());
        Assert.Equal("hourly-sync", status.ScheduleId);
        Assert.Equal("inventory-sync", status.WireName);
        Assert.Equal(T0.AddHours(1), status.Cursor);
        Assert.Equal(T0.AddHours(2), status.NextDue);
        Assert.False(status.HasLiveInstance);

        var minted = await fixture.Monitor.ListJobsAsync(new JobQuery { ScheduleId = "hourly-sync" });
        var instance = Assert.Single(minted);
        Assert.Equal(JobState.Succeeded, instance.State);
        Assert.Equal("hourly-sync", instance.ScheduleId);
    }

    [Fact]
    public async Task ListJobs_PageIsBoundedByMaxMonitorPageSize()
    {
        var fixture = CreateFixture(new StoreBounds { MaxMonitorPageSize = 2 });

        await fixture.Client.EnqueueAsync(new InventorySync("a"), dueTime: T0);
        await fixture.Client.EnqueueAsync(new InventorySync("b"), dueTime: T0);
        await fixture.Client.EnqueueAsync(new InventorySync("c"), dueTime: T0);

        Assert.Equal(2, (await fixture.Monitor.ListJobsAsync()).Count);
        Assert.Single(await fixture.Monitor.ListJobsAsync(new JobQuery { MaxResults = 1 }));
    }
}
