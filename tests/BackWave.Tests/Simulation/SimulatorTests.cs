using BackWave.Core;
using BackWave.Driver;
using BackWave.Storage;
using BackWave.Storage.InMemory;

namespace BackWave.Tests.Simulation;

public class SimulatorTests
{
    /// <summary>
    /// The PR seed battery: fixed seeds, each driving a 3-node cluster through 2 simulated
    /// hours of crashes, heartbeat loss, clock skew, and executions outliving their Leases.
    /// The oracle runs after every step; any failure names its seed and replays exactly.
    /// </summary>
    [Theory]
    [InlineData(1UL)]
    [InlineData(2UL)]
    [InlineData(3UL)]
    [InlineData(1337UL)]
    [InlineData(0xDEADBEEFUL)]
    [InlineData(42_424_242UL)]
    [InlineData(987_654_321UL)]
    [InlineData(0x5EED_5EED_5EEDUL)]
    [InlineData(2026_06_10UL)]
    [InlineData(ulong.MaxValue)]
    public void SeedBattery_SurvivesFaultInjection_AllJobsTerminal(ulong seed)
    {
        var result = new Simulator(new SimulationOptions { Seed = seed }).Run();

        Assert.Equal(200, result.FinalJobs.Count);
        Assert.Equal(200, result.Succeeded + result.DeadLettered + result.Cancelled);
    }

    [Fact]
    public void SameSeed_ReproducesTheIdenticalRun()
    {
        var options = new SimulationOptions { Seed = 1337 };
        var first = new Simulator(options).Run();
        var second = new Simulator(options).Run();

        Assert.Equal(first.Steps, second.Steps);
        Assert.Equal(first.Crashes, second.Crashes);
        Assert.Equal(first.StaleOutcomes, second.StaleOutcomes);
        Assert.Equal(first.FinalJobs, second.FinalJobs);
    }

    [Fact]
    public void SameSeed_RecordsTheIdenticalTransitionLog()
    {
        // The Transition Log (issue 0057) is observability the store records under Virtual Time,
        // so it must be as deterministic as everything else: same seed → byte-identical timelines.
        var options = new SimulationOptions { Seed = 1337 };
        var first = new Simulator(options).Run();
        var second = new Simulator(options).Run();

        // Flatten to a fully value-typed shape: IReadOnlyList has no structural equality, so the
        // nested timelines are compared element-by-element here, not by list reference.
        static IReadOnlyList<(Guid, DateTimeOffset, JobState, int)> Flatten(SimulationResult r) =>
            [.. r.FinalTransitions.SelectMany(e => e.Timeline.Select(t => (e.JobId, t.Timestamp, t.State, t.Attempt)))];

        Assert.Equal(Flatten(first), Flatten(second));
        Assert.All(first.FinalTransitions, t => Assert.NotEmpty(t.Timeline)); // every job has a history
    }

    [Theory]
    [InlineData(1UL)]
    [InlineData(1337UL)]
    [InlineData(0xDEADBEEFUL)]
    public void TheSimulator_RecordsNoFailureDetail_AndReplaysIdentically(ulong seed)
    {
        // Failure Detail (issue 0059) is captured Shell-side only on a REAL handler throw. The
        // Simulator drives Drivers with injected faults and fake handlers — no real exceptions —
        // so no transition may carry detail; and the run is still byte-identical from a seed, so
        // the production-only detail never perturbed the deterministic Core.
        var options = new SimulationOptions { Seed = seed };
        var first = new Simulator(options).Run();
        var second = new Simulator(options).Run();

        Assert.False(first.AnyFailureDetailRecorded, $"seed {seed} recorded Failure Detail under simulation");
        Assert.False(second.AnyFailureDetailRecorded);

        static IReadOnlyList<(Guid, DateTimeOffset, JobState, int)> Flatten(SimulationResult r) =>
            [.. r.FinalTransitions.SelectMany(e => e.Timeline.Select(t => (e.JobId, t.Timestamp, t.State, t.Attempt)))];
        Assert.Equal(Flatten(first), Flatten(second)); // identical replay, undisturbed by Failure Detail
    }

    [Theory]
    [InlineData(1UL)]
    [InlineData(1337UL)]
    [InlineData(0xDEADBEEFUL)]
    public void TheTransitionLog_AgreesWithEachJobsFinalState_AndAttempt(ulong seed)
    {
        // The recorded timeline ends exactly where the job ended: its last transition is the
        // job's final (State, Attempt). The log is the orchestration history, never out of step
        // with the snapshot the Core produced.
        var result = new Simulator(new SimulationOptions { Seed = seed }).Run();
        var finalById = result.FinalJobs.ToDictionary(j => j.JobId, j => (j.State, j.Attempt));

        Assert.All(result.FinalTransitions, entry =>
        {
            Assert.NotEmpty(entry.Timeline);
            var last = entry.Timeline[^1];
            var (state, attempt) = finalById[entry.JobId];
            Assert.Equal(state, last.State);
            Assert.Equal(attempt, last.Attempt);
            // The first transition is the enqueue's resulting state: Scheduled, AwaitingParent,
            // or Cancelled (a Dependency whose parent set was already terminal against it at
            // enqueue). Never Leased or a ran-to-completion terminal first.
            Assert.Contains(entry.Timeline[0].State,
                new[] { JobState.Scheduled, JobState.AwaitingParent, JobState.Cancelled });
        });
    }

    [Fact]
    public void DifferentSeeds_ProduceDifferentRuns()
    {
        var first = new Simulator(new SimulationOptions { Seed = 1 }).Run();
        var second = new Simulator(new SimulationOptions { Seed = 2 }).Run();

        Assert.NotEqual(first.FinalJobs, second.FinalJobs);
    }

    /// <summary>
    /// Named scenario: lease-expiry-during-GC-pause. Heartbeat loss is high and executions
    /// regularly outlive the Lease, so jobs get re-claimed while the original execution is
    /// still running — the resulting stale outcomes must be rejected, never double-applied.
    /// </summary>
    [Theory]
    [InlineData(7UL)]
    [InlineData(8UL)]
    [InlineData(9UL)]
    public void GcPauseRegime_DoubleExecutionsResolveByLeaseFencing(ulong seed)
    {
        var result = new Simulator(new SimulationOptions
        {
            Seed = seed,
            CrashProbabilityPerPoll = 0,
            HeartbeatLossProbability = 0.5,
            MaxExecutionDuration = TimeSpan.FromSeconds(150),
            HandlerFailureProbability = 0,
        }).Run();

        // The regime must actually produce contested completions, and every job must
        // still land in exactly one terminal state. Dead-letters are legitimate here:
        // each expiry of a still-running execution burns an Attempt by design.
        Assert.True(result.StaleOutcomes > 0, $"seed {seed}: regime produced no stale outcomes");
        Assert.Equal(200, result.Succeeded + result.DeadLettered + result.Cancelled);
    }

    /// <summary>
    /// Named scenario: Concurrency-Limits-never-exceeded. A limit of 5 across the cluster
    /// under crashes and heartbeat loss; the oracle asserts I3 after every step, including
    /// slot release via Lease expiry from crashed nodes.
    /// </summary>
    [Theory]
    [InlineData(21UL)]
    [InlineData(22UL)]
    [InlineData(23UL)]
    public void ConcurrencyLimitRegime_LimitHoldsClusterWideUnderFaults(ulong seed)
    {
        var result = new Simulator(new SimulationOptions
        {
            Seed = seed,
            ConcurrencyLimit = 5,
            JobCount = 100,
        }).Run();

        Assert.Equal(100, result.Succeeded + result.DeadLettered + result.Cancelled);
    }

    /// <summary>
    /// Named scenario: hint-loss equivalence (ADR-0005, spec §8). The same seeded workload
    /// runs with every hint dropped, every hint delivered, and half delivered; the final
    /// state must be identical — a hint may only ever change *when* a poll happens.
    /// Handler failures stay on (they're keyed per Attempt); crashes and heartbeat loss are
    /// off because those faults are coupled to event order by construction, not via hints.
    /// </summary>
    [Theory]
    [InlineData(31UL)]
    [InlineData(32UL)]
    [InlineData(33UL)]
    public void HintLossEquivalence_FinalStateIsIdentical_AtAnyDeliveryRate(ulong seed)
    {
        SimulationResult Run(double delivery) => new Simulator(new SimulationOptions
        {
            Seed = seed,
            CrashProbabilityPerPoll = 0,
            HeartbeatLossProbability = 0,
            HintDeliveryProbability = delivery,
        }).Run();

        var dropped = Run(0);
        var delivered = Run(1);
        var lossy = Run(0.5);

        Assert.True(delivered.Steps > dropped.Steps, $"seed {seed}: hints never fired");
        Assert.Equal(dropped.FinalJobs, delivered.FinalJobs);
        Assert.Equal(dropped.FinalJobs, lossy.FinalJobs);
    }

    /// <summary>
    /// Named scenario: I1 (no double execution under a live Lease) under GC-pause and clock
    /// skew. Executions routinely outlive their Leases and clocks disagree, so jobs run on
    /// multiple nodes at once — yet the oracle (now checking I1 every step) must never find two
    /// nodes each holding the live Lease for the same job.
    /// </summary>
    [Theory]
    [InlineData(51UL)]
    [InlineData(52UL)]
    [InlineData(53UL)]
    public void I1Regime_NeverTwoLiveLeaseExecutionsUnderGcPauseAndSkew(ulong seed)
    {
        var result = new Simulator(new SimulationOptions
        {
            Seed = seed,
            CrashProbabilityPerPoll = 0,
            HeartbeatLossProbability = 0.5,
            MaxExecutionDuration = TimeSpan.FromSeconds(150),
            MaxClockSkew = TimeSpan.FromSeconds(10),
        }).Run();

        Assert.True(result.StaleOutcomes > 0, $"seed {seed}: regime produced no contested executions");
        Assert.Equal(200, result.Succeeded + result.DeadLettered + result.Cancelled);
    }

