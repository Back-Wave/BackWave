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

public sealed record NightlySync(string Source);

public sealed class NightlySyncHandler(SyncRecorder recorder) : IJobHandler<NightlySync>
{
    public Task HandleAsync(NightlySync job, JobContext context, CancellationToken cancellationToken)
    {
        recorder.Runs.Add(job.Source);
        return Task.CompletedTask;
    }
}

public sealed class SyncRecorder
{
    public List<string> Runs { get; } = [];
}

[JsonSerializable(typeof(NightlySync))]
internal sealed partial class ScheduleJsonContext : JsonSerializerContext;

public class RecurringScheduleTests
{
    private static readonly DateTimeOffset Midnight = new(2026, 6, 10, 0, 0, 0, TimeSpan.Zero);

    private sealed class Fixture
    {
        public InMemoryJobStore Store { get; } = new();
        public BackWaveClient Client { get; }
        public SyncRecorder Recorder { get; }
        private readonly JobRegistry _registry;
        private readonly IServiceProvider _services;

        public Fixture()
        {
            _services = new ServiceCollection()
                .AddSingleton<SyncRecorder>()
                .AddTransient<IJobHandler<NightlySync>, NightlySyncHandler>()
                .BuildServiceProvider();
            _registry = new JobRegistry(
            [
                JobRegistration.Create<NightlySync, NightlySyncHandler>(
                    "nightly-sync", ScheduleJsonContext.Default.NightlySync),
            ]);
            Recorder = _services.GetRequiredService<SyncRecorder>();
            Client = new BackWaveClient(Store, _registry);
        }

        public DeterministicPump NewNode(string workerId) => new(
            new NodeDriver(new NodeOptions { WorkerId = workerId, Policy = new Core.DispatchPolicy.Strict(["default"]) }),
            Store, _registry, _services);
    }

    [Fact]
    public async Task Schedule_MintsAtDueTicks_AndInstancesRunThroughTheNormalPipeline()
    {
        var fixture = new Fixture();
        var node = fixture.NewNode("node-1");

        await fixture.Client.UpsertRecurringAsync(
            "nightly-sync", Cron.Daily(2), new NightlySync("crm"), now: Midnight);

        // Before the tick: nothing minted, nothing run.
        await node.PumpAsync(Midnight.AddHours(1));
        Assert.Empty(fixture.Recorder.Runs);
        Assert.Empty(await fixture.Store.ListJobsAsync(new JobQuery { ScheduleId = "nightly-sync" }));

        // At the tick: exactly one instance, minted due at the tick and executed.
        await node.PumpAsync(Midnight.AddHours(2));
        Assert.Equal(["crm"], fixture.Recorder.Runs);

        var instances = await fixture.Store.ListJobsAsync(new JobQuery { ScheduleId = "nightly-sync" });
        var instance = Assert.Single(instances);
        Assert.Equal(Midnight.AddHours(2), instance.DueTime);
        Assert.Equal(JobState.Succeeded, instance.State);
        Assert.Equal("nightly-sync", instance.ScheduleId);
    }

    [Fact]
    public async Task ScheduleAndInstances_AreDistinctSeparatelyVisibleThings()
    {
        var fixture = new Fixture();
        var node = fixture.NewNode("node-1");

        await fixture.Client.UpsertRecurringAsync(
            "nightly-sync", Cron.Daily(2), new NightlySync("crm"), now: Midnight);
        await node.PumpAsync(Midnight.AddHours(2));

        // The instance succeeded; the schedule lives on, cursor advanced past the tick.
        var schedule = Assert.Single(await fixture.Store.ListSchedulesAsync()).Schedule;
        Assert.Equal("nightly-sync", schedule.ScheduleId);
        Assert.Equal(Midnight.AddHours(2), schedule.Cursor);
        Assert.Single(await fixture.Store.ListJobsAsync(new JobQuery { ScheduleId = "nightly-sync" }));
    }

    [Fact]
    public async Task TwoNodesPollingTogether_MintEachTickExactlyOnce()
    {
        var fixture = new Fixture();
        var nodeA = fixture.NewNode("node-a");
        var nodeB = fixture.NewNode("node-b");

        await fixture.Client.UpsertRecurringAsync(
            "nightly-sync", Cron.Hourly(), new NightlySync("crm"), now: Midnight);

        for (var hour = 1; hour <= 3; hour++)
        {
            await nodeA.PumpAsync(Midnight.AddHours(hour));
            await nodeB.PumpAsync(Midnight.AddHours(hour));
        }

        Assert.Equal(3, (await fixture.Store.ListJobsAsync(new JobQuery { ScheduleId = "nightly-sync" })).Count);
        Assert.Equal(3, fixture.Recorder.Runs.Count);
    }

