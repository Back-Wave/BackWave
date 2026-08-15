using BackWave.Sqlite;
using BackWave.Sqlite.Internal;
using Microsoft.Data.Sqlite;

namespace BackWave.Sqlite.Tests;

/// <summary>
/// Unit tests for <see cref="SqliteSameFileGuard"/>. Each test opens real <see cref="SqliteConnection"/>s
/// against temp files (no DB schema needed) and cleans them up.
/// </summary>
public sealed class SqliteSameFileGuardTests : IDisposable
{
    private readonly string _tempDir;

    public SqliteSameFileGuardTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "bw-samefile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        // Drop pooled handles so the temp files are releasable on Windows, then best-effort delete.
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string PathFor(string name) => Path.Combine(_tempDir, name);

    private static SqliteConnection OpenFile(string path)
    {
        var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        return connection;
    }

    [Fact]
    public void Same_file_is_accepted()
    {
        var path = PathFor("store.db");
        var guard = new SqliteSameFileGuard($"Data Source={path}");
        using var connection = OpenFile(path);

        guard.EnsureSameFile(connection); // no throw
    }

    [Fact]
    public void Mismatch_throws_and_message_contains_both_paths()
    {
        var configured = PathFor("configured.db");
        var other = PathFor("other.db");
        var guard = new SqliteSameFileGuard($"Data Source={configured}");
        using var connection = OpenFile(other);

        var ex = Assert.Throws<SqliteSameFileMismatchException>(() => guard.EnsureSameFile(connection));

        // Paths are canonicalised (symlinks in directory components resolved), so assert on the distinct
        // filenames and on the structured properties rather than on a non-canonical full-path string.
        Assert.Contains("configured.db", ex.Message);
        Assert.Contains("other.db", ex.Message);
        Assert.EndsWith("configured.db", ex.ConfiguredPath);
        Assert.EndsWith("other.db", ex.ConnectionPath);
        Assert.NotEqual(ex.ConfiguredPath, ex.ConnectionPath);
    }

    [Fact]
    public void Symlink_to_configured_file_resolves_equal()
    {
        var real = PathFor("real.db");
        // Materialise the real file so the link has a target to resolve to.
        using (var seed = OpenFile(real))
        {
        }

        var link = PathFor("link.db");
        try
        {
            File.CreateSymbolicLink(link, real);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Environment refuses symlink creation — skip gracefully rather than fail.
            return;
        }

        var guard = new SqliteSameFileGuard($"Data Source={real}");
        using var connection = OpenFile(link);

        guard.EnsureSameFile(connection); // link resolves to real → no throw
    }

    [Fact]
    public void Relative_and_absolute_to_same_file_resolve_equal()
    {
        var absolute = PathFor("rel-abs.db");
        // Build a relative path to the same file by going via the temp dir's parent.
        var relative = Path.GetRelativePath(Directory.GetCurrentDirectory(), absolute);

        var guard = new SqliteSameFileGuard($"Data Source={relative}");
        using var connection = OpenFile(absolute);

        guard.EnsureSameFile(connection); // relative canonicalises to the same absolute → no throw
    }

    [Fact]
    public void Case_variant_resolves_equal_on_case_insensitive_filesystem()
    {
        if (OperatingSystem.IsLinux())
        {
            // Linux filesystems are case-sensitive; the equality this asserts only holds on macOS/Windows.
            return;
        }

        var path = PathFor("CaseTest.db");
        using (var seed = OpenFile(path))
        {
        }

        var upper = Path.Combine(_tempDir, "CASETEST.DB");
        var guard = new SqliteSameFileGuard($"Data Source={upper}");
        using var connection = OpenFile(path);

        guard.EnsureSameFile(connection); // case-folded compare → no throw
    }

    [Fact]
    public void In_memory_connection_is_skipped()
    {
        // Configured to a real file; connection is in-memory. Path identity is meaningless → no throw.
        var guard = new SqliteSameFileGuard($"Data Source={PathFor("configured.db")}");
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        guard.EnsureSameFile(connection);
    }

    [Fact]
    public void In_memory_configured_store_skips_every_connection()
    {
        var guard = new SqliteSameFileGuard("Data Source=:memory:");
        using var connection = OpenFile(PathFor("whatever.db"));

        guard.EnsureSameFile(connection); // configured side has no file identity → no throw
    }

    [Fact]
    public void Check_runs_once_per_connection()
    {
        var path = PathFor("memoised.db");
        var resolveCount = 0;
        var guard = new SqliteSameFileGuard($"Data Source={path}", onResolve: () => resolveCount++);
        using var connection = OpenFile(path);

        guard.EnsureSameFile(connection);
        guard.EnsureSameFile(connection);
        guard.EnsureSameFile(connection);

        Assert.Equal(1, resolveCount);
    }
}
