using BackWave.Storage;

namespace BackWave.Torture;

/// <summary>
/// The journal-only oracle, held as counters instead of re-derived from the journal.
///
/// The journal only ever grows and it is never rewritten, so every one of these checks is a pure
/// function of counters that a new entry can only increase. <see cref="Absorb"/> folds one delta into
/// those counters and marks what the delta touched. <see cref="Evaluate"/> then re-reads only the
/// touched keys, so one mid-run pass costs the size of its delta - not the size of the journal so far.
/// Re-deriving the whole journal every pass made the audit cost grow with the square of the run.
///
/// A verdict taken over a prefix stays true under append, which is what makes these checks safe to run
/// while the clients are still hammering the store.
/// </summary>
internal sealed class JournalOracle
{
    // Each check family keeps its touched keys in first-touch order, so a pass reports findings in the
    // order the journal produced them. A HashSet alone would make the artifact's order vary per run.
    private readonly Dictionary<string, RawGroup> _rawExceptions = new(StringComparer.Ordinal);
    private readonly List<string> _dirtyRaw = [];
    private readonly Dictionary<string, TriggerGroup> _haltTriggers = new(StringComparer.Ordinal);
    private readonly List<string> _dirtyTriggers = [];
    private readonly List<JournalEntry> _pendingCrashes = [];
    private readonly Dictionary<Guid, int> _workflowCreates = [];
    private readonly List<Guid> _dirtyWorkflows = [];
    private readonly Dictionary<Guid, JobCounters> _jobs = [];
    private readonly List<Guid> _dirtyJobs = [];

    /// <summary>Folds one journal delta into the counters. Every entry is read exactly once, ever.</summary>
    public void Absorb(IReadOnlyList<JournalEntry> delta)
    {
        foreach (var entry in delta)
        {
            switch (entry.Op)
            {
                case Ops.UnexpectedException:
                    var rawKey = $"{entry.Result}: {entry.Detail}";
                    if (!_rawExceptions.TryGetValue(rawKey, out var raw))
                    {
                        _rawExceptions[rawKey] = raw = new RawGroup();
                    }
                    raw.Count++;
                    Touch(_dirtyRaw, rawKey, raw.Dirty, d => raw.Dirty = d);
                    break;

                case Ops.InvariantViolation:
                    var triggerKey = entry.Result ?? string.Empty;
                    if (!_haltTriggers.TryGetValue(triggerKey, out var trigger))
                    {
                        // The FIRST entry's detail is the one the finding quotes: it sits nearest the cause.
                        _haltTriggers[triggerKey] = trigger = new TriggerGroup { FirstDetail = entry.Detail };
                    }
                    trigger.Count++;
                    Touch(_dirtyTriggers, triggerKey, trigger.Dirty, d => trigger.Dirty = d);
                    break;

                case Ops.ClientCrash:
                    _pendingCrashes.Add(entry);
                    break;

                case Ops.Workflow when entry.Result == nameof(WorkflowEnqueueResult.Ok)
                    && entry.Detail == "create" && entry.WorkflowId is { } workflowId:
                    _workflowCreates[workflowId] = _workflowCreates.GetValueOrDefault(workflowId) + 1;
                    if (!_dirtyWorkflows.Contains(workflowId))
                    {
                        _dirtyWorkflows.Add(workflowId);
                    }
                    break;
            }

            if (entry.JobId is not { } jobId)
            {
                continue;
            }

            var job = Job(jobId);
            switch (entry.Op)
            {
                case Ops.Enqueue when entry.Result == nameof(EnqueueResult.Ok):
                    job.EnqueuesAccepted++;
                    job.EnqueueClients.Add(entry.Client);
                    break;

                case Ops.Claim when entry.Attempt is { } claimedAttempt:
                    var claimed = job.Attempt(claimedAttempt);
                    claimed.Claims++;
                    claimed.ClaimClients.Add(entry.Client);
                    claimed.FirstClaimReturn = Math.Min(claimed.FirstClaimReturn, entry.T1);
                    break;

                case Ops.Outcome when entry.Result == nameof(OutcomeResult.Applied) && entry.Attempt is { } reportedAttempt:
                    job.Attempt(reportedAttempt).Applied++;
                    job.AppliedOutcomes.Add(new AppliedOutcome(reportedAttempt, entry.T0));
                    break;

                case Ops.RequeueRequested:
                    job.RequeueRequests++;
                    job.FirstRequeueRequest = Math.Min(job.FirstRequeueRequest, entry.T0);
                    break;

                case Ops.Requeue:
                    job.RequeueAnswers++;
                    if (entry.Result == nameof(RequeueResult.Requeued))
                    {
                        job.RequeueLives++;
                    }
                    break;

                case Ops.TransientFault or Ops.UnexpectedException when entry.Result == Ops.Requeue:
                    // A faulted requeue ANSWERS its request, and still grants a life: the store may have
                    // committed before the fault reached the client, and the client cannot tell which
                    // happened. Without the answer the request reads as in flight for the rest of the run,
                    // which leaves the provenance check below widened long after the requeue resolved.
                    job.RequeueAnswers++;
                    job.RequeueLives++;
                    break;
            }

            Touch(_dirtyJobs, jobId, job.Dirty, d => job.Dirty = d);
        }
    }