    [Fact]
    public async Task CanonicalCronForm_IsWhatGetsStored()
    {
        var fixture = new Fixture();
        await fixture.Client.UpsertRecurringAsync(
            "nightly-sync", CronExpression.Parse("0 2 * * *"), new NightlySync("crm"), now: Midnight);

        var schedule = Assert.Single(await fixture.Store.ListSchedulesAsync()).Schedule;
        Assert.Equal("0 0 2 * * *", schedule.Cron);
    }

    [Fact]
    public async Task RedefiningASchedule_PreservesItsCursor()
    {
        var fixture = new Fixture();
        var node = fixture.NewNode("node-1");

        await fixture.Client.UpsertRecurringAsync(
            "nightly-sync", Cron.Daily(2), new NightlySync("crm"), now: Midnight);
        await node.PumpAsync(Midnight.AddHours(2));

        // Redefine to 3am with a stale 'now': the resolved cursor must not rewind,
        // so the 2am tick is never minted twice.
        await fixture.Client.UpsertRecurringAsync(
            "nightly-sync", Cron.Daily(3), new NightlySync("crm"), now: Midnight);
        await node.PumpAsync(Midnight.AddHours(3));

        Assert.Equal(2, fixture.Recorder.Runs.Count);
        Assert.Equal(2, (await fixture.Store.ListJobsAsync(new JobQuery { ScheduleId = "nightly-sync" })).Count);
    }

    [Fact]
    public async Task RemovedSchedule_StopsMinting_ExistingInstancesUntouched()
    {
        var fixture = new Fixture();
        var node = fixture.NewNode("node-1");

        await fixture.Client.UpsertRecurringAsync(
            "nightly-sync", Cron.Hourly(), new NightlySync("crm"), now: Midnight);
        await node.PumpAsync(Midnight.AddHours(1));

        await fixture.Client.RemoveRecurringAsync("nightly-sync");
        await node.PumpAsync(Midnight.AddHours(2));

        Assert.Single(await fixture.Store.ListJobsAsync(new JobQuery { ScheduleId = "nightly-sync" }));
        Assert.Equal(["crm"], fixture.Recorder.Runs);
        Assert.Empty(await fixture.Store.ListSchedulesAsync());
    }

    // --- Per-schedule fault isolation (issue 0029) ---

    [Fact]
    public async Task UpsertRecurring_WithUnknownTimeZone_ThrowsLoudly_AndStoresNothing()
    {
        var fixture = new Fixture();

        var exception = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await fixture.Client.UpsertRecurringAsync(
                "nightly-sync", Cron.Daily(2), new NightlySync("crm"), now: Midnight,
                timeZone: "Mars/Phobos"));

        Assert.Contains("Mars/Phobos", exception.Message);
        Assert.Empty(await fixture.Store.ListSchedulesAsync());
    }

    [Fact]
    public async Task APoisonedScheduleRow_DoesNotHaltTheGroup_HealthySchedulesKeepMinting()
    {
        var fixture = new Fixture();
        var node = fixture.NewNode("node-1");

        // A malformed schedule reaches storage directly — as an older version or corruption
        // would — bypassing the upsert door. Its zone id does not resolve on this host.
        await fixture.Store.UpsertScheduleAsync(new ScheduleRecord
        {
            ScheduleId = "poisoned",
            Cron = Cron.Hourly().Canonical,
            WireName = "nightly-sync",
            Payload = ReadOnlyMemory<byte>.Empty,
            Queue = "default",
            Cursor = Midnight,
            TimeZoneId = "Mars/Phobos",
        });
        await fixture.Client.UpsertRecurringAsync(
            "nightly-sync", Cron.Hourly(), new NightlySync("crm"), now: Midnight);

        // The pump must not throw: minting skips the poisoned row and the healthy schedule
        // mints and runs as normal.
        await node.PumpAsync(Midnight.AddHours(1));
        await node.PumpAsync(Midnight.AddHours(2));

        Assert.Equal(["crm", "crm"], fixture.Recorder.Runs);

        // The poisoned row is visible via the Monitor as errored — quarantined, not silent.
        var monitor = new BackWaveMonitor(fixture.Store);
        var schedules = await monitor.ListSchedulesAsync();
        var poisoned = Assert.Single(schedules, s => s.ScheduleId == "poisoned");
        Assert.NotNull(poisoned.Error);
        Assert.Contains("Mars/Phobos", poisoned.Error);
        Assert.Null(poisoned.NextDue);

        var healthy = Assert.Single(schedules, s => s.ScheduleId == "nightly-sync");
        Assert.Null(healthy.Error);
        Assert.NotNull(healthy.NextDue);
    }
}
