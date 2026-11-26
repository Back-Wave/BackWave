namespace BackWave.Driver;

/// <summary>
/// A node's Unwritten outcomes: the finished jobs whose outcome write has not answered. They are the terminal
/// outcomes buffered for one coalesced write, in completion order, plus the rows of the one write in flight.
/// Each still holds its store Lease. The pump stays single-writer, so a flush while a write is in flight waits
/// for that write's answer. A crash discards all of it (a fresh Driver is installed), so those Leases lapse and
/// the jobs are reclaimed, which is At-Least-Once working as designed.
/// <para>
/// The configured headroom is extra slots that only these outcomes can hold. A faulted write closes it until a
/// write lands again, so a node whose writes fail holds a slot for every Unwritten outcome.
/// </para>
/// </summary>
internal sealed class UnwrittenOutcomes(int maxBatch, int headroom)
{
    private List<ReportedOutcome> _buffer = [];

    private int _rowsInFlight;

    private bool _flushDeferred;

    private bool _lastWriteFaulted;

    /// <summary>The buffered outcomes plus the rows of the write in flight.</summary>
    public int Count => _buffer.Count + _rowsInFlight;

    public int BufferedCount => _buffer.Count;

    public IEnumerable<Guid> BufferedJobIds => _buffer.Select(row => row.JobId);

    public bool IsBufferFull => _buffer.Count >= maxBatch;

    /// <summary>Whether this node is configured with any headroom, open or closed.</summary>
    public bool HasHeadroom => headroom > 0;

    /// <summary>The headroom the Unwritten outcomes may fill now: none after a faulted write.</summary>
    private int OpenHeadroom => _lastWriteFaulted ? 0 : headroom;

    /// <summary>The pool slots the Unwritten outcomes hold: those beyond the open headroom.</summary>
    public int SlotsHeld => Math.Max(0, Count - OpenHeadroom);

    public void Add(ReportedOutcome outcome) => _buffer.Add(outcome);

    /// <summary>
    /// Takes the next batch for the write lane, or null when the buffer is empty or a write is already in flight.
    /// The second case defers the flush to that write's answer.
    /// </summary>
    public Command.ReportOutcomeBatch? TryFlush()
    {
        if (_buffer.Count == 0)
        {
            return null;
        }
        if (_rowsInFlight > 0)
        {
            _flushDeferred = true;
            return null;
        }
        var batch = Drain();
        _rowsInFlight = batch.Outcomes.Count;
        _flushDeferred = _buffer.Count > 0;
        return batch;
    }

    /// <summary>The write in flight landed: it opens the headroom again, and returns the flush it deferred, if any.</summary>
    public Command.ReportOutcomeBatch? Landed()
    {
        _lastWriteFaulted = false;
        _rowsInFlight = 0;
        return _flushDeferred ? TryFlush() : null;
    }

    /// <summary>The write in flight faulted: its rows are dropped, and the headroom closes.</summary>
    public void Faulted()
    {
        _lastWriteFaulted = true;
        _rowsInFlight = 0;
    }

    /// <summary>The Shell never issued the write: its rows are dropped, and the headroom stays as it was.</summary>
    public void NotIssued() => _rowsInFlight = 0;

    /// <summary>
    /// Drains the next batch of buffered outcomes for a clean stop, or null when the buffer is empty. The stop
    /// writes each batch to completion itself, so this does not count it as in flight.
    /// </summary>
    public Command.ReportOutcomeBatch? DrainBuffered() => _buffer.Count > 0 ? Drain() : null;

    /// <summary>Drains up to the batch bound of buffered outcomes into one write, in completion order.</summary>
    private Command.ReportOutcomeBatch Drain()
    {
        List<ReportedOutcome> rows;
        if (_buffer.Count <= maxBatch)
        {
            rows = _buffer;
            _buffer = [];
        }
        else
        {
            rows = _buffer.GetRange(0, maxBatch);
            _buffer.RemoveRange(0, maxBatch);
        }
        return new Command.ReportOutcomeBatch(rows);
    }
}