    /// <summary>
    /// Oracle self-test: a silently dead oracle looks identical to a healthy codebase. With
    /// Sabotage on, the store admits past the Concurrency Limit the oracle enforces, so a
    /// working oracle MUST fail the run — and the failure MUST print the replay seed.
    /// </summary>
    [Theory]
    [InlineData(61UL)]
    [InlineData(62UL)]
    public void OracleSelfTest_ADeliberateViolation_FailsTheRun_AndPrintsTheSeed(ulong seed)
    {
        var exception = Assert.Throws<SimulationInvariantException>(() =>
            new Simulator(new SimulationOptions { Seed = seed, ConcurrencyLimit = 1, Sabotage = true }).Run());

        Assert.Contains($"seed {seed}", exception.Message);
        Assert.Contains("Concurrency Limit", exception.Message); // the I3 limit-exceeded invariant fired
        Assert.Equal(InvariantId.ConcurrencyLimit, exception.InvariantId); // matched by identity, not message
    }

    /// <summary>
    /// The structured invariant ID (issue 0085) is the Seed Minimizer's (0088) and the VOPR Runner's (0087)
    /// dedup key — "same failure" is matched on it, never on the message. So it must survive a FailureStamp's
    /// JSON round-trip by identity, serialized as its stable name rather than a fragile ordinal.
    /// </summary>
    [Fact]
    public void TrippedInvariantId_RoundTripsThroughTheFailureStamp_ByStableIdentity()
    {
        var exception = Assert.Throws<SimulationInvariantException>(() =>
            new Simulator(new SimulationOptions { Seed = 61, ConcurrencyLimit = 1, Sabotage = true }).Run());
        Assert.Equal(InvariantId.ConcurrencyLimit, exception.InvariantId);

        var plan = new Plan
        {
            Scenario = Scenario.FromOptions(new SimulationOptions { Seed = 61 }),
            FaultMap = [],
            Failure = new FailureStamp(exception.Message, exception.InvariantId),
        };
        var json = PlanJson.Serialize(plan);
        Assert.Contains("ConcurrencyLimit", json); // serialized by stable name, not an ordinal

        var roundTripped = PlanJson.Deserialize(json);
        Assert.Equal(InvariantId.ConcurrencyLimit, roundTripped.Failure!.InvariantId);
    }

    /// <summary>
    /// Named scenario: storage faults. Hot-path store calls throw transiently during the
    /// workload; the cluster must absorb them — a faulted node action retries on its next
    /// tick, a faulted enqueue is retried by the client — and once faults stop, every job
    /// still converges to a terminal state with all invariants intact.
    /// </summary>
    [Theory]
    [InlineData(71UL)]
    [InlineData(72UL)]
    [InlineData(73UL)]
    public void StorageFaultRegime_ConvergesAfterFaultsSubside_AllInvariantsIntact(ulong seed)
    {
        var result = new Simulator(new SimulationOptions
        {
            Seed = seed,
            JobCount = 100,
            StoreFaultProbability = 0.2,
            CrashProbabilityPerPoll = 0,
            HeartbeatLossProbability = 0,
        }).Run();

        // No lost jobs: all 100 enqueued (faulted enqueues retried) and all terminal.
        Assert.Equal(100, result.FinalJobs.Count);
        Assert.Equal(100, result.Succeeded + result.DeadLettered + result.Cancelled);
    }

    /// <summary>
    /// Named scenario: crash-between-commit. Crashes are frequent, so nodes constantly die
    /// between the claim commit and the outcome commit; Lease expiry must recover every
    /// orphaned execution with the expiry counted as an Attempt.
    /// </summary>
    [Theory]
    [InlineData(11UL)]
    [InlineData(12UL)]
    [InlineData(13UL)]
    public void CrashHeavyRegime_LeaseExpiryRecoversEverything(ulong seed)
    {
        var result = new Simulator(new SimulationOptions
        {
            Seed = seed,
            CrashProbabilityPerPoll = 0.05,
            MaxCrashDowntime = TimeSpan.FromMinutes(2),
            HandlerFailureProbability = 0,
        }).Run();

        Assert.True(result.Crashes > 0, $"seed {seed}: regime produced no crashes");
        Assert.Equal(200, result.Succeeded + result.DeadLettered + result.Cancelled);
    }

    /// <summary>
    /// Named scenario: Operator Actions racing cluster chaos (§5.8). Cancel, Requeue, Pause/Resume,
    /// and TriggerScheduleNow are injected across the workload while crashes, heartbeat loss, clock
    /// skew, and lease-outliving executions run underneath. The oracle (now including the Pause
    /// invariant) holds every step, and every job still converges to exactly one terminal state —
    /// the operator surface composes safely with the distributed state machine.
    /// </summary>
    [Theory]
    [InlineData(81UL)]
    [InlineData(82UL)]
    [InlineData(83UL)]
    public void OperatorActionRegime_RacesActionsAgainstChaos_EverythingConverges(ulong seed)
    {
        var result = new Simulator(new SimulationOptions
        {
            Seed = seed,
            OperatorActionCount = 80,
        }).Run();

        Assert.Equal(200, result.FinalJobs.Count);
        Assert.Equal(200, result.Succeeded + result.DeadLettered + result.Cancelled);
        // The regime actually exercised the operator surface (Cancel and Pause fire reliably under
        // this workload); Requeue needs a dead job to exist and is asserted in its own scenario.
        Assert.True(result.OperatorCancels > 0, $"seed {seed}: no jobs were operator-cancelled");
        Assert.True(result.QueuePauses > 0, $"seed {seed}: the Queue was never Paused");
    }

    /// <summary>
    /// Named scenario: Operator Requeue of dead jobs (§5.8) — the one legal terminal→non-terminal
    /// move. Every handler fails, so jobs Dead-Letter quickly and the operator revives them; the
    /// revived jobs (Attempt budget reset) re-run and, once the actions stop at WorkloadEnd, drain
    /// to terminal. The oracle's terminal-stability check must tolerate the revival yet still guard
    /// every other terminal change.
    /// </summary>
    [Theory]
    [InlineData(91UL)]
    [InlineData(92UL)]
    [InlineData(93UL)]
    public void OperatorRequeueRegime_RevivesDeadJobs_AndStillConverges(ulong seed)
    {
        var result = new Simulator(new SimulationOptions
        {
            Seed = seed,
            JobCount = 100,
            OperatorActionCount = 120,
            HandlerFailureProbability = 1.0, // every Attempt fails → a steady supply of dead jobs
            CrashProbabilityPerPoll = 0,
        }).Run();

        Assert.Equal(100, result.FinalJobs.Count);
        Assert.Equal(100, result.Succeeded + result.DeadLettered + result.Cancelled);
        Assert.True(result.OperatorRequeues > 0, $"seed {seed}: no dead jobs were requeued");
    }

    /// <summary>
    /// Oracle self-test for the Pause invariant: with SabotagePausedClaim the store is NOT actually
    /// paused, but the oracle is told the Queue is Paused — so the claims that keep flowing MUST
    /// trip the invariant. A silently dead Pause oracle would let this run pass; a live one fails it
    /// and prints the replay seed.
    /// </summary>
    [Theory]
    [InlineData(95UL)]
    [InlineData(96UL)]
    public void OperatorPauseSelfTest_AClaimWhilePaused_FailsTheRun_AndPrintsTheSeed(ulong seed)
    {
        var exception = Assert.Throws<SimulationInvariantException>(() =>
            new Simulator(new SimulationOptions
            {
                Seed = seed,
                OperatorActionCount = 80,
                SabotagePausedClaim = true,
            }).Run());

        Assert.Contains($"seed {seed}", exception.Message);
        Assert.Contains("Paused", exception.Message); // the Pause invariant fired
        Assert.Equal(InvariantId.PausedClaim, exception.InvariantId);
    }

    /// <summary>
    /// Same seed and operator count reproduce the identical run: the Operator Actions draw from
    /// their own rng stream, so the regime is as deterministic as every other fault stream.
    /// </summary>
    [Fact]
    public void OperatorActionRegime_SameSeed_ReproducesTheIdenticalRun()
    {
        var options = new SimulationOptions { Seed = 1337, OperatorActionCount = 80 };
        var first = new Simulator(options).Run();
        var second = new Simulator(options).Run();

        Assert.Equal(first.FinalJobs, second.FinalJobs);
        Assert.Equal(first.OperatorCancels, second.OperatorCancels);
        Assert.Equal(first.OperatorCancelRequests, second.OperatorCancelRequests);
        Assert.Equal(first.CooperativeCancels, second.CooperativeCancels);
        Assert.Equal(first.OperatorRequeues, second.OperatorRequeues);
        Assert.Equal(first.QueuePauses, second.QueuePauses);
    }

