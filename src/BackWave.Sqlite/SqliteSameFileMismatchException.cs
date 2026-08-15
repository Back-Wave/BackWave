namespace BackWave.Sqlite;

/// <summary>
/// Thrown at wire-up when a caller-supplied <see cref="Microsoft.Data.Sqlite.SqliteConnection"/>
/// used for a co-resident Transactional Enqueue points at a <em>different</em> database file than
/// the one BackWave is configured to use.
/// </summary>
/// <remarks>
/// Why this is fatal rather than best-effort: in the co-resident topology BackWave's
/// <c>backwave_*</c> tables live inside the user's own application SQLite file, and a Transactional
/// Enqueue rides the caller's transaction so the job and the caller's domain writes commit atomically.
/// If the caller's connection is actually attached to a <em>different</em> file, the INSERT would commit
/// invisibly into that other database — the job would never be seen by the workers reading BackWave's
/// configured file, and no error would ever surface at runtime. That silent split-brain is exactly the
/// failure we refuse to ship, so we fail loud at wire-up and name both paths to make the
/// misconfiguration self-diagnosing.
/// </remarks>
public sealed class SqliteSameFileMismatchException : InvalidOperationException
{
    /// <summary>The canonicalised file path BackWave is configured to use.</summary>
    public string ConfiguredPath { get; }

    /// <summary>The canonicalised file path the caller's connection is actually attached to.</summary>
    public string ConnectionPath { get; }

    /// <summary>
    /// Creates the exception, baking both canonical paths into the message so the mismatch is obvious
    /// from a log line alone.
    /// </summary>
    public SqliteSameFileMismatchException(string configuredPath, string connectionPath)
        : base(
            $"Caller SqliteConnection is attached to a different database file than BackWave is configured to use. "
            + $"A Transactional Enqueue on this connection would commit the job invisibly into the wrong file. "
            + $"Configured: '{configuredPath}'. Connection: '{connectionPath}'.")
    {
        ConfiguredPath = configuredPath;
        ConnectionPath = connectionPath;
    }
}
