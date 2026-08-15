using BackWave.Postgres;
using BackWave.Storage;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BackWave.EntityFrameworkCore.Tests;

/// <summary>The transactional-enqueue clause family through the EF path, against real Postgres.</summary>
public sealed class PostgresTransactionalEnqueueEfTests : TransactionalEnqueueEfConformance
{
    // A dedicated database: these tests share a server, never tables, with the adapter suite.
    private static readonly string AdminConnectionString =
        Environment.GetEnvironmentVariable("BACKWAVE_POSTGRES_DSN")
        ?? "Host=localhost;Port=5499;Username=backwave;Password=backwave;Database=backwave_test";

    private static readonly string ConnectionString =
        new NpgsqlConnectionStringBuilder(AdminConnectionString) { Database = "backwave_eftest" }.ConnectionString;

    protected override OrdersDbContext CreateContext()
        => new(new DbContextOptionsBuilder<OrdersDbContext>().UseNpgsql(ConnectionString).Options);

    protected override IJobStore CreateStore(bool faultOnEnqueue = false)
        => new PostgresJobStore(new PostgresStoreOptions
        {
            ConnectionString = ConnectionString,
            AutoMigrate = true,
            FaultHook = faultOnEnqueue ? SabotageEnqueue : null,
        });

    protected override async ValueTask ResetDatabaseAsync()
    {
        await using (var admin = NpgsqlDataSource.Create(AdminConnectionString))
        {
            await using var exists = admin.CreateCommand(
                "SELECT 1 FROM pg_database WHERE datname = 'backwave_eftest'");
            if (await exists.ExecuteScalarAsync() is null)
            {
                await using var create = admin.CreateCommand("CREATE DATABASE backwave_eftest");
                await create.ExecuteNonQueryAsync();
            }
        }

        await using (var context = CreateContext())
        {
            await context.Database.EnsureCreatedAsync();
        }

        await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
        await PostgresMigrator.MigrateAsync(dataSource);
        await using var truncate = dataSource.CreateCommand(
            "TRUNCATE backwave.jobs, backwave.job_parents, app.orders CASCADE");
        await truncate.ExecuteNonQueryAsync();
    }
}
