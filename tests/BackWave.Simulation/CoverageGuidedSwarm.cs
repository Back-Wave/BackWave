using System.Diagnostics;

namespace BackWave.Tests.Simulation;

/// <summary>How a candidate was disposed of by the two-part retention rule (issue 0125, ADR 0025 decision 3).</summary>
internal enum RetentionOutcome
{
    /// <summary>Clean and advanced the coverage union — added to the corpus.</summary>
    AdvancedCoverage,

    /// <summary>Clean but advanced nothing — discarded (the parent's energy decays).</summary>
    Cold,

    /// <summary>Tripped a not-yet-seen invariant — the raw realized Plan is persisted to the <see cref="PlanStore"/>.</summary>
    NewInvariant,

    /// <summary>Tripped an already-seen invariant — tallied but not re-persisted (deduped by ID).</summary>
    RepeatInvariant,

    /// <summary>
    /// A clean trace mutant whose realized map exhibited a never-seen interaction tuple — added to the corpus
    /// (issue 0126). The trace gradient is co-occurring Situations, not the endpoint-shaped edge/Situation union.
    /// </summary>
    NewInteraction,

    /// <summary>
    /// A trace edit that was inert — vetoed by a budget guard, or aimed at an address the replay never consults
    /// — so its realized map equals the parent's (issue 0126). Discarded rather than banked as a duplicate.
    /// </summary>
    VetoCollapsed,
}

/// <summary>
/// The coverage-guided explorer (issue 0125, ADR 0025 decisions 3/6-config/8) — the first guided loop on top of
/// the Phase-3a discovery engine. It grows an in-memory <see cref="Corpus"/> of coverage-advancing Plans by
/// mutating their config-space (<see cref="ConfigMutator"/>) and evaluating each mutant by <b>generate</b> (a
/// fresh, in-envelope run through the budget guards), applying the two-part retention rule:
/// <list type="number">
/// <item>a mutant tripping a <b>not-yet-seen <see cref="InvariantId"/></b> is routed to the failure path —
/// <see cref="PlanStore.Save"/> of the raw realized Plan, deduped by ID (the real prize); minimization
/// (<see cref="SeedMinimizer.Minimize"/>) is deferred to triage so it never stalls the hot search; or</item>
/// <item>a clean mutant that <b>advances coverage</b> (a new edge/Situation in the 0090 union) is added to the
/// corpus.</item>
/// </list>
/// Everything else is cold and discarded. Per-entry <see cref="CorpusEntry.Energy"/> decays on cold mutations and
/// replenishes on a productive child, and the scheduler picks the next entry by weighted-random energy.
///
/// <para>Single-threaded for now (deterministic and debuggable); multi-threading is issue 0128. Reuses
/// <see cref="SeedMinimizer"/> / <see cref="PlanStore"/> / <see cref="CoverageTracker"/> unchanged — this is
/// purely additive.</para>
/// </summary>
internal sealed class CoverageGuidedSwarm
{
    private readonly PlanStore _store;
    private readonly CoverageTracker _coverage = new();
    private readonly InteractionTuples _tuples = new();
    private readonly Corpus _corpus = new();
    private readonly HashSet<InvariantId> _seen = [];
    private readonly Dictionary<InvariantId, long> _failures = [];

    // Honest "equivalent cluster-time tested" accumulator: each clean sim's actual virtual span (ticks), summed
    // across workers via Interlocked over both the scenario and trace-replay paths. Fed into the cross-run ledger.
    private long _virtualTicks;

    /// <summary>
    /// One lock guarding ALL shared mutable state — the corpus (entries + per-entry energy/stage), the coverage
    /// union, the interaction-tuple novelty set, and the seen/failure tallies (issue 0128, ADR 0025 decision 9).
    /// Workers run the expensive <see cref="Simulator"/> OUTSIDE this lock and take it only to pick a parent and
    /// to apply retention, mirroring <see cref="VoprRunner"/>'s lock-guard shape. The session trajectory is
    /// non-deterministic by design under more than one worker; every banked artifact still replays from itself.
    /// </summary>
    private readonly object _gate = new();

