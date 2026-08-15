using BackWave.Jobs;

namespace BackWave.Benchmarks.Workload;

/// <summary>
/// The single job the harness drives. The source generator emits the payload record
/// <c>BenchJob(string Payload, int DelayMs)</c>, its wire format, and the handler from this method —
/// exactly as a consumer would declare a job. Pinned to one Queue ("bench") so the headline measures
/// single-queue claim contention (ADR 0027 §2).
///
/// <para>The handler is the workload's only variable: <c>DelayMs == 0</c> is the noop framework-overhead
/// ceiling; a small positive delay (~10ms) is the realistic anchor that shows overhead washing out
/// against real work.</para>
/// </summary>
public sealed class BenchmarkJobs
{
    /// <summary>Executes one benchmark job: an optional spin-free async delay, nothing else.</summary>
    [Job("bench", Queue = "bench")]
    public Task BenchJobAsync(string payload, int delayMs, JobContext context, CancellationToken cancellationToken)
    {
        // payload is carried only to exercise the ~100-200B serialization path; it is intentionally unused.
        _ = payload;
        _ = context;
        return delayMs > 0 ? Task.Delay(delayMs, cancellationToken) : Task.CompletedTask;
    }
}
