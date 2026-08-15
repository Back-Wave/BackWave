namespace BackWave.Tests.Simulation;

/// <summary>
/// The envelope authority (issue 0125, ADR 0025): <see cref="SwarmEnvelope.Confine"/> clamps a mutated
/// <see cref="Scenario"/> back into the supported envelope so config-space mutations stay in-envelope by
/// construction, and <see cref="ConfigMutator"/> perturbs a Scenario's knobs only ever producing such mutants.
/// These prove: a <see cref="SwarmConfig.FromSeed"/> output is already a Confine fixed-point (the bands never
/// drift); Confine drags an arbitrarily out-of-envelope Scenario back in and it then runs clean; and a long
/// stream of mutations evaluated by generate trips no oracle.
/// </summary>
public sealed class SwarmEnvelopeTests
{
    private static Scenario FromSeed(ulong seed) => Scenario.FromOptions(SwarmConfig.FromSeed(seed));

    /// <summary>Asserts the two Scenarios agree on every knob the swarm tunes (records don't structurally compare dicts).</summary>
    private static void AssertSameKnobs(Scenario a, Scenario b)
    {
        Assert.Equal(a.NodeCount, b.NodeCount);
        Assert.Equal(a.JobCount, b.JobCount);
        Assert.Equal(a.WorkloadDuration, b.WorkloadDuration);
        Assert.Equal(a.DrainAllowance, b.DrainAllowance);
        Assert.Equal(a.MaxExecutionDuration, b.MaxExecutionDuration);
        Assert.Equal(a.CrashProbabilityPerPoll, b.CrashProbabilityPerPoll);
        Assert.Equal(a.HeartbeatLossProbability, b.HeartbeatLossProbability);
        Assert.Equal(a.HandlerFailureProbability, b.HandlerFailureProbability);
        Assert.Equal(a.StoreFaultProbability, b.StoreFaultProbability);
        Assert.Equal(a.AckLossProbability, b.AckLossProbability);
        Assert.Equal(a.UnroutableProbability, b.UnroutableProbability);
        Assert.Equal(a.IsolationCount, b.IsolationCount);
        Assert.Equal(a.PermanentLossProbability, b.PermanentLossProbability);
        Assert.Equal(a.OperatorActionCount, b.OperatorActionCount);
        Assert.Equal(a.TopologyQueues, b.TopologyQueues);
        Assert.Equal(a.PoolSize, b.PoolSize);
        Assert.Equal(a.ConcurrencyLimits.Count, b.ConcurrencyLimits.Count);
        foreach (var (queue, cap) in a.ConcurrencyLimits)
        {
            Assert.Equal(cap, b.ConcurrencyLimits[queue]);
        }
        // Schedule + Observer axes (issue 0201) — compare identity (Confine must neither drop nor invent them).
        Assert.Equal(a.Schedules.Select(s => s.Id), b.Schedules.Select(s => s.Id));
        Assert.Equal(a.Observers.Select(o => o.Id), b.Observers.Select(o => o.Id));
    }

    [Fact]
    public void Confine_IsAFixedPointOf_SwarmConfigFromSeed()
    {
        // The bands and gates in SwarmEnvelope must match SwarmConfig.FromSeed exactly: a freshly-generated
        // config is already in-envelope, so Confine must leave it untouched. This catches any silent drift.
        for (var seed = 0UL; seed < 500; seed++)
        {
            var generated = FromSeed(seed);
            AssertSameKnobs(generated, SwarmEnvelope.Confine(generated));
        }
    }

