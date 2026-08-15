namespace BackWave.Tests.Simulation;

/// <summary>
/// The single authority on what makes a swarm <see cref="Scenario"/> in-envelope (issue 0125, ADR 0018/0025).
/// <see cref="SwarmConfig.FromSeed"/> draws an in-envelope config straight from a Seed; the coverage-guided
/// explorer (issue 0125) instead MUTATES an existing Scenario's knobs, so it needs a way to keep the mutant
/// inside the supported envelope — config-space mutations evaluate by <b>generate</b>, which has no
/// veto-on-replay, so an out-of-envelope config would trip a real oracle and masquerade as a bug.
///
/// <para><see cref="Confine"/> clamps every tuned knob to the same bands <see cref="SwarmConfig.FromSeed"/>
/// draws within and re-applies the same structural gates, so any mutant routed through it is as in-envelope as
/// a freshly-generated config. The bands here are the single source of truth; a lock-step test asserts a
/// <see cref="SwarmConfig.FromSeed"/> output is already a <see cref="Confine"/> fixed-point, so the two can
/// never silently drift.</para>
/// </summary>
internal static class SwarmEnvelope
{
    // Fault-axis intensity bands — identical to the bands SwarmConfig.FromSeed draws within. An active axis is
    // clamped into its band; a non-positive value is OFF. (Lock-step guarded by SwarmEnvelopeTests.)
    public static readonly (double Lo, double Hi) CrashBand = (0.001, 0.01);
    public static readonly (double Lo, double Hi) HeartbeatBand = (0.02, 0.10);
    public static readonly (double Lo, double Hi) HandlerBand = (0.05, 0.25);
    public static readonly (double Lo, double Hi) StoreBand = (0.02, 0.15);
    public static readonly (double Lo, double Hi) AckLossBand = (0.02, 0.10);
    public static readonly (double Lo, double Hi) UnroutableBand = (0.02, 0.10);

    public const int IsolationMax = 8;          // 1..8 healing episodes
    public const int OperatorMin = 5, OperatorMax = 30;
    public const int JobMin = 30, JobMax = 60;
    public const int TopologyMin = 2, TopologyMax = 3;
    public const int LimitMin = 1, LimitMax = 3;
    public const int PoolMin = 1, PoolMax = 4;

    public static readonly TimeSpan WorkloadDuration = TimeSpan.FromHours(1);
    public static readonly TimeSpan DrainAllowance = TimeSpan.FromHours(6);
    public static readonly TimeSpan DefaultMaxExecution = TimeSpan.FromSeconds(90); // GC-pause regime (> Lease)
    public static readonly TimeSpan PooledMaxExecution = TimeSpan.FromSeconds(40);  // <= 60s Lease when pooled

    private static readonly IReadOnlyDictionary<string, int> NoLimits = new Dictionary<string, int>();
    private static readonly IReadOnlyList<SeededSchedule> NoSchedules = [];

    /// <summary>
    /// Returns <paramref name="scenario"/> confined to the supported envelope: a 3-node healing-only,
    /// Sabotage-free cluster on the converging workload/drain band, with every tuned knob clamped to its band
    /// and the structural gates enforced in precedence order <b>isolation &gt; pool &gt; limits</b>:
    /// <list type="bullet">
    /// <item><b>Isolation</b> active ⟹ the throttling surface is cleared (single Queue, no limits, unbounded
    /// pool): the tight Migration-Liveness bound assumes a survivor has spare capacity to re-home a lapsed Lease.</item>
    /// <item><b>Finite pool</b> ⟹ the clean-execution single-Queue regime: no lease-loss axes (crash / heartbeat /
    /// store / ack-loss, each of which can leave a node executing a zombie past its Lease), no Recurring Schedule
    /// (a mid-run mint injects out-of-band work), and a sub-Lease execution, so real in-flight == leases ≤ pool
    /// and the per-node cap holds.</item>
    /// <item><b>Per-Queue limits</b> ⟹ a multi-Queue topology (≥2); limits over a single Queue are dropped.</item>
    /// </list>
    /// </summary>
    public static Scenario Confine(Scenario scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);

        var crash = ConfineProbability(scenario.CrashProbabilityPerPoll, CrashBand);
        var heartbeat = ConfineProbability(scenario.HeartbeatLossProbability, HeartbeatBand);
        var handler = ConfineProbability(scenario.HandlerFailureProbability, HandlerBand);
        var store = ConfineProbability(scenario.StoreFaultProbability, StoreBand);
        var ackLoss = ConfineProbability(scenario.AckLossProbability, AckLossBand);
        var unroutable = ConfineProbability(scenario.UnroutableProbability, UnroutableBand);

