namespace BackWave.Tests.Simulation;

/// <summary>
/// The coverage-guided explorer (issue 0125, ADR 0025): an in-memory <see cref="Corpus"/> of coverage-advancing
/// Plans, the two-part retention rule (new-invariant → persist raw; advances-coverage → corpus), per-entry
/// decaying <see cref="CorpusEntry.Energy"/>, and a weighted-random scheduler. These prove each piece in isolation
/// and the loop end-to-end: every evaluation stays in-envelope, the corpus grows, and its entries replay.
/// </summary>
public sealed class CoverageGuidedSwarmTests : IDisposable
{
    private readonly string _corpusDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public void Dispose()
    {
        if (Directory.Exists(_corpusDir))
        {
            Directory.Delete(_corpusDir, recursive: true);
        }
    }

    private PlanStore NewStore() => new(Path.Combine(_corpusDir, Path.GetRandomFileName()));

    private static Scenario CleanScenario(ulong seed) => Scenario.FromOptions(SwarmConfig.FromSeed(seed));

    /// <summary>A Scenario that deterministically trips <see cref="InvariantId.LegalTransition"/> via the sabotage self-test.</summary>
    private static Scenario SabotageScenario(ulong seed) => CleanScenario(seed) with { SabotageLegalTransition = true };

    private static CorpusEntry CleanEntry(ulong seed) =>
        new(new Plan { Scenario = CleanScenario(seed), FaultMap = [] });

    // ── Corpus + energy ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Energy_DecaysOnCold_ReplenishesOnProductive_AndIsFloored()
    {
        var entry = CleanEntry(1);
        Assert.Equal(CorpusEntry.InitialEnergy, entry.Energy);

        entry.Decay();
        Assert.True(entry.Energy < CorpusEntry.InitialEnergy);

        var afterOneDecay = entry.Energy;
        entry.Replenish();
        Assert.True(entry.Energy > afterOneDecay);
        Assert.True(entry.Energy <= CorpusEntry.InitialEnergy); // capped at the hot start

        // Many cold mutations never starve the entry to zero — it keeps a positive floor.
        for (var i = 0; i < 200; i++)
        {
            entry.Decay();
        }
        Assert.Equal(CorpusEntry.EnergyFloor, entry.Energy);
    }

    [Fact]
    public void Entry_EscalatesToTrace_WhenColdStreakCrossesThreshold_AndProductiveChildResetsIt()
    {
        var entry = CleanEntry(1);
        Assert.Equal(CorpusStage.Config, entry.Stage); // every entry starts in the explorer

        // One short of the threshold is still Config — escalation needs the full unbroken streak.
        for (var i = 0; i < CorpusEntry.EscalationThreshold - 1; i++)
        {
            entry.Decay();
        }
        Assert.Equal(CorpusStage.Config, entry.Stage);
        Assert.Equal(CorpusEntry.EscalationThreshold - 1, entry.ColdStreak);

        // A productive child resets the cold-counter — escalation is for *consecutive* cold mutants.
        entry.Replenish();
        Assert.Equal(0, entry.ColdStreak);
        Assert.Equal(CorpusStage.Config, entry.Stage);

        // A full unbroken cold streak escalates the entry to the exploiter.
        for (var i = 0; i < CorpusEntry.EscalationThreshold; i++)
        {
            entry.Decay();
        }
        Assert.Equal(CorpusStage.Trace, entry.Stage);

        // Trace is terminal — neither a later productive child nor more cold mutants flip it back.
        entry.Replenish();
        entry.Decay();
        Assert.Equal(CorpusStage.Trace, entry.Stage);
    }

    [Fact]
    public void PickByEnergy_IsWeighted_HotEntryChosenFarMoreThanColdOne()
    {
        var corpus = new Corpus();
        var hot = CleanEntry(1);   // energy 1.0
        var cold = CleanEntry(2);
        for (var i = 0; i < 30; i++)
        {
            cold.Decay(); // floored low
        }
        corpus.Add(hot);
        corpus.Add(cold);

        var rng = new DeterministicRandom(99);
        var hotPicks = 0;
        for (var i = 0; i < 1000; i++)
        {
            if (ReferenceEquals(corpus.PickByEnergy(rng), hot))
            {
                hotPicks++;
            }
        }

        // hot ~ 1.0 vs cold ~ 0.01: hot should dominate overwhelmingly.
        Assert.True(hotPicks > 950, $"hot picked {hotPicks}/1000 — weighting is off");
    }

