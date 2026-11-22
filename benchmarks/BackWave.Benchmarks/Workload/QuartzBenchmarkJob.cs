using System.Collections.Concurrent;
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
/// The instant each Quartz firing's handler finished, keyed by trigger name. Quartz deletes a one-off trigger
/// when its firing completes, so the store keeps no completion timestamp to read back afterwards.
/// </summary>
public sealed class QuartzCompletionLog
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _completedAt = new();
    private int _executions;

    /// <summary>The handler-finished instant per trigger name.</summary>
    public IReadOnlyDictionary<string, DateTimeOffset> CompletedAt => _completedAt;

    /// <summary>How many times the handler ran, so a trigger fired twice is not hidden by the keyed map.</summary>
    public int Executions => Volatile.Read(ref _executions);

    /// <summary>Records that the firing of <paramref name="triggerName"/> finished now.</summary>
    public void Record(string triggerName)
    {
        _completedAt[triggerName] = DateTimeOffset.UtcNow;
        Interlocked.Increment(ref _executions);
    }

    /// <summary>Forgets every recorded completion, before the next run.</summary>
    public void Clear()
    {
        _completedAt.Clear();
        _executions = 0;
    }
}

/// <summary>
/// The single job the Quartz.NET competitor drives, the twin of <see cref="BenchmarkJobs"/>: an optional
/// spin-free async delay, then a completion stamp.
/// </summary>
public sealed class QuartzBenchmarkJob(QuartzCompletionLog completions) : IJob<QuartzBenchInput>
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
