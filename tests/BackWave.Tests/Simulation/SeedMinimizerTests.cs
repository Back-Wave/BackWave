using BackWave.Core;

namespace BackWave.Tests.Simulation;

/// <summary>
/// The Seed Minimizer (issue 0088, PRD 0004, ADR 0018): exact fault-removal ddmin over the Fault Map with
/// the Scenario frozen, removing faults and re-replaying — accepting a candidate only when the SAME
/// invariant ID re-trips. These prove the headline contract: a known-superfluous fault is removed and the
/// same invariant still trips; the Scenario is untouched and a non-reproducing Plan is never returned; the
/// operator axis is carried through whole; un-mutated replay equals generation; and the opt-in scenario
/// shrink produces a separate labelled artifact that never overwrites the exact repro.
/// </summary>
public class SeedMinimizerTests
{
    /// <summary>
    /// A scenario that deterministically trips <see cref="InvariantId.LegalTransition"/> via the sabotage
    /// self-test (an illegal Succeeded→Leased edge spliced in) WHILE also firing the store-fault axis, so the
    /// captured Plan carries removable store faults that are irrelevant to the bug.
    /// </summary>
    private static SimulationOptions SabotageWithExtraFaults(ulong seed) => new()
    {
        Seed = seed,
        NodeCount = 3,
        JobCount = 60,
        StoreFaultProbability = 0.3,   // removable noise — the sabotage trips regardless
        CrashProbabilityPerPoll = 0,
        HeartbeatLossProbability = 0,
        SabotageLegalTransition = true,
    };

    private static (Plan Plan, InvariantId Invariant) CaptureFailingPlan(SimulationOptions options)
    {
        var sim = new Simulator(options);
        try
        {
            sim.Run();
        }
        catch (SimulationInvariantException ex)
        {
            var plan = new Plan
            {
                Scenario = Scenario.FromOptions(options),
                FaultMap = sim.RealizedFaultMap,
                Failure = new FailureStamp(ex.Message, ex.InvariantId),
            };
            return (plan, ex.InvariantId);
        }

        throw new InvalidOperationException("Expected the sabotage scenario to trip an oracle.");
    }

    [Fact]
    public void Minimize_RemovesSuperfluousFaults_AndStillTripsTheSameInvariant()
    {
        var (plan, invariant) = CaptureFailingPlan(SabotageWithExtraFaults(4242));
        Assert.Equal(InvariantId.LegalTransition, invariant);

        // The captured map carries store faults that are irrelevant to the legal-transition sabotage.
        Assert.Contains(plan.FaultMap, e => e.Axis == "store" && e.Fault);

        var minimized = SeedMinimizer.Minimize(plan, invariant);

        // Strictly smaller fault map — the superfluous store faults were removed.
        Assert.True(minimized.FaultMap.Count < plan.FaultMap.Count,
            $"expected a smaller map: {minimized.FaultMap.Count} < {plan.FaultMap.Count}");

        // And it still trips the same invariant on replay.
        AssertReproduces(minimized, invariant);
    }

    [Fact]
    public void Minimize_LeavesScenarioUnchanged_AndNeverReturnsNonReproducingPlan()
    {
        var (plan, invariant) = CaptureFailingPlan(SabotageWithExtraFaults(7));

        var minimized = SeedMinimizer.Minimize(plan, invariant);

        // The exact pass freezes the Scenario.
        Assert.Equal(plan.Scenario, minimized.Scenario);
        // The failure stamp is preserved.
        Assert.Equal(plan.Failure, minimized.Failure);
        // Floor: the result reproduces (worst case, the input unchanged) — never a non-reproducing Plan.
        AssertReproduces(minimized, invariant);
    }

    [Fact]
    public void Minimize_OnAPlanWithNoRemovableFaults_ReturnsAnEquivalentlyReproducingPlan()
    {
        // No fault axes active, so the only entries are the operator decisions (pinned) — nothing removable.
        var options = new SimulationOptions
        {
            Seed = 51,
            NodeCount = 3,
            JobCount = 40,
            StoreFaultProbability = 0,
            CrashProbabilityPerPoll = 0,
            HeartbeatLossProbability = 0,
            SabotageLegalTransition = true,
        };
        var (plan, invariant) = CaptureFailingPlan(options);

        var minimized = SeedMinimizer.Minimize(plan, invariant);

        // Nothing removable → map preserved, and it still reproduces.
        Assert.Equal(plan.Scenario, minimized.Scenario);
        Assert.Equal(plan.FaultMap.Count, minimized.FaultMap.Count);
        AssertReproduces(minimized, invariant);
    }

