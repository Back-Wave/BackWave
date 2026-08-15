using System.Collections.Concurrent;
using BackWave.Sqlite.Internal;

namespace BackWave.Sqlite.Tests;

/// <summary>
/// Unit tests for <see cref="WakeUpHintHub"/>. No DB. All waits are bounded so a regression fails fast
/// rather than hanging the suite.
/// </summary>
public sealed class WakeUpHintHubTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Publish_reaches_subscriber()
    {
        await using var hub = new WakeUpHintHub();
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var sub = hub.Subscribe(q => received.TrySetResult(q));
        hub.Publish("orders");

        var got = await received.Task.WaitAsync(Timeout);
        Assert.Equal("orders", got);
    }

    [Fact]
    public async Task Burst_of_same_queue_coalesces()
    {
        await using var hub = new WakeUpHintHub();
        var deliveries = new ConcurrentQueue<string>();
        var firstSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var sub = hub.Subscribe(q =>
        {
            deliveries.Enqueue(q);
            firstSeen.TrySetResult();
        });

        const int n = 1000;
        for (var i = 0; i < n; i++)
        {
            hub.Publish("q");
        }

        await firstSeen.Task.WaitAsync(Timeout);
        // Give the loop a beat to drain any further coalesced wakeups before asserting bounds.
        await Task.Delay(100);

        var seen = deliveries.ToArray();
        Assert.NotEmpty(seen);
        Assert.All(seen, q => Assert.Equal("q", q));
        Assert.Equal(new HashSet<string> { "q" }, seen.ToHashSet());
        // The whole point: far fewer deliveries than publishes.
        Assert.True(seen.Length < n, $"expected coalescing but saw {seen.Length} deliveries for {n} publishes");
    }

    [Fact]
    public async Task Distinct_queues_are_not_coalesced_together()
    {
        await using var hub = new WakeUpHintHub();
        var deliveries = new ConcurrentDictionary<string, byte>();
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var sub = hub.Subscribe(q =>
        {
            deliveries.TryAdd(q, 0);
            if (deliveries.ContainsKey("a") && deliveries.ContainsKey("b"))
            {
                both.TrySetResult();
            }
        });

        hub.Publish("a");
        hub.Publish("b");

        await both.Task.WaitAsync(Timeout);
        Assert.True(deliveries.ContainsKey("a"));
        Assert.True(deliveries.ContainsKey("b"));
    }

    [Fact]
    public async Task Disposed_subscription_stops_receiving()
    {
        await using var hub = new WakeUpHintHub();
        var count = 0;
        var sub = hub.Subscribe(_ => Interlocked.Increment(ref count));

        await sub.DisposeAsync();

        for (var i = 0; i < 50; i++)
        {
            hub.Publish("q");
        }

        await Task.Delay(150);
        Assert.Equal(0, Volatile.Read(ref count));
    }

    [Fact]
    public async Task Multiple_subscribers_each_receive_the_hint()
    {
        await using var hub = new WakeUpHintHub();
        var a = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var b = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var subA = hub.Subscribe(q => a.TrySetResult(q));
        await using var subB = hub.Subscribe(q => b.TrySetResult(q));

        hub.Publish("fanout");

        Assert.Equal("fanout", await a.Task.WaitAsync(Timeout));
        Assert.Equal("fanout", await b.Task.WaitAsync(Timeout));
    }

    [Fact]
    public async Task Throwing_subscriber_does_not_break_a_second_subscriber()
    {
        await using var hub = new WakeUpHintHub();
        var good = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var bad = hub.Subscribe(_ => throw new InvalidOperationException("boom"));
        await using var ok = hub.Subscribe(q => good.TrySetResult(q));

        hub.Publish("survives");

        Assert.Equal("survives", await good.Task.WaitAsync(Timeout));
    }

    [Fact]
    public async Task Concurrent_publishers_deliver_all_distinct_queues_without_throwing()
    {
        await using var hub = new WakeUpHintHub();
        var delivered = new ConcurrentDictionary<string, byte>();

        const int queues = 50;
        var allSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var sub = hub.Subscribe(q =>
        {
            delivered.TryAdd(q, 0);
            if (delivered.Count >= queues)
            {
                allSeen.TrySetResult();
            }
        });

        var publishers = Enumerable.Range(0, queues).Select(i => Task.Run(() =>
        {
            for (var j = 0; j < 20; j++)
            {
                hub.Publish("queue-" + i);
            }
        })).ToArray();

        await Task.WhenAll(publishers); // no deadlock / no throw out of Publish
        await allSeen.Task.WaitAsync(Timeout);

        for (var i = 0; i < queues; i++)
        {
            Assert.True(delivered.ContainsKey("queue-" + i), $"queue-{i} was never delivered");
        }
    }

    [Fact]
    public async Task Publish_after_dispose_is_a_silent_no_op()
    {
        var hub = new WakeUpHintHub();
        var count = 0;
        var sub = hub.Subscribe(_ => Interlocked.Increment(ref count));

        await hub.DisposeAsync();

        hub.Publish("ignored"); // must not throw
        await sub.DisposeAsync(); // disposing handle after hub dispose must be safe

        await Task.Delay(100);
        Assert.Equal(0, Volatile.Read(ref count));
    }
}
