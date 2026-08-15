using BackWave.Observers;
using BackWave.Storage;

namespace BackWave.Tests.Simulation;

/// <summary>
/// Config-space mutation for the coverage-guided explorer (issue 0125, ADR 0025 decision 6-config): perturbs a
/// corpus entry's <see cref="Scenario"/> knobs and routes the result through <see cref="SwarmEnvelope.Confine"/>,
/// so the mutant is in-envelope by construction and can be evaluated by <b>generate</b> (a fresh run through the
/// budget guards) with no risk of a false bug. Each call nudges one to three knobs from the search RNG — toggling
/// a fault axis on/off, redrawing an active intensity within its band, or flipping a structural knob (multi-Queue
/// topology, per-Queue limits, finite pool, or a registered Transition Observer) — and stamps a fresh Seed so
/// generate regenerates a fresh world.
/// </summary>
internal static class ConfigMutator
{
    /// <summary>
    /// Produces an in-envelope mutant of <paramref name="parent"/> under a fresh <paramref name="childSeed"/>,
    /// drawing 1..3 knob perturbations from <paramref name="rng"/>. The result is always <see cref="SwarmEnvelope.Confine"/>d.
    /// </summary>
    public static Scenario Mutate(Scenario parent, ulong childSeed, DeterministicRandom rng)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(rng);

        var s = parent with { Seed = childSeed };
        var perturbations = 1 + rng.Next(3); // 1..3
        for (var i = 0; i < perturbations; i++)
        {
            s = ApplyOne(s, rng);
        }
        return SwarmEnvelope.Confine(s);
    }

    private const int OpCount = 13;

    private static Scenario ApplyOne(Scenario s, DeterministicRandom rng) => rng.Next(OpCount) switch
    {
        0 => s with { CrashProbabilityPerPoll = ToggleProbability(s.CrashProbabilityPerPoll, SwarmEnvelope.CrashBand, rng) },
        1 => s with { HeartbeatLossProbability = ToggleProbability(s.HeartbeatLossProbability, SwarmEnvelope.HeartbeatBand, rng) },
        2 => s with { HandlerFailureProbability = ToggleProbability(s.HandlerFailureProbability, SwarmEnvelope.HandlerBand, rng) },
        3 => s with { StoreFaultProbability = ToggleProbability(s.StoreFaultProbability, SwarmEnvelope.StoreBand, rng) },
        4 => s with { AckLossProbability = ToggleProbability(s.AckLossProbability, SwarmEnvelope.AckLossBand, rng) },
        5 => s with { UnroutableProbability = ToggleProbability(s.UnroutableProbability, SwarmEnvelope.UnroutableBand, rng) },
        6 => s with { IsolationCount = ToggleCount(s.IsolationCount, 1, SwarmEnvelope.IsolationMax, rng) },
        7 => s with { OperatorActionCount = ToggleCount(s.OperatorActionCount, SwarmEnvelope.OperatorMin, SwarmEnvelope.OperatorMax, rng) },
        8 => s with { JobCount = SwarmEnvelope.JobMin + rng.Next(SwarmEnvelope.JobMax - SwarmEnvelope.JobMin + 1) },
        9 => s with { TopologyQueues = ToggleTopology(s.TopologyQueues, rng) },
        10 => s with { ConcurrencyLimits = ToggleLimits(s, rng) },
        11 => s with { PoolSize = TogglePool(s.PoolSize, rng) },
        _ => s with { Observers = ToggleObservers(s.Observers) },
    };

    // A terminal-state Observer — the pure-data registration the swarm toggles as a coverage axis (issue 0201).
    // Matching SwarmConfig.FromSeed's registration; a succeeding sink, so redeliveries under any lease-disrupting
    // fault are tolerated duplicates (never a poison callback). Confine carries it through untouched.
    private static readonly IReadOnlyList<ObserverRegistration> TerminalObserver =
    [
        new ObserverRegistration(
            "swarm-observer",
            new ObserverSubscription(
                [JobState.Succeeded, JobState.DeadLettered, JobState.Cancelled, JobState.Quarantined])),
    ];

    /// <summary>No Observer registered → register the terminal Observer; registered → clear it.</summary>
    private static IReadOnlyList<ObserverRegistration> ToggleObservers(IReadOnlyList<ObserverRegistration> observers)
        => observers.Count > 0 ? [] : TerminalObserver;

    /// <summary>OFF → activate at a random in-band intensity; ON → coin-flip OFF or redraw in-band.</summary>
    private static double ToggleProbability(double value, (double Lo, double Hi) band, DeterministicRandom rng)
    {
        if (value <= 0)
        {
            return Draw(band, rng);
        }
        return rng.NextDouble() < 0.5 ? 0.0 : Draw(band, rng);
    }

    /// <summary>0 → activate at a random count in [min,max]; >0 → coin-flip 0 or redraw.</summary>
    private static int ToggleCount(int value, int min, int max, DeterministicRandom rng)
    {
        if (value <= 0)
        {
            return min + rng.Next(max - min + 1);
        }
        return rng.NextDouble() < 0.5 ? 0 : min + rng.Next(max - min + 1);
    }

    /// <summary>Single-Queue → a 2..3-Queue topology; multi-Queue → coin-flip single-Queue or redraw.</summary>
    private static int ToggleTopology(int topologyQueues, DeterministicRandom rng)
    {
        if (topologyQueues < 2)
        {
            return SwarmEnvelope.TopologyMin + rng.Next(SwarmEnvelope.TopologyMax - SwarmEnvelope.TopologyMin + 1);
        }
        return rng.NextDouble() < 0.5 ? 0 : SwarmEnvelope.TopologyMin + rng.Next(SwarmEnvelope.TopologyMax - SwarmEnvelope.TopologyMin + 1);
    }

    /// <summary>
    /// Toggles per-Queue limits: when none are set, caps every Queue of the current topology at a random band
    /// value; otherwise clears them. <see cref="SwarmEnvelope.Confine"/> drops limits that lack a multi-Queue
    /// topology to live over, so this op is a no-op on a single-Queue Scenario (the topology op enables it first).
    /// </summary>
    private static IReadOnlyDictionary<string, int> ToggleLimits(Scenario s, DeterministicRandom rng)
    {
        if (s.ConcurrencyLimits.Count > 0)
        {
            return new Dictionary<string, int>();
        }
        var queues = Math.Max(s.TopologyQueues, SwarmEnvelope.TopologyMin);
        var limits = new Dictionary<string, int> { ["default"] = DrawCap(rng) };
        for (var q = 1; q < queues; q++)
        {
            limits[$"q{q}"] = DrawCap(rng);
        }
        return limits;
    }

    /// <summary>Unbounded → a finite 1..4 pool; finite → coin-flip unbounded or redraw.</summary>
    private static int TogglePool(int poolSize, DeterministicRandom rng)
    {
        if (poolSize == int.MaxValue)
        {
            return SwarmEnvelope.PoolMin + rng.Next(SwarmEnvelope.PoolMax - SwarmEnvelope.PoolMin + 1);
        }
        return rng.NextDouble() < 0.5 ? int.MaxValue : SwarmEnvelope.PoolMin + rng.Next(SwarmEnvelope.PoolMax - SwarmEnvelope.PoolMin + 1);
    }

    private static double Draw((double Lo, double Hi) band, DeterministicRandom rng)
        => band.Lo + rng.NextDouble() * (band.Hi - band.Lo);

    private static int DrawCap(DeterministicRandom rng)
        => SwarmEnvelope.LimitMin + rng.Next(SwarmEnvelope.LimitMax - SwarmEnvelope.LimitMin + 1);
}
