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
/// Adds an over-long Job Tag for any job whose Key starts with "big", and an in-bound one otherwise -
/// the "one handler tags too much" defect, next to a well-behaved sibling on the same pump.
/// </summary>
public sealed class OverLongTagHandler(Barrier barrier) : IJobHandler<OutputJob>
{
    public Task HandleAsync(OutputJob job, JobContext context, CancellationToken cancellationToken)
    {
        var length = job.Key.StartsWith("big", StringComparison.Ordinal) ? JobTagTooLongTests.TagCap + 1 : JobTagTooLongTests.TagCap;
        context.AddTag("note", new string('v', length));
        // Hold every handler until all of them have buffered their tag, so their outcome writes hit the
        // Shell's batch drain in one window rather than one at a time.
        barrier.SignalAndWait(TimeSpan.FromSeconds(10));
        return Task.CompletedTask;
    }
}

/// <summary>
/// An over-long Job Tag from a handler is a defect in ONE job, not an invariant violation: the store
/// rejects it loudly (it is never truncated), and that rejection must dead-letter the offending job with a
/// clear cause and leave the Worker Group claiming and executing - the same shape as an over-cap Job
/// Output, not a fail-stop of every healthy job on the group.
/// </summary>
public class JobTagTooLongTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan FastPoll = TimeSpan.FromMilliseconds(25);

    /// <summary>Small enough that one character over is a clear rejection and exactly-at fits.</summary>
    public const int TagCap = 16;

    [Fact]
    public async Task OverLongTag_AgainstTheStoresOwnBound_DeadLettersTheJobAndLeavesTheGroupHealthy()
    {
        // The real store against its real bounds, with no test double in the path: the In-Memory Store
        // pre-scans the batch against its own MaxTagValueLength and rejects every row before writing any.
        var store = new InMemoryJobStore(bounds: new StoreBounds { MaxTagValueLength = TagCap });
        var barrier = new Barrier(2);
        await using var app = await StartGroupAsync(store, barrier, poolSize: 2);

        var client = app.Services.GetRequiredService<BackWaveClient>();
        var monitor = app.Services.GetRequiredService<BackWaveMonitor>();
        var health = app.Services.GetRequiredService<BackWaveHealth>();

        var overLong = await client.EnqueueAsync(new OutputJob("big-1"), dueTime: DateTimeOffset.UtcNow);
        var healthy = await client.EnqueueAsync(new OutputJob("small-1"), dueTime: DateTimeOffset.UtcNow);

        await AwaitTerminalAsync(monitor, overLong, healthy);

        // The offending job dead-letters with a cause naming the rejection and none of its tag delta, its
        // sibling in the same drain still succeeds with its at-bound tag, and the group never halted.
        var dead = await monitor.GetJobAsync(overLong);
        Assert.Equal(JobState.DeadLettered, dead!.State);
        Assert.Contains("Job Tag", dead.TerminalCause);
        Assert.DoesNotContain(dead.Tags, tag => tag.Key == "note");

        var succeeded = await monitor.GetJobAsync(healthy);
        Assert.Equal(JobState.Succeeded, succeeded?.State);
        Assert.Contains(JobTag.Keyed("note", new string('v', TagCap)), succeeded!.Tags);

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

    private static async Task<WebApplication> StartGroupAsync(IJobStore store, Barrier barrier, int poolSize)
    {
        var registry = new JobRegistry(
        [
            JobRegistration.Create<OutputJob, OverLongTagHandler>(
                "over-long-tag", OutputJsonContext.Default.OutputJob, "default"),
        ]);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(barrier);
        builder.Services.AddTransient<IJobHandler<OutputJob>, OverLongTagHandler>();
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
