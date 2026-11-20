using Npgsql;

namespace BackWave.Benchmarks.Targets;

/// <summary>Creates the database a Postgres target names, so each target runs on a fresh container.</summary>
internal static class PostgresDatabase
{
    /// <summary>Creates the database in <paramref name="connectionString"/> when the server does not have it.</summary>
    public static void EnsureExists(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var database = builder.Database ?? throw new InvalidOperationException("The DSN names no database.");
        builder.Database = "postgres";
        using var admin = NpgsqlDataSource.Create(builder.ConnectionString);
        using var exists = admin.CreateCommand("SELECT 1 FROM pg_database WHERE datname = $1");
        exists.Parameters.AddWithValue(database);
        if (exists.ExecuteScalar() is not null)
        {
            return;
        }

        using var create = admin.CreateCommand($"CREATE DATABASE \"{database}\"");
        create.ExecuteNonQuery();
    }
}
