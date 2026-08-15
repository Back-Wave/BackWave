using BackWave.Storage;
using Microsoft.Extensions.Logging;

namespace BackWave.Postgres;

/// <summary>
/// Configuration for the Postgres-backed job store. Supply at least
/// <see cref="ConnectionString"/>; the rest have production-safe defaults.
/// </summary>
public sealed record PostgresStoreOptions
{
    /// <summary>
    /// The Npgsql connection string the store opens its own connection pool from. Required —
    /// it must point at a database the store has rights to read, write, and (if
    /// <see cref="AutoMigrate"/> is on) create its schema in.
    /// </summary>
    public required string ConnectionString { get; init; }

    /// <summary>
    /// When <see langword="true"/>, the store runs its versioned schema scripts on first use so the
    /// tables exist before any job is stored. Convenient in development and single-deployment apps.
    /// Defaults to <see langword="false"/>: production pipelines typically apply the same scripts
    /// themselves as a deliberate migration step. Either way, a schema-version mismatch stops the
    /// worker before any job state can be corrupted, rather than running against a schema it does
    /// not understand.
    /// </summary>
    public bool AutoMigrate { get; init; }

    /// <summary>
    /// When <see langword="true"/> (the default), auto-migration is coordinated across a cold-booting
    /// fleet: exactly one Node applies the schema at a time while the rest block, re-check, and no-op,
    /// and the migration runs atomically (all-or-nothing) under a transaction-scoped database lock.
    /// This makes a fleet that boots with <see cref="AutoMigrate"/> on safe under true concurrency —
    /// without it, several Nodes race to apply the schema and a genuine first-boot race can throw a
    /// duplicate-type error even though the scripts use <c>IF NOT EXISTS</c>. Turning it off is a
    /// footgun: it restores that race and is safe only when your deployment already serializes
    /// migration itself (for example a dedicated migration step, or a single Node that boots before the
    /// rest). Applies only when <see cref="AutoMigrate"/> is on; has no effect otherwise.
    /// </summary>
    public bool CoordinateMigration { get; init; } = true;

    /// <summary>
    /// The Postgres schema that holds every BackWave table, index, and sequence. Defaults to
    /// <c>backwave</c>. Change it to place the job store under a schema that fits your naming
    /// conventions or to keep it clear of other applications' objects in a shared database. The same
    /// name must be used everywhere the store touches this database — auto-migrate, out-of-band
    /// migration, and every query all read it from here — so set it once and leave it fixed for the
    /// life of the data. Must be a valid unqualified identifier: 1–63 characters, a letter or
    /// underscore followed by letters, digits, or underscores; any other value is rejected when the
    /// store is created. Postgres folds unquoted identifiers to lower case, so <c>Jobs</c> and
    /// <c>jobs</c> name the same schema.
    /// </summary>
    public string SchemaName { get; init; } = "backwave";

    /// <summary>
    /// The storage size limits the store enforces — maximum payload size, wire-name length, claim
    /// batch, parents per job, retained history per job, and failure-detail length. Defaults to the
    /// framework's standard bounds, which suit most applications; raise or lower them to match your
    /// payload sizes and retention needs.
    /// </summary>
    public StoreBounds Bounds { get; init; } = StoreBounds.Default;

    /// <summary>
    /// How much per-job history the store records: the full transition timeline with failure detail,
    /// transitions only, or nothing. Controls writes only — never the schema — so changing it is a
    /// configuration change, not a migration. Defaults to recording the full timeline with failure
    /// detail, so the dashboard's per-job history works out of the box. Lower it to trim write volume
    /// and storage when you do not need the timeline. Set the matching value on your BackWave
    /// registration so the read side reports the same policy.
    /// </summary>
    public JobHistoryPolicy HistoryPolicy { get; init; } = JobHistoryPolicy.TransitionsAndFailureDetail;

    /// <summary>
    /// Optional logger factory. When supplied and <see cref="AutoMigrate"/> is on, the store records a
    /// schema-migration event at Information level after applying the schema on first use. Null (the
    /// default) disables that log with no allocation and does not otherwise affect the store.
    /// </summary>
    public ILoggerFactory? LoggerFactory { get; init; }

    /// <summary>
    /// A test-only hook for fault-injection; leave <see langword="null"/> (the default) in
    /// production. When set, the store invokes it at a named point partway through a multi-step
    /// write, inside the open transaction and before commit. Throwing from the hook aborts that
    /// transaction, which must roll back every step — letting a test prove the store's writes are
    /// all-or-nothing. The argument is the named point reached (for example <c>"claim"</c> or
    /// <c>"enqueue"</c>).
    /// </summary>
    // Internal: this is a test-affordance for the conformance/unit suites (issue 0034), not a
    // consumer API; granted to BackWave.Postgres.Tests via [InternalsVisibleTo].
    internal Func<string, CancellationToken, Task>? FaultHook { get; init; }
}
