using BackWave.Storage;
using Oracle.ManagedDataAccess.Client;

namespace BackWave.Oracle.Tests;

/// <summary>
/// The configurable schema name (ADR 0040): every store operation must work under a non-default schema,
/// and the objects must live there.
///
/// A schema is a user on Oracle, so the non-default schema is a dedicated bw_alt user, provisioned via
/// SYSTEM the same way the torture target isolates itself. The store connects as that user with its
/// SchemaName set to match, so every rewritten query runs against the bw_alt schema and nowhere else. An
/// invalid schema name is already rejected by OracleAdapterTests, so that theory is not repeated here.
/// </summary>
[Collection("oracle")]
public sealed class OracleCustomSchemaTests : IAsyncLifetime
{
    private const string Schema = "bw_alt";
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly string BaseConnectionString = OracleTestDatabase.ConnectionString;

    // The store connection: the same service as the base DSN, but as the isolated bw_alt user. Pooling is
    // off so no session lingers in a pool to block the DROP USER between tests.
    private static readonly string SchemaConnectionString =
        new OracleConnectionStringBuilder(BaseConnectionString)
        {
            UserID = Schema,
            Password = Schema,
            Pooling = false,
        }.ConnectionString;

    private static NewJob Job(JobTags? tags = null)
        => new(Guid.NewGuid(), "custom-schema-job", "{}"u8.ToArray(), "default", T0)
        {
            Tags = tags ?? JobTags.Empty,
        };

    // A fresh, empty custom-schema user with an auto-migrating store pointed at it. The store migrates the
    // schema lazily on its first operation.
    private static async Task<OracleJobStore> FreshCustomSchemaStoreAsync()
    {
        await OracleTestDatabase.ResetUserAsync(Schema);
        return new OracleJobStore(new OracleStoreOptions
        {
            ConnectionString = SchemaConnectionString,
            SchemaName = Schema,
            AutoMigrate = true,
        });
    }

    [Fact]
    public async Task FullLifecycle_RunsEntirelyUnderTheConfiguredSchema()
    {
        var store = await FreshCustomSchemaStoreAsync();
        var job = Job(JobTags.Empty.WithTag("tenant", "acme").WithLabel("nightly"));

        // Enqueue, claim, heartbeat, and report a success with output - the multi-statement write paths.
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

        // Read paths: list, count, and a tag facet.
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

        await using var connection = new OracleConnection(SchemaConnectionString);
        await connection.OpenAsync();

        // The jobs table exists under the configured schema (Oracle folds unquoted names to upper case)...
        await using (var inSchema = connection.CreateCommand())
        {
            inSchema.CommandText =
                "SELECT count(*) FROM all_tables WHERE owner = :owner AND table_name = 'JOBS'";
            inSchema.Parameters.Add(new OracleParameter("owner", Schema.ToUpperInvariant()));
            Assert.Equal(1, Convert.ToInt32(await inSchema.ExecuteScalarAsync()));
        }

        // ...and the row the store wrote is visible there, addressed by the schema-qualified name.
        await using (var rows = connection.CreateCommand())
        {
            rows.CommandText = $"SELECT count(*) FROM {Schema}.jobs";
            Assert.Equal(1, Convert.ToInt32(await rows.ExecuteScalarAsync()));
        }
    }

    [Fact]
    public async Task OutOfBandMigrator_ProvisionsAndVerifiesTheCustomSchema()
    {
        await OracleTestDatabase.ResetUserAsync(Schema);

        // The public out-of-band API applies and verifies the versioned scripts under the name.
        await OracleMigrator.MigrateAsync(SchemaConnectionString, Schema);
        await OracleMigrator.VerifySchemaVersionAsync(SchemaConnectionString, Schema);

        // A store configured with only-verify (no auto-migrate) then runs against it.
        var store = new OracleJobStore(new OracleStoreOptions
        {
            ConnectionString = SchemaConnectionString,
            SchemaName = Schema,
        });
        Assert.Equal(EnqueueResult.Ok, await store.EnqueueAsync(Job(), now: T0));
    }

    public Task InitializeAsync() => Task.CompletedTask;

    // Drop the custom-schema user at end of suite so bw_alt and every object it owns do not linger in the
    // shared container.
    public Task DisposeAsync() => OracleTestDatabase.DropUserAsync(Schema);
}
