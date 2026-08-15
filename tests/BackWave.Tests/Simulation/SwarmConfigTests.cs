using System.Reflection;

namespace BackWave.Tests.Simulation;

/// <summary>
/// The Swarm generator (issue 0086, ADR 0018): <see cref="SwarmConfig.FromSeed"/> derives a complete,
/// envelope-confined <see cref="SimulationOptions"/> as a pure function of the Seed. These prove it is
/// deterministic, stays inside the supported envelope (converges, no precondition violated), never sets a
/// <c>Sabotage*</c> flag, and that over many Seeds calm / single-fault / full-chaos runs all occur.
/// </summary>
public class SwarmConfigTests
{
    /// <summary>The eight fault axes the swarm flips on/off, read off the generated config.</summary>
    private static int ActiveAxisCount(SimulationOptions o)
    {
        var count = 0;
        if (o.CrashProbabilityPerPoll > 0) count++;
        if (o.HeartbeatLossProbability > 0) count++;
        if (o.HandlerFailureProbability > 0) count++;
        if (o.StoreFaultProbability > 0) count++;
        if (o.AckLossProbability > 0) count++;
        if (o.UnroutableProbability > 0) count++;
        if (o.IsolationCount > 0) count++;
        if (o.OperatorActionCount > 0) count++;
        return count;
    }

    [Fact]
    public void FromSeed_IsDeterministic_SameSeedYieldsIdenticalConfig()
    {
        for (var seed = 0UL; seed < 50; seed++)
        {
            var a = SwarmConfig.FromSeed(seed);
            var b = SwarmConfig.FromSeed(seed);

            // SimulationOptions is a record, but it carries collection members without structural
            // equality; the swarm only sets scalar knobs, so compare every knob the generator touches.
            Assert.Equal(a.Seed, b.Seed);
            Assert.Equal(a.NodeCount, b.NodeCount);
            Assert.Equal(a.JobCount, b.JobCount);
            Assert.Equal(a.WorkloadDuration, b.WorkloadDuration);
            Assert.Equal(a.DrainAllowance, b.DrainAllowance);
            Assert.Equal(a.CrashProbabilityPerPoll, b.CrashProbabilityPerPoll);
            Assert.Equal(a.HeartbeatLossProbability, b.HeartbeatLossProbability);
            Assert.Equal(a.HandlerFailureProbability, b.HandlerFailureProbability);
            Assert.Equal(a.StoreFaultProbability, b.StoreFaultProbability);
            Assert.Equal(a.AckLossProbability, b.AckLossProbability);
            Assert.Equal(a.UnroutableProbability, b.UnroutableProbability);
            Assert.Equal(a.IsolationCount, b.IsolationCount);
            Assert.Equal(a.PermanentLossProbability, b.PermanentLossProbability);
            Assert.Equal(a.OperatorActionCount, b.OperatorActionCount);

            // The Schedule + Observer axes (issue 0201) are derived from the same Seed, so their populated
            // collections must match too — compare identity (count + ids), the only knobs the generator sets.
            Assert.Equal(a.Schedules.Select(s => s.Id), b.Schedules.Select(s => s.Id));
            Assert.Equal(a.Observers.Select(o => o.Id), b.Observers.Select(o => o.Id));
        }
    }

    [Fact]
    public void FromSeed_RespectsEnvelopePreconditions_AcrossManySeeds()
    {
        for (var seed = 0UL; seed < 500; seed++)
        {
            var o = SwarmConfig.FromSeed(seed);

            // NodeCount >= 2 so the N−1 isolation budget always leaves a live node (and >= 2 when isolation
            // is active is satisfied unconditionally).
            Assert.True(o.NodeCount >= 2, $"seed {seed}: NodeCount {o.NodeCount} < 2");

            // Healing-only isolation keeps Migration-Liveness convergent.
            Assert.Equal(0.0, o.PermanentLossProbability);

            // Every probability is a real probability; counts are non-negative.
            Assert.InRange(o.CrashProbabilityPerPoll, 0.0, 1.0);
            Assert.InRange(o.HeartbeatLossProbability, 0.0, 1.0);
            Assert.InRange(o.HandlerFailureProbability, 0.0, 1.0);
            Assert.InRange(o.StoreFaultProbability, 0.0, 1.0);
            Assert.InRange(o.AckLossProbability, 0.0, 1.0);
            Assert.InRange(o.UnroutableProbability, 0.0, 1.0);
            Assert.True(o.IsolationCount >= 0);
            Assert.True(o.OperatorActionCount >= 0);

            // A generous drain relative to the workload so faults stop and the cluster converges.
            Assert.True(o.DrainAllowance >= o.WorkloadDuration, $"seed {seed}: drain shorter than workload");
            Assert.True(o.JobCount > 0);
        }
    }

