using BackWave.Storage;

namespace BackWave.Torture;

/// <summary>
/// The quiescent oracle audit (issue 0200): after the workload stops and the drain converges, this
/// walks the store's end state plus every job's Transition Log, and cross-checks them against the
/// merged client observation journals. Every check is sound under wall-clock nondeterminism — the
/// journal checks use only conservative windows (a lease was *definitely* live between the claim's
/// return and the outcome call's start), so a torture failure is always a bug, never noise.
/// It is the COMPLETE audit: the mid-run pass shares its check bodies (see <see cref="Checks"/>)
/// but runs only the subset that stays sound against a live store.
/// </summary>
internal sealed class Auditor(IJobStore store, KeySpace keys, TortureOptions options)
{
    public List<JobRecord> ScannedJobs { get; } = [];

    public Dictionary<Guid, IReadOnlyList<JobTransition>> Histories { get; } = [];

    public async Task AuditAsync(
        IReadOnlyList<JournalEntry> journal, ViolationSink violations, CancellationToken cancellationToken)
    {
        await ScanAsync(cancellationToken);
        var jobsById = ScannedJobs.ToDictionary(j => j.JobId);

        AuditTransitionLogs(violations);
        AuditEndState(violations);
        await AuditAwaitingParentsAsync(violations, jobsById, cancellationToken);
        Checks.LiveJournal(journal, violations);
        AuditJournal(violations, journal, jobsById);
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

    private void AuditTransitionLogs(ViolationSink violations)
    {
        foreach (var job in ScannedJobs)
        {
            var history = Histories[job.JobId];
            if (history.Count == 0)
            {
                continue;
            }

            Checks.InitialTransition(job.JobId, Facts(history[0]), violations);

            for (var i = 1; i < history.Count; i++)
            {
                Checks.TransitionEdge(
                    job.JobId, Facts(history[i - 1]), Facts(history[i]), options.MaxAttempts, violations);
            }

            // TerminalStable is post-drain ONLY: it compares the log tail against the row, and mid-run
            // a newer transition can already exist that the cursor has not reached.
            if (history[^1].State != job.State)
            {
                violations.Add(new TortureViolation(
                    TortureInvariant.TerminalStable,
                    $"Job {job.JobId} is {job.State} but its last recorded transition is {history[^1].State} — " +
                    "the log tail and the row disagree.", job.JobId));
            }
        }
    }

    private static TransitionFacts Facts(JobTransition transition)
        => new(transition.Ordinal, transition.State, transition.Attempt);

    // ---- End-state audit ------------------------------------------------------------------------

    private void AuditEndState(ViolationSink violations)
    {
        foreach (var job in ScannedJobs)
        {
            Checks.JobRow(
                new JobRowFacts(
                    job.JobId, job.State, job.Attempt, job.WireName, job.LeaseOwner,
                    job.LeaseExpiry is not null, job.TerminalAt?.ToString("O")),
                keys, options, violations);
        }
    }

    private async Task AuditAwaitingParentsAsync(
        ViolationSink violations, Dictionary<Guid, JobRecord> jobsById, CancellationToken cancellationToken)
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
        ViolationSink violations, IReadOnlyList<JournalEntry> journal, Dictionary<Guid, JobRecord> jobsById)
    {
        var enqueueOks = journal
            .Where(e => e.Op == Ops.Enqueue && e.Result == nameof(EnqueueResult.Ok) && e.JobId is { } id)
            .ToLookup(e => e.JobId!.Value);
        var claims = journal.Where(e => e.Op == Ops.Claim && e is { JobId: not null, Attempt: not null }).ToList();

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
        // A relinquish hands back every lease of one owner, so from its call start the owner's leases
        // are no longer definitely live. The request entry (not the return) carries that start: a
        // relinquish that commits and then loses its reply still revoked the lease. The return, when
        // there is one, bounds the call: a relinquish that returned before a claim's call began cannot
        // have touched that lease.
        var relinquishReturns = journal
            .Where(e => e.Op == Ops.Relinquish)
            .ToDictionary(e => (e.Client, e.T0), e => e.T1);
        var relinquishRequests = journal
            .Where(e => e.Op == Ops.RelinquishRequested && e.Detail is not null)
            .ToLookup(e => e.Detail!);
        var intervals = claims
            .Select(claim =>
            {
                var key = (claim.JobId!.Value, claim.Attempt!.Value);
                var end = claim.LeaseExpiry ?? claim.T1;
                if (firstOutcomeStart.TryGetValue(key, out var outcomeStart))
                {
                    end = Math.Min(end, outcomeStart);
                }
                foreach (var request in relinquishRequests[$"torture-{keys.Seed:x8}-{claim.Client}"])
                {
                    var callEnd = relinquishReturns.TryGetValue((request.Client, request.T0), out var t1) ? t1 : long.MaxValue;
                    if (callEnd > claim.T0 && request.T0 < end)
                    {
                        end = Math.Min(end, request.T0);
                    }
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
        ViolationSink violations, IReadOnlyList<JournalEntry> journal, Dictionary<Guid, JobRecord> jobsById)
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
        ViolationSink violations, IReadOnlyList<JournalEntry> journal, Dictionary<Guid, JobRecord> jobsById)
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
