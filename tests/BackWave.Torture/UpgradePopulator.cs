using BackWave.Storage;

namespace BackWave.Torture;

/// <summary>
/// The GOLDEN-FIXTURE INVENTORY for the in-place upgrade harness (issue 0202 / ADR 0038), materialized
/// as version-aware direct SQL rather than checked-in binary dumps. Given a throwaway database migrated
/// to a prior version N-1, this writes one populated database that covers every interesting shape the
/// upgrade contract must carry across a migration — using ONLY the tables and columns that exist at N-1
/// (workflows since v7, tags since v6, observers since v5, the transition log since v4, operator audit
/// and queue pause since v3). The current-build store cannot help here: it speaks only the current
/// schema and fail-stops on any older one, so every fixture row is written with raw parameterized SQL.
///
/// Population inventory (each shape recorded in the journal as an accepted enqueue so the post-upgrade
/// conservation oracle proves it survived the migration — accounted for, none lost, no phantom rows):
///
///   JOB STATES (backwave.jobs)
///     - Scheduled (due in the past)              — live; drains to Succeeded after the upgrade.
///     - Leased mid-flight (lease already lapsed) — the "jobs in flight during upgrade" case; the drain
///                                                  reclaims and completes it.
///     - AwaitingParent (+ gating parent + edge)  — the Continuation latch must still fire post-upgrade.
///     - Dead-lettered (terminal, at the ceiling) — terminal; carried through untouched.
///     - Quarantined (terminal, unroutable wire)  — terminal; the wire is designated-unroutable so the
///                                                  end-state oracle's quarantine check holds.
///     - Succeeded / Cancelled (terminal)         — terminal; Cancelled carries a journal cancel so the
///                                                  cancel-provenance oracle stays satisfied.
///   DEPENDENCY EDGES (backwave.job_parents)      — the AwaitingParent gate.
///   TRANSITION HISTORY (backwave.job_transitions, v4+) — a self-consistent terminal history on each
///                                                  terminal fixture job, so the transition-log oracle
///                                                  has teeth across the migration (position auto-fills
///                                                  from the v5 sequence default).
///   RECURRING SCHEDULE (backwave.schedules)      — a cron template, proving the schedule table migrates.
///   QUEUE LIMITS + PAUSE (backwave.queue_limits) — a concurrency-limited queue and (v3+) a paused queue.
///   OPERATOR AUDIT (backwave.operator_audit, v3+)— an append-only audit record.
///   OBSERVER CURSOR (backwave.observers, v5+)    — a registered observer with a non-initial cursor.
///   TAGS (backwave.job_tags, v6+)                — tag rows on a fixture job; the tag-durability oracle
///                                                  confirms they survive the migration.
///   WORKFLOW GRAPH (backwave.workflows/_edges, v7+) — a workflow with members and immutable edges.
///
/// SQLite is intentionally excluded: it ships a single consolidated v1 schema with no vN-1 → vN step,
/// so there is nothing to upgrade in place yet. When SQLite gains its first migration, add it here.
/// </summary>
internal sealed class UpgradePopulator(IUpgradeStore store, KeySpace keys)
{
    private static readonly byte[] Payload = [0x0B, 0xAC, 0x40, 0xAE];

    // The set of fixture job ids the harness must find intact after the upgrade — the conservation
    // baseline. Seeded via the journal too, but tracked here for the optional sabotage deletion.
    public List<Guid> TerminalFixtureJobs { get; } = [];

    // Non-terminal fixtures the store will drive to terminal after the upgrade (claim/expire/latch).
    // Because they are inserted with raw SQL, they carry no birth transition; the store then logs its
    // first transition at ordinal 0 (MAX(ordinal)+1 over an empty log), which would read as an illegal
    // "born Leased/Scheduled-mid-life" to the transition-log oracle. SeedLiveBirthHistoryAsync gives
    // each a minimal legal birth once the log table exists (post-migration), modelling the pre-upgrade
    // history a real job carried — sound for every prior version, including v1..v3 that predate the log.
    private readonly List<LiveFixture> _liveFixtures = [];