    /// <summary>
    /// Named scenario: cooperative cancellation of a Leased job (§5.8) — the heartbeat round-trip.
    /// The operator cancels jobs mid-execution; the request rides the next Heartbeat back as
    /// CancelRequested, the Driver emits SignalCancellation, and the handler unwinds as
    /// ExecutionCancelled → ReportOutcome(Cancelled). Long executions keep jobs Leased so the cancel
    /// can land, while clock skew and lease-outliving executions let the signal race completion and
    /// lease lapse underneath. Every job still converges, the cancellation-provenance oracle holds
    /// every step, and the round-trip provably fires and completes (not merely composes).
    /// </summary>
    [Theory]
    [InlineData(111UL)]
    [InlineData(112UL)]
    [InlineData(113UL)]
    public void CooperativeCancelRegime_CancelsLeasedJobsMidFlight_AndConverges(ulong seed)
    {
        var result = new Simulator(new SimulationOptions
        {
            Seed = seed,
            OperatorActionCount = 200,
            MaxExecutionDuration = TimeSpan.FromSeconds(150), // executions stay Leased long enough to cancel
            HeartbeatLossProbability = 0,                     // the signal must reach the node
            CrashProbabilityPerPoll = 0,                      // keep leases alive so the round-trip can complete
        }).Run();

        Assert.Equal(200, result.FinalJobs.Count);
        Assert.Equal(200, result.Succeeded + result.DeadLettered + result.Cancelled);
        Assert.True(result.OperatorCancelRequests > 0, $"seed {seed}: no Leased job was ever cancel-requested");
        Assert.True(result.CooperativeCancels > 0, $"seed {seed}: the cooperative cancel round-trip never completed");
    }

    /// <summary>
    /// Oracle self-test for the cancellation-provenance invariant: with SabotageCancelProvenance the
    /// operator cancels still hit the store (jobs genuinely reach Cancelled), but the sim withholds
    /// the provenance record — so a real cancellation surfaces as unprovenanced. A live oracle MUST
    /// fail the run and print the replay seed; a silently dead one would let it pass.
    /// </summary>
    [Theory]
    [InlineData(121UL)]
    [InlineData(122UL)]
    public void CancelProvenanceSelfTest_AnUnprovenancedCancel_FailsTheRun_AndPrintsTheSeed(ulong seed)
    {
        var exception = Assert.Throws<SimulationInvariantException>(() =>
            new Simulator(new SimulationOptions
            {
                Seed = seed,
                OperatorActionCount = 80,
                SabotageCancelProvenance = true,
            }).Run());

        Assert.Contains($"seed {seed}", exception.Message);
        Assert.Contains("provenance", exception.Message); // the cancellation-provenance invariant fired
        Assert.Equal(InvariantId.CancelProvenance, exception.InvariantId);
    }

    /// <summary>
    /// Oracle self-test for the legal-transition invariant (§3): the store records only legal edges,
    /// so the sim splices a single illegal Succeeded→Leased edge into the history the oracle walks. A
    /// live walk MUST reject it and fail with the replay seed; a silently dead one would let it pass.
    /// </summary>
    [Theory]
    [InlineData(131UL)]
    [InlineData(132UL)]
    public void LegalTransitionSelfTest_AnIllegalEdge_FailsTheRun_AndPrintsTheSeed(ulong seed)
    {
        var exception = Assert.Throws<SimulationInvariantException>(() =>
            new Simulator(new SimulationOptions { Seed = seed, SabotageLegalTransition = true }).Run());

        Assert.Contains($"seed {seed}", exception.Message);
        Assert.Contains("illegal transition", exception.Message); // the legal-transition invariant fired
        Assert.Equal(InvariantId.LegalTransition, exception.InvariantId);
    }

    /// <summary>
    /// Oracle self-test for the at-least-once-execute liveness invariant (issue 0042): jobs still run
    /// and reach their terminal states for real, but the sim withholds the "this job executed" record,
    /// so a ran-to-completion terminal surfaces as never-executed. A live oracle MUST fail with the
    /// replay seed; a silently dead one would let it pass.
    /// </summary>
    [Theory]
    [InlineData(141UL)]
    [InlineData(142UL)]
    public void ExecuteLivenessSelfTest_ATerminalThatNeverRan_FailsTheRun_AndPrintsTheSeed(ulong seed)
    {
        var exception = Assert.Throws<SimulationInvariantException>(() =>
            new Simulator(new SimulationOptions { Seed = seed, SabotageExecuteLiveness = true }).Run());

        Assert.Contains($"seed {seed}", exception.Message);
        Assert.Contains("never executed", exception.Message); // the at-least-once-execute invariant fired
        Assert.Equal(InvariantId.ExecuteLiveness, exception.InvariantId);
    }

    /// <summary>
    /// Oracle self-test for the audit-log-completeness invariant (§5.8): every Operator Action still
    /// hits the store and is recorded for real, but the sim tallies one phantom extra action the store
    /// never logged, so the sim's tally carries one more entry than the audit log — exactly as a dropped
    /// audit row would surface. A live oracle MUST catch the mismatch and fail with the replay seed.
    /// </summary>
    [Theory]
    [InlineData(151UL)]
    [InlineData(152UL)]
    public void AuditCompletenessSelfTest_AnUntalliedAction_FailsTheRun_AndPrintsTheSeed(ulong seed)
    {
        var exception = Assert.Throws<SimulationInvariantException>(() =>
            new Simulator(new SimulationOptions
            {
                Seed = seed,
                OperatorActionCount = 80,
                SabotageAuditCompleteness = true,
            }).Run());

        Assert.Contains($"seed {seed}", exception.Message);
        Assert.Contains("audit-log", exception.Message); // the audit-completeness invariant fired
        Assert.Equal(InvariantId.AuditCompleteness, exception.InvariantId);
    }

    /// <summary>
    /// Named scenario: Operator TriggerScheduleNow (§5.8) raced against chaos — the one operator path
    /// the other operator regimes never reach, because they seed no schedules and OperatorTrigger
    /// returns early every time. Here a schedule is seeded alongside the full operator surface, so the
    /// operator's on-demand triggers mint an instance immediately and race the live cluster. Every
    /// instance — triggered or regular cron mint — must drain terminal once the schedule is removed at
    /// WorkloadEnd; this also gives the audit-completeness oracle its first TriggerScheduleNow rows and
    /// the legal-transition walk its first minted-via-trigger jobs.
    /// </summary>
    [Theory]
    [InlineData(161UL)]
    [InlineData(162UL)]
    [InlineData(163UL)]
    public void OperatorTriggerRegime_MintsScheduleOnDemand_RacesChaos_AndConverges(ulong seed)
    {
        var result = new Simulator(new SimulationOptions
        {
            Seed = seed,
            JobCount = 100,
            OperatorActionCount = 200,
            Schedules =
            [
                new SeededSchedule { Id = "ops-hourly", Cron = "0 * * * *" },
            ],
        }).Run();

        Assert.Equal(100, result.FinalJobs.Count);
        Assert.Equal(100, result.Succeeded + result.DeadLettered + result.Cancelled);
        Assert.True(result.ScheduleTriggers > 0, $"seed {seed}: the operator never triggered the schedule");
        Assert.NotEmpty(result.MintedJobs);
        Assert.All(result.MintedJobs, m => Assert.True(m.State.IsTerminal(), $"seed {seed}: {m.Tick:O} not terminal"));
    }

    // --- Node Isolation suites (issue 0068, ADR 0013): healing isolation + Effect-Once provenance ---

    /// <summary>
    /// Named scenario: healing-isolation safety — Effect-Once at the Storage Contract boundary. Nodes
    /// are cut off from storage for bounded windows while they keep executing on a stale Lease belief,
    /// then heal. With crashes and heartbeat loss OFF, the ONLY way a Lease can lapse is isolation, so
    /// every stale outcome is the heal-into-stale-write race: the isolated node's Lease lapsed, a
    /// survivor re-leased (Attempt incremented), and the healed node's stale ReportOutcome hit the
    /// (workerId, attempt) fence and mutated nothing. The Outcome-Provenance Oracle holds every step,
    /// and every job still converges to exactly one terminal state.
    /// </summary>
    [Theory]
    [InlineData(501UL)]
    [InlineData(502UL)]
    [InlineData(503UL)]
    public void HealingIsolationRegime_StaleReportsAreFenced_AndConverges(ulong seed)
    {
        var result = new Simulator(new SimulationOptions
        {
            Seed = seed,
            IsolationCount = 40,
            CrashProbabilityPerPoll = 0,                     // isolation is the only source of lapsed Leases
            HeartbeatLossProbability = 0,                    // ditto: a renewed Lease never lapses on its own
            MaxExecutionDuration = TimeSpan.FromSeconds(150), // executions stay in flight when isolation hits
        }).Run();

        Assert.Equal(200, result.FinalJobs.Count);
        Assert.Equal(200, result.Succeeded + result.DeadLettered + result.Cancelled);
        Assert.True(result.Isolations > 0, $"seed {seed}: no node was ever isolated");
        // The race actually fired: a healed node reported on a Lease a survivor had already taken, and
        // the fence rejected it. With crash/heartbeat-loss off, a stale outcome can come from nowhere else.
        Assert.True(result.StaleOutcomes > 0, $"seed {seed}: isolation produced no fenced stale reports");
    }

    /// <summary>
    /// Same seed and isolation count reproduce the identical run: episodes draw from their own
    /// <c>Seed ^ "ISOLATION"</c> stream, so the regime is as deterministic as every other fault stream.
    /// </summary>
    [Fact]
    public void IsolationRegime_SameSeed_ReproducesTheIdenticalRun()
    {
        var options = new SimulationOptions
        {
            Seed = 511,
            IsolationCount = 40,
            CrashProbabilityPerPoll = 0,
            HeartbeatLossProbability = 0,
            MaxExecutionDuration = TimeSpan.FromSeconds(150),
        };
        var first = new Simulator(options).Run();
        var second = new Simulator(options).Run();

        Assert.Equal(first.FinalJobs, second.FinalJobs);
        Assert.Equal(first.Isolations, second.Isolations);
        Assert.Equal(first.StaleOutcomes, second.StaleOutcomes);
    }