    public CoverageGuidedSwarm(PlanStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <summary>The growing corpus of coverage-advancing clean Plans.</summary>
    internal Corpus Corpus => _corpus;

    /// <summary>
    /// The corpus's clean Plans, for cross-session persistence (<see cref="GuidedCorpusStore.SaveAll"/>) — the
    /// export half of the compounding-depth loop whose import half is <see cref="Seed"/>.
    /// </summary>
    internal IReadOnlyList<Plan> CorpusPlans => _corpus.Entries.Select(e => e.Plan).ToList();

    /// <summary>
    /// Reconstitutes a persisted corpus before <see cref="Run"/> starts, so a cycle stands on the previous
    /// cycle's shoulders instead of re-warming from an empty corpus (<see cref="GuidedCorpusStore"/>). Each
    /// <paramref name="plans"/> entry is REPLAYED (deterministic, via <see cref="FaultPlan.Replay"/>): a clean
    /// replay folds its hits into the coverage union and interaction-tuple frontier — so the retention gate is
    /// rebuilt exactly as this build measures it — and re-enters the corpus as a fresh <see cref="CorpusEntry"/>.
    /// A persisted Plan that now trips an oracle (product drift since it was banked) is routed to the bug sink
    /// via <see cref="RecordFailure"/> and NOT re-added — reload doubles as a regression check.
    ///
    /// <para>Reconstitution replay is deliberately NOT added to <see cref="GuidedTally.TotalVirtualTime"/>: the
    /// cycle that first banked these Plans already recorded their virtual time in the cross-run ledger, so
    /// re-counting it here would inflate the "equivalent cluster-time tested" headline. Runs single-threaded
    /// (before the worker pool starts); returns how many Plans reloaded cleanly vs regressed into findings.</para>
    /// </summary>
    internal (int Reloaded, int Regressed) Seed(IReadOnlyList<Plan> plans, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plans);

        var reloaded = 0;
        var regressed = 0;
        foreach (var plan in plans)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var scenario = plan.Scenario;
            var sim = new Simulator(scenario.ToOptions(), FaultPlan.Replay(scenario.Seed, plan.FaultMap));
            try
            {
                var result = sim.Run(cancellationToken);
                // Fold BOTH frontiers back in — a persisted Plan may be config-origin (edge/Situation union) or
                // trace-origin (interaction tuple); unioning both reconstitutes the full retention gate.
                var hits = CoverageTracker.HitsOf(result);
                _coverage.Union(hits);
                _tuples.Union(hits.Situations);
                _corpus.Add(new CorpusEntry(plan));
                reloaded++;
            }
            catch (OperationCanceledException)
            {
                break; // deadline fired mid-reseed — stop reconstituting, run with what we have
            }
            catch (SimulationInvariantException ex)
            {
                RecordFailure(scenario, sim.RealizedFaultMap, ex, parent: null);
                regressed++;
            }
        }
        return (reloaded, regressed);
    }

    /// <summary>The running coverage union (the 0090 report's source).</summary>
    internal CoverageTracker Coverage => _coverage;

    /// <summary>The exploiter's denominator-free novelty frontier (issue 0126) — kept out of the 0090 report.</summary>
    internal InteractionTuples Tuples => _tuples;

    /// <summary>Failing runs tallied by invariant ID — one persisted Plan per distinct ID.</summary>
    internal IReadOnlyDictionary<InvariantId, long> Failures => _failures;

    /// <summary>
    /// Evaluates one candidate <paramref name="scenario"/> by GENERATE and applies the two-part retention rule,
    /// updating <paramref name="parent"/>'s energy (decay on a cold child, replenish on a productive one). The
    /// caller has already <see cref="SwarmEnvelope.Confine"/>d the scenario (via <see cref="ConfigMutator"/> or
    /// <see cref="SwarmConfig.FromSeed"/>), so a generate trip is a genuine bug, never an out-of-envelope artifact.
    /// </summary>
    internal RetentionOutcome EvaluateAndRetain(Scenario scenario, CorpusEntry? parent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scenario);

        var options = scenario.ToOptions();
        var sim = new Simulator(options);
        try
        {
            // The expensive simulation runs OUTSIDE the lock (issue 0128) — only retention is serialized.
            var result = sim.Run(cancellationToken);
            Interlocked.Add(ref _virtualTicks, result.VirtualElapsed.Ticks);
            lock (_gate)
            {
                // Advanced == the union grew. Built from Scenario.FromOptions + the realized map (populated in
                // generate mode too — FaultPlan records both modes), so the corpus entry replays from itself.
                if (_coverage.Union(CoverageTracker.HitsOf(result)))
                {
                    var plan = new Plan
                    {
                        Scenario = Scenario.FromOptions(options),
                        FaultMap = sim.RealizedFaultMap,
                    };
                    _corpus.Add(new CorpusEntry(plan));
                    parent?.Replenish();
                    return RetentionOutcome.AdvancedCoverage;
                }
                parent?.Decay();
                return RetentionOutcome.Cold;
            }
        }
        catch (SimulationInvariantException ex)
        {
            lock (_gate)
            {
                return RecordFailure(Scenario.FromOptions(options), sim.RealizedFaultMap, ex, parent);
            }
        }
    }

    /// <summary>
    /// Evaluates one trace mutant — the exploiter's path (issue 0126, ADR 0025 decisions 4/5/6). The
    /// <paramref name="candidate"/> freezes <paramref name="parent"/>'s <see cref="Scenario"/> and carries an
    /// edited <b>requested</b> Fault Map (from <see cref="TraceMutator"/>); this REPLAYS it, so an illegal edit
    /// is vetoed by the Simulator's budget guards and the realized Plan stays in-envelope. Retention:
    /// <list type="number">
    /// <item>a not-yet-seen <see cref="InvariantId"/> → the existing failure path (minimize + persist);</item>
    /// <item>an inert edit whose realized map equals the parent's (vetoed / unconsulted) → discarded, not
    /// banked as a duplicate (<see cref="RetentionOutcome.VetoCollapsed"/>);</item>
    /// <item>a never-seen co-occurring-Situation interaction tuple → added to the corpus
    /// (<see cref="RetentionOutcome.NewInteraction"/>) — the trace gradient, not the endpoint-shaped union.</item>
    /// </list>
    /// Everything else is cold. The run's edge/Situation hits are still folded into the 0090 report union (kept
    /// complete across every run), but they do not drive trace retention — that is the tuple set's job.
    /// </summary>
    internal RetentionOutcome EvaluateTrace(Plan candidate, CorpusEntry parent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(parent);

        var scenario = candidate.Scenario;
        var sim = new Simulator(scenario.ToOptions(), FaultPlan.Replay(scenario.Seed, candidate.FaultMap));
        try
        {
            // The expensive replay runs OUTSIDE the lock (issue 0128) — only retention is serialized.
            var result = sim.Run(cancellationToken);
            Interlocked.Add(ref _virtualTicks, result.VirtualElapsed.Ticks);
            var realized = sim.RealizedFaultMap;
            lock (_gate)
            {
                // Veto-collapse: the edit changed nothing the run actually realized (a budget guard vetoed it, or
                // it targeted an address this replay never consults), so banking it would duplicate the parent.
                if (realized.SequenceEqual(parent.Plan.FaultMap))
                {
                    parent.Decay();
                    return RetentionOutcome.VetoCollapsed;
                }

                // Keep the 0090 report union complete, then climb the exploiter's own gradient: a never-seen pair
                // of co-occurring Situations (interleaving-sensitive, where the frozen-world edge union is blind).
                var hits = CoverageTracker.HitsOf(result);
                _coverage.Union(hits);
                if (_tuples.Union(hits.Situations))
                {
                    _corpus.Add(new CorpusEntry(new Plan { Scenario = scenario, FaultMap = realized }));
                    parent.Replenish();
                    return RetentionOutcome.NewInteraction;
                }
                parent.Decay();
                return RetentionOutcome.Cold;
            }
        }
        catch (SimulationInvariantException ex)
        {
            lock (_gate)
            {
                return RecordFailure(scenario, sim.RealizedFaultMap, ex, parent);
            }
        }
    }

    /// <summary>
    /// Routes a tripped oracle to the bug sink (shared by the explorer and exploiter): tallies the invariant,
    /// and on a not-yet-seen ID persists the RAW realized failing Plan (deduped by ID), replenishing the parent.
    /// A repeat ID is tallied only and decays the parent. Must be called while holding <see cref="_gate"/>
    /// (issue 0128); persisting raw is a cheap serialize-and-write, so unlike the old inline ddmin it does not
    /// stall the other workers. Minimization is deferred to triage/graduation (see the Save call below).
    /// </summary>
    private RetentionOutcome RecordFailure(
        Scenario scenario, IReadOnlyList<FaultEntry> realizedMap, SimulationInvariantException ex, CorpusEntry? parent)
    {
        _failures[ex.InvariantId] = _failures.GetValueOrDefault(ex.InvariantId) + 1;
        if (_seen.Add(ex.InvariantId))
        {
            var failing = new Plan
            {
                Scenario = scenario,
                FaultMap = realizedMap,
                Failure = new FailureStamp(ex.Message, ex.InvariantId),
            };
            // Persist the RAW realized failing Plan, mirroring VoprRunner. Minimization is a deliberate
            // graduation/triage step (SeedMinimizer → commit as a regression fixture), NOT hot-path work: under
            // _gate an inline ddmin stalls every worker, and being uncancellable it made --duration overshoot by
            // minutes on slow liveness failures. The raw Plan replays the bug faithfully (Scenario + realized
            // FaultMap); triage shrinks it before it graduates to a fixture.
            _store.Save(failing);
            parent?.Replenish();
            return RetentionOutcome.NewInvariant;
        }
        parent?.Decay();
        return RetentionOutcome.RepeatInvariant;
    }

    /// <summary>
    /// Runs the guided loop for <paramref name="maxIterations"/> mutations (null = forever, until
    /// <paramref name="cancellationToken"/> trips — the console's overnight mode), closing the
    /// explorer↔exploiter loop (issue 0127, ADR 0025 decision 7). While the corpus is empty it bootstraps from
    /// fresh <see cref="SwarmConfig.FromSeed"/> worlds; once non-empty it picks an entry by weighted-random energy
    /// and mutates the surface its <see cref="CorpusEntry.Stage"/> selects:
    /// <list type="bullet">
    /// <item><see cref="CorpusStage.Config"/> entries are explored — a fresh <see cref="ConfigMutator"/> world,
    /// evaluated by generate. A cold streak escalates the entry to Trace (per-entry, in <see cref="CorpusEntry.Decay"/>).</item>
    /// <item><see cref="CorpusStage.Trace"/> entries are exploited — a <see cref="TraceMutator.Flip"/> of the frozen
    /// Fault Map, evaluated by replay. A coverage-advancing trace mutant re-enters as a fresh Config-stage entry
    /// (in <see cref="EvaluateTrace"/>), closing the loop.</item>
    /// </list>
    /// Throughput vs replay (issue 0128, ADR 0025 decision 9): <paramref name="workerCount"/> threads (default
    /// <see cref="Environment.ProcessorCount"/>) share the one lock-guarded corpus, mirroring <see cref="VoprRunner"/>.
    /// Each worker salts the <see cref="GuidedTally.EntropyBase"/> by its index for an independent Seed stream, so
    /// under more than one worker the session <i>trajectory</i> is non-deterministic by design — but every banked
    /// artifact (bug Plan, corpus Plan) replays from itself via <see cref="FaultPlan.Replay"/> + invariant-ID match,
    /// which is the only guarantee ADR 0018 ever made. A <c>workerCount: 1</c> debug run is fully deterministic from
    /// the logged base (worker 0's salt is the identity, so its stream is exactly <c>new DeterministicRandom(base)</c>).
    ///
    /// <para>Run control mirrors <see cref="VoprRunner"/> so the console drives both modes identically (issue 0129):
    /// <paramref name="onTally"/> is invoked every <paramref name="tallyInterval"/> (default 5s) with a live
    /// productivity snapshot — corpus size and interaction-tuples alongside iterations — so a flatline reads as the
    /// search having gone cold. The gate is thread-safe across the workers; the snapshot reads the lock-guarded
    /// corpus / tuple / failure state under <see cref="_gate"/>.</para>
    /// </summary>
    public GuidedTally Run(
        long? maxIterations = null,
        ulong? entropyBase = null,
        int? workerCount = null,
        CancellationToken cancellationToken = default,
        Action<GuidedTallySnapshot>? onTally = null,
        TimeSpan? tallyInterval = null)
    {
        if (maxIterations is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxIterations), maxIterations, "Iterations cannot be negative.");
        }
        var workers = workerCount ?? Environment.ProcessorCount;
        if (workers < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(workerCount), workers, "Need at least one worker.");
        }

        var seedBase = entropyBase ?? RandomEntropyBase();
        var iterations = 0L;
        var traceMutations = 0L;

        // Live-tally plumbing, mirroring VoprRunner: a stopwatch for iterations/sec, a single gate serializing the
        // "is it time to emit?" check across workers, and a 5s default cadence.
        var stopwatch = Stopwatch.StartNew();
        var interval = tallyInterval ?? TimeSpan.FromSeconds(5);
        var lastTally = TimeSpan.Zero;
        var tallyGate = new object();

        // One worker's loop: claim an iteration slot atomically, then pick-and-build under the lock and evaluate
        // (the sim runs lock-free inside Evaluate*). Worker 0's salt is 0, so a single-threaded run is the exact
        // deterministic trajectory of the pre-0128 engine.
        void Worker(int index)
        {
            var rng = new DeterministicRandom(seedBase ^ ((ulong)index * 0x9E3779B97F4A7C15UL));
            while (!cancellationToken.IsCancellationRequested)
            {
                var claimed = Interlocked.Increment(ref iterations);
                if (maxIterations is { } cap && claimed > cap)
                {
                    Interlocked.Decrement(ref iterations); // don't count the over-cap claim
                    return;
                }

                Scenario? configCandidate = null;
                Plan? traceCandidate = null;
                CorpusEntry? parent = null;
                lock (_gate)
                {
                    if (_corpus.IsEmpty)
                    {
                        // Bootstrap: a fresh in-envelope world from the Seed-derived swarm config.
                        configCandidate = Scenario.FromOptions(SwarmConfig.FromSeed(NextSeed(rng)));
                    }
                    else
                    {
                        parent = _corpus.PickByEnergy(rng);
                        if (parent.Stage == CorpusStage.Config)
                        {
                            configCandidate = ConfigMutator.Mutate(parent.Plan.Scenario, NextSeed(rng), rng);
                        }
                        else
                        {
                            // Exploiter: drill the frozen world's interleaving neighborhood. Flip is the workhorse
                            // (ADR 0025 decision 6); Splice stays a tested capability but the loop's diet is flips.
                            Interlocked.Increment(ref traceMutations);
                            traceCandidate = TraceMutator.Flip(parent.Plan, rng);
                        }
                    }
                }

                // Evaluate (and apply retention) outside the pick/build lock — Evaluate* takes _gate only for the
                // retention step, so the heavy simulation overlaps across workers. The token rides into the sim so
                // a --duration deadline can cut a long iteration short instead of waiting it out at Join.
                try
                {
                    if (configCandidate is not null)
                    {
                        EvaluateAndRetain(configCandidate, parent, cancellationToken);
                    }
                    else
                    {
                        EvaluateTrace(traceCandidate!, parent!, cancellationToken);
                    }
                }
                catch (OperationCanceledException)
                {
                    return; // deadline fired mid-simulation — stop this worker promptly
                }

                if (onTally is not null)
                {
                    MaybeEmitTally(
                        onTally, tallyGate, stopwatch, ref lastTally, interval,
                        Interlocked.Read(ref iterations), Interlocked.Read(ref traceMutations));
                }
            }
        }

        var threads = new Thread[workers];
        for (var i = 0; i < workers; i++)
        {
            var index = i;
            threads[i] = new Thread(() => Worker(index)) { IsBackground = true, Name = $"guided-swarm-{index}" };
            threads[i].Start();
        }
        foreach (var t in threads)
        {
            t.Join();
        }
        stopwatch.Stop();

        return new GuidedTally(
            seedBase, Interlocked.Read(ref iterations), Interlocked.Read(ref traceMutations), _corpus.Count,
            new Dictionary<InvariantId, long>(_failures), _coverage.Report(), _tuples.Count, stopwatch.Elapsed)
        {
            TotalVirtualTime = TimeSpan.FromTicks(Interlocked.Read(ref _virtualTicks)),
        };
    }

    /// <summary>
    /// Emits a live <see cref="GuidedTallySnapshot"/> at most once per <paramref name="interval"/> (issue 0129),
    /// mirroring <see cref="VoprRunner"/>'s gate: <paramref name="gate"/> serializes the "is it time?" check across
    /// workers, and the productivity counters (corpus size, interaction-tuples, unique failures) are read under
    /// <see cref="_gate"/> since they are the lock-guarded shared state.
    /// </summary>
    private void MaybeEmitTally(
        Action<GuidedTallySnapshot> onTally,
        object gate,
        Stopwatch stopwatch,
        ref TimeSpan lastTally,
        TimeSpan interval,
        long iterations,
        long traceMutations)
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

        int corpusSize, interactionTuples, uniqueFailures;
        lock (_gate)
        {
            corpusSize = _corpus.Count;
            interactionTuples = _tuples.Count;
            uniqueFailures = _failures.Count;
        }
        onTally(new GuidedTallySnapshot(
            iterations,
            traceMutations,
            uniqueFailures,
            corpusSize,
            interactionTuples,
            elapsed.TotalSeconds > 0 ? iterations / elapsed.TotalSeconds : 0,
            elapsed));
    }

    /// <summary>A uniform 64-bit Seed composed from two 32-bit PCG draws (the harness RNG is 32-bit).</summary>
    private static ulong NextSeed(DeterministicRandom rng) => ((ulong)rng.NextUInt() << 32) | rng.NextUInt();

    /// <summary>A non-deterministic per-process entropy base — logged so the run can be replayed exactly.</summary>
    private static ulong RandomEntropyBase()
    {
        Span<byte> bytes = stackalloc byte[8];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return BitConverter.ToUInt64(bytes);
    }
}

