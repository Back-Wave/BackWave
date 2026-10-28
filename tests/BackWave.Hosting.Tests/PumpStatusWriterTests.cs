using System.Diagnostics;
using BackWave.Core;
using BackWave.Diagnostics;
using BackWave.Jobs;
using BackWave.Monitor;
using BackWave.Storage;
using BackWave.Storage.InMemory;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace BackWave.Hosting.Tests;

public sealed class PumpStatusWriterTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan WriteInterval = TimeSpan.FromSeconds(30);
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private const int WriteFailedEventId = 2201;
    private const int WriteRecoveredEventId = 2202;

    [Fact]
    public void PumpStatuses_RankStoppedThenHaltedThenStoppingThenDegradedThenRunning_AndAHaltedPumpStopsWithItsProcess()
    {
        var health = new BackWaveHealth();
        var now = Start.AddHours(1);
        var timeout = new TimeoutException("store blip");
        foreach (var pump in new[] { "running", "marked", "inWindow", "outOfWindow", "halted", "stopped" })
        {
            health.ReportStarted("workers", pump);
        }
        health.ReportDegraded("workers", "marked", timeout, now - TimeSpan.FromHours(1));
        health.ReportDegraded("workers", "inWindow", timeout, now - WriteInterval);
        health.ReportRecovered("workers", "inWindow");
        health.ReportDegraded("workers", "outOfWindow", timeout, now - WriteInterval - TimeSpan.FromTicks(1));
        health.ReportRecovered("workers", "outOfWindow");
        health.ReportDegraded("workers", "halted", timeout, now);
        health.ReportHalted("workers", "halted", groupPumpCount: 6,
            Invariant.Halt(InvariantTrigger.ClaimedJobTerminal, "forced"));
        health.ReportDegraded("workers", "stopped", timeout, now);
        health.ReportStoppedCleanly("workers", "stopped");

        var running = health.PumpStatuses(now, now, WriteInterval, HostPhase.Running).ToDictionary(s => s.WorkerId);
        var stopping = health.PumpStatuses(now, now, WriteInterval, HostPhase.Stopping).ToDictionary(s => s.WorkerId);
        var stopped = health.PumpStatuses(now, now, WriteInterval, HostPhase.Stopped).ToDictionary(s => s.WorkerId);

        var timeoutType = typeof(TimeoutException).FullName;
        var haltType = typeof(InvariantViolationException).FullName;
        Assert.Equal((PumpState.Running, null, null), Verdict(running["running"]));
        Assert.Equal((PumpState.Degraded, timeoutType, null), Verdict(running["marked"]));
        Assert.Equal((PumpState.Degraded, timeoutType, null), Verdict(running["inWindow"]));
        Assert.Equal((PumpState.Running, null, null), Verdict(running["outOfWindow"]));
        Assert.Equal((PumpState.Halted, haltType, InvariantTrigger.ClaimedJobTerminal), Verdict(running["halted"]));
        Assert.Equal((PumpState.Stopped, null, null), Verdict(running["stopped"]));

        Assert.Equal((PumpState.Stopping, null, null), Verdict(stopping["running"]));
        Assert.Equal((PumpState.Stopping, null, null), Verdict(stopping["marked"]));
        Assert.Equal((PumpState.Halted, haltType, InvariantTrigger.ClaimedJobTerminal), Verdict(stopping["halted"]));
        Assert.Equal((PumpState.Stopped, null, null), Verdict(stopping["stopped"]));

        Assert.Equal((PumpState.Stopping, null, null), Verdict(stopped["running"]));
        Assert.Equal((PumpState.Stopping, null, null), Verdict(stopped["marked"]));
        Assert.Equal((PumpState.Stopped, haltType, InvariantTrigger.ClaimedJobTerminal), Verdict(stopped["halted"]));
        Assert.Equal((PumpState.Stopped, null, null), Verdict(stopped["stopped"]));

        Assert.All(running.Values, s =>
        {
            Assert.Equal("workers", s.WorkerGroup);
            Assert.Equal(now, s.ReportedAt);
            Assert.Equal(WriteInterval, s.ReportInterval);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pumps_ReadRunningAfterStart_ThenStoppingAndStoppedThroughACleanStop(bool servicesStopConcurrently)
    {
        var store = new FaultableStore(new InMemoryJobStore());
        var clock = new FakeTimeProvider(Start);
        await using var app = BuildHost(store, clock, Group(pumps: 2), servicesStopConcurrently);
        var monitor = app.Services.GetRequiredService<BackWaveMonitor>();

        await app.StartAsync();
        var started = await monitor.ListPumpStatusesAsync();

        Assert.Equal(2, started.Count);
        Assert.All(started, s =>
        {
            Assert.Equal("workers", s.WorkerGroup);
            Assert.Equal(PumpState.Running, s.State);
            Assert.Equal(Start, s.ReportedAt);
            Assert.Equal(WriteInterval, s.ReportInterval);
        });

        await app.StopAsync();
        var stopped = await monitor.ListPumpStatusesAsync();

        Assert.Equal(started.Select(s => s.WorkerId), stopped.Select(s => s.WorkerId));
        Assert.All(stopped, s => Assert.Equal(PumpState.Stopped, s.State));
        Assert.Equal(
            [
                [PumpState.Running, PumpState.Running],
                [PumpState.Stopping, PumpState.Stopping],
                [PumpState.Stopped, PumpState.Stopped],
            ],
            store.PumpStatusWrites.Select(write => write.Select(s => s.State).ToList()).ToList());
    }

    [Fact]
    public async Task AHaltedPump_ReadsHaltedWithItsTrigger_FromTheNextWrite_ThenStoppedWithItsFault_AfterItsProcessStops()
    {
        var store = new FaultableStore(new InMemoryJobStore());
        var clock = new FakeTimeProvider(Start);
        await using var app = BuildHost(store, clock, Group(pumps: 1));
        var monitor = app.Services.GetRequiredService<BackWaveMonitor>();
        var health = app.Services.GetRequiredService<BackWaveHealth>();
        await app.StartAsync();

        store.ClaimInvariant = InvariantTrigger.ClaimedJobTerminal;
        await WaitForAsync(() => !health.IsHealthy, "the Pump to halt");
        clock.Advance(WriteInterval);
        await WaitForAsync(
            () => store.LandedPumpStatusWrites == 2, "the writer's next write");

        var halted = Assert.Single(await monitor.ListPumpStatusesAsync());
        Assert.Equal(PumpState.Halted, halted.State);
        Assert.Equal(InvariantTrigger.ClaimedJobTerminal, halted.Trigger);
        Assert.Equal(typeof(InvariantViolationException).FullName, halted.FaultType);
        Assert.Equal(Start + WriteInterval, halted.ReportedAt);

        await app.StopAsync();
        var stopped = Assert.Single(await monitor.ListPumpStatusesAsync());
        Assert.Equal(PumpState.Stopped, stopped.State);
        Assert.Equal(InvariantTrigger.ClaimedJobTerminal, stopped.Trigger);
        Assert.Equal(typeof(InvariantViolationException).FullName, stopped.FaultType);
        Assert.Equal(
            [PumpState.Running, PumpState.Halted, PumpState.Halted, PumpState.Stopped],
            store.PumpStatusWrites.Select(write => Assert.Single(write).State));
    }

    [Fact]
    public async Task AClockSteppedBack_StillLandsTheNextWrite_AHalt_AndTheStoppedWrite()
    {
        var store = new FaultableStore(new InMemoryJobStore());
        var clock = new SteppableClock(Start);
        await using var app = BuildHost(store, clock, Group(pumps: 1));
        var monitor = app.Services.GetRequiredService<BackWaveMonitor>();
        var health = app.Services.GetRequiredService<BackWaveHealth>();
        await app.StartAsync();
        await WaitForAsync(() => store.PumpStatusWrites.Count == 1, "the first write");

        clock.StepBack(TimeSpan.FromHours(1));
        clock.Advance(WriteInterval);
        await WaitForAsync(() => store.LandedPumpStatusWrites == 2, "the write after the step back");

        var running = Assert.Single(await monitor.ListPumpStatusesAsync());
        Assert.Equal(PumpState.Running, running.State);
        Assert.Equal(Start + PumpStatusWriter.ReportedAtStep, running.ReportedAt);

        store.ClaimInvariant = InvariantTrigger.ClaimedJobTerminal;
        await WaitForAsync(() => !health.IsHealthy, "the Pump to halt");
        clock.Advance(WriteInterval);
        await WaitForAsync(() => store.LandedPumpStatusWrites == 3, "the write after the halt");

        var halted = Assert.Single(await monitor.ListPumpStatusesAsync());
        Assert.Equal(PumpState.Halted, halted.State);
        Assert.Equal(InvariantTrigger.ClaimedJobTerminal, halted.Trigger);

        await app.StopAsync();
        var stopped = Assert.Single(await monitor.ListPumpStatusesAsync());
        Assert.Equal(PumpState.Stopped, stopped.State);
        Assert.Equal(InvariantTrigger.ClaimedJobTerminal, stopped.Trigger);
    }

    [Fact]
    public async Task AStoreThatRejectsEveryWrite_LeavesThePumpsRunningAndHealthy_AndLogsOnceEachWay()
    {
        var store = new FaultableStore(new InMemoryJobStore()) { FailPumpStatusWrites = true };
        var clock = new FakeTimeProvider(Start);
        var logs = new CapturingLoggerProvider();
        var recorder = new PingRecorder();
        await using var app = BuildHost(store, clock, Group(pumps: 1), logs: logs, recorder: recorder);
        var health = app.Services.GetRequiredService<BackWaveHealth>();
        await app.StartAsync();

        clock.Advance(WriteInterval);
        await WaitForAsync(() => store.PumpStatusWrites.Count == 2, "a second failed write");
        await app.Services.GetRequiredService<BackWaveClient>().EnqueueAsync(new PingJob("after-failed-writes"), dueTime: clock.GetUtcNow());
        await WaitForAsync(() => recorder.Handled.Contains("after-failed-writes"), "the Pump to run a job");

        Assert.True(health.IsHealthy);
        Assert.Empty(health.DegradedGroups);
        Assert.Empty(health.PartiallyHaltedGroups);
        var failed = Assert.Single(logs.Entries, e => e.EventId == WriteFailedEventId);
        Assert.Equal(LogLevel.Warning, failed.Level);
        Assert.IsType<InvalidOperationException>(failed.Exception);

        store.FailPumpStatusWrites = false;
        clock.Advance(WriteInterval);
        await WaitForAsync(
            () => logs.Entries.Any(e => e.EventId == WriteRecoveredEventId), "the recovery log");
        Assert.Equal(LogLevel.Information, Assert.Single(logs.Entries, e => e.EventId == WriteRecoveredEventId).Level);
        Assert.Single(logs.Entries, e => e.EventId == WriteFailedEventId);

        await app.StopAsync();
    }

    [Fact]
    public async Task WritesCutByCancellation_LogNoWriteFailure_EvenWhenTheStoreThrowsNoCancellationException()
    {
        var store = new FaultableStore(new InMemoryJobStore()) { PumpStatusWritesFaultOnCancellation = true };
        var logs = new CapturingLoggerProvider();
        await using var app = BuildHost(store, new FakeTimeProvider(Start), Group(pumps: 1), logs: logs);
        await app.StartAsync();
        await WaitForAsync(() => store.PumpStatusWrites.Count == 1, "the first write to reach the store");

        await app.StopAsync();

        Assert.Equal(3, store.PumpStatusWrites.Count);
        Assert.DoesNotContain(logs.Entries, e => e.EventId == WriteFailedEventId);
    }

    [Fact]
    public async Task AStop_WaitsOnAHungLoopWriteNoLongerThanTheStopWriteBudget()
    {
        var store = new FaultableStore(new InMemoryJobStore()) { HangNextPumpStatusWrite = FaultableStore.HungWriteEnding.TimesOut };
        await using var app = BuildHost(store, new FakeTimeProvider(Start), Group(pumps: 1));
        var monitor = app.Services.GetRequiredService<BackWaveMonitor>();
        await app.StartAsync();
        await WaitForAsync(() => store.PumpStatusWrites.Count == 1, "the first write to hang in the store");

        try
        {
            var stopping = Stopwatch.StartNew();
            await app.StopAsync().WaitAsync(TestTimeout);

            Assert.True(stopping.Elapsed < PumpStatusWriter.StopWriteBudget * 2, $"the stop took {stopping.Elapsed}");
            Assert.Equal(PumpState.Stopped, Assert.Single(await monitor.ListPumpStatusesAsync()).State);
        }
        finally
        {
            store.ReleaseHungPumpStatusWrite();
        }
    }

    [Fact]
    public async Task ALoopWriteThatLandsAfterTheStoppedWrite_LeavesThePumpReadingStopped()
    {
        var store = new FaultableStore(new InMemoryJobStore())
        {
            HangNextPumpStatusWrite = FaultableStore.HungWriteEnding.LandsLate,
        };
        var clock = new FakeTimeProvider(Start);
        await using var app = BuildHost(store, clock, Group(pumps: 1));
        var monitor = app.Services.GetRequiredService<BackWaveMonitor>();
        await app.StartAsync();
        await WaitForAsync(() => store.PumpStatusWrites.Count == 1, "the first write to hang in the store");

        await app.StopAsync().WaitAsync(TestTimeout);
        Assert.Equal(PumpState.Stopped, Assert.Single(await monitor.ListPumpStatusesAsync()).State);

        store.ReleaseHungPumpStatusWrite();
        await store.HungPumpStatusWriteLanded.WaitAsync(TestTimeout);

        Assert.Equal(PumpState.Running, Assert.Single(store.PumpStatusWrites[0]).State);
        Assert.Equal(PumpState.Stopped, Assert.Single(await monitor.ListPumpStatusesAsync()).State);
    }

    [Fact]
    public async Task AHostDisposedWithoutAStop_WritesNoMore_AndTheWriterDisposesAgainSafely()
    {
        var store = new FaultableStore(new InMemoryJobStore());
        var clock = new FakeTimeProvider(Start);
        var app = BuildHost(store, clock, Group(pumps: 1));
        var writer = app.Services.GetServices<IHostedService>().OfType<PumpStatusWriter>().Single();
        await app.StartAsync();
        await WaitForAsync(() => store.PumpStatusWrites.Count == 1, "the first write");

        await app.DisposeAsync();
        clock.Advance(WriteInterval);
        await Task.Delay(TimeSpan.FromMilliseconds(500));

        Assert.Single(store.PumpStatusWrites);
        writer.Dispose();
    }

    private sealed class SteppableClock(DateTimeOffset start) : TimeProvider
    {
        private readonly FakeTimeProvider _time = new(start);
        private TimeSpan _stepped;

        public void Advance(TimeSpan by) => _time.Advance(by);

        public void StepBack(TimeSpan by) => _stepped -= by;

        public override DateTimeOffset GetUtcNow() => _time.GetUtcNow() + _stepped;

        public override long GetTimestamp() => _time.GetTimestamp();

        public override long TimestampFrequency => _time.TimestampFrequency;

        public override TimeZoneInfo LocalTimeZone => _time.LocalTimeZone;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => _time.CreateTimer(callback, state, dueTime, period);
    }

    private static (PumpState, string?, InvariantTrigger?) Verdict(PumpStatus status)
        => (status.State, status.FaultType, status.Trigger);

    private static WorkerGroupOptions Group(int pumps) => new()
    {
        Name = "workers",
        Policy = new DispatchPolicy.Strict(["default"]),
        PollInterval = TimeSpan.FromMilliseconds(25),
        LeaseDuration = TimeSpan.FromSeconds(5),
        HeartbeatInterval = TimeSpan.FromMinutes(10),
        Pumps = pumps,
    };

    private static WebApplication BuildHost(
        IJobStore store, TimeProvider clock, WorkerGroupOptions group, bool servicesStopConcurrently = false,
        CapturingLoggerProvider? logs = null, PingRecorder? recorder = null)
    {
        var registry = new JobRegistry(
        [
            JobRegistration.Create<PingJob, PingHandler>("ping", HostingJsonContext.Default.PingJob, "default"),
        ]);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        if (logs is not null)
        {
            builder.Logging.AddProvider(logs);
        }
        builder.Services.Configure<HostOptions>(o => o.ServicesStopConcurrently = servicesStopConcurrently);
        builder.Services.AddSingleton(clock);
        builder.Services.AddSingleton(recorder ?? new PingRecorder());
        builder.Services.AddTransient<IJobHandler<PingJob>, PingHandler>();
        builder.Services.AddBackWave(backwave =>
        {
            backwave.UseStore(store).UseRegistry(registry);
            backwave.AddWorkerGroup(group);
        });
        return builder.Build();
    }

    private static async Task WaitForAsync(Func<bool> condition, string description)
    {
        var deadline = DateTimeOffset.UtcNow + TestTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }
            await Task.Delay(25);
        }
        Assert.Fail($"Timed out waiting for: {description}");
    }
}
