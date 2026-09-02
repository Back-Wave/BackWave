using BackWave.Storage;

namespace BackWave.Torture;

/// <summary>One Transition Log row, reduced to what the edge checks read.</summary>
internal readonly record struct TransitionFacts(long Ordinal, JobState State, int Attempt);

/// <summary>
/// One job row, reduced to what the row checks read: built from a <see cref="JobRecord"/> post-drain
/// and from the mid-run pass's joined read while the workload is live. TerminalAt is the rendered
/// instant, or null when the row carries none.
/// </summary>
internal readonly record struct JobRowFacts(
    Guid JobId,
    JobState State,
    int Attempt,
    string WireName,
    string? LeaseOwner,
    bool HasLeaseExpiry,
    string? TerminalAt);

/// <summary>
/// One row of the change feed the mid-run pass walks: a Transition Log row joined to its job row,
/// both from a single statement and therefore a single snapshot.
/// </summary>
internal sealed record AuditRow
{
    public required long Position { get; init; }
    public required Guid JobId { get; init; }
    public required long Ordinal { get; init; }
    public required JobState State { get; init; }
    public required int Attempt { get; init; }
    public required JobRowFacts Job { get; init; }

    public TransitionFacts Transition => new(Ordinal, State, Attempt);
}

/// <summary>
/// The check bodies shared by the post-drain <see cref="Auditor"/> and the mid-run cursor pass. A
/// fork would let the two drift, and a drift means the mid-run pass quietly stops matching the real
/// audit, so every check lives here exactly once and each caller feeds it the rows it has.
/// </summary>
internal static class Checks
{
    private static readonly IReadOnlySet<(JobState From, JobState To)> LegalEdges = new HashSet<(JobState, JobState)>
    {
        (JobState.AwaitingParent, JobState.Scheduled),
        (JobState.AwaitingParent, JobState.Cancelled),
        (JobState.Scheduled, JobState.Leased),
        (JobState.Scheduled, JobState.Cancelled),
        (JobState.Leased, JobState.Succeeded),
        (JobState.Leased, JobState.Scheduled),
        (JobState.Leased, JobState.DeadLettered),
        (JobState.Leased, JobState.Cancelled),
        (JobState.Leased, JobState.Quarantined),
        (JobState.DeadLettered, JobState.Scheduled),
        (JobState.Quarantined, JobState.Scheduled),
    };

    /// <summary>LegalInitialState: the ordinal-0 Transition is the birth state.</summary>
    public static void InitialTransition(Guid jobId, TransitionFacts first, ViolationSink sink)
    {
        if (first.Ordinal == 0
            && first.State is not (JobState.Scheduled or JobState.AwaitingParent or JobState.Cancelled))
        {
            sink.Add(new TortureViolation(
                TortureInvariant.LegalInitialState, $"Job {jobId} was born {first.State}.", jobId));
        }
    }

    /// <summary>
    /// LegalTransition / AttemptMonotonic / AttemptCeiling across one adjacent Transition pair. Only
    /// adjacent ordinals are a real edge; a gap is an aged-out (or, mid-run, a not-yet-visible) row.
    /// </summary>
    public static void TransitionEdge(
        Guid jobId, TransitionFacts prev, TransitionFacts next, int maxAttempts, ViolationSink sink)
    {
        if (next.Ordinal != prev.Ordinal + 1)
        {
            return;
        }
        if (!LegalEdges.Contains((prev.State, next.State)))
        {
            sink.Add(new TortureViolation(
                TortureInvariant.LegalTransition,
                $"Job {jobId} transitioned {prev.State} → {next.State} (ordinals {prev.Ordinal}→{next.Ordinal}).",
                jobId));
        }
        var requeueReset = prev.State is JobState.DeadLettered or JobState.Quarantined
            && next.State == JobState.Scheduled && next.Attempt == 0;
        if (next.Attempt < prev.Attempt && !requeueReset)
        {
            sink.Add(new TortureViolation(
                TortureInvariant.AttemptMonotonic,
                $"Job {jobId} attempt went {prev.Attempt} → {next.Attempt} on {prev.State} → {next.State}.",
                jobId));
        }
        if (next.Attempt > maxAttempts)
        {
            sink.Add(new TortureViolation(
                TortureInvariant.AttemptCeiling,
                $"Job {jobId} recorded attempt {next.Attempt} above the ceiling {maxAttempts}.", jobId));
        }
    }

