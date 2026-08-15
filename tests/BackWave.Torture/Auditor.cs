using BackWave.Storage;

namespace BackWave.Torture;

/// <summary>
/// The quiescent oracle audit (issue 0200): after the workload stops and the drain converges, this
/// walks the store's end state plus every job's Transition Log, and cross-checks them against the
/// merged client observation journals. Every check is sound under wall-clock nondeterminism — the
/// journal checks use only conservative windows (a lease was *definitely* live between the claim's
/// return and the outcome call's start), so a torture failure is always a bug, never noise.
/// </summary>
internal sealed class Auditor(IJobStore store, KeySpace keys, TortureOptions options)
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

    public List<JobRecord> ScannedJobs { get; } = [];

    public Dictionary<Guid, IReadOnlyList<JobTransition>> Histories { get; } = [];

    public async Task<List<TortureViolation>> AuditAsync(
        IReadOnlyList<JournalEntry> journal, CancellationToken cancellationToken)
    {
        var violations = new List<TortureViolation>();

        await ScanAsync(cancellationToken);
        var jobsById = ScannedJobs.ToDictionary(j => j.JobId);

        AuditTransitionLogs(violations);
        AuditEndState(violations);
        await AuditAwaitingParentsAsync(violations, jobsById, cancellationToken);
        AuditJournal(violations, journal, jobsById);

        return violations;
    }

    private async Task ScanAsync(CancellationToken cancellationToken)
    {
        long? after = null;
        while (true)
        {
            var page = await store.ListJobsAsync(new JobQuery
            {
                AfterSequence = after,
                SortDirection = JobSortDirection.OldestFirst,
                MaxResults = 200,
            }, cancellationToken);
            ScannedJobs.AddRange(page);
            if (page.Count < 200)
            {
                break;
            }
            after = page[^1].Sequence;
        }
        foreach (var job in ScannedJobs)
        {
            Histories[job.JobId] = await store.GetJobHistoryAsync(job.JobId, cancellationToken);
        }
    }

    // ---- Transition Log audit -------------------------------------------------------------------

    private void AuditTransitionLogs(List<TortureViolation> violations)
    {
        foreach (var job in ScannedJobs)
        {
            var history = Histories[job.JobId];
            if (history.Count == 0)
            {
                continue;
            }

            if (history[0].Ordinal == 0
                && history[0].State is not (JobState.Scheduled or JobState.AwaitingParent or JobState.Cancelled))
            {
                violations.Add(new TortureViolation(
                    TortureInvariant.LegalInitialState,
                    $"Job {job.JobId} was born {history[0].State}.", job.JobId));
            }

            for (var i = 1; i < history.Count; i++)
            {
                var prev = history[i - 1];
                var next = history[i];
                if (next.Ordinal != prev.Ordinal + 1)
                {
                    continue; // aged-out gap — not a real edge
                }
                if (!LegalEdges.Contains((prev.State, next.State)))
                {
                    violations.Add(new TortureViolation(
                        TortureInvariant.LegalTransition,
                        $"Job {job.JobId} transitioned {prev.State} → {next.State} (ordinals {prev.Ordinal}→{next.Ordinal}).",
                        job.JobId));
                }
                var requeueReset = prev.State is JobState.DeadLettered or JobState.Quarantined
                    && next.State == JobState.Scheduled && next.Attempt == 0;
                if (next.Attempt < prev.Attempt && !requeueReset)
                {
                    violations.Add(new TortureViolation(
                        TortureInvariant.AttemptMonotonic,
                        $"Job {job.JobId} attempt went {prev.Attempt} → {next.Attempt} on {prev.State} → {next.State}.",
                        job.JobId));
                }
                if (next.Attempt > options.MaxAttempts)
                {
                    violations.Add(new TortureViolation(
                        TortureInvariant.AttemptCeiling,
                        $"Job {job.JobId} recorded attempt {next.Attempt} above the ceiling {options.MaxAttempts}.",
                        job.JobId));
                }
            }

            if (history[^1].State != job.State)
            {
                violations.Add(new TortureViolation(
                    TortureInvariant.TerminalStable,
                    $"Job {job.JobId} is {job.State} but its last recorded transition is {history[^1].State} — " +
                    "the log tail and the row disagree.", job.JobId));
            }
        }
    }

    // ---- End-state audit ------------------------------------------------------------------------

    private void AuditEndState(List<TortureViolation> violations)
    {
        foreach (var job in ScannedJobs)
        {
            var terminal = JobStates.IsTerminal(job.State);

            if (terminal && job.TerminalAt is null)
            {
                violations.Add(new TortureViolation(
                    TortureInvariant.TerminalTimestamp,
                    $"Job {job.JobId} is terminal {job.State} with no TerminalAt.", job.JobId));
            }
            if (!terminal && job.TerminalAt is not null)
            {
                violations.Add(new TortureViolation(
                    TortureInvariant.TerminalTimestamp,
                    $"Job {job.JobId} is live {job.State} but carries TerminalAt {job.TerminalAt:O}.", job.JobId));
            }

            if (job.State == JobState.Leased)
            {
                if (job.LeaseOwner is null || job.LeaseExpiry is null)
                {
                    violations.Add(new TortureViolation(
                        TortureInvariant.LeaseOwnerPresent,
                        $"Job {job.JobId} is Leased with owner '{job.LeaseOwner ?? "null"}' / expiry '{job.LeaseExpiry?.ToString("O") ?? "null"}'.",
                        job.JobId));
                }
            }
            else if (job.LeaseOwner is not null)
            {
                violations.Add(new TortureViolation(
                    TortureInvariant.LeaseOwnerCleared,
                    $"Job {job.JobId} is {job.State} but still names lease owner '{job.LeaseOwner}'.", job.JobId));
            }

            if (job.Attempt > options.MaxAttempts)
            {
                violations.Add(new TortureViolation(
                    TortureInvariant.AttemptCeiling,
                    $"Job {job.JobId} ended at attempt {job.Attempt}, above the ceiling {options.MaxAttempts}.", job.JobId));
            }

            if (job.State == JobState.Quarantined && !keys.IsUnroutable(job.WireName))
            {
                violations.Add(new TortureViolation(
                    TortureInvariant.QuarantineNotExecuted,
                    $"Job {job.JobId} is Quarantined but its wire '{job.WireName}' is routable — no client ever reports " +
                    "Unroutable for a routable wire.", job.JobId));
            }
        }
    }

    private async Task AuditAwaitingParentsAsync(
        List<TortureViolation> violations, Dictionary<Guid, JobRecord> jobsById, CancellationToken cancellationToken)
    {
        foreach (var job in ScannedJobs.Where(j => j.State == JobState.AwaitingParent))
        {
            var edges = await store.GetDependencyEdgesAsync(job.JobId, cancellationToken);
            var parents = edges.GatingParents;
            if (parents.Count == 0)
            {
                violations.Add(new TortureViolation(
                    TortureInvariant.NoAwaitingParentOrphan,
                    $"Job {job.JobId} is AwaitingParent with no gating parents.", job.JobId));
                continue;
            }
            var parentStates = parents
                .Select(p => jobsById.TryGetValue(p, out var parent) ? parent.State : (JobState?)null)
                .ToList();
            if (parentStates.All(s => s is { } state && JobStates.IsTerminal(state)))
            {
                violations.Add(new TortureViolation(
                    TortureInvariant.NoAwaitingParentOrphan,
                    $"Job {job.JobId} is AwaitingParent but every gating parent is terminal " +
                    $"({string.Join(", ", parentStates)}) — the latch never fired.", job.JobId));
            }
        }
    }

    // ---- Journal cross-check ----------------------------------------------------------------------

    private void AuditJournal(
        List<TortureViolation> violations, IReadOnlyList<JournalEntry> journal, Dictionary<Guid, JobRecord> jobsById)
    {
        var enqueueOks = journal
            .Where(e => e.Op == Ops.Enqueue && e.Result == nameof(EnqueueResult.Ok) && e.JobId is { } id)
            .ToLookup(e => e.JobId!.Value);
        var claims = journal.Where(e => e.Op == Ops.Claim && e is { JobId: not null, Attempt: not null }).ToList();
        var appliedOutcomes = journal
            .Where(e => e.Op == Ops.Outcome && e.Result == nameof(OutcomeResult.Applied) && e is { JobId: not null, Attempt: not null })
            .ToList();

        // Raw provider exceptions and crashed clients are findings in themselves.
        foreach (var group in journal.Where(e => e.Op == Ops.UnexpectedException).GroupBy(e => $"{e.Result}: {e.Detail}"))
        {
            violations.Add(new TortureViolation(
                TortureInvariant.RawStoreException,
                $"{group.Count()}× unexpected exception escaped the store surface during '{group.Key}'."));
        }
        foreach (var crash in journal.Where(e => e.Op == Ops.ClientCrash))
        {
            violations.Add(new TortureViolation(
                TortureInvariant.ClientCrash, $"Client {crash.Client}: {crash.Detail}"));
        }

        // At most one accepted enqueue per JobId, ever (ids are never purged during a run).
        foreach (var group in enqueueOks.Where(g => g.Count() > 1))
        {
            violations.Add(new TortureViolation(
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
                violations.Add(new TortureViolation(
                    TortureInvariant.DuplicateWorkflowAccepted,
                    $"WorkflowId {group.Key} was created (Ok) {group.Count()} times."));
            }
        }

        // Every accepted enqueue must still be visible at quiescence (nothing purges during a run) —
        // and the reverse: every job in the store must trace to an accepted enqueue. A phantom row
        // means an enqueue that reported failure still committed.
        foreach (var group in enqueueOks)
        {
            if (!jobsById.ContainsKey(group.Key))
            {
                violations.Add(new TortureViolation(
                    TortureInvariant.EnqueueDurability,
                    $"Job {group.Key} was accepted (Ok) but is gone from the store.", group.Key));
            }
        }
        foreach (var job in ScannedJobs.Where(j => !enqueueOks.Contains(j.JobId)))
        {
            violations.Add(new TortureViolation(
                TortureInvariant.EnqueueDurability,
                $"Job {job.JobId} exists in the store but no client's enqueue was accepted for it.", job.JobId));
        }

        // A Requeue resets the attempt counter to 0, so a requeued job legitimately re-runs the same
        // attempt numbers — one extra life per successful requeue. The claim/report Effect-Once
        // checks therefore allow (1 + requeues) occurrences per (job, attempt), not 1.
        var requeueLives = journal
            .Where(e => e.Op == Ops.Requeue && e.Result == nameof(RequeueResult.Requeued) && e.JobId is { } id)
            .GroupBy(e => e.JobId!.Value)
            .ToDictionary(g => g.Key, g => g.Count());

        // NoDoubleExecution: a claim hands an attempt to exactly one worker, once per life.
        foreach (var group in claims.GroupBy(e => (e.JobId!.Value, e.Attempt!.Value)))
        {
            var allowed = 1 + requeueLives.GetValueOrDefault(group.Key.Item1);
            if (group.Count() > allowed)
            {
                violations.Add(new TortureViolation(
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
                violations.Add(new TortureViolation(
                    TortureInvariant.SlotDoubleRelease,
                    $"Job {group.Key.Item1} attempt {group.Key.Item2} had {group.Count()} Applied outcomes " +
                    $"with only {allowed} life/lives.", group.Key.Item1));
            }
        }

        // Fence supersession: an outcome for attempt a must not apply after attempt a' > a was
        // already handed out (the later claim's return proves attempt a's lease was gone).
        var claimReturnByJobAttempt = claims
            .GroupBy(e => (e.JobId!.Value, e.Attempt!.Value))
            .ToDictionary(g => g.Key, g => g.Min(e => e.T1));
        foreach (var outcome in appliedOutcomes)
        {
            if (requeueLives.ContainsKey(outcome.JobId!.Value))
            {
                continue; // lives interleave attempt numbers; the cross-life ordering is not checkable
            }
            var laterClaim = claimReturnByJobAttempt
                .Where(kv => kv.Key.Item1 == outcome.JobId!.Value
                    && kv.Key.Item2 > outcome.Attempt!.Value
                    && kv.Value < outcome.T0)
                .Select(kv => (KeyValuePair<(Guid, int), long>?)kv)
                .FirstOrDefault();
            if (laterClaim is { } later)
            {
                violations.Add(new TortureViolation(
                    TortureInvariant.OutcomeProvenance,
                    $"Job {outcome.JobId} attempt {outcome.Attempt} outcome APPLIED although attempt " +
                    $"{later.Key.Item2} had already been claimed before the report began — the fence let a stale " +
                    "writer through.", outcome.JobId));
            }
        }

        // Conservative lease intervals: [claim return, min(first outcome call start, lease expiry,
        // any renewed-heartbeat expiry)]. A renewal can move the expiry EARLIER than the claim set
        // it, so heartbeat-set expiries must cap the "definitely live until" bound too.
        var firstOutcomeStart = journal
            .Where(e => e.Op == Ops.Outcome && e is { JobId: not null, Attempt: not null })
            .GroupBy(e => (e.JobId!.Value, e.Attempt!.Value))
            .ToDictionary(g => g.Key, g => g.Min(e => e.T0));
        var renewals = journal
            .Where(e => e.Op == Ops.Heartbeat && e.Result == "Renewed" && e is { JobId: not null, LeaseExpiry: not null })
            .ToLookup(e => e.JobId!.Value);
        var intervals = claims
            .Select(claim =>
            {
                var key = (claim.JobId!.Value, claim.Attempt!.Value);
                var end = claim.LeaseExpiry ?? claim.T1;
                if (firstOutcomeStart.TryGetValue(key, out var outcomeStart))
                {
                    end = Math.Min(end, outcomeStart);
                }
                // Renewals are attributed by time containment (a stray heartbeat carries no attempt):
                // one that lands inside this window renewed THIS lease and re-set its expiry.
                foreach (var renewal in renewals[claim.JobId!.Value])
                {
                    if (renewal.T0 >= claim.T1 && renewal.T0 < end)
                    {
                        end = Math.Min(end, renewal.LeaseExpiry!.Value);
                    }
                }
                return (claim.JobId!.Value, Attempt: claim.Attempt!.Value, Queue: claim.Queue, Start: claim.T1, End: end);
            })
            .Where(i => i.End > i.Start)
            .ToList();

        // NoOverlap: one job, two definitely-live leases at once.
        foreach (var group in intervals.GroupBy(i => i.Item1))
        {
            var ordered = group.OrderBy(i => i.Start).ToList();
            for (var i = 1; i < ordered.Count; i++)
            {
                if (ordered[i].Start < ordered[i - 1].End)
                {
                    violations.Add(new TortureViolation(
                        TortureInvariant.NoOverlap,
                        $"Job {group.Key}: leases for attempts {ordered[i - 1].Attempt} and {ordered[i].Attempt} were " +
                        "definitely live simultaneously.", group.Key));
                }
            }
        }

        // I3 on the governed queue: its limit is fixed for the whole run, so if more definitely-live
        // leases than the limit ever coexist, the store over-admitted.
        var events = new List<(long At, int Delta)>();
        foreach (var interval in intervals.Where(i => i.Queue == keys.GovernedQueue))
        {
            events.Add((interval.Start, +1));
            events.Add((interval.End, -1));
        }
        var live = 0;
        foreach (var (at, delta) in events.OrderBy(e => e.At).ThenBy(e => e.Delta))
        {
            live += delta;
            if (live > options.GovernedLimit)
            {
                violations.Add(new TortureViolation(
                    TortureInvariant.ConcurrencyLimit,
                    $"Governed queue '{keys.GovernedQueue}' (limit {options.GovernedLimit}) had {live} definitely-live " +
                    $"leases at {new DateTimeOffset(at, TimeSpan.Zero):O}."));
                break;
            }
        }

        // QuarantineNotExecuted, journal half: no client may have executed a Quarantined job.
        var executed = journal
            .Where(e => e.Op == Ops.Outcome && e.Executed == true && e.JobId is { } id)
            .Select(e => e.JobId!.Value)
            .ToHashSet();
        foreach (var job in ScannedJobs.Where(j => j.State == JobState.Quarantined && executed.Contains(j.JobId)))
        {
            violations.Add(new TortureViolation(
                TortureInvariant.QuarantineNotExecuted,
                $"Job {job.JobId} is Quarantined yet a client journaled executing it.", job.JobId));
        }

        AuditCancelProvenance(violations, journal, jobsById);
        AuditTagDurability(violations, journal, jobsById);
    }

    private void AuditCancelProvenance(
        List<TortureViolation> violations, IReadOnlyList<JournalEntry> journal, Dictionary<Guid, JobRecord> jobsById)
    {
        var cancelRequests = journal
            .Where(e => e.Op == Ops.Cancel
                && e.Result is nameof(CancelResult.CancelledImmediately) or nameof(CancelResult.CancellationRequested)
                && e.JobId is { } id)
            .Select(e => e.JobId!.Value)
            .ToHashSet();
        var cancelledOutcomes = journal
            .Where(e => e.Op == Ops.Outcome && e.Result == nameof(OutcomeResult.Applied)
                && e.Detail == nameof(JobOutcome.Cancelled) && e.JobId is { } id)
            .Select(e => e.JobId!.Value)
            .ToHashSet();
        var parentage = journal
            .Where(e => e.Op == Ops.Enqueue && e.Result == nameof(EnqueueResult.Ok) && e is { JobId: not null, Parents: not null })
            .GroupBy(e => e.JobId!.Value)
            .ToDictionary(g => g.Key, g => g.First());

        foreach (var job in ScannedJobs.Where(j => j.State == JobState.Cancelled))
        {
            if (cancelRequests.Contains(job.JobId) || cancelledOutcomes.Contains(job.JobId))
            {
                continue;
            }
            // Dependency cancellation — an OnSuccess parent went terminal without succeeding. The
            // parent's TRANSITION LOG is consulted, not its end state: a requeue can resurrect a
            // dead-lettered parent into a Succeeded end state, erasing the provenance from the row
            // while the child's cancellation stays perfectly legitimate.
            if (parentage.TryGetValue(job.JobId, out var enqueue)
                && enqueue.Mode == nameof(Storage.DependencyMode.OnSuccess)
                && enqueue.Parents!.Any(ParentEverFailed))
            {
                continue;
            }
            violations.Add(new TortureViolation(
                TortureInvariant.CancelProvenance,
                $"Job {job.JobId} is Cancelled with no cancel request, no cooperative-cancel outcome, and no " +
                "failed OnSuccess parent.", job.JobId));
        }
    }

    private bool ParentEverFailed(Guid parentId)
    {
        if (!Histories.TryGetValue(parentId, out var history))
        {
            return false;
        }
        if (history.Count > 0 && history[0].Ordinal > 0)
        {
            return true; // history aged out — the failing entry may be among the aged; stay lenient
        }
        return history.Any(t => t.State is JobState.DeadLettered or JobState.Quarantined or JobState.Cancelled);
    }

    private void AuditTagDurability(
        List<TortureViolation> violations, IReadOnlyList<JournalEntry> journal, Dictionary<Guid, JobRecord> jobsById)
    {
        var expected = new Dictionary<Guid, HashSet<string>>();
        foreach (var entry in journal)
        {
            var counts = entry.Op == Ops.Enqueue && entry.Result == nameof(EnqueueResult.Ok)
                || entry.Op == Ops.Outcome && entry.Result == nameof(OutcomeResult.Applied);
            if (!counts || entry.Tags is null || entry.JobId is null)
            {
                continue;
            }
            if (!expected.TryGetValue(entry.JobId.Value, out var set))
            {
                expected[entry.JobId.Value] = set = [];
            }
            set.UnionWith(entry.Tags);
        }

        foreach (var (jobId, tags) in expected)
        {
            if (!jobsById.TryGetValue(jobId, out var job))
            {
                continue; // EnqueueDurability already flags missing jobs
            }
            var stored = job.Tags
                .Select(t => t.Key.Length == 0 ? t.Value : $"{t.Key}={t.Value}")
                .ToHashSet();
            var missing = tags.Where(t => !stored.Contains(t)).ToList();
            if (missing.Count > 0)
            {
                violations.Add(new TortureViolation(
                    TortureInvariant.TagDurability,
                    $"Job {jobId} lost accepted tag write(s): {string.Join(", ", missing)}.", jobId));
            }
        }
    }
}
