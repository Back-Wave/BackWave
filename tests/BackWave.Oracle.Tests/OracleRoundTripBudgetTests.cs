using BackWave.Storage;
using Oracle.ManagedDataAccess.Client;

namespace BackWave.Oracle.Tests;

/// <summary>
/// The Oracle adapter's round-trip budget: the statements and LOB reads three hot operations cost today,
/// pinned so a change has to move them on purpose.
///
/// These numbers are the point. This adapter's per-row round trips are invisible to every other test in
/// the suite - against a co-located container a batched write and a per-row loop finish in the same
/// blink, which is exactly how the adapter shipped with a clean conformance and torture record while
/// costing roughly thirty times what Postgres costs for the same claim. A count is the only instrument
/// that sees it, and unlike a stopwatch it is deterministic enough to gate CI.
///
/// The budget fails in BOTH directions on purpose. A number that falls is the win an optimization was
/// after, and it should be recorded here in the same commit that earns it; a number that rises is a
/// regression that would otherwise reach production as nothing but a slightly slower job rate.
///
/// What the count covers: statements executed and LOB values read, per operation. What it does not:
/// opening a pooled connection, COMMIT, and the one-time schema-version check. Those are round trips
/// too, but they are a small constant per operation rather than a cost per row - and per row is the
/// shape of the problem these budgets exist to watch.
/// </summary>
[Collection("oracle")]
public sealed class OracleRoundTripBudgetTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    // MaxClaimBatch: the default a worker group claims with, and the batch size the per-row costs multiply.
    private const int ClaimBatch = 32;

    private static NewJob Job(string queue = "budget") =>
        new(Guid.NewGuid(), "budget-test", "{}"u8.ToArray(), queue, T0);

    // The recorded budgets: measured 2026-08-22 against Oracle Free 23 on ODP.NET 23.9.1, at schema
    // version 1. Each is the cost of ONE call; the arithmetic behind each number is in its test.

    private static readonly Budget Claim = new(
        "ClaimBatchAsync of 32 jobs (one queue, cold caches)", Statements: 9, LobReads: 32);

    private static readonly Budget ReportOutcomes = new(
        "ReportOutcomesAsync of 32 succeeded rows", Statements: 35, LobReads: 0);

    private static readonly Budget ListJobs = new(
        "ListJobsAsync over a 200-job page of terminal jobs", Statements: 2, LobReads: 400);

    [Fact]
    public async Task Claim_of_a_full_batch_stays_within_its_round_trip_budget()
    {
        // A cold store on purpose: the first claim after startup pays the queue-config read and the
        // tags-in-use probe, both of which a later claim skips for the next five seconds. Pinning the
        // cold path keeps the number deterministic (a warm one would depend on wall-clock timing) and
        // pins the worse of the two, which is the one a fleet pays on every node at boot.
        var store = await OracleTestDatabase.CreateFreshStoreAsync();
        for (var i = 0; i < ClaimBatch; i++)
        {
            await store.EnqueueAsync(Job(), T0); // also warms the one-time schema check, off the measured path
        }

        // 2 queue-lock anchor + 1 queue_limits read + 1 claim select + 1 lease update
        // + 1 batched transition insert + 1 highest-ordinal read + 1 tags probe + 1 next-due = 9. The
        // insert is set-based over JSON_TABLE, so 32 rows cost one statement; the read after it is how
        // the batch learns the highest ordinal it assigned, because Oracle rejects RETURNING on an
        // INSERT ... SELECT. No prune: the batch recorder issues a DELETE only when some job in it
        // reached MaxTransitionsPerJob, and a freshly claimed job is on its second transition.
        // The 32 LOB reads are one payload BLOB per claimed row; terminal_cause is null on a Scheduled
        // job, and a null LOB costs nothing.
        ClaimResult result;
        int statements, lobReads;
        using (var scope = OracleRoundTrips.Observe())
        {
            result = await store.ClaimBatchAsync(
                new ClaimRequest("budget-worker", ["budget"], ClaimBatch, Lease, T0));
            (statements, lobReads) = (scope.Statements, scope.LobReads);
        }

        Assert.Equal(ClaimBatch, result.Jobs.Count);
        AssertBudget(Claim, statements, lobReads);
    }

    [Fact]
    public async Task ReportOutcomes_of_a_full_batch_stays_within_its_round_trip_budget()
    {
        var store = await OracleTestDatabase.CreateFreshStoreAsync();
        for (var i = 0; i < ClaimBatch; i++)
        {
            await store.EnqueueAsync(Job(), T0);
        }
        var claimed = await store.ClaimAsync(new ClaimRequest("budget-worker", ["budget"], ClaimBatch, Lease, T0));
        Assert.Equal(ClaimBatch, claimed.Count);

        // The plain drain: every row succeeded, none carries output or a tag delta, so nothing but the
        // fenced state write and the transition log runs.
        // 32 fenced updates + 1 batched transition insert + 1 highest-ordinal read + 1 child-latch probe
        // = 35. As above, no job in this batch is near the cap, so the batch recorder issues no prune
        // DELETE. The 32 fenced updates are still per row - batching them is issue 0264.
        // Writing a terminal_cause CLOB is a parameter bind, not a materialization, so no LOB is read.
        var batch = claimed
            .Select(job => new OutcomeReport(job.JobId, "budget-worker", job.Attempt, new JobOutcome.Success()))
            .ToArray();

        IReadOnlyList<OutcomeReportResult> results;
        int statements, lobReads;
        using (var scope = OracleRoundTrips.Observe())
        {
            results = await store.ReportOutcomesAsync(batch, T0);
            (statements, lobReads) = (scope.Statements, scope.LobReads);
        }

        Assert.All(results, result => Assert.Equal(OutcomeResult.Applied, result.Result));
        AssertBudget(ReportOutcomes, statements, lobReads);
    }

    [Fact]
    public async Task ListJobs_over_a_full_page_of_terminal_jobs_stays_within_its_round_trip_budget()
    {
        var store = await OracleTestDatabase.CreateFreshStoreAsync();
        var page = store.Bounds.MaxMonitorPageSize;
        for (var i = 0; i < page; i++)
        {
            await store.EnqueueAsync(Job(), T0);
        }

        // Terminal is the expensive shape and the one a dashboard's default view is full of: a live job
        // has a null terminal_cause, which costs nothing to read, while a terminal one carries a CLOB
        // that costs a round trip of its own on top of the payload BLOB. Driving 200 jobs to terminal
        // through the store would be seven claim/report cycles of setup noise, so the row SHAPE is set
        // directly here - the assertion below proves the shape actually took.
        await MarkEveryJobDeadLetteredAsync();

        // 1 page select + 1 batched tag hydration = 2 statements, independent of page size.
        // 200 payload BLOBs + 200 terminal_cause CLOBs = 400 LOB reads, one round trip each: the whole
        // cost of this operation is per row, and none of it is in the statement count.
        IReadOnlyList<JobRecord> jobs;
        int statements, lobReads;
        using (var scope = OracleRoundTrips.Observe())
        {
            jobs = await store.ListJobsAsync(new JobQuery { MaxResults = page });
            (statements, lobReads) = (scope.Statements, scope.LobReads);
        }

        Assert.Equal(page, jobs.Count);
        Assert.All(jobs, job => Assert.False(string.IsNullOrEmpty(job.TerminalCause)));
        AssertBudget(ListJobs, statements, lobReads);
    }

    [Fact]
    public void An_unobserved_counter_allocates_nothing()
    {
        // The claim that this instrument is free in production, measured rather than asserted. Every hook
        // reads one volatile int and returns before it reaches the AsyncLocal, so an unobserved process
        // must allocate exactly zero bytes no matter how many statements it runs.
        for (var i = 0; i < 1_000; i++)
        {
            OracleRoundTrips.CountStatement(); // let the JIT tier up before the measurement
            OracleRoundTrips.CountLobRead();
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1_000_000; i++)
        {
            OracleRoundTrips.CountStatement();
            OracleRoundTrips.CountLobRead();
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void A_scope_counts_only_what_happens_inside_it()
    {
        // The instrument's own correctness check: without this, a budget of zero would pass forever.
        OracleRoundTrips.CountStatement(); // outside any scope - must not be seen by the scope below

        using var scope = OracleRoundTrips.Observe();
        OracleRoundTrips.CountStatement();
        OracleRoundTrips.CountStatement();
        OracleRoundTrips.CountLobRead();

        Assert.Equal(2, scope.Statements);
        Assert.Equal(1, scope.LobReads);

        scope.Dispose();
        OracleRoundTrips.CountStatement(); // after the scope closed - the counts are frozen
        Assert.Equal(2, scope.Statements);
    }

    // Sets every job's row to the Dead-Lettered shape in one statement: terminal state, terminal instant,
    // and - the part that matters here - a non-null terminal_cause CLOB.
    private static async Task MarkEveryJobDeadLetteredAsync()
    {
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        await connection.OpenAsync();
        await using var update = connection.CreateCommand();
        update.CommandText =
            """
            UPDATE backwave.jobs
            SET state = 5, terminal_at = SYSTIMESTAMP, terminal_cause = 'budget fixture: dead-lettered'
            """;
        await update.ExecuteNonQueryAsync();
    }

    private sealed record Budget(string Operation, int Statements, int LobReads);

    private static void AssertBudget(Budget budget, int statements, int lobReads)
    {
        if (statements == budget.Statements && lobReads == budget.LobReads)
        {
            return;
        }
        Assert.Fail(
            $"""
            Oracle round-trip budget moved: {budget.Operation}
              statements: budgeted {budget.Statements}, measured {statements} ({Delta(budget.Statements, statements)})
              LOB reads:  budgeted {budget.LobReads}, measured {lobReads} ({Delta(budget.LobReads, lobReads)})

            A budget moves in either direction only deliberately. If a change was meant to move this,
            record the new numbers in OracleRoundTripBudgetTests in the same commit. If it was not, the
            operation just gained or lost database round trips that no other test in the suite can see.
            """);
    }

    private static string Delta(int budgeted, int measured) => measured switch
    {
        _ when measured > budgeted => $"+{measured - budgeted}, a regression",
        _ when measured < budgeted => $"{measured - budgeted}, an improvement to record",
        _ => "unchanged",
    };
}
