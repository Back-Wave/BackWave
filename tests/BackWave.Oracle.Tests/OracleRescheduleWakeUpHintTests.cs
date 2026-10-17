using System.Collections.Concurrent;
using BackWave.Storage;
using Oracle.ManagedDataAccess.Client;

namespace BackWave.Oracle.Tests;

/// <summary>
/// Pins the Wake-Up Hint on Reschedule: a Scheduled job moved to now signals the same DBMS_ALERT as a due
/// enqueue, on its own commit or the caller's. A move later, or earlier but still in the future, or of an
/// AwaitingParent child, signals nothing. Every job is enqueued in the future, so setup never signals,
/// and each test uses its own queue, so a signal from anything else on the alert cannot pass for this
/// one. These are integration tests: they need "docker compose up -d oracle".
/// </summary>
[Collection("oracle")]
public sealed class OracleRescheduleWakeUpHintTests
{
    // How long a "no hint" case waits, the same window the pump-level hint tests give the alert.
    private static readonly TimeSpan QuietWindow = TimeSpan.FromSeconds(2);

    private static NewJob Job(string queue, DateTimeOffset dueTime, IReadOnlyList<Guid>? parents = null) =>
        new(Guid.NewGuid(), "reschedule-probe", "{}"u8.ToArray(), queue, dueTime)
        {
            Parents = parents ?? [],
        };

    private static string UniqueQueue() => $"resched-{Guid.NewGuid():N}";

    private static async Task<OracleJobStore> NewStoreAsync()
    {
        await OracleTestDatabase.CreateFreshStoreAsync();
        await OracleTestDatabase.EnsureAlertAccessAsync();
        return new OracleJobStore(new OracleStoreOptions
        {
            ConnectionString = OracleTestDatabase.ConnectionString,
            EnableWakeUpHints = true,
        });
    }

    [Fact]
    public async Task Reschedule_ScheduledJobToNow_SignalsAWakeHint()
    {
        var store = await NewStoreAsync();
        var now = DateTimeOffset.UtcNow;
        var queue = UniqueQueue();
        var job = Job(queue, dueTime: now.AddHours(1));
        Assert.Equal(EnqueueResult.Ok, await store.EnqueueAsync(job, now));

        await using var sink = await HintSink.StartAsync(store);

        Assert.Equal(RescheduleResult.Rescheduled, await store.RescheduleAsync(job.JobId, now, "alice", now));

        // DBMS_ALERT merges signals that arrive before the waiter reads, so this cannot tell one signal
        // from two sent together. The quiet window only catches a repeat that arrives after the first.
        await sink.WaitForAsync(queue);
        await Task.Delay(QuietWindow);
        Assert.Equal([queue], sink.For(queue));
    }

    [Fact]
    public async Task Reschedule_ScheduledJobLater_SignalsNoWakeHint()
    {
        var store = await NewStoreAsync();
        var now = DateTimeOffset.UtcNow;
        var queue = UniqueQueue();
        var job = Job(queue, dueTime: now.AddHours(1));
        Assert.Equal(EnqueueResult.Ok, await store.EnqueueAsync(job, now));

        await using var sink = await HintSink.StartAsync(store);

        Assert.Equal(RescheduleResult.Rescheduled, await store.RescheduleAsync(job.JobId, now.AddHours(2), "alice", now));

        await Task.Delay(QuietWindow);
        Assert.Empty(sink.For(queue));
    }

    [Fact]
    public async Task Reschedule_ScheduledJobEarlierButStillInTheFuture_SignalsNoWakeHint()
    {
        var store = await NewStoreAsync();
        var now = DateTimeOffset.UtcNow;
        var queue = UniqueQueue();
        var job = Job(queue, dueTime: now.AddHours(1));
        Assert.Equal(EnqueueResult.Ok, await store.EnqueueAsync(job, now));

        await using var sink = await HintSink.StartAsync(store);

        // Earlier is not due: a pump woken now would find nothing to claim.
        Assert.Equal(RescheduleResult.Rescheduled, await store.RescheduleAsync(job.JobId, now.AddMinutes(30), "alice", now));

        await Task.Delay(QuietWindow);
        Assert.Empty(sink.For(queue));
    }