    [Fact]
    public void Confine_DragsAnOutOfEnvelopeScenarioBackIn_AndItRunsClean()
    {
        // Deliberately illegal on every axis: out-of-band probabilities, permanent loss, a Sabotage flag, AND
        // the three mutually-exclusive throttles all at once (isolation + multi-Queue + limits + finite pool).
        var nasty = FromSeed(1) with
        {
            CrashProbabilityPerPoll = 0.9,
            HeartbeatLossProbability = 0.9,
            HandlerFailureProbability = 0.9,
            StoreFaultProbability = 0.9,
            AckLossProbability = 0.9,
            UnroutableProbability = 0.9,
            PermanentLossProbability = 0.5,
            IsolationCount = 50,
            OperatorActionCount = 9999,
            JobCount = 100000,
            TopologyQueues = 9,
            ConcurrencyLimits = new Dictionary<string, int> { ["default"] = 99, ["q1"] = 0, ["nonexistent"] = 5 },
            PoolSize = 1,
            Sabotage = true,
            SabotageLegalTransition = true,
            SabotagePoolSize = true,
        };

        var confined = SwarmEnvelope.Confine(nasty);

        // Fixed structural invariants.
        Assert.Equal(3, confined.NodeCount);
        Assert.Equal(0.0, confined.PermanentLossProbability);
        Assert.False(confined.Sabotage);
        Assert.False(confined.SabotageLegalTransition);
        Assert.False(confined.SabotagePoolSize);

        // Isolation wins the precedence: the throttling surface is cleared.
        Assert.True(confined.IsolationCount is >= 1 and <= SwarmEnvelope.IsolationMax);
        Assert.Equal(0, confined.TopologyQueues);
        Assert.Empty(confined.ConcurrencyLimits);
        Assert.Equal(int.MaxValue, confined.PoolSize);

        // Probabilities clamped into their bands; counts into theirs.
        Assert.InRange(confined.CrashProbabilityPerPoll, SwarmEnvelope.CrashBand.Lo, SwarmEnvelope.CrashBand.Hi);
        Assert.InRange(confined.JobCount, SwarmEnvelope.JobMin, SwarmEnvelope.JobMax);
        Assert.InRange(confined.OperatorActionCount, SwarmEnvelope.OperatorMin, SwarmEnvelope.OperatorMax);

        // And the confined Scenario actually runs clean (no oracle trips on a generate run).
        var result = new Simulator(confined.ToOptions()).Run();
        Assert.Equal(confined.JobCount, result.FinalJobs.Count);
    }

    [Fact]
    public void Confine_FinitePoolScenario_BecomesTheCleanExecutionSingleQueueRegime()
    {
        // A finite pool with lease-loss faults on, a long execution, and a Recurring Schedule — Confine must strip
        // the faults, force a single Queue, cap the execution under the Lease, AND drop the schedule (the 0072
        // regime that keeps the per-node cap honest: a mid-run mint injects out-of-band work the pool oracle does
        // not account for, so pooled+scheduled would trip PerNodeCap — issue 0201).
        var pooled = FromSeed(2) with
        {
            PoolSize = 3,
            IsolationCount = 0,
            CrashProbabilityPerPoll = 0.005,
            HeartbeatLossProbability = 0.05,
            StoreFaultProbability = 0.1,
            AckLossProbability = 0.05,
            MaxExecutionDuration = TimeSpan.FromSeconds(90),
            TopologyQueues = 3,
            Schedules = [new SeededSchedule { Id = "swarm-schedule", Cron = "*/20 * * * *" }],
        };

        var confined = SwarmEnvelope.Confine(pooled);

        Assert.InRange(confined.PoolSize, SwarmEnvelope.PoolMin, SwarmEnvelope.PoolMax);
        Assert.Equal(0, confined.TopologyQueues);
        Assert.Equal(0.0, confined.CrashProbabilityPerPoll);
        Assert.Equal(0.0, confined.HeartbeatLossProbability);
        Assert.Equal(0.0, confined.StoreFaultProbability);
        Assert.Equal(0.0, confined.AckLossProbability);
        Assert.True(confined.MaxExecutionDuration <= confined.LeaseDuration);
        Assert.Empty(confined.Schedules); // the finite-pool regime excludes a mid-run mint (issue 0201)

        var result = new Simulator(confined.ToOptions()).Run();
        Assert.Equal(confined.JobCount, result.FinalJobs.Count);
    }

    [Fact]
    public void Mutate_ProducesOnlyInEnvelopeConfigs_EvaluatedByGenerate()
    {
        // The headline 0125 guarantee: every config-space mutant passes the budget guards (runs clean by
        // generate). Mutate a chain from each parent and run each mutant; a trip here would be an envelope leak.
        var rng = new DeterministicRandom(0xC0FFEE);
        var mutantsRun = 0;
        for (var seed = 0UL; seed < 20; seed++)
        {
            var parent = FromSeed(seed);
            for (var step = 0; step < 4; step++)
            {
                var childSeed = ((ulong)rng.NextUInt() << 32) | rng.NextUInt();
                var mutant = ConfigMutator.Mutate(parent, childSeed, rng);

                // Idempotence: a mutant is already confined (Confine of it changes nothing).
                AssertSameKnobs(mutant, SwarmEnvelope.Confine(mutant));

                var result = new Simulator(mutant.ToOptions()).Run(); // throws on any oracle trip
                Assert.Equal(mutant.JobCount, result.FinalJobs.Count);
                mutantsRun++;

                parent = mutant; // walk the chain so deeper mutations are exercised too
            }
        }
        Assert.True(mutantsRun >= 80);
    }
}
