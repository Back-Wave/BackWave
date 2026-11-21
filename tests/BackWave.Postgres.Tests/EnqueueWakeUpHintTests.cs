using System.Text.Json.Serialization;
using BackWave.Core;
using BackWave.Hosting;
using BackWave.Jobs;
using BackWave.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace BackWave.Postgres.Tests;

public sealed record BurstProbe(int Index);

public sealed class BurstProbeHandler(BurstRecorder recorder) : IJobHandler<BurstProbe>
{
    public Task HandleAsync(BurstProbe job, JobContext context, CancellationToken cancellationToken)
    {
        recorder.Record();
        return Task.CompletedTask;
    }
}

public sealed class BurstRecorder(int expected)
{
    private int _handled;

    public TaskCompletionSource AllHandled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Record()
    {
        if (Interlocked.Increment(ref _handled) == expected)
        {
            AllHandled.TrySetResult();
        }
    }
}

[JsonSerializable(typeof(BurstProbe))]
internal sealed partial class BurstJsonContext : JsonSerializerContext;

[Collection("postgres")]
public sealed class EnqueueWakeUpHintTests
{
    private static NewJob Job(string queue, DateTimeOffset dueTime, IReadOnlyList<Guid>? parents = null) =>
        new(Guid.NewGuid(), "enqueue-hint-probe", "{}"u8.ToArray(), queue, dueTime)
        {
            Parents = parents ?? [],
        };

    private static string UniqueQueue() => $"enqueue-hint-{Guid.NewGuid():N}";

    private static IReadOnlyList<string> For(IReadOnlyList<string> hints, string queue) =>
        hints.Where(hint => hint == queue).ToArray();

    [Fact]
    public async Task Enqueue_InACallerTransaction_PublishesTheWakeHintOnlyOnCommit()
    {
        await using var store = await PostgresTestDatabase.CreateFreshStoreAsync();
        var now = DateTimeOffset.UtcNow;
        var queue = UniqueQueue();

        await using var listener = await HintListener.StartAsync();

        await using var caller = new NpgsqlConnection(PostgresTestDatabase.ConnectionString);
        await caller.OpenAsync();
        await using (var transaction = await caller.BeginTransactionAsync())
        {
            Assert.Equal(EnqueueResult.Ok, await store.EnqueueAsync(Job(queue, dueTime: now), now, transaction));

            Assert.Empty(For(await listener.DrainAsync(), queue));

            await transaction.CommitAsync();
        }

        Assert.Equal([queue], For(await listener.DrainAsync(), queue));
    }

    [Fact]
    public async Task Enqueue_InACallerTransactionThatRollsBack_PublishesNoWakeHint()
    {
        await using var store = await PostgresTestDatabase.CreateFreshStoreAsync();
        var now = DateTimeOffset.UtcNow;
        var queue = UniqueQueue();

        await using var listener = await HintListener.StartAsync();

        await using var caller = new NpgsqlConnection(PostgresTestDatabase.ConnectionString);
        await caller.OpenAsync();
        await using (var transaction = await caller.BeginTransactionAsync())
        {
            Assert.Equal(EnqueueResult.Ok, await store.EnqueueAsync(Job(queue, dueTime: now), now, transaction));
            await transaction.RollbackAsync();
        }

        Assert.Empty(For(await listener.DrainAsync(), queue));
    }

    [Fact]
    public async Task EnqueueWorkflow_InItsOwnTransaction_PublishesTheDueMembersQueueHint()
    {
        await using var store = await PostgresTestDatabase.CreateFreshStoreAsync();
        var now = DateTimeOffset.UtcNow;
        var queue = UniqueQueue();
        var root = Job(queue, dueTime: now);
        var child = Job(queue, dueTime: now, parents: [root.JobId]);

        await using var listener = await HintListener.StartAsync();

        Assert.Equal(WorkflowEnqueueResult.Ok, await store.EnqueueWorkflowAsync(
            new WorkflowDefinition { WorkflowId = Guid.NewGuid(), Members = [root, child] }, now));

        Assert.Equal([queue], For(await listener.DrainAsync(), queue));
    }

