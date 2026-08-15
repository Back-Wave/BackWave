using BackWave.Storage;
using Microsoft.Extensions.Logging;

namespace BackWave.SqlServer;

/// <summary>
/// Configuration for the SQL Server storage adapter. Supply at minimum a
/// <see cref="ConnectionString"/>; the remaining settings have safe production defaults.
/// </summary>
public sealed record SqlServerStoreOptions
{
    /// <summary>
    /// The ADO.NET connection string the adapter uses for every database operation. Required.
    /// Point it at the database that holds (or will hold) the BackWave schema.
    /// </summary>
    public required string ConnectionString { get; init; }

    /// <summary>
    /// When <see langword="true"/>, the adapter applies its versioned schema scripts the first time
    /// it touches the database. When <see langword="false"/> (the default), it assumes the schema is
    /// already in place and refuses to run against a missing one. Leave it off in production and apply
    /// the schema as part of your deployment pipeline; turn it on for tests and local development where
    /// creating the schema on demand is convenient. Either way, a schema-version mismatch stops the
    /// workers before any job state can be corrupted.
    /// </summary>
    public bool AutoMigrate { get; init; }

    /// <summary>
    /// When <see langword="true"/> (the default), auto-migration is coordinated across a cold-booting
    /// fleet: exactly one Node applies the schema at a time while the rest block, re-check, and no-op,
    /// and the migration runs atomically (all-or-nothing) under a transaction-scoped database lock.
    /// This makes a fleet that boots with <see cref="AutoMigrate"/> on safe under true concurrency —
    /// without it, several Nodes race to apply the schema and a genuine first-boot race can throw a
    /// duplicate-object error even though the scripts guard every object with <c>IF NOT EXISTS</c>.
    /// Turning it off is a footgun: it restores that race and is safe only when your deployment already
    /// serializes migration itself (for example a dedicated migration step, or a single Node that boots
    /// before the rest). Applies only when <see cref="AutoMigrate"/> is on; has no effect otherwise.
    /// </summary>
    public bool CoordinateMigration { get; init; } = true;

    /// <summary>
    /// The SQL Server schema that holds every BackWave table, index, and sequence. Defaults to
    /// <c>backwave</c>. Change it to place the job store under a schema that fits your naming
    /// conventions or to keep it clear of other applications' objects in a shared database. The same
    /// name must be used everywhere the store touches this database — auto-migrate, out-of-band
    /// migration, and every query all read it from here — so set it once and leave it fixed for the
    /// life of the data. Must be a valid unqualified identifier: 1–128 characters, a letter or
    /// underscore followed by letters, digits, or underscores; any other value is rejected when the
    /// store is created.
    /// </summary>
    public string SchemaName { get; init; } = "backwave";

    /// <summary>
    /// The size and batch limits the store enforces — maximum payload size, claim batch size, and the
    /// like. Defaults to <see cref="StoreBounds.Default"/>. Tighten or loosen these to match your
    /// database's capacity and your jobs' payload sizes.
    /// </summary>
    public StoreBounds Bounds { get; init; } = StoreBounds.Default;

    /// <summary>
    /// How much per-job history the store records: nothing, state transitions only, or transitions
    /// plus captured failure detail. Defaults to recording transitions and failure detail, so the
    /// dashboard timeline and error messages work out of the box. Changing this affects only what new
    /// transitions write — it is a configuration change, never a schema change. Lower it to reduce
    /// write volume when you do not need the history.
    /// </summary>
    public JobHistoryPolicy HistoryPolicy { get; init; } = JobHistoryPolicy.TransitionsAndFailureDetail;

    /// <summary>
    /// Optional logger factory. When supplied and <see cref="AutoMigrate"/> is on, the adapter records a
    /// schema-migration event at Information level after applying the schema on first use. Null (the
    /// default) disables that log with no allocation and does not otherwise affect the store.
    /// </summary>
    public ILoggerFactory? LoggerFactory { get; init; }

    /// <summary>
    /// A diagnostic fault hook for fault-injection testing; leave it <see langword="null"/> in
    /// production (the default). When set, the adapter invokes it at named points partway through a
    /// multi-step operation, inside the open transaction and before commit. Throwing from the hook
    /// aborts that transaction, which rolls back every effect — the mechanism by which tests verify
    /// the store's all-or-nothing guarantee under a mid-operation crash. The argument is the name of
    /// the point reached (for example <c>"claim"</c> or <c>"enqueue"</c>).
    /// </summary>
    // Test-only failpoint (issue 0034): armed by the conformance suite's fault-armed store.
    public Func<string, CancellationToken, Task>? FaultHook { get; init; }
}
