using BackWave.Storage;

namespace BackWave.Tests.Simulation;

/// <summary>
/// The curated set of named <b>Situations</b> the Coverage tracker (issue 0090, ADR 0018) watches for —
/// an interesting product/fault configuration a run may or may not exercise. Like <see cref="InvariantId"/>,
/// this enum IS the registry of Situations; its companion is <see cref="CoverageTracker.SituationPredicates"/>,
/// the single map from each member to its "was this hit?" predicate over a <see cref="SimulationResult"/>.
///
/// <para><b>Extension point (the ONE place to add a Situation):</b> add a member here, then add its predicate
/// to <see cref="CoverageTracker.SituationPredicates"/>. Nothing else changes — the per-run hit-set, the union,
/// and the never-hit complement in the report are all derived from this registry. The tracker has a startup
/// guard asserting the two stay in lock-step, so a member with no predicate cannot ship.</para>
///
/// <para>Situations are an OPEN registry by design: a new fault axis or oracle is expected to contribute its
/// own Situations here. Where a Situation needs data not on <see cref="SimulationResult"/>, prefer deriving it
/// post-hoc — tally it in the oracle pass off state an existing oracle already reads (see <c>LimitSaturated</c>/
/// <c>BackpressureIdle</c>, lit this way in issue 0124) rather than threading new state through the Simulator's
/// hot path — that keeps coverage post-hoc and the determinism battery untouched.</para>
/// </summary>
internal enum Situation
{
    /// <summary>A stale outcome write was fenced at the Storage Contract boundary (Effect-Once held).</summary>
    StaleOutcomeFenced,

    /// <summary>A Lease lapsed under an isolated/lost owner and was re-homed to a survivor (migration fired).</summary>
    MigrationFired,

    /// <summary>A job reached Quarantined via a dispatch-side Unroutable outcome (issue 0073).</summary>
    QuarantineReached,

    /// <summary>An outcome write committed but lost its ack, forcing a fenced retry (issue 0070).</summary>
    AckLossRetry,

    /// <summary>A node-isolation episode began under the N−1 budget while work was in flight (issue 0068).</summary>
    IsolationDuringExecuting,

    /// <summary>A node crashed and the cluster recovered its in-flight work.</summary>
    CrashRecovery,

    /// <summary>A node crashed while terminal outcomes were buffered-but-unflushed, dropping them (ADR 0035 batch coalescing).</summary>
    OutcomeBufferDroppedOnCrash,

    /// <summary>An Operator Requeue moved a terminal job back to Scheduled (Attempt reset to 0).</summary>
    OperatorRequeue,

    /// <summary>A cooperative cancel round-trip completed (ExecutionCancelled → ReportOutcome(Cancelled)).</summary>
    CooperativeCancel,

    /// <summary>A Recurring Schedule minted at least one instance during the run (issue 0057).</summary>
    ScheduleMinted,

    /// <summary>A job exhausted its attempt budget and dead-lettered.</summary>
    DeadLetterReached,

    /// <summary>An Observer redelivered a transition: total deliveries exceeded unique ones (issue 0076).</summary>
    ObserverRedeliveryFired,

    /// <summary>An Observer delivered a transition during a node-isolation episode (issue 0068 × §0076).</summary>
    ObserverDeliveryUnderIsolation,

    // ── Limit / backpressure saturation Situations (issue 0124) ─────────────────────────────────────
    //
    // Formerly constant-false "deferred" predicates the single-Queue 3a envelope could never reach. 0124
    // widens config-space (SwarmConfig now reaches multi-Queue topologies + ConcurrencyLimits + a finite
    // Backpressure pool), which makes both reachable, and lights them from real SimulationResult counters
    // (LimitSaturations / BackpressureIdleTicks). Those counters are tallied in the oracle pass off counts
    // the I3 and per-node-cap oracles already compute every step — post-hoc, no hot-path instrumentation,
    // determinism battery byte-identical. No constant-false predicate remains in the registry.

    /// <summary>A Concurrency-Limited Queue held every slot at once — the limit binds (issue 0124).</summary>
    LimitSaturated,

    /// <summary>A node's finite Backpressure pool was full, so its next claim is blocked purely by backpressure (issue 0124).</summary>
    BackpressureIdle,
}

/// <summary>
/// A single run's coverage hit-set (issue 0090): the legal transition edges its jobs traversed and the named
/// <see cref="Situation"/>s it exercised. Derived POST-HOC from <see cref="SimulationResult"/> — no Simulator
/// instrumentation — so it never perturbs the determinism battery.
/// </summary>
internal sealed record CoverageHits(
    IReadOnlySet<(JobState From, JobState To)> Edges,
    IReadOnlySet<Situation> Situations);