    [Fact]
    public void Minimize_CarriesEveryOperatorEntryThrough()
    {
        // OperatorActionCount > 0 plus sabotage → operator entries present, must all survive minimization.
        var options = new SimulationOptions
        {
            Seed = 314,
            NodeCount = 3,
            JobCount = 60,
            StoreFaultProbability = 0.3,   // removable, gives ddmin something to chew on
            CrashProbabilityPerPoll = 0,
            HeartbeatLossProbability = 0,
            OperatorActionCount = 25,
            SabotageLegalTransition = true,
        };
        var (plan, invariant) = CaptureFailingPlan(options);

        var operatorBefore = plan.FaultMap.Where(e => e.Axis == "operator").ToList();
        Assert.NotEmpty(operatorBefore);

        var minimized = SeedMinimizer.Minimize(plan, invariant);

        var operatorAfter = minimized.FaultMap.Where(e => e.Axis == "operator").ToList();
        // Every operator entry is carried through untouched (never proposed for removal).
        Assert.Equal(operatorBefore, operatorAfter);
        AssertReproduces(minimized, invariant);
    }

    [Fact]
    public void UnmutatedReplay_VetoesNothing_AndEqualsGeneration()
    {
        // A converging all-axes scenario (no sabotage): replaying the captured realized map reproduces the
        // identical run — mirrors FaultPlanTests' round-trip, and proves replay records the realized map too.
        var options = new SimulationOptions
        {
            Seed = 909,
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

        var sim = new Simulator(options);
        var original = sim.Run();
        var generatedMap = sim.RealizedFaultMap;
        Assert.NotEmpty(generatedMap);

        // Replay the FULL captured map — vetoes nothing, reproduces the identical run.
        var replaySim = new Simulator(options, FaultPlan.Replay(options.Seed, generatedMap));
        var replayed = replaySim.Run();

        Assert.Equal(original.Steps, replayed.Steps);
        Assert.Equal(original.Crashes, replayed.Crashes);
        Assert.Equal(original.StaleOutcomes, replayed.StaleOutcomes);
        Assert.Equal(original.Isolations, replayed.Isolations);
        Assert.Equal(original.FinalJobs, replayed.FinalJobs);

        // The realized map recorded ON REPLAY equals what generation produced (the 0088 FaultPlan fix).
        Assert.Equal(generatedMap, replaySim.RealizedFaultMap);
    }

    [Fact]
    public void MinimizeScenario_ProducesASeparateLabelledArtifact_AndDoesNotOverwriteTheExactRepro()
    {
        var (plan, invariant) = CaptureFailingPlan(SabotageWithExtraFaults(8080));
        var exact = SeedMinimizer.Minimize(plan, invariant);

        var artifact = SeedMinimizer.MinimizeScenario(exact, invariant);

        // The sabotage trips per-job, so a smaller world still reproduces → a sibling artifact is produced.
        Assert.NotNull(artifact);
        Assert.True(artifact!.IsCoarseSibling);

        // It is a SEPARATE artifact: the exact minimized Plan is untouched, and the sibling's world is smaller.
        Assert.True(artifact.SiblingPlan.Scenario.JobCount <= exact.Scenario.JobCount);
        Assert.Equal(invariant, artifact.SiblingPlan.Failure?.InvariantId);
        // The exact repro keeps its full Scenario — never overwritten by the coarse pass.
        Assert.Equal(plan.Scenario.JobCount, exact.Scenario.JobCount);
        AssertReproduces(exact, invariant);
        AssertReproduces(artifact.SiblingPlan, invariant);
    }

    private static void AssertReproduces(Plan plan, InvariantId expected)
    {
        var sim = new Simulator(plan.Scenario.ToOptions(), FaultPlan.Replay(plan.Seed, plan.FaultMap));
        var ex = Assert.Throws<SimulationInvariantException>(() => sim.Run());
        Assert.Equal(expected, ex.InvariantId);
    }
}
