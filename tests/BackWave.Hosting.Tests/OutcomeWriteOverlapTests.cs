using System.Collections.Concurrent;
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

public sealed class PingBoard
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _ran = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _finish = new();
    private readonly ConcurrentDictionary<string, bool> _finishOnStop = new();

    public Task Ran(string name) => Signal(_ran, name).Task;

    public bool HasRun(string name) => Signal(_ran, name).Task.IsCompleted;

    public void Hold(string name) => Signal(_finish, name);

    public void Finish(string name) => Signal(_finish, name).TrySetResult();

    public void FinishOnStop(string name) => _finishOnStop[name] = true;

    internal async Task RunAsync(string name, CancellationToken cancellationToken)
    {
        Signal(_ran, name).TrySetResult();
        if (_finishOnStop.ContainsKey(name))
        {
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }
        }
        else if (_finish.TryGetValue(name, out var held))
        {
            await held.Task;
        }
    }

    private static TaskCompletionSource Signal(ConcurrentDictionary<string, TaskCompletionSource> signals, string name)
        => signals.GetOrAdd(name, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
}

public sealed class BoardPingHandler(PingBoard board) : IJobHandler<PingJob>
{
    public Task HandleAsync(PingJob job, JobContext context, CancellationToken cancellationToken)
        => board.RunAsync(job.Name, cancellationToken);
}

public class OutcomeWriteOverlapTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan FastPoll = TimeSpan.FromMilliseconds(25);
    private const int HandBackFailedEventId = 1206;

    private static readonly JobRegistry Registry = new(
    [
        JobRegistration.Create<PingJob, BoardPingHandler>("ping", HostingJsonContext.Default.PingJob),
    ]);

    private static WebApplication BuildHost(
        IJobStore store, PingBoard board, int poolSize, ILoggerProvider? logs = null, TimeSpan? shutdownBudget = null,
        int? maxOutcomeBatch = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        if (logs is not null)
        {
            builder.Logging.AddProvider(logs);
        }
        builder.Services.AddSingleton(board);
        builder.Services.AddTransient<IJobHandler<PingJob>, BoardPingHandler>();
        builder.Services.AddBackWave(backwave => backwave
            .UseStore(store)
            .UseRegistry(Registry)
            .AddWorkerGroup(new WorkerGroupOptions
            {
                Name = "workers",
                Policy = new DispatchPolicy.Strict(["default"]),
                PollInterval = FastPoll,
                PoolSize = poolSize,
                LeaseDuration = TimeSpan.FromMinutes(5),
                ShutdownBudget = shutdownBudget ?? TimeSpan.FromSeconds(5),
                MaxOutcomeBatch = maxOutcomeBatch,
            }));
        return builder.Build();
    }

    private static async Task WaitForStateAsync(BackWaveMonitor monitor, Guid jobId, JobState state)
    {
        var deadline = DateTimeOffset.UtcNow + TestTimeout;
        while ((await monitor.GetJobAsync(jobId))?.State != state)
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, $"job {jobId} never reached {state}");
            await Task.Delay(25);
        }
    }

    [Fact]
    public async Task TheNextJob_IsClaimedAndRun_WhileAnOutcomeWriteIsInFlight()
    {
        var store = new FaultableStore(new InMemoryJobStore()) { HoldOutcomeWrites = true };
        var board = new PingBoard();
        await using var app = BuildHost(store, board, poolSize: 2);
        await app.StartAsync();
        var client = app.Services.GetRequiredService<BackWaveClient>();
        var monitor = app.Services.GetRequiredService<BackWaveMonitor>();

        var first = await client.EnqueueAsync(new PingJob("first"), dueTime: DateTimeOffset.UtcNow);
        await store.HeldOutcomeWriteEntered.WaitAsync(TestTimeout);
        var second = await client.EnqueueAsync(new PingJob("second"), dueTime: DateTimeOffset.UtcNow);

        await board.Ran("second").WaitAsync(TestTimeout);
        Assert.Equal(JobState.Leased, (await monitor.GetJobAsync(first))!.State);

        store.ReleaseOutcomeWrites();
        await WaitForStateAsync(monitor, first, JobState.Succeeded);
        await WaitForStateAsync(monitor, second, JobState.Succeeded);
        await app.StopAsync();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task AMaxOutcomeBatchBelowOne_WritesEachOutcomeOnItsOwn(int maxOutcomeBatch)
    {
        var store = new FaultableStore(new InMemoryJobStore());
        await using var app = BuildHost(store, new PingBoard(), poolSize: 2, maxOutcomeBatch: maxOutcomeBatch);
        await app.StartAsync();
        var client = app.Services.GetRequiredService<BackWaveClient>();
        var monitor = app.Services.GetRequiredService<BackWaveMonitor>();

        Guid[] jobs =
        [
            await client.EnqueueAsync(new PingJob("first"), dueTime: DateTimeOffset.UtcNow),
            await client.EnqueueAsync(new PingJob("second"), dueTime: DateTimeOffset.UtcNow),
            await client.EnqueueAsync(new PingJob("third"), dueTime: DateTimeOffset.UtcNow),
        ];

        foreach (var job in jobs)
        {
            await WaitForStateAsync(monitor, job, JobState.Succeeded);
        }
        Assert.Equal(jobs.Length, store.ReportOutcomesCalls);
        await app.StopAsync();
    }

    [Fact]
    public async Task ARowBeingWritten_KeepsItsPoolSlot_UntilTheWriteAnswers()
    {
        var store = new FaultableStore(new InMemoryJobStore()) { HoldOutcomeWrites = true };
        var board = new PingBoard();
        await using var app = BuildHost(store, board, poolSize: 1);
        await app.StartAsync();
        var client = app.Services.GetRequiredService<BackWaveClient>();
        var monitor = app.Services.GetRequiredService<BackWaveMonitor>();

        var first = await client.EnqueueAsync(new PingJob("first"), dueTime: DateTimeOffset.UtcNow);
        await store.HeldOutcomeWriteEntered.WaitAsync(TestTimeout);
        var second = await client.EnqueueAsync(new PingJob("second"), dueTime: DateTimeOffset.UtcNow);

        await Task.Delay(FastPoll * 20);
        Assert.False(board.HasRun("second"), "the second job ran while the first job's row still held the only slot");
        Assert.Equal(JobState.Scheduled, (await monitor.GetJobAsync(second))!.State);

        store.ReleaseOutcomeWrites();
        await WaitForStateAsync(monitor, first, JobState.Succeeded);
        await WaitForStateAsync(monitor, second, JobState.Succeeded);
        await app.StopAsync();
    }

    [Fact]
    public async Task ACleanStop_LandsTheOutcomeWriteInFlight_BeforeItRelinquishes()
    {
        var store = new FaultableStore(new InMemoryJobStore()) { HoldOutcomeWrites = true };
        var board = new PingBoard();
        await using var app = BuildHost(store, board, poolSize: 2);
        await app.StartAsync();
        var client = app.Services.GetRequiredService<BackWaveClient>();
        var monitor = app.Services.GetRequiredService<BackWaveMonitor>();

        var jobId = await client.EnqueueAsync(new PingJob("finished"), dueTime: DateTimeOffset.UtcNow);
        await store.HeldOutcomeWriteEntered.WaitAsync(TestTimeout);

        var stopping = app.StopAsync();
        await Task.Delay(FastPoll * 8);
        Assert.Equal(0, store.RelinquishCalls);
        store.ReleaseOutcomeWrites();
        await stopping.WaitAsync(TestTimeout);

        Assert.Equal(JobState.Succeeded, (await monitor.GetJobAsync(jobId))!.State);
        Assert.Equal(1, store.RelinquishCalls);
        Assert.True(
            store.LastOutcomeReportAt <= store.RelinquishedAt,
            $"the write landed at {store.LastOutcomeReportAt:o}, after the relinquish at {store.RelinquishedAt:o}");
    }

    [Fact]
    public async Task AnOutcome_IsWritten_WhileAClaimIsInFlight()
    {
        var store = new FaultableStore(new InMemoryJobStore());
        var board = new PingBoard();
        board.Hold("running");
        await using var app = BuildHost(store, board, poolSize: 2);
        await app.StartAsync();
        var client = app.Services.GetRequiredService<BackWaveClient>();
        var monitor = app.Services.GetRequiredService<BackWaveMonitor>();

        var running = await client.EnqueueAsync(new PingJob("running"), dueTime: DateTimeOffset.UtcNow);
        await board.Ran("running").WaitAsync(TestTimeout);
        store.HoldClaims = true;
        await store.HeldClaimEntered.WaitAsync(TestTimeout);

        board.Finish("running");
        await WaitForStateAsync(monitor, running, JobState.Succeeded);

        store.ReleaseClaims();
        await app.StopAsync();
    }

    [Fact]
    public async Task ACleanStop_WaitsForTheClaimInFlight_AndHandsBackWhatItLeased()
    {
        var store = new FaultableStore(new InMemoryJobStore()) { HoldClaims = true };
        var board = new PingBoard();
        await using var app = BuildHost(store, board, poolSize: 2);
        await app.StartAsync();
        var client = app.Services.GetRequiredService<BackWaveClient>();
        var monitor = app.Services.GetRequiredService<BackWaveMonitor>();
        await store.HeldClaimEntered.WaitAsync(TestTimeout);
        var jobId = await client.EnqueueAsync(new PingJob("late"), dueTime: DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1));

        var stopping = app.StopAsync();
        await Task.Delay(FastPoll * 8);
        Assert.Equal(0, store.RelinquishCalls);
        store.ReleaseClaims();
        await stopping.WaitAsync(TestTimeout);

        Assert.False(board.HasRun("late"), "a stopping pump ran a job its last claim leased");
        Assert.Equal(1, store.RelinquishCalls);
        Assert.True(
            store.LastClaimAt <= store.RelinquishedAt,
            $"the claim landed at {store.LastClaimAt:o}, after the relinquish at {store.RelinquishedAt:o}");
        var late = (await monitor.GetJobAsync(jobId))!;
        Assert.Equal(JobState.Scheduled, late.State);
        Assert.Equal(1, late.Attempt);
    }

    [Fact]
    public async Task ACleanStop_CancelsTheClaimInFlight_InsteadOfWaitingOutItsBudget()
    {
        var store = new FaultableStore(new InMemoryJobStore()) { ClaimsHangUntilCancelled = true };
        var logs = new CapturingLoggerProvider();
        await using var app = BuildHost(store, new PingBoard(), poolSize: 2, logs);
        await app.StartAsync();
        await store.HangingClaimEntered.WaitAsync(TestTimeout);

        await app.StopAsync().WaitAsync(TestTimeout);

        Assert.True(store.HangingClaimCancelled.IsCompleted);
        Assert.Equal(1, store.RelinquishCalls);
        Assert.DoesNotContain(logs.Entries, e => e.EventId == HandBackFailedEventId);
    }

    [Fact]
    public async Task ACleanStop_CancelsAnOutcomeWriteThatOutlastsItsBudget_AndStillRelinquishes()
    {
        var store = new FaultableStore(new InMemoryJobStore()) { HoldOutcomeWrites = true };
        var board = new PingBoard();
        board.FinishOnStop("finishes-on-stop");
        var logs = new CapturingLoggerProvider();
        await using var app = BuildHost(store, board, poolSize: 2, logs, shutdownBudget: TimeSpan.FromSeconds(1));
        await app.StartAsync();
        var client = app.Services.GetRequiredService<BackWaveClient>();
        var monitor = app.Services.GetRequiredService<BackWaveMonitor>();
        var jobId = await client.EnqueueAsync(new PingJob("finishes-on-stop"), dueTime: DateTimeOffset.UtcNow);
        await board.Ran("finishes-on-stop").WaitAsync(TestTimeout);

        await app.StopAsync().WaitAsync(TestTimeout);

        Assert.True(store.HeldOutcomeWriteEntered.IsCompleted);
        Assert.Single(logs.Entries, e => e.EventId == HandBackFailedEventId);
        Assert.Equal(1, store.RelinquishCalls);
        Assert.Equal(JobState.Scheduled, (await monitor.GetJobAsync(jobId))!.State);
    }

    [Fact]
    public async Task AHaltingPump_CancelsTheClaimInFlight()
    {
        var store = new FaultableStore(new InMemoryJobStore());
        var board = new PingBoard();
        board.Hold("running");
        await using var app = BuildHost(store, board, poolSize: 2);
        await app.StartAsync();
        var client = app.Services.GetRequiredService<BackWaveClient>();
        await client.EnqueueAsync(new PingJob("running"), dueTime: DateTimeOffset.UtcNow);
        await board.Ran("running").WaitAsync(TestTimeout);
        store.ClaimsHangUntilCancelled = true;
        await store.HangingClaimEntered.WaitAsync(TestTimeout);

        store.DropOutcomeResults = 1;
        board.Finish("running");

        await store.HangingClaimCancelled.WaitAsync(TestTimeout);
        var health = app.Services.GetRequiredService<BackWaveHealth>();
        var deadline = DateTimeOffset.UtcNow + TestTimeout;
        while (!health.HaltedGroups.ContainsKey("workers"))
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, "the pump never halted");
            await Task.Delay(25);
        }
        await app.StopAsync();
    }
}
