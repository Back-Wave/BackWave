namespace BackWave.Tests.Simulation;

/// <summary>
/// Which surface a <see cref="CorpusEntry"/> is currently mutated on (issue 0127, ADR 0025 decision 7).
/// Escalation is <b>per-entry</b>, not a global phase flip: an entry begins in <see cref="Config"/> (the
/// explorer — fresh generated worlds grow the corpus) and graduates to <see cref="Trace"/> (the exploiter —
/// a frozen world's interleaving neighborhood is drilled) once it has gone cold enough times.
/// </summary>
internal enum CorpusStage
{
    /// <summary>Config-space exploration: the mutator edits the <see cref="CorpusEntry.Plan"/>'s Scenario and re-generates.</summary>
    Config,

    /// <summary>Trace-level exploitation: the mutator freezes the Scenario and replays an edited Fault Map.</summary>
    Trace,
}

/// <summary>
/// One entry in the coverage-guided explorer's <see cref="Corpus"/> (issue 0125, ADR 0025 decision 8): a clean,
/// coverage-advancing <see cref="Plan"/> the mutator climbs from, plus a per-entry <b>energy</b> — a decaying,
/// productivity-weighted scalar that biases the scheduler. New entries start hot (<see cref="InitialEnergy"/>);
/// energy <see cref="Decay">decays</see> each time a mutation of this entry goes cold and
/// <see cref="Replenish">replenishes</see> when one produces a coverage-advancing child, so the scheduler spends
/// its time on entries that are still paying out.
///
/// <para>An entry also carries a <see cref="Stage"/> (issue 0127, ADR 0025 decision 7). It starts in
/// <see cref="CorpusStage.Config"/>; a per-entry <b>cold-counter</b> (<see cref="ColdStreak"/>, the consecutive
/// config mutants that advanced nothing) escalates it to <see cref="CorpusStage.Trace"/> once it crosses
/// <see cref="EscalationThreshold"/>. A productive child resets the counter. Escalation is one-way and per-entry,
/// so different entries can be on different surfaces at the same time.</para>
/// </summary>
internal sealed class CorpusEntry
{
    public const double InitialEnergy = 1.0;
    private const double DecayFactor = 0.8;
    private const double ReplenishAmount = 0.5;

    /// <summary>A small positive floor so a cold entry keeps a non-zero weighted-random weight (never starves).</summary>
    public const double EnergyFloor = 0.01;

    /// <summary>Consecutive cold config mutants that escalate this entry from <see cref="CorpusStage.Config"/> to Trace.</summary>
    public const int EscalationThreshold = 5;

    private int _coldStreak;

    public CorpusEntry(Plan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        Plan = plan;
    }

    /// <summary>The clean, coverage-advancing Plan this entry mutates from.</summary>
    public Plan Plan { get; }

    /// <summary>The entry's current energy — the scheduler's weighted-random weight.</summary>
    public double Energy { get; private set; } = InitialEnergy;

    /// <summary>The surface this entry is mutated on — starts at <see cref="CorpusStage.Config"/>, escalates once.</summary>
    public CorpusStage Stage { get; private set; } = CorpusStage.Config;

    /// <summary>Consecutive cold mutations of this entry — the escalation cold-counter (reset by a productive child).</summary>
    public int ColdStreak => _coldStreak;

    /// <summary>
    /// Decays energy after a cold mutation (multiplicative, floored so it never reaches zero) and advances the
    /// escalation cold-counter: a <see cref="CorpusStage.Config"/> entry whose cold streak reaches
    /// <see cref="EscalationThreshold"/> graduates to <see cref="CorpusStage.Trace"/> (one-way, per-entry).
    /// </summary>
    public void Decay()
    {
        Energy = Math.Max(EnergyFloor, Energy * DecayFactor);
        if (Stage == CorpusStage.Config && ++_coldStreak >= EscalationThreshold)
        {
            Stage = CorpusStage.Trace;
        }
    }

    /// <summary>
    /// Replenishes energy after a productive (coverage-advancing) child, capped at the hot start, and resets the
    /// escalation cold-counter — a paying-out entry is kept on its current surface rather than pushed to escalate.
    /// </summary>
    public void Replenish()
    {
        Energy = Math.Min(InitialEnergy, Energy + ReplenishAmount);
        _coldStreak = 0;
    }
}

/// <summary>
/// The coverage-guided explorer's in-memory corpus (issue 0125, ADR 0025 decision 8): the set of clean,
/// coverage-advancing <see cref="Plan"/>s the mutator grows worlds from. No eviction. Cross-session persistence
/// is layered above it by <see cref="GuidedCorpusStore"/> (save the Plans, reseed via
/// <see cref="CoverageGuidedSwarm.Seed"/>), so a corpus survives across cycles. Distinct from
/// <see cref="PlanStore"/>, which is the bug sink (failing Plans only) — the corpus never holds a failing Plan
/// and the store never holds a clean one.
///
/// <para>The scheduler picks the next entry to mutate by <b>weighted-random energy</b>: an entry's chance of
/// being chosen is its <see cref="CorpusEntry.Energy"/> over the corpus total, so productive entries are mined
/// more and cold ones fade (without ever fully starving — energy has a positive floor).</para>
/// </summary>
internal sealed class Corpus
{
    private readonly List<CorpusEntry> _entries = [];

    public int Count => _entries.Count;
    public bool IsEmpty => _entries.Count == 0;
    public IReadOnlyList<CorpusEntry> Entries => _entries;

    /// <summary>Adds a clean, coverage-advancing entry to the corpus.</summary>
    public void Add(CorpusEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _entries.Add(entry);
    }

    /// <summary>
    /// Picks the next entry to mutate by weighted-random energy. Throws if the corpus is empty (the caller
    /// bootstraps a fresh generated world while the corpus is empty, so this is only reached once it is not).
    /// </summary>
    public CorpusEntry PickByEnergy(DeterministicRandom rng)
    {
        ArgumentNullException.ThrowIfNull(rng);
        if (_entries.Count == 0)
        {
            throw new InvalidOperationException("Corpus is empty — nothing to pick.");
        }

        var total = 0.0;
        foreach (var entry in _entries)
        {
            total += entry.Energy;
        }

        var target = rng.NextDouble() * total;
        var cumulative = 0.0;
        foreach (var entry in _entries)
        {
            cumulative += entry.Energy;
            if (target < cumulative)
            {
                return entry;
            }
        }
        return _entries[^1]; // floating-point tail guard
    }
}
