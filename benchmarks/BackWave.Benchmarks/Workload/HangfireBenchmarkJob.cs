using Hangfire;

namespace BackWave.Benchmarks.Workload;

/// <summary>
/// The single job the Hangfire competitor drives — the head-to-head twin of <see cref="BenchmarkJobs"/>.
/// Hangfire enqueues a reflected method call and rehydrates the arguments from Newtonsoft.Json, so the
/// handler is a plain static method whose body matches BackWave's exactly: an optional spin-free async
/// delay, nothing else. Pinned to the same single Queue ("bench") so both systems measure single-queue
/// claim contention (ADR 0027 §2).
/// </summary>
public static class HangfireBenchmarkJob
{
    /// <summary>
    /// Executes one benchmark job under Hangfire: an optional async delay of <paramref name="delayMs"/>
    /// milliseconds, and nothing else. The payload is carried only to exercise Hangfire's serialization
    /// path (the same ~100–200B band BackWave writes) and is intentionally unused.
    /// </summary>
    /// <param name="payload">The fixed filler payload, carried to size the serialized job; unused by the body.</param>
    /// <param name="delayMs">Per-job handler delay in milliseconds; 0 is the noop framework-overhead ceiling.</param>
    /// <returns>A task that completes after the configured delay (immediately when <paramref name="delayMs"/> is 0).</returns>
    [Queue(WorkloadSpec.BenchQueue)]
    public static Task ExecuteAsync(string payload, int delayMs)
    {
        _ = payload;
        return delayMs > 0 ? Task.Delay(delayMs) : Task.CompletedTask;
    }
}
