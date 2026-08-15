using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;

namespace BackWave.Sqlite.Internal;

/// <summary>
/// Verifies, once per caller connection, that a caller-supplied <see cref="SqliteConnection"/> used for a
/// co-resident Transactional Enqueue is attached to the <em>same</em> on-disk file BackWave is configured to
/// use. A mismatch means the job would commit into the wrong database invisibly, so the guard throws
/// <see cref="SqliteSameFileMismatchException"/> loudly at wire-up (see that type for the full rationale).
/// </summary>
/// <remarks>
/// <para>
/// "Same file" is decided by <em>path canonicalisation only</em> — OS-level file identity (inode / volume
/// serial number) is explicitly out of scope for v1. We canonicalise both sides with
/// <see cref="Path.GetFullPath(string)"/> (collapsing relative-vs-absolute forms) and then resolve symlinks
/// recursively, so two names that walk to the same real file compare equal. The final string comparison is
/// case-insensitive on macOS/Windows and case-sensitive on Linux, matching how those filesystems actually
/// treat names.
/// </para>
/// <para>
/// In-memory and temp databases are skipped entirely: their <see cref="SqliteConnection.DataSource"/> has no
/// stable filesystem identity, so a path comparison there is meaningless rather than safe.
/// </para>
/// <para>
/// The check can touch the filesystem (symlink resolution), so it must stay off the hot enqueue path. We
/// memoise the verdict per connection in a <see cref="ConditionalWeakTable{TKey,TValue}"/>: the first
/// Transactional Enqueue on a given connection pays the resolution cost, every subsequent one is a single
/// table lookup, and the entry is collected with the connection.
/// </para>
/// </remarks>
internal sealed class SqliteSameFileGuard
{
    /// <summary>
    /// Per-connection memoised verdict. Presence of an entry means "already verified OK for this
    /// connection" — we never cache a failing verdict because a failure throws before it could be stored.
    /// </summary>
    private readonly ConditionalWeakTable<SqliteConnection, object> _verified = new();

    /// <summary>The configured data-source string BackWave was told to use (raw, may be a path or special form).</summary>
    private readonly string _configuredDataSource;

    /// <summary>
    /// Canonical configured path, or <c>null</c> when the configured store itself targets an in-memory/temp
    /// database (in which case the guard is a no-op for every connection).
    /// </summary>
    private readonly string? _configuredCanonicalPath;

    /// <summary>
    /// Optional seam for tests to observe how often the (potentially filesystem-touching) resolution actually
    /// runs, so memoisation can be proven without weakening the public API. Production never sets this.
    /// </summary>
    private readonly Action? _onResolve;

    /// <summary>
    /// Builds a guard for a store configured with <paramref name="configuredConnectionString"/>. Accepts either
    /// a full Microsoft.Data.Sqlite connection string (with a <c>Data Source=</c> keyword) or a bare path.
    /// </summary>
    /// <param name="configuredConnectionString">The connection string (or data-source path) BackWave is configured with.</param>
    /// <param name="onResolve">Test-only seam invoked each time a connection's backing file is resolved; pass <c>null</c> in production.</param>
    public SqliteSameFileGuard(string configuredConnectionString, Action? onResolve = null)
    {
        ArgumentNullException.ThrowIfNull(configuredConnectionString);
        _onResolve = onResolve;
        _configuredDataSource = ExtractDataSource(configuredConnectionString);
        _configuredCanonicalPath = IsNonFileDataSource(_configuredDataSource)
            ? null
            : Canonicalise(_configuredDataSource);
    }

    /// <summary>
    /// Verifies <paramref name="connection"/> targets the configured file, throwing
    /// <see cref="SqliteSameFileMismatchException"/> on a real mismatch. The check runs at most once per
    /// connection; in-memory/temp connections (and an in-memory/temp configured store) are silently skipped.
    /// </summary>
    /// <param name="connection">The open caller connection about to carry a Transactional Enqueue.</param>
    public void EnsureSameFile(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // Configured store has no file identity (in-memory/temp): nothing to compare against.
        if (_configuredCanonicalPath is null)
        {
            return;
        }

        // Already verified this exact connection — stay off the hot path.
        if (_verified.TryGetValue(connection, out _))
        {
            return;
        }

        var connectionDataSource = connection.DataSource ?? string.Empty;

        // Caller connection is itself in-memory/temp: path identity is meaningless, skip (and don't memoise,
        // since DataSource can in principle change before the connection is reused for a real file).
        if (IsNonFileDataSource(connectionDataSource))
        {
            return;
        }

        _onResolve?.Invoke();
        var connectionCanonicalPath = Canonicalise(connectionDataSource);

        if (!PathsEqual(_configuredCanonicalPath, connectionCanonicalPath))
        {
            throw new SqliteSameFileMismatchException(_configuredCanonicalPath, connectionCanonicalPath);
        }

        // Verified OK — memoise so the next enqueue on this connection is a single lookup.
        _verified.AddOrUpdate(connection, Sentinel);
    }

