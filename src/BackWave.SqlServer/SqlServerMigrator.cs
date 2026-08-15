using Microsoft.Data.SqlClient;

namespace BackWave.SqlServer;

/// <summary>
/// Creates and upgrades the BackWave SQL Server schema by running the adapter's versioned schema
/// scripts. The scripts are idempotent, so running them more than once is safe. This is the same
/// work the adapter does when auto-migration is enabled; call it directly from a deployment pipeline
/// when you prefer to control schema changes yourself. When a whole fleet cold-boots with
/// auto-migrate on, migration is coordinated so exactly one Node applies the schema at a time — the
/// rest block, re-check the version, and no-op.
/// </summary>
public static class SqlServerMigrator
{
    /// <summary>The schema version this build of the adapter requires the database to be at.</summary>
    public const int ExpectedSchemaVersion = 1;

    /// <summary>
    /// Runs every schema script in version order, bringing the database up to the version this
    /// adapter requires. Idempotent — safe to run against an already-current database. Migration is
    /// coordinated across a cold-booting fleet: exactly one caller applies the schema while the rest
    /// block, re-check, and no-op, and the whole migration is atomic (all-or-nothing). Transient
    /// connection faults from a fleet cold-boot (a connection-handshake storm against a cold pool) are
    /// retried internally with a bounded backoff, so a booting node survives them without the caller
    /// retrying; a fault that persists past those attempts propagates.
    /// </summary>
    /// <param name="connectionString">The ADO.NET connection string for the target database.</param>
    /// <param name="cancellationToken">Token to cancel the migration.</param>
    /// <returns>A task that completes once every script has run.</returns>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public static Task MigrateAsync(string connectionString, CancellationToken cancellationToken = default)
        => MigrateAsync(connectionString, SchemaRewriter.DefaultSchema, coordinate: true, cancellationToken);

