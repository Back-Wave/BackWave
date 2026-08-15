using System.Text.Json;
using System.Text.Json.Serialization;
using BackWave.Observers;

namespace BackWave.Tests.Simulation;

/// <summary>
/// The serializable, replayable world of one run (PRD 0004, ADR 0018): the scalar knobs the main RNG
/// regenerates deterministically — node/job counts, the workload window, durations, and the fault
/// parameters the Swarm chooses. Distinct from the <see cref="FaultPlan"/>'s Fault Map: the Scenario is
/// the world, the Fault Map is the injected decisions, and only the Fault Map is editable by the minimizer.
///
/// This mirrors the JSON-serializable surface of <see cref="SimulationOptions"/>. The Observer REGISTRATION
/// (<see cref="SimulationOptions.Observers"/>) is pure data (ids + subscribed states), so it is carried here
/// (issue 0201) — otherwise the guided swarm, which round-trips every candidate through a Scenario, could
/// never reach the Observer coverage Situations. The Observer FAULT surface (<see cref="SimulationOptions.FailingObservers"/>,
/// <see cref="SimulationOptions.ObserverDeliveryRetryPolicy"/>, <see cref="SimulationOptions.SabotageObserverDelivery"/>)
/// is still out of scope for the Plan tracer and reset to defaults by <see cref="ToOptions"/> — it joins the
/// Fault Map as ordinary stable-keyed entries in a later slice (PRD 0004 US 27).
/// </summary>
internal sealed record Scenario
{
    public required ulong Seed { get; init; }
    public int NodeCount { get; init; } = 3;
    public int JobCount { get; init; } = 200;
    public TimeSpan WorkloadDuration { get; init; } = TimeSpan.FromHours(2);
    public TimeSpan DrainAllowance { get; init; } = TimeSpan.FromHours(2);
    public double CrashProbabilityPerPoll { get; init; } = 0.005;
    public double HeartbeatLossProbability { get; init; } = 0.05;
    public double HandlerFailureProbability { get; init; } = 0.15;
    public TimeSpan MaxExecutionDuration { get; init; } = TimeSpan.FromSeconds(90);
    public TimeSpan MaxClockSkew { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan MaxCrashDowntime { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromSeconds(60);
    public int MaxAttempts { get; init; } = 3;
    public TimeSpan Backoff { get; init; } = TimeSpan.FromSeconds(30);
    public int? ConcurrencyLimit { get; init; }
    public IReadOnlyDictionary<string, int> ConcurrencyLimits { get; init; } = new Dictionary<string, int>();
    public int PoolSize { get; init; } = int.MaxValue;
    public double HintDeliveryProbability { get; init; }
    public TimeSpan MaxHintLatency { get; init; } = TimeSpan.FromMilliseconds(250);
    public double StoreFaultProbability { get; init; }
    public int OperatorActionCount { get; init; }
    public int IsolationCount { get; init; }
    public double PermanentLossProbability { get; init; }
    public double AckLossProbability { get; init; }
    public double UnroutableProbability { get; init; }
    public int TopologyQueues { get; init; }
    public bool ForceWeightedDispatch { get; init; }
    public DateTimeOffset StartTime { get; init; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public IReadOnlyList<SeededSchedule> Schedules { get; init; } = [];

    /// <summary>
    /// Registered Transition Observers (issue 0201): the pure-data registration surface (ids + subscribed
    /// states), carried so the guided swarm — which round-trips every candidate through a Scenario — can reach
    /// the Observer coverage Situations. The Observer FAULT surface stays out of scope (see the type remarks).
    /// </summary>
    public IReadOnlyList<ObserverRegistration> Observers { get; init; } = [];

    // Sabotage flags — never set by the Swarm (ADR 0018), but carried so a Plan captured from a sabotage
    // self-test round-trips faithfully.
    public bool Sabotage { get; init; }
    public bool SabotageSlotDoubleRelease { get; init; }
    public bool SabotagePoolSize { get; init; }
    public bool SabotageOutcomeFence { get; init; }
    public bool SabotageBatchFence { get; init; }
    public bool SabotageMigrationSweep { get; init; }
    public bool SabotageMigrationFaultGrace { get; init; }
    public bool SabotagePausedClaim { get; init; }
    public bool SabotageCancelProvenance { get; init; }
    public bool SabotageLegalTransition { get; init; }
    public bool SabotageExecuteLiveness { get; init; }
    public bool SabotageAuditCompleteness { get; init; }
    public bool SabotageServedSet { get; init; }
    public bool SabotageInlineUnroutableReport { get; init; }
    public bool SabotageDeferredUnroutableReport { get; init; }

    // Radioactive regime — carried so a Plan captured by the --radioactive swarm replays with the same liveness
    // oracles disarmed (a safety finding still re-trips its per-step oracle either way; this keeps replay exact).
    public bool RadioactiveMode { get; init; }

    public static Scenario FromOptions(SimulationOptions o) => new()
    {
        Seed = o.Seed,
        NodeCount = o.NodeCount,
        JobCount = o.JobCount,
        WorkloadDuration = o.WorkloadDuration,
        DrainAllowance = o.DrainAllowance,
        CrashProbabilityPerPoll = o.CrashProbabilityPerPoll,
        HeartbeatLossProbability = o.HeartbeatLossProbability,
        HandlerFailureProbability = o.HandlerFailureProbability,
        MaxExecutionDuration = o.MaxExecutionDuration,
        MaxClockSkew = o.MaxClockSkew,
        MaxCrashDowntime = o.MaxCrashDowntime,
        PollInterval = o.PollInterval,
        HeartbeatInterval = o.HeartbeatInterval,
        LeaseDuration = o.LeaseDuration,
        MaxAttempts = o.MaxAttempts,
        Backoff = o.Backoff,
        ConcurrencyLimit = o.ConcurrencyLimit,
        ConcurrencyLimits = o.ConcurrencyLimits,
        PoolSize = o.PoolSize,
        HintDeliveryProbability = o.HintDeliveryProbability,
        MaxHintLatency = o.MaxHintLatency,
        StoreFaultProbability = o.StoreFaultProbability,
        OperatorActionCount = o.OperatorActionCount,
        IsolationCount = o.IsolationCount,
        PermanentLossProbability = o.PermanentLossProbability,
        AckLossProbability = o.AckLossProbability,
        UnroutableProbability = o.UnroutableProbability,
        TopologyQueues = o.TopologyQueues,
        ForceWeightedDispatch = o.ForceWeightedDispatch,
        StartTime = o.StartTime,
        Schedules = o.Schedules,
        Observers = o.Observers,
        Sabotage = o.Sabotage,
        SabotageSlotDoubleRelease = o.SabotageSlotDoubleRelease,
        SabotagePoolSize = o.SabotagePoolSize,
        SabotageOutcomeFence = o.SabotageOutcomeFence,
        SabotageBatchFence = o.SabotageBatchFence,
        SabotageMigrationSweep = o.SabotageMigrationSweep,
        SabotageMigrationFaultGrace = o.SabotageMigrationFaultGrace,
        SabotagePausedClaim = o.SabotagePausedClaim,
        SabotageCancelProvenance = o.SabotageCancelProvenance,
        SabotageLegalTransition = o.SabotageLegalTransition,
        SabotageExecuteLiveness = o.SabotageExecuteLiveness,
        SabotageAuditCompleteness = o.SabotageAuditCompleteness,
        SabotageServedSet = o.SabotageServedSet,
        SabotageInlineUnroutableReport = o.SabotageInlineUnroutableReport,
        SabotageDeferredUnroutableReport = o.SabotageDeferredUnroutableReport,
        RadioactiveMode = o.RadioactiveMode,
    };

    public SimulationOptions ToOptions() => new()
    {
        Seed = Seed,
        NodeCount = NodeCount,
        JobCount = JobCount,
        WorkloadDuration = WorkloadDuration,
        DrainAllowance = DrainAllowance,
        CrashProbabilityPerPoll = CrashProbabilityPerPoll,
        HeartbeatLossProbability = HeartbeatLossProbability,
        HandlerFailureProbability = HandlerFailureProbability,
        MaxExecutionDuration = MaxExecutionDuration,
        MaxClockSkew = MaxClockSkew,
        MaxCrashDowntime = MaxCrashDowntime,
        PollInterval = PollInterval,
        HeartbeatInterval = HeartbeatInterval,
        LeaseDuration = LeaseDuration,
        MaxAttempts = MaxAttempts,
        Backoff = Backoff,
        ConcurrencyLimit = ConcurrencyLimit,
        ConcurrencyLimits = ConcurrencyLimits,
        PoolSize = PoolSize,
        HintDeliveryProbability = HintDeliveryProbability,
        MaxHintLatency = MaxHintLatency,
        StoreFaultProbability = StoreFaultProbability,
        OperatorActionCount = OperatorActionCount,
        IsolationCount = IsolationCount,
        PermanentLossProbability = PermanentLossProbability,
        AckLossProbability = AckLossProbability,
        UnroutableProbability = UnroutableProbability,
        TopologyQueues = TopologyQueues,
        ForceWeightedDispatch = ForceWeightedDispatch,
        StartTime = StartTime,
        Schedules = Schedules,
        Observers = Observers,
        Sabotage = Sabotage,
        SabotageSlotDoubleRelease = SabotageSlotDoubleRelease,
        SabotagePoolSize = SabotagePoolSize,
        SabotageOutcomeFence = SabotageOutcomeFence,
        SabotageBatchFence = SabotageBatchFence,
        SabotageMigrationSweep = SabotageMigrationSweep,
        SabotageMigrationFaultGrace = SabotageMigrationFaultGrace,
        SabotagePausedClaim = SabotagePausedClaim,
        SabotageCancelProvenance = SabotageCancelProvenance,
        SabotageLegalTransition = SabotageLegalTransition,
        SabotageExecuteLiveness = SabotageExecuteLiveness,
        SabotageAuditCompleteness = SabotageAuditCompleteness,
        SabotageServedSet = SabotageServedSet,
        SabotageInlineUnroutableReport = SabotageInlineUnroutableReport,
        SabotageDeferredUnroutableReport = SabotageDeferredUnroutableReport,
        RadioactiveMode = RadioactiveMode,
    };
}

/// <summary>
/// A failing run's identifying stamp, recorded on a Plan when a run trips an oracle (PRD 0004 US 8): the
/// human-readable message and (issue 0085) the stable invariant ID matched on. Null on a clean Plan.
/// </summary>
internal sealed record FailureStamp(string Message, InvariantId? InvariantId = null);

/// <summary>
/// The minimization, replay, and regression unit (PRD 0004, ADR 0018): a serializable description of one
/// run = a <see cref="Scenario"/> (the world the main RNG regenerates) plus a Fault Map (every injected
/// fault decision, addressed by stable identity). The Seed remains the compact discovery unit; the Plan is
/// what is edited, replayed, minimized, and checked in as a regression fixture.
/// </summary>
internal sealed record Plan
{
    /// <summary>Schema version for forward-compatible deserialization of checked-in fixtures.</summary>
    public int SchemaVersion { get; init; } = 1;

    public required Scenario Scenario { get; init; }

    /// <summary>The realized Fault Map — every fault decision the run took, addressed by stable identity.</summary>
    public required IReadOnlyList<FaultEntry> FaultMap { get; init; }

    /// <summary>The failure this Plan reproduces, if any; null for a clean Plan.</summary>
    public FailureStamp? Failure { get; init; }

    /// <summary>The discovery Seed (mirrors <see cref="Scenario.Seed"/>), kept top-level for diffability.</summary>
    [JsonIgnore]
    public ulong Seed => Scenario.Seed;
}

/// <summary>Diffable JSON round-trip for a <see cref="Plan"/> (PRD 0004 US 8) — the on-disk fixture format.</summary>
internal static class PlanJson
{
    // Every scalar knob is written explicitly — no default-omission. A Scenario property initializer
    // (e.g. CrashProbabilityPerPoll = 0.005) differs from the type default (0), so omitting type-defaults
    // would silently rewrite a run's `0` back to the initializer default on deserialize, breaking replay
    // fidelity. Faithful over compact; the JSON is still diffable.
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize(Plan plan) => JsonSerializer.Serialize(plan, Options);

    public static Plan Deserialize(string json) =>
        JsonSerializer.Deserialize<Plan>(json, Options)
        ?? throw new InvalidOperationException("Plan JSON deserialized to null.");
}
