using BackWave.Benchmarks.Workload;
using TickerQ.Utilities.Base;

namespace BackWave.Benchmarks.TickerQHost;

/// <summary>
/// The TickerQ handler for the benchmark job: it mirrors BackWave's <c>bench</c> handler (sleep for the
/// configured delay, or return at once for noop).
/// </summary>
public sealed class TickerQBenchmarkJob
{
    /// <summary>The function name every benchmark ticker targets.</summary>
    public const string FunctionName = "bench";

    private static int _executions;

    /// <summary>Handler executions in this process; the host is fresh per run, so this is the run's count.</summary>
    public static int Executions => Volatile.Read(ref _executions);

    /// <summary>Executes one benchmark job.</summary>
    [TickerFunction(FunctionName)]
    public async Task ExecuteAsync(TickerFunctionContext<BenchJob> context, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _executions);
        var delayMs = context.Request.DelayMs;
        if (delayMs > 0)
        {
            await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
        }
    }
}
