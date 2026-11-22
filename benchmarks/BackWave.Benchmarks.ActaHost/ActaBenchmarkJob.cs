using Acta;
using BackWave.Benchmarks.Targets;

namespace BackWave.Benchmarks.Workload;

/// <summary>
/// The input of one Acta bench job: the same filler string and handler delay as the generated <c>BenchJob</c>, plus
/// the key the harness stamps its clocks against.
/// </summary>
/// <param name="Key">The harness-assigned identity of this enqueue, unique within a run.</param>
/// <param name="Payload">The fixed filler payload, carried to size the stored input; unused by the body.</param>
/// <param name="DelayMs">Per-job handler delay in milliseconds; 0 is the noop framework-overhead ceiling.</param>
public sealed record ActaBenchInput(string Key, string Payload, int DelayMs);

/// <summary>
/// The single handler the Acta competitor drives, the twin of the harness's <c>BenchmarkJobs</c>: an optional
/// spin-free async delay, then a completion stamp.
/// </summary>
public sealed class ActaBenchmarkHandler(CompletionLog completions)
{
    /// <summary>The bench job. Every policy on it is the Acta default.</summary>
    [Job(ActaHostProtocol.JobName)]
    public async Task Run(ActaBenchInput input, CancellationToken ct)
    {
        if (input.DelayMs > 0)
        {
            await Task.Delay(input.DelayMs, ct).ConfigureAwait(false);
        }

        completions.Record(input.Key);
    }
}
