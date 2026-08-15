using BackWave.Postgres;
using BackWave.Storage;
using Npgsql;

namespace BackWave.Postgres.Tests;

/// <summary>
/// The dockerized test database (docker compose up -d postgres). Migrates once per run and
/// truncates between tests so every test sees a fresh store.
/// </summary>
public static class PostgresTestDatabase
{
    public static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("BACKWAVE_POSTGRES_DSN")
        ?? "Host=localhost;Port=5499;Username=backwave;Password=backwave;Database=backwave_test";

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static bool _migrated;

    public static async ValueTask<PostgresJobStore> CreateFreshStoreAsync(
        JobHistoryPolicy historyPolicy = JobHistoryPolicy.TransitionsAndFailureDetail)
    {
        await Gate.WaitAsync();
        try
        {
            await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
            if (!_migrated)
            {
                try
                {
                    await PostgresMigrator.MigrateAsync(dataSource);
                }
                catch (NpgsqlException exception)
                {
                    throw new InvalidOperationException(
                        "Postgres is not reachable. Start it with: docker compose up -d postgres", exception);
                }
                _migrated = true;
            }

            await using var truncate = dataSource.CreateCommand(
                "TRUNCATE backwave.jobs, backwave.job_parents, backwave.job_tags, backwave.schedules, backwave.queue_limits, " +
                "backwave.operator_audit, backwave.observers, backwave.observer_deliveries, " +
                "backwave.observer_dead_letters, backwave.workflows, backwave.workflow_edges " +
                "RESTART IDENTITY CASCADE");
            await truncate.ExecuteNonQueryAsync();
        }
        finally
        {
            Gate.Release();
        }

        // Each test constructs a store but never disposes it, so its NpgsqlDataSource pool lingers.
        // Bound the pool and prune idle connections quickly so a long suite never trips Postgres's
        // max_connections (53300: too many clients) — purely a test-harness concern.
        var bounded = new NpgsqlConnectionStringBuilder(ConnectionString)
        {
            MaxPoolSize = 4,
            ConnectionIdleLifetime = 1,
            ConnectionPruningInterval = 1,
        }.ConnectionString;

        return new PostgresJobStore(new PostgresStoreOptions
        {
            ConnectionString = bounded,
            HistoryPolicy = historyPolicy,
        });
    }
}

/// <summary>Serializes every Postgres test class — they share one database.</summary>
[CollectionDefinition("postgres")]
public sealed class PostgresCollection;
