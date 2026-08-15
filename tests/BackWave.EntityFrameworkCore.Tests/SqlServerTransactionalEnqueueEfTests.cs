using BackWave.SqlServer;
using BackWave.Storage;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace BackWave.EntityFrameworkCore.Tests;

/// <summary>The transactional-enqueue clause family through the EF path, against real SQL Server.</summary>
public sealed class SqlServerTransactionalEnqueueEfTests : TransactionalEnqueueEfConformance
{
    private const string Database = "backwave_eftest";

    // A dedicated database: these tests share a server, never tables, with the adapter suite.
    private static readonly string MasterConnectionString =
        Environment.GetEnvironmentVariable("BACKWAVE_SQLSERVER_MASTER_DSN")
        ?? "Server=localhost,14330;User Id=sa;Password=BackWave!Passw0rd;TrustServerCertificate=true;Database=master";

    private static readonly string ConnectionString =
        new SqlConnectionStringBuilder(MasterConnectionString) { InitialCatalog = Database }.ConnectionString;

    protected override OrdersDbContext CreateContext()
        => new(new DbContextOptionsBuilder<OrdersDbContext>().UseSqlServer(ConnectionString).Options);

    protected override IJobStore CreateStore(bool faultOnEnqueue = false)
        => new SqlServerJobStore(new SqlServerStoreOptions
        {
            ConnectionString = ConnectionString,
            AutoMigrate = true,
            FaultHook = faultOnEnqueue ? SabotageEnqueue : null,
        });

    protected override async ValueTask ResetDatabaseAsync()
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

        await using (var context = CreateContext())
        {
            await context.Database.EnsureCreatedAsync();
        }

        await SqlServerMigrator.MigrateAsync(ConnectionString);

        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var wipe = new SqlCommand(
            // FK order: child rows first. DELETE, not TRUNCATE — job_parents references jobs.
            "DELETE FROM backwave.job_parents; DELETE FROM backwave.jobs; DELETE FROM app.orders;",
            connection);
        await wipe.ExecuteNonQueryAsync();
    }
}
