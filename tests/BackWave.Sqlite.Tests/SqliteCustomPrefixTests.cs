using BackWave.Storage;
using Microsoft.Data.Sqlite;

namespace BackWave.Sqlite.Tests;

/// <summary>
/// The configurable table prefix (ADR 0040): every store operation must work under a non-default
/// prefix, the tables must be named with it (and not the canonical <c>backwave_</c> root), and an
/// invalid prefix must fail fast.
/// </summary>
public sealed class SqliteCustomPrefixTests
{
    private const string Prefix = "myapp";
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static NewJob Job(JobTags? tags = null)
        => new(Guid.NewGuid(), "custom-prefix-job", "{}"u8.ToArray(), "default", T0)
        {
            Tags = tags ?? JobTags.Empty,
        };

    [Fact]
    public async Task FullLifecycle_RunsEntirelyUnderTheConfiguredPrefix()
    {
        await using var temp = TempSqliteStore.Create(tablePrefix: Prefix);
        var store = temp.Store;
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
    public async Task Tables_AreNamedWithTheConfiguredPrefix_NotTheDefault()
    {
        await using var temp = TempSqliteStore.Create(tablePrefix: Prefix);
        await temp.Store.EnqueueAsync(Job(), now: T0);

        await using var connection = new SqliteConnection($"Data Source={temp.Path}");
        await connection.OpenAsync();

        Assert.Equal("1", await ScalarText(connection,
            $"SELECT count(*) FROM sqlite_master WHERE type='table' AND name='{Prefix}_jobs'"));
        Assert.Equal("0", await ScalarText(connection,
            "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='backwave_jobs'"));

        // Indexes are namespaced too, so two co-resident prefixes never collide on an index name.
        Assert.Equal($"ix_{Prefix}_jobs_claim", await ScalarText(connection,
            $"SELECT name FROM sqlite_master WHERE type='index' AND name='ix_{Prefix}_jobs_claim'"));
    }

    [Fact]
    public async Task OutOfBandMigrator_ProvisionsAndVerifiesTheCustomPrefix()
    {
        var path = Path.Combine(Path.GetTempPath(), $"backwave_sqlite_{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={path}";
        try
        {
            await SqliteMigrator.MigrateAsync(connectionString, Prefix);
            await SqliteMigrator.VerifySchemaVersionAsync(connectionString, Prefix);

            await using var store = new SqliteJobStore(new SqliteStoreOptions
            {
                ConnectionString = connectionString,
                TablePrefix = Prefix,
            });
            Assert.Equal(EnqueueResult.Ok, await store.EnqueueAsync(Job(), now: T0));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                try { File.Delete(path + suffix); } catch (IOException) { }
            }
        }
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("has-dash")]
    [InlineData("has.dot")]
    [InlineData("1leading_digit")]
    [InlineData("drop;table")]
    [InlineData("")]
    public void InvalidPrefix_IsRejectedWhenTheStoreIsCreated(string prefix)
    {
        var exception = Assert.Throws<ArgumentException>(() => new SqliteJobStore(new SqliteStoreOptions
        {
            ConnectionString = "Data Source=rejected.db",
            TablePrefix = prefix,
        }));
        Assert.Contains("valid SQLite table prefix", exception.Message);
    }

    private static async Task<string?> ScalarText(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? null : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
    }
}
