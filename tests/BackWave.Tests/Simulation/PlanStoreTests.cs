namespace BackWave.Tests.Simulation;

/// <summary>
/// The working corpus on disk (issue 0087, PRD 0004, ADR 0018): one JSON file per invariant ID, dedup by
/// ID, full-fidelity round-trip (the fixture format the regression suite in issue 0089 reads back).
/// </summary>
public sealed class PlanStoreTests : IDisposable
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

    /// <summary>Captures a real failing Plan by tripping a known invariant via the sabotage self-test.</summary>
    private static Plan CaptureFailingPlan(ulong seed)
    {
        var options = new SimulationOptions
        {
            Seed = seed,
            NodeCount = 3,
            JobCount = 40,
            StoreFaultProbability = 0.2, // gives the realized Fault Map some entries to round-trip
            SabotageLegalTransition = true,
        };
        var sim = new Simulator(options);
        try
        {
            sim.Run();
        }
        catch (SimulationInvariantException ex)
        {
            return new Plan
            {
                Scenario = Scenario.FromOptions(options),
                FaultMap = sim.RealizedFaultMap,
                Failure = new FailureStamp(ex.Message, ex.InvariantId),
            };
        }

        throw new InvalidOperationException("Expected the sabotage scenario to trip an oracle.");
    }

    [Fact]
    public void Save_ThenLoadAll_RoundTripsFullFidelity()
    {
        var plan = CaptureFailingPlan(4242);
        var store = new PlanStore(_corpusDir);

        Assert.True(store.Save(plan));

        var loaded = Assert.Single(store.LoadAll());

        // Full-fidelity: re-serialize equals (mirrors FaultPlanTests/SeedMinimizerTests round-trip style).
        // Equality is on the JSON, not the record — an empty Schedules survives as a List vs the source
        // array, a serialization artifact, not a fidelity loss; every scalar knob round-trips identically.
        Assert.Equal(PlanJson.Serialize(plan), PlanJson.Serialize(loaded));
        Assert.Equal(plan.FaultMap, loaded.FaultMap);
        Assert.Equal(plan.Failure, loaded.Failure);
    }

    [Fact]
    public void Save_DedupsByInvariantId_FirstWins_RepeatsNotRePersisted()
    {
        var first = CaptureFailingPlan(7);
        var second = CaptureFailingPlan(8); // different seed, SAME invariant ID
        Assert.Equal(InvariantId.LegalTransition, first.Failure?.InvariantId);
        Assert.Equal(first.Failure?.InvariantId, second.Failure?.InvariantId);

        var store = new PlanStore(_corpusDir);

        Assert.True(store.Save(first));   // first-of-ID → persisted
        Assert.False(store.Save(second)); // same ID → deduped, not re-written

        var files = Directory.GetFiles(_corpusDir, "*.json");
        Assert.Single(files);
        Assert.Equal(store.PathFor(InvariantId.LegalTransition), files[0]);

        // First wins: the persisted Plan is the FIRST one (seed 7), not the second.
        var persisted = PlanStore.Load(files[0]);
        Assert.Equal(first.Seed, persisted.Seed);
    }

    [Fact]
    public void Save_RejectsACleanPlan_BecauseTheCorpusIsFailuresOnly()
    {
        var store = new PlanStore(_corpusDir);
        var clean = new Plan
        {
            Scenario = Scenario.FromOptions(new SimulationOptions { Seed = 1 }),
            FaultMap = [],
            Failure = null,
        };

        Assert.Throws<ArgumentException>(() => store.Save(clean));
    }
}
