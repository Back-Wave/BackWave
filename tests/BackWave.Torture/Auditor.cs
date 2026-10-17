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
                return (claim.JobId!.Value, Attempt: claim.Attempt!.Value, Queue: claim.Queue, Start: claim.T1, End: end, Claim: claim);
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
        AuditReschedules(violations, journal, claims, intervals);
    }

    // Both reschedule checks are post-drain only: each pairs a reschedule with entries journaled on either
    // side of it, and mid-run a prefix can lack the in-flight call that would excuse the pair.
    private static void AuditReschedules(
        ViolationSink violations,
        IReadOnlyList<JournalEntry> journal,
        List<JournalEntry> claims,
        List<(Guid JobId, int Attempt, string? Queue, long Start, long End, JournalEntry Claim)> intervals)
    {
        // A client is sequential and journals in call order, so its neighbours bound a call that faulted:
        // the call began after the client's previous entry returned, and a request's answer (a result or
        // a fault) is the client's next entry.
        var previousReturn = new Dictionary<JournalEntry, long>(ReferenceEqualityComparer.Instance);
        var requeueWindows = new List<(Guid JobId, long T0, long T1)>();
        var lastReturn = new Dictionary<string, long>(StringComparer.Ordinal);
        var openRequeue = new Dictionary<string, JournalEntry>(StringComparer.Ordinal);
        foreach (var entry in journal)
        {
            previousReturn[entry] = lastReturn.GetValueOrDefault(entry.Client, long.MinValue);
            lastReturn[entry.Client] = entry.T1;
            if (openRequeue.Remove(entry.Client, out var request))
            {
                requeueWindows.Add((request.JobId!.Value, request.T0, entry.T1));
            }
            if (entry.Op == Ops.RequeueRequested && entry.JobId is not null)
            {
                openRequeue[entry.Client] = entry;
            }
        }
        foreach (var request in openRequeue.Values)
        {
            requeueWindows.Add((request.JobId!.Value, request.T0, long.MaxValue));
        }

        var faults = journal.Where(e => e.Op is Ops.TransientFault or Ops.UnexpectedException or Ops.InvariantViolation).ToList();
        var faultsByClient = faults.ToLookup(e => e.Client);
        var faultsByJob = faults.Where(e => e.JobId is not null).ToLookup(e => e.JobId!.Value);
        var claimsByJob = claims.ToLookup(e => e.JobId!.Value);
        var reschedules = journal.Where(e => e.Op == Ops.Reschedule && e.JobId is not null).ToList();
        var reschedulesByJob = reschedules.ToLookup(e => e.JobId!.Value);
        var rescheduled = reschedules
            .Where(e => e.Result == nameof(RescheduleResult.Rescheduled) && e.DueTime is not null)
            .ToList();
        var rescheduledByJob = rescheduled.ToLookup(e => e.JobId!.Value);
        var enqueuesByJob = journal
            .Where(e => e.Op == Ops.Enqueue && e.Result == nameof(EnqueueResult.Ok) && e.JobId is not null)
            .ToLookup(e => e.JobId!.Value);
        var heartbeatsLost = journal
            .Where(e => e.Op == Ops.Heartbeat && e.Result != "Renewed" && e.JobId is not null)
            .ToLookup(e => (e.Client, e.JobId!.Value));
        var cancelledAtOnce = journal
            .Where(e => e.Op == Ops.Cancel && e.Result == nameof(CancelResult.CancelledImmediately) && e.JobId is not null)
            .ToLookup(e => e.JobId!.Value);

        // ReschedulePlacedInLease. The NoOverlap interval is narrowed further, because here a long interval
        // convicts: a fault by the holder may be an outcome or a heartbeat that ended or shortened the lease,
        // so it ends the interval where that call could have begun; a later claim of the job, a heartbeat
        // of the holder that did not renew, or a cancel that took the job at once, proves the lease was gone.
        foreach (var interval in intervals)
        {
            var claim = interval.Claim;
            var end = interval.End;
            foreach (var fault in faultsByClient[claim.Client].Where(f => f.T1 > claim.T1))
            {
                end = Math.Min(end, previousReturn[fault]);
            }
            foreach (var other in claimsByJob[interval.JobId].Where(c => !ReferenceEquals(c, claim) && c.T1 >= claim.T1))
            {
                end = Math.Min(end, other.T0);
            }
            foreach (var heartbeat in heartbeatsLost[(claim.Client, interval.JobId)].Where(h => h.T1 > claim.T1))
            {
                end = Math.Min(end, heartbeat.T0);
            }
            foreach (var cancel in cancelledAtOnce[interval.JobId].Where(c => c.T1 > claim.T1))
            {
                end = Math.Min(end, cancel.T0);
            }
            foreach (var reschedule in rescheduledByJob[interval.JobId].Where(r => r.T0 > interval.Start && r.T1 < end))
            {
                violations.Add(new TortureViolation(
                    TortureInvariant.ReschedulePlacedInLease,
                    $"Client {reschedule.Client} rescheduled job {interval.JobId} while client {claim.Client}'s lease " +
                    $"of attempt {interval.Attempt} was definitely live (" +
                    $"{new DateTimeOffset(interval.Start, TimeSpan.Zero):O} to {new DateTimeOffset(end, TimeSpan.Zero):O}).",
                    interval.JobId));
            }
        }

        // ClaimedBeforeRescheduledDue. A claim that began after a reschedule returned must see the new due
        // time unless something moved it in between. A requeue, another reschedule, an accepted enqueue, or
        // any fault naming the job whose window meets [reschedule start, claim return] excuses the pair. A
        // lease taken and handed back in between (then a failure, an expiry, or a relinquish sets a new due
        // time) is ruled out by the attempt counter: with no requeue to reset it, the counter only grows, so
        // a claim of attempt k leaves no room for another claim when attempt k-1 was already handed out
        // before the reschedule began. That holds for a claim whose reply was lost too. The parent latch
        // keeps the later of the due time and its now, so it never makes a job due earlier.
        // The 1 ms margin covers an adapter that stores a due time less precisely than a tick.
        static bool Meets(long t0, long t1, long from, long to) => t0 <= to && t1 >= from;
        foreach (var reschedule in rescheduled)
        {
            var jobId = reschedule.JobId!.Value;
            foreach (var claim in claimsByJob[jobId].Where(c =>
                c.T0 > reschedule.T1 && c.OpNow < reschedule.DueTime - TimeSpan.TicksPerMillisecond))
            {
                var from = reschedule.T0;
                if (claim.Attempt > 1)
                {
                    var previous = claimsByJob[jobId]
                        .Where(c => c.Attempt == claim.Attempt - 1 && c.T1 < reschedule.T0)
                        .Select(c => (long?)c.T0)
                        .Max();
                    if (previous is null)
                    {
                        continue;
                    }
                    from = previous.Value;
                }
                var to = claim.T1;
                var moved = requeueWindows.Any(w => w.JobId == jobId && Meets(w.T0, w.T1, from, to))
                    || reschedulesByJob[jobId].Any(r => !ReferenceEquals(r, reschedule) && Meets(r.T0, r.T1, reschedule.T0, to))
                    || enqueuesByJob[jobId].Any(e => Meets(e.T0, e.T1, reschedule.T0, to))
                    || faultsByJob[jobId].Any(f => Meets(previousReturn[f], f.T1, reschedule.T0, to));
                if (moved)
                {
                    continue;
                }
                violations.Add(new TortureViolation(
                    TortureInvariant.ClaimedBeforeRescheduledDue,
                    $"Client {claim.Client} claimed job {jobId} attempt {claim.Attempt} with a claim now of " +
                    $"{new DateTimeOffset(claim.OpNow!.Value, TimeSpan.Zero):O} after client {reschedule.Client} " +
                    $"rescheduled it to {new DateTimeOffset(reschedule.DueTime!.Value, TimeSpan.Zero):O}, and nothing " +
                    "else moved its due time in between.", jobId));
            }
        }
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
