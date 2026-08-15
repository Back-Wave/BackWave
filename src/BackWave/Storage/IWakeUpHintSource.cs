namespace BackWave.Storage;

/// <summary>
/// The optional Wake-Up Hint channel. Hints carry the Queue name and have no delivery
/// guarantees whatsoever — they may be dropped, duplicated, delayed, or reordered. A hint only
/// ever makes a worker poll sooner; no correctness decision anywhere may depend on one.
/// Implementations without a notification primitive simply don't implement this interface, and
/// latency degrades to the configured poll interval.
/// </summary>
public interface IWakeUpHintSource
{
    /// <summary>
    /// Subscribes to hints; <paramref name="onHint"/> receives the Queue name (possibly
    /// concurrently). The subscription maintains itself — on channel loss it keeps trying
    /// to reconnect until disposed, and while it is down latency degrades to the poll
    /// interval and nothing else changes. Because hints have no delivery guarantee, a callback
    /// that never fires (or fires for a Queue with no work) is always acceptable.
    /// </summary>
    /// <param name="onHint">
    /// Invoked with the name of a Queue that may have newly eligible work. May be called
    /// concurrently, more than once for the same wake-up, or not at all; treat it purely as a
    /// "poll this Queue sooner" nudge, never as authoritative.
    /// </param>
    /// <param name="cancellationToken">Cancels the subscription attempt.</param>
    /// <returns>
    /// A handle whose disposal tears the subscription down and stops further callbacks. Dispose
    /// it to unsubscribe.
    /// </returns>
    Task<IAsyncDisposable> SubscribeAsync(Action<string> onHint, CancellationToken cancellationToken = default);
}