/// <summary>
/// The Coverage report (issue 0090): the COMPLEMENT after N unioned runs — the legal edges and registered
/// Situations that were NEVER reached — plus the hit fractions. This is the 3b guided fuzzer's target list.
/// It is a report artifact only: nothing here gates a build (an async runner would flake a hard gate, ADR 0018).
/// </summary>
internal sealed record CoverageReport(
    int LegalEdgeCount,
    int EdgesHit,
    IReadOnlyList<(JobState From, JobState To)> NeverReachedEdges,
    int SituationCount,
    int SituationsHit,
    IReadOnlyList<Situation> NeverHitSituations)
{
    public double EdgeCoverage => LegalEdgeCount == 0 ? 1.0 : (double)EdgesHit / LegalEdgeCount;
    public double SituationCoverage => SituationCount == 0 ? 1.0 : (double)SituationsHit / SituationCount;

    /// <summary>A one-block console rendering of the complement and the two fractions.</summary>
    public override string ToString()
    {
        var edges = NeverReachedEdges.Count == 0
            ? "    (all legal edges reached)"
            : string.Join("\n", NeverReachedEdges.Select(e => $"    {e.From} -> {e.To}"));
        var situations = NeverHitSituations.Count == 0
            ? "    (all Situations hit)"
            : string.Join("\n", NeverHitSituations.Select(s => $"    {s}"));
        return
            $"Coverage report\n" +
            $"  edges:      {EdgesHit}/{LegalEdgeCount} ({EdgeCoverage:P0})\n" +
            $"  situations: {SituationsHit}/{SituationCount} ({SituationCoverage:P0})\n" +
            $"  never-reached edges:\n{edges}\n" +
            $"  never-hit situations:\n{situations}";
    }
}

/// <summary>
/// The Coverage tracker (issue 0090, ADR 0018): records the transition edges and named <see cref="Situation"/>s
/// a run exercises, unions them across many runs, and renders the never-reached COMPLEMENT as a
/// <see cref="CoverageReport"/>. Both signals are derived post-hoc from <see cref="SimulationResult"/> — edges
/// from consecutive states in each job's <see cref="SimulationResult.FinalTransitions"/> timeline, Situations
/// from the result's counters via <see cref="SituationPredicates"/> — so the tracker adds no hot-path cost.
///
/// <para>Edge coverage is measured against <see cref="Simulator.LegalTransitionEdges"/> as the denominator (the
/// 11 legal edges), not the universe of all <see cref="JobState"/> pairs.</para>
///
/// <para>Thread-safe: <see cref="Union"/> guards its accumulators with a lock so the VOPR Runner's workers can
/// fold their per-run hits in concurrently.</para>
/// </summary>
internal sealed class CoverageTracker
{
    /// <summary>
    /// The ONE registration point: each <see cref="Situation"/> mapped to its "was this hit?" predicate over a
    /// <see cref="SimulationResult"/>. Adding a Situation is exactly two lines — a member on the enum and an
    /// entry here. The static ctor below asserts every enum member appears, so an unmapped member fails fast.
    /// </summary>
    internal static readonly IReadOnlyDictionary<Situation, Func<SimulationResult, bool>> SituationPredicates =
        new Dictionary<Situation, Func<SimulationResult, bool>>
        {
            [Situation.StaleOutcomeFenced] = r => r.StaleOutcomes > 0,
            [Situation.MigrationFired] = r => r.LeasesExpired > 0,
            [Situation.QuarantineReached] = r => r.Quarantined > 0,
            [Situation.AckLossRetry] = r => r.AckLosses > 0,
            [Situation.IsolationDuringExecuting] = r => r.Isolations > 0,
            [Situation.CrashRecovery] = r => r.Crashes > 0,
            [Situation.OutcomeBufferDroppedOnCrash] = r => r.OutcomeBufferDropped > 0,
            [Situation.OperatorRequeue] = r => r.OperatorRequeues > 0,
            [Situation.CooperativeCancel] = r => r.CooperativeCancels > 0,
            [Situation.ScheduleMinted] = r => r.MintedJobs.Count > 0,
            [Situation.DeadLetterReached] = r => r.DeadLettered > 0,
            [Situation.ObserverRedeliveryFired] = r => r.ObserverDeliveries.Values.Any(d => d.Total > d.Unique),
            [Situation.ObserverDeliveryUnderIsolation] =
                r => r.Isolations > 0 && r.ObserverDeliveries.Values.Any(d => d.Total > 0),

            // Limit / backpressure saturation (issue 0124): lit from real SimulationResult counters now that
            // config-space reaches multi-Queue topologies, ConcurrencyLimits, and a finite Backpressure pool.
            [Situation.LimitSaturated] = r => r.LimitSaturations > 0,
            [Situation.BackpressureIdle] = r => r.BackpressureIdleTicks > 0,
        };

