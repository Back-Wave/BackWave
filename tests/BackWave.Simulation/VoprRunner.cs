using System.Collections.Concurrent;
using System.Diagnostics;

namespace BackWave.Tests.Simulation;

/// <summary>
/// The continuous discovery engine (issue 0087, PRD 0004, ADR 0018): a pool of
/// <see cref="Environment.ProcessorCount"/> in-process workers, each with its OWN <see cref="Simulator"/>
/// (no shared mutable state), drawing uniform-random 64-bit Seeds from a logged per-process entropy base.
/// Each worker runs the config factory (defaulting to <see cref="SwarmConfig.FromSeed"/>), and on a tripped
/// oracle persists the realized <see cref="Plan"/> to the shared <see cref="PlanStore"/>, deduped by
/// invariant ID — then CONTINUES. It never halts on a failure.
///
/// Bounded vs forever: <see cref="Run"/> takes a max-runs cap and/or a <see cref="CancellationToken"/>.
/// The console runs it forever (cap = null, cancel on Ctrl-C); the smoke test runs it bounded so the test
/// path is never an unbounded loop.
///
/// Reproducibility: the whole run replays from <see cref="VoprTally.EntropyBase"/> — seed a per-process
/// <see cref="DeterministicRandom"/> from it and the identical Seed stream (and therefore the identical
/// findings) regenerates.
/// </summary>
internal sealed class VoprRunner
{
    private readonly PlanStore _store;
    private readonly Func<ulong, SimulationOptions> _configFactory;
    private readonly int _workerCount;

    public VoprRunner(
        PlanStore store,
        Func<ulong, SimulationOptions>? configFactory = null,
        int? workerCount = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _configFactory = configFactory ?? SwarmConfig.FromSeed;
        _workerCount = workerCount ?? Environment.ProcessorCount;
        if (_workerCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(workerCount), _workerCount, "Need at least one worker.");
        }
    }

    /// <summary>
    /// Runs the discovery loop. <paramref name="maxRuns"/> caps total simulations (null = forever);
    /// <paramref name="entropyBase"/> seeds the per-process Seed stream (null = a fresh random base, logged
    /// on the returned tally); <paramref name="onTally"/> is invoked periodically with a live snapshot so the
    /// console can print progress. Returns the final tally.
    /// </summary>
    public VoprTally Run(
        long? maxRuns = null,
        ulong? entropyBase = null,
        CancellationToken cancellationToken = default,
        Action<VoprTallySnapshot>? onTally = null,
        TimeSpan? tallyInterval = null)
    {
        var seedBase = entropyBase ?? RandomEntropyBase();

        var totalRuns = 0L;
        // Honest "equivalent cluster-time tested" accumulator: each clean run's actual virtual span (ticks),
        // summed across workers via Interlocked. A failing run throws before returning a result, so its (partial)
        // virtual time is not counted — a negligible undercount, since failing runs are rare.
        var totalVirtualTicks = 0L;
        var perId = new ConcurrentDictionary<InvariantId, long>();
        // Coverage signal (issue 0090): every run's hit-set is unioned here (thread-safe), and the running
        // union's never-reached COMPLEMENT rides along on each tally snapshot and the final tally. Report
        // only — it never gates the run (an oracle trip persists a Plan and CONTINUES; coverage is observed).
        var coverage = new CoverageTracker();
        var stopwatch = Stopwatch.StartNew();
        var nextTallyAt = tallyInterval is { } iv ? iv : TimeSpan.FromSeconds(5);
        var lastTally = TimeSpan.Zero;
        var tallyGate = new object();

        // Each worker owns an independent Seed stream: salt the shared entropy base by worker index so two
        // workers never draw the same Seed, while the whole run still replays from the single base.
        void Worker(int index)
        {
            var rng = new DeterministicRandom(seedBase ^ ((ulong)index * 0x9E3779B97F4A7C15UL));

            while (!cancellationToken.IsCancellationRequested)
            {
                // Claim a run slot atomically; in bounded mode stop once the cap is reached (and don't
                // count the over-cap claim). In forever mode every claim is a real run.
                var claimed = Interlocked.Increment(ref totalRuns);
                if (maxRuns is { } cap && claimed > cap)
                {
                    Interlocked.Decrement(ref totalRuns);
                    return;
                }

                var seed = NextSeed(rng);
                var opts = _configFactory(seed);
                var sim = new Simulator(opts);

                try
                {
                    // A clean run's recorded history feeds the coverage union; a tripped oracle throws before
                    // returning a result, so that run contributes no coverage (it contributes a Plan instead).
                    var result = sim.Run(cancellationToken);
                    coverage.Union(result);
                    Interlocked.Add(ref totalVirtualTicks, result.VirtualElapsed.Ticks);
                }
                catch (OperationCanceledException)
                {
                    return; // deadline fired mid-simulation — stop this worker promptly
                }
                catch (SimulationInvariantException ex)
                {
                    perId.AddOrUpdate(ex.InvariantId, 1, static (_, n) => n + 1);

                    var plan = new Plan
                    {
                        Scenario = Scenario.FromOptions(opts),
                        FaultMap = sim.RealizedFaultMap,
                        Failure = new FailureStamp(ex.Message, ex.InvariantId),
                    };
                    // Dedup-by-ID: first failure of an ID is persisted, repeats are tallied above but not re-written.
                    _store.Save(plan);
                    // CONTINUE — a discovered bug never halts the swarm.
                }

                if (onTally is not null)
                {
                    MaybeEmitTally(onTally, tallyGate, stopwatch, ref lastTally, nextTallyAt, totalRuns, perId, coverage);
                }
            }
        }

        var workers = new Thread[_workerCount];
        for (var i = 0; i < _workerCount; i++)
        {
            var index = i;
            workers[i] = new Thread(() => Worker(index)) { IsBackground = true, Name = $"vopr-worker-{index}" };
            workers[i].Start();
        }
        foreach (var w in workers)
        {
            w.Join();
        }

        stopwatch.Stop();
        return new VoprTally(
            seedBase,
            Interlocked.Read(ref totalRuns),
            perId.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
            stopwatch.Elapsed)
        {
            Coverage = coverage.Report(),
            TotalVirtualTime = TimeSpan.FromTicks(Interlocked.Read(ref totalVirtualTicks)),
        };
    }

    private static void MaybeEmitTally(
        Action<VoprTallySnapshot> onTally,
        object gate,
        Stopwatch stopwatch,
        ref TimeSpan lastTally,
        TimeSpan interval,
        long totalRuns,
        ConcurrentDictionary<InvariantId, long> perId,
        CoverageTracker coverage)
    {
        var elapsed = stopwatch.Elapsed;
        lock (gate)
        {
            if (elapsed - lastTally < interval)
            {
                return;
            }
            lastTally = elapsed;
        }
        onTally(new VoprTallySnapshot(
            Interlocked.Read(ref totalRuns),
            perId.Count,
            elapsed.TotalSeconds > 0 ? totalRuns / elapsed.TotalSeconds : 0,
            elapsed)
        {
            Coverage = coverage.Report(),
        });
    }

    /// <summary>A uniform 64-bit Seed composed from two 32-bit PCG draws (the harness RNG is 32-bit).</summary>
    private static ulong NextSeed(DeterministicRandom rng)
        => ((ulong)rng.NextUInt() << 32) | rng.NextUInt();

    /// <summary>A non-deterministic per-process entropy base — logged so the run can be replayed exactly.</summary>
    private static ulong RandomEntropyBase()
    {
        Span<byte> bytes = stackalloc byte[8];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return BitConverter.ToUInt64(bytes);
    }
}

