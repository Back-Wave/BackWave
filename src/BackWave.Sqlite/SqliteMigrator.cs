using System.Globalization;
using System.Reflection;
using Microsoft.Data.Sqlite;

namespace BackWave.Sqlite;

/// <summary>
/// Creates and upgrades the BackWave SQLite schema by running the adapter's versioned schema
/// scripts. The scripts are idempotent, so running them more than once is safe. This is the same
/// work the adapter does when auto-migration is enabled; call it directly from a deployment pipeline
/// when you prefer to control schema changes yourself.
/// </summary>
// The store drives migration/verification itself from EnsureReady (AutoMigrate is sugar over
// MigrateAsync). Mirrors PostgresMigrator / SqlServerMigrator. ADR 0019, issue 0098.
public static class SqliteMigrator
{
    /// <summary>The schema version this build of the adapter requires the database to be at.</summary>
    public const int ExpectedSchemaVersion = 1;

    // 3.35 is the floor that ships UPDATE … RETURNING, which the claim path relies on (ADR 0019).
    internal static readonly Version MinimumEngineVersion = new(3, 35, 0);

    // Wait budget for a second writer to acquire the reserved write lock during migration. Migration
    // is fast, so a generous value simply means a co-resident boot blocks (rather than getting an
    // immediate SQLITE_BUSY) until the first migrator commits and releases the lock. Coordinated
    // migration formalizes SQLite's single-writer lock as the coordination primitive (ADR 0046).
    private const int MigrationBusyTimeoutMs = 30_000;

    /// <summary>
    /// Runs every schema script in version order against the database file, bringing it up to the
    /// version this adapter requires. Enables write-ahead logging on the file as part of the run.
    /// Idempotent — safe to run against an already-current database, so "run it on every deploy" is
    /// a legitimate strategy.
    /// </summary>
    /// <param name="connectionString">The <c>Microsoft.Data.Sqlite</c> connection string for the target database file.</param>
    /// <param name="cancellationToken">Token to cancel the migration.</param>
    /// <returns>A task that completes once every script has run.</returns>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public static Task MigrateAsync(string connectionString, CancellationToken cancellationToken = default)
        => MigrateAsync(connectionString, SchemaRewriter.DefaultPrefix, coordinate: true, cancellationToken);

