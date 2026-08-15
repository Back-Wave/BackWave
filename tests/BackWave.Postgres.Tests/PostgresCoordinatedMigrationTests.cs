using Npgsql;

namespace BackWave.Postgres.Tests;

/// <summary>
/// Coordinated migration (ADR 0046): a Postgres fleet cold-booting with AutoMigrate on must be safe
/// under true concurrency — exactly one Node applies the schema while the rest block, re-check, and
/// no-op. Proven by a concurrent-boot storm against a fresh schema (teeth), a storm against an
/// already-current schema (steady state), and a negative control with coordination off.
/// </summary>
[Collection("postgres")]
public sealed class PostgresCoordinatedMigrationTests
{
    // A schema of its own so the storm can DROP/CREATE freely without disturbing the shared
    // 'backwave' schema every other Postgres test uses.
    private const string Schema = "bw_migtest";
    private const int Fleet = 16;

    private static async Task DropSchemaAsync()
    {
        await using var dataSource = NpgsqlDataSource.Create(PostgresTestDatabase.ConnectionString);
        await using var drop = dataSource.CreateCommand($"DROP SCHEMA IF EXISTS {Schema} CASCADE");
        await drop.ExecuteNonQueryAsync();
    }

    // Fires K migrators as simultaneously as possible (a barrier releases them together) and returns
    // each task's outcome — null on success, the thrown exception otherwise.
    private static async Task<Exception?[]> StormAsync(bool coordinate)
    {
        using var barrier = new Barrier(Fleet);
        var tasks = Enumerable.Range(0, Fleet).Select(_ => Task.Run(async () =>
        {
            await using var dataSource = NpgsqlDataSource.Create(PostgresTestDatabase.ConnectionString);
            barrier.SignalAndWait();
            try
            {
                await PostgresMigrator.MigrateAsync(dataSource, Schema, coordinate);
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
        await using var dataSource = NpgsqlDataSource.Create(PostgresTestDatabase.ConnectionString);
        await using var count = dataSource.CreateCommand($"SELECT count(*) FROM {Schema}.schema_version");
        return (int)(long)(await count.ExecuteScalarAsync())!;
    }

    private static async Task<int> DeployedVersionAsync()
    {
        await using var dataSource = NpgsqlDataSource.Create(PostgresTestDatabase.ConnectionString);
        await using var version = dataSource.CreateCommand($"SELECT version FROM {Schema}.schema_version LIMIT 1");
        return (int)(await version.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task ConcurrentFirstMigration_AllSucceed_ExactlyOneSchemaVersionRow()
    {
        await DropSchemaAsync();

        var outcomes = await StormAsync(coordinate: true);

        Assert.All(outcomes, outcome => Assert.Null(outcome));
        Assert.Equal(1, await SchemaVersionRowCountAsync());
        Assert.Equal(PostgresMigrator.ExpectedSchemaVersion, await DeployedVersionAsync());
    }

    [Fact]
    public async Task ConcurrentBootAgainstCurrentSchema_AllNoOp()
    {
        await DropSchemaAsync();
        await using var dataSource = NpgsqlDataSource.Create(PostgresTestDatabase.ConnectionString);
        await PostgresMigrator.MigrateAsync(dataSource, Schema);

        // Every Node in this storm should take the unlocked pre-check path and no-op — no error, no
        // second migration.
        var outcomes = await StormAsync(coordinate: true);

        Assert.All(outcomes, outcome => Assert.Null(outcome));
        Assert.Equal(1, await SchemaVersionRowCountAsync());
        Assert.Equal(PostgresMigrator.ExpectedSchemaVersion, await DeployedVersionAsync());
    }

    // SQLSTATEs that prove the first-boot DDL race the coordination lock exists to prevent: two Nodes
    // concurrently running the unguarded `CREATE ... IF NOT EXISTS` DDL collide on the same catalog
    // object. 23505 covers the concurrent pg_type / pg_namespace unique-index collision the ADR calls
    // out — Postgres does not make that DDL concurrency-safe, so a genuine race surfaces as one of these.
    private static readonly HashSet<string> DuplicateObjectSqlStates = new()
    {
        PostgresErrorCodes.DuplicateTable,   // 42P07
        PostgresErrorCodes.DuplicateObject,  // 42710
        PostgresErrorCodes.DuplicateSchema,  // 42P06
        PostgresErrorCodes.UniqueViolation,  // 23505 — e.g. the duplicate pg_type insert
    };

    private enum StormOutcome
    {
        Race,       // The duplicate-object DDL race the coordination lock exists to prevent.
        Noise,      // Ambient concurrency/transport blip — neither proves nor disproves the control.
        Unexpected, // A genuinely unexpected error (real defect) — fail regardless of mode.
    }

    private static StormOutcome Classify(Exception exception) => exception switch
    {
        PostgresException pg when DuplicateObjectSqlStates.Contains(pg.SqlState) => StormOutcome.Race,
        // Transient server-side (serialization / deadlock / lock-not-available) and cold-pool transport
        // faults both flow through Npgsql's IsTransient flag; neither is the DDL race we are proving.
        NpgsqlException { IsTransient: true } => StormOutcome.Noise,
        _ => StormOutcome.Unexpected,
    };

    // Negative control: with coordination OFF, the same first-boot storm races on unguarded
    // `CREATE ... IF NOT EXISTS` DDL, which Postgres does not make concurrency-safe. The race is
    // timing-dependent, so this storms a generous attempt budget and passes the moment it observes the
    // SPECIFIC duplicate-object error the lock exists to prevent — demonstrating the lock is load-bearing.
    // Even under the race the schema_version INSERT is guarded (WHERE NOT EXISTS), so the failure always
    // surfaces as a thrown error, never a duplicate version row.
    //
    // Non-reproduction of a timing-dependent race must not red ordinary CI, so if no attempt races (only
    // ambient noise, if anything) the test soft-passes by default. Set BACKWAVE_STRICT_NEGATIVE_CONTROLS=1
    // for a strict/nightly run, where non-reproduction becomes a hard failure that would catch coordination
    // leaking into the coordinate:false path. A genuinely unexpected error fails immediately in either mode.
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
                    var postgres = Assert.IsAssignableFrom<PostgresException>(exception);
                    Assert.Contains(postgres.SqlState, DuplicateObjectSqlStates);
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
