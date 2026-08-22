using BackWave.Storage;
using Oracle.ManagedDataAccess.Client;

namespace BackWave.Oracle.Tests;

/// <summary>
/// The per-job-life transition cap, on the batched write path. The batch recorder skips the prune DELETE
/// unless some job in the batch actually reached <c>MaxTransitionsPerJob</c>, so the cap is now enforced by
/// a statement that usually does not run. That makes two things worth proving that a round-trip count alone
/// cannot: the skip never lets a job past the cap, and the one DELETE that does run bounds only the job
/// that earned it while every other job in the same batch keeps its whole history.
/// </summary>
[Collection("oracle")]
public sealed class OracleTransitionPruneTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    private static NewJob Job(Guid id) => new(id, "prune-test", "{}"u8.ToArray(), "prune", T0);

    [Fact]
    public async Task A_batch_leaves_a_job_at_the_cap_alone_and_prunes_it_one_transition_later()
    {
        // The boundary in both directions. A job holding exactly MaxTransitionsPerJob entries is AT the
        // cap, not over it, and must keep every one; the very next transition is the first that exceeds
        // the cap, and it must cost exactly the oldest entry.
        var store = await OracleTestDatabase.CreateFreshStoreAsync();
        var cap = store.Bounds.MaxTransitionsPerJob;
        var jobId = Guid.NewGuid();
        await store.EnqueueAsync(Job(jobId), T0);

        // Enqueue wrote ordinal 0. Fill to ordinal cap - 2, so the claim below lands on cap - 1 and takes
        // the log to exactly cap entries.
        await FillTransitionsUpToAsync(jobId, cap - 2);
        var claimed = Assert.Single(await store.ClaimAsync(new ClaimRequest("w1", ["prune"], 32, Lease, T0)));

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
        // The mixed batch the hoist introduces: one DELETE now covers every job id in the batch, so jobs
        // nowhere near the cap are inside the statement's reach for the first time. They must come through
        // untouched, batch after batch, while their neighbour stays pinned at the cap. The churner sits
        // BETWEEN the two bystanders in claim order, so a prune that reached only the head or only the tail
        // of the batch would miss the one job that needs it.
        var store = await OracleTestDatabase.CreateFreshStoreAsync();
        var cap = store.Bounds.MaxTransitionsPerJob;
        var first = Guid.NewGuid();
        var churner = Guid.NewGuid();
        var last = Guid.NewGuid();
        foreach (var jobId in new[] { first, churner, last }) // claim order follows enqueue order
        {
            await store.EnqueueAsync(Job(jobId), T0);
        }

        // The churner starts one transition short of the cap; the bystanders start fresh, on ordinal 0.
        await FillTransitionsUpToAsync(churner, cap - 2);

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
    public async Task The_single_row_recorder_prunes_at_the_same_boundary_as_the_batch()
    {
        // The single-row recorder skips its DELETE on the same rule, and it is still reached directly -
        // ReportOutcomeAsync, cancellation, and minting all call it alone. Same boundary, driven one
        // transition at a time: at the cap it keeps everything, one past it drops exactly the oldest.
        var store = await OracleTestDatabase.CreateFreshStoreAsync();
        var cap = store.Bounds.MaxTransitionsPerJob;
        var jobId = Guid.NewGuid();
        await store.EnqueueAsync(Job(jobId), T0);

        // Enqueue wrote ordinal 0. Fill to cap - 2, so the claim lands on cap - 1 and the single-row report
        // below is the first transition that exceeds the cap.
        await FillTransitionsUpToAsync(jobId, cap - 2);
        var claimed = Assert.Single(await store.ClaimAsync(new ClaimRequest("w1", ["prune"], 32, Lease, T0)));
        var atCap = await store.GetJobHistoryAsync(jobId);
        Assert.Equal(cap, atCap.Count);
        Assert.Equal(0L, atCap[0].Ordinal);

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
    public async Task A_batch_pays_for_the_prune_only_when_a_job_reaches_the_cap()
    {
        // The round-trip half of the claim, measured against itself: two batched reports of the SAME
        // shape - two jobs, both retried, no output and no tags - where the only difference is that one
        // job crossed the cap in the second. The gap between them is the whole cost of the prune, and it
        // has to be exactly one statement.
        var store = await OracleTestDatabase.CreateFreshStoreAsync();
        var cap = store.Bounds.MaxTransitionsPerJob;
        var churner = Guid.NewGuid();
        var bystander = Guid.NewGuid();
        await store.EnqueueAsync(Job(churner), T0);
        await store.EnqueueAsync(Job(bystander), T0);

        var underCap = await ReportRetryBatchAsync(store, T0);
        var next = T0.AddMinutes(10);

        // Take the churner to the cap, so the next report is the first transition that exceeds it. The
        // bystander is left where it is, so the batch's shape does not change.
        await FillTransitionsUpToAsync(churner, cap - 1);
        var atCap = await ReportRetryBatchAsync(store, next);

        Assert.Equal(underCap + 1, atCap);
        Assert.Equal(cap, (await store.GetJobHistoryAsync(churner)).Count);
    }

    // Claims both jobs, batch-reports both as retries, and returns the statements the REPORT cost. The
    // claim runs outside the measured scope: it writes transitions of its own, and only the report is
    // under test here.
    private static async Task<int> ReportRetryBatchAsync(OracleJobStore store, DateTimeOffset now)
    {
        var claimed = await store.ClaimAsync(new ClaimRequest("w1", ["prune"], 32, Lease, now));
        Assert.Equal(2, claimed.Count);
        var batch = claimed
            .Select(job => new OutcomeReport(
                job.JobId, "w1", job.Attempt, new JobOutcome.Failure(now.AddMinutes(10), "again")))
            .ToArray();

        using var scope = OracleRoundTrips.Observe();
        var results = await store.ReportOutcomesAsync(batch, now);
        Assert.All(results, result => Assert.Equal(OutcomeResult.Applied, result.Result));
        return scope.Statements;
    }

    // Extends a job's Transition Log with filler entries so its highest ordinal is exactly
    // <paramref name="highestOrdinal"/>. Walking the log there through the store would be dozens of
    // claim/report cycles of setup noise; the fixture writes the rows directly instead, and every
    // assertion reads the log back through the store, so a fixture that lied would fail them.
    private static async Task FillTransitionsUpToAsync(Guid jobId, int highestOrdinal)
    {
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        await connection.OpenAsync();
        await using var fill = connection.CreateCommand();
        fill.BindByName = true;
        fill.CommandText =
            """
            DECLARE
                highest NUMBER;
            BEGIN
                SELECT COALESCE(MAX(ordinal), -1) INTO highest
                FROM backwave.job_transitions WHERE job_id = :id;
                FOR ordinal IN highest + 1 .. :top LOOP
                    INSERT INTO backwave.job_transitions
                        (job_id, ordinal, recorded_at, state, attempt, failure_detail)
                    VALUES (:id, ordinal, SYSTIMESTAMP, :state, 0, NULL);
                END LOOP;
            END;
            """;
        fill.Parameters.Add(new OracleParameter("id", OracleDbType.Raw) { Size = 16, Value = jobId.ToByteArray() });
        fill.Parameters.Add(new OracleParameter("top", OracleDbType.Int32) { Value = highestOrdinal });
        fill.Parameters.Add(new OracleParameter("state", OracleDbType.Int32) { Value = (int)JobState.Leased });
        await fill.ExecuteNonQueryAsync();
    }
}
