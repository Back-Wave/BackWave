using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using BackWave.Core;
using BackWave.Diagnostics;
using BackWave.Observers;
using BackWave.Storage;

namespace BackWave.Tests.Simulation;

/// <summary>
/// Transition Observer delivery resilience (§0077, ADR 0017): delivery is itself at-least-once work
/// with its own Attempts, bounded backoff, and Dead-Letter. A throwing / timed-out / hung callback is
/// caught at the dispatch edge (never fail-stopping the worker pump), held, and retried with backoff
/// until the ceiling — then dead-lettered so the cursor advances past a poison row (bounded
/// head-of-line). Per-Observer cursors keep one poison Observer from starving another. The
/// Observer-delivery oracle proves bounded poison (dead-letter only after the ceiling, cursor
/// advances) while still tolerating duplicate deliveries. Node Isolation / crash faults are §0078.
/// </summary>
public class ObserverResilienceSimulationTests
{
    private static readonly JobState[] TerminalStates =
        [JobState.Succeeded, JobState.DeadLettered, JobState.Cancelled, JobState.Quarantined];

    /// <summary>A fast delivery policy so a poison row reaches its ceiling inside a short drain window.</summary>
    private static RetryPolicy FastDeliveryPolicy(int ceiling) =>
        new() { MaxAttempts = ceiling, Backoff = _ => TimeSpan.FromSeconds(1) };

    /// <summary>A short workload so resilience runs stay snappy — faults are off, so jobs converge quickly.</summary>
    private static SimulationOptions BaseOptions(ulong seed) => new()
    {
        Seed = seed,
        JobCount = 40,
        WorkloadDuration = TimeSpan.FromMinutes(30),
        DrainAllowance = TimeSpan.FromMinutes(30),
        CrashProbabilityPerPoll = 0,
        HeartbeatLossProbability = 0,
    };

    /// <summary>
    /// A flaky observer whose callback throws on its first two delivery Attempts then succeeds: every
    /// transition is held, retried with backoff, and ultimately delivered — none dead-lettered, none
    /// lost. Proves the retry-with-backoff path recovers (the throw is contained, not fatal).
    /// </summary>
    [Theory]
    [InlineData(1UL)]
    [InlineData(1337UL)]
    public void FlakyObserver_RecoversAfterBackoff_DeliveredNeverDeadLettered(ulong seed)
    {
        var result = new Simulator(BaseOptions(seed) with
        {
            Observers = [new ObserverRegistration("terminal", new ObserverSubscription(TerminalStates))],
            ObserverDeliveryRetryPolicy = FastDeliveryPolicy(ceiling: 5),
            FailingObservers = new Dictionary<string, int>(StringComparer.Ordinal) { ["terminal"] = 2 },
        }).Run();

        var (total, unique, deadLettered) = result.ObserverDeliveries["terminal"];
        // Every job's one terminal transition is eventually delivered — recovered after the failed
        // attempts, not dead-lettered.
        Assert.Equal(result.FinalJobs.Count, unique);
        Assert.Equal(0, deadLettered);
        // Failed attempts never record to the sink, so a recovered delivery lands exactly once.
        Assert.Equal(unique, total);
    }

    /// <summary>
    /// A poison observer whose callback always throws: every transition is retried to the ceiling,
    /// then dead-lettered, and the cursor advances past it so the run still converges (a poison row
    /// that wedged the cursor would never drain → the run would fail liveness). Nothing reaches the
    /// sink; everything is recorded as a dead-letter (loud, never silent).
    /// </summary>
    [Theory]
    [InlineData(2UL)]
    [InlineData(99UL)]
    public void PoisonObserver_DeadLetteredAfterCeiling_CursorAdvances_RunConverges(ulong seed)
    {
        var result = new Simulator(BaseOptions(seed) with
        {
            Observers = [new ObserverRegistration("poison", new ObserverSubscription(TerminalStates))],
            ObserverDeliveryRetryPolicy = FastDeliveryPolicy(ceiling: 3),
            FailingObservers = new Dictionary<string, int>(StringComparer.Ordinal) { ["poison"] = int.MaxValue },
        }).Run();

        var (total, unique, deadLettered) = result.ObserverDeliveries["poison"];
        Assert.Equal(0, unique);
        Assert.Equal(0, total);
        // Every terminal transition was dead-lettered and recorded — and the run converging at all is
        // the proof the cursor advanced past each poison row.
        Assert.Equal(result.FinalJobs.Count, deadLettered);
        Assert.True(deadLettered > 0);
    }

    /// <summary>
    /// Pump isolation (ADR 0007's contrapositive): a poison observer never touches job execution —
    /// every job still reaches a terminal state. The throwing callback is caught at the egress edge,
    /// so it cannot fail-stop the worker pump.
    /// </summary>
    [Theory]
    [InlineData(7UL)]
    [InlineData(2024UL)]
    public void PoisonObserver_NeverAffectsJobExecution(ulong seed)
    {
        var result = new Simulator(BaseOptions(seed) with
        {
            Observers = [new ObserverRegistration("poison", new ObserverSubscription(TerminalStates))],
            ObserverDeliveryRetryPolicy = FastDeliveryPolicy(ceiling: 3),
            FailingObservers = new Dictionary<string, int>(StringComparer.Ordinal) { ["poison"] = int.MaxValue },
        }).Run();

        // The run is green (the harness asserts AllTerminal), and every enqueued job terminated —
        // observer delivery failures are peripheral and contained.
        Assert.Equal(40, result.FinalJobs.Count);
        Assert.All(result.FinalJobs, j => Assert.Contains(j.State, TerminalStates));
    }

