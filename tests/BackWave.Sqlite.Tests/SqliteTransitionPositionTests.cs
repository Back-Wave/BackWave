using BackWave.Storage;
using Microsoft.Data.Sqlite;

namespace BackWave.Sqlite.Tests;

/// <summary>
/// The Transition Log position on SQLite. Postgres draws it from a SEQUENCE, which never hands out a
/// value twice; SQLite has no sequences, so the adapter keeps a high-water mark of its own in
/// <c>backwave_transition_position</c>. That mark exists for one reason: a retention purge cascades
/// transitions away, and a position derived from what is LEFT in the log would drop back under an
/// Observer cursor that already walked past it - a transition recorded at a reused position is one
/// that Observer never sees. These pin the mark against that purge, and the v1 -> v2 step that seeds
/// it from a log written before the mark existed.
/// </summary>
public sealed class SqliteTransitionPositionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);
    private static readonly JobState[] Succeeded = [JobState.Succeeded];

    [Fact]
    public async Task A_transition_recorded_after_a_retention_purge_lands_above_the_observer_cursor()
    {
        await using var temp = TempSqliteStore.Create();
        var store = temp.Store;

        // Three jobs run to Succeeded, and the Observer walks past every one of them: its cursor now
        // sits on the highest position the log has ever held.
        for (var i = 0; i < 3; i++)
        {
            await SucceedAsync(store, T0);
        }
        var walked = await ClaimObsAsync(store, T0);
        Assert.Equal(3, walked.Deliveries.Count);
        var highWater = walked.Deliveries.Max(delivery => delivery.Position);
        await ReportDeliveredAsync(store, walked, T0);
        Assert.Equal(highWater, await store.GetObserverCursorAsync("obs"));

        // Retention purges all three jobs, and the FK cascade takes every transition with them: the
        // log is empty, so anything derived from its contents has forgotten how far it got.
        Assert.Equal(3, await store.PurgeTerminalAsync(TerminalStateClass.SucceededOrCancelled, T0, maxJobs: 32));
        await using (var connection = new SqliteConnection($"Data Source={temp.Path}"))
        {
            await connection.OpenAsync();
            await using var count = connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM backwave_job_transitions";
            Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);
        }

        // A fresh job runs to Succeeded. Its transitions must land ABOVE the cursor, because the walk
        // resumes from there: a position at or below it is a transition the Observer never delivers.
        var later = T0.AddMinutes(1);
        var jobId = await SucceedAsync(store, later);
        var resumed = await ClaimObsAsync(store, later);
        var delivered = Assert.Single(resumed.Deliveries);
        Assert.Equal(jobId, delivered.JobId);
        Assert.True(
            delivered.Position > highWater,
            $"position {delivered.Position} was handed out at or below the Observer cursor {highWater}");
    }

    [Fact]
    public async Task Migrating_a_v1_log_seeds_the_high_water_mark_from_its_highest_position()
    {
        // A file written by a v1 node: the log already holds positions, and no mark exists yet. The
        // v1 -> v2 step has to pick the mark up from the highest of them, so the first transition the
        // upgraded node records continues the sequence instead of restarting it under the cursors.
        var path = Path.Combine(Path.GetTempPath(), $"backwave_sqlite_{Guid.NewGuid():N}.db");
        try
        {
            await using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                await connection.OpenAsync();
                await using var v1 = connection.CreateCommand();
                v1.CommandText = await ReadScriptAsync("0001_initial.sql")
                    + """

                      INSERT INTO backwave_jobs (job_id, wire_name, payload, queue, state, due_time)
                      VALUES ('v1-job', 'demo', x'', 'default', 3, 0);
                      INSERT INTO backwave_job_transitions
                          (job_id, ordinal, recorded_at, state, attempt, failure_detail, position)
                      VALUES ('v1-job', 0, 0, 0, 0, NULL, 41),
                             ('v1-job', 1, 0, 2, 1, NULL, 42),
                             ('v1-job', 2, 0, 3, 1, NULL, 43);
                      """;
                await v1.ExecuteNonQueryAsync();
            }
            SqliteConnection.ClearAllPools();

            await SqliteMigrator.MigrateAsync($"Data Source={path}");

            await using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                await connection.OpenAsync();
                await using var read = connection.CreateCommand();
                read.CommandText =
                    "SELECT (SELECT position FROM backwave_transition_position), (SELECT version FROM backwave_schema_version)";
                await using var reader = await read.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal(43L, reader.GetInt64(0));
                Assert.Equal((long)SqliteMigrator.ExpectedSchemaVersion, reader.GetInt64(1));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    // The v1 script exactly as the adapter ships it, so the fixture cannot drift from the schema a
    // real v1 node wrote.
    private static async Task<string> ReadScriptAsync(string name)
    {
        var assembly = typeof(SqliteMigrator).Assembly;
        var resource = Assert.Single(assembly.GetManifestResourceNames(), n => n.EndsWith(name, StringComparison.Ordinal));
        await using var stream = assembly.GetManifestResourceStream(resource)!;
        using var text = new StreamReader(stream);
        return await text.ReadToEndAsync();
    }

    // Drives one job Scheduled -> Leased -> Succeeded, recording three transitions.
    private static async Task<Guid> SucceedAsync(SqliteJobStore store, DateTimeOffset now)
    {
        var jobId = Guid.NewGuid();
        await store.EnqueueAsync(new NewJob(jobId, "demo", "{}"u8.ToArray(), "default", now), now);
        var claimed = Assert.Single(await store.ClaimAsync(new ClaimRequest("w1", ["default"], 1, Lease, now)));
        Assert.Equal(
            OutcomeResult.Applied,
            await store.ReportOutcomeAsync(jobId, "w1", claimed.Attempt, new JobOutcome.Success(), now));
        return jobId;
    }

    private static ValueTask<ObserverClaim> ClaimObsAsync(SqliteJobStore store, DateTimeOffset now)
        => store.ClaimObserverDeliveriesAsync(
            new ObserverClaimRequest("obs", Succeeded, null, null, "w1", 16, Lease, now));

    private static ValueTask ReportDeliveredAsync(SqliteJobStore store, ObserverClaim claim, DateTimeOffset now)
        => store.ReportObserverDeliveriesAsync(new ObserverDeliveryReport(
            "obs", "w1",
            [.. claim.Deliveries.Select(d => new ObserverDeliveryOutcome(d.Position, ObserverDeliveryDisposition.Delivered))],
            now));
}
