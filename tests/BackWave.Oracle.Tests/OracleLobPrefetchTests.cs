using BackWave.Storage;
using Oracle.ManagedDataAccess.Client;

namespace BackWave.Oracle.Tests;

/// <summary>
/// The LOB prefetch on the Oracle read paths: what it buys, what it must never change, and what keeps it
/// from buying speed with unbounded memory.
///
/// ODP.NET hands back a locator rather than a value for a BLOB or CLOB unless the command asks for a
/// prefetch, and following a locator costs a round trip per value. The prefetch has two halves that only
/// work together: the prefetch size decides which values ride in the row, and the fetch size is the BYTE
/// budget for one fetch round trip. Prefetching without widening the budget makes every row bigger than
/// the budget, so the fetch array collapses to one row per trip and the trips come straight back under a
/// different name. These tests measure the database's own round-trip counter, so neither half can be
/// dropped without one of them going red.
/// </summary>
[Collection("oracle")]
public sealed class OracleLobPrefetchTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly StoreBounds Bounds = new();

    // The prefetch size the adapter uses for a text column the store puts no explicit cap on, such as a
    // workflow name. It is the failure-detail cap, the largest bound the store does enforce on text.
    private const int UncappedTextPrefetch = 8_192;

    // The non-LOB part of a jobs row, rounded up generously: ids, timestamps, states, counters. Only used
    // to leave headroom in the memory ceiling below, so it is deliberately far larger than the roughly
    // 9 KB the driver actually reserves.
    private const int ScalarAllowance = 16 * 1024;

    private static NewJob Job(ReadOnlyMemory<byte> payload = default, string queue = "prefetch") =>
        new(Guid.NewGuid(), "prefetch-test", payload, queue, T0);

    [Theory]
    [InlineData(0)]         // empty payload: never prefetched, always read through its locator
    [InlineData(1)]
    [InlineData(65_535)]    // one byte under the cap, so it rides in the row
    [InlineData(65_536)]    // exactly the cap, and exactly the prefetch size: the last size that rides
    public async Task Payload_bytes_come_back_identical_at_every_size_up_to_the_cap(int size)
    {
        var store = await OracleTestDatabase.CreateFreshStoreAsync();
        var payload = Pattern(size);
        var job = Job(payload);
        Assert.Equal(EnqueueResult.Ok, await store.EnqueueAsync(job, T0));

        // Read back through both paths that materialize a payload: the single-row get and the claim.
        var fetched = await store.GetJobAsync(job.JobId);
        Assert.NotNull(fetched);
        Assert.True(payload.Span.SequenceEqual(fetched.Payload.Span));

        var claimed = await store.ClaimAsync(
            new ClaimRequest("prefetch-worker", ["prefetch"], 1, TimeSpan.FromMinutes(5), T0));
        Assert.True(payload.Span.SequenceEqual(Assert.Single(claimed).Payload.Span));
    }

    [Theory]
    [InlineData(UncappedTextPrefetch - 1)]  // under the prefetch size: rides in the row
    [InlineData(UncappedTextPrefetch)]      // exactly the prefetch size: the last size that rides
    [InlineData(UncappedTextPrefetch + 1)]  // over it: the driver leaves a locator and the read follows it
    public async Task Text_comes_back_identical_on_both_sides_of_the_prefetch_boundary(int length)
    {
        var store = await OracleTestDatabase.CreateFreshStoreAsync();

        // A workflow name is the store's one uncapped text column, so it is the only value that can be
        // written on the far side of its own prefetch size. Multi-byte on purpose: the prefetch size is
        // counted in CHARACTERS for a CLOB, not in UTF-8 bytes, and a character-counted boundary read as
        // a byte-counted one puts the crossover in the wrong place.
        var name = string.Concat(Enumerable.Repeat("é", length));
        Assert.Equal(length, name.Length);
        var workflowId = Guid.NewGuid();
        Assert.Equal(
            WorkflowEnqueueResult.Ok,
            await store.EnqueueWorkflowAsync(
                new WorkflowDefinition { WorkflowId = workflowId, Name = name, Members = [Job()] }, T0));

        Assert.Equal(name, Assert.Single(await store.ListWorkflowsAsync()).Name);
        Assert.Equal(name, (await store.GetWorkflowAsync(workflowId))!.Name);
    }

    [Fact]
    public async Task A_value_over_the_prefetch_size_costs_the_round_trip_a_prefetched_one_does_not()
    {
        var store = await OracleTestDatabase.CreateFreshStoreAsync();

        // Two workflows either side of the boundary, read by the same statement. The LOB counter reports
        // what the wire carried: the short name arrived inside its row, the long one left a locator that
        // the read had to follow. If this ever reports two, the prefetch stopped being applied; if it
        // reports zero, the locator fallback stopped being exercised and the boundary test above proves
        // nothing.
        await AddNamedWorkflowAsync(store, new string('a', UncappedTextPrefetch));
        await AddNamedWorkflowAsync(store, new string('a', UncappedTextPrefetch + 1));

        using var scope = OracleRoundTrips.Observe();
        var workflows = await store.ListWorkflowsAsync();
        Assert.Equal(2, workflows.Count);
        Assert.Equal(1, scope.LobReads);
    }

    [Fact]
    public async Task A_null_cause_stays_null_and_an_empty_one_does_not_become_a_value()
    {
        var store = await OracleTestDatabase.CreateFreshStoreAsync();
        var explicitlyNull = Job();
        var emptyString = Job();
        Assert.Equal(EnqueueResult.Ok, await store.EnqueueAsync(explicitlyNull, T0));
        Assert.Equal(EnqueueResult.Ok, await store.EnqueueAsync(emptyString, T0));

        // Oracle stores the empty string as NULL, so a terminal_cause assigned '' reads back as NULL and
        // not as "". That is the behavior callers already see, and prefetching the column must not change
        // it: a prefetched empty value and an absent one have to stay indistinguishable, because the
        // driver reports both the same way - as a null indicator carried with the row.
        await SetTerminalCauseAsync(explicitlyNull.JobId, "NULL");
        await SetTerminalCauseAsync(emptyString.JobId, "''");

        Assert.Null((await store.GetJobAsync(explicitlyNull.JobId))!.TerminalCause);
        Assert.Null((await store.GetJobAsync(emptyString.JobId))!.TerminalCause);

        // And through the page read, which selects the same column with a different statement.
        Assert.All(await store.ListJobsAsync(new JobQuery()), job => Assert.Null(job.TerminalCause));
    }

    [Fact]
    public async Task A_full_job_page_costs_the_database_no_round_trip_per_row()
    {
        var store = await OracleTestDatabase.CreateFreshStoreAsync();
        var page = Bounds.MaxMonitorPageSize;
        for (var i = 0; i < page; i++)
        {
            Assert.Equal(EnqueueResult.Ok, await store.EnqueueAsync(Job(), T0));
        }
        await MarkEveryJobDeadLetteredAsync();

        // Measured by the database, not by the adapter: v$sesstat's SQL*Net round-trip counter, summed
        // over the store's pooled sessions and read from a session that excludes itself. The adapter's
        // own instrument cannot see this, because the round trips a half-applied prefetch costs are fetch
        // trips rather than LOB reads, and the LOB count is zero either way.
        //
        // The three outcomes are far enough apart that the threshold needs no precision. Both halves
        // applied: two statements arriving in seven fetch windows of 32 rows, plus the connection's own
        // handshake, so roughly a dozen trips. Prefetch alone, with the fetch size left at the driver's
        // 131,072-byte default: a prefetched jobs row is larger than that, so the fetch array collapses
        // to one row per trip and the page costs about 200. Neither half: a locator per payload and per
        // terminal cause, about 400. Measured on this database: 13, 211, and 419.
        await using var meter = new OracleConnection(OracleTestDatabase.ConnectionString);
        await meter.OpenAsync();
        var before = await RoundTripsAsync(meter);
        var jobs = await store.ListJobsAsync(new JobQuery { MaxResults = page });
        var spent = await RoundTripsAsync(meter) - before;

        Assert.Equal(page, jobs.Count);
        Assert.InRange(spent, 1, 60);
    }

    [Fact]
    public async Task The_buffer_a_read_declares_is_bounded_by_the_row_shape_and_not_by_the_page()
    {
        var store = await OracleTestDatabase.CreateFreshStoreAsync();
        for (var i = 0; i < Bounds.MaxMonitorPageSize; i++)
        {
            Assert.Equal(EnqueueResult.Ok, await store.EnqueueAsync(Job(), T0));
        }

        // The fetch size is the bytes the driver may hold in flight for one read command, so it is the
        // adapter's memory ceiling. It has to be bounded by the row SHAPE, and the shape is bounded
        // because every LOB column in it is prefetched at a cap the store already enforces. A jobs row
        // carries two of them, both capped at MaxPayloadBytes, and the window is one claim batch wide.
        var shape = (long)Bounds.MaxClaimBatch * ((2L * Bounds.MaxPayloadBytes) + ScalarAllowance);

        long fullPage;
        using (var scope = OracleRoundTrips.Observe())
        {
            await store.ListJobsAsync(new JobQuery { MaxResults = Bounds.MaxMonitorPageSize });
            fullPage = scope.FetchWindowBytes;
        }
        Assert.InRange(fullPage, 1, shape);

        // The page size must not enter it. A window sized from the rows a query returns rather than from
        // the row it returns them in would make the widest page the widest buffer, which is the memory
        // the cap exists to rule out: 200 rows at the payload cap is a 26 MB buffer. Instead the page
        // arrives in windows of MaxClaimBatch rows, so a page seven times longer asks for no more memory.
        long shortPage;
        using (var scope = OracleRoundTrips.Observe())
        {
            await store.ListJobsAsync(new JobQuery { MaxResults = Bounds.MaxClaimBatch });
            shortPage = scope.FetchWindowBytes;
        }
        Assert.Equal(fullPage, shortPage);
    }

    [Fact]
    public async Task Raising_the_size_bounds_cannot_raise_the_buffer_past_the_absolute_ceiling()
    {
        await OracleTestDatabase.CreateFreshStoreAsync();

        // Every input the window is derived from is operator-settable and validated by nothing, so the
        // derivation on its own promises no bound at all. At this payload cap a jobs row carries two
        // prefetched LOB columns of 8 MB each, and a window one claim batch wide works out to about half
        // a gigabyte - which the driver would hold in memory for one monitor page. The check above is
        // blind to that, because it computes its ceiling from the same bounds it reads. This one names
        // an absolute number instead, so raising a bound moves the derived window and not the answer.
        var store = new OracleJobStore(new OracleStoreOptions
        {
            ConnectionString = OracleTestDatabase.ConnectionString,
            Bounds = new StoreBounds { MaxPayloadBytes = 8 * 1024 * 1024 },
        });
        Assert.Equal(EnqueueResult.Ok, await store.EnqueueAsync(Job(), T0));

        using var scope = OracleRoundTrips.Observe();
        var jobs = await store.ListJobsAsync(new JobQuery { MaxResults = Bounds.MaxMonitorPageSize });

        // Equal, not merely within: the clamp is what produced this number, so anything smaller would
        // mean the derived window came in under the ceiling and the ceiling was never exercised.
        Assert.Single(jobs);
        Assert.Equal(OracleJobStore.MaxFetchWindowBytes, scope.FetchWindowBytes);
    }

    [Fact]
    public async Task A_page_of_jobs_at_the_payload_cap_still_costs_the_database_no_round_trip_per_row()
    {
        var store = await OracleTestDatabase.CreateFreshStoreAsync();
        var page = Bounds.MaxMonitorPageSize;
        var payload = Pattern(Bounds.MaxPayloadBytes);
        for (var i = 0; i < page; i++)
        {
            Assert.Equal(EnqueueResult.Ok, await store.EnqueueAsync(Job(payload), T0));
        }
        await MarkEveryJobDeadLetteredAsync();

        // The sibling wire-truth check above reads a page whose payloads are all EMPTY, and an empty LOB
        // is the one value the driver never prefetches. So it proves the fetch window and the terminal
        // cause, and proves nothing at all about a payload riding in its row - which is the size class
        // the customer report is about. This reads the same page with every payload at the cap.
        //
        // A prefetch that stops applying at this size costs a locator per payload on top of the one per
        // terminal cause, so the page goes from a dozen trips to several hundred. The 28 MB of payload
        // still crosses the wire either way. What changes is how many trips carry it. Measured on this
        // database: 19, which leaves the threshold below a wide margin in both directions.
        await using var meter = new OracleConnection(OracleTestDatabase.ConnectionString);
        await meter.OpenAsync();
        var before = await RoundTripsAsync(meter);
        List<JobRecord> jobs;
        using (var scope = OracleRoundTrips.Observe())
        {
            jobs = [.. await store.ListJobsAsync(new JobQuery { MaxResults = page })];

            // The adapter's own counter has to agree with the database's: nothing on this page was read
            // through a locator, at either the payload cap or the failure-detail cap.
            Assert.Equal(0, scope.LobReads);
        }
        var spent = await RoundTripsAsync(meter) - before;

        Assert.Equal(page, jobs.Count);
        Assert.InRange(spent, 1, 60);

        // And the bytes are the bytes. A prefetch that silently clipped a payload at its cap would cost
        // no round trip and pass every count above.
        Assert.All(jobs, job => Assert.True(payload.Span.SequenceEqual(job.Payload.Span)));
    }

    private static async Task AddNamedWorkflowAsync(OracleJobStore store, string name) =>
        Assert.Equal(
            WorkflowEnqueueResult.Ok,
            await store.EnqueueWorkflowAsync(
                new WorkflowDefinition { WorkflowId = Guid.NewGuid(), Name = name, Members = [Job()] }, T0));

    // A payload whose every byte depends on its position, so a value that comes back truncated, padded,
    // or shifted fails rather than matching by luck.
    private static ReadOnlyMemory<byte> Pattern(int size)
    {
        var bytes = new byte[size];
        for (var i = 0; i < size; i++)
        {
            bytes[i] = (byte)((i * 31) + 7);
        }
        return bytes;
    }

    // Sets terminal_cause from a SQL literal rather than a bound parameter, because for this column the
    // two are not the same thing. Oracle's empty-string-is-NULL rule is a property of the SQL literal:
    // an assignment of '' stores NULL. Binding an empty string through a parameter instead writes an
    // empty CLOB, which reads back as "" and is a value. The literal is the case that has to be pinned.
    private static async Task SetTerminalCauseAsync(Guid jobId, string sqlLiteral)
    {
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"UPDATE backwave.jobs SET terminal_cause = {sqlLiteral} WHERE job_id = :id";
        command.Parameters.Add(new OracleParameter("id", OracleDbType.Raw) { Size = 16, Value = jobId.ToByteArray() });
        await command.ExecuteNonQueryAsync();
    }

    private static async Task MarkEveryJobDeadLetteredAsync()
    {
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE backwave.jobs SET state = 6, terminal_at = SYSTIMESTAMP, " +
            "terminal_cause = 'prefetch fixture: dead-lettered'";
        await command.ExecuteNonQueryAsync();
    }

    // The database's count of SQL*Net round trips made by the store's sessions. The measuring session is
    // excluded by sid: its own count moves while it runs this query, and including it would report the
    // measurement instead of the thing measured. Read over ONE held-open connection so the excluded sid
    // is the same at both ends of a delta.
    private static async Task<long> RoundTripsAsync(OracleConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT NVL(SUM(st.value), 0)
            FROM   v$sesstat st
                   JOIN v$statname sn ON sn.statistic# = st.statistic#
                   JOIN v$session s ON s.sid = st.sid
            WHERE  sn.name = 'SQL*Net roundtrips to/from client'
            AND    s.username = :app
            AND    st.sid <> SYS_CONTEXT('USERENV', 'SID')
            """;
        command.Parameters.Add(new OracleParameter("app", OracleTestDatabase.AppUser.ToUpperInvariant()));
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}