    [Fact]
    public async Task Reschedule_AwaitingParentChildToNow_SignalsNoWakeHint()
    {
        var store = await NewStoreAsync();
        var now = DateTimeOffset.UtcNow;
        var queue = UniqueQueue();
        var parent = Job(queue, dueTime: now.AddHours(1));
        Assert.Equal(EnqueueResult.Ok, await store.EnqueueAsync(parent, now));
        var child = Job(queue, dueTime: now.AddHours(1), parents: [parent.JobId]);
        Assert.Equal(EnqueueResult.Ok, await store.EnqueueAsync(child, now));
        Assert.Equal(JobState.AwaitingParent, (await store.GetJobAsync(child.JobId))!.State);

        await using var sink = await HintSink.StartAsync(store);

        // The child cannot run until its parent resolves, so waking a pump for it would be pointless.
        Assert.Equal(RescheduleResult.Rescheduled, await store.RescheduleAsync(child.JobId, now, "alice", now));

        await Task.Delay(QuietWindow);
        Assert.Empty(sink.For(queue));
    }

    [Fact]
    public async Task Reschedule_InACallerTransactionThatRollsBack_SignalsNoWakeHint()
    {
        var store = await NewStoreAsync();
        var now = DateTimeOffset.UtcNow;
        var queue = UniqueQueue();
        var job = Job(queue, dueTime: now.AddHours(1));
        Assert.Equal(EnqueueResult.Ok, await store.EnqueueAsync(job, now));
        var dueBefore = (await store.GetJobAsync(job.JobId))!.DueTime;

        await using var sink = await HintSink.StartAsync(store);

        await using (var caller = new OracleConnection(OracleTestDatabase.ConnectionString))
        {
            await caller.OpenAsync();
            await using var transaction = caller.BeginTransaction();
            Assert.Equal(RescheduleResult.Rescheduled,
                await store.RescheduleAsync(job.JobId, now, "alice", now, transaction));
            await transaction.RollbackAsync();
        }

        await Task.Delay(QuietWindow);
        Assert.Empty(sink.For(queue));
        Assert.Equal(dueBefore, (await store.GetJobAsync(job.JobId))!.DueTime);
    }

    [Fact]
    public async Task Reschedule_InACallerTransaction_SignalsTheWakeHintOnlyOnCommit()
    {
        var store = await NewStoreAsync();
        var now = DateTimeOffset.UtcNow;
        var queue = UniqueQueue();
        var job = Job(queue, dueTime: now.AddHours(1));
        Assert.Equal(EnqueueResult.Ok, await store.EnqueueAsync(job, now));

        await using var sink = await HintSink.StartAsync(store);

        await using (var caller = new OracleConnection(OracleTestDatabase.ConnectionString))
        {
            await caller.OpenAsync();
            await using var transaction = caller.BeginTransaction();
            Assert.Equal(RescheduleResult.Rescheduled,
                await store.RescheduleAsync(job.JobId, now, "alice", now, transaction));

            // The caller has not committed, so a pump woken now would find the old due time.
            await Task.Delay(QuietWindow);
            Assert.Empty(sink.For(queue));

            await transaction.CommitAsync();
        }

        await sink.WaitForAsync(queue);
        Assert.Equal([queue], sink.For(queue));
    }

    /// <summary>
    /// Records every hint the store's own DBMS_ALERT subscription delivers. A signal sent before the
    /// waiter registers is lost, so StartAsync returns only once a probe hint has come through.
    /// </summary>
    private sealed class HintSink : IAsyncDisposable
    {
        private readonly ConcurrentQueue<string> _hints = new();
        private IAsyncDisposable? _subscription;

        public static async Task<HintSink> StartAsync(OracleJobStore store)
        {
            var sink = new HintSink();
            sink._subscription = await store.SubscribeAsync(sink._hints.Enqueue);

            // A due enqueue on a probe queue signals; repeat it until the waiter is registered and sees one.
            var probeQueue = UniqueQueue();
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
            while (DateTimeOffset.UtcNow < deadline)
            {
                var now = DateTimeOffset.UtcNow;
                await store.EnqueueAsync(Job(probeQueue, dueTime: now), now);
                for (var i = 0; i < 10; i++)
                {
                    if (sink.For(probeQueue).Count > 0)
                    {
                        return sink;
                    }
                    await Task.Delay(50);
                }
            }
            await sink.DisposeAsync();
            Assert.Fail("the DBMS_ALERT subscription never delivered the probe hint");
            return sink;
        }

        public IReadOnlyList<string> For(string queue) => _hints.Where(hint => hint == queue).ToArray();

        public async Task WaitForAsync(string queue)
        {
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTimeOffset.UtcNow < deadline)
            {
                if (For(queue).Count > 0)
                {
                    return;
                }
                await Task.Delay(25);
            }
            Assert.Fail($"Timed out waiting for a wake hint on {queue}");
        }

        public async ValueTask DisposeAsync()
        {
            if (_subscription is not null)
            {
                await _subscription.DisposeAsync();
            }
        }
    }
}
