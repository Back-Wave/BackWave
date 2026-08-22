using BackWave.Core;
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
/// What the count covers: statements executed, LOB values read, and the widest fetch window declared,
/// per operation. What it does not: opening a pooled connection, COMMIT, and the one-time
/// schema-version check. Those are round trips too, but they are a small constant per operation rather
/// than a cost per row - and per row is the shape of the problem these budgets exist to watch.
///
/// The fetch window is here because it is the other half of the LOB prefetch, and the half a change can
/// silently drop. FetchSize is a byte budget: prefetching LOBs makes a row orders of magnitude larger,
/// and a window left at the driver default collapses to one row per round trip, which trades the LOB
/// trips away for an equal number of fetch trips that the LOB count cannot see. Pinning the window
/// makes that trade visible. It is also the per-command memory ceiling, since it is the bytes the
/// driver may hold in flight for one read.
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
        "ClaimBatchAsync of 32 jobs (one queue, cold caches)",
        Statements: 9, LobReads: 0, FetchWindowBytes: JobPageWindow);

    private static readonly Budget ReportOutcomes = new(
        "ReportOutcomesAsync of 32 succeeded rows",
        Statements: 5, LobReads: 0, FetchWindowBytes: 0);

    private static readonly Budget ReportOutcomesWithOutput = new(
        "ReportOutcomesAsync of 32 succeeded rows, every one carrying job output",
        Statements: 6, LobReads: 0, FetchWindowBytes: 0);

    private static readonly Budget ListJobs = new(
        "ListJobsAsync over a 200-job page of terminal jobs",
        Statements: 2, LobReads: 0, FetchWindowBytes: JobPageWindow);

    // The window a statement selecting the full jobs column set declares: 32 rows (one claim batch) of
    // 140,447 bytes, which is the driver's own size for that row - both LOB columns at the 65,536
    // payload prefetch, plus about 9 KB of scalars. Claim and job list select the same columns, so they
    // share it. The page size does NOT enter it: a 200-row page arrives in seven windows of this size
    // rather than one window seven times as wide, which is what keeps the monitor listing off the
    // memory ceiling.
    private const long JobPageWindow = 4_494_304;

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
        // Zero LOB reads: the claim select prefetches every payload BLOB into its row, and
        // terminal_cause is null on a Scheduled job, which costs nothing either way. Before the payload
        // was prefetched this was 32 - one round trip per claimed row, on the hottest path there is.
        ClaimResult result;
        Measured measured;
        using (var scope = OracleRoundTrips.Observe())
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
        var store = await OracleTestDatabase.CreateFreshStoreAsync();
        for (var i = 0; i < ClaimBatch; i++)
        {
            await store.EnqueueAsync(Job(), T0);
        }
        var claimed = await store.ClaimAsync(new ClaimRequest("budget-worker", ["budget"], ClaimBatch, Lease, T0));
        Assert.Equal(ClaimBatch, claimed.Count);

        // The plain drain: every row succeeded, none carries output or a tag delta, so nothing but the
        // fenced state write and the transition log runs.
        // 1 fenced locking read + 1 batched MERGE + 1 batched transition insert + 1 highest-ordinal read
        // + 1 child-latch probe = 5, independent of batch size. The locking read is the round trip this
        // path ADDS to recover the per-row Effect-Once verdict a multi-row write cannot report: Oracle
        // has no OUTPUT and no set-valued RETURNING, so the fence is read under FOR UPDATE and applied
        // second. It replaces 32 per-row updates, so the batch is 30 statements cheaper than it was.
        // As above, no job in this batch is near the cap, so the batch recorder issues no prune DELETE.
        // Writing a terminal_cause CLOB is a parameter bind, not a materialization, so no LOB is read,
        // and the fenced read pulls no LOB column at all, so it declares no fetch window either.
        var batch = claimed
            .Select(job => new OutcomeReport(job.JobId, "budget-worker", job.Attempt, new JobOutcome.Success()))
            .ToArray();

        IReadOnlyList<OutcomeReportResult> results;
        Measured measured;
        using (var scope = OracleRoundTrips.Observe())
        {
            results = await store.ReportOutcomesAsync(batch, T0);
            measured = Measured.From(scope);
        }

        Assert.All(results, result => Assert.Equal(OutcomeResult.Applied, result.Result));
        AssertBudget(ReportOutcomes, measured);
    }

    [Fact]
    public async Task ReportOutcomes_carrying_job_output_costs_one_statement_for_every_blob_together()
    {
        var store = await OracleTestDatabase.CreateFreshStoreAsync();
        for (var i = 0; i < ClaimBatch; i++)
        {
            await store.EnqueueAsync(Job(), T0);
        }
        var claimed = await store.ClaimAsync(new ClaimRequest("budget-worker", ["budget"], ClaimBatch, Lease, T0));
        Assert.Equal(ClaimBatch, claimed.Count);

        // The drain budget above plus ONE statement, not plus 32. Output is the one write on this path
        // that no set-based statement can carry: a blob has no JSON representation, and HEXTORAW caps at
        // 32,767 bytes - half of MaxOutputBytes - so the ids and the blobs go over as two parallel arrays
        // under ArrayBindCount instead. The driver sends them in one round trip and the server runs the
        // UPDATE once per element.
        //
        // This budget is the guard on that. A refactor back to one UPDATE per row reads as the obvious
        // way to write it and passes every functional test in the suite; here it fails, 37 against 6.
        // Zero LOB reads still: binding a blob out is a parameter cost, never a materialization.
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
        using (var scope = OracleRoundTrips.Observe())
        {
            results = await store.ReportOutcomesAsync(batch, T0);
            measured = Measured.From(scope);
        }

        Assert.All(results, result => Assert.Equal(OutcomeResult.Applied, result.Result));
        AssertBudget(ReportOutcomesWithOutput, measured);

        // The batching is only worth pinning if every row actually landed. Array binding runs the
        // statement once per element server-side, so a bad bind loses rows silently rather than throwing.
        foreach (var job in claimed)
        {
            var stored = await store.GetJobOutputAsync(job.JobId);
            Assert.Equal(payload, stored?.ToArray());
        }
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
        // Zero LOB reads: both the payload BLOB and the terminal_cause CLOB ride in the row. This was
        // 400 - the whole cost of this operation was per row, and none of it was in the statement count.
        // The window is the claim's, not the page's: 200 rows arrive in seven trips of 32.
        IReadOnlyList<JobRecord> jobs;
        Measured measured;
        using (var scope = OracleRoundTrips.Observe())
        {
            jobs = await store.ListJobsAsync(new JobQuery { MaxResults = page });
            measured = Measured.From(scope);
        }

        Assert.Equal(page, jobs.Count);
        Assert.All(jobs, job => Assert.False(string.IsNullOrEmpty(job.TerminalCause)));
        AssertBudget(ListJobs, measured);
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

    private sealed record Budget(string Operation, int Statements, int LobReads, long FetchWindowBytes);

    private sealed record Measured(int Statements, int LobReads, long FetchWindowBytes)
    {
        public static Measured From(OracleRoundTripScope scope)
            => new(scope.Statements, scope.LobReads, scope.FetchWindowBytes);
    }

    private static void AssertBudget(Budget budget, Measured measured)
    {
        if (measured.Statements == budget.Statements
            && measured.LobReads == budget.LobReads
            && measured.FetchWindowBytes == budget.FetchWindowBytes)
        {
            return;
        }
        Assert.Fail(
            $"""
            Oracle round-trip budget moved: {budget.Operation}
              statements:   budgeted {budget.Statements}, measured {measured.Statements} ({Delta(budget.Statements, measured.Statements)})
              LOB reads:    budgeted {budget.LobReads}, measured {measured.LobReads} ({Delta(budget.LobReads, measured.LobReads)})
              fetch window: budgeted {budget.FetchWindowBytes} bytes, measured {measured.FetchWindowBytes} ({Delta(budget.FetchWindowBytes, measured.FetchWindowBytes)})

            A budget moves in either direction only deliberately. If a change was meant to move this,
            record the new numbers in OracleRoundTripBudgetTests in the same commit. If it was not, the
            operation just gained or lost database round trips that no other test in the suite can see.
            A fetch window that fell to the driver default means a LOB prefetch lost its other half.
            """);
    }

    private static string Delta(long budgeted, long measured) => measured switch
    {
        _ when measured > budgeted => $"+{measured - budgeted}, a regression",
        _ when measured < budgeted => $"{measured - budgeted}, an improvement to record",
        _ => "unchanged",
    };
}
