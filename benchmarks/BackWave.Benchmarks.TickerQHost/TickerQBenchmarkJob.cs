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

    /// <summary>Executes one benchmark job.</summary>
    [TickerFunction(FunctionName)]
    public async Task ExecuteAsync(TickerFunctionContext<BenchJob> context, CancellationToken cancellationToken)
    {
        var delayMs = context.Request.DelayMs;
        if (delayMs > 0)
        {
            await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
        }
    }
}
