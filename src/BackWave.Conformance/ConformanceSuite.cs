using BackWave.Core;
using BackWave.Storage;

namespace BackWave.Conformance;

// Contributor breadcrumb: this suite is the executable form of docs/storage-contract.md — the
// Clause_ prefixes in the method names track that spec's section numbers. Keep them in lockstep.
/// <summary>
/// The certification suite for the storage contract behind <see cref="IJobStore"/>. To certify an
/// adapter, subclass this class in an xunit test project and override
/// <see cref="CreateStoreAsync(JobHistoryPolicy)"/> to return a fresh, empty store honoring the
/// given history policy; every test here is a public xunit fact, so the test runner discovers and
/// runs the whole suite against your store. The In-Memory reference store that ships with BackWave
/// passes the suite 100%, so a failing test indicates a divergence between your adapter and the
/// contract. Test names carry a stable clause-numbering scheme (<c>Clause_5_2_…</c>) that groups
/// related guarantees in test output; each test's summary states, in plain English, the guarantee
/// it certifies.
/// </summary>
/// <example>
/// A minimal certification class — the suite's tests run in your project against your store:
/// <code>
/// public sealed class MyStoreConformanceTests : ConformanceSuite
/// {
///     protected override async ValueTask&lt;IJobStore&gt; CreateStoreAsync(JobHistoryPolicy historyPolicy)
///     {
///         // Return a fresh, empty store per call — e.g. over a new temp database — honoring the policy.
///         var store = new MyJobStore(CreateEmptyDatabase(), historyPolicy);
///         await store.MigrateAsync();
///         return store;
///     }
/// }
/// </code>
/// </example>
public abstract class ConformanceSuite
{
    /// <summary>The fixed instant every test starts from; each test's clock advances from here.</summary>
    protected static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>The lease duration every claim in the suite uses.</summary>
    protected static readonly TimeSpan Lease = TimeSpan.FromMinutes(1);

    private static readonly RetryDisposition TwoAttempts = new RetryPolicy
    {
        MaxAttempts = 2,
        Backoff = _ => TimeSpan.FromMinutes(1),
    }.ToDisposition();

    // The Queue every conformance job lands in, scoping the expiry sweep.
    private static readonly string[] DefaultQueues = ["default"];

    /// <summary>
    /// Creates a fresh, empty store with the default history policy (transitions plus failure
    /// detail). Most tests build their store through this overload.
    /// </summary>
    /// <returns>A store with no jobs, schedules, or queue settings, ready for one test.</returns>
    protected ValueTask<IJobStore> CreateStoreAsync()
        => CreateStoreAsync(JobHistoryPolicy.TransitionsAndFailureDetail);

    /// <summary>
    /// The store factory every test calls, and the only member you must override to run the suite:
    /// returns a fresh, empty store honoring <paramref name="historyPolicy"/>. Each call must yield
    /// a store with no residual state (an empty database, schema, or file) — every test assumes a
    /// clean slate.
    /// </summary>
    /// <param name="historyPolicy">How much per-job history the returned store must record; the store under test must honor it.</param>
    /// <returns>A fresh, empty store honoring the given history policy.</returns>
    protected abstract ValueTask<IJobStore> CreateStoreAsync(JobHistoryPolicy historyPolicy);

    private static NewJob Job(string wireName = "conformance-job", string queue = "default", DateTimeOffset? dueTime = null)
        => new(Guid.NewGuid(), wireName, "{}"u8.ToArray(), queue, dueTime ?? T0);

    private static ValueTask<IReadOnlyList<JobRecord>> ClaimAsync(
        IJobStore store, DateTimeOffset now, int maxJobs = 32, string worker = "w1", string queue = "default")
        => store.ClaimAsync(new ClaimRequest(worker, [queue], maxJobs, Lease, now));

    // ── §5.1 Enqueue ────────────────────────────────────────────────────────────

    /// <summary>
    /// Certifies that a plain enqueue creates the job in the Scheduled state at attempt zero,
    /// immediately visible to reads.
    /// </summary>
    [Fact]
    public async Task Clause_5_1_Enqueue_CreatesAScheduledJob_VisibleToReads()
    {
        var store = await CreateStoreAsync();
        var job = Job();

        Assert.Equal(EnqueueResult.Ok, await store.EnqueueAsync(job, now: T0));

        var stored = await store.GetJobAsync(job.JobId);
        Assert.NotNull(stored);
        Assert.Equal(JobState.Scheduled, stored.State);
        Assert.Equal(job.WireName, stored.WireName);
        Assert.Equal(0, stored.Attempt);
    }

    /// <summary>
    /// Certifies that enqueuing a job with a duplicate id is rejected as a duplicate and leaves the
    /// original job untouched.
    /// </summary>
    [Fact]
    public async Task Clause_5_1_DuplicateJobId_IsRejected_OriginalUntouched()
    {
        var store = await CreateStoreAsync();
        var job = Job();
        await store.EnqueueAsync(job, now: T0);

        var duplicate = job with { DueTime = T0.AddDays(1) };
        Assert.Equal(EnqueueResult.Duplicate, await store.EnqueueAsync(duplicate, now: T0));
        Assert.Equal(T0, (await store.GetJobAsync(job.JobId))!.DueTime);
    }

    /// <summary>
    /// Certifies that an oversized payload or over-long wire name is rejected with the matching result
    /// and leaves no trace — never truncated, never partially stored.
    /// </summary>
    [Fact]
    public async Task Clause_5_1_Bounds_AreEnforcedWithClearResults_NeverTruncation()
    {
        var store = await CreateStoreAsync();

        var oversized = Job() with { Payload = new byte[StoreBounds.Default.MaxPayloadBytes + 1] };
        Assert.Equal(EnqueueResult.PayloadTooLarge, await store.EnqueueAsync(oversized, now: T0));

        var longName = Job(wireName: new string('w', StoreBounds.Default.MaxWireNameLength + 1));
        Assert.Equal(EnqueueResult.WireNameTooLong, await store.EnqueueAsync(longName, now: T0));

        Assert.Null(await store.GetJobAsync(oversized.JobId)); // rejected means no trace
    }

    /// <summary>
    /// Certifies that enqueuing with an unknown parent is rejected, while a known parent creates the
    /// child in the AwaitingParent state.
    /// </summary>
    [Fact]
    public async Task Clause_5_1_ParentSet_MustExist_AndCreatesAwaitingParent()
    {
        var store = await CreateStoreAsync();

        var orphan = Job() with { Parents = [Guid.NewGuid()] };
        Assert.Equal(EnqueueResult.UnknownParent, await store.EnqueueAsync(orphan, now: T0));

        var parent = Job();
        await store.EnqueueAsync(parent, now: T0);
        var child = Job() with { Parents = [parent.JobId] };
        Assert.Equal(EnqueueResult.Ok, await store.EnqueueAsync(child, now: T0));
        Assert.Equal(JobState.AwaitingParent, (await store.GetJobAsync(child.JobId))!.State);
    }

    /// <summary>
    /// Certifies that a parent already terminal at enqueue resolves the child's latch immediately: an
    /// on-success child of a failed parent is Cancelled, an on-any-terminal child is Scheduled.
    /// </summary>
    [Fact]
    public async Task Clause_5_1_AlreadyTerminalParent_ResolvesTheLatchAtEnqueue()
    {
        var store = await CreateStoreAsync();
        var parent = Job();
        await store.EnqueueAsync(parent, now: T0);
        var claimed = Assert.Single(await ClaimAsync(store, T0));
        await store.ReportOutcomeAsync(claimed.JobId, "w1", claimed.Attempt, new JobOutcome.Failure(null, "boom"), T0);

        // OnSuccess against a failed parent: cancelled immediately, never an orphan.
        var onSuccess = Job() with { Parents = [parent.JobId] };
        await store.EnqueueAsync(onSuccess, now: T0.AddMinutes(1));
        Assert.Equal(JobState.Cancelled, (await store.GetJobAsync(onSuccess.JobId))!.State);

        // OnAnyTerminal releases regardless of how the parent ended.
        var onAnyTerminal = Job() with { Parents = [parent.JobId], Mode = DependencyMode.OnAnyTerminal };
        await store.EnqueueAsync(onAnyTerminal, now: T0.AddMinutes(1));
        Assert.Equal(JobState.Scheduled, (await store.GetJobAsync(onAnyTerminal.JobId))!.State);
    }

    /// <summary>
    /// Certifies that duplicate parent ids collapse to a set — one remaining parent — and the single
    /// real parent going terminal releases the child exactly once.
    /// </summary>
    [Fact]
    public async Task Clause_5_1_DuplicateParentIds_CollapseToTheParentSet()
    {
        var store = await CreateStoreAsync();
        var parent = Job();
        await store.EnqueueAsync(parent, now: T0);

        // [p, p] is exactly [p]: the parent set is a set, not a list.
        var child = Job() with { Parents = [parent.JobId, parent.JobId] };
        Assert.Equal(EnqueueResult.Ok, await store.EnqueueAsync(child, now: T0));
        var stored = await store.GetJobAsync(child.JobId);
        Assert.Equal(JobState.AwaitingParent, stored!.State);
        Assert.Equal(1, stored.ParentsRemaining);

        // The one real parent going terminal fires the latch exactly once.
        var claimed = Assert.Single(await ClaimAsync(store, T0));
        await store.ReportOutcomeAsync(claimed.JobId, "w1", claimed.Attempt, new JobOutcome.Success(), T0);
        Assert.Equal(JobState.Scheduled, (await store.GetJobAsync(child.JobId))!.State);
    }

    /// <summary>
    /// Certifies that a caller-supplied trace context round-trips verbatim through reads and claims,
    /// and that the store never fabricates one when it is absent.
    /// </summary>
    [Fact]
    public async Task Clause_2_TraceContext_IsStoredAndReturnedVerbatim()
    {
        var store = await CreateStoreAsync();
        const string traceparent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01";
        var job = Job() with { TraceContext = traceparent };

        Assert.Equal(EnqueueResult.Ok, await store.EnqueueAsync(job, now: T0));
        Assert.Equal(traceparent, (await store.GetJobAsync(job.JobId))!.TraceContext);

        // The claim path returns it too — that's where the execution span gets its parent.
        Assert.Equal(traceparent, Assert.Single(await ClaimAsync(store, T0)).TraceContext);

        // Absent stays absent: the store never fabricates one.
        var bare = Job();
        await store.EnqueueAsync(bare, now: T0);
        Assert.Null((await store.GetJobAsync(bare.JobId))!.TraceContext);
    }

    // ── §5.1 Transactional Enqueue (gated on the §6 capability flag) ───────────

    /// <summary>
    /// Certifies that on stores supporting transactional enqueue, rolling back the caller's transaction
    /// means the job never existed — invisible to reads and never claimable.
    /// </summary>
    [Fact]
    public async Task Clause_5_1_TransactionalEnqueue_RollbackMeansItNeverExisted()
    {
        var store = await CreateStoreAsync();
        if (!store.SupportsTransactionalEnqueue)
        {
            return; // §6: the single optional capability
        }

        var job = Job();
        using (var transaction = BeginTransaction(store))
        {
            Assert.Equal(EnqueueResult.Ok, await store.EnqueueAsync(job, now: T0, transaction));
            Assert.Null(await store.GetJobAsync(job.JobId));
            Assert.Empty(await ClaimAsync(store, T0)); // never claimable
            transaction.Rollback();
        }

        Assert.Null(await store.GetJobAsync(job.JobId));
        Assert.Empty(await ClaimAsync(store, T0));
    }

    /// <summary>
    /// Certifies that on stores supporting transactional enqueue, committing the caller's transaction
    /// publishes the job as Scheduled and claimable.
    /// </summary>
    [Fact]
    public async Task Clause_5_1_TransactionalEnqueue_CommitMakesItClaimable()
    {
        var store = await CreateStoreAsync();
        if (!store.SupportsTransactionalEnqueue)
        {
            return;
        }

        var job = Job();
        using (var transaction = BeginTransaction(store))
        {
            await store.EnqueueAsync(job, now: T0, transaction);
            transaction.Commit();
        }

        Assert.Equal(JobState.Scheduled, (await store.GetJobAsync(job.JobId))!.State);
        Assert.Single(await ClaimAsync(store, T0));
    }

    /// <summary>
    /// Certifies that a BackWave store declares the transactional-enqueue capability — every shipped
    /// adapter (in-memory, embedded, and server) enlists in a caller-owned transaction.
    /// </summary>
    [Fact]
    public async Task Clause_5_1_SupportsTransactionalEnqueue_IsDeclared()
    {
        // The capability flag callers branch on must not silently read false: every conformant store
        // supports transactional enqueue (issue 0240 R2-F).
        var store = await CreateStoreAsync();
        Assert.True(store.SupportsTransactionalEnqueue);
    }

    /// <summary>
    /// Certifies that a multi-byte payload round-trips verbatim through enqueue and claim — never
    /// clipped to a single byte or otherwise truncated by the store.
    /// </summary>
    [Fact]
    public async Task Clause_5_1_Payload_RoundTripsVerbatim_ThroughClaim()
    {
        // A distinctive multi-byte payload proves the stored blob is neither clipped to one byte nor
        // reshaped: the claim returns exactly the bytes enqueued (issue 0240 R2-F).
        var store = await CreateStoreAsync();
        var payload = new byte[] { 0x7B, 0x22, 0x6B, 0x22, 0x3A, 0x31, 0x7D }; // {"k":1}
        var job = Job() with { Payload = payload };
        await store.EnqueueAsync(job, now: T0);

        var claimed = Assert.Single(await ClaimAsync(store, T0));
        Assert.Equal(payload, claimed.Payload.ToArray());
    }

    /// <summary>
    /// Starts a caller-owned database transaction for the transactional-enqueue tests. Adapters
    /// whose store reports <see cref="IJobStore.SupportsTransactionalEnqueue"/> as <c>true</c>
    /// must override this to begin a transaction the store can enlist in; for stores without the
    /// capability, leave the default — the transactional tests return early on them and never
    /// call this.
    /// </summary>
    /// <param name="store">The store under test, whose backing database the transaction targets.</param>
    /// <returns>An open transaction the enqueue calls under test enlist in.</returns>
    /// <exception cref="NotSupportedException">The default implementation always throws; override it when the store supports transactional enqueue.</exception>
    protected virtual System.Data.Common.DbTransaction BeginTransaction(IJobStore store)
        => throw new NotSupportedException(
            "Adapters declaring SupportsTransactionalEnqueue must override BeginTransaction.");

    // ── §5.2 Claim ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Certifies that a claim leases only currently-due jobs in the candidate queues, incrementing the
    /// attempt and stamping the lease owner and expiry.
    /// </summary>
    [Fact]
    public async Task Clause_5_2_Claim_TakesOnlyDueJobs_InTheCandidateQueues()
    {
        var store = await CreateStoreAsync();
        var due = Job();
        var future = Job(dueTime: T0.AddHours(1));
        var elsewhere = Job(queue: "other");
        await store.EnqueueAsync(due, now: T0);
        await store.EnqueueAsync(future, now: T0);
        await store.EnqueueAsync(elsewhere, now: T0);

        var claimed = Assert.Single(await ClaimAsync(store, T0));
        Assert.Equal(due.JobId, claimed.JobId);
        Assert.Equal(JobState.Leased, claimed.State);
        Assert.Equal(1, claimed.Attempt); // the claim IS the start of the Attempt
        Assert.Equal("w1", claimed.LeaseOwner);
        Assert.Equal(T0 + Lease, claimed.LeaseExpiry);
    }

    /// <summary>
    /// Certifies that a claim returns jobs in due-time order and never exceeds the requested batch
    /// size.
    /// </summary>
    [Fact]
    public async Task Clause_5_2_Claim_ReturnsDueTimeOrder_AndRespectsMaxJobs()
    {
        var store = await CreateStoreAsync();
        var second = Job(dueTime: T0.AddMinutes(-1));
        var first = Job(dueTime: T0.AddMinutes(-2));
        await store.EnqueueAsync(second, now: T0.AddMinutes(-1));
        await store.EnqueueAsync(first, now: T0.AddMinutes(-2));

        var claimed = Assert.Single(await ClaimAsync(store, T0, maxJobs: 1));
        Assert.Equal(first.JobId, claimed.JobId);
    }

    /// <summary>
    /// Certifies that jobs with identical due times are claimed in enqueue order — the deterministic
    /// tiebreak.
    /// </summary>
    [Fact]
    public async Task Clause_5_2_WithinAQueue_TheTiebreak_IsEnqueueOrder()
    {
        var store = await CreateStoreAsync();
        var first = Job();
        var second = Job();
        var third = Job();
        await store.EnqueueAsync(first, now: T0);
        await store.EnqueueAsync(second, now: T0);
        await store.EnqueueAsync(third, now: T0);

        // Identical DueTimes: enqueue order (Sequence) is the deterministic tiebreak.
        var claimed = await ClaimAsync(store, T0);
        Assert.Equal([first.JobId, second.JobId, third.JobId], claimed.Select(j => j.JobId).ToList());
    }

    /// <summary>
    /// Certifies that a claim honors the caller's queue ordering: a higher-priority queue's jobs come
    /// first even when a lower-priority queue holds older work.
    /// </summary>
    [Fact]
    public async Task Clause_5_2_TheClaimedBatch_FollowsOrderedCandidateQueues()
    {
        var store = await CreateStoreAsync();
        // The lower-priority Queue holds the OLDER job: a cross-queue due-time re-sort
        // would silently undo the Dispatch Policy's decision.
        var bulk = Job(queue: "bulk", dueTime: T0.AddMinutes(-5));
        var critical = Job(queue: "critical", dueTime: T0.AddMinutes(-1));
        await store.EnqueueAsync(bulk, now: T0);
        await store.EnqueueAsync(critical, now: T0);

        var claimed = await store.ClaimAsync(new ClaimRequest("w1", ["critical", "bulk"], 32, Lease, T0));
        Assert.Equal([critical.JobId, bulk.JobId], claimed.Select(j => j.JobId).ToList());
    }

    /// <summary>
    /// Certifies that concurrent claimers never receive the same job twice: every job is leased to
    /// exactly one claimer.
    /// </summary>
    [Fact]
    public async Task Clause_5_2_ConcurrentClaimers_NeverDoubleClaim()
    {
        var store = await CreateStoreAsync();
        const int jobCount = 64;
        for (var i = 0; i < jobCount; i++)
        {
            await store.EnqueueAsync(Job(), now: T0);
        }

        // Invariant I1: each returned job is leased to at most one claimer.
        var claimers = Enumerable.Range(0, 8).Select(n => Task.Run(async () =>
        {
            var mine = new List<Guid>();
            while (true)
            {
                var batch = await ClaimAsync(store, T0, maxJobs: 4, worker: $"w{n}");
                if (batch.Count == 0)
                {
                    return mine;
                }
                mine.AddRange(batch.Select(j => j.JobId));
            }
        })).ToList();

        var perClaimer = await Task.WhenAll(claimers);
        var all = perClaimer.SelectMany(ids => ids).ToList();
        Assert.Equal(jobCount, all.Count);
        Assert.Equal(jobCount, all.Distinct().Count());
    }

    /// <summary>
    /// Certifies that several claimers draining one queue under distinct worker identities each claim
    /// and complete a disjoint set of jobs — every job executes exactly once.
    /// </summary>
    [Fact]
    public async Task Clause_5_2_MultiplePumpsOfOneGroup_OneQueue_ClaimAndExecuteDisjointRows()
    {
        // The Pumps fan-out guarantee (ADR 0037): a Worker Group running several Pumps on one Queue
        // claims under one distinct worker identity per Pump, so SKIP LOCKED hands each Pump a disjoint
        // set of rows with no extra coordination — no job is claimed or executed by two Pumps. Verified
        // here on a real database, where SKIP LOCKED actually runs.
        var store = await CreateStoreAsync();
        const int pumpCount = 4;
        const int jobCount = 80;
        for (var i = 0; i < jobCount; i++)
        {
            await store.EnqueueAsync(Job(), now: T0);
        }

        // Each Pump claims under a distinct worker identity (shaped like the Shell's
        // "{machine}:{group}:{guid}") and drains its claims to a terminal Success, exactly as a Pump's
        // claim → execute → report loop does. A report only applies for the fenced lease owner.
        var pumps = Enumerable.Range(0, pumpCount).Select(p => Task.Run(async () =>
        {
            var worker = $"node:emails:{p}";
            var executed = new List<Guid>();
            while (true)
            {
                var batch = await ClaimAsync(store, T0, maxJobs: 4, worker: worker);
                if (batch.Count == 0)
                {
                    return executed;
                }
                foreach (var job in batch)
                {
                    // Applied iff this Pump owns the lease — proves no sibling executed the same row.
                    Assert.Equal(
                        OutcomeResult.Applied,
                        await store.ReportOutcomeAsync(job.JobId, worker, job.Attempt, new JobOutcome.Success(), T0));
                    executed.Add(job.JobId);
                }
            }
        })).ToList();

        var perPump = await Task.WhenAll(pumps);
        var all = perPump.SelectMany(ids => ids).ToList();
        Assert.Equal(jobCount, all.Count);            // every job ran
        Assert.Equal(jobCount, all.Distinct().Count()); // each exactly once — disjoint across Pumps
    }

    /// <summary>
    /// Certifies that a queue's concurrency limit caps the number of concurrently leased jobs, and that
    /// a terminal outcome or a lease expiry frees the slot for the next claim.
    /// </summary>
    [Fact]
    public async Task Clause_5_10_ConcurrencyLimit_CapsLeased_AndFreesSlotsOnTerminalAndExpiry()
    {
        var store = await CreateStoreAsync();
        await store.SetConcurrencyLimitAsync("default", 1, "alice", T0);
        await store.EnqueueAsync(Job(), now: T0);
        await store.EnqueueAsync(Job(), now: T0);

        var firstBatch = await ClaimAsync(store, T0);
        var held = Assert.Single(firstBatch); // limit 1: one slot
        Assert.Empty(await ClaimAsync(store, T0, worker: "w2"));

        // Terminal state frees the slot (invariant I3).
        await store.ReportOutcomeAsync(held.JobId, "w1", held.Attempt, new JobOutcome.Success(), T0);
        var second = Assert.Single(await ClaimAsync(store, T0, worker: "w2"));

        // Lease expiry frees the slot too: slots are the count of live Leases by construction.
        var pastExpiry = T0 + Lease + TimeSpan.FromSeconds(1);
        await store.ExpireLeasesAsync(pastExpiry, maxJobs: 32, DefaultQueues, TwoAttempts);
        Assert.NotNull(second); // expired job rescheduled; the slot is claimable again
        Assert.Single(await ClaimAsync(store, pastExpiry.AddMinutes(2), worker: "w3"));
    }

    // ── §5.6 ReportOutcome ──────────────────────────────────────────────────────

    /// <summary>
    /// Certifies that a Success outcome moves the job to Succeeded, clears the lease, and stamps the
    /// terminal instant.
    /// </summary>
    [Fact]
    public async Task Clause_5_6_Success_GoesTerminal_AndClearsTheLease()
    {
        var store = await CreateStoreAsync();
        await store.EnqueueAsync(Job(), now: T0);
        var claimed = Assert.Single(await ClaimAsync(store, T0));

        Assert.Equal(OutcomeResult.Applied,
            await store.ReportOutcomeAsync(claimed.JobId, "w1", claimed.Attempt, new JobOutcome.Success(), T0));

        var job = await store.GetJobAsync(claimed.JobId);
        Assert.Equal(JobState.Succeeded, job!.State);
        Assert.Null(job.LeaseOwner);
        Assert.Equal(T0, job.TerminalAt);
    }

    /// <summary>
    /// Certifies that a Failure outcome with a retry instant reschedules the job at that instant, and
    /// one without dead-letters it with the given cause.
    /// </summary>
    [Fact]
    public async Task Clause_5_6_Failure_RetriesAtTheGivenInstant_OrDeadLettersWithoutOne()
    {
        var store = await CreateStoreAsync();
        await store.EnqueueAsync(Job(), now: T0);
        var claimed = Assert.Single(await ClaimAsync(store, T0));

        var retryAt = T0.AddMinutes(5);
        await store.ReportOutcomeAsync(claimed.JobId, "w1", claimed.Attempt, new JobOutcome.Failure(retryAt, "transient"), T0);
        var retrying = await store.GetJobAsync(claimed.JobId);
        Assert.Equal(JobState.Scheduled, retrying!.State);
        Assert.Equal(retryAt, retrying.DueTime);

        var again = Assert.Single(await ClaimAsync(store, retryAt));
        await store.ReportOutcomeAsync(again.JobId, "w1", again.Attempt, new JobOutcome.Failure(null, "fatal"), retryAt);
        var dead = await store.GetJobAsync(claimed.JobId);
        Assert.Equal(JobState.DeadLettered, dead!.State);
        Assert.Equal("fatal", dead.TerminalCause);
    }

    /// <summary>
    /// Certifies that outcome reports are fenced by the worker/attempt pair and the live lease: a wrong
    /// worker, a wrong attempt, or a lapsed lease changes nothing and returns StaleLease.
    /// </summary>
    [Fact]
    public async Task Clause_5_6_TheWorkerAttemptPair_FencesEveryOutcome()
    {
        var store = await CreateStoreAsync();
        await store.EnqueueAsync(Job(), now: T0);
        var claimed = Assert.Single(await ClaimAsync(store, T0));

        // Wrong worker, wrong attempt, expired lease: all fenced, nothing changes.
        Assert.Equal(OutcomeResult.StaleLease,
            await store.ReportOutcomeAsync(claimed.JobId, "impostor", claimed.Attempt, new JobOutcome.Success(), T0));
        Assert.Equal(OutcomeResult.StaleLease,
            await store.ReportOutcomeAsync(claimed.JobId, "w1", claimed.Attempt + 1, new JobOutcome.Success(), T0));
        Assert.Equal(OutcomeResult.StaleLease,
            await store.ReportOutcomeAsync(claimed.JobId, "w1", claimed.Attempt, new JobOutcome.Success(), T0 + Lease + TimeSpan.FromSeconds(1)));

        Assert.Equal(JobState.Leased, (await store.GetJobAsync(claimed.JobId))!.State);
    }

    /// <summary>
    /// Certifies that an Unroutable outcome quarantines the job — a terminal state distinct from
    /// DeadLettered — recording the cause.
    /// </summary>
    [Fact]
    public async Task Clause_5_6_Unroutable_Quarantines_DistinctFromDeadLettered()
    {
        var store = await CreateStoreAsync();
        await store.EnqueueAsync(Job(), now: T0);
        var claimed = Assert.Single(await ClaimAsync(store, T0));

        await store.ReportOutcomeAsync(
            claimed.JobId, "w1", claimed.Attempt, new JobOutcome.Unroutable("no handler for wire name"), T0);

        var job = await store.GetJobAsync(claimed.JobId);
        Assert.Equal(JobState.Quarantined, job!.State);
        Assert.NotEqual(JobState.DeadLettered, job.State);
        Assert.Equal("no handler for wire name", job.TerminalCause);
    }

    /// <summary>
    /// Certifies that a parent going terminal resolves every child latch atomically: on-success
    /// children of a failed parent are Cancelled, on-any-terminal children are released to Scheduled.
    /// </summary>
    [Fact]
    public async Task Clause_5_6_TerminalTransition_ResolvesChildLatches_Atomically()
    {
        var store = await CreateStoreAsync();
        var parent = Job();
        await store.EnqueueAsync(parent, now: T0);
        var onSuccess = Job() with { Parents = [parent.JobId] };
        var onAnyTerminal = Job() with { Parents = [parent.JobId], Mode = DependencyMode.OnAnyTerminal };
        await store.EnqueueAsync(onSuccess, now: T0);
        await store.EnqueueAsync(onAnyTerminal, now: T0);

        var claimed = Assert.Single(await ClaimAsync(store, T0));
        await store.ReportOutcomeAsync(claimed.JobId, "w1", claimed.Attempt, new JobOutcome.Failure(null, "boom"), T0);

        // Invariant I2: no AwaitingParent survives its parent set going terminal.
        Assert.Equal(JobState.Cancelled, (await store.GetJobAsync(onSuccess.JobId))!.State);
        Assert.Equal(JobState.Scheduled, (await store.GetJobAsync(onAnyTerminal.JobId))!.State);
    }

    // ── §5.6b ReportOutcomes (batch) ─────────────────────────────────────────────

    /// <summary>
    /// Certifies that a batched outcome report applies every row whose lease is live, fences every
    /// stale row, and returns one result per row, in order, keyed by job id.
    /// </summary>
    [Fact]
    public async Task Clause_5_6b_Batch_AppliesEveryLiveRow_AndFencesEveryStaleRow()
    {
        var store = await CreateStoreAsync();
        var a = Job();
        var b = Job();
        var c = Job();
        await store.EnqueueAsync(a, now: T0);
        await store.EnqueueAsync(b, now: T0);
        await store.EnqueueAsync(c, now: T0);
        var claimed = await ClaimAsync(store, T0);
        var ca = claimed.Single(j => j.JobId == a.JobId);
        var cb = claimed.Single(j => j.JobId == b.JobId);
        var cc = claimed.Single(j => j.JobId == c.JobId);

        // One live success, one live failure, one stale row (wrong attempt) — all in one batch.
        var results = await store.ReportOutcomesAsync(
        [
            new OutcomeReport(ca.JobId, "w1", ca.Attempt, new JobOutcome.Success()),
            new OutcomeReport(cb.JobId, "w1", cb.Attempt, new JobOutcome.Failure(null, "fatal")),
            new OutcomeReport(cc.JobId, "w1", cc.Attempt + 1, new JobOutcome.Success()),
        ], T0);

        // Results come back one per row, in order, keyed by job id.
        Assert.Equal(3, results.Count);
        Assert.Equal(new OutcomeReportResult(ca.JobId, OutcomeResult.Applied), results[0]);
        Assert.Equal(new OutcomeReportResult(cb.JobId, OutcomeResult.Applied), results[1]);
        Assert.Equal(new OutcomeReportResult(cc.JobId, OutcomeResult.StaleLease), results[2]);

        Assert.Equal(JobState.Succeeded, (await store.GetJobAsync(a.JobId))!.State);
        Assert.Equal(JobState.DeadLettered, (await store.GetJobAsync(b.JobId))!.State);
        Assert.Equal(JobState.Leased, (await store.GetJobAsync(c.JobId))!.State); // untouched by the fenced row
    }

    /// <summary>
    /// Certifies that an empty outcome batch applies nothing and returns an empty result list.
    /// </summary>
    [Fact]
    public async Task Clause_5_6b_Batch_EmptyBatch_AppliesNothing_ReturnsEmpty()
    {
        var store = await CreateStoreAsync();
        Assert.Empty(await store.ReportOutcomesAsync([], T0));
    }

    /// <summary>
    /// Certifies that per-row output blobs and tag deltas in a batched report persist exactly as they
    /// do through single reports.
    /// </summary>
    [Fact]
    public async Task Clause_5_6b_Batch_OutputAndTagsRows_RideTheSameFence_AsSingleReports()
    {
        var store = await CreateStoreAsync();
        var withOutput = Job();
        var withTags = Job();
        await store.EnqueueAsync(withOutput, now: T0);
        await store.EnqueueAsync(withTags, now: T0);
        var claimed = await ClaimAsync(store, T0);
        var co = claimed.Single(j => j.JobId == withOutput.JobId);
        var ct = claimed.Single(j => j.JobId == withTags.JobId);

        var output = new byte[] { 1, 2, 3, 4 };
        var addedTags = JobTags.Empty.WithTag("tenant", "acme");
        await store.ReportOutcomesAsync(
        [
            new OutcomeReport(co.JobId, "w1", co.Attempt, new JobOutcome.Success()) { Output = output },
            new OutcomeReport(ct.JobId, "w1", ct.Attempt, new JobOutcome.Success()) { AddedTags = addedTags },
        ], T0);

        Assert.Equal(output, (await store.GetJobOutputAsync(withOutput.JobId))!.Value.ToArray());
        Assert.Contains(JobTag.Keyed("tenant", "acme"), (await store.GetJobAsync(withTags.JobId))!.Tags);
    }

