using BackWave.Sqlite.Internal;
using BackWave.Storage;
using Microsoft.Data.Sqlite;

namespace BackWave.Sqlite.Tests;

/// <summary>
/// The per-job-life transition cap, on both write paths. Each recorder skips its prune DELETE unless a
/// transition it just wrote reached <c>MaxTransitionsPerJob</c>, so the cap is enforced by a statement
/// that usually does not run. That makes three things worth proving. The skip never lets a job past the
/// cap on either path; the one batch DELETE that does run bounds only the job that earned it while every
/// other job in the same batch keeps its whole history; and the prune costs exactly one statement, only
/// when a job reaches the cap.
///
/// The Conformance Suite's cap clause interleaves the two recorders, so a batch re-prunes on the very
/// next transition and hides an off-by-one in either skip. These pin each path on its own.
/// </summary>
// The prune assertions measure statements, so this class holds an Observe() scope and shares the
// statement-count collection rather than running beside one that depends on no scope being open.
[Collection(SqliteStatementBudgetTests.StatementCounts)]
public sealed class SqliteTransitionPruneTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    private static NewJob Job(Guid id) => new(id, "prune-test", "{}"u8.ToArray(), "prune", T0);

    [Fact]
    public async Task The_single_row_recorder_keeps_a_job_at_the_cap_and_prunes_it_one_transition_later()
    {
        // The boundary in both directions. A job holding exactly MaxTransitionsPerJob entries is AT the
        // cap, not over it, and must keep every one; the very next single-row transition is the first that
        // exceeds the cap, and it must cost exactly the oldest entry.
        await using var temp = TempSqliteStore.Create();
        var store = temp.Store;
        var cap = store.Bounds.MaxTransitionsPerJob;
        var jobId = Guid.NewGuid();
        await store.EnqueueAsync(new NewJob(jobId, "demo", default, "prune", T0), T0);

        // Enqueue wrote ordinal 0. Fill to ordinal cap - 2, so the claim below lands on cap - 1 and takes
        // the log to exactly cap entries.
        await FillTransitionsUpToAsync(temp.Path, jobId, cap - 2);
        var claimed = Assert.Single(
            await store.ClaimAsync(new ClaimRequest("w1", ["prune"], 32, Lease, T0)));

        var atCap = await store.GetJobHistoryAsync(jobId);
        Assert.Equal(cap, atCap.Count);
        Assert.Equal(0L, atCap[0].Ordinal); // nothing was pruned: the oldest entry is still the first one
        Assert.Equal(cap - 1L, atCap[^1].Ordinal);

        // One more transition, through the single-row recorder (ReportOutcomeAsync), which is the first
        // to exceed the cap.
        Assert.Equal(
            OutcomeResult.Applied,
            await store.ReportOutcomeAsync(
                jobId, "w1", claimed.Attempt, new JobOutcome.Failure(T0.AddMinutes(10), "again"), T0));

        var pastCap = await store.GetJobHistoryAsync(jobId);
        Assert.Equal(cap, pastCap.Count);
        Assert.Equal(1L, pastCap[0].Ordinal); // exactly one entry aged out - the oldest
        Assert.Equal(cap, (int)pastCap[^1].Ordinal);
    }

    [Fact]
    public async Task A_batch_leaves_a_job_at_the_cap_alone_and_prunes_it_one_transition_later()
    {
        // The same boundary on the batched write path. The batch recorder decides from the HIGHEST ordinal
        // it just assigned across the whole batch, not from any one job's, so its skip is a different piece
        // of arithmetic from the single-row one above and can be off by one on its own.
        await using var temp = TempSqliteStore.Create();
        var store = temp.Store;
        var cap = store.Bounds.MaxTransitionsPerJob;
        var jobId = Guid.NewGuid();
        await store.EnqueueAsync(Job(jobId), T0);

        // Enqueue wrote ordinal 0. Fill to ordinal cap - 2, so the claim below lands on cap - 1 and takes
        // the log to exactly cap entries.
        await FillTransitionsUpToAsync(temp.Path, jobId, cap - 2);
        var claimed = Assert.Single(
            await store.ClaimAsync(new ClaimRequest("w1", ["prune"], 32, Lease, T0)));

        var atCap = await store.GetJobHistoryAsync(jobId);
        Assert.Equal(cap, atCap.Count);
        Assert.Equal(0L, atCap[0].Ordinal); // nothing was pruned: the oldest entry is still the first one
        Assert.Equal(cap - 1L, atCap[^1].Ordinal);

        // One more transition through the batch path (a batched retry report), which is the first to
        // exceed the cap.
        var retryAt = T0.AddMinutes(10);
        var results = await store.ReportOutcomesAsync(
            [new OutcomeReport(jobId, "w1", claimed.Attempt, new JobOutcome.Failure(retryAt, "again"))], T0);
        Assert.Equal(OutcomeResult.Applied, results[0].Result);

        var pastCap = await store.GetJobHistoryAsync(jobId);
        Assert.Equal(cap, pastCap.Count);
        Assert.Equal(1L, pastCap[0].Ordinal); // exactly one entry aged out - the oldest
        Assert.Equal(cap, (int)pastCap[^1].Ordinal);
    }

    [Fact]
    public async Task A_batch_prune_bounds_only_the_job_that_reached_the_cap()
    {
        // The mixed batch the lease sweep and the batched report both produce: one DELETE now covers every
        // job id in the batch, so jobs nowhere near the cap are inside that statement's reach. They must
        // come through untouched, batch after batch, while their neighbour stays pinned at the cap. The
        // churner sits BETWEEN the two bystanders in claim order, so a prune that reached only the head or
        // only the tail of the batch would miss the one job that needs it.
        await using var temp = TempSqliteStore.Create();
        var store = temp.Store;
        var cap = store.Bounds.MaxTransitionsPerJob;
        var first = Guid.NewGuid();
        var churner = Guid.NewGuid();
        var last = Guid.NewGuid();
        foreach (var jobId in new[] { first, churner, last }) // claim order follows enqueue order
        {
            await store.EnqueueAsync(Job(jobId), T0);
        }

        // The churner starts one transition short of the cap; the bystanders start fresh, on ordinal 0.
        await FillTransitionsUpToAsync(temp.Path, churner, cap - 2);

        // Three claim/report cycles. Every one is its own batch and carries all three jobs, so the cap has
        // to hold across separate batches, not just within one - and it has to hold after EVERY batch, not
        // merely by the end, which a prune that fires one batch late would still satisfy.
        var now = T0;
        for (var cycle = 0; cycle < 3; cycle++)
        {
            var claimed = await store.ClaimAsync(new ClaimRequest("w1", ["prune"], 32, Lease, now));
            Assert.Equal([first, churner, last], claimed.Select(job => job.JobId).ToList());
            var retryAt = now.AddMinutes(10);
            var results = await store.ReportOutcomesAsync(
                [.. claimed.Select(job => new OutcomeReport(
                    job.JobId, "w1", job.Attempt, new JobOutcome.Failure(retryAt, "again")))],
                now);
            Assert.All(results, result => Assert.Equal(OutcomeResult.Applied, result.Result));
            Assert.Equal(cap, (await store.GetJobHistoryAsync(churner)).Count);
            now = retryAt;
        }

        // The churner is bounded and its ordinals stayed contiguous at the tail: the counter kept climbing
        // while the oldest entries aged out.
        var churnerHistory = await store.GetJobHistoryAsync(churner);
        Assert.Equal(cap, churnerHistory.Count);
        Assert.True(churnerHistory.Zip(churnerHistory.Skip(1)).All(p => p.Second.Ordinal == p.First.Ordinal + 1));
        Assert.Equal(churnerHistory[0].Ordinal + cap - 1L, churnerHistory[^1].Ordinal);

        // Both bystanders rode in every one of those batches and lost nothing: ordinal 0 (the enqueue) plus
        // a Leased and a Scheduled entry per cycle, none of them aged out.
        foreach (var bystander in new[] { first, last })
        {
            var history = await store.GetJobHistoryAsync(bystander);
            Assert.Equal([0L, 1, 2, 3, 4, 5, 6], history.Select(t => t.Ordinal).ToList());
        }
    }

    [Fact]
    public async Task A_batch_pays_for_the_prune_only_when_a_job_reaches_the_cap()
    {
        // The cost half of the skip, measured against itself: two batched reports of the SAME shape - two
        // jobs, both retried, no output and no tags - where the only difference is that one job crossed the
        // cap in the second. The gap between them is the whole cost of the prune, and it has to be exactly
        // one statement. On SQLite that statement is a write, so it is also one more turn holding the
        // single write lock the rest of the process is queued behind.
        await using var temp = TempSqliteStore.Create();
        var store = temp.Store;
        var cap = store.Bounds.MaxTransitionsPerJob;
        var churner = Guid.NewGuid();
        var bystander = Guid.NewGuid();
        await store.EnqueueAsync(Job(churner), T0);
        await store.EnqueueAsync(Job(bystander), T0);

        var underCap = await ReportRetryBatchAsync(store, T0);
        var next = T0.AddMinutes(10);

        // Take the churner to the cap, so the next report is the first transition that exceeds it. The
        // bystander is left where it is, so the batch's shape does not change.
        await FillTransitionsUpToAsync(temp.Path, churner, cap - 1);
        var atCap = await ReportRetryBatchAsync(store, next);

        Assert.Equal(underCap + 1, atCap);
        Assert.Equal(cap, (await store.GetJobHistoryAsync(churner)).Count);
    }

    // Claims both jobs, batch-reports both as retries, and returns the statements the REPORT cost. The
    // claim runs outside the measured scope: it writes transitions of its own, and only the report is
    // under test here.
    private static async Task<int> ReportRetryBatchAsync(SqliteJobStore store, DateTimeOffset now)
    {
        var claimed = await store.ClaimAsync(new ClaimRequest("w1", ["prune"], 32, Lease, now));
        Assert.Equal(2, claimed.Count);
        var batch = claimed
            .Select(job => new OutcomeReport(
                job.JobId, "w1", job.Attempt, new JobOutcome.Failure(now.AddMinutes(10), "again")))
            .ToArray();

        using var scope = SqliteStatementCounts.Observe();
        var results = await store.ReportOutcomesAsync(batch, now);
        Assert.All(results, result => Assert.Equal(OutcomeResult.Applied, result.Result));
        return scope.Statements;
    }

    // Extends a job's Transition Log with filler entries so its highest ordinal is exactly
    // <paramref name="highestOrdinal"/>. Walking the log there through the store would be dozens of
    // claim/report cycles of setup noise; the fixture writes the rows directly instead (moving the
    // position high-water mark up past them, as the store's own writes do), and every assertion
    // reads the log back through the store, so a fixture that lied would fail them.
    private static async Task FillTransitionsUpToAsync(string path, Guid jobId, int highestOrdinal)
    {
        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        await using var fill = connection.CreateCommand();
        fill.CommandText =
            """
            WITH RECURSIVE ordinals(n) AS (
                SELECT COALESCE(MAX(ordinal), -1) + 1 FROM backwave_job_transitions WHERE job_id = $id
                UNION ALL
                SELECT n + 1 FROM ordinals WHERE n < $top
            )
            INSERT INTO backwave_job_transitions
                (job_id, ordinal, recorded_at, state, attempt, failure_detail, position)
            SELECT $id, n, $now, $state, 0, NULL,
                   (SELECT position FROM backwave_transition_position) + n + 1
            FROM ordinals WHERE n <= $top;
            UPDATE backwave_transition_position
            SET position = (SELECT MAX(position) FROM backwave_job_transitions)
            """;
        fill.Parameters.AddWithValue("$id", SqliteValueCodec.ToText(jobId));
        fill.Parameters.AddWithValue("$now", SqliteValueCodec.ToTicks(T0));
        fill.Parameters.AddWithValue("$state", (int)JobState.Leased);
        fill.Parameters.AddWithValue("$top", (long)highestOrdinal);
        await fill.ExecuteNonQueryAsync();
    }
}
