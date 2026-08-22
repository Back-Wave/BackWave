using BackWave.Storage;
using Oracle.ManagedDataAccess.Client;

namespace BackWave.Oracle.Tests;

/// <summary>
/// The batched outcome write, which is one fenced read plus one MERGE rather than a fenced UPDATE per
/// row. Oracle returns no per-row verdict for a multi-row write, so the Effect-Once fence is split: a
/// locking read decides which rows may apply, and the MERGE re-states the same fence so the database
/// still authorizes every write. Splitting a guard in two is where a fence loses a clause, and a lost
/// clause is silent - the batch still succeeds, it just applies a row it had no right to touch.
///
/// These tests pin each half of the fence separately, and each asserts BOTH halves of the contract: the
/// caller is told StaleLease, AND the job row is unchanged. A shape that reports honestly but writes
/// anyway, or writes correctly but lies to the caller, fails here.
/// </summary>
[Collection("oracle")]
public sealed class OracleBatchOutcomeTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    // The batch size a worker group reports with, and the size the old per-row loop multiplied.
    private const int Batch = 32;

    private static NewJob Job(Guid id, string queue) => new(id, "outcome-test", "{}"u8.ToArray(), queue, T0);

    [Fact]
    public async Task An_expired_lease_among_live_rows_reports_stale_and_changes_nothing()
    {
        // The batch-wide half of the fence: lease_expiry > now. One row's lease has run out while the
        // other 32 are live, and all 33 report in the same call at the same instant. The expired row
        // must not apply - its worker has lost the job, and another node may already be running it.
        //
        // The old loop got this from each UPDATE's own WHERE and read the rowcount back. The read here
        // has to filter it out instead, and a batched write cannot notice afterwards: 33 rows in, one
        // summed rowcount out.
        var store = await OracleTestDatabase.CreateFreshStoreAsync();

        // The stale job leases for one minute, in its own queue so the later claim cannot take it.
        var stale = Guid.NewGuid();
        await store.EnqueueAsync(Job(stale, "stale"), T0);
        var staleClaim = Assert.Single(
            await store.ClaimAsync(new ClaimRequest("w1", ["stale"], 1, TimeSpan.FromMinutes(1), T0)));

        // Two minutes on, that lease is gone. The live batch claims now and holds a five-minute lease.
        var now = T0.AddMinutes(2);
        var liveIds = new List<Guid>();
        for (var i = 0; i < Batch; i++)
        {
            var id = Guid.NewGuid();
            liveIds.Add(id);
            await store.EnqueueAsync(Job(id, "live"), now);
        }
        var live = await store.ClaimAsync(new ClaimRequest("w1", ["live"], Batch, Lease, now));
        Assert.Equal(Batch, live.Count);

        var reports = new List<OutcomeReport>
        {
            new(stale, "w1", staleClaim.Attempt, new JobOutcome.Success()),
        };
        reports.AddRange(live.Select(job => new OutcomeReport(job.JobId, "w1", job.Attempt, new JobOutcome.Success())));

        var results = await store.ReportOutcomesAsync(reports, now);

        // 32 apply, the stale one does not, and the results stay in input order.
        Assert.Equal(OutcomeResult.StaleLease, results[0].Result);
        Assert.Equal(stale, results[0].JobId);
        Assert.All(results.Skip(1), result => Assert.Equal(OutcomeResult.Applied, result.Result));

        // The stale job is untouched: still Leased, still owned by the lease it no longer holds, no
        // terminal instant, and no transition appended.
        var record = await store.GetJobAsync(stale);
        Assert.NotNull(record);
        Assert.Equal(JobState.Leased, record.State);
        Assert.Null(record.TerminalAt);
        Assert.Equal([0L, 1L], (await store.GetJobHistoryAsync(stale)).Select(t => t.Ordinal).ToList());

        foreach (var id in liveIds)
        {
            var applied = await store.GetJobAsync(id);
            Assert.Equal(JobState.Succeeded, applied!.State);
            Assert.Equal(now, applied.TerminalAt);
            Assert.Null(applied.LeaseOwner);
            Assert.Null(applied.LeaseExpiry);
        }
    }

    [Fact]
    public async Task A_wrong_attempt_among_live_rows_reports_stale_and_changes_nothing()
    {
        // The per-row half of the fence: attempt. A worker that timed out, had its job reclaimed, and
        // then came back reports against the attempt it ran - which is no longer the job's attempt. Its
        // outcome belongs to a run nobody is waiting on and must not overwrite the run in flight.
        //
        // This half cannot live in the locking read's WHERE, because attempt varies per row and Oracle
        // rejects FOR UPDATE on any query naming JSON_TABLE. It is compared in code against the locked
        // value, and repeated in the MERGE. Both have to hold.
        var store = await OracleTestDatabase.CreateFreshStoreAsync();
        var (stale, live) = await ClaimOneAndABatchAsync(store);

        var reports = new List<OutcomeReport>
        {
            // Same worker, same live lease - only the attempt is off by one.
            new(stale.JobId, "w1", stale.Attempt + 1, new JobOutcome.Success()),
        };
        reports.AddRange(live.Select(job => new OutcomeReport(job.JobId, "w1", job.Attempt, new JobOutcome.Success())));

        var results = await store.ReportOutcomesAsync(reports, T0);

        Assert.Equal(OutcomeResult.StaleLease, results[0].Result);
        Assert.All(results.Skip(1), result => Assert.Equal(OutcomeResult.Applied, result.Result));
        await AssertStillLeasedAsync(store, stale.JobId, "w1");
    }

    [Fact]
    public async Task A_wrong_worker_among_live_rows_reports_stale_and_changes_nothing()
    {
        // The other per-row half: lease owner. Two nodes cannot both hold a job, so a report naming a
        // worker that is not the current holder is reporting on somebody else's run.
        var store = await OracleTestDatabase.CreateFreshStoreAsync();
        var (stale, live) = await ClaimOneAndABatchAsync(store);

        var reports = new List<OutcomeReport>
        {
            new(stale.JobId, "w2", stale.Attempt, new JobOutcome.Success()),
        };
        reports.AddRange(live.Select(job => new OutcomeReport(job.JobId, "w1", job.Attempt, new JobOutcome.Success())));

        var results = await store.ReportOutcomesAsync(reports, T0);

        Assert.Equal(OutcomeResult.StaleLease, results[0].Result);
        Assert.All(results.Skip(1), result => Assert.Equal(OutcomeResult.Applied, result.Result));
        await AssertStillLeasedAsync(store, stale.JobId, "w1");
    }

    [Fact]
    public async Task Every_outcome_kind_in_one_batch_lands_its_own_columns()
    {
        // One MERGE now writes every outcome kind, so the per-state columns are decided by expressions
        // over the payload rather than by which SQL text the loop chose. due_time must move only for a
        // retry and cancel_requested must clear only for a cancel; a CASE or COALESCE that spans the
        // wrong rows is invisible until the wrong job runs early or a cancel flag survives a success.
        var store = await OracleTestDatabase.CreateFreshStoreAsync();
        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            var id = Guid.NewGuid();
            ids.Add(id);
            await store.EnqueueAsync(Job(id, "kinds"), T0);
        }
        var claimed = await store.ClaimAsync(new ClaimRequest("w1", ["kinds"], 5, Lease, T0));
        Assert.Equal(5, claimed.Count);
        var byId = claimed.ToDictionary(job => job.JobId);

        // Every one of them carries a pending cancel request, so "cleared only on a cancelled row" is
        // a claim four of the five rows can falsify.
        foreach (var id in ids)
        {
            Assert.Equal(CancelResult.CancellationRequested, await store.CancelJobAsync(id, "op", T0));
        }

        var retryAt = T0.AddMinutes(30);
        var results = await store.ReportOutcomesAsync(
            [
                new OutcomeReport(ids[0], "w1", byId[ids[0]].Attempt, new JobOutcome.Success()),
                new OutcomeReport(ids[1], "w1", byId[ids[1]].Attempt, new JobOutcome.Failure(retryAt, "again")),
                new OutcomeReport(ids[2], "w1", byId[ids[2]].Attempt, new JobOutcome.Failure(null, "ceiling")),
                new OutcomeReport(ids[3], "w1", byId[ids[3]].Attempt, new JobOutcome.Cancelled("operator")),
                new OutcomeReport(ids[4], "w1", byId[ids[4]].Attempt, new JobOutcome.Unroutable("no handler")),
            ],
            T0);
        Assert.All(results, result => Assert.Equal(OutcomeResult.Applied, result.Result));

        var succeeded = (await store.GetJobAsync(ids[0]))!;
        Assert.Equal(JobState.Succeeded, succeeded.State);
        Assert.Equal(T0, succeeded.TerminalAt);
        Assert.Null(succeeded.TerminalCause);
        Assert.Equal(T0, succeeded.DueTime); // untouched by a non-retry row
        Assert.True(succeeded.CancelRequested);

        var retrying = (await store.GetJobAsync(ids[1]))!;
        Assert.Equal(JobState.Scheduled, retrying.State);
        Assert.Equal(retryAt, retrying.DueTime);
        Assert.Null(retrying.TerminalAt);
        Assert.Null(retrying.TerminalCause);
        Assert.True(retrying.CancelRequested);

        var deadLettered = (await store.GetJobAsync(ids[2]))!;
        Assert.Equal(JobState.DeadLettered, deadLettered.State);
        Assert.Equal(T0, deadLettered.TerminalAt);
        Assert.Equal("ceiling", deadLettered.TerminalCause);
        Assert.Equal(T0, deadLettered.DueTime);
        Assert.True(deadLettered.CancelRequested);

        var cancelled = (await store.GetJobAsync(ids[3]))!;
        Assert.Equal(JobState.Cancelled, cancelled.State);
        Assert.Equal(T0, cancelled.TerminalAt);
        Assert.Equal("operator", cancelled.TerminalCause);
        Assert.False(cancelled.CancelRequested); // the one row that clears it

        var quarantined = (await store.GetJobAsync(ids[4]))!;
        Assert.Equal(JobState.Quarantined, quarantined.State);
        Assert.Equal(T0, quarantined.TerminalAt);
        Assert.Equal("no handler", quarantined.TerminalCause);
        Assert.True(quarantined.CancelRequested);

        // Every matched row drops its lease, whatever it resolved to.
        foreach (var id in ids)
        {
            var record = (await store.GetJobAsync(id))!;
            Assert.Null(record.LeaseOwner);
            Assert.Null(record.LeaseExpiry);
        }
    }

    [Fact]
    public async Task A_terminal_instant_keeps_all_seven_fractional_digits()
    {
        // terminal_at and due_time now cross into the database inside a JSON document, and JSON has no
        // timestamp - every instant is text on the way in. A store hands instants back exactly as it was
        // given them, down to the tick, so anything on that trip that thinks in microseconds returns a
        // terminal_at a tick away from the one the caller passed.
        var store = await OracleTestDatabase.CreateFreshStoreAsync();
        var now = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero).AddTicks(1_234_567);
        var retryAt = now.AddMinutes(9).AddTicks(7_654_321 - 1_234_567);

        var succeeded = Guid.NewGuid();
        var retrying = Guid.NewGuid();
        await store.EnqueueAsync(Job(succeeded, "ticks"), T0);
        await store.EnqueueAsync(Job(retrying, "ticks"), T0);
        var claimed = await store.ClaimAsync(new ClaimRequest("w1", ["ticks"], 2, Lease, now));
        var byId = claimed.ToDictionary(job => job.JobId);

        await store.ReportOutcomesAsync(
            [
                new OutcomeReport(succeeded, "w1", byId[succeeded].Attempt, new JobOutcome.Success()),
                new OutcomeReport(retrying, "w1", byId[retrying].Attempt, new JobOutcome.Failure(retryAt, "again")),
            ],
            now);

        Assert.Equal(now, (await store.GetJobAsync(succeeded))!.TerminalAt);
        Assert.Equal(retryAt, (await store.GetJobAsync(retrying))!.DueTime);
    }

    [Fact]
    public async Task Tags_added_by_a_batch_converge_on_a_duplicate_triple()
    {
        // The tag insert is one set-based statement now, and it has to stay idempotent without the
        // per-row catch of ORA-00001 that used to absorb a duplicate. Three duplicates at once here:
        // a triple the job already carries from enqueue, the same triple twice inside one payload, and
        // the same triple again on a second report of the same job. None may reach the caller as an
        // error, and none may double a row.
        var store = await OracleTestDatabase.CreateFreshStoreAsync();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var shared = JobTags.Empty.WithTag("tenant", "acme").WithLabel("nightly");

        await store.EnqueueAsync(Job(first, "tags") with { Tags = shared }, T0);
        await store.EnqueueAsync(Job(second, "tags"), T0);
        var claimed = await store.ClaimAsync(new ClaimRequest("w1", ["tags"], 2, Lease, T0));
        var byId = claimed.ToDictionary(job => job.JobId);

        var results = await store.ReportOutcomesAsync(
            [
                // Already on the job from enqueue, plus one that is new.
                new OutcomeReport(first, "w1", byId[first].Attempt, new JobOutcome.Success())
                    { AddedTags = shared.WithTag("region", "eu") },
                // The same triple as the row above, on a different job.
                new OutcomeReport(second, "w1", byId[second].Attempt, new JobOutcome.Success())
                    { AddedTags = shared },
            ],
            T0);
        Assert.All(results, result => Assert.Equal(OutcomeResult.Applied, result.Result));

        Assert.Equal(shared.WithTag("region", "eu"), (await store.GetJobAsync(first))!.Tags);
        Assert.Equal(shared, (await store.GetJobAsync(second))!.Tags);
        Assert.Equal(3, await TagRowCountAsync(first));
        Assert.Equal(2, await TagRowCountAsync(second));
    }

    [Fact]
    public async Task Two_transactions_adding_the_same_tag_triple_converge()
    {
        // Two nodes reporting outcomes on two different jobs can still write the SAME tag row, because
        // a tag is keyed by (job_id, key, value) and nothing stops both from touching one job over a
        // retry. The second writer must block on the first and then find its row already there rather
        // than raise a duplicate key. This is the case the old per-row catch handled and the reason the
        // batched insert carries the ignore-on-duplicate hint.
        var store = await OracleTestDatabase.CreateFreshStoreAsync();
        var jobId = Guid.NewGuid();
        await store.EnqueueAsync(Job(jobId, "tags"), T0);
        var claimed = Assert.Single(await store.ClaimAsync(new ClaimRequest("w1", ["tags"], 1, Lease, T0)));

        // A competing writer holds an uncommitted row for the very triple the report is about to add.
        await using var rival = new OracleConnection(OracleTestDatabase.ConnectionString);
        await rival.OpenAsync();
        await using var rivalTransaction = (OracleTransaction)await rival.BeginTransactionAsync();
        await using (var insert = rival.CreateCommand())
        {
            insert.Transaction = rivalTransaction;
            insert.BindByName = true;
            insert.CommandText =
                "INSERT INTO backwave.job_tags (job_id, key, value) VALUES (:id, 'tenant', 'acme')";
            insert.Parameters.Add(new OracleParameter("id", OracleDbType.Raw) { Size = 16, Value = jobId.ToByteArray() });
            await insert.ExecuteNonQueryAsync();
        }

        // The report blocks inside the tag insert until the rival releases the key.
        var report = store.ReportOutcomesAsync(
            [new OutcomeReport(jobId, "w1", claimed.Attempt, new JobOutcome.Success())
                { AddedTags = JobTags.Empty.WithTag("tenant", "acme") }],
            T0).AsTask();
        await rivalTransaction.CommitAsync();

        var results = await report;
        Assert.Equal(OutcomeResult.Applied, results[0].Result);
        Assert.Equal(JobTags.Empty.WithTag("tenant", "acme"), (await store.GetJobAsync(jobId))!.Tags);
        Assert.Equal(1, await TagRowCountAsync(jobId));
    }

    // Leases one job in its own queue plus a full live batch, all at T0 with the same worker and lease,
    // so a test can vary exactly one fence input on the single row.
    private static async Task<(JobRecord Stale, IReadOnlyList<JobRecord> Live)> ClaimOneAndABatchAsync(
        OracleJobStore store)
    {
        var odd = Guid.NewGuid();
        await store.EnqueueAsync(Job(odd, "odd"), T0);
        var stale = Assert.Single(await store.ClaimAsync(new ClaimRequest("w1", ["odd"], 1, Lease, T0)));

        for (var i = 0; i < Batch; i++)
        {
            await store.EnqueueAsync(Job(Guid.NewGuid(), "live"), T0);
        }
        var live = await store.ClaimAsync(new ClaimRequest("w1", ["live"], Batch, Lease, T0));
        Assert.Equal(Batch, live.Count);
        return (stale, live);
    }

    // The fenced-out job kept its lease and gained no transition beyond enqueue and claim.
    private static async Task AssertStillLeasedAsync(OracleJobStore store, Guid jobId, string owner)
    {
        var record = await store.GetJobAsync(jobId);
        Assert.NotNull(record);
        Assert.Equal(JobState.Leased, record.State);
        Assert.Equal(owner, record.LeaseOwner);
        Assert.NotNull(record.LeaseExpiry);
        Assert.Null(record.TerminalAt);
        Assert.Equal([0L, 1L], (await store.GetJobHistoryAsync(jobId)).Select(t => t.Ordinal).ToList());
    }

    // Rows in job_tags for one job. GetJobAsync collapses duplicates into a set, so only a direct count
    // can show a doubled row.
    private static async Task<int> TagRowCountAsync(Guid jobId)
    {
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        await connection.OpenAsync();
        await using var count = connection.CreateCommand();
        count.BindByName = true;
        count.CommandText = "SELECT COUNT(*) FROM backwave.job_tags WHERE job_id = :id";
        count.Parameters.Add(new OracleParameter("id", OracleDbType.Raw) { Size = 16, Value = jobId.ToByteArray() });
        return Convert.ToInt32(await count.ExecuteScalarAsync());
    }
}