    private sealed record LiveFixture(Guid JobId, int State, int Attempt);

    public async Task PopulateAsync(int priorVersion, Journal journal, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var g0 = keys.GeneralQueues[0];
        var g1 = keys.GeneralQueues[1 % keys.GeneralQueues.Count];

        // --- Live jobs (drain must carry these to a clean terminal state after the upgrade) ---------
        // The scheduled job carries the fixture tags (v6+); its enqueue is journaled once, with those
        // tags, so the tag-durability oracle expects exactly them and never sees a duplicate enqueue.
        var scheduledTags = priorVersion >= 6 ? new[] { "hot", "tenant=1" } : null;
        var scheduled = FixtureGuid(1);
        await InsertJobAsync(scheduled, keys.RoutableWires[0], g0, state: 0, dueTime: now.AddSeconds(-5),
            cancellationToken: cancellationToken);
        journal.Record(new JournalEntry
        {
            Client = "fixture", Op = Ops.Enqueue, T0 = now.UtcTicks, T1 = now.UtcTicks,
            JobId = scheduled, Queue = g0, Wire = keys.RoutableWires[0], Result = nameof(EnqueueResult.Ok),
            Tags = scheduledTags,
        });
        _liveFixtures.Add(new LiveFixture(scheduled, State: 0, Attempt: 0));

        var leased = FixtureGuid(2);
        await InsertJobAsync(leased, keys.RoutableWires[1 % keys.RoutableWires.Count], g1, state: 2,
            dueTime: now.AddSeconds(-30), attempt: 1, leaseOwner: "fixture-inflight",
            leaseExpiry: now.AddSeconds(-1), cancellationToken: cancellationToken);
        RecordEnqueue(journal, leased, g1, keys.RoutableWires[1 % keys.RoutableWires.Count], now);
        _liveFixtures.Add(new LiveFixture(leased, State: 2, Attempt: 1));

        // AwaitingParent: a gating parent (Scheduled, routable → drains to Succeeded) unlatches the child.
        var parent = FixtureGuid(3);
        var child = FixtureGuid(4);
        await InsertJobAsync(parent, keys.RoutableWires[0], g0, state: 0, dueTime: now.AddSeconds(-5),
            cancellationToken: cancellationToken);
        await InsertJobAsync(child, keys.RoutableWires[0], g0, state: 1, dueTime: now.AddSeconds(-5),
            parentsRemaining: 1, mode: (int)DependencyMode.OnSuccess, cancellationToken: cancellationToken);
        await store.ExecuteAsync(
            "INSERT INTO backwave.job_parents (parent_id, child_id) VALUES (@p0, @p1)",
            [parent, child], cancellationToken);
        RecordEnqueue(journal, parent, g0, keys.RoutableWires[0], now);
        RecordEnqueue(journal, child, g0, keys.RoutableWires[0], now, parents: [parent], mode: DependencyMode.OnSuccess);
        _liveFixtures.Add(new LiveFixture(parent, State: 0, Attempt: 0));
        _liveFixtures.Add(new LiveFixture(child, State: 1, Attempt: 0));

        // --- Terminal jobs (carried through the upgrade untouched) ----------------------------------
        var succeeded = FixtureGuid(5);
        await InsertJobAsync(succeeded, keys.RoutableWires[0], g0, state: 3, dueTime: now.AddSeconds(-60),
            attempt: 1, terminalAt: now.AddSeconds(-40), terminalCause: "succeeded", cancellationToken: cancellationToken);
        RecordEnqueue(journal, succeeded, g0, keys.RoutableWires[0], now);
        TerminalFixtureJobs.Add(succeeded);
        await InsertTerminalHistoryAsync(priorVersion, succeeded, finalState: 3, attempts: 1, now, cancellationToken);

        var deadLettered = FixtureGuid(6);
        await InsertJobAsync(deadLettered, keys.RoutableWires[0], g1, state: 5, dueTime: now.AddSeconds(-60),
            attempt: 4, terminalAt: now.AddSeconds(-30), terminalCause: "exhausted", cancellationToken: cancellationToken);
        RecordEnqueue(journal, deadLettered, g1, keys.RoutableWires[0], now);
        TerminalFixtureJobs.Add(deadLettered);
        await InsertTerminalHistoryAsync(priorVersion, deadLettered, finalState: 5, attempts: 4, now, cancellationToken);

        var quarantined = FixtureGuid(7);
        await InsertJobAsync(quarantined, keys.UnroutableWires[0], g0, state: 6, dueTime: now.AddSeconds(-60),
            attempt: 1, terminalAt: now.AddSeconds(-30), terminalCause: "unroutable", cancellationToken: cancellationToken);
        RecordEnqueue(journal, quarantined, g0, keys.UnroutableWires[0], now);
        TerminalFixtureJobs.Add(quarantined);
        await InsertTerminalHistoryAsync(priorVersion, quarantined, finalState: 6, attempts: 1, now, cancellationToken);

        var cancelled = FixtureGuid(8);
        await InsertJobAsync(cancelled, keys.RoutableWires[0], g1, state: 4, dueTime: now.AddSeconds(-60),
            terminalAt: now.AddSeconds(-30), terminalCause: "cancelled", cancellationToken: cancellationToken);
        RecordEnqueue(journal, cancelled, g1, keys.RoutableWires[0], now);
        TerminalFixtureJobs.Add(cancelled);
        await InsertTerminalHistoryAsync(priorVersion, cancelled, finalState: 4, attempts: 0, now, cancellationToken);
        // Cancel provenance: an explicit cancel request, so the cancel-provenance oracle is satisfied.
        journal.Record(new JournalEntry
        {
            Client = "fixture", Op = Ops.Cancel, T0 = now.UtcTicks, T1 = now.UtcTicks,
            JobId = cancelled, Result = nameof(CancelResult.CancelledImmediately),
        });

        // --- Schedules, queue limits, pauses, operator audit ----------------------------------------
        await store.ExecuteAsync(
            $"INSERT INTO backwave.schedules (schedule_id, cron, wire_name, payload, queue, {store.Quote("cursor")}) " +
            "VALUES (@p0, @p1, @p2, @p3, @p4, @p5)",
            ["fixture-schedule", "0 * * * * *", keys.RoutableWires[0], Payload, g0, now.AddMinutes(-1)],
            cancellationToken);

        await store.ExecuteAsync(
            "INSERT INTO backwave.queue_limits (queue, max_concurrent) VALUES (@p0, @p1)",
            [keys.ConfigQueues[0], 2], cancellationToken);
        if (priorVersion >= 3)
        {
            await store.ExecuteAsync(
                "INSERT INTO backwave.queue_limits (queue, max_concurrent, paused) VALUES (@p0, @p1, @p2)",
                [keys.ConfigQueues[1], null, true], cancellationToken);
            await store.ExecuteAsync(
                "INSERT INTO backwave.operator_audit (actor, action, target, recorded_at) VALUES (@p0, @p1, @p2, @p3)",
                ["fixture-operator", 3, keys.ConfigQueues[1], now.AddMinutes(-2)], cancellationToken);
        }

        // --- Observer cursor (v5+) ------------------------------------------------------------------
        if (priorVersion >= 5)
        {
            await store.ExecuteAsync(
                "INSERT INTO backwave.observers (observer_id, cursor_pos, sub_states) VALUES (@p0, @p1, @p2)",
                ["fixture-observer", 3L, ""], cancellationToken);
        }

        // --- Tags (v6+): recorded in the journal so tag-durability proves they survive the migration -
        if (priorVersion >= 6)
        {
            await store.ExecuteAsync(
                $"INSERT INTO backwave.job_tags (job_id, {store.Quote("key")}, {store.Quote("value")}) VALUES (@p0, @p1, @p2)",
                [scheduled, "", "hot"], cancellationToken);
            await store.ExecuteAsync(
                $"INSERT INTO backwave.job_tags (job_id, {store.Quote("key")}, {store.Quote("value")}) VALUES (@p0, @p1, @p2)",
                [scheduled, "tenant", "1"], cancellationToken);
        }

        // --- Workflow graph (v7+) -------------------------------------------------------------------
        if (priorVersion >= 7)
        {
            var workflowId = FixtureGuid(20);
            var m1 = FixtureGuid(21);
            var m2 = FixtureGuid(22);
            await store.ExecuteAsync(
                "INSERT INTO backwave.workflows (workflow_id, name, created_at) VALUES (@p0, @p1, @p2)",
                [workflowId, "fixture-workflow", now.AddMinutes(-3)], cancellationToken);
            await InsertJobAsync(m1, keys.RoutableWires[0], g0, state: 3, dueTime: now.AddSeconds(-60),
                attempt: 1, terminalAt: now.AddSeconds(-40), terminalCause: "succeeded", workflowId: workflowId,
                cancellationToken: cancellationToken);
            await InsertJobAsync(m2, keys.RoutableWires[0], g0, state: 0, dueTime: now.AddSeconds(-5),
                workflowId: workflowId, cancellationToken: cancellationToken);
            await store.ExecuteAsync(
                "INSERT INTO backwave.workflow_edges (workflow_id, parent_id, child_id) VALUES (@p0, @p1, @p2)",
                [workflowId, m1, m2], cancellationToken);
            RecordEnqueue(journal, m1, g0, keys.RoutableWires[0], now, workflowId: workflowId);
            RecordEnqueue(journal, m2, g0, keys.RoutableWires[0], now, workflowId: workflowId);
            _liveFixtures.Add(new LiveFixture(m2, State: 0, Attempt: 0));
            TerminalFixtureJobs.Add(m1);
            await InsertTerminalHistoryAsync(priorVersion, m1, finalState: 3, attempts: 1, now, cancellationToken);
        }
    }

