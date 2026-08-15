namespace BackWave.Tests.Simulation;

/// <summary>
/// Trace-level mutation for the exploiter (issue 0126, ADR 0025 decisions 6/9-partial): freezes a corpus
/// entry's <see cref="Scenario"/> (the world the main RNG regenerates is unchanged — same Seed) and edits its
/// <b>Fault Map</b> in place, drilling the entry's interleaving neighborhood. Every edit is evaluated by
/// <b>replay</b> (<see cref="FaultPlan.Replay"/>), so an edit that requests an illegal fault is vetoed by the
/// Simulator's budget guards on the way through and the <i>realized</i> Plan stays in-envelope — there is no
/// generate-mode false-bug risk here, unlike <see cref="ConfigMutator"/>.
///
/// <para>The four specced operators collapse to two (ADR 0025 decision 6). Because <see cref="FaultPlan"/>
/// records every consultation — faulting or not — the realized map already holds every reachable address as an
/// editable entry, so:</para>
/// <list type="bullet">
/// <item><b><see cref="Flip"/></b> is the workhorse: invert 1–4 recorded outcomes (<c>false→true</c> injects,
/// <c>true→false</c> calms), with <b>blind-biased</b> selection — a false entry on an axis the run already
/// exercises is weighted up, so injection concentrates where faults are already in play, with no curated
/// tuple→axis map. <c>add-at-uncovered</c> is just steered flip-to-true; <c>remove</c> is dropped (it is
/// flip-to-false and the minimizer's job). The <c>operator</c> axis is excluded (its actions ordinal-drift if
/// any decision changes — same reason <see cref="SeedMinimizer"/> pins it).</item>
/// <item><b><see cref="Splice"/></b> is uniform crossover, restricted to a <b>same-frozen-<see cref="Scenario"/>
/// family</b>: for each address the two parents share, take one parent's outcome by coin flip. Cross-Scenario
/// splice is dead (disjoint address spaces) and returns null.</item>
/// </list>
/// </summary>
internal static class TraceMutator
{
    /// <summary>The axis excluded from flips — its actions ordinal-drift if any decision changes (ADR 0018).</summary>
    private const string OperatorAxis = "operator";

    /// <summary>Weight multiplier for a steered injection target (a false entry on an already-exercised axis).</summary>
    private const int SteeredWeight = 8;

    /// <summary>
    /// Produces a flip mutant of <paramref name="parent"/>: the same frozen <see cref="Scenario"/> with 1–4 of
    /// its Fault-Map outcomes inverted, drawn from <paramref name="rng"/>. Selection is blind-biased toward
    /// false entries on axes the run already exercises (injection where faults are in play); the
    /// <c>operator</c> axis is never touched. The returned Plan carries the <b>requested</b> map — the swarm
    /// evaluates it by replay, and the budget-guarded realized map is the banked artifact.
    /// </summary>
    public static Plan Flip(Plan parent, DeterministicRandom rng)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(rng);

        var entries = parent.FaultMap.ToArray();

        // Candidate indices = everything except the pinned operator axis.
        var candidates = new List<int>(entries.Length);
        for (var i = 0; i < entries.Length; i++)
        {
            if (entries[i].Axis != OperatorAxis)
            {
                candidates.Add(i);
            }
        }
        if (candidates.Count == 0)
        {
            return parent with { FaultMap = entries, Failure = null };
        }

        // Axes the run already exercises = axes (excluding operator) with at least one active (true) decision.
        // Computed once off the parent so the bias means "axes the entry already exercises" (ADR 0025).
        var exercised = entries
            .Where(e => e.Fault && e.Axis != OperatorAxis)
            .Select(e => e.Axis)
            .ToHashSet(StringComparer.Ordinal);

        // 1..4 stacked flips, capped at the candidate count; drawn WITHOUT replacement so each flip lands a
        // distinct outcome (a re-picked index would cancel itself — "stacks N flips" means N net changes).
        var flips = Math.Min(1 + rng.Next(4), candidates.Count);
        for (var f = 0; f < flips; f++)
        {
            var position = PickBiasedPosition(entries, candidates, exercised, rng);
            var index = candidates[position];
            candidates.RemoveAt(position);
            entries[index] = entries[index] with { Fault = !entries[index].Fault };
        }

        return parent with { FaultMap = entries, Failure = null };
    }

    /// <summary>
    /// Weighted-random POSITION into <paramref name="candidates"/> (so the caller can draw without replacement):
    /// a false entry on an already-exercised axis is the steered injection target (heavy weight); every other
    /// candidate keeps a base weight of one, so calming (true→false) and the occasional off-axis probe stay
    /// possible. Blind — no tuple→axis map.
    /// </summary>
    private static int PickBiasedPosition(
        FaultEntry[] entries, List<int> candidates, HashSet<string> exercised, DeterministicRandom rng)
    {
        var total = 0.0;
        foreach (var index in candidates)
        {
            total += WeightOf(entries[index], exercised);
        }

        var target = rng.NextDouble() * total;
        var cumulative = 0.0;
        for (var position = 0; position < candidates.Count; position++)
        {
            cumulative += WeightOf(entries[candidates[position]], exercised);
            if (target < cumulative)
            {
                return position;
            }
        }
        return candidates.Count - 1; // floating-point tail guard
    }

    private static double WeightOf(FaultEntry entry, HashSet<string> exercised)
        => !entry.Fault && exercised.Contains(entry.Axis) ? SteeredWeight : 1.0;

    /// <summary>
    /// Uniform crossover of two <b>same-frozen-<see cref="Scenario"/></b> parents: returns <paramref name="a"/>'s
    /// Fault-Map shape with each address it shares with <paramref name="b"/> resolved to one parent's outcome by
    /// a fair coin. Returns null when the two parents are not the same family (disjoint address spaces make
    /// cross-Scenario splice dead — ADR 0025 decision 6). The result carries the requested map for replay.
    /// </summary>
    public static Plan? Splice(Plan a, Plan b, DeterministicRandom rng)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        ArgumentNullException.ThrowIfNull(rng);

        // Same-frozen-Scenario family only: identical world (Seed + every knob). Scenario is a record, so this
        // is structural equality. A mismatch is a dead splice.
        if (a.Scenario != b.Scenario)
        {
            return null;
        }

        var bByAddress = new Dictionary<(string Axis, string Id, long Ordinal), bool>();
        foreach (var entry in b.FaultMap)
        {
            bByAddress[(entry.Axis, entry.Id, entry.Ordinal)] = entry.Fault;
        }

        var crossed = new List<FaultEntry>(a.FaultMap.Count);
        foreach (var entry in a.FaultMap)
        {
            var address = (entry.Axis, entry.Id, entry.Ordinal);
            // Shared address → take a's or b's outcome by coin flip; a-only address → keep a's.
            var fault = bByAddress.TryGetValue(address, out var bFault) && rng.NextDouble() < 0.5
                ? bFault
                : entry.Fault;
            crossed.Add(entry with { Fault = fault });
        }

        return a with { FaultMap = crossed, Failure = null };
    }
}