    // ── Two-part retention ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void NewInvariantMutant_IsPersistedRaw_AndReplenishesParent()
    {
        var store = NewStore();
        var swarm = new CoverageGuidedSwarm(store);
        var parent = CleanEntry(7);
        parent.Decay();
        parent.Decay();
        var energyBefore = parent.Energy;

        var outcome = swarm.EvaluateAndRetain(SabotageScenario(4242), parent);

        Assert.Equal(RetentionOutcome.NewInvariant, outcome);
        Assert.True(store.Has(InvariantId.LegalTransition));
        var persisted = Assert.Single(store.LoadAll());
        Assert.Equal(InvariantId.LegalTransition, persisted.Failure!.InvariantId);
        Assert.Empty(swarm.Corpus.Entries); // a bug is the sink's, never the corpus
        Assert.True(parent.Energy > energyBefore); // productive → replenished
    }

    [Fact]
    public void RepeatInvariantMutant_IsTalliedButNotRePersisted_AndDecaysParent()
    {
        var store = NewStore();
        var swarm = new CoverageGuidedSwarm(store);

        Assert.Equal(RetentionOutcome.NewInvariant, swarm.EvaluateAndRetain(SabotageScenario(4242), parent: null));

        var parent = CleanEntry(7);
        var energyBefore = parent.Energy;
        var outcome = swarm.EvaluateAndRetain(SabotageScenario(9001), parent); // same invariant, different seed

        Assert.Equal(RetentionOutcome.RepeatInvariant, outcome);
        Assert.Single(store.LoadAll()); // still exactly one — deduped by ID
        Assert.Equal(2, swarm.Failures[InvariantId.LegalTransition]);
        Assert.True(parent.Energy < energyBefore); // unproductive repeat → decayed
    }

    [Fact]
    public void CoverageAdvancingMutant_IsAddedToCorpus_NonAdvancingIsDiscarded_StoreRejectsCleanPlans()
    {
        var store = NewStore();
        var swarm = new CoverageGuidedSwarm(store);

        // From empty coverage, the first clean run advances and is retained to the corpus.
        var first = swarm.EvaluateAndRetain(CleanScenario(3), parent: null);
        Assert.Equal(RetentionOutcome.AdvancedCoverage, first);
        Assert.Single(swarm.Corpus.Entries);

        // The SAME scenario re-run hits the same edges/Situations — advances nothing → cold, discarded.
        var parent = swarm.Corpus.Entries[0];
        var energyBefore = parent.Energy;
        var second = swarm.EvaluateAndRetain(CleanScenario(3), parent);
        Assert.Equal(RetentionOutcome.Cold, second);
        Assert.Single(swarm.Corpus.Entries); // unchanged
        Assert.True(parent.Energy < energyBefore); // cold → decayed

        // The bug sink never holds a clean Plan — only the corpus does.
        Assert.Empty(store.LoadAll());
    }

    // ── End-to-end ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Run_GrowsACorpus_StaysInEnvelope_AndEveryEntryReplays()
    {
        var swarm = new CoverageGuidedSwarm(NewStore());
        var tally = swarm.Run(maxIterations: 40, entropyBase: 0xA11CE, workerCount: 1);

        Assert.True(tally.CorpusSize > 0, "the explorer grew no corpus");
        Assert.Equal(0, tally.UniqueFailures);          // in-envelope: a generate run trips nothing
        Assert.True(tally.Coverage.EdgesHit > 0);

        // Every corpus entry is a clean Plan that replays from itself (veto-free, un-mutated replay == generation).
        foreach (var entry in swarm.Corpus.Entries)
        {
            Assert.Null(entry.Plan.Failure);
            var replay = new Simulator(entry.Plan.Scenario.ToOptions(), FaultPlan.Replay(entry.Plan.Seed, entry.Plan.FaultMap));
            var result = replay.Run(); // does not throw
            Assert.Equal(entry.Plan.Scenario.JobCount, result.FinalJobs.Count);
        }
    }