    // Inserts one job row using only the v1 columns, plus workflow_id when the schema carries it (v7+).
    private async Task InsertJobAsync(
        Guid jobId, string wire, string queue, int state, DateTimeOffset dueTime, int attempt = 0,
        string? leaseOwner = null, DateTimeOffset? leaseExpiry = null, DateTimeOffset? terminalAt = null,
        string? terminalCause = null, int parentsRemaining = 0, int mode = 0, Guid? workflowId = null,
        CancellationToken cancellationToken = default)
    {
        var columns =
            "job_id, wire_name, payload, queue, state, due_time, attempt, lease_owner, lease_expiry, " +
            "cancel_requested, terminal_at, terminal_cause, schedule_id, parents_remaining, mode";
        var values = new List<object?>
        {
            jobId, wire, Payload, queue, state, dueTime, attempt, leaseOwner, leaseExpiry,
            false, terminalAt, terminalCause, null, parentsRemaining, mode,
        };
        if (workflowId is not null)
        {
            columns += ", workflow_id";
            values.Add(workflowId);
        }
        var placeholders = string.Join(", ", Enumerable.Range(0, values.Count).Select(i => $"@p{i}"));
        await store.ExecuteAsync(
            $"INSERT INTO backwave.jobs ({columns}) VALUES ({placeholders})", [.. values], cancellationToken);
    }

