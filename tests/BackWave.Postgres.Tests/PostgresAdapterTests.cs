using BackWave.Storage;
using Npgsql;

namespace BackWave.Postgres.Tests;

/// <summary>Adapter behaviors beyond the Conformance Suite: schema versioning and migration.</summary>
[Collection("postgres")]
public sealed class PostgresAdapterTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static NewJob Job() => new(Guid.NewGuid(), "adapter-test", "{}"u8.ToArray(), "default", T0);

    [Fact]
    public async Task SchemaVersionMismatch_FailsLoudly_BeforeAnyOperation()
    {
        await using var store = await PostgresTestDatabase.CreateFreshStoreAsync();
        await using var dataSource = NpgsqlDataSource.Create(PostgresTestDatabase.ConnectionString);

        await using (var bump = dataSource.CreateCommand("UPDATE backwave.schema_version SET version = 99"))
        {
            await bump.ExecuteNonQueryAsync();
        }
        try
        {
            await using var skewed = new PostgresJobStore(
                new PostgresStoreOptions { ConnectionString = PostgresTestDatabase.ConnectionString });
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await skewed.EnqueueAsync(Job(), now: T0));
            Assert.Contains("schema version mismatch", exception.Message);
        }
        finally
        {
            await using var restore = dataSource.CreateCommand(
                $"UPDATE backwave.schema_version SET version = {PostgresMigrator.ExpectedSchemaVersion}");
            await restore.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task AutoMigrate_IsOptIn_AndExecutesTheCanonicalScripts()
    {
        await using var dataSource = NpgsqlDataSource.Create(PostgresTestDatabase.ConnectionString);
        await using (var drop = dataSource.CreateCommand("DROP SCHEMA IF EXISTS backwave CASCADE"))
        {
            await drop.ExecuteNonQueryAsync();
        }

        // Without opting in: a clear refusal, not a silently created schema.
        await using (var bare = new PostgresJobStore(
            new PostgresStoreOptions { ConnectionString = PostgresTestDatabase.ConnectionString }))
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await bare.EnqueueAsync(Job(), now: T0));
            Assert.Contains("schema not found", exception.Message);
        }

        // Opted in: the same versioned scripts run, then work proceeds.
        await using (var migrating = new PostgresJobStore(
            new PostgresStoreOptions { ConnectionString = PostgresTestDatabase.ConnectionString, AutoMigrate = true }))
        {
            Assert.Equal(EnqueueResult.Ok, await migrating.EnqueueAsync(Job(), now: T0));
        }
    }
}
