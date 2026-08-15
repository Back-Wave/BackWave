using BackWave.Core;
using BackWave.Storage;

namespace BackWave.Tests.Simulation;

/// <summary>
/// The Plan tracer (issue 0083, PRD 0004, ADR 0018): the FaultPlan records in generate mode and looks up
/// in replay mode through one code path, and a Plan round-trips to diffable JSON and replays to the
/// identical run. These prove the whole Plan loop — generate → serialize → replay → identical result — on
/// the single store-fault axis routed through the FaultPlan in this slice.
/// </summary>
public class FaultPlanTests
{
    [Fact]
    public void FaultPlan_RecordsInGenerate_AndLooksUpInReplay_OneCodePath()
    {
        const ulong seed = 1337;
        var generate = FaultPlan.Generate(seed);

        // Two consultations of the same (axis, id) get distinct ordinals; a different id is independent.
        var a0 = generate.Fault("store", "0:Claim", 0.5);
        var a1 = generate.Fault("store", "0:Claim", 0.5);
        var b0 = generate.Fault("store", "1:Heartbeat", 0.5);
        var map = generate.ToFaultMap();

        Assert.Equal([0, 1, 0], map.Select(e => e.Ordinal));
        Assert.Equal([a0, a1, b0], map.Select(e => e.Fault));

        // Replay returns the recorded outcome for each recorded address, in the same call order.
        var replay = FaultPlan.Replay(seed, map);
        Assert.Equal(a0, replay.Fault("store", "0:Claim", 0.5));
        Assert.Equal(a1, replay.Fault("store", "0:Claim", 0.5));
        Assert.Equal(b0, replay.Fault("store", "1:Heartbeat", 0.5));

        // A miss — an address never recorded — defaults to no-fault and makes no draw.
        Assert.False(replay.Fault("store", "9:Enqueue", 1.0));
    }

    [Fact]
    public void FaultPlan_Decide_RecordsTheGeneratedOutcome_AndReplaysItByStableKey()
    {
        const ulong seed = 99;
        var generate = FaultPlan.Generate(seed);

        // Decide records an externally-drawn outcome (no stream draw) under a stable key.
        var c = generate.Decide("crash", "0:t0", true);
        var h = generate.Decide("handler", "job:1", false);
        var map = generate.ToFaultMap();

        Assert.True(c);
        Assert.False(h);
        Assert.Equal([("crash", "0:t0", true), ("handler", "job:1", false)],
            map.Select(e => (e.Axis, e.Id, e.Fault)));

        var replay = FaultPlan.Replay(seed, map);
        Assert.True(replay.Decide("crash", "0:t0", false));   // the passed outcome is ignored; the map wins
        Assert.False(replay.Decide("handler", "job:1", true));
        Assert.False(replay.Decide("crash", "9:t9", true));   // a miss defaults to no-fault
    }

    /// <summary>
    /// The headline of issue 0084: with every fault axis active at once, a Plan captured from a run
    /// replays — through serialization — to the byte-identical run. This proves crash and heartbeat-loss
    /// (now on their own keyed streams), the handler/ack-loss/unroutable decisions (recorded off their
    /// existing streams), and the isolation/operator firings all round-trip from the Fault Map.
    /// </summary>
    [Fact]
    public void FullAxisPlan_SerializedDeserializedAndReplayed_ReproducesTheIdenticalRun()
    {
        var options = AllAxesScenario(909);

        var sim = new Simulator(options);
        var original = sim.Run();
        var plan = new Plan { Scenario = Scenario.FromOptions(options), FaultMap = sim.RealizedFaultMap };

        // Every routed axis fired at least once, so the round-trip is genuinely exercising all of them.
        var axes = plan.FaultMap.Select(e => e.Axis).ToHashSet();
        Assert.Superset(
            new HashSet<string> { "store", "crash", "heartbeat", "handler", "ackloss", "unroutable", "isolation", "operator" },
            axes);

        var rehydrated = PlanJson.Deserialize(PlanJson.Serialize(plan));
        var replayed = new Simulator(rehydrated.Scenario.ToOptions(), FaultPlan.Replay(rehydrated.Seed, rehydrated.FaultMap)).Run();

        Assert.Equal(original.Steps, replayed.Steps);
        Assert.Equal(original.Crashes, replayed.Crashes);
        Assert.Equal(original.StaleOutcomes, replayed.StaleOutcomes);
        Assert.Equal(original.Isolations, replayed.Isolations);
        Assert.Equal(original.AckLosses, replayed.AckLosses);
        Assert.Equal(original.Quarantined, replayed.Quarantined);
        Assert.Equal(original.OperatorCancels, replayed.OperatorCancels);
        Assert.Equal(original.FinalJobs, replayed.FinalJobs);
        Assert.Equal(Flatten(original), Flatten(replayed));
    }

