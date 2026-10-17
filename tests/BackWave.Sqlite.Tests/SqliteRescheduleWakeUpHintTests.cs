using System.Collections.Concurrent;
using BackWave.Sqlite.Internal;
using BackWave.Storage;
using Microsoft.Data.Sqlite;

namespace BackWave.Sqlite.Tests;

/// <summary>
/// Pins the in-process Wake-Up Hint on Reschedule: a Scheduled job moved to now publishes after the
/// adapter's own commit, the same as a due enqueue. A move later, or earlier but still in the future, or
/// of an AwaitingParent child, publishes nothing, and a move in a caller transaction stays silent, the
/// same as Transactional Enqueue. Every job is enqueued in the future, so setup never publishes.
/// </summary>
public sealed class SqliteRescheduleWakeUpHintTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // How long a "no hint" case waits, the same window the adapter-level hint tests use.
    private static readonly TimeSpan QuietWindow = TimeSpan.FromMilliseconds(300);

    private static NewJob Job(DateTimeOffset dueTime, IReadOnlyList<Guid>? parents = null) =>
        new(Guid.NewGuid(), "demo", default, "orders", dueTime)
        {
            Parents = parents ?? [],
        };

    private static string NormalizedConnectionString(string path)
        => SqliteConnectionStringNormalizer.Normalize($"Data Source={path}", TimeSpan.FromSeconds(5));

    [Fact]
    public async Task Reschedule_scheduled_job_to_now_publishes_a_hint_for_the_queue()
    {
        await using var temp = TempSqliteStore.Create();
        var job = Job(T0.AddHours(1));
        await temp.Store.EnqueueAsync(job, T0);
        var hints = new ConcurrentQueue<string>();
        await using var subscription = await temp.Store.SubscribeAsync(hints.Enqueue);

        Assert.Equal(RescheduleResult.Rescheduled, await temp.Store.RescheduleAsync(job.JobId, T0, "alice", T0));

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (hints.IsEmpty && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
        await Task.Delay(QuietWindow);
        Assert.Equal(["orders"], hints.ToArray());
    }

    [Fact]
    public async Task Reschedule_scheduled_job_later_does_not_hint()
    {
        await using var temp = TempSqliteStore.Create();
        var job = Job(T0.AddHours(1));
        await temp.Store.EnqueueAsync(job, T0);
        var hinted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var subscription = await temp.Store.SubscribeAsync(queue => hinted.TrySetResult(queue));

        Assert.Equal(RescheduleResult.Rescheduled, await temp.Store.RescheduleAsync(job.JobId, T0.AddHours(2), "alice", T0));

        await Assert.ThrowsAsync<TimeoutException>(async () => await hinted.Task.WaitAsync(QuietWindow));
    }

    [Fact]
    public async Task Reschedule_scheduled_job_earlier_but_still_in_the_future_does_not_hint()
    {
        await using var temp = TempSqliteStore.Create();
        var job = Job(T0.AddHours(1));
        await temp.Store.EnqueueAsync(job, T0);
        var hinted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var subscription = await temp.Store.SubscribeAsync(queue => hinted.TrySetResult(queue));

        // Earlier is not due: a pump woken now would find nothing to claim.
        Assert.Equal(RescheduleResult.Rescheduled, await temp.Store.RescheduleAsync(job.JobId, T0.AddMinutes(30), "alice", T0));

        await Assert.ThrowsAsync<TimeoutException>(async () => await hinted.Task.WaitAsync(QuietWindow));
    }

    [Fact]
    public async Task Reschedule_awaiting_parent_child_to_now_does_not_hint()
    {
        await using var temp = TempSqliteStore.Create();
        var parent = Job(T0.AddHours(1));
        await temp.Store.EnqueueAsync(parent, T0);
        var child = Job(T0.AddHours(1), parents: [parent.JobId]);
        await temp.Store.EnqueueAsync(child, T0);
        Assert.Equal(JobState.AwaitingParent, (await temp.Store.GetJobAsync(child.JobId))!.State);
        var hinted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var subscription = await temp.Store.SubscribeAsync(queue => hinted.TrySetResult(queue));

        // The child cannot run until its parent resolves, so waking a pump for it would be pointless.
        Assert.Equal(RescheduleResult.Rescheduled, await temp.Store.RescheduleAsync(child.JobId, T0, "alice", T0));

        await Assert.ThrowsAsync<TimeoutException>(async () => await hinted.Task.WaitAsync(QuietWindow));
    }

    [Fact]
    public async Task Reschedule_in_a_caller_transaction_that_rolls_back_does_not_hint()
    {
        await using var temp = TempSqliteStore.Create();
        var job = Job(T0.AddHours(1));
        await temp.Store.EnqueueAsync(job, T0);
        var hinted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var subscription = await temp.Store.SubscribeAsync(queue => hinted.TrySetResult(queue));

        await using var caller = new SqliteConnection(NormalizedConnectionString(temp.Path));
        await caller.OpenAsync();
        await using (var transaction = caller.BeginTransaction(deferred: true))
        {
            Assert.Equal(RescheduleResult.Rescheduled,
                await temp.Store.RescheduleAsync(job.JobId, T0, "alice", T0, transaction));
            await transaction.RollbackAsync();
        }

        await Assert.ThrowsAsync<TimeoutException>(async () => await hinted.Task.WaitAsync(QuietWindow));
        Assert.Equal(T0.AddHours(1), (await temp.Store.GetJobAsync(job.JobId))!.DueTime);
    }

    [Fact]
    public async Task Reschedule_in_a_caller_transaction_stays_silent_even_after_commit()
    {
        await using var temp = TempSqliteStore.Create();
        var job = Job(T0.AddHours(1));
        await temp.Store.EnqueueAsync(job, T0);
        var hinted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var subscription = await temp.Store.SubscribeAsync(queue => hinted.TrySetResult(queue));

        await using var caller = new SqliteConnection(NormalizedConnectionString(temp.Path));
        await caller.OpenAsync();
        await using (var transaction = caller.BeginTransaction(deferred: true))
        {
            Assert.Equal(RescheduleResult.Rescheduled,
                await temp.Store.RescheduleAsync(job.JobId, T0, "alice", T0, transaction));
            await transaction.CommitAsync();
        }

        // The caller owns the commit and ADO.NET exposes no commit callback, so the adapter stays
        // silent and polling carries the work. The move itself IS committed.
        await Assert.ThrowsAsync<TimeoutException>(async () => await hinted.Task.WaitAsync(QuietWindow));
        Assert.Equal(T0, (await temp.Store.GetJobAsync(job.JobId))!.DueTime);
    }
}