    [Fact]
    public void Run_IsDeterministic_SameEntropyBaseYieldsSameTrajectory()
    {
        var a = new CoverageGuidedSwarm(NewStore()).Run(maxIterations: 30, entropyBase: 777, workerCount: 1);
        var b = new CoverageGuidedSwarm(NewStore()).Run(maxIterations: 30, entropyBase: 777, workerCount: 1);

        Assert.Equal(a.CorpusSize, b.CorpusSize);
        Assert.Equal(a.Coverage.EdgesHit, b.Coverage.EdgesHit);
        Assert.Equal(a.Coverage.SituationsHit, b.Coverage.SituationsHit);
        Assert.Equal(a.UniqueFailures, b.UniqueFailures);
    }

    // ── Exploiter: trace-level mutation (issue 0126) ─────────────────────────────────────────────────

    /// <summary>
    /// A raw 3-node healing-isolation Scenario whose 8-episode schedule OVERRUNS the N−1 budget on seed 8:
    /// 8 isolation begins are requested but only 6 are admitted (2 vetoed), reproduced identically on replay.
    /// No other fault axis is active, so isolation begins are the only interesting decisions in its Fault Map.
    /// </summary>
    private static Scenario IsolationOverrunScenario() => new()
    {
        Seed = 8,
        NodeCount = 3,
        JobCount = 40,
        IsolationCount = 8,
        WorkloadDuration = TimeSpan.FromHours(1),
        DrainAllowance = TimeSpan.FromHours(6),
        CrashProbabilityPerPoll = 0,
        HeartbeatLossProbability = 0,
        HandlerFailureProbability = 0,
        StoreFaultProbability = 0,
    };

    [Fact]
    public void Keystone_AddedOverBudgetIsolation_IsVetoedOnReplay_AndRealizedPlanIsInEnvelope()
    {
        var scenario = IsolationOverrunScenario();

        // The full schedule with every isolation requested ON — the over-budget request the exploiter "adds".
        var gen = new Simulator(scenario.ToOptions());
        gen.Run();
        var fullMap = gen.RealizedFaultMap;
        var requestedBegins = fullMap.Count(e => e.Axis == "isolation" && e.Fault);
        Assert.True(requestedBegins >= 3, "scenario must request enough isolations to overrun the N−1 budget");

        // Base: the same world with every isolation calmed OFF — trivially in budget, no episode begins.
        var calmMap = fullMap.Select(e => e.Axis == "isolation" ? e with { Fault = false } : e).ToList();
        var baseReplay = new Simulator(scenario.ToOptions(), FaultPlan.Replay(scenario.Seed, calmMap));
        Assert.Equal(0, baseReplay.Run().Isolations);

        // Flip the isolation faults back ON (the deliberately-illegal ADD) and REPLAY: the IsolationScheduler's
        // N−1 budget vetoes the over-budget begins, so the run stays in-envelope (does NOT throw) and fewer
        // episodes begin than were requested — the keystone veto-on-replay, triggered in anger for the first time.
        var replay = new Simulator(scenario.ToOptions(), FaultPlan.Replay(scenario.Seed, fullMap));
        var result = replay.Run(); // no SimulationInvariantException — the veto kept it legal
        Assert.True(result.Isolations < requestedBegins,
            $"expected the budget to veto some begins, but all {requestedBegins} were admitted");

        // The realized Plan is in-envelope: replaying IT is a fixed point (same admitted-isolation count).
        var realized = replay.RealizedFaultMap;
        var again = new Simulator(scenario.ToOptions(), FaultPlan.Replay(scenario.Seed, realized));
        Assert.Equal(result.Isolations, again.Run().Isolations);

        // And the swarm's trace path treats it as in-envelope — routed to coverage, never the bug sink.
        var swarm = new CoverageGuidedSwarm(NewStore());
        var parent = new CorpusEntry(new Plan { Scenario = scenario, FaultMap = calmMap });
        var outcome = swarm.EvaluateTrace(new Plan { Scenario = scenario, FaultMap = fullMap }, parent);
        Assert.True(outcome is RetentionOutcome.NewInteraction or RetentionOutcome.Cold,
            $"an in-envelope vetoed mutant must not be a failure, was {outcome}");
        Assert.Empty(swarm.Failures);
    }