    /// <summary>
    /// Runs every schema script in version order under a custom table prefix, bringing the database
    /// file up to the version this adapter requires. Use this when the store is configured with a
    /// non-default table prefix so the out-of-band migration provisions the same table names the store
    /// expects. Enables write-ahead logging and is idempotent, exactly like the default-prefix overload.
    /// </summary>
    /// <param name="connectionString">The <c>Microsoft.Data.Sqlite</c> connection string for the target database file.</param>
    /// <param name="tablePrefix">
    /// The prefix on every BackWave table and index — the same value the store is configured with.
    /// Must be a valid identifier root (1–64 characters: a letter or underscore followed by letters,
    /// digits, or underscores); any other value is rejected.
    /// </param>
    /// <param name="coordinate">
    /// When <see langword="true"/> (the default), the migration runs inside a single
    /// <c>BEGIN IMMEDIATE</c> write transaction. If several processes open the same file and migrate at
    /// once, exactly one applies the schema while the others block on SQLite's single-writer write lock,
    /// re-check, and no-op — and the whole migration is atomic (all-or-nothing). For SQLite this write
    /// lock <em>is</em> the coordination: there is no distributed SQLite, so the guarantee is per-host
    /// (across processes sharing the file). Setting it to <see langword="false"/> runs the scripts
    /// without an explicit transaction and is safe only when your deployment already serializes
    /// migration itself.
    /// </param>
    /// <param name="cancellationToken">Token to cancel the migration.</param>
    /// <returns>A task that completes once every script has run.</returns>
    /// <exception cref="ArgumentException"><paramref name="tablePrefix"/> is not a valid identifier root.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public static async Task MigrateAsync(
        string connectionString, string tablePrefix, bool coordinate = true,
        CancellationToken cancellationToken = default)
    {
        var rewriter = new SchemaRewriter(tablePrefix);

        // Migration mutates native, connection-local state — PRAGMA busy_timeout below — and SQLite
        // carries that state with a connection back into the pool. The store forces Pooling=true and
        // shares one pool per connection string, so a pooled migration connection would hand its 30s
        // busy_timeout to the next runtime claim/reap, silently stretching every contended write far
        // past the store's configured BusyTimeout. Run migration on its own non-pooled connection so
        // nothing it sets can leak; the file-level write lock that coordinates migration is unaffected.
        var migrationConnectionString =
            new SqliteConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString;
        await using var connection = new SqliteConnection(migrationConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        // WAL is persisted in the database header, so this need only run once, and it must run
        // outside an explicit transaction. busy_timeout makes a second writer BLOCK on the reserved
        // write lock (up to the budget) instead of getting an immediate SQLITE_BUSY — this is the wait
        // mechanism that lets a co-resident boot queue behind the actual migrator and then re-check.
        // Set explicitly here because MigrateAsync may be called with a raw connection string that the
        // store's connection-string normalizer never touched.
        await using (var pragmas = connection.CreateCommand())
        {
            pragmas.CommandText = $"PRAGMA journal_mode=WAL; PRAGMA busy_timeout={MigrationBusyTimeoutMs};";
            await pragmas.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!coordinate)
        {
            await ApplyScriptsAsync(connection, transaction: null, rewriter, cancellationToken).ConfigureAwait(false);
            return;
        }

        // Check-before-lock (ADR 0046): an already-current file skips the write lock entirely, so the
        // reserved lock is contended only during a genuine first-boot migration window.
        if (await IsSchemaCurrentAsync(connection, transaction: null, rewriter, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        // BEGIN IMMEDIATE grabs SQLite's reserved write lock up front. A second writer racing the same
        // fresh file blocks here (up to busy_timeout) until this connection commits, then proceeds and
        // finds the schema current. Running the whole migration in this one transaction also makes it
        // atomic (all-or-nothing) — a fault mid-run rolls every DDL statement back.
        await using var transaction = (SqliteTransaction)connection.BeginTransaction(deferred: false);

        // Double-checked locking: re-verify inside the write lock so a waiter that queued behind the
        // actual migrator finds the schema current and no-ops instead of re-running the scripts.
        if (!await IsSchemaCurrentAsync(connection, transaction, rewriter, cancellationToken).ConfigureAwait(false))
        {
            await ApplyScriptsAsync(connection, transaction, rewriter, cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    // Runs every embedded schema script in version order on the given connection, optionally inside a
    // transaction. Shared by the coordinated (in-transaction) and opt-out (autocommit) paths. The WAL
    // pragma is intentionally NOT here — it runs once, before, outside any transaction.
    private static async Task ApplyScriptsAsync(
        SqliteConnection connection, SqliteTransaction? transaction, SchemaRewriter rewriter,
        CancellationToken cancellationToken)
    {
        var assembly = typeof(SqliteMigrator).Assembly;
        var scripts = assembly.GetManifestResourceNames()
            .Where(name => name.EndsWith(".sql", StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal);

        foreach (var script in scripts)
        {
            await using var stream = assembly.GetManifestResourceStream(script)!;
            using var reader = new StreamReader(stream);
            var sql = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = rewriter.Rewrite(sql);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    // True when the deployed schema is at ExpectedSchemaVersion. Probes for the version table without
    // relying on catching a "no such table" error — an error inside the migration transaction is not
    // needed, and a plain existence probe keeps the in-lock re-check clean. Returns false for both a
    // missing and a stale schema — either needs the (idempotent) scripts run.
    private static async Task<bool> IsSchemaCurrentAsync(
        SqliteConnection connection, SqliteTransaction? transaction, SchemaRewriter rewriter,
        CancellationToken cancellationToken)
    {
        await using (var probe = connection.CreateCommand())
        {
            probe.Transaction = transaction;
            probe.CommandText = rewriter.Rewrite(
                "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='backwave_schema_version'");
            var exists = (long)(await probe.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
            if (exists == 0)
            {
                return false;
            }
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = rewriter.Rewrite("SELECT version FROM backwave_schema_version LIMIT 1");
        var version = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return version is long deployed && deployed == ExpectedSchemaVersion;
    }

    /// <summary>
    /// Checks that the database is at the schema version this adapter requires. A missing or
    /// mismatched schema throws rather than letting the workers run against a schema they do not
    /// understand and risk corrupting job state.
    /// </summary>
    /// <param name="connectionString">The <c>Microsoft.Data.Sqlite</c> connection string for the target database file.</param>
    /// <param name="cancellationToken">Token to cancel the check.</param>
    /// <returns>A task that completes when the schema is confirmed current.</returns>
    /// <exception cref="InvalidOperationException">
    /// The BackWave schema is missing, or its version does not match the version this adapter
    /// requires.
    /// </exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    // Fail-stop on version skew (ADR-0007): never run against an unknown schema.
    public static Task VerifySchemaVersionAsync(
        string connectionString, CancellationToken cancellationToken = default)
        => VerifySchemaVersionAsync(connectionString, SchemaRewriter.DefaultPrefix, cancellationToken);

    /// <summary>
    /// Checks the schema version under a custom table prefix. Use this when the store is configured
    /// with a non-default table prefix and you provision it out of band. Behaves exactly like the
    /// default-prefix overload, but reads the version from the prefixed table.
    /// </summary>
    /// <param name="connectionString">The <c>Microsoft.Data.Sqlite</c> connection string for the target database file.</param>
    /// <param name="tablePrefix">
    /// The prefix on every BackWave table — the same value the store is configured with. Must be a
    /// valid identifier root (1–64 characters: a letter or underscore followed by letters, digits, or
    /// underscores); any other value is rejected.
    /// </param>
    /// <param name="cancellationToken">Token to cancel the check.</param>
    /// <returns>A task that completes when the schema is confirmed current.</returns>
    /// <exception cref="ArgumentException"><paramref name="tablePrefix"/> is not a valid identifier root.</exception>
    /// <exception cref="InvalidOperationException">
    /// The BackWave schema is missing, or its version does not match the version this adapter requires.
    /// </exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public static async Task VerifySchemaVersionAsync(
        string connectionString, string tablePrefix, CancellationToken cancellationToken = default)
    {
        var rewriter = new SchemaRewriter(tablePrefix);
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = rewriter.Rewrite("SELECT version FROM backwave_schema_version LIMIT 1");

        object? version;
        try
        {
            version = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 1) // SQLITE_ERROR: no such table
        {
            throw new InvalidOperationException(
                "BackWave schema not found. Run SqliteMigrator.MigrateAsync (or opt in to AutoMigrate) before starting work.",
                exception);
        }

        if (version is not long deployed || deployed != ExpectedSchemaVersion)
        {
            throw new InvalidOperationException(
                $"BackWave schema version mismatch: database has {version ?? "none"}, this adapter requires " +
                $"{ExpectedSchemaVersion}. Fail-stopping the Worker Group — version skew must never corrupt job state.");
        }
    }

    // Enforces the engine floor fail-stop: an engine below MinimumEngineVersion lacks RETURNING, so
    // the adapter refuses to start rather than silently degrade the claim path.
    internal static async Task EnsureEngineVersionAsync(
        string connectionString, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT sqlite_version()";
        var raw = (string)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;

        if (!TryParseEngineVersion(raw, out var actual) || actual < MinimumEngineVersion)
        {
            throw new InvalidOperationException(
                $"BackWave's SQLite adapter requires engine version {MinimumEngineVersion} or newer " +
                $"(for UPDATE … RETURNING); this process is linked against {raw}. Fail-stopping the Worker Group.");
        }
    }

    /// <summary>Parses the leading <c>major.minor.patch</c> of a <c>sqlite_version()</c> string.</summary>
    internal static bool TryParseEngineVersion(string raw, out Version version)
    {
        var parts = raw.Split('.');
        var major = parts.Length > 0 && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ma) ? ma : -1;
        var minor = parts.Length > 1 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var mi) ? mi : 0;
        var patch = parts.Length > 2 && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pa) ? pa : 0;

        if (major < 0)
        {
            version = new Version(0, 0);
            return false;
        }

        version = new Version(major, minor, patch);
        return true;
    }
}