/// <summary>The final outcome of a <see cref="CoverageGuidedSwarm.Run"/> (issue 0125): enough to assert and to print.</summary>
internal sealed record GuidedTally(
    ulong EntropyBase,
    long Iterations,
    long TraceMutations,
    int CorpusSize,
    IReadOnlyDictionary<InvariantId, long> FailuresByInvariant,
    CoverageReport Coverage,
    int InteractionTuples,
    TimeSpan Elapsed)
{
    /// <summary>Distinct invariant IDs tripped (= persisted Plans in the bug sink).</summary>
    public int UniqueFailures => FailuresByInvariant.Count;

    /// <summary>Total failing runs across all IDs.</summary>
    public long TotalFailures => FailuresByInvariant.Values.Sum();

    public double IterationsPerSecond => Elapsed.TotalSeconds > 0 ? Iterations / Elapsed.TotalSeconds : 0;

    /// <summary>
    /// The summed virtual (simulated) cluster time across every clean sim in this run — the honest
    /// "equivalent cluster-time tested" figure, fed into the cross-run coverage ledger.
    /// </summary>
    public TimeSpan TotalVirtualTime { get; init; }
}

/// <summary>
/// A live productivity snapshot emitted mid-run so the console can print a running tally (issue 0129). Beyond
/// iterations and rate it surfaces the search's two productivity signals — <see cref="CorpusSize"/> and
/// <see cref="InteractionTuples"/> — so a flatline in both reads as "the search has gone cold" (the cue to stop
/// the window or re-seed).
/// </summary>
internal sealed record GuidedTallySnapshot(
    long Iterations,
    long TraceMutations,
    int UniqueFailures,
    int CorpusSize,
    int InteractionTuples,
    double IterationsPerSecond,
    TimeSpan Elapsed);
