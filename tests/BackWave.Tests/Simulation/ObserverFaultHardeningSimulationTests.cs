using BackWave.Observers;
using BackWave.Storage;

namespace BackWave.Tests.Simulation;

/// <summary>
/// Transition Observer fault hardening (§0078, ADR 0017): point the existing Node Isolation, crash,
/// and store-fault regimes at the delivery cursor and prove the Observer-delivery oracle still holds —
/// every matching transition is delivered at-least-once or delivery-dead-lettered, with no silent loss.
/// A node isolated/crashed/faulted mid-delivery has its claim lapse (or its round-trip abort), and
/// another node — or itself, on heal — redelivers, so duplicates are expected and tolerated; the
/// oracle never asserts single delivery. The sabotage self-test proves the oracle is not vacuous under
/// faults: a dropped delivery (cursor wrongly advanced, no redelivery to repair it) trips it.
/// </summary>
public class ObserverFaultHardeningSimulationTests
{
    private static readonly ObserverRegistration TerminalObserver = new(
        "terminal",
        new ObserverSubscription([JobState.Succeeded, JobState.DeadLettered, JobState.Cancelled, JobState.Quarantined]));

    /// <summary>
    /// Transient store faults on the claim/report path: a faulted round-trip aborts cleanly (the guard
    /// is released, the cursor stands un-advanced) and a later claim redelivers. Every terminal
    /// transition is still delivered at least once — no silent loss — and duplicates are tolerated.
    /// </summary>
    [Theory]
    [InlineData(331UL)]
    [InlineData(332UL)]
    [InlineData(404UL)]
    public void UnderStoreFaults_EveryTransitionDeliveredAtLeastOnce(ulong seed)
    {
        var result = new Simulator(new SimulationOptions
        {
            Seed = seed,
            StoreFaultProbability = 0.1,
            Observers = [TerminalObserver],
        }).Run();

        var (total, unique, deadLettered) = result.ObserverDeliveries["terminal"];
        // The run is green (the oracle proved at-least-once liveness). Every job's terminal transition
        // was delivered, none dead-lettered (the sink never fails — only the store path faults).
        Assert.Equal(result.FinalJobs.Count, unique);
        Assert.Equal(0, deadLettered);
        Assert.True(total >= unique); // duplicates are legal; delivery is never asserted to be single
    }

    /// <summary>
    /// Node Isolation aimed at the delivery cursor: an isolated node's claim Lease lapses while it is
    /// cut off, so a survivor redelivers — the same heal-into-stale-write interleaving the isolation
    /// harness manufactures (issue 0068), now against delivery. Redelivery produces duplicates, which
    /// the oracle tolerates while still proving at-least-once liveness.
    /// </summary>
    [Theory]
    [InlineData(331UL)]
    [InlineData(332UL)]
    [InlineData(606UL)]
    public void UnderNodeIsolation_NoSilentLoss_ToleratesDuplicates(ulong seed)
    {
        var result = new Simulator(new SimulationOptions
        {
            Seed = seed,
            IsolationCount = 40,
            CrashProbabilityPerPoll = 0,
            HeartbeatLossProbability = 0,
            MaxExecutionDuration = TimeSpan.FromSeconds(150),
            Observers = [TerminalObserver],
        }).Run();

        var (total, unique, deadLettered) = result.ObserverDeliveries["terminal"];
        Assert.Equal(result.FinalJobs.Count, unique);
        Assert.Equal(0, deadLettered);
        Assert.True(total >= unique);
    }

    /// <summary>
    /// The full fault stack — crashes, heartbeat loss, store faults, and Node Isolation together —
    /// against the delivery cursor. The oracle proves no silent loss under the worst interleavings.
    /// </summary>
    [Theory]
    [InlineData(700UL)]
    [InlineData(701UL)]
    public void UnderCombinedFaults_NoSilentLoss(ulong seed)
    {
        var result = new Simulator(new SimulationOptions
        {
            Seed = seed,
            IsolationCount = 20,
            StoreFaultProbability = 0.05,
            MaxExecutionDuration = TimeSpan.FromSeconds(150),
            Observers = [TerminalObserver],
        }).Run();

        var (_, unique, deadLettered) = result.ObserverDeliveries["terminal"];
        Assert.Equal(result.FinalJobs.Count, unique);
        Assert.Equal(0, deadLettered);
    }

    /// <summary>
    /// Sabotage self-test under faults (proves the fault-hardened oracle bites, not just passes): the
    /// delivery is reported Delivered to the store (cursor advances) but withheld from the recording
    /// sink — a silent drop that no redelivery can repair, since the cursor moved past it. With Node
    /// Isolation and store faults also active, a live oracle MUST still catch the missing delivery and
    /// fail with the replay seed.
    /// </summary>
    [Theory]
    [InlineData(331UL)]
    [InlineData(332UL)]
    public void FaultRegime_DroppedDelivery_TripsTheOracle_AndReplaysFromSeed(ulong seed)
    {
        var exception = Assert.Throws<SimulationInvariantException>(() =>
            new Simulator(new SimulationOptions
            {
                Seed = seed,
                IsolationCount = 30,
                StoreFaultProbability = 0.05,
                MaxExecutionDuration = TimeSpan.FromSeconds(150),
                Observers = [TerminalObserver],
                SabotageObserverDelivery = true,
            }).Run());

        Assert.Contains($"seed {seed}", exception.Message);
        Assert.Contains("liveness", exception.Message);
        Assert.Equal(InvariantId.ObserverDeliveryLiveness, exception.InvariantId);
    }

    /// <summary>
    /// Determinism: a fault-laden observer run replays bit-identically from its seed — the delivery
    /// tallies match across two runs of the same options, so any failing seed is reproducible.
    /// </summary>
    [Fact]
    public void FaultRegime_SameSeed_ReplaysIdenticalDeliveries()
    {
        var options = new SimulationOptions
        {
            Seed = 909,
            IsolationCount = 30,
            StoreFaultProbability = 0.05,
            MaxExecutionDuration = TimeSpan.FromSeconds(150),
            Observers = [TerminalObserver],
        };
        var first = new Simulator(options).Run();
        var second = new Simulator(options).Run();

        Assert.Equal(first.ObserverDeliveries["terminal"], second.ObserverDeliveries["terminal"]);
    }
}
