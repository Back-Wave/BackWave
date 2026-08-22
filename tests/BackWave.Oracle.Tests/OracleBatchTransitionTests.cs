using System.Text;
using BackWave.Storage;
using Oracle.ManagedDataAccess.Client;

namespace BackWave.Oracle.Tests;

/// <summary>
/// The batched Transition Log insert, which is one set-based statement over JSON_TABLE rather than a row
/// per entry. Two things the per-row loop got for free are now the statement's own job, and neither is
/// visible to a round-trip count or to a test that only counts rows.
///
/// The first is the ordinal. One statement computing MAX(ordinal)+1 for a whole batch reads the table as
/// it stood before the statement began, so every row of one job would land on the same number without the
/// window function that separates them. The second is the payload. A failure detail used to travel as its
/// own CLOB bind and now travels inside a JSON document, so quoting, escaping, and the size cap are
/// suddenly the adapter's problem instead of the driver's.
/// </summary>
[Collection("oracle")]
public sealed class OracleBatchTransitionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    private static NewJob Job(Guid id, string queue = "batch") =>
        new(id, "batch-test", "{}"u8.ToArray(), queue, T0);

    [Fact]
    public async Task A_job_named_twice_in_one_batch_gets_two_consecutive_ordinals()
    {
        // A batch may name the same job more than once - ReportOutcomes applies the first row, fences the
        // rest, and still logs a transition for every row it matched. Under the per-row loop each got its
        // own MAX(ordinal)+1 because the previous one had already landed. One set-based INSERT sees the
        // table as it was before the statement started, so both rows read the SAME max: without the
        // ROW_NUMBER that spaces them, they collide on the (job_id, ordinal) primary key and the whole
        // report - state write included - fails. Ordinals must stay dense and distinct, per job.
        var store = await OracleTestDatabase.CreateFreshStoreAsync();
        var jobId = Guid.NewGuid();
        await store.EnqueueAsync(Job(jobId), T0);
        var claimed = Assert.Single(await store.ClaimAsync(new ClaimRequest("w1", ["batch"], 32, Lease, T0)));

        var report = new OutcomeReport(jobId, "w1", claimed.Attempt, new JobOutcome.Success());
        var results = await store.ReportOutcomesAsync([report, report], T0);

        // Results are keyed by job id, so both rows report Applied, and the transition batch carries an
        // entry for each of them.
        Assert.All(results, result => Assert.Equal(OutcomeResult.Applied, result.Result));

        // 0 enqueue (Scheduled), 1 claim (Leased), then 2 and 3 for the two reported rows.
        var history = await store.GetJobHistoryAsync(jobId);
        Assert.Equal([0L, 1L, 2L, 3L], history.Select(t => t.Ordinal).ToList());
        Assert.Equal([JobState.Succeeded, JobState.Succeeded], history.Skip(2).Select(t => t.State).ToList());

        // Observers walk the log by global Position, so a job's Positions must climb with its ordinals
        // or the two entries reach an Observer out of order.
        var positions = await PositionsByOrdinalAsync(jobId);
        Assert.Equal(positions.OrderBy(p => p).ToList(), positions);
    }

    [Fact]
    public async Task Each_job_in_a_batch_continues_its_own_ordinal_sequence()
    {
        // Ordinals are per job, not per table. One statement covering a batch has to correlate each row's
        // next ordinal to that row's OWN job, which a single MAX over the batch would not: a job deep in
        // its history and a job on its first transition ride the same INSERT here, and the shallow one
        // must not inherit the deep one's numbering and tear a gap in its log.
        var store = await OracleTestDatabase.CreateFreshStoreAsync();
        var deep = Guid.NewGuid();
        var shallow = Guid.NewGuid();
        await store.EnqueueAsync(Job(deep), T0);

        // Three retry cycles on the deep job alone, two transitions each, taking it to ordinal 6.
        var now = T0;
        for (var cycle = 0; cycle < 3; cycle++)
        {
            var only = Assert.Single(await store.ClaimAsync(new ClaimRequest("w1", ["batch"], 32, Lease, now)));
            var retryAt = now.AddMinutes(10);
            await store.ReportOutcomesAsync(
                [new OutcomeReport(deep, "w1", only.Attempt, new JobOutcome.Failure(retryAt, "again"))], now);
            now = retryAt;
        }
        Assert.Equal(6L, (await store.GetJobHistoryAsync(deep))[^1].Ordinal);

        // Now a batch carrying both. The shallow job has only its enqueue entry, ordinal 0.
        await store.EnqueueAsync(Job(shallow), now);
        var claimed = await store.ClaimAsync(new ClaimRequest("w1", ["batch"], 32, Lease, now));
        Assert.Equal(2, claimed.Count);
        var results = await store.ReportOutcomesAsync(
            [.. claimed.Select(job => new OutcomeReport(job.JobId, "w1", job.Attempt, new JobOutcome.Success()))],
            now);
        Assert.All(results, result => Assert.Equal(OutcomeResult.Applied, result.Result));

        // Each log is a dense run from 0, and the two runs are different lengths - proof the ordinals
        // followed their own job rather than a batch-wide counter.
        Assert.Equal([0L, 1L, 2L, 3L, 4L, 5L, 6L, 7L, 8L], (await store.GetJobHistoryAsync(deep)).Select(t => t.Ordinal).ToList());
        Assert.Equal([0L, 1L, 2L], (await store.GetJobHistoryAsync(shallow)).Select(t => t.Ordinal).ToList());
    }

    [Fact]
    public async Task Failure_detail_survives_the_json_round_trip_byte_for_byte()
    {
        // The detail no longer travels as its own CLOB bind: it is serialized into a JSON document that
        // Oracle parses back apart. Every character JSON gives meaning to now has to survive two hops -
        // a double quote would end the string early, a backslash would start an escape, and a control
        // character is illegal raw inside a JSON string. Non-ASCII adds the encoding question on top,
        // since the document crosses the wire as a CLOB in the database's character set.
        var store = await OracleTestDatabase.CreateFreshStoreAsync();
        const string detail = "he said \"boom\" at C:\\logs\\a\"b\\ - \u00e9\u4e2d\u6587\U0001F600 - \r\n\ttail";
        var recorded = await ReportFailureThroughTheBatchAsync(store, detail);
        Assert.Equal(detail, recorded);
    }

    [Fact]
    public async Task Failure_detail_at_the_size_cap_survives_the_json_round_trip_whole()
    {
        // The cap is measured in UTF-8 bytes of the ORIGINAL text, not of the JSON document, and a detail
        // sitting exactly on it is kept whole. Escaping inflates the document well past the cap here -
        // every quote and backslash doubles and every non-ASCII character grows - so a shape that sized
        // any part of the trip by the encoded form, or that unpacked the detail into a VARCHAR2 rather
        // than a CLOB, truncates or throws right here.
        var store = await OracleTestDatabase.CreateFreshStoreAsync();
        var cap = store.Bounds.MaxFailureDetailBytes;

        // 10 UTF-8 bytes per unit: a " b \ c (5) + e-acute (2) + CJK (3).
        const string unit = "a\"b\\c\u00e9\u4e2d";
        var atCap = string.Concat(Enumerable.Repeat(unit, cap / 10)) + new string('x', cap % 10);
        Assert.Equal(cap, Encoding.UTF8.GetByteCount(atCap));

        var recorded = await ReportFailureThroughTheBatchAsync(store, atCap);
        Assert.Equal(atCap, recorded);
    }

    // The global Positions of a job's Transition Log entries, in ordinal order. Position is the
    // observer cursor and is not on the public JobTransition record, so the test reads it directly.
    private static async Task<List<long>> PositionsByOrdinalAsync(Guid jobId)
    {
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        await connection.OpenAsync();
        await using var read = connection.CreateCommand();
        read.BindByName = true;
        read.CommandText =
            "SELECT position FROM backwave.job_transitions WHERE job_id = :id ORDER BY ordinal";
        read.Parameters.Add(new OracleParameter("id", OracleDbType.Raw) { Size = 16, Value = jobId.ToByteArray() });
        await using var reader = await read.ExecuteReaderAsync();
        var positions = new List<long>();
        while (await reader.ReadAsync())
        {
            positions.Add(Convert.ToInt64(reader.GetValue(0)));
        }
        return positions;
    }

    // Runs one job to a dead-letter through the BATCH report path, carrying the given failure detail, and
    // returns the detail as the Transition Log read it back. Dead-letter (a Failure with no retry instant)
    // keeps the whole exchange to one claim and one batched report.
    private static async Task<string?> ReportFailureThroughTheBatchAsync(OracleJobStore store, string detail)
    {
        var jobId = Guid.NewGuid();
        await store.EnqueueAsync(Job(jobId), T0);
        var claimed = Assert.Single(await store.ClaimAsync(new ClaimRequest("w1", ["batch"], 32, Lease, T0)));

        var results = await store.ReportOutcomesAsync(
            [new OutcomeReport(jobId, "w1", claimed.Attempt, new JobOutcome.Failure(null, "boom"))
                { FailureDetail = detail }],
            T0);
        Assert.Equal(OutcomeResult.Applied, results[0].Result);

        var history = await store.GetJobHistoryAsync(jobId);
        Assert.Equal(JobState.DeadLettered, history[^1].State);
        return history[^1].FailureDetail;
    }
}