    [Fact]
    public async Task EnqueueWorkflow_InItsOwnTransaction_HintsEveryDueMembersQueue()
    {
        await using var store = await PostgresTestDatabase.CreateFreshStoreAsync();
        var now = DateTimeOffset.UtcNow;
        var (first, second) = (UniqueQueue(), UniqueQueue());

        await using var listener = await HintListener.StartAsync();

        Assert.Equal(WorkflowEnqueueResult.Ok, await store.EnqueueWorkflowAsync(
            new WorkflowDefinition { WorkflowId = Guid.NewGuid(), Members = [Job(first, now), Job(second, now)] }, now));

        var hints = await listener.DrainAsync();
        Assert.Equal([first], For(hints, first));
        Assert.Equal([second], For(hints, second));
    }

    [Fact]
    public async Task EnqueueWorkflow_InACallerTransaction_PublishesTheWakeHintOnlyOnCommit()
    {
        await using var store = await PostgresTestDatabase.CreateFreshStoreAsync();
        var now = DateTimeOffset.UtcNow;
        var (committed, rolledBack) = (UniqueQueue(), UniqueQueue());

        await using var listener = await HintListener.StartAsync();

        await using var caller = new NpgsqlConnection(PostgresTestDatabase.ConnectionString);
        await caller.OpenAsync();
        await using (var transaction = await caller.BeginTransactionAsync())
        {
            Assert.Equal(WorkflowEnqueueResult.Ok, await store.EnqueueWorkflowAsync(
                new WorkflowDefinition { WorkflowId = Guid.NewGuid(), Members = [Job(rolledBack, now)] }, now, transaction));
            await transaction.RollbackAsync();
        }
        await using (var transaction = await caller.BeginTransactionAsync())
        {
            Assert.Equal(WorkflowEnqueueResult.Ok, await store.EnqueueWorkflowAsync(
                new WorkflowDefinition { WorkflowId = Guid.NewGuid(), Members = [Job(committed, now)] }, now, transaction));
            Assert.Empty(For(await listener.DrainAsync(), committed));
            await transaction.CommitAsync();
        }

        var hints = await listener.DrainAsync();
        Assert.Equal([committed], For(hints, committed));
        Assert.Empty(For(hints, rolledBack));
    }

    [Fact]
    public async Task ConcurrentEnqueues_AllStartWithinSeconds_NotThePollInterval()
    {
        const int Producers = 16;
        const int JobsPerProducer = 13;
        const int Jobs = Producers * JobsPerProducer;
        var pollInterval = TimeSpan.FromSeconds(10);
        var hintDeadline = pollInterval / 2;

        await using var store = await PostgresTestDatabase.CreateFreshStoreAsync();
        var queue = UniqueQueue();
        var recorder = new BurstRecorder(Jobs);
        var provider = new ServiceCollection()
            .AddSingleton(recorder)
            .AddTransient<IJobHandler<BurstProbe>, BurstProbeHandler>()
            .BuildServiceProvider();
        var registry = new JobRegistry(
        [
            JobRegistration.Create<BurstProbe, BurstProbeHandler>("burst-probe", BurstJsonContext.Default.BurstProbe),
        ]);
        var service = new WorkerGroupService(
            new WorkerGroupOptions
            {
                Name = "burst-workers",
                Policy = new DispatchPolicy.Strict([queue]),
                PollInterval = pollInterval,
            },
            store,
            registry,
            provider.GetRequiredService<IServiceScopeFactory>(),
            new BackWaveHealth(),
            NullLogger<WorkerGroupService>.Instance);
        var client = new BackWaveClient(store, registry);

        await service.StartAsync(CancellationToken.None);
        try
        {
            await WakeUpHintTests.WaitForListenerAsync();

            await Task.WhenAll(Enumerable.Range(0, Producers).Select(producer => Task.Run(async () =>
            {
                for (var i = 0; i < JobsPerProducer; i++)
                {
                    await client.EnqueueAsync(new BurstProbe(producer * JobsPerProducer + i), DateTimeOffset.UtcNow, queue);
                }
            })));

            await recorder.AllHandled.Task.WaitAsync(hintDeadline);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }
}
