using Microsoft.Data.Sqlite;

namespace BackWave.Sqlite.Internal;

/// <summary>
/// Pure connection-string normalizer: the adapter never trusts the caller's raw
/// connection string for the pragmas that bear on correctness. It FORCES, regardless of user input:
/// <list type="bullet">
/// <item><c>Foreign Keys = true</c> — the FK cascades (transitions, tags, edges, parents) are part
///   of the contract; SQLite leaves FK enforcement off per-connection by default.</item>
/// <item><c>Default Timeout</c> = <see cref="SqliteStoreOptions.BusyTimeout"/> in whole seconds —
///   so a contended writer queues for the single write lock instead of erroring immediately.</item>
/// <item><c>Pooling = true</c> — pool-only connections; the adapter opens and closes freely and
///   relies on the pool, never a single long-lived connection.</item>
/// </list>
/// No DB, no state — unit-tested in isolation.
/// </summary>
internal static class SqliteConnectionStringNormalizer
{
    /// <summary>
    /// Returns the caller's connection string with the correctness pragmas forced on. A
    /// <paramref name="busyTimeout"/> below one second is clamped up to one second (the minimum a
    /// whole-second <c>Default Timeout</c> can express without rounding to "no wait").
    /// </summary>
    public static string Normalize(string connectionString, TimeSpan busyTimeout)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString)
        {
            ForeignKeys = true,
            Pooling = true,
            DefaultTimeout = Math.Max(1, (int)Math.Ceiling(busyTimeout.TotalSeconds)),
        };

        return builder.ConnectionString;
    }
}