    /// <summary>
    /// Per-Observer cursors (ADR 0017): a poison observer and a healthy observer watching the same
    /// states are delivered independently — the poison one dead-letters every transition while the
    /// healthy one is delivered every transition, undelayed and complete. Equal expected sets, so the
    /// healthy delivery count and the poison dead-letter count must match exactly.
    /// </summary>
    [Theory]
    [InlineData(11UL)]
    [InlineData(555UL)]
    public void PoisonObserver_DoesNotStarveHealthyObserver(ulong seed)
    {
        var result = new Simulator(BaseOptions(seed) with
        {
            Observers =
            [
                new ObserverRegistration("healthy", new ObserverSubscription(TerminalStates)),
                new ObserverRegistration("poison", new ObserverSubscription(TerminalStates)),
            ],
            ObserverDeliveryRetryPolicy = FastDeliveryPolicy(ceiling: 3),
            FailingObservers = new Dictionary<string, int>(StringComparer.Ordinal) { ["poison"] = int.MaxValue },
        }).Run();

        var healthy = result.ObserverDeliveries["healthy"];
        var poison = result.ObserverDeliveries["poison"];

        Assert.Equal(result.FinalJobs.Count, healthy.Unique);
        Assert.Equal(0, healthy.DeadLettered);
        Assert.Equal(0, poison.Unique);
        // Same subscription ⇒ same expected set: the poison observer dead-letters exactly what the
        // healthy one delivers. Neither is starved by the other.
        Assert.Equal(healthy.Unique, poison.DeadLettered);
    }

    /// <summary>
    /// The delivery-edge OpenTelemetry counters (0081): a healthy observer's deliveries are counted
    /// attempted == succeeded for every delivered transition with none dead-lettered, and a permanently
    /// failing observer eventually records dead_lettered ≥ 1 — all attributed by <c>backwave.observer_id</c>.
    /// A MeterListener subscribed to the BackWave Meter proves the zero-cost counters fire at the edge.
    /// Unique per-test observer ids isolate this run's measurements from any concurrent harness.
    /// </summary>
    [Fact]
    public void DeliveryCounters_Emit_AttemptedSucceeded_AndDeadLettered_PerObserver()
    {
        var measurements = new ConcurrentBag<(string Instrument, long Value, Dictionary<string, object?> Tags)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == BackWaveDiagnostics.SourceName)
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            measurements.Add((instrument.Name, value, tags.ToArray().ToDictionary(t => t.Key, t => t.Value))));
        listener.Start();

        // Distinct ids so the global counters' measurements are isolatable from other tests' runs.
        const string healthyId = "otel-healthy-0081";
        const string poisonId = "otel-poison-0081";

        var result = new Simulator(BaseOptions(seed: 0081) with
        {
            Observers =
            [
                new ObserverRegistration(healthyId, new ObserverSubscription(TerminalStates)),
                new ObserverRegistration(poisonId, new ObserverSubscription(TerminalStates)),
            ],
            ObserverDeliveryRetryPolicy = FastDeliveryPolicy(ceiling: 3),
            FailingObservers = new Dictionary<string, int>(StringComparer.Ordinal) { [poisonId] = int.MaxValue },
        }).Run();

        long Sum(string instrument, string observerId) => measurements
            .Where(m => m.Instrument == instrument
                && Equals(m.Tags.GetValueOrDefault("backwave.observer_id"), observerId))
            .Sum(m => m.Value);

        // Healthy: every attempt succeeds (never throws), one delivered transition per job, none dead-lettered.
        var (_, healthyUnique, _) = result.ObserverDeliveries[healthyId];
        Assert.Equal(healthyUnique, Sum("backwave.observer.deliveries.attempted", healthyId));
        Assert.Equal(healthyUnique, Sum("backwave.observer.deliveries.succeeded", healthyId));
        Assert.Equal(0, Sum("backwave.observer.deliveries.dead_lettered", healthyId));

        // Poison: every transition is retried to the ceiling then dead-lettered — attempts pile up,
        // none succeed, and the dead-letter counter fires at least once for this observer.
        Assert.True(Sum("backwave.observer.deliveries.attempted", poisonId) >= 1);
        Assert.Equal(0, Sum("backwave.observer.deliveries.succeeded", poisonId));
        Assert.True(Sum("backwave.observer.deliveries.dead_lettered", poisonId) >= 1);

        // The healthy attempts carry the transition's wire_name/queue when cheaply available at the edge.
        Assert.Contains(measurements, m =>
            m.Instrument == "backwave.observer.deliveries.attempted"
            && Equals(m.Tags.GetValueOrDefault("backwave.observer_id"), healthyId)
            && Equals(m.Tags.GetValueOrDefault("backwave.wire_name"), "sim-job"));
    }
}
