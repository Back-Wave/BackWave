namespace BackWave.Benchmarks.Workload;

/// <summary>
/// How jobs arrive during a run. <see cref="Drain"/> preloads the whole backlog then times the cluster
/// emptying it (the clean, reproducible headline). <see cref="Sustained"/> runs producers and consumers
/// together at a target rate, exposing the enqueue-vs-claim table contention production actually hits
/// (ADR 0027 §3).
/// </summary>
public enum ArrivalMode
{
    /// <summary>Preload N jobs, then time the cluster draining them to empty.</summary>
    Drain,

    /// <summary>Producers enqueue at a target rate while consumers run concurrently.</summary>
    Sustained,
}

/// <summary>
/// A pure, deterministic description of the job stream a run executes: how many jobs, what each job's
/// handler does (noop vs realistic delay), how they arrive, which Queue, and the payload size band. It
/// produces the concrete <see cref="BenchJob"/> stream and holds no I/O — the testable shape of the
/// workload (ADR 0027 §2–3).
/// </summary>
public sealed record WorkloadSpec
{
    /// <summary>The single Queue every benchmark job is enqueued into (single-queue headline contention).</summary>
    public const string BenchQueue = "bench";

    /// <summary>Total number of jobs in the stream.</summary>
    public required int JobCount { get; init; }

    /// <summary>
    /// Per-job handler delay. <see cref="TimeSpan.Zero"/> is the noop framework-overhead ceiling; a small
    /// positive value (~10ms) is the realistic anchor.
    /// </summary>
    public TimeSpan HandlerDelay { get; init; } = TimeSpan.Zero;

    /// <summary>Whether jobs are preloaded and drained, or enqueued at a rate alongside consumers.</summary>
    public ArrivalMode Arrival { get; init; } = ArrivalMode.Drain;

    /// <summary>
    /// Target enqueue rate (jobs/sec) for <see cref="ArrivalMode.Sustained"/>. Ignored for drain. A
    /// non-positive value means "as fast as the producers can enqueue".
    /// </summary>
    public double SustainedRatePerSecond { get; init; }

    /// <summary>
    /// Number of concurrent producer tasks that enqueue the stream. The default of 1 keeps drain preload a
    /// clean, isolated enqueue-latency measurement; a higher count is what makes <see cref="ArrivalMode.Sustained"/>
    /// a fair steady-state test — a single producer caps sustained throughput at its own enqueue rate
    /// (starving the consumers), so the arrival side must be able to outpace the cluster's drain capacity.
    /// </summary>
    public int ProducerCount { get; init; } = 1;

    /// <summary>
    /// Optional per-node worker pool size (max concurrent jobs) for the pump under test. When null the
    /// BackWave target uses its built-in aggressive default; set it to sweep concurrency and chart where
    /// throughput plateaus for a given handler delay.
    /// </summary>
    public int? PumpPoolSize { get; init; }

    /// <summary>
    /// Number of independent worker-group pumps the BackWave target runs in one process, all serving the
    /// same queue. Defaults to 1; raising it measures whether in-process pump fan-out scales throughput the
    /// way multi-process scale-out does.
    /// </summary>
    public int WorkerGroupCount { get; init; } = 1;

    /// <summary>
    /// Fixed serialized payload size band, in bytes (~100–200B). The generated payload string is sized so
    /// the serialized job lands in this band, exercising a realistic small-payload write path.
    /// </summary>
    public int PayloadSizeBytes { get; init; } = 128;

    /// <summary>The handler delay in whole milliseconds, as the generated job carries it.</summary>
    public int DelayMs => (int)Math.Round(HandlerDelay.TotalMilliseconds);

    /// <summary>
    /// Produces the deterministic job stream: <see cref="JobCount"/> identical <see cref="BenchJob"/>
    /// payloads, each carrying a fixed-size filler string and the configured handler delay. Pure — no I/O,
    /// no clock, no randomness — so a given spec always yields the same stream.
    /// </summary>
    /// <returns>An eagerly-evaluable sequence of exactly <see cref="JobCount"/> jobs.</returns>
    public IEnumerable<BenchJob> Stream()
    {
        var payload = FixedPayload(PayloadSizeBytes);
        var delayMs = DelayMs;
        for (var i = 0; i < JobCount; i++)
        {
            yield return new BenchJob(payload, delayMs);
        }
    }

    // A deterministic filler string of `bytes` ASCII characters (one byte each), so the serialized
    // payload sits in the configured ~100-200B band without depending on a clock or RNG.
    private static string FixedPayload(int bytes)
        => new('x', Math.Max(0, bytes));
}
