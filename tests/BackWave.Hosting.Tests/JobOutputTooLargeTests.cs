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
/// Orders two handlers so the small job's outcome is buffered before the oversized one's, which is what
/// puts a settled row AHEAD of the rejection when the store applies the batch row by row.
/// </summary>
public sealed class SequenceGate
{
    /// <summary>Completes when the small job's handler has returned.</summary>
    public TaskCompletionSource SmallFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>The oversized/tiny split of <see cref="OversizedOutputHandler"/>, but sequenced rather than barriered.</summary>
public sealed class SequencedOutputHandler(SequenceGate gate) : IJobHandler<OutputJob>
{
    /// <inheritdoc/>
    public async Task HandleAsync(OutputJob job, JobContext context, CancellationToken cancellationToken)
    {
        var oversized = job.Key.StartsWith("big", StringComparison.Ordinal);
        context.SetOutput(
            new OutputPayload(job.Key, oversized ? new string('x', 4096) : "ok"),
            OutputJsonContext.Default.OutputPayload);
        if (!oversized)
        {
            gate.SmallFinished.TrySetResult();
            return;
        }

        // Stay running until the small outcome is buffered - and a beat longer, because the buffering
        // happens on the pump loop after the handler returns. The buffer cannot flush meanwhile: the
        // drain rule is "nothing still executing", and this handler is still executing.
        await gate.SmallFinished.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        await Task.Delay(250, cancellationToken);
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
/// every row before writing any). A third test pins the row-by-row ordering rather than leaving it to a
/// race, because the re-apply of an already-settled row is the one that comes back fenced out.
/// </para>
/// </summary>
public class JobOutputTooLargeTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan FastPoll = TimeSpan.FromMilliseconds(25);

    // Small enough that the 4096-char body blows it and the "ok" body fits comfortably.
    private const int OutputCap = 64;

    private const int FencedOutEventId = 1208;

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

    [Fact]
    public async Task OversizedOutput_OnARowByRowStore_ReAppliesTheBatchOnceAndCountsNoFenceViolation()
    {
        // The third store shape, and the nastiest: rows apply ONE AT A TIME, so the over-cap row rejects
        // the batch only after every row ahead of it is already settled. The re-apply therefore meets rows
        // the store has already written and gets StaleLease back on them - a benign fence answer that must
        // settle the batch exactly once and leave the invariant ledger at zero, not read as an impossible
        // state just because the Lease behind it had not lapsed.
        var store = new FaultableStore(new InMemoryJobStore()) { RowOutputCap = OutputCap };
        var logs = new CapturingLoggerProvider();
        using var violations = new ViolationRecorder(InvariantTrigger.OutcomeFenceRejected);
        var gate = new SequenceGate();
        await using var app = StartSequencedGroup(store, gate, logs);
        await app.StartAsync();

        var client = app.Services.GetRequiredService<BackWaveClient>();
        var monitor = app.Services.GetRequiredService<BackWaveMonitor>();

        var healthy = await client.EnqueueAsync(new OutputJob("small-1"), dueTime: DateTimeOffset.UtcNow);
        var oversized = await client.EnqueueAsync(new OutputJob("big-1"), dueTime: DateTimeOffset.UtcNow);

        await AwaitTerminalAsync(monitor, oversized, healthy);

        // The shape really was exercised: the rejection threw with one row already settled ahead of it,
        // and the Shell re-applied the whole batch exactly once more.
        Assert.Equal(1, store.RowsSettledBeforeRejection);
        Assert.Equal(2, store.ReportOutcomesCalls);

        Assert.Equal(JobState.Succeeded, (await monitor.GetJobAsync(healthy))?.State);
        Assert.NotNull(await monitor.GetJobOutputAsync(healthy));
        var dead = await monitor.GetJobAsync(oversized);
        Assert.Equal(JobState.DeadLettered, dead!.State);
        Assert.Contains("Job Output", dead.TerminalCause);

        // The settled row fences out on the second pass. Benign, so: Debug, no counter, group untouched.
        var debug = Assert.Single(logs.Entries, entry => entry.EventId == FencedOutEventId);
        Assert.Contains(healthy.ToString(), debug.Message);
        Assert.Empty(violations.Measurements);

        var health = app.Services.GetRequiredService<BackWaveHealth>();
        Assert.True(health.IsHealthy);
        Assert.Empty(health.HaltedGroups);

        await app.StopAsync();
    }

    private static WebApplication StartSequencedGroup(
        IJobStore store, SequenceGate gate, ILoggerProvider logs)
    {
        var registry = new JobRegistry(
        [
            JobRegistration.Create<OutputJob, SequencedOutputHandler>(
                "oversized-output", OutputJsonContext.Default.OutputJob, "default"),
        ]);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.AddProvider(logs);
        builder.Logging.SetMinimumLevel(LogLevel.Debug);  // the benign fence answer logs only there
        builder.Services.AddSingleton(gate);
        builder.Services.AddTransient<IJobHandler<OutputJob>, SequencedOutputHandler>();
        builder.Services.AddBackWave(backwave => backwave
            .UseStore(store)
            .UseRegistry(registry)
            .AddWorkerGroup(new WorkerGroupOptions
            {
                Name = "workers",
                Policy = new DispatchPolicy.Strict(["default"]),
                // The buffer also flushes on a poll or heartbeat tick, so both must stay well outside the
                // window between the two handlers finishing - otherwise the rows land in separate batches
                // and the settled-row-ahead shape this test exists for never happens.
                PollInterval = TimeSpan.FromSeconds(2),
                HeartbeatInterval = TimeSpan.FromMinutes(10),
                MaintenanceInterval = TimeSpan.FromMinutes(10),
                PoolSize = 2,
                LeaseDuration = TimeSpan.FromMinutes(5),
            }));

        return builder.Build();
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