    /// <summary>
    /// Determinism guard: Node Isolation is gated behind a default-0 knob on its own rng stream, so an
    /// IsolationCount of 0 takes zero draws and leaves the run byte-identical to the ungated baseline —
    /// the property the whole seed battery rests on — while a non-zero count provably perturbs it.
    /// </summary>
    [Theory]
    [InlineData(1UL)]
    [InlineData(1337UL)]
    public void IsolationCountZero_IsByteIdenticalToTheBaseline_AndNonZeroPerturbsIt(ulong seed)
    {
        var baseline = new Simulator(new SimulationOptions { Seed = seed }).Run();
        var stillBaseline = new Simulator(new SimulationOptions { Seed = seed, IsolationCount = 0 }).Run();

        Assert.Equal(0, baseline.Isolations);
        Assert.Equal(baseline.Steps, stillBaseline.Steps);
        Assert.Equal(baseline.FinalJobs, stillBaseline.FinalJobs);
        Assert.Equal(baseline.StaleOutcomes, stillBaseline.StaleOutcomes);

        var isolated = new Simulator(new SimulationOptions { Seed = seed, IsolationCount = 40 }).Run();
        Assert.True(isolated.Isolations > 0, $"seed {seed}: the isolation knob did nothing");
        Assert.NotEqual(baseline.FinalJobs, isolated.FinalJobs); // a different, but still-converging, run
    }

    /// <summary>
    /// Oracle self-test for the Outcome-Provenance invariant (issue 0068, ADR 0013): with
    /// SabotageOutcomeFence the store drops the (workerId, attempt) fence, so a healed node's stale
    /// ReportOutcome is forced through under the current Lease holder's identity and mutates a job it does
    /// not own — a real Effect-Once violation that reaches a perfectly legal terminal state, invisible to
    /// every other oracle. A live provenance oracle MUST catch the applied-by-a-non-holder outcome and
    /// fail with the replay seed; a silently dead one would let it pass.
    /// </summary>
    [Theory]
    [InlineData(521UL)]
    [InlineData(522UL)]
    public void OutcomeProvenanceSelfTest_AStaleWriteThatLands_FailsTheRun_AndPrintsTheSeed(ulong seed)
    {
        var exception = Assert.Throws<SimulationInvariantException>(() =>
            new Simulator(new SimulationOptions
            {
                Seed = seed,
                IsolationCount = 40,
                CrashProbabilityPerPoll = 0,
                HeartbeatLossProbability = 0,
                MaxExecutionDuration = TimeSpan.FromSeconds(150),
                SabotageOutcomeFence = true,
            }).Run());

        Assert.Contains($"seed {seed}", exception.Message);
        Assert.Contains("Provenance", exception.Message); // the Outcome-Provenance invariant fired
        Assert.Equal(InvariantId.OutcomeProvenance, exception.InvariantId);
    }

    /// <summary>
    /// Oracle self-test for the VECTORIZED Outcome-Provenance fence (ADR 0035): SabotageBatchFence models a
    /// native batch report that fences single reports correctly but applies a MULTI-row batch as a whole —
    /// so a stale row riding alongside live ones lands. A live Outcome-Provenance oracle MUST catch the
    /// applied-by-a-non-holder row, proving the fence is enforced per row in the batch path, not just on the
    /// batch as a whole. Runs at a Pristine fault level (crashes and heartbeat loss off) so the sabotage's
    /// injected violation is the only thing that can trip the oracle; the batched twin of
    /// <see cref="OutcomeProvenanceSelfTest_AStaleWriteThatLands_FailsTheRun_AndPrintsTheSeed"/>.
    /// </summary>
    [Theory]
    [InlineData(1UL)]
    [InlineData(2UL)]
    public void BatchFenceSelfTest_AStaleRowThatLandsInABatch_FailsTheRun_AndPrintsTheSeed(ulong seed)
    {
        var exception = Assert.Throws<SimulationInvariantException>(() =>
            new Simulator(new SimulationOptions
            {
                Seed = seed,
                IsolationCount = 40,
                CrashProbabilityPerPoll = 0,
                HeartbeatLossProbability = 0,
                MaxExecutionDuration = TimeSpan.FromSeconds(150),
                SabotageBatchFence = true,
            }).Run());

        Assert.Contains($"seed {seed}", exception.Message);
        Assert.Contains("Provenance", exception.Message); // the per-row Outcome-Provenance fence fired
        Assert.Equal(InvariantId.OutcomeProvenance, exception.InvariantId);
    }

    /// <summary>
    /// Named scenario: permanent node loss + migration liveness (issue 0069). Half the isolation episodes
    /// never heal — nodes lost for good — while crashes and heartbeat loss stay OFF, so the only way a Lease
    /// ever lapses is isolation and the only way a job a lost node held can reach terminal is to MIGRATE: its
    /// Lease expires, a survivor re-leases or terminalizes it. The Migration-Liveness Oracle holds every step
    /// (no job stuck on a lost node past its config-derived bound), and the run still converges by DrainEnd —
    /// the headline liveness property a permanently-lost node must not break. A positive expired count proves
    /// migration actually fired rather than the lost nodes having held no work.
    /// </summary>
    [Theory]
    [InlineData(601UL)]
    [InlineData(602UL)]
    [InlineData(606UL)]
    public void PermanentLossRegime_LostNodesWorkMigrates_AndConverges(ulong seed)
    {
        var result = new Simulator(new SimulationOptions
        {
            Seed = seed,
            IsolationCount = 30,
            PermanentLossProbability = 0.5,
            CrashProbabilityPerPoll = 0,                      // isolation is the only source of lapsed Leases
            HeartbeatLossProbability = 0,
            MaxExecutionDuration = TimeSpan.FromSeconds(150), // lost nodes are holding work when they vanish
        }).Run();

        Assert.Equal(200, result.FinalJobs.Count);
        Assert.Equal(200, result.Succeeded + result.DeadLettered + result.Cancelled);
        Assert.True(result.PermanentLosses > 0, $"seed {seed}: no node was ever permanently lost");
        // A permanently-lost node never reports, so convergence is impossible unless its work migrated:
        // a positive sweep count is migration firing, and full terminality is migration completing.
        Assert.True(result.LeasesExpired > 0, $"seed {seed}: no Lease ever migrated off a lost node");
    }

    /// <summary>
    /// Determinism guard: permanent loss is a never-healing isolation gated behind its own default-0 knob.
    /// With PermanentLossProbability 0 the permanent draw short-circuits, so an isolation run is byte-identical
    /// whether or not the knob is named — the property the healing-isolation battery rests on — while a
    /// positive probability provably introduces permanent losses and still converges.
    /// </summary>
    [Theory]
    [InlineData(601UL)]
    [InlineData(606UL)]
    public void PermanentLossProbabilityZero_IsByteIdenticalToTheHealingRegime_AndNonZeroAddsLosses(ulong seed)
    {
        SimulationResult Run(double permanentLossProbability) => new Simulator(new SimulationOptions
        {
            Seed = seed,
            IsolationCount = 30,
            PermanentLossProbability = permanentLossProbability,
            CrashProbabilityPerPoll = 0,
            HeartbeatLossProbability = 0,
            MaxExecutionDuration = TimeSpan.FromSeconds(150),
        }).Run();

        var healing = Run(0);
        Assert.Equal(0, healing.PermanentLosses);

        var withLoss = Run(0.5);
        Assert.True(withLoss.PermanentLosses > 0, $"seed {seed}: the permanent-loss knob did nothing");
        Assert.Equal(200, withLoss.Succeeded + withLoss.DeadLettered + withLoss.Cancelled);
    }

    /// <summary>
    /// Oracle self-test for the Migration-Liveness invariant (issue 0069): with SabotageMigrationSweep no
    /// survivor ever sweeps ExpireLeases, so a Lease that lapses under an isolated holder never expires in the
    /// store and its job can never migrate — it sits stranded on a node that will never report. A live
    /// migration oracle MUST flag the stuck job at its exact (jobId, time) once the config-derived bound
    /// lapses, and fail with the replay seed; a silently dead one would wait for the vague end-of-run check.
    /// </summary>
    [Theory]
    [InlineData(623UL)]
    [InlineData(624UL)]
    [InlineData(625UL)]
    public void MigrationLivenessSelfTest_AnUnsweptLostLease_FailsTheRun_AndPrintsTheSeed(ulong seed)
    {
        var exception = Assert.Throws<SimulationInvariantException>(() =>
            new Simulator(new SimulationOptions
            {
                Seed = seed,
                IsolationCount = 30,
                PermanentLossProbability = 0.5,
                CrashProbabilityPerPoll = 0,
                HeartbeatLossProbability = 0,
                MaxExecutionDuration = TimeSpan.FromSeconds(150),
                SabotageMigrationSweep = true,
            }).Run());

        Assert.Contains($"seed {seed}", exception.Message);
        Assert.Contains("migration-liveness", exception.Message); // the Migration-Liveness invariant fired
        Assert.Equal(InvariantId.MigrationLiveness, exception.InvariantId);
    }

    /// <summary>
    /// Named scenario: ack-loss isolation (issue 0070, Phase 1.5). On a selected outcome the store write
    /// COMMITS but the node's ack is lost, so the node believes the report failed and re-reports once it is
    /// back — by which point the store has moved on (a committed Failure became due and a survivor
    /// re-leased it on a fresh Attempt). This exercises the (workerId, attempt) fence and Effect-Once on the
    /// path where the write actually LANDED, not merely where every call failed. The retried write is
    /// stale, so the fence rejects it and the Outcome-Provenance Oracle stays green; every job still
    /// converges, the committed outcome standing exactly once.
    /// </summary>
    [Theory]
    [InlineData(701UL)]
    [InlineData(702UL)]
    [InlineData(703UL)]
    public void AckLossRegime_CommittedWritesRetryStaleAndAreFenced_AndConverges(ulong seed)
    {
        var result = new Simulator(new SimulationOptions
        {
            Seed = seed,
            AckLossProbability = 0.3,
            CrashProbabilityPerPoll = 0,   // ack-loss is the only source of lost reports
            HeartbeatLossProbability = 0,
        }).Run();

        Assert.Equal(200, result.FinalJobs.Count);
        Assert.Equal(200, result.Succeeded + result.DeadLettered + result.Cancelled);
        Assert.True(result.AckLosses > 0, $"seed {seed}: no outcome write ever lost its ack");
        // Every ack-loss commits a write then forces a retry; the retry is stale, so the fence rejects it.
        Assert.True(result.StaleOutcomes > 0, $"seed {seed}: no committed-write retry was fenced");
    }

