using Npgsql;

namespace BackWave.Benchmarks.Targets;

/// <summary>Creates the database a Postgres target names, so each target runs on a fresh container.</summary>
internal static class PostgresDatabase
{
    /// <summary>
    /// Creates the database in <paramref name="connectionString"/> when the server does not have it. It opens
    /// the database first and connects to the <c>postgres</c> maintenance database only when that fails with
    /// 3D000 (database does not exist), so a user who can reach only the target database still runs.
    /// </summary>
    public static void EnsureExists(string connectionString)
    {
        try
        {
            using var probe = new NpgsqlConnection(connectionString);
            probe.Open();
            return;
        }
        catch (PostgresException missing) when (missing.SqlState == PostgresErrorCodes.InvalidCatalogName)
        {
        }

        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var database = builder.Database ?? throw new InvalidOperationException("The DSN names no database.");
        builder.Database = "postgres";
        using var admin = NpgsqlDataSource.Create(builder.ConnectionString);
        using var create = admin.CreateCommand($"CREATE DATABASE \"{database}\"");
        create.ExecuteNonQuery();
    }
}
