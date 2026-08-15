using BackWave.Observers;
using BackWave.Storage;

namespace BackWave.Tests.Simulation;

/// <summary>
/// The Swarm generator (issue 0086, PRD 0004, ADR 0018): <see cref="FromSeed"/> derives a complete
/// <see cref="SimulationOptions"/> as a pure function of one 64-bit Seed, so a swarm-discovered failure
/// still replays from its Seed alone. It is the direct descendant of <c>SoakTests</c>' seed-derived knobs,
/// made into a first-class, envelope-confined generator.
///
/// For each fault axis (crash, heartbeat-loss, handler-failure, store-fault, ack-loss, unroutable,
/// isolation, operator-actions) it flips an <b>active?</b> coin biased toward OFF, then — only if active —
/// draws an <b>intensity</b> within a sane band. So over many Seeds you get calm runs (no axis active),
/// single-fault runs, and full-chaos runs (most axes active), and no axis permanently masks another.
///
/// Every generated config stays inside the harness's supported envelope (ADR 0018):
/// <list type="bullet">
/// <item><see cref="SimulationOptions.NodeCount"/> is fixed at 3, so the Isolation Scheduler's N−1 budget
/// always keeps ≥1 node reachable (isolation's <c>NodeCount ≥ 2</c> precondition is satisfied by
/// construction).</item>
/// <item>Healing-only isolation: <see cref="SimulationOptions.PermanentLossProbability"/> stays 0, so every
/// isolation episode heals well inside the generous drain window and Migration-Liveness always converges.</item>
/// <item>Multi-Queue topologies, per-Queue <see cref="SimulationOptions.ConcurrencyLimits"/>, and a finite
/// Backpressure <see cref="SimulationOptions.PoolSize"/> are drawn in (issue 0124) so the limit/saturation
/// coverage regions are reachable — but always in-envelope: node-0 is the universal anchor so no Queue is
/// stranded, limits/pools are ≥1 so nothing starves, and the generous drain absorbs the slower throughput.</item>
/// <item>A Recurring <see cref="SimulationOptions.Schedules"/> and a Transition <see cref="SimulationOptions.Observers"/>
/// axis are drawn in (issue 0201) so the mint and Observer-delivery coverage regions are reachable — a coarse
/// schedule mints onto the always-served "default" Queue and its instances drain in the window, and one
/// terminal Observer with a succeeding sink rides every fault axis (§0078), producing tolerated redeliveries.</item>
/// <item>Workload/drain/job-count bands chosen so generated runs always terminate (no liveness trip): a
/// modest workload and a generous drain, the SoakTests/AllAxesScenario converging regime.</item>
/// </list>
///
/// It <b>never</b> sets any <c>Sabotage*</c> flag — those manufacture failures on purpose. Combined with
/// the envelope confinement, that makes a swarm-discovered failure always a real bug, never fed-in garbage.
/// </summary>
internal static class SwarmConfig
{
    // The swarm runs BEFORE a Simulator, so it just needs to be a pure function of the Seed. We still xor
    // with an ASCII constant ("SWARMCFG") to keep an independent stream by convention with the rest of the
    // harness — it does not collide with any in-Simulator stream.
    private const ulong SwarmStreamSalt = 0x5357_4152_4D43_4647UL; // "SWARMCFG"

    /// <summary>The per-axis probability a fault axis is active at all — biased toward OFF.</summary>
    private const double AxisActiveProbability = 0.30;