/// <summary>The final outcome of a <see cref="VoprRunner.Run"/>: enough for a test to assert and a console to print.</summary>
internal sealed record VoprTally(
    ulong EntropyBase,
    long TotalRuns,
    IReadOnlyDictionary<InvariantId, long> FailuresByInvariant,
    TimeSpan Elapsed)
{
    /// <summary>Distinct invariant IDs tripped (the number of unique failures = number of persisted Plans).</summary>
    public int UniqueFailures => FailuresByInvariant.Count;

    /// <summary>Total failing runs across all IDs (≥ <see cref="UniqueFailures"/> when bugs repeat).</summary>
    public long TotalFailures => FailuresByInvariant.Values.Sum();

    public double RunsPerSecond => Elapsed.TotalSeconds > 0 ? TotalRuns / Elapsed.TotalSeconds : 0;

    /// <summary>
    /// The unioned Coverage report at run's end (issue 0090): the never-reached legal edges and never-hit
    /// Situations across every clean run, plus the two hit fractions. A report artifact, not a gate.
    /// </summary>
    public CoverageReport? Coverage { get; init; }

    /// <summary>
    /// The summed virtual (simulated) cluster time across every clean run in this tally — the honest
    /// "equivalent cluster-time tested" figure for the run, fed into the cross-run coverage ledger.
    /// </summary>
    public TimeSpan TotalVirtualTime { get; init; }
}

/// <summary>A live progress snapshot emitted mid-run so the console can print a running tally.</summary>
internal sealed record VoprTallySnapshot(long TotalRuns, int UniqueFailures, double RunsPerSecond, TimeSpan Elapsed)
{
    /// <summary>The running Coverage union's complement at the moment this snapshot was emitted (issue 0090).</summary>
    public CoverageReport? Coverage { get; init; }
}
