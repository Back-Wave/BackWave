using BackWave.Storage;
using Microsoft.Data.SqlClient;

namespace BackWave.SqlServer.Tests;

/// <summary>
/// The configurable schema name (ADR 0040): every store operation must work under a non-default
/// schema, the objects must live there, and an invalid name must fail fast.
/// </summary>
[Collection("sqlserver")]
public sealed class SqlServerCustomSchemaTests
{
    private const string Schema = "bw_alt";
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static NewJob Job(JobTags? tags = null)
        => new(Guid.NewGuid(), "custom-schema-job", "{}"u8.ToArray(), "default", T0)
        {
            Tags = tags ?? JobTags.Empty,
        };

    // The migrator is idempotent, so we (idempotently) provision the custom schema, then wipe its rows
    // for a clean slate. Self-sufficient: creates the test database if a custom-schema test runs first.
    private static async Task<SqlServerJobStore> FreshCustomSchemaStoreAsync()
    {
        var master = Environment.GetEnvironmentVariable("BACKWAVE_SQLSERVER_MASTER_DSN")
            ?? "Server=localhost,14330;User Id=sa;Password=BackWave!Passw0rd;TrustServerCertificate=true;Database=master";
        await using (var masterConnection = new SqlConnection(master))
        {
            await masterConnection.OpenAsync();
            await using var create = new SqlCommand(
                "IF DB_ID('backwave_test') IS NULL CREATE DATABASE [backwave_test]", masterConnection);
            await create.ExecuteNonQueryAsync();
        }

        await SqlServerMigrator.MigrateAsync(SqlServerTestDatabase.ConnectionString, Schema);

        await using (var connection = new SqlConnection(SqlServerTestDatabase.ConnectionString))
        {
            await connection.OpenAsync();
            await using var wipe = new SqlCommand(
                $"DELETE FROM {Schema}.workflow_edges; DELETE FROM {Schema}.job_parents; " +
                $"DELETE FROM {Schema}.job_tags; DELETE FROM {Schema}.jobs; DELETE FROM {Schema}.workflows; " +
                $"DELETE FROM {Schema}.schedules; DELETE FROM {Schema}.queue_limits; " +
                $"DELETE FROM {Schema}.operator_audit; DELETE FROM {Schema}.observers; " +
                $"DELETE FROM {Schema}.observer_deliveries; DELETE FROM {Schema}.observer_dead_letters;",
                connection);
            await wipe.ExecuteNonQueryAsync();
        }

        return new SqlServerJobStore(new SqlServerStoreOptions
        {
            ConnectionString = SqlServerTestDatabase.ConnectionString,
            SchemaName = Schema,
        });
    }

    [Fact]
    public async Task FullLifecycle_RunsEntirelyUnderTheConfiguredSchema()
    {
        var store = await FreshCustomSchemaStoreAsync();
        var job = Job(JobTags.Empty.WithTag("tenant", "acme").WithLabel("nightly"));

        Assert.Equal(EnqueueResult.Ok, await store.EnqueueAsync(job, now: T0));

        var claimed = await store.ClaimAsync(
            new ClaimRequest("w1", ["default"], MaxJobs: 8, LeaseDuration: TimeSpan.FromMinutes(1), Now: T0));
        var record = Assert.Single(claimed);
        Assert.Equal(job.JobId, record.JobId);
        Assert.Contains(JobTag.Keyed("tenant", "acme"), record.Tags);

        var beats = await store.HeartbeatAsync("w1", [job.JobId], TimeSpan.FromMinutes(1), now: T0);
        Assert.True(Assert.Single(beats).Renewed);

        Assert.Equal(
            OutcomeResult.Applied,
            await store.ReportOutcomeAsync(
                job.JobId, "w1", record.Attempt, new JobOutcome.Success(), now: T0,
                output: "result"u8.ToArray()));

        var listed = await store.ListJobsAsync(new JobQuery { State = JobState.Succeeded });
        Assert.Equal(job.JobId, Assert.Single(listed).JobId);

        var counts = await store.CountJobsAsync();
        Assert.Contains(counts, c => c is { Queue: "default", State: JobState.Succeeded, Count: 1 });

        var facet = await store.FacetAsync("tenant");
        Assert.Contains(facet, f => f is { Value: "acme", Count: 1 });
    }

    [Fact]
    public async Task Cancel_UnderCustomSchema_TransitionsAndIsQueryable()
    {
        var store = await FreshCustomSchemaStoreAsync();
        var job = Job();
        await store.EnqueueAsync(job, now: T0);

        Assert.Equal(CancelResult.CancelledImmediately, await store.CancelJobAsync(job.JobId, "operator", now: T0));

        var cancelled = await store.ListJobsAsync(new JobQuery { State = JobState.Cancelled });
        Assert.Equal(job.JobId, Assert.Single(cancelled).JobId);
    }

    [Fact]
    public async Task Objects_LiveInTheConfiguredSchema()
    {
        var store = await FreshCustomSchemaStoreAsync();
        await store.EnqueueAsync(Job(), now: T0);

        await using var connection = new SqlConnection(SqlServerTestDatabase.ConnectionString);
        await connection.OpenAsync();

        await using (var inSchema = new SqlCommand(
            $"SELECT count(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = '{Schema}' AND TABLE_NAME = 'jobs'",
            connection))
        {
            Assert.Equal(1, (int)(await inSchema.ExecuteScalarAsync())!);
        }

        await using (var rows = new SqlCommand($"SELECT count(*) FROM {Schema}.jobs", connection))
        {
            Assert.Equal(1, (int)(await rows.ExecuteScalarAsync())!);
        }
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("has-dash")]
    [InlineData("has.dot")]
    [InlineData("1leading_digit")]
    [InlineData("drop;table")]
    [InlineData("")]
    public void InvalidSchemaName_IsRejectedWhenTheStoreIsCreated(string schema)
    {
        var exception = Assert.Throws<ArgumentException>(() => new SqlServerJobStore(new SqlServerStoreOptions
        {
            ConnectionString = SqlServerTestDatabase.ConnectionString,
            SchemaName = schema,
        }));
        Assert.Contains("valid SQL Server schema name", exception.Message);
    }
}
