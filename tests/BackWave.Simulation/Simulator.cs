using BackWave.Core;
using BackWave.Diagnostics;
using BackWave.Driver;
using BackWave.Observers;
using BackWave.Storage;
using BackWave.Storage.InMemory;

namespace BackWave.Tests.Simulation;

internal sealed record SimulationOptions
{
    public required ulong Seed { get; init; }
    public int NodeCount { get; init; } = 3;
    public int JobCount { get; init; } = 200;

    /// <summary>Window during which jobs are enqueued and faults are injected.</summary>
    public TimeSpan WorkloadDuration { get; init; } = TimeSpan.FromHours(2);

    /// <summary>Extra fault-free time allowed for everything to reach a terminal state.</summary>
    public TimeSpan DrainAllowance { get; init; } = TimeSpan.FromHours(2);

    public double CrashProbabilityPerPoll { get; init; } = 0.005;
    public double HeartbeatLossProbability { get; init; } = 0.05;
    public double HandlerFailureProbability { get; init; } = 0.15;

    /// <summary>Executions may outlive the Lease — the GC-pause / double-execution regime.</summary>
    public TimeSpan MaxExecutionDuration { get; init; } = TimeSpan.FromSeconds(90);

    public TimeSpan MaxClockSkew { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan MaxCrashDowntime { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The idle poll-backoff ceiling, mirroring <c>WorkerGroupOptions.MaxPollInterval</c>. When it is
    /// greater than <see cref="PollInterval"/>, an idle node backs off from the poll interval toward this
    /// value and sleeps to the store-reported next-due instant, instead of polling at the fixed rate.
    /// Defaults to <see cref="TimeSpan.Zero"/> (disabled): the sim polls at <see cref="PollInterval"/>,
    /// byte-identical to before.
    /// </summary>
    public TimeSpan MaxPollInterval { get; init; }

    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromSeconds(60);
    public int MaxAttempts { get; init; } = 3;
    public TimeSpan Backoff { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Optional cluster-wide Concurrency Limit on the sim's <c>"default"</c> Queue. Shorthand,
    /// folded into <see cref="ConcurrencyLimits"/> as <c>{ "default" = limit }</c>; kept so the legacy
    /// single-Queue band stays byte-identical.</summary>
    public int? ConcurrencyLimit { get; init; }

    /// <summary>
    /// Per-Queue cluster-wide Concurrency Limits over the multi-Queue topology (issue 0074): each named
    /// Queue's cap on simultaneously-Leased instances, enforced independently and simultaneously. Empty (the
    /// default) — together with a null <see cref="ConcurrencyLimit"/> — configures NO limit and makes no
    /// store call, so the existing seed battery is byte-identical. The new bug class is per-Queue slot
    /// accounting under a node that juggles ≥2 limited Queues: with one counter there is only a single number
    /// to get wrong; with N, an over-release on one Queue can let another's limit be exceeded. I3 is checked
    /// for every limited Queue at once, and the slot-double-release detector guards Effect-Once on the slot.
    /// </summary>
    public IReadOnlyDictionary<string, int> ConcurrencyLimits { get; init; } = new Dictionary<string, int>();

    /// <summary>
    /// Oracle self-test for the slot-double-release detector (issue 0074): a Concurrency slot must be
    /// released at most once per Attempt. With this on, the sim tallies one phantom extra release for the
    /// first slot-releasing outcome on a limited Queue — exactly as a dropped (workerId, attempt) fence
    /// would double-apply a stale outcome and free the same Attempt's slot twice — so a live detector MUST
    /// catch the second release and fail with the seed. Real runs (the fence intact) never double-release.
    /// Never set in real regimes.
    /// </summary>
    public bool SabotageSlotDoubleRelease { get; init; }

    /// <summary>
    /// Node-local Backpressure bound (issue 0072): the maximum concurrent in-flight executions a single
    /// node admits. The Node Driver already subtracts its in-flight count from each claim
    /// (<c>available = Math.Min(MaxClaimBatch, PoolSize - _executing.Count)</c>), so a finite pool makes
    /// claims partial — a node with a full pool claims nothing until an execution completes. This is a
    /// deterministic config gate, not a fault stream: it draws NO rng, so the default (<c>int.MaxValue</c>,
    /// today's unbounded behavior) leaves every existing seed battery byte-identical with no stream to
    /// short-circuit. The per-node-cap oracle reads the sim's REAL in-flight set every step. Independent of
    /// the cluster-wide <see cref="ConcurrencyLimit"/> (issue 0074) — either gate may legitimately idle a node.
    /// </summary>
    public int PoolSize { get; init; } = int.MaxValue;

    /// <summary>
    /// Oracle self-test for the per-node-cap invariant (issue 0072): the node's Driver is built with an
    /// UNBOUNDED pool while the oracle keeps checking against the configured <see cref="PoolSize"/>, so the
    /// Driver over-admits past the small pool and the sim's real in-flight set exceeds the cap — exactly
    /// mirroring the Concurrency-Limit <see cref="Sabotage"/>. A working cap oracle MUST catch the breach and
    /// fail with the seed. Pair with a small finite <see cref="PoolSize"/> and enough load. Never set in real regimes.
    /// </summary>
    public bool SabotagePoolSize { get; init; }

    /// <summary>
    /// Wake-Up Hint delivery rate (ADR-0005): 0 = every hint dropped (no channel), 1 =
    /// every enqueue hints every node. Hints draw from their own rng stream so the
    /// workload is bit-identical across delivery rates — the hint-loss equivalence
    /// scenario compares exactly that.
    /// </summary>
    public double HintDeliveryProbability { get; init; }

    /// <summary>Hints may be delayed arbitrarily (spec §8); this bounds the sim's delay.</summary>
    public TimeSpan MaxHintLatency { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Per-store-call probability a hot-path operation throws a transient fault during the
    /// workload window (0 = no store faults). Faults stop at WorkloadEnd; the cluster must
    /// then converge — no lost jobs, every invariant intact.
    /// </summary>
    public double StoreFaultProbability { get; init; }

    /// <summary>
    /// Oracle self-test: admit far past the configured Concurrency Limit so a working oracle
    /// MUST catch the I3 violation and fail the run with its seed. Never set in real regimes.
    /// </summary>
    public bool Sabotage { get; init; }

    /// <summary>
    /// Operator-action regime (§5.8): how many Operator Actions to race against the cluster
    /// chaos during the workload window — Cancel, Requeue, Pause/Resume, and TriggerScheduleNow.
    /// 0 (the default) schedules no Operator events and never touches the operator rng stream,
    /// so every existing regime stays byte-identical. Actions draw from their own stream so the
    /// main interleaving is untouched whether the regime is on or off.
    /// </summary>
    public int OperatorActionCount { get; init; }

    /// <summary>
    /// Node Isolation regime (issue 0068, ADR 0013): how many healing-isolation episodes to draw and
    /// race against the cluster during the workload window. Each cuts one node off from the Storage
    /// Contract for a bounded window while it keeps executing on a stale Lease belief, then heals — the
    /// heal-into-stale-write race that proves Effect-Once at the (workerId, attempt) fence. 0 (the
    /// default) makes zero draws on the isolation stream and never schedules an isolation event, so the
    /// existing seed battery stays byte-identical. Episodes draw from their own <c>Seed ^ "ISOLATION"</c>
    /// stream so the main interleaving is untouched whether the regime is on or off.
    /// </summary>
    public int IsolationCount { get; init; }

    /// <summary>
    /// Oracle self-test for the Outcome-Provenance invariant (issue 0068): the store drops the
    /// (workerId, attempt) fence, so a healed node's stale ReportOutcome is forced through under the
    /// CURRENT lease holder's identity and mutates state it should not — a working oracle MUST catch the
    /// applied-by-a-non-holder outcome and fail with the seed. Pair with <see cref="IsolationCount"/> so
    /// the stale-report race actually occurs. Never set in real regimes.
    /// </summary>
    public bool SabotageOutcomeFence { get; init; }

    /// <summary>
    /// Oracle self-test for the VECTORIZED (batched) Outcome-Provenance fence (ADR 0035): models a native
    /// batch report that evaluates the (workerId, attempt) fence ONCE for the whole batch — from its first
    /// row — and applies every row under that single verdict, instead of fencing each row independently. So
    /// a stale row riding behind a live first row is wrongly applied. A live Outcome-Provenance oracle MUST
    /// catch the applied-by-a-non-holder row, proving the fence is enforced per row and not on the batch as a
    /// whole. Distinct from <see cref="SabotageOutcomeFence"/> (which drops the fence on every report): this
    /// drops it only for the tail of a multi-row batch whose first row is live. Pair with
    /// <see cref="IsolationCount"/> so a stale row actually rides a batch. Never set in real regimes.
    /// </summary>
    public bool SabotageBatchFence { get; init; }

    /// <summary>
    /// Permanent node loss (issue 0069): the per-episode probability that an isolation never heals,
    /// modeling a node lost for good. A lost node's leased work must migrate to a survivor via Lease
    /// expiry; the Migration-Liveness Oracle proves it does within a config-derived bound. 0 (the default)
    /// short-circuits the permanent draw, so every healing-only isolation regime stays byte-identical. The
    /// Isolation Scheduler's N−1 budget keeps the last reachable node from ever being lost permanently.
    /// </summary>
    public double PermanentLossProbability { get; init; }

    /// <summary>
    /// Oracle self-test for the Migration-Liveness invariant (issue 0069): survivors stop sweeping
    /// ExpireLeases, so a permanently-lost node's Lease never expires in the store and its job can never
    /// migrate — a working oracle MUST flag the stuck job at its exact (jobId, time) once the bound
    /// lapses. Pair with permanent loss so a job is actually stranded. Never set in real regimes.
    /// </summary>
    public bool SabotageMigrationSweep { get; init; }

    /// <summary>
    /// Oracle self-test for the Migration-Liveness fault grace (issue vopr-0139): restores the pre-fix
    /// migration bound that applied its tight, config-derived sweep deadline even when store faults are active.
    /// The real oracle exempts store-fault-active worlds, because a transient store fault unwinds the survivor's
    /// reclaim sweep to a retry on its next poll, so a run of faults on consecutive survivor sweeps legitimately
    /// delays reclaim past a poll-cadence bound without the job being stranded (the convergence backstop covers
    /// genuine stranding there). With this on, the tight bound applies despite store faults, so the
    /// transient-delayed reclaim falsely re-trips MigrationLiveness — proving the exemption is load-bearing.
    /// Never set in real regimes.
    /// </summary>
    public bool SabotageMigrationFaultGrace { get; init; }

    /// <summary>
    /// Ack-loss isolation (issue 0070, Phase 1.5): the per-attempt probability that a node's outcome write
    /// COMMITS to the store but its acknowledgement is lost, so the node believes the report failed and
    /// re-reports — by which point the store may have genuinely moved on. This exercises the
    /// (workerId, attempt) fence and Effect-Once on the path where the write actually LANDED, not merely
    /// the path where every call failed. The retry must be idempotent at the boundary or fenced; the
    /// Outcome-Provenance Oracle (issue 0068) is the property under test — no new oracle. Drawn from its
    /// own <c>Seed ^ "ACKLOSS"</c> stream, default 0 with zero draws, so the existing battery stays
    /// byte-identical.
    /// </summary>
    public double AckLossProbability { get; init; }

    /// <summary>
    /// Quarantine-via-routing (issue 0073, Phase 2 slice E): the per-job probability that an enqueued job
    /// is dispatch-side UNROUTABLE — the Wire Name has no handler, or its payload won't decode. One axis
    /// covers both because <c>JobRegistry.Route</c> returns <c>Unroutable</c> for either. A marked job is
    /// branched exactly as the production/test pump branches an Unroutable route at <c>ExecuteJob</c>: it
    /// drives <see cref="NodeEvent.ExecutionUnroutable"/> instead of scheduling an execution, so the Driver
    /// emits <c>ReportOutcome(Unroutable)</c> and the job reaches Quarantined — never executed (it never
    /// enters <c>_everExecuted</c>), yet legitimately terminal. The Unroutable outcome flows through the
    /// same (workerId, attempt) ReportOutcome fence as Succeeded/Failed, so the existing Outcome-Provenance
    /// Oracle (issue 0068) covers the stale-report-under-isolation case for free — no new oracle. Drawn from
    /// its own <c>Seed ^ "UNROUTAB"</c> stream, default 0 with zero draws, so the existing battery stays
    /// byte-identical.
    /// </summary>
    public double UnroutableProbability { get; init; }

    /// <summary>
    /// Oracle self-test for the Pause invariant: an operator Pause updates the oracle's belief
    /// that the Queue is paused but DOESN'T actually pause the store, so the cluster keeps
    /// claiming — a working oracle MUST catch a fresh Lease appearing in a Paused Queue and fail
    /// with its seed. Never set in real regimes.
    /// </summary>
    public bool SabotagePausedClaim { get; init; }

    /// <summary>
    /// Oracle self-test for the cancellation-provenance invariant: every operator cancel still hits
    /// the store (jobs genuinely reach Cancelled), but the sim withholds the provenance record — so a
    /// real cancellation surfaces as unprovenanced and a working oracle MUST fail with its seed.
    /// Never set in real regimes.
    /// </summary>
    public bool SabotageCancelProvenance { get; init; }

    /// <summary>
    /// Oracle self-test for the legal-transition invariant: the store records only legal state-machine
    /// edges, so to prove the walk is alive the sim splices a single illegal Succeeded→Leased edge into
    /// the recorded history the oracle validates for one job — a live walk MUST reject it and fail with
    /// its seed; a silently dead one would pass. Never set in real regimes.
    /// </summary>
    public bool SabotageLegalTransition { get; init; }

    /// <summary>
    /// Oracle self-test for the at-least-once-execute liveness invariant: jobs still execute and reach
    /// their terminal states for real, but the sim withholds the "this job executed" record — so a job
    /// that genuinely ran surfaces as having reached a ran-to-completion terminal without ever
    /// executing, and a live oracle MUST fail with its seed. Never set in real regimes.
    /// </summary>
    public bool SabotageExecuteLiveness { get; init; }

    /// <summary>
    /// Oracle self-test for the audit-log-completeness invariant: every Operator Action still hits the
    /// store and is recorded for real, but the sim tallies one phantom extra action the store never
    /// logged — so the sim's tally carries one more entry than the store's audit log, exactly as a
    /// dropped audit row would surface, and a live oracle MUST catch the mismatch and fail with its
    /// seed. Never set in real regimes.
    /// </summary>
    public bool SabotageAuditCompleteness { get; init; }

    /// <summary>
    /// Topology Generator (issue 0071): how many distinct named Queues to seed across the fleet —
    /// <c>"default"</c> plus <c>q1..q{TopologyQueues-1}</c>. 0 or 1 (the default) leaves today's single-Queue
    /// world: every node serves <c>Strict(["default"])</c>, every job is enqueued to <c>"default"</c>, and the
    /// generator makes ZERO draws on its <c>Seed ^ "TOPOLOGY"</c> stream, so the existing seed battery stays
    /// byte-identical. With <c>&gt;= 2</c> the generator assigns each node a heterogeneous Worker Group config
    /// (a served-Queue subset plus a Strict or Weighted Dispatch Policy) with deliberate overlap, so a shared
    /// Queue draws cross-group claiming contention. node-0 is the universal anchor (it serves every Queue), so
    /// every Queue survives the N−1 isolation budget — a stranded Queue is always a real bug.
    /// </summary>
    public int TopologyQueues { get; init; }

    /// <summary>
    /// Oracle self-test for the served-set-containment invariant (issue 0071): a node whose recorded served
    /// set is narrow is handed a DRIVER policy that ALSO serves a Queue NOT in that recorded set (the anchor's
    /// full set), while the oracle still checks against the narrow recorded set — so the node legitimately
    /// claims a foreign Queue and a live containment oracle MUST catch the Lease held outside the declared
    /// served set and fail with the seed. Pair with <see cref="TopologyQueues"/> >= 2. Never set in real regimes.
    /// </summary>
    public bool SabotageServedSet { get; init; }

    /// <summary>
    /// Drain-liveness self-test (issue 0136): restore the pre-fix behaviour where an Unroutable
    /// <c>ExecuteJob</c>'s terminal report is driven INLINE inside the claim batch instead of deferred to
    /// after it. With this on, a store fault on that inline report unwinds through <see cref="TryDrive"/> and
    /// abandons the sibling <c>ExecuteJob</c>s still queued behind it in the same <c>ClaimCompleted</c> batch —
    /// those jobs stay Leased, the Driver heartbeats their leases forever, and they never converge, so a live
    /// <see cref="InvariantId.DrainLiveness"/> oracle MUST trip. Off (the default and post-fix behaviour) defers
    /// the report, matching the production Shell. Never set in real regimes.
    /// </summary>
    public bool SabotageInlineUnroutableReport { get; init; }

    /// <summary>
    /// Self-test for the deferred-report durability fix. The 0136 deferral runs an Unroutable job's report
    /// AFTER its <c>ClaimCompleted</c> batch, but the production Shell enqueues each report as its OWN durable
    /// feedback event — so a transient store fault on a SIBLING command in the same batch (a follow-up claim,
    /// say) never discards an already-enqueued report. With this on, the deferred reports run only on the
    /// batch's NORMAL completion path instead of from a <c>finally</c>: a fault mid-batch unwinds before they
    /// run, so the deferred Unroutable jobs are never reported and stay in the Driver's executing set, their
    /// leases heartbeated forever, never converging — so a live <see cref="InvariantId.DrainLiveness"/> oracle
    /// MUST trip. Off (the default and post-fix behaviour) runs the deferred reports from a <c>finally</c> so
    /// a mid-batch fault can't lose them, matching the production Shell. Never set in real regimes.
    /// </summary>
    public bool SabotageDeferredUnroutableReport { get; init; }

    /// <summary>
    /// Weighted-under-load regime (issue 0075, ADR 0016): force EVERY node's Worker Group to the Weighted
    /// Dispatch Policy instead of leaving the Strict/Weighted choice to the topology coin in
    /// <see cref="MakePolicy"/>. Paired with a finite <see cref="PoolSize"/> (claims go partial) and Driver
    /// restarts (crashes rebuild the <c>SmoothWeightedRoundRobin</c> credit state fresh on <c>NewDriver</c>),
    /// this stresses SWRR credit accounting across short claims and restarts — the surface the pure-Core
    /// fairness unit test cannot reach because it assumes unbounded capacity and a fresh allocator. Honesty
    /// guard: without it a seed could draw an all-Strict topology and the regime would exercise no Weighted
    /// node at all (the test asserts <see cref="SimulationResult.WeightedNodeCount"/> &gt; 0). Gated behind
    /// <see cref="TopologyQueues"/> &gt;= 2 — with a single Queue <see cref="BuildTopology"/> returns before any
    /// policy is built, so this knob is inert and the existing battery stays byte-identical. It only SKIPS the
    /// policy coin (no new rng stream, no extra draw), so a default-false run is byte-identical to today.
    /// Fairness ratios are never asserted here (work redistributes under fault injection); work-conservation
    /// is verified solely as drain-time liveness (the convergence check), per ADR 0016.
    /// </summary>
    public bool ForceWeightedDispatch { get; init; }

    /// <summary>
    /// Virtual-time origin. The default matches the historical T0, so every regime that does
    /// not set it stays byte-identical; the Recurring-Schedule suites start near a DST
    /// transition so virtual time stays bounded while spanning the transition.
    /// </summary>
    public DateTimeOffset StartTime { get; init; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Recurring Schedules seeded before the run. Empty (the default) leaves the mint path
    /// dormant and adds no rng draws, so the determinism battery is untouched. Seeded
    /// schedules are removed at WorkloadEnd so the already-minted instances can drain.
    /// </summary>
    public IReadOnlyList<SeededSchedule> Schedules { get; init; } = [];

    /// <summary>
    /// Transition Observers registered for the run (§0076, ADR 0017). Empty (the default) builds no
    /// Observer Dispatch Driver and never polls a delivery cursor, so every existing regime stays
    /// byte-identical — observers cost nothing until something subscribes. Each node delivers each
    /// Observer at-least-once by claiming its cursor under a Lease; the Observer-delivery oracle
    /// proves liveness, in-order-per-Observer (modulo duplicates), and tolerates duplicates.
    /// </summary>
    public IReadOnlyList<ObserverRegistration> Observers { get; init; } = [];

    /// <summary>Max rows each node claims per Observer per poll (the bounded delivery batch).</summary>
    public int ObserverMaxBatch { get; init; } = 16;

    /// <summary>
    /// Delivery retry policy for every registered Observer (§0077): the backoff schedule and attempt
    /// ceiling the Dispatch Core applies to a failed delivery. The default mirrors job execution;
    /// resilience tests shrink it so a poison row reaches its dead-letter ceiling inside the drain window.
    /// </summary>
    public RetryPolicy ObserverDeliveryRetryPolicy { get; init; } = RetryPolicy.Default;

    /// <summary>
    /// Observer ids whose host callback fails on delivery (§0077), mapping each id to the delivery
    /// Attempt through which it keeps failing — a stand-in for a throwing, timing-out, or hung
    /// callback. A delivery on an Attempt past the threshold succeeds, so a small value models a flaky
    /// observer that recovers after backoff; <see cref="int.MaxValue"/> is a poison observer that
    /// never succeeds and must be dead-lettered after the ceiling, the cursor advancing past it. The
    /// Shell edge catches the failure so it never fail-stops the worker pump. Empty in real regimes.
    /// </summary>
    public IReadOnlyDictionary<string, int> FailingObservers { get; init; } =
        new Dictionary<string, int>(StringComparer.Ordinal);

    /// <summary>
    /// Oracle self-test for the Observer-delivery liveness invariant (§0078): a claimed batch is
    /// reported as delivered to the store (cursor advances) but the recording sink is NOT told — so a
    /// transition that genuinely should have been delivered is silently dropped, and a live oracle
    /// MUST catch the missing delivery and fail with the seed. Never set in real regimes.
    /// </summary>
    public bool SabotageObserverDelivery { get; init; }

    /// <summary>
    /// "Radioactive" chaos regime (the catastrophic fault profile the <c>--radioactive</c> swarm draws): every
    /// fault axis is active at maximum intensity, isolation episodes can be permanent, and the throttle surfaces
    /// are combined — a world that is, by construction, OUT of the convergence envelope the rest of the swarm
    /// stays inside. The bound-dependent liveness oracles assert <i>eventual progress within a config-derived
    /// window</i> — a property a deliberately non-converging world cannot satisfy — so this flag DISARMS exactly
    /// those: <see cref="InvariantId.DrainLiveness"/> (jobs may never drain under permanent loss) and
    /// <see cref="InvariantId.ExecuteLiveness"/> (a routable job can burn its Attempt budget across claim→crash
    /// cycles without ever executing, because Attempt increments on Claim). Every SAFETY oracle — Effect-Once
    /// (<see cref="InvariantId.OutcomeProvenance"/>, <see cref="InvariantId.NoDoubleExecution"/>,
    /// <see cref="InvariantId.SlotDoubleRelease"/>), legal-transition, attempt-ceiling, the Concurrency-Limit
    /// and served-set containment checks, terminal stability, and the runaway-step backstop — stays fully armed,
    /// because a consistency invariant must hold no matter how chaotic the world is. A tripped safety oracle in
    /// this regime is therefore a real, deep bug, not an over-intensity artifact. <see cref="InvariantId.MigrationLiveness"/>
    /// is already inert here (its gate requires crashes and store faults OFF, both maxed in this regime), and
    /// <see cref="InvariantId.ObserverDeliveryLiveness"/> never fires without registered Observers. Default false,
    /// so every existing regime stays byte-identical. Never set outside the radioactive swarm.
    /// </summary>
    public bool RadioactiveMode { get; init; }
}

/// <summary>
/// A Recurring Schedule to seed into a simulation. <see cref="CursorOffset"/> places the
/// schedule's Cursor relative to <see cref="SimulationOptions.StartTime"/> — a negative
/// offset seeds a backlog of missed ticks, the Catch-Up Policy's contested input.
/// </summary>
internal sealed record SeededSchedule
{
    public required string Id { get; init; }

    /// <summary>Standard 5-field cron; canonicalised when seeded.</summary>
    public required string Cron { get; init; }

    public string? TimeZoneId { get; init; }
    public CatchUpPolicy CatchUp { get; init; } = CatchUpPolicy.Skip;
    public bool NoOverlap { get; init; }
    public string Queue { get; init; } = "default";
    public TimeSpan CursorOffset { get; init; }
}

/// <summary>A seeded transient store fault for the storage-fault regime; the node retries next tick.</summary>
internal sealed class SimTransientFault() : Exception("simulated transient store fault");

/// <summary>
/// A failed Observer callback (§0077): the deterministic stand-in for a host observer that throws,
/// times out, or hangs. The Shell edge catches it and reports the delivery as failed, so it never
/// fail-stops the worker pump; the Dispatch Core then retries with backoff or dead-letters.
/// </summary>
internal sealed class ObserverCallbackFault() : Exception("simulated observer callback fault");

internal sealed record SimulationResult(
    ulong Seed,
    long Steps,
    int Crashes,
    int StaleOutcomes,
    IReadOnlyList<(Guid JobId, JobState State, int Attempt)> FinalJobs)
{
    public int Succeeded => FinalJobs.Count(j => j.State == JobState.Succeeded);
    public int DeadLettered => FinalJobs.Count(j => j.State == JobState.DeadLettered);
    public int Cancelled => FinalJobs.Count(j => j.State == JobState.Cancelled);

    /// <summary>Jobs that reached Quarantined via a dispatch-side Unroutable outcome (issue 0073).</summary>
    public int Quarantined => FinalJobs.Count(j => j.State == JobState.Quarantined);

    /// <summary>
    /// Virtual (simulated) cluster time this run spanned — the event-loop's <c>_now</c> at termination minus
    /// its start. A converging run breaks shortly after WorkloadEnd, so this is far less than the DrainEnd
    /// ceiling; a non-converging (radioactive) run grinds to DrainEnd. Summed across runs, it is the honest
    /// "equivalent cluster-time tested" figure — not the per-sim ceiling, which overstates converging runs.
    /// </summary>
    public TimeSpan VirtualElapsed { get; init; }

    /// <summary>Every instance minted from a seeded Recurring Schedule, with its final state.</summary>
    public IReadOnlyList<(string ScheduleId, DateTimeOffset Tick, JobState State)> MintedJobs { get; init; } = [];

    /// <summary>The seeded schedules captured at WorkloadEnd, just before removal (Cursor + SkippedTicks).</summary>
    public IReadOnlyList<ScheduleSnapshot> FinalSchedules { get; init; } = [];

    /// <summary>The ticks minted for one schedule, in tick order.</summary>
    public IReadOnlyList<DateTimeOffset> MintedTicks(string scheduleId) =>
        MintedJobs.Where(m => m.ScheduleId == scheduleId).Select(m => m.Tick).OrderBy(t => t).ToList();

    /// <summary>
    /// Each tracked job's Transition Log (issue 0057), oldest first: the (Timestamp, State,
    /// Attempt) timeline the In-Memory Store recorded under Virtual Time. Same seed → identical
    /// sequences, so the oracle can assert determinism on the recorded history itself, not just
    /// the final state.
    /// </summary>
    public IReadOnlyList<(Guid JobId, IReadOnlyList<(DateTimeOffset Timestamp, JobState State, int Attempt)> Timeline)>
        FinalTransitions { get; init; } = [];

    /// <summary>
    /// Whether ANY recorded transition carried a Failure Detail string (issue 0059). The Simulator
    /// drives the Drivers with injected faults and fake handlers — no real exceptions — and the
    /// Shell-side capture only fires on a real throw, so this MUST stay false: Failure Detail is
    /// production-only and never crosses the determinism boundary into the simulated Core.
    /// </summary>
    public bool AnyFailureDetailRecorded { get; init; }

    /// <summary>Operator Actions (§5.8) that took effect during the run — the operator-regime counters.</summary>
    public int OperatorCancels { get; init; }

    /// <summary>Cooperative cancels requested against a Leased job (CancelRequested set, round-trip pending).</summary>
    public int OperatorCancelRequests { get; init; }

    /// <summary>Cooperative cancels that completed: ExecutionCancelled → ReportOutcome(Cancelled) applied.</summary>
    public int CooperativeCancels { get; init; }

    public int OperatorRequeues { get; init; }
    public int QueuePauses { get; init; }
    public int ScheduleTriggers { get; init; }

    /// <summary>Isolation episodes that actually began under the N−1 budget (issue 0068).</summary>
    public int Isolations { get; init; }

    /// <summary>Episodes that began as a never-healing permanent loss (issue 0069); a subset of Isolations.</summary>
    public int PermanentLosses { get; init; }

    /// <summary>Crashes that discarded a non-empty outcome buffer — the buffer-loss-on-crash window (ADR 0035).</summary>
    public int OutcomeBufferDropped { get; init; }

    /// <summary>
    /// Leases swept by ExpireLeases over the run — the migration mechanism. In an isolation regime with
    /// crashes and heartbeat loss off, every expiry is a Lease lapsed under an isolated holder and re-homed
    /// to a survivor, so a positive count proves migration actually fired (issue 0069).
    /// </summary>
    public int LeasesExpired { get; init; }

    /// <summary>Outcome writes that committed but lost their ack, forcing a fenced retry (issue 0070).</summary>
    public int AckLosses { get; init; }

    /// <summary>
    /// Nodes whose Worker Group runs the Weighted Dispatch Policy (issue 0075). 0 in the single-Queue world;
    /// under the Topology Generator it counts the Weighted nodes the coin (or <see
    /// cref="SimulationOptions.ForceWeightedDispatch"/>) produced — so the Weighted-under-load regime can prove
    /// it actually exercised Weighted rather than silently drawing an all-Strict topology.
    /// </summary>
    public int WeightedNodeCount { get; init; }

    /// <summary>
    /// Per-Observer delivery tallies (§0076), keyed by Observer id: total deliveries handed to the
    /// recording sink (≥ unique, since duplicates are legal), the count of distinct transitions
    /// delivered ≥1, and dead-lettered deliveries. Empty when no Observer is registered.
    /// </summary>
    public IReadOnlyDictionary<string, (int Total, int Unique, int DeadLettered)> ObserverDeliveries { get; init; }
        = new Dictionary<string, (int, int, int)>();

    /// <summary>
    /// Oracle-pass observations where a Concurrency-Limited Queue held every slot at once (leased == limit),
    /// the LimitSaturated coverage signal (issue 0124). 0 when no limit is configured. Lights the formerly
    /// constant-false <c>Situation.LimitSaturated</c> post-hoc — derived from the count the I3 oracle already
    /// computes every step, so it draws no rng and leaves the determinism battery byte-identical.
    /// </summary>
    public int LimitSaturations { get; init; }

    /// <summary>
    /// Oracle-pass observations where a node's finite Backpressure pool was full (in-flight == PoolSize), so
    /// its next claim is blocked purely by backpressure — the BackpressureIdle coverage signal (issue 0124).
    /// 0 when the pool is unbounded. Lights the formerly constant-false <c>Situation.BackpressureIdle</c>
    /// post-hoc from the in-flight count the per-node-cap oracle already reads; no hot-path instrumentation.
    /// </summary>
    public int BackpressureIdleTicks { get; init; }

    /// <summary>
    /// The number of Poll events driven to a live (non-crashed) node during the run. With adaptive idle backoff
    /// (<see cref="SimulationOptions.MaxPollInterval"/> greater than <see cref="SimulationOptions.PollInterval"/>)
    /// an idle fleet polls fewer times over the same workload, so this drops well below the fixed-cadence count.
    /// </summary>
    public long PollCount { get; init; }
}

/// <summary>
/// The seeded whole-cluster simulator (ADR-0008): N Node Drivers + the In-Memory Store
/// driven through Virtual Time by one event queue, with per-node clock skew, crash/restart,
/// heartbeat loss, and executions that outlive their Leases. One 64-bit seed fully
/// determines a run; the invariant oracle runs after every step and any failure message
/// carries the seed for exact replay.
/// </summary>
internal sealed class Simulator(SimulationOptions options, FaultPlan? faultPlan = null)
{
    private enum EventKind { Enqueue, Poll, Heartbeat, ExecutionComplete, Restart, Hint, OperatorAction, IsolationStart, IsolationHeal }

    private sealed record SimEvent(EventKind Kind, int Node, int Epoch, JobRecord? Job, bool Fails)
    {
        /// <summary>The episode an <see cref="EventKind.IsolationStart"/> carries; null for every other kind.</summary>
        public IsolationEpisode? Episode { get; init; }
    }

    private sealed class SimNode
    {
        public required NodeDriver Driver { get; set; }
        public required TimeSpan Skew { get; init; }
        public bool Crashed { get; set; }
        public int Epoch { get; set; }
        // The current idle poll-backoff delay, used only when the run enables adaptive polling
        // (MaxPollInterval > PollInterval). A claim outcome folds into it: work found or due-now pressure
        // snaps it to the floor, an empty poll with a future next-due sleeps to it, an empty poll with no
        // next-due grows it toward the ceiling in step with how long the node has been idle. Starts at the
        // floor (PollInterval).
        public TimeSpan PollDelay { get; set; }
        // The instant this node first went idle (first empty poll with no next-due hint), or null while busy.
        // The idle ramp grows PollDelay by elapsed idle time, not by the count of empty polls, so a drain
        // tail's burst of empty re-polls cannot saturate the delay to the ceiling. Mirrors _idleSince.
        public DateTimeOffset? IdleSince { get; set; }
        // The jobs this node is executing, keyed by JobId → the Attempt's JobRecord. The record is
        // kept (not just the id) so a cooperative cancel can raise ExecutionCancelled for the exact
        // in-flight Attempt, mirroring the pumps' captured flight.Job rather than re-reading state.
        public Dictionary<Guid, JobRecord> Executing { get; } = [];
    }

    private readonly DeterministicRandom _rng = new(options.Seed);
    private readonly DeterministicRandom _hintRng = new(options.Seed ^ 0x48494E54_48494E54UL); // "HINT": its own stream
    // The single fault path (issue 0083): every fault decision is taken here, recorded in generate mode and
    // looked up in replay mode. Generate (a null injected plan, the default) draws the store-fault axis from
    // its historical `Seed ^ "FAULT"` stream inside the FaultPlan, so a generate run is byte-identical to the
    // pre-extraction draw. Issue 0084 routes the remaining axes through it.
    private readonly FaultPlan _faultPlan = faultPlan ?? FaultPlan.Generate(options.Seed);
    private readonly DeterministicRandom _opRng = new(options.Seed ^ 0x4F50455241544F52UL); // "OPERATOR": its own stream
    // Topology Generator (issue 0071): its own stream so a TopologyQueues < 2 makes zero draws and the existing
    // battery stays byte-identical. The generated Queue names, each node's recorded served set (what the oracle
    // checks containment against), and each node's Dispatch Policy are built in Run() BEFORE the nodes, so
    // NewDriver can read the per-node policy. Off → _topoQueues stays ["default"] and the served sets all
    // ["default"], so the containment oracle is a no-op and Enqueue makes no topology draw.
    private readonly DeterministicRandom _topoRng = new(options.Seed ^ 0x544F504F4C4F4759UL); // "TOPOLOGY": its own stream
    private IReadOnlyList<string> _topoQueues = ["default"];
    private IReadOnlyList<string>[] _servedSets = [];
    private Core.DispatchPolicy[] _policies = [];
    private bool _servedSetSabotaged;
    private readonly InMemoryJobStore _store = new();
    private readonly PriorityQueue<SimEvent, (DateTimeOffset At, long Seq)> _queue = new();
    private readonly List<Guid> _jobIds = [];
    // Minted instances, captured the step they commit (jobId → its cron tick). The tick is
    // recorded here, not read back from JobRecord.DueTime, because a retry (Lease expiry or
    // handler failure) overwrites DueTime with the node's next-attempt clock.
    private readonly Dictionary<Guid, DateTimeOffset> _minted = [];
    private readonly Dictionary<Guid, (Guid ParentId, DependencyMode Mode)> _parentsByChild = [];
    private readonly Dictionary<Guid, JobState> _terminalSeen = [];
    // Operator-action regime: the Queues the oracle believes are Paused, and the jobs that were
    // Leased at the previous oracle check — together they catch a Claim from a Paused Queue.
    private readonly HashSet<string> _oraclePausedQueues = [];
    private HashSet<Guid> _leasedPrev = [];
    // Cancellation provenance (§5.8): jobs the operator legitimately issued a cancel against, and
    // jobs an on-success dependency latch cancelled because a parent reached a non-success
    // terminal state. A job may only be Cancelled for one of these reasons (the sim has no other
    // cancellation source); the dependency set is sticky so a later parent Requeue can't unjustify
    // a cancel the latch already fired.
    private readonly HashSet<Guid> _cancelTargets = [];
    private readonly HashSet<Guid> _dependencyCancelled = [];
    // Legal-transition oracle (§3): the last Transition Log entry validated per tracked job. The walk
    // reads a job's history only when its (State, Attempt) changed since the previous step, so each
    // recorded edge is checked once and the walk stays off the hot path. Truncation past
    // MaxTransitionsPerJob is tolerated via the ordinal — a surviving entry whose predecessor aged out
    // keeps no in-edge to check.
    private readonly Dictionary<Guid, (JobState State, int Attempt, long Ordinal)> _lastTransition = [];
    private bool _legalTransitionSabotaged;
    // At-least-once-execute liveness: every job ever handed to a node for execution (issue 0042). A job
    // that reaches a ran-to-completion terminal (Succeeded/DeadLettered) must appear here — it cannot
    // succeed or fail an Attempt it never ran.
    private readonly HashSet<Guid> _everExecuted = [];
    // Audit-log completeness (§5.8): the multiset of (Action, Target) the sim issued to the store at a
    // site the store audits, so the end-of-run check can assert the recorded log matches exactly — no
    // operator action silently unlogged, none double-logged. Empty (and the check a no-op) with no
    // operator actions, so the determinism battery is untouched.
    private readonly Dictionary<(OperatorAction Action, string Target), int> _issuedAudits = [];
    private bool _auditSabotaged;
    private int _operatorCancels;
    private int _operatorCancelRequests;
    private int _cooperativeCancels;
    private int _operatorRequeues;
    private int _queuePauses;
    private int _scheduleTriggers;
    private bool _pausedQueuesResumed;
    private readonly RetryPolicy _retryPolicy = new()
    {
        MaxAttempts = options.MaxAttempts,
        Backoff = _ => options.Backoff,
    };
    // Node Isolation (issue 0068): the deep module owning the N−1 fault budget and the isolation rng
    // stream. Always constructed (cheap), but it makes no draws and isolates nothing until the regime
    // seeds episodes, so a zero IsolationCount leaves the determinism battery byte-identical.
    private readonly IsolationScheduler _isolation = new(options.Seed, options.NodeCount);
    private int _isolations;
    private int _permanentLosses;
    private int _leasesExpired;
    // Ack-loss isolation (issue 0070): its own stream so a zero probability makes no draws and the battery
    // stays byte-identical. _ackLost fences the injection to once per (job, attempt) — the retry that
    // follows is a normal report the (workerId, attempt) fence must reject as stale.
    private readonly DeterministicRandom _ackLossRng = new(options.Seed ^ 0x41434B4C4F535353UL); // "ACKLOSS": its own stream
    private readonly HashSet<(Guid JobId, int Attempt)> _ackLost = [];
    private int _ackLosses;
    // Quarantine-via-routing (issue 0073): the dispatch-side unroutable axis on its own stream so a zero
    // probability makes no draws and the battery stays byte-identical. A job committed to _unroutable is
    // branched at ExecuteJob exactly as the pump branches a RouteResult.Unroutable — it drives
    // ExecutionUnroutable instead of executing, so it reaches Quarantined and never enters _everExecuted.
    private readonly DeterministicRandom _routeRng = new(options.Seed ^ 0x554E524F55544142UL); // "UNROUTAB": its own stream
    private readonly HashSet<Guid> _unroutable = [];
    // Outcome-Provenance Oracle (issue 0068): a captured violation message, asserted in CheckInvariants
    // so the oracle "runs in the oracle" — the store applied an outcome whose reporter did not hold the
    // live Lease for that Attempt. Null on every legitimate run.
    private string? _provenanceViolation;
    // Per-Queue Concurrency Limits (issue 0074): the effective limits, computed once in Run() by folding the
    // ConcurrencyLimit shorthand into ConcurrencyLimits. Empty → no limit configured (the I3 loop and the
    // slot detector are no-ops, so the battery is byte-identical). Slot accounting tracks, per (jobId,
    // attempt) on a LIMITED Queue, how many times that Attempt's slot was released by an APPLIED outcome —
    // sound WITHIN one Attempt budget because the store increments Attempt on every claim (Claim: Attempt+1),
    // so each (jobId, attempt) is Leased at most once and legitimately releases its slot at most once. The one
    // exception is an operator Requeue, which resets the Attempt budget to 0 (§3) and so recycles Attempt
    // numbers across a fresh lease lifetime — OperatorRequeue forgets the prior tally for the job so the two
    // legitimate releases of recycled Attempt 1 are not read as a double (issue 0130). A second release for one
    // Attempt WITHIN a budget is the slot-double-release symptom (a dropped fence double-applying a stale
    // outcome); captured here and asserted in CheckInvariants beside the provenance violation.
    private IReadOnlyDictionary<string, int> _limits = new Dictionary<string, int>();
    private readonly Dictionary<(Guid JobId, int Attempt), int> _slotReleases = [];
    private string? _slotDoubleReleaseViolation;
    private bool _slotReleaseSabotaged;
    // Coverage saturation signals (issue 0124): tallied in the oracle pass (CheckInvariants), which already
    // recomputes per-Queue leased counts and reads each node's real in-flight set every step — so these draw
    // NO rng and never perturb the determinism battery (both stay 0 when no limit is configured and the pool
    // is unbounded, exactly the default regime). They light the two formerly constant-false Situations
    // (LimitSaturated / BackpressureIdle) post-hoc from the SimulationResult, never from hot-path code.
    private int _limitSaturations;
    private int _backpressureIdleTicks;
    // Count of Poll events driven to a live (non-crashed) node, for measuring the idle-poll load
    // an adaptive-backoff run saves against a fixed-cadence run over the same workload.
    private long _pollCount;
    private SimNode[] _nodes = [];
    // The store the node hot-path writes go through. Identical to _store in every real regime; the
    // fence-dropping sabotage wraps it so a stale ReportOutcome is forced through (issue 0068). Reads in
    // the oracle always go to _store directly, so they see committed truth regardless of the sabotage.
    private IJobStore? _outcomeStore;
    private IJobStore OutcomeStore =>
        _outcomeStore ??= options.SabotageOutcomeFence ? new FenceDroppingStore(_store) : _store;
    // SabotageBatchFence (ADR 0035): a fence-dropping handle the batch path routes a tail row through so a
    // stale row in a multi-row batch lands. Distinct from OutcomeStore, which the node uses for ALL writes.
    private FenceDroppingStore? _batchFenceDroppingStore;
    private FenceDroppingStore BatchFenceDroppingStore => _batchFenceDroppingStore ??= new FenceDroppingStore(_store);
    private FaultInjectingStore? _faulty;
    private FaultInjectingStore Faulty => _faulty ??= new FaultInjectingStore(_store, op => StoreShouldFault(-1, op));
    // One store handle per node (issue 0068): each faults on the shared StoreFaultProbability OR when the
    // Isolation Scheduler has this node cut off. The IJobStore interface is unchanged; an isolated node's
    // hot-path call throws the existing transient, which the Node Driver already retries.
    private FaultInjectingStore[] _nodeFaulty = [];
    // Transition Observers (§0076, ADR 0017). One sans-IO Observer Dispatch Driver per node, each
    // claiming every registered Observer's cursor under a Lease on its poll cadence. Null array when
    // no Observer is registered, so the feature is zero-cost and the determinism battery untouched.
    private ObserverDispatchDriver?[] _observerDrivers = [];
    // The recording sink (ADR 0017): every delivery handed to the callback, in arrival order, keyed
    // by Observer — duplicates kept on purpose (delivery is at-least-once, never single). The oracle
    // walks this against the expected set.
    private readonly Dictionary<string, List<(Guid JobId, long Ordinal, JobState State, int Attempt)>> _observerDeliveries
        = new(StringComparer.Ordinal);
    // Oracle bookkeeping. _observerExpected: every transition matching an Observer's subscription, by
    // (jobId, ordinal), accumulated as the Transition Log walk sees it — it persists even if a job is
    // later purged. _observerFirstSeen: the transitions already delivered ≥1 (for duplicate tolerance
    // + the in-order check). _observerJobMaxOrdinal: the highest first-delivered ordinal per
    // (Observer, job), so first deliveries are proven ascending per job. _observerDeliveryCursor: how
    // far the per-step oracle has walked each sink, so it processes only new deliveries.
    private readonly Dictionary<string, HashSet<(Guid JobId, long Ordinal)>> _observerExpected
        = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<(Guid JobId, long Ordinal)>> _observerFirstSeen
        = new(StringComparer.Ordinal);
    private readonly Dictionary<(string ObserverId, Guid JobId), long> _observerJobMaxOrdinal = [];
    private readonly Dictionary<string, int> _observerDeliveryCursor = new(StringComparer.Ordinal);
    private long _sequence;
    private long _steps;
    private int _crashes;
    private int _staleOutcomes;
    // Crashes that discarded a non-empty outcome buffer (ADR 0035): the buffer-loss-on-crash window. Tallied
    // off the Driver's buffered count at crash time — no rng, no hot-path instrumentation, battery untouched.
    private int _outcomeBufferDropped;
    private long? _mintScanCursor;
    private bool _schedulesRemoved;
    private IReadOnlyList<ScheduleSnapshot> _finalSchedules = [];
    private readonly DateTimeOffset _start = options.StartTime;
    private DateTimeOffset _now = options.StartTime;

    private DateTimeOffset WorkloadEnd => _start + options.WorkloadDuration;
    private DateTimeOffset DrainEnd => WorkloadEnd + options.DrainAllowance;

    private NodeDriver NewDriver(int node) => new(new NodeOptions
    {
        WorkerId = $"node-{node}",
        // Off (the default): the exact historical Strict(["default"]). On: the per-node policy the Topology
        // Generator built into _policies — which, under SabotageServedSet, serves a Queue beyond the node's
        // recorded served set so the containment oracle trips.
        Policy = _policies.Length == 0 ? new Core.DispatchPolicy.Strict(["default"]) : _policies[node],
        LeaseDuration = options.LeaseDuration,
        RetryPolicy = _retryPolicy,
        // Backpressure (issue 0072): plumb the node-local pool bound through the Driver's existing
        // claim subtraction. The cap-sabotage builds the Driver UNBOUNDED so it over-admits past the
        // configured PoolSize, tripping the oracle. When PoolSize is int.MaxValue and not sabotaged this
        // is byte-for-byte the historical NodeOptions, so the existing seed battery stays untouched.
        PoolSize = options.SabotagePoolSize ? int.MaxValue : options.PoolSize,
    });

    /// <summary>
    /// Builds the deterministic Topology (issue 0071): the named Queue list, each node's recorded served set,
    /// and each node's Dispatch Policy. Drawn ONLY from the topology stream, and only when the regime is on
    /// (<see cref="SimulationOptions.TopologyQueues"/> &gt;= 2) — off makes zero draws and leaves the single-Queue
    /// world (<c>_topoQueues = ["default"]</c>, every served set <c>["default"]</c>), so the existing battery is
    /// byte-identical. Must run BEFORE the nodes are constructed so <see cref="NewDriver"/> can read the policy.
    /// </summary>
    /// <remarks>
    /// node-0 is the universal anchor: its served set is ALL Queues. Because the Isolation Scheduler's N−1 budget
    /// keeps at least one node reachable and every healing isolation heals by DrainEnd, the anchor guarantees
    /// every Queue is served by a node that survives the isolation budget — so a stranded Queue is always a real
    /// bug, never a legitimate no-server idle. Every other node draws a non-empty random subset with deliberate
    /// overlap (node 1 always includes "default", and at least one shared Queue is served by ≥2 nodes), giving
    /// cross-group claiming contention. Each non-anchor node is Strict (its served set, deterministically ordered)
    /// or Weighted (its served set, weights ≥1) by a topology-stream coin.
    /// </remarks>
    private void BuildTopology()
    {
        if (options.TopologyQueues < 2)
        {
            return; // single-Queue world: zero topology draws, byte-identical battery
        }

        var queues = new List<string>(options.TopologyQueues) { "default" };
        for (var q = 1; q < options.TopologyQueues; q++)
        {
            queues.Add($"q{q}");
        }
        _topoQueues = queues;

        _servedSets = new IReadOnlyList<string>[options.NodeCount];
        _policies = new Core.DispatchPolicy[options.NodeCount];
        for (var n = 0; n < options.NodeCount; n++)
        {
            IReadOnlyList<string> served;
            if (n == 0)
            {
                served = queues; // the universal anchor: serves every Queue, survives the N−1 budget
            }
            else
            {
                // A non-empty random subset with deliberate overlap. Node 1 always includes "default" so the
                // anchor's "default" is shared by ≥2 nodes (cross-group contention is guaranteed); every other
                // node draws each Queue in with probability 1/2 and is back-filled to a single Queue if empty.
                var subset = new List<string>();
                foreach (var queue in queues)
                {
                    if ((n == 1 && queue == "default") || _topoRng.NextDouble() < 0.5)
                    {
                        subset.Add(queue);
                    }
                }
                if (subset.Count == 0)
                {
                    subset.Add(queues[_topoRng.Next(queues.Count)]);
                }
                served = subset;
            }
            _servedSets[n] = served;
            _policies[n] = MakePolicy(n, served);
        }
    }

    /// <summary>
    /// Turns a node's recorded served set into a Dispatch Policy: a topology-stream coin chooses Strict (the
    /// served set in deterministic declaration order — earlier Queues preempt) or Weighted (each served Queue a
    /// weight in 1..4). The <see cref="SimulationOptions.SabotageServedSet"/> self-test widens the FIRST narrow
    /// non-anchor node's DRIVER policy to also serve the anchor's full Queue set, while its recorded served set
    /// (what the oracle checks) stays narrow — so the node legitimately claims a foreign Queue and the
    /// containment oracle MUST trip. The recorded served set is never widened, only the driver's claim reach.
    /// </summary>
    private Core.DispatchPolicy MakePolicy(int node, IReadOnlyList<string> servedSet)
    {
        var claimQueues = servedSet;
        if (options.SabotageServedSet && node != 0 && servedSet.Count < _topoQueues.Count && !_servedSetSabotaged)
        {
            _servedSetSabotaged = true;
            claimQueues = _topoQueues; // claim beyond the recorded served set: the oracle must catch it
        }
        // Weighted-under-load (issue 0075): force Weighted, SKIPPING the policy coin so a default-false run
        // draws and branches exactly as before (byte-identical). The weight draws are identical to the coin's
        // Weighted branch, so the regime still picks deterministic weights from the topology stream.
        if (options.ForceWeightedDispatch)
        {
            return new Core.DispatchPolicy.Weighted([.. claimQueues.Select(q => (q, 1 + _topoRng.Next(4)))]);
        }
        return _topoRng.NextDouble() < 0.5
            ? new Core.DispatchPolicy.Strict(claimQueues)
            : new Core.DispatchPolicy.Weighted([.. claimQueues.Select(q => (q, 1 + _topoRng.Next(4)))]);
    }

    /// <summary>
    /// The effective per-Queue Concurrency Limits (issue 0074): <see cref="SimulationOptions.ConcurrencyLimits"/>
    /// with the <see cref="SimulationOptions.ConcurrencyLimit"/> shorthand folded in as the <c>"default"</c>
    /// Queue's cap. Empty when neither is set, so the I3 loop and the slot detector are no-ops and the battery
    /// is byte-identical.
    /// </summary>
    private IReadOnlyDictionary<string, int> EffectiveLimits()
    {
        if (options.ConcurrencyLimits.Count == 0 && options.ConcurrencyLimit is null)
        {
            return _limits; // empty: no store call, no oracle work
        }
        var merged = new Dictionary<string, int>(options.ConcurrencyLimits);
        if (options.ConcurrencyLimit is { } limit)
        {
            merged["default"] = limit;
        }
        return merged;
    }

    public SimulationResult Run(CancellationToken cancellationToken = default)
    {
        _limits = EffectiveLimits();
        foreach (var (queue, limit) in _limits)
        {
            // Sabotage (oracle self-test): the store admits far past the limit the oracle enforces, so a
            // working I3 check MUST catch leased > limit on that Queue and fail with the seed.
            Get2(_store.SetConcurrencyLimitAsync(queue, options.Sabotage ? int.MaxValue : limit, "sim-config", _now));
            // The store now audits the limit write (§5.8), so the completeness ledger must expect it.
            RecordIssuedAudit(OperatorAction.SetConcurrencyLimit, queue);
        }

        SeedSchedules();
        BuildTopology(); // issue 0071: per-node served sets + policies, before the nodes so NewDriver reads them

        _nodes = new SimNode[options.NodeCount];
        _nodeFaulty = new FaultInjectingStore[options.NodeCount];
        for (var i = 0; i < options.NodeCount; i++)
        {
            var node = i; // capture for the per-node fault closure
            _nodeFaulty[i] = new FaultInjectingStore(OutcomeStore, op => StoreShouldFault(node, op) || _isolation.IsIsolated(node));
            _nodes[i] = new SimNode
            {
                Driver = NewDriver(i),
                Skew = _rng.NextTimeSpan(options.MaxClockSkew * 2) - options.MaxClockSkew,
                PollDelay = options.PollInterval,
            };
            Schedule(_start + _rng.NextTimeSpan(options.PollInterval), new SimEvent(EventKind.Poll, i, 0, null, false));
            Schedule(_start + _rng.NextTimeSpan(options.HeartbeatInterval), new SimEvent(EventKind.Heartbeat, i, 0, null, false));
        }

        // Observer Dispatch Drivers (§0076): one per node, only when something subscribes — so a run
        // with no Observer never constructs a driver, never claims a cursor, and stays byte-identical.
        _observerDrivers = new ObserverDispatchDriver?[options.NodeCount];
        if (options.Observers.Count > 0)
        {
            foreach (var observer in options.Observers)
            {
                _observerDeliveries[observer.Id] = [];
                _observerExpected[observer.Id] = [];
                _observerFirstSeen[observer.Id] = [];
                _observerDeliveryCursor[observer.Id] = 0;
            }
            for (var i = 0; i < options.NodeCount; i++)
            {
                _observerDrivers[i] = new ObserverDispatchDriver(new ObserverDispatchOptions
                {
                    WorkerId = $"node-{i}",
                    Observers = options.Observers,
                    MaxBatch = options.ObserverMaxBatch,
                    LeaseDuration = options.LeaseDuration,
                    DeliveryRetryPolicy = options.ObserverDeliveryRetryPolicy,
                });
            }
        }

        for (var i = 0; i < options.JobCount; i++)
        {
            Schedule(_start + _rng.NextTimeSpan(options.WorkloadDuration / 2), new SimEvent(EventKind.Enqueue, -1, 0, null, false));
        }

        // Operator Actions spread across the whole workload window, drawn from their own stream so
        // a zero count makes no draws and leaves every existing regime's interleaving untouched.
        for (var i = 0; i < options.OperatorActionCount; i++)
        {
            Schedule(_start + _opRng.NextTimeSpan(options.WorkloadDuration), new SimEvent(EventKind.OperatorAction, -1, 0, null, false));
        }

        // Node Isolation episodes (issue 0068), drawn from the dedicated isolation stream so a zero count
        // makes no draws and the determinism battery is byte-identical. Durations exceed the Lease so an
        // isolated node's Lease always lapses while it is cut off — the survivor re-leases (Attempt
        // incremented) and the healed node's stale report hits the fence. The window keeps starts inside
        // the workload; the longest episode still heals far inside the drain allowance, so isolation never
        // outlives the fault window.
        foreach (var episode in _isolation.Plan(
            options.IsolationCount, _start, options.WorkloadDuration,
            minDuration: options.LeaseDuration * 1.5, maxDuration: options.LeaseDuration * 3,
            permanentLossProbability: options.PermanentLossProbability))
        {
            Schedule(episode.StartAt, new SimEvent(EventKind.IsolationStart, episode.Node, 0, null, false) { Episode = episode });
        }

        while (_queue.TryDequeue(out var simEvent, out var at))
        {
            // Honor an external deadline mid-simulation (VOPR overnight runs): a single sim can grind through up
            // to 10M steps (RunawayEventLoop cap below), which on a heavy world is tens of minutes of wall-clock.
            // The discovery loop only checks its token BETWEEN iterations, so without this a --duration bound can
            // overshoot by a whole iteration. Default token is None, so every replay/minimize/test run — and the
            // determinism battery — is byte-identical; only the live wall-clock-bounded loop ever observes cancel.
            cancellationToken.ThrowIfCancellationRequested();
            _now = at.At;
            if (_now > DrainEnd)
            {
                break;
            }

            RemoveSchedulesAtWorkloadEnd();
            ResumePausedQueuesAtWorkloadEnd();
            Process(simEvent);
            _steps++;
            IngestMintedJobs();
            CheckInvariants();

            if (_now > WorkloadEnd && AllTerminal() && ObserversDrained())
            {
                break;
            }

            Invariant(InvariantId.RunawayEventLoop, _steps < 10_000_000, "runaway event loop");
        }

        // Slot-non-leak liveness (issue 0074, ADR 0016): work-conservation as drain-time liveness. A leaked
        // Concurrency slot (an under-release) caps a limited Queue below its true budget, stranding that
        // Queue's due work past DrainEnd — so convergence fails. The stuck-job diagnostic names the blocking
        // Queue/limit/constraint (the Migration-Liveness Oracle pattern of issue 0069), so a leak points at a
        // Queue rather than surfacing as a bare "not all terminal".
        // Disarmed under the radioactive regime: permanent node loss + maxed crash/store faults make a
        // never-draining world the EXPECTED outcome, not a bug — the safety oracles carry the signal there.
        if (!options.RadioactiveMode)
        {
            Invariant(InvariantId.DrainLiveness, AllTerminal(), $"liveness: not all jobs reached a terminal state by the end of the drain window{StuckJobDiagnostic()}");
        }

        // Observer-delivery liveness (§0076, ADR 0017): every transition matching a registered
        // Observer was delivered to its recording sink at least once, or dead-lettered — nothing
        // silently lost. Tolerates duplicates; never asserts single delivery.
        ObserverLivenessCheck();

        // At-least-once-execute liveness: a ran-to-completion terminal (Succeeded or DeadLettered) is
        // only reachable by running an Attempt, so every such job must have been handed to a node.
        // Cancelled (idle/latch cancels never run) and unroutable jobs are excluded. An unroutable job is
        // NEVER dispatched to a handler (issue 0073), so it legitimately never executes. It usually ends
        // Quarantined, but under store faults on its quarantine report its Lease can instead keep expiring
        // — the Driver never heartbeats it (it was never in _executing), so the Lease lapses, the job is
        // rescheduled, and after MaxAttempts of this the Attempt ceiling dead-letters it (issue 0137): a
        // handler-less DeadLettered that is still legitimately terminal. Excluding by the unroutable draw
        // (not by terminal STATE) keeps full teeth on routable jobs, which must always have executed.
        foreach (var jobId in TrackedJobs())
        {
            var state = Get(_store.GetJobAsync(jobId))!.State;
            // Disarmed under the radioactive regime: Attempt increments on Claim, so maxed crashes + permanent
            // loss let a routable job exhaust its Attempt budget across claim→crash cycles and reach a terminal
            // (DeadLettered) without ever entering _everExecuted — expected here, not a bug. QuarantineNotExecuted
            // below stays armed: a Quarantined job must still NEVER have executed, in any regime.
            if (!options.RadioactiveMode)
            {
                Invariant(
                    InvariantId.ExecuteLiveness,
                    state is not (JobState.Succeeded or JobState.DeadLettered)
                        || _everExecuted.Contains(jobId)
                        || _unroutable.Contains(jobId),
                    $"liveness: job {jobId} reached {state} but never executed");
            }
            // The dual of the above, exercising the Quarantined exclusion the comment promises (issue 0073):
            // a Quarantined job was unroutable and NEVER dispatched, so it must be absent from _everExecuted.
            // With no unroutable axis no job is ever Quarantined, so this asserted nothing until issue 0073;
            // it is now a live check that the never-executed property holds for every quarantined job.
            Invariant(
                InvariantId.QuarantineNotExecuted,
                state != JobState.Quarantined || !_everExecuted.Contains(jobId),
                $"liveness: job {jobId} reached Quarantined yet was recorded as executed");
        }

        // Audit-log completeness (§5.8): every Operator Action the sim issued to the store was recorded
        // in the audit log exactly once — none silently unlogged, none double-logged. Compared per target
        // (a job id, Queue, or schedule id) as a multiset over action classes, so a missing, duplicated,
        // or spurious record on any touched target trips. A no-op with no operator actions issued.
        foreach (var target in _issuedAudits.Keys.Select(k => k.Target).Distinct())
        {
            var recorded = Get(_store.ListAuditRecordsAsync(target))
                .GroupBy(a => a.Action)
                .ToDictionary(g => g.Key, g => g.Count());
            var issued = _issuedAudits
                .Where(kv => kv.Key.Target == target)
                .ToDictionary(kv => kv.Key.Action, kv => kv.Value);
            foreach (var action in recorded.Keys.Union(issued.Keys))
            {
                Invariant(
                    InvariantId.AuditCompleteness,
                    recorded.GetValueOrDefault(action) == issued.GetValueOrDefault(action),
                    $"audit-log: {action} on {target} recorded {recorded.GetValueOrDefault(action)}× "
                    + $"but the sim issued it {issued.GetValueOrDefault(action)}×");
            }
        }

        var finals = _jobIds
            .Select(id => Get(_store.GetJobAsync(id))!)
            .Select(j => (j.JobId, j.State, j.Attempt))
            .ToList();
        var minted = _minted
            .Select(kv =>
            {
                var job = Get(_store.GetJobAsync(kv.Key))!;
                return (job.ScheduleId!, Tick: kv.Value, job.State);
            })
            .ToList();
        // The Transition Log per tracked job (issue 0057): same seed → identical timelines, so
        // the oracle can assert determinism on the recorded history. Ordered oldest first.
        var rawHistories = _jobIds
            .OrderBy(id => id)
            .Select(id => (id, History: Get(_store.GetJobHistoryAsync(id))))
            .ToList();
        var transitions = rawHistories
            .Select(h => (h.id, (IReadOnlyList<(DateTimeOffset, JobState, int)>)
                [.. h.History.Select(t => (t.Timestamp, t.State, t.Attempt))]))
            .ToList();
        // Failure Detail is production-only: the Simulator throws no real exceptions, so every
        // recorded transition must carry null detail (issue 0059, ADR 0011 determinism boundary).
        var anyFailureDetail = rawHistories.Any(h => h.History.Any(t => t.FailureDetail is not null));
        return new SimulationResult(options.Seed, _steps, _crashes, _staleOutcomes, finals)
        {
            VirtualElapsed = _now - _start,
            MintedJobs = minted,
            FinalSchedules = _finalSchedules,
            FinalTransitions = transitions,
            AnyFailureDetailRecorded = anyFailureDetail,
            OperatorCancels = _operatorCancels,
            OperatorCancelRequests = _operatorCancelRequests,
            CooperativeCancels = _cooperativeCancels,
            OperatorRequeues = _operatorRequeues,
            QueuePauses = _queuePauses,
            ScheduleTriggers = _scheduleTriggers,
            Isolations = _isolations,
            PermanentLosses = _permanentLosses,
            OutcomeBufferDropped = _outcomeBufferDropped,
            LeasesExpired = _leasesExpired,
            AckLosses = _ackLosses,
            WeightedNodeCount = _policies.Count(p => p is Core.DispatchPolicy.Weighted),
            LimitSaturations = _limitSaturations,
            BackpressureIdleTicks = _backpressureIdleTicks,
            PollCount = _pollCount,
            ObserverDeliveries = options.Observers.ToDictionary(
                o => o.Id,
                o => (
                    Total: _observerDeliveries[o.Id].Count,
                    Unique: _observerFirstSeen[o.Id].Count,
                    DeadLettered: Get(_store.ListObserverDeadLettersAsync(o.Id)).Count),
                StringComparer.Ordinal),
        };
    }

    /// <summary>Seeds the Recurring Schedules before the run; no rng, so empty leaves the battery untouched.</summary>
    private void SeedSchedules()
    {
        foreach (var seed in options.Schedules)
        {
            Get2(_store.UpsertScheduleAsync(new ScheduleRecord
            {
                ScheduleId = seed.Id,
                Cron = Core.CronExpression.Parse(seed.Cron).Canonical,
                WireName = "sim-schedule",
                Payload = ReadOnlyMemory<byte>.Empty,
                Queue = seed.Queue,
                Cursor = _start + seed.CursorOffset,
                TimeZoneId = seed.TimeZoneId,
                CatchUp = seed.CatchUp,
                NoOverlap = seed.NoOverlap,
            }));
        }
    }

    /// <summary>
    /// One-shot at WorkloadEnd: capture the schedules (Cursor + recorded SkippedTicks) then
    /// remove them so no new ticks mint and the already-minted instances drain to terminal.
    /// </summary>
    private void RemoveSchedulesAtWorkloadEnd()
    {
        if (_schedulesRemoved || options.Schedules.Count == 0 || _now < WorkloadEnd)
        {
            return;
        }
        _finalSchedules = Get(_store.ListSchedulesAsync());
        foreach (var seed in options.Schedules)
        {
            Get2(_store.RemoveScheduleAsync(seed.Id));
        }
        _schedulesRemoved = true;
    }

    /// <summary>
    /// One-shot at WorkloadEnd: lift every operator Pause so the cluster can drain. Faults and
    /// Operator Actions stop at WorkloadEnd by construction; a Queue left Paused would stall the
    /// drain forever, so the oracle's liveness check could never pass. Clears the oracle's belief
    /// too, so the legitimate drain claims that follow are not mistaken for claims-while-paused.
    /// </summary>
    private void ResumePausedQueuesAtWorkloadEnd()
    {
        if (_pausedQueuesResumed || _oraclePausedQueues.Count == 0 || _now < WorkloadEnd)
        {
            return;
        }
        foreach (var queue in _oraclePausedQueues.ToList())
        {
            Get2(_store.ResumeQueueAsync(queue, "sim-drain", _now));
            RecordIssuedAudit(OperatorAction.ResumeQueue, queue); // the drain resume is audited too
        }
        _oraclePausedQueues.Clear();
        _pausedQueuesResumed = true;
    }

    /// <summary>
    /// Folds newly committed minted instances into <see cref="_minted"/> so the oracle and the
    /// drain track them. Incremental by Sequence cursor (each job ingested once); a no-op with no
    /// seeded schedules, so the determinism battery makes no extra store calls.
    /// </summary>
    private void IngestMintedJobs()
    {
        if (options.Schedules.Count == 0)
        {
            return;
        }
        const int pageSize = 200; // StoreBounds.Default.MaxMonitorPageSize
        while (true)
        {
            var page = Get(_store.ListJobsAsync(new JobQuery { AfterSequence = _mintScanCursor, MaxResults = pageSize }));
            foreach (var job in page)
            {
                if (job.ScheduleId is not null)
                {
                    _minted.TryAdd(job.JobId, job.DueTime); // DueTime is still the cron tick at mint
                }
            }
            if (page.Count > 0)
            {
                _mintScanCursor = page[^1].Sequence;
            }
            if (page.Count < pageSize)
            {
                break;
            }
        }
    }

    private void Process(SimEvent simEvent)
    {
        switch (simEvent.Kind)
        {
            case EventKind.Enqueue:
                var jobId = _rng.NextGuid();
                // Topology Generator (issue 0071): when on, spread the workload across the generated Queues from
                // the topology stream. Off → _topoQueues is ["default"], the draw is skipped, and every job lands
                // on "default" exactly as before (zero topology draws, byte-identical battery).
                var queue = _topoQueues.Count == 1 ? "default" : _topoQueues[_topoRng.Next(_topoQueues.Count)];
                NewJob newJob;
                (Guid ParentId, DependencyMode Mode)? parentLink = null;
                // A fifth of the workload are Dependencies of random earlier jobs, so the
                // no-orphan invariant is contested under every crash interleaving.
                if (_jobIds.Count > 0 && _rng.NextDouble() < 0.2)
                {
                    var parentId = _jobIds[_rng.Next(_jobIds.Count)];
                    var mode = _rng.NextDouble() < 0.5 ? DependencyMode.OnSuccess : DependencyMode.OnAnyTerminal;
                    parentLink = (parentId, mode);
                    newJob = new NewJob(jobId, "sim-job", ReadOnlyMemory<byte>.Empty, queue, _now)
                    {
                        Parents = [parentId],
                        Mode = mode,
                    };
                }
                else
                {
                    newJob = new NewJob(
                        jobId, "sim-job", ReadOnlyMemory<byte>.Empty, queue,
                        _now + _rng.NextTimeSpan(TimeSpan.FromMinutes(10)));
                }
                try
                {
                    Get(Faulty.EnqueueAsync(newJob, _now));
                }
                catch (SimTransientFault)
                {
                    // The enqueue hit a transient store fault: a client retries — no job is lost.
                    Schedule(_now + options.PollInterval, simEvent);
                    break;
                }
                if (parentLink is { } link)
                {
                    _parentsByChild[jobId] = link;
                }
                _jobIds.Add(jobId);
                // Quarantine-via-routing (issue 0073): draw once per committed enqueue whether this job is
                // dispatch-side unroutable. Guarded on the probability so OFF makes no draw, and placed after
                // the successful enqueue so a transient-fault retry (which breaks out above) never draws twice.
                // The unroutable draw stays on its own stream (byte-identical), recorded through the
                // FaultPlan keyed by jobId so it is in the Fault Map and removable by the minimizer
                // (a removed unroutable job replays as routable: strictly calmer). Issue 0084.
                if (options.UnroutableProbability > 0
                    && _faultPlan.Decide("unroutable", jobId.ToString(), _routeRng.NextDouble() < options.UnroutableProbability))
                {
                    _unroutable.Add(jobId);
                }
                PublishHints();
                break;

            case EventKind.Hint:
                // One-shot, never rescheduled, no fault draws: a hint is only an earlier
                // poll. A crashed node simply misses it — hints have no delivery guarantees.
                if (!_nodes[simEvent.Node].Crashed)
                {
                    TryDrive(simEvent.Node, new NodeEvent.PollDue(NodeNow(simEvent.Node)));
                }
                break;

            case EventKind.Poll:
                var pollNode = _nodes[simEvent.Node];
                if (pollNode.Crashed)
                {
                    break;
                }
                if (_now < WorkloadEnd
                    && options.CrashProbabilityPerPoll > 0 // skip the consultation, not just the crash:
                    // The crash decision rides its own keyed stream (issue 0084), off the main RNG, so a
                    // crash is stable-keyed by (node, scheduledTime) and the minimizer can remove it. Off
                    // makes no consultation, so a crash-free run is byte-identical on the crash axis.
                    && _faultPlan.Fault("crash", $"{simEvent.Node}:{_now:O}", options.CrashProbabilityPerPoll))
                {
                    Crash(simEvent.Node);
                    break;
                }
                _pollCount++;
                TryDrive(simEvent.Node, new NodeEvent.PollDue(NodeNow(simEvent.Node)));
                // Observer delivery rides the same poll cadence (§0076): claim each Observer's next
                // batch, invoke the recording sink, advance the cursor. Same per-node faulty store, so
                // Node Isolation and store faults reach the delivery cursor too (§0078).
                TryDriveObservers(simEvent.Node, new ObserverEvent.PollDue(NodeNow(simEvent.Node)));
                // TryDrive above already ran this poll's claim, which folded its outcome into the node's
                // PollDelay when adaptive. Reschedule at that delay; otherwise the fixed interval as before.
                Schedule(_now + (AdaptivePoll ? _nodes[simEvent.Node].PollDelay : options.PollInterval), simEvent);
                break;

            case EventKind.Heartbeat:
                var heartbeatNode = _nodes[simEvent.Node];
                if (heartbeatNode.Crashed)
                {
                    break;
                }
                // Heartbeat loss: the GC-pause regime — the node keeps executing but its Lease silently
                // lapses, so someone else may legally run the job too. The loss decision rides its own
                // keyed stream (issue 0084), off the main RNG, stable-keyed by (node, scheduledTime). The
                // probability/window guard short-circuits the consultation entirely, so a no-loss run is
                // byte-identical on the heartbeat axis.
                var heartbeatLost = _now < WorkloadEnd
                    && options.HeartbeatLossProbability > 0
                    && _faultPlan.Fault("heartbeat", $"{simEvent.Node}:{_now:O}", options.HeartbeatLossProbability);
                if (!heartbeatLost)
                {
                    TryDrive(simEvent.Node, new NodeEvent.HeartbeatDue(NodeNow(simEvent.Node)));
                }
                Schedule(_now + options.HeartbeatInterval, simEvent);
                break;

            case EventKind.ExecutionComplete:
                var execNode = _nodes[simEvent.Node];
                if (execNode.Crashed
                    || simEvent.Epoch != execNode.Epoch
                    || !execNode.Executing.ContainsKey(simEvent.Job!.JobId))
                {
                    break;
                }
                // Node Isolation (issue 0068): an isolated node finished computing but cannot reach the
                // store to report. Unlike a crash (which forgets in-flight work), it RETAINS the execution
                // and keeps believing it holds the Lease, retrying on heal. Re-arm the completion a tick
                // later; it lands once isolation heals — by then the Lease has lapsed and a survivor has
                // re-leased, so the stale ReportOutcome hits the (workerId, attempt) fence (ADR 0013).
                if (_isolation.IsIsolated(simEvent.Node))
                {
                    Schedule(_now + options.PollInterval, simEvent);
                    break;
                }
                // Ack-loss isolation (issue 0070): once per (job, attempt), the outcome write commits but
                // the ack is lost. Arm the per-node store to apply-then-throw, then re-arm this completion:
                // the node believes the report failed and re-reports on its next tick. The committed write
                // has already moved the job on (terminal, or retried then re-leased by a survivor), so the
                // retry is stale and the (workerId, attempt) fence rejects it — Effect-Once on the path
                // where the write actually landed. The job is kept in Executing so the retry can re-report.
                var ackKey = (simEvent.Job!.JobId, simEvent.Job.Attempt);
                // The ack-loss draw stays on its own stream (byte-identical), recorded through the FaultPlan
                // keyed by (jobId, attempt) so it is in the Fault Map and removable by the minimizer (issue
                // 0084). The _ackLost fence keeps the consultation once per Attempt, so the ordinal is always
                // 0 and the key is stable.
                if (options.AckLossProbability > 0
                    && _ackLost.Add(ackKey)
                    && _faultPlan.Decide(
                        "ackloss", $"{simEvent.Job.JobId}:{simEvent.Job.Attempt}",
                        _ackLossRng.NextDouble() < options.AckLossProbability))
                {
                    _ackLosses++;
                    _nodeFaulty[simEvent.Node].AckLossArmed = true;
                    // The node is out (the "isolation" of a lost ack) until it retries on heal — long enough
                    // that the store moves on: a committed Failure becomes due past its Backoff and a
                    // survivor re-leases it on a fresh Attempt, so the retry lands stale and is fenced.
                    Schedule(_now + options.LeaseDuration + options.Backoff, simEvent);
                    TryDrive(simEvent.Node, simEvent.Fails // commits, then throws — the lost ack
                        ? new NodeEvent.ExecutionFailed(simEvent.Job, "simulated failure", NodeNow(simEvent.Node))
                        : new NodeEvent.ExecutionSucceeded(simEvent.Job, NodeNow(simEvent.Node)));
                    break;
                }
                execNode.Executing.Remove(simEvent.Job.JobId);
                TryDrive(simEvent.Node, simEvent.Fails
                    ? new NodeEvent.ExecutionFailed(simEvent.Job, "simulated failure", NodeNow(simEvent.Node))
                    : new NodeEvent.ExecutionSucceeded(simEvent.Job, NodeNow(simEvent.Node)));
                break;

            case EventKind.Restart:
                var restarting = _nodes[simEvent.Node];
                restarting.Crashed = false;
                restarting.Driver = NewDriver(simEvent.Node);
                restarting.PollDelay = options.PollInterval; // a fresh pump starts at the floor
                restarting.IdleSince = null;

                Schedule(_now + _rng.NextTimeSpan(options.PollInterval), new SimEvent(EventKind.Poll, simEvent.Node, 0, null, false));
                Schedule(_now + _rng.NextTimeSpan(options.HeartbeatInterval), new SimEvent(EventKind.Heartbeat, simEvent.Node, 0, null, false));
                break;

            case EventKind.OperatorAction:
                // Operator Actions race the cluster chaos but stop at WorkloadEnd so the drain can converge —
                // the same window the fault streams respect. The firing is recorded through the FaultPlan
                // keyed by its scheduled time (always true in generate, so byte-identical), so operator
                // actions appear in the Fault Map. They are recorded but NOT minimizer-removable: the action
                // type/target are drawn from the shared _opRng at apply time, so skipping one would shift the
                // stream for every later action (ADR 0018's ordinal-drift hazard). The minimizer excludes the
                // "operator" axis from removal; operator actions still replay faithfully from the Scenario seed.
                if (_now < WorkloadEnd && _faultPlan.Decide("operator", $"{_now:O}", true))
                {
                    ApplyOperatorAction();
                }
                break;

            case EventKind.IsolationStart:
                // Begin isolating the node if the N−1 budget allows (a refused start is a deterministic
                // no-op). A healing episode arms its heal at start + duration; a permanent loss (issue
                // 0069) never heals — no heal is scheduled, so its leased work can only leave the lost
                // node by migrating to a survivor via Lease expiry. Either way the node keeps stepping
                // while cut off, but every hot-path store call throws.
                var episode = simEvent.Episode!;
                // The episode's begin is gated through the FaultPlan keyed by (node, startAt) — recorded as
                // on (always true in generate, so byte-identical) and removable by the minimizer (a removed
                // episode never begins: the node is never cut off, strictly calmer). The episode parameters
                // were drawn upfront in Plan(), so removing a begin shifts no stream. Issue 0084.
                if (_faultPlan.Decide("isolation", $"{episode.Node}:{episode.StartAt:O}", true)
                    && _isolation.TryBegin(episode.Node))
                {
                    _isolations++;
                    if (episode.Permanent)
                    {
                        _permanentLosses++;
                    }
                    else
                    {
                        Schedule(_now + episode.Duration, new SimEvent(EventKind.IsolationHeal, episode.Node, 0, null, false));
                    }
                }
                break;

            case EventKind.IsolationHeal:
                // The node reconnects to the store. Its Poll cycle never stopped (it only failed), so the
                // next tick re-claims and its deferred ExecutionComplete lands and hits the fence — no new
                // poll is scheduled here, which would fork a second concurrent poll loop for this node.
                _isolation.Heal(simEvent.Node);
                break;
        }
    }

    /// <summary>
    /// Applies one Operator Action (§5.8) chosen from the operator rng stream, racing it against
    /// the live cluster: Cancel an idle or Leased job, Requeue a dead job, Pause or Resume the Queue,
    /// or Trigger a seeded schedule. Each action is a store transition; a draw with no eligible target
    /// is a deterministic no-op. A Leased-job cancel sets CancelRequested and the cooperative heartbeat
    /// round-trip (SignalCancellation → ExecutionCancelled) then races the in-flight execution.
    /// </summary>
    private void ApplyOperatorAction()
    {
        var roll = _opRng.NextDouble();
        if (roll < 0.40)
        {
            OperatorCancel();
        }
        else if (roll < 0.65)
        {
            OperatorRequeue();
        }
        else if (roll < 0.80)
        {
            OperatorPause();
        }
        else if (roll < 0.95)
        {
            OperatorResume();
        }
        else
        {
            OperatorTrigger();
        }
    }

    /// <summary>
    /// Cancels a random cancellable job, racing the request against the live cluster. An idle job
    /// (Scheduled/AwaitingParent) cancels immediately (a pure terminal transition); a Leased job
    /// only has CancelRequested set — the cooperative round-trip (Heartbeat → SignalCancellation →
    /// ExecutionCancelled → ReportOutcome) then races the in-flight execution, a lease lapse, and a
    /// crash. Either way the target's id is recorded for the cancellation-provenance oracle.
    /// </summary>
    private void OperatorCancel()
    {
        var candidates = TrackedJobs()
            .Where(id => Get(_store.GetJobAsync(id))!.State
                is JobState.Scheduled or JobState.AwaitingParent or JobState.Leased)
            .ToList();
        if (candidates.Count == 0)
        {
            return;
        }
        var jobId = candidates[_opRng.Next(candidates.Count)];
        var result = Get(_store.CancelJobAsync(jobId, "sim-operator", _now));
        switch (result)
        {
            case CancelResult.CancelledImmediately:
                _operatorCancels++;
                RecordCancelTarget(jobId);
                RecordIssuedAudit(OperatorAction.Cancel, jobId.ToString());
                break;
            case CancelResult.CancellationRequested:
                _operatorCancelRequests++;
                RecordCancelTarget(jobId);
                RecordIssuedAudit(OperatorAction.Cancel, jobId.ToString());
                break;
        }
    }

    /// <summary>
    /// Records that the sim issued an audited Operator Action against <paramref name="target"/>, so the
    /// end-of-run audit-completeness check can reconcile against the store's log. Sabotage tallies one
    /// phantom extra (the store logged the action only once) so the tally exceeds the log by one — a real
    /// mismatch, on a target the check is guaranteed to visit, the live oracle must catch.
    /// </summary>
    private void RecordIssuedAudit(OperatorAction action, string target)
    {
        var key = (action, target);
        _issuedAudits[key] = _issuedAudits.GetValueOrDefault(key) + 1;
        if (options.SabotageAuditCompleteness && !_auditSabotaged)
        {
            _auditSabotaged = true;
            _issuedAudits[key]++; // a phantom extra the store never logged: tally one past the audit row
        }
    }

    /// <summary>
    /// Records that the operator legitimately issued a cancel against this job, so the provenance
    /// oracle accepts its eventual Cancelled state. Sabotage withholds the record (the cancel still
    /// hits the store) so a real cancellation surfaces as unprovenanced — proving the oracle is live.
    /// </summary>
    private void RecordCancelTarget(Guid jobId)
    {
        if (!options.SabotageCancelProvenance)
        {
            _cancelTargets.Add(jobId);
        }
    }

    /// <summary>
    /// Requeues a random dead job (DeadLettered/Quarantined → Scheduled, Attempt reset). This is the
    /// one legal terminal→non-terminal move (§3), so the oracle is told to forget the job's terminal
    /// state — every OTHER terminal change still trips the terminal-stability invariant.
    /// </summary>
    private void OperatorRequeue()
    {
        var candidates = TrackedJobs()
            .Where(id => Get(_store.GetJobAsync(id))!.State is JobState.DeadLettered or JobState.Quarantined)
            .ToList();
        if (candidates.Count == 0)
        {
            return;
        }
        var jobId = candidates[_opRng.Next(candidates.Count)];
        if (Get(_store.RequeueAsync(jobId, "sim-operator", _now)) == RequeueResult.Requeued)
        {
            _terminalSeen.Remove(jobId); // the operator legitimately revived it
            // Requeue resets the Attempt budget to 0 (InMemoryJobStore §3), so the next Claim re-mints
            // Attempt 1 — a genuinely NEW occupancy of the Concurrency slot, not a second release of the
            // old one. Forget the job's prior per-Attempt slot-release tally so the new lifetime's single
            // release isn't conflated with the pre-requeue one: the (jobId, Attempt) key is unique only
            // WITHIN one Attempt budget, not across a requeue that recycles Attempt numbers (issue 0130).
            foreach (var key in _slotReleases.Keys.Where(k => k.JobId == jobId).ToList())
            {
                _slotReleases.Remove(key);
            }
            _operatorRequeues++;
            RecordIssuedAudit(OperatorAction.Requeue, jobId.ToString());
        }
    }

    /// <summary>Pauses the sim's Queue cluster-wide; the oracle then forbids any fresh Lease there.</summary>
    private void OperatorPause()
    {
        const string queue = "default";
        if (!options.SabotagePausedClaim)
        {
            Get2(_store.PauseQueueAsync(queue, "sim-operator", _now));
            RecordIssuedAudit(OperatorAction.PauseQueue, queue);
        }
        // Sabotage leaves the store unpaused but still tells the oracle it is paused, so the
        // claims that keep flowing MUST trip the Pause invariant — proving the oracle is alive.
        if (_oraclePausedQueues.Add(queue))
        {
            _queuePauses++;
        }
    }

    /// <summary>Resumes the sim's Queue, restoring claiming.</summary>
    private void OperatorResume()
    {
        const string queue = "default";
        Get2(_store.ResumeQueueAsync(queue, "sim-operator", _now));
        _oraclePausedQueues.Remove(queue);
        RecordIssuedAudit(OperatorAction.ResumeQueue, queue);
    }

    /// <summary>Triggers one extra instance of a random seeded schedule; a no-op when none are seeded.</summary>
    private void OperatorTrigger()
    {
        if (options.Schedules.Count == 0)
        {
            return;
        }
        var schedule = options.Schedules[_opRng.Next(options.Schedules.Count)];
        if (Get(_store.TriggerScheduleNowAsync(schedule.Id, "sim-operator", _now)) == TriggerScheduleResult.Triggered)
        {
            _scheduleTriggers++;
            RecordIssuedAudit(OperatorAction.TriggerScheduleNow, schedule.Id);
        }
    }

    // Re-polls the Driver requested mid-cascade (Command.RequestPoll), deferred to the END of the top-level
    // Drive rather than run inline. Both production pumps enqueue an obeyed re-poll onto their FIFO event
    // queue, so it runs only AFTER the poll that requested it has finished — its ClaimBatch already claimed
    // and its ClaimCompleted already landed each job in Executing. Driving the re-poll inline instead (as
    // the sim once did) let a flush's re-poll size its claim against a pool that did not yet reflect the
    // outer poll's in-flight claim, double-booking a freed buffer slot and briefly admitting one execution
    // past PoolSize (vopr-0140). Deferring it here makes the sim's dispatch order faithful to the pumps.
    private readonly Queue<(int Node, DateTimeOffset At)> _pendingRePolls = new();
    private bool _inDriveCascade;

    private void Drive(int nodeIndex, NodeEvent nodeEvent)
    {
        // Nested drive (a command's inline feedback within an active cascade): run it directly so ClaimBatch →
        // ClaimCompleted and the other feedback events still resolve inline, exactly as before.
        if (_inDriveCascade)
        {
            DriveInner(nodeIndex, nodeEvent);
            return;
        }
        _inDriveCascade = true;
        try
        {
            DriveInner(nodeIndex, nodeEvent);
        }
        finally
        {
            // FIFO re-poll drain (faithful to the pumps' event queue): each requested re-poll runs only after
            // the cascade that requested it has fully unwound. Runs even if that cascade hit a transient store
            // fault — the pump's channel keeps an already-enqueued re-poll — and a re-poll's own transient is
            // absorbed like any Drive so it neither aborts a sibling re-poll nor masks the cascade's fault.
            while (_pendingRePolls.TryDequeue(out var rp))
            {
                try
                {
                    DriveInner(rp.Node, new NodeEvent.PollDue(rp.At));
                }
                catch (SimTransientFault)
                {
                    // A re-poll hit a transient store fault: committed effects stand, it retries next tick —
                    // exactly the production pump's transient handling for an obeyed re-poll.
                }
            }
            _inDriveCascade = false;
        }
    }

    private void DriveInner(int nodeIndex, NodeEvent nodeEvent)
    {
        var node = _nodes[nodeIndex];
        // An Unroutable ExecuteJob's terminal report is DEFERRED to after the whole command batch
        // (issue 0136), mirroring the production Shell: the pump enqueues the ExecutionUnroutable as a
        // feedback event and only runs its ReportOutcome once every ExecuteJob in the batch has been
        // handled (routable ones already started in flight). Driving it inline instead let a store fault
        // on the report unwind through TryDrive and abandon the sibling ExecuteJobs still queued behind
        // it in the same ClaimCompleted batch — leaving those jobs Leased-but-never-executed, their
        // leases renewed by the Driver's heartbeat forever, so they never converged in the drain window.
        List<JobRecord>? deferredUnroutable = null;
        // The command batch runs inside a try so the deferred Unroutable reports below run even when a later
        // command in the SAME batch faults (a follow-up claim hitting a transient store fault, say). The
        // foreach/switch body keeps its original indentation to stay a surgical diff.
        try
        {
        foreach (var command in node.Driver.Step(nodeEvent))
        {
            switch (command)
            {
                case Command.ExpireLeases expire:
                    // Migration-sweep sabotage (issue 0069): survivors stop sweeping, so a permanently-lost
                    // node's lapsed Lease never expires in the store and its job can never migrate — the
                    // Migration-Liveness Oracle must then flag the stranded job. Real regimes always sweep.
                    if (!options.SabotageMigrationSweep)
                    {
                        _leasesExpired += Get(_nodeFaulty[nodeIndex].ExpireLeasesAsync(NodeNow(nodeIndex), expire.MaxJobs, expire.Queues, expire.Disposition));
                    }
                    break;

                case Command.LoadSchedules:
                    var schedules = Get(_store.ListSchedulesAsync());
                    if (schedules.Count > 0)
                    {
                        Drive(nodeIndex, new NodeEvent.SchedulesLoaded(schedules, NodeNow(nodeIndex)));
                    }
                    break;

                case Command.MintDue mint:
                    // Mirror the production/test pump: the store applies the mint, then the
                    // Driver is told how many instances committed so it can decide whether a
                    // mint that produced work warrants an immediate re-poll (issue 0042).
                    var minted = Get(_nodeFaulty[nodeIndex].MintDueAsync(mint.Decisions));
                    Drive(nodeIndex, new NodeEvent.MintCompleted(minted, NodeNow(nodeIndex)));
                    break;

                case Command.RequestPoll repoll:
                    // The Driver asked to poll again at this instant (an applied outcome may have released a
                    // Dependency due now, or a mint produced work). The Shell only obeys — no scheduling
                    // judgment of its own (ADR-0008). Both pumps obey by ENQUEUEING the re-poll on their FIFO
                    // event queue, so it runs after the current cascade drains; the sim mirrors that by
                    // deferring it to the top-level Drive (see _pendingRePolls) rather than driving it inline,
                    // so production and the Simulator drain everything due at this instant in the same order.
                    _pendingRePolls.Enqueue((nodeIndex, repoll.Now));
                    break;

                case Command.ClaimBatch claim:
                    var claimNow = NodeNow(nodeIndex);
                    var request = new ClaimRequest(claim.WorkerId, claim.Queues, claim.MaxJobs, claim.LeaseDuration, claimNow);
                    IReadOnlyList<JobRecord> jobs;
                    try
                    {
                        if (AdaptivePoll)
                        {
                            // Take the store's next-due hint and fold it into this node's backoff, exactly as the
                            // host pump does. Off by default (ClaimAsync path below), so a run stays byte-identical.
                            var result = Get(_nodeFaulty[nodeIndex].ClaimBatchAsync(request));
                            jobs = result.Jobs;
                            UpdatePollBackoff(nodeIndex, jobs.Count > 0, result.NextDue, claimNow);
                        }
                        else
                        {
                            jobs = Get(_nodeFaulty[nodeIndex].ClaimAsync(request));
                        }
                    }
                    catch (SimTransientFault)
                    {
                        // The claim faulted: land an empty completion so the Driver frees the slots it reserved
                        // for this batch, then let the fault propagate exactly as the production pump does — else
                        // the reservation would strand and wedge the pool once faults accumulate past PoolSize.
                        Drive(nodeIndex, new NodeEvent.ClaimCompleted([], NodeNow(nodeIndex)));
                        throw;
                    }
                    // Always report the claim's completion, an empty result included: the Driver reserved this
                    // batch's slots at issue and frees them here, so an empty claim must still land or the
                    // reservation would strand and wedge the pool.
                    Drive(nodeIndex, new NodeEvent.ClaimCompleted(jobs, NodeNow(nodeIndex)));
                    break;

                case Command.ExecuteJob execute:
                    // Quarantine-via-routing (issue 0073): mirror the production/test pump's ExecuteJob
                    // handling of a RouteResult.Unroutable. A marked job is NEVER added to Executing, NEVER
                    // added to _everExecuted, and schedules no ExecutionComplete — it drives ExecutionUnroutable
                    // immediately, which re-enters Drive: the Driver removes it and emits ReportOutcome(Unroutable)
                    // through the SAME (workerId, attempt) fence as every other outcome (so the Outcome-Provenance
                    // Oracle covers the stale-under-isolation case), and the store quarantines it. The job is thus
                    // terminal as Quarantined yet legitimately absent from _everExecuted.
                    if (_unroutable.Contains(execute.Job.JobId))
                    {
                        if (options.SabotageInlineUnroutableReport)
                        {
                            // Self-test (issue 0136, preserved under ADR 0035): Core-side coalescing now
                            // BUFFERS every outcome, which structurally subsumes the 0136 deferral — a
                            // buffered unroutable report cannot fault inline while siblings still execute
                            // (they keep _executing non-zero, so nothing flushes). The sabotage therefore
                            // reintroduces the pre-deferral INLINE report directly (bypassing the buffer): a
                            // store fault on it unwinds through TryDrive and strands the sibling ExecuteJobs
                            // queued behind it in this same batch, re-tripping DrainLiveness.
                            ReportOneOutcome(nodeIndex, execute.Job.JobId, $"node-{nodeIndex}",
                                execute.Job.Attempt, new JobOutcome.Unroutable("unroutable: no handler for sim-job"));
                            break;
                        }
                        // Defer the report (see deferredUnroutable above) instead of driving it inline.
                        (deferredUnroutable ??= []).Add(execute.Job);
                        break;
                    }
                    node.Executing[execute.Job.JobId] = execute.Job;
                    if (!options.SabotageExecuteLiveness)
                    {
                        _everExecuted.Add(execute.Job.JobId); // liveness: this job ran at least once
                    }
                    // Stream splitting: duration and outcome belong to the Attempt, keyed by
                    // (jobId, attempt) — not drawn from the run's interleaving. Whichever node
                    // claims it, however soon a hint made it poll, the Attempt behaves the same.
                    var attemptRng = PerAttempt(execute.Job.JobId, execute.Job.Attempt);
                    var duration = attemptRng.NextTimeSpan(options.MaxExecutionDuration);
                    // The handler outcome stays on the PerAttempt stream (the draw is unchanged, so the
                    // battery is byte-identical) but is recorded through the FaultPlan keyed by
                    // (jobId, attempt) — so it is in the Fault Map and the minimizer can remove it
                    // (a removed handler failure replays as a success: strictly calmer). Issue 0084.
                    var fails = _faultPlan.Decide(
                        "handler", $"{execute.Job.JobId}:{execute.Job.Attempt}",
                        attemptRng.NextDouble() < options.HandlerFailureProbability);
                    Schedule(
                        _now + duration,
                        new SimEvent(EventKind.ExecutionComplete, nodeIndex, node.Epoch, execute.Job, Fails: fails));
                    break;

                case Command.ReportOutcomeBatch batch:
                    // Core-side coalescing (ADR 0035): the Driver buffered terminal outcomes and flushes
                    // them as ONE command. The harness vectorizes the singular report — per row a pre-state
                    // read, the per-(workerId, attempt) fence verdict, the Outcome-Provenance assertion, the
                    // slot-release detection, then Drive(OutcomeReported). Each row consults the per-node
                    // faulty store on the same "ReportOutcome" axis, so the store-fault stream is keyed
                    // exactly as it was per single report (the buffer adds no draws); a row that faults
                    // aborts the rest of the batch, and those leases lapse and reclaim (At-Least-Once, the
                    // buffer-loss window modeled for free). The batch is applied synchronously — no new
                    // SimEvent, so the determinism boundary is unchanged.
                    //
                    // SabotageBatchFence self-test (ADR 0035): model a native batch impl that fences single
                    // reports correctly but applies a MULTI-row batch as a whole — without re-checking the
                    // (workerId, attempt) fence per row. Drop the fence for every row of a >1 batch so a
                    // stale row riding alongside live ones lands; the Outcome-Provenance oracle must catch it,
                    // proving the vectorized fence is enforced per row. Single-row batches stay fenced, so
                    // this is strictly the batched-path twin of the single-report SabotageOutcomeFence.
                    var dropBatchFence = options.SabotageBatchFence && batch.Outcomes.Count > 1;
                    foreach (var outcome in batch.Outcomes)
                    {
                        ReportOneOutcome(nodeIndex, outcome.JobId, outcome.WorkerId, outcome.Attempt,
                            outcome.Outcome, dropFence: dropBatchFence);
                    }
                    break;

                case Command.Heartbeat heartbeat:
                    var results = Get(_nodeFaulty[nodeIndex].HeartbeatAsync(
                        heartbeat.WorkerId, heartbeat.JobIds, heartbeat.LeaseDuration, NodeNow(nodeIndex)));
                    Drive(nodeIndex, new NodeEvent.HeartbeatCompleted(results, NodeNow(nodeIndex)));
                    break;

                case Command.AbandonExecution abandon:
                    node.Executing.Remove(abandon.JobId);
                    break;

                case Command.SignalCancellation signal:
                    // Cooperative cancellation round-trip (§5.8): an operator requested cancel on a
                    // Leased job, the next Heartbeat carried CancelRequested back, and the Driver now
                    // signals the handler. Model the handler unwinding cooperatively — raise
                    // ExecutionCancelled for the exact in-flight Attempt, exactly as both pumps do.
                    // A job no longer executing here lost the race (it already completed, or its Lease
                    // lapsed and it was abandoned/reclaimed): the signal is a no-op, just as cancelling
                    // a finished handler's token is — the terminal outcome already fenced it.
                    if (node.Executing.Remove(signal.JobId, out var cancelledJob))
                    {
                        Drive(nodeIndex, new NodeEvent.ExecutionCancelled(
                            cancelledJob, "operator-cancel", NodeNow(nodeIndex)));
                    }
                    break;

                // Fail loud on any Command the Simulator does not model rather than silently
                // dropping it (the harness's own safety net): a Driver that grows a new Command
                // — or one the sim quietly ignored, as RequestPoll once was — surfaces here with
                // its seed instead of degrading fidelity invisibly. PurgeTerminal lands here by
                // design: no regime seeds Retention, so the Driver never emits it.
                default:
                    throw new InvalidOperationException(
                        $"[seed {options.Seed}] Simulator received an unmodeled command: {command.GetType().Name}");
            }
        }
        }
        finally
        {
            // The whole batch's ExecuteJobs are now handled (routable ones already in flight); only now do
            // the deferred Unroutable reports run — and they run from a finally so they fire EVEN IF a later
            // command in the same batch faulted. The production Shell enqueues each ExecutionUnroutable as its
            // OWN durable feedback event, so a transient store fault on a sibling command (a follow-up claim,
            // say) never discards an already-enqueued report. A bare in-batch loop instead lost the reports
            // when the batch unwound, stranding those Unroutable jobs in the Driver's executing set —
            // heartbeated forever, never reclaimed, never converging in the drain window (this is the gap the
            // issue-0136 deferral still left: it deferred the report but did not protect it from a mid-batch
            // fault). Each runs through TryDrive so one report's own fault neither aborts the siblings nor
            // masks an in-flight batch fault on the way out.
            if (deferredUnroutable is not null && !options.SabotageDeferredUnroutableReport)
            {
                foreach (var job in deferredUnroutable)
                {
                    TryDrive(nodeIndex, new NodeEvent.ExecutionUnroutable(
                        job, "unroutable: no handler for sim-job", NodeNow(nodeIndex)));
                }
            }
        }

        // Self-test (SabotageDeferredUnroutableReport): the pre-fix placement — the deferred reports run only
        // on the NORMAL completion path (here, after the try), so a fault mid-batch unwinds before they run
        // and strands the Unroutable jobs in the Driver's executing set, tripping the DrainLiveness oracle.
        // Never set in real regimes.
        if (deferredUnroutable is not null && options.SabotageDeferredUnroutableReport)
        {
            foreach (var job in deferredUnroutable)
            {
                Drive(nodeIndex, new NodeEvent.ExecutionUnroutable(
                    job, "unroutable: no handler for sim-job", NodeNow(nodeIndex)));
            }
        }
    }

    /// <summary>
    /// Applies one outcome row through the per-node faulty store and runs the per-row oracles, then drives
    /// the resulting OutcomeReported back into the Driver. Shared by the singular ReportOutcome path and by
    /// each row of a coalesced ReportOutcomeBatch (ADR 0035), so the vectorized fence is enforced per row
    /// and keyed on the same "ReportOutcome" store-fault axis as a single report. <paramref name="dropFence"/>
    /// is the SabotageBatchFence self-test hook: when set, the row's write goes through a fence-dropping
    /// store so a stale row in a batch lands and the Outcome-Provenance oracle catches it.
    /// </summary>
    private void ReportOneOutcome(
        int nodeIndex, Guid jobId, string workerId, int attempt, JobOutcome outcome, bool dropFence = false)
    {
        // Outcome-Provenance Oracle (issue 0068, ADR 0013): compute the fence verdict from the
        // authoritative pre-state (the live Lease holder for this exact Attempt, not expired),
        // then assert the store applied the outcome EXACTLY when the reporter held that Lease.
        // Reads hit _store directly so the oracle sees committed truth even under the sabotage.
        var reportNow = NodeNow(nodeIndex);
        var pre = Get(_store.GetJobAsync(jobId));
        var fenceShouldApply = pre is { State: JobState.Leased }
            && pre.LeaseOwner == workerId
            && pre.Attempt == attempt
            && pre.LeaseExpiry > reportNow;
        // SabotageBatchFence forces the row through a fence-dropping store; every real regime reports
        // through the per-node faulty store on the same "ReportOutcome" fault axis.
        var result = dropFence
            ? Get(BatchFenceDroppingStore.ReportOutcomeAsync(jobId, workerId, attempt, outcome, reportNow))
            : Get(_nodeFaulty[nodeIndex].ReportOutcomeAsync(jobId, workerId, attempt, outcome, reportNow));
        // Effect-Once: an applied outcome is caused only by the node holding the live Lease for
        // that Attempt; a healed node's stale report mutates nothing. Two nodes merely EXECUTING
        // the same job concurrently never reaches here as a violation — only a store WRITE does
        // (at-least-once execution working as designed). The verdict is captured and asserted in
        // CheckInvariants so the oracle fires there.
        if ((result == OutcomeResult.Applied) != fenceShouldApply)
        {
            _provenanceViolation ??=
                $"Outcome-Provenance: job {jobId} outcome by {workerId} attempt {attempt} "
                + $"applied={result == OutcomeResult.Applied} but the live-Lease fence said {fenceShouldApply} "
                + $"(store holder {pre?.LeaseOwner}, attempt {pre?.Attempt})";
        }
        if (result == OutcomeResult.StaleLease)
        {
            _staleOutcomes++;
        }
        // Slot-double-release detector (issue 0074): an APPLIED outcome on a Leased job in a
        // LIMITED Queue frees that Attempt's Concurrency slot. Within one Attempt budget each
        // (jobId, attempt) is Leased at most once (Claim increments Attempt), so it may release at
        // most once; a second release for one Attempt is the Effect-Once symptom a dropped fence
        // would cause. Recorded by the pre-state's Queue/Attempt — the authoritative slot the store
        // charged. An operator Requeue resets the Attempt budget to 0 (§3), recycling Attempt
        // numbers across a NEW lease lifetime; OperatorRequeue forgets the prior tally so the two
        // legitimate releases of recycled Attempt 1 are not conflated into a false double (0130).
        if (result == OutcomeResult.Applied && pre is { State: JobState.Leased } && _limits.ContainsKey(pre.Queue))
        {
            RecordSlotRelease(jobId, pre.Attempt);
        }
        // A Cancelled outcome that applied is an honored cooperative cancel: the only
        // source of JobOutcome.Cancelled in the sim is the SignalCancellation round-trip
        // (immediate idle cancels go straight through CancelJobAsync, never ReportOutcome).
        if (result == OutcomeResult.Applied && outcome is JobOutcome.Cancelled)
        {
            _cooperativeCancels++;
        }
        Drive(nodeIndex, new NodeEvent.OutcomeReported(jobId, result, NodeNow(nodeIndex)));
    }

    /// <summary>Drives one node event, absorbing a transient store fault as a retry-next-tick no-op.</summary>
    private void TryDrive(int nodeIndex, NodeEvent nodeEvent)
    {
        try
        {
            Drive(nodeIndex, nodeEvent);
        }
        catch (SimTransientFault)
        {
            // The node's action hit a transient store fault: committed effects stand and it
            // retries on its next poll — exactly the production pump's transient handling.
        }
    }

    /// <summary>Drives the node's Observer Dispatch Driver, absorbing a transient fault as a retry-next-poll no-op.</summary>
    private void TryDriveObservers(int nodeIndex, ObserverEvent observerEvent)
    {
        if (_observerDrivers[nodeIndex] is null)
        {
            return; // no Observer registered — no dispatcher, no claim, no draw
        }
        try
        {
            DriveObservers(nodeIndex, observerEvent);
        }
        catch (SimTransientFault)
        {
            // Isolation / store fault on a claim or report: the cursor stands un-advanced (or the
            // report was lost), and another node — or this one, on heal — redelivers (§0078).
        }
    }

    /// <summary>
    /// The Observer Shell edge (ADR 0017): steps the sans-IO Dispatch Driver and runs its Commands
    /// against this node's faulty store, recursing on the feedback events exactly as <see cref="Drive"/>
    /// does for the Node Driver. The egress effect is a recording sink — the deterministic analogue of
    /// the host callback — so the whole delivery path stays simulated under Virtual Time.
    /// </summary>
    private void DriveObservers(int nodeIndex, ObserverEvent observerEvent)
    {
        var driver = _observerDrivers[nodeIndex]!;
        foreach (var command in driver.Step(observerEvent))
        {
            switch (command)
            {
                case ObserverCommand.ClaimBatch claim:
                    var subscription = claim.Subscription;
                    ObserverClaim batch;
                    try
                    {
                        batch = Get(_nodeFaulty[nodeIndex].ClaimObserverDeliveriesAsync(new ObserverClaimRequest(
                            claim.ObserverId, subscription.States, subscription.WireName, subscription.Queue,
                            claim.WorkerId, claim.MaxRows, claim.LeaseDuration, NodeNow(nodeIndex))));
                    }
                    catch (SimTransientFault)
                    {
                        // The claim faulted (store fault / Node Isolation): no batch came back. Tell the
                        // driver the round-trip aborted so it releases the in-flight guard and re-claims
                        // next poll — otherwise this node would never claim this Observer again (§0078).
                        DriveObservers(nodeIndex, new ObserverEvent.DeliveryAborted(claim.ObserverId, NodeNow(nodeIndex)));
                        break;
                    }
                    DriveObservers(nodeIndex, new ObserverEvent.BatchClaimed(claim.ObserverId, batch.Deliveries, NodeNow(nodeIndex)));
                    break;

                case ObserverCommand.InvokeBatch invoke:
                    var results = new List<ObserverInvocationResult>(invoke.Deliveries.Count);
                    foreach (var delivery in invoke.Deliveries)
                    {
                        // The Shell edge invokes the host callback inside a guard (§0077): a throwing,
                        // timed-out, or hung observer is caught here and turned into a failed result —
                        // it never escapes to fail-stop the worker pump. A success records to the sink;
                        // the Dispatch Core decides retry-with-backoff vs dead-letter on a failure.
                        // The delivery counters (0081) are emitted here at the same edge — the future
                        // production observer pump must call these same BackWaveDiagnostics methods so
                        // the seam stays shared, exactly as WorkerGroupService does for job counters.
                        BackWaveDiagnostics.RecordObserverDeliveryAttempted(invoke.ObserverId, delivery.WireName, delivery.Queue);
                        bool succeeded;
                        try
                        {
                            InvokeObserverCallback(invoke.ObserverId, delivery);
                            succeeded = true;
                        }
                        catch (ObserverCallbackFault)
                        {
                            succeeded = false;
                        }
                        if (succeeded)
                        {
                            BackWaveDiagnostics.RecordObserverDeliverySucceeded(invoke.ObserverId, delivery.WireName, delivery.Queue);
                        }
                        results.Add(new ObserverInvocationResult(delivery.Position, succeeded, delivery.DeliveryAttempt));
                    }
                    DriveObservers(nodeIndex, new ObserverEvent.BatchInvoked(invoke.ObserverId, results, NodeNow(nodeIndex)));
                    break;

                case ObserverCommand.ReportBatch report:
                    // The Dispatch Core's Decide produced these dispositions; the dead-letter count is
                    // attributed to this Observer at the report edge (0081). The outcome carries only the
                    // log Position, so wire_name/queue aren't cheaply available here — observer_id alone.
                    foreach (var outcome in report.Outcomes)
                    {
                        if (outcome.Disposition == ObserverDeliveryDisposition.DeadLettered)
                        {
                            BackWaveDiagnostics.RecordObserverDeliveryDeadLettered(report.ObserverId);
                        }
                    }
                    try
                    {
                        Get2(_nodeFaulty[nodeIndex].ReportObserverDeliveriesAsync(new ObserverDeliveryReport(
                            report.ObserverId, report.WorkerId, report.Outcomes, NodeNow(nodeIndex))));
                    }
                    catch (SimTransientFault)
                    {
                        // The report faulted: the cursor stands un-advanced, so the claimed rows redeliver
                        // on a later claim (at-least-once). The guard was already released at BatchInvoked;
                        // signal the abort defensively and stop this round-trip.
                        DriveObservers(nodeIndex, new ObserverEvent.DeliveryAborted(report.ObserverId, NodeNow(nodeIndex)));
                        break;
                    }
                    DriveObservers(nodeIndex, new ObserverEvent.BatchReported(report.ObserverId, NodeNow(nodeIndex)));
                    break;

                case ObserverCommand.RequestPoll repoll:
                    DriveObservers(nodeIndex, new ObserverEvent.PollDue(repoll.Now));
                    break;
            }
        }
    }

    /// <summary>
    /// The deterministic analogue of invoking the host callback (§0077). A success records the
    /// delivery to the recording sink; a <see cref="SimulationOptions.FailingObservers"/> entry
    /// whose threshold this delivery Attempt has not yet passed throws <see cref="ObserverCallbackFault"/>,
    /// standing in for a throwing / timed-out / hung observer that the Shell edge must contain. The
    /// §0078 silent-drop self-test reports success without recording — a drop the oracle must catch.
    /// </summary>
    private void InvokeObserverCallback(string observerId, ObserverClaimedDelivery delivery)
    {
        if (options.FailingObservers.TryGetValue(observerId, out var failThroughAttempt)
            && delivery.DeliveryAttempt <= failThroughAttempt)
        {
            throw new ObserverCallbackFault();
        }
        if (!options.SabotageObserverDelivery)
        {
            _observerDeliveries[observerId].Add((delivery.JobId, delivery.Ordinal, delivery.State, delivery.Attempt));
        }
    }

    /// <summary>
    /// Whether the next hot-path store call faults, taken through the single fault path (issue 0083).
    /// Active only during the workload window; faults stop at WorkloadEnd so the drain can converge. The
    /// <c>StoreFaultProbability &gt; 0</c> guard short-circuits the consultation when the regime is off, so a
    /// fault-free run makes no draw and stays bit-identical. The decision is keyed by <c>(node, op)</c> —
    /// node <c>-1</c> is the shared (non-node) store handle — so each store call's fault is addressed by
    /// stable identity and survives into the Fault Map. The store axis draws from the FaultPlan's historical
    /// <c>Seed ^ "FAULT"</c> stream in call order, so a generate run is byte-identical to the old draw.
    /// </summary>
    private bool StoreShouldFault(int node, string op)
        => options.StoreFaultProbability > 0
           && _now < WorkloadEnd
           && _faultPlan.Fault("store", $"{node}:{op}", options.StoreFaultProbability);

    /// <summary>The realized Fault Map for this run (issue 0083): every fault decision taken, in call order.</summary>
    public IReadOnlyList<FaultEntry> RealizedFaultMap => _faultPlan.ToFaultMap();

    private void Crash(int nodeIndex)
    {
        var node = _nodes[nodeIndex];
        // The crash discards the Driver's outcome buffer (Restart installs a fresh Driver): if it held
        // unflushed outcomes, this is the buffer-loss-on-crash window (those leases lapse and reclaim —
        // At-Least-Once). Tally it for the coverage Situation before the buffer is gone.
        if (node.Driver.BufferedOutcomeCount > 0)
        {
            _outcomeBufferDropped++;
        }
        node.Crashed = true;
        node.Epoch++;
        node.Executing.Clear();
        _crashes++;
        Schedule(_now + _rng.NextTimeSpan(options.MaxCrashDowntime), new SimEvent(EventKind.Restart, nodeIndex, 0, null, false));
    }

    /// <summary>
    /// The single source of truth for the legal-edge set, exposed for the Coverage tracker (issue 0090)
    /// so transition-edge coverage is measured against this denominator rather than a duplicated copy of
    /// the 11 edges. Read-only; the oracle's own membership tests still go through <see cref="LegalTransitions"/>.
    /// </summary>
    internal static IReadOnlyCollection<(JobState From, JobState To)> LegalTransitionEdges => LegalTransitions;

    /// <summary>
    /// The legal state-machine edges (§3) the In-Memory Store can record, the oracle's allow-list.
    /// The two terminal→Scheduled moves are Operator Requeue — the only legal terminal→non-terminal
    /// transition, which also resets the Attempt budget to 0 (handled in <see cref="WalkTransitionLog"/>).
    /// </summary>
    private static readonly HashSet<(JobState From, JobState To)> LegalTransitions =
    [
        (JobState.AwaitingParent, JobState.Scheduled),   // latch released
        (JobState.AwaitingParent, JobState.Cancelled),   // on-success parent failed, or operator cancel
        (JobState.Scheduled, JobState.Leased),           // claim
        (JobState.Scheduled, JobState.Cancelled),        // operator cancel of an idle job
        (JobState.Leased, JobState.Succeeded),           // outcome: success
        (JobState.Leased, JobState.Scheduled),           // outcome: retry, or lease-expiry requeue
        (JobState.Leased, JobState.DeadLettered),        // outcome: terminal failure, or expiry at the ceiling
        (JobState.Leased, JobState.Cancelled),           // cooperative cancel round-trip
        (JobState.Leased, JobState.Quarantined),         // outcome: unroutable
        (JobState.DeadLettered, JobState.Scheduled),     // operator requeue (Attempt → 0)
        (JobState.Quarantined, JobState.Scheduled),      // operator requeue (Attempt → 0)
    ];

    /// <summary>
    /// Legal-transition oracle (§3): walks a job's recorded Transition Log as edges appear, asserting
    /// every consecutive State move is in <see cref="LegalTransitions"/> and that Attempt never
    /// regresses — except the one legal terminal→non-terminal move, an Operator Requeue, which resets
    /// Attempt to 0 (DeadLettered/Quarantined → Scheduled). The log is read only when the job's (State,
    /// Attempt) changed since the previous step, so the walk stays off the hot path; truncation past
    /// MaxTransitionsPerJob is tolerated via the ordinal (a surviving entry whose predecessor aged out
    /// keeps no in-edge to check, and only a genuine first entry — Ordinal 0 — constrains the start state).
    /// </summary>
    private void WalkTransitionLog(Guid jobId, JobRecord job)
    {
        var seen = _lastTransition.TryGetValue(jobId, out var last);
        if (seen && last.State == job.State && last.Attempt == job.Attempt)
        {
            return; // no new transition since the last check
        }

        var history = Get(_store.GetJobHistoryAsync(jobId));

        // Observer-delivery oracle (§0076), expected side: every transition matching a registered
        // Observer's subscription is something that must eventually be delivered ≥1 or dead-lettered.
        // Accumulated from the real recorded history (before any legal-transition splice below), keyed
        // by (jobId, ordinal) so it survives even if the job is later purged.
        if (options.Observers.Count > 0)
        {
            foreach (var observer in options.Observers)
            {
                foreach (var entry in history)
                {
                    if (observer.Subscription.Matches(entry.State, job.WireName, job.Queue))
                    {
                        _observerExpected[observer.Id].Add((jobId, entry.Ordinal));
                    }
                }
            }
        }

        if (options.SabotageLegalTransition && !_legalTransitionSabotaged && job.State == JobState.Succeeded)
        {
            // Self-test: the store records only legal edges, so splice one illegal Succeeded→Leased
            // edge into the history the oracle walks — a live walk MUST reject it (proving it fires).
            history = [.. history, new JobTransition(history[^1].Ordinal + 1, _now, JobState.Leased, job.Attempt, null)];
            _legalTransitionSabotaged = true;
        }

        (JobState State, int Attempt, long Ordinal)? prev = seen ? last : null;
        foreach (var entry in history)
        {
            if (prev is not { } p)
            {
                // No predecessor yet: a genuine first edge (Ordinal 0 — its start state is constrained)
                // or a truncated suffix whose predecessor aged out (Ordinal > 0 — nothing to check).
                if (entry.Ordinal == 0)
                {
                    Invariant(
                        InvariantId.LegalInitialState,
                        entry.State is JobState.Scheduled or JobState.AwaitingParent or JobState.Cancelled,
                        $"job {jobId} first recorded state {entry.State} is not a legal initial state");
                }
                prev = (entry.State, entry.Attempt, entry.Ordinal);
                continue;
            }
            if (entry.Ordinal <= p.Ordinal)
            {
                continue; // already validated in an earlier step (or older than our baseline)
            }
            Invariant(
                InvariantId.LegalTransition,
                LegalTransitions.Contains((p.State, entry.State)),
                $"job {jobId} made an illegal transition {p.State} → {entry.State}");
            var requeueReset = p.State is JobState.DeadLettered or JobState.Quarantined
                && entry.State == JobState.Scheduled && entry.Attempt == 0;
            Invariant(
                InvariantId.AttemptMonotonic,
                requeueReset || entry.Attempt >= p.Attempt,
                $"job {jobId} Attempt regressed {p.Attempt} → {entry.Attempt} (not a Requeue reset)");
            prev = (entry.State, entry.Attempt, entry.Ordinal);
        }

        if (prev is { } final)
        {
            _lastTransition[jobId] = final;
        }
    }

    /// <summary>
    /// Observer-delivery oracle (§0076), in-order side: walks each Observer's recording sink and
    /// proves the first delivery of every transition arrives in ascending log order <i>per job</i> —
    /// you never see "Succeeded" before "Started". Duplicates (re-deliveries of an already-seen
    /// transition) are tolerated, never flagged: delivery is at-least-once, and asserting single
    /// delivery would encode a promise BackWave does not make. Walks only deliveries added since the
    /// last step, so it stays off the hot path.
    /// </summary>
    private void ObserverDeliveryOracle()
    {
        if (options.Observers.Count == 0)
        {
            return;
        }
        foreach (var observer in options.Observers)
        {
            var deliveries = _observerDeliveries[observer.Id];
            var seen = _observerFirstSeen[observer.Id];
            for (var i = _observerDeliveryCursor[observer.Id]; i < deliveries.Count; i++)
            {
                var (jobId, ordinal, _, _) = deliveries[i];
                if (!seen.Add((jobId, ordinal)))
                {
                    continue; // a duplicate delivery — legal by design
                }
                var key = (observer.Id, jobId);
                var prevMax = _observerJobMaxOrdinal.GetValueOrDefault(key, -1L);
                Invariant(
                    InvariantId.ObserverDeliveryOrder,
                    ordinal > prevMax,
                    $"Observer {observer.Id}: job {jobId} transition ordinal {ordinal} first delivered after "
                    + $"ordinal {prevMax} — out of per-job log order");
                _observerJobMaxOrdinal[key] = ordinal;
            }
            _observerDeliveryCursor[observer.Id] = deliveries.Count;
        }
    }

    /// <summary>
    /// Whether every registered Observer has caught up: each transition it watches is delivered ≥1 or
    /// dead-lettered. Gates the drain break so the run never ends with delivery still owed — delivery
    /// lags the transitions, and ending early would manufacture a false liveness failure.
    /// </summary>
    private bool ObserversDrained()
    {
        if (options.Observers.Count == 0)
        {
            return true;
        }
        foreach (var observer in options.Observers)
        {
            var deadLettered = Get(_store.ListObserverDeadLettersAsync(observer.Id))
                .Select(d => (d.JobId, d.Ordinal))
                .ToHashSet();
            var seen = _observerFirstSeen[observer.Id];
            foreach (var expected in _observerExpected[observer.Id])
            {
                if (!seen.Contains(expected) && !deadLettered.Contains(expected))
                {
                    return false;
                }
            }
        }
        return true;
    }

    /// <summary>
    /// Observer-delivery liveness + bounded poison (§0076, §0077): every transition matching a
    /// registered Observer was delivered to its recording sink at least once or dead-lettered —
    /// nothing silently lost, including (from §0078) under Node Isolation and crash-mid-delivery.
    /// A dead-lettered delivery must be <i>bounded poison</i>: it exhausted the attempt ceiling
    /// before being dead-lettered (never dropped prematurely) and was never also delivered to the
    /// sink (a delivered transition never dead-letters).
    /// </summary>
    private void ObserverLivenessCheck()
    {
        if (options.Observers.Count == 0)
        {
            return;
        }
        var ceiling = options.ObserverDeliveryRetryPolicy.MaxAttempts;
        ObserverDeliveryOracle(); // flush any deliveries from the final step into first-seen
        foreach (var observer in options.Observers)
        {
            var deadLetterRecords = Get(_store.ListObserverDeadLettersAsync(observer.Id));
            var deadLettered = deadLetterRecords.Select(d => (d.JobId, d.Ordinal)).ToHashSet();
            var seen = _observerFirstSeen[observer.Id];
            foreach (var expected in _observerExpected[observer.Id])
            {
                Invariant(
                    InvariantId.ObserverDeliveryLiveness,
                    seen.Contains(expected) || deadLettered.Contains(expected),
                    $"Observer {observer.Id}: transition (job {expected.JobId}, ordinal {expected.Ordinal}) was "
                    + "never delivered and never dead-lettered — at-least-once liveness violated");
            }
            foreach (var record in deadLetterRecords)
            {
                // Bounded poison: a delivery dead-letters only after exhausting the ceiling, so a
                // premature drop (the resilience path collapsing failure straight to dead-letter)
                // trips here. Redelivery after a lapsed Lease can overshoot, hence >=.
                Invariant(
                    InvariantId.ObserverPoisonBounded,
                    record.DeliveryAttempts >= ceiling,
                    $"Observer {observer.Id}: transition (job {record.JobId}, ordinal {record.Ordinal}) "
                    + $"dead-lettered after {record.DeliveryAttempts} attempts, below the ceiling of {ceiling} "
                    + "— poison was not bounded by the retry ceiling");
                Invariant(
                    InvariantId.ObserverDeliveredXorDeadLettered,
                    !seen.Contains((record.JobId, record.Ordinal)),
                    $"Observer {observer.Id}: transition (job {record.JobId}, ordinal {record.Ordinal}) was both "
                    + "delivered to the sink and dead-lettered — a delivered transition must never dead-letter");
            }
        }
    }

    /// <summary>The invariant oracle (runs after every step; messages carry the seed).</summary>
    private void CheckInvariants()
    {
        // Outcome-Provenance Oracle (issue 0068, ADR 0013): Effect-Once at the Storage Contract boundary.
        // A stale write from a healed isolated node — applied despite not holding the live Lease for the
        // Attempt — was captured at the report site; fail here with the seed if any was seen.
        if (_provenanceViolation is { } violation)
        {
            Invariant(InvariantId.OutcomeProvenance, false, violation);
        }

        // Observer-delivery oracle (§0076): in-order-per-Observer modulo duplicates, checked after
        // every step over the recording sink. Liveness (at-least-once) is the end-of-run check.
        ObserverDeliveryOracle();

        // Slot-double-release detector (issue 0074): a Concurrency slot is released at most once per Attempt.
        // A captured second release — the Effect-Once symptom a dropped fence causes under isolation load — is
        // asserted here with the seed. Null on every legitimate run.
        if (_slotDoubleReleaseViolation is { } slotViolation)
        {
            Invariant(InvariantId.SlotDoubleRelease, false, slotViolation);
        }

        // Served-set containment (issue 0071): a node only ever holds a Lease on a Queue in its declared
        // served set. A no-op when the Topology Generator is off (every served set is ["default"]).
        CheckServedSetContainment();

        // Per-node cap (issue 0072) — a node's actual concurrent in-flight executions never exceed its
        // PoolSize. Read against the sim's REAL in-flight set, not the Driver's belief, so an over-claim is
        // caught in reality. A no-op (zero-cost early return) off-regime.
        CheckPerNodeCap();

        // I3 — Concurrency Limits never exceeded, checked for EVERY limited Queue simultaneously (issue 0074,
        // extending the single-Queue check). An over-release that lets one Queue's slot leak into another's
        // budget surfaces as that other Queue exceeding its own limit. A no-op when no limit is configured.
        foreach (var (queue, limit) in _limits)
        {
            var leased = TrackedJobs().Count(id =>
            {
                var job = Get(_store.GetJobAsync(id))!;
                return job.State == JobState.Leased && job.Queue == queue;
            });
            Invariant(InvariantId.ConcurrencyLimit, leased <= limit, $"Concurrency Limit {limit} on Queue {queue} exceeded: {leased} jobs Leased");
            // LimitSaturated coverage (issue 0124): the Queue held every slot at once — the limit binds. Tallied
            // off the count the invariant already computed, so it costs nothing extra and draws no rng.
            if (leased >= limit)
            {
                _limitSaturations++;
            }
        }

        // No-Overlap — at most one instance of a No-Overlap schedule may be non-terminal at once
        // (the headline §5.7 guarantee under cluster chaos: the planner suppresses overlapping mints).
        foreach (var seed in options.Schedules.Where(s => s.NoOverlap))
        {
            var live = _minted.Keys.Count(id =>
            {
                var job = Get(_store.GetJobAsync(id))!;
                return job.ScheduleId == seed.Id && !job.State.IsTerminal();
            });
            Invariant(InvariantId.NoOverlap, live <= 1, $"No-Overlap schedule {seed.Id} had {live} instances live at once");
        }

        var leasedNow = new HashSet<Guid>();
        foreach (var jobId in TrackedJobs())
        {
            var job = Get(_store.GetJobAsync(jobId))!;

            // Legal-transition oracle (§3): every recorded edge is a legal state-machine move and
            // Attempt never regresses (bar the Requeue reset). Walked off the recorded history.
            WalkTransitionLog(jobId, job);

            // Pause (§5.8) — a Paused Queue yields nothing to Claim: no job in a Queue the oracle
            // believes Paused may transition INTO Leased. A job already Leased when the Pause
            // landed is fine (Pause never reclaims); only a fresh Lease is the violation.
            if (job.State == JobState.Leased)
            {
                leasedNow.Add(jobId);
                Invariant(
                    InvariantId.PausedClaim,
                    !_oraclePausedQueues.Contains(job.Queue) || _leasedPrev.Contains(jobId),
                    $"Queue {job.Queue} is Paused but job {jobId} was newly Leased");
            }

            // I1 — no double execution while a Lease is live: at most one node may be executing
            // a job whose unexpired Lease the store still attributes to it. Two nodes running the
            // same job is legal under a GC pause (one Lease has lapsed and been reclaimed); two
            // running it while each still owns a live Lease is the headline invariant violation.
            var liveExecutors = 0;
            for (var n = 0; n < _nodes.Length; n++)
            {
                if (_nodes[n].Executing.ContainsKey(jobId)
                    && job.State == JobState.Leased
                    && job.LeaseOwner == $"node-{n}"
                    && job.LeaseExpiry > NodeNow(n))
                {
                    liveExecutors++;
                }
            }
            Invariant(
                InvariantId.NoDoubleExecution,
                liveExecutors <= 1,
                $"I1: job {jobId} executed by {liveExecutors} nodes each holding a live Lease");

            // Migration-Liveness Oracle (issue 0069): once a job's Lease holder is isolated and the Lease
            // has lapsed, a survivor must sweep the expired Lease (then re-lease or terminalize) within a
            // bound derived from config — never a magic number. A permanently-lost node never reports, so
            // without migration the job is stuck forever; this flags it at the exact (jobId, time) rather
            // than as a vague end-of-run convergence failure. The lapsed Lease is folded into LeaseExpiry;
            // the bound then allows a survivor its worst-case clock lag plus a poll or two to sweep.
            //
            // The tight bound is only valid when the reclaim sweep itself cannot be transiently blocked — the
            // same "a *running* survivor sweeps within a poll or two" precondition the bound is derived from:
            //   • Crashes off — with crashes on, all nodes can be transiently down (2 isolated + 1 crashed), the
            //     all-nodes-lost case the PRD scopes out of Phase 1.
            //   • Store faults off — the survivor's reclaim is its ExpireLeases store call, and a transient store
            //     fault unwinds it to a retry on the survivor's NEXT poll (the production pump's transient
            //     handling). A run of store faults on consecutive survivor sweeps delays reclaim arbitrarily far
            //     past a poll-cadence bound while the job stays Leased — yet it is NOT stranded: the survivor
            //     keeps sweeping every poll and reclaims the moment one does not fault (faults stop at
            //     WorkloadEnd, the drain absorbs the rest). That transient delay is indistinguishable, at any
            //     single step, from permanent stranding, so the tight per-step bound cannot tell them apart;
            //     issue vopr-0139 (the only finding in a 72h cycled run: the lone survivor faulted on three
            //     straight reclaim-capable sweeps, 0.2s past the bound) is exactly this false positive.
            // In either regime the end-of-run convergence check remains the backstop, catching a genuine
            // permanent stall (a never-swept Lease) that the relaxed per-step bound now lets pass. The
            // SabotageMigrationFaultGrace self-test restores the pre-fix (ungated) bound to prove the gate is
            // load-bearing; the SabotageMigrationSweep self-test runs store-faults-off, so it keeps full teeth.
            if (options.CrashProbabilityPerPoll == 0
                && (options.StoreFaultProbability == 0 || options.SabotageMigrationFaultGrace)
                && job is { State: JobState.Leased, LeaseOwner: { } leaseOwner, LeaseExpiry: { } leaseExpiry }
                && leaseExpiry <= _now
                && IsIsolatedOwner(leaseOwner))
            {
                Invariant(
                    InvariantId.MigrationLiveness,
                    _now <= leaseExpiry + MigrationBound,
                    $"migration-liveness: job {jobId} still Leased by isolated {leaseOwner} "
                    + $"{(_now - leaseExpiry).TotalSeconds:0.0}s past Lease expiry (bound {MigrationBound.TotalSeconds:0.0}s)");
            }

            Invariant(
                InvariantId.LeaseOwnerPresent,
                job.State != JobState.Leased || (job.LeaseOwner is not null && job.LeaseExpiry is not null),
                $"Leased job {job.JobId} without owner/expiry");
            Invariant(
                InvariantId.LeaseOwnerCleared,
                job.State == JobState.Leased || job.LeaseOwner is null,
                $"non-Leased job {job.JobId} still has a LeaseOwner");
            Invariant(
                InvariantId.AttemptCeiling,
                job.Attempt <= options.MaxAttempts,
                $"job {job.JobId} exceeded the attempt ceiling: {job.Attempt}");

            // I2 — no Awaiting-Parent orphans: once a job's parent is terminal, the latch
            // must already have fired (resolution is atomic with the parent's transition).
            if (job.State == JobState.AwaitingParent)
            {
                var (parentId, _) = _parentsByChild[jobId];
                Invariant(
                    InvariantId.NoAwaitingParentOrphan,
                    !_terminalSeen.ContainsKey(parentId),
                    $"orphan: job {jobId} is AwaitingParent but its parent {parentId} is terminal");
            }

            // Cancellation provenance (§5.8) — the only legal ways into Cancelled are an operator
            // cancel issued against this job (immediate, or the cooperative Leased round-trip) or an
            // on-success dependency whose parent reached a non-success terminal state. A Cancelled
            // job with neither — a signal applied to the wrong job, a cancellation leaking across
            // attempts, a spurious latch — is the bug this catches. The dependency justification is
            // recorded sticky the step the latch fires, so a later parent Requeue can't unjustify it.
            if (job.State == JobState.Cancelled
                && !_cancelTargets.Contains(jobId)
                && !_dependencyCancelled.Contains(jobId))
            {
                var latchCancelled =
                    _parentsByChild.TryGetValue(jobId, out var link)
                    && link.Mode == DependencyMode.OnSuccess
                    && Get(_store.GetJobAsync(link.ParentId)) is { } parent
                    && parent.State.IsTerminal()
                    && parent.State != JobState.Succeeded;
                Invariant(
                    InvariantId.CancelProvenance,
                    latchCancelled,
                    $"job {jobId} is Cancelled with no provenance: no operator cancel and no failed on-success parent");
                _dependencyCancelled.Add(jobId);
            }

            var isTerminal = job.State.IsTerminal();
            if (_terminalSeen.TryGetValue(jobId, out var seenState))
            {
                Invariant(InvariantId.TerminalStable, job.State == seenState, $"terminal job {job.JobId} changed state {seenState} → {job.State}");
                Invariant(InvariantId.TerminalTimestamp, job.TerminalAt is not null, $"terminal job {job.JobId} lost TerminalAt");
            }
            else if (isTerminal)
            {
                _terminalSeen[jobId] = job.State;
            }
        }

        _leasedPrev = leasedNow; // baseline for the next step's Pause check
    }

    /// <summary>
    /// Per-node-cap invariant (issue 0072): a node's actual concurrent in-flight executions never exceed its
    /// PoolSize. Zero-tolerance — there is no regime in which real in-flight legitimately exceeds the pool, so
    /// any breach is a bug. The check reads <see cref="SimNode.Executing"/>, the sim's REAL in-flight set, not
    /// the Driver's internal <c>_executing</c> belief: a Driver miscount that over-claims is then caught in
    /// reality rather than masked by its own bookkeeping. Guarded to a true no-op when PoolSize is unbounded so
    /// the default regime stays byte-identical and pays nothing. The cap-sabotage builds the Driver unbounded,
    /// so it over-admits past the configured PoolSize and Executing.Count exceeds it — tripping this oracle.
    /// </summary>
    private void CheckPerNodeCap()
    {
        if (options.PoolSize == int.MaxValue)
        {
            return;
        }
        for (var n = 0; n < _nodes.Length; n++)
        {
            Invariant(
                InvariantId.PerNodeCap,
                _nodes[n].Executing.Count <= options.PoolSize,
                $"per-node cap {options.PoolSize} exceeded: node-{n} has {_nodes[n].Executing.Count} in-flight executions");
            // BackpressureIdle coverage (issue 0124): the node's finite pool is full, so its next claim is
            // blocked purely by backpressure (the Driver subtracts in-flight from each claim batch). Tallied
            // off the in-flight count the invariant already read — no rng, no hot-path instrumentation.
            if (_nodes[n].Executing.Count >= options.PoolSize)
            {
                _backpressureIdleTicks++;
            }
        }
    }

    /// <summary>
    /// Records that an applied outcome freed the Concurrency slot for one (jobId, attempt) on a limited Queue
    /// (issue 0074). The first release per Attempt is normal; a second is the slot-double-release symptom —
    /// captured for the detector in CheckInvariants. The <see cref="SimulationOptions.SabotageSlotDoubleRelease"/>
    /// self-test tallies one phantom extra release for the first slot freed, so the detector MUST catch it.
    /// </summary>
    private void RecordSlotRelease(Guid jobId, int attempt)
    {
        var key = (jobId, attempt);
        var count = _slotReleases.GetValueOrDefault(key) + 1;
        if (options.SabotageSlotDoubleRelease && !_slotReleaseSabotaged)
        {
            _slotReleaseSabotaged = true;
            count++; // a phantom second release the intact fence would never allow: free this slot twice
        }
        _slotReleases[key] = count;
        if (count > 1)
        {
            _slotDoubleReleaseViolation ??=
                $"slot-double-release: Concurrency slot for job {jobId} attempt {attempt} released {count}× "
                + "(an Attempt's slot must free at most once — Effect-Once on the slot)";
        }
    }

    /// <summary>
    /// The Migration-Liveness bound (issue 0069), derived purely from config — never a magic number.
    /// Once a Lease has lapsed (its LeaseExpiry already folds in MaxLeaseDuration), a survivor notices at
    /// worst its full clock skew late and sweeps on its next maintenance poll; the extra poll cadence is ε
    /// slack. A permanently-lost node's Lease that is never swept exceeds this finite bound and trips.
    /// </summary>
    private TimeSpan MigrationBound => options.MaxClockSkew + options.PollInterval * 3;

    /// <summary>Whether <paramref name="owner"/> ("node-N") is a node the Isolation Scheduler currently has cut off.</summary>
    private bool IsIsolatedOwner(string owner)
        => owner.StartsWith("node-", StringComparison.Ordinal)
           && int.TryParse(owner.AsSpan(5), out var node)
           && _isolation.IsIsolated(node);

    /// <summary>
    /// Served-set-containment oracle (issue 0071): a node only ever holds a Lease on a Queue in its DECLARED
    /// (recorded) served set. For every tracked job that is Leased, map its <c>LeaseOwner</c> ("node-N") to that
    /// node's recorded served set and assert the job's Queue is in it. A no-op when the Topology Generator is off
    /// (<c>_servedSets</c> empty → every node serves only "default", and every job is on "default"). The
    /// <see cref="SimulationOptions.SabotageServedSet"/> twin gives a narrow node a DRIVER that claims a foreign
    /// Queue while its recorded served set stays narrow, so the claimed foreign Lease trips this check.
    /// </summary>
    private void CheckServedSetContainment()
    {
        if (_servedSets.Length == 0)
        {
            return; // single-Queue world: every node serves "default", every job is on "default"
        }
        foreach (var jobId in TrackedJobs())
        {
            var job = Get(_store.GetJobAsync(jobId))!;
            if (job.State != JobState.Leased || job.LeaseOwner is not { } owner)
            {
                continue;
            }
            if (owner.StartsWith("node-", StringComparison.Ordinal)
                && int.TryParse(owner.AsSpan(5), out var node)
                && node >= 0 && node < _servedSets.Length)
            {
                Invariant(
                    InvariantId.ServedSetContainment,
                    _servedSets[node].Contains(job.Queue),
                    $"served-set: job {jobId} Leased by {owner} on Queue {job.Queue} outside its served set "
                    + $"[{string.Join(", ", _servedSets[node])}]");
            }
        }
    }

    private bool AllTerminal()
        => _jobIds.Count == options.JobCount
           && _jobIds.All(id => _terminalSeen.ContainsKey(id))
           && _minted.Keys.All(id => _terminalSeen.ContainsKey(id));

    /// <summary>The jobs the oracle tracks: the enqueued workload plus every minted schedule instance.</summary>
    private IEnumerable<Guid> TrackedJobs() => _minted.Count == 0 ? _jobIds : _jobIds.Concat(_minted.Keys);

    /// <summary>
    /// Stuck-job diagnostic for the work-conservation drain-liveness check (issue 0074/0075, ADR 0016): when
    /// the run fails to converge, names the non-terminal jobs grouped by Queue, flagging each limited Queue
    /// with its cap — so a leaked Concurrency slot or a starved served Queue points at the blocking
    /// constraint rather than surfacing as a bare "not all terminal". Empty when everything converged.
    /// </summary>
    private string StuckJobDiagnostic()
    {
        var stuck = TrackedJobs()
            .Select(id => Get(_store.GetJobAsync(id))!)
            .Where(job => !job.State.IsTerminal())
            .GroupBy(job => job.Queue)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var byState = g.GroupBy(j => j.State).OrderBy(s => s.Key)
                    .Select(s => $"{s.Count()} {s.Key}");
                var cap = _limits.TryGetValue(g.Key, out var limit) ? $", limit {limit}" : "";
                return $"Queue {g.Key}{cap}: {string.Join(", ", byState)}";
            })
            .ToList();
        return stuck.Count == 0 ? "" : $" — stuck [{string.Join("; ", stuck)}]";
    }

    private void Invariant(InvariantId id, bool condition, string message)
    {
        if (!condition)
        {
            throw new SimulationInvariantException(
                id,
                $"[seed {options.Seed}] invariant {id} violated at virtual {_now:O} after {_steps} steps: {message}");
        }
    }

    /// <summary>Per-Attempt rng stream: seed ⊕ jobId ⊕ attempt, replayable forever.</summary>
    private DeterministicRandom PerAttempt(Guid jobId, int attempt)
    {
        Span<byte> bytes = stackalloc byte[16];
        jobId.TryWriteBytes(bytes);
        var lo = BitConverter.ToUInt64(bytes);
        var hi = BitConverter.ToUInt64(bytes[8..]);
        return new DeterministicRandom(options.Seed ^ lo ^ hi ^ ((ulong)attempt * 0xA24BAED4963EE407UL));
    }

    /// <summary>Something was enqueued: each node may get a delayed Wake-Up Hint, or none.</summary>
    private void PublishHints()
    {
        if (options.HintDeliveryProbability <= 0)
        {
            return;
        }
        for (var node = 0; node < _nodes.Length; node++)
        {
            if (_hintRng.NextDouble() < options.HintDeliveryProbability)
            {
                Schedule(
                    _now + _hintRng.NextTimeSpan(options.MaxHintLatency),
                    new SimEvent(EventKind.Hint, node, 0, null, false));
            }
        }
    }

    private DateTimeOffset NodeNow(int nodeIndex) => _now + _nodes[nodeIndex].Skew;

    // Adaptive idle backoff runs only when a strictly larger ceiling is set; otherwise the fixed
    // PollInterval cadence governs polling and every run stays byte-identical to before.
    private bool AdaptivePoll => options.MaxPollInterval > options.PollInterval;

    // Fold a claim outcome into a node's idle poll-backoff delay, mirroring the host pump. Work claimed, or
    // a store that reports due-now pressure it withheld, resets to the floor. An empty poll with a future
    // next-due sleeps to that instant (clamped to floor/ceiling). An empty poll with no next-due grows the
    // delay toward the ceiling in step with how long the node has been idle. The delay only sets WHEN the
    // node next polls, never WHETHER work is claimed.
    private void UpdatePollBackoff(int nodeIndex, bool claimedWork, DateTimeOffset? nextDue, DateTimeOffset now)
    {
        var floor = options.PollInterval;
        var ceiling = options.MaxPollInterval;
        TimeSpan next;
        if (claimedWork || (nextDue is { } due && due <= now))
        {
            _nodes[nodeIndex].IdleSince = null;
            next = floor;
        }
        else if (nextDue is { } scheduled)
        {
            var untilDue = scheduled - now;
            next = untilDue < floor ? floor : (untilDue > ceiling ? ceiling : untilDue);
        }
        else
        {
            var idleSince = _nodes[nodeIndex].IdleSince ?? now;
            _nodes[nodeIndex].IdleSince = idleSince;
            var idleFor = now - idleSince;
            next = idleFor < floor ? floor : (idleFor > ceiling ? ceiling : idleFor);
        }
        _nodes[nodeIndex].PollDelay = next;
    }

    private void Schedule(DateTimeOffset at, SimEvent simEvent) => _queue.Enqueue(simEvent, (at, _sequence++));

    /// <summary>The In-Memory Store always completes synchronously; the Simulator is single-threaded.</summary>
    private static T Get<T>(ValueTask<T> task) => task.GetAwaiter().GetResult();

    private static void Get2(ValueTask task) => task.GetAwaiter().GetResult();
}

internal sealed class SimulationInvariantException(InvariantId invariantId, string message) : Exception(message)
{
    /// <summary>The stable identity of the tripped oracle (issue 0085) — the dedup/match key, never the message.</summary>
    public InvariantId InvariantId { get; } = invariantId;
}

/// <summary>
/// Wraps the In-Memory Store and throws a <see cref="SimTransientFault"/> before the hot-path
/// operations when <paramref name="shouldFault"/> fires — the storage-fault regime. Reads pass
/// through untouched so the oracle and final assertions always see real, committed state.
/// </summary>
internal sealed class FaultInjectingStore(IJobStore inner, Func<string, bool> shouldFault) : IJobStore
{
    /// <summary>
    /// Ack-loss isolation (issue 0070): when armed, the next <see cref="ReportOutcomeAsync"/> applies its
    /// write to the store and THEN throws the transient, so the caller believes the report failed even
    /// though it committed. One-shot — consumed by the next outcome write. Off in every non-ack-loss regime.
    /// </summary>
    public bool AckLossArmed { get; set; }

    private void MaybeFault(string op)
    {
        if (shouldFault(op))
        {
            throw new SimTransientFault();
        }
    }

    public bool SupportsTransactionalEnqueue => inner.SupportsTransactionalEnqueue;

    public ValueTask<EnqueueResult> EnqueueAsync(
        NewJob job, DateTimeOffset now, System.Data.Common.DbTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        MaybeFault("Enqueue");
        return inner.EnqueueAsync(job, now, transaction, cancellationToken);
    }

    public ValueTask<IReadOnlyList<JobRecord>> ClaimAsync(ClaimRequest request, CancellationToken cancellationToken = default)
    {
        MaybeFault("Claim");
        return inner.ClaimAsync(request, cancellationToken);
    }

    // Forward the batch claim so the inner store's next-due hint reaches the adaptive poll pacer; a fault
    // aborts the whole claim, exactly as it does for ClaimAsync.
    public ValueTask<ClaimResult> ClaimBatchAsync(ClaimRequest request, CancellationToken cancellationToken = default)
    {
        MaybeFault("Claim");
        return inner.ClaimBatchAsync(request, cancellationToken);
    }

    public ValueTask<OutcomeResult> ReportOutcomeAsync(
        Guid jobId, string workerId, int attempt, JobOutcome outcome, DateTimeOffset now,
        string? failureDetail = null, JobTags? addedTags = null, ReadOnlyMemory<byte>? output = null,
        CancellationToken cancellationToken = default)
    {
        if (AckLossArmed)
        {
            // Ack-loss: the write commits, then the ack is lost. Apply (the In-Memory Store is synchronous),
            // then throw so the node believes its report failed and retries — the committed-write path.
            AckLossArmed = false;
            inner.ReportOutcomeAsync(jobId, workerId, attempt, outcome, now, failureDetail, addedTags, output, cancellationToken)
                .GetAwaiter().GetResult();
            throw new SimTransientFault();
        }
        MaybeFault("ReportOutcome");
        return inner.ReportOutcomeAsync(jobId, workerId, attempt, outcome, now, failureDetail, addedTags, output, cancellationToken);
    }

    public ValueTask<ReadOnlyMemory<byte>?> GetJobOutputAsync(Guid jobId, CancellationToken cancellationToken = default)
        => inner.GetJobOutputAsync(jobId, cancellationToken);

    public ValueTask<IReadOnlyList<HeartbeatResult>> HeartbeatAsync(
        string workerId, IReadOnlyList<Guid> jobIds, TimeSpan leaseDuration, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        MaybeFault("Heartbeat");
        return inner.HeartbeatAsync(workerId, jobIds, leaseDuration, now, cancellationToken);
    }

    public ValueTask<int> ExpireLeasesAsync(
        DateTimeOffset now, int maxJobs, IReadOnlyList<string> queues, RetryDisposition disposition,
        CancellationToken cancellationToken = default)
    {
        MaybeFault("ExpireLeases");
        return inner.ExpireLeasesAsync(now, maxJobs, queues, disposition, cancellationToken);
    }

    public ValueTask<int> MintDueAsync(IReadOnlyList<MintDecision> decisions, CancellationToken cancellationToken = default)
    {
        MaybeFault("MintDue");
        return inner.MintDueAsync(decisions, cancellationToken);
    }

    // Reads and config never fault: the oracle must always see committed truth.
    public ValueTask<CancelResult> CancelJobAsync(Guid jobId, string actor, DateTimeOffset now, CancellationToken cancellationToken = default)
        => inner.CancelJobAsync(jobId, actor, now, cancellationToken);

    public ValueTask<RequeueResult> RequeueAsync(Guid jobId, string actor, DateTimeOffset now, CancellationToken cancellationToken = default)
        => inner.RequeueAsync(jobId, actor, now, cancellationToken);

    public ValueTask PauseQueueAsync(string queue, string actor, DateTimeOffset now, CancellationToken cancellationToken = default)
        => inner.PauseQueueAsync(queue, actor, now, cancellationToken);

    public ValueTask ResumeQueueAsync(string queue, string actor, DateTimeOffset now, CancellationToken cancellationToken = default)
        => inner.ResumeQueueAsync(queue, actor, now, cancellationToken);

    public ValueTask<TriggerScheduleResult> TriggerScheduleNowAsync(string scheduleId, string actor, DateTimeOffset now, CancellationToken cancellationToken = default)
        => inner.TriggerScheduleNowAsync(scheduleId, actor, now, cancellationToken);

    public ValueTask<IReadOnlyList<OperatorAuditRecord>> ListAuditRecordsAsync(string target, CancellationToken cancellationToken = default)
        => inner.ListAuditRecordsAsync(target, cancellationToken);

    public ValueTask UpsertScheduleAsync(ScheduleRecord schedule, CancellationToken cancellationToken = default)
        => inner.UpsertScheduleAsync(schedule, cancellationToken);

    public ValueTask RemoveScheduleAsync(string scheduleId, CancellationToken cancellationToken = default)
        => inner.RemoveScheduleAsync(scheduleId, cancellationToken);

    public ValueTask<IReadOnlyList<ScheduleSnapshot>> ListSchedulesAsync(CancellationToken cancellationToken = default)
        => inner.ListSchedulesAsync(cancellationToken);

    public ValueTask SetConcurrencyLimitAsync(string queue, int? limit, string actor, DateTimeOffset now, CancellationToken cancellationToken = default)
        => inner.SetConcurrencyLimitAsync(queue, limit, actor, now, cancellationToken);

    public ValueTask<JobRecord?> GetJobAsync(Guid jobId, CancellationToken cancellationToken = default)
        => inner.GetJobAsync(jobId, cancellationToken);

    public ValueTask<IReadOnlyList<JobTransition>> GetJobHistoryAsync(Guid jobId, CancellationToken cancellationToken = default)
        => inner.GetJobHistoryAsync(jobId, cancellationToken);

    public ValueTask<IReadOnlyList<JobRecord>> ListJobsAsync(JobQuery query, CancellationToken cancellationToken = default)
        => inner.ListJobsAsync(query, cancellationToken);

    public ValueTask<IReadOnlyList<QueueStateCount>> CountJobsAsync(CancellationToken cancellationToken = default)
        => inner.CountJobsAsync(cancellationToken);

    public ValueTask<IReadOnlyList<TagFacet>> FacetAsync(
        string key, JobQuery? baseQuery = null, int maxResults = int.MaxValue, CancellationToken cancellationToken = default)
        => inner.FacetAsync(key, baseQuery, maxResults, cancellationToken);

    public ValueTask<IReadOnlyList<TagSuggestion>> SuggestTagsAsync(TagSuggestQuery query, CancellationToken cancellationToken = default)
        => inner.SuggestTagsAsync(query, cancellationToken);

    public ValueTask<IReadOnlyList<QueueSettings>> ListQueueSettingsAsync(CancellationToken cancellationToken = default)
        => inner.ListQueueSettingsAsync(cancellationToken);

    public ValueTask<DependencyEdges> GetDependencyEdgesAsync(Guid jobId, CancellationToken cancellationToken = default)
        => inner.GetDependencyEdgesAsync(jobId, cancellationToken);

    public ValueTask<WorkflowEnqueueResult> EnqueueWorkflowAsync(
        WorkflowDefinition workflow, DateTimeOffset now, System.Data.Common.DbTransaction? transaction = null,
        CancellationToken cancellationToken = default)
        => inner.EnqueueWorkflowAsync(workflow, now, transaction, cancellationToken);

    public ValueTask<IReadOnlyList<WorkflowSnapshot>> ListWorkflowsAsync(CancellationToken cancellationToken = default)
        => inner.ListWorkflowsAsync(cancellationToken);

    public ValueTask<WorkflowGraph?> GetWorkflowAsync(Guid workflowId, CancellationToken cancellationToken = default)
        => inner.GetWorkflowAsync(workflowId, cancellationToken);

    public ValueTask<int> PurgeTerminalAsync(
        TerminalStateClass stateClass, DateTimeOffset terminalBefore, int maxJobs, CancellationToken cancellationToken = default)
        => inner.PurgeTerminalAsync(stateClass, terminalBefore, maxJobs, cancellationToken);

    // Observer-delivery claim/report fault exactly as the job hot path does — so Node Isolation and
    // store faults reach the delivery cursor too (issue 0078). The reads never fault: the oracle and
    // the dashboard must always see committed delivery truth.
    public ValueTask<ObserverClaim> ClaimObserverDeliveriesAsync(ObserverClaimRequest request, CancellationToken cancellationToken = default)
    {
        MaybeFault("ClaimObserver");
        return inner.ClaimObserverDeliveriesAsync(request, cancellationToken);
    }

    public ValueTask ReportObserverDeliveriesAsync(ObserverDeliveryReport report, CancellationToken cancellationToken = default)
    {
        MaybeFault("ReportObserver");
        return inner.ReportObserverDeliveriesAsync(report, cancellationToken);
    }

    public ValueTask<long> GetObserverCursorAsync(string observerId, CancellationToken cancellationToken = default)
        => inner.GetObserverCursorAsync(observerId, cancellationToken);

    public ValueTask<ObserverLag> GetObserverLagAsync(ObserverLagRequest request, CancellationToken cancellationToken = default)
        => inner.GetObserverLagAsync(request, cancellationToken);

    public ValueTask<IReadOnlyList<ObserverDeadLetterRecord>> ListObserverDeadLettersAsync(string observerId, CancellationToken cancellationToken = default)
        => inner.ListObserverDeadLettersAsync(observerId, cancellationToken);
}

/// <summary>
/// Outcome-Provenance sabotage (issue 0068): the In-Memory Store with the (workerId, attempt) identity
/// fence (§5.6) dropped. <see cref="ReportOutcomeAsync"/> forces a reporter's outcome through under the
/// CURRENT live-Lease holder's identity, so a healed isolated node's stale report mutates state it does
/// not own — exactly the Effect-Once violation the Outcome-Provenance Oracle must catch. The expiry
/// fence is left intact, so the canonical race (a survivor re-leased on a fresh Lease before the stale
/// node healed) is what lands the bad write. Every other operation, and all reads, pass straight
/// through, so the oracle still sees committed truth. Never used in real regimes.
/// </summary>
internal sealed class FenceDroppingStore(InMemoryJobStore inner) : IJobStore
{
    public bool SupportsTransactionalEnqueue => inner.SupportsTransactionalEnqueue;

    public ValueTask<OutcomeResult> ReportOutcomeAsync(
        Guid jobId, string workerId, int attempt, JobOutcome outcome, DateTimeOffset now,
        string? failureDetail = null, JobTags? addedTags = null, ReadOnlyMemory<byte>? output = null,
        CancellationToken cancellationToken = default)
    {
        var job = inner.GetJobAsync(jobId, cancellationToken).GetAwaiter().GetResult();
        if (job is { State: JobState.Leased, LeaseOwner: { } holder } && job.LeaseExpiry > now)
        {
            // Drop the identity fence: apply under whoever currently holds the live Lease, regardless of
            // who actually reported. A stale report from a non-holder now mutates state — and Effect-Once
            // breaks loudly when the harness compares the true reporter against the live holder.
            return inner.ReportOutcomeAsync(jobId, holder, job.Attempt, outcome, now, failureDetail, addedTags, output, cancellationToken);
        }
        return inner.ReportOutcomeAsync(jobId, workerId, attempt, outcome, now, failureDetail, addedTags, output, cancellationToken);
    }

    public ValueTask<ReadOnlyMemory<byte>?> GetJobOutputAsync(Guid jobId, CancellationToken cancellationToken = default)
        => inner.GetJobOutputAsync(jobId, cancellationToken);

    public ValueTask<EnqueueResult> EnqueueAsync(
        NewJob job, DateTimeOffset now, System.Data.Common.DbTransaction? transaction = null,
        CancellationToken cancellationToken = default)
        => inner.EnqueueAsync(job, now, transaction, cancellationToken);

    public ValueTask<IReadOnlyList<JobRecord>> ClaimAsync(ClaimRequest request, CancellationToken cancellationToken = default)
        => inner.ClaimAsync(request, cancellationToken);

    public ValueTask<ClaimResult> ClaimBatchAsync(ClaimRequest request, CancellationToken cancellationToken = default)
        => inner.ClaimBatchAsync(request, cancellationToken);

    public ValueTask<IReadOnlyList<HeartbeatResult>> HeartbeatAsync(
        string workerId, IReadOnlyList<Guid> jobIds, TimeSpan leaseDuration, DateTimeOffset now,
        CancellationToken cancellationToken = default)
        => inner.HeartbeatAsync(workerId, jobIds, leaseDuration, now, cancellationToken);

    public ValueTask<int> ExpireLeasesAsync(
        DateTimeOffset now, int maxJobs, IReadOnlyList<string> queues, RetryDisposition disposition,
        CancellationToken cancellationToken = default)
        => inner.ExpireLeasesAsync(now, maxJobs, queues, disposition, cancellationToken);

    public ValueTask<int> MintDueAsync(IReadOnlyList<MintDecision> decisions, CancellationToken cancellationToken = default)
        => inner.MintDueAsync(decisions, cancellationToken);

    public ValueTask<CancelResult> CancelJobAsync(Guid jobId, string actor, DateTimeOffset now, CancellationToken cancellationToken = default)
        => inner.CancelJobAsync(jobId, actor, now, cancellationToken);

    public ValueTask<RequeueResult> RequeueAsync(Guid jobId, string actor, DateTimeOffset now, CancellationToken cancellationToken = default)
        => inner.RequeueAsync(jobId, actor, now, cancellationToken);

    public ValueTask PauseQueueAsync(string queue, string actor, DateTimeOffset now, CancellationToken cancellationToken = default)
        => inner.PauseQueueAsync(queue, actor, now, cancellationToken);

    public ValueTask ResumeQueueAsync(string queue, string actor, DateTimeOffset now, CancellationToken cancellationToken = default)
        => inner.ResumeQueueAsync(queue, actor, now, cancellationToken);

    public ValueTask<TriggerScheduleResult> TriggerScheduleNowAsync(string scheduleId, string actor, DateTimeOffset now, CancellationToken cancellationToken = default)
        => inner.TriggerScheduleNowAsync(scheduleId, actor, now, cancellationToken);

    public ValueTask<IReadOnlyList<OperatorAuditRecord>> ListAuditRecordsAsync(string target, CancellationToken cancellationToken = default)
        => inner.ListAuditRecordsAsync(target, cancellationToken);

    public ValueTask UpsertScheduleAsync(ScheduleRecord schedule, CancellationToken cancellationToken = default)
        => inner.UpsertScheduleAsync(schedule, cancellationToken);

    public ValueTask RemoveScheduleAsync(string scheduleId, CancellationToken cancellationToken = default)
        => inner.RemoveScheduleAsync(scheduleId, cancellationToken);

    public ValueTask<IReadOnlyList<ScheduleSnapshot>> ListSchedulesAsync(CancellationToken cancellationToken = default)
        => inner.ListSchedulesAsync(cancellationToken);

    public ValueTask SetConcurrencyLimitAsync(string queue, int? limit, string actor, DateTimeOffset now, CancellationToken cancellationToken = default)
        => inner.SetConcurrencyLimitAsync(queue, limit, actor, now, cancellationToken);

    public ValueTask<JobRecord?> GetJobAsync(Guid jobId, CancellationToken cancellationToken = default)
        => inner.GetJobAsync(jobId, cancellationToken);

    public ValueTask<IReadOnlyList<JobTransition>> GetJobHistoryAsync(Guid jobId, CancellationToken cancellationToken = default)
        => inner.GetJobHistoryAsync(jobId, cancellationToken);

    public ValueTask<IReadOnlyList<JobRecord>> ListJobsAsync(JobQuery query, CancellationToken cancellationToken = default)
        => inner.ListJobsAsync(query, cancellationToken);

    public ValueTask<IReadOnlyList<QueueStateCount>> CountJobsAsync(CancellationToken cancellationToken = default)
        => inner.CountJobsAsync(cancellationToken);

    public ValueTask<IReadOnlyList<TagFacet>> FacetAsync(
        string key, JobQuery? baseQuery = null, int maxResults = int.MaxValue, CancellationToken cancellationToken = default)
        => inner.FacetAsync(key, baseQuery, maxResults, cancellationToken);

    public ValueTask<IReadOnlyList<TagSuggestion>> SuggestTagsAsync(TagSuggestQuery query, CancellationToken cancellationToken = default)
        => inner.SuggestTagsAsync(query, cancellationToken);

    public ValueTask<IReadOnlyList<QueueSettings>> ListQueueSettingsAsync(CancellationToken cancellationToken = default)
        => inner.ListQueueSettingsAsync(cancellationToken);

    public ValueTask<DependencyEdges> GetDependencyEdgesAsync(Guid jobId, CancellationToken cancellationToken = default)
        => inner.GetDependencyEdgesAsync(jobId, cancellationToken);

    public ValueTask<WorkflowEnqueueResult> EnqueueWorkflowAsync(
        WorkflowDefinition workflow, DateTimeOffset now, System.Data.Common.DbTransaction? transaction = null,
        CancellationToken cancellationToken = default)
        => inner.EnqueueWorkflowAsync(workflow, now, transaction, cancellationToken);

    public ValueTask<IReadOnlyList<WorkflowSnapshot>> ListWorkflowsAsync(CancellationToken cancellationToken = default)
        => inner.ListWorkflowsAsync(cancellationToken);

    public ValueTask<WorkflowGraph?> GetWorkflowAsync(Guid workflowId, CancellationToken cancellationToken = default)
        => inner.GetWorkflowAsync(workflowId, cancellationToken);

    public ValueTask<int> PurgeTerminalAsync(
        TerminalStateClass stateClass, DateTimeOffset terminalBefore, int maxJobs, CancellationToken cancellationToken = default)
        => inner.PurgeTerminalAsync(stateClass, terminalBefore, maxJobs, cancellationToken);

    public ValueTask<ObserverClaim> ClaimObserverDeliveriesAsync(ObserverClaimRequest request, CancellationToken cancellationToken = default)
        => inner.ClaimObserverDeliveriesAsync(request, cancellationToken);

    public ValueTask ReportObserverDeliveriesAsync(ObserverDeliveryReport report, CancellationToken cancellationToken = default)
        => inner.ReportObserverDeliveriesAsync(report, cancellationToken);

    public ValueTask<long> GetObserverCursorAsync(string observerId, CancellationToken cancellationToken = default)
        => inner.GetObserverCursorAsync(observerId, cancellationToken);

    public ValueTask<ObserverLag> GetObserverLagAsync(ObserverLagRequest request, CancellationToken cancellationToken = default)
        => inner.GetObserverLagAsync(request, cancellationToken);

    public ValueTask<IReadOnlyList<ObserverDeadLetterRecord>> ListObserverDeadLettersAsync(string observerId, CancellationToken cancellationToken = default)
        => inner.ListObserverDeadLettersAsync(observerId, cancellationToken);
}
