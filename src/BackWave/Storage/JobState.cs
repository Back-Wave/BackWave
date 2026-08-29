namespace BackWave.Storage;

/// <summary>
/// The lifecycle states a job moves through.
///
/// Members are a stable wire identity (persisted by number): every adapter writes <c>(int)</c> of this
/// enum into an <c>int</c> column, and the claim and lease-expiry partial indexes hard-code the number in
/// their predicates. The assigned value, not the member's position, is the storage contract - inserting,
/// reordering, or removing a member renumbers the rest, and every row already written then reads back as
/// a different state. Give a new member the next free number and never reuse a retired one; renumber only
/// with a migration that rewrites the existing rows.
/// </summary>
public enum JobState
{
    /// <summary>Enqueued and waiting for its due time; eligible to be claimed once due.</summary>
    Scheduled = 0,

    /// <summary>Held back until its parents reach a terminal state; becomes Scheduled once the last parent resolves.</summary>
    AwaitingParent = 1,

    /// <summary>Claimed by a worker and running under a lease that must be renewed by heartbeat.</summary>
    Leased = 2,

    /// <summary>Terminal: the job completed successfully.</summary>
    Succeeded = 3,

    /// <summary>Terminal: the job was cancelled (by an operator, or because an on-success parent failed).</summary>
    Cancelled = 4,

    /// <summary>Terminal: the job exhausted its retry budget and was set aside for inspection.</summary>
    DeadLettered = 5,

    /// <summary>Terminal: the job could not be routed to a handler and was set aside.</summary>
    Quarantined = 6,
}

/// <summary>Queries over the job state machine.</summary>
public static class JobStates
{
    /// <summary>
    /// Whether <paramref name="state"/> is terminal. A terminal job never transitions again on its
    /// own — only an explicit operator action (such as a requeue) can move it.
    /// </summary>
    /// <param name="state">The state to test.</param>
    /// <returns>True for Succeeded, Cancelled, DeadLettered, or Quarantined; false otherwise.</returns>
    public static bool IsTerminal(this JobState state)
        => state is JobState.Succeeded or JobState.Cancelled or JobState.DeadLettered or JobState.Quarantined;
}