    /// <summary>
    /// The per-row store checks: TerminalTimestamp, LeaseOwnerPresent/Cleared, AttemptCeiling, and the
    /// store half of QuarantineNotExecuted. Each reads one atomic row, so it is a valid instantaneous
    /// observation whether the workload is running or long stopped.
    /// </summary>
    public static void JobRow(JobRowFacts job, KeySpace keys, TortureOptions options, ViolationSink sink)
    {
        var terminal = JobStates.IsTerminal(job.State);

        if (terminal && job.TerminalAt is null)
        {
            sink.Add(new TortureViolation(
                TortureInvariant.TerminalTimestamp,
                $"Job {job.JobId} is terminal {job.State} with no TerminalAt.", job.JobId));
        }
        if (!terminal && job.TerminalAt is not null)
        {
            sink.Add(new TortureViolation(
                TortureInvariant.TerminalTimestamp,
                $"Job {job.JobId} is live {job.State} but carries TerminalAt {job.TerminalAt}.", job.JobId));
        }

        if (job.State == JobState.Leased)
        {
            if (job.LeaseOwner is null || !job.HasLeaseExpiry)
            {
                sink.Add(new TortureViolation(
                    TortureInvariant.LeaseOwnerPresent,
                    $"Job {job.JobId} is Leased with owner '{job.LeaseOwner ?? "null"}' / " +
                    $"expiry '{(job.HasLeaseExpiry ? "set" : "null")}'.", job.JobId));
            }
        }
        else if (job.LeaseOwner is not null)
        {
            sink.Add(new TortureViolation(
                TortureInvariant.LeaseOwnerCleared,
                $"Job {job.JobId} is {job.State} but still names lease owner '{job.LeaseOwner}'.", job.JobId));
        }

        if (job.Attempt > options.MaxAttempts)
        {
            sink.Add(new TortureViolation(
                TortureInvariant.AttemptCeiling,
                $"Job {job.JobId} ended at attempt {job.Attempt}, above the ceiling {options.MaxAttempts}.", job.JobId));
        }

        if (job.State == JobState.Quarantined && !keys.IsUnroutable(job.WireName))
        {
            sink.Add(new TortureViolation(
                TortureInvariant.QuarantineNotExecuted,
                $"Job {job.JobId} is Quarantined but its wire '{job.WireName}' is routable — no client ever reports " +
                "Unroutable for a routable wire.", job.JobId));
        }
    }

    /// <summary>
    /// The Degrade half of the fail-stop vocabulary: counted, not thrown. <see cref="DegradeWatch"/>
    /// subscribes to <c>backwave.invariant.violations</c> and journals each one, because that counter is the
    /// only surface a degraded trigger reaches. Grouped by trigger id for the same reason the halt check is -
    /// a finding must name the invariant that broke, not merely say that one did.
    ///
    /// Deliberately NOT part of <see cref="LiveJournal"/>: a run calls this once, over the whole journal, at
    /// the very end. Folding it in would report the same degrade twice (the mid-run pass and the post-drain
    /// audit both run LiveJournal over their own prefix) and would still miss the two windows that matter -
    /// a run cut short by a mid-run finding never audits, and the audit's view is pinned before the drain, so
    /// a trigger degraded BY the drain or the audit falls outside it.
    /// </summary>
    public static void DegradedTriggers(IReadOnlyList<JournalEntry> journal, ViolationSink sink)
    {
        foreach (var group in journal.Where(e => e.Op == Ops.InvariantDegrade).GroupBy(e => e.Result))
        {
            sink.Add(new TortureViolation(
                TortureInvariant.DegradeTriggerFired,
                $"Degrade trigger {group.Key} fired {group.Count()} time(s) - a production worker group would " +
                "have counted it and stayed in service, but the state it names must not have been reachable."));
        }
    }

