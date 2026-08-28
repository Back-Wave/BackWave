using BackWave.Core;
using BackWave.Storage;
using Microsoft.Data.Sqlite;

namespace BackWave.Sqlite.Tests;

/// <summary>
/// The SQLite adapter's statement budget: the number of statements seven hot operations execute today,
/// pinned so a change has to move them on purpose.
///
/// Read the number for what it is. SQLite runs in-process, so a statement here is a prepare plus a step
/// against a local file, not a network round trip, and none of these numbers is a latency figure. What
/// makes them worth pinning is that SQLite has exactly one writer: a per-row loop over N rows holds the
/// connection's write lock for N prepares and N steps where a set-based statement holds it for one, and
/// every other pump, enqueue, and dashboard write in the process waits behind it for the whole time.
/// Serializing on the write lock is how a SQLite deployment falls over.
///
/// A statement count is the only instrument in the suite that can see that. Against a local file a
/// batched write and a per-row loop finish in the same blink, so every functional test, every timing
/// test, and the whole conformance suite pass identically either way. These counts do not, and unlike a
/// stopwatch they are deterministic enough to gate CI.
///
/// The budget fails in BOTH directions on purpose. A number that falls is the win an optimization was
/// after, and it should be recorded here in the same commit that earns it; a number that rises is a
/// regression that would otherwise reach production as nothing but a slightly slower job rate.
///
/// What the count covers: statements executed per operation. What it does not: opening a pooled
/// connection, BEGIN IMMEDIATE, COMMIT, and the one-time schema-version and engine-version checks.
/// Those are real work too, but they are a small constant per operation rather than a cost per row -
/// and per row is the shape of the problem these budgets exist to watch.
///
/// Several of these numbers are far higher than the equivalent Postgres budget, and that is honest
/// rather than shameful: SQLite has no <c>unnest</c> and no array binding, so a few paths are genuinely
/// per-row and are pinned here as they are. A budget records what the adapter costs; it does not assert
/// what it ought to cost.
/// </summary>
public sealed class SqliteStatementBudgetTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    // MaxClaimBatch: the default a worker group claims with, and the batch size the per-row costs multiply.
    private const int ClaimBatch = 32;

    private static NewJob Job(string queue = "budget") =>
        new(Guid.NewGuid(), "budget-test", "{}"u8.ToArray(), queue, T0);

    // The recorded budgets. Each is the cost of ONE call; the arithmetic behind each number is in its
    // test, and a number nobody can account for is a number nobody can safely change.

    private static readonly Budget Claim = new(
        "ClaimBatchAsync of 32 jobs (one queue, cold store)", Statements: 7);

    private static readonly Budget ReportOutcomes = new(
        "ReportOutcomesAsync of 32 succeeded rows", Statements: 66);

    private static readonly Budget ReportOutcomesWithOutput = new(
        "ReportOutcomesAsync of 32 succeeded rows, every one carrying job output", Statements: 66);

    private static readonly Budget ExpireLeases = new(
        "ExpireLeasesAsync over 32 expired leases, all rescheduled", Statements: 35);

    private static readonly Budget ListJobs = new(
        "ListJobsAsync over a full page of terminal jobs", Statements: 2);

    private static readonly Budget Enqueue = new(
        "EnqueueAsync of one untagged job", Statements: 2);

    private static readonly Budget EnqueueTagged = new(
        "EnqueueAsync of one job carrying three tags", Statements: 5);

    [Fact]
    public async Task Claim_of_a_full_batch_stays_within_its_statement_budget()
    {
        // A cold store on purpose: it is the shape a fleet actually boots into, and it is deterministic
        // where a warm one would depend on what a previous test left behind in the same file.
        await using var temp = TempSqliteStore.Create();
        var store = temp.Store;
        for (var i = 0; i < ClaimBatch; i++)
        {
            await store.EnqueueAsync(Job(), T0); // also warms the one-time schema check, off the measured path
        }

        // 1 committed-candidate peek + 1 queue_limits read + 1 claim UPDATE ... RETURNING
        // + 2 batched transition write + 1 tag hydration + 1 next-due read = 7, independent of batch
        // size.
        //
        // The peek is the lock-free WAL read that lets a co-resident writer keep its own transaction
        // open without blocking this claim; it costs a statement and saves the write lock. The
        // queue_limits row is absent on a fresh queue, so the leased-count probe behind it never runs -
        // a queue with a configured concurrency limit pays one statement more than this.
        //
        // The batched transition write is TWO statements, not one: SQLite has no sequence, so the
        // recorder reads the pre-batch MAX(position) and then does the set-based INSERT that assigns
        // each row that base plus its ROW_NUMBER. It writes 32 Leased entries in that one INSERT. No
        // prune: the batch recorder issues its DELETE only when some job in the batch reached
        // MaxTransitionsPerJob, and a freshly claimed job is on its second transition.
        //
        // The tag hydration is one statement for all 32 job ids (a variadic IN list, SQLite's stand-in
        // for Postgres's = ANY), and it runs whether or not any job carries a tag.
        //
        // The next-due read is the one statement ClaimBatchAsync costs over ClaimAsync (measured: 7
        // against 6). It is the idle-poll backoff hint, taken on the same connection right after the
        // claim commits so it reads the post-claim snapshot, and it runs unconditionally rather than
        // only when a queue came up empty.
        ClaimResult result;
        int measured;
        using (var scope = SqliteStatementCounts.Observe())
        {
            result = await store.ClaimBatchAsync(
                new ClaimRequest("budget-worker", ["budget"], ClaimBatch, Lease, T0));
            measured = scope.Statements;
        }

        Assert.Equal(ClaimBatch, result.Jobs.Count);
        AssertBudget(Claim, measured);
    }

    [Fact]
    public async Task ReportOutcomes_of_a_full_batch_stays_within_its_statement_budget()
    {
        await using var temp = TempSqliteStore.Create();
        var store = temp.Store;
        for (var i = 0; i < ClaimBatch; i++)
        {
            await store.EnqueueAsync(Job(), T0);
        }
        var claimed = await store.ClaimAsync(new ClaimRequest("budget-worker", ["budget"], ClaimBatch, Lease, T0));
        Assert.Equal(ClaimBatch, claimed.Count);

        // 32 fenced UPDATE ... RETURNING + 32 latch-cascade edge DELETEs + 2 batched transition write
        // = 66. This is the highest number in the file and the one that most needs its arithmetic said
        // out loud, because two thirds of it is per row.
        //
        // The 32 UPDATEs are deliberate, not an oversight. The per-(worker, attempt) fence has to
        // report a verdict PER ROW - Applied or StaleLease - and it does that by returning the new
        // state from each row's own UPDATE. A set-based write could not report which rows the fence
        // rejected. This is the one place where SQLite's lack of array binding costs the most, and the
        // batch still buys what it was written to buy: all 32 rows apply in one transaction, so the
        // write lock is taken once rather than 32 times, and the whole batch is all-or-nothing.
        //
        // The 32 DELETEs are the child-latch cascade. Every succeeded row is TERMINAL, so every one
        // enters the cascade and issues its edge-claiming DELETE ... RETURNING child_id; none of these
        // jobs has a child, so each returns nothing and the cascade stops there. A batch of retries
        // rather than successes skips this entirely and costs 34 - non-terminal rows gate nothing.
        //
        // The transition write is 2, as in the claim: MAX(position) read plus one set-based INSERT
        // covering all 32 rows, and no prune, since no job here is near the cap.
        var batch = claimed
            .Select(job => new OutcomeReport(job.JobId, "budget-worker", job.Attempt, new JobOutcome.Success()))
            .ToArray();

        IReadOnlyList<OutcomeReportResult> results;
        int measured;
        using (var scope = SqliteStatementCounts.Observe())
        {
            results = await store.ReportOutcomesAsync(batch, T0);
            measured = scope.Statements;
        }

        Assert.All(results, result => Assert.Equal(OutcomeResult.Applied, result.Result));
        AssertBudget(ReportOutcomes, measured);
    }

    [Fact]
    public async Task ReportOutcomes_carrying_job_output_costs_no_extra_statement()
    {
        await using var temp = TempSqliteStore.Create();
        var store = temp.Store;
        for (var i = 0; i < ClaimBatch; i++)
        {
            await store.EnqueueAsync(Job(), T0);
        }
        var claimed = await store.ClaimAsync(new ClaimRequest("budget-worker", ["budget"], ClaimBatch, Lease, T0));
        Assert.Equal(ClaimBatch, claimed.Count);

        // Exactly the drain budget above, not one statement more. Output on SQLite is an extra SET
        // clause and an extra bound parameter on the fenced UPDATE that already runs for that row -
        // there is no separate write for it and no LOB locator to follow, which is the whole reason
        // this path needed a dedicated statement on Oracle and needs none here.
        //
        // This budget is the guard on that. A refactor that gives output its own UPDATE per row reads
        // as the tidy way to write it and passes every functional test in the suite; here it fails,
        // 98 against 66.
        var payload = new byte[4_096];
        Random.Shared.NextBytes(payload);
        var batch = claimed
            .Select(job => new OutcomeReport(job.JobId, "budget-worker", job.Attempt, new JobOutcome.Success())
            {
                Output = payload,
            })
            .ToArray();

        IReadOnlyList<OutcomeReportResult> results;
        int measured;
        using (var scope = SqliteStatementCounts.Observe())
        {
            results = await store.ReportOutcomesAsync(batch, T0);
            measured = scope.Statements;
        }

        Assert.All(results, result => Assert.Equal(OutcomeResult.Applied, result.Result));
        AssertBudget(ReportOutcomesWithOutput, measured);

        // The budget is only worth pinning if every row actually landed with its blob.
        foreach (var job in claimed)
        {
            var stored = await store.GetJobOutputAsync(job.JobId);
            Assert.Equal(payload, stored?.ToArray());
        }
    }

    [Fact]
    public async Task Expiring_a_full_sweep_of_leases_stays_within_its_statement_budget()
    {
        await using var temp = TempSqliteStore.Create();
        var store = temp.Store;
        for (var i = 0; i < ClaimBatch; i++)
        {
            await store.EnqueueAsync(Job(), T0);
        }
        var claimed = await store.ClaimAsync(new ClaimRequest("budget-worker", ["budget"], ClaimBatch, Lease, T0));
        Assert.Equal(ClaimBatch, claimed.Count);

        // 1 expired-lease select + 32 per-row reschedule UPDATEs + 2 batched transition write = 35.
        //
        // This is the budget that guards the batched lease sweep. Before the sweep's transition log was
        // batched it recorded one transition per reclaimed job, each of them its own INSERT ...
        // RETURNING, and this same call cost 65. The two-statement batch recorder replaced 32 of those
        // with one set-based INSERT plus its MAX(position) read.
        //
        // The sweep is the widest per-row path in the adapter - maxJobs is 500 by default where a claim
        // batch is 32 - so a per-row transition here holds the single write lock for up to 500 prepare
        // and step cycles while every enqueue and every other pump in the process queues behind it.
        // That is exactly the failure mode this number exists to keep an eye on.
        //
        // The 32 reschedule UPDATEs remain per row and are pinned as such: each expired job's next due
        // time comes from the RetryDisposition evaluated in C# against that job's own attempt count, so
        // there is no single value a set-based UPDATE could write. Every job here retries rather than
        // dead-letters, which is the shape a sweep is usually full of and the cheaper of the two: a
        // dead-lettered job also enters the child-latch cascade and adds an edge DELETE of its own.
        var afterExpiry = T0 + Lease + TimeSpan.FromMinutes(1);
        var disposition = new RetryDisposition(MaxAttempts: 5, [TimeSpan.FromMinutes(1)]);

        int reclaimed;
        int measured;
        using (var scope = SqliteStatementCounts.Observe())
        {
            reclaimed = await store.ExpireLeasesAsync(afterExpiry, maxJobs: 500, ["budget"], disposition);
            measured = scope.Statements;
        }

        Assert.Equal(ClaimBatch, reclaimed);
        AssertBudget(ExpireLeases, measured);
    }

    [Fact]
    public async Task ListJobs_over_a_full_page_of_terminal_jobs_stays_within_its_statement_budget()
    {
        await using var temp = TempSqliteStore.Create();
        var store = temp.Store;
        var page = store.Bounds.MaxMonitorPageSize;
        for (var i = 0; i < page; i++)
        {
            await store.EnqueueAsync(Job(), T0);
        }

        // Terminal is the shape a dashboard's default view is full of, and it is the shape that carries
        // a terminal_cause on top of the payload. Driving a whole page to terminal through the store
        // would be seven claim/report cycles of setup noise, so the row SHAPE is set directly here - the
        // assertion below proves the shape actually took.
        await MarkEveryJobDeadLetteredAsync(temp.Path);

        // 1 page select + 1 batched tag hydration = 2 statements, independent of page size. The
        // hydration covers every job id on the page in one variadic IN list, and it is the statement
        // most at risk here: a refactor that hydrates tags per row would turn a 200-job page into 201
        // statements and no other test in the suite would notice.
        IReadOnlyList<JobRecord> jobs;
        int measured;
        using (var scope = SqliteStatementCounts.Observe())
        {
            jobs = await store.ListJobsAsync(new JobQuery { MaxResults = page });
            measured = scope.Statements;
        }

        Assert.Equal(page, jobs.Count);
        Assert.All(jobs, job => Assert.False(string.IsNullOrEmpty(job.TerminalCause)));
        AssertBudget(ListJobs, measured);
    }

    [Fact]
    public async Task Enqueue_of_one_job_stays_within_its_statement_budget()
    {
        await using var temp = TempSqliteStore.Create();
        var store = temp.Store;
        await store.EnqueueAsync(Job(), T0); // warms the one-time schema check, off the measured path

        // 1 job INSERT + 1 transition INSERT ... RETURNING = 2 statements.
        //
        // This is the budget that guards the skipped prune. The single-row transition recorder returns
        // the ordinal it just assigned and only issues its bounded DELETE when that ordinal reached
        // MaxTransitionsPerJob. An enqueue writes ordinal 0, so the DELETE is skipped - and under the
        // cap it could only ever have been a no-op, since its bound is MAX(ordinal) minus the cap,
        // which is negative while the newest ordinal is below the cap. Take the skip away and every
        // enqueue in the system pays a third statement, under the write lock, to delete nothing. That
        // is a 50% increase on the hottest per-call path there is.
        //
        // Enqueue is a per-CALL path rather than a per-row one, so there is no batch here for a
        // set-based statement to collapse. That is exactly why the batching work behind the other
        // budgets in this file left this number alone.
        int measured;
        using (var scope = SqliteStatementCounts.Observe())
        {
            await store.EnqueueAsync(Job(), T0);
            measured = scope.Statements;
        }

        AssertBudget(Enqueue, measured);
    }

    [Fact]
    public async Task Enqueue_of_a_tagged_job_costs_one_statement_for_every_tag()
    {
        await using var temp = TempSqliteStore.Create();
        var store = temp.Store;
        await store.EnqueueAsync(Job(), T0);

        // The untagged budget plus one statement per tag: 1 job INSERT + 3 tag INSERTs + 1 transition
        // INSERT = 5. The tag write is a genuine per-row loop, and it is pinned here as it is rather
        // than fixed - this is a budget, not an ambition. Three tags on one enqueue is a small,
        // bounded loop; what the number is here for is to make it visible if the loop ever grows a
        // statement per tag somewhere it is not bounded.
        var tagged = Job() with
        {
            Tags = JobTags.Empty.WithTag("tenant", "acme").WithTag("region", "eu").WithTag("tier", "gold"),
        };

        int measured;
        using (var scope = SqliteStatementCounts.Observe())
        {
            await store.EnqueueAsync(tagged, T0);
            measured = scope.Statements;
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
            SqliteStatementCounts.CountStatement(); // let the JIT tier up before the measurement
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1_000_000; i++)
        {
            SqliteStatementCounts.CountStatement();
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void A_scope_counts_only_what_happens_inside_it()
    {
        // The instrument's own correctness check: without this, a budget of zero would pass forever.
        SqliteStatementCounts.CountStatement(); // outside any scope - must not be seen by the scope below

        using var scope = SqliteStatementCounts.Observe();
        SqliteStatementCounts.CountStatement();
        SqliteStatementCounts.CountStatement();

        Assert.Equal(2, scope.Statements);

        scope.Dispose();
        SqliteStatementCounts.CountStatement(); // after the scope closed - the count is frozen
        Assert.Equal(2, scope.Statements);
    }

    // Sets every job's row to the Dead-Lettered shape in one statement: terminal state, terminal
    // instant, and - the part that matters here - a non-null terminal_cause.
    private static async Task MarkEveryJobDeadLetteredAsync(string path)
    {
        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        await using var update = connection.CreateCommand();
        update.CommandText =
            """
            UPDATE backwave_jobs
            SET state = $state, terminal_at = $now, terminal_cause = 'budget fixture: dead-lettered'
            """;
        update.Parameters.AddWithValue("$state", (int)JobState.DeadLettered);
        update.Parameters.AddWithValue("$now", T0.UtcTicks);
        await update.ExecuteNonQueryAsync();
    }

    private sealed record Budget(string Operation, int Statements);

    private static void AssertBudget(Budget budget, int measured)
    {
        if (measured == budget.Statements)
        {
            return;
        }
        Assert.Fail(
            $"""
            SQLite statement budget moved: {budget.Operation}
              statements: budgeted {budget.Statements}, measured {measured} ({Delta(budget.Statements, measured)})

            A budget moves in either direction only deliberately. If a change was meant to move this,
            record the new number in SqliteStatementBudgetTests in the same commit, with the arithmetic
            behind it. If it was not, the operation just gained or lost statements that no other test in
            the suite can see - and on SQLite every extra write statement is another turn holding the
            single write lock that the rest of the process is queued behind.
            """);
    }

    private static string Delta(long budgeted, long measured) => measured switch
    {
        _ when measured > budgeted => $"+{measured - budgeted}, a regression",
        _ when measured < budgeted => $"{measured - budgeted}, an improvement to record",
        _ => "unchanged",
    };
}