    /// <summary>
    /// Derives a deterministic, envelope-confined <see cref="SimulationOptions"/> from <paramref name="seed"/>.
    /// The same Seed always yields an identical config.
    /// </summary>
    public static SimulationOptions FromSeed(ulong seed)
    {
        var rng = new DeterministicRandom(seed ^ SwarmStreamSalt);

        // Crash — per-poll probability in a calm band (the historical default is 0.005).
        var crash = rng.NextDouble() < AxisActiveProbability
            ? Band(rng, 0.001, 0.01)
            : 0.0;

        // Heartbeat loss — per-heartbeat probability.
        var heartbeat = rng.NextDouble() < AxisActiveProbability
            ? Band(rng, 0.02, 0.10)
            : 0.0;

        // Handler failure — per-execution probability; jobs that fail every attempt dead-letter (terminal),
        // so a high band still converges.
        var handler = rng.NextDouble() < AxisActiveProbability
            ? Band(rng, 0.05, 0.25)
            : 0.0;

        // Store fault — per-store-call transient probability; faults stop at WorkloadEnd and the node retries.
        var store = rng.NextDouble() < AxisActiveProbability
            ? Band(rng, 0.02, 0.15)
            : 0.0;

        // Ack loss — per-attempt commit-but-lost-ack probability; the fenced retry is absorbed.
        var ackLoss = rng.NextDouble() < AxisActiveProbability
            ? Band(rng, 0.02, 0.10)
            : 0.0;

        // Unroutable — per-job dispatch-side probability; a marked job reaches Quarantined (terminal).
        var unroutable = rng.NextDouble() < AxisActiveProbability
            ? Band(rng, 0.02, 0.10)
            : 0.0;

        // Isolation — a count of healing episodes; NodeCount is 3 so the N−1 budget always leaves a live node.
        var isolationActive = rng.NextDouble() < AxisActiveProbability;
        var isolation = isolationActive ? 1 + rng.Next(8) : 0; // 1..8 episodes

        // Operator actions — a count of Cancel/Requeue/Pause-Resume/TriggerScheduleNow races; all lifted at
        // WorkloadEnd so the cluster can drain.
        var operatorActive = rng.NextDouble() < AxisActiveProbability;
        var operatorActions = operatorActive ? 5 + rng.Next(26) : 0; // 5..30 actions

        // Topology + Concurrency Limits + Backpressure (issue 0124): widen config-space so the limit/saturation
        // coverage regions become REACHABLE — without this the guided search has constant-false targets to chase
        // forever (ADR 0025, decision 2). These draws are APPENDED after the eight fault axes so the existing
        // axis distribution and convergence battery stay byte-identical. Everything stays in-envelope:
        //   • NodeCount is 3 and node-0 is BuildTopology's universal anchor, so every generated Queue is served
        //     by a node that survives the N−1 isolation budget — no Queue is ever stranded.
        //   • Limits and pools are >= 1 (never starve), faults stop at WorkloadEnd, and the 6h drain absorbs the
        //     slower throughput a small limit/pool imposes, so every run still converges.
        //   • No Sabotage* flag is ever set, so the per-Queue-limit, slot-double-release, and per-node-cap
        //     oracles only fire on a genuine bug — a multi-Queue+limited run is clean by construction.
        // Confine the entire widened surface (multi-Queue topology, per-Queue limits, finite pool) to the
        // NON-isolation regime. The Migration-Liveness Oracle enforces a deliberately tight, config-derived
        // sweep bound (MaxClockSkew + 3·PollInterval) that assumes a survivor re-homes a lapsed Lease within a
        // poll or two — the same "a running survivor" precondition that already requires crashes off. Two new
        // knobs break that assumption when node-0 (the universal anchor) is the isolated owner:
        //   • multi-Queue topology makes ExpireLeases queue-scoped, so only an overlap survivor that SERVES the
        //     lapsed Queue can sweep it — not necessarily the next node to poll;
        //   • a tight ConcurrencyLimit / finite PoolSize removes the spare capacity to sweep promptly.
        // Either can push the sweep just past the bound, so combining them with active isolation is OUT of the
        // supported envelope. Gating here keeps every generated config in-envelope (isolation runs stay the
        // pre-0124 single-Queue / unbounded shape) while still exercising the limit/saturation regions on the
        // majority isolation-free runs — coverage that does not need isolation to co-occur (ADR 0025 decision 2).
        var wideConfigAllowed = !isolationActive;

        // The two throttles stay within their individually-tested regimes and never combine:
        //   • Per-Queue ConcurrencyLimits live over a MULTI-Queue topology (the issue-0074 regime) — unbounded
        //     pool, so the per-node-cap oracle is inert. Lights LimitSaturated.
        //   • A finite Backpressure pool lives over the SINGLE-Queue world (the issue-0072 regime). Lights
        //     BackpressureIdle.
        // Keeping them apart avoids the multi-throttle corner the migration/per-node envelope does not cover.
        var multiQueue = wideConfigAllowed && rng.NextDouble() < AxisActiveProbability;
        var topologyQueues = multiQueue ? 2 + rng.Next(2) : 0; // 2..3 named Queues, or the single-Queue world

        // Per-Queue Concurrency Limits — small caps (1..3) so saturation is reachable under the modest job load.
        // Each named Queue gets its own independent cap (the per-Queue slot-accounting surface). Multi-Queue only.
        var limited = multiQueue && rng.NextDouble() < AxisActiveProbability;
        var concurrencyLimits = new Dictionary<string, int>();
        if (limited)
        {
            concurrencyLimits["default"] = 1 + rng.Next(3); // 1..3
            for (var q = 1; q < topologyQueues; q++)
            {
                concurrencyLimits[$"q{q}"] = 1 + rng.Next(3); // 1..3
            }
        }

        // Node-local Backpressure pool — a small finite cap (1..4) so a busy node idles on backpressure. A
        // deterministic config gate (no rng stream of its own at runtime). Confined to a CLEAN-execution,
        // single-Queue regime: the finite pool bounds a node's real in-flight only while no execution outlives
        // its Lease. The harness's GC-pause regime — an execution whose Lease lapses (heartbeat loss, a store
        // fault dropping the renewal, a crash) while it keeps running — legitimately leaves a node executing a
        // zombie PLUS a fresh claim, so real in-flight exceeds the pool and the per-node-cap oracle trips. That
        // is outside the pool-backpressure envelope, so the pool is drawn only when every lease-loss / zombie
        // axis is off — crash, heartbeat loss, store fault (all drop the Lease renewal) and ack loss (the node
        // re-reports a committed-but-unacked outcome while still holding the execution). Handler failure is fine
        // (it completes within the execution and retries); unroutable jobs never enter the in-flight set. Paired
        // with a sub-Lease execution, real in-flight == leases <= pool then holds: the clean issue-0072 regime.
        var leaseStable = crash == 0.0 && heartbeat == 0.0 && store == 0.0 && ackLoss == 0.0;
        var poolBounded = wideConfigAllowed && !multiQueue && leaseStable && rng.NextDouble() < AxisActiveProbability;
        var poolSize = poolBounded ? 1 + rng.Next(4) : int.MaxValue; // 1..4 or unbounded
        var maxExecution = poolBounded ? TimeSpan.FromSeconds(40) : TimeSpan.FromSeconds(90); // <= 60s Lease when pooled

        // A modest job count keeps the swarm suite fast while still giving the faults something to chew on. Drawn
        // here — its historical stream position, the first draw after the pool block — so the two new coins below
        // stay strictly last and this draw is unshifted (see the byte-identical note on those coins).
        var jobCount = 30 + rng.Next(31); // 30..60

        // Recurring Schedule + Transition Observer axes (issue 0201): the mint path and the Observer delivery
        // path were REACHABLE by the product yet never drawn by the generator, so three Situations (ScheduleMinted,
        // ObserverRedeliveryFired, ObserverDeliveryUnderIsolation) sat permanently unreached — a generator gap, not
        // dead situations. Two more active-coins close it. Both draws are APPENDED after every pre-existing draw
        // above (fault/topology and the job-count draw just hoisted), so each seed's existing config stays
        // byte-identical; only the two new collections get populated.
        // Both stay in-envelope:
        //   • A Recurring Schedule mints a handful of instances (a coarse */20 cron over the 1h workload) onto the
        //     "default" Queue — always served by the node-0 anchor even under a multi-Queue topology, so no minted
        //     job is ever stranded. Minted jobs are ordinary jobs; the 6h drain absorbs them and the schedule is
        //     removed at WorkloadEnd so already-minted instances converge. It is drawn OFF in the finite-pool
        //     regime, though: that regime's per-node-cap oracle assumes a closed job set claimed through the pool
        //     (real in-flight == leases ≤ pool), and a mid-run mint injects out-of-band work the same way the
        //     lease-loss axes do — so the pool excludes it exactly as it excludes crash/heartbeat/store/ack-loss.
        //   • One terminal-state Observer with a SUCCEEDING sink (FailingObservers stays empty — a poison callback
        //     is a Sabotage-class fault the swarm never injects). §0078 proves delivery converges under crash,
        //     heartbeat loss, store faults, and Node Isolation, producing tolerated duplicates (Total > Unique)
        //     that light ObserverRedeliveryFired; a delivery during an isolation episode lights
        //     ObserverDeliveryUnderIsolation. Isolation already forces the single-Queue world (wideConfigAllowed
        //     above), so Observer × Isolation always lands in exactly the single-Queue regime §0078 hardened.
        // Draw the coin unconditionally (so the Observer coin below keeps a stable stream position), then gate
        // the result off the finite-pool regime — see the note above.
        var scheduleActive = rng.NextDouble() < AxisActiveProbability;
        var schedules = scheduleActive && !poolBounded
            ? new[] { new SeededSchedule { Id = "swarm-schedule", Cron = "*/20 * * * *" } }
            : Array.Empty<SeededSchedule>();

        var observed = rng.NextDouble() < AxisActiveProbability;
        var observers = observed
            ? new[]
            {
                new ObserverRegistration(
                    "swarm-observer",
                    new ObserverSubscription(
                        [JobState.Succeeded, JobState.DeadLettered, JobState.Cancelled, JobState.Quarantined])),
            }
            : Array.Empty<ObserverRegistration>();

        return new SimulationOptions
        {
            Seed = seed,
            NodeCount = 3,
            JobCount = jobCount,
            // A modest workload window plus a generous drain — the SoakTests/AllAxesScenario converging band.
            WorkloadDuration = TimeSpan.FromHours(1),
            DrainAllowance = TimeSpan.FromHours(6),

            CrashProbabilityPerPoll = crash,
            HeartbeatLossProbability = heartbeat,
            HandlerFailureProbability = handler,
            StoreFaultProbability = store,
            AckLossProbability = ackLoss,
            UnroutableProbability = unroutable,
            IsolationCount = isolation,
            // Healing-only: never a permanent loss, so Migration-Liveness always converges inside the drain.
            PermanentLossProbability = 0.0,
            OperatorActionCount = operatorActions,
            TopologyQueues = topologyQueues,
            ConcurrencyLimits = concurrencyLimits,
            PoolSize = poolSize,
            MaxExecutionDuration = maxExecution,
            Schedules = schedules,
            Observers = observers,
        };
    }

