using BackWave.Diagnostics;

namespace BackWave.Storage;

/// <summary>
/// What a Pump's own process last wrote about it: its state, the fault behind a halt or a degrade, and
/// the instant it was written. A Pump is one Worker Group's claim, dispatch and report loop in one
/// process, identified by its worker id, the same id a leased job's
/// <see cref="Monitor.JobSnapshot.LeaseOwner"/> carries.
/// <para>
/// Stale is not a state any process writes. It is the reader's verdict, made against the reader's own
/// clock: call <see cref="IsStaleAt"/> and <see cref="IsStoppedAt"/> rather than comparing ages yourself.
/// </para>
/// </summary>
public sealed record PumpStatus
{
    private const int MissedReportsBeforeQuiet = 3;

    /// <summary>The longest <see cref="FaultType"/> a store must hold.</summary>
    public const int MaxFaultTypeLength = 450;

    /// <summary>The Pump's worker id. Unique per Pump per process start.</summary>
    public required string WorkerId { get; init; }

    /// <summary>The Worker Group this Pump runs.</summary>
    public required string WorkerGroup { get; init; }

    /// <summary>The state the Pump's process last wrote.</summary>
    public required PumpState State { get; init; }

    /// <summary>
    /// The full name of the exception type behind a <see cref="PumpState.Halted"/> or
    /// <see cref="PumpState.Degraded"/> state, or of a <see cref="PumpState.Stopped"/> Pump that halted before
    /// it stopped, at most <see cref="MaxFaultTypeLength"/> characters; <see langword="null"/> otherwise.
    /// Never the exception's message, which can name a host, a database and a login.
    /// </summary>
    public string? FaultType { get; init; }

    /// <summary>
    /// The invariant a named check found broken, for a halt that check raised; <see langword="null"/>
    /// otherwise, and when this reader does not know the stored trigger name.
    /// </summary>
    public InvariantTrigger? Trigger { get; init; }

    /// <summary>The instant the Pump's process wrote this row, on that process's clock.</summary>
    public required DateTimeOffset ReportedAt { get; init; }

    /// <summary>How often the Pump's process writes this row while it runs.</summary>
    public required TimeSpan ReportInterval { get; init; }

    /// <summary>
    /// True when the row is too old to trust: the Pump has not stopped, and its process has not written
    /// for more than three of its <see cref="ReportInterval"/>s. The process crashed, lost the store, or
    /// its writer died; the row cannot tell which. A <see cref="PumpState.Stopped"/> or
    /// <see cref="PumpState.Stopping"/> row never goes stale.
    /// </summary>
    /// <param name="now">The reader's current instant.</param>
    /// <returns><see langword="true"/> when the row is stale at <paramref name="now"/>.</returns>
    public bool IsStaleAt(DateTimeOffset now) =>
        State is not (PumpState.Stopped or PumpState.Stopping) && IsQuietAt(now);

    /// <summary>
    /// True when the Pump stopped with its process: the row says <see cref="PumpState.Stopped"/>, or it
    /// says <see cref="PumpState.Stopping"/> and its process has written nothing since, for more than
    /// three of its <see cref="ReportInterval"/>s. A Pump that halted first also reads as stopped once its
    /// process stops. A stop whose final write did not reach the store still reads as stopped, not as a
    /// crash.
    /// </summary>
    /// <param name="now">The reader's current instant.</param>
    /// <returns><see langword="true"/> when the Pump reads as stopped at <paramref name="now"/>.</returns>
    public bool IsStoppedAt(DateTimeOffset now) =>
        State == PumpState.Stopped || (State == PumpState.Stopping && IsQuietAt(now));

    private bool IsQuietAt(DateTimeOffset now) => now - ReportedAt > ReportInterval * MissedReportsBeforeQuiet;
}

/// <summary>The state a Pump's own process writes for it. Values are persisted and pinned.</summary>
public enum PumpState
{
    /// <summary>
    /// A stored value this version does not define, written by a newer BackWave. The row is still
    /// listed so the Pump stays visible. No process writes it.
    /// </summary>
    Unknown = -1,

    /// <summary>Claiming and executing, with no store fault in the last write interval.</summary>
    Running = 0,

    /// <summary>Still claiming and executing, but rode out at least one store fault in the last write interval.</summary>
    Degraded = 1,

    /// <summary>
    /// Fail-stopped: a named invariant check failed, or it hit a fault it could not classify as transient.
    /// Stays halted until its process stops, then reads <see cref="Stopped"/> and keeps the fault type and
    /// trigger.
    /// </summary>
    Halted = 2,

    /// <summary>Its process has begun a clean stop, and the Pump is handing its Leases back.</summary>
    Stopping = 3,

    /// <summary>
    /// Stopped with its process. A Pump that halted first keeps its <see cref="PumpStatus.FaultType"/>
    /// and <see cref="PumpStatus.Trigger"/>.
    /// </summary>
    Stopped = 4,
}