    [Fact]
    public void Plan_RoundTripsThroughJson_WithFullFidelity()
    {
        var options = StoreFaultScenario(71);
        var sim = new Simulator(options);
        sim.Run();
        var plan = new Plan { Scenario = Scenario.FromOptions(options), FaultMap = sim.RealizedFaultMap };

        Assert.NotEmpty(plan.FaultMap); // the store-fault axis actually fired, so the map is meaningful

        var json = PlanJson.Serialize(plan);
        var roundTripped = PlanJson.Deserialize(json);

        // The Fault Map is value-typed records, so it compares element-wise and structurally.
        Assert.Equal(plan.FaultMap, roundTripped.FaultMap);
        // The Scenario carries collection members without structural equality, so prove fidelity by
        // serialization idempotence: re-serializing the round-tripped Plan reproduces the same JSON.
        Assert.Equal(json, PlanJson.Serialize(roundTripped));
        Assert.Equal(plan.Seed, roundTripped.Seed);
        Assert.Equal(plan.SchemaVersion, roundTripped.SchemaVersion);
    }

    [Fact]
    public void GeneratedPlan_SerializedDeserializedAndReplayed_ReproducesTheIdenticalRun()
    {
        var options = StoreFaultScenario(72);

        // Generate: run once, capturing the realized Fault Map into a Plan.
        var sim = new Simulator(options);
        var original = sim.Run();
        var plan = new Plan { Scenario = Scenario.FromOptions(options), FaultMap = sim.RealizedFaultMap };
        Assert.NotEmpty(plan.FaultMap);

        // Serialize → deserialize → replay from the Plan (the Fault Map drives the store axis in replay mode).
        var rehydrated = PlanJson.Deserialize(PlanJson.Serialize(plan));
        var replayed = new Simulator(rehydrated.Scenario.ToOptions(), FaultPlan.Replay(rehydrated.Seed, rehydrated.FaultMap)).Run();

        Assert.Equal(original.Steps, replayed.Steps);
        Assert.Equal(original.Crashes, replayed.Crashes);
        Assert.Equal(original.StaleOutcomes, replayed.StaleOutcomes);
        Assert.Equal(original.FinalJobs, replayed.FinalJobs);
        Assert.Equal(Flatten(original), Flatten(replayed));
    }

    private static SimulationOptions StoreFaultScenario(ulong seed) => new()
    {
        Seed = seed,
        JobCount = 100,
        StoreFaultProbability = 0.2,
        CrashProbabilityPerPoll = 0,
        HeartbeatLossProbability = 0,
    };

    /// <summary>A converging scenario that exercises every routed fault axis at once (healing isolation only,
    /// generous drain) so the full-axis round-trip is meaningful.</summary>
    private static SimulationOptions AllAxesScenario(ulong seed) => new()
    {
        Seed = seed,
        NodeCount = 3,
        JobCount = 50,
        DrainAllowance = TimeSpan.FromHours(6),
        CrashProbabilityPerPoll = 0.003,
        HeartbeatLossProbability = 0.03,
        HandlerFailureProbability = 0.1,
        StoreFaultProbability = 0.05,
        AckLossProbability = 0.05,
        UnroutableProbability = 0.05,
        IsolationCount = 5,
        OperatorActionCount = 20,
    };

    private static IReadOnlyList<(Guid, DateTimeOffset, JobState, int)> Flatten(SimulationResult r) =>
        [.. r.FinalTransitions.SelectMany(e => e.Timeline.Select(t => (e.JobId, t.Timestamp, t.State, t.Attempt)))];
}
