using BackWave.Core;
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

    private static readonly JobRegistry BlockingRegistry = new(
    [
        JobRegistration.Create<PingJob, RecoveryHandler>("ping", HostingJsonContext.Default.PingJob),
    ]);

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
}