        var isolation = scenario.IsolationCount <= 0 ? 0 : Math.Clamp(scenario.IsolationCount, 1, IsolationMax);
        var operatorActions = scenario.OperatorActionCount <= 0
            ? 0
            : Math.Clamp(scenario.OperatorActionCount, OperatorMin, OperatorMax);
        var jobCount = Math.Clamp(scenario.JobCount, JobMin, JobMax);

        var topologyQueues = scenario.TopologyQueues;
        var limits = scenario.ConcurrencyLimits;
        var poolSize = scenario.PoolSize;
        var schedules = scenario.Schedules;

        if (isolation > 0)
        {
            // Isolation excludes the throttling surface entirely (multi-Queue makes ExpireLeases queue-scoped,
            // a tight limit/pool removes sweep capacity — either can miss the tight migration bound).
            topologyQueues = 0;
            limits = NoLimits;
            poolSize = int.MaxValue;
        }
        else if (poolSize != int.MaxValue)
        {
            // Finite pool = the clean-execution single-Queue 0072 regime. Its per-node-cap oracle assumes a
            // closed job set claimed through the pool (real in-flight == leases ≤ pool), so it also excludes a
            // Recurring Schedule — a mid-run mint injects out-of-band work the same way the lease-loss axes do
            // (issue 0201). Cleared here alongside crash/heartbeat/store/ack-loss.
            topologyQueues = 0;
            limits = NoLimits;
            crash = heartbeat = store = ackLoss = 0;
            poolSize = Math.Clamp(poolSize, PoolMin, PoolMax);
            schedules = NoSchedules;
        }
        else if (topologyQueues >= 2)
        {
            topologyQueues = Math.Clamp(topologyQueues, TopologyMin, TopologyMax);
            limits = ConfineLimits(limits, topologyQueues);
        }
        else
        {
            // Single-Queue, unbounded pool: per-Queue limits have no multi-Queue topology to live over.
            topologyQueues = 0;
            limits = NoLimits;
        }

        var maxExecution = poolSize == int.MaxValue ? DefaultMaxExecution : PooledMaxExecution;

        return scenario with
        {
            NodeCount = 3,
            PermanentLossProbability = 0.0,
            WorkloadDuration = WorkloadDuration,
            DrainAllowance = DrainAllowance,
            MaxExecutionDuration = maxExecution,
            CrashProbabilityPerPoll = crash,
            HeartbeatLossProbability = heartbeat,
            HandlerFailureProbability = handler,
            StoreFaultProbability = store,
            AckLossProbability = ackLoss,
            UnroutableProbability = unroutable,
            IsolationCount = isolation,
            OperatorActionCount = operatorActions,
            JobCount = jobCount,
            TopologyQueues = topologyQueues,
            ConcurrencyLimits = limits,
            PoolSize = poolSize,
            Schedules = schedules,
            // The Swarm never manufactures failures (ADR 0018): clear every Sabotage flag.
            Sabotage = false,
            SabotageSlotDoubleRelease = false,
            SabotagePoolSize = false,
            SabotageOutcomeFence = false,
            SabotageBatchFence = false,
            SabotageMigrationSweep = false,
            SabotageMigrationFaultGrace = false,
            SabotagePausedClaim = false,
            SabotageCancelProvenance = false,
            SabotageLegalTransition = false,
            SabotageExecuteLiveness = false,
            SabotageAuditCompleteness = false,
            SabotageServedSet = false,
            SabotageInlineUnroutableReport = false,
            SabotageDeferredUnroutableReport = false,
        };
    }

    /// <summary>A non-positive probability is OFF; a positive one is clamped into the axis's band.</summary>
    private static double ConfineProbability(double value, (double Lo, double Hi) band)
        => value <= 0 ? 0.0 : Math.Clamp(value, band.Lo, band.Hi);

    /// <summary>
    /// Prunes per-Queue limits to the Queues a <paramref name="topologyQueues"/>-Queue topology actually has
    /// (<c>default</c> plus <c>q1..q{topologyQueues-1}</c>) and clamps each cap into the limit band. An empty
    /// result (no valid Queue named) is fine — that is just an unlimited multi-Queue topology.
    /// </summary>
    private static IReadOnlyDictionary<string, int> ConfineLimits(IReadOnlyDictionary<string, int> limits, int topologyQueues)
    {
        if (limits.Count == 0)
        {
            return NoLimits;
        }
        var valid = new HashSet<string>(topologyQueues) { "default" };
        for (var q = 1; q < topologyQueues; q++)
        {
            valid.Add($"q{q}");
        }
        var confined = new Dictionary<string, int>();
        foreach (var (queue, cap) in limits)
        {
            if (valid.Contains(queue))
            {
                confined[queue] = Math.Clamp(cap, LimitMin, LimitMax);
            }
        }
        return confined.Count == 0 ? NoLimits : confined;
    }
}