    /// <summary>
    /// Same seed and ack-loss probability reproduce the identical run: ack-loss draws from its own
    /// <c>Seed ^ "ACKLOSS"</c> stream, so it is as deterministic as every other fault, and a zero
    /// probability leaves the run byte-identical to the baseline (no draws, no behavior change).
    /// </summary>
    [Theory]
    [InlineData(1UL)]
    [InlineData(701UL)]
    public void AckLoss_IsDeterministic_AndZeroProbabilityIsByteIdenticalToTheBaseline(ulong seed)
    {
        SimulationResult Run(double ackLoss) => new Simulator(new SimulationOptions
        {
            Seed = seed,
            AckLossProbability = ackLoss,
            CrashProbabilityPerPoll = 0,
            HeartbeatLossProbability = 0,
        }).Run();

        var baseline = new Simulator(new SimulationOptions
        {
            Seed = seed,
            CrashProbabilityPerPoll = 0,
            HeartbeatLossProbability = 0,
        }).Run();
        var off = Run(0);
        Assert.Equal(0, off.AckLosses);
        Assert.Equal(baseline.Steps, off.Steps);
        Assert.Equal(baseline.FinalJobs, off.FinalJobs);

        var first = Run(0.3);
        var second = Run(0.3);
        Assert.Equal(first.FinalJobs, second.FinalJobs);
        Assert.Equal(first.AckLosses, second.AckLosses);
        Assert.True(first.AckLosses > 0, $"seed {seed}: the ack-loss knob did nothing");
    }

    /// <summary>
    /// Oracle self-test for ack-loss under a sabotaged fence (issue 0070, ADR 0013): handlers fail often,
    /// so ack-loss commits Failure writes that requeue the job; a survivor re-leases it on a fresh Attempt
    /// before the node's stale retry lands. With SabotageOutcomeFence the (workerId, attempt) fence is
    /// dropped, so that stale retry is forced through under the survivor's identity and mutates a job the
    /// retrying node no longer owns. The Outcome-Provenance Oracle MUST catch the applied-by-a-non-holder
    /// outcome and fail with the replay seed — proving the fence, not the absence of a race, is what keeps
    /// the ack-loss retry safe in the real regime.
    /// </summary>
    [Theory]
    [InlineData(721UL)]
    [InlineData(722UL)]
    [InlineData(723UL)]
    public void AckLossSelfTest_AStaleRetryUnderADroppedFence_FailsTheRun_AndPrintsTheSeed(ulong seed)
    {
        var exception = Assert.Throws<SimulationInvariantException>(() =>
            new Simulator(new SimulationOptions
            {
                Seed = seed,
                AckLossProbability = 0.5,
                HandlerFailureProbability = 0.6, // committed Failures that requeue and get re-leased
                CrashProbabilityPerPoll = 0,
                HeartbeatLossProbability = 0,
                SabotageOutcomeFence = true,
            }).Run());

        Assert.Contains($"seed {seed}", exception.Message);
        Assert.Contains("Provenance", exception.Message); // the Outcome-Provenance invariant fired
        Assert.Equal(InvariantId.OutcomeProvenance, exception.InvariantId);
    }

    // --- Recurring-Schedule suites (issue 0035): seeded schedules minted under cluster chaos ---

    public enum DstTransition { SpringForward, FallBack }

    /// <summary>
    /// Named scenario: DST minting across a transition. A daily America/New_York schedule and a
    /// sub-hour (every-minute) zoned schedule are minted by a 3-node cluster with crash and
    /// clock-skew injection, started a couple hours before either the spring-forward gap or the
    /// fall-back repeated hour. The instants minted must be exactly the zone's occurrences — the
    /// skipped 02:00 (spring) and the un-repeated second 01:00 (fall) included by construction —
    /// each minted once cluster-wide, with nothing silently lost. The oracle runs throughout.
    /// </summary>
    [Theory]
    [InlineData(DstTransition.SpringForward, 101UL)]
    [InlineData(DstTransition.SpringForward, 102UL)]
    [InlineData(DstTransition.FallBack, 201UL)]
    [InlineData(DstTransition.FallBack, 202UL)]
    public void DstRegime_MintsExactlyOncePerTick_AcrossTheTransition_UnderCrashAndSkew(
        DstTransition transition, ulong seed)
    {
        const string tz = "America/New_York";
        // Start ~an hour before the transition (in local terms) so the gap / repeated hour falls
        // squarely inside a short window; the daily cron targets the contested local hour.
        var (start, dailyCron) = transition switch
        {
            // 2026-03-08 01:00 EST; 02:00 is skipped → daily 2am remaps to 03:00 EDT.
            DstTransition.SpringForward => (new DateTimeOffset(2026, 3, 8, 6, 0, 0, TimeSpan.Zero), "0 2 * * *"),
            // 2026-11-01 00:00 EDT; 01:00 occurs twice → daily 1am fires the first occurrence only.
            _ => (new DateTimeOffset(2026, 11, 1, 4, 0, 0, TimeSpan.Zero), "0 1 * * *"),
        };

        var result = new Simulator(new SimulationOptions
        {
            Seed = seed,
            JobCount = 0,
            StartTime = start,
            WorkloadDuration = TimeSpan.FromHours(3),
            PollInterval = TimeSpan.FromSeconds(10),
            CrashProbabilityPerPoll = 0.01,
            MaxCrashDowntime = TimeSpan.FromSeconds(30),
            MaxClockSkew = TimeSpan.FromSeconds(5),
            Schedules =
            [
                new SeededSchedule { Id = "dst-daily", Cron = dailyCron, TimeZoneId = tz },
                new SeededSchedule { Id = "dst-minute", Cron = "* * * * *", TimeZoneId = tz },
            ],
        }).Run();

        Assert.True(result.Crashes > 0, $"seed {seed}: regime produced no crashes");
        AssertExactlyOncePerTick(result, "dst-daily", dailyCron, tz, start);
        AssertExactlyOncePerTick(result, "dst-minute", "* * * * *", tz, start);
        Assert.All(result.MintedJobs, m => Assert.True(m.State.IsTerminal(), $"seed {seed}: {m.Tick:O} not terminal"));
    }

    /// <summary>
    /// Named scenario: Catch-Up under an outage. An hourly schedule is seeded with its Cursor five
    /// hours in the past — a backlog of missed ticks, exactly the state a recovered cluster faces.
    /// Under Skip nothing is minted for the missed ticks (all recorded); under Coalesce exactly one
    /// make-up run is minted for the whole missed set — never a thundering herd — whichever of the
    /// three crashing nodes resolves the backlog first.
    /// </summary>
    [Theory]
    [InlineData(CatchUpPolicy.Skip, 301UL)]
    [InlineData(CatchUpPolicy.Skip, 302UL)]
    [InlineData(CatchUpPolicy.Coalesce, 311UL)]
    [InlineData(CatchUpPolicy.Coalesce, 312UL)]
    [InlineData(CatchUpPolicy.Coalesce, 313UL)]
    public void CatchUpRegime_OutageThenRecovery_SkipMintsNothing_CoalesceMintsExactlyOneMakeUp(
        CatchUpPolicy catchUp, ulong seed)
    {
        var start = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var missed = new[] { start.AddHours(-4), start.AddHours(-3), start.AddHours(-2), start.AddHours(-1) };

        var result = new Simulator(new SimulationOptions
        {
            Seed = seed,
            JobCount = 0,
            StartTime = start,
            WorkloadDuration = TimeSpan.FromHours(3),
            PollInterval = TimeSpan.FromSeconds(10),
            CrashProbabilityPerPoll = 0.01,
            MaxCrashDowntime = TimeSpan.FromSeconds(30),
            Schedules =
            [
                new SeededSchedule
                {
                    Id = "hourly",
                    Cron = "0 * * * *",
                    CatchUp = catchUp,
                    CursorOffset = TimeSpan.FromHours(-5),
                },
            ],
        }).Run();

        var snapshot = Assert.Single(result.FinalSchedules);
        var skipped = snapshot.Schedule.SkippedTicks;
        var mintedFromMissed = result.MintedTicks("hourly").Where(missed.Contains).ToList();

        if (catchUp == CatchUpPolicy.Skip)
        {
            Assert.Empty(mintedFromMissed);                          // missed means missed
            Assert.All(missed, t => Assert.Contains(t, skipped));    // every missed tick recorded
        }
        else
        {
            Assert.Equal([missed[^1]], mintedFromMissed);            // exactly one make-up: the latest missed
            Assert.All(missed[..^1], t => Assert.Contains(t, skipped));
        }

        Assert.All(result.MintedJobs, m => Assert.True(m.State.IsTerminal(), $"seed {seed}: {m.Tick:O} not terminal"));
    }