    // A self-consistent terminal transition history: Scheduled(0) → Leased(1) → <final>. Only when the
    // transition log exists (v4+). Position is omitted so the v5 sequence default fills it. Ordinals
    // are consecutive from 0 and the tail state matches the row, so the transition-log oracle passes.
    private async Task InsertTerminalHistoryAsync(
        int priorVersion, Guid jobId, int finalState, int attempts, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (priorVersion < 4)
        {
            return;
        }

        var rows = new List<(int Ordinal, int State, int Attempt)> { (0, 0, 0) }; // born Scheduled
        if (finalState == 4)
        {
            rows.Add((1, 4, 0)); // Scheduled → Cancelled (never leased)
        }
        else
        {
            rows.Add((1, 2, Math.Max(1, attempts))); // Scheduled → Leased
            rows.Add((2, finalState, Math.Max(1, attempts))); // Leased → terminal
        }

        foreach (var (ordinal, state, attempt) in rows)
        {
            await InsertTransitionAsync(jobId, ordinal, state, attempt, now.AddSeconds(-50 + ordinal), cancellationToken);
        }
    }

    /// <summary>
    /// Gives every non-terminal fixture a minimal, legal BIRTH transition history, keyed to its current
    /// state, so the store's own subsequent transitions (claim/expire/latch) continue at the right
    /// ordinal and the transition-log oracle reads a legal life. Call this ONCE, after the migration
    /// (the log table always exists then) and before the workload/drain touch any job.
    /// </summary>
    public async Task SeedLiveBirthHistoryAsync(CancellationToken cancellationToken)
    {
        var born = DateTimeOffset.UtcNow.AddSeconds(-45);
        foreach (var fixture in _liveFixtures)
        {
            switch (fixture.State)
            {
                case 0: // Scheduled: born Scheduled; the store's first op (claim) becomes ordinal 1.
                    await InsertTransitionAsync(fixture.JobId, 0, 0, 0, born, cancellationToken);
                    break;
                case 1: // AwaitingParent: born AwaitingParent; the latch's Scheduled becomes ordinal 1.
                    await InsertTransitionAsync(fixture.JobId, 0, 1, 0, born, cancellationToken);
                    break;
                case 2: // Leased mid-flight: Scheduled(0) → Leased(1); the expire's Scheduled becomes ordinal 2.
                    await InsertTransitionAsync(fixture.JobId, 0, 0, 0, born, cancellationToken);
                    await InsertTransitionAsync(fixture.JobId, 1, 2, fixture.Attempt, born.AddSeconds(1), cancellationToken);
                    break;
            }
        }
    }