    /// <summary>
    /// Certifies that a batched row carrying an over-limit output blob is rejected with a throw before
    /// its write lands: the job stays Leased and no truncated blob is ever stored.
    /// </summary>
    [Fact]
    public async Task Clause_5_6b_Batch_OverCapOutputRow_IsRejectedLoudly_NeverTruncated()
    {
        var store = await CreateStoreAsync();
        await store.EnqueueAsync(Job(), now: T0);
        var claimed = Assert.Single(await ClaimAsync(store, T0));

        // The over-cap row's OWN write is rejected before it lands, on every store — the universal
        // "rejected, never truncated" promise: the throw fires, the job stays Leased, no clipped blob.
        var oversized = new byte[StoreBounds.Default.MaxOutputBytes + 1];
        await Assert.ThrowsAsync<JobOutputTooLargeException>(() => store.ReportOutcomesAsync(
        [
            new OutcomeReport(claimed.JobId, "w1", claimed.Attempt, new JobOutcome.Success()) { Output = oversized },
        ], T0).AsTask());

        Assert.Equal(JobState.Leased, (await store.GetJobAsync(claimed.JobId))!.State);
        Assert.Null(await store.GetJobOutputAsync(claimed.JobId));
    }

    /// <summary>
    /// Whether the store under test applies a batched outcome report as one all-or-nothing unit.
    /// Override to return <c>true</c> when your adapter wraps the whole batch in a transaction.
    /// The default is <c>false</c>, matching stores that loop the single-report primitive per row
    /// (best-effort, not atomic); the whole-batch atomicity test returns early on those stores.
    /// </summary>
    protected virtual bool BatchOutcomesAreAtomic => false;

    /// <summary>
    /// Certifies that on stores declaring atomic batch reporting, an over-limit output row aborts the
    /// whole batch — sibling rows ordered before it are not applied either.
    /// </summary>
    [Fact]
    public async Task Clause_5_6b_Batch_AtomicStore_OverCapOutputRow_LeavesSiblingRowsUntouched()
    {
        if (!BatchOutcomesAreAtomic)
        {
            return; // the default per-row loop applies earlier rows before a later row throws — not atomic
        }

        var store = await CreateStoreAsync();
        var live = Job();
        var oversize = Job();
        await store.EnqueueAsync(live, now: T0);
        await store.EnqueueAsync(oversize, now: T0);
        var claimed = await ClaimAsync(store, T0);
        var cl = claimed.Single(j => j.JobId == live.JobId);
        var co = claimed.Single(j => j.JobId == oversize.JobId);

        // The over-cap check spans the WHOLE batch and precedes ANY write. The live row is ordered FIRST,
        // so a per-row impl that wrote before scanning would already have applied it — proving the
        // property requires asserting that sibling is left untouched once a later row throws.
        var oversized = new byte[StoreBounds.Default.MaxOutputBytes + 1];
        await Assert.ThrowsAsync<JobOutputTooLargeException>(() => store.ReportOutcomesAsync(
        [
            new OutcomeReport(cl.JobId, "w1", cl.Attempt, new JobOutcome.Success()),
            new OutcomeReport(co.JobId, "w1", co.Attempt, new JobOutcome.Success()) { Output = oversized },
        ], T0).AsTask());

        // Nothing was written: both rows' jobs are still Leased, and the live row recorded no output.
        Assert.Equal(JobState.Leased, (await store.GetJobAsync(live.JobId))!.State);
        Assert.Equal(JobState.Leased, (await store.GetJobAsync(oversize.JobId))!.State);
        Assert.Null(await store.GetJobOutputAsync(live.JobId));
    }

    /// <summary>
    /// Certifies that concurrent terminal outcomes and dependency enqueues over overlapping
    /// parent/child rows never deadlock, and every child latch resolves intact.
    /// </summary>
    [Fact]
    public async Task Clause_5_6_ConcurrentTerminalOutcomesAndEnqueues_OverSharedRows_NeverDeadlock()
    {
        // Latch resolution (report-outcome) and enqueue both lock multiple job rows. Two
        // transactions over an overlapping set that acquire locks in opposite orders deadlock
        // on a real database; the adapters now lock in one deterministic id order (issue 0032).
        // Hammer overlapping parent/child sets and assert every round completes with no torn
        // latch — a deadlock surfaces as a thrown provider exception out of Task.WhenAll.
        var store = await CreateStoreAsync();
        const int rounds = 12;
        const int childrenPerRound = 6;

        for (var round = 0; round < rounds; round++)
        {
            var parentA = Job();
            var parentB = Job();
            await store.EnqueueAsync(parentA, now: T0);
            await store.EnqueueAsync(parentB, now: T0);

            // Children awaiting BOTH parents: every child row is contended by both parents'
            // latch resolutions at once.
            var children = new List<Guid>();
            for (var i = 0; i < childrenPerRound; i++)
            {
                var child = Job(wireName: "child") with
                {
                    Parents = [parentA.JobId, parentB.JobId],
                    Mode = DependencyMode.OnAnyTerminal,
                };
                await store.EnqueueAsync(child, now: T0);
                children.Add(child.JobId);
            }

            var leased = await ClaimAsync(store, T0);
            var a = leased.Single(j => j.JobId == parentA.JobId);
            var b = leased.Single(j => j.JobId == parentB.JobId);

            // Fresh dependencies sharing the same parents, with the parent set in the reverse
            // order — the natural-order divergence that used to deadlock against the latch.
            var concurrentEnqueues = Enumerable.Range(0, childrenPerRound).Select(_ =>
                Job(wireName: "child") with
                {
                    Parents = [parentB.JobId, parentA.JobId],
                    Mode = DependencyMode.OnAnyTerminal,
                }).ToList();

            await Task.WhenAll(
            [
                Task.Run(async () =>
                    await store.ReportOutcomeAsync(a.JobId, "w1", a.Attempt, new JobOutcome.Success(), T0)),
                Task.Run(async () =>
                    await store.ReportOutcomeAsync(b.JobId, "w1", b.Attempt, new JobOutcome.Success(), T0)),
                .. concurrentEnqueues.Select(c => Task.Run(async () => await store.EnqueueAsync(c, now: T0))),
            ]);

            // Both parents went terminal under on-any-terminal: every original child released,
            // none orphaned, none left mid-latch.
            Assert.Equal(JobState.Succeeded, (await store.GetJobAsync(parentA.JobId))!.State);
            Assert.Equal(JobState.Succeeded, (await store.GetJobAsync(parentB.JobId))!.State);
            foreach (var childId in children)
            {
                Assert.Equal(JobState.Scheduled, (await store.GetJobAsync(childId))!.State);
            }
        }
    }

    /// <summary>
    /// Certifies that a batched report records each matched job's resulting-state transition (dropping
    /// FailureDetail on a non-failure, keeping it on a failure) while a fenced stale row persists no
    /// transition, output, or tags.
    /// </summary>
    [Fact]
    public async Task Clause_5_6b_Batch_RecordsMatchedTransitions_DropsDetailOnSuccess_KeepsOnFailure_FencesStaleOutput()
    {
        var store = await CreateStoreAsync();
        var okJob = Job();
        var failJob = Job();
        var staleJob = Job();
        await store.EnqueueAsync(okJob, now: T0);
        await store.EnqueueAsync(failJob, now: T0);
        await store.EnqueueAsync(staleJob, now: T0);
        var claimed = await ClaimAsync(store, T0);
        var ok = claimed.Single(j => j.JobId == okJob.JobId);
        var fail = claimed.Single(j => j.JobId == failJob.JobId);
        var stale = claimed.Single(j => j.JobId == staleJob.JobId);

        var results = await store.ReportOutcomesAsync(
        [
            new OutcomeReport(ok.JobId, "w1", ok.Attempt, new JobOutcome.Success()) { FailureDetail = "dropped-on-success" },
            new OutcomeReport(fail.JobId, "w1", fail.Attempt, new JobOutcome.Failure(null, "boom")) { FailureDetail = "recorded-detail" },
            new OutcomeReport(stale.JobId, "w1", stale.Attempt + 1, new JobOutcome.Success())
                { Output = new byte[] { 9, 9 }, AddedTags = JobTags.Empty.WithTag("leaked", "yes") },
        ], T0);
        Assert.Equal(OutcomeResult.StaleLease, results[2].Result);

        // Matched Success: terminal transition recorded, detail dropped (not a failure).
        var okHistory = await store.GetJobHistoryAsync(okJob.JobId);
        Assert.Equal(JobState.Succeeded, okHistory[^1].State);
        Assert.Null(okHistory[^1].FailureDetail);

        // Matched Failure: terminal transition recorded WITH its (clamped) detail.
        var failHistory = await store.GetJobHistoryAsync(failJob.JobId);
        Assert.Equal(JobState.DeadLettered, failHistory[^1].State);
        Assert.Equal("recorded-detail", failHistory[^1].FailureDetail);

        // Fenced stale row: no terminal transition, and its buffered output/tags never landed.
        var staleHistory = await store.GetJobHistoryAsync(staleJob.JobId);
        Assert.Equal([JobState.Scheduled, JobState.Leased], staleHistory.Select(t => t.State).ToList());
        Assert.Null(await store.GetJobOutputAsync(staleJob.JobId));
        Assert.Empty((await store.GetJobAsync(staleJob.JobId))!.Tags);
    }

    /// <summary>
    /// Certifies that transitions appended through the batched report path are bounded per job life: past
    /// the cap the oldest are dropped while ordinals stay contiguous.
    /// </summary>
    [Fact]
    public async Task Clause_5_6b_Batch_History_IsBoundedPerJobLife_DroppingOldestBeyondTheCap()
    {
        var store = await CreateStoreAsync();
        var cap = StoreBounds.Default.MaxTransitionsPerJob;
        var job = Job();
        await store.EnqueueAsync(job, now: T0);

        // Each cycle claims (a Leased transition) then BATCH-reports a retry (a Scheduled transition via
        // ReportOutcomesAsync), so ~2*cap transitions run and the LAST operation is a batch report.
        var now = T0;
        for (var i = 0; i < cap; i++)
        {
            var claimed = Assert.Single(await ClaimAsync(store, now));
            var retryAt = now.AddMinutes(5);
            var results = await store.ReportOutcomesAsync(
                [new OutcomeReport(claimed.JobId, "w1", claimed.Attempt, new JobOutcome.Failure(retryAt, "again"))], now);
            Assert.Equal(OutcomeResult.Applied, results[0].Result);
            now = retryAt;
        }

        var history = await store.GetJobHistoryAsync(job.JobId);
        Assert.Equal(cap, history.Count); // the batch path prunes to the per-job-life cap
        Assert.True(history.Zip(history.Skip(1)).All(p => p.Second.Ordinal == p.First.Ordinal + 1));
    }

    /// <summary>
    /// Certifies that a batched terminal report resolves dependent child latches: a succeeding parent
    /// releases its on-success child, and a failing parent cascade-cancels its child.
    /// </summary>
    [Fact]
    public async Task Clause_5_6b_Batch_TerminalParents_ReleaseAndCascadeChildLatches()
    {
        var store = await CreateStoreAsync();
        var p1 = Job();
        var p2 = Job();
        await store.EnqueueAsync(p1, now: T0);
        await store.EnqueueAsync(p2, now: T0);
        var child1 = Job() with { Parents = [p1.JobId] };
        var child2 = Job() with { Parents = [p2.JobId] };
        await store.EnqueueAsync(child1, now: T0);
        await store.EnqueueAsync(child2, now: T0);
        var claimed = await ClaimAsync(store, T0); // only the two parents are claimable
        var c1 = claimed.Single(j => j.JobId == p1.JobId);
        var c2 = claimed.Single(j => j.JobId == p2.JobId);

        await store.ReportOutcomesAsync(
        [
            new OutcomeReport(c1.JobId, "w1", c1.Attempt, new JobOutcome.Success()),
            new OutcomeReport(c2.JobId, "w1", c2.Attempt, new JobOutcome.Failure(null, "boom")),
        ], T0);

        Assert.Equal(JobState.Scheduled, (await store.GetJobAsync(child1.JobId))!.State); // released
        Assert.Equal(JobState.Cancelled, (await store.GetJobAsync(child2.JobId))!.State); // cascade-cancelled
    }

    /// <summary>
    /// Certifies that a batched outcome report interrupted before the child-latch cascade rolls back
    /// whole: the parent stays Leased and the child stays latched, then a clean batch resolves it.
    /// </summary>
    [Fact]
    public async Task Clause_4_ReportOutcomes_Batch_CrashBeforeLatchCascade_RollsBackWhole()
    {
        var armed = await CreateFaultArmedStoreAsync("report-outcome");
        if (armed is null)
        {
            return; // interruption not simulable on this store
        }
        var store = await CreateStoreAsync();
        var parent = Job();
        await store.EnqueueAsync(parent, now: T0);
        var child = Job() with { Parents = [parent.JobId], Mode = DependencyMode.OnAnyTerminal };
        await store.EnqueueAsync(child, now: T0);
        var claimed = Assert.Single(await ClaimAsync(store, T0));

        await Assert.ThrowsAsync<FaultInjectedException>(() =>
            armed.ReportOutcomesAsync(
                [new OutcomeReport(claimed.JobId, "w1", claimed.Attempt, new JobOutcome.Success())], T0).AsTask());

        Assert.Equal(JobState.Leased, (await store.GetJobAsync(parent.JobId))!.State);
        var stillWaiting = await store.GetJobAsync(child.JobId);
        Assert.Equal(JobState.AwaitingParent, stillWaiting!.State);
        Assert.Equal(1, stillWaiting.ParentsRemaining);

        var results = await store.ReportOutcomesAsync(
            [new OutcomeReport(claimed.JobId, "w1", claimed.Attempt, new JobOutcome.Success())], T0);
        Assert.Equal(OutcomeResult.Applied, results[0].Result);
        Assert.Equal(JobState.Scheduled, (await store.GetJobAsync(child.JobId))!.State);
    }

    /// <summary>
    /// Certifies that an output blob of exactly MaxOutputBytes is accepted, and that a worker-reported
    /// Cancelled outcome terminalizes the job with its lease cleared and cause recorded.
    /// </summary>
    [Fact]
    public async Task Clause_5_6_ReportOutcome_OutputExactlyAtCap_IsAccepted_AndCancelledOutcome_Terminalizes()
    {
        var store = await CreateStoreAsync();

        // (a) Output of exactly the cap is accepted (inclusive bound) and reads back byte-identical.
        await store.EnqueueAsync(Job(), now: T0);
        var atCap = Assert.Single(await ClaimAsync(store, T0));
        var exact = Output(StoreBounds.Default.MaxOutputBytes);
        Assert.Equal(OutcomeResult.Applied, await store.ReportOutcomeAsync(
            atCap.JobId, "w1", atCap.Attempt, new JobOutcome.Success(), T0, output: exact));
        Assert.Equal(exact, (await store.GetJobOutputAsync(atCap.JobId))!.Value.ToArray());

        // (b) A worker-reported Cancelled outcome terminalizes: Cancelled, lease cleared, cause set.
        await store.EnqueueAsync(Job(), now: T0);
        var toCancel = Assert.Single(await ClaimAsync(store, T0));
        Assert.Equal(OutcomeResult.Applied, await store.ReportOutcomeAsync(
            toCancel.JobId, "w1", toCancel.Attempt, new JobOutcome.Cancelled("worker-abort"), T0));
        var cancelled = await store.GetJobAsync(toCancel.JobId);
        Assert.Equal(JobState.Cancelled, cancelled!.State);
        Assert.Null(cancelled.LeaseOwner);
        Assert.Equal(T0, cancelled.TerminalAt);
        Assert.Equal("worker-abort", cancelled.TerminalCause);
    }

    /// <summary>
    /// Certifies that a multi-job claim from one queue returns jobs ascending by due time — earliest-due
    /// first — regardless of enqueue order.
    /// </summary>
    [Fact]
    public async Task Clause_5_2_Claim_ReturnsBatch_AscendingByDueTime()
    {
        var store = await CreateStoreAsync();
        // Distinct due times, enqueued in REVERSE due order, so the ascending guarantee is not the
        // enqueue-sequence tiebreak masquerading as due order.
        await store.EnqueueAsync(Job(dueTime: T0.AddMinutes(3)), now: T0);
        await store.EnqueueAsync(Job(dueTime: T0.AddMinutes(2)), now: T0);
        await store.EnqueueAsync(Job(dueTime: T0.AddMinutes(1)), now: T0);

        var claimed = await ClaimAsync(store, T0.AddMinutes(10));
        Assert.Equal(
            [T0.AddMinutes(1), T0.AddMinutes(2), T0.AddMinutes(3)],
            claimed.Select(j => j.DueTime).ToList());
    }

    /// <summary>
    /// Certifies that a failed parent cascade-cancels its on-success descendants (recording the Cancelled
    /// transition and cause), reaches grandchildren, and cancels a two-parent child exactly once.
    /// </summary>
    [Fact]
    public async Task Clause_5_6_ChildLatchCascade_OnParentFailure_ReachesGrandchildren_AndCancelsOnce()
    {
        var store = await CreateStoreAsync();

        // A -> B -> C on-success chain: failing A cascade-cancels B and the grandchild C.
        var a = Job();
        await store.EnqueueAsync(a, now: T0);
        var b = Job() with { Parents = [a.JobId] };
        await store.EnqueueAsync(b, now: T0);
        var c = Job() with { Parents = [b.JobId] };
        await store.EnqueueAsync(c, now: T0);
        var claimedA = Assert.Single(await ClaimAsync(store, T0)); // only A is claimable
        await store.ReportOutcomeAsync(claimedA.JobId, "w1", claimedA.Attempt, new JobOutcome.Failure(null, "boom"), T0);

        var bRow = await store.GetJobAsync(b.JobId);
        Assert.Equal(JobState.Cancelled, bRow!.State);
        Assert.Equal("parent-failure:DeadLettered", bRow.TerminalCause);
        Assert.Contains(JobState.Cancelled, (await store.GetJobHistoryAsync(b.JobId)).Select(t => t.State)); // logged
        Assert.Equal(JobState.Cancelled, (await store.GetJobAsync(c.JobId))!.State); // cascade reached the grandchild

        // A child gated on TWO failing on-success parents is cancelled exactly once, cause from the first.
        var p1 = Job();
        var p2 = Job();
        await store.EnqueueAsync(p1, now: T0);
        await store.EnqueueAsync(p2, now: T0);
        var child = Job() with { Parents = [p1.JobId, p2.JobId] };
        await store.EnqueueAsync(child, now: T0);
        var claimed = await ClaimAsync(store, T0);
        var cp1 = claimed.Single(j => j.JobId == p1.JobId);
        var cp2 = claimed.Single(j => j.JobId == p2.JobId);
        await store.ReportOutcomeAsync(cp1.JobId, "w1", cp1.Attempt, new JobOutcome.Failure(null, "boom"), T0);
        await store.ReportOutcomeAsync(cp2.JobId, "w1", cp2.Attempt, new JobOutcome.Failure(null, "boom"), T0);
        var childRow = await store.GetJobAsync(child.JobId);
        Assert.Equal(JobState.Cancelled, childRow!.State);
        Assert.Equal("parent-failure:DeadLettered", childRow.TerminalCause); // from the FIRST failing parent
        Assert.Single(await store.GetJobHistoryAsync(child.JobId), t => t.State == JobState.Cancelled);
    }

    // ── §5.4 Heartbeat ──────────────────────────────────────────────────────────

    /// <summary>
    /// Certifies that a heartbeat renews the caller's held leases, surfaces a pending cancellation
    /// request, and renews nothing for a lease the caller does not hold.
    /// </summary>
    [Fact]
    public async Task Clause_5_4_Heartbeat_RenewsHeldLeases_AndSurfacesCancellation()
    {
        var store = await CreateStoreAsync();
        await store.EnqueueAsync(Job(), now: T0);
        var claimed = Assert.Single(await ClaimAsync(store, T0));
        await store.CancelJobAsync(claimed.JobId, "operator-cancel", T0);

        var beat = Assert.Single(await store.HeartbeatAsync("w1", [claimed.JobId], Lease, T0.AddSeconds(30)));
        Assert.True(beat.Renewed);
        Assert.True(beat.CancelRequested);
        Assert.Equal(T0.AddSeconds(30) + Lease, (await store.GetJobAsync(claimed.JobId))!.LeaseExpiry);

        // A lease this worker no longer holds renews nothing.
        var foreign = Assert.Single(await store.HeartbeatAsync("impostor", [claimed.JobId], Lease, T0.AddSeconds(31)));
        Assert.False(foreign.Renewed);
    }

    // ── §5.5 ExpireLeases ───────────────────────────────────────────────────────

    /// <summary>
    /// Certifies that the expiry sweep reschedules a lapsed lease while attempts remain and dead-
    /// letters it at the attempt ceiling — the claim itself having already counted the attempt.
    /// </summary>
    [Fact]
    public async Task Clause_5_5_ExpiredLease_Reschedules_OrDeadLettersAtTheCeiling()
    {
        var store = await CreateStoreAsync();
        await store.EnqueueAsync(Job(), now: T0);
        var claimed = Assert.Single(await ClaimAsync(store, T0)); // attempt 1 of 2

        var afterExpiry = T0 + Lease + TimeSpan.FromSeconds(1);
        Assert.Equal(1, await store.ExpireLeasesAsync(afterExpiry, maxJobs: 32, DefaultQueues, TwoAttempts));
        Assert.Equal(JobState.Scheduled, (await store.GetJobAsync(claimed.JobId))!.State);

        var second = Assert.Single(await ClaimAsync(store, afterExpiry.AddMinutes(2))); // attempt 2: the ceiling
        var secondExpiry = afterExpiry.AddMinutes(2) + Lease + TimeSpan.FromSeconds(1);
        Assert.Equal(1, await store.ExpireLeasesAsync(secondExpiry, maxJobs: 32, DefaultQueues, TwoAttempts));
        var job = await store.GetJobAsync(second.JobId);
        Assert.Equal(JobState.DeadLettered, job!.State);
        Assert.Equal(2, job.Attempt); // expiry-as-Attempt: the claim already counted it
    }

    /// <summary>
    /// Certifies that concurrent expiry sweeps dispose each lapsed lease exactly once: the per-sweep
    /// counts sum to the number of lapsed leases.
    /// </summary>
    [Fact]
    public async Task Clause_5_5_ConcurrentExpiry_DisposesEachLeaseExactlyOnce()
    {
        var store = await CreateStoreAsync();
        for (var i = 0; i < 16; i++)
        {
            await store.EnqueueAsync(Job(), now: T0);
        }
        await ClaimAsync(store, T0, maxJobs: 16);

        var afterExpiry = T0 + Lease + TimeSpan.FromSeconds(1);
        var sweeps = await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(_ => Task.Run(async () => await store.ExpireLeasesAsync(afterExpiry, maxJobs: 16, DefaultQueues, TwoAttempts))));

