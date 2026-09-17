using System.Reflection;
using BackWave.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace BackWave.Postgres;

/// <summary>
/// Applies the Postgres job-store schema and verifies its version. Use this to provision or
/// upgrade the schema yourself as an explicit deployment step — the alternative to the store's
/// opt-in auto-migrate. The schema scripts are versioned and idempotent, so re-running
/// <see cref="MigrateAsync(NpgsqlDataSource, CancellationToken)"/> against an up-to-date database is a
/// no-op. When a whole fleet cold-boots with auto-migrate on, migration is coordinated so exactly one
/// Node applies the schema at a time — the rest block, re-check, and no-op.
/// </summary>
public static class PostgresMigrator
{
    /// <summary>The schema version this build of the adapter requires.</summary>
    public const int ExpectedSchemaVersion = 1;

    // Reserved advisory-lock classid for migration coordination (ADR 0046). pg_advisory_xact_lock has
    // a two-int32 key space that is DISJOINT from the single-bigint per-queue config lock (issue 0193),
    // so a migration lock and a queue-claim lock can never collide. This constant is LOAD-BEARING and
    // must NEVER change: two binaries that disagreed on it could migrate the same schema concurrently
    // under different keys, defeating the coordination. objid = hashtext(schemaName) keeps independent
    // schemas sharing one database from false-contending. Value is ASCII "BWM1" (BackWave Migration v1).
    private const int MigrationLockClassId = 0x42574D31;

    /// <summary>
    /// Applies every schema script in version order, creating or upgrading the job-store tables.
    /// Idempotent: safe to run on every deploy and harmless against an already-current database.
    /// Migration is coordinated across a cold-booting fleet: exactly one caller applies the schema
    /// while the rest block, re-check, and no-op, and the whole migration is atomic (all-or-nothing).
    /// </summary>
    /// <param name="dataSource">An open data source for the target database. Not disposed here.</param>
    /// <param name="cancellationToken">Cancels the migration.</param>
    /// <returns>A task that completes once every script has run.</returns>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public static Task MigrateAsync(NpgsqlDataSource dataSource, CancellationToken cancellationToken = default)
        => MigrateAsync(dataSource, SchemaRewriter.DefaultSchema, coordinate: true, cancellationToken);