    /// <summary>
    /// Named scenario: No-Overlap under chaos. A No-Overlap every-minute schedule whose instances
    /// run for minutes suppresses minting while a previous instance is non-terminal; the suppressed
    /// ticks are recorded. Crashes mean an executing instance is orphaned — its Lease expiry
    /// requeues it (still the one live instance) until it finally terminates and minting resumes.
    /// The oracle asserts at most one live instance every step.
    /// </summary>
    [Theory]
    [InlineData(401UL)]
    [InlineData(402UL)]
    [InlineData(403UL)]
    public void NoOverlapRegime_SuppressesMintingWhileLive_RecordsSkips_RecoversOrphans(ulong seed)
    {
        var start = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

        var result = new Simulator(new SimulationOptions
        {
            Seed = seed,
            JobCount = 0,
            StartTime = start,
            WorkloadDuration = TimeSpan.FromHours(1),
            PollInterval = TimeSpan.FromSeconds(10),
            MaxExecutionDuration = TimeSpan.FromMinutes(5),
            CrashProbabilityPerPoll = 0.01,
            MaxCrashDowntime = TimeSpan.FromSeconds(30),
            Schedules =
            [
                new SeededSchedule { Id = "no-overlap", Cron = "* * * * *", NoOverlap = true },
            ],
        }).Run();

        var snapshot = Assert.Single(result.FinalSchedules);
        Assert.True(result.Crashes > 0, $"seed {seed}: regime produced no crashes");
        Assert.NotEmpty(result.MintedJobs);                          // it did mint
        Assert.NotEmpty(snapshot.Schedule.SkippedTicks);             // and it suppressed, visibly
        Assert.All(result.MintedJobs, m => Assert.True(m.State.IsTerminal(), $"seed {seed}: {m.Tick:O} not terminal"));
        // The at-most-one-live invariant was checked by the oracle after every step.
    }

    /// <summary>
    /// Asserts a schedule minted exactly the zone's occurrences over the resolved window: every due
    /// tick minted (in order), each exactly once, with nothing silently lost to a skip. The expected
    /// set is recomputed from the same <see cref="ZonedCron"/> the planner uses, so the assertion
    /// pins the simulated distributed mint path to the canonical DST semantics.
    /// </summary>
    private static void AssertExactlyOncePerTick(
        SimulationResult result, string scheduleId, string cron, string timeZoneId, DateTimeOffset start)
    {
        var snapshot = Assert.Single(result.FinalSchedules, s => s.Schedule.ScheduleId == scheduleId);
        var expected = ExpectedTicks(cron, timeZoneId, start, snapshot.Schedule.Cursor);
        var minted = result.MintedTicks(scheduleId);

        Assert.NotEmpty(expected);                                   // the window actually contained ticks
        Assert.Equal(expected, minted);                             // every due tick, in order, none extra
        Assert.Equal(minted.Count, minted.Distinct().Count());      // exactly once per tick
        Assert.Empty(snapshot.Schedule.SkippedTicks);              // nothing silently lost
    }

    /// <summary>
    /// Named scenario: quarantine-via-routing (issue 0073, Phase 2 slice E). A fraction of enqueued jobs are
    /// dispatch-side UNROUTABLE; each is branched at ExecuteJob exactly as the pump branches a
    /// RouteResult.Unroutable — it drives ExecutionUnroutable → ReportOutcome(Unroutable) and reaches
    /// Quarantined WITHOUT ever executing. The in-line liveness Invariant (a Quarantined job must be absent
    /// from _everExecuted) holds every run, so a converging run is itself proof the unroutable jobs were never
    /// dispatched; here we additionally assert some job actually reached Quarantined and the run is terminal.
    /// </summary>
    [Theory]
    [InlineData(831UL)]
    [InlineData(832UL)]
    [InlineData(833UL)]
    public void QuarantineRegime_UnroutableJobsReachQuarantined_NeverExecuted_AndConverge(ulong seed)
    {
        var options = new SimulationOptions { Seed = seed, UnroutableProbability = 0.2 };
        var result = new Simulator(options).Run();

        // The run converged (all 200 terminal) and some jobs took the unroutable path to Quarantined. The
        // never-executed property is the Simulator's own in-line Invariant — its passing IS the proof here.
        Assert.Equal(200, result.FinalJobs.Count);
        Assert.Equal(200, result.Succeeded + result.DeadLettered + result.Cancelled + result.Quarantined);
        Assert.True(result.Quarantined > 0, $"seed {seed}: the unroutable axis quarantined nothing");

        // Same seed reproduces the identical run — the route stream perturbs nothing else.
        var replay = new Simulator(options).Run();
        Assert.Equal(result.FinalJobs, replay.FinalJobs);
    }

    /// <summary>
    /// Determinism guard: the unroutable axis is gated behind a default-0 knob on its own rng stream (issue
    /// 0073), so UnroutableProbability 0 takes zero draws and leaves the run byte-identical to the ungated
    /// baseline — the property the seed battery rests on — while a non-zero probability provably perturbs it
    /// and produces Quarantined jobs the baseline never had.
    /// </summary>
    [Theory]
    [InlineData(1UL)]
    [InlineData(1337UL)]
    public void UnroutableOff_IsByteIdenticalToBaseline_AndOnPerturbs(ulong seed)
    {
        var baseline = new Simulator(new SimulationOptions { Seed = seed }).Run();
        var stillBaseline = new Simulator(new SimulationOptions { Seed = seed, UnroutableProbability = 0 }).Run();

        Assert.Equal(0, baseline.Quarantined);
        Assert.Equal(baseline.Steps, stillBaseline.Steps);
        Assert.Equal(baseline.FinalJobs, stillBaseline.FinalJobs);
        Assert.Equal(baseline.StaleOutcomes, stillBaseline.StaleOutcomes);

        var unroutable = new Simulator(new SimulationOptions { Seed = seed, UnroutableProbability = 0.2 }).Run();
        Assert.True(unroutable.Quarantined > 0, $"seed {seed}: the unroutable knob did nothing");
        Assert.NotEqual(baseline.FinalJobs, unroutable.FinalJobs); // a different, but still-converging, run
    }

    /// <summary>
    /// Named scenario: quarantine under Node Isolation (issue 0073 × 0068). The Unroutable outcome flows
    /// through the same (workerId, attempt) ReportOutcome fence as Succeeded/Failed, so an isolated node that
    /// heals and replays a now-stale Unroutable report must be REJECTED by the existing Outcome-Provenance
    /// Oracle — no new oracle is added. A clean, converging run with both knobs on is the demonstration: any
    /// stale Unroutable report that mutated a job it no longer owned would have tripped the provenance oracle
    /// mid-run with the seed.
    /// </summary>
    [Theory]
    [InlineData(834UL)]
    [InlineData(835UL)]
    public void QuarantineUnderIsolation_StaleUnroutableReportIsFenced_AndConverges(ulong seed)
    {
        var result = new Simulator(new SimulationOptions
        {
            Seed = seed,
            UnroutableProbability = 0.2,
            IsolationCount = 30,
        }).Run();

        // The run reached the end of the drain (the Outcome-Provenance Oracle ran every step and never fired)
        // and every job is terminal — so every stale Unroutable report under isolation was fenced.
        Assert.Equal(200, result.FinalJobs.Count);
        Assert.Equal(200, result.Succeeded + result.DeadLettered + result.Cancelled + result.Quarantined);
        Assert.True(result.Quarantined > 0, $"seed {seed}: the unroutable axis quarantined nothing under isolation");
    }

    /// <summary>The zone's cron occurrences in <c>(start, cursorEnd]</c>, via the production planner's clock.</summary>
    private static List<DateTimeOffset> ExpectedTicks(
        string cron, string? timeZoneId, DateTimeOffset start, DateTimeOffset cursorEnd)
    {
        var expression = CronExpression.Parse(cron);
        var zone = ZonedCron.ResolveZone(timeZoneId);
        var ticks = new List<DateTimeOffset>();
        for (var cursor = start; ZonedCron.NextAfter(expression, cursor, zone) is { } tick && tick <= cursorEnd; cursor = tick)
        {
            ticks.Add(tick);
        }
        return ticks;
    }

    /// <summary>
    /// The multi-Queue Topology regime (issue 0071): a seeded fleet with several named Queues and
    /// heterogeneous per-node Worker Groups (overlapping served sets, Strict and Weighted policies), so a
    /// shared Queue draws cross-group claiming contention. The served-set-containment oracle runs in-line
    /// after every step, so a clean Run() return proves no node ever held a Lease outside its declared
    /// served set; convergence (every job terminal by DrainEnd) is the headline liveness property the
    /// universal anchor (node-0 serves every Queue) guarantees under the N−1 isolation budget. Same seed
    /// replays byte-identically — the topology is drawn from its own deterministic stream.
    /// </summary>
    [Theory]
    [InlineData(811UL)]
    [InlineData(812UL)]
    [InlineData(813UL)]
    public void TopologyRegime_MultiQueueContention_ServedSetHolds_AndConverges(ulong seed)
    {
        var options = new SimulationOptions { Seed = seed, TopologyQueues = 3 };
        var first = new Simulator(options).Run();

        // A clean Run() return means the served-set-containment oracle (and every other invariant) held
        // after every step, and the liveness check passed — the cluster converged.
        Assert.Equal(200, first.FinalJobs.Count);
        Assert.Equal(200, first.Succeeded + first.DeadLettered + first.Cancelled);

        // Determinism: the topology and the whole interleaving replay identically from the seed.
        var second = new Simulator(options).Run();
        Assert.Equal(first.FinalJobs, second.FinalJobs);
    }