    /// <summary>
    /// The "radioactive" generator (the <c>--radioactive</c> swarm, cf. TigerBeetle's Level-3 radioactive): a
    /// deliberately CATASTROPHIC, out-of-envelope config where — unlike <see cref="FromSeed"/> — every fault axis
    /// is ALWAYS active at maximum intensity, isolation episodes can be permanent (a node lost for good), and the
    /// throttle surfaces are combined. The Seed still varies the exact intensities within their high bands, so
    /// successive Seeds explore distinct max-chaos worlds, and the run still replays from its Seed alone.
    ///
    /// This world is, by construction, non-converging, so it is meaningful ONLY with the bound-dependent liveness
    /// oracles disarmed — hence <see cref="SimulationOptions.RadioactiveMode"/> is set. The SAFETY oracles
    /// (Effect-Once at the boundary, no-double-execution, legal-transition, attempt-ceiling, Concurrency-Limit and
    /// served-set containment, slot Effect-Once, terminal stability) stay fully armed and must hold no matter how
    /// chaotic the world is — a trip is a real, deep bug.
    ///
    /// Two deliberate confinements keep every ARMED oracle honest while everything else melts:
    /// <list type="bullet">
    /// <item>The node Backpressure pool stays UNBOUNDED. A finite pool's per-node-cap oracle assumes lease-stable
    /// execution (no zombies); maxed crash/heartbeat/store/ack-loss legitimately leave a node running a zombie
    /// PLUS a fresh claim, so a finite pool would false-positive. Per-Queue Concurrency Limits (store-enforced on
    /// Leased rows, robust to zombies) ARE drawn, so I3 and slot Effect-Once stay exercised.</item>
    /// <item><see cref="SimulationOptions.NodeCount"/> stays at 3: the Isolation Scheduler's N−1 budget protects
    /// the last reachable node from permanent loss, so even maxed isolation + permanent loss always leaves one
    /// node alive to make (slow) progress — the run still terminates at the drain-window time bound.</item>
    /// </list>
    /// </summary>
    public static SimulationOptions Radioactive(ulong seed)
    {
        var rng = new DeterministicRandom(seed ^ SwarmStreamSalt);

        // Every axis ON — no active-coin. Intensities draw from HIGH bands (well above FromSeed's calm bands),
        // varied per Seed so each world differs. The draw ORDER mirrors FromSeed for familiarity.
        var crash = Band(rng, 0.05, 0.15);        // per-poll: a node crashes every ~7..20 polls
        var heartbeat = Band(rng, 0.30, 0.60);    // most heartbeats lost — leases lapse constantly
        var handler = Band(rng, 0.40, 0.80);      // most executions fail — heavy retry/dead-letter churn
        var store = Band(rng, 0.20, 0.50);        // frequent transient store faults on the hot path
        var ackLoss = Band(rng, 0.20, 0.50);      // committed-but-unacked re-reports stress the fence
        var unroutable = Band(rng, 0.10, 0.30);   // a sizeable minority quarantine; the rest still execute
        var isolation = 10 + rng.Next(21);        // 10..30 isolation episodes
        var operatorActions = 30 + rng.Next(51);  // 30..80 operator races
        var permanentLoss = Band(rng, 0.10, 0.30); // a fraction of isolations never heal — nodes lost for good

        // Throttles combined (FromSeed keeps them apart; radioactive does not). Multi-Queue is always on so the
        // topology is heterogeneous and contended; per-Queue Concurrency Limits are always drawn so I3 and the
        // slot-double-release Effect-Once oracle stay under load. node-0 stays the universal anchor.
        var topologyQueues = 2 + rng.Next(2);     // 2..3 named Queues
        var concurrencyLimits = new Dictionary<string, int> { ["default"] = 1 + rng.Next(3) };
        for (var q = 1; q < topologyQueues; q++)
        {
            concurrencyLimits[$"q{q}"] = 1 + rng.Next(3); // 1..3
        }

        return new SimulationOptions
        {
            Seed = seed,
            NodeCount = 3,
            JobCount = 30 + rng.Next(31), // 30..60
            WorkloadDuration = TimeSpan.FromHours(1),
            DrainAllowance = TimeSpan.FromHours(6),

            CrashProbabilityPerPoll = crash,
            HeartbeatLossProbability = heartbeat,
            HandlerFailureProbability = handler,
            StoreFaultProbability = store,
            AckLossProbability = ackLoss,
            UnroutableProbability = unroutable,
            IsolationCount = isolation,
            PermanentLossProbability = permanentLoss,
            OperatorActionCount = operatorActions,
            TopologyQueues = topologyQueues,
            ConcurrencyLimits = concurrencyLimits,
            // Pool UNBOUNDED (see remarks): a finite pool's cap oracle is out-of-envelope under the zombie regime.
            PoolSize = int.MaxValue,
            // Executions outlive the 60s Lease — the GC-pause / double-execution regime the safety oracles target.
            MaxExecutionDuration = TimeSpan.FromSeconds(90),

            // Disarms ONLY the bound-dependent liveness oracles (DrainLiveness, ExecuteLiveness); every safety
            // oracle stays armed. See SimulationOptions.RadioactiveMode.
            RadioactiveMode = true,
        };
    }

    /// <summary>A uniform intensity draw within <paramref name="low"/>..<paramref name="high"/>.</summary>
    private static double Band(DeterministicRandom rng, double low, double high)
        => low + rng.NextDouble() * (high - low);
}
