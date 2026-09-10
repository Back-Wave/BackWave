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
using Microsoft.Extensions.Logging;

namespace BackWave.Hosting.Tests;

/// <summary>
/// The clean-stop hand-back: a Worker Group that stops normally relinquishes the Leases it holds, so
/// its in-flight work returns to the queue at once instead of waiting out the whole Lease duration.
/// </summary>
public class ShutdownHandBackTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan FastPoll = TimeSpan.FromMilliseconds(25);

    private const int LeasesRelinquishedEventId = 1205;
    private const int HandBackFailedEventId = 1206;
    private const int DrainIncompleteEventId = 1207;

    private static readonly JobRegistry BlockingRegistry = new(
    [
        JobRegistration.Create<PingJob, RecoveryHandler>("ping", HostingJsonContext.Default.PingJob),
    ]);

    // A second registry, because a host takes exactly one: same wire name, a handler that ignores its
    // cancellation token instead of honouring it.
    private static readonly JobRegistry StubbornRegistry = new(
    [
        JobRegistration.Create<PingJob, StubbornHandler>("ping", HostingJsonContext.Default.PingJob),
    ]);

    private static WebApplication BuildStubbornHost(
        IJobStore store,
        StubbornGate gate,
        ILoggerProvider loggerProvider,
        TimeSpan shutdownBudget,
        TimeSpan? hostShutdownTimeout = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.AddProvider(loggerProvider);
        if (hostShutdownTimeout is { } timeout)
        {
            // Registered after the web host's own, so this one wins - the window the group clamps against.
            builder.Services.Configure<Microsoft.Extensions.Hosting.HostOptions>(
                host => host.ShutdownTimeout = timeout);
        }

        builder.Services.AddSingleton(gate);
        builder.Services.AddTransient<IJobHandler<PingJob>, StubbornHandler>();
        builder.Services.AddBackWave(backwave => backwave
            .UseStore(store)
            .UseRegistry(StubbornRegistry)
            .AddWorkerGroup(new WorkerGroupOptions
            {
                Name = "workers",
                Policy = new DispatchPolicy.Strict(["default"]),
                PollInterval = FastPoll,
                LeaseDuration = TimeSpan.FromMinutes(5),
                ShutdownBudget = shutdownBudget,
            }));
        return builder.Build();
    }

    private static WebApplication BuildHost(
        IJobStore store, RecoveryGate gate, ILoggerProvider? loggerProvider = null, TimeSpan? shutdownBudget = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        if (loggerProvider is not null)
        {
            builder.Logging.AddProvider(loggerProvider);
        }

        builder.Services.AddSingleton(gate);
        builder.Services.AddTransient<IJobHandler<PingJob>, RecoveryHandler>();
        builder.Services.AddBackWave(backwave => backwave
            .UseStore(store)
            .UseRegistry(BlockingRegistry)
            .AddWorkerGroup(new WorkerGroupOptions
            {
                Name = "workers",
                Policy = new DispatchPolicy.Strict(["default"]),
                PollInterval = FastPoll,
                LeaseDuration = TimeSpan.FromMinutes(5), // long enough that a lapse cannot be mistaken for a hand-back
                ShutdownBudget = shutdownBudget ?? TimeSpan.FromSeconds(5),
            }));
        return builder.Build();
    }

    [Fact]
    public async Task CleanStop_HandsTheInFlightLeaseBack_ReadyNowAtTheSameAttempt()
    {
        var store = new InMemoryJobStore();
        var gate = new RecoveryGate();
        var logs = new CapturingLoggerProvider();
        await using var app = BuildHost(store, gate, logs);
        await app.StartAsync();

        var client = app.Services.GetRequiredService<BackWaveClient>();
        var monitor = app.Services.GetRequiredService<BackWaveMonitor>();
        var jobId = await client.EnqueueAsync(new PingJob("hand-me-back"), dueTime: DateTimeOffset.UtcNow);
        await gate.FirstAttemptStarted.Task.WaitAsync(TestTimeout);

        await app.StopAsync();
        var stoppedAt = DateTimeOffset.UtcNow;

        var job = await monitor.GetJobAsync(jobId);
        Assert.Equal(JobState.Scheduled, job!.State); // Ready, not still Leased on a node that is gone
        Assert.Null(job.LeaseOwner);
        Assert.Null(job.LeaseExpiry);
        Assert.Equal(1, job.Attempt); // the claim already charged it; the hand-back neither charges nor refunds
        Assert.True(job.DueTime <= stoppedAt, "a relinquished job skips the retry backoff and is due now");

        var relinquished = Assert.Single(logs.Entries, entry => entry.EventId == LeasesRelinquishedEventId);
        Assert.Equal(LogLevel.Information, relinquished.Level);
        Assert.Contains("relinquished 1 lease(s)", relinquished.Message);
    }

    [Fact]
    public async Task CleanStop_LogsTheTransition_NamingTheStateTheJobLandsIn()
    {
        var store = new InMemoryJobStore();
        var gate = new RecoveryGate();
        await using var app = BuildHost(store, gate);
        await app.StartAsync();

        var client = app.Services.GetRequiredService<BackWaveClient>();
        var monitor = app.Services.GetRequiredService<BackWaveMonitor>();
        var jobId = await client.EnqueueAsync(new PingJob("logged"), dueTime: DateTimeOffset.UtcNow);
        await gate.FirstAttemptStarted.Task.WaitAsync(TestTimeout);

        await app.StopAsync();

        var transitions = await monitor.GetJobHistoryAsync(jobId);
        var last = transitions[^1];
        Assert.Equal(JobState.Scheduled, last.State);
        Assert.Equal(1, last.Attempt); // the same post-claim Attempt, unchanged by the hand-back
    }

    [Fact]
    public async Task HandBackFailure_LogsAWarning_AndStillLetsTheHostStop()
    {
        var store = new FaultableStore(new InMemoryJobStore()) { FailRelinquish = true };
        var gate = new RecoveryGate();
        var logs = new CapturingLoggerProvider();
        await using var app = BuildHost(store, gate, logs);
        await app.StartAsync();

        var client = app.Services.GetRequiredService<BackWaveClient>();
        var monitor = app.Services.GetRequiredService<BackWaveMonitor>();
        var jobId = await client.EnqueueAsync(new PingJob("unreachable-store"), dueTime: DateTimeOffset.UtcNow);
        await gate.FirstAttemptStarted.Task.WaitAsync(TestTimeout);

        await app.StopAsync().WaitAsync(TestTimeout); // the hand-back never blocks the host from exiting

        var warning = Assert.Single(logs.Entries, entry => entry.EventId == HandBackFailedEventId);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Equal(JobState.Leased, (await monitor.GetJobAsync(jobId))!.State); // degraded to today's lapse
    }

    [Fact]
    public async Task ZeroShutdownBudget_SkipsTheHandBack_LeavingTheLeaseToLapse()
    {
        var store = new FaultableStore(new InMemoryJobStore());
        var gate = new RecoveryGate();
        await using var app = BuildHost(store, gate, shutdownBudget: TimeSpan.Zero);
        await app.StartAsync();

        var client = app.Services.GetRequiredService<BackWaveClient>();
        var monitor = app.Services.GetRequiredService<BackWaveMonitor>();
        var jobId = await client.EnqueueAsync(new PingJob("opted-out"), dueTime: DateTimeOffset.UtcNow);
        await gate.FirstAttemptStarted.Task.WaitAsync(TestTimeout);

        await app.StopAsync();

        Assert.Equal(0, store.RelinquishCalls);
        Assert.Equal(JobState.Leased, (await monitor.GetJobAsync(jobId))!.State);
    }

    [Fact]
    public async Task CleanStop_WaitsOutAHandlerThatIgnoresItsToken_BeforeRelinquishingItsLease()
    {
        // Nothing used to wait for the executions, so a handler that does not honor its cancellation token
        // was still running while the relinquish handed its job to another node - which would then run the
        // very same Attempt concurrently instead of after the Lease lapsed. The hand-back waits first.
        var store = new FaultableStore(new InMemoryJobStore());
        var gate = new StubbornGate { Hold = TimeSpan.FromMilliseconds(500) };
        var logs = new CapturingLoggerProvider();
        await using var app = BuildStubbornHost(store, gate, logs, shutdownBudget: TimeSpan.FromSeconds(10));
        await app.StartAsync();

        var client = app.Services.GetRequiredService<BackWaveClient>();
        var monitor = app.Services.GetRequiredService<BackWaveMonitor>();
        var jobId = await client.EnqueueAsync(new PingJob("stubborn"), dueTime: DateTimeOffset.UtcNow);
        await gate.Started.Task.WaitAsync(TestTimeout);

        await app.StopAsync();

        Assert.NotNull(gate.FinishedAt);
        Assert.NotNull(store.RelinquishedAt);
        Assert.True(
            gate.FinishedAt <= store.RelinquishedAt,
            $"the handler finished at {gate.FinishedAt:o} but its Lease was relinquished at {store.RelinquishedAt:o}");
        Assert.DoesNotContain(logs.Entries, entry => entry.EventId == DrainIncompleteEventId);
        // Succeeded, not Scheduled. The handler completed during the wait, so its outcome was written to a
        // channel the pump loop had already stopped reading. The hand-back drains that channel before it
        // relinquishes, so the success is reported. Left undrained, the relinquish would return a job that
        // had genuinely succeeded to Scheduled at the same Attempt, and a healthy node would run it again.
        Assert.Equal(JobState.Succeeded, (await monitor.GetJobAsync(jobId))!.State);
    }

    [Fact]
    public async Task AHandlerThatOutlastsTheBudget_IsLoggedAndItsLeaseRelinquishedAnyway()
    {
        // Best-effort, like the rest of the hand-back: the wait is bounded by the same one budget, so a
        // handler that never returns degrades to today's behaviour rather than holding the host open.
        var store = new FaultableStore(new InMemoryJobStore());
        var gate = new StubbornGate { Hold = TimeSpan.FromSeconds(2) };
        var logs = new CapturingLoggerProvider();
        await using var app = BuildStubbornHost(store, gate, logs, shutdownBudget: TimeSpan.FromMilliseconds(250));
        await app.StartAsync();

        var client = app.Services.GetRequiredService<BackWaveClient>();
        var monitor = app.Services.GetRequiredService<BackWaveMonitor>();
        var jobId = await client.EnqueueAsync(new PingJob("never-returns"), dueTime: DateTimeOffset.UtcNow);
        await gate.Started.Task.WaitAsync(TestTimeout);

        await app.StopAsync().WaitAsync(TestTimeout);

        var warning = Assert.Single(logs.Entries, entry => entry.EventId == DrainIncompleteEventId);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains("1 execution(s) running", warning.Message);
        Assert.Null(gate.FinishedAt);  // it really had not finished
        Assert.Equal(1, store.RelinquishCalls);  // and its Lease was given back regardless
        Assert.Equal(JobState.Scheduled, (await monitor.GetJobAsync(jobId))!.State);
    }

    [Fact]
    public async Task AShutdownBudgetLongerThanTheHostAllows_IsClampedBelowTheHostsOwnTimeout()
    {
        // The budget is documented as clamped against HostOptions.ShutdownTimeout, and now is: without the
        // clamp this hand-back would spend a minute while the host stops waiting after two and a half
        // seconds, so the relinquish this test asserts on would not have happened yet when StopAsync
        // returned - the mid-relinquish kill the clamp exists to prevent.
        var store = new FaultableStore(new InMemoryJobStore());
        var gate = new StubbornGate { Hold = TimeSpan.FromSeconds(30) };
        var logs = new CapturingLoggerProvider();
        await using var app = BuildStubbornHost(
            store, gate, logs,
            shutdownBudget: TimeSpan.FromMinutes(1),
            hostShutdownTimeout: TimeSpan.FromMilliseconds(2500));
        await app.StartAsync();

        var client = app.Services.GetRequiredService<BackWaveClient>();
        var jobId = await client.EnqueueAsync(new PingJob("clamped"), dueTime: DateTimeOffset.UtcNow);
        await gate.Started.Task.WaitAsync(TestTimeout);

        await app.StopAsync().WaitAsync(TestTimeout);

        // Four fifths of the host's window, so the relinquish lands with the last fifth to spare.
        Assert.Single(logs.Entries, entry => entry.EventId == DrainIncompleteEventId);
        Assert.Equal(1, store.RelinquishCalls);
        Assert.Equal(
            JobState.Scheduled,
            (await app.Services.GetRequiredService<BackWaveMonitor>().GetJobAsync(jobId))!.State);
    }

    [Fact]
    public async Task FailStoppedGroup_HandsNothingBack_BecauseAHaltedPumpWritesNoMore()
    {
        var store = new FaultableStore(new InMemoryJobStore());
        var gate = new RecoveryGate();
        await using var app = BuildHost(store, gate);
        await app.StartAsync();

        var client = app.Services.GetRequiredService<BackWaveClient>();
        var monitor = app.Services.GetRequiredService<BackWaveMonitor>();
        var jobId = await client.EnqueueAsync(new PingJob("halt-me"), dueTime: DateTimeOffset.UtcNow);
        await gate.FirstAttemptStarted.Task.WaitAsync(TestTimeout);

        store.FailEverything = true; // the forced invariant violation
        var health = app.Services.GetRequiredService<BackWaveHealth>();
        var deadline = DateTimeOffset.UtcNow + TestTimeout;
        while (health.IsHealthy && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(25);
        }
        Assert.False(health.IsHealthy, "the group should have fail-stopped while holding the Lease");

        await app.StopAsync();

        Assert.Equal(0, store.RelinquishCalls);
        Assert.Equal(JobState.Leased, (await monitor.GetJobAsync(jobId))!.State);
    }

    [Fact]
    public async Task CleanStop_ReportsTheBufferedOutcome_BeforeItReachesTheRelinquish()
    {
        // The order of the two store calls, not the state they leave behind. A flush that lands AFTER the
        // relinquish writes into rows the relinquish already handed back, so every outcome in it fences
        // out as StaleLease and the work is silently re-run. The final state alone cannot tell the two
        // orders apart, which is how the loss this test now pins survived review.
        var store = new FaultableStore(new InMemoryJobStore());
        var gate = new StubbornGate { Hold = TimeSpan.FromMilliseconds(500) };
        var logs = new CapturingLoggerProvider();
        await using var app = BuildStubbornHost(store, gate, logs, shutdownBudget: TimeSpan.FromSeconds(10));
        await app.StartAsync();

        var client = app.Services.GetRequiredService<BackWaveClient>();
        var monitor = app.Services.GetRequiredService<BackWaveMonitor>();
        var jobId = await client.EnqueueAsync(new PingJob("flush-first"), dueTime: DateTimeOffset.UtcNow);
        await gate.Started.Task.WaitAsync(TestTimeout);

        await app.StopAsync();

        Assert.NotNull(store.LastOutcomeReportAt);
        Assert.NotNull(store.RelinquishedAt);
        Assert.True(
            store.LastOutcomeReportAt <= store.RelinquishedAt,
            $"the buffer flushed at {store.LastOutcomeReportAt:o}, after the relinquish at {store.RelinquishedAt:o}");
        Assert.Equal(1, store.RelinquishCalls);
        Assert.Equal(JobState.Succeeded, (await monitor.GetJobAsync(jobId))!.State);
    }

    [Fact]
    public async Task AStoreTooSlowForItsReservedSlice_IsCancelledInside_AndTheFailureIsLogged()
    {
        // The other end of the budget: the store is reachable and simply slow. Its own token expires with
        // the call still open, so the hand-back degrades to today's lapse rather than holding the host.
        // Nothing but a store that honors its token can reach this branch, which is why it was untested.
        var store = new FaultableStore(new InMemoryJobStore()) { RelinquishDelay = TimeSpan.FromSeconds(5) };
        var gate = new RecoveryGate();
        var logs = new CapturingLoggerProvider();
        await using var app = BuildHost(store, gate, logs, shutdownBudget: TimeSpan.FromMilliseconds(500));
        await app.StartAsync();

        var client = app.Services.GetRequiredService<BackWaveClient>();
        var monitor = app.Services.GetRequiredService<BackWaveMonitor>();
        var jobId = await client.EnqueueAsync(new PingJob("slow-store"), dueTime: DateTimeOffset.UtcNow);
        await gate.FirstAttemptStarted.Task.WaitAsync(TestTimeout);

        await app.StopAsync().WaitAsync(TestTimeout);

        var warning = Assert.Single(logs.Entries, entry => entry.EventId == HandBackFailedEventId);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Equal(1, store.RelinquishCalls); // it was reached, and it ran out of its slice inside
        Assert.DoesNotContain(logs.Entries, entry => entry.EventId == LeasesRelinquishedEventId);
        Assert.Equal(JobState.Leased, (await monitor.GetJobAsync(jobId))!.State);
    }

    [Fact]
    public async Task AnAdapterBuiltBeforeTheRelinquish_KeepsTodaysLapse_AndStopsCleanly()
    {
        // The N-1 half of a rolling deploy: a newer Shell over an adapter that never implemented the
        // relinquish. The default body on IJobStore answers zero, so the Leases lapse and the expiry path
        // sweeps them - the behaviour that shipped before this feature - and nothing is logged as failed.
        var store = new PreRelinquishStore(new InMemoryJobStore());
        var gate = new RecoveryGate();
        var logs = new CapturingLoggerProvider();
        await using var app = BuildHost(store, gate, logs);
        await app.StartAsync();

        var client = app.Services.GetRequiredService<BackWaveClient>();
        var monitor = app.Services.GetRequiredService<BackWaveMonitor>();
        var jobId = await client.EnqueueAsync(new PingJob("older-adapter"), dueTime: DateTimeOffset.UtcNow);
        await gate.FirstAttemptStarted.Task.WaitAsync(TestTimeout);

        await app.StopAsync().WaitAsync(TestTimeout);

        Assert.DoesNotContain(logs.Entries, entry => entry.EventId == LeasesRelinquishedEventId);
        Assert.DoesNotContain(logs.Entries, entry => entry.EventId == HandBackFailedEventId);
        Assert.Equal(JobState.Leased, (await monitor.GetJobAsync(jobId))!.State);
    }

    [Fact]
    public async Task AViolationRaisedWhileTheHandBackDrains_HaltsTheGroup_AndHandsNothingBack()
    {
        // A named check trips inside a handler that is still running when the stop begins. The pump loop
        // that would have raised it is already gone, so the violation sits in the completed channel with
        // nobody reading. Unread it would be lost entirely: the group would report green, the trigger
        // nothing else records would vanish, and the relinquish would hand back rows a halted group must
        // leave alone.
        var store = new FaultableStore(new InMemoryJobStore());
        var gate = new StubbornGate
        {
            Hold = TimeSpan.FromMilliseconds(300),
            Violation = InvariantTrigger.WorkflowMemberWithoutWorkflow,
        };
        var logs = new CapturingLoggerProvider();
        await using var app = BuildStubbornHost(store, gate, logs, shutdownBudget: TimeSpan.FromSeconds(10));
        await app.StartAsync();

        var client = app.Services.GetRequiredService<BackWaveClient>();
        await client.EnqueueAsync(new PingJob("violate-mid-drain"), dueTime: DateTimeOffset.UtcNow);
        await gate.Started.Task.WaitAsync(TestTimeout);

        await app.StopAsync().WaitAsync(TestTimeout);

        var health = app.Services.GetRequiredService<BackWaveHealth>();
        Assert.False(health.IsHealthy);
        var halt = Assert.Contains("workers", health.HaltedGroups);
        Assert.Equal(InvariantTrigger.WorkflowMemberWithoutWorkflow, halt.Trigger);
        var critical = Assert.Single(logs.Entries, entry => entry.EventId == 2001);
        Assert.Equal(LogLevel.Critical, critical.Level);
        Assert.Contains(nameof(InvariantTrigger.WorkflowMemberWithoutWorkflow), critical.Message);
        Assert.Equal(0, store.RelinquishCalls); // a halting group writes nothing more to the store
    }
}

