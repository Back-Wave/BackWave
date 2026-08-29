using BackWave.Core;
using BackWave.Jobs;
using BackWave.Monitor;
using BackWave.Storage;
using BackWave.Storage.InMemory;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace BackWave.Hosting.Tests;

/// <summary>
/// Emits an oversized Job Output for any job whose Key starts with "big", and a tiny one otherwise -
/// the "one handler returns too much" defect, next to a well-behaved sibling on the same pump.
/// </summary>
public sealed class OversizedOutputHandler(Barrier barrier) : IJobHandler<OutputJob>
{
    public Task HandleAsync(OutputJob job, JobContext context, CancellationToken cancellationToken)
    {
        var body = job.Key.StartsWith("big", StringComparison.Ordinal) ? new string('x', 4096) : "ok";
        context.SetOutput(new OutputPayload(job.Key, body), OutputJsonContext.Default.OutputPayload);
        // Hold every handler until all of them have buffered their output, so their outcome writes hit
        // the Shell's batch drain in one window rather than one at a time.
        barrier.SignalAndWait(TimeSpan.FromSeconds(10));
        return Task.CompletedTask;
    }
}

/// <summary>
/// An over-cap Job Output is a defect in ONE job, not an invariant violation: the store rejects it loudly
/// (it is never truncated), and that rejection used to reach the pump's fail-stop catch and halt the whole
/// Worker Group - every healthy job on it stranded. It must dead-letter the offending job with a clear
/// cause and leave the group claiming and executing.
/// <para>
/// Both store shapes are covered, because the rejection has two throw sites: the per-row check on the
/// success-path output write (the In-Memory Store applies a batch row by row through it, so rows ahead of
/// the offender are already written when it throws) and the adapters' whole-batch pre-scan (which rejects
/// every row before writing any).
/// </para>
/// </summary>
public class JobOutputTooLargeTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan FastPoll = TimeSpan.FromMilliseconds(25);

    // Small enough that the 4096-char body blows it and the "ok" body fits comfortably.
    private const int OutputCap = 64;

    [Fact]
    public async Task OversizedOutput_OnThePerRowCheck_DeadLettersTheJobAndLeavesTheGroupHealthy()
    {
        // The In-Memory Store has no batch override, so the batch applies row by row and the rejection
        // comes from the per-row check that guards the success-path output write.
        var store = new InMemoryJobStore(bounds: new StoreBounds { MaxOutputBytes = OutputCap });
        var barrier = new Barrier(2);
        await using var app = await StartGroupAsync(store, barrier, poolSize: 2);

        var client = app.Services.GetRequiredService<BackWaveClient>();
        var monitor = app.Services.GetRequiredService<BackWaveMonitor>();
        var health = app.Services.GetRequiredService<BackWaveHealth>();

        var oversized = await client.EnqueueAsync(new OutputJob("big-1"), dueTime: DateTimeOffset.UtcNow);
        var healthy = await client.EnqueueAsync(new OutputJob("small-1"), dueTime: DateTimeOffset.UtcNow);

        await AwaitTerminalAsync(monitor, oversized, healthy);

        // The offending job dead-letters with a cause naming the rejection, its sibling in the same drain
        // still succeeds with its own output, and the group never halted.
        var dead = await monitor.GetJobAsync(oversized);
        Assert.Equal(JobState.DeadLettered, dead!.State);
        Assert.Contains("Job Output", dead.TerminalCause);
        Assert.Null(await monitor.GetJobOutputAsync(oversized));

        Assert.Equal(JobState.Succeeded, (await monitor.GetJobAsync(healthy))?.State);
        Assert.NotNull(await monitor.GetJobOutputAsync(healthy));

        Assert.True(health.IsHealthy);
        Assert.Empty(health.HaltedGroups);
        Assert.Empty(health.PartiallyHaltedGroups);

        // The group is still live: a job enqueued after the rejection still runs.
        var after = await client.EnqueueAsync(new OutputJob("small-2"), dueTime: DateTimeOffset.UtcNow);
        barrier.RemoveParticipant(); // "small-2" runs alone
        await AwaitTerminalAsync(monitor, after);
        Assert.Equal(JobState.Succeeded, (await monitor.GetJobAsync(after))?.State);

        await app.StopAsync();
    }

    [Fact]
    public async Task OversizedOutput_OnTheBatchPreScan_DeadLettersTheJobAndSettlesItsSiblings()
    {
        // The adapters scan the WHOLE batch before writing anything, so one over-cap row rejects every
        // row - the sibling outcome must still land rather than being lost with the offender.
        var store = new FaultableStore(new InMemoryJobStore()) { BatchOutputCap = OutputCap };
        var barrier = new Barrier(2);
        await using var app = await StartGroupAsync(store, barrier, poolSize: 2);

        var client = app.Services.GetRequiredService<BackWaveClient>();
        var monitor = app.Services.GetRequiredService<BackWaveMonitor>();
        var health = app.Services.GetRequiredService<BackWaveHealth>();

        var oversized = await client.EnqueueAsync(new OutputJob("big-1"), dueTime: DateTimeOffset.UtcNow);
        var healthy = await client.EnqueueAsync(new OutputJob("small-1"), dueTime: DateTimeOffset.UtcNow);

        await AwaitTerminalAsync(monitor, oversized, healthy);

        var dead = await monitor.GetJobAsync(oversized);
        Assert.Equal(JobState.DeadLettered, dead!.State);
        Assert.Contains("Job Output", dead.TerminalCause);
        Assert.Null(await monitor.GetJobOutputAsync(oversized));

        Assert.Equal(JobState.Succeeded, (await monitor.GetJobAsync(healthy))?.State);
        Assert.NotNull(await monitor.GetJobOutputAsync(healthy));

        Assert.True(health.IsHealthy);
        Assert.Empty(health.HaltedGroups);
        Assert.Empty(health.PartiallyHaltedGroups);

        await app.StopAsync();
    }

    private static async Task<WebApplication> StartGroupAsync(IJobStore store, Barrier barrier, int poolSize)
    {
        var registry = new JobRegistry(
        [
            JobRegistration.Create<OutputJob, OversizedOutputHandler>(
                "oversized-output", OutputJsonContext.Default.OutputJob, "default"),
        ]);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(barrier);
        builder.Services.AddTransient<IJobHandler<OutputJob>, OversizedOutputHandler>();
        builder.Services.AddBackWave(backwave => backwave
            .UseStore(store)
            .UseRegistry(registry)
            .AddWorkerGroup(new WorkerGroupOptions
            {
                Name = "workers",
                Policy = new DispatchPolicy.Strict(["default"]),
                PollInterval = FastPoll,
                // PoolSize >= the job count so both run concurrently and the Barrier can release them
                // together into one outcome batch drain.
                PoolSize = poolSize,
                LeaseDuration = TimeSpan.FromSeconds(5),
            }));

        var app = builder.Build();
        await app.StartAsync();
        return app;
    }

    private static async Task AwaitTerminalAsync(BackWaveMonitor monitor, params Guid[] jobIds)
    {
        var deadline = DateTimeOffset.UtcNow + TestTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var states = await Task.WhenAll(jobIds.Select(async id => (await monitor.GetJobAsync(id))?.State));
            if (states.All(state => state is { } present && present.IsTerminal()))
            {
                return;
            }
            await Task.Delay(25);
        }
    }
}
