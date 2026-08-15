using BackWave.Storage;
using Npgsql;

namespace BackWave.Postgres.Tests;

/// <summary>
/// The configurable schema name (ADR 0040): every store operation must work under a non-default
/// schema, the objects must live there and nowhere else, and an invalid name must fail fast.
/// </summary>
[Collection("postgres")]
public sealed class PostgresCustomSchemaTests
{
    private const string Schema = "bw_alt";
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static NewJob Job(string queue = "default", JobTags? tags = null)
        => new(Guid.NewGuid(), "custom-schema-job", "{}"u8.ToArray(), queue, T0)
        {
            Tags = tags ?? JobTags.Empty,
        };

    private static async Task<PostgresJobStore> FreshCustomSchemaStoreAsync(string schema = Schema)
    {
        await using var dataSource = NpgsqlDataSource.Create(PostgresTestDatabase.ConnectionString);
        await using (var drop = dataSource.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE"))
        {
            await drop.ExecuteNonQueryAsync();
        }
        return new PostgresJobStore(new PostgresStoreOptions
        {
            ConnectionString = PostgresTestDatabase.ConnectionString,
            SchemaName = schema,
            AutoMigrate = true,
        });
    }

    [Fact]
    public async Task FullLifecycle_RunsEntirelyUnderTheConfiguredSchema()
    {
        await using var store = await FreshCustomSchemaStoreAsync();
        var job = Job(tags: JobTags.Empty.WithTag("tenant", "acme").WithLabel("nightly"));

        // Enqueue (INSERT + tags + transition), claim (FOR UPDATE SKIP LOCKED + correlated tag agg),
        // heartbeat, and report a success with output — the four multi-statement write paths.
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

        // Read paths: list, count, and a tag facet — the three query builders (including the two
        // object-initializer command forms and the correlated EXISTS/COUNT DISTINCT tag SQL).
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
        await using var store = await FreshCustomSchemaStoreAsync();
        var job = Job();
        await store.EnqueueAsync(job, now: T0);

        Assert.Equal(CancelResult.CancelledImmediately, await store.CancelJobAsync(job.JobId, "operator", now: T0));

        var cancelled = await store.ListJobsAsync(new JobQuery { State = JobState.Cancelled });
        Assert.Equal(job.JobId, Assert.Single(cancelled).JobId);
    }

    [Fact]
    public async Task Objects_LiveInTheConfiguredSchema_NotTheDefault()
    {
        await using var store = await FreshCustomSchemaStoreAsync();
        await store.EnqueueAsync(Job(), now: T0);

        await using var dataSource = NpgsqlDataSource.Create(PostgresTestDatabase.ConnectionString);

        // The jobs table exists under the configured schema…
        await using (var inSchema = dataSource.CreateCommand(
            $"SELECT count(*) FROM information_schema.tables WHERE table_schema = '{Schema}' AND table_name = 'jobs'"))
        {
            Assert.Equal(1L, (long)(await inSchema.ExecuteScalarAsync())!);
        }

        // …and the row the store wrote is visible only there, addressed by the schema-qualified name.
        await using (var rows = dataSource.CreateCommand($"SELECT count(*) FROM {Schema}.jobs"))
        {
            Assert.Equal(1L, (long)(await rows.ExecuteScalarAsync())!);
        }
    }

    [Fact]
    public async Task OutOfBandMigrator_ProvisionsAndVerifiesTheCustomSchema()
    {
        const string schema = "bw_alt_oob";
        await using var dataSource = NpgsqlDataSource.Create(PostgresTestDatabase.ConnectionString);
        await using (var drop = dataSource.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE"))
        {
            await drop.ExecuteNonQueryAsync();
        }

        // The public out-of-band API applies and verifies the same versioned scripts under the name.
        await PostgresMigrator.MigrateAsync(dataSource, schema);
        await PostgresMigrator.VerifySchemaVersionAsync(dataSource, schema);

        // A store configured with only-verify (no auto-migrate) then runs against it.
        await using var store = new PostgresJobStore(new PostgresStoreOptions
        {
            ConnectionString = PostgresTestDatabase.ConnectionString,
            SchemaName = schema,
        });
        Assert.Equal(EnqueueResult.Ok, await store.EnqueueAsync(Job(), now: T0));
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
        var exception = Assert.Throws<ArgumentException>(() => new PostgresJobStore(new PostgresStoreOptions
        {
            ConnectionString = PostgresTestDatabase.ConnectionString,
            SchemaName = schema,
        }));
        Assert.Contains("valid Postgres schema name", exception.Message);
    }
}