    /// <summary>
    /// Determinism guard: the Topology Generator is gated behind a default-0 knob on its own rng stream, so
    /// a TopologyQueues of 0 takes zero draws and leaves the run byte-identical to the ungated baseline —
    /// the property the whole seed battery rests on — while a multi-Queue topology provably perturbs it.
    /// Mirrors <see cref="IsolationCountZero_IsByteIdenticalToTheBaseline_AndNonZeroPerturbsIt"/>.
    /// </summary>
    [Theory]
    [InlineData(1UL)]
    [InlineData(1337UL)]
    public void TopologyDefault_IsByteIdenticalToBaseline_AndEnabledPerturbs(ulong seed)
    {
        var baseline = new Simulator(new SimulationOptions { Seed = seed }).Run();
        var stillBaseline = new Simulator(new SimulationOptions { Seed = seed, TopologyQueues = 0 }).Run();

        Assert.Equal(baseline.Steps, stillBaseline.Steps);
        Assert.Equal(baseline.FinalJobs, stillBaseline.FinalJobs);
        Assert.Equal(baseline.StaleOutcomes, stillBaseline.StaleOutcomes);

        var topology = new Simulator(new SimulationOptions { Seed = seed, TopologyQueues = 3 }).Run();
        Assert.NotEqual(baseline.FinalJobs, topology.FinalJobs); // a different, but still-converging, run
    }

    /// <summary>
    /// Oracle self-test for the served-set-containment invariant (issue 0071): with SabotageServedSet a narrow
    /// non-anchor node is handed a DRIVER that also serves a Queue beyond its recorded served set, so it
    /// legitimately claims a foreign Queue while the oracle still checks against the narrow recorded set — a
    /// real containment violation a live oracle MUST catch and fail with the replay seed; a dead one would let
    /// the foreign Lease pass.
    /// </summary>
    [Theory]
    [InlineData(815UL)]
    [InlineData(816UL)]
    public void ServedSetSelfTest_AClaimOutsideTheServedSet_FailsTheRun_AndPrintsTheSeed(ulong seed)
    {
        var exception = Assert.Throws<SimulationInvariantException>(() =>
            new Simulator(new SimulationOptions
            {
                Seed = seed,
                TopologyQueues = 3,
                SabotageServedSet = true,
            }).Run());

        Assert.Contains($"seed {seed}", exception.Message);
        Assert.Contains("served-set", exception.Message); // the containment invariant fired
        Assert.Equal(InvariantId.ServedSetContainment, exception.InvariantId);
    }

    /// <summary>
    /// Strict preemption (issue 0071): a Strict Worker Group serving ["default", "q1"] always claims the
    /// higher-priority Queue ("default") before the lower one ("q1"). A node with both Queues holding due
    /// work claims "default" first — proven by enqueuing one job in each Queue, driving a single poll, and
    /// asserting the "default" job is the one that became Leased. A focused, deterministic store + driver
    /// scenario, no fault injection, so the ordering is unambiguous.
    /// </summary>
    [Fact]
    public async Task StrictPreemption_DueHigherPriorityQueueIsClaimedFirst()
    {
        const ulong seed = 817UL;
        var store = new InMemoryJobStore();
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        // Two due jobs, one per Queue, both claimable. The Strict policy orders "default" before "q1", and
        // PoolSize 1 forces a single claim per pass — so the winner is whichever Queue Strict preempts to.
        var high = new NewJob(Guid.NewGuid(), "sim-job", ReadOnlyMemory<byte>.Empty, "default", now);
        var low = new NewJob(Guid.NewGuid(), "sim-job", ReadOnlyMemory<byte>.Empty, "q1", now);
        await store.EnqueueAsync(low, now);   // enqueue the LOWER-priority job first
        await store.EnqueueAsync(high, now);  // so claim order can't be an enqueue artifact

        var driver = new NodeDriver(new NodeOptions
        {
            WorkerId = $"node-strict-{seed}",
            Policy = new DispatchPolicy.Strict(["default", "q1"]),
            PoolSize = 1, // one slot: the claim pass can take only the preempted Queue's job
            LeaseDuration = TimeSpan.FromSeconds(60),
        });

        // Drive one poll, executing the store side of each command the Driver emits.
        foreach (var command in driver.Step(new NodeEvent.PollDue(now)))
        {
            if (command is Command.ClaimBatch claim)
            {
                await store.ClaimAsync(new ClaimRequest(
                    claim.WorkerId, claim.Queues, claim.MaxJobs, claim.LeaseDuration, now));
            }
        }

        // Strict preempted to "default": the higher-priority job is Leased, the lower one still Scheduled.
        var highState = (await store.GetJobAsync(high.JobId))!;
        var lowState = (await store.GetJobAsync(low.JobId))!;
        Assert.Equal(JobState.Leased, highState.State);
        Assert.Equal(JobState.Scheduled, lowState.State);
    }

    /// <summary>
    /// Named scenario: bounded pool / Backpressure (issue 0072). A small finite PoolSize makes each node's
    /// claims partial — the Driver subtracts in-flight work, so a full pool claims nothing until an execution
    /// completes. The strict per-node-cap oracle asserts <c>node.Executing.Count &lt;= PoolSize</c> after every
    /// step against the sim's REAL in-flight set, so a clean run is itself proof the cap held the whole time —
    /// under the default crash and heartbeat-loss chaos AND, on the second variant, under isolation fault
    /// injection too (the acceptance criterion: cap holds under crash, heartbeat-loss, and isolation). The run
    /// still converges to all-terminal, and the same seed reproduces the identical run.
    /// </summary>
    [Theory]
    [InlineData(821UL)]
    [InlineData(822UL)]
    [InlineData(823UL)]
    public void BoundedPoolRegime_PerNodeCapHolds_UnderChaos_AndConverges(ulong seed)
    {
        // Variant A: a small pool under the default crash + heartbeat-loss chaos. A clean run means the cap
        // held every step; the run converges to all-terminal.
        var chaos = new SimulationOptions { Seed = seed, PoolSize = 2 };
        var result = new Simulator(chaos).Run();
        Assert.Equal(200, result.FinalJobs.Count);
        Assert.Equal(200, result.Succeeded + result.DeadLettered + result.Cancelled);

        // Same seed reproduces the identical run — PoolSize is a deterministic config gate, not a fault stream.
        var replay = new Simulator(chaos).Run();
        Assert.Equal(result.FinalJobs, replay.FinalJobs);

        // Variant B: the same small pool with isolation fault injection ALSO on, so the cap is shown holding
        // under crash, heartbeat-loss, AND isolation at once. A clean run is the cap holding through all three.
        var isolated = new Simulator(new SimulationOptions { Seed = seed, PoolSize = 2, IsolationCount = 30 }).Run();
        Assert.True(isolated.Isolations > 0, $"seed {seed}: isolation injected nothing");
        Assert.Equal(200, isolated.Succeeded + isolated.DeadLettered + isolated.Cancelled);
    }

    /// <summary>
    /// Determinism guard: PoolSize is a deterministic config gate that draws NO rng, so the default
    /// (<c>int.MaxValue</c>) leaves the run byte-identical to the baseline with no stream to short-circuit —
    /// the property the whole seed battery rests on. A finite pool changes the in-flight set, so it generally
    /// diverges from baseline, but it is still fully deterministic (same seed twice → equal) and converges.
    /// </summary>
    [Theory]
    [InlineData(1UL)]
    [InlineData(1337UL)]
    public void PoolSizeDefault_IsByteIdenticalToBaseline_AndFiniteConvergesDeterministically(ulong seed)
    {
        var baseline = new Simulator(new SimulationOptions { Seed = seed }).Run();
        var stillBaseline = new Simulator(new SimulationOptions { Seed = seed, PoolSize = int.MaxValue }).Run();

        Assert.Equal(baseline.Steps, stillBaseline.Steps);
        Assert.Equal(baseline.FinalJobs, stillBaseline.FinalJobs);
        Assert.Equal(baseline.StaleOutcomes, stillBaseline.StaleOutcomes);

        // A finite pool is deterministic (same seed twice → equal) and still converges to all-terminal.
        var finiteOptions = new SimulationOptions { Seed = seed, PoolSize = 2 };
        var finite = new Simulator(finiteOptions).Run();
        var finiteReplay = new Simulator(finiteOptions).Run();
        Assert.Equal(finite.FinalJobs, finiteReplay.FinalJobs);
        Assert.Equal(200, finite.Succeeded + finite.DeadLettered + finite.Cancelled);
        // The finite pool reshapes the in-flight set, so it diverges from the unbounded baseline.
        Assert.NotEqual(baseline.FinalJobs, finite.FinalJobs);
    }

    /// <summary>
    /// Oracle self-test for the per-node-cap invariant (issue 0072): a silently dead cap oracle looks identical
    /// to a healthy codebase. With SabotagePoolSize the node's Driver is built UNBOUNDED while the oracle keeps
    /// checking against the configured PoolSize, so the Driver over-admits past the small pool, the sim's real
    /// in-flight set exceeds the cap, and a working oracle MUST fail the run — and the failure MUST print the
    /// replay seed. The default JobCount=200 with chaos is plenty of load to fill the pool past 2.
    /// </summary>
    [Theory]
    [InlineData(825UL)]
    [InlineData(826UL)]
    public void PoolSizeSelfTest_AdmittingPastThePool_FailsTheRun_AndPrintsTheSeed(ulong seed)
    {
        var exception = Assert.Throws<SimulationInvariantException>(() =>
            new Simulator(new SimulationOptions { Seed = seed, PoolSize = 2, SabotagePoolSize = true }).Run());

        Assert.Contains($"seed {seed}", exception.Message);
        Assert.Contains("per-node cap", exception.Message); // the per-node-cap invariant fired
        Assert.Equal(InvariantId.PerNodeCap, exception.InvariantId);
    }

