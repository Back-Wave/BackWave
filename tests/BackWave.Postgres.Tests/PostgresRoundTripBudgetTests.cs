using BackWave.Core;
using BackWave.Storage;
using Npgsql;

namespace BackWave.Postgres.Tests;

/// <summary>
/// The Postgres adapter's round-trip budget: the statements seven hot operations cost today, pinned so a
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
/// What the count covers: statements executed, per operation. What it does not: opening a pooled
/// connection, BEGIN, COMMIT, and the one-time schema-version check. Those are round trips too, but they
/// are a small constant per operation rather than a cost per row - and per row is the shape of the
/// problem these budgets exist to watch.
///
/// Unlike the Oracle budgets there is no LOB-read column and no fetch window here, because Postgres has
/// no LOB locator on these paths: bytea and text values arrive inside the row like every other column,
/// so a statement count is the whole cost. Every number below is therefore a plain statement count.
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

    // The recorded budgets: measured 2026-08-28 against Postgres on Npgsql 9.0.3, at schema version 1.
    // Each is the cost of ONE call; the arithmetic behind each number is in its test.

    private static readonly Budget Claim = new(
        "ClaimBatchAsync of 32 jobs (one queue, cold caches)",
        Statements: 5);

    private static readonly Budget ReportOutcomes = new(
        "ReportOutcomesAsync of 32 succeeded rows",
        Statements: 3);

    private static readonly Budget ReportOutcomesWithOutput = new(
        "ReportOutcomesAsync of 32 succeeded rows, every one carrying job output",
        Statements: 35);

    private static readonly Budget ExpireLeases = new(
        "ExpireLeasesAsync over 32 expired leases, all rescheduled",
        Statements: 3);

    private static readonly Budget ListJobs = new(
        "ListJobsAsync over a 200-job page of terminal jobs",
        Statements: 2);

    private static readonly Budget Enqueue = new(
        "EnqueueAsync of one untagged job",
        Statements: 3);

    private static readonly Budget EnqueueTagged = new(
        "EnqueueAsync of one job carrying three tags",
        Statements: 6);

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
        // here the way there is in the job listing below. The transition insert is set-based over
        // unnest(), and its own RETURNING hands back the assigned ordinals, so - unlike Oracle, which
        // rejects RETURNING on an INSERT ... SELECT and has to read the highest ordinal back - there is
        // no second statement to learn them. No prune DELETE either: the batch recorder issues one only
        // when some job in the batch reached MaxTransitionsPerJob, and a freshly claimed job is on its
        // second transition.
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
        // 1 fenced batch UPDATE ... RETURNING + 1 batched transition insert + 1 child-latch probe = 3,
        // independent of batch size. The UPDATE joins the outcome vector in through unnest() and applies
        // the per-(worker, attempt) Effect-Once fence to every row independently, and its RETURNING
        // reports which rows matched - so unlike Oracle, which has no set-valued RETURNING and pays an
        // extra FOR UPDATE read to recover the same verdict, there is nothing to read back.
        //
        // The third statement is the child-latch probe: every row here is Succeeded, hence terminal,
        // so the adapter asks once - SELECT DISTINCT parent_id ... = ANY(@ids) - whether any of these
        // 32 ids parents a dependency. It finds none, and the per-parent cascade never runs. A batch
        // of pure retries would not pay it at all: a retry row stays non-terminal and gates nothing.
        // As above, no job in this batch is near the cap, so the batch recorder issues no prune DELETE.
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

        // The drain budget above plus ONE STATEMENT PER BLOB: 3 + 32 = 35. This is the one honest
        // per-row cost left on the Postgres outcome path, and pinning it is how it stays visible.
        //
        // Output is deliberately kept out of the fenced batch vector: the vector carries only the
        // columns every row in a drain has, so the common pure-drain row (the budget above) rides one
        // UPDATE alone rather than dragging a bytea[] across the wire for the few rows that use it.
        // The rows that DO carry output then pay their own UPDATE each, inside the same transaction.
        //
        // Postgres can do better than this - unnest(@ids::uuid[], @outputs::bytea[]) would fold all 32
        // into the batch the same way the state vector already folds - and when someone does that work,
        // this number should fall to 4 and be recorded here in the same commit. Until then 35 is the
        // truth, and a budget that quietly said 4 would be a number nobody could trust.
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

        // 1 job insert + 1 transition insert + 1 pg_notify wake-up hint = 3 statements. Enqueue is a
        // per-CALL path, not a per-row one, so there is no batch here for a set-based statement to
        // collapse - which is why the batching work in this file's other budgets left this number alone.
        //
        // This is the budget that guards the skipped prune. The transition insert RETURNs the ordinal
        // it just assigned, and the recorder issues its bounded DELETE only when that ordinal reached
        // MaxTransitionsPerJob. A brand-new job's first transition is ordinal 0, so the DELETE is
        // skipped - it could only ever have been a no-op, since its bound is MAX(ordinal) - cap, which
        // is negative while the newest ordinal is under the cap. Take the skip back out and every
        // transition on every job in the system pays a wasted round trip, and this test fails with 4
        // against 3 while nothing else in the suite changes.
        //
        // Three is also NOT the number the wire charges. This counter deliberately excludes acquiring a
        // pooled connection, BEGIN and COMMIT (see the class remarks), and enqueue pays all of them on
        // every call because it opens and commits its own transaction per job. That fixed per-call cost
        // is why enqueue barely moves when the claim and outcome paths get faster, and only a bulk
        // enqueue on the Storage Contract can amortize it.
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

        // The untagged budget above plus ONE STATEMENT PER TAG: 3 + 3 = 6. InsertTagsAsync walks the
        // set and binds one INSERT ... ON CONFLICT DO NOTHING per tag, so a job carrying n tags costs
        // n statements on top of its enqueue.
        //
        // That is a per-row cost with an obvious set-based form - unnest(@keys::text[], @values::text[])
        // would fold the whole set into one INSERT, the way the transition batch already folds - and
        // when someone writes it this number should fall to 4. Recording 6 rather than a rounder number
        // is what makes the size of that win visible; recording an aspirational 4 would just make this
        // test fail on a green tree.
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
    public void An_unobserved_counter_allocates_nothing()
    {
        // The claim that this instrument is free in production, measured rather than asserted. The hook
        // reads one volatile int and returns before it reaches the AsyncLocal, so an unobserved process
        // must allocate exactly zero bytes no matter how many statements it runs.
        for (var i = 0; i < 1_000; i++)
        {
            PostgresRoundTrips.CountStatement(); // let the JIT tier up before the measurement
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1_000_000; i++)
        {
            PostgresRoundTrips.CountStatement();
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void A_scope_counts_only_what_happens_inside_it()
    {
        // The instrument's own correctness check: without this, a budget of zero would pass forever.
        PostgresRoundTrips.CountStatement(); // outside any scope - must not be seen by the scope below

        using var scope = PostgresRoundTrips.Observe();
        PostgresRoundTrips.CountStatement();
        PostgresRoundTrips.CountStatement();

        Assert.Equal(2, scope.Statements);

        scope.Dispose();
        PostgresRoundTrips.CountStatement(); // after the scope closed - the counts are frozen
        Assert.Equal(2, scope.Statements);
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

    private sealed record Budget(string Operation, int Statements);

    private sealed record Measured(int Statements)
    {
        public static Measured From(PostgresRoundTripScope scope) => new(scope.Statements);
    }

    private static void AssertBudget(Budget budget, Measured measured)
    {
        if (measured.Statements == budget.Statements)
        {
            return;
        }
        Assert.Fail(
            $"""
            Postgres round-trip budget moved: {budget.Operation}
              statements: budgeted {budget.Statements}, measured {measured.Statements} ({Delta(budget.Statements, measured.Statements)})

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
