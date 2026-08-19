using Oracle.ManagedDataAccess.Client;

namespace BackWave.Oracle.Tests;

/// <summary>
/// Coordinated migration (ADR 0046): an Oracle fleet cold-booting with AutoMigrate on must be safe under
/// true concurrency - several Nodes apply the schema at once and converge on one current schema, with no
/// error and exactly one schema_version row. Proven by a concurrent-boot storm against a fresh schema
/// (teeth) and a storm against an already-current schema (steady state).
///
/// A schema is a user on Oracle, so the storm runs under its own dedicated bw_migtest user/schema,
/// provisioned via SYSTEM the same way the torture target isolates itself, and never touches the shared
/// backwave schema the other tests own.
///
/// Note on the negative control: the Postgres and SQL Server suites also run a coordinate:false control
/// that reproduces the first-boot DDL race their coordination lock exists to serialize away. Oracle takes
/// no such lock - the schema script is idempotent and concurrency-safe by construction (guarded object DDL
/// plus Oracle serializing DDL on each object inside the database). There is no race to reproduce, so
/// instead of a control that could never fire, the third test asserts the Oracle-true outcome: even with
/// coordinate:false the same first-boot storm converges cleanly.
/// </summary>
[Collection("oracle")]
public sealed class OracleCoordinatedMigrationTests : IAsyncLifetime
{
    // A user/schema of its own so the storm can DROP/CREATE freely without disturbing the shared 'backwave'
    // schema every other Oracle test uses.
    private const string Schema = "bw_migtest";
    private const int Fleet = 16;

    private static readonly string BaseConnectionString = OracleTestDatabase.ConnectionString;

    // The storm connection: the same service as the base DSN, but as the isolated bw_migtest user. Pooling
    // is off so no session lingers in a pool to block the DROP USER between tests.
    private static readonly string MigrationConnectionString =
        new OracleConnectionStringBuilder(BaseConnectionString)
        {
            UserID = Schema,
            Password = Schema,
            Pooling = false,
        }.ConnectionString;

    // Fires K migrators as simultaneously as possible (a barrier releases them together) and returns each
    // task's outcome - null on success, the thrown exception otherwise.
    private static async Task<Exception?[]> StormAsync(bool coordinate)
    {
        using var barrier = new Barrier(Fleet);
        var tasks = Enumerable.Range(0, Fleet).Select(_ => Task.Run(async () =>
        {
            barrier.SignalAndWait();
            try
            {
                await MigrateRidingOutConnectivityFaultsAsync(coordinate);
                return (Exception?)null;
            }
            catch (Exception exception)
            {
                return exception;
            }
        })).ToArray();

        return await Task.WhenAll(tasks);
    }

    // The coordinate:false path strips the migrator's own transient-connectivity retry, so a cold-boot
    // listener-handoff fault (ORA-12518, ORA-12570, and the rest of the connectivity set) can surface here -
    // unrelated to the guarded-DDL convergence this test proves. Ride those out with a bounded retry. Any
    // non-connectivity error (a real convergence fault such as ORA-00955 or ORA-00001) propagates unchanged
    // and fails the storm as before.
    private static async Task MigrateRidingOutConnectivityFaultsAsync(bool coordinate)
    {
        const int maxAttempts = 10;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await OracleMigrator.MigrateAsync(MigrationConnectionString, Schema, coordinate);
                return;
            }
            catch (OracleException exception)
                when (OracleFaultCodes.IsConnectivityFault(exception.Number) && attempt < maxAttempts)
            {
                await Task.Delay(250);
            }
        }
    }

    private static async Task<int> SchemaVersionRowCountAsync()
    {
        await using var connection = new OracleConnection(MigrationConnectionString);
        await connection.OpenAsync();
        await using var count = connection.CreateCommand();
        count.CommandText = $"SELECT count(*) FROM {Schema}.schema_version";
        return Convert.ToInt32(await count.ExecuteScalarAsync());
    }

    private static async Task<int> DeployedVersionAsync()
    {
        await using var connection = new OracleConnection(MigrationConnectionString);
        await connection.OpenAsync();
        await using var version = connection.CreateCommand();
        version.CommandText = $"SELECT version FROM {Schema}.schema_version FETCH FIRST 1 ROW ONLY";
        return Convert.ToInt32(await version.ExecuteScalarAsync());
    }

    [Fact]
    public async Task ConcurrentFirstMigration_AllSucceed_ExactlyOneSchemaVersionRow()
    {
        await OracleTestDatabase.ResetUserAsync(Schema);

        var outcomes = await StormAsync(coordinate: true);

        Assert.All(outcomes, outcome => Assert.Null(outcome));
        Assert.Equal(1, await SchemaVersionRowCountAsync());
        Assert.Equal(OracleMigrator.ExpectedSchemaVersion, await DeployedVersionAsync());
    }

    [Fact]
    public async Task ConcurrentBootAgainstCurrentSchema_AllNoOp()
    {
        await OracleTestDatabase.ResetUserAsync(Schema);
        await OracleMigrator.MigrateAsync(MigrationConnectionString, Schema);

        // Every Node in this storm should re-run the idempotent, guarded scripts and no-op - no error, no
        // second schema_version row.
        var outcomes = await StormAsync(coordinate: true);

        Assert.All(outcomes, outcome => Assert.Null(outcome));
        Assert.Equal(1, await SchemaVersionRowCountAsync());
        Assert.Equal(OracleMigrator.ExpectedSchemaVersion, await DeployedVersionAsync());
    }

    // Oracle-true analog of the siblings' negative control. Oracle takes no coordination lock, so the
    // coordinate:false path is not a race waiting to happen - the guarded DDL is concurrency-safe on its
    // own. This proves it: the same first-boot storm with coordinate:false still converges with no error
    // and exactly one schema_version row.
    [Fact]
    public async Task UncoordinatedFirstBootStorm_StillConverges_OnOracle()
    {
        await OracleTestDatabase.ResetUserAsync(Schema);

        var outcomes = await StormAsync(coordinate: false);

        Assert.All(outcomes, outcome => Assert.Null(outcome));
        Assert.Equal(1, await SchemaVersionRowCountAsync());
        Assert.Equal(OracleMigrator.ExpectedSchemaVersion, await DeployedVersionAsync());
    }

    public Task InitializeAsync() => Task.CompletedTask;

    // Drop the storm's user at end of suite so bw_migtest and every object it owns do not linger in the
    // shared container.
    public Task DisposeAsync() => OracleTestDatabase.DropUserAsync(Schema);
}
