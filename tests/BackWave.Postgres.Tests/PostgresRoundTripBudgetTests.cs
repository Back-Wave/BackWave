using BackWave.Core;
using BackWave.Storage;
using Npgsql;

namespace BackWave.Postgres.Tests;

/// <summary>
/// The Postgres adapter's round-trip budget: the round trips hot operations cost today, pinned so a
/// change has to move them on purpose.
///
/// These numbers are the point. A per-row round trip on this adapter is invisible to every other test in
/// the suite - against a co-located container a batched write and a per-row loop over the same rows
/// finish in the same blink, so the functional tests, the Conformance Suite and the torture runs all
/// stay green either way. A count is the only instrument that sees the difference, and unlike a
/// stopwatch it is deterministic enough to gate CI.
///
/// The budget fails in BOTH directions on purpose. A number that falls is the win an optimization was
/// after, and it should be recorded here in the same commit that earns it; a number that rises is a
/// regression that would otherwise reach production as nothing but a slightly slower job rate.
///
/// What the count covers: commands executed and COMMITs sent, per operation; their sum is the
/// operation's round trips. A command that holds two statements is one round trip. What it does not:
/// opening a pooled connection, BEGIN (Npgsql prepends it to the first statement, so it never waits on
/// its own), and the one-time schema-version check.
///
/// Unlike the Oracle budgets there is no LOB-read column and no fetch window here, because Postgres has
/// no LOB locator on these paths: bytea and text values arrive inside the row like every other column,
/// so commands and commits are the whole cost.
/// </summary>
[Collection("postgres")]
public sealed class PostgresRoundTripBudgetTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    // MaxClaimBatch: the default a worker group claims with, and the batch size the per-row costs multiply.
    private const int ClaimBatch = 32;

    private static NewJob Job(string queue = "budget") =>
        new(Guid.NewGuid(), "budget-test", "{}"u8.ToArray(), queue, T0);

    // The recorded budgets, measured against Postgres on Npgsql 9.0.3.
    // Each is the cost of ONE call; the arithmetic behind each number is in its test.

    private static readonly Budget Claim = new(
        "ClaimBatchAsync of 32 jobs (one queue, cold caches)",
        Commands: 5, Commits: 1);

    private static readonly Budget WarmClaim = new(
        "ClaimAsync of 32 jobs (one queue already seen unlimited)",
        Commands: 1, Commits: 0);

    private static readonly Budget ReportOutcomes = new(
        "ReportOutcomesAsync of 32 succeeded rows",
        Commands: 1, Commits: 0);

    private static readonly Budget ReportOutcomesWithOutput = new(
        "ReportOutcomesAsync of 32 succeeded rows, every one carrying job output",
        Commands: 34, Commits: 1);

    private static readonly Budget ExpireLeases = new(
        "ExpireLeasesAsync over 32 expired leases, all rescheduled",
        Commands: 3, Commits: 1);

    private static readonly Budget ListJobs = new(
        "ListJobsAsync over a 200-job page of terminal jobs",
        Commands: 2, Commits: 0);

    private static readonly Budget Enqueue = new(
        "EnqueueAsync of one untagged job",
        Commands: 1, Commits: 0);

    private static readonly Budget EnqueueTagged = new(
        "EnqueueAsync of one job carrying three tags",
        Commands: 4, Commits: 1);

    private static readonly Budget WritePumpStatuses = new(
        "WritePumpStatusesAsync of 8 Pumps that also reaps 8 old rows",
        Commands: 1, Commits: 0);

    [Fact]
    public async Task Claim_of_a_full_batch_stays_within_its_round_trip_budget()
    {
        // A cold store on purpose: the first claim after startup pays the queue-config lock and read,
        // both of which a later claim skips for the next five seconds once the queue is stamped
        // unlimited. Pinning the cold path keeps the number deterministic (a warm one would depend on
        // wall-clock timing) and pins the worse of the two, which is the one a fleet pays at boot.
        await using var store = await PostgresTestDatabase.CreateFreshStoreAsync();
        for (var i = 0; i < ClaimBatch; i++)
        {
            await store.EnqueueAsync(Job(), T0); // also warms the one-time schema check, off the measured path
        }

        // 1 queue-config advisory lock + 1 queue_limits read + 1 claim UPDATE ... RETURNING
        // + 1 batched transition insert + 1 next-due read = 5, independent of batch size.
        //
        // No per-row anything. The claim is a single set-based FOR UPDATE SKIP LOCKED whose RETURNING
        // hands back the whole job row, so 32 jobs cost the same statement as 1. Tags ride back inside
        // that same statement as a correlated json_agg, which is why there is no tag-hydration trip
        // here the way there is in the job listing below. The transition write is one set-based
        // statement over unnest() that appends and prunes together, so - unlike Oracle, which has to
        // read the highest ordinal back - there is no second statement for the ordinals or the prune.
        //
        // The concurrency-limit count is absent because this queue has no queue_limits row: `configured`
        // comes back null, so the "count leased rows" statement never runs. A limited queue costs 6.
        ClaimResult result;
        Measured measured;
        using (var scope = PostgresRoundTrips.Observe())
        {
            result = await store.ClaimBatchAsync(
                new ClaimRequest("budget-worker", ["budget"], ClaimBatch, Lease, T0));
            measured = Measured.From(scope);
        }

        Assert.Equal(ClaimBatch, result.Jobs.Count);
        AssertBudget(Claim, measured);
    }

    [Fact]
    public async Task Claim_on_a_queue_already_seen_unlimited_stays_within_its_round_trip_budget()
    {
        // The claim a running Pump makes: the queue was stamped unlimited by an earlier claim less than
        // five seconds ago, so the config lock and read are skipped. ClaimAsync rather than
        // ClaimBatchAsync because that is what a Worker Group without adaptive poll backoff calls, so no
        // next-due read either.
        await using var store = await PostgresTestDatabase.CreateFreshStoreAsync();
        for (var i = 0; i < 2 * ClaimBatch; i++)
        {
            await store.EnqueueAsync(Job(), T0);
        }
        Assert.Equal(ClaimBatch, (await store.ClaimAsync(
            new ClaimRequest("budget-worker", ["budget"], ClaimBatch, Lease, T0))).Count);

        // 1 autocommit statement, so no COMMIT: the claim UPDATE ... RETURNING, the batched transition
        // insert and the capped prune are CTEs of one statement.
        IReadOnlyList<JobRecord> claimed;
        Measured measured;
        using (var scope = PostgresRoundTrips.Observe())
        {
            claimed = await store.ClaimAsync(new ClaimRequest("budget-worker", ["budget"], ClaimBatch, Lease, T0));
            measured = Measured.From(scope);
        }

        Assert.Equal(ClaimBatch, claimed.Count);
        AssertBudget(WarmClaim, measured);
    }

    [Fact]
    public async Task ReportOutcomes_of_a_full_batch_stays_within_its_round_trip_budget()
    {
        await using var store = await PostgresTestDatabase.CreateFreshStoreAsync();
        for (var i = 0; i < ClaimBatch; i++)
        {
            await store.EnqueueAsync(Job(), T0);
        }
        var claimed = await store.ClaimAsync(new ClaimRequest("budget-worker", ["budget"], ClaimBatch, Lease, T0));
        Assert.Equal(ClaimBatch, claimed.Count);

        // The plain drain: every row succeeded, none carries output or a tag delta, so nothing but the
        // fenced state write and the transition log runs.
        //
        // 1 command and no COMMIT, independent of batch size: two statements that Npgsql runs as one
        // implicit transaction. The first locks the 32 fenced rows. The second, in a snapshot taken
        // after those locks, checks once whether any row it makes terminal parents a Dependency, and
        // since none does, writes the outcomes and their transitions as CTEs of one statement. The
        // UPDATE joins the outcome vector in through unnest() and applies the per-(worker, attempt)
        // Effect-Once fence to every row independently, and its RETURNING reports which rows matched -
        // so unlike Oracle, which has no set-valued RETURNING and pays an extra FOR UPDATE read to
        // recover the same verdict, there is nothing to read back.
        var batch = claimed
            .Select(job => new OutcomeReport(job.JobId, "budget-worker", job.Attempt, new JobOutcome.Success()))
            .ToArray();

        IReadOnlyList<OutcomeReportResult> results;
        Measured measured;
        using (var scope = PostgresRoundTrips.Observe())
        {
            results = await store.ReportOutcomesAsync(batch, T0);
            measured = Measured.From(scope);
        }

        Assert.All(results, result => Assert.Equal(OutcomeResult.Applied, result.Result));
        AssertBudget(ReportOutcomes, measured);
    }

    [Fact]
    public async Task ReportOutcomes_carrying_job_output_costs_one_statement_per_blob()
    {
        await using var store = await PostgresTestDatabase.CreateFreshStoreAsync();
        for (var i = 0; i < ClaimBatch; i++)
        {
            await store.EnqueueAsync(Job(), T0);
        }
        var claimed = await store.ClaimAsync(new ClaimRequest("budget-worker", ["budget"], ClaimBatch, Lease, T0));
        Assert.Equal(ClaimBatch, claimed.Count);

        // A batch carrying output takes the transaction: 1 outcome command (the write and its
        // transitions, as on the drain path) + ONE STATEMENT PER BLOB + 1 child-latch lookup = 34, and
        // the COMMIT. The blobs are the one honest per-row cost left on the Postgres outcome path, and
        // pinning it is how it stays visible.
        //
        // Output is deliberately kept out of the fenced batch vector: the vector carries only the
        // columns every row in a drain has, so the common pure-drain row (the budget above) rides one
        // UPDATE alone rather than dragging a bytea[] across the wire for the few rows that use it.
        // The rows that DO carry output then pay their own UPDATE each, inside the same transaction.
        //
        // Postgres can do better than this - unnest(@ids::uuid[], @outputs::bytea[]) would fold all 32
        // into the batch the same way the state vector already folds - and when someone does that work,
        // this number should fall to 2 and be recorded here in the same commit. Until then 34 is the
        // truth, and a budget that quietly said 2 would be a number nobody could trust.
        var payload = new byte[4_096];
        Random.Shared.NextBytes(payload);
        var batch = claimed
            .Select(job => new OutcomeReport(job.JobId, "budget-worker", job.Attempt, new JobOutcome.Success())
            {
                Output = payload,
            })
            .ToArray();

        IReadOnlyList<OutcomeReportResult> results;
        Measured measured;
        using (var scope = PostgresRoundTrips.Observe())
        {
            results = await store.ReportOutcomesAsync(batch, T0);
            measured = Measured.From(scope);
        }

        Assert.All(results, result => Assert.Equal(OutcomeResult.Applied, result.Result));
        AssertBudget(ReportOutcomesWithOutput, measured);

        // The budget is only worth pinning if every row actually landed - a write path that lost rows
        // would be cheap for the wrong reason.
        foreach (var job in claimed)
        {
            var stored = await store.GetJobOutputAsync(job.JobId);
            Assert.Equal(payload, stored?.ToArray());
        }
    }

    [Fact]
    public async Task Expiring_a_full_sweep_of_leases_stays_within_its_round_trip_budget()
    {
        await using var store = await PostgresTestDatabase.CreateFreshStoreAsync();
        for (var i = 0; i < ClaimBatch; i++)
        {
            await store.EnqueueAsync(Job(), T0);
        }
        var claimed = await store.ClaimAsync(new ClaimRequest("budget-worker", ["budget"], ClaimBatch, Lease, T0));
        Assert.Equal(ClaimBatch, claimed.Count);

        // 1 locking select + 1 batched reschedule UPDATE + 1 batched transition insert = 3, independent
        // of how many leases the sweep reclaims. Every job here retries rather than dead-letters, which
        // is the shape a sweep is usually full of and the cheaper of the two: the dead-letter arm adds
        // its own UPDATE and a child-latch lookup.
        //
        // This is the budget that guards the batched transition log. The sweep is the widest per-row
        // loop the adapter had - maxJobs is 500 by default where a claim batch is 32 - so a per-row
        // RecordTransitionAsync loop here costs one statement per expired lease and nothing else in the
        // suite notices. Before the log was batched, this same call cost 34: the two dispositions were
        // already set-based, but every reclaimed lease still wrote its own transition row. Put that loop
        // back and this test fails with 34 against 3, which is exactly the point of writing it down.
        var afterExpiry = T0 + Lease + TimeSpan.FromMinutes(1);
        var disposition = new RetryDisposition(MaxAttempts: 5, [TimeSpan.FromMinutes(1)]);

        int reclaimed;
        Measured measured;
        using (var scope = PostgresRoundTrips.Observe())
        {
            reclaimed = await store.ExpireLeasesAsync(afterExpiry, maxJobs: 500, ["budget"], disposition);
            measured = Measured.From(scope);
        }

        Assert.Equal(ClaimBatch, reclaimed);
        AssertBudget(ExpireLeases, measured);
    }

    [Fact]
    public async Task ListJobs_over_a_full_page_of_terminal_jobs_stays_within_its_round_trip_budget()
    {
        await using var store = await PostgresTestDatabase.CreateFreshStoreAsync();
        var page = store.Bounds.MaxMonitorPageSize;
        for (var i = 0; i < page; i++)
        {
            await store.EnqueueAsync(Job(), T0);
        }

        // Terminal is the shape a dashboard's default view is full of, and the expensive one on the
        // adapters that materialize a terminal_cause separately. Driving 200 jobs to terminal through
        // the store would be seven claim/report cycles of setup noise, so the row SHAPE is set directly
        // here - the assertion below proves the shape actually took.
        await MarkEveryJobDeadLetteredAsync();

        // 1 page select + 1 batched tag hydration = 2 statements, independent of page size. The tag
        // hydration is a single `job_id = ANY(@ids)` read for the whole page, never N+1, and it runs
        // even when no job on the page carries a tag - so 2 is the floor as well as the ceiling here.
        //
        // Terminal costs nothing extra on Postgres: terminal_cause is a text column that arrives in the
        // row. That is the whole difference from Oracle, where the same page paid a LOB round trip per
        // row before its read paths were taught to prefetch.
        IReadOnlyList<JobRecord> jobs;
        Measured measured;
        using (var scope = PostgresRoundTrips.Observe())
        {
            jobs = await store.ListJobsAsync(new JobQuery { MaxResults = page });
            measured = Measured.From(scope);
        }

        Assert.Equal(page, jobs.Count);
        Assert.All(jobs, job => Assert.False(string.IsNullOrEmpty(job.TerminalCause)));
        AssertBudget(ListJobs, measured);
    }

    [Fact]
    public async Task Enqueue_of_one_job_stays_within_its_round_trip_budget()
    {
        await using var store = await PostgresTestDatabase.CreateFreshStoreAsync();
        await store.EnqueueAsync(Job(), T0); // warms the one-time schema check, off the measured path

        // 1 autocommit insert (the job row and its Scheduled transition in one CTE), and no COMMIT: a
        // job with no parents and no tags is that one statement, so it needs no transaction of its own.
        // This is still one round trip per job; only a bulk enqueue on the Storage Contract can
        // amortize it.
        Measured measured;
        using (var scope = PostgresRoundTrips.Observe())
        {
            await store.EnqueueAsync(Job(), T0);
            measured = Measured.From(scope);
        }

        AssertBudget(Enqueue, measured);
    }

    [Fact]
    public async Task Enqueue_of_a_tagged_job_costs_one_statement_per_tag()
    {
        await using var store = await PostgresTestDatabase.CreateFreshStoreAsync();
        await store.EnqueueAsync(Job(), T0);

        var tagged = Job() with
        {
            Tags = JobTags.Empty.WithTag("tenant", "acme").WithTag("region", "eu").WithTag("tier", "gold"),
        };

        Measured measured;
        using (var scope = PostgresRoundTrips.Observe())
        {
            await store.EnqueueAsync(tagged, T0);
            measured = Measured.From(scope);
        }

        AssertBudget(EnqueueTagged, measured);
    }

    [Fact]
    public async Task WritePumpStatuses_of_a_whole_process_stays_within_its_round_trip_budget()
    {
        await using var store = await PostgresTestDatabase.CreateFreshStoreAsync();
        static PumpStatus[] Pumps(DateTimeOffset reportedAt) => [.. Enumerable.Range(0, 8).Select(i => new PumpStatus
        {
            WorkerId = $"budget-host:group-{i}:{Guid.NewGuid():N}",
            WorkerGroup = $"group-{i}",
            State = PumpState.Running,
            ReportedAt = reportedAt,
            ReportInterval = TimeSpan.FromSeconds(30),
        })];
        var old = Pumps(T0);
        await store.WritePumpStatusesAsync(old, T0, maxReap: 100);

        // 1 command = 1 statement on this counter: the unnest upsert and the capped reap travel together
        // in one flush, so the write costs one round trip however many Pumps the process runs and
        // however many rows it reaps.
        var fresh = Pumps(T0.AddMinutes(10));
        Measured measured;
        using (var scope = PostgresRoundTrips.Observe())
        {
            await store.WritePumpStatusesAsync(fresh, T0.AddMinutes(5), maxReap: 100);
            measured = Measured.From(scope);
        }

        var listed = await store.ListPumpStatusesAsync();
        Assert.All(fresh, pump => Assert.Contains(listed, s => s.WorkerId == pump.WorkerId));
        Assert.All(old, pump => Assert.DoesNotContain(listed, s => s.WorkerId == pump.WorkerId));
        AssertBudget(WritePumpStatuses, measured);
    }

    [Fact]
    public void An_unobserved_counter_allocates_nothing()
    {
        // The claim that this instrument is free in production, measured rather than asserted. The hook
        // reads one volatile int and returns before it reaches the AsyncLocal, so an unobserved process
        // must allocate exactly zero bytes no matter how many statements it runs.
        for (var i = 0; i < 1_000; i++)
        {
            PostgresRoundTrips.CountCommand(); // let the JIT tier up before the measurement
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1_000_000; i++)
        {
            PostgresRoundTrips.CountCommand();
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void A_scope_counts_only_what_happens_inside_it()
    {
        // The instrument's own correctness check: without this, a budget of zero would pass forever.
        PostgresRoundTrips.CountCommand(); // outside any scope - must not be seen by the scope below

        using var scope = PostgresRoundTrips.Observe();
        PostgresRoundTrips.CountCommand();
        PostgresRoundTrips.CountCommand();

        Assert.Equal(2, scope.Commands);

        scope.Dispose();
        PostgresRoundTrips.CountCommand(); // after the scope closed - the counts are frozen
        Assert.Equal(2, scope.Commands);
    }

    // Sets every job's row to the Dead-Lettered shape in one statement: terminal state, terminal
    // instant, and - the part that matters here - a non-null terminal_cause.
    private static async Task MarkEveryJobDeadLetteredAsync()
    {
        await using var dataSource = NpgsqlDataSource.Create(PostgresTestDatabase.ConnectionString);
        await using var update = dataSource.CreateCommand(
            """
            UPDATE backwave.jobs
            SET state = 5, terminal_at = now(), terminal_cause = 'budget fixture: dead-lettered'
            """);
        await update.ExecuteNonQueryAsync();
    }

    private sealed record Budget(string Operation, int Commands, int Commits);

    private sealed record Measured(int Commands, int Commits)
    {
        public static Measured From(PostgresRoundTripScope scope) => new(scope.Commands, scope.Commits);
    }

    private static void AssertBudget(Budget budget, Measured measured)
    {
        if (measured.Commands == budget.Commands && measured.Commits == budget.Commits)
        {
            return;
        }
        Assert.Fail(
            $"""
            Postgres round-trip budget moved: {budget.Operation}
              commands: budgeted {budget.Commands}, measured {measured.Commands} ({Delta(budget.Commands, measured.Commands)})
              commits:  budgeted {budget.Commits}, measured {measured.Commits} ({Delta(budget.Commits, measured.Commits)})

            A budget moves in either direction only deliberately. If a change was meant to move this,
            record the new number in PostgresRoundTripBudgetTests in the same commit. If it was not, the
            operation just gained or lost database round trips that no other test in the suite can see.
            """);
    }

    private static string Delta(long budgeted, long measured) => measured switch
    {
        _ when measured > budgeted => $"+{measured - budgeted}, a regression",
        _ when measured < budgeted => $"{measured - budgeted}, an improvement to record",
        _ => "unchanged",
    };
}
