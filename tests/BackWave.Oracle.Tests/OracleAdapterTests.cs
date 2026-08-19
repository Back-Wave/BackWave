using BackWave.Core;
using BackWave.Storage;
using Oracle.ManagedDataAccess.Client;

namespace BackWave.Oracle.Tests;

/// <summary>Adapter behaviors beyond the Conformance Suite: schema versioning, first-write races, and migration.</summary>
[Collection("oracle")]
public sealed class OracleAdapterTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static NewJob Job() => new(Guid.NewGuid(), "adapter-test", "{}"u8.ToArray(), "default", T0);

    [Fact]
    public async Task SchemaVersionMismatch_FailsLoudly_BeforeAnyOperation()
    {
        _ = await OracleTestDatabase.CreateFreshStoreAsync();
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        await connection.OpenAsync();

        await using (var bump = connection.CreateCommand())
        {
            bump.CommandText = "UPDATE backwave.schema_version SET version = 99";
            await bump.ExecuteNonQueryAsync();
        }
        try
        {
            var skewed = new OracleJobStore(
                new OracleStoreOptions { ConnectionString = OracleTestDatabase.ConnectionString });
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await skewed.EnqueueAsync(Job(), now: T0));
            Assert.Contains("schema version mismatch", exception.Message);
        }
        finally
        {
            await using var restore = connection.CreateCommand();
            restore.CommandText = $"UPDATE backwave.schema_version SET version = {OracleMigrator.ExpectedSchemaVersion}";
            await restore.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task ConcurrentFirstUpserts_OfTheSameSchedule_NeverCollide()
    {
        var store = await OracleTestDatabase.CreateFreshStoreAsync();

        // Eight nodes defining "the nightly sync" at startup, concurrently. The MERGE must converge on one
        // row rather than racing to a primary-key violation.
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
        var store = await OracleTestDatabase.CreateFreshStoreAsync();

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
        _ = await OracleTestDatabase.CreateFreshStoreAsync(); // ensure the schema exists
        await DropCoreObjectsAsync();

        // Without opting in: a clear refusal, not a silently created schema.
        var bare = new OracleJobStore(
            new OracleStoreOptions { ConnectionString = OracleTestDatabase.ConnectionString });
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await bare.EnqueueAsync(Job(), now: T0));
        Assert.Contains("schema not found", exception.Message);

        // Opted in: the same versioned scripts run (idempotently), then work proceeds.
        var migrating = new OracleJobStore(
            new OracleStoreOptions { ConnectionString = OracleTestDatabase.ConnectionString, AutoMigrate = true });
        Assert.Equal(EnqueueResult.Ok, await migrating.EnqueueAsync(Job(), now: T0));
    }

    [Fact]
    public async Task EnqueueWorkflow_MemberWithDuplicateParents_CollapsesToOneEdge()
    {
        var store = await OracleTestDatabase.CreateFreshStoreAsync();

        // A member listing the same parent twice must collapse to one job_parents edge. Inserting it
        // twice raises ORA-00001 and rolls back the whole workflow; the parent set is a set, not a list.
        var parent = Job();
        var child = Job() with { Parents = [parent.JobId, parent.JobId] };
        var workflow = new WorkflowDefinition
        {
            WorkflowId = Guid.NewGuid(),
            Members = [parent, child],
        };

        Assert.Equal(WorkflowEnqueueResult.Ok, await store.EnqueueWorkflowAsync(workflow, now: T0));
        Assert.Equal(1, await CountParentEdgesAsync(child.JobId));
        Assert.Equal(1, (await store.GetJobAsync(child.JobId))!.ParentsRemaining);
    }

    // Counts the live gating edges (job_parents rows) recorded for a child, straight from the table the
    // duplicate-parent bug double-inserted into.
    private static async Task<int> CountParentEdgesAsync(Guid childId)
    {
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM backwave.job_parents WHERE child_id = :child";
        command.Parameters.Add(new OracleParameter("child", OracleDbType.Raw) { Size = 16, Value = childId.ToByteArray() });
        return Convert.ToInt32(await command.ExecuteScalarAsync());
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
        var exception = Assert.Throws<ArgumentException>(() => new OracleJobStore(new OracleStoreOptions
        {
            ConnectionString = OracleTestDatabase.ConnectionString,
            SchemaName = schema,
        }));
        Assert.Contains("valid Oracle schema name", exception.Message);
    }

    // Drops the version marker and the jobs graph so a bare store sees "schema not found" and an opted-in
    // store re-provisions. Each drop is guarded (swallows ORA-942 table-does-not-exist) so a re-run is safe;
    // the migrator recreates everything idempotently afterward.
    private static async Task DropCoreObjectsAsync()
    {
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        await connection.OpenAsync();
        await using var drop = connection.CreateCommand();
        drop.CommandText =
            """
            BEGIN
                FOR t IN (SELECT column_value AS name FROM TABLE(sys.odcivarchar2list(
                    'job_transitions', 'job_parents', 'job_tags', 'jobs', 'schedules',
                    'queue_limits', 'schema_version'))) LOOP
                    BEGIN
                        EXECUTE IMMEDIATE 'DROP TABLE backwave.' || t.name || ' CASCADE CONSTRAINTS';
                    EXCEPTION WHEN OTHERS THEN IF SQLCODE != -942 THEN RAISE; END IF;
                    END;
                END LOOP;
            END;
            """;
        await drop.ExecuteNonQueryAsync();
    }
}
