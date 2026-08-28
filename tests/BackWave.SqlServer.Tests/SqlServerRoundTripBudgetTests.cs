using System.Data;
using BackWave.Core;
using BackWave.Storage;
using Microsoft.Data.SqlClient;

namespace BackWave.SqlServer.Tests;

/// <summary>
/// The SQL Server adapter's round-trip budget: the statements its hot operations cost today, pinned so a
/// change has to move them on purpose.
///
/// These numbers are the point. A per-row round trip is invisible to every other test in the suite -
/// against a co-located container a batched write and a per-row loop finish in the same blink, so a
/// regression that doubles an operation's wire cost ships green and surfaces only as a slightly lower job
/// rate against a real network. A count separates them, and unlike a stopwatch it is deterministic enough
/// to gate CI. That matters more here than on any other adapter: the container runs emulated on Apple
/// Silicon, so wall-clock timing in this suite measures the emulator, while a statement count does not
/// move at all.
///
/// The budget fails in BOTH directions on purpose. A number that falls is the win an optimization was
/// after, and it should be recorded here in the same commit that earns it; a number that rises is a
/// regression that would otherwise reach production as nothing but a slightly slower job rate.
///
/// What the count covers: statements executed, per operation, where a statement is one execute and so one
/// TDS request - a multi-statement CommandText the adapter deliberately sends as one batch counts once,
/// because the wire carries it once. What it does not cover: opening a pooled connection, BEGIN/COMMIT,
/// and the one-time schema-version check. Those are round trips too, but they are a small constant per
/// operation rather than a cost per row - and per row is the shape of the problem these budgets watch.
/// </summary>
[Collection("sqlserver")]
public sealed class SqlServerRoundTripBudgetTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    // MaxClaimBatch: the default a worker group claims with, and the batch size the per-row costs multiply.
    private const int ClaimBatch = 32;

    private static NewJob Job(string queue = "budget") =>
        new(Guid.NewGuid(), "budget-test", "{}"u8.ToArray(), queue, T0);

    // The recorded budgets: measured 2026-08-28 against SQL Server 2022 on Microsoft.Data.SqlClient
    // 6.1.1, at schema version 1. Each is the cost of ONE call; the arithmetic behind each number is
    // in its test.

    private static readonly Budget Claim = new(
        "ClaimBatchAsync of 32 jobs (one queue, cold caches)", Statements: 6);

    private static readonly Budget ReportOutcomes = new(
        "ReportOutcomesAsync of 32 succeeded rows", Statements: 3);

    private static readonly Budget ReportOutcomesWithOutput = new(
        "ReportOutcomesAsync of 32 succeeded rows, every one carrying job output", Statements: 35);

    private static readonly Budget ExpireLeases = new(
        "ExpireLeasesAsync over 32 expired leases, all rescheduled", Statements: 3);

    private static readonly Budget ListJobs = new(
        "ListJobsAsync over a 200-job page of terminal jobs", Statements: 2);

    private static readonly Budget Enqueue = new(
        "EnqueueAsync of one untagged job", Statements: 2);

    private static readonly Budget EnqueueTagged = new(
        "EnqueueAsync of one job carrying three tags", Statements: 5);

    [Fact]
    public async Task Claim_of_a_full_batch_stays_within_its_round_trip_budget()
    {
        // A cold store on purpose: the first claim after startup pays the queue-config applock, the
        // queue_limits read, and the tags-in-use probe, all of which a later claim skips for the next
        // few seconds. Pinning the cold path keeps the number deterministic (a warm one would depend on
        // wall-clock timing) and pins the worse of the two, which is the one a fleet pays at boot.
        var store = await SqlServerTestDatabase.CreateFreshStoreAsync();
        for (var i = 0; i < ClaimBatch; i++)
        {
            await store.EnqueueAsync(Job(), T0); // also warms the one-time schema check, off the measured path
        }

        // 1 queue-config applock + 1 queue_limits read + 1 claim UPDATE ... OUTPUT
        // + 1 batched transition insert + 1 tags-in-use probe + 1 next-due read = 6, independent of
        // batch size. The claim is a single UPDATE with OUTPUT, so 32 leased rows come back on the same
        // trip that writes them, and the transition insert is set-based over OPENJSON, so 32 log entries
        // cost one statement. No prune: the batch recorder issues a DELETE only when some job in it
        // reached MaxTransitionsPerJob, and a freshly claimed job is on its second transition.
        //
        // ClaimBatchAsync, not ClaimAsync: the extra statement over the plain claim is the next-due read,
        // which is this adapter's ONLY idle-wakeup mechanism (SQL Server has no Wake-Up Hint channel), so
        // it is worth having pinned where a change would have to notice it.
        ClaimResult result;
        Measured measured;
        using (var scope = SqlServerRoundTrips.Observe())
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
        var store = await SqlServerTestDatabase.CreateFreshStoreAsync();
        for (var i = 0; i < ClaimBatch; i++)
        {
            await store.EnqueueAsync(Job(), T0);
        }
        var claimed = await store.ClaimAsync(new ClaimRequest("budget-worker", ["budget"], ClaimBatch, Lease, T0));
        Assert.Equal(ClaimBatch, claimed.Count);

        // The plain drain: every row succeeded, none carries output or a tag delta, so nothing but the
        // fenced state write and the transition log runs.
        // 1 fenced batch UPDATE ... OUTPUT + 1 batched transition insert + 1 child-latch probe = 3,
        // independent of batch size. The fence is applied per row inside that one UPDATE - OPENJSON
        // unpacks the payload and the WHERE tests each row's (worker, attempt) independently - and
        // OUTPUT reports which rows matched, so the per-row Effect-Once verdict costs no extra trip.
        // The child-latch probe runs because Succeeded is terminal: one lookup asks whether ANY of the
        // 32 ids parents a Dependency, and the answer here is no, so nothing cascades.
        // As above, no job in this batch is near the cap, so the batch recorder issues no prune DELETE.
        var batch = claimed
            .Select(job => new OutcomeReport(job.JobId, "budget-worker", job.Attempt, new JobOutcome.Success()))
            .ToArray();

        IReadOnlyList<OutcomeReportResult> results;
        Measured measured;
        using (var scope = SqlServerRoundTrips.Observe())
        {
            results = await store.ReportOutcomesAsync(batch, T0);
            measured = Measured.From(scope);
        }

        Assert.All(results, result => Assert.Equal(OutcomeResult.Applied, result.Result));
        AssertBudget(ReportOutcomes, measured);
    }

    [Fact]
    public async Task ReportOutcomes_carrying_job_output_still_costs_one_statement_per_blob()
    {
        var store = await SqlServerTestDatabase.CreateFreshStoreAsync();
        for (var i = 0; i < ClaimBatch; i++)
        {
            await store.EnqueueAsync(Job(), T0);
        }
        var claimed = await store.ClaimAsync(new ClaimRequest("budget-worker", ["budget"], ClaimBatch, Lease, T0));
        Assert.Equal(ClaimBatch, claimed.Count);

        // The drain budget above plus ONE STATEMENT PER ROW: 3 + 32 = 35. Output is the one write on
        // this path that is still a per-row loop on this adapter - the fenced UPDATE cannot carry the
        // blob, because OPENJSON has no varbinary(max) column type, so a blob would have to go over as
        // base64 text and be converted back per row.
        //
        // This number is deliberately recorded as it is rather than as an aspiration: it is the largest
        // per-row cost left in the SQL Server adapter, and pinning it is what makes a future fix visible
        // as an improvement rather than invisible. A table-valued parameter would collapse the 32 to 1;
        // until then this budget at least stops the count from growing further, and stops the plain
        // drain above from quietly acquiring the same shape.
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
        using (var scope = SqlServerRoundTrips.Observe())
        {
            results = await store.ReportOutcomesAsync(batch, T0);
            measured = Measured.From(scope);
        }

        Assert.All(results, result => Assert.Equal(OutcomeResult.Applied, result.Result));
        AssertBudget(ReportOutcomesWithOutput, measured);

        // The budget is only worth pinning if every row actually landed.
        foreach (var job in claimed)
        {
            var stored = await store.GetJobOutputAsync(job.JobId);
            Assert.Equal(payload, stored?.ToArray());
        }
    }

    [Fact]
    public async Task Expiring_a_full_sweep_of_leases_stays_within_its_round_trip_budget()
    {
        var store = await SqlServerTestDatabase.CreateFreshStoreAsync();
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
        // The sweep is the widest per-row loop the adapter had - maxJobs is 500 by default where a claim
        // batch is 32 - so this is where a per-row transition write costs the most. Before the sweep's
        // transition log was batched this same call cost 34 statements, and nothing in the suite could
        // tell the difference; this budget is the guard on that.
        var afterExpiry = T0 + Lease + TimeSpan.FromMinutes(1);
        var disposition = new RetryDisposition(MaxAttempts: 5, [TimeSpan.FromMinutes(1)]);

        int reclaimed;
        Measured measured;
        using (var scope = SqlServerRoundTrips.Observe())
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
        var store = await SqlServerTestDatabase.CreateFreshStoreAsync();
        var page = store.Bounds.MaxMonitorPageSize;
        for (var i = 0; i < page; i++)
        {
            await store.EnqueueAsync(Job(), T0);
        }

        // Terminal is the shape a dashboard's default view is full of, and the widest row this query
        // reads: it carries a non-null terminal_cause nvarchar(max) on top of the payload varbinary(max).
        // Driving 200 jobs to terminal through the store would be seven claim/report cycles of setup
        // noise, so the row SHAPE is set directly here - the assertion below proves the shape took.
        await MarkEveryJobDeadLetteredAsync();

        // 1 page select + 1 batched tag hydration = 2 statements, independent of page size. The
        // hydration is one `job_id IN (…)` read over all 200 ids, never N+1, and SqlClient streams the
        // page's max-length columns inline with the rows, so a wide row costs no extra trip.
        IReadOnlyList<JobRecord> jobs;
        Measured measured;
        using (var scope = SqlServerRoundTrips.Observe())
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
        var store = await SqlServerTestDatabase.CreateFreshStoreAsync();
        await store.EnqueueAsync(Job(), T0); // warms the one-time schema check, off the measured path

        // 1 job insert + 1 transition insert = 2 statements. The transition insert is the one that is
        // easy to lose: it OUTPUTs the ordinal it assigned precisely so the per-job-life prune can be
        // SKIPPED, and a job on its first transition is nowhere near MaxTransitionsPerJob, so no DELETE
        // runs. Put the unconditional prune back and this becomes 3 - one extra round trip on the
        // single hottest per-call path in the system, which no functional test would notice.
        //
        // Enqueue is a per-CALL path, not a per-row one, so there is no batch here for a set-based
        // statement to collapse. Two is also NOT the number the wire charges: this counter deliberately
        // excludes acquiring a pooled connection, BEGIN, and COMMIT (see the class remarks), and enqueue
        // pays all three on every single call because it opens and commits its own transaction per job.
        // That fixed per-call cost is what only a bulk enqueue on the Storage Contract can amortize.
        Measured measured;
        using (var scope = SqlServerRoundTrips.Observe())
        {
            await store.EnqueueAsync(Job(), T0);
            measured = Measured.From(scope);
        }

        AssertBudget(Enqueue, measured);
    }

    [Fact]
    public async Task Enqueue_of_a_tagged_job_costs_one_statement_per_tag()
    {
        var store = await SqlServerTestDatabase.CreateFreshStoreAsync();
        await store.EnqueueAsync(Job(), T0);

        // 1 job insert + 3 tag inserts + 1 transition insert = 5. The tag insert is still a per-tag
        // loop on this adapter, unlike the set-based writes on the claim and outcome paths, so the
        // enqueue budget grows linearly with the tag count.
        //
        // Recorded honestly rather than aspirationally: a caller tagging every job pays a round trip per
        // tag, and pinning that is what makes a future set-based tag insert show up here as an
        // improvement to record instead of a change nobody can see. It also fences the number from
        // above - a tag write that grew to two statements per tag would fail this test at 8.
        var tagged = Job() with
        {
            Tags = JobTags.Empty.WithTag("tenant", "acme").WithTag("region", "eu").WithTag("tier", "gold"),
        };

        Measured measured;
        using (var scope = SqlServerRoundTrips.Observe())
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
            SqlServerRoundTrips.CountStatement(); // let the JIT tier up before the measurement
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1_000_000; i++)
        {
            SqlServerRoundTrips.CountStatement();
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void A_scope_counts_only_what_happens_inside_it()
    {
        // The instrument's own correctness check: without this, a budget of zero would pass forever.
        SqlServerRoundTrips.CountStatement(); // outside any scope - must not be seen by the scope below

        using var scope = SqlServerRoundTrips.Observe();
        SqlServerRoundTrips.CountStatement();
        SqlServerRoundTrips.CountStatement();

        Assert.Equal(2, scope.Statements);

        scope.Dispose();
        SqlServerRoundTrips.CountStatement(); // after the scope closed - the counts are frozen
        Assert.Equal(2, scope.Statements);
    }

    // Sets every job's row to the Dead-Lettered shape in one statement: terminal state, terminal instant,
    // and - the part that matters here - a non-null terminal_cause.
    private static async Task MarkEveryJobDeadLetteredAsync()
    {
        await using var connection = new SqlConnection(SqlServerTestDatabase.ConnectionString);
        await connection.OpenAsync();
        await using var update = connection.CreateCommand();
        update.CommandText =
            """
            UPDATE backwave.jobs
            SET state = 5, terminal_at = @now, terminal_cause = N'budget fixture: dead-lettered'
            """;
        update.Parameters.Add("now", SqlDbType.DateTimeOffset).Value = T0;
        await update.ExecuteNonQueryAsync();
    }

    private sealed record Budget(string Operation, int Statements);

    private sealed record Measured(int Statements)
    {
        public static Measured From(SqlServerRoundTripScope scope) => new(scope.Statements);
    }

    private static void AssertBudget(Budget budget, Measured measured)
    {
        if (measured.Statements == budget.Statements)
        {
            return;
        }
        Assert.Fail(
            $"""
            SQL Server round-trip budget moved: {budget.Operation}
              statements: budgeted {budget.Statements}, measured {measured.Statements} ({Delta(budget.Statements, measured.Statements)})

            A budget moves in either direction only deliberately. If a change was meant to move this,
            record the new number in SqlServerRoundTripBudgetTests in the same commit. If it was not, the
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
