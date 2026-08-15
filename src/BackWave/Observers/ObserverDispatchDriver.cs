using BackWave.Storage;

namespace BackWave.Observers;

/// <summary>
/// The <b>Observer Dispatch Core</b>: a sans-IO <c>Step(event) → Commands</c> state
/// machine, a sibling to the Node Driver. It owns delivery ordering-per-cursor and
/// attempt counting, backoff, the dead-letter ceiling, and bounded head-of-line advance. It is a
/// <i>separate</i> module from the Node Driver by design, so job-claim logic and delivery logic
/// never entangle and each is testable in isolation.
/// <para>
/// Like the Node Driver it never awaits, times, or threads — the Shell (production pump or the
/// Simulator) owns all I/O and clocks. Its only state is the set of Observers this node currently
/// has a batch in flight for, which keeps a single node from claiming the same Observer twice
/// while a round-trip is outstanding.
/// </para>
/// </summary>
internal sealed class ObserverDispatchDriver(ObserverDispatchOptions options)
{
    // The Observers this node is mid-delivery for: claim issued, not yet reported. Prevents a
    // re-poll from issuing a second concurrent claim for the same Observer on this node.
    private readonly HashSet<string> _inFlight = new(StringComparer.Ordinal);

    /// <summary>Decide what the Shell does next given one event. Pure: no I/O, no clock.</summary>
    public IReadOnlyList<ObserverCommand> Step(ObserverEvent observerEvent)
    {
        switch (observerEvent)
        {
            case ObserverEvent.PollDue:
                var claims = new List<ObserverCommand>();
                foreach (var observer in options.Observers)
                {
                    // Skip an Observer already mid-flight on this node; claim every other.
                    if (_inFlight.Add(observer.Id))
                    {
                        claims.Add(new ObserverCommand.ClaimBatch(
                            observer.Id, observer.Subscription, options.WorkerId, options.MaxBatch, options.LeaseDuration));
                    }
                }
                return claims;

            case ObserverEvent.BatchClaimed claimed:
                if (claimed.Deliveries.Count == 0)
                {
                    // Lease held elsewhere, or nothing due: free this Observer to be claimed again later.
                    _inFlight.Remove(claimed.ObserverId);
                    return [];
                }
                return [new ObserverCommand.InvokeBatch(claimed.ObserverId, claimed.Deliveries)];

            case ObserverEvent.BatchInvoked invoked:
                // Free this Observer here, at the decision point — not after the report confirms —
                // so a store fault on the report can never wedge the node (the Node Driver forgets
                // an Attempt the same way before reporting its outcome). A lost report just leaves
                // the cursor un-advanced; the next claim redelivers (at-least-once).
                _inFlight.Remove(invoked.ObserverId);
                var outcomes = new List<ObserverDeliveryOutcome>(invoked.Results.Count);
                foreach (var result in invoked.Results)
                {
                    outcomes.Add(Decide(result, invoked.Now));
                }
                return [new ObserverCommand.ReportBatch(invoked.ObserverId, options.WorkerId, outcomes)];

            case ObserverEvent.BatchReported reported:
                // A batch just drained; more rows may be claimable now. Re-poll to keep up.
                return [new ObserverCommand.RequestPoll(reported.Now)];

            case ObserverEvent.DeliveryAborted aborted:
                // The claim/report round-trip faulted at the Shell edge: release the in-flight guard
                // so the next poll re-claims this Observer. Without this a single faulted claim would
                // leave the guard stuck set, and this node would never claim the Observer again.
                _inFlight.Remove(aborted.ObserverId);
                return [];

            default:
                return [];
        }
    }

    /// <summary>
    /// Map one invocation result to a durable outcome. A success advances the cursor; a
    /// failure (the callback threw, timed out, or hung at the dispatch edge) is at-least-once work —
    /// the <see cref="ObserverDispatchOptions.DeliveryRetryPolicy"/> gives the backoff instant to
    /// retry at, or <c>null</c> once the attempt ceiling is exhausted, in which case the delivery is
    /// dead-lettered and the cursor advances past it so one poison row can't wedge later notifications.
    /// </summary>
    private ObserverDeliveryOutcome Decide(ObserverInvocationResult result, DateTimeOffset now)
    {
        if (result.Succeeded)
        {
            return new ObserverDeliveryOutcome(result.Position, ObserverDeliveryDisposition.Delivered);
        }
        return options.DeliveryRetryPolicy.NextAttemptAt(result.DeliveryAttempt, now) is { } retryAt
            ? new ObserverDeliveryOutcome(result.Position, ObserverDeliveryDisposition.Retry, retryAt)
            : new ObserverDeliveryOutcome(result.Position, ObserverDeliveryDisposition.DeadLettered);
    }
}
