using System.Collections.Concurrent;

namespace BackWave.Sqlite.Internal;

/// <summary>
/// A best-effort, in-process pub/sub that lets the SQLite adapter nudge same-process Worker Group pumps to
/// poll sooner after an adapter-owned commit. Hints carry a Queue name and have <em>no</em>
/// delivery guarantees — they are a latency optimisation, never correctness-bearing. A pump that misses a
/// hint simply waits for its next ordinary poll; nothing breaks. Scoped to a single store instance.
/// </summary>
/// <remarks>
/// <para>
/// <b>Coalescing.</b> A burst of <see cref="Publish(string)"/> calls for the same Queue that arrives faster
/// than a subscriber drains collapses to a single observed hint per drain cycle. Each subscriber owns a
/// private pending-set plus a signal: publishing adds the name to every subscriber's set and pulses its
/// signal; the subscriber's loop wakes, snapshots-and-clears the set, and delivers each distinct name once.
/// Distinct names are <em>not</em> merged — "a" and "b" are both delivered.
/// </para>
/// <para>
/// <b>Isolation.</b> Callbacks run on each subscriber's own loop, off the publisher's thread, so a slow or
/// throwing subscriber cannot block <see cref="Publish(string)"/> or starve other subscribers. A throwing
/// <c>onHint</c> is swallowed (the hint was best-effort anyway) and the loop keeps running.
/// </para>
/// <para>
/// <b>Lifecycle.</b> <see cref="DisposeAsync"/> tears down every subscriber loop and clears the roster; after
/// disposal <see cref="Publish(string)"/> is a silent no-op. Disposing an individual subscription handle
/// unhooks just that subscriber and stops its loop.
/// </para>
/// </remarks>
internal sealed class WakeUpHintHub : IAsyncDisposable
{
    /// <summary>Live subscribers. Concurrent so publish (read) races subscribe/dispose (mutate) without locks.</summary>
    private readonly ConcurrentDictionary<Subscription, byte> _subscribers = new();

    /// <summary>Latched once on dispose; flips <see cref="Publish(string)"/> to a no-op and blocks new subscribes.</summary>
    private volatile bool _disposed;

    /// <summary>
    /// Publishes a hint for <paramref name="queue"/> to every live subscriber. Fan-out is best-effort and
    /// returns immediately — the publisher never waits on subscriber callbacks. A no-op after disposal.
    /// </summary>
    /// <param name="queue">The Queue name to hint.</param>
    public void Publish(string queue)
    {
        ArgumentNullException.ThrowIfNull(queue);

        if (_disposed)
        {
            return;
        }

        foreach (var subscription in _subscribers.Keys)
        {
            subscription.Offer(queue);
        }
    }

    /// <summary>
    /// Registers <paramref name="onHint"/> to receive hints. Returns a handle whose
    /// <see cref="IAsyncDisposable.DisposeAsync"/> unhooks the subscriber and stops its delivery loop.
    /// Callbacks run on a private loop, coalesced per drain cycle.
    /// </summary>
    /// <param name="onHint">Invoked once per distinct Queue name per drain cycle; exceptions are swallowed.</param>
    public IAsyncDisposable Subscribe(Action<string> onHint)
    {
        ArgumentNullException.ThrowIfNull(onHint);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var subscription = new Subscription(onHint, this);
        _subscribers.TryAdd(subscription, 0);

        // Lost a race with DisposeAsync that latched _disposed after our check but before our add: undo and
        // tear down so we don't leak a loop the hub will never reach.
        if (_disposed)
        {
            _subscribers.TryRemove(subscription, out _);
            subscription.BeginShutdown();
        }

        return subscription;
    }

    /// <summary>Removes a subscription from the roster (called by the subscription on its own disposal).</summary>
    private void Remove(Subscription subscription) => _subscribers.TryRemove(subscription, out _);

    /// <summary>Stops all delivery loops and clears the roster. Idempotent.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Snapshot then clear so concurrent Publish sees an empty roster immediately.
        var live = _subscribers.Keys.ToArray();
        _subscribers.Clear();

        foreach (var subscription in live)
        {
            await subscription.StopAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// One subscriber: a private coalescing pending-set, a signal, and a long-running loop that drains and
    /// delivers. Disposing the handle unhooks it from the hub and stops the loop.
    /// </summary>
    private sealed class Subscription : IAsyncDisposable
    {
        private readonly Action<string> _onHint;
        private readonly WakeUpHintHub _hub;

        /// <summary>Pending distinct Queue names awaiting delivery. The coalescing buffer.</summary>
        private readonly HashSet<string> _pending = new(StringComparer.Ordinal);

        /// <summary>Guards <see cref="_pending"/> (publisher writes, loop drains).</summary>
        private readonly object _gate = new();

        /// <summary>Pulsed by <see cref="Offer(string)"/>, awaited by the loop. Auto-resets on each wait.</summary>
        private readonly SemaphoreSlim _signal = new(0);

        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;
        private int _disposed;

        public Subscription(Action<string> onHint, WakeUpHintHub hub)
        {
            _onHint = onHint;
            _hub = hub;
            _loop = Task.Run(() => RunAsync(_cts.Token));
        }

        /// <summary>
        /// Adds a Queue name to the pending-set (deduping) and pulses the loop. Cheap and non-blocking so it
        /// stays safe to call from the publisher's hot path. Only signals on a 0→1 transition for a given
        /// name+empty buffer so a burst doesn't pile up redundant semaphore counts.
        /// </summary>
        public void Offer(string queue)
        {
            bool shouldSignal;
            lock (_gate)
            {
                // Adding to a non-empty buffer means the loop is already due to wake — no extra pulse needed.
                var wasEmpty = _pending.Count == 0;
                var added = _pending.Add(queue);
                shouldSignal = wasEmpty && added;
            }

            if (shouldSignal)
            {
                // Release can throw only if disposed; we tolerate that as a benign best-effort drop.
                try
                {
                    _signal.Release();
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }

        /// <summary>The delivery loop: wait for a signal, snapshot-and-clear the buffer, deliver each name once.</summary>
        private async Task RunAsync(CancellationToken token)
        {
            try
            {
                while (true)
                {
                    await _signal.WaitAsync(token).ConfigureAwait(false);

                    string[] batch;
                    lock (_gate)
                    {
                        if (_pending.Count == 0)
                        {
                            continue;
                        }

                        batch = _pending.ToArray();
                        _pending.Clear();
                    }

                    foreach (var queue in batch)
                    {
                        try
                        {
                            _onHint(queue);
                        }
                        catch
                        {
                            // Best-effort hint: a throwing callback must not tear down this loop, the hub, or
                            // other subscribers. Swallow and carry on.
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown path.
            }
        }

        /// <summary>Signals the loop to stop without awaiting it (used on a lost subscribe/dispose race).</summary>
        public void BeginShutdown()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _cts.Cancel();
            }
        }

        /// <summary>Cancels the loop, awaits its exit, and disposes resources. Called by the hub on its own dispose.</summary>
        public async Task StopAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            await _cts.CancelAsync().ConfigureAwait(false);
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            _cts.Dispose();
            _signal.Dispose();
        }

        /// <summary>Unhooks this subscriber from the hub and stops its loop.</summary>
        public async ValueTask DisposeAsync()
        {
            _hub.Remove(this);
            await StopAsync().ConfigureAwait(false);
        }
    }
}