/// <summary>
/// What a handler that does not honor its cancellation token looks like: it runs out a fixed hold no
/// matter what the token says, and stamps the instant it finally returned so a test can order that
/// against the hand-back's relinquish.
/// </summary>
public sealed class StubbornGate
{
    /// <summary>Completes once the handler is genuinely running, so a test can stop into a live execution.</summary>
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>How long the handler runs, measured from the moment it starts.</summary>
    public TimeSpan Hold { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>When the handler returned, or <see langword="null"/> if it never has.</summary>
    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>When set, the handler raises a named violation at the end of its hold instead of returning.</summary>
    public InvariantTrigger? Violation { get; init; }
}

/// <summary>Runs out its gate's hold with the cancellation token ignored entirely.</summary>
public sealed class StubbornHandler(StubbornGate gate) : IJobHandler<PingJob>
{
    /// <inheritdoc/>
    public async Task HandleAsync(PingJob job, JobContext context, CancellationToken cancellationToken)
    {
        gate.Started.TrySetResult();
        await Task.Delay(gate.Hold, CancellationToken.None);
        gate.FinishedAt = DateTimeOffset.UtcNow;
        if (gate.Violation is { } trigger)
        {
            throw new InvariantViolationException(trigger, "forced named violation from inside a handler");
        }
    }
}
