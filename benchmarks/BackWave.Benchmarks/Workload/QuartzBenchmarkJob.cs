using Quartz;

namespace BackWave.Benchmarks.Workload;

/// <summary>
/// The payload a Quartz firing carries: the same filler string and handler delay as the generated
/// <c>BenchJob</c>, stored by Quartz as System.Text.Json on the trigger.
/// </summary>
/// <param name="Payload">The fixed filler payload, carried to size the serialized job; unused by the body.</param>
/// <param name="DelayMs">Per-job handler delay in milliseconds; 0 is the noop framework-overhead ceiling.</param>
public sealed record QuartzBenchInput(string Payload, int DelayMs);

/// <summary>
/// The single job the Quartz.NET competitor drives, the twin of <see cref="BenchmarkJobs"/>: an optional
/// spin-free async delay, then a completion stamp.
/// </summary>
public sealed class QuartzBenchmarkJob(CompletionLog completions) : IJob<QuartzBenchInput>
{
    /// <inheritdoc/>
    public async ValueTask Execute(IJobExecutionContext context, QuartzBenchInput input, CancellationToken cancellationToken)
    {
        if (input.DelayMs > 0)
        {
            await Task.Delay(input.DelayMs, cancellationToken).ConfigureAwait(false);
        }

        completions.Record(context.Trigger.Key.Name);
    }
}
