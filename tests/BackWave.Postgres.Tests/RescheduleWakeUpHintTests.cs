using BackWave.Storage;
using Npgsql;

namespace BackWave.Postgres.Tests;

/// <summary>
/// Pins the Wake-Up Hint on Reschedule: a Scheduled job moved to now publishes the same NOTIFY as a due
/// enqueue, on its own commit or the caller's. A move later, or earlier but still in the future, or of an
/// AwaitingParent child, publishes none. Every job is enqueued in the future, so setup never sends a
/// hint, and each test uses its own queue, so a hint from anything else on the channel cannot pass for
/// this one.
/// </summary>
[Collection("postgres")]
public sealed class RescheduleWakeUpHintTests
{
    private static NewJob Job(string queue, DateTimeOffset dueTime, IReadOnlyList<Guid>? parents = null) =>
        new(Guid.NewGuid(), "reschedule-probe", "{}"u8.ToArray(), queue, dueTime)
        {
            Parents = parents ?? [],
        };

    private static string UniqueQueue() => $"resched-{Guid.NewGuid():N}";

    private static IReadOnlyList<string> For(IReadOnlyList<string> hints, string queue) =>
        hints.Where(hint => hint == queue).ToArray();

    [Fact]
    public async Task Reschedule_ScheduledJobToNow_PublishesExactlyOneWakeHint()
    {
        await using var store = await PostgresTestDatabase.CreateFreshStoreAsync();
        var now = DateTimeOffset.UtcNow;
        var queue = UniqueQueue();
        var job = Job(queue, dueTime: now.AddHours(1));
        Assert.Equal(EnqueueResult.Ok, await store.EnqueueAsync(job, now));

        await using var listener = await HintListener.StartAsync();

        Assert.Equal(RescheduleResult.Rescheduled, await store.RescheduleAsync(job.JobId, now, "alice", now));

        Assert.Equal([queue], For(await listener.DrainAsync(), queue));
    }

    [Fact]
    public async Task Reschedule_ScheduledJobLater_PublishesNoWakeHint()
    {
        await using var store = await PostgresTestDatabase.CreateFreshStoreAsync();
        var now = DateTimeOffset.UtcNow;
        var queue = UniqueQueue();
        var job = Job(queue, dueTime: now.AddHours(1));
        Assert.Equal(EnqueueResult.Ok, await store.EnqueueAsync(job, now));

        await using var listener = await HintListener.StartAsync();

        Assert.Equal(RescheduleResult.Rescheduled, await store.RescheduleAsync(job.JobId, now.AddHours(2), "alice", now));

        Assert.Empty(For(await listener.DrainAsync(), queue));
    }

    [Fact]
    public async Task Reschedule_ScheduledJobEarlierButStillInTheFuture_PublishesNoWakeHint()
    {
        await using var store = await PostgresTestDatabase.CreateFreshStoreAsync();
        var now = DateTimeOffset.UtcNow;
        var queue = UniqueQueue();
        var job = Job(queue, dueTime: now.AddHours(1));
        Assert.Equal(EnqueueResult.Ok, await store.EnqueueAsync(job, now));

        await using var listener = await HintListener.StartAsync();

        // Earlier is not due: a pump woken now would find nothing to claim.
        Assert.Equal(RescheduleResult.Rescheduled, await store.RescheduleAsync(job.JobId, now.AddMinutes(30), "alice", now));

        Assert.Empty(For(await listener.DrainAsync(), queue));
    }

    [Fact]
    public async Task Reschedule_AwaitingParentChildToNow_PublishesNoWakeHint()
    {
        await using var store = await PostgresTestDatabase.CreateFreshStoreAsync();
        var now = DateTimeOffset.UtcNow;
        var queue = UniqueQueue();
        var parent = Job(queue, dueTime: now.AddHours(1));
        Assert.Equal(EnqueueResult.Ok, await store.EnqueueAsync(parent, now));
        var child = Job(queue, dueTime: now.AddHours(1), parents: [parent.JobId]);
        Assert.Equal(EnqueueResult.Ok, await store.EnqueueAsync(child, now));
        Assert.Equal(JobState.AwaitingParent, (await store.GetJobAsync(child.JobId))!.State);

        await using var listener = await HintListener.StartAsync();

        // The child cannot run until its parent resolves, so waking a pump for it would be pointless.
        Assert.Equal(RescheduleResult.Rescheduled, await store.RescheduleAsync(child.JobId, now, "alice", now));

        Assert.Empty(For(await listener.DrainAsync(), queue));
    }

    [Fact]
    public async Task Reschedule_InACallerTransactionThatRollsBack_PublishesNoWakeHint()
    {
        await using var store = await PostgresTestDatabase.CreateFreshStoreAsync();
        var now = DateTimeOffset.UtcNow;
        var queue = UniqueQueue();
        var job = Job(queue, dueTime: now.AddHours(1));
        Assert.Equal(EnqueueResult.Ok, await store.EnqueueAsync(job, now));
        var dueBefore = (await store.GetJobAsync(job.JobId))!.DueTime;

        await using var listener = await HintListener.StartAsync();

        await using var caller = new NpgsqlConnection(PostgresTestDatabase.ConnectionString);
        await caller.OpenAsync();
        await using (var transaction = await caller.BeginTransactionAsync())
        {
            Assert.Equal(RescheduleResult.Rescheduled,
                await store.RescheduleAsync(job.JobId, now, "alice", now, transaction));
            await transaction.RollbackAsync();
        }

        Assert.Empty(For(await listener.DrainAsync(), queue));
        Assert.Equal(dueBefore, (await store.GetJobAsync(job.JobId))!.DueTime);
    }

    [Fact]
    public async Task Reschedule_InACallerTransaction_PublishesTheWakeHintOnlyOnCommit()
    {
        await using var store = await PostgresTestDatabase.CreateFreshStoreAsync();
        var now = DateTimeOffset.UtcNow;
        var queue = UniqueQueue();
        var job = Job(queue, dueTime: now.AddHours(1));
        Assert.Equal(EnqueueResult.Ok, await store.EnqueueAsync(job, now));

        await using var listener = await HintListener.StartAsync();

        await using var caller = new NpgsqlConnection(PostgresTestDatabase.ConnectionString);
        await caller.OpenAsync();
        await using (var transaction = await caller.BeginTransactionAsync())
        {
            Assert.Equal(RescheduleResult.Rescheduled,
                await store.RescheduleAsync(job.JobId, now, "alice", now, transaction));

            // The caller has not committed, so a pump woken now would find the old due time.
            Assert.Empty(For(await listener.DrainAsync(), queue));

            await transaction.CommitAsync();
        }

        Assert.Equal([queue], For(await listener.DrainAsync(), queue));
    }
}
