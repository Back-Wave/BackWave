namespace BackWave.Storage;

/// <summary>
/// Why an attempt went wrong and sent its job back to Scheduled for another attempt. A job that carries
/// a cause and is Scheduled is Retrying: it is waiting to run again because something failed, not because
/// it is new or because a worker handed it back on a clean stop.
///
/// Members are a stable wire identity (persisted by number): every adapter writes <c>(int)</c> of this
/// enum into a nullable <c>int</c> column, and no cause is stored as null, never as a number. The assigned
/// value, not the member's position, is the storage contract - give a new member the next free number and
/// never reuse a retired one.
/// </summary>
public enum RetryCause
{
    /// <summary>The handler failed (it threw or reported a failure) and the retry policy scheduled another attempt.</summary>
    HandlerFailed = 1,

    /// <summary>The lease lapsed before the worker reported an outcome (for example the worker crashed or stalled), and the store scheduled another attempt.</summary>
    LeaseExpired = 2,
}
