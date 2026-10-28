using BackWave.Storage;
using Microsoft.Data.Sqlite;

namespace BackWave.Sqlite.Tests;

public sealed class SqlitePumpStatusTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("SomeFutureTrigger")]
    [InlineData("0")]
    public async Task A_row_a_newer_version_wrote_lists_with_an_unknown_state_and_no_trigger(string trigger)
    {
        await using var temp = TempSqliteStore.Create();
        await temp.Store.CountJobsAsync();

        await using (var connection = new SqliteConnection($"Data Source={temp.Path}"))
        {
            await connection.OpenAsync();
            await using var insert = connection.CreateCommand();
            insert.CommandText =
                """
                INSERT INTO backwave_pumps
                    (worker_id, worker_group, state, fault_type, invariant_trigger, reported_at, report_interval_ms)
                VALUES ('host-1:pump-a', 'default', 99, 'Some.Future.Fault', $trigger, $at, 30000)
                """;
            insert.Parameters.AddWithValue("$trigger", trigger);
            insert.Parameters.AddWithValue("$at", T0.UtcTicks);
            await insert.ExecuteNonQueryAsync();
        }

        var listed = Assert.Single(await temp.Store.ListPumpStatusesAsync());

        Assert.Equal("host-1:pump-a", listed.WorkerId);
        Assert.Equal(PumpState.Unknown, listed.State);
        Assert.Null(listed.Trigger);
        Assert.Equal("Some.Future.Fault", listed.FaultType);
        Assert.Equal(T0, listed.ReportedAt);
        Assert.Equal(TimeSpan.FromSeconds(30), listed.ReportInterval);
    }
}
