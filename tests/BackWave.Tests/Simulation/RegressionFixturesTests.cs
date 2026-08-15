using System.Reflection;

namespace BackWave.Tests.Simulation;

/// <summary>
/// The discovery loop's terminal stage (issue 0089, PRD 0004, ADR 0018): a minimized Plan graduates from the
/// working corpus to a <i>checked-in regression fixture</i> — committed JSON, not a bare Seed, because a
/// minimized Plan deliberately diverges from <c>FromSeed</c>. The committed fixtures replay CLEAN (the suite
/// is green post-fix); a sabotage twin proves the catch: flip the fixture's sabotage flag back on and the
/// SAME invariant ID trips, so if anyone reintroduces the bug the interleaving re-trips and the test goes red.
///
/// Framing of the synthetic fixture <c>LegalTransition.json</c>:
///   • sabotage-OFF = post-fix world — the committed fixture; replays clean → guards the green suite.
///   • sabotage-ON  = the regression this fixture guards against — re-tripping <see cref="InvariantId.LegalTransition"/>.
/// The two share the SAME minimized Fault-Map interleaving; only the one Scenario sabotage flag differs.
/// </summary>
public class RegressionFixturesTests
{
    /// <summary>
    /// Resolves the committed fixtures directory beside the test binary. The csproj copies
    /// <c>Simulation/fixtures/**/*.json</c> with <c>PreserveNewest</c>, so they sit under the output dir.
    /// </summary>
    private static string FixturesDir =>
        Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!, "Simulation", "fixtures");

    private static IEnumerable<string> FixtureFiles =>
        Directory.Exists(FixturesDir)
            ? Directory.EnumerateFiles(FixturesDir, "*.json").OrderBy(p => p, StringComparer.Ordinal)
            : [];

    /// <summary>MemberData source: one row per committed fixture file, keyed by file name for readable IDs.</summary>
    public static IEnumerable<object[]> Fixtures =>
        FixtureFiles.Select(path => new object[] { Path.GetFileName(path) });

    /// <summary>
    /// Guards against a vacuous green: an empty fixtures dir would make the <see cref="Theory"/> below pass with
    /// zero rows, so we assert the suite actually discovered the committed artifacts.
    /// </summary>
    [Fact]
    public void FixturesDirectory_ContainsAtLeastOneCheckedInFixture()
    {
        Assert.True(Directory.Exists(FixturesDir), $"fixtures dir not found at {FixturesDir}");
        Assert.NotEmpty(FixtureFiles);
    }

    /// <summary>
    /// Every checked-in fixture replays CLEAN: <see cref="Simulator.Run"/> converges without tripping any
    /// oracle. If a Core regression reintroduces the bug a fixture guards, this replay throws and goes red.
    /// </summary>
    [Theory]
    [MemberData(nameof(Fixtures))]
    public void EveryCheckedInFixture_ReplaysClean(string fileName)
    {
        var plan = PlanStore.Load(Path.Combine(FixturesDir, fileName));

        var sim = new Simulator(plan.Scenario.ToOptions(), FaultPlan.Replay(plan.Seed, plan.FaultMap));

        // No invariant trips — the run converges (Run does not throw).
        var result = sim.Run();
        Assert.NotNull(result);
    }

    /// <summary>
    /// Sabotage twin (the regression this fixture guards): the committed clean fixture and this twin share the
    /// SAME minimized interleaving — the only difference is the one Scenario sabotage flag. Flipping it back on
    /// re-trips the exact <see cref="InvariantId"/> the fixture was minimized against, proving the catch.
    /// </summary>
    [Fact]
    public void SabotageTwin_OfTheLegalTransitionFixture_RetripsTheSameInvariant()
    {
        var cleanFixture = PlanStore.Load(Path.Combine(FixturesDir, "LegalTransition.json"));

        // The committed fixture is the post-fix world: sabotage off, replays clean.
        Assert.False(cleanFixture.Scenario.SabotageLegalTransition);
        var cleanSim = new Simulator(cleanFixture.Scenario.ToOptions(), FaultPlan.Replay(cleanFixture.Seed, cleanFixture.FaultMap));
        Assert.NotNull(cleanSim.Run());

        // The twin re-enables sabotage on the SAME minimized Fault-Map interleaving: the bug returns.
        var sabotaged = cleanFixture with
        {
            Scenario = cleanFixture.Scenario with { SabotageLegalTransition = true },
        };
        var twinSim = new Simulator(sabotaged.Scenario.ToOptions(), FaultPlan.Replay(sabotaged.Seed, sabotaged.FaultMap));

        var ex = Assert.Throws<SimulationInvariantException>(() => twinSim.Run());
        Assert.Equal(InvariantId.LegalTransition, ex.InvariantId);
    }

    /// <summary>
    /// Sabotage twin for the <c>SlotDoubleRelease.json</c> fixture (issue 0130): the swarm's exploiter found a
    /// crash + multi-Queue interleaving where an operator Requeue recycled an Attempt number (Requeue resets the
    /// Attempt budget to 0, §3), making the slot-double-release oracle conflate two legitimate single releases of
    /// recycled Attempt 1 into a false double. The fix forgets a job's per-Attempt slot tally on Requeue, so the
    /// committed fixture replays clean. This twin flips <see cref="InvariantId.SlotDoubleRelease"/>'s sabotage on
    /// over the SAME minimized interleaving and asserts the oracle still catches a GENUINE double — proving the
    /// fix narrowed only the false positive, not the detector's teeth.
    /// </summary>
    [Fact]
    public void SabotageTwin_OfTheSlotDoubleReleaseFixture_RetripsTheSameInvariant()
    {
        var cleanFixture = PlanStore.Load(Path.Combine(FixturesDir, "SlotDoubleRelease.json"));

        // The committed fixture is the post-fix world: sabotage off, replays clean.
        Assert.False(cleanFixture.Scenario.SabotageSlotDoubleRelease);
        var cleanSim = new Simulator(cleanFixture.Scenario.ToOptions(), FaultPlan.Replay(cleanFixture.Seed, cleanFixture.FaultMap));
        Assert.NotNull(cleanSim.Run());

        // The twin re-enables sabotage on the SAME minimized Fault-Map interleaving: a genuine double returns.
        var sabotaged = cleanFixture with
        {
            Scenario = cleanFixture.Scenario with { SabotageSlotDoubleRelease = true },
        };
        var twinSim = new Simulator(sabotaged.Scenario.ToOptions(), FaultPlan.Replay(sabotaged.Seed, sabotaged.FaultMap));

        var ex = Assert.Throws<SimulationInvariantException>(() => twinSim.Run());
        Assert.Equal(InvariantId.SlotDoubleRelease, ex.InvariantId);
    }

    /// <summary>
    /// Sabotage twin for the <c>DrainLiveness.json</c> fixture (issue 0136): the swarm's overnight run found a
    /// drain stall where an Unroutable job's terminal report was driven INLINE inside the claim batch, so a
    /// store fault on that report unwound through the Simulator's <c>TryDrive</c> and abandoned the sibling
    /// <c>ExecuteJob</c>s claimed in the same batch — those jobs stayed Leased, the Driver's heartbeat renewed
    /// their leases for the whole drain window, and they never converged (<see cref="InvariantId.DrainLiveness"/>).
    /// The fix defers the Unroutable report to after the batch, matching the production Shell, so the committed
    /// fixture replays clean. This twin flips the sabotage flag back on over the SAME minimized interleaving and
    /// asserts the drain-liveness oracle still trips — proving the fix closed the stall without dulling the oracle.
    /// </summary>
    [Fact]
    public void SabotageTwin_OfTheDrainLivenessFixture_RetripsTheSameInvariant()
    {
        var cleanFixture = PlanStore.Load(Path.Combine(FixturesDir, "DrainLiveness.json"));

        // The committed fixture is the post-fix world: sabotage off, replays clean.
        Assert.False(cleanFixture.Scenario.SabotageInlineUnroutableReport);
        var cleanSim = new Simulator(cleanFixture.Scenario.ToOptions(), FaultPlan.Replay(cleanFixture.Seed, cleanFixture.FaultMap));
        Assert.NotNull(cleanSim.Run());

        // The twin re-enables the inline Unroutable report on the SAME minimized Fault-Map interleaving: the stall returns.
        var sabotaged = cleanFixture with
        {
            Scenario = cleanFixture.Scenario with { SabotageInlineUnroutableReport = true },
        };
        var twinSim = new Simulator(sabotaged.Scenario.ToOptions(), FaultPlan.Replay(sabotaged.Seed, sabotaged.FaultMap));

        var ex = Assert.Throws<SimulationInvariantException>(() => twinSim.Run());
        Assert.Equal(InvariantId.DrainLiveness, ex.InvariantId);
    }

    /// <summary>
    /// Sabotage twin for the <c>UnroutableDrainLiveness.json</c> fixture: the swarm's overnight run found a
    /// second drain stall sharing the <c>DrainLiveness.json</c> symptom (a job stuck Leased) but a distinct
    /// mechanism. The 0136 fix deferred an Unroutable job's terminal report to AFTER its claim batch, but ran
    /// the deferred report from inside the same command loop — so when a LATER command in the batch (here a
    /// follow-up claim) hit a transient store fault, the loop unwound before the deferred report ran. The
    /// Unroutable job had already been added to the Driver's executing set on claim, so its lease was
    /// heartbeated for the whole drain window and it never converged (<see cref="InvariantId.DrainLiveness"/>).
    /// The fix runs the deferred reports from a <c>finally</c> so a mid-batch fault can't lose them, matching
    /// the production Shell's durable feedback-event queue; the committed fixture replays clean. This twin
    /// flips <c>SabotageDeferredUnroutableReport</c> back on over the SAME minimized interleaving — restoring
    /// the report's loss on a mid-batch fault — and asserts the stall returns, proving the fix closed the gap
    /// the 0136 deferral still left without dulling the oracle.
    /// </summary>
    [Fact]
    public void SabotageTwin_OfTheUnroutableDrainLivenessFixture_RetripsTheSameInvariant()
    {
        var cleanFixture = PlanStore.Load(Path.Combine(FixturesDir, "UnroutableDrainLiveness.json"));

        // The committed fixture is the post-fix world: sabotage off, replays clean.
        Assert.False(cleanFixture.Scenario.SabotageDeferredUnroutableReport);
        var cleanSim = new Simulator(cleanFixture.Scenario.ToOptions(), FaultPlan.Replay(cleanFixture.Seed, cleanFixture.FaultMap));
        Assert.NotNull(cleanSim.Run());

        // The twin restores the pre-fix placement on the SAME minimized Fault-Map interleaving: the stall returns.
        var sabotaged = cleanFixture with
        {
            Scenario = cleanFixture.Scenario with { SabotageDeferredUnroutableReport = true },
        };
        var twinSim = new Simulator(sabotaged.Scenario.ToOptions(), FaultPlan.Replay(sabotaged.Seed, sabotaged.FaultMap));

        var ex = Assert.Throws<SimulationInvariantException>(() => twinSim.Run());
        Assert.Equal(InvariantId.DrainLiveness, ex.InvariantId);
    }

    /// <summary>
    /// Sabotage twin for the <c>ExecuteLiveness.json</c> fixture (issue 0137): the swarm's overnight run found a
    /// job that reached <c>DeadLettered</c> without ever executing — root-caused to an oracle artifact, not a
    /// product or model bug. The offending job was drawn UNROUTABLE; its deferred quarantine
    /// <c>ReportOutcome(Unroutable)</c> hit a store fault on every claim, so it was never Quarantined, its Lease
    /// (never heartbeated — the Driver never tracks an unroutable job) lapsed each time, and after
    /// <c>MaxAttempts</c> lease-expiries the Attempt ceiling dead-lettered it. That is legitimate termination: an
    /// unroutable job is never dispatched to a handler regardless of which terminal state it lands in. The fix
    /// relaxes <see cref="InvariantId.ExecuteLiveness"/> to exclude unroutable jobs (by the unroutable DRAW, not
    /// by terminal STATE), so the committed fixture replays clean. This twin flips <c>SabotageExecuteLiveness</c>
    /// back on over the SAME minimized interleaving — which withholds the "this job executed" mark for the
    /// fixture's many ROUTABLE jobs — and asserts the oracle still trips, proving the carve-out narrowed only the
    /// unroutable case and kept full teeth on routable terminals.
    /// </summary>
    [Fact]
    public void SabotageTwin_OfTheExecuteLivenessFixture_RetripsTheSameInvariant()
    {
        var cleanFixture = PlanStore.Load(Path.Combine(FixturesDir, "ExecuteLiveness.json"));

        // The committed fixture is the post-fix world: sabotage off, replays clean.
        Assert.False(cleanFixture.Scenario.SabotageExecuteLiveness);
        var cleanSim = new Simulator(cleanFixture.Scenario.ToOptions(), FaultPlan.Replay(cleanFixture.Seed, cleanFixture.FaultMap));
        Assert.NotNull(cleanSim.Run());

        // The twin withholds the execute mark on the SAME minimized interleaving: routable terminals re-trip.
        var sabotaged = cleanFixture with
        {
            Scenario = cleanFixture.Scenario with { SabotageExecuteLiveness = true },
        };
        var twinSim = new Simulator(sabotaged.Scenario.ToOptions(), FaultPlan.Replay(sabotaged.Seed, sabotaged.FaultMap));

        var ex = Assert.Throws<SimulationInvariantException>(() => twinSim.Run());
        Assert.Equal(InvariantId.ExecuteLiveness, ex.InvariantId);
    }

    /// <summary>
    /// Sabotage twin for the <c>MigrationLiveness.json</c> fixture (issue vopr-0139): a 72h cycled VOPR run
    /// tripped the Migration-Liveness Oracle once — a job held Leased by an isolated node 0.2s past the tight
    /// 5.0s reclaim bound. Root-caused to an oracle artifact, not a product or model bug. The bound is a tight,
    /// config-derived sweep deadline (<c>MaxClockSkew + 3·PollInterval</c>) that assumes a survivor re-homes a
    /// lapsed Lease within a poll or two; but the survivor's reclaim is an ExpireLeases store call, and a run of
    /// transient store faults on consecutive survivor sweeps delays reclaim past the bound while the job stays
    /// Leased — yet it is never stranded (the survivor keeps sweeping and reclaims the moment one does not fault;
    /// the end-of-run convergence backstop already covers genuine permanent stalls). The fix exempts
    /// store-fault-active worlds from the tight per-step bound, mirroring the crash-off precondition the bound
    /// already required, so the committed fixture replays clean. This twin flips <c>SabotageMigrationFaultGrace</c>
    /// back on over the SAME minimized interleaving — restoring the pre-fix bound that ignored store faults — and
    /// asserts the transient-delayed reclaim re-trips <see cref="InvariantId.MigrationLiveness"/>, proving the
    /// exemption is load-bearing and the oracle keeps full teeth wherever the sweep is not transiently blocked.
    /// </summary>
    [Fact]
    public void SabotageTwin_OfTheMigrationLivenessFixture_RetripsTheSameInvariant()
    {
        var cleanFixture = PlanStore.Load(Path.Combine(FixturesDir, "MigrationLiveness.json"));

        // The committed fixture is the post-fix world: the store-fault exemption is active, so it replays clean.
        Assert.False(cleanFixture.Scenario.SabotageMigrationFaultGrace);
        var cleanSim = new Simulator(cleanFixture.Scenario.ToOptions(), FaultPlan.Replay(cleanFixture.Seed, cleanFixture.FaultMap));
        Assert.NotNull(cleanSim.Run());

        // The twin restores the pre-fix bound on the SAME minimized interleaving: the transient-delayed reclaim
        // re-trips the same invariant.
        var sabotaged = cleanFixture with
        {
            Scenario = cleanFixture.Scenario with { SabotageMigrationFaultGrace = true },
        };
        var twinSim = new Simulator(sabotaged.Scenario.ToOptions(), FaultPlan.Replay(sabotaged.Seed, sabotaged.FaultMap));

        var ex = Assert.Throws<SimulationInvariantException>(() => twinSim.Run());
        Assert.Equal(InvariantId.MigrationLiveness, ex.InvariantId);
    }

    /// <summary>
    /// Fixture-format fidelity (acceptance criterion): a committed fixture round-trips through the on-disk
    /// PlanStore format unchanged. Compared on serialized JSON, not record equality — empty-array-vs-list on
    /// <c>Schedules</c> is a known serialization artifact, so the byte form is the fidelity contract.
    /// </summary>
    [Theory]
    [MemberData(nameof(Fixtures))]
    public void EveryFixture_RoundTripsThroughPlanStoreWithFullFidelity(string fileName)
    {
        var loaded = PlanStore.Load(Path.Combine(FixturesDir, fileName));

        var reserialized = PlanJson.Serialize(loaded);
        var reloaded = PlanJson.Deserialize(reserialized);

        Assert.Equal(reserialized, PlanJson.Serialize(reloaded));
    }
}