    // One transition row. Position (v5+) is omitted so the schema default fills it from the sequence.
    private Task InsertTransitionAsync(
        Guid jobId, int ordinal, int state, int attempt, DateTimeOffset recordedAt, CancellationToken cancellationToken)
        => store.ExecuteAsync(
            "INSERT INTO backwave.job_transitions (job_id, ordinal, recorded_at, state, attempt, failure_detail) " +
            "VALUES (@p0, @p1, @p2, @p3, @p4, @p5)",
            [jobId, (long)ordinal, recordedAt, state, attempt, null],
            cancellationToken).AsTask();

    private static void RecordEnqueue(
        Journal journal, Guid jobId, string queue, string wire, DateTimeOffset now,
        Guid[]? parents = null, DependencyMode? mode = null, Guid? workflowId = null)
        => journal.Record(new JournalEntry
        {
            Client = "fixture", Op = Ops.Enqueue, T0 = now.UtcTicks, T1 = now.UtcTicks,
            JobId = jobId, Queue = queue, Wire = wire, WorkflowId = workflowId, Result = nameof(EnqueueResult.Ok),
            Parents = parents, Mode = mode?.ToString(),
        });

    // Deterministic per-run fixture ids, disjoint from the KeySpace collision streams the live workload uses.
    private Guid FixtureGuid(int index)
    {
        var bytes = new byte[16];
        var a = SplitMix64.Next(keys.Seed ^ 0xF1C_0000UL ^ ((ulong)index * 0x9E3779B97F4A7C15UL));
        var b = SplitMix64.Next(a);
        BitConverter.TryWriteBytes(bytes.AsSpan(0, 8), a);
        BitConverter.TryWriteBytes(bytes.AsSpan(8, 8), b);
        return new Guid(bytes);
    }
}