    static CoverageTracker()
    {
        // Lock-step guard: the enum (the registry) and the predicate map must agree, so a new Situation
        // member cannot ship without its single registration. Cheap; runs once per process.
        var missing = Enum.GetValues<Situation>().Where(s => !SituationPredicates.ContainsKey(s)).ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"Situation(s) have no predicate in {nameof(SituationPredicates)}: {string.Join(", ", missing)}. " +
                "Add an entry there — it is the single Situation registration point (issue 0090).");
        }
    }

    private readonly object _gate = new();
    private readonly HashSet<(JobState From, JobState To)> _edges = [];
    private readonly HashSet<Situation> _situations = [];

    /// <summary>The legal-edge denominator: the single source of truth on <see cref="Simulator"/>, never duplicated.</summary>
    internal static IReadOnlyCollection<(JobState From, JobState To)> LegalEdges => Simulator.LegalTransitionEdges;

    /// <summary>The full registered Situation set (the Situation denominator).</summary>
    internal static IReadOnlyCollection<Situation> RegisteredSituations => Enum.GetValues<Situation>();

    /// <summary>
    /// Computes a single run's hit-set POST-HOC: the legal edges its job timelines traversed (consecutive
    /// state pairs in <see cref="SimulationResult.FinalTransitions"/>, filtered to the legal set) and the
    /// Situations whose predicate the result satisfies. A traversed pair outside the legal set is dropped —
    /// the legal-transition oracle owns that failure; coverage only measures the legal denominator.
    /// </summary>
    internal static CoverageHits HitsOf(SimulationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var legal = Simulator.LegalTransitionEdges.ToHashSet();

        var edges = new HashSet<(JobState From, JobState To)>();
        foreach (var (_, timeline) in result.FinalTransitions)
        {
            for (var i = 1; i < timeline.Count; i++)
            {
                var edge = (timeline[i - 1].State, timeline[i].State);
                if (legal.Contains(edge))
                {
                    edges.Add(edge);
                }
            }
        }

        var situations = new HashSet<Situation>();
        foreach (var (situation, predicate) in SituationPredicates)
        {
            if (predicate(result))
            {
                situations.Add(situation);
            }
        }

        return new CoverageHits(edges, situations);
    }

    /// <summary>
    /// Folds one run's hits (or a pre-computed <see cref="CoverageHits"/>) into the running union and returns
    /// whether the union GREW — i.e. whether this run advanced coverage (issue 0125's retention signal). Thread-safe.
    /// </summary>
    internal bool Union(SimulationResult result) => Union(HitsOf(result));

    /// <summary>
    /// Folds a pre-computed hit-set into the running union; returns true iff a new edge or Situation was added
    /// (the run advanced coverage). The coverage-guided explorer (issue 0125) retains a clean mutant to the
    /// corpus exactly when this is true. Thread-safe.
    /// </summary>
    internal bool Union(CoverageHits hits)
    {
        ArgumentNullException.ThrowIfNull(hits);
        lock (_gate)
        {
            var before = _edges.Count + _situations.Count;
            _edges.UnionWith(hits.Edges);
            _situations.UnionWith(hits.Situations);
            return _edges.Count + _situations.Count > before;
        }
    }

    /// <summary>
    /// Renders the union as a <see cref="CoverageReport"/>: the COMPLEMENT (legal edges and registered
    /// Situations never reached) plus the two hit fractions. Report only — nothing here gates a build.
    /// </summary>
    internal CoverageReport Report()
    {
        var legal = Simulator.LegalTransitionEdges;
        var allSituations = Enum.GetValues<Situation>();

        lock (_gate)
        {
            var neverEdges = legal.Where(e => !_edges.Contains(e)).ToList();
            var neverSituations = allSituations.Where(s => !_situations.Contains(s)).ToList();
            return new CoverageReport(
                LegalEdgeCount: legal.Count,
                EdgesHit: _edges.Count,
                NeverReachedEdges: neverEdges,
                SituationCount: allSituations.Length,
                SituationsHit: _situations.Count,
                NeverHitSituations: neverSituations);
        }
    }
}
