namespace BackWave.Diagnostics;

// The Shell's verdict on one handler execution, handed to the record edge so the recorder never
// re-derives it from the raw exception: the caller classifies, the recorder records.
//
// The distinction the raw exception cannot carry is cancellation. Only the Shell knows whether an
// OperationCanceledException is a cancel it ASKED for - an operator cancel, or host shutdown - or one
// the handler raised on its own (an HttpClient timeout surfacing as TaskCanceledException). The first
// is deliberate non-execution: neither consumed nor failed. The second is a plain failure that retries
// and dead-letters like any other, so it must count as one - otherwise the failed counter and the
// process-duration histogram would miss it while the dead-letter counter still fired, breaking the
// invariant that dead_lettered is a terminal subset of the failed count.
internal readonly record struct ExecutionOutcome
{
    private ExecutionOutcome(Exception? failure, bool cancelled)
    {
        Failure = failure;
        IsCancelled = cancelled;
    }

    // The handler returned normally.
    public static ExecutionOutcome Success { get; } = new(null, false);

    // The Shell cancelled the execution (an operator cancel, or host shutdown). Never a cancellation
    // the handler raised on its own - that is a Failed.
    public static ExecutionOutcome Cancelled { get; } = new(null, true);

    // The handler threw, whatever the exception type - an OperationCanceledException the Shell did not
    // ask for included.
    public static ExecutionOutcome Failed(Exception exception) => new(exception, false);

    // The throwing exception on a Failed verdict; null on Success and Cancelled.
    public Exception? Failure { get; }

    public bool IsCancelled { get; }
}
