namespace BackWave.Observers;

/// <summary>
/// Host-supplied, egress-only code BackWave invokes when a job reaches a state you subscribe to.
/// It is a sanctioned "do X when Y happens to a job" without polling — for example, send an alert
/// when a <c>PaymentJob</c> dead-letters. An observer only reacts: it can never veto, redirect, or
/// rewrite a transition.
/// <para>
/// Reach for this when you want a side effect triggered by a job's outcome (notify, audit, fan a
/// metric out) and you do not want to bolt that logic into the job handler itself.
/// </para>
/// <para>
/// Gotchas. Delivery is at-least-once: a crash between the transition and your callback completing
/// is covered by redelivery, so the same transition may arrive more than once. Your reaction must
/// therefore be idempotent — the same contract a job handler already carries. The callback runs at
/// handler trust and is bounded by a timeout; a throw, timeout, or hang is contained and never
/// stops the worker that processes jobs — it just marks the delivery for retry.
/// </para>
/// </summary>
public interface ITransitionObserver
{
    /// <summary>
    /// React to one matched transition. Must be idempotent: it may be invoked more than once for
    /// the same transition because delivery is at-least-once.
    /// </summary>
    /// <param name="context">
    /// The facts of the matched transition — job id, type, queue, the state reached, attempt number,
    /// timestamp, optional failure detail, and a lazy accessor for the job payload.
    /// </param>
    /// <param name="cancellationToken">
    /// Cancelled when the dispatch timeout elapses or the host is shutting down. Honor it so a slow
    /// reaction is contained rather than left hanging.
    /// </param>
    /// <returns>A task that completes when the reaction is done; completing without throwing marks the delivery delivered.</returns>
    ValueTask OnTransitionAsync(ObserverContext context, CancellationToken cancellationToken);
}