    [Fact]
    public void VetoCollapsedMutant_RealizedMapEqualsParent_IsDiscardedNotBanked()
    {
        var swarm = new CoverageGuidedSwarm(NewStore());

        // A generated corpus entry — its realized map is a replay fixed point.
        Assert.Equal(RetentionOutcome.AdvancedCoverage, swarm.EvaluateAndRetain(CleanScenario(13), parent: null));
        var parent = swarm.Corpus.Entries[0];
        var corpusBefore = swarm.Corpus.Count;
        var energyBefore = parent.Energy;

        // Add a fault at an address this world never consults (a fabricated, never-scheduled isolation episode).
        // Replay ignores it, so the realized map collapses back to the parent's — banking it would duplicate.
        var deadAdd = new FaultEntry("isolation", "2:9999-01-01T00:00:00.0000000+00:00", 0, true);
        var candidate = parent.Plan with { FaultMap = parent.Plan.FaultMap.Append(deadAdd).ToList() };

        var outcome = swarm.EvaluateTrace(candidate, parent);

        Assert.Equal(RetentionOutcome.VetoCollapsed, outcome);
        Assert.Equal(corpusBefore, swarm.Corpus.Count); // not banked
        Assert.True(parent.Energy < energyBefore);       // an inert child decays the parent
    }

    [Fact]
    public void TraceMutant_WithNeverSeenInteractionTuple_IsRetainedToCorpus()
    {
        var swarm = new CoverageGuidedSwarm(NewStore());
        // A fault-rich world that co-occurs several Situations (its realized map is a replay fixed point).
        var scenario = CleanScenario(1);
        var gen = new Simulator(scenario.ToOptions());
        var genResult = gen.Run();
        Assert.True(CoverageTracker.HitsOf(genResult).Situations.Count >= 2,
            "the trace gradient needs a world that co-occurs at least two Situations");
        var parent = new CorpusEntry(new Plan { Scenario = scenario, FaultMap = gen.RealizedFaultMap });

        // Flip ONE recorded decision OFF — a real edit (realized differs from the parent), still clean and
        // still co-occurring ≥2 Situations. On a fresh swarm (empty tuple set) that co-occurrence is novel.
        var map = gen.RealizedFaultMap.ToList();
        var idx = map.FindIndex(e => e.Fault && e.Axis != "operator");
        map[idx] = map[idx] with { Fault = false };
        var candidate = parent.Plan with { FaultMap = map, Failure = null };

        var corpusBefore = swarm.Corpus.Count;
        var outcome = swarm.EvaluateTrace(candidate, parent);

        Assert.Equal(RetentionOutcome.NewInteraction, outcome);
        Assert.Equal(corpusBefore + 1, swarm.Corpus.Count);
        Assert.True(swarm.Tuples.Count > 0);

        // The productive trace mutant re-enters as a fresh Config-stage entry, closing the explorer↔exploiter
        // loop (issue 0127): the exploiter's prize is fed back to the explorer, not banked as another exploiter.
        Assert.Equal(CorpusStage.Config, swarm.Corpus.Entries[^1].Stage);
    }

    // ── Per-entry Config→Trace escalation: the self-sustaining loop (issue 0127) ─────────────────────

    [Fact]
    public void Run_SelfSustains_EntriesEscalateToTrace_AndTheLoopFeedsItself()
    {
        var swarm = new CoverageGuidedSwarm(NewStore());
        // Enough iterations that config-space saturates and cold entries graduate to the exploiter.
        // workerCount: 1 pins the deterministic-trajectory assertions below (multi-threaded is exercised separately).
        var tally = swarm.Run(maxIterations: 300, entropyBase: 0xC0FFEE, workerCount: 1);

        Assert.True(tally.CorpusSize > 0, "the loop grew no corpus");
        Assert.True(tally.TraceMutations > 0,
            "no entry ever escalated to the exploiter — the loop did not self-sustain");

        // Per-entry escalation: at least one entry reached the Trace stage while others can still be in Config.
        Assert.Contains(swarm.Corpus.Entries, e => e.Stage == CorpusStage.Trace);

        // The corpus is the clean-Plan side of the loop (the bug sink holds any failure the exploiter trips —
        // routing a not-yet-seen invariant there is the two-part retention rule, not a contradiction). Every
        // corpus entry — config-born and trace-born alike — is a clean Plan that replays from itself.
        foreach (var entry in swarm.Corpus.Entries)
        {
            Assert.Null(entry.Plan.Failure);
            var replay = new Simulator(
                entry.Plan.Scenario.ToOptions(), FaultPlan.Replay(entry.Plan.Seed, entry.Plan.FaultMap));
            replay.Run(); // does not throw — the corpus stays in-envelope
        }
    }