    /// <summary>
    /// Per-Queue Concurrency Limits over the multi-Queue topology (issue 0074): several Queues each carry their
    /// own cap, enforced simultaneously, while the anchor node juggles all of them — the regime that makes
    /// per-Queue slot accounting falsifiable (a single "default" counter cannot reveal a slot leaking between
    /// Queues). I3-extended holds for every limited Queue after each step (a clean run is the proof), and
    /// work-conservation is verified as drain-liveness: a leaked slot would strand a Queue's due work and fail
    /// convergence with the stuck-job diagnostic (ADR 0016). Variant B adds isolation — the load under which
    /// slot Effect-Once matters and the slot-double-release detector must stay silent. Same seed replays.
    /// </summary>
    [Theory]
    [InlineData(841UL)]
    [InlineData(842UL)]
    [InlineData(843UL)]
    public void PerQueueConcurrencyRegime_MultipleLimitsHoldSimultaneously_AndConverges(ulong seed)
    {
        var options = new SimulationOptions
        {
            Seed = seed,
            JobCount = 100,
            TopologyQueues = 3,
            ConcurrencyLimits = new Dictionary<string, int> { ["default"] = 3, ["q1"] = 2, ["q2"] = 2 },
        };
        var result = new Simulator(options).Run();

        // A clean Run() return means I3-extended held for every limited Queue after every step and the
        // cluster converged (slot-non-leak drain-liveness). All 100 jobs reached a terminal class.
        Assert.Equal(100, result.Succeeded + result.DeadLettered + result.Cancelled + result.Quarantined);

        // Determinism: topology + per-Queue limits replay identically from the seed.
        var replay = new Simulator(options).Run();
        Assert.Equal(result.FinalJobs, replay.FinalJobs);

        // Variant B: the same multi-limit topology under isolation load — per-Queue limits AND slot
        // Effect-Once together. A clean run is both holding through the isolation chaos.
        var isolated = new Simulator(options with { IsolationCount = 30 }).Run();
        Assert.True(isolated.Isolations > 0, $"seed {seed}: isolation injected nothing");
        Assert.Equal(100, isolated.Succeeded + isolated.DeadLettered + isolated.Cancelled + isolated.Quarantined);
    }

    /// <summary>
    /// Determinism guard: with no Concurrency Limits configured (empty <c>ConcurrencyLimits</c> and a null
    /// <c>ConcurrencyLimit</c>) the effective limit set is empty — no SetConcurrencyLimit store call is made
    /// and the I3 loop and slot detector are no-ops — so the run is byte-identical to the baseline. The whole
    /// seed battery rests on this.
    /// </summary>
    [Theory]
    [InlineData(1UL)]
    [InlineData(1337UL)]
    public void ConcurrencyLimitsOff_IsByteIdenticalToBaseline(ulong seed)
    {
        var baseline = new Simulator(new SimulationOptions { Seed = seed }).Run();
        var stillBaseline = new Simulator(new SimulationOptions
        {
            Seed = seed,
            ConcurrencyLimits = new Dictionary<string, int>(),
        }).Run();

        Assert.Equal(baseline.Steps, stillBaseline.Steps);
        Assert.Equal(baseline.FinalJobs, stillBaseline.FinalJobs);
        Assert.Equal(baseline.StaleOutcomes, stillBaseline.StaleOutcomes);
    }

    /// <summary>
    /// Oracle self-test for the slot-double-release detector (issue 0074): with SabotageSlotDoubleRelease the
    /// sim frees one Attempt's Concurrency slot twice — exactly what a dropped (workerId, attempt) fence would
    /// do by double-applying a stale outcome under isolation load — so a live detector MUST catch the second
    /// release and fail with the seed. The intact-fence regimes above never double-release, so the detector
    /// stays silent there.
    /// </summary>
    [Theory]
    [InlineData(844UL)]
    [InlineData(845UL)]
    public void SlotDoubleReleaseSelfTest_AnAttemptSlotFreedTwice_FailsTheRun_AndPrintsTheSeed(ulong seed)
    {
        var exception = Assert.Throws<SimulationInvariantException>(() =>
            new Simulator(new SimulationOptions
            {
                Seed = seed,
                JobCount = 100,
                ConcurrencyLimit = 3, // folded into { "default" = 3 }; the first slot freed trips the phantom double
                SabotageSlotDoubleRelease = true,
            }).Run());

        Assert.Contains($"seed {seed}", exception.Message);
        Assert.Contains("slot-double-release", exception.Message); // the slot Effect-Once detector fired
        Assert.Equal(InvariantId.SlotDoubleRelease, exception.InvariantId);
    }

    /// <summary>
    /// Weighted Dispatch under load (issue 0075, ADR 0016): every node runs the Weighted Policy
    /// (<c>ForceWeightedDispatch</c>) over the multi-Queue topology with a finite <c>PoolSize</c>, so claims
    /// are PARTIAL — a full pool claims nothing until an execution completes — and Driver restarts (crashes)
    /// rebuild each node's <c>SmoothWeightedRoundRobin</c> credit state fresh. This stresses SWRR credit
    /// accounting across short claims and restarts, the surface the pure-Core fairness unit test cannot reach.
    /// Work-conservation is verified as drain-time liveness: a leaked or double-counted credit would starve a
    /// served Queue and leave its work non-terminal, so the convergence check (reported with the stuck-job
    /// diagnostic) IS the oracle — no per-step "should-have-claimed" check (ADR 0016). Fairness ratios are
    /// never asserted here; work redistributes under fault injection by design. The run proves it actually
    /// exercised Weighted (<c>WeightedNodeCount &gt; 0</c>) under real restarts (<c>Crashes &gt; 0</c>), and
    /// the same seed replays byte-identically.
    /// </summary>
    [Theory]
    [InlineData(851UL)]
    [InlineData(852UL)]
    [InlineData(853UL)]
    public void WeightedDispatchUnderLoad_PartialClaimsSurviveRestarts_AndConverges(ulong seed)
    {
        // Variant A: all-Weighted topology, finite pool (partial claims), default crash/heartbeat-loss chaos.
        var options = new SimulationOptions
        {
            Seed = seed,
            TopologyQueues = 3,
            ForceWeightedDispatch = true,
            PoolSize = 2, // finite ⇒ claims are partial, the whole reason Weighted earns Simulator coverage
        };
        var result = new Simulator(options).Run();

        // The regime honestly exercised Weighted under real restarts — not a silent all-Strict topology, not a
        // crash-free run that never rebuilt SWRR credit.
        Assert.True(result.WeightedNodeCount > 0, $"seed {seed}: no Weighted node — regime did not exercise SWRR");
        Assert.True(result.Crashes > 0, $"seed {seed}: no crash — SWRR credit was never rebuilt on restart");

        // A clean Run() return means drain-time liveness held: SWRR credit survived partial claims and the
        // restarts without leaking or double-counting, so no served Queue starved — all 200 jobs converged.
        Assert.Equal(200, result.Succeeded + result.DeadLettered + result.Cancelled + result.Quarantined);

        // Determinism: the forced-Weighted topology and the whole interleaving replay identically from the seed.
        var replay = new Simulator(options).Run();
        Assert.Equal(result.FinalJobs, replay.FinalJobs);

        // Variant B: the same Weighted-under-partial-claims regime with isolation fault injection ALSO on, so
        // SWRR credit is shown surviving short claims, restarts, AND isolation at once. A clean run is the proof.
        var isolated = new Simulator(options with { IsolationCount = 30 }).Run();
        Assert.True(isolated.Isolations > 0, $"seed {seed}: isolation injected nothing");
        Assert.Equal(200, isolated.Succeeded + isolated.DeadLettered + isolated.Cancelled + isolated.Quarantined);
    }

    /// <summary>
    /// Determinism guard: <c>ForceWeightedDispatch</c> is gated behind <c>TopologyQueues &gt;= 2</c>, so in the
    /// default single-Queue world the Topology Generator never builds a policy and the knob is inert — the run
    /// is byte-identical to the baseline, the property the whole seed battery rests on. With the topology on it
    /// provably perturbs the run (forcing Weighted everywhere) yet still converges deterministically.
    /// Mirrors <see cref="TopologyDefault_IsByteIdenticalToBaseline_AndEnabledPerturbs"/>.
    /// </summary>
    [Theory]
    [InlineData(1UL)]
    [InlineData(1337UL)]
    public void ForceWeightedDispatch_IsInertWithoutTopology_AndPerturbsWithIt(ulong seed)
    {
        var baseline = new Simulator(new SimulationOptions { Seed = seed }).Run();
        var stillBaseline = new Simulator(new SimulationOptions { Seed = seed, ForceWeightedDispatch = true }).Run();

        // No topology ⇒ no policy is ever built ⇒ the knob is a true no-op: byte-identical to baseline.
        Assert.Equal(baseline.Steps, stillBaseline.Steps);
        Assert.Equal(baseline.FinalJobs, stillBaseline.FinalJobs);
        Assert.Equal(baseline.StaleOutcomes, stillBaseline.StaleOutcomes);
        Assert.Equal(0, stillBaseline.WeightedNodeCount);

        // With the topology on, forcing Weighted makes every node Weighted, perturbs the run, and still converges.
        var weightedOptions = new SimulationOptions { Seed = seed, TopologyQueues = 3, ForceWeightedDispatch = true };
        var weighted = new Simulator(weightedOptions).Run();
        Assert.Equal(3, weighted.WeightedNodeCount); // every node (anchor + 2) is Weighted
        Assert.NotEqual(baseline.FinalJobs, weighted.FinalJobs);
        Assert.Equal(weighted.FinalJobs, new Simulator(weightedOptions).Run().FinalJobs); // deterministic replay
        Assert.Equal(200, weighted.Succeeded + weighted.DeadLettered + weighted.Cancelled + weighted.Quarantined);
    }
}