    /// <summary>
    /// Runs every schema script in version order under a custom schema, bringing the database up to
    /// the version this adapter requires. Use this when the store is configured with a non-default
    /// schema name so the out-of-band migration provisions the same schema the store expects.
    /// Idempotent — safe to run against an already-current database.
    /// </summary>
    /// <param name="connectionString">The ADO.NET connection string for the target database.</param>
    /// <param name="schemaName">
    /// The schema to create the tables in — the same name the store is configured with. Must be a
    /// valid unqualified identifier (1–128 characters: a letter or underscore followed by letters,
    /// digits, or underscores); any other value is rejected.
    /// </param>
    /// <param name="coordinate">
    /// When <see langword="true"/> (the default), the migration runs under a transaction-scoped
    /// database lock so that when a whole fleet cold-boots at once, exactly one caller applies the
    /// schema and the rest block, re-check, and no-op — the migration is also atomic (all-or-nothing).
    /// Setting it to <see langword="false"/> is a footgun: it restores the unguarded first-migration
    /// race (concurrent first-boot migrators can throw a duplicate-object error) and is safe only when
    /// the deployment already serializes migration itself (a single migration job, or one Node booting
    /// before the rest).
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
            // Opt-out path: documented as caller-serialized, so a fault here is a real failure to
            // surface — no transient retry, unlike the coordinated path below.
            await using var opt = new SqlConnection(connectionString);
            await opt.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ApplyScriptsAsync(opt, transaction: null, rewriter, cancellationToken).ConfigureAwait(false);
            return;
        }

        // Cold-boot resilience. When a whole fleet cold-boots at once, a booting Node can hit TRANSIENT
        // faults it did nothing to cause: (1) the barrier fires many simultaneous TLS pre-login
        // handshakes at a cold connection pool, and SQL Server's logon path transiently rejects some of
        // the burst with 18456; (2) the RCSI preamble's ROLLBACK IMMEDIATE server-kills idle pooled
        // app-DB connections left by the pre-check, so a later open can be handed a dead connection and
        // throw a transport-level error (ClearPool inside RunCoordinatedMigrationAsync evicts these, but
        // the retry backstops any that slip through). Because the whole coordinated sequence is
        // idempotent — unlocked pre-check, in-lock double-check, and IF EXISTS-guarded RCSI — re-running
        // it is safe, so ride out a bounded number of transient faults instead of crashing startup and
        // forcing the caller to retry. On exhaustion the last fault propagates (fail-stop preserved).
        const int maxAttempts = 8;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await RunCoordinatedMigrationAsync(connectionString, schemaName, rewriter, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
            catch (SqlException exception) when (attempt < maxAttempts && IsTransient(exception))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50 * attempt), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    // The coordinated migration sequence, re-runnable end-to-end by the bounded transient retry in
    // MigrateAsync: pre-check → EnableSnapshotIsolation → ClearPool → lock → apply → commit. Every step
    // is idempotent, so a retry after a transient cold-boot fault converges to the same result.
    private static async Task RunCoordinatedMigrationAsync(
        string connectionString, string schemaName, SchemaRewriter rewriter, CancellationToken cancellationToken)
    {
        // Check-before-lock: an already-current schema skips ALL coordination. It is safe to probe this
        // unlocked because a current schema implies RCSI is already on, so no concurrent RCSI ALTER can
        // be running to kill this probe's connection. Any connection failure here is treated as "needs
        // migration" — the authoritative coordinated path below re-validates and surfaces a genuine,
        // persistent error rather than swallowing it.
        if (await IsSchemaCurrentUnlockedAsync(connectionString, rewriter, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        // Read-committed snapshot isolation is a DATABASE setting the store requires, enabled by
        // ALTER DATABASE — which is NOT allowed inside a transaction (so it cannot ride the migration
        // transaction below) and which requires EXCLUSIVE database access (WITH ROLLBACK IMMEDIATE kills
        // every other connection to the database to get it). Enable it here, outside the transaction and
        // serialized across the fleet by a tempdb-scoped applock so that during the one ALTER no sibling
        // holds an ACTIVE app-DB connection to be killed (ADR 0046: non-transactional DDL takes a
        // session-scoped lock — here a server-wide one in tempdb because the lock must outlive the app-DB
        // connection). Guarded + idempotent: it runs — and kills nothing — at most once, then every later
        // boot skips it.
        await EnableSnapshotIsolationAsync(connectionString, cancellationToken).ConfigureAwait(false);

        // The ALTER above still server-kills IDLE POOLED app-DB connections — the unlocked pre-check
        // opened and disposed one, returning it to this process's pool. Evict them so the open below gets
        // a fresh physical connection instead of a dead one (which would throw a transport-level error).
        // This clears only THIS Node's pool, but every Node runs EnableSnapshotIsolationAsync in its own
        // process, so each evicts its own stale pre-check connection — correct fleet-wide. Any killed
        // connection that still slips through is caught by MigrateAsync's bounded transient retry.
        using var probe = new SqlConnection(connectionString);
        SqlConnection.ClearPool(probe);

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        // Exactly one Node migrates at a time. sp_getapplock with LockOwner='Transaction' is
        // transaction-scoped, so it auto-releases on commit/rollback/disconnect — no leak even if a
        // migrator crashes mid-run — and, with the default -1 timeout, blocks every waiter (honoring
        // the token via ExecuteNonQueryAsync, no artificial @LockTimeout) until it frees. Running the
        // whole migration in this one transaction also makes it atomic. The resource is namespaced
        // away from the per-queue config lock and schema-suffixed so independent schemas sharing one
        // database don't false-contend; @schema is validated to a strict identifier by SchemaRewriter,
        // so composing it into the resource literal is safe.
        //
        // NOTE: a transaction-scoped lock structurally forbids non-transactional DDL. The day a
        // migration wants an online index rebuild, that becomes a per-script property (that script opts
        // into a session-scoped lock), decided when it is written — revisit per ADR 0046.
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var lockCommand = new SqlCommand(
            "DECLARE @resource nvarchar(255) = N'BackWave:migration:' + @schema; " +
            "EXEC sp_getapplock @Resource = @resource, @LockMode = 'Exclusive', @LockOwner = 'Transaction';",
            connection, transaction))
        {
            lockCommand.Parameters.AddWithValue("schema", schemaName);
            await lockCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // Double-checked locking: re-verify inside the lock so a waiter that queued behind the actual
        // migrator finds the schema current and no-ops instead of re-running the scripts.
        if (!await IsSchemaCurrentAsync(connection, transaction, rewriter, cancellationToken).ConfigureAwait(false))
        {
            await ApplyScriptsAsync(connection, transaction, rewriter, cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    // Transient SQL faults a cold-booting fleet can hit that the bounded retry should ride out rather
    // than surface. The number set:
    //   18456                    login failed — here a handshake-storm artifact when many Nodes hit a
    //                            cold connection pool at once, not an auth failure
    //   -2                       command timeout
    //   53 / 121 / 233 /
    //   10053 / 10054            transport-level / connection-reset errors (e.g. a pooled connection the
    //                            RCSI ROLLBACK IMMEDIATE server-killed)
    //   40197 / 40501 /
    //   40613 / 4060             Azure SQL throttling / unavailable- or unable-to-open-database transients
    private static bool IsTransient(SqlException exception) => exception.Number is
        18456 or -2 or 53 or 121 or 233 or 10053 or 10054 or 40197 or 40501 or 40613 or 4060;

    // Runs every embedded schema script in version order on the given connection, optionally inside a
    // transaction. Shared by the coordinated (in-transaction) and opt-out (autocommit) paths. Each
    // script is a single batch — the SqlServer schema has no GO separators — so one ExecuteNonQuery
    // per script is correct.
    private static async Task ApplyScriptsAsync(
        SqlConnection connection, SqlTransaction? transaction, SchemaRewriter rewriter,
        CancellationToken cancellationToken)
    {
        var assembly = typeof(SqlServerMigrator).Assembly;
        var scripts = assembly.GetManifestResourceNames()
            .Where(name => name.EndsWith(".sql", StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal);

        foreach (var script in scripts)
        {
            using var stream = assembly.GetManifestResourceStream(script)!;
            using var reader = new StreamReader(stream);
            var sql = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

            await using var command = new SqlCommand(rewriter.Rewrite(sql), connection, transaction);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    // Enables read-committed snapshot isolation on the target database if it is not already on. RCSI is
    // enabled by ALTER DATABASE, which SQL Server forbids inside a transaction (so it cannot ride the
    // migration transaction) and which needs EXCLUSIVE database access — WITH ROLLBACK IMMEDIATE kills
    // every other connection to the database to obtain it. To make a concurrent fleet cold-boot safe,
    // the fleet serializes here on a SERVER-WIDE applock taken in tempdb (reachable by every login with
    // no elevated rights): a waiter blocks holding ONLY its tempdb connection — it has not yet opened an
    // app-DB connection — so when the lock holder runs the ALTER, no sibling holds an ACTIVE app-DB
    // connection for ROLLBACK IMMEDIATE to kill. It CAN still kill IDLE POOLED app-DB connections that
    // the earlier unlocked pre-check opened and disposed (dispose returns a connection to the pool, it
    // does not close it server-side); the caller evicts those with SqlConnection.ClearPool right after
    // this returns, and MigrateAsync's bounded transient retry backstops any killed connection that slips
    // through. The holder opens a short-lived app-DB connection JUST for the guarded ALTER, then closes
    // it. Idempotent: the ALTER runs (and kills nothing) at most once; every
    // later fleet member finds RCSI on and skips it. The resource is keyed by the target database name so
    // migrations of different databases on one server never false-serialize.
    private static async Task EnableSnapshotIsolationAsync(
        string connectionString, CancellationToken cancellationToken)
    {
        var targetDatabase = new SqlConnectionStringBuilder(connectionString).InitialCatalog;
        var tempdbConnectionString =
            new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "tempdb" }.ConnectionString;

        var resource = "BackWave:migration:rcsi:" + targetDatabase;
        await using var coordinator = new SqlConnection(tempdbConnectionString);
        await coordinator.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (var acquire = new SqlCommand(
            "EXEC sp_getapplock @Resource = @resource, @LockMode = 'Exclusive', @LockOwner = 'Session';",
            coordinator))
        {
            acquire.Parameters.AddWithValue("resource", resource);
            await acquire.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            // Only the lock holder reaches here; siblings block on the acquire above with no app-DB connection.
            await using var appDb = new SqlConnection(connectionString);
            await appDb.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var alter = new SqlCommand(
                "IF EXISTS (SELECT 1 FROM sys.databases " +
                "           WHERE database_id = DB_ID() AND is_read_committed_snapshot_on = 0) " +
                "ALTER DATABASE CURRENT SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;",
                appDb);
            await alter.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Release explicitly — a Session-scoped applock is NOT freed when a POOLED connection is
            // disposed (it lingers until the connection is reset on reuse), which would stall the next
            // fleet member. Freeing it here hands the lock over promptly.
            await using var release = new SqlCommand(
                "EXEC sp_releaseapplock @Resource = @resource, @LockOwner = 'Session';", coordinator);
            release.Parameters.AddWithValue("resource", resource);
            await release.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    // The unlocked, fast-path schema-version probe used before any coordination. Opens its own
    // connection so a concurrent RCSI ALTER that kills it surfaces as a caught SqlException → "not
    // current", never a thrown migration failure; the coordinated path re-validates authoritatively.
    private static async Task<bool> IsSchemaCurrentUnlockedAsync(
        string connectionString, SchemaRewriter rewriter, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return await IsSchemaCurrentAsync(connection, transaction: null, rewriter, cancellationToken).ConfigureAwait(false);
        }
        catch (SqlException)
        {
            return false;
        }
    }

    // True when the deployed schema is at ExpectedSchemaVersion. Probes for the table with OBJECT_ID
    // WITHOUT raising: a missing-table error inside the migration transaction would be messy, so the
    // in-lock re-check must never throw for a not-yet-created schema. Returns false for both a missing
    // and a stale schema — either needs the (idempotent) scripts run.
    private static async Task<bool> IsSchemaCurrentAsync(
        SqlConnection connection, SqlTransaction? transaction, SchemaRewriter rewriter,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            rewriter.Rewrite(
                "IF OBJECT_ID('backwave.schema_version', 'U') IS NULL SELECT CAST(NULL AS int); " +
                "ELSE SELECT TOP 1 version FROM backwave.schema_version;"),
            connection, transaction);
        var version = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return version is int deployed && deployed == ExpectedSchemaVersion;
    }

    /// <summary>
    /// Checks that the database is at the schema version this adapter requires. A missing or
    /// mismatched schema throws rather than letting the workers run against a schema they do not
    /// understand and risk corrupting job state.
    /// </summary>
    /// <param name="connectionString">The ADO.NET connection string for the target database.</param>
    /// <param name="cancellationToken">Token to cancel the check.</param>
    /// <returns>A task that completes when the schema is confirmed current.</returns>
    /// <exception cref="InvalidOperationException">
    /// The BackWave schema is missing, or its version does not match the version this adapter
    /// requires.
    /// </exception>
    // Fail-stop on version skew (ADR-0007): never run against an unknown schema.
    public static Task VerifySchemaVersionAsync(
        string connectionString, CancellationToken cancellationToken = default)
        => VerifySchemaVersionAsync(connectionString, SchemaRewriter.DefaultSchema, cancellationToken);

    /// <summary>
    /// Checks the deployed schema version under a custom schema. Use this when the store is configured
    /// with a non-default schema name and you provision it out of band. Behaves exactly like the
    /// default-schema overload, but reads the version from the named schema.
    /// </summary>
    /// <param name="connectionString">The ADO.NET connection string for the target database.</param>
    /// <param name="schemaName">
    /// The schema the tables live in — the same name the store is configured with. Must be a valid
    /// unqualified identifier (1–128 characters: a letter or underscore followed by letters, digits, or
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
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(
            rewriter.Rewrite("SELECT TOP 1 version FROM backwave.schema_version"), connection);

        object? version;
        try
        {
            version = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqlException exception) when (exception.Number == 208) // invalid object name
        {
            throw new InvalidOperationException(
                "BackWave schema not found. Apply src/BackWave.SqlServer/Schema/*.sql (or opt in to AutoMigrate).",
                exception);
        }

        if (version is not int deployed || deployed != ExpectedSchemaVersion)
        {
            throw new InvalidOperationException(
                $"BackWave schema version mismatch: database has {version ?? "none"}, this adapter requires " +
                $"{ExpectedSchemaVersion}. Fail-stopping the Worker Group — version skew must never corrupt job state.");
        }
    }
}
