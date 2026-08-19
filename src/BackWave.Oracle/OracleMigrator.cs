using Oracle.ManagedDataAccess.Client;

namespace BackWave.Oracle;

/// <summary>
/// Creates and upgrades the BackWave Oracle schema by running the adapter's versioned schema scripts.
/// The scripts are idempotent, so running them more than once is safe. This is the same work the adapter
/// does when auto-migration is enabled; call it directly from a deployment pipeline when you prefer to
/// control schema changes yourself. When a whole fleet cold-boots with auto-migrate on, no database lock
/// is needed: every object is created through a guarded block that swallows the duplicate-object and
/// duplicate-key races, and Oracle serializes DDL on each object inside the database, so concurrent
/// migrators converge on the same schema.
/// </summary>
public static class OracleMigrator
{
    /// <summary>The schema version this build of the adapter requires the database to be at.</summary>
    public const int ExpectedSchemaVersion = 1;

    // Transient connection faults a cold-booting fleet can hit that the bounded retry should ride out
    // rather than surface: the shared listener/handshake-storm and connection-lost connectivity set.
    private static bool IsTransient(OracleException exception)
        => OracleFaultCodes.IsConnectivityFault(exception.Number);

    /// <summary>
    /// Runs every schema script in version order, bringing the database up to the version this adapter
    /// requires. Idempotent - safe to run against an already-current database. Transient connection faults
    /// from a fleet cold-boot are retried internally with a bounded backoff; a fault that persists past
    /// those attempts propagates.
    /// </summary>
    /// <param name="connectionString">The ODP.NET connection string for the target database.</param>
    /// <param name="cancellationToken">Token to cancel the migration.</param>
    /// <returns>A task that completes once every script has run.</returns>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public static Task MigrateAsync(string connectionString, CancellationToken cancellationToken = default)
        => MigrateAsync(connectionString, SchemaRewriter.DefaultSchema, coordinate: true, cancellationToken);

