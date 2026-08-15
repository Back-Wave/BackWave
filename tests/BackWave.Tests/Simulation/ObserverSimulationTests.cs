using BackWave.Observers;
using BackWave.Storage;

namespace BackWave.Tests.Simulation;

/// <summary>
/// Transition Observer tracer bullet (§0076, ADR 0017): a registered Observer is delivered every
/// matching transition at-least-once by a Lease-claimed walk of the Transition Log, proven by the
/// Observer-delivery oracle (liveness + in-order-per-Observer, tolerating duplicates). No fault
/// injection here — that is §0078.
/// </summary>
public class ObserverSimulationTests
{
    private static readonly ObserverRegistration TerminalObserver = new(
        "terminal",
        new ObserverSubscription([JobState.Succeeded, JobState.DeadLettered, JobState.Cancelled, JobState.Quarantined]));

    /// <summary>A clean multi-node run with no faults: registration → delivery → oracle, all green.</summary>
    [Theory]
    [InlineData(1UL)]
    [InlineData(1337UL)]
    [InlineData(0xDEADBEEFUL)]
    public void TracerBullet_EveryTerminalTransition_DeliveredOnce(ulong seed)
    {
        var result = new Simulator(new SimulationOptions
        {
            Seed = seed,
            CrashProbabilityPerPoll = 0,
            HeartbeatLossProbability = 0,
            Observers = [TerminalObserver],
        }).Run();

        // Every one of the 200 jobs reached exactly one terminal state, so the Observer must have
        // seen exactly 200 distinct transitions — none lost (the oracle already proved liveness).
        Assert.Equal(200, result.FinalJobs.Count);
        var (total, unique, deadLettered) = result.ObserverDeliveries["terminal"];
        Assert.Equal(200, unique);
        // Happy path = single delivery: one node delivers each transition, so no duplicates and
        // nothing dead-lettered (the recording sink never fails).
        Assert.Equal(unique, total);
        Assert.Equal(0, deadLettered);
    }

    /// <summary>
    /// Registration is run config (ADR 0017): same seed + same Observers ⇒ identical delivery tally.
    /// </summary>
    [Fact]
    public void SameSeed_ReproducesIdenticalDeliveries()
    {
        var options = new SimulationOptions
        {
            Seed = 1337,
            CrashProbabilityPerPoll = 0,
            HeartbeatLossProbability = 0,
            Observers = [TerminalObserver],
        };
        var first = new Simulator(options).Run();
        var second = new Simulator(options).Run();

        Assert.Equal(first.ObserverDeliveries["terminal"], second.ObserverDeliveries["terminal"]);
    }

    /// <summary>
    /// No registered Observer ⇒ no dispatcher, no delivery state, and the run stays byte-identical to
    /// the same seed without observers — the zero-cost-when-unused guarantee.
    /// </summary>
    [Fact]
    public void NoObserverRegistered_IsByteIdenticalToBaseline()
    {
        var withoutField = new Simulator(new SimulationOptions { Seed = 4242 }).Run();
        var withEmpty = new Simulator(new SimulationOptions { Seed = 4242, Observers = [] }).Run();

        Assert.Equal(withoutField.Steps, withEmpty.Steps);
        Assert.Equal(withoutField.FinalJobs, withEmpty.FinalJobs);
        Assert.Empty(withEmpty.ObserverDeliveries);
    }

    /// <summary>
    /// Per-Observer cursors (ADR 0017): two Observers with different subscriptions are delivered
    /// independently, each seeing exactly its matching transitions.
    /// </summary>
    [Theory]
    [InlineData(5UL)]
    [InlineData(77UL)]
    public void TwoObservers_AreDeliveredIndependently(ulong seed)
    {
        var leased = new ObserverRegistration("leased", new ObserverSubscription([JobState.Leased]));
        var result = new Simulator(new SimulationOptions
        {
            Seed = seed,
            CrashProbabilityPerPoll = 0,
            HeartbeatLossProbability = 0,
            Observers = [TerminalObserver, leased],
        }).Run();

        Assert.Equal(200, result.ObserverDeliveries["terminal"].Unique);
        // Every job is Leased at least once (it must run to terminate), so the Leased Observer sees
        // strictly more transitions than the terminal one (retries add extra Leased edges).
        Assert.True(result.ObserverDeliveries["leased"].Unique >= 200);
    }

    /// <summary>
    /// A Wire Name filter that matches nothing yields no deliveries — and the run is still green
    /// (an empty expected set is trivially live), proving filtering actually narrows.
    /// </summary>
    [Fact]
    public void WireNameFilter_ThatMatchesNothing_DeliversNothing()
    {
        var result = new Simulator(new SimulationOptions
        {
            Seed = 9,
            CrashProbabilityPerPoll = 0,
            HeartbeatLossProbability = 0,
            Observers =
            [
                new ObserverRegistration(
                    "other-type",
                    new ObserverSubscription([JobState.Succeeded]) { WireName = "not-sim-job" }),
            ],
        }).Run();

        Assert.Equal(0, result.ObserverDeliveries["other-type"].Unique);
    }

    /// <summary>
    /// A Queue filter narrows delivery (§0079): a subscription bound to a Queue no job lives in gets
    /// nothing, while the same subscription on the populated Queue is delivered every match — the
    /// filter is sourced from the job row, exactly like the Wire Name filter.
    /// </summary>
    [Fact]
    public void QueueFilter_NarrowsToTheSubscribedQueue()
    {
        var result = new Simulator(new SimulationOptions
        {
            Seed = 21,
            CrashProbabilityPerPoll = 0,
            HeartbeatLossProbability = 0,
            Observers =
            [
                new ObserverRegistration("default-queue", new ObserverSubscription([JobState.Succeeded]) { Queue = "default" }),
                new ObserverRegistration("other-queue", new ObserverSubscription([JobState.Succeeded]) { Queue = "no-such-queue" }),
            ],
        }).Run();

        // Every sim job lives in "default", so the default-queue observer sees its Succeeded jobs while
        // the other-queue observer — a Queue that matches nothing — is delivered nothing.
        Assert.True(result.ObserverDeliveries["default-queue"].Unique > 0);
        Assert.Equal(0, result.ObserverDeliveries["other-queue"].Unique);
    }

    /// <summary>
    /// Oracle self-test: a silently dead liveness oracle looks identical to a healthy one. With the
    /// sabotage on, a claimed batch is reported delivered to the store (cursor advances) but withheld
    /// from the recording sink — a silent drop a working oracle MUST catch, failing with the seed.
    /// </summary>
    [Theory]
    [InlineData(101UL)]
    [InlineData(102UL)]
    public void OracleSelfTest_DroppedDelivery_FailsTheRun_AndPrintsTheSeed(ulong seed)
    {
        var exception = Assert.Throws<SimulationInvariantException>(() =>
            new Simulator(new SimulationOptions
            {
                Seed = seed,
                CrashProbabilityPerPoll = 0,
                HeartbeatLossProbability = 0,
                Observers = [TerminalObserver],
                SabotageObserverDelivery = true,
            }).Run());

        Assert.Contains($"seed {seed}", exception.Message);
        Assert.Contains("liveness", exception.Message);
        Assert.Equal(InvariantId.ObserverDeliveryLiveness, exception.InvariantId);
    }
}
