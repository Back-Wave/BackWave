using System.Diagnostics;

namespace BackWave.Benchmarks.Workload;

/// <summary>
/// Drives the enqueue side of a run across <see cref="WorkloadSpec.ProducerCount"/> concurrent producers and
/// returns the per-call enqueue latencies. A single-threaded producer caps sustained throughput at its own
/// enqueue rate — it starves the consumers and the run measures the producer, not the engine (bench-0137 fix)
/// — so the arrival side fans out until it can outpace the cluster's drain capacity. The actual enqueue call
/// is supplied by the target, so the fan-out, partitioning, pacing, and timing are <em>identical</em> for
/// BackWave (async client) and Hangfire (sync client), keeping the comparison fair (ADR 0027 §5).
/// </summary>
internal static class ParallelProducer
{
    /// <summary>
    /// Enqueues the spec's whole job stream across <see cref="WorkloadSpec.ProducerCount"/> producers, each
    /// pacing at an equal share of <paramref name="paceRatePerSecond"/>, and returns every per-call latency.
    /// </summary>
    /// <param name="spec">The workload whose stream is enqueued; its <see cref="WorkloadSpec.ProducerCount"/> sets the fan-out.</param>
    /// <param name="paceRatePerSecond">Total target enqueue rate; non-positive means "as fast as possible". Split evenly across producers.</param>
    /// <param name="enqueueAsync">The target's enqueue action for one job; invoked concurrently across producers, so it must be thread-safe.</param>
    /// <param name="cancellationToken">Cancels the enqueue.</param>
    /// <returns>The per-call enqueue latencies across all producers, concatenated.</returns>
    public static async Task<IReadOnlyList<TimeSpan>> RunAsync(
        WorkloadSpec spec,
        double paceRatePerSecond,
        Func<BenchJob, CancellationToken, ValueTask> enqueueAsync,
        CancellationToken cancellationToken)
    {
        // Materialize once so producers partition a fixed stream by index range — correct even if a future
        // workload yields a non-uniform stream (a latency distribution, a failure fraction), not just N clones.
        var jobs = spec.Stream().ToArray();
        var producerCount = Math.Max(1, spec.ProducerCount);
        var ranges = Partition(jobs.Length, producerCount);
        // Each producer paces at its share of the target rate, so the producers together hit the total rate.
        var ratePerProducer = paceRatePerSecond > 0 ? paceRatePerSecond / producerCount : 0d;

        if (producerCount == 1)
        {
            return await ProduceAsync(jobs, ranges[0].Offset, ranges[0].Count, ratePerProducer, enqueueAsync, cancellationToken)
                .ConfigureAwait(false);
        }

        var tasks = ranges
            .Select(range => Task.Run(
                () => ProduceAsync(jobs, range.Offset, range.Count, ratePerProducer, enqueueAsync, cancellationToken),
                cancellationToken))
            .ToArray();
        var perProducer = await Task.WhenAll(tasks).ConfigureAwait(false);

        var all = new List<TimeSpan>(jobs.Length);
        foreach (var latencies in perProducer)
        {
            all.AddRange(latencies);
        }

        return all;
    }

    private static async Task<List<TimeSpan>> ProduceAsync(
        BenchJob[] jobs, int offset, int count, double paceRatePerSecond,
        Func<BenchJob, CancellationToken, ValueTask> enqueueAsync, CancellationToken cancellationToken)
    {
        var latencies = new List<TimeSpan>(count);
        var perJobInterval = paceRatePerSecond > 0 ? TimeSpan.FromSeconds(1d / paceRatePerSecond) : TimeSpan.Zero;
        var paceClock = Stopwatch.StartNew();
        var stopwatch = new Stopwatch();
        for (var i = 0; i < count; i++)
        {
            if (perJobInterval > TimeSpan.Zero)
            {
                var due = perJobInterval * i;
                var wait = due - paceClock.Elapsed;
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
                }
            }

            stopwatch.Restart();
            await enqueueAsync(jobs[offset + i], cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
            latencies.Add(stopwatch.Elapsed);
        }

        return latencies;
    }

    /// <summary>
    /// Splits <paramref name="total"/> items into <paramref name="parts"/> contiguous ranges as evenly as
    /// possible (the first <c>total % parts</c> ranges carry one extra item), so every job is enqueued exactly
    /// once across the producers.
    /// </summary>
    /// <param name="total">Total number of items to partition.</param>
    /// <param name="parts">Number of producers to split across; clamped to at least 1.</param>
    /// <returns>One <c>(offset, count)</c> range per producer, covering the whole stream without overlap.</returns>
    public static (int Offset, int Count)[] Partition(int total, int parts)
    {
        parts = Math.Max(1, parts);
        var ranges = new (int Offset, int Count)[parts];
        var baseCount = total / parts;
        var remainder = total % parts;
        var offset = 0;
        for (var p = 0; p < parts; p++)
        {
            var count = baseCount + (p < remainder ? 1 : 0);
            ranges[p] = (offset, count);
            offset += count;
        }

        return ranges;
    }
}