    /// <summary>
    /// The journal-only checks. They read nothing but the journal, and the journal only ever grows,
    /// so a verdict taken over a prefix stays true - which is what makes them safe to run mid-run.
    /// The checks that compare the journal against separately-read store state are NOT here; they
    /// stay in the post-drain audit.
    /// </summary>
    public static void LiveJournal(IReadOnlyList<JournalEntry> journal, ViolationSink sink)
    {
        var claims = journal.Where(e => e.Op == Ops.Claim && e is { JobId: not null, Attempt: not null }).ToList();
        var appliedOutcomes = journal
            .Where(e => e.Op == Ops.Outcome && e.Result == nameof(OutcomeResult.Applied) && e is { JobId: not null, Attempt: not null })
            .ToList();

        // Raw provider exceptions and crashed clients are findings in themselves.
        foreach (var group in journal.Where(e => e.Op == Ops.UnexpectedException).GroupBy(e => $"{e.Result}: {e.Detail}"))
        {
            sink.Add(new TortureViolation(
                TortureInvariant.RawStoreException,
                $"{group.Count()}× unexpected exception escaped the store surface during '{group.Key}'."));
        }
        // A production fail-stop trigger: the adapter observed a state its own invariants forbid, which
        // in production halts the worker group. Grouped by trigger id so the finding names the invariant.
        foreach (var group in journal.Where(e => e.Op == Ops.InvariantViolation).GroupBy(e => e.Result))
        {
            sink.Add(new TortureViolation(
                TortureInvariant.HaltTriggerFired,
                $"Halt trigger {group.Key} fired {group.Count()} time(s) - a production worker group would have " +
                $"fail-stopped: {group.First().Detail}"));
        }
        foreach (var crash in journal.Where(e => e.Op == Ops.ClientCrash))
        {
            sink.Add(new TortureViolation(
                TortureInvariant.ClientCrash, $"Client {crash.Client}: {crash.Detail}"));
        }

        // At most one accepted enqueue per JobId, ever (ids are never purged during a run).
        foreach (var group in journal
            .Where(e => e.Op == Ops.Enqueue && e.Result == nameof(EnqueueResult.Ok) && e.JobId is not null)
            .GroupBy(e => e.JobId!.Value)
            .Where(g => g.Count() > 1))
        {
            sink.Add(new TortureViolation(
                TortureInvariant.DuplicateEnqueueAccepted,
                $"JobId {group.Key} was accepted (Ok) by {group.Count()} enqueues: " +
                $"{string.Join(", ", group.Select(e => e.Client))}.", group.Key));
        }

        foreach (var group in journal
            .Where(e => e.Op == Ops.Workflow && e.Result == nameof(WorkflowEnqueueResult.Ok) && e.Detail == "create")
            .GroupBy(e => e.WorkflowId))
        {
            if (group.Count() > 1)
            {
                sink.Add(new TortureViolation(
                    TortureInvariant.DuplicateWorkflowAccepted,
                    $"WorkflowId {group.Key} was created (Ok) {group.Count()} times."));
            }
        }

        // A Requeue resets the attempt counter to 0, so a requeued job legitimately re-runs the same
        // attempt numbers — one extra life per successful requeue. The claim/report Effect-Once
        // checks therefore allow (1 + requeues) occurrences per (job, attempt), not 1.
        var requeueLives = journal
            .Where(e => e.Op == Ops.Requeue && e.Result == nameof(RequeueResult.Requeued) && e.JobId is not null)
            .GroupBy(e => e.JobId!.Value)
            .ToDictionary(g => g.Key, g => g.Count());

        // NoDoubleExecution: a claim hands an attempt to exactly one worker, once per life.
        foreach (var group in claims.GroupBy(e => (e.JobId!.Value, e.Attempt!.Value)))
        {
            var allowed = 1 + requeueLives.GetValueOrDefault(group.Key.Item1);
            if (group.Count() > allowed)
            {
                sink.Add(new TortureViolation(
                    TortureInvariant.NoDoubleExecution,
                    $"Job {group.Key.Item1} attempt {group.Key.Item2} was claimed {group.Count()} times " +
                    $"(by {string.Join(", ", group.Select(e => e.Client))}) with only {allowed} life/lives.",
                    group.Key.Item1));
            }
        }

        // Effect-Once on the report: at most one Applied outcome per (job, attempt) per life.
        foreach (var group in appliedOutcomes.GroupBy(e => (e.JobId!.Value, e.Attempt!.Value)))
        {
            var allowed = 1 + requeueLives.GetValueOrDefault(group.Key.Item1);
            if (group.Count() > allowed)
            {
                sink.Add(new TortureViolation(
                    TortureInvariant.SlotDoubleRelease,
                    $"Job {group.Key.Item1} attempt {group.Key.Item2} had {group.Count()} Applied outcomes " +
                    $"with only {allowed} life/lives.", group.Key.Item1));
            }
        }

        // Fence supersession: an outcome for attempt a must not apply after attempt a' > a was
        // already handed out (the later claim's return proves attempt a's lease was gone). Keyed by
        // job, because the mid-run pass re-runs this and a flat (job, attempt) map would make the
        // lookup a scan of every claim group in the journal.
        var claimReturns = claims
            .GroupBy(e => e.JobId!.Value)
            .ToDictionary(
                g => g.Key,
                g => g.GroupBy(e => e.Attempt!.Value)
                    .Select(a => (Attempt: a.Key, Returned: a.Min(e => e.T1)))
                    .ToList());
        foreach (var outcome in appliedOutcomes)
        {
            if (requeueLives.ContainsKey(outcome.JobId!.Value))
            {
                continue; // lives interleave attempt numbers; the cross-life ordering is not checkable
            }
            if (!claimReturns.TryGetValue(outcome.JobId!.Value, out var perAttempt))
            {
                continue;
            }
            foreach (var (attempt, returned) in perAttempt)
            {
                if (attempt > outcome.Attempt!.Value && returned < outcome.T0)
                {
                    sink.Add(new TortureViolation(
                        TortureInvariant.OutcomeProvenance,
                        $"Job {outcome.JobId} attempt {outcome.Attempt} outcome APPLIED although attempt " +
                        $"{attempt} had already been claimed before the report began — the fence let a stale " +
                        "writer through.", outcome.JobId));
                    break;
                }
            }
        }
    }
}
