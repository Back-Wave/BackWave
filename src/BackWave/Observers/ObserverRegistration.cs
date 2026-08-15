using BackWave.Storage;

namespace BackWave.Observers;

/// <summary>
/// A registered transition observer: a stable <see cref="Id"/> plus the <see cref="Subscription"/>
/// it watches. The id keys the durable delivery cursor, so reusing the same id with the same
/// subscription across a restart resumes delivery where it left off — keep it stable for an
/// observer you want to survive restarts, and change it only when you intend a fresh cursor.
/// </summary>
/// <param name="Id">A stable identifier for this observer; keys its durable delivery cursor.</param>
/// <param name="Subscription">The transitions this observer is notified about.</param>
public sealed record ObserverRegistration(string Id, ObserverSubscription Subscription)
{
    /// <summary>
    /// Verifies that recorded history is rich enough for this observer to ever fire. Observers are
    /// driven by recorded job transitions, so with history turned off nothing is recorded to observe
    /// and the observer would silently never fire. Called at startup so the misconfiguration surfaces
    /// as a clear error instead of an observer that mysteriously stays quiet.
    /// </summary>
    /// <param name="historyPolicy">The configured job history policy to check against.</param>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="historyPolicy"/> is <see cref="JobHistoryPolicy.Off"/>: no transitions are
    /// recorded, so this observer could never receive a delivery. Raise the policy to at least
    /// transitions to enable it.
    /// </exception>
    public void EnsureDeliverableUnder(JobHistoryPolicy historyPolicy)
    {
        if (historyPolicy == JobHistoryPolicy.Off)
        {
            throw new InvalidOperationException(
                $"Transition Observer '{Id}' requires Job History Policy of at least Transitions, but it is Off: "
                + "with history Off no transition rows are recorded, so there is nothing to observe. Raise the "
                + "policy to Transitions or TransitionsAndFailureDetail to enable observers.");
        }
    }
}
