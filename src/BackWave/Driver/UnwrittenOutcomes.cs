namespace BackWave.Driver;

/// <summary>
/// A node's Unwritten Outcomes: the finished jobs whose outcome write has not answered. They are the terminal
/// outcomes buffered for one coalesced write, in completion order, plus the rows of the one write in flight.
/// Each still holds its store Lease. The pump stays single-writer, so a flush while a write is in flight waits
/// for that write's answer. A crash discards all of it (a fresh Driver is installed), so those Leases lapse and
/// the jobs are reclaimed, which is At-Least-Once working as designed.
/// <para>
/// The configured headroom is extra slots that only these outcomes can hold. A faulted write closes it until a
/// write lands again, so a node whose writes fail holds a slot for every Unwritten Outcome.
/// </para>
/// </summary>
internal sealed class UnwrittenOutcomes(int maxBatch, int headroom)
{
    private List<ReportedOutcome> _buffer = [];

    private int _rowsInFlight;

    private bool _flushDeferred;

    private bool _lastWriteFaulted;

    public int Count => _buffer.Count + _rowsInFlight;

    public int BufferedCount => _buffer.Count;

    public IEnumerable<Guid> BufferedJobIds => _buffer.Select(row => row.JobId);

    public bool IsBufferFull => _buffer.Count >= maxBatch;

    public bool IsHeadroomConfigured => headroom > 0;

    public bool IsHeadroomOpen => OpenHeadroom > 0;

    public int SlotsHeld => Math.Max(0, Count - OpenHeadroom);

    private int OpenHeadroom => _lastWriteFaulted ? 0 : headroom;

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

    /// <summary>Returns the flush deferred behind this write, or null.</summary>
    public Command.ReportOutcomeBatch? Landed()
    {
        _lastWriteFaulted = false;
        _rowsInFlight = 0;
        return _flushDeferred ? TryFlush() : null;
    }

    /// <summary>
    /// Leaves a deferred flush to the next poll, heartbeat or finish trigger, so a failing store is not hit
    /// again at once.
    /// </summary>
    public void Faulted()
    {
        _lastWriteFaulted = true;
        _rowsInFlight = 0;
    }

    /// <summary>
    /// Leaves a deferred flush to the next poll, heartbeat or finish trigger, so a failing store is not hit
    /// again at once.
    /// </summary>
    public void NotIssued() => _rowsInFlight = 0;

    /// <summary>
    /// Drains the next batch of buffered outcomes for a clean stop, or null when the buffer is empty. The stop
    /// writes each batch to completion itself, so this does not count it as in flight.
    /// </summary>
    public Command.ReportOutcomeBatch? DrainBuffered() => _buffer.Count > 0 ? Drain() : null;

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
