using MassTransit;

namespace BackWave.Benchmarks.Workload;

/// <summary>
/// The message one MassTransit enqueue sends: the same filler string and handler delay as the generated
/// <c>BenchJob</c>, plus the key the harness stamps its clocks against.
/// </summary>
/// <param name="Key">The harness-assigned identity of this enqueue, unique within a run.</param>
/// <param name="Payload">The fixed filler payload, carried to size the serialized message; unused by the body.</param>
/// <param name="DelayMs">Per-job handler delay in milliseconds; 0 is the noop framework-overhead ceiling.</param>
public sealed record MassTransitBenchMessage(string Key, string Payload, int DelayMs);

/// <summary>
/// The single consumer the MassTransit competitor drives, the twin of the harness's <c>BenchmarkJobs</c>: an optional
/// spin-free async delay, then a completion stamp.
/// </summary>
public sealed class MassTransitBenchmarkConsumer(CompletionLog completions) : IConsumer<MassTransitBenchMessage>
{
    /// <inheritdoc/>
    public async Task Consume(ConsumeContext<MassTransitBenchMessage> context)
    {
        var message = context.Message;
        if (message.DelayMs > 0)
        {
            await Task.Delay(message.DelayMs, context.CancellationToken).ConfigureAwait(false);
        }

        completions.Record(message.Key);
    }
}