    // ── Multi-threaded shared corpus (issue 0128) ────────────────────────────────────────────────────

    [Fact]
    public void Run_AcrossManyWorkers_SharesOneCorpus_AndEveryArtifactReplays()
    {
        var store = NewStore();
        var swarm = new CoverageGuidedSwarm(store);
        // More workers than cores maximizes contention on the shared corpus / energy / tuple-novelty set.
        var workers = Math.Max(4, Environment.ProcessorCount);
        var tally = swarm.Run(maxIterations: 200, entropyBase: 0xBADF00D, workerCount: workers);

        // The atomic claim is exact: every slot ran once — no lost or double-counted iterations under contention.
        Assert.Equal(200, tally.Iterations);
        Assert.True(tally.CorpusSize > 0, "the shared corpus stayed empty");

        // Artifacts replay from themselves regardless of the non-deterministic session trajectory (ADR 0018).
        // Corpus Plans are clean and replay without tripping…
        foreach (var entry in swarm.Corpus.Entries)
        {
            Assert.Null(entry.Plan.Failure);
            var replay = new Simulator(
                entry.Plan.Scenario.ToOptions(), FaultPlan.Replay(entry.Plan.Seed, entry.Plan.FaultMap));
            replay.Run(); // does not throw
        }
        // …and any bug Plan the exploiter banked replays to the very invariant ID it was banked under.
        foreach (var plan in store.LoadAll())
        {
            var ex = Assert.Throws<SimulationInvariantException>(() =>
                new Simulator(plan.Scenario.ToOptions(), FaultPlan.Replay(plan.Scenario.Seed, plan.FaultMap)).Run());
            Assert.Equal(plan.Failure!.InvariantId, ex.InvariantId);
        }
    }

    // ── Console run-control parity: forever-mode + onTally (issue 0129) ──────────────────────────────

    [Fact]
    public void Run_InvokesOnTally_WithTheProductivityPulse()
    {
        var swarm = new CoverageGuidedSwarm(NewStore());
        var snapshots = new List<GuidedTallySnapshot>();
        // tallyInterval Zero emits on every iteration; workerCount 1 keeps the snapshot list race-free and ordered.
        var tally = swarm.Run(
            maxIterations: 80, entropyBase: 0xCAFE, workerCount: 1,
            onTally: s => snapshots.Add(s),
            tallyInterval: TimeSpan.Zero);

        Assert.NotEmpty(snapshots);
        // The pulse is present and the run is productive — the corpus grows during the window, not just at the end.
        Assert.Contains(snapshots, s => s.CorpusSize > 0);
        Assert.All(snapshots, s => Assert.True(s.Iterations > 0));
        // Iterations are monotone non-decreasing and never exceed the final tally.
        for (var i = 1; i < snapshots.Count; i++)
        {
            Assert.True(snapshots[i].Iterations >= snapshots[i - 1].Iterations);
        }
        Assert.True(snapshots[^1].Iterations <= tally.Iterations);
        Assert.True(snapshots[^1].CorpusSize <= tally.CorpusSize);
    }

    [Fact]
    public void Run_ForeverMode_RunsUntilCancelled_AndStillReturnsAReplayableTally()
    {
        var swarm = new CoverageGuidedSwarm(NewStore());
        using var cts = new CancellationTokenSource();

        // No iteration cap (null = forever) — the run is bounded only by the token. Cancel from inside onTally
        // after a few emissions, exactly the console's Ctrl-C / --duration drain path.
        var emitted = 0;
        var tally = swarm.Run(
            maxIterations: null, entropyBase: 0xF0E, workerCount: 1, cancellationToken: cts.Token,
            onTally: _ => { if (Interlocked.Increment(ref emitted) >= 3) cts.Cancel(); },
            tallyInterval: TimeSpan.Zero);

        Assert.True(cts.IsCancellationRequested);
        Assert.True(tally.Iterations > 0, "forever mode ran no iterations before cancellation");
        Assert.Equal(0xF0EUL, tally.EntropyBase); // the search-RNG base is logged for replay
        Assert.True(tally.Elapsed > TimeSpan.Zero);
    }

