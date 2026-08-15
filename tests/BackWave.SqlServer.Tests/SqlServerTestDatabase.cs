using BackWave.Storage;
using Microsoft.Data.SqlClient;

namespace BackWave.SqlServer.Tests;

/// <summary>
/// The dockerized test database (docker compose up -d sqlserver). Creates the database and
/// migrates once per run, then deletes between tests so every test sees a fresh store.
/// </summary>
public static class SqlServerTestDatabase
{
    private const string Database = "backwave_test";

    private static readonly string MasterConnectionString =
        Environment.GetEnvironmentVariable("BACKWAVE_SQLSERVER_MASTER_DSN")
        ?? "Server=localhost,14330;User Id=sa;Password=BackWave!Passw0rd;TrustServerCertificate=true;Database=master";

    public static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("BACKWAVE_SQLSERVER_DSN")
        ?? $"Server=localhost,14330;User Id=sa;Password=BackWave!Passw0rd;TrustServerCertificate=true;Database={Database}";

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static bool _migrated;

    public static async ValueTask<SqlServerJobStore> CreateFreshStoreAsync(
        JobHistoryPolicy historyPolicy = JobHistoryPolicy.TransitionsAndFailureDetail)
    {
        await Gate.WaitAsync();
        try
        {
            if (!_migrated)
            {
                try
                {
                    await using var master = new SqlConnection(MasterConnectionString);
                    await master.OpenAsync();
                    await using var create = new SqlCommand(
                        $"IF DB_ID('{Database}') IS NULL CREATE DATABASE [{Database}]", master);
                    await create.ExecuteNonQueryAsync();
                }
                catch (SqlException exception)
                {
                    throw new InvalidOperationException(
                        "SQL Server is not reachable. Start it with: docker compose up -d sqlserver", exception);
                }
                await SqlServerMigrator.MigrateAsync(ConnectionString);
                _migrated = true;
            }

            await using var connection = new SqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var wipe = new SqlCommand(
                // FK order: child rows first. DELETE, not TRUNCATE — job_parents/job_tags reference jobs.
                "DELETE FROM backwave.workflow_edges; DELETE FROM backwave.job_parents; " +
                "DELETE FROM backwave.job_tags; DELETE FROM backwave.jobs; DELETE FROM backwave.workflows; " +
                "DELETE FROM backwave.schedules; DELETE FROM backwave.queue_limits; " +
                "DELETE FROM backwave.operator_audit; DELETE FROM backwave.observers; " +
                "DELETE FROM backwave.observer_deliveries; DELETE FROM backwave.observer_dead_letters;",
                connection);
            await wipe.ExecuteNonQueryAsync();
        }
        finally
        {
            Gate.Release();
        }

        return new SqlServerJobStore(new SqlServerStoreOptions
        {
            ConnectionString = ConnectionString,
            HistoryPolicy = historyPolicy,
        });
    }
}

/// <summary>Serializes every SQL Server test class — they share one database.</summary>
[CollectionDefinition("sqlserver")]
public sealed class SqlServerCollection;
