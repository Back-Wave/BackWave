using BackWave.Core;
using BackWave.Storage;
using Microsoft.Data.SqlClient;

namespace BackWave.SqlServer.Tests;

/// <summary>Adapter behaviors beyond the Conformance Suite: schema versioning and migration.</summary>
[Collection("sqlserver")]
public sealed class SqlServerAdapterTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static NewJob Job() => new(Guid.NewGuid(), "adapter-test", "{}"u8.ToArray(), "default", T0);

    [Fact]
    public async Task SchemaVersionMismatch_FailsLoudly_BeforeAnyOperation()
    {
        _ = await SqlServerTestDatabase.CreateFreshStoreAsync();
        await using var connection = new SqlConnection(SqlServerTestDatabase.ConnectionString);
        await connection.OpenAsync();

        await using (var bump = new SqlCommand("UPDATE backwave.schema_version SET version = 99", connection))
        {
            await bump.ExecuteNonQueryAsync();
        }
        try
        {
            var skewed = new SqlServerJobStore(
                new SqlServerStoreOptions { ConnectionString = SqlServerTestDatabase.ConnectionString });
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await skewed.EnqueueAsync(Job(), now: T0));
            Assert.Contains("schema version mismatch", exception.Message);
        }
        finally
        {
            await using var restore = new SqlCommand(
                $"UPDATE backwave.schema_version SET version = {SqlServerMigrator.ExpectedSchemaVersion}", connection);
            await restore.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task ConcurrentFirstUpserts_OfTheSameSchedule_NeverCollide()
    {
        var store = await SqlServerTestDatabase.CreateFreshStoreAsync();

        // Eight nodes defining "the nightly sync" at startup, concurrently. The HOLDLOCK
        // range only serializes them inside a transaction — autocommit raced to a PK
        // violation here.
        var schedule = new ScheduleRecord
        {
            ScheduleId = "racy-upsert",
            Cron = CronExpression.Parse("0 3 * * *").Canonical,
            WireName = "adapter-test",
            Payload = "{}"u8.ToArray(),
            Queue = "default",
            Cursor = T0,
        };
        await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => Task.Run(async () => await store.UpsertScheduleAsync(schedule))));

        Assert.Single(await store.ListSchedulesAsync());
    }

    [Fact]
    public async Task ConcurrentFirstLimitWrites_OfTheSameQueue_NeverCollide()
    {
        var store = await SqlServerTestDatabase.CreateFreshStoreAsync();

        await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => Task.Run(async () => await store.SetConcurrencyLimitAsync("racy-queue", 1, "op", T0))));

        // The limit holds: one slot, so a second claimer gets nothing.
        await store.EnqueueAsync(Job() with { Queue = "racy-queue" }, now: T0);
        await store.EnqueueAsync(Job() with { Queue = "racy-queue" }, now: T0);
        Assert.Single(await store.ClaimAsync(new ClaimRequest("w1", ["racy-queue"], 32, TimeSpan.FromMinutes(1), T0)));
        Assert.Empty(await store.ClaimAsync(new ClaimRequest("w2", ["racy-queue"], 32, TimeSpan.FromMinutes(1), T0)));
    }

    [Fact]
    public async Task AutoMigrate_IsOptIn_AndExecutesTheCanonicalScripts()
    {
        _ = await SqlServerTestDatabase.CreateFreshStoreAsync(); // ensure the database exists
        await using var connection = new SqlConnection(SqlServerTestDatabase.ConnectionString);
        await connection.OpenAsync();
        await using (var drop = new SqlCommand(
            """
            DROP TABLE IF EXISTS backwave.job_transitions;
            DROP TABLE IF EXISTS backwave.job_parents;
            DROP TABLE IF EXISTS backwave.job_tags;
            DROP TABLE IF EXISTS backwave.jobs;
            DROP TABLE IF EXISTS backwave.schedules;
            DROP TABLE IF EXISTS backwave.queue_limits;
            DROP TABLE IF EXISTS backwave.schema_version;
            """,
            connection))
        {
            await drop.ExecuteNonQueryAsync();
        }

        // Without opting in: a clear refusal, not a silently created schema.
        var bare = new SqlServerJobStore(
            new SqlServerStoreOptions { ConnectionString = SqlServerTestDatabase.ConnectionString });
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await bare.EnqueueAsync(Job(), now: T0));
        Assert.Contains("schema not found", exception.Message);

        // Opted in: the same versioned scripts run, then work proceeds.
        var migrating = new SqlServerJobStore(
            new SqlServerStoreOptions { ConnectionString = SqlServerTestDatabase.ConnectionString, AutoMigrate = true });
        Assert.Equal(EnqueueResult.Ok, await migrating.EnqueueAsync(Job(), now: T0));
    }
}