    [Fact]
    public void Run_SingleThreaded_IsReproducibleFromTheLoggedEntropyBase()
    {
        // A first debug run with NO fixed base — the engine picks one and logs it on the tally.
        var first = new CoverageGuidedSwarm(NewStore()).Run(maxIterations: 30, workerCount: 1);

        // Replaying that logged base single-threaded reproduces the whole trajectory exactly.
        var replay = new CoverageGuidedSwarm(NewStore())
            .Run(maxIterations: 30, entropyBase: first.EntropyBase, workerCount: 1);

        Assert.Equal(first.CorpusSize, replay.CorpusSize);
        Assert.Equal(first.TraceMutations, replay.TraceMutations);
        Assert.Equal(first.InteractionTuples, replay.InteractionTuples);
        Assert.Equal(first.Coverage.EdgesHit, replay.Coverage.EdgesHit);
        Assert.Equal(first.Coverage.SituationsHit, replay.Coverage.SituationsHit);
        Assert.Equal(first.UniqueFailures, replay.UniqueFailures);
    }

    // ── Cross-session corpus persistence (GuidedCorpusStore + Seed) ──────────────────────────────────

    private GuidedCorpusStore NewGuidedCorpus() => new(Path.Combine(_corpusDir, Path.GetRandomFileName()));

    [Fact]
    public void GuidedCorpusStore_RoundTripsPlans_AndDedupesIdenticalContent()
    {
        var store = NewGuidedCorpus();
        var a = new Plan { Scenario = CleanScenario(1), FaultMap = [] };
        var b = new Plan { Scenario = CleanScenario(2), FaultMap = [] };

        store.SaveAll([a, b]);
        store.SaveAll([a, b]); // idempotent: content-hash filenames mean re-saving writes no duplicates

        var loaded = store.LoadAll();
        Assert.Equal(2, loaded.Count);
        Assert.Equal([1UL, 2UL], loaded.Select(p => p.Seed).OrderBy(s => s));

        // Two byte-identical Plans collapse to one file (same content hash).
        var dupStore = NewGuidedCorpus();
        dupStore.SaveAll([a, a]);
        Assert.Single(dupStore.LoadAll());
    }

    [Fact]
    public void Seed_ReconstitutesCorpusAndCoverage_SoTheNextCycleStandsOnItsShoulders()
    {
        // Cycle 1: grow a corpus from a fixed base and persist its Plans to disk.
        var first = new CoverageGuidedSwarm(NewStore());
        first.Run(maxIterations: 60, entropyBase: 0xABCDEF, workerCount: 1);
        var banked = first.CorpusPlans;
        Assert.NotEmpty(banked);

        var store = NewGuidedCorpus();
        store.SaveAll(banked);

        // Cycle 2: a fresh process reseeds from disk — same corpus, coverage union rebuilt by replay (not empty),
        // so the very first mutation climbs from the prior cycle's frontier instead of bootstrapping cold.
        var second = new CoverageGuidedSwarm(NewStore());
        var (reloaded, regressed) = second.Seed(store.LoadAll());

        Assert.Equal(banked.Count, reloaded);
        Assert.Equal(0, regressed);
        Assert.Equal(banked.Count, second.Corpus.Entries.Count);
        Assert.True(second.Coverage.Report().EdgesHit > 0, "reseed rebuilt no coverage — the retention gate would be empty");
    }

    [Fact]
    public void Seed_PlanThatNowTripsAnOracle_IsRoutedToTheBugSink_NotTheCorpus()
    {
        // A persisted "clean" Plan that regresses (its world now trips an oracle) must not re-enter the corpus —
        // reload catches it as a finding instead.
        var swarm = new CoverageGuidedSwarm(NewStore());
        var regressedPlan = new Plan { Scenario = SabotageScenario(7), FaultMap = [] };

        var (reloaded, regressed) = swarm.Seed([regressedPlan]);

        Assert.Equal(0, reloaded);
        Assert.Equal(1, regressed);
        Assert.Empty(swarm.Corpus.Entries);
        Assert.Equal(1, swarm.Failures[InvariantId.LegalTransition]);
    }
}