    /// <summary>
    /// Applies every schema script in version order under a custom schema, creating or upgrading the
    /// job-store tables. Use this when the store is configured with a non-default schema name so the
    /// out-of-band migration provisions the same schema the store expects. Idempotent: safe to run on
    /// every deploy and harmless against an already-current database.
    /// </summary>
    /// <param name="dataSource">An open data source for the target database. Not disposed here.</param>
    /// <param name="schemaName">
    /// The schema to create the tables in — the same name the store is configured with. Must be a
    /// valid unqualified identifier (1–63 characters: a letter or underscore followed by letters,
    /// digits, or underscores); any other value is rejected.
    /// </param>
    /// <param name="coordinate">
    /// When <see langword="true"/> (the default), the migration runs under a transaction-scoped
    /// database lock so that when a whole fleet cold-boots at once, exactly one caller applies the
    /// schema and the rest block, re-check, and no-op — the migration is also atomic (all-or-nothing).
    /// Setting it to <see langword="false"/> is a footgun: it restores the unguarded first-migration
    /// race (concurrent first-boot migrators can throw a duplicate-type error) and is safe only when
    /// the deployment already serializes migration itself (a single migration job, or one Node booting
    /// before the rest).
    /// </param>
    /// <param name="cancellationToken">Cancels the migration.</param>
    /// <returns>A task that completes once every script has run.</returns>
    /// <exception cref="ArgumentException"><paramref name="schemaName"/> is not a valid identifier.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public static async Task MigrateAsync(
        NpgsqlDataSource dataSource, string schemaName, bool coordinate = true,
        CancellationToken cancellationToken = default)
    {
        var rewriter = new SchemaRewriter(schemaName);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        if (!coordinate)
        {
            await ApplyScriptsAsync(connection, transaction: null, rewriter, cancellationToken).ConfigureAwait(false);
            return;
        }

        // Check-before-lock (ADR 0046): an already-current schema skips the lock entirely, so the
        // migration lock is contended only during a genuine first-boot migration window.
        if (await IsSchemaCurrentAsync(connection, transaction: null, rewriter, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        // Exactly one Node migrates at a time. pg_advisory_xact_lock is transaction-scoped, so it
        // auto-releases on commit/rollback/disconnect — no leak even if a migrator crashes mid-run —
        // and blocks every waiter (honoring the token, no artificial timeout) until it frees. Running
        // the whole migration in this one transaction also makes it atomic.
        //
        // NOTE (ADR 0046): a transaction-scoped lock structurally forbids non-transactional DDL. The
        // day a migration wants CREATE INDEX CONCURRENTLY or an online rebuild, that becomes a
        // per-script property (that script opts into a session-scoped lock), decided when it is written.
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var lockCommand = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(@classid, hashtext(@schema))", connection, transaction))
        {
            lockCommand.Parameters.AddWithValue("classid", MigrationLockClassId);
            lockCommand.Parameters.AddWithValue("schema", schemaName);
            // uncounted round trip: taking the migration advisory lock is part of provisioning the
            // schema, not of any store operation. It runs once at startup on the migrator's own
            // connection, before there is an operation in flight to charge it to.
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

    // Runs every embedded schema script in version order on the given connection, optionally inside a
    // transaction. Shared by the coordinated (in-transaction) and opt-out (autocommit) paths.
    private static async Task ApplyScriptsAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, SchemaRewriter rewriter,
        CancellationToken cancellationToken)
    {
        var assembly = typeof(PostgresMigrator).Assembly;
        var scripts = assembly.GetManifestResourceNames()
            .Where(name => name.EndsWith(".sql", StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal);

        foreach (var script in scripts)
        {
            using var stream = assembly.GetManifestResourceStream(script)!;
            using var reader = new StreamReader(stream);
            var sql = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

            await using var command = new NpgsqlCommand(rewriter.Rewrite(sql), connection, transaction);
            // uncounted round trip: migration runs once at startup, on its own connection, before any
            // store operation exists to charge it to. Its cost is a fixed price for provisioning the
            // schema rather than a per-operation cost, which is the only thing the budgets watch.
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    // True when the deployed schema is at ExpectedSchemaVersion or newer. Uses to_regclass to probe for
    // the table WITHOUT raising: a missing-table error inside the migration transaction would poison it
    // (Postgres aborts a transaction on the first error), so the in-lock re-check must never throw for
    // a not-yet-created schema. Returns false for both a missing and a stale schema - either needs the
    // (idempotent) scripts run. A newer schema is current for this build: every script it ships is
    // already applied, and re-running them would stamp the version back down under the nodes that need it.
    private static async Task<bool> IsSchemaCurrentAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, SchemaRewriter rewriter,
        CancellationToken cancellationToken)
    {
        await using (var probe = new NpgsqlCommand(
            rewriter.Rewrite("SELECT to_regclass('backwave.schema_version')::text"), connection, transaction))
        {
            // uncounted round trip: the schema-current probe runs only inside MigrateAsync, on the
            // migrator's own connection and inside the migration transaction. No operation is measured
            // through it.
            var exists = await probe.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (exists is null or DBNull)
            {
                return false;
            }
        }

        await using var command = new NpgsqlCommand(
            rewriter.Rewrite("SELECT version FROM backwave.schema_version LIMIT 1"), connection, transaction);
        // uncounted round trip: the second half of the same schema-current probe, on the migrator's
        // own connection inside the migration transaction.
        var version = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return version is int deployed && deployed >= ExpectedSchemaVersion;
    }

    /// <summary>
    /// Checks that the database's deployed schema version satisfies the version this adapter build
    /// requires. The store calls this on first use and refuses to run on a missing or older schema, so
    /// version skew can never corrupt job state - call it yourself if you provision the schema out of
    /// band and want to fail fast before starting work. A newer schema is accepted, because every
    /// BackWave migration is additive: an older node keeps running through a rolling upgrade.
    /// </summary>
    /// <param name="dataSource">An open data source for the target database. Not disposed here.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>A task that completes when the deployed version matches the required version.</returns>
    /// <exception cref="InvalidOperationException">
    /// The schema is missing (no tables found), or the deployed version is older than the version
    /// this adapter build requires.
    /// </exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public static Task VerifySchemaVersionAsync(
        NpgsqlDataSource dataSource, CancellationToken cancellationToken = default)
        => VerifySchemaVersionAsync(dataSource, SchemaRewriter.DefaultSchema, cancellationToken);

    /// <summary>
    /// Checks the deployed schema version under a custom schema. Use this when the store is configured
    /// with a non-default schema name and you provision it out of band. Behaves exactly like the
    /// default-schema overload, but reads the version from the named schema.
    /// </summary>
    /// <param name="dataSource">An open data source for the target database. Not disposed here.</param>
    /// <param name="schemaName">
    /// The schema the tables live in — the same name the store is configured with. Must be a valid
    /// unqualified identifier (1–63 characters: a letter or underscore followed by letters, digits, or
    /// underscores); any other value is rejected.
    /// </param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>A task that completes when the deployed version matches the required version.</returns>
    /// <exception cref="ArgumentException"><paramref name="schemaName"/> is not a valid identifier.</exception>
    /// <exception cref="InvalidOperationException">
    /// The schema is missing (no tables found), or the deployed version is older than the version
    /// this adapter build requires.
    /// </exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public static Task VerifySchemaVersionAsync(
        NpgsqlDataSource dataSource, string schemaName, CancellationToken cancellationToken = default)
        => VerifySchemaVersionAsync(dataSource, schemaName, NullLogger.Instance, cancellationToken);

    // The store's entry point: the same check, with the store's logger to name a newer schema on.
    internal static async Task VerifySchemaVersionAsync(
        NpgsqlDataSource dataSource, string schemaName, ILogger logger, CancellationToken cancellationToken)
    {
        var rewriter = new SchemaRewriter(schemaName);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            rewriter.Rewrite("SELECT version FROM backwave.schema_version LIMIT 1"), connection);

        object? version;
        try
        {
            // uncounted round trip: the schema-version check is the one-time startup probe the budgets
            // already exclude by name, on a connection of its own that no operation is measured through.
            version = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            throw new InvalidOperationException(
                "BackWave schema not found. Apply the schema (or opt in to AutoMigrate) before starting work.",
                exception);
        }

        if (version is not int deployed || deployed < ExpectedSchemaVersion)
        {
            throw new InvalidOperationException(
                $"BackWave schema version mismatch: database has {version ?? "none"}, this adapter requires " +
                $"{ExpectedSchemaVersion}. Refusing to start — version skew must never corrupt job state.");
        }
        if (deployed > ExpectedSchemaVersion)
        {
            // The N-1 window of a rolling upgrade: a newer schema is additive by contract, so this older
            // build keeps running on it rather than fail-stopping every node the upgrade has not reached.
            BackWaveLog.SchemaNewerThanAdapter(logger, deployed, ExpectedSchemaVersion);
        }
    }
}
