namespace BackWave.Diagnostics;

/// <summary>
/// Thrown when BackWave observes a state its own invariants forbid - a store answer no legal race can
/// produce. It is the one exception type every such check raises: the condition is named by
/// <see cref="Trigger"/> rather than by a type of its own, so a handler switches on the id instead of
/// on a type hierarchy. Reaching a worker group's pump, it fail-stops that group (its leases lapse and
/// healthy nodes inherit the work) while the host keeps serving; reaching an application caller of the
/// client, it means the write it asked for cannot be trusted and must not be retried blindly.
/// </summary>
/// <remarks>
/// The message describes the condition and carries no trigger id. That keeps the id out of the
/// failure-detail text a failing attempt persists to its transition log, so the id lives only in
/// places a rename is visible in: <see cref="Trigger"/>, the halt log's <c>invariant_trigger</c>
/// parameter, and the <c>backwave.invariant.trigger</c> metric tag.
/// </remarks>
public sealed class InvariantViolationException : Exception
{
    /// <summary>Creates a violation of the named invariant, described by <paramref name="message"/>.</summary>
    /// <param name="trigger">The invariant the observed state broke.</param>
    /// <param name="message">A description of the observed state, naming the two values that disagree.</param>
    public InvariantViolationException(InvariantTrigger trigger, string message)
        : base(message) => Trigger = trigger;

    /// <summary>Creates a violation of the named invariant, wrapping the fault that exposed it.</summary>
    /// <param name="trigger">The invariant the observed state broke.</param>
    /// <param name="message">A description of the observed state, naming the two values that disagree.</param>
    /// <param name="innerException">The underlying fault the check was inspecting.</param>
    public InvariantViolationException(InvariantTrigger trigger, string message, Exception innerException)
        : base(message, innerException) => Trigger = trigger;

    /// <summary>The invariant that was broken - the stable id an alert rule and a log filter match on.</summary>
    public InvariantTrigger Trigger { get; }
}