    /// <summary>Marker stored as the memoised "verified OK" value; identity only, never read.</summary>
    private static readonly object Sentinel = new();

    /// <summary>
    /// Pulls the data-source out of a connection string. If no <c>Data Source</c> keyword is present we treat
    /// the whole input as a bare path, which is how a caller passing just a filename would expect it to behave.
    /// </summary>
    private static string ExtractDataSource(string connectionStringOrPath)
    {
        // A bare path won't parse meaningfully as a keyword string but also won't throw; the builder simply
        // yields an empty DataSource. Detect the keyword form first to avoid mis-classifying "a=b" filenames.
        if (connectionStringOrPath.Contains('=', StringComparison.Ordinal))
        {
            try
            {
                var builder = new SqliteConnectionStringBuilder(connectionStringOrPath);
                return builder.DataSource ?? string.Empty;
            }
            catch (ArgumentException)
            {
                // Not a valid connection string — fall through and treat it as a literal path.
            }
        }

        return connectionStringOrPath;
    }

    /// <summary>
    /// True for data sources that have no stable on-disk identity: in-memory databases (in any of their
    /// spellings) and temp databases (empty data source). These are skipped by the guard.
    /// </summary>
    private static bool IsNonFileDataSource(string dataSource)
    {
        if (string.IsNullOrEmpty(dataSource))
        {
            // Empty data source == private temp database.
            return true;
        }

        // ":memory:" and shared-cache memory forms ("file::memory:?cache=shared"). Mode=Memory shows up via
        // the connection-string keyword, which ExtractDataSource normalises into the data source it produced;
        // we additionally guard the raw forms here for direct DataSource values.
        if (dataSource.Equals(":memory:", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (dataSource.Contains(":memory:", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (dataSource.Contains("Mode=Memory", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Resolves a data-source path to its canonical full path, realpath-style: collapse relative-vs-absolute
    /// with <see cref="Path.GetFullPath(string)"/>, then resolve symlinks in <em>every</em> path component —
    /// not just the final entry. The latter matters because real systems symlink directory components too
    /// (on macOS <c>/var</c> is a symlink to <c>/private/var</c>, so two names for the same file would
    /// otherwise canonicalise differently and the guard would false-positive).
    /// </summary>
    private static string Canonicalise(string path)
    {
        var full = Path.GetFullPath(path);
        return ResolveAllComponents(full);
    }

    /// <summary>
    /// Walks <paramref name="fullPath"/> from the root down, following any symlink found at each component to
    /// its final target before descending further — the recursion realpath performs. Components that don't
    /// exist yet (typically the trailing DB file, which BackWave creates lazily) are tolerated: the
    /// already-resolved prefix is kept and the remaining segments are appended verbatim, so a freshly
    /// configured store still canonicalises consistently with the connection that later creates the file.
    /// A bounded hop count guards against link cycles.
    /// </summary>
    private static string ResolveAllComponents(string fullPath)
    {
        const int maxHops = 64; // cycle guard: realpath-style chains are short; 64 is generous.
        var hops = 0;

        var root = Path.GetPathRoot(fullPath) ?? string.Empty;
        var remainder = fullPath.Length > root.Length ? fullPath[root.Length..] : string.Empty;
        var segments = remainder.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var current = root;
        foreach (var segment in segments)
        {
            if (segment.Length == 0)
            {
                continue;
            }

            current = Path.Combine(current, segment);

            // Follow a chain of links at this component to its final target.
            while (true)
            {
                if (++hops > maxHops)
                {
                    return current; // suspected cycle — stop resolving, keep what we have.
                }

                FileSystemInfo? target;
                try
                {
                    target = File.ResolveLinkTarget(current, returnFinalTarget: false);
                }
                catch (IOException)
                {
                    // Component doesn't exist yet (e.g. the DB file) — keep the resolved prefix and stop.
                    target = null;
                }
                catch (UnauthorizedAccessException)
                {
                    // Can't inspect link metadata here — treat as non-link rather than fail the guard.
                    target = null;
                }

                if (target is null)
                {
                    break; // not a link (or unresolvable) — this component stands, descend.
                }

                // FullName already resolves a relative link target against the link's own directory.
                current = Path.GetFullPath(target.FullName);
            }
        }

        return current;
    }

    /// <summary>
    /// Compares two already-canonicalised paths using the host filesystem's case semantics: case-insensitive
    /// on macOS/Windows, case-sensitive on Linux.
    /// </summary>
    private static bool PathsEqual(string a, string b)
    {
        var comparison = OperatingSystem.IsLinux()
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;
        return string.Equals(a, b, comparison);
    }
}
