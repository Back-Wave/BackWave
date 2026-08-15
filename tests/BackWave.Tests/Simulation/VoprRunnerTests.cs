namespace BackWave.Tests.Simulation;

/// <summary>
/// The VOPR Runner (issue 0087, PRD 0004, ADR 0018): the continuous discovery engine. These prove the
/// headline contract — a bounded run with an injected sabotage persists exactly one Plan per invariant ID,
/// same-ID repeats are deduped (tallied, not re-written), the runner CONTINUES past a failure, and the
/// real <see cref="SwarmConfig.FromSeed"/> regime persists nothing because it stays in-envelope.
/// </summary>
public sealed class VoprRunnerTests : IDisposable
{
    private readonly string _corpusDir =
        Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public void Dispose()
    {
        if (Directory.Exists(_corpusDir))
        {
            Directory.Delete(_corpusDir, recursive: true);
        }
    }

    [Fact]
    public void BoundedRun_WithInjectedSabotage_PersistsOnePlanPerInvariant_DedupsRepeats_AndContinues()
    {
        // A config factory that always returns a sabotage config tripping a KNOWN invariant — the real swarm
        // never needs Sabotage; the test injects it through the factory the runner is parameterized on.
        var store = new PlanStore(_corpusDir);
        var runner = new VoprRunner(
            store,
            configFactory: seed => new SimulationOptions
            {
                Seed = seed,
                NodeCount = 3,
                JobCount = 30,
                SabotageLegalTransition = true,
            },
            workerCount: 4);

        const int n = 20;
        var tally = runner.Run(maxRuns: n, entropyBase: 0xABCD_1234_5678_9F01UL);

        // Continued past every failure: completed all N runs.
        Assert.Equal(n, tally.TotalRuns);

        // Every run tripped the same known invariant — so one unique failure, many hits.
        Assert.Single(tally.FailuresByInvariant);
        Assert.True(tally.FailuresByInvariant.ContainsKey(InvariantId.LegalTransition));
        Assert.True(tally.FailuresByInvariant[InvariantId.LegalTransition] > 1,
            "expected the sabotage to trip many times (so dedup is exercised)");
        Assert.Equal(n, tally.TotalFailures);

        // Dedup-on-disk: exactly ONE persisted file, named by the invariant ID.
        var files = Directory.GetFiles(_corpusDir, "*.json");
        Assert.Single(files);
        Assert.Equal(store.PathFor(InvariantId.LegalTransition), files[0]);
        Assert.True(store.Has(InvariantId.LegalTransition));

        // The persisted Plan reproduces the failure it claims.
        var plan = PlanStore.Load(files[0]);
        Assert.Equal(InvariantId.LegalTransition, plan.Failure?.InvariantId);
    }

    [Fact]
    public void BoundedRun_WithRealSwarmConfig_PersistsNothing_BecauseEveryRunStaysInEnvelope()
    {
        var store = new PlanStore(_corpusDir);
        var runner = new VoprRunner(store, configFactory: SwarmConfig.FromSeed, workerCount: 4);

        // A small bounded run with the real (envelope-confined, never-sabotage) regime — fast and clean.
        var tally = runner.Run(maxRuns: 16, entropyBase: 0x0000_0000_0000_0042UL);

        Assert.Equal(16, tally.TotalRuns);
        Assert.Empty(tally.FailuresByInvariant);
        Assert.Empty(Directory.GetFiles(_corpusDir, "*.json"));
    }

    [Fact]
    public void Run_LogsTheEntropyBase_ForReproducibility()
    {
        var store = new PlanStore(_corpusDir);
        var runner = new VoprRunner(store, configFactory: SwarmConfig.FromSeed, workerCount: 2);

        const ulong fixedBase = 0xDEAD_BEEF_CAFE_F00DUL;
        var tally = runner.Run(maxRuns: 4, entropyBase: fixedBase);

        // The base is surfaced on the tally so the run replays from it.
        Assert.Equal(fixedBase, tally.EntropyBase);
    }
}
