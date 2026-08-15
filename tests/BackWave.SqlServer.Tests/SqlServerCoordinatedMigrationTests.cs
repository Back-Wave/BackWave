using Microsoft.Data.SqlClient;

namespace BackWave.SqlServer.Tests;

/// <summary>
/// Coordinated migration (ADR 0046): a SQL Server fleet cold-booting with AutoMigrate on must be safe
/// under true concurrency — exactly one Node applies the schema while the rest block, re-check, and
/// no-op. Proven by a concurrent-boot storm against a fresh schema (teeth), a storm against an
/// already-current schema (steady state), and a negative control with coordination off.
/// </summary>
[Collection("sqlserver")]
public sealed class SqlServerCoordinatedMigrationTests
{
    // A schema of its own so the storm can DROP/CREATE freely without disturbing the shared
    // 'backwave' schema every other SQL Server test uses.
    private const string Schema = "bw_migtest";
    private const int Fleet = 16;

    // SQL Server won't DROP a schema that still owns objects, so drop the schema's tables (FKs first)
    // and sequences, then the schema itself. Guarded on SCHEMA_ID so a first run against a never-created
    // schema is a no-op.
    private static async Task DropSchemaAsync()
    {
        await using var connection = new SqlConnection(SqlServerTestDatabase.ConnectionString);
        await connection.OpenAsync();
        await using var drop = new SqlCommand(
            $"""
            IF SCHEMA_ID('{Schema}') IS NOT NULL
            BEGIN
                DECLARE @sql nvarchar(max) = N'';
                SELECT @sql += 'ALTER TABLE ' + QUOTENAME(s.name) + '.' + QUOTENAME(t.name)
                             + ' DROP CONSTRAINT ' + QUOTENAME(f.name) + ';'
                  FROM sys.foreign_keys f
                  JOIN sys.tables t ON f.parent_object_id = t.object_id
                  JOIN sys.schemas s ON t.schema_id = s.schema_id
                  WHERE s.name = '{Schema}';
                SELECT @sql += 'DROP TABLE ' + QUOTENAME(s.name) + '.' + QUOTENAME(t.name) + ';'
                  FROM sys.tables t JOIN sys.schemas s ON t.schema_id = s.schema_id
                  WHERE s.name = '{Schema}';
                SELECT @sql += 'DROP SEQUENCE ' + QUOTENAME(s.name) + '.' + QUOTENAME(sq.name) + ';'
                  FROM sys.sequences sq JOIN sys.schemas s ON sq.schema_id = s.schema_id
                  WHERE s.name = '{Schema}';
                IF LEN(@sql) > 0 EXEC sp_executesql @sql;
                EXEC('DROP SCHEMA {Schema}');
            END
            """,
            connection);
        await drop.ExecuteNonQueryAsync();
    }

    // Fires K migrators as simultaneously as possible (a barrier releases them together) and returns
    // each task's outcome — null on success, the thrown exception otherwise.
    private static async Task<Exception?[]> StormAsync(bool coordinate)
    {
        using var barrier = new Barrier(Fleet);
        var tasks = Enumerable.Range(0, Fleet).Select(_ => Task.Run(async () =>
        {
            barrier.SignalAndWait();
            try
            {
                await SqlServerMigrator.MigrateAsync(SqlServerTestDatabase.ConnectionString, Schema, coordinate);
                return (Exception?)null;
            }
            catch (Exception exception)
            {
                return exception;
            }
        })).ToArray();

        return await Task.WhenAll(tasks);
    }

    private static async Task<int> SchemaVersionRowCountAsync()
    {
        await using var connection = new SqlConnection(SqlServerTestDatabase.ConnectionString);
        await connection.OpenAsync();
        await using var count = new SqlCommand($"SELECT count(*) FROM {Schema}.schema_version", connection);
        return (int)(await count.ExecuteScalarAsync())!;
    }