    /// <summary>Reports every finding the absorbed deltas made reachable, then clears the touched marks.</summary>
    public void Evaluate(ViolationSink sink)
    {
        foreach (var key in _dirtyRaw)
        {
            var group = _rawExceptions[key];
            group.Dirty = false;
            sink.Add(new TortureViolation(
                TortureInvariant.RawStoreException,
                $"{group.Count}× unexpected exception escaped the store surface during '{key}'."));
        }
        _dirtyRaw.Clear();

        // A production fail-stop trigger: the adapter observed a state its own invariants forbid, which
        // in production halts the worker group. Keyed by trigger id so the finding names the invariant.
        foreach (var key in _dirtyTriggers)
        {
            var group = _haltTriggers[key];
            group.Dirty = false;
            sink.Add(new TortureViolation(
                TortureInvariant.HaltTriggerFired,
                $"Halt trigger {key} fired {group.Count} time(s) - a production worker group would have " +
                $"fail-stopped: {group.FirstDetail}"));
        }
        _dirtyTriggers.Clear();

        foreach (var crash in _pendingCrashes)
        {
            sink.Add(new TortureViolation(
                TortureInvariant.ClientCrash, $"Client {crash.Client}: {crash.Detail}"));
        }
        _pendingCrashes.Clear();

        // At most one accepted enqueue per JobId, ever (ids are never purged during a run).
        foreach (var jobId in _dirtyJobs)
        {
            var job = _jobs[jobId];
            if (job.EnqueuesAccepted > 1)
            {
                sink.Add(new TortureViolation(
                    TortureInvariant.DuplicateEnqueueAccepted,
                    $"JobId {jobId} was accepted (Ok) by {job.EnqueuesAccepted} enqueues: " +
                    $"{string.Join(", ", job.EnqueueClients)}.", jobId));
            }
        }

        foreach (var workflowId in _dirtyWorkflows)
        {
            var created = _workflowCreates[workflowId];
            if (created > 1)
            {
                sink.Add(new TortureViolation(
                    TortureInvariant.DuplicateWorkflowAccepted,
                    $"WorkflowId {workflowId} was created (Ok) {created} times."));
            }
        }
        _dirtyWorkflows.Clear();

        // NoDoubleExecution: a claim hands an attempt to exactly one worker, once per life.
        foreach (var jobId in _dirtyJobs)
        {
            var job = _jobs[jobId];
            var allowed = 1 + job.Lives;
            foreach (var (attempt, state) in job.Attempts)
            {
                if (state.Claims > allowed)
                {
                    sink.Add(new TortureViolation(
                        TortureInvariant.NoDoubleExecution,
                        $"Job {jobId} attempt {attempt} was claimed {state.Claims} times " +
                        $"(by {string.Join(", ", state.ClaimClients)}) with only {allowed} life/lives.",
                        jobId));
                }
            }
        }

        // Effect-Once on the report: at most one Applied outcome per (job, attempt) per life.
        foreach (var jobId in _dirtyJobs)
        {
            var job = _jobs[jobId];
            var allowed = 1 + job.Lives;
            foreach (var (attempt, state) in job.Attempts)
            {
                if (state.Applied > allowed)
                {
                    sink.Add(new TortureViolation(
                        TortureInvariant.SlotDoubleRelease,
                        $"Job {jobId} attempt {attempt} had {state.Applied} Applied outcomes " +
                        $"with only {allowed} life/lives.", jobId));
                }
            }
        }

        // Fence supersession: an outcome for attempt a must not apply after attempt a' > a was already
        // handed out (the later claim's return proves attempt a's lease was gone). A job carries at most
        // MaxAttempts attempts and a bounded number of outcomes, so re-reading one touched job is a fixed
        // cost - and a claim absorbed long after an outcome can still be the claim that indicts it.
        foreach (var jobId in _dirtyJobs)
        {
            var job = _jobs[jobId];
            foreach (var outcome in job.AppliedOutcomes)
            {
                if (job.Lives > 0 && outcome.T0 >= job.FirstRequeueRequest)
                {
                    continue; // lives interleave attempt numbers; the cross-life ordering is not checkable
                }
                foreach (var (attempt, state) in job.Attempts)
                {
                    if (attempt > outcome.Attempt && state.FirstClaimReturn < outcome.T0)
                    {
                        sink.Add(new TortureViolation(
                            TortureInvariant.OutcomeProvenance,
                            $"Job {jobId} attempt {outcome.Attempt} outcome APPLIED although attempt " +
                            $"{attempt} had already been claimed before the report began — the fence let a stale " +
                            "writer through.", jobId));
                        break;
                    }
                }
            }
        }

        foreach (var jobId in _dirtyJobs)
        {
            _jobs[jobId].Dirty = false;
        }
        _dirtyJobs.Clear();
    }

