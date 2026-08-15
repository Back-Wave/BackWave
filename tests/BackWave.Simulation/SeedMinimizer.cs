namespace BackWave.Tests.Simulation;

/// <summary>
/// The Seed Minimizer (issue 0088, PRD 0004, ADR 0018): shrinks a failing <see cref="Plan"/> to the
/// smallest Fault Map that still trips the SAME invariant identity, with the <see cref="Scenario"/> frozen.
///
/// <para><b>Exact fault-removal ddmin.</b> The primary pass is delta-debugging over the Fault Map: it tries
/// removing subsets of fault entries, replays each candidate, and accepts it ONLY if the same
/// <see cref="InvariantId"/> re-trips. Removing a fault only ever <i>calms</i> a run (never makes it angrier
/// and never triggers a veto), so this pass is exact and bug-preserving. The original failing Plan is the
/// retained floor: <see cref="Minimize"/> returns, worst case, an equivalently-reproducing Plan, never a
/// non-reproducing one.</para>
///
/// <para><b>Operator axis excluded.</b> Operator type/target draw from a shared <c>_opRng</c> at apply-time,
/// so removing one operator entry shifts every later operator action (ADR 0018 ordinal-drift). Operator
/// entries are never proposed for removal — they are carried through untouched and still replay from the
/// map. Every other axis (store/crash/heartbeat/handler/ackloss/unroutable/isolation) is cleanly removable.</para>
///
/// <para><b>Realized map persisted.</b> Replay routes through the Simulator's N−1 isolation budget guard, so
/// an illegal requested fault is vetoed automatically; the minimizer persists the <i>realized</i> Fault Map
/// (what was actually consulted+applied), not the requested one. Removal-only ddmin never triggers a veto;
/// the realized map of the calmer surviving run is exactly the surviving consulted entries.</para>
/// </summary>
internal static class SeedMinimizer
{
    /// <summary>The one axis excluded from removal — its actions ordinal-drift if any entry is dropped.</summary>
    private const string OperatorAxis = "operator";

    /// <summary>
    /// Exact fault-removal ddmin: returns the failing Plan with the smallest Fault Map (Scenario frozen) that
    /// still trips <paramref name="target"/>. Operator entries are carried through; the input is the floor.
    /// </summary>
    public static Plan Minimize(Plan plan, InvariantId target)
    {
        ArgumentNullException.ThrowIfNull(plan);

        // Partition the captured map: operator entries are pinned (never removed), the rest are candidates.
        var pinned = plan.FaultMap.Where(e => e.Axis == OperatorAxis).ToList();
        var removable = plan.FaultMap.Where(e => e.Axis != OperatorAxis).ToList();

        // ddmin over the removable entries: shrink the granularity from coarse halves down to singletons,
        // removing the complement of each chunk and keeping any removal that still reproduces `target`.
        var n = 2;
        while (removable.Count >= 2)
        {
            var chunkSize = Math.Max(1, removable.Count / n);
            var removedSomething = false;

            for (var start = 0; start < removable.Count; start += chunkSize)
            {
                // Candidate = everything EXCEPT this chunk (plus the pinned operator entries, always present).
                var kept = new List<FaultEntry>(removable.Count);
                kept.AddRange(removable.Take(start));
                kept.AddRange(removable.Skip(start + chunkSize));

                if (kept.Count == removable.Count)
                {
                    continue; // chunk was empty (off the end) — nothing to remove
                }

                if (Reproduces(plan.Scenario, Merge(pinned, kept), target, out _))
                {
                    // The smaller REQUESTED map still trips the same invariant — shrink the working set to it.
                    // ddmin must shrink the requested set MONOTONICALLY; we must NOT re-derive it from the
                    // realized map, because removing a fault calms the run and can let it progress further and
                    // consult MORE fault sites, so the realized map can grow back and the search would never
                    // converge. Operator entries are pinned and never change. (kept.Count < removable.Count
                    // always here — empty chunks are skipped, so each acceptance strictly shrinks the set.)
                    removable = kept;
                    n = Math.Max(n - 1, 2);
                    removedSomething = true;
                    break;
                }
            }

            if (removedSomething)
            {
                continue;
            }

            if (n >= removable.Count)
            {
                break; // already at singleton granularity with no further reduction — converged
            }
            n = Math.Min(removable.Count, n * 2);
        }

        var minimizedMap = Merge(pinned, removable);

        // Floor guarantee: if for any reason the shrunk map does not reproduce (it always should, since the
        // last accepted candidate did), fall back to the original input unchanged. Never return a
        // non-reproducing Plan.
        if (!Reproduces(plan.Scenario, minimizedMap, target, out _))
        {
            return plan;
        }

        // Persist the minimized REQUESTED map. Removal never triggers a veto (ADR 0018), so requested ==
        // realized in applied faults; persisting the requested map keeps the result 1-minimal and avoids the
        // realized map's consulted-but-missed padding (every replay consultation is recorded, faulting or not).
        // The realized-on-replay machinery stays for the fuzzer's veto path (issue 0091), where requested and
        // realized genuinely diverge.
        return plan with { FaultMap = minimizedMap, Failure = plan.Failure };
    }