        Assert.Equal(16, sweeps.Sum());
    }

    /// <summary>
    /// Certifies that an expiry sweep applies its retry disposition only to the queues it names, so
    /// each worker group's jobs are disposed under their own group's policy.
    /// </summary>
    [Fact]
    public async Task Clause_5_5_ExpirySweep_AppliesEachGroupsOwnPolicy_ScopedToItsServedQueues()
    {
        // Two Worker Groups on different Queues hold different retry policies. Any node may run
        // the sweep, so the only way a job's ceiling and backoff can follow its OWN policy is for
        // a node to sweep just its served Queues with just its own disposition — proven here by
        // the aggressive group's sweep never reaching the lenient group's job.
        var store = await CreateStoreAsync();
        var groupA = Job(queue: "group-a"); // policy A below: dead-letters on first expiry
        var groupB = Job(queue: "group-b"); // policy B below: reschedules on expiry
        await store.EnqueueAsync(groupA, now: T0);
        await store.EnqueueAsync(groupB, now: T0);

        await store.ClaimAsync(new ClaimRequest("node-a", ["group-a"], 32, Lease, T0));
        await store.ClaimAsync(new ClaimRequest("node-b", ["group-b"], 32, Lease, T0));

        var afterExpiry = T0 + Lease + TimeSpan.FromSeconds(1);
        var deadLetterAtOnce = new RetryPolicy { MaxAttempts = 1 }.ToDisposition();
        var reschedule = new RetryPolicy { MaxAttempts = 5, Backoff = _ => TimeSpan.FromMinutes(1) }.ToDisposition();

        // node-a sweeps first with its aggressive policy, but scoped to its own Queue: it disposes
        // only its own job and leaves group-b's Lease untouched.
        Assert.Equal(1, await store.ExpireLeasesAsync(afterExpiry, maxJobs: 32, ["group-a"], deadLetterAtOnce));
        Assert.Equal(JobState.DeadLettered, (await store.GetJobAsync(groupA.JobId))!.State);
        Assert.Equal(JobState.Leased, (await store.GetJobAsync(groupB.JobId))!.State);

        // group-b's own node applies group-b's lenient policy: its job retries, never dead-letters
        // under the policy node-a happened to hold.
        Assert.Equal(1, await store.ExpireLeasesAsync(afterExpiry, maxJobs: 32, ["group-b"], reschedule));
        Assert.Equal(JobState.Scheduled, (await store.GetJobAsync(groupB.JobId))!.State);
    }

    /// <summary>
    /// Certifies that a single expiry sweep spanning multiple queues dead-letters every lapsed lease
    /// at once and cascades every dead-lettered parent's child latches — the set-based path holds for
    /// batches of two or more, not just a single row.
    /// </summary>
    [Fact]
    public async Task Clause_5_5_ExpirySweep_DeadLettersMultipleAcrossQueues_AndCascadesEveryChildLatch()
    {
        // The dynamically-built expiry SQL joins its served-queue list, its dead-letter VALUES rows,
        // and its parent-probe id list with ", " separators. Those separators are only observable at
        // 2+ items, so this drives ONE sweep that spans two queues, dead-letters two parents, and
        // cascades both their children (issue 0240 R2-B).
        var store = await CreateStoreAsync();
        var parentA = Job(queue: "sweep-a");
        var parentB = Job(queue: "sweep-b");
        await store.EnqueueAsync(parentA, now: T0);
        await store.EnqueueAsync(parentB, now: T0);
        var childA = Job(queue: "sweep-a") with { Parents = [parentA.JobId] };
        var childB = Job(queue: "sweep-b") with { Parents = [parentB.JobId] };
        await store.EnqueueAsync(childA, now: T0);
        await store.EnqueueAsync(childB, now: T0);

        // Claim both parents in one request spanning both queues (each to attempt 1).
        var claimed = await store.ClaimAsync(new ClaimRequest("w1", ["sweep-a", "sweep-b"], 32, Lease, T0));
        Assert.Equal(2, claimed.Count);

        // One sweep over BOTH queues with a dead-letter-at-once policy: both parents hit the ceiling.
        var afterExpiry = T0 + Lease + TimeSpan.FromSeconds(1);
        var deadLetterAtOnce = new RetryPolicy { MaxAttempts = 1 }.ToDisposition();
        Assert.Equal(2, await store.ExpireLeasesAsync(afterExpiry, maxJobs: 32, ["sweep-a", "sweep-b"], deadLetterAtOnce));

        Assert.Equal(JobState.DeadLettered, (await store.GetJobAsync(parentA.JobId))!.State);
        Assert.Equal(JobState.DeadLettered, (await store.GetJobAsync(parentB.JobId))!.State);
        // Both on-success children cascade to Cancelled off their dead-lettered parent (invariant I2).
        Assert.Equal(JobState.Cancelled, (await store.GetJobAsync(childA.JobId))!.State);
        Assert.Equal(JobState.Cancelled, (await store.GetJobAsync(childB.JobId))!.State);
    }

    // ── §5.8 Cancel ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Certifies that cancelling a pending job is immediate, cancelling a leased job only requests
    /// cooperative cancellation, and cancelling a terminal job is refused.
    /// </summary>
    [Fact]
    public async Task Clause_5_8_Cancel_IsImmediateWhenPending_CooperativeWhenLeased_RefusedWhenTerminal()
    {
        var store = await CreateStoreAsync();
        var pending = Job(dueTime: T0.AddHours(1));
        await store.EnqueueAsync(pending, now: T0);
        Assert.Equal(CancelResult.CancelledImmediately, await store.CancelJobAsync(pending.JobId, "changed my mind", T0));
        Assert.Equal(JobState.Cancelled, (await store.GetJobAsync(pending.JobId))!.State);

        var running = Job();
        await store.EnqueueAsync(running, now: T0);
        var claimed = Assert.Single(await ClaimAsync(store, T0));
        Assert.Equal(CancelResult.CancellationRequested, await store.CancelJobAsync(claimed.JobId, "operator", T0));
        Assert.Equal(JobState.Leased, (await store.GetJobAsync(claimed.JobId))!.State);

        await store.ReportOutcomeAsync(claimed.JobId, "w1", claimed.Attempt, new JobOutcome.Success(), T0);
        Assert.Equal(CancelResult.NotCancellable, await store.CancelJobAsync(claimed.JobId, "too late", T0));
    }

    // ── §5.7 Schedules & minting ────────────────────────────────────────────────

    /// <summary>
    /// Certifies that redefining an existing schedule updates its definition but preserves its cursor,
    /// so already-resolved ticks are never replayed or skipped.
    /// </summary>
    [Fact]
    public async Task Clause_5_7_RedefiningASchedule_PreservesItsCursor()
    {
        var store = await CreateStoreAsync();
        var schedule = Schedule("nightly", cursor: T0);
        await store.UpsertScheduleAsync(schedule);
        await store.UpsertScheduleAsync(schedule with { Cursor = T0.AddDays(9), Queue = "other" });

        var snapshot = Assert.Single(await store.ListSchedulesAsync());
        Assert.Equal(T0, snapshot.Schedule.Cursor); // never replays or skips resolved ticks
        Assert.Equal("other", snapshot.Schedule.Queue);
    }

    /// <summary>
    /// Certifies that minting is fenced on the expected cursor: a decision whose expected cursor is
    /// stale is skipped whole, so each tick mints exactly once.
    /// </summary>
    [Fact]
    public async Task Clause_5_7_MintDue_IsCursorFenced_SkippingStaleDecisionsWhole()
    {
        var store = await CreateStoreAsync();
        await store.UpsertScheduleAsync(Schedule("nightly", cursor: T0));
        var tick = T0.AddDays(1).AddHours(3);

        var decision = new MintDecision("nightly", ExpectedCursor: T0, NewCursor: tick, Ticks: [tick], SkippedTicks: []);
        Assert.Equal(1, await store.MintDueAsync([decision]));

        // The same decision again: stale ExpectedCursor, skipped whole — exactly-once minting.
        Assert.Equal(0, await store.MintDueAsync([decision]));
        var minted = await store.ListJobsAsync(new JobQuery { ScheduleId = "nightly" });
        Assert.Single(minted);
    }

    /// <summary>
    /// Certifies that the schedule listing omits the payload blob while a minted instance still carries
    /// the template payload.
    /// </summary>
    [Fact]
    public async Task Clause_5_7_ListSchedules_OmitsPayload_ButMintStillCarriesIt()
    {
        var store = await CreateStoreAsync();
        await store.UpsertScheduleAsync(Schedule("nightly", cursor: T0)); // payload "{}"
        var tick = T0.AddDays(1).AddHours(3);

        // The per-poll listing carries no payload blob (§5.7 hot path, issue 0039).
        var snapshot = Assert.Single(await store.ListSchedulesAsync());
        Assert.True(snapshot.Schedule.Payload.IsEmpty);

        // Minting re-reads the row, so the claimed instance still carries the template payload.
        Assert.Equal(1, await store.MintDueAsync(
            [new MintDecision("nightly", ExpectedCursor: T0, NewCursor: tick, Ticks: [tick], SkippedTicks: [])]));
        var claimed = Assert.Single(await ClaimAsync(store, tick));
        Assert.Equal("nightly", claimed.ScheduleId);
        Assert.Equal("{}"u8.ToArray(), claimed.Payload.ToArray());
    }

    // ── §5.9 Monitor reads ──────────────────────────────────────────────────────

    /// <summary>
    /// Certifies that the monitoring reads filter by state, queue, and wire name, honor the page bound,
    /// and count jobs per queue and state.
    /// </summary>
    [Fact]
    public async Task Clause_5_9_Reads_FilterAndCount_OverCommittedEffectsOnly()
    {
        var store = await CreateStoreAsync();
        await store.EnqueueAsync(Job(), now: T0);
        await store.EnqueueAsync(Job(queue: "other", wireName: "other-job"), now: T0);
        var claimed = Assert.Single(await ClaimAsync(store, T0));
        await store.ReportOutcomeAsync(claimed.JobId, "w1", claimed.Attempt, new JobOutcome.Success(), T0);

        Assert.Single(await store.ListJobsAsync(new JobQuery { State = JobState.Succeeded }));
        Assert.Single(await store.ListJobsAsync(new JobQuery { Queue = "other" }));
        Assert.Single(await store.ListJobsAsync(new JobQuery { WireName = "other-job" }));
        Assert.Single(await store.ListJobsAsync(new JobQuery { MaxResults = 1 }));

        var counts = await store.CountJobsAsync();
        Assert.Contains(new QueueStateCount("default", JobState.Succeeded, 1), counts);
        Assert.Contains(new QueueStateCount("other", JobState.Scheduled, 1), counts);
    }

    // ── Mutation teeth: boundary/misc contract facts (issue 0235) ────────────────

    /// <summary>
    /// Certifies that a concurrency limit set after an unlimited claim is honored on the very next claim,
    /// never over-claiming past the freshly-set limit.
    /// </summary>
    [Fact]
    public async Task Clause_5_10_ConcurrencyLimit_SetMidLife_IsHonoredOnTheNextClaim()
    {
        var store = await CreateStoreAsync();
        for (var i = 0; i < 3; i++)
        {
            await store.EnqueueAsync(Job(), now: T0);
        }

        // Warm any same-process 'unlimited queue' cache with a real claim on the still-unlimited queue.
        Assert.Single(await ClaimAsync(store, T0, maxJobs: 1));

        // A limit of 1 is set mid-life; one job is already leased, so the next claim must lease nothing.
        await store.SetConcurrencyLimitAsync("default", 1, "alice", T0);
        Assert.Empty(await ClaimAsync(store, T0));
    }

    /// <summary>
    /// Certifies that pausing a queue is honored on the very next claim and stays honored on repeated
    /// claims, even after an earlier claim warmed the queue's config cache.
    /// </summary>
    [Fact]
    public async Task Clause_5_8_PauseQueue_IsHonoredOnEveryClaim_EvenAfterAClaimWarmedTheQueue()
    {
        var store = await CreateStoreAsync();
        for (var i = 0; i < 3; i++)
        {
            await store.EnqueueAsync(Job(), now: T0);
        }

        // Warm any same-process 'unlimited queue' cache with a real claim while the queue is live.
        Assert.Single(await ClaimAsync(store, T0, maxJobs: 1));

        await store.PauseQueueAsync("default", "alice", T0);
        Assert.Empty(await ClaimAsync(store, T0)); // honored on the next claim
        Assert.Empty(await ClaimAsync(store, T0)); // never cached as claimable while paused
    }

    /// <summary>
    /// Certifies that a lease-expiry dead-letter at the attempt ceiling records the canonical, non-empty
    /// terminal cause rather than a null or blank one.
    /// </summary>
    [Fact]
    public async Task Clause_5_5_LeaseExpiryDeadLetter_RecordsTheAttemptCeilingCause()
    {
        var store = await CreateStoreAsync();
        await store.EnqueueAsync(Job(), now: T0);
        Assert.Single(await ClaimAsync(store, T0)); // attempt 1 of 2
        var afterFirst = T0 + Lease + TimeSpan.FromSeconds(1);
        await store.ExpireLeasesAsync(afterFirst, maxJobs: 32, DefaultQueues, TwoAttempts); // reschedules

        var second = Assert.Single(await ClaimAsync(store, afterFirst.AddMinutes(2))); // attempt 2 of 2
        var afterSecond = afterFirst.AddMinutes(2) + Lease + TimeSpan.FromSeconds(1);
        await store.ExpireLeasesAsync(afterSecond, maxJobs: 32, DefaultQueues, TwoAttempts); // dead-letters at ceiling

        var dead = await store.GetJobAsync(second.JobId);
        Assert.Equal(JobState.DeadLettered, dead!.State);
        Assert.Equal("Lease expired on attempt 2 (attempt ceiling reached).", dead.TerminalCause);
    }

    /// <summary>
    /// Certifies that cancelling a pending parent cascade-cancels its on-success child, and that
    /// cancelling a leased (running) job requests cooperative cancel and appends its operator audit.
    /// </summary>
    [Fact]
    public async Task Clause_5_8_CancelJob_CascadesToChildren_AndAuditsALeasedCancel()
    {
        var store = await CreateStoreAsync();

        // (a) Cancelling a Scheduled parent cascade-cancels its on-success AwaitingParent child.
        var parent = Job();
        await store.EnqueueAsync(parent, now: T0);
        var child = Job() with { Parents = [parent.JobId] };
        await store.EnqueueAsync(child, now: T0);
        Assert.Equal(CancelResult.CancelledImmediately, await store.CancelJobAsync(parent.JobId, "alice", T0));
        Assert.Equal(JobState.Cancelled, (await store.GetJobAsync(child.JobId))!.State);

        // (b) Cancelling a Leased job requests cooperative cancel AND writes the operator audit.
        var running = Job();
        await store.EnqueueAsync(running, now: T0);
        var claimed = Assert.Single(await ClaimAsync(store, T0));
        Assert.Equal(CancelResult.CancellationRequested, await store.CancelJobAsync(claimed.JobId, "bob", T0));
        var audit = Assert.Single(await store.ListAuditRecordsAsync(running.JobId.ToString()));
        Assert.Equal(OperatorAction.Cancel, audit.Action);
        Assert.Equal("bob", audit.Actor);
    }

    /// <summary>
    /// Certifies that terminal stamps at enqueue are set only for a job cancelled at enqueue: a normal
    /// Scheduled job carries none, while an on-success child of a dead parent is stamped terminal.
    /// </summary>
    [Fact]
    public async Task Clause_5_1_Enqueue_StampsTerminalAtAndCause_OnlyWhenCancelledAtEnqueue()
    {
        var store = await CreateStoreAsync();

        // A child cancelled AT ENQUEUE by an already-dead-lettered on-success parent is stamped terminal
        // at the enqueue instant, with the parent-failure cause. Run this while the parent is the only
        // Scheduled job so the claim leases exactly it.
        var parent = Job();
        await store.EnqueueAsync(parent, now: T0);
        var pClaim = Assert.Single(await ClaimAsync(store, T0));
        await store.ReportOutcomeAsync(pClaim.JobId, "w1", pClaim.Attempt, new JobOutcome.Failure(null, "boom"), T0);
        var enqueueAt = T0.AddMinutes(5);
        var child = Job() with { Parents = [parent.JobId] }; // OnSuccess against a dead-lettered parent
        await store.EnqueueAsync(child, now: enqueueAt);
        var childRow = await store.GetJobAsync(child.JobId);
        Assert.Equal(JobState.Cancelled, childRow!.State);
        Assert.Equal(enqueueAt, childRow.TerminalAt);
        Assert.Equal("parent-failure:DeadLettered", childRow.TerminalCause);

        // A normally-enqueued, non-terminal job carries no terminal stamps.
        var normal = Job();
        await store.EnqueueAsync(normal, now: T0);
        var normalRow = await store.GetJobAsync(normal.JobId);
        Assert.Equal(JobState.Scheduled, normalRow!.State);
        Assert.Null(normalRow.TerminalAt);
        Assert.Null(normalRow.TerminalCause);
    }

    /// <summary>
    /// Certifies that a predicate-less base query facets the whole population (equal to unscoped), and a
    /// multi-predicate base query scopes the facet by every predicate.
    /// </summary>
    [Fact]
    public async Task Clause_Facet_BaseQueryScoping_EmptyEqualsUnscoped_AndMultiPredicateScopesByAll()
    {
        var store = await CreateStoreAsync();
        await store.EnqueueAsync(Job(queue: "lab") with { Tags = JobTags.Empty.WithTag("tenant", "acme") }, T0);
        await store.EnqueueAsync(Job(queue: "lab") with { Tags = JobTags.Empty.WithTag("tenant", "acme") }, T0);
        await store.EnqueueAsync(Job(queue: "lab") with { Tags = JobTags.Empty.WithTag("tenant", "globex") }, T0);
        await store.EnqueueAsync(Job(queue: "other") with { Tags = JobTags.Empty.WithTag("tenant", "acme") }, T0);

        // (a) A non-null but predicate-less base query scopes nothing: it equals the unscoped facet.
        Assert.Equal(await store.FacetAsync("tenant"), await store.FacetAsync("tenant", new JobQuery()));

        // (b) A base query with TWO predicates (queue AND state) scopes by both.
        var lab = Assert.Single(await ClaimAsync(store, T0, maxJobs: 1, queue: "lab")); // leases one lab/acme
        await store.ReportOutcomeAsync(lab.JobId, "w1", lab.Attempt, new JobOutcome.Success(), T0);
        var scoped = await store.FacetAsync("tenant", new JobQuery { Queue = "lab", State = JobState.Scheduled });
        Assert.Equal([new TagFacet("acme", 1), new TagFacet("globex", 1)], scoped);
    }

    /// <summary>
    /// Certifies that the default oldest-first listing is strictly ascending by Sequence and pages the
    /// whole set exactly once, even after state transitions perturb physical row order.
    /// </summary>
    [Fact]
    public async Task Clause_5_9_ListJobs_DefaultListing_IsStrictlyAscendingBySequence_AfterPerturbation()
    {
        var store = await CreateStoreAsync();
        for (var i = 0; i < 5; i++)
        {
            await store.EnqueueAsync(Job(), now: T0);
        }

        // Perturb physical order with intervening transitions (claim three, succeed the middle one).
        var claimed = await ClaimAsync(store, T0, maxJobs: 3);
        await store.ReportOutcomeAsync(claimed[1].JobId, "w1", claimed[1].Attempt, new JobOutcome.Success(), T0);

        var seen = new List<long>();
        long? cursor = null;
        while (true)
        {
            var page = await store.ListJobsAsync(new JobQuery { MaxResults = 2, AfterSequence = cursor });
            if (page.Count == 0)
            {
                break;
            }
            seen.AddRange(page.Select(j => j.Sequence));
            cursor = page[^1].Sequence;
        }
        Assert.Equal(5, seen.Count);            // full set, once each
        Assert.Equal(seen.OrderBy(s => s), seen); // strictly ascending, never reordered
    }

    /// <summary>
    /// Certifies that a backslash in a suggest prefix is matched as a literal, never as the SQL escape
    /// character that would swallow the next character.
    /// </summary>
    [Fact]
    public async Task Clause_Suggest_Prefix_TreatsBackslashAsLiteral_NotTheSqlEscapeChar()
    {
        var store = await CreateStoreAsync();
        await store.EnqueueAsync(Job() with { Tags = JobTags.Empty.WithTag("path", "a\\b") }, T0); // literal backslash
        await store.EnqueueAsync(Job() with { Tags = JobTags.Empty.WithTag("path", "aXb") }, T0);   // decoy

        // 'a\' (a backslash) must match only the literal-backslash value, never the decoy a swallowing
        // escape char would sweep in.
        var s = await store.SuggestTagsAsync(new TagSuggestQuery { Key = "path", Prefix = "a\\" });
        Assert.Equal([new TagSuggestion("path", "a\\b")], s);
    }

    /// <summary>
    /// Certifies that a heartbeat from a worker that no longer holds the lease is not renewed and reports
    /// no pending cancellation.
    /// </summary>
    [Fact]
    public async Task Clause_4_Heartbeat_ForeignJob_IsNotRenewed_AndReportsNoCancelRequest()
    {
        var store = await CreateStoreAsync();
        await store.EnqueueAsync(Job(), now: T0);
        var claimed = Assert.Single(await ClaimAsync(store, T0)); // held by "w1"

        var result = Assert.Single(await store.HeartbeatAsync("impostor", [claimed.JobId], Lease, T0.AddSeconds(30)));
        Assert.False(result.Renewed);
        Assert.False(result.CancelRequested);
    }

    /// <summary>
    /// Certifies that the sequence cursor pages through the full filtered set exactly once, oldest
    /// first — the page bound caps one read, never the reachable rows.
    /// </summary>
    [Fact]
    public async Task Clause_5_9_TheAfterSequenceCursor_WalksTheFullFilteredSet()
    {
        var store = await CreateStoreAsync();
        var enqueued = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            var job = Job();
            enqueued.Add(job.JobId);
            await store.EnqueueAsync(job, now: T0);
        }

        // Page size 2: rows past the bound stay reachable via the cursor, exactly once,
        // in Sequence order. The page bound caps one read, never the reachable rows.
        var seen = new List<Guid>();
        long? cursor = null;
        for (var pages = 0; pages < 5; pages++)
        {
            var page = await store.ListJobsAsync(new JobQuery { MaxResults = 2, AfterSequence = cursor });
            if (page.Count == 0)
            {
                break;
            }
            Assert.True(page.Count <= 2);
            seen.AddRange(page.Select(j => j.JobId));
            cursor = page[^1].Sequence;
        }
        Assert.Equal(enqueued, seen);
    }

    /// <summary>
    /// Certifies that newest-first listing orders descending by sequence and its cursor walks the full
    /// set exactly once, toward older jobs.
    /// </summary>
    [Fact]
    public async Task Clause_5_9_NewestFirst_OrdersDescending_AndTheCursorWalksTheFullFilteredSet()
    {
        var store = await CreateStoreAsync();
        var enqueued = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            var job = Job();
            enqueued.Add(job.JobId);
            await store.EnqueueAsync(job, now: T0);
        }

        // NewestFirst is descending by Sequence: the direction-relative cursor continues
        // toward OLDER jobs, walking the full set exactly once in reverse enqueue order.
        var seen = new List<Guid>();
        long? cursor = null;
        for (var pages = 0; pages < 5; pages++)
        {
            var page = await store.ListJobsAsync(new JobQuery
            {
                MaxResults = 2,
                AfterSequence = cursor,
                SortDirection = JobSortDirection.NewestFirst,
            });
            if (page.Count == 0)
            {
                break;
            }
            Assert.True(page.Count <= 2);
            // Each page is itself descending by Sequence.
            Assert.True(page.SequenceEqual(page.OrderByDescending(j => j.Sequence)));
            seen.AddRange(page.Select(j => j.JobId));
            cursor = page[^1].Sequence;
        }
        Assert.Equal(Enumerable.Reverse(enqueued), seen);
    }

    /// <summary>
    /// Certifies that a Leased job's committed reads expose its lease owner and expiry, and that every
    /// other state clears both.
    /// </summary>
    [Fact]
    public async Task Clause_5_9_LeasedJobs_CarryLeaseOwnerAndExpiry_ClearedOnEveryOtherState()
    {
        // The "executing now" view (ADR 0009): a Leased job exposes who holds it and when the
        // Lease expires; every non-Leased state clears both. The adapters return them on the
        // committed read, not only on the claim that minted them.
        var store = await CreateStoreAsync();
        var job = Job();
        await store.EnqueueAsync(job, now: T0);

        // Scheduled: no Lease bookkeeping.
        var scheduled = await store.GetJobAsync(job.JobId);
        Assert.Null(scheduled!.LeaseOwner);
        Assert.Null(scheduled.LeaseExpiry);

        var claimed = Assert.Single(await ClaimAsync(store, T0));

        // Leased, read back through both Monitor reads: owner and expiry are present.
        var read = await store.GetJobAsync(job.JobId);
        Assert.Equal(JobState.Leased, read!.State);
        Assert.Equal("w1", read.LeaseOwner);
        Assert.Equal(T0 + Lease, read.LeaseExpiry);

        var listed = Assert.Single(await store.ListJobsAsync(new JobQuery { State = JobState.Leased }));
        Assert.Equal("w1", listed.LeaseOwner);
        Assert.Equal(T0 + Lease, listed.LeaseExpiry);

        // Terminal: the Lease is cleared.
        await store.ReportOutcomeAsync(claimed.JobId, "w1", claimed.Attempt, new JobOutcome.Success(), T0);
        var done = await store.GetJobAsync(job.JobId);
        Assert.Null(done!.LeaseOwner);
        Assert.Null(done.LeaseExpiry);
    }

    /// <summary>
    /// Certifies that the queue-settings read surfaces the paused flag and concurrency limit for
    /// exactly the queues with settings on record, tracking pauses, resumes, and cleared limits.
    /// </summary>
    [Fact]
    public async Task Clause_5_9_QueueSettings_SurfaceThePausedFlagAndConcurrencyLimitCap()
    {
        // The read-side mirror of the write-only Pause (§5.8) and Concurrency Limit (§5.10): set
        // them through the existing writes, then assert the new settings read returns them. A Queue
        // can carry either independently, and only Queues with settings on record appear.
        var store = await CreateStoreAsync();

        await store.PauseQueueAsync("orders", "alice", T0);
        await store.SetConcurrencyLimitAsync("orders", 4, "alice", T0);
        await store.SetConcurrencyLimitAsync("bulk", 2, "alice", T0); // limited but not paused
        await store.PauseQueueAsync("reports", "bob", T0); // paused but no limit

        var settings = await store.ListQueueSettingsAsync();

        var orders = Assert.Single(settings, s => s.Queue == "orders");
        Assert.True(orders.Paused);
        Assert.Equal(4, orders.ConcurrencyLimit);

        var bulk = Assert.Single(settings, s => s.Queue == "bulk");
        Assert.False(bulk.Paused);
        Assert.Equal(2, bulk.ConcurrencyLimit);

        var reports = Assert.Single(settings, s => s.Queue == "reports");
        Assert.True(reports.Paused);
        Assert.Null(reports.ConcurrencyLimit);

        // A Queue never touched by either write has no settings row.
        Assert.DoesNotContain(settings, s => s.Queue == "untouched");

        // Resume clears the flag; the limit it carried stays.
        await store.ResumeQueueAsync("orders", "alice", T0.AddMinutes(1));
        var resumed = Assert.Single(await store.ListQueueSettingsAsync(), s => s.Queue == "orders");
        Assert.False(resumed.Paused);
        Assert.Equal(4, resumed.ConcurrencyLimit);

        // Clearing the limit (null) leaves the row reachable via its still-set pause, and absent otherwise.
        await store.SetConcurrencyLimitAsync("bulk", null, "alice", T0);
        var clearedBulk = Assert.Single(await store.ListQueueSettingsAsync(), s => s.Queue == "bulk");
        Assert.Null(clearedBulk.ConcurrencyLimit);
        Assert.False(clearedBulk.Paused);
    }

    /// <summary>
    /// Certifies that the dependency-edge read returns a child's still-gating (non-terminal) parents
    /// and a parent's children, with edges resolving away as parents terminate.
    /// </summary>
    [Fact]
    public async Task Clause_5_9_DependencyEdges_ReturnTheStillGatingParents_AndTheChildren()
    {
        // The Dependency gating read (ADR 0009): given a child, the parents STILL non-terminal;
        // given a parent, its children. Edges resolve away as parents terminate, so the parent side
        // shrinks to the still-gating set — never the child's full original parent history.
        var store = await CreateStoreAsync();
        var parentA = Job();
        var parentB = Job();
        await store.EnqueueAsync(parentA, now: T0);
        await store.EnqueueAsync(parentB, now: T0);

        var child = Job() with { Parents = [parentA.JobId, parentB.JobId], Mode = DependencyMode.OnAnyTerminal };
        await store.EnqueueAsync(child, now: T0);

        // Child side: both parents still gate it.
        var initial = await store.GetDependencyEdgesAsync(child.JobId);
        Assert.Equal(
            new[] { parentA.JobId, parentB.JobId }.OrderBy(g => g).ToList(),
            initial.GatingParents.OrderBy(g => g).ToList());
        Assert.Empty(initial.Children);

        // Parent side: each parent names the child as its dependent.
        var parentAEdges = await store.GetDependencyEdgesAsync(parentA.JobId);
        Assert.Equal([child.JobId], parentAEdges.Children);
        Assert.Empty(parentAEdges.GatingParents);

        // One parent terminates: its edge is gone, so it no longer gates the child (the still-gating set).
        var leased = await ClaimAsync(store, T0);
        var a = leased.Single(j => j.JobId == parentA.JobId);
        await store.ReportOutcomeAsync(a.JobId, "w1", a.Attempt, new JobOutcome.Success(), T0);

        var afterOne = await store.GetDependencyEdgesAsync(child.JobId);
        Assert.Equal([parentB.JobId], afterOne.GatingParents);
        Assert.Empty(await store.GetDependencyEdgesAsync(parentA.JobId) is { } gone ? gone.Children : []);

        // The last parent terminates: the child releases and no edge gates it any longer.
        var b = leased.Single(j => j.JobId == parentB.JobId);
        await store.ReportOutcomeAsync(b.JobId, "w1", b.Attempt, new JobOutcome.Success(), T0);
        Assert.Equal(JobState.Scheduled, (await store.GetJobAsync(child.JobId))!.State);
        Assert.Empty((await store.GetDependencyEdgesAsync(child.JobId)).GatingParents);
    }

    // ── §5.11 Retention sweep ───────────────────────────────────────────────────

    /// <summary>
    /// Certifies that the retention purge deletes only jobs terminal at or before the cutoff — clocked
    /// on the terminal instant, never enqueue time — and only in the requested state class.
    /// </summary>
    [Fact]
    public async Task Clause_5_11_Purge_HonorsTheTerminalClock_AndTheStateClass()
    {
        var store = await CreateStoreAsync();

        // One job terminal at T0, one still Scheduled, one Dead-Lettered at T0.
        await store.EnqueueAsync(Job(), now: T0);
        await store.EnqueueAsync(Job(), now: T0);
        await store.EnqueueAsync(Job(dueTime: T0.AddDays(9)), now: T0);
        var claimed = await ClaimAsync(store, T0, maxJobs: 2);
        await store.ReportOutcomeAsync(claimed[0].JobId, "w1", claimed[0].Attempt, new JobOutcome.Success(), T0);
        await store.ReportOutcomeAsync(claimed[1].JobId, "w1", claimed[1].Attempt, new JobOutcome.Failure(null, "x"), T0);

        // Before the window: nothing purges — the clock is TerminalAt, never enqueue time.
        Assert.Equal(0, await store.PurgeTerminalAsync(
            TerminalStateClass.SucceededOrCancelled, T0.AddSeconds(-1), maxJobs: 32));

        // After: only the matching state class goes.
        Assert.Equal(1, await store.PurgeTerminalAsync(TerminalStateClass.SucceededOrCancelled, T0, maxJobs: 32));
        Assert.Null(await store.GetJobAsync(claimed[0].JobId));
        Assert.Equal(JobState.DeadLettered, (await store.GetJobAsync(claimed[1].JobId))!.State);
        Assert.Equal(1, await store.PurgeTerminalAsync(TerminalStateClass.DeadLetteredOrQuarantined, T0, maxJobs: 32));
        Assert.Null(await store.GetJobAsync(claimed[1].JobId));
    }

    /// <summary>
    /// Certifies that each purge pass deletes at most the requested maximum and repeated passes drain
    /// the backlog to zero.
    /// </summary>
    [Fact]
    public async Task Clause_5_11_Sweep_IsBounded_AndDrainsByRepeating()
    {
        var store = await CreateStoreAsync();
        for (var i = 0; i < 5; i++)
        {
            await store.EnqueueAsync(Job(), now: T0);
        }
        foreach (var job in await ClaimAsync(store, T0, maxJobs: 5))
        {
            await store.ReportOutcomeAsync(job.JobId, "w1", job.Attempt, new JobOutcome.Success(), T0);
        }

        // Each pass deletes at most maxJobs; repeating drains to zero.
        Assert.Equal(2, await store.PurgeTerminalAsync(TerminalStateClass.SucceededOrCancelled, T0, maxJobs: 2));
        Assert.Equal(2, await store.PurgeTerminalAsync(TerminalStateClass.SucceededOrCancelled, T0, maxJobs: 2));
        Assert.Equal(1, await store.PurgeTerminalAsync(TerminalStateClass.SucceededOrCancelled, T0, maxJobs: 2));
        Assert.Equal(0, await store.PurgeTerminalAsync(TerminalStateClass.SucceededOrCancelled, T0, maxJobs: 2));
    }

    // ── §4 Crash-mid-write atomicity (all-or-nothing under interruption) ─────────

    /// <summary>
    /// Builds a second store over the same database as this test's store, with the named test-only
    /// failpoint armed: the next multi-effect operation that reaches that point throws
    /// <see cref="FaultInjectedException"/>, aborting its open transaction before commit. The
    /// crash-mid-write tests use it to prove each multi-effect operation lands all-or-nothing. The
    /// default returns null, meaning mid-write interruption is not simulable on this store — for
    /// example, an in-memory store with no transaction to abort and no separate connection to read
    /// torn state from — and those tests return early.
    /// </summary>
    /// <param name="failpoint">The failpoint to arm: "claim", "enqueue", "report-outcome", "mint-due", or "lease-expiry".</param>
    /// <returns>The fault-armed store over this test's database, or null when interruption is not simulable on this store.</returns>
    protected virtual ValueTask<IJobStore?> CreateFaultArmedStoreAsync(string failpoint)
        => new((IJobStore?)null);

    // Contributor breadcrumb: the interleavings this forces are the 0193/0194 anomaly windows;
    // see docs/adapter-concurrency-review-checklist.md for the read-then-write pattern.
    /// <summary>
    /// Builds a second store over the same database whose test-only failpoint hook runs the given
    /// callback at each named point, letting a test park a transaction there (past a read, before
    /// commit) and release it on cue — forcing a specific interleaving that random parallelism
    /// almost never hits. The default returns null, meaning the interleaving is not simulable on
    /// this store — an in-memory store has no transaction to park, and a store that serializes
    /// every writer on one database-wide lock can never interleave the two operations — and those
    /// tests return early.
    /// </summary>
    /// <param name="onFailpoint">Invoked with the failpoint name and a cancellation token whenever an operation reaches a failpoint; awaiting inside parks that operation there.</param>
    /// <returns>The instrumented store over this test's database, or null when forced interleaving is not simulable on this store.</returns>
    protected virtual ValueTask<IJobStore?> CreateInterleavingStoreAsync(
        Func<string, CancellationToken, Task> onFailpoint)
        => new((IJobStore?)null);

    /// <summary>
    /// Acquires and holds, on a separate connection, the per-queue lock the adapter's claim read
    /// path and its operator config setters share to serialize a claim against a queue's first-ever
    /// pause or concurrency-limit write — a lock that must exist before any queue-settings row
    /// does, because a row lock on a not-yet-existent row does not reliably serialize against the
    /// first insert. The override must derive the lock key exactly as the store does, or the guard
    /// silently lapses. The default returns null, meaning the adapter takes no such lock (nothing
    /// to reproduce on a single-process or single-writer store); the test returns early.
    /// </summary>
    /// <param name="queue">The queue whose claim-vs-config lock to acquire and hold.</param>
    /// <returns>A handle whose disposal releases the held lock, or null when the adapter takes no such lock.</returns>
    protected virtual ValueTask<IAsyncDisposable?> HoldQueueConfigLockAsync(string queue)
        => new((IAsyncDisposable?)null);

    /// <summary>
    /// On a separate connection, inserts the given tag row for an already-committed job inside an
    /// open, uncommitted transaction, and returns a handle whose disposal commits it. This pins the
    /// concurrent-duplicate window: an in-flight store write of the same (job id, key, value)
    /// blocks on the uncommitted row and, when it commits, meets the committed key — which a sound
    /// insert must converge on idempotently, never surface as a raw duplicate-key error. The
    /// default returns null, meaning the race is not simulable on this store (no transaction, or
    /// one database-wide writer lock so the two inserts can never overlap); the test returns early.
    /// </summary>
    /// <param name="jobId">The already-committed job the duplicate tag row targets.</param>
    /// <param name="tag">The tag row to hold uncommitted.</param>
    /// <returns>A handle whose disposal commits the held row, or null when the race is not simulable on this store.</returns>
    protected virtual ValueTask<IAsyncDisposable?> HoldTagRowAsync(Guid jobId, JobTag tag)
        => new((IAsyncDisposable?)null);

    /// <summary>
    /// The workflow-edge twin of <see cref="HoldTagRowAsync"/>: on a separate connection, holds an
    /// uncommitted (workflow, parent, child) structural-edge row inside an open transaction and
    /// returns a handle whose disposal commits it, pinning the concurrent-duplicate window for edge
    /// inserts. The default returns null, meaning the race is not simulable on this store, exactly
    /// as for the tag row; the test returns early.
    /// </summary>
    /// <param name="workflowId">The workflow the held edge belongs to.</param>
    /// <param name="parentId">The held edge's parent job.</param>
    /// <param name="childId">The held edge's child job.</param>
    /// <returns>A handle whose disposal commits the held row, or null when the race is not simulable on this store.</returns>
    protected virtual ValueTask<IAsyncDisposable?> HoldEdgeRowAsync(Guid workflowId, Guid parentId, Guid childId)
        => new((IAsyncDisposable?)null);

    /// <summary>
    /// Certifies that a claim interrupted before commit rolls back whole: no lease bookkeeping
    /// survives, no attempt is consumed, and the job remains claimable.
    /// </summary>
    [Fact]
    public async Task Clause_4_Claim_CrashBeforeCommit_RollsBackTheLease_NeverHalfClaimed()
    {
        var armed = await CreateFaultArmedStoreAsync("claim");
        if (armed is null)
        {
            return; // §4: interruption not simulable on this store
        }
        var store = await CreateStoreAsync();
        var job = Job();
        await store.EnqueueAsync(job, now: T0);

        await Assert.ThrowsAsync<FaultInjectedException>(async () => await ClaimAsync(armed, T0));

        // All-or-nothing: the lease write rolled back with its transaction — no leased job
        // missing its lease, no consumed attempt.
        var stored = await store.GetJobAsync(job.JobId);
        Assert.Equal(JobState.Scheduled, stored!.State);
        Assert.Null(stored.LeaseOwner);
        Assert.Null(stored.LeaseExpiry);
        Assert.Equal(0, stored.Attempt);

        // The slot was never consumed: a clean claim still takes it.
        Assert.Single(await ClaimAsync(store, T0));
    }

    /// <summary>
    /// Certifies that a dependency enqueue interrupted between writing the job and its parent edge
    /// rolls back whole, leaving no orphaned job or latch and permitting a clean re-enqueue.
    /// </summary>
    [Fact]
    public async Task Clause_4_Enqueue_CrashBetweenJobAndEdges_LeavesNoOrphanedJobOrLatch()
    {
        var armed = await CreateFaultArmedStoreAsync("enqueue");
        if (armed is null)
        {
            return;
        }
        var store = await CreateStoreAsync();
        var parent = Job();
        await store.EnqueueAsync(parent, now: T0);

        // A dependency: the job row and its parent edge are written in one transaction.
        var child = Job() with { Parents = [parent.JobId] };
        await Assert.ThrowsAsync<FaultInjectedException>(async () => await armed.EnqueueAsync(child, now: T0));

        // Neither effect survives: no AwaitingParent job stranded without its edge.
        Assert.Null(await store.GetJobAsync(child.JobId));

        // No leftover edge corrupts a clean re-enqueue — it awaits its parent as usual.
        Assert.Equal(EnqueueResult.Ok, await store.EnqueueAsync(child, now: T0));
        Assert.Equal(JobState.AwaitingParent, (await store.GetJobAsync(child.JobId))!.State);
    }

    /// <summary>
    /// Certifies that an outcome report interrupted before the child-latch cascade rolls back whole:
    /// the parent stays Leased and the child stays latched — never a terminal parent over an unresolved
    /// latch.
    /// </summary>
    [Fact]
    public async Task Clause_4_ReportOutcome_CrashBeforeLatchCascade_LeavesParentLeased_AndChildLatched()
    {
        var armed = await CreateFaultArmedStoreAsync("report-outcome");
        if (armed is null)
        {
            return;
        }
        var store = await CreateStoreAsync();
        var parent = Job();
        await store.EnqueueAsync(parent, now: T0);
        var child = Job() with { Parents = [parent.JobId], Mode = DependencyMode.OnAnyTerminal };
        await store.EnqueueAsync(child, now: T0);
        var claimed = Assert.Single(await ClaimAsync(store, T0));

        await Assert.ThrowsAsync<FaultInjectedException>(async () =>
            await armed.ReportOutcomeAsync(claimed.JobId, "w1", claimed.Attempt, new JobOutcome.Success(), T0));

        // The terminal write and the latch cascade are one transaction (invariant I2): the parent
        // is still Leased and the child still awaits it — never a terminal parent over a latched child.
        Assert.Equal(JobState.Leased, (await store.GetJobAsync(parent.JobId))!.State);
        var stillWaiting = await store.GetJobAsync(child.JobId);
        Assert.Equal(JobState.AwaitingParent, stillWaiting!.State);
        Assert.Equal(1, stillWaiting.ParentsRemaining);

        // A clean outcome resolves the latch atomically, as always.
        Assert.Equal(OutcomeResult.Applied,
            await store.ReportOutcomeAsync(claimed.JobId, "w1", claimed.Attempt, new JobOutcome.Success(), T0));
        Assert.Equal(JobState.Scheduled, (await store.GetJobAsync(child.JobId))!.State);
    }

    /// <summary>
    /// Certifies that a mint interrupted after the cursor advance rolls back whole: the cursor is
    /// restored so the tick is still owed, and a clean retry mints exactly once.
    /// </summary>
    [Fact]
    public async Task Clause_4_MintDue_CrashAfterCursorAdvance_RestoresCursor_NeverLosingTicks()
    {
        var armed = await CreateFaultArmedStoreAsync("mint-due");
        if (armed is null)
        {
            return;
        }
        var store = await CreateStoreAsync();
        await store.UpsertScheduleAsync(Schedule("nightly", cursor: T0));
        var tick = T0.AddDays(1).AddHours(3);
        var decision = new MintDecision("nightly", ExpectedCursor: T0, NewCursor: tick, Ticks: [tick], SkippedTicks: []);

        await Assert.ThrowsAsync<FaultInjectedException>(async () => await armed.MintDueAsync([decision]));

        // Cursor advance and instance mint are one transaction: the cursor rolled back, so the tick
        // is still owed — never an advanced cursor with no minted instance (the tick lost forever).
        Assert.Equal(T0, Assert.Single(await store.ListSchedulesAsync()).Schedule.Cursor);
        Assert.Empty(await store.ListJobsAsync(new JobQuery { ScheduleId = "nightly" }));

        // The same decision now mints exactly once against the unmoved cursor.
        Assert.Equal(1, await store.MintDueAsync([decision]));
        Assert.Single(await store.ListJobsAsync(new JobQuery { ScheduleId = "nightly" }));
    }

    // ── §5.7/§5.8 Schedule round-trips (mutation teeth, issue 0232) ───────────────

    /// <summary>
    /// Certifies that skipped ticks carried by a mint decision are persisted and read back on the
    /// schedule snapshot — the DST/no-overlap skip audit trail round-trips.
    /// </summary>
    [Fact]
    public async Task Clause_5_7_MintDue_RecordsSkippedTicks_SurfacedOnTheScheduleSnapshot()
    {
        var store = await CreateStoreAsync();
        await store.UpsertScheduleAsync(Schedule("nightly", cursor: T0));

        var skipped = new[] { T0.AddHours(1), T0.AddHours(2), T0.AddHours(3) };
        Assert.Equal(0, await store.MintDueAsync(
            [new MintDecision("nightly", ExpectedCursor: T0, NewCursor: T0.AddDays(1), Ticks: [], SkippedTicks: skipped)]));

        var snapshot = Assert.Single(await store.ListSchedulesAsync());
        Assert.Equal(skipped.OrderBy(t => t), snapshot.Schedule.SkippedTicks.OrderBy(t => t));
    }

    /// <summary>
    /// Certifies that a schedule's snapshot reports no live instance when it has none, and reports one
    /// once it mints a live (non-terminal) job — the flag no-overlap minting depends on.
    /// </summary>
    [Fact]
    public async Task Clause_5_7_ScheduleSnapshot_HasLiveInstance_TracksTheNonTerminalInstance()
    {
        // HasLiveInstance drives no-overlap minting, so it must flip exactly with the presence of a
        // non-terminal instance — false when fresh, true after minting, false again once terminal
        // (issue 0240 R2-D).
        var store = await CreateStoreAsync();
        await store.UpsertScheduleAsync(Schedule("nightly", cursor: T0));

        // Freshly upserted: no instance yet.
        Assert.False(Assert.Single(await store.ListSchedulesAsync()).HasLiveInstance);

        // Mint one live (Scheduled) instance from a due tick.
        var tick = T0.AddDays(1);
        Assert.Equal(1, await store.MintDueAsync(
            [new MintDecision("nightly", ExpectedCursor: T0, NewCursor: tick, Ticks: [tick], SkippedTicks: [])]));
        Assert.True(Assert.Single(await store.ListSchedulesAsync()).HasLiveInstance);

        // Drive that instance terminal; the flag drops back to false.
        var minted = Assert.Single(await store.ListJobsAsync(new JobQuery { ScheduleId = "nightly" }));
        var claimed = Assert.Single(await ClaimAsync(store, tick));
        await store.ReportOutcomeAsync(claimed.JobId, "w1", claimed.Attempt, new JobOutcome.Success(), tick);
        Assert.False(Assert.Single(await store.ListSchedulesAsync()).HasLiveInstance);
    }

    /// <summary>
    /// Certifies that minting carries the schedule's multi-byte payload verbatim onto the minted job —
    /// never clipped to a single byte or otherwise truncated.
    /// </summary>
    [Fact]
    public async Task Clause_5_7_MintDue_CarriesTheSchedulePayloadVerbatim_ToTheMintedJob()
    {
        // The mint re-reads the schedule's stored payload and writes it to the new job; a distinctive
        // multi-byte blob proves it is neither clipped to one byte nor reshaped (issue 0240 R2-F).
        var store = await CreateStoreAsync();
        var payload = new byte[] { 0x7B, 0x22, 0x73, 0x22, 0x3A, 0x32, 0x7D }; // {"s":2}
        await store.UpsertScheduleAsync(Schedule("nightly", cursor: T0) with { Payload = payload });

        var tick = T0.AddDays(1);
        Assert.Equal(1, await store.MintDueAsync(
            [new MintDecision("nightly", ExpectedCursor: T0, NewCursor: tick, Ticks: [tick], SkippedTicks: [])]));

        var minted = Assert.Single(await store.ListJobsAsync(new JobQuery { ScheduleId = "nightly" }));
        Assert.Equal(payload, minted.Payload.ToArray());
    }

    /// <summary>
    /// Certifies that skipped ticks accumulate across mints and are bounded to the most-recent
    /// MaxRecordedSkippedTicks — the oldest age out, and the set is a union, not a difference.
    /// </summary>
    [Fact]
    public async Task Clause_5_7_MintDue_SkippedTicks_AccumulateAcrossMints_BoundedToMostRecent()
    {
        var store = await CreateStoreAsync();
        await store.UpsertScheduleAsync(Schedule("nightly", cursor: T0));
        var bound = StoreBounds.Default.MaxRecordedSkippedTicks;

        var firstSkips = Enumerable.Range(0, bound).Select(i => T0.AddMinutes(i)).ToArray();
        var secondSkips = Enumerable.Range(0, bound).Select(i => T0.AddDays(1).AddMinutes(i)).ToArray();
        Assert.Equal(0, await store.MintDueAsync(
            [new MintDecision("nightly", ExpectedCursor: T0, NewCursor: T0.AddDays(1), Ticks: [], SkippedTicks: firstSkips)]));
        Assert.Equal(0, await store.MintDueAsync(
            [new MintDecision("nightly", ExpectedCursor: T0.AddDays(1), NewCursor: T0.AddDays(2), Ticks: [], SkippedTicks: secondSkips)]));

        var retained = Assert.Single(await store.ListSchedulesAsync()).Schedule.SkippedTicks;
        Assert.Equal(bound, retained.Count);                                 // bounded to the cap
        Assert.Equal(secondSkips.OrderBy(t => t), retained.OrderBy(t => t)); // most-recent set kept
        Assert.DoesNotContain(firstSkips[0], retained);                      // oldest aged out
    }

    /// <summary>
    /// Certifies that a triggered instance records exactly one Scheduled transition, a repeat trigger at
    /// the same instant is idempotent, and a mint colliding with that instance adds no duplicate.
    /// </summary>
    [Fact]
    public async Task Clause_5_8_TriggerScheduleNow_AndMint_RecordExactlyOneScheduledTransition_PerInstance()
    {
        var store = await CreateStoreAsync();
        await store.UpsertScheduleAsync(Schedule("nightly", cursor: T0));
        var at = T0.AddHours(5);

        Assert.Equal(TriggerScheduleResult.Triggered, await store.TriggerScheduleNowAsync("nightly", "alice", at));
        var minted = Assert.Single(await store.ListJobsAsync(new JobQuery { ScheduleId = "nightly" }));
        Assert.Equal(JobState.Scheduled, Assert.Single(await store.GetJobHistoryAsync(minted.JobId)).State);

        // A repeat trigger at the same instant is idempotent — no second job, no second transition.
        Assert.Equal(TriggerScheduleResult.Triggered, await store.TriggerScheduleNowAsync("nightly", "alice", at));
        Assert.Single(await store.ListJobsAsync(new JobQuery { ScheduleId = "nightly" }));
        Assert.Single(await store.GetJobHistoryAsync(minted.JobId));

        // A mint tick colliding with the existing triggered instance (same instant ⇒ same id) mints
        // nothing and appends no duplicate Scheduled transition.
        Assert.Equal(0, await store.MintDueAsync(
            [new MintDecision("nightly", ExpectedCursor: T0, NewCursor: at, Ticks: [at], SkippedTicks: [])]));
        Assert.Single(await store.GetJobHistoryAsync(minted.JobId));
    }

    /// <summary>
    /// Certifies that triggering a schedule on demand carries its multi-byte payload verbatim onto the
    /// triggered job — never clipped to a single byte or otherwise truncated (the trigger-now twin of the
    /// scheduled-mint payload guarantee).
    /// </summary>
    [Fact]
    public async Task Clause_5_8_TriggerScheduleNow_CarriesThePayloadVerbatim_ToTheTriggeredJob()
    {
        // TriggerScheduleNow re-reads the schedule's stored payload and writes it to the triggered job on
        // a write path distinct from the timer-driven mint; a distinctive multi-byte blob proves it is
        // neither clipped to one byte nor reshaped (issue 0241).
        var store = await CreateStoreAsync();
        var payload = new byte[] { 0x7B, 0x22, 0x73, 0x22, 0x3A, 0x32, 0x7D }; // {"s":2}
        await store.UpsertScheduleAsync(Schedule("nightly", cursor: T0) with { Payload = payload });

        Assert.Equal(TriggerScheduleResult.Triggered, await store.TriggerScheduleNowAsync("nightly", "ops", T0));

        var triggered = Assert.Single(await store.ListJobsAsync(new JobQuery { ScheduleId = "nightly" }));
        Assert.Equal(payload, triggered.Payload.ToArray());
    }

    /// <summary>
    /// Certifies that removing a schedule deletes exactly that schedule, leaving the others intact.
    /// </summary>
    [Fact]
    public async Task Clause_5_7_RemoveSchedule_DeletesExactlyThatSchedule()
    {
        var store = await CreateStoreAsync();
        await store.UpsertScheduleAsync(Schedule("nightly", cursor: T0));
        await store.UpsertScheduleAsync(Schedule("hourly", cursor: T0));
        Assert.Equal(2, (await store.ListSchedulesAsync()).Count);

        await store.RemoveScheduleAsync("nightly");

        var remaining = Assert.Single(await store.ListSchedulesAsync());
        Assert.Equal("hourly", remaining.Schedule.ScheduleId); // the correct row was removed, the other kept
    }

    /// <summary>
    /// Certifies that a schedule's non-null time zone round-trips through upsert and the schedule listing.
    /// </summary>
    [Fact]
    public async Task Clause_5_7_Schedule_TimeZoneId_RoundTripsThroughListSchedules()
    {
        var store = await CreateStoreAsync();
        await store.UpsertScheduleAsync(Schedule("nightly", cursor: T0) with { TimeZoneId = "America/New_York" });

        var snapshot = Assert.Single(await store.ListSchedulesAsync());
        Assert.Equal("America/New_York", snapshot.Schedule.TimeZoneId);
    }

    /// <summary>
    /// Certifies that an expiry sweep interrupted before the child-latch cascade rolls back whole: the
    /// lease survives and the child stays latched until a clean sweep disposes both atomically.
    /// </summary>
    [Fact]
    public async Task Clause_4_LeaseExpiry_CrashBeforeLatchCascade_LeavesLeaseAndLatchIntact()
    {
        var armed = await CreateFaultArmedStoreAsync("lease-expiry");
        if (armed is null)
        {
            return;
        }
        var store = await CreateStoreAsync();
        var parent = Job();
        await store.EnqueueAsync(parent, now: T0);
        var child = Job() with { Parents = [parent.JobId], Mode = DependencyMode.OnAnyTerminal };
        await store.EnqueueAsync(child, now: T0);
        Assert.Single(await ClaimAsync(store, T0)); // parent leased, attempt 1

        var deadLetterAtOnce = new RetryPolicy { MaxAttempts = 1 }.ToDisposition();
        var afterExpiry = T0 + Lease + TimeSpan.FromSeconds(1);

        await Assert.ThrowsAsync<FaultInjectedException>(async () =>
            await armed.ExpireLeasesAsync(afterExpiry, maxJobs: 32, DefaultQueues, deadLetterAtOnce));

        // Dead-letter write and latch cascade are one transaction: the lease survives and the child
        // still awaits — never a dead-lettered parent over an unresolved child latch (invariant I2).
        Assert.Equal(JobState.Leased, (await store.GetJobAsync(parent.JobId))!.State);
        var stillWaiting = await store.GetJobAsync(child.JobId);
        Assert.Equal(JobState.AwaitingParent, stillWaiting!.State);
        Assert.Equal(1, stillWaiting.ParentsRemaining);

        // A clean sweep dead-letters the parent and releases the on-any-terminal child atomically.
        Assert.Equal(1, await store.ExpireLeasesAsync(afterExpiry, maxJobs: 32, DefaultQueues, deadLetterAtOnce));
        Assert.Equal(JobState.DeadLettered, (await store.GetJobAsync(parent.JobId))!.State);
        Assert.Equal(JobState.Scheduled, (await store.GetJobAsync(child.JobId))!.State);
    }

    // ── §5.8 Operator Actions (Requeue, Pause/Resume, TriggerScheduleNow, audit) ──

    /// <summary>
    /// Certifies that requeue returns a dead-lettered or quarantined job to Scheduled with a reset
    /// attempt budget and cleared terminal fields, and rejects live or unknown jobs without effect.
    /// </summary>
    [Fact]
    public async Task Clause_5_8_Requeue_RecoversDeadLetteredAndQuarantined_ResettingAttempt()
    {
        var store = await CreateStoreAsync();

        // Dead-letter one job (a failed Attempt with no retry instant).
        var dead = Job();
        await store.EnqueueAsync(dead, now: T0);
        var deadClaimed = Assert.Single(await ClaimAsync(store, T0));
        await store.ReportOutcomeAsync(deadClaimed.JobId, "w1", deadClaimed.Attempt, new JobOutcome.Failure(null, "boom"), T0);
        Assert.Equal(JobState.DeadLettered, (await store.GetJobAsync(dead.JobId))!.State);

        // Quarantine another (unroutable).
        var quarantined = Job();
        await store.EnqueueAsync(quarantined, now: T0);
        var qClaimed = Assert.Single(await ClaimAsync(store, T0));
        await store.ReportOutcomeAsync(qClaimed.JobId, "w1", qClaimed.Attempt, new JobOutcome.Unroutable("no handler"), T0);
        Assert.Equal(JobState.Quarantined, (await store.GetJobAsync(quarantined.JobId))!.State);

        var requeueTime = T0.AddHours(1);
        Assert.Equal(RequeueResult.Requeued, await store.RequeueAsync(dead.JobId, "alice", requeueTime));
        Assert.Equal(RequeueResult.Requeued, await store.RequeueAsync(quarantined.JobId, "alice", requeueTime));

        foreach (var id in new[] { dead.JobId, quarantined.JobId })
        {
            var requeued = await store.GetJobAsync(id);
            Assert.Equal(JobState.Scheduled, requeued!.State);
            Assert.Equal(0, requeued.Attempt); // requeue resets the Attempt budget (§3)
            Assert.Equal(requeueTime, requeued.DueTime);
            Assert.Null(requeued.TerminalAt);
            Assert.Null(requeued.TerminalCause);
        }

        // Both claim again as fresh Scheduled jobs.
        Assert.Equal(2, (await ClaimAsync(store, requeueTime)).Count);

        // A live job and an unknown id are rejected without effect.
        var live = Job();
        await store.EnqueueAsync(live, now: requeueTime);
        Assert.Equal(RequeueResult.NotRequeueable, await store.RequeueAsync(live.JobId, "alice", requeueTime));
        Assert.Equal(RequeueResult.NotRequeueable, await store.RequeueAsync(Guid.NewGuid(), "alice", requeueTime));
    }

    /// <summary>
    /// Certifies that a paused queue yields nothing to claim and that resuming it restores
    /// claimability.
    /// </summary>
    [Fact]
    public async Task Clause_5_8_PausedQueue_YieldsNothingToClaim_ResumeRestores()
    {
        var store = await CreateStoreAsync();
        await store.EnqueueAsync(Job(), now: T0);

        await store.PauseQueueAsync("default", "alice", T0);
        Assert.Empty(await ClaimAsync(store, T0)); // a Paused Queue yields nothing (§5.8)

        await store.ResumeQueueAsync("default", "alice", T0);
        Assert.Single(await ClaimAsync(store, T0)); // resumed: claimable again
    }

    /// <summary>
    /// Certifies that pausing a queue acts only on claiming: an already-leased job still heartbeats and
    /// reports its outcome.
    /// </summary>
    [Fact]
    public async Task Clause_5_8_Pause_LeavesAlreadyLeasedJobsUntouched()
    {
        var store = await CreateStoreAsync();
        await store.EnqueueAsync(Job(), now: T0);
        var leased = Assert.Single(await ClaimAsync(store, T0));

        await store.PauseQueueAsync("default", "alice", T0);

        // Pausing acts on claiming, never on a live Lease: the held job heartbeats and completes.
        var beat = Assert.Single(await store.HeartbeatAsync("w1", [leased.JobId], Lease, T0.AddSeconds(10)));
        Assert.True(beat.Renewed);
        Assert.Equal(OutcomeResult.Applied,
            await store.ReportOutcomeAsync(leased.JobId, "w1", leased.Attempt, new JobOutcome.Success(), T0.AddSeconds(20)));
    }

    // NOTE (issue 0196): a random Task.WhenAll "pause races 8 claimers" test used to live here. It was
    // RETIRED, not moved. Under random parallelism you cannot attribute a claim's commit order relative to
    // the pause commit, so its oracle could only assert the WEAKENED invariant (bounded work + pause holds
    // afterward) and thus PASSED whether or not the 0193 anomaly was present — a guard that never bites is
    // worse than none. Its real coverage is owned elsewhere, deterministically:
    //   • I1 no-double-claim under contention → Clause_5_2_ConcurrentClaimers_NeverDoubleClaim.
    //   • §5.8 serializability (a claim committing after the first pause yields nothing) →
    //     Clause_5_8_FirstPause_CommittingDuringAnInFlightClaim (behavioural, deterministic on Postgres)
    //     and Clause_5_8_Claim_SerializesAgainstFirstConfig_OnASharedKey (co-resident lock, deterministic
    //     on both SQL adapters — the SQL Server guard, whose UPDLOCK phantom behaviour is plan-dependent).
    // See docs/adapter-concurrency-review-checklist.md for the pattern every read-then-write site follows.

    // How long the forced-interleaving tests wait for the operator's first-config write to commit
    // while a claim is parked. In the buggy interleaving that write locks nothing and commits at once
    // (well under this); once claim-vs-first-config is serialized it blocks on the parked claim and
    // this timeout elapses — that non-commit is the pass signal, so it is generous, not tight.
    private static readonly TimeSpan FirstConfigCommitWindow = TimeSpan.FromSeconds(5);

    // How long the 0195 tests wait to confirm a store write is parked on a held uncommitted duplicate
    // row before releasing it. A write that is NOT blocked (the anomaly is impossible) would complete
    // well under this; that it does not is the premise, so this is generous, not tight.
    private static readonly TimeSpan ConcurrentDuplicateWindow = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Certifies that a claim serializes against a queue's first-ever pause or limit write on a shared
    /// per-queue lock that exists before any settings row does: while that key is held elsewhere, the
    /// claim stalls at its config read instead of proceeding.
    /// </summary>
    [Fact]
    public async Task Clause_5_8_Claim_SerializesAgainstFirstConfig_OnASharedKey_EvenWithNoQueueLimitsRow()
    {
        // The 0193 fix serializes a claim against a Queue's FIRST pause/limit on a per-Queue advisory /
        // application lock that exists BEFORE the queue_limits row does — because a row lock (FOR UPDATE /
        // UPDLOCK) on a not-yet-existent row does not reliably serialize against the first INSERT. This
        // pins that contract deterministically on both SQL adapters (the behavioural races below reproduce
        // it observably on Postgres, but on SQL Server the row lock's phantom-key behaviour is
        // plan-dependent): holding the shared key externally must STALL a claim at its config read.
        var held = await HoldQueueConfigLockAsync("default");
        if (held is null)
        {
            return; // adapter takes no claim-vs-config lock (see HoldQueueConfigLockAsync)
        }

        var store = await CreateStoreAsync();
        await store.EnqueueAsync(Job(), now: T0);

        // With the shared key held elsewhere, a claim that serializes claim-vs-first-config cannot get
        // past its config read; a claim that does NOT take the lock (pre-fix) returns at once — RED.
        var claim = Task.Run(async () => await ClaimAsync(store, T0));
        var completedWhileLockHeld = await Task.WhenAny(claim, Task.Delay(FirstConfigCommitWindow)) == claim;

        await held.DisposeAsync(); // release; a serialized claim now proceeds
        var claimed = await claim;

        Assert.False(completedWhileLockHeld,
            "a claim must serialize against first-config on the shared per-Queue lock (§5.8/I3, issue 0193)");
        Assert.Single(claimed); // once the key is free the claim completes normally
    }

    /// <summary>
    /// Certifies that when a queue's first-ever pause commits while a claim is in flight, the two
    /// serialize: a claim that commits after the pause yields nothing.
    /// </summary>
    [Fact]
    public async Task Clause_5_8_FirstPause_CommittingDuringAnInFlightClaim_IsSerialized_ClaimYieldsNothing()
    {
        // The 0193 window pinned deterministically: a claim parked PAST its queue_limits read (at the
        // "claim" failpoint, before commit) while the Queue's FIRST-EVER pause commits. A FOR UPDATE /
        // UPDLOCK on the not-yet-existent queue_limits row locks nothing, so the pause is not serialized
        // against this in-flight claim. §5.8 serializability: any claim that COMMITS AFTER the first
        // pause commit must yield nothing. RED on pre-0193 adapters; GREEN once the claim read path and
        // the pause setter take a shared advisory lock keyed on the Queue.
        var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var claimStore = await CreateInterleavingStoreAsync(async (name, _) =>
        {
            if (name != "claim")
            {
                return;
            }
            parked.TrySetResult();
            await release.Task;
        });
        if (claimStore is null)
        {
            return; // race not simulable on this store (see CreateInterleavingStoreAsync)
        }

        var store = await CreateStoreAsync();
        for (var i = 0; i < 5; i++)
        {
            await store.EnqueueAsync(Job(), now: T0);
        }

        // The claimer reaches the failpoint: past its (lock-nothing) config read, holding whatever
        // lock the read path takes, before its own commit.
        var claimTask = Task.Run(async () => await ClaimAsync(claimStore, T0, maxJobs: 2));
        await parked.Task;

        // Fire the first pause. It must NOT be awaited before releasing the claim: once serialized it
        // blocks on the lock the parked claim holds, so awaiting here would deadlock. Un-serialized it
        // commits at once — that it committed WHILE the claim was still parked is the anomaly's premise.
        var pauseTask = Task.Run(async () => await store.PauseQueueAsync("default", "alice", T0));
        var pausedWhileClaimParked =
            await Task.WhenAny(pauseTask, Task.Delay(FirstConfigCommitWindow)) == pauseTask;

        release.SetResult();
        var claimed = await claimTask;
        await pauseTask;

        if (pausedWhileClaimParked)
        {
            // The pause committed before this claim did: §5.8 says the claim must yield nothing.
            Assert.Empty(claimed);
        }

        // Whatever the interleaving, the pause holds afterward.
        Assert.Empty(await ClaimAsync(store, T0, worker: "after"));
    }

    /// <summary>
    /// Certifies that when a queue's first-ever concurrency limit commits while a claim is in flight,
    /// the two serialize: a claim that commits after the limit never leases past it.
    /// </summary>
    [Fact]
    public async Task Clause_5_10_FirstConcurrencyLimit_CommittingDuringAnInFlightClaim_IsSerialized_NoOverClaim()
    {
        // The I3 half of the 0193 window: a claim parked past its config read (which read "unlimited"
        // because no queue_limits row exists yet) while the Queue's FIRST-EVER concurrency limit commits.
        // Serializability: a claim that COMMITS AFTER the first limit commit must not lease past it.
        // RED on pre-0193 adapters (the parked claim leases its whole batch); GREEN once claim and the
        // limit setter share the advisory lock.
        const int limit = 2;
        const int batch = 5;
        var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var claimStore = await CreateInterleavingStoreAsync(async (name, _) =>
        {
            if (name != "claim")
            {
                return;
            }
            parked.TrySetResult();
            await release.Task;
        });
        if (claimStore is null)
        {
            return; // race not simulable on this store (see CreateInterleavingStoreAsync)
        }

        var store = await CreateStoreAsync();
        for (var i = 0; i < batch; i++)
        {
            await store.EnqueueAsync(Job(), now: T0);
        }

        var claimTask = Task.Run(async () => await ClaimAsync(claimStore, T0, maxJobs: batch));
        await parked.Task;

        // First-ever limit; not awaited before release, for the same reason as the pause test.
        var limitTask = Task.Run(async () => await store.SetConcurrencyLimitAsync("default", limit, "alice", T0));
        var limitSetWhileClaimParked =
            await Task.WhenAny(limitTask, Task.Delay(FirstConfigCommitWindow)) == limitTask;

        release.SetResult();
        var claimed = await claimTask;
        await limitTask;

        if (limitSetWhileClaimParked)
        {
            // The limit committed before this claim did: it must not have leased past the limit (I3).
            Assert.True(claimed.Count <= limit,
                $"a claim committing after the first concurrency limit leased {claimed.Count} > {limit} (I3)");
        }
    }

    // Parks EVERY create that reaches the workflow-apply failpoint until the caller releases them,
    // so two concurrent same-target creates can be held PAST their existence checks and then loosed to
    // race the inserts (issue 0194). Returns null on stores where the race is not simulable.
    private static async Task<(IJobStore Store, SemaphoreSlim Arrived, TaskCompletionSource Release)?>
        ParkedWorkflowCreateStoreAsync(Func<Func<string, CancellationToken, Task>, ValueTask<IJobStore?>> factory)
    {
        var arrived = new SemaphoreSlim(0);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = await factory(async (name, _) =>
        {
            if (name != "workflow-apply")
            {
                return;
            }
            arrived.Release();
            await release.Task;
        });
        return store is null ? null : (store, arrived, release);
    }

    /// <summary>
    /// Certifies that two concurrent creates of the same workflow id resolve to one Ok and one
    /// DuplicateWorkflow result — never a raw database uniqueness error — with exactly the winner's
    /// graph committed.
    /// </summary>
    [Fact]
    public async Task Clause_5_1_ConcurrentWorkflowCreate_SameId_YieldsOneOk_AndOneDuplicateWorkflow_NoThrow()
    {
        // The 0194 window: workflow create checks existence with an unlocked SELECT then INSERTs the
        // workflow row unconditionally, with nothing serializing the two. Two concurrent creates of the
        // same client-supplied WorkflowId both read "not exists" (neither insert is committed yet), then
        // both INSERT — and the loser hits a raw PK violation (Postgres 23505 / SQL Server 2627) that no
        // catch on this path maps to the defined WorkflowEnqueueResult.DuplicateWorkflow. RED now on both
        // SQL adapters (one create throws); GREEN once the duplicate is a defined result.
        var parked = await ParkedWorkflowCreateStoreAsync(CreateInterleavingStoreAsync);
        if (parked is null)
        {
            return; // race not simulable on this store (see CreateInterleavingStoreAsync)
        }
        var (store, arrived, release) = parked.Value;
        var reader = await CreateStoreAsync(); // truncates for a clean slate, then reads back the same DB

        var workflowId = Guid.NewGuid();
        // Same WorkflowId, DISJOINT member ids: the ONLY collision is the workflow row itself.
        var createA = new WorkflowDefinition { WorkflowId = workflowId, Members = [Job(wireName: "a")] };
        var createB = new WorkflowDefinition { WorkflowId = workflowId, Members = [Job(wireName: "b")] };

        var enqueueA = Task.Run(async () => await store.EnqueueWorkflowAsync(createA, T0));
        var enqueueB = Task.Run(async () => await store.EnqueueWorkflowAsync(createB, T0));

        // Hold both past their existence checks, then loose them to race the inserts.
        await arrived.WaitAsync();
        await arrived.WaitAsync();
        release.SetResult();

        var results = await Task.WhenAll(enqueueA, enqueueB); // throws here (RED) if the loser raises a raw PK error
        Assert.Equal(1, results.Count(r => r == WorkflowEnqueueResult.Ok));
        Assert.Equal(1, results.Count(r => r == WorkflowEnqueueResult.DuplicateWorkflow));

        // Exactly one workflow committed; the read sees the winner's single member, not the loser's.
        var graph = await reader.GetWorkflowAsync(workflowId);
        Assert.NotNull(graph);
        Assert.Single(graph!.Members);
    }

    /// <summary>
    /// Certifies that two distinct workflows racing to create the same member job resolve to one Ok and
    /// one DuplicateMember result — never a raw error — with the loser's whole graph rolled back.
    /// </summary>
    [Fact]
    public async Task Clause_5_1_ConcurrentWorkflowCreate_SharedMemberId_YieldsDuplicateMember_NoThrow()
    {
        // The member twin of 0194: two DISTINCT workflows created concurrently that share one member
        // JobId. Both pass the per-member existence check (the shared job is not yet inserted), then race
        // the member insert. The loser's insert comes back Duplicate — which the workflow path re-throws
        // as an InvalidOperationException ("commit failed") instead of the defined DuplicateMember. RED
        // now on both SQL adapters; GREEN once the member Duplicate maps to DuplicateMember.
        var parked = await ParkedWorkflowCreateStoreAsync(CreateInterleavingStoreAsync);
        if (parked is null)
        {
            return; // race not simulable on this store (see CreateInterleavingStoreAsync)
        }
        var (store, arrived, release) = parked.Value;
        var reader = await CreateStoreAsync(); // truncates for a clean slate, then reads back the same DB

        var sharedMember = Job(wireName: "shared");
        var createA = new WorkflowDefinition { WorkflowId = Guid.NewGuid(), Members = [sharedMember] };
        var createB = new WorkflowDefinition { WorkflowId = Guid.NewGuid(), Members = [sharedMember] };

        var enqueueA = Task.Run(async () => await store.EnqueueWorkflowAsync(createA, T0));
        var enqueueB = Task.Run(async () => await store.EnqueueWorkflowAsync(createB, T0));

        await arrived.WaitAsync();
        await arrived.WaitAsync();
        release.SetResult();

        var results = await Task.WhenAll(enqueueA, enqueueB); // throws here (RED) if the loser re-throws "commit failed"
        Assert.Equal(1, results.Count(r => r == WorkflowEnqueueResult.Ok));
        Assert.Equal(1, results.Count(r => r == WorkflowEnqueueResult.DuplicateMember));

        // The shared member exists exactly once, under the winning workflow — the loser rolled back whole.
        var member = await reader.GetJobAsync(sharedMember.JobId);
        Assert.NotNull(member);
        var winner = results[0] == WorkflowEnqueueResult.Ok ? createA : createB;
        var loser = winner == createA ? createB : createA;
        Assert.Equal(winner.WorkflowId, member!.WorkflowId);
        // The loser inserted its own (distinct-id) workflow row BEFORE the member conflict — assert that
        // row rolled back too, so "whole" is verified, not just the shared member's ownership.
        Assert.Null(await reader.GetWorkflowAsync(loser.WorkflowId));
    }

    /// <summary>
    /// Certifies that a tag write racing a concurrent insert of the identical tag row converges
    /// idempotently once the rival commits: the outcome still applies, exactly one tag row survives,
    /// and no duplicate-key error escapes.
    /// </summary>
    [Fact]
    public async Task Clause_5_6_ConcurrentDuplicateTagInsert_ConvergesIdempotently_NoThrow()
    {
        // 0195: the job_tags insert guards its primary key with an unlocked NOT EXISTS, which does not
        // serialize two concurrent writers of the same (job_id, key, value) — the loser hits a raw
        // duplicate-key throw where Postgres's ON CONFLICT DO NOTHING converges silently. A duplicate tag
        // row held uncommitted on a second connection forces exactly that window: the store's tag write
        // (riding a fenced success report) blocks on it, then meets the committed key when it is released.
        // RED on pre-fix SQL Server (the write throws 2627); GREEN once the insert swallows the duplicate.
        var tag = JobTag.Keyed("tenant", "acme");
        var store = await CreateStoreAsync();
        var job = Job();
        await store.EnqueueAsync(job, now: T0);
        var claimed = Assert.Single(await ClaimAsync(store, T0));

        var held = await HoldTagRowAsync(job.JobId, tag);
        if (held is null)
        {
            return; // race not simulable on this store (see HoldTagRowAsync)
        }

        // The tag write parks on the held uncommitted duplicate — it must not complete while it is held.
        var report = Task.Run(async () => await store.ReportOutcomeAsync(
            claimed.JobId, "w1", claimed.Attempt, new JobOutcome.Success(), T0,
            addedTags: JobTags.Empty.WithTag(tag.Key, tag.Value)));
        var completedWhileHeld = await Task.WhenAny(report, Task.Delay(ConcurrentDuplicateWindow)) == report;
        Assert.False(completedWhileHeld, "the tag write must block on the concurrent duplicate row (issue 0195)");

        await held.DisposeAsync(); // commit the duplicate; the parked write now meets the committed key
        Assert.Equal(OutcomeResult.Applied, await report); // converges — no raw duplicate-key throw

        // Exactly one tag row survives and the outcome applied atomically with it.
        var stored = await store.GetJobAsync(job.JobId);
        Assert.Equal(JobState.Succeeded, stored!.State);
        Assert.Equal([tag], stored.Tags.ToArray());
    }

    /// <summary>
    /// Certifies that a workflow append racing a concurrent insert of the identical structural edge
    /// converges idempotently: the append succeeds, the edge exists exactly once, and no duplicate-key
    /// error escapes.
    /// </summary>
    [Fact]
    public async Task Clause_5_1_ConcurrentDuplicateWorkflowEdge_ConvergesIdempotently_NoThrow()
    {
        // The workflow_edges twin of the tag race (0195): the structural-edge insert guards its primary
        // key with an unlocked NOT EXISTS, so two concurrent same-workflow appends of the same edge race
        // and the loser throws a raw duplicate key where Postgres converges. A duplicate edge held
        // uncommitted pins the window: an appending store write blocks on it, then meets the committed key.
        // RED on pre-fix SQL Server; GREEN once the edge insert swallows the duplicate.
        var store = await CreateStoreAsync();
        var parent = Job(wireName: "parent");
        var workflowId = Guid.NewGuid();
        Assert.Equal(WorkflowEnqueueResult.Ok, await store.EnqueueWorkflowAsync(
            new WorkflowDefinition { WorkflowId = workflowId, Members = [parent] }, T0));

        // The append adds child C with parent P — its structural edge is (workflowId, P, C).
        var child = Job(wireName: "child") with { Parents = [parent.JobId] };
        var held = await HoldEdgeRowAsync(workflowId, parent.JobId, child.JobId);
        if (held is null)
        {
            return; // race not simulable on this store (see HoldEdgeRowAsync)
        }

        var append = Task.Run(async () => await store.EnqueueWorkflowAsync(
            new WorkflowDefinition { WorkflowId = workflowId, Members = [child], IsAppend = true }, T0));
        var completedWhileHeld = await Task.WhenAny(append, Task.Delay(ConcurrentDuplicateWindow)) == append;
        Assert.False(completedWhileHeld, "the edge write must block on the concurrent duplicate row (issue 0195)");

        await held.DisposeAsync(); // commit the duplicate; the parked append now meets the committed key
        Assert.Equal(WorkflowEnqueueResult.Ok, await append); // converges — no raw duplicate-key throw

        // The edge exists exactly once and the appended child is now a member.
        var graph = await store.GetWorkflowAsync(workflowId);
        Assert.NotNull(graph);
        Assert.Single(graph!.Edges, e => e.Parent == parent.JobId && e.Child == child.JobId);
        Assert.Contains(graph.Members, m => m.JobId == child.JobId);
    }

    /// <summary>
    /// Certifies that triggering a schedule mints one instance due at the trigger instant, leaves the
    /// cursor and future ticks untouched, and rejects an unknown schedule without effect.
    /// </summary>
    [Fact]
    public async Task Clause_5_8_TriggerScheduleNow_MintsOneInstance_LeavingCursorAndFutureTicksUntouched()
    {
        var store = await CreateStoreAsync();
        await store.UpsertScheduleAsync(Schedule("nightly", cursor: T0));

        var triggerAt = T0.AddHours(7);
        Assert.Equal(TriggerScheduleResult.Triggered, await store.TriggerScheduleNowAsync("nightly", "alice", triggerAt));

        var instance = Assert.Single(await store.ListJobsAsync(new JobQuery { ScheduleId = "nightly" }));
        Assert.Equal(JobState.Scheduled, instance.State);
        Assert.Equal(triggerAt, instance.DueTime); // due now (the trigger instant)
        Assert.Equal("nightly", instance.ScheduleId);

        // The Cursor never moved, so the natural tick schedule is exactly as before.
        Assert.Equal(T0, Assert.Single(await store.ListSchedulesAsync()).Schedule.Cursor);

        // An unknown schedule is rejected without effect.
        Assert.Equal(TriggerScheduleResult.ScheduleNotFound, await store.TriggerScheduleNowAsync("ghost", "alice", triggerAt));
    }

    /// <summary>
    /// Certifies that every applied operator action — cancel, pause, resume, set-limit, requeue,
    /// trigger — appends an audit record carrying its actor, readable by target in chronological
    /// order, while a rejected action leaves no trace.
    /// </summary>
    [Fact]
    public async Task Clause_5_8_EveryOperatorAction_AppendsAnAuditRecord_ReadableByTarget()
    {
        var store = await CreateStoreAsync();

        // Cancel records the actor — not a hardcoded cause string — on the job and in the audit log.
        var job = Job(dueTime: T0.AddHours(1));
        await store.EnqueueAsync(job, now: T0);
        Assert.Equal(CancelResult.CancelledImmediately, await store.CancelJobAsync(job.JobId, "alice", T0));
        Assert.Equal("alice", (await store.GetJobAsync(job.JobId))!.TerminalCause);

        var cancelAudit = Assert.Single(await store.ListAuditRecordsAsync(job.JobId.ToString()));
        Assert.Equal("alice", cancelAudit.Actor);
        Assert.Equal(OperatorAction.Cancel, cancelAudit.Action);
        Assert.Equal(T0, cancelAudit.RecordedAt);

        // Pause, resume, then a Concurrency Limit set: all audited under the Queue target, in
        // chronological order — the limit write is audited even though nothing observable changed yet.
        await store.PauseQueueAsync("orders", "bob", T0.AddMinutes(1));
        await store.ResumeQueueAsync("orders", "carol", T0.AddMinutes(2));
        await store.SetConcurrencyLimitAsync("orders", 8, "frank", T0.AddMinutes(3));
        Assert.Equal(
            [(OperatorAction.PauseQueue, "bob"), (OperatorAction.ResumeQueue, "carol"),
             (OperatorAction.SetConcurrencyLimit, "frank")],
            (await store.ListAuditRecordsAsync("orders")).Select(a => (a.Action, a.Actor)).ToList());

        // Requeue audited under the job target.
        var dead = Job();
        await store.EnqueueAsync(dead, now: T0);
        var claimed = Assert.Single(await ClaimAsync(store, T0));
        await store.ReportOutcomeAsync(claimed.JobId, "w1", claimed.Attempt, new JobOutcome.Failure(null, "x"), T0);
        await store.RequeueAsync(dead.JobId, "dave", T0.AddMinutes(3));
        Assert.Equal(OperatorAction.Requeue,
            Assert.Single(await store.ListAuditRecordsAsync(dead.JobId.ToString())).Action);

        // TriggerScheduleNow audited under the schedule target.
        await store.UpsertScheduleAsync(Schedule("hourly", cursor: T0));
        await store.TriggerScheduleNowAsync("hourly", "erin", T0.AddMinutes(4));
        Assert.Equal(OperatorAction.TriggerScheduleNow,
            Assert.Single(await store.ListAuditRecordsAsync("hourly")).Action);

        // A rejected action leaves no trace, and an unrelated target sees nothing.
        await store.CancelJobAsync(Guid.NewGuid(), "mallory", T0);
        Assert.Empty(await store.ListAuditRecordsAsync("unrelated"));
    }

    // ── §5.12 Transition Log ─────────────────────────────────────────────────────

    /// <summary>
    /// Certifies that an enqueue appends exactly one Scheduled transition at attempt zero and ordinal
    /// zero, carrying no failure detail.
    /// </summary>
    [Fact]
    public async Task Clause_5_12_Enqueue_AppendsOneScheduledTransition_AtAttemptZero()
    {
        var store = await CreateStoreAsync();
        var job = Job();
        await store.EnqueueAsync(job, now: T0);

        var transition = Assert.Single(await store.GetJobHistoryAsync(job.JobId));
        Assert.Equal(0, transition.Ordinal);
        Assert.Equal(JobState.Scheduled, transition.State);
        Assert.Equal(0, transition.Attempt);
        Assert.Equal(T0, transition.Timestamp);
        Assert.Null(transition.FailureDetail); // no capture path until issue 0059
    }

    /// <summary>
    /// Certifies that enqueuing a gated child records the state the job actually entered —
    /// AwaitingParent, not Scheduled.
    /// </summary>
    [Fact]
    public async Task Clause_5_12_EnqueueAwaitingParent_RecordsTheActualResultingState()
    {
        var store = await CreateStoreAsync();
        var parent = Job();
        await store.EnqueueAsync(parent, now: T0);

        var child = Job() with { Parents = [parent.JobId] };
        await store.EnqueueAsync(child, now: T0);

        // The Transition Log records the RESULTING state — AwaitingParent, not Scheduled (§5.12).
        var transition = Assert.Single(await store.GetJobHistoryAsync(child.JobId));
        Assert.Equal(JobState.AwaitingParent, transition.State);
    }

    /// <summary>
    /// Certifies that a claim appends exactly one Leased transition carrying the incremented attempt.
    /// </summary>
    [Fact]
    public async Task Clause_5_12_Claim_AppendsExactlyOneLeasedTransition_WithIncrementedAttempt()
    {
        var store = await CreateStoreAsync();
        var job = Job();
        await store.EnqueueAsync(job, now: T0);
        var claimed = Assert.Single(await ClaimAsync(store, T0));

        var history = await store.GetJobHistoryAsync(job.JobId);
        Assert.Equal([JobState.Scheduled, JobState.Leased], history.Select(t => t.State).ToList());
        Assert.Equal(1, history[^1].Attempt); // claim is the start of an Attempt
        Assert.Equal(claimed.Attempt, history[^1].Attempt);
    }

    /// <summary>
    /// Certifies that each outcome appends its resulting state to the history: Succeeded on success,
    /// Quarantined on unroutable.
    /// </summary>
    [Fact]
    public async Task Clause_5_12_EveryOutcome_AppendsItsResultingState()
    {
        // Success → Succeeded, retry → Scheduled, dead-letter → DeadLettered, unroutable → Quarantined.
        var store = await CreateStoreAsync();

        var ok = Job();
        await store.EnqueueAsync(ok, now: T0);
        var okClaimed = Assert.Single(await ClaimAsync(store, T0));
        await store.ReportOutcomeAsync(okClaimed.JobId, "w1", okClaimed.Attempt, new JobOutcome.Success(), T0);
        Assert.Equal(JobState.Succeeded, (await store.GetJobHistoryAsync(ok.JobId))[^1].State);

        var quarantined = Job();
        await store.EnqueueAsync(quarantined, now: T0);
        var qClaimed = Assert.Single(await ClaimAsync(store, T0));
        await store.ReportOutcomeAsync(qClaimed.JobId, "w1", qClaimed.Attempt, new JobOutcome.Unroutable("no handler"), T0);
        Assert.Equal(JobState.Quarantined, (await store.GetJobHistoryAsync(quarantined.JobId))[^1].State);
    }

    /// <summary>
    /// Certifies that a lease expiry appends the disposed state — Scheduled on a retry, DeadLettered at
    /// the ceiling — at the attempt the claim already counted, timestamped at the sweep instant.
    /// </summary>
    [Fact]
    public async Task Clause_5_12_LeaseExpiry_AppendsTheDisposedState_AtThePostClaimAttempt()
    {
        var store = await CreateStoreAsync();
        await store.EnqueueAsync(Job(), now: T0);
        var claimed = Assert.Single(await ClaimAsync(store, T0)); // attempt 1 of 2

        var afterExpiry = T0 + Lease + TimeSpan.FromSeconds(1);
        await store.ExpireLeasesAsync(afterExpiry, maxJobs: 32, DefaultQueues, TwoAttempts);
        var rescheduled = (await store.GetJobHistoryAsync(claimed.JobId))[^1];
        Assert.Equal(JobState.Scheduled, rescheduled.State); // first expiry reschedules

        var second = Assert.Single(await ClaimAsync(store, afterExpiry.AddMinutes(2)));
        var secondExpiry = afterExpiry.AddMinutes(2) + Lease + TimeSpan.FromSeconds(1);
        await store.ExpireLeasesAsync(secondExpiry, maxJobs: 32, DefaultQueues, TwoAttempts);
        var dead = (await store.GetJobHistoryAsync(claimed.JobId))[^1];
        Assert.Equal(JobState.DeadLettered, dead.State);
        Assert.Equal(2, dead.Attempt); // expiry-as-Attempt
        Assert.Equal(secondExpiry, dead.Timestamp);
        _ = second;
    }

    /// <summary>
    /// Certifies that cancel appends Cancelled, requeue appends Scheduled at attempt zero, and a minted
    /// schedule instance's first transition is Scheduled.
    /// </summary>
    [Fact]
    public async Task Clause_5_12_Cancel_Requeue_AndMint_AppendTheirResultingStates()
    {
        var store = await CreateStoreAsync();

        // Immediate cancel of a pending job.
        var pending = Job(dueTime: T0.AddHours(1));
        await store.EnqueueAsync(pending, now: T0);
        await store.CancelJobAsync(pending.JobId, "alice", T0);
        Assert.Equal(
            [JobState.Scheduled, JobState.Cancelled],
            (await store.GetJobHistoryAsync(pending.JobId)).Select(t => t.State).ToList());

        // Dead-letter then requeue: requeue appends Scheduled at Attempt 0.
        var dead = Job();
        await store.EnqueueAsync(dead, now: T0);
        var claimed = Assert.Single(await ClaimAsync(store, T0));
        await store.ReportOutcomeAsync(claimed.JobId, "w1", claimed.Attempt, new JobOutcome.Failure(null, "boom"), T0);
        await store.RequeueAsync(dead.JobId, "alice", T0.AddHours(1));
        var requeued = (await store.GetJobHistoryAsync(dead.JobId))[^1];
        Assert.Equal(JobState.Scheduled, requeued.State);
        Assert.Equal(0, requeued.Attempt); // requeue resets the Attempt budget

        // Mint records the new instance's first Scheduled state.
        await store.UpsertScheduleAsync(Schedule("nightly", cursor: T0));
        var tick = T0.AddDays(1).AddHours(3);
        await store.MintDueAsync(
            [new MintDecision("nightly", ExpectedCursor: T0, NewCursor: tick, Ticks: [tick], SkippedTicks: [])]);
        var minted = Assert.Single(await store.ListJobsAsync(new JobQuery { ScheduleId = "nightly" }));
        var mintTransition = Assert.Single(await store.GetJobHistoryAsync(minted.JobId));
        Assert.Equal(JobState.Scheduled, mintTransition.State);
    }

    /// <summary>
    /// Certifies that a child's latch release appends a Scheduled transition after its AwaitingParent
    /// entry.
    /// </summary>
    [Fact]
    public async Task Clause_5_12_DependencyRelease_AppendsScheduled_ToTheChild()
    {
        var store = await CreateStoreAsync();
        var parent = Job();
        await store.EnqueueAsync(parent, now: T0);
        var child = Job() with { Parents = [parent.JobId] };
        await store.EnqueueAsync(child, now: T0);

        var claimed = Assert.Single(await ClaimAsync(store, T0));
        await store.ReportOutcomeAsync(claimed.JobId, "w1", claimed.Attempt, new JobOutcome.Success(), T0);

        // The latch release is a state change worth a transition: AwaitingParent → Scheduled (§5.12).
        Assert.Equal(
            [JobState.AwaitingParent, JobState.Scheduled],
            (await store.GetJobHistoryAsync(child.JobId)).Select(t => t.State).ToList());
    }

    /// <summary>
    /// Certifies that a full enqueue, claim, retry, claim, succeed lifecycle records one ordered,
    /// oldest-first timeline with the correct attempt on each entry and strictly increasing ordinals.
    /// </summary>
    [Fact]
    public async Task Clause_5_12_TheFullLifecycle_RecordsTheOrderedSequence()
    {
        // enqueue → claim → fail/retry → claim → succeed: one ordered, oldest-first timeline.
        var store = await CreateStoreAsync();
        var job = Job();
        await store.EnqueueAsync(job, now: T0);

        var first = Assert.Single(await ClaimAsync(store, T0));
        var retryAt = T0.AddMinutes(5);
        await store.ReportOutcomeAsync(first.JobId, "w1", first.Attempt, new JobOutcome.Failure(retryAt, "transient"), T0);

        var second = Assert.Single(await ClaimAsync(store, retryAt));
        await store.ReportOutcomeAsync(second.JobId, "w1", second.Attempt, new JobOutcome.Success(), retryAt);

        var history = await store.GetJobHistoryAsync(job.JobId);
        Assert.Equal(
            [JobState.Scheduled, JobState.Leased, JobState.Scheduled, JobState.Leased, JobState.Succeeded],
            history.Select(t => t.State).ToList());
        Assert.Equal([0, 1, 1, 2, 2], history.Select(t => t.Attempt).ToList());
        // Ordinals are strictly increasing, oldest first.
        Assert.Equal([0L, 1, 2, 3, 4], history.Select(t => t.Ordinal).ToList());
    }

    /// <summary>
    /// Certifies that the per-job history is bounded: past the cap the oldest entries are dropped while
    /// ordinals stay contiguous and never repeat.
    /// </summary>
    [Fact]
    public async Task Clause_5_12_TheLog_IsBoundedPerJobLife_DroppingOldestBeyondTheCap()
    {
        var store = await CreateStoreAsync();
        var cap = StoreBounds.Default.MaxTransitionsPerJob;
        var job = Job();
        await store.EnqueueAsync(job, now: T0);

        // Churn through many retries: each cycle appends Leased + Scheduled, so cap cycles drive
        // ~2*cap transitions — well past the per-job-life cap. Retry is driven via ReportOutcome
        // with a NextDueTime, so no attempt ceiling ever intervenes.
        var now = T0;
        for (var i = 0; i < cap; i++) // cap cycles ⇒ ~2*cap transitions, well over the cap
        {
            var claimed = Assert.Single(await ClaimAsync(store, now));
            // Report at the claim instant (the Lease is still live there), retrying further out
            // than the Lease so the next claim finds the job due again.
            var retryAt = now.AddMinutes(5);
            await store.ReportOutcomeAsync(claimed.JobId, "w1", claimed.Attempt, new JobOutcome.Failure(retryAt, "again"), now);
            now = retryAt;
        }

        var history = await store.GetJobHistoryAsync(job.JobId);
        Assert.Equal(cap, history.Count); // capped: oldest dropped
        // Oldest first, and ordinals are contiguous at the TAIL of all appended entries — the
        // earliest survivors were dropped, but the ordinal counter never repeated.
        Assert.True(history.Zip(history.Skip(1)).All(p => p.Second.Ordinal == p.First.Ordinal + 1));
        Assert.Equal(history[^1].Ordinal, history[0].Ordinal + cap - 1);
    }

    /// <summary>
    /// Certifies that purging a terminal job deletes its transition history with it.
    /// </summary>
    [Fact]
    public async Task Clause_5_11_Purge_DeletesTheJobsTransitionLog_WithTheJob()
    {
        var store = await CreateStoreAsync();
        var job = Job();
        await store.EnqueueAsync(job, now: T0);
        var claimed = Assert.Single(await ClaimAsync(store, T0));
        await store.ReportOutcomeAsync(claimed.JobId, "w1", claimed.Attempt, new JobOutcome.Success(), T0);
        Assert.NotEmpty(await store.GetJobHistoryAsync(job.JobId));

        // The Transition Log lives exactly as long as the job (§5.11, §5.12).
        Assert.Equal(1, await store.PurgeTerminalAsync(TerminalStateClass.SucceededOrCancelled, T0, maxJobs: 32));
        Assert.Null(await store.GetJobAsync(job.JobId));
        Assert.Empty(await store.GetJobHistoryAsync(job.JobId));
    }

    /// <summary>
    /// Certifies that an unknown job id yields an empty history rather than an error.
    /// </summary>
    [Fact]
    public async Task Clause_5_12_UnknownJob_HasAnEmptyTimeline()
    {
        var store = await CreateStoreAsync();
        Assert.Empty(await store.GetJobHistoryAsync(Guid.NewGuid()));
    }

    /// <summary>
    /// Certifies that a failure's diagnostic detail is recorded on the failing transition and on no
    /// other row — a detail passed with a Success outcome is dropped.
    /// </summary>
    [Fact]
    public async Task Clause_5_12_FailureOutcome_RecordsFailureDetail_OnlyOnTheFailingTransition()
    {
        // A Failure outcome carrying a failureDetail string writes it onto the failing transition
        // row, and onto NO other row (the prior Leased entry, and a later non-failure, stay null).
        var store = await CreateStoreAsync();
        var job = Job();
        await store.EnqueueAsync(job, now: T0);

        var first = Assert.Single(await ClaimAsync(store, T0));
        const string detail = "System.InvalidOperationException: boom\n   at Handler.Run()";
        var retryAt = T0.AddMinutes(5);
        await store.ReportOutcomeAsync(
            first.JobId, "w1", first.Attempt, new JobOutcome.Failure(retryAt, "boom"), T0, failureDetail: detail);

        var second = Assert.Single(await ClaimAsync(store, retryAt));
        await store.ReportOutcomeAsync(
            second.JobId, "w1", second.Attempt, new JobOutcome.Success(), retryAt, failureDetail: "ignored-on-success");

        var history = await store.GetJobHistoryAsync(job.JobId);
        // Scheduled, Leased, Scheduled (the failing/retry transition), Leased, Succeeded.
        Assert.Equal(detail, history.Single(t => t.State == JobState.Scheduled && t.FailureDetail is not null).FailureDetail);
        // The failing retry transition is the ONLY row with detail; every other row is null —
        // including the Success, whose detail argument is dropped because it is not a Failure.
        Assert.Single(history, t => t.FailureDetail is not null);
        Assert.Null(history[^1].FailureDetail); // Succeeded carries none
    }

    /// <summary>
    /// Certifies that a lease-expiry transition records no failure detail.
    /// </summary>
    [Fact]
    public async Task Clause_5_12_LeaseExpiry_RecordsTheTransition_WithNoFailureDetail()
    {
        // Lease expiry takes no detail argument: the disposed transition records null detail.
        var store = await CreateStoreAsync();
        await store.EnqueueAsync(Job(), now: T0);
        var claimed = Assert.Single(await ClaimAsync(store, T0));

        var afterExpiry = T0 + Lease + TimeSpan.FromSeconds(1);
        await store.ExpireLeasesAsync(afterExpiry, maxJobs: 32, DefaultQueues, TwoAttempts);

        var rescheduled = (await store.GetJobHistoryAsync(claimed.JobId))[^1];
        Assert.Equal(JobState.Scheduled, rescheduled.State);
        Assert.Null(rescheduled.FailureDetail);
    }

    /// <summary>
    /// Certifies that over-limit failure detail is truncated to the byte bound as a prefix of the input
    /// while the outcome still applies — diagnostics are truncated, never rejected.
    /// </summary>
    [Fact]
    public async Task Clause_5_12_FailureDetail_BeyondTheCap_IsTruncated_NeverRejected()
    {
        // Failure Detail is diagnostics, so an oversize string is TRUNCATED (the outcome still
        // applies), bounded to MaxFailureDetailBytes UTF-8 bytes, and is a prefix of the input.
        var store = await CreateStoreAsync();
        var job = Job();
        await store.EnqueueAsync(job, now: T0);
        var claimed = Assert.Single(await ClaimAsync(store, T0));

        var cap = StoreBounds.Default.MaxFailureDetailBytes;
        var oversized = new string('x', cap + 5_000); // well past the cap (ASCII ⇒ 1 byte each)
        var result = await store.ReportOutcomeAsync(
            claimed.JobId, "w1", claimed.Attempt, new JobOutcome.Failure(null, "boom"), T0, failureDetail: oversized);
        Assert.Equal(OutcomeResult.Applied, result); // never rejected

        var dead = (await store.GetJobHistoryAsync(job.JobId))[^1];
        Assert.Equal(JobState.DeadLettered, dead.State);
        Assert.NotNull(dead.FailureDetail);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(dead.FailureDetail!) <= cap);
        Assert.StartsWith(dead.FailureDetail!, oversized, StringComparison.Ordinal);
    }

    // ── §5.12 Job History Policy (ADR 0011) ──────────────────────────────────────

    /// <summary>
    /// Certifies that with history recording off, no state-changing operation — enqueue, claim,
    /// outcome, expiry, cancel, requeue, or mint — writes a transition row.
    /// </summary>
    [Fact]
    public async Task Clause_5_12_HistoryPolicyOff_RecordsNoTransitions_FromAnyOperation()
    {
        // Off (ADR 0011): the policy gates WRITES — no transition row from any state-changing op.
        // Exercise enqueue, claim, outcome, expire, cancel, requeue, and mint, then assert empty.
        var store = await CreateStoreAsync(JobHistoryPolicy.Off);

        // enqueue + claim + a failing outcome (carrying detail) + a successful one.
        var ok = Job();
        await store.EnqueueAsync(ok, now: T0);
        var okClaimed = Assert.Single(await ClaimAsync(store, T0));
        await store.ReportOutcomeAsync(
            okClaimed.JobId, "w1", okClaimed.Attempt, new JobOutcome.Failure(T0.AddMinutes(5), "boom"), T0,
            failureDetail: "stack");
        var okSecond = Assert.Single(await ClaimAsync(store, T0.AddMinutes(5)));
        await store.ReportOutcomeAsync(okSecond.JobId, "w1", okSecond.Attempt, new JobOutcome.Success(), T0.AddMinutes(5));
        Assert.Empty(await store.GetJobHistoryAsync(ok.JobId));

        // lease expiry.
        var expiring = Job();
        await store.EnqueueAsync(expiring, now: T0);
        await ClaimAsync(store, T0);
        await store.ExpireLeasesAsync(T0 + Lease + TimeSpan.FromSeconds(1), maxJobs: 32, DefaultQueues, TwoAttempts);
        Assert.Empty(await store.GetJobHistoryAsync(expiring.JobId));

        // immediate cancel.
        var pending = Job(dueTime: T0.AddHours(1));
        await store.EnqueueAsync(pending, now: T0);
        await store.CancelJobAsync(pending.JobId, "alice", T0);
        Assert.Empty(await store.GetJobHistoryAsync(pending.JobId));

        // dead-letter then requeue.
        var dead = Job();
        await store.EnqueueAsync(dead, now: T0);
        var deadClaimed = Assert.Single(await ClaimAsync(store, T0));
        await store.ReportOutcomeAsync(deadClaimed.JobId, "w1", deadClaimed.Attempt, new JobOutcome.Failure(null, "boom"), T0);
        await store.RequeueAsync(dead.JobId, "alice", T0.AddHours(1));
        Assert.Empty(await store.GetJobHistoryAsync(dead.JobId));

        // mint.
        await store.UpsertScheduleAsync(Schedule("nightly", cursor: T0));
        var tick = T0.AddDays(1).AddHours(3);
        await store.MintDueAsync(
            [new MintDecision("nightly", ExpectedCursor: T0, NewCursor: tick, Ticks: [tick], SkippedTicks: [])]);
        var minted = Assert.Single(await store.ListJobsAsync(new JobQuery { ScheduleId = "nightly" }));
        Assert.Empty(await store.GetJobHistoryAsync(minted.JobId));
    }

    /// <summary>
    /// Certifies that the transitions-only history policy records the full state sequence but stores
    /// null failure detail even when a detail string is supplied.
    /// </summary>
    [Fact]
    public async Task Clause_5_12_HistoryPolicyTransitions_RecordsRows_ButForcesFailureDetailNull()
    {
        // Transitions (the middle rung): transitions record, but a Failure outcome carrying a detail
        // string records the failing row with FailureDetail == null. Failure Detail is the inner rung.
        var store = await CreateStoreAsync(JobHistoryPolicy.Transitions);
        var job = Job();
        await store.EnqueueAsync(job, now: T0);
        var claimed = Assert.Single(await ClaimAsync(store, T0));
        await store.ReportOutcomeAsync(
            claimed.JobId, "w1", claimed.Attempt, new JobOutcome.Failure(null, "boom"), T0,
            failureDetail: "System.InvalidOperationException: boom\n   at Handler.Run()");

        var history = await store.GetJobHistoryAsync(job.JobId);
        // The transitions still record — Scheduled, Leased, DeadLettered (full state sequence).
        Assert.Equal(
            [JobState.Scheduled, JobState.Leased, JobState.DeadLettered],
            history.Select(t => t.State).ToList());
        // But the failing transition carries NO detail even though a string was passed.
        Assert.Equal(JobState.DeadLettered, history[^1].State);
        Assert.All(history, t => Assert.Null(t.FailureDetail));
    }

    /// <summary>
    /// Certifies that the transitions-only history policy forces failure detail to null on the BATCHED
    /// report path exactly as it does on the single-report path.
    /// </summary>
    [Fact]
    public async Task Clause_5_12_HistoryPolicyTransitions_ForcesFailureDetailNull_OnTheBatchPath()
    {
        // Twin of the single-report fact above, but on the BATCH path (ReportOutcomesAsync ->
        // RecordTransitionsBatchAsync): the detail-dropping ternary is policy-gated and only
        // observable under Transitions, so this is the fact that gives it teeth (issue 0238).
        var store = await CreateStoreAsync(JobHistoryPolicy.Transitions);
        var job = Job();
        await store.EnqueueAsync(job, now: T0);
        var claimed = Assert.Single(await ClaimAsync(store, T0));

        await store.ReportOutcomesAsync(
            [new OutcomeReport(claimed.JobId, "w1", claimed.Attempt, new JobOutcome.Failure(null, "boom"))
                { FailureDetail = "System.InvalidOperationException: boom\n   at Handler.Run()" }], T0);

        var history = await store.GetJobHistoryAsync(job.JobId);
        Assert.Equal(
            [JobState.Scheduled, JobState.Leased, JobState.DeadLettered],
            history.Select(t => t.State).ToList());
        // The failing row carries NO detail even though a string was supplied through the batch API.
        Assert.All(history, t => Assert.Null(t.FailureDetail));
    }

    /// <summary>
    /// Certifies that turning history off is configuration, not a migration: history reads still answer
    /// with an empty timeline rather than erroring.
    /// </summary>
    [Fact]
    public async Task Clause_5_12_HistoryPolicyOff_RequiresNoSchemaMigration_TableAlwaysPresent()
    {
        // Flipping to Off is config, not a migration: the transition table is created regardless of
        // policy, so an Off store still answers GetJobHistory (with an empty list), never errors.
        var store = await CreateStoreAsync(JobHistoryPolicy.Off);
        var job = Job();
        await store.EnqueueAsync(job, now: T0);
        // No throw — the table exists; Off merely wrote no rows.
        Assert.Empty(await store.GetJobHistoryAsync(job.JobId));
    }

    // ── §5.13 Observer-delivery capability (ADR 0017) ────────────────────────────

    private static ValueTask<ObserverClaim> ClaimObsAsync(
        IJobStore store, string observerId, IReadOnlyList<JobState> states, DateTimeOffset now,
        string worker = "w1", string? wireName = null, string? queue = null, int maxRows = 16)
        => store.ClaimObserverDeliveriesAsync(
            new ObserverClaimRequest(observerId, states, wireName, queue, worker, maxRows, Lease, now));

    private static ValueTask ReportObsAsync(
        IJobStore store, string observerId, DateTimeOffset now, string worker, params ObserverDeliveryOutcome[] outcomes)
        => store.ReportObserverDeliveriesAsync(new ObserverDeliveryReport(observerId, worker, outcomes, now));

    // Drives one job Scheduled → Leased → Succeeded, recording three Transitions.
    private static async Task<Guid> SucceedAsync(
        IJobStore store, DateTimeOffset now, string wireName = "conformance-job", string queue = "default")
    {
        var job = Job(wireName, queue);
        await store.EnqueueAsync(job, now);
        var claimed = Assert.Single(await ClaimAsync(store, now, queue: queue));
        await store.ReportOutcomeAsync(claimed.JobId, "w1", claimed.Attempt, new JobOutcome.Success(), now);
        return job.JobId;
    }

    /// <summary>
    /// Certifies that an observer that has never claimed reports a cursor of -1.
    /// </summary>
    [Fact]
    public async Task Clause_5_13_UnknownObserver_HasCursorMinusOne()
    {
        var store = await CreateStoreAsync();
        Assert.Equal(-1, await store.GetObserverCursorAsync("never-claimed"));
    }

    /// <summary>
    /// Certifies the observer happy path: a matching transition is delivered once, reporting it
    /// delivered advances the cursor past it, and a re-claim finds nothing more.
    /// </summary>
    [Fact]
    public async Task Clause_5_13_HappyPath_DeliversEachMatchingTransitionOnce_ThenAdvancesTheCursor()
    {
        var store = await CreateStoreAsync();
        var jobId = await SucceedAsync(store, T0);

        var claim = await ClaimObsAsync(store, "obs", [JobState.Succeeded], T0);
        Assert.True(claim.Acquired);
        var delivery = Assert.Single(claim.Deliveries);
        Assert.Equal(jobId, delivery.JobId);
        Assert.Equal(JobState.Succeeded, delivery.State);
        Assert.Equal(1, delivery.DeliveryAttempt); // the claim starts the first delivery Attempt

        await ReportObsAsync(store, "obs", T0, "w1", new ObserverDeliveryOutcome(delivery.Position, ObserverDeliveryDisposition.Delivered));
        Assert.Equal(delivery.Position, await store.GetObserverCursorAsync("obs"));

        // The cursor passed it: a re-claim finds nothing more to deliver.
        var again = await ClaimObsAsync(store, "obs", [JobState.Succeeded], T0);
        Assert.Empty(again.Deliveries);
    }

    /// <summary>
    /// Certifies that an observer's live claim lease excludes a second node, so exactly one node
    /// delivers at a time.
    /// </summary>
    [Fact]
    public async Task Clause_5_13_ClaimLease_ExcludesASecondNode_SoTheHappyPathDeliversOnce()
    {
        var store = await CreateStoreAsync();
        await SucceedAsync(store, T0);

        var first = await ClaimObsAsync(store, "obs", [JobState.Succeeded], T0, worker: "node-a");
        Assert.True(first.Acquired);
        Assert.NotEmpty(first.Deliveries);

        // A second node, while the Lease is live, advances nothing — exactly one node delivers (§5.13).
        var second = await ClaimObsAsync(store, "obs", [JobState.Succeeded], T0, worker: "node-b");
        Assert.False(second.Acquired);
        Assert.Empty(second.Deliveries);
    }

    /// <summary>
    /// Certifies that when an observer claim lapses unreported, another node redelivers the same
    /// position at the next delivery attempt — at-least-once delivery.
    /// </summary>
    [Fact]
    public async Task Clause_5_13_LapsedClaim_RedeliversToAnotherNode_AtTheNextAttempt()
    {
        var store = await CreateStoreAsync();
        await SucceedAsync(store, T0);

        var first = await ClaimObsAsync(store, "obs", [JobState.Succeeded], T0, worker: "node-a");
        var firstDelivery = Assert.Single(first.Deliveries);
        Assert.Equal(1, firstDelivery.DeliveryAttempt);
        // node-a crashes mid-delivery: it never reports. The claim Lease lapses.

        var afterLapse = T0 + Lease + TimeSpan.FromSeconds(1);
        var second = await ClaimObsAsync(store, "obs", [JobState.Succeeded], afterLapse, worker: "node-b");
        var redelivery = Assert.Single(second.Deliveries);
        Assert.Equal(firstDelivery.Position, redelivery.Position);
        Assert.Equal(2, redelivery.DeliveryAttempt); // at-least-once: a second Attempt after the lapse
    }

    /// <summary>
    /// Certifies that a worker whose observer claim lease has lapsed is fenced out: its late report
    /// does not advance the cursor.
    /// </summary>
    [Fact]
    public async Task Clause_5_13_StaleWorker_CannotAdvanceTheCursor_AfterItsLeaseLapsed()
    {
        var store = await CreateStoreAsync();
        await SucceedAsync(store, T0);

        var claim = await ClaimObsAsync(store, "obs", [JobState.Succeeded], T0, worker: "node-a");
        var delivery = Assert.Single(claim.Deliveries);

        // node-a's Lease lapses, then it reports — fenced out, the cursor must not move.
        var afterLapse = T0 + Lease + TimeSpan.FromSeconds(1);
        await ReportObsAsync(store, "obs", afterLapse, "node-a",
            new ObserverDeliveryOutcome(delivery.Position, ObserverDeliveryDisposition.Delivered));
        Assert.Equal(-1, await store.GetObserverCursorAsync("obs"));
    }

    /// <summary>
    /// Certifies that a dead-lettered delivery is recorded in the observer's dead-letter list while the
    /// cursor advances past it, so a poison row never wedges later rows.
    /// </summary>
    [Fact]
    public async Task Clause_5_13_DeadLetteredDelivery_IsRecordedLoudly_AndTheCursorAdvancesPastIt()
    {
        var store = await CreateStoreAsync();
        var jobId = await SucceedAsync(store, T0);

        var claim = await ClaimObsAsync(store, "obs", [JobState.Succeeded], T0);
        var delivery = Assert.Single(claim.Deliveries);
        await ReportObsAsync(store, "obs", T0, "w1",
            new ObserverDeliveryOutcome(delivery.Position, ObserverDeliveryDisposition.DeadLettered));

        Assert.Equal(delivery.Position, await store.GetObserverCursorAsync("obs")); // a poison row can't wedge later rows
        var deadLetter = Assert.Single(await store.ListObserverDeadLettersAsync("obs"));
        Assert.Equal(delivery.Position, deadLetter.Position);
        Assert.Equal(jobId, deadLetter.JobId);
        Assert.Equal(JobState.Succeeded, deadLetter.State);
    }

    /// <summary>
    /// Certifies in-order delivery per observer: a retrying row holds the cursor at its position
    /// through its backoff, redelivers alone at the next attempt, and only its success lets the cursor
    /// sweep past both resolved rows.
    /// </summary>
    [Fact]
    public async Task Clause_5_13_RetryRow_HoldsTheCursor_HeadOfLinePerObserver()
    {
        var store = await CreateStoreAsync();
        await SucceedAsync(store, T0); // first matching Succeeded transition
        await SucceedAsync(store, T0); // second matching Succeeded transition

        var claim = await ClaimObsAsync(store, "obs", [JobState.Succeeded], T0);
        Assert.Equal(2, claim.Deliveries.Count);
        var first = claim.Deliveries[0];
        var second = claim.Deliveries[1];

        // The first delivery retries; the second succeeds. In-order-per-Observer: the cursor cannot
        // pass the held first row, so it never reaches the second's Position.
        var retryAt = T0 + TimeSpan.FromMinutes(5);
        await ReportObsAsync(store, "obs", T0, "w1",
            new ObserverDeliveryOutcome(first.Position, ObserverDeliveryDisposition.Retry, retryAt),
            new ObserverDeliveryOutcome(second.Position, ObserverDeliveryDisposition.Delivered));
        Assert.True(await store.GetObserverCursorAsync("obs") < first.Position);

        // Still in its backoff window: a re-claim finds nothing (the head-of-line block holds).
        Assert.Empty((await ClaimObsAsync(store, "obs", [JobState.Succeeded], T0)).Deliveries);

        // Past the backoff: only the held row redelivers — the second is already resolved.
        var afterBackoff = retryAt + TimeSpan.FromSeconds(1);
        var redelivery = Assert.Single((await ClaimObsAsync(store, "obs", [JobState.Succeeded], afterBackoff)).Deliveries);
        Assert.Equal(first.Position, redelivery.Position);
        Assert.Equal(2, redelivery.DeliveryAttempt);

        // Now it succeeds: the cursor sweeps over both resolved rows to the end.
        await ReportObsAsync(store, "obs", afterBackoff, "w1",
            new ObserverDeliveryOutcome(first.Position, ObserverDeliveryDisposition.Delivered));
        Assert.True(await store.GetObserverCursorAsync("obs") >= second.Position);
    }

    /// <summary>
    /// Certifies that a wire-name subscription delivers only matching transitions, and non-matching
    /// rows neither deliver nor block that observer's cursor.
    /// </summary>
    [Fact]
    public async Task Clause_5_13_Subscription_FiltersByWireName_NonMatchingRowsNeverBlockOrDeliver()
    {
        var store = await CreateStoreAsync();
        await SucceedAsync(store, T0, wireName: "alpha");
        await SucceedAsync(store, T0, wireName: "beta");

        // Subscribed to alpha only: exactly the alpha Succeeded transition is delivered.
        var claim = await ClaimObsAsync(store, "obs-alpha", [JobState.Succeeded], T0, wireName: "alpha");
        var delivery = Assert.Single(claim.Deliveries);
        Assert.Equal("alpha", delivery.WireName);

        // The beta transition was recorded and is matchable by another subscription — it was simply
        // ignored here, never blocking the alpha cursor.
        await ReportObsAsync(store, "obs-alpha", T0, "w1",
            new ObserverDeliveryOutcome(delivery.Position, ObserverDeliveryDisposition.Delivered));
        Assert.Empty((await ClaimObsAsync(store, "obs-alpha", [JobState.Succeeded], T0, wireName: "alpha")).Deliveries);

        var betaDelivery = Assert.Single((await ClaimObsAsync(store, "obs-beta", [JobState.Succeeded], T0, wireName: "beta")).Deliveries);
        Assert.Equal("beta", betaDelivery.WireName);
    }

    /// <summary>
    /// Certifies that with history recording off there are no transitions to observe: claims return
    /// nothing and the cursor stays at -1.
    /// </summary>
    [Fact]
    public async Task Clause_5_13_HistoryOff_HasNothingToObserve()
    {
        var store = await CreateStoreAsync(JobHistoryPolicy.Off);
        await SucceedAsync(store, T0);

        var claim = await ClaimObsAsync(store, "obs", [JobState.Succeeded], T0);
        Assert.Empty(claim.Deliveries);
        Assert.Equal(-1, await store.GetObserverCursorAsync("obs"));
    }

    /// <summary>
    /// Certifies the observer lag read: an unknown observer is caught up at cursor -1; once matching
    /// transitions are recorded it counts exactly those after the cursor (never the non-matching rows)
    /// and reports the oldest's timestamp; and after they are delivered the observer reads caught up.
    /// </summary>
    [Fact]
    public async Task Clause_5_13_Lag_CountsMatchingBacklog_ThenReadsCaughtUpOnceDelivered()
    {
        var store = await CreateStoreAsync();

        // Unknown observer: nothing recorded to observe → caught up at cursor -1.
        var idle = await store.GetObserverLagAsync(new ObserverLagRequest("obs", [JobState.Succeeded], null, null));
        Assert.Equal(-1, idle.Cursor);
        Assert.Equal(0, idle.Pending);
        Assert.Null(idle.OldestPendingAt);

        // Two matching Succeeded transitions. Each SucceedAsync also records non-matching Scheduled and
        // Leased rows the subscription must not count.
        await SucceedAsync(store, T0);
        await SucceedAsync(store, T0);

        var lag = await store.GetObserverLagAsync(new ObserverLagRequest("obs", [JobState.Succeeded], null, null));
        Assert.Equal(-1, lag.Cursor);          // nothing delivered yet
        Assert.Equal(2, lag.Pending);          // exactly the two Succeeded rows, not the Scheduled/Leased rows
        Assert.Equal(T0, lag.OldestPendingAt); // the oldest pending transition's timestamp

        // Deliver everything the observer watches; it then reads caught up.
        var claim = await ClaimObsAsync(store, "obs", [JobState.Succeeded], T0);
        await ReportObsAsync(store, "obs", T0, "w1",
            [.. claim.Deliveries.Select(d => new ObserverDeliveryOutcome(d.Position, ObserverDeliveryDisposition.Delivered))]);

        var caughtUp = await store.GetObserverLagAsync(new ObserverLagRequest("obs", [JobState.Succeeded], null, null));
        Assert.True(caughtUp.Cursor >= 0);
        Assert.Equal(0, caughtUp.Pending);
        Assert.Null(caughtUp.OldestPendingAt);
    }

    /// <summary>
    /// Certifies that a wire-name observer sweeps its cursor past a later non-matching-wire transition and
    /// that its lag read is scoped to the subscribed wire.
    /// </summary>
    [Fact]
    public async Task Clause_5_13_Observer_WireSubscription_CursorSweepsPastNonMatchingWire_AndLagIsWireScoped()
    {
        var store = await CreateStoreAsync();
        await SucceedAsync(store, T0, wireName: "alpha");
        await SucceedAsync(store, T0, wireName: "beta");

        var claim = await ClaimObsAsync(store, "obs-alpha", [JobState.Succeeded], T0, wireName: "alpha");
        var delivery = Assert.Single(claim.Deliveries);
        await ReportObsAsync(store, "obs-alpha", T0, "w1",
            new ObserverDeliveryOutcome(delivery.Position, ObserverDeliveryDisposition.Delivered));

        // Locate the later beta row's position via a beta-scoped observer; the alpha cursor must be past it.
        var betaDelivery = Assert.Single(
            (await ClaimObsAsync(store, "obs-beta", [JobState.Succeeded], T0, wireName: "beta")).Deliveries);
        Assert.True(await store.GetObserverCursorAsync("obs-alpha") >= betaDelivery.Position);

        // The lag read is wire-scoped: a fresh observer watching only 'beta' counts the one beta backlog row.
        var lag = await store.GetObserverLagAsync(new ObserverLagRequest("obs-lag", [JobState.Succeeded], "beta", null));
        Assert.Equal(1, lag.Pending);
    }

    /// <summary>
    /// Certifies that a queue-scoped observer delivers only its queue's transitions, sweeps its cursor past
    /// other-queue rows, and reports a queue-scoped lag.
    /// </summary>
    [Fact]
    public async Task Clause_5_13_Observer_QueueSubscription_FiltersDelivery_SweepsPastOtherQueue_AndLagIsQueueScoped()
    {
        var store = await CreateStoreAsync();
        var jobX = await SucceedAsync(store, T0, queue: "qx");
        var jobY = await SucceedAsync(store, T0, queue: "qy");

        // obs-x watches only queue 'qx': its claim delivers only the qx row.
        var claim = await ClaimObsAsync(store, "obs-x", [JobState.Succeeded], T0, queue: "qx");
        var delivery = Assert.Single(claim.Deliveries);
        Assert.Equal(jobX, delivery.JobId);
        await ReportObsAsync(store, "obs-x", T0, "w1",
            new ObserverDeliveryOutcome(delivery.Position, ObserverDeliveryDisposition.Delivered));

        // Once reported, its cursor sweeps past the later qy row (other queue never blocks).
        var yDelivery = Assert.Single(
            (await ClaimObsAsync(store, "obs-y", [JobState.Succeeded], T0, queue: "qy")).Deliveries);
        Assert.Equal(jobY, yDelivery.JobId);
        Assert.True(await store.GetObserverCursorAsync("obs-x") >= yDelivery.Position);

        // The lag read is queue-scoped.
        var lag = await store.GetObserverLagAsync(new ObserverLagRequest("obs-lag", [JobState.Succeeded], null, "qx"));
        Assert.Equal(1, lag.Pending);
    }

    /// <summary>
    /// Certifies that one observer's claim never overwrites another observer's stored subscription:
    /// two observers on different wire filters keep their own cursor-advance scoping even when their
    /// claims interleave.
    /// </summary>
    [Fact]
    public async Task Clause_5_13_Observer_SubscriptionIsolation_OneClaimNeverOverwritesAnothers()
    {
        // The subscription write must be scoped to its own observer. With two observers on DIFFERENT
        // wire filters, obs-bravo's claim landing BEFORE obs-alpha reports is what would expose a
        // subscription write that forgot its observer scope (issue 0240 R2-E).
        var store = await CreateStoreAsync();
        await SucceedAsync(store, T0, wireName: "alphawire");
        await SucceedAsync(store, T0, wireName: "bravowire"); // a later, non-matching-for-alpha row

        var alpha = await ClaimObsAsync(store, "obs-alpha", [JobState.Succeeded], T0, worker: "wa", wireName: "alphawire");
        var alphaDelivery = Assert.Single(alpha.Deliveries);

        // obs-bravo claims WHILE obs-alpha is mid-delivery — this must not touch obs-alpha's subscription.
        var bravo = await ClaimObsAsync(store, "obs-bravo", [JobState.Succeeded], T0, worker: "wb", wireName: "bravowire");
        var bravoDelivery = Assert.Single(bravo.Deliveries);

        // obs-alpha reports: still scoped to alphawire, its cursor sweeps PAST the non-matching bravo row.
        await ReportObsAsync(store, "obs-alpha", T0, "wa",
            new ObserverDeliveryOutcome(alphaDelivery.Position, ObserverDeliveryDisposition.Delivered));
        Assert.True(await store.GetObserverCursorAsync("obs-alpha") >= bravoDelivery.Position);
    }

    /// <summary>
    /// Certifies that a multi-character wire subscription is stored and matched in full, not truncated:
    /// the observer holds head-of-line on a later same-wire row rather than sweeping past it.
    /// </summary>
    [Fact]
    public async Task Clause_5_13_Observer_MultiCharWireSubscription_HoldsHeadOfLine_NotTruncated()
    {
        // A same-wire row after the cursor must BLOCK the cursor. If the stored wire filter were
        // clipped to one character it would no longer match its own multi-character wire, and the
        // held row would be wrongly swept past (issue 0240 R2-E).
        var store = await CreateStoreAsync();
        var first = await SucceedAsync(store, T0, wireName: "alphawire");
        var second = await SucceedAsync(store, T0, wireName: "alphawire");

        // Deliver only the first same-wire row, leaving the second pending.
        var claim = await ClaimObsAsync(store, "obs", [JobState.Succeeded], T0, wireName: "alphawire", maxRows: 1);
        var d1 = Assert.Single(claim.Deliveries);
        Assert.Equal(first, d1.JobId);
        await ReportObsAsync(store, "obs", T0, "w1",
            new ObserverDeliveryOutcome(d1.Position, ObserverDeliveryDisposition.Delivered));

        // The second same-wire row still blocks the cursor: a re-claim delivers it (never swept past).
        var reclaim = await ClaimObsAsync(store, "obs", [JobState.Succeeded], T0, wireName: "alphawire");
        Assert.Equal(second, Assert.Single(reclaim.Deliveries).JobId);
    }

    /// <summary>
    /// Certifies that a multi-character queue subscription is stored and matched in full, not truncated:
    /// the observer holds head-of-line on a later same-queue row rather than sweeping past it.
    /// </summary>
    [Fact]
    public async Task Clause_5_13_Observer_MultiCharQueueSubscription_HoldsHeadOfLine_NotTruncated()
    {
        // The queue-filter twin of the wire-filter head-of-line fact: clipping the stored queue to one
        // character would stop it matching its own multi-character queue (issue 0240 R2-E).
        var store = await CreateStoreAsync();
        var first = await SucceedAsync(store, T0, queue: "qxray");
        var second = await SucceedAsync(store, T0, queue: "qxray");

        var claim = await ClaimObsAsync(store, "obs", [JobState.Succeeded], T0, queue: "qxray", maxRows: 1);
        var d1 = Assert.Single(claim.Deliveries);
        Assert.Equal(first, d1.JobId);
        await ReportObsAsync(store, "obs", T0, "w1",
            new ObserverDeliveryOutcome(d1.Position, ObserverDeliveryDisposition.Delivered));

        var reclaim = await ClaimObsAsync(store, "obs", [JobState.Succeeded], T0, queue: "qxray");
        Assert.Equal(second, Assert.Single(reclaim.Deliveries).JobId);
    }

    /// <summary>
    /// Certifies that an observer claim lease is dead exactly at its expiry instant: a report at that
    /// instant is fenced, and a second node claiming at that instant acquires and redelivers.
    /// </summary>
    [Fact]
    public async Task Clause_5_13_ObserverLease_IsDeadAtItsExpiryInstant_OnClaimAndReport()
    {
        var store = await CreateStoreAsync();
        await SucceedAsync(store, T0);
        var expiry = T0 + Lease; // the exact instant node-a's claim lease lapses

        var first = await ClaimObsAsync(store, "obs", [JobState.Succeeded], T0, worker: "node-a");
        var delivery = Assert.Single(first.Deliveries);

        // A report AT the exact expiry instant is fenced out — the cursor does not advance.
        await ReportObsAsync(store, "obs", expiry, "node-a",
            new ObserverDeliveryOutcome(delivery.Position, ObserverDeliveryDisposition.Delivered));
        Assert.Equal(-1, await store.GetObserverCursorAsync("obs"));

        // A second node claiming AT the exact expiry instant acquires (the lease is dead) and redelivers.
        var second = await ClaimObsAsync(store, "obs", [JobState.Succeeded], expiry, worker: "node-b");
        var redelivery = Assert.Single(second.Deliveries);
        Assert.Equal(delivery.Position, redelivery.Position);
        Assert.Equal(2, redelivery.DeliveryAttempt);
    }

    /// <summary>
    /// Certifies that an observer delivering a failing (dead-letter) transition receives its failure
    /// detail on the claimed delivery.
    /// </summary>
    [Fact]
    public async Task Clause_5_13_Observer_FailedTransitionDelivery_CarriesTheFailureDetail()
    {
        var store = await CreateStoreAsync();
        await store.EnqueueAsync(Job(), now: T0);
        var claimed = Assert.Single(await ClaimAsync(store, T0));
        const string detail = "System.InvalidOperationException: boom\n   at Handler.Run()";
        await store.ReportOutcomeAsync(
            claimed.JobId, "w1", claimed.Attempt, new JobOutcome.Failure(null, "boom"), T0, failureDetail: detail);

        var claim = await ClaimObsAsync(store, "obs", [JobState.DeadLettered], T0);
        var delivery = Assert.Single(claim.Deliveries);
        Assert.Equal(JobState.DeadLettered, delivery.State);
        Assert.Equal(detail, delivery.FailureDetail);
    }

    /// <summary>
    /// Certifies that reporting to an unknown observer, or to a leaseless observer created by an
    /// empty-States claim, is a safe no-op that neither throws nor advances the cursor.
    /// </summary>
    [Fact]
    public async Task Clause_5_13_ReportObserverDeliveries_IsASafeNoOp_ForUnknownOrLeaselessObservers()
    {
        var store = await CreateStoreAsync();

        // Reporting to an observer that was never claimed is a safe no-op.
        await ReportObsAsync(store, "never-claimed", T0, "w1",
            new ObserverDeliveryOutcome(0, ObserverDeliveryDisposition.Delivered));
        Assert.Equal(-1, await store.GetObserverCursorAsync("never-claimed"));

        // An empty-States subscription is legal: the claim persists a leaseless row, and a later report to
        // it round-trips as a safe no-op — never a parse crash.
        await SucceedAsync(store, T0);
        var empty = await ClaimObsAsync(store, "obs-empty", [], T0);
        Assert.Empty(empty.Deliveries);
        await ReportObsAsync(store, "obs-empty", T0, "w1",
            new ObserverDeliveryOutcome(0, ObserverDeliveryDisposition.Delivered));
        Assert.Equal(-1, await store.GetObserverCursorAsync("obs-empty"));
    }

    // ── ADR 0022 Job Tags ────────────────────────────────────────────────────────

    /// <summary>
    /// Certifies that a job's tags — labels, keyed tags, and multiple values under one key — round-trip
    /// as a set through enqueue and read.
    /// </summary>
    [Fact]
    public async Task Clause_Tags_EnqueueAndRead_RoundTripsLabelsKeyedTags_AndMultiValueKeys()
    {
        // A job carries a Label, a Keyed Tag, and a second value under the same key. Read-back
        // round-trips the whole set (set equality, order-independent).
        var store = await CreateStoreAsync();
        var job = Job() with
        {
            Tags = JobTags.Empty
                .WithLabel("urgent")
                .WithTag("tenant", "acme")
                .WithTag("variant", "BRCA1")
                .WithTag("variant", "TP53"),
        };
        await store.EnqueueAsync(job, now: T0);

        var stored = await store.GetJobAsync(job.JobId);
        Assert.NotNull(stored);
        Assert.Equal(job.Tags, stored.Tags); // set equality
        Assert.Contains(JobTag.Label("urgent"), stored.Tags);
        Assert.Contains(JobTag.Keyed("tenant", "acme"), stored.Tags);
        Assert.Contains(JobTag.Keyed("variant", "BRCA1"), stored.Tags);
        Assert.Contains(JobTag.Keyed("variant", "TP53"), stored.Tags);
    }

    /// <summary>
    /// Certifies that the record returned by Claim carries the job's enqueue-time tags — the claiming
    /// worker sees the full tag set without a second read.
    /// </summary>
    [Fact]
    public async Task Clause_Tags_ClaimedRecord_CarriesTheEnqueueTimeTags()
    {
        // The claim result — not just a later GetJob — hydrates the job's tags. Callers hand the
        // claimed JobRecord straight to a handler, so its Tags must already be populated (issue 0240).
        var store = await CreateStoreAsync();
        var job = Job() with
        {
            Tags = JobTags.Empty.WithLabel("urgent").WithTag("tenant", "acme").WithTag("variant", "TP53"),
        };
        await store.EnqueueAsync(job, now: T0);

        var claimed = Assert.Single(await ClaimAsync(store, T0));
        Assert.Equal(job.Tags, claimed.Tags); // set equality, straight off the claim
        Assert.Contains(JobTag.Label("urgent"), claimed.Tags);
        Assert.Contains(JobTag.Keyed("tenant", "acme"), claimed.Tags);
        Assert.Contains(JobTag.Keyed("variant", "TP53"), claimed.Tags);
    }

    /// <summary>
    /// Certifies per-job set semantics: re-adding an identical tag on the same job collapses to one,
    /// while the same tag on different jobs is allowed and both jobs match a tag query.
    /// </summary>
    [Fact]
    public async Task Clause_Tags_PerJobUniqueness_SameTagCollapses_AcrossJobsAllowed()
    {
        // The SAME Tag re-added on the SAME job collapses (set semantics, the per-job UNIQUE
        // constraint); the SAME Tag on DIFFERENT jobs is the point and is allowed.
        var store = await CreateStoreAsync();
        var a = Job() with { Tags = JobTags.Empty.WithLabel("urgent").WithLabel("urgent") };
        var b = Job() with { Tags = JobTags.Empty.WithLabel("urgent") };
        await store.EnqueueAsync(a, now: T0);
        await store.EnqueueAsync(b, now: T0);

        var storedA = await store.GetJobAsync(a.JobId);
        Assert.Equal(JobTags.Empty.WithLabel("urgent"), storedA!.Tags); // collapsed to one
        Assert.Single(storedA.Tags);

        // Both jobs carry the same Label — cross-job duplicates are allowed.
        var matches = await store.ListJobsAsync(new JobQuery { TagPredicates = [JobTagPredicate.HasLabel("urgent")] });
        Assert.Equal(2, matches.Count);
    }

    /// <summary>
    /// Certifies that a label and a keyed tag with the same string are distinct tags: a label predicate
    /// never matches a keyed tag, and vice versa.
    /// </summary>
    [Fact]
    public async Task Clause_Tags_EmptyKeySentinel_DistinguishesLabelFromKeyedTag()
    {
        // The '' sentinel is the structural discriminator: a Label "x" and a Keyed Tag whose key is
        // "x" are different Tags, and a has-label predicate never matches a Keyed Tag (and vice
        // versa) even when the strings coincide.
        var store = await CreateStoreAsync();
        var labelled = Job() with { Tags = JobTags.Empty.WithLabel("acme") };
        var keyed = Job() with { Tags = JobTags.Empty.WithTag("acme", "v") };
        await store.EnqueueAsync(labelled, now: T0);
        await store.EnqueueAsync(keyed, now: T0);

        var labelMatch = await store.ListJobsAsync(new JobQuery { TagPredicates = [JobTagPredicate.HasLabel("acme")] });
        Assert.Equal(labelled.JobId, Assert.Single(labelMatch).JobId);

        var keyMatch = await store.ListJobsAsync(new JobQuery { TagPredicates = [JobTagPredicate.HasKey("acme")] });
        Assert.Equal(keyed.JobId, Assert.Single(keyMatch).JobId);
    }

    /// <summary>
    /// Certifies that tag predicates AND together — a job must satisfy every predicate — and compose
    /// with the scalar state filter.
    /// </summary>
    [Fact]
    public async Task Clause_Tags_AndedFiltering_ReturnsMatchingJobs_AndComposesWithStateFilter()
    {
        // AND-ed tag predicates intersect; a job must satisfy EVERY predicate. And tag predicates
        // AND-compose with the scalar State filter.
        var store = await CreateStoreAsync();
        var both = Job() with { Tags = JobTags.Empty.WithTag("tenant", "acme").WithLabel("urgent") };
        var tenantOnly = Job() with { Tags = JobTags.Empty.WithTag("tenant", "acme") };
        var urgentOnly = Job() with { Tags = JobTags.Empty.WithLabel("urgent") };
        await store.EnqueueAsync(both, now: T0);
        await store.EnqueueAsync(tenantOnly, now: T0);
        await store.EnqueueAsync(urgentOnly, now: T0);

        // tenant=acme AND label urgent → only the job carrying both.
        var anded = await store.ListJobsAsync(new JobQuery
        {
            TagPredicates = [JobTagPredicate.HasKeyValue("tenant", "acme"), JobTagPredicate.HasLabel("urgent")],
        });
        Assert.Equal(both.JobId, Assert.Single(anded).JobId);

        // has-key-any-value matches both tenant-tagged jobs.
        var anyValue = await store.ListJobsAsync(new JobQuery { TagPredicates = [JobTagPredicate.HasKey("tenant")] });
        Assert.Equal(2, anyValue.Count);

        // Compose with a State filter: claim exactly `both` (oldest-first → it is enqueued first),
        // so only it is Leased; the tenant predicate then AND-composes to exclude the still-Scheduled
        // tenantOnly job.
        var claimed = Assert.Single(await ClaimAsync(store, T0, maxJobs: 1));
        Assert.Equal(both.JobId, claimed.JobId);
        var leasedTenant = await store.ListJobsAsync(new JobQuery
        {
            State = JobState.Leased,
            TagPredicates = [JobTagPredicate.HasKeyValue("tenant", "acme")],
        });
        Assert.Equal(both.JobId, Assert.Single(leasedTenant).JobId);
    }

    /// <summary>
    /// Certifies that a runtime tag delta rides the outcome fence: an applied outcome unions its tags
    /// onto the job, while a stale-lease outcome writes nothing.
    /// </summary>
    [Fact]
    public async Task Clause_Tags_OutcomeDelta_Persists_AndIsFenced()
    {
        // The runtime Tag delta rides the fenced outcome write (ADR 0022): an APPLIED outcome unions
        // its Tags onto the job; a fenced-out (StaleLease) outcome writes NOTHING (Effect-Once).
        var store = await CreateStoreAsync();
        var job = Job() with { Tags = JobTags.Empty.WithLabel("enqueued") };
        await store.EnqueueAsync(job, now: T0);
        var claimed = Assert.Single(await ClaimAsync(store, T0));

        // A fenced-out outcome (wrong worker) must not persist its delta.
        Assert.Equal(OutcomeResult.StaleLease, await store.ReportOutcomeAsync(
            claimed.JobId, "impostor", claimed.Attempt, new JobOutcome.Success(), T0,
            addedTags: JobTags.Empty.WithTag("variant", "ghost")));
        var afterStale = await store.GetJobAsync(job.JobId);
        Assert.DoesNotContain(JobTag.Keyed("variant", "ghost"), afterStale!.Tags);
        Assert.Equal(JobState.Leased, afterStale.State); // nothing changed

        // The live-Lease holder applies: the delta unions onto the enqueue-time Tags.
        Assert.Equal(OutcomeResult.Applied, await store.ReportOutcomeAsync(
            claimed.JobId, "w1", claimed.Attempt, new JobOutcome.Success(), T0,
            addedTags: JobTags.Empty.WithTag("variant", "BRCA1")));
        var applied = await store.GetJobAsync(job.JobId);
        Assert.Equal(JobState.Succeeded, applied!.State);
        Assert.Contains(JobTag.Label("enqueued"), applied.Tags); // enqueue-time Tag survives
        Assert.Contains(JobTag.Keyed("variant", "BRCA1"), applied.Tags); // delta unioned in
        Assert.DoesNotContain(JobTag.Keyed("variant", "ghost"), applied.Tags); // the stale delta never landed
    }

    /// <summary>
    /// Certifies that a job's tags are deleted with it under retention, so purged tags never match a
    /// later query.
    /// </summary>
    [Fact]
    public async Task Clause_Tags_AreDeletedWithTheJob_UnderRetention()
    {
        // Tags live exactly as long as the job (FK ON DELETE CASCADE): purging the terminal job
        // removes its tag rows, so they never match a later query nor leak onto a reused id.
        var store = await CreateStoreAsync();
        var job = Job() with { Tags = JobTags.Empty.WithTag("tenant", "acme") };
        await store.EnqueueAsync(job, now: T0);
        var claimed = Assert.Single(await ClaimAsync(store, T0));
        await store.ReportOutcomeAsync(claimed.JobId, "w1", claimed.Attempt, new JobOutcome.Success(), T0);

        Assert.NotEmpty((await store.GetJobAsync(job.JobId))!.Tags);
        Assert.Equal(1, await store.PurgeTerminalAsync(TerminalStateClass.SucceededOrCancelled, T0, maxJobs: 32));

        Assert.Null(await store.GetJobAsync(job.JobId)); // job gone
        // The tag rows are gone with it: a tenant query finds nothing.
        Assert.Empty(await store.ListJobsAsync(new JobQuery { TagPredicates = [JobTagPredicate.HasKeyValue("tenant", "acme")] }));
    }

    /// <summary>
    /// Certifies that faceting a keyed dimension returns one count per distinct value, ordered by count
    /// descending.
    /// </summary>
    [Fact]
    public async Task Clause_Facet_KeyedDimension_ReturnsValueCounts_DescendingByCount()
    {
        // Facet("tenant") buckets the population by the tenant dimension: one (value, count) per
        // distinct value, count = distinct jobs carrying it, ordered by count DESC (value ASC tiebreak).
        var store = await CreateStoreAsync();
        // acme×3, globex×1, initech×2 — choose values whose ascending order is NOT the count order so
        // the count-desc sort is actually exercised.
        await store.EnqueueAsync(Job() with { Tags = JobTags.Empty.WithTag("tenant", "acme") }, T0);
        await store.EnqueueAsync(Job() with { Tags = JobTags.Empty.WithTag("tenant", "acme") }, T0);
        await store.EnqueueAsync(Job() with { Tags = JobTags.Empty.WithTag("tenant", "acme") }, T0);
        await store.EnqueueAsync(Job() with { Tags = JobTags.Empty.WithTag("tenant", "globex") }, T0);
        await store.EnqueueAsync(Job() with { Tags = JobTags.Empty.WithTag("tenant", "initech") }, T0);
        await store.EnqueueAsync(Job() with { Tags = JobTags.Empty.WithTag("tenant", "initech") }, T0);

        var facet = await store.FacetAsync("tenant");
        Assert.Equal(
            [new TagFacet("acme", 3), new TagFacet("initech", 2), new TagFacet("globex", 1)],
            facet);
    }

    /// <summary>
    /// Certifies that faceting the empty key counts labels, and keyed tags never leak into the label
    /// facet.
    /// </summary>
    [Fact]
    public async Task Clause_Facet_EmptyKey_FacetsLabels()
    {
        // Facet("") counts Labels: each Label's value → number of jobs carrying that Label. Keyed Tags
        // (non-empty key) never leak into the Label facet (the '' sentinel is the discriminator).
        var store = await CreateStoreAsync();
        await store.EnqueueAsync(Job() with { Tags = JobTags.Empty.WithLabel("urgent").WithLabel("vip") }, T0);
        await store.EnqueueAsync(Job() with { Tags = JobTags.Empty.WithLabel("urgent") }, T0);
        // A Keyed Tag whose key string is non-empty must not appear among Labels.
        await store.EnqueueAsync(Job() with { Tags = JobTags.Empty.WithTag("tenant", "acme") }, T0);

        var labels = await store.FacetAsync("");
        Assert.Equal([new TagFacet("urgent", 2), new TagFacet("vip", 1)], labels);
    }

    /// <summary>
    /// Certifies that a scoped facet restricts the population by the base query's filters first, then
    /// buckets the scoped set by the key.
    /// </summary>
    [Fact]
    public async Task Clause_Facet_BaseQuery_RestrictsThePopulationFirst()
    {
        // A scoped facet computes over only the jobs matching the base scope's predicates (the same
        // State/Queue/tag filters ListJobs applies), then breaks the scoped set down by the key.
        var store = await CreateStoreAsync();
        // In the `lab` queue: acme×2, globex×1. In another queue: acme×1 — must NOT count once scoped.
        await store.EnqueueAsync(Job(queue: "lab") with { Tags = JobTags.Empty.WithTag("tenant", "acme") }, T0);
        await store.EnqueueAsync(Job(queue: "lab") with { Tags = JobTags.Empty.WithTag("tenant", "acme") }, T0);
        await store.EnqueueAsync(Job(queue: "lab") with { Tags = JobTags.Empty.WithTag("tenant", "globex") }, T0);
        await store.EnqueueAsync(Job(queue: "other") with { Tags = JobTags.Empty.WithTag("tenant", "acme") }, T0);

        var scoped = await store.FacetAsync("tenant", new JobQuery { Queue = "lab" });
        Assert.Equal([new TagFacet("acme", 2), new TagFacet("globex", 1)], scoped);

        // Scoping by a tag predicate composes the same way: only jobs also Labelled `urgent` count.
        await store.EnqueueAsync(
            Job(queue: "lab") with { Tags = JobTags.Empty.WithTag("tenant", "acme").WithLabel("urgent") }, T0);
        var byPredicate = await store.FacetAsync(
            "tenant", new JobQuery { TagPredicates = [JobTagPredicate.HasLabel("urgent")] });
        Assert.Equal([new TagFacet("acme", 1)], byPredicate);
    }

    /// <summary>
    /// Certifies that facet counts are distinct jobs, never tag rows: a multi-value key counts a job
    /// under each value it carries, and re-adding an identical tag never inflates the count.
    /// </summary>
    [Fact]
    public async Task Clause_Facet_CountsDistinctJobs_MultiValueKeysCountUnderEachValue()
    {
        // Count is DISTINCT JOBS, never tag rows: a job carrying the same Tag once contributes one,
        // and a multi-value key (variant→BRCA1 AND variant→TP53 on one job) counts that job under
        // EACH value it carries.
        var store = await CreateStoreAsync();
        // job A: variant BRCA1 + TP53; job B: variant BRCA1. → BRCA1=2 (A,B), TP53=1 (A).
        await store.EnqueueAsync(
            Job() with { Tags = JobTags.Empty.WithTag("variant", "BRCA1").WithTag("variant", "TP53") }, T0);
        await store.EnqueueAsync(Job() with { Tags = JobTags.Empty.WithTag("variant", "BRCA1") }, T0);

        var facet = await store.FacetAsync("variant");
        Assert.Equal([new TagFacet("BRCA1", 2), new TagFacet("TP53", 1)], facet);

        // A runtime re-add of an identical Tag on the same job is a no-op (set semantics) and must
        // not inflate the count above its distinct-job value.
        var claimed = Assert.Single(await ClaimAsync(store, T0, maxJobs: 1)); // job A (oldest-first)
        await store.ReportOutcomeAsync(
            claimed.JobId, "w1", claimed.Attempt, new JobOutcome.Success(), T0,
            addedTags: JobTags.Empty.WithTag("variant", "BRCA1")); // already on A → no-op
        var afterReAdd = await store.FacetAsync("variant");
        Assert.Equal([new TagFacet("BRCA1", 2), new TagFacet("TP53", 1)], afterReAdd);
    }

    /// <summary>
    /// Certifies that <c>maxResults</c> caps a facet to the highest-count buckets — the cap is applied
    /// after counting, so it keeps the TOP buckets, never an arbitrary subset.
    /// </summary>
    [Fact]
    public async Task Clause_Facet_MaxResults_CapsToTopByCount()
    {
        var store = await CreateStoreAsync();
        // Distinct counts (acme×4, initech×3, globex×2, umbrella×1) so the top-2 is unambiguous.
        for (var i = 0; i < 4; i++)
        {
            await store.EnqueueAsync(Job() with { Tags = JobTags.Empty.WithTag("tenant", "acme") }, T0);
        }

        for (var i = 0; i < 3; i++)
        {
            await store.EnqueueAsync(Job() with { Tags = JobTags.Empty.WithTag("tenant", "initech") }, T0);
        }

        for (var i = 0; i < 2; i++)
        {
            await store.EnqueueAsync(Job() with { Tags = JobTags.Empty.WithTag("tenant", "globex") }, T0);
        }

        await store.EnqueueAsync(Job() with { Tags = JobTags.Empty.WithTag("tenant", "umbrella") }, T0);

        // The cap keeps the two highest-count buckets in count-desc order — not the two alphabetically
        // first, and not an arbitrary pair.
        var top2 = await store.FacetAsync("tenant", maxResults: 2);
        Assert.Equal([new TagFacet("acme", 4), new TagFacet("initech", 3)], top2);

        // A cap at or below zero returns nothing; a cap beyond the distinct count returns them all.
        Assert.Empty(await store.FacetAsync("tenant", maxResults: 0));
        Assert.Equal(4, (await store.FacetAsync("tenant", maxResults: 100)).Count);
    }

    /// <summary>
    /// Certifies the facet's count tiebreak is byte-ORDINAL, not the store's native collation: when the
    /// row cap splits two buckets tied on count, the ordinal-lower value survives — identically on the
    /// In-Memory reference and every adapter. Before the cap existed the tiebreak was cosmetic (every
    /// bucket was returned); it now decides membership, and a case-insensitive or locale collation would
    /// keep the other bucket.
    /// </summary>
    [Fact]
    public async Task Clause_Facet_CountTie_TiebreakIsByOrdinalValue()
    {
        var store = await CreateStoreAsync();
        // "acme" is the unambiguous top bucket (count 2); "Zebra" and "apple" tie at count 1. Byte-
        // ordinal orders 'Z' (0x5A) before 'a' (0x61), so the tiebreak keeps "Zebra"; a case-folding
        // collation would rank "apple" first and the cap would keep it instead.
        await store.EnqueueAsync(Job() with { Tags = JobTags.Empty.WithTag("tenant", "acme") }, T0);
        await store.EnqueueAsync(Job() with { Tags = JobTags.Empty.WithTag("tenant", "acme") }, T0);
        await store.EnqueueAsync(Job() with { Tags = JobTags.Empty.WithTag("tenant", "Zebra") }, T0);
        await store.EnqueueAsync(Job() with { Tags = JobTags.Empty.WithTag("tenant", "apple") }, T0);

        // Cap of 2 straddles the tie: "acme" then the ordinal-first of the tied pair, never "apple".
        var top2 = await store.FacetAsync("tenant", maxResults: 2);
        Assert.Equal([new TagFacet("acme", 2), new TagFacet("Zebra", 1)], top2);

        // Unbounded, the full order is still count-desc then ordinal value.
        var all = await store.FacetAsync("tenant");
        Assert.Equal(
            [new TagFacet("acme", 2), new TagFacet("Zebra", 1), new TagFacet("apple", 1)],
            all);
    }

    // ── Tag Suggest (ADR 0042, issue 0211) ───────────────────────────────────────
    //
    // A case-insensitive prefix completion over the Tags in the store. Every clause must hold
    // identically on the In-Memory reference and on each adapter: the ASCII fold, the lexicographic
    // order, and the keyset cursor are pinned here as the contract, so a divergence is an adapter bug.

    /// <summary>
    /// Certifies that stage two (a non-null key) suggests the distinct values under that key, prefix
    /// matched, and that values under other keys never leak in.
    /// </summary>
    [Fact]
    public async Task Clause_Suggest_StageTwo_ValuesUnderKey()
    {
        var store = await CreateStoreAsync();
        await store.EnqueueAsync(
            Job() with { Tags = JobTags.Empty.WithTag("tenant", "acme").WithTag("tenant", "aperture") }, T0);
        await store.EnqueueAsync(Job() with { Tags = JobTags.Empty.WithTag("tenant", "acme") }, T0); // dup value → one row
        await store.EnqueueAsync(Job() with { Tags = JobTags.Empty.WithTag("tenant", "globex") }, T0);
        await store.EnqueueAsync(Job() with { Tags = JobTags.Empty.WithTag("region", "asia") }, T0); // other key → excluded

        var all = await store.SuggestTagsAsync(new TagSuggestQuery { Key = "tenant" });
        Assert.Equal(
            [
                new TagSuggestion("tenant", "acme"),
                new TagSuggestion("tenant", "aperture"),
                new TagSuggestion("tenant", "globex"),
            ],
            all);

        // A prefix narrows within the key; the "region" key never contributes even though "asia"
        // shares the prefix.
        var a = await store.SuggestTagsAsync(new TagSuggestQuery { Key = "tenant", Prefix = "a" });
        Assert.Equal([new TagSuggestion("tenant", "acme"), new TagSuggestion("tenant", "aperture")], a);
    }

    /// <summary>
    /// Certifies that the empty-string key selects the Label dimension in stage two — only Label values,
    /// never keyed values.
    /// </summary>
    [Fact]
    public async Task Clause_Suggest_EmptyKey_SelectsLabelDimension()
    {
        var store = await CreateStoreAsync();
        await store.EnqueueAsync(
            Job() with { Tags = JobTags.Empty.WithLabel("urgent").WithLabel("upcoming").WithTag("tenant", "unrelated") }, T0);

        // Key="" is stage two over Labels: the keyed value "unrelated" is excluded despite the prefix.
        var s = await store.SuggestTagsAsync(new TagSuggestQuery { Key = "", Prefix = "u" });
        Assert.Equal([new TagSuggestion("", "upcoming"), new TagSuggestion("", "urgent")], s);
    }

    /// <summary>
    /// Certifies that stage one (a null key) suggests matching Labels first (empty Key, a Value), then
    /// each distinct matching key as a drill-in (a Key, empty Value).
    /// </summary>
    [Fact]
    public async Task Clause_Suggest_StageOne_LabelsThenKeys()
    {
        var store = await CreateStoreAsync();
        await store.EnqueueAsync(
            Job() with { Tags = JobTags.Empty.WithLabel("archived").WithTag("account", "acme") }, T0);
        await store.EnqueueAsync(
            Job() with { Tags = JobTags.Empty.WithLabel("active").WithTag("region", "asia") }, T0);

        // Prefix "a" matches Labels active/archived and the key "account"; the key "region" does not
        // start with "a" and is excluded. Labels sort as a block before keys.
        var s = await store.SuggestTagsAsync(new TagSuggestQuery { Prefix = "a" });
        Assert.Equal(
            [
                new TagSuggestion("", "active"),
                new TagSuggestion("", "archived"),
                new TagSuggestion("account", ""),
            ],
            s);
        Assert.True(s[0].IsLabel);
        Assert.True(s[2].IsKey);
    }

    /// <summary>
    /// Certifies the ASCII case-insensitive promise: a prefix of either casing finds mixed-case values,
    /// and each suggestion carries the canonical STORED casing.
    /// </summary>
    [Fact]
    public async Task Clause_Suggest_Prefix_IsAsciiCaseInsensitive()
    {
        var store = await CreateStoreAsync();
        await store.EnqueueAsync(Job() with { Tags = JobTags.Empty.WithTag("tenant", "Acme") }, T0);
        await store.EnqueueAsync(Job() with { Tags = JobTags.Empty.WithTag("tenant", "ACME") }, T0);

        var lower = await store.SuggestTagsAsync(new TagSuggestQuery { Key = "tenant", Prefix = "ac" });
        var upper = await store.SuggestTagsAsync(new TagSuggestQuery { Key = "tenant", Prefix = "AC" });

        // Both prefix casings find both stored values, each carrying its canonical casing.
        string[] expected = ["ACME", "Acme"];
        Assert.Equal(expected, lower.Select(x => x.Value).OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(expected, upper.Select(x => x.Value).OrderBy(x => x, StringComparer.Ordinal));
    }

    /// <summary>
    /// Certifies the literal-prefix promise: a prefix is matched as literal text, never as a
    /// store-specific pattern. Tags are free-form, so a value may contain a character that some
    /// store's underlying LIKE treats as a wildcard — <c>%</c>, <c>_</c>, or (on SQL Server) the
    /// character-class bracket <c>[</c>. Each must match only its own literal, identically on every
    /// adapter, or the suggestion would not compose into an exact-match filter.
    /// </summary>
    [Fact]
    public async Task Clause_Suggest_Prefix_IsLiteralNotPattern()
    {
        var store = await CreateStoreAsync();
        // For each metacharacter: a value CONTAINING it, and a decoy the character would wrongly match
        // if it were treated as a wildcard/class rather than a literal.
        await store.EnqueueAsync(Job() with { Tags = JobTags.Empty.WithTag("promo", "50%off") }, T0);
        await store.EnqueueAsync(Job() with { Tags = JobTags.Empty.WithTag("promo", "50-off") }, T0); // '%' wildcard decoy
        await store.EnqueueAsync(Job() with { Tags = JobTags.Empty.WithTag("code", "a_b") }, T0);
        await store.EnqueueAsync(Job() with { Tags = JobTags.Empty.WithTag("code", "axb") }, T0);      // '_' wildcard decoy
        await store.EnqueueAsync(Job() with { Tags = JobTags.Empty.WithTag("path", "[env]prod") }, T0);
        await store.EnqueueAsync(Job() with { Tags = JobTags.Empty.WithTag("path", "eprod") }, T0);     // '[e' class decoy

        // '%' stays literal: "50%" matches "50%off", never the "50-off" a wildcard would sweep in.
        var pct = await store.SuggestTagsAsync(new TagSuggestQuery { Key = "promo", Prefix = "50%" });
        Assert.Equal([new TagSuggestion("promo", "50%off")], pct);

        // '_' stays literal: "a_" matches "a_b", never the single-char-class "axb".
        var underscore = await store.SuggestTagsAsync(new TagSuggestQuery { Key = "code", Prefix = "a_" });
        Assert.Equal([new TagSuggestion("code", "a_b")], underscore);

        // '[' stays literal: "[e" matches "[env]prod", never the "eprod" a T-SQL character class would.
        var bracket = await store.SuggestTagsAsync(new TagSuggestQuery { Key = "path", Prefix = "[e" });
        Assert.Equal([new TagSuggestion("path", "[env]prod")], bracket);
    }

    /// <summary>
    /// Certifies that suggestions come back in lexicographic order, not insertion order.
    /// </summary>
    [Fact]
    public async Task Clause_Suggest_LexicographicOrder()
    {
        var store = await CreateStoreAsync();
        foreach (var v in new[] { "delta", "alpha", "charlie", "bravo" })
        {
            await store.EnqueueAsync(Job() with { Tags = JobTags.Empty.WithTag("stage", v) }, T0);
        }

        var s = await store.SuggestTagsAsync(new TagSuggestQuery { Key = "stage" });
        Assert.Equal(["alpha", "bravo", "charlie", "delta"], s.Select(x => x.Value));
    }

    /// <summary>
    /// Certifies the suggest ordering under strictly reverse-loaded input: values and keys enqueued in
    /// descending byte order still come back ascending, so the ordering is the query's own, not an
    /// artifact of the rows' physical order.
    /// </summary>
    [Fact]
    public async Task Clause_Suggest_ReverseLoadedInput_StillReturnsAscending_AcrossBothStages()
    {
        // Load in STRICTLY DESCENDING order so an unordered scan would surface the rows reversed —
        // this is what gives the suggest ORDER BY clauses teeth (issue 0240 R2-C). The reverse-input
        // technique mirrors the claim due-time ordering fact.
        var store = await CreateStoreAsync();
        // Descending values under one key, and descending keys+labels sharing the 'x' prefix so a
        // single stage-one query returns them; every load precedes a smaller sibling.
        foreach (var v in new[] { "epsilon", "delta", "charlie", "bravo", "alpha" })
        {
            await store.EnqueueAsync(Job() with { Tags = JobTags.Empty.WithTag("stage", v) }, T0);
        }
        foreach (var t in new[] { "xray", "xenon", "xdelta", "xbravo", "xalpha" })
        {
            await store.EnqueueAsync(Job() with { Tags = JobTags.Empty.WithLabel(t).WithTag(t, "v") }, T0);
        }

        // Stage two — distinct values under one key, ascending despite reverse loading.
        var values = await store.SuggestTagsAsync(new TagSuggestQuery { Key = "stage" });
        Assert.Equal(["alpha", "bravo", "charlie", "delta", "epsilon"], values.Select(x => x.Value));

        // Stage one — labels (a block) then keys, each block ascending despite reverse loading.
        var stageOne = await store.SuggestTagsAsync(new TagSuggestQuery { Prefix = "x" });
        var labels = stageOne.Where(x => x.IsLabel).Select(x => x.Value).ToList();
        var keys = stageOne.Where(x => x.IsKey).Select(x => x.Key).ToList();
        Assert.Equal(["xalpha", "xbravo", "xdelta", "xenon", "xray"], labels);
        Assert.Equal(["xalpha", "xbravo", "xdelta", "xenon", "xray"], keys);
        // Labels sort as a block before keys (section order holds too).
        Assert.True(stageOne.TakeWhile(x => x.IsLabel).Count() == labels.Count);
    }

    /// <summary>
    /// Certifies the keyset cursor: paging with the last suggestion as <c>After</c> walks the whole set
    /// exactly once — no gaps, no duplicates — in the same lexicographic order.
    /// </summary>
    [Fact]
    public async Task Clause_Suggest_CursorWalk_PagesWithoutGapsOrDuplicates()
    {
        var store = await CreateStoreAsync();
        string[] values = ["alpha", "bravo", "charlie", "delta", "echo"];
        foreach (var v in values)
        {
            await store.EnqueueAsync(Job() with { Tags = JobTags.Empty.WithTag("stage", v) }, T0);
        }

        var seen = new List<string>();
        TagSuggestion? cursor = null;
        while (true)
        {
            var page = await store.SuggestTagsAsync(
                new TagSuggestQuery { Key = "stage", After = cursor, MaxResults = 2 });
            if (page.Count == 0)
            {
                break;
            }

            Assert.True(page.Count <= 2);
            seen.AddRange(page.Select(x => x.Value));
            cursor = page[^1];
        }

        Assert.Equal(values, seen);
    }

    /// <summary>
    /// Certifies that <see cref="TagSuggestQuery.MaxResults"/> is clamped: a request beyond the bound
    /// returns at most the bound, and a request at or below zero still returns at least one.
    /// </summary>
    [Fact]
    public async Task Clause_Suggest_MaxResults_ClampedToBound()
    {
        var store = await CreateStoreAsync();
        for (var i = 0; i < TagSuggestQuery.MaxSuggestResults + 10; i++)
        {
            await store.EnqueueAsync(Job() with { Tags = JobTags.Empty.WithTag("n", $"v{i:D4}") }, T0);
        }

        var capped = await store.SuggestTagsAsync(new TagSuggestQuery { Key = "n", MaxResults = int.MaxValue });
        Assert.Equal(TagSuggestQuery.MaxSuggestResults, capped.Count);

        var atLeastOne = await store.SuggestTagsAsync(new TagSuggestQuery { Key = "n", MaxResults = 0 });
        Assert.Single(atLeastOne);
    }

    /// <summary>
    /// Certifies the STAGE-ONE keyset cursor across the labels→keys transition — the boundary a stage-two
    /// walk never exercises. The walk resumes from a label cursor into the keys block, and a window even
    /// straddles the two blocks; an After-cursor bug that dropped the first key or duplicated the last
    /// label would surface here and nowhere else.
    /// </summary>
    [Fact]
    public async Task Clause_Suggest_StageOne_CursorWalk_CrossesLabelsToKeysBoundary()
    {
        var store = await CreateStoreAsync();
        // Three Labels (block 0) and three keys (block 1). The key names sort alphabetically BEFORE the
        // Label values ("k…" < "l…"), so a correct walk that still returns every Label first also proves
        // the block order — not raw token order — drives the cursor.
        await store.EnqueueAsync(
            Job() with
            {
                Tags = JobTags.Empty
                    .WithLabel("l-alpha").WithLabel("l-bravo").WithLabel("l-charlie")
                    .WithTag("k-delta", "x").WithTag("k-echo", "y").WithTag("k-foxtrot", "z"),
            },
            T0);

        TagSuggestion[] expected =
        [
            new("", "l-alpha"), new("", "l-bravo"), new("", "l-charlie"),
            new("k-delta", ""), new("k-echo", ""), new("k-foxtrot", ""),
        ];

        // MaxResults = 2 puts a window boundary mid-walk: [l-alpha, l-bravo], [l-charlie, k-delta] (the
        // block straddle), [k-echo, k-foxtrot]. The concatenated walk must equal the whole ordered set.
        var seen = new List<TagSuggestion>();
        TagSuggestion? cursor = null;
        while (true)
        {
            var page = await store.SuggestTagsAsync(new TagSuggestQuery { After = cursor, MaxResults = 2 });
            if (page.Count == 0)
            {
                break;
            }

            Assert.True(page.Count <= 2);
            seen.AddRange(page);
            cursor = page[^1];
        }

        Assert.Equal(expected, seen);
    }

    /// <summary>
    /// Certifies the ordinal tiebreak between two values that fold equal: the store returns them in
    /// canonical byte-ordinal order (its own returned order, not a re-sort), and a cursor split BETWEEN
    /// the fold-equal pair resumes correctly. This is the classic PG/MSSQL/SQLite divergence point —
    /// where the ASCII fold makes two values equal and only the ordinal tiebreak separates them.
    /// </summary>
    [Fact]
    public async Task Clause_Suggest_FoldEqualValues_OrderByOrdinalAndCursorSplitsThem()
    {
        var store = await CreateStoreAsync();
        // "ACME" and "Acme" fold to the same token; the ordinal tiebreak orders 'C' (0x43) before 'c'
        // (0x63), so the canonical order is ["ACME", "Acme"] on every adapter.
        await store.EnqueueAsync(Job() with { Tags = JobTags.Empty.WithTag("tenant", "ACME") }, T0);
        await store.EnqueueAsync(Job() with { Tags = JobTags.Empty.WithTag("tenant", "Acme") }, T0);

        // Assert the store's OWN returned order — no re-sort — so a collation-dependent tiebreak is caught.
        var all = await store.SuggestTagsAsync(new TagSuggestQuery { Key = "tenant", Prefix = "ac" });
        Assert.Equal(
            [new TagSuggestion("tenant", "ACME"), new TagSuggestion("tenant", "Acme")],
            all);

        // MaxResults = 1 forces the cursor to split the fold-equal pair: "ACME" then, resuming from it,
        // "Acme" — the keyset predicate must compare the canonical token, not just the folded one, or it
        // would either re-emit "ACME" or skip "Acme".
        var seen = new List<TagSuggestion>();
        TagSuggestion? cursor = null;
        while (true)
        {
            var page = await store.SuggestTagsAsync(
                new TagSuggestQuery { Key = "tenant", Prefix = "ac", After = cursor, MaxResults = 1 });
            if (page.Count == 0)
            {
                break;
            }

            Assert.Single(page);
            seen.Add(page[0]);
            cursor = page[0];
        }

        Assert.Equal(
            [new TagSuggestion("tenant", "ACME"), new TagSuggestion("tenant", "Acme")],
            seen);
    }

    // ── Workflows (ADR 0023, issue 0120) ─────────────────────────────────────────
    //
    // A Workflow is a grouping above the Dependency layer: identity + config + immutable structural
    // edges, with a status that is always a PROJECTION of member states (never stored). The whole
    // graph enqueues atomically. Every clause here must hold identically on the In-Memory reference
    // and on each Networked Adapter — a divergence is a bug in one of the two.

    private static NewJob WorkflowMember(string wireName = "wf-member", string queue = "default", DateTimeOffset? dueTime = null)
        => new(Guid.NewGuid(), wireName, "{}"u8.ToArray(), queue, dueTime ?? T0);

    private static WorkflowDefinition Workflow(
        Guid workflowId, IReadOnlyList<NewJob> members, string? name = null,
        bool isAppend = false, Guid? restartedFrom = null)
        => new()
        {
            WorkflowId = workflowId,
            Name = name,
            Members = members,
            IsAppend = isAppend,
            RestartedFrom = restartedFrom,
        };

    /// <summary>
    /// Certifies that a workflow enqueue inserts the whole graph atomically — members carrying their
    /// membership, with dependency gating intact — and the fresh workflow projects Running.
    /// </summary>
    [Fact]
    public async Task Clause_Workflow_AtomicEnqueue_InsertsTheWholeGraph_AndProjectsRunning()
    {
        var store = await CreateStoreAsync();
        var workflowId = Guid.NewGuid();
        var root = WorkflowMember("root");
        var leaf = WorkflowMember("leaf") with { Parents = [root.JobId] };
        Assert.Equal(WorkflowEnqueueResult.Ok,
            await store.EnqueueWorkflowAsync(Workflow(workflowId, [root, leaf], name: "etl"), T0));

        // Members exist, carry the membership scalar, and gate correctly (the leaf awaits its parent).
        var rootRow = await store.GetJobAsync(root.JobId);
        var leafRow = await store.GetJobAsync(leaf.JobId);
        Assert.Equal(workflowId, rootRow!.WorkflowId);
        Assert.Equal(workflowId, leafRow!.WorkflowId);
        Assert.Equal(JobState.Scheduled, rootRow.State);
        Assert.Equal(JobState.AwaitingParent, leafRow.State);

        var graph = await store.GetWorkflowAsync(workflowId);
        Assert.NotNull(graph);
        Assert.Equal("etl", graph.Name);
        Assert.Equal(T0, graph.CreatedAt);
        Assert.Equal(WorkflowStatus.Running, graph.Status);
        Assert.Equal(2, graph.Members.Count);
        Assert.Equal([new WorkflowEdge(root.JobId, leaf.JobId)], graph.Edges);
    }

    /// <summary>
    /// Certifies that a member depending on a job outside the workflow rejects the whole enqueue with
    /// nothing inserted.
    /// </summary>
    [Fact]
    public async Task Clause_Workflow_Containment_IsEnforcedAtEnqueue_NothingInserted()
    {
        var store = await CreateStoreAsync();

        // An outside (non-member) parent is a containment violation — the whole enqueue rejects.
        var outsider = Job();
        await store.EnqueueAsync(outsider, T0);
        var workflowId = Guid.NewGuid();
        var member = WorkflowMember() with { Parents = [outsider.JobId] };
        Assert.Equal(WorkflowEnqueueResult.ContainmentViolation,
            await store.EnqueueWorkflowAsync(Workflow(workflowId, [member]), T0));

        Assert.Null(await store.GetJobAsync(member.JobId)); // all-or-nothing: nothing inserted
        Assert.Null(await store.GetWorkflowAsync(workflowId));
    }

    /// <summary>
    /// Certifies that creating a workflow with an id that already exists is rejected as a duplicate.
    /// </summary>
    [Fact]
    public async Task Clause_Workflow_DuplicateWorkflowId_IsRejected()
    {
        var store = await CreateStoreAsync();
        var workflowId = Guid.NewGuid();
        await store.EnqueueWorkflowAsync(Workflow(workflowId, [WorkflowMember()]), T0);

        Assert.Equal(WorkflowEnqueueResult.DuplicateWorkflow,
            await store.EnqueueWorkflowAsync(Workflow(workflowId, [WorkflowMember()]), T0));
    }

    /// <summary>
    /// Certifies that a workflow with no members is rejected.
    /// </summary>
    [Fact]
    public async Task Clause_Workflow_EmptyWorkflow_IsRejected()
    {
        var store = await CreateStoreAsync();
        Assert.Equal(WorkflowEnqueueResult.EmptyWorkflow,
            await store.EnqueueWorkflowAsync(Workflow(Guid.NewGuid(), []), T0));
    }

    /// <summary>
    /// Certifies that appending adds members and edges to an existing workflow — reopening a drained
    /// workflow's projected status to Running — without rewriting the workflow's own row.
    /// </summary>
    [Fact]
    public async Task Clause_Workflow_Append_AddsMembersAndEdges_AndReopensStatus()
    {
        var store = await CreateStoreAsync();
        var workflowId = Guid.NewGuid();
        var first = WorkflowMember("first");
        await store.EnqueueWorkflowAsync(Workflow(workflowId, [first]), T0);

        // Drain the original sole member: the Workflow projects Succeeded.
        var claimed = Assert.Single(await ClaimAsync(store, T0));
        await store.ReportOutcomeAsync(claimed.JobId, "w1", claimed.Attempt, new JobOutcome.Success(), T0);
        Assert.Equal(WorkflowStatus.Succeeded, (await store.GetWorkflowAsync(workflowId))!.Status);

        // Append a member that depends on the (existing) drained member — containment allows it.
        var appended = WorkflowMember("appended") with { Parents = [first.JobId] };
        Assert.Equal(WorkflowEnqueueResult.Ok,
            await store.EnqueueWorkflowAsync(Workflow(workflowId, [appended], isAppend: true), T0.AddMinutes(1)));

        var graph = await store.GetWorkflowAsync(workflowId);
        Assert.Equal(2, graph!.Members.Count);
        Assert.Equal([new WorkflowEdge(first.JobId, appended.JobId)], graph.Edges);
        // A live appended member reopens the derived status (Succeeded → Running).
        Assert.Equal(WorkflowStatus.Running, graph.Status);
        Assert.Equal(T0, graph.CreatedAt); // append never rewrites the Workflows row
    }

    /// <summary>
    /// Certifies that appending to a workflow that does not exist is rejected.
    /// </summary>
    [Fact]
    public async Task Clause_Workflow_AppendToUnknownWorkflow_IsRejected()
    {
        var store = await CreateStoreAsync();
        Assert.Equal(WorkflowEnqueueResult.WorkflowNotFound,
            await store.EnqueueWorkflowAsync(Workflow(Guid.NewGuid(), [WorkflowMember()], isAppend: true), T0));
    }

    /// <summary>
    /// Certifies that one failed member dominates the projection: the drained workflow's status is
    /// Failed.
    /// </summary>
    [Fact]
    public async Task Clause_Workflow_FailedMemberDominates_TheProjectionIsFailed()
    {
        var store = await CreateStoreAsync();
        var workflowId = Guid.NewGuid();
        var a = WorkflowMember("a");
        var b = WorkflowMember("b");
        await store.EnqueueWorkflowAsync(Workflow(workflowId, [a, b]), T0);

        // Succeed one member, dead-letter the other: failure dominates the projection.
        var claimed = await ClaimAsync(store, T0);
        await store.ReportOutcomeAsync(
            claimed[0].JobId, "w1", claimed[0].Attempt, new JobOutcome.Success(), T0);
        await store.ReportOutcomeAsync(
            claimed[1].JobId, "w1", claimed[1].Attempt, new JobOutcome.Failure(null, "boom"), T0);

        Assert.Equal(WorkflowStatus.Failed, (await store.GetWorkflowAsync(workflowId))!.Status);
    }

    /// <summary>
    /// Certifies that cancelling every live member drains the workflow to a projected Cancelled status
    /// when no member failed.
    /// </summary>
    [Fact]
    public async Task Clause_Workflow_OperatorCancelFanout_ProjectsCancelled()
    {
        // WorkflowCancel (0118) is operator-level: per-job CancelJobAsync fan-out, which adapters
        // already support. With no failed members, the drained Workflow projects Cancelled.
        var store = await CreateStoreAsync();
        var workflowId = Guid.NewGuid();
        var a = WorkflowMember("a");
        var b = WorkflowMember("b") with { Parents = [a.JobId] };
        await store.EnqueueWorkflowAsync(Workflow(workflowId, [a, b]), T0);

        var graph = await store.GetWorkflowAsync(workflowId);
        foreach (var member in graph!.Members.Where(m => !m.State.IsTerminal()))
        {
            await store.CancelJobAsync(member.JobId, "operator", T0);
        }

        Assert.Equal(WorkflowStatus.Cancelled, (await store.GetWorkflowAsync(workflowId))!.Status);
    }

    /// <summary>
    /// Certifies that the workflow listing returns workflows oldest first with their name, member
    /// count, and projected status.
    /// </summary>
    [Fact]
    public async Task Clause_Workflow_Monitor_ListsWorkflows_OldestFirst_WithProjectedStatus()
    {
        var store = await CreateStoreAsync();
        var older = Guid.NewGuid();
        var newer = Guid.NewGuid();
        await store.EnqueueWorkflowAsync(Workflow(older, [WorkflowMember()], name: "older"), T0);
        await store.EnqueueWorkflowAsync(Workflow(newer, [WorkflowMember()], name: "newer"), T0.AddMinutes(1));

        var listed = await store.ListWorkflowsAsync();
        Assert.Equal(2, listed.Count);
        Assert.Equal(older, listed[0].WorkflowId); // CreatedAt ascending
        Assert.Equal("older", listed[0].Name);
        Assert.Equal(1, listed[0].MemberCount);
        Assert.Equal(WorkflowStatus.Running, listed[0].Status);
        Assert.Equal(newer, listed[1].WorkflowId);
    }

    /// <summary>
    /// Certifies that a restarted workflow records the workflow it was restarted from, readable from
    /// both the graph and the listing.
    /// </summary>
    [Fact]
    public async Task Clause_Workflow_RestartLineage_IsRecordedAndRead()
    {
        var store = await CreateStoreAsync();
        var original = Guid.NewGuid();
        var restarted = Guid.NewGuid();
        await store.EnqueueWorkflowAsync(Workflow(original, [WorkflowMember()]), T0);
        await store.EnqueueWorkflowAsync(
            Workflow(restarted, [WorkflowMember()], restartedFrom: original), T0.AddMinutes(1));

        Assert.Null((await store.GetWorkflowAsync(original))!.RestartedFrom);
        Assert.Equal(original, (await store.GetWorkflowAsync(restarted))!.RestartedFrom);
        var listed = await store.ListWorkflowsAsync();
        Assert.Equal(original, listed.Single(w => w.WorkflowId == restarted).RestartedFrom);
    }

    /// <summary>
    /// Certifies that workflow members are retained as a unit: nothing purges while any member is live,
    /// the retention window opens at the drain instant, and purging the drained workflow drops its
    /// identity too.
    /// </summary>
    [Fact]
    public async Task Clause_Workflow_Retention_DrainsAsAUnit_FromTheDrainInstant()
    {
        var store = await CreateStoreAsync();
        var workflowId = Guid.NewGuid();
        var a = WorkflowMember("a");
        var b = WorkflowMember("b");
        await store.EnqueueWorkflowAsync(Workflow(workflowId, [a, b]), T0);

        // Succeed member A at T0; B stays Scheduled (live). The Workflow has NOT drained, so even a
        // generous window purges nothing — members are retained as a unit until the whole graph drains.
        var aClaim = Assert.Single(await ClaimAsync(store, T0, maxJobs: 1)); // due-order: A claimed first
        await store.ReportOutcomeAsync(aClaim.JobId, "w1", aClaim.Attempt, new JobOutcome.Success(), T0);
        Assert.Equal(0, await store.PurgeTerminalAsync(
            TerminalStateClass.SucceededOrCancelled, T0.AddHours(1), maxJobs: 32));
        Assert.NotNull(await store.GetJobAsync(a.JobId)); // still retained — sibling B is live

        // Drain B much later (claim it with a fresh, valid lease). The drain instant is
        // max(member TerminalAt) = T0+1h, so the window starts there: a cutoff before it purges nothing.
        var bClaim = Assert.Single(await ClaimAsync(store, T0.AddHours(1)));
        Assert.Equal(b.JobId, bClaim.JobId);
        await store.ReportOutcomeAsync(bClaim.JobId, "w1", bClaim.Attempt, new JobOutcome.Success(), T0.AddHours(1));
        Assert.Equal(0, await store.PurgeTerminalAsync(
            TerminalStateClass.SucceededOrCancelled, T0.AddMinutes(30), maxJobs: 32));
        Assert.NotNull(await store.GetJobAsync(a.JobId)); // window opens at the drain instant, not A's terminal

        // A cutoff at/after the drain instant purges the whole drained Workflow and drops its identity.
        Assert.Equal(2, await store.PurgeTerminalAsync(
            TerminalStateClass.SucceededOrCancelled, T0.AddHours(1), maxJobs: 32));
        Assert.Null(await store.GetJobAsync(a.JobId));
        Assert.Null(await store.GetJobAsync(b.JobId));
        Assert.Null(await store.GetWorkflowAsync(workflowId)); // orphaned identity dropped
        Assert.Empty(await store.ListWorkflowsAsync());
    }

    /// <summary>
    /// Certifies that on stores supporting transactional enqueue, rolling back the caller's transaction
    /// means the whole workflow graph never existed.
    /// </summary>
    [Fact]
    public async Task Clause_Workflow_TransactionalEnqueue_RollbackMeansTheGraphNeverExisted()
    {
        var store = await CreateStoreAsync();
        if (!store.SupportsTransactionalEnqueue)
        {
            return; // §6: the single optional capability — the In-Memory + Networked adapters all set it
        }

        var workflowId = Guid.NewGuid();
        var member = WorkflowMember();
        using (var transaction = BeginTransaction(store))
        {
            Assert.Equal(WorkflowEnqueueResult.Ok,
                await store.EnqueueWorkflowAsync(Workflow(workflowId, [member]), T0, transaction));
            // The whole graph is invisible to committed reads until the caller commits.
            Assert.Null(await store.GetJobAsync(member.JobId));
            Assert.Null(await store.GetWorkflowAsync(workflowId));
            transaction.Rollback();
        }

        Assert.Null(await store.GetJobAsync(member.JobId));
        Assert.Null(await store.GetWorkflowAsync(workflowId));
    }

    /// <summary>
    /// Certifies that on stores supporting transactional enqueue, committing publishes the whole graph
    /// and makes its roots claimable.
    /// </summary>
    [Fact]
    public async Task Clause_Workflow_TransactionalEnqueue_CommitPublishesTheWholeGraph()
    {
        var store = await CreateStoreAsync();
        if (!store.SupportsTransactionalEnqueue)
        {
            return;
        }

        var workflowId = Guid.NewGuid();
        var root = WorkflowMember("root");
        var leaf = WorkflowMember("leaf") with { Parents = [root.JobId] };
        using (var transaction = BeginTransaction(store))
        {
            await store.EnqueueWorkflowAsync(Workflow(workflowId, [root, leaf]), T0, transaction);
            transaction.Commit();
        }

        var graph = await store.GetWorkflowAsync(workflowId);
        Assert.NotNull(graph);
        Assert.Equal(2, graph.Members.Count);
        Assert.Equal([new WorkflowEdge(root.JobId, leaf.JobId)], graph.Edges);
        Assert.Single(await ClaimAsync(store, T0)); // the root is claimable post-commit
    }

    /// <summary>
    /// Certifies that workflow-member payload, wire-name, and parent-count bounds are enforced at the same
    /// inclusive thresholds as a plain enqueue: exactly at the cap is accepted, one over is rejected.
    /// </summary>
    [Fact]
    public async Task Clause_Workflow_MemberBounds_AreEnforcedAtTheSameThresholds_AsPlainEnqueue()
    {
        var store = await CreateStoreAsync();

        var atPayload = WorkflowMember() with { Payload = new byte[StoreBounds.Default.MaxPayloadBytes] };
        Assert.Equal(WorkflowEnqueueResult.Ok, await store.EnqueueWorkflowAsync(Workflow(Guid.NewGuid(), [atPayload]), T0));
        var overPayload = WorkflowMember() with { Payload = new byte[StoreBounds.Default.MaxPayloadBytes + 1] };
        Assert.Equal(WorkflowEnqueueResult.PayloadTooLarge,
            await store.EnqueueWorkflowAsync(Workflow(Guid.NewGuid(), [overPayload]), T0));

        var atWire = WorkflowMember(wireName: new string('w', StoreBounds.Default.MaxWireNameLength));
        Assert.Equal(WorkflowEnqueueResult.Ok, await store.EnqueueWorkflowAsync(Workflow(Guid.NewGuid(), [atWire]), T0));
        var overWire = WorkflowMember(wireName: new string('w', StoreBounds.Default.MaxWireNameLength + 1));
        Assert.Equal(WorkflowEnqueueResult.WireNameTooLong,
            await store.EnqueueWorkflowAsync(Workflow(Guid.NewGuid(), [overWire]), T0));

        var maxParents = Enumerable.Range(0, StoreBounds.Default.MaxParentsPerJob).Select(_ => WorkflowMember("p")).ToArray();
        var atParents = WorkflowMember("leaf") with { Parents = [.. maxParents.Select(p => p.JobId)] };
        Assert.Equal(WorkflowEnqueueResult.Ok,
            await store.EnqueueWorkflowAsync(Workflow(Guid.NewGuid(), [.. maxParents, atParents]), T0));

        var overList = Enumerable.Range(0, StoreBounds.Default.MaxParentsPerJob + 1).Select(_ => WorkflowMember("p")).ToArray();
        var overParents = WorkflowMember("leaf") with { Parents = [.. overList.Select(p => p.JobId)] };
        Assert.Equal(WorkflowEnqueueResult.TooManyParents,
            await store.EnqueueWorkflowAsync(Workflow(Guid.NewGuid(), [.. overList, overParents]), T0));
    }

    /// <summary>
    /// Certifies workflow member-collision results and their precedence: a duplicate member id in a batch,
    /// job-exists winning over a bound violation, and a duplicate workflow id winning over a member collision.
    /// </summary>
    [Fact]
    public async Task Clause_Workflow_MemberCollisions_AndTheirPrecedence()
    {
        var store = await CreateStoreAsync();

        // (a) The same JobId twice in one batch → DuplicateMember, nothing inserted.
        var dupId = Guid.NewGuid();
        var twinA = WorkflowMember("a") with { JobId = dupId };
        var twinB = WorkflowMember("b") with { JobId = dupId };
        Assert.Equal(WorkflowEnqueueResult.DuplicateMember,
            await store.EnqueueWorkflowAsync(Workflow(Guid.NewGuid(), [twinA, twinB]), T0));
        Assert.Null(await store.GetJobAsync(dupId));

        // (b) A member colliding with an existing standalone job wins over a bound violation.
        var existing = Job();
        await store.EnqueueAsync(existing, now: T0);
        var collidingOversized = WorkflowMember() with
            { JobId = existing.JobId, Payload = new byte[StoreBounds.Default.MaxPayloadBytes + 1] };
        Assert.Equal(WorkflowEnqueueResult.DuplicateMember,
            await store.EnqueueWorkflowAsync(Workflow(Guid.NewGuid(), [collidingOversized]), T0));

        // (c) Re-enqueuing an existing WorkflowId wins over a member collision.
        var wf = Guid.NewGuid();
        var m1 = WorkflowMember("m1");
        await store.EnqueueWorkflowAsync(Workflow(wf, [m1]), T0);
        var reMember = WorkflowMember("m2") with { JobId = m1.JobId };
        Assert.Equal(WorkflowEnqueueResult.DuplicateWorkflow,
            await store.EnqueueWorkflowAsync(Workflow(wf, [reMember]), T0));
    }

    /// <summary>
    /// Certifies that workflow members listed child-before-parent are topologically sorted before insert,
    /// so the parent always inserts first and the graph enqueues cleanly.
    /// </summary>
    [Fact]
    public async Task Clause_Workflow_MembersInChildBeforeParentOrder_AreTopologicallySortedBeforeInsert()
    {
        var store = await CreateStoreAsync();
        var workflowId = Guid.NewGuid();
        var root = WorkflowMember("root");
        var leaf = WorkflowMember("leaf") with { Parents = [root.JobId] };

        // Members are listed child-BEFORE-parent: a broken sort would insert the leaf before its in-batch
        // parent exists and throw; a correct sort inserts the root first.
        Assert.Equal(WorkflowEnqueueResult.Ok,
            await store.EnqueueWorkflowAsync(Workflow(workflowId, [leaf, root]), T0));
        Assert.Equal(JobState.AwaitingParent, (await store.GetJobAsync(leaf.JobId))!.State);
        Assert.Equal(JobState.Scheduled, (await store.GetJobAsync(root.JobId))!.State);
    }

    /// <summary>
    /// Certifies that the workflow listing is oldest-CreatedAt first even when insertion order differs
    /// from creation order.
    /// </summary>
    [Fact]
    public async Task Clause_Workflow_Monitor_ListsOldestFirst_EvenWhenInsertionOrderDiffersFromCreatedAt()
    {
        var store = await CreateStoreAsync();
        var later = Guid.NewGuid();
        var earlier = Guid.NewGuid();
        // Insert the LATER-created workflow physically first, so insertion order differs from CreatedAt.
        await store.EnqueueWorkflowAsync(Workflow(later, [WorkflowMember()], name: "later"), T0.AddMinutes(1));
        await store.EnqueueWorkflowAsync(Workflow(earlier, [WorkflowMember()], name: "earlier"), T0);

        var listed = await store.ListWorkflowsAsync();
        Assert.Equal(earlier, listed[0].WorkflowId); // oldest CreatedAt first
        Assert.Equal(later, listed[1].WorkflowId);
    }

    // ── Job Output (ADR 0026, issue 0133) ───────────────────────────────────────
    // The opaque blob a handler emits via SetOutput on its Succeeded Attempt — functional data a
    // Dependency descendant pulls, NOT diagnostics. It rides the fenced outcome write, persists ONLY
    // on Success, is independent of Job History Policy, is rejected (never truncated) over the bound,
    // and lives on the job row so it deletes with the job under retention. Every clause here must hold
    // identically on the In-Memory reference and each Networked Adapter.

    private static byte[] Output(int length = 16)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
        {
            bytes[i] = (byte)(i % 251);
        }
        return bytes;
    }

    /// <summary>
    /// Certifies that an output blob reported with a Success outcome commits with the Succeeded
    /// transition and reads back byte-identical.
    /// </summary>
    [Fact]
    public async Task Clause_Output_CoCommitsWithTheSucceededTransition_OnTheFence()
    {
        var store = await CreateStoreAsync();
        await store.EnqueueAsync(Job(), now: T0);
        var claimed = Assert.Single(await ClaimAsync(store, T0));

        var blob = Output();
        Assert.Equal(OutcomeResult.Applied, await store.ReportOutcomeAsync(
            claimed.JobId, "w1", claimed.Attempt, new JobOutcome.Success(), T0, output: blob));

        Assert.Equal(JobState.Succeeded, (await store.GetJobAsync(claimed.JobId))!.State);
        Assert.Equal(blob, (await store.GetJobOutputAsync(claimed.JobId))!.Value.ToArray());
    }

    /// <summary>
    /// Certifies that output persists only on a Success outcome: a failure carrying an output blob
    /// still applies but stores no output.
    /// </summary>
    [Fact]
    public async Task Clause_Output_PersistsOnlyOnSuccess_AGracefulFailureDiscardsIt()
    {
        var store = await CreateStoreAsync();
        await store.EnqueueAsync(Job(), now: T0);
        var claimed = Assert.Single(await ClaimAsync(store, T0));

        // A graceful Failure carrying an output blob persists no output — every non-Success outcome does.
        Assert.Equal(OutcomeResult.Applied, await store.ReportOutcomeAsync(
            claimed.JobId, "w1", claimed.Attempt, new JobOutcome.Failure(null, "fatal"), T0, output: Output()));

        Assert.Equal(JobState.DeadLettered, (await store.GetJobAsync(claimed.JobId))!.State);
        Assert.Null(await store.GetJobOutputAsync(claimed.JobId));
    }

    /// <summary>
    /// Certifies that a fenced-out (stale lease) outcome writes nothing, discarding its buffered output
    /// with it.
    /// </summary>
    [Fact]
    public async Task Clause_Output_AStaleLeaseOutcome_DiscardsTheBufferedOutput()
    {
        var store = await CreateStoreAsync();
        await store.EnqueueAsync(Job(), now: T0);
        var claimed = Assert.Single(await ClaimAsync(store, T0));

        // Fenced out (wrong attempt): nothing is written, so the buffered output is discarded with it.
        Assert.Equal(OutcomeResult.StaleLease, await store.ReportOutcomeAsync(
            claimed.JobId, "w1", claimed.Attempt + 1, new JobOutcome.Success(), T0, output: Output()));

        Assert.Equal(JobState.Leased, (await store.GetJobAsync(claimed.JobId))!.State);
        Assert.Null(await store.GetJobOutputAsync(claimed.JobId));
    }

    /// <summary>
    /// Certifies that output is functional data, not diagnostics: it persists even when history
    /// recording is off.
    /// </summary>
    [Fact]
    public async Task Clause_Output_IsWritten_EvenWhenJobHistoryPolicyIsOff()
    {
        // Output is functional data, not diagnostics, so History = Off must NOT erase it.
        var store = await CreateStoreAsync(JobHistoryPolicy.Off);
        await store.EnqueueAsync(Job(), now: T0);
        var claimed = Assert.Single(await ClaimAsync(store, T0));

        var blob = Output();
        await store.ReportOutcomeAsync(claimed.JobId, "w1", claimed.Attempt, new JobOutcome.Success(), T0, output: blob);

        Assert.Empty(await store.GetJobHistoryAsync(claimed.JobId)); // confirms History = Off (no transitions)
        Assert.Equal(blob, (await store.GetJobOutputAsync(claimed.JobId))!.Value.ToArray());
    }

    /// <summary>
    /// Certifies that an over-limit output blob is rejected with a throw reporting the actual size —
    /// never truncated — leaving the job Leased and the store untouched.
    /// </summary>
    [Fact]
    public async Task Clause_Output_OverMaxOutputBytes_IsRejected_NotTruncated()
    {
        var store = await CreateStoreAsync();
        await store.EnqueueAsync(Job(), now: T0);
        var claimed = Assert.Single(await ClaimAsync(store, T0));

        var oversized = Output(StoreBounds.Default.MaxOutputBytes + 1);
        var rejection = await Assert.ThrowsAsync<JobOutputTooLargeException>(async () =>
            await store.ReportOutcomeAsync(
                claimed.JobId, "w1", claimed.Attempt, new JobOutcome.Success(), T0, output: oversized));
        Assert.Equal(oversized.Length, rejection.ActualBytes);

        // Rejected loudly — never truncated, and the over-limit write left the store untouched.
        Assert.Equal(JobState.Leased, (await store.GetJobAsync(claimed.JobId))!.State);
        Assert.Null(await store.GetJobOutputAsync(claimed.JobId));
    }

    /// <summary>
    /// Certifies that reading output returns null both for a job that never set one and for an unknown
    /// job.
    /// </summary>
    [Fact]
    public async Task Clause_Output_GetJobOutput_ReturnsNull_ForNoOutputOrUnknownJobs()
    {
        var store = await CreateStoreAsync();
        await store.EnqueueAsync(Job(), now: T0);
        var claimed = Assert.Single(await ClaimAsync(store, T0));
        // Succeed with no output rider at all.
        await store.ReportOutcomeAsync(claimed.JobId, "w1", claimed.Attempt, new JobOutcome.Success(), T0);

        Assert.Null(await store.GetJobOutputAsync(claimed.JobId)); // a job that never set output
        Assert.Null(await store.GetJobOutputAsync(Guid.NewGuid())); // an unknown job
    }

    /// <summary>
    /// Certifies that a job's output is deleted with the job under retention.
    /// </summary>
    [Fact]
    public async Task Clause_Output_IsDeletedWithTheJob_UnderRetention()
    {
        var store = await CreateStoreAsync();
        await store.EnqueueAsync(Job(), now: T0);
        var claimed = Assert.Single(await ClaimAsync(store, T0));
        await store.ReportOutcomeAsync(claimed.JobId, "w1", claimed.Attempt, new JobOutcome.Success(), T0, output: Output());
        Assert.NotNull(await store.GetJobOutputAsync(claimed.JobId));

        // Output lives on the job row, so retention (§5.11) deletes it with the job for free.
        Assert.Equal(1, await store.PurgeTerminalAsync(
            TerminalStateClass.SucceededOrCancelled, T0.AddHours(1), maxJobs: 32));
        Assert.Null(await store.GetJobAsync(claimed.JobId));
        Assert.Null(await store.GetJobOutputAsync(claimed.JobId));
    }

    /// <summary>
    /// Certifies that a workflow member's output is retained until the whole workflow drains, then
    /// purged with it.
    /// </summary>
    [Fact]
    public async Task Clause_Output_OnAWorkflowMember_IsRetainedUntilTheWorkflowDrains()
    {
        var store = await CreateStoreAsync();
        var workflowId = Guid.NewGuid();
        var a = WorkflowMember("a");
        var b = WorkflowMember("b");
        await store.EnqueueWorkflowAsync(Workflow(workflowId, [a, b]), T0);

        // Succeed A with output; B stays live, so the Workflow has not drained — A's output is retained.
        var aClaim = Assert.Single(await ClaimAsync(store, T0, maxJobs: 1));
        var blob = Output();
        await store.ReportOutcomeAsync(aClaim.JobId, "w1", aClaim.Attempt, new JobOutcome.Success(), T0, output: blob);
        Assert.Equal(0, await store.PurgeTerminalAsync(
            TerminalStateClass.SucceededOrCancelled, T0.AddHours(1), maxJobs: 32));
        Assert.Equal(blob, (await store.GetJobOutputAsync(a.JobId))!.Value.ToArray()); // retained — sibling B is live

        // Drain B; now the whole Workflow drains as a unit and the member output is purged with it.
        var bClaim = Assert.Single(await ClaimAsync(store, T0.AddHours(1)));
        await store.ReportOutcomeAsync(bClaim.JobId, "w1", bClaim.Attempt, new JobOutcome.Success(), T0.AddHours(1));
        Assert.Equal(2, await store.PurgeTerminalAsync(
            TerminalStateClass.SucceededOrCancelled, T0.AddHours(1), maxJobs: 32));
        Assert.Null(await store.GetJobOutputAsync(a.JobId));
    }

    private static ScheduleRecord Schedule(string id, DateTimeOffset cursor) => new()
    {
        ScheduleId = id,
        Cron = CronExpression.Parse("0 3 * * *").Canonical,
        WireName = "conformance-job",
        Payload = "{}"u8.ToArray(),
        Queue = "default",
        Cursor = cursor,
    };
}