    private JobCounters Job(Guid jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var job))
        {
            _jobs[jobId] = job = new JobCounters();
        }
        return job;
    }

    private static void Touch<T>(List<T> dirty, T key, bool alreadyDirty, Action<bool> mark)
    {
        if (alreadyDirty)
        {
            return;
        }
        mark(true);
        dirty.Add(key);
    }

    private sealed class RawGroup
    {
        public int Count;
        public bool Dirty;
    }

    private sealed class TriggerGroup
    {
        public int Count;
        public bool Dirty;
        public string? FirstDetail;
    }

    private readonly record struct AppliedOutcome(int Attempt, long T0);

    private sealed class AttemptCounters
    {
        public int Claims;
        public int Applied;

        /// <summary>The earliest return of any claim of this attempt - the instant the lease was proven handed out.</summary>
        public long FirstClaimReturn = long.MaxValue;
        public readonly List<string> ClaimClients = [];
    }

    private sealed class JobCounters
    {
        public bool Dirty;
        public int EnqueuesAccepted;
        public readonly List<string> EnqueueClients = [];
        public readonly Dictionary<int, AttemptCounters> Attempts = [];
        public readonly List<AppliedOutcome> AppliedOutcomes = [];
        public int RequeueLives;
        public int RequeueRequests;
        public int RequeueAnswers;
        public long FirstRequeueRequest = long.MaxValue;

        /// <summary>
        /// A Requeue resets the attempt counter to 0, so a requeued job legitimately re-runs the same
        /// attempt numbers - one extra life per successful requeue. An UNANSWERED request counts as a life
        /// too: the client journals its request before the store call and the result after it, so a prefix
        /// can hold a claim of the reset attempt while the requeue that permitted it is still in flight.
        /// Counting the gap as a life can only hide a finding. Counting it as nothing would invent one, and
        /// the mid-run audit reads exactly these prefixes. Every request is answered - by a result or by a
        /// fault - before the post-drain pass runs, so that pass counts only committed lives and in-flight ones.
        /// </summary>
        public int Lives => RequeueLives + Math.Max(0, RequeueRequests - RequeueAnswers);

        public AttemptCounters Attempt(int attempt)
        {
            if (!Attempts.TryGetValue(attempt, out var state))
            {
                Attempts[attempt] = state = new AttemptCounters();
            }
            return state;
        }
    }
}