    /// <summary>
    /// Runs every schema script in version order under a custom schema, bringing the database up to the
    /// version this adapter requires. Use this when the store is configured with a non-default schema name
    /// so the out-of-band migration provisions the same schema the store expects. Idempotent - safe to run
    /// against an already-current database.
    /// </summary>
    /// <param name="connectionString">The ODP.NET connection string for the target database.</param>
    /// <param name="schemaName">
    /// The schema to create the tables in - the same name the store is configured with. Must be a valid
    /// unqualified identifier (1-128 characters: a letter or underscore followed by letters, digits, or
    /// underscores); any other value is rejected.
    /// </param>
    /// <param name="coordinate">
    /// When <see langword="true"/> (the default), a cold-booting fleet rides out the transient connection
    /// faults of a first-boot handshake storm with a bounded retry. The Oracle schema script is idempotent
    /// and concurrency-safe on its own, so no database lock is taken either way. Setting it to
    /// <see langword="false"/> runs the script once with no transient retry; it is safe when the deployment
    /// already serializes migration itself.
    /// </param>
    /// <param name="cancellationToken">Token to cancel the migration.</param>
    /// <returns>A task that completes once every script has run.</returns>
    /// <exception cref="ArgumentException"><paramref name="schemaName"/> is not a valid identifier.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public static async Task MigrateAsync(
        string connectionString, string schemaName, bool coordinate = true,
        CancellationToken cancellationToken = default)
    {
        var rewriter = new SchemaRewriter(schemaName);

        if (!coordinate)
        {
            // Opt-out path: documented as caller-serialized, so a fault here is a real failure to surface -
            // no transient retry, unlike the coordinated path below.
            await ApplyScriptsAsync(connectionString, rewriter, cancellationToken).ConfigureAwait(false);
            return;
        }

        // Cold-boot resilience. When a whole fleet cold-boots at once, a booting node can hit TRANSIENT
        // listener/handshake faults it did nothing to cause. The schema script is idempotent and
        // concurrency-safe by construction (guarded DDL + DDL serialized on each object by Oracle), so
        // re-running it converges; ride out a bounded number of transient faults instead of crashing
        // startup. On exhaustion the last fault propagates (fail-stop preserved).
        const int maxAttempts = 8;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await ApplyScriptsAsync(connectionString, rewriter, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (OracleException exception) when (attempt < maxAttempts && IsTransient(exception))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50 * attempt), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    // Runs every embedded schema script in version order. Each script is a single anonymous PL/SQL block
    // (guarded object-by-object), so one ExecuteNonQuery per script is correct and Oracle's implicit DDL
    // commit needs no explicit transaction.
    private static async Task ApplyScriptsAsync(
        string connectionString, SchemaRewriter rewriter, CancellationToken cancellationToken)
    {
        await using var connection = new OracleConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var assembly = typeof(OracleMigrator).Assembly;
        var scripts = assembly.GetManifestResourceNames()
            .Where(name => name.EndsWith(".sql", StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal);

        foreach (var script in scripts)
        {
            await using var stream = assembly.GetManifestResourceStream(script)!;
            using var reader = new StreamReader(stream);
            var sql = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText = rewriter.Rewrite(sql);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Checks that the database is at the schema version this adapter requires. A missing or mismatched
    /// schema throws rather than letting the workers run against a schema they do not understand and risk
    /// corrupting job state.
    /// </summary>
    /// <param name="connectionString">The ODP.NET connection string for the target database.</param>
    /// <param name="cancellationToken">Token to cancel the check.</param>
    /// <returns>A task that completes when the schema is confirmed current.</returns>
    /// <exception cref="InvalidOperationException">
    /// The BackWave schema is missing, or its version does not match the version this adapter requires.
    /// </exception>
    // Fail-stop on version skew: never run against an unknown schema.
    public static Task VerifySchemaVersionAsync(
        string connectionString, CancellationToken cancellationToken = default)
        => VerifySchemaVersionAsync(connectionString, SchemaRewriter.DefaultSchema, cancellationToken);

    /// <summary>
    /// Checks the deployed schema version under a custom schema. Use this when the store is configured
    /// with a non-default schema name and you provision it out of band. Behaves exactly like the
    /// default-schema overload, but reads the version from the named schema.
    /// </summary>
    /// <param name="connectionString">The ODP.NET connection string for the target database.</param>
    /// <param name="schemaName">
    /// The schema the tables live in - the same name the store is configured with. Must be a valid
    /// unqualified identifier (1-128 characters: a letter or underscore followed by letters, digits, or
    /// underscores); any other value is rejected.
    /// </param>
    /// <param name="cancellationToken">Token to cancel the check.</param>
    /// <returns>A task that completes when the schema is confirmed current.</returns>
    /// <exception cref="ArgumentException"><paramref name="schemaName"/> is not a valid identifier.</exception>
    /// <exception cref="InvalidOperationException">
    /// The BackWave schema is missing, or its version does not match the version this adapter requires.
    /// </exception>
    public static async Task VerifySchemaVersionAsync(
        string connectionString, string schemaName, CancellationToken cancellationToken = default)
    {
        var rewriter = new SchemaRewriter(schemaName);
        await using var connection = new OracleConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = rewriter.Rewrite(
            "SELECT version FROM backwave.schema_version FETCH FIRST 1 ROW ONLY");

        object? version;
        try
        {
            version = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OracleException exception) when (exception.Number == 942) // table or view does not exist
        {
            throw new InvalidOperationException(
                "BackWave schema not found. Apply src/BackWave.Oracle/Schema/*.sql (or opt in to AutoMigrate).",
                exception);
        }

        // Oracle returns NUMBER as decimal; normalize before comparing to the expected version.
        var deployed = version is null ? (int?)null : Convert.ToInt32(version);
        if (deployed != ExpectedSchemaVersion)
        {
            throw new InvalidOperationException(
                $"BackWave schema version mismatch: database has {deployed?.ToString() ?? "none"}, this adapter " +
                $"requires {ExpectedSchemaVersion}. Fail-stopping the Worker Group - version skew must never " +
                "corrupt job state.");
        }
    }
}
