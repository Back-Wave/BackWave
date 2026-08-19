using BackWave.Storage;
using Oracle.ManagedDataAccess.Client;

namespace BackWave.Oracle.Tests;

/// <summary>
/// The dockerized test database (docker compose up -d oracle). Migrates once per run, then deletes every
/// row between tests so each test sees a fresh store. The wipe is a single anonymous PL/SQL block - ODP.NET
/// sends one statement per round-trip, so the DELETEs cannot be semicolon-batched into one command text.
/// </summary>
public static class OracleTestDatabase
{
    public static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("BACKWAVE_ORACLE_DSN")
        ?? "User Id=backwave;Password=backwave;Data Source=localhost:15210/FREEPDB1;";

    // Privileged login used to create and drop the per-class test users. A schema is a user on Oracle, so
    // the isolated test schemas (bw_migtest, bw_alt) are provisioned via SYSTEM. Defaults to SYSTEM on the
    // same service as the base DSN, matching the torture target's provisioning.
    public static readonly string SystemConnectionString =
        Environment.GetEnvironmentVariable("BACKWAVE_ORACLE_SYSTEM_DSN")
        ?? new OracleConnectionStringBuilder(ConnectionString) { UserID = "SYSTEM", Password = "backwave" }.ConnectionString;

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static bool _migrated;

    // FK-safe order: job_parents references jobs without ON DELETE CASCADE, so children go first. Every
    // other child either cascades or is deleted here explicitly, so a plain DELETE per table is enough.
    private const string WipeBlock =
        """
        BEGIN
            DELETE FROM backwave.workflow_edges;
            DELETE FROM backwave.job_parents;
            DELETE FROM backwave.job_tags;
            DELETE FROM backwave.job_transitions;
            DELETE FROM backwave.observer_dead_letters;
            DELETE FROM backwave.observer_deliveries;
            DELETE FROM backwave.observers;
            DELETE FROM backwave.jobs;
            DELETE FROM backwave.workflows;
            DELETE FROM backwave.schedules;
            DELETE FROM backwave.queue_limits;
            DELETE FROM backwave.queue_locks;
            DELETE FROM backwave.operator_audit;
        END;
        """;

    public static async ValueTask<OracleJobStore> CreateFreshStoreAsync(
        JobHistoryPolicy historyPolicy = JobHistoryPolicy.TransitionsAndFailureDetail)
    {
        await Gate.WaitAsync();
        try
        {
            if (!_migrated)
            {
                try
                {
                    await OracleMigrator.MigrateAsync(ConnectionString);
                }
                catch (OracleException exception)
                {
                    throw new InvalidOperationException(
                        "Oracle is not reachable. Start it with: docker compose up -d oracle", exception);
                }
                _migrated = true;
            }

            await using var connection = new OracleConnection(ConnectionString);
            await connection.OpenAsync();
            await using var wipe = connection.CreateCommand();
            wipe.CommandText = WipeBlock;
            await wipe.ExecuteNonQueryAsync();
        }
        finally
        {
            Gate.Release();
        }

        return new OracleJobStore(new OracleStoreOptions
        {
            ConnectionString = ConnectionString,
            HistoryPolicy = historyPolicy,
        });
    }

    // Drops the schema's user (with every object it owns) and recreates it empty, so the next test boots
    // against a genuinely fresh schema. Idempotent: a first run against a never-created user swallows
    // ORA-01918 (user does not exist). RESOURCE plus UNLIMITED TABLESPACE lets a migration create objects.
    public static async Task ResetUserAsync(string schema)
    {
        await using var admin = new OracleConnection(SystemConnectionString);
        await admin.OpenAsync();

        await DropUserCoreAsync(admin, schema);

        await using (var create = admin.CreateCommand())
        {
            create.CommandText = $"CREATE USER {schema} IDENTIFIED BY {schema}";
            await create.ExecuteNonQueryAsync();
        }

        await using var grant = admin.CreateCommand();
        grant.CommandText = $"GRANT CONNECT, RESOURCE, UNLIMITED TABLESPACE TO {schema}";
        await grant.ExecuteNonQueryAsync();
    }

    // Drops the schema's user (with every object it owns), tolerating a not-yet-created user. Used for
    // end-of-suite teardown so provisioned users do not linger in the shared container.
    public static async Task DropUserAsync(string schema)
    {
        await using var admin = new OracleConnection(SystemConnectionString);
        await admin.OpenAsync();
        await DropUserCoreAsync(admin, schema);
    }

    private static async Task DropUserCoreAsync(OracleConnection admin, string schema)
    {
        await using var drop = admin.CreateCommand();
        drop.CommandText = $"DROP USER {schema} CASCADE";

        // After a 16-session storm the prior user's server-side sessions are reaped asynchronously by PMON,
        // so the DROP can briefly raise ORA-01940 (cannot drop a user that is currently connected). Poll a
        // bounded number of times while the sessions clear, then give up rather than loop unbounded.
        const int maxAttempts = 10;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await drop.ExecuteNonQueryAsync();
                return;
            }
            catch (OracleException exception) when (exception.Number == 1918)
            {
                // The user does not exist yet; nothing to drop.
                return;
            }
            catch (OracleException exception) when (exception.Number == 1940 && attempt < maxAttempts)
            {
                // A session is still being reaped; wait briefly and retry the drop.
                await Task.Delay(500);
            }
        }
    }
}

/// <summary>Serializes every Oracle test class - they share one database.</summary>
[CollectionDefinition("oracle")]
public sealed class OracleCollection;