    [Fact]
    public void FromSeed_NeverSetsAnySabotageFlag_AcrossManySeeds()
    {
        var sabotageProps = typeof(SimulationOptions)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.Name.StartsWith("Sabotage", StringComparison.Ordinal) && p.PropertyType == typeof(bool))
            .ToList();

        // Guard the guard: there really are the ~11 Sabotage* flags the issue calls out.
        Assert.True(sabotageProps.Count >= 11, $"expected >= 11 Sabotage* flags, found {sabotageProps.Count}");

        for (var seed = 0UL; seed < 500; seed++)
        {
            var o = SwarmConfig.FromSeed(seed);
            foreach (var prop in sabotageProps)
            {
                Assert.False((bool)prop.GetValue(o)!, $"seed {seed}: {prop.Name} was set");
            }
        }
    }

    [Fact]
    public void FromSeed_Distribution_ProducesCalm_SingleFault_AndFullChaosRuns()
    {
        var calm = 0;        // no axis active
        var single = 0;      // exactly one axis active
        var fullChaos = 0;   // most axes active (>= 6 of 8)

        for (var seed = 0UL; seed < 2000; seed++)
        {
            var active = ActiveAxisCount(SwarmConfig.FromSeed(seed));
            if (active == 0) calm++;
            else if (active == 1) single++;
            else if (active >= 6) fullChaos++;
        }

        Assert.True(calm > 0, "no calm (zero-fault) runs occurred");
        Assert.True(single > 0, "no single-fault runs occurred");
        Assert.True(fullChaos > 0, "no full-chaos (>= 6 axes) runs occurred");
    }

    [Fact]
    public void FromSeed_GeneratedConfigsConverge_NoOracleTrips_AcrossASpreadOfSeeds()
    {
        // A modest spread end-to-end: each must converge (AllTerminal by DrainEnd) and trip no oracle, i.e.
        // Run() does not throw. Job/workload bands are deliberately small so the suite stays fast.
        for (var seed = 100UL; seed < 140; seed++)
        {
            var options = SwarmConfig.FromSeed(seed);
            var result = new Simulator(options).Run();

            // A sanity floor: the run produced a final state for every tracked job.
            Assert.Equal(options.JobCount, result.FinalJobs.Count);
        }
    }

    [Fact]
    public void FromSeed_ReachesMultiQueueTopologies_ConcurrencyLimits_AndFinitePools_AllInEnvelope()
    {
        // issue 0124: config-space must now reach multi-Queue topologies + ConcurrencyLimits + a finite
        // Backpressure pool (so limit/saturation regions become reachable), while EVERY generated config
        // still stays inside the supported envelope.
        var sawMultiQueue = false;
        var sawPerQueueLimits = false;
        var sawFinitePool = false;

        for (var seed = 0UL; seed < 500; seed++)
        {
            var o = SwarmConfig.FromSeed(seed);

            if (o.TopologyQueues >= 2) sawMultiQueue = true;
            if (o.ConcurrencyLimits.Count > 0) sawPerQueueLimits = true;
            if (o.PoolSize != int.MaxValue) sawFinitePool = true;

            // Still in-envelope: caps and pools never starve, healing-only isolation, the anchor keeps every
            // Queue served, and the two throttles stay within their tested regimes and never combine.
            foreach (var (queue, limit) in o.ConcurrencyLimits)
            {
                Assert.True(limit >= 1, $"seed {seed}: Queue {queue} limit {limit} < 1 (would starve)");
            }
            Assert.True(o.PoolSize >= 1, $"seed {seed}: PoolSize {o.PoolSize} < 1 (would starve)");
            if (o.ConcurrencyLimits.Count > 0)
            {
                Assert.True(o.TopologyQueues >= 2, $"seed {seed}: per-Queue limits without a multi-Queue topology");
            }
            if (o.PoolSize != int.MaxValue)
            {
                // The finite pool is the single-Queue regime, paired with a sub-Lease execution so real
                // in-flight == leases <= pool (no GC-pause zombie executions breaching the per-node cap).
                Assert.Equal(0, o.TopologyQueues);
                Assert.True(o.MaxExecutionDuration <= o.LeaseDuration, $"seed {seed}: pooled run lets executions outlive the Lease");
            }
            Assert.Equal(0.0, o.PermanentLossProbability); // healing-only still holds
        }

        Assert.True(sawMultiQueue, "no multi-Queue topology generated");
        Assert.True(sawPerQueueLimits, "no per-Queue ConcurrencyLimits generated");
        Assert.True(sawFinitePool, "no finite Backpressure pool generated");
    }

    [Fact]
    public void FromSeed_MultiQueueLimitedAndPooledConfigs_Converge_NoOracleTrips()
    {
        // issue 0124 acceptance: every generated evaluation that reaches the new config-space still passes the
        // budget guards (in-envelope by construction) — it converges and trips no oracle. Sweep the seeds that
        // actually exercise the new surface and run each end to end.
        var ran = 0;
        for (var seed = 0UL; seed < 400 && ran < 25; seed++)
        {
            var options = SwarmConfig.FromSeed(seed);
            if (options.TopologyQueues < 2 && options.ConcurrencyLimit is null && options.PoolSize == int.MaxValue)
            {
                continue; // not a new-surface config — covered by the existing convergence sweep
            }
            ran++;

            var result = new Simulator(options).Run(); // throws on any oracle trip
            Assert.Equal(options.JobCount, result.FinalJobs.Count);
        }
        Assert.True(ran >= 10, $"expected to exercise >= 10 new-surface configs, ran {ran}");
    }

    [Fact]
    public void GeneratedRuns_LightEveryGeneratorGapSituation_AcrossTheCorpus()
    {
        // Acceptance for BOTH issue 0124 (LimitSaturated, BackpressureIdle — the limit/saturation regions the
        // 3a single-Queue envelope could never reach) AND issue 0201 (ScheduleMinted, ObserverRedeliveryFired,
        // ObserverDeliveryUnderIsolation — the mint/Observer-delivery generator gap). One union over a single
        // SwarmConfig sweep (exactly how the VOPR Runner folds coverage), since every Run() here is a full 1h+6h
        // simulation — folding both assertions into one sweep avoids running the shared seed range twice. Each
        // Run() throws on any oracle trip, so a lit Situation is also proof the widened axes stay in-envelope.
        var coverage = new CoverageTracker();
        for (var seed = 0UL; seed < 500; seed++)
        {
            var result = new Simulator(SwarmConfig.FromSeed(seed)).Run();
            coverage.Union(result);
        }

        var report = coverage.Report();
        Assert.DoesNotContain(Situation.LimitSaturated, report.NeverHitSituations);
        Assert.DoesNotContain(Situation.BackpressureIdle, report.NeverHitSituations);
        Assert.DoesNotContain(Situation.ScheduleMinted, report.NeverHitSituations);
        Assert.DoesNotContain(Situation.ObserverRedeliveryFired, report.NeverHitSituations);
        Assert.DoesNotContain(Situation.ObserverDeliveryUnderIsolation, report.NeverHitSituations);
    }

    [Fact]
    public void SwarmRegime_RunsManySeedsEndToEnd_EachConverging_ExercisingNewFaultAxes()
    {
        // The swarm-regime smoke: across the corpus the new fault axes are genuinely exercised (a run with
        // isolation, ack-loss, unroutable, and operator actions all firing actually occurs), and every run
        // converges. We confirm exercise via the per-run result counters.
        var sawIsolation = false;
        var sawAckLoss = false;
        var sawQuarantine = false;
        var sawOperatorAction = false;

        for (var seed = 200UL; seed < 240; seed++)
        {
            var options = SwarmConfig.FromSeed(seed);
            var result = new Simulator(options).Run();

            Assert.Equal(options.JobCount, result.FinalJobs.Count);

            if (result.Isolations > 0) sawIsolation = true;
            if (result.AckLosses > 0) sawAckLoss = true;
            if (result.Quarantined > 0) sawQuarantine = true;
            if (result.OperatorCancels + result.OperatorRequeues + result.QueuePauses + result.ScheduleTriggers > 0)
                sawOperatorAction = true;
        }

        Assert.True(sawIsolation, "no isolation episode fired across the swarm corpus");
        Assert.True(sawAckLoss, "no ack-loss fired across the swarm corpus");
        Assert.True(sawQuarantine, "no unroutable/quarantine fired across the swarm corpus");
        Assert.True(sawOperatorAction, "no operator action fired across the swarm corpus");
    }
}
