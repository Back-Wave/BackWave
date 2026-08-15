namespace BackWave.Storage;

/// <summary>The lifecycle states a job moves through.</summary>
public enum JobState
{
    /// <summary>Enqueued and waiting for its due time; eligible to be claimed once due.</summary>
    Scheduled,

    /// <summary>Held back until its parents reach a terminal state; becomes Scheduled once the last parent resolves.</summary>
    AwaitingParent,

    /// <summary>Claimed by a worker and running under a lease that must be renewed by heartbeat.</summary>
    Leased,

    /// <summary>Terminal: the job completed successfully.</summary>
    Succeeded,

    /// <summary>Terminal: the job was cancelled (by an operator, or because an on-success parent failed).</summary>
    Cancelled,

    /// <summary>Terminal: the job exhausted its retry budget and was set aside for inspection.</summary>
    DeadLettered,

    /// <summary>Terminal: the job could not be routed to a handler and was set aside.</summary>
    Quarantined,
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