    private static async Task<int> DeployedVersionAsync()
    {
        await using var connection = new SqlConnection(SqlServerTestDatabase.ConnectionString);
        await connection.OpenAsync();
        await using var version = new SqlCommand($"SELECT TOP 1 version FROM {Schema}.schema_version", connection);
        return (int)(await version.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task ConcurrentFirstMigration_AllSucceed_ExactlyOneSchemaVersionRow()
    {
        await DropSchemaAsync();

        var outcomes = await StormAsync(coordinate: true);

        Assert.All(outcomes, outcome => Assert.Null(outcome));
        Assert.Equal(1, await SchemaVersionRowCountAsync());
        Assert.Equal(SqlServerMigrator.ExpectedSchemaVersion, await DeployedVersionAsync());
    }

    // The custom-schema storm above runs inside the shared test database, where RCSI is already on, so
    // it never exercises the ALTER DATABASE preamble. This one creates a genuinely FRESH database (RCSI
    // off) and storms it — the true cold-boot path, where the migration must both enable RCSI (outside
    // the transaction) and create the schema (inside it) under concurrency, all succeeding. A cold pool
    // makes the barrier fire 16 simultaneous TLS pre-login handshakes, which SQL Server transiently
    // rejects with 18456, and the RCSI preamble can kill idle pooled pre-check connections — both
    // transient cold-boot faults. MigrateAsync now absorbs them itself (ClearPool + a bounded transient
    // retry), so this calls it DIRECTLY: the assertion that all 16 succeed proves the product path is
    // self-sufficient, no test-side retry propping it up.
    [Fact]
    public async Task ConcurrentFirstBootAgainstFreshDatabase_EnablesRcsiAndMigrates()
    {
        var database = "backwave_migtest_" + Guid.NewGuid().ToString("N");
        var master = new SqlConnectionStringBuilder(SqlServerTestDatabase.ConnectionString)
        {
            InitialCatalog = "master",
        }.ConnectionString;
        var freshDb = new SqlConnectionStringBuilder(SqlServerTestDatabase.ConnectionString)
        {
            InitialCatalog = database,
        }.ConnectionString;

        await using (var admin = new SqlConnection(master))
        {
            await admin.OpenAsync();
            await using var create = new SqlCommand($"CREATE DATABASE [{database}]", admin);
            await create.ExecuteNonQueryAsync();
        }

        try
        {
            using var barrier = new Barrier(Fleet);
            var tasks = Enumerable.Range(0, Fleet).Select(_ => Task.Run(async () =>
            {
                barrier.SignalAndWait();
                try
                {
                    // Straight into the product path: MigrateAsync now rides out the cold-boot
                    // handshake storm itself, so the test does not wrap it in a retry (default schema,
                    // coordinate: true).
                    await SqlServerMigrator.MigrateAsync(freshDb);

                    return (Exception?)null;
                }
                catch (Exception exception)
                {
                    return exception;
                }
            })).ToArray();
            var outcomes = await Task.WhenAll(tasks);

            Assert.All(outcomes, outcome => Assert.Null(outcome));

            await using var check = new SqlConnection(freshDb);
            await check.OpenAsync();
            await using var rcsi = new SqlCommand(
                "SELECT CAST(is_read_committed_snapshot_on AS int) FROM sys.databases WHERE database_id = DB_ID()",
                check);
            Assert.Equal(1, (int)(await rcsi.ExecuteScalarAsync())!);
            await using var version = new SqlCommand("SELECT TOP 1 version FROM backwave.schema_version", check);
            Assert.Equal(SqlServerMigrator.ExpectedSchemaVersion, (int)(await version.ExecuteScalarAsync())!);
        }
        finally
        {
            await using var admin = new SqlConnection(master);
            await admin.OpenAsync();
            await using var drop = new SqlCommand(
                $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}];",
                admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task ConcurrentBootAgainstCurrentSchema_AllNoOp()
    {
        await DropSchemaAsync();
        await SqlServerMigrator.MigrateAsync(SqlServerTestDatabase.ConnectionString, Schema);

        // Every Node in this storm should take the unlocked pre-check path and no-op — no error, no
        // second migration.
        var outcomes = await StormAsync(coordinate: true);

        Assert.All(outcomes, outcome => Assert.Null(outcome));
        Assert.Equal(1, await SchemaVersionRowCountAsync());
        Assert.Equal(SqlServerMigrator.ExpectedSchemaVersion, await DeployedVersionAsync());
    }

    // SqlException.Number values that prove the first-boot DDL race the coordination lock exists to
    // prevent: two Nodes concurrently running the unguarded `CREATE ... IF NOT EXISTS`-style DDL collide
    // on the same object / schema / index. SQL Server does not make that DDL concurrency-safe, so a
    // genuine race surfaces as one of these already-exists errors.
    private static readonly HashSet<int> DuplicateObjectErrorNumbers = new()
    {
        2714,  // There is already an object named '...' in the database.
        2759,  // CREATE SCHEMA failed due to previous errors.
        1913,  // The operation failed because an index or statistics already exists.
        15530, // Duplicate object — the object already exists.
        1779,  // Table already has a primary key / clustered index.
        2705,  // Column/object names must be unique — already specified.
    };

    private enum StormOutcome
    {
        Race,       // The duplicate-object DDL race the coordination lock exists to prevent.
        Noise,      // Ambient login/transport blip — neither proves nor disproves the control.
        Unexpected, // A genuinely unexpected error (real defect) — fail regardless of mode.
    }

    private static StormOutcome Classify(Exception exception) => exception switch
    {
        SqlException sql when DuplicateObjectErrorNumbers.Contains(sql.Number) => StormOutcome.Race,
        // 18456 is the cold-pool TLS-handshake-storm login rejection (see the fresh-DB test), and
        // IsTransient covers transport/connection blips. Both are ambient noise, NOT a DDL race.
        SqlException { Number: 18456 } => StormOutcome.Noise,
        SqlException { IsTransient: true } => StormOutcome.Noise,
        _ => StormOutcome.Unexpected,
    };

    // Negative control: with coordination OFF, the same first-boot storm races on unguarded
    // `CREATE ... IF NOT EXISTS`-style DDL, which SQL Server does not make concurrency-safe. The race is
    // timing-dependent, so this storms a generous attempt budget and passes the moment it observes the
    // SPECIFIC duplicate-object error the lock exists to prevent — demonstrating the lock is load-bearing.
    // Even under the race the schema_version INSERT is guarded (WHERE NOT EXISTS), so the failure always
    // surfaces as a thrown error, never a duplicate version row.
    //
    // Non-reproduction of a timing-dependent race must not red ordinary CI, so if no attempt races (only
    // ambient login/transport noise, if anything) the test soft-passes by default. Set
    // BACKWAVE_STRICT_NEGATIVE_CONTROLS=1 for a strict/nightly run, where non-reproduction becomes a hard
    // failure that would catch coordination leaking into the coordinate:false path. A genuinely unexpected
    // error fails immediately in either mode.
    [Fact]
    public async Task WithoutCoordination_FirstBootStormRaces()
    {
        const int attempts = 30;
        var strict = Environment.GetEnvironmentVariable("BACKWAVE_STRICT_NEGATIVE_CONTROLS") == "1";

        for (var attempt = 0; attempt < attempts; attempt++)
        {
            await DropSchemaAsync();
            var outcomes = await StormAsync(coordinate: false);

            foreach (var exception in outcomes.OfType<Exception>())
            {
                var outcome = Classify(exception);
                if (outcome == StormOutcome.Race)
                {
                    // The load-bearing proof: the storm collided on unguarded CREATE DDL with the exact
                    // duplicate-object error the coordination lock exists to serialize away.
                    var sql = Assert.IsAssignableFrom<SqlException>(exception);
                    Assert.Contains(sql.Number, DuplicateObjectErrorNumbers);
                    return;
                }

                if (outcome == StormOutcome.Unexpected)
                {
                    Assert.Fail(
                        "Uncoordinated first-boot storm threw an unexpected, non-transient error that is " +
                        $"neither a duplicate-object race nor ambient noise: {exception}");
                }
                // Noise: ignore and keep storming.
            }
        }

        if (strict)
        {
            Assert.Fail(
                $"Expected the uncoordinated first-boot storm to race within {attempts} attempts, but none " +
                "did. Either the race is not reproducing on this engine/timing, or coordination is leaking " +
                "into the coordinate:false path.");
        }

        // Soft-pass: the race did not reproduce and only ambient noise (if anything) was seen. Not a
        // failure in ordinary CI — opt into strictness with BACKWAVE_STRICT_NEGATIVE_CONTROLS=1.
    }
}