    /// <summary>
    /// Opt-in, deliberately-coarse Scenario-scalar shrink (ADR 0018): tries fewer jobs/nodes, replaying each
    /// candidate, and returns a SECOND, clearly-labelled artifact — it NEVER replaces the exact minimized
    /// Plan. Because shrinking the Scenario rebuilds the world from the main RNG, it may reproduce a SIBLING
    /// run rather than the exact one; the caller treats it as a coarser hint, not the canonical repro. Returns
    /// null when no smaller Scenario reproduces <paramref name="target"/>.
    /// </summary>
    public static ScenarioShrinkArtifact? MinimizeScenario(Plan exactMinimized, InvariantId target)
    {
        ArgumentNullException.ThrowIfNull(exactMinimized);

        var scenario = exactMinimized.Scenario;
        var bestMap = exactMinimized.FaultMap;
        var shrunk = false;

        // Coarse linear ratchet on JobCount, then NodeCount — halve while the smaller world still trips the
        // same invariant. No ddmin subtlety; this pass is explicitly a rough sibling-finder.
        scenario = ShrinkScalar(scenario, target, ref bestMap, ref shrunk,
            s => s.JobCount, (s, v) => s with { JobCount = v }, floor: 1);
        scenario = ShrinkScalar(scenario, target, ref bestMap, ref shrunk,
            s => s.NodeCount, (s, v) => s with { NodeCount = v }, floor: 1);

        if (!shrunk)
        {
            return null;
        }

        var sibling = new Plan
        {
            Scenario = scenario,
            FaultMap = bestMap,
            Failure = exactMinimized.Failure,
        };
        return new ScenarioShrinkArtifact(sibling);
    }

    private static Scenario ShrinkScalar(
        Scenario scenario,
        InvariantId target,
        ref IReadOnlyList<FaultEntry> bestMap,
        ref bool shrunk,
        Func<Scenario, int> get,
        Func<Scenario, int, Scenario> set,
        int floor)
    {
        var value = get(scenario);
        while (value > floor)
        {
            var candidate = Math.Max(floor, value / 2);
            var trialScenario = set(scenario, candidate);
            if (Reproduces(trialScenario, bestMap, target, out _))
            {
                // Keep replaying the SAME exact-minimized fault map against the shrinking world (don't adopt
                // the realized map — it's the padded full-consultation map). The sibling artifact stays small.
                scenario = trialScenario;
                shrunk = true;
                value = candidate;
            }
            else
            {
                break;
            }
        }
        return scenario;
    }

    /// <summary>Operator entries first (pinned), then the kept removable entries — preserves call-order shape.</summary>
    private static List<FaultEntry> Merge(IReadOnlyList<FaultEntry> pinned, IReadOnlyList<FaultEntry> kept)
    {
        var merged = new List<FaultEntry>(pinned.Count + kept.Count);
        merged.AddRange(pinned);
        merged.AddRange(kept);
        return merged;
    }

    /// <summary>
    /// Replays <paramref name="candidateMap"/> against the frozen <paramref name="scenario"/> and reports
    /// whether the run trips the SAME invariant identity. A reproduction = <see cref="Run"/> throws
    /// <see cref="SimulationInvariantException"/> whose <see cref="SimulationInvariantException.InvariantId"/>
    /// equals <paramref name="target"/>. Anything else (no throw, or a different invariant) is a rejection.
    /// On a reproduction, <paramref name="realized"/> is the realized Fault Map (post-veto) to persist.
    /// </summary>
    private static bool Reproduces(
        Scenario scenario,
        IReadOnlyList<FaultEntry> candidateMap,
        InvariantId target,
        out IReadOnlyList<FaultEntry> realized)
    {
        var sim = new Simulator(scenario.ToOptions(), FaultPlan.Replay(scenario.Seed, candidateMap));
        try
        {
            sim.Run();
        }
        catch (SimulationInvariantException ex) when (ex.InvariantId == target)
        {
            realized = sim.RealizedFaultMap;
            return true;
        }
        catch (SimulationInvariantException)
        {
            realized = candidateMap; // a DIFFERENT invariant tripped — not the same bug, reject
            return false;
        }

        realized = candidateMap; // no throw — the candidate calmed the run past failure, reject
        return false;
    }
}

/// <summary>
/// The second, clearly-labelled artifact from <see cref="SeedMinimizer.MinimizeScenario"/> (ADR 0018): a
/// coarsely scalar-shrunk Plan that reproduces the same invariant but may be a SIBLING run (a smaller world
/// rebuilt from the main RNG), NOT the exact minimized repro. Held separately so it can never overwrite the
/// canonical exact Plan.
/// </summary>
internal sealed record ScenarioShrinkArtifact(Plan SiblingPlan)
{
    /// <summary>True — this artifact is a coarse sibling, not the exact minimized repro (ADR 0018).</summary>
    public bool IsCoarseSibling => true;
}
