namespace BackWave.Tests.Simulation;

/// <summary>
/// The exploiter's gradient (issue 0126, ADR 0025 decisions 4/5): an AFL-style "new tuple seen" novelty
/// signal over <b>co-occurring-Situation interaction tuples</b>, derived POST-HOC from a run's
/// <see cref="Situation"/> hit-set (no hot-path instrumentation — 0090's discipline). A tuple is an
/// unordered PAIR of Situations a single run exercised together; trace-level mutation reorders faults within
/// a frozen world, so which Situations <i>co-occur</i> shifts even when the endpoint-shaped edge + single-
/// Situation coverage does not — exactly the interleaving sensitivity the config-space gradient is blind to.
///
/// <para><b>Denominator-free by design.</b> Which co-occurrences are <i>possible</i> is state-dependent —
/// the same reason ADR 0018 rejected a static validity re-checker — so there is no complement and no
/// fraction, only a growing set of pairs actually seen. This is deliberately kept OUT of the published 0090
/// <see cref="CoverageReport"/> (which stays edges + Situations, denominator-based): the report steers
/// config-space, this set steers trace-level.</para>
///
/// <para>Thread-safe: <see cref="Union"/> guards its accumulator so the future multi-threaded swarm
/// (issue 0128) can fold concurrently, mirroring <see cref="CoverageTracker"/>.</para>
/// </summary>
internal sealed class InteractionTuples
{
    private readonly object _gate = new();
    private readonly HashSet<(Situation Lo, Situation Hi)> _seen = [];

    /// <summary>Distinct co-occurring-Situation pairs seen so far (the size of the novelty frontier).</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _seen.Count;
            }
        }
    }

    /// <summary>
    /// Folds a run's <paramref name="situations"/> hit-set into the running tuple set and returns whether a
    /// <b>never-seen pair</b> was added — the exploiter retains a clean trace mutant exactly when this is true.
    /// A run hitting fewer than two Situations contributes no pair (a lone-Situation novelty is the config-space
    /// gradient's concern, not this one). Pairs are canonicalised low→high so ordering never doubles a tuple.
    /// </summary>
    public bool Union(IReadOnlySet<Situation> situations)
    {
        ArgumentNullException.ThrowIfNull(situations);
        if (situations.Count < 2)
        {
            return false;
        }

        // Sort by the enum's integer value so each unordered pair has one canonical (Lo, Hi) key.
        var ordered = situations.OrderBy(s => (int)s).ToArray();
        lock (_gate)
        {
            var grew = false;
            for (var i = 0; i < ordered.Length; i++)
            {
                for (var j = i + 1; j < ordered.Length; j++)
                {
                    grew |= _seen.Add((ordered[i], ordered[j]));
                }
            }
            return grew;
        }
    }
}
