namespace BackWave.Tests.Simulation;

/// <summary>Whether a <see cref="FaultPlan"/> is recording fresh fault decisions or replaying recorded ones.</summary>
internal enum FaultMode
{
    /// <summary>Draw each fault decision from its axis stream and record it (the discovery path).</summary>
    Generate,

    /// <summary>Look each fault decision up in the recorded Fault Map; a miss is no-fault (the replay path).</summary>
    Replay,
}

/// <summary>
/// One recorded fault decision in a <see cref="FaultPlan"/>'s Fault Map (issue 0083): the stable-identity
/// address of a single <c>Fault(axis, id, p)</c> consultation and its realized outcome. The address is
/// <c>(Axis, Id, Ordinal)</c> — <see cref="Axis"/> names the fault stream, <see cref="Id"/> is the
/// stable within-axis identity (e.g. <c>"{node}:{op}"</c> for a store fault), and <see cref="Ordinal"/>
/// disambiguates repeated consultations of the same <c>(Axis, Id)</c> in deterministic call order. The
/// address is never the draw ordinal across the whole run, so removing one fault leaves every other
/// entry's address meaningful (ADR 0018).
/// </summary>
internal sealed record FaultEntry(string Axis, string Id, long Ordinal, bool Fault);

/// <summary>
/// The single fault path (PRD 0004, ADR 0018), consulted at every fault site as
/// <c>Fault(axis, id, probability) → bool</c>. In <see cref="FaultMode.Generate"/> it draws from the
/// axis's dedicated RNG stream, records the outcome under the call's stable-identity key, and returns it;
/// in <see cref="FaultMode.Replay"/> it looks the outcome up and defaults to no-fault on a miss, making no
/// draw. One code path, both modes — record and replay are never two implementations.
///
/// Each axis owns an independent stream derived as <c>Seed ^ constant</c>, exactly mirroring the
/// Simulator's historical per-axis streams, so routing an axis through the FaultPlan in generate mode is
/// byte-identical to the pre-extraction draw. A zero-probability axis is short-circuited by the caller
/// (the call site keeps its <c>probability &gt; 0</c> guard) so it makes no draw and the battery is
/// byte-identical.
/// </summary>
internal sealed class FaultPlan
{
    // Per-axis stream constants — each mirrors the Simulator's historical `Seed ^ "AXIS"` derivation so
    // that routing the axis through the FaultPlan is byte-identical to the pre-extraction draw. Issue 0083
    // routes only the store-fault axis; issue 0084 adds the remaining axes (each on its own constant).
    private static readonly IReadOnlyDictionary<string, ulong> AxisConstants = new Dictionary<string, ulong>(StringComparer.Ordinal)
    {
        ["store"] = 0x4641554C_54_46_4C54UL,     // "FAULT":    the historical store-fault stream (issue 0083)
        ["crash"] = 0x4352415348504F4CUL,        // "CRASHPOL": crash-per-poll, moved off the main RNG (issue 0084)
        ["heartbeat"] = 0x48454152544C4F53UL,    // "HEARTLOS": heartbeat-loss, moved off the main RNG (issue 0084)
    };

    private readonly FaultMode _mode;
    private readonly ulong _seed;

    // Generate: one stream per consulted axis, created on first use from `Seed ^ constant`.
    private readonly Dictionary<string, DeterministicRandom> _streams = new(StringComparer.Ordinal);

    // The per-(axis, id) call counter that assigns each consultation its stable ordinal. Advances
    // identically in generate and replay because both consult the FaultPlan in the same deterministic
    // order, so a replay computes the same address sequence it recorded.
    private readonly Dictionary<(string Axis, string Id), long> _ordinals = new();

    // The realized Fault Map, accumulated in call order — in BOTH modes (issue 0088): generate records each
    // freshly-drawn outcome, replay records each looked-up outcome. `_byKey` is the frozen recorded map the
    // replay path looks decisions up in, indexed by full address for O(1) lookup.
    private readonly List<FaultEntry> _recorded = [];
    private readonly Dictionary<(string Axis, string Id, long Ordinal), bool> _byKey = new();

    private FaultPlan(FaultMode mode, ulong seed, IReadOnlyList<FaultEntry>? map)
    {
        _mode = mode;
        _seed = seed;
        if (map is null)
        {
            return;
        }
        foreach (var entry in map)
        {
            _byKey[(entry.Axis, entry.Id, entry.Ordinal)] = entry.Fault;
        }
    }

    /// <summary>A recording FaultPlan that draws fresh decisions from <paramref name="seed"/>'s axis streams.</summary>
    public static FaultPlan Generate(ulong seed) => new(FaultMode.Generate, seed, map: null);

    /// <summary>A replaying FaultPlan that looks decisions up in <paramref name="map"/>; a miss is no-fault.</summary>
    public static FaultPlan Replay(ulong seed, IReadOnlyList<FaultEntry> map) => new(FaultMode.Replay, seed, map);

    public FaultMode Mode => _mode;

    /// <summary>
    /// Consult the fault path for one decision. <paramref name="axis"/> selects the fault stream;
    /// <paramref name="id"/> is the stable within-axis identity; the per-<c>(axis, id)</c> call ordinal is
    /// assigned here so callers never thread a counter. In generate mode draws from the axis stream and
    /// records the outcome; in replay mode looks it up, defaulting to no-fault (false) on a miss.
    /// </summary>
    public bool Fault(string axis, string id, double probability)
        => Resolve(axis, id, () => StreamFor(axis).NextDouble() < probability);

    /// <summary>
    /// Consult the fault path for one decision whose draw stays on an existing dedicated per-key stream
    /// (issue 0084) — e.g. the handler outcome on the PerAttempt stream, or ack-loss / unroutable on their
    /// own streams. The caller draws the outcome (so its stream sequence is unchanged and the generate run
    /// stays byte-identical) and passes it in; generate records it under the call's stable-identity key,
    /// replay looks it up and defaults to no-fault on a miss. The drawn-then-ignored outcome on a replay is
    /// harmless because the decision is matched by stable key, never re-derived from the stream.
    /// </summary>
    public bool Decide(string axis, string id, bool generatedOutcome)
        => Resolve(axis, id, () => generatedOutcome);

    private bool Resolve(string axis, string id, Func<bool> generate)
    {
        var key = (axis, id);
        var ordinal = _ordinals.TryGetValue(key, out var next) ? next : 0;
        _ordinals[key] = ordinal + 1;

        // Both modes accumulate the REALIZED outcome into `_recorded` so `ToFaultMap()` returns what was
        // actually consulted+applied — on replay too (issue 0088, ADR 0018 "persist the realized Fault Map").
        // Replay records the looked-up value (a miss is no-fault), which adds NO rng draw, so the battery
        // stays byte-identical; addresses never consulted on a calmer run simply drop out of the realized map.
        bool outcome;
        if (_mode == FaultMode.Replay)
        {
            outcome = _byKey.TryGetValue((axis, id, ordinal), out var recorded) && recorded;
        }
        else
        {
            outcome = generate();
        }

        _recorded.Add(new FaultEntry(axis, id, ordinal, outcome));
        return outcome;
    }

    /// <summary>
    /// The realized Fault Map: every decision actually consulted+applied this run, in call order. Recorded in
    /// both generate and replay mode (issue 0088), so after a replay it holds exactly the surviving consulted
    /// entries — the realized map the Seed Minimizer persists into the returned Plan.
    /// </summary>
    public IReadOnlyList<FaultEntry> ToFaultMap() => _recorded;

    private DeterministicRandom StreamFor(string axis)
    {
        if (_streams.TryGetValue(axis, out var stream))
        {
            return stream;
        }
        if (!AxisConstants.TryGetValue(axis, out var constant))
        {
            throw new InvalidOperationException($"FaultPlan: unknown fault axis '{axis}' — register its stream constant in {nameof(AxisConstants)}.");
        }
        stream = new DeterministicRandom(_seed ^ constant);
        _streams[axis] = stream;
        return stream;
    }
}
