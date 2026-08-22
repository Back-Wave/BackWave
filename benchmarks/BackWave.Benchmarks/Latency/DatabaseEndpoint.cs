using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Npgsql;
using Oracle.ManagedDataAccess.Client;

namespace BackWave.Benchmarks.Latency;

/// <summary>The ADO.NET driver a target connects with, which is what decides how its DSN spells the endpoint.</summary>
public enum DatabaseDriver
{
    /// <summary>Npgsql, which keeps the endpoint in separate <c>Host</c> and <c>Port</c> keywords.</summary>
    Postgres,

    /// <summary>Microsoft.Data.SqlClient, which keeps it in <c>Data Source</c> as <c>host,port</c>.</summary>
    SqlServer,

    /// <summary>ODP.NET, which keeps it in <c>Data Source</c> as an EZConnect <c>host:port/service</c>.</summary>
    Oracle,
}

/// <summary>The host and port a DSN points at.</summary>
/// <param name="Host">The database host.</param>
/// <param name="Port">The database port.</param>
public readonly record struct DatabaseEndpoint(string Host, int Port);

/// <summary>
/// Reads and replaces the endpoint inside a connection string, leaving every other keyword alone. The
/// latency dial needs this to point a target at the local proxy: same credentials, same database, same
/// options, one different host and port.
/// </summary>
public static partial class ConnectionStringEndpoint
{
    private const int DefaultPostgresPort = 5432;
    private const int DefaultSqlServerPort = 1433;
    private const int DefaultOraclePort = 1521;

    /// <summary>Reads the endpoint a DSN points at, applying the driver's default port when none is given.</summary>
    /// <exception cref="ArgumentException">The DSN carries no endpoint this code knows how to read.</exception>
    public static DatabaseEndpoint Read(DatabaseDriver driver, string connectionString) => driver switch
    {
        DatabaseDriver.Postgres => ReadPostgres(connectionString),
        DatabaseDriver.SqlServer => ReadSqlServer(connectionString),
        DatabaseDriver.Oracle => ReadOracle(connectionString),
        _ => throw new ArgumentOutOfRangeException(nameof(driver), driver, "Unknown driver."),
    };

    /// <summary>Returns the DSN with its endpoint replaced and everything else preserved.</summary>
    /// <exception cref="ArgumentException">The DSN carries no endpoint this code knows how to rewrite.</exception>
    public static string Rewrite(DatabaseDriver driver, string connectionString, DatabaseEndpoint endpoint)
        => driver switch
        {
            DatabaseDriver.Postgres => RewritePostgres(connectionString, endpoint),
            DatabaseDriver.SqlServer => RewriteSqlServer(connectionString, endpoint),
            DatabaseDriver.Oracle => RewriteOracle(connectionString, endpoint),
            _ => throw new ArgumentOutOfRangeException(nameof(driver), driver, "Unknown driver."),
        };

    private static DatabaseEndpoint ReadPostgres(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(builder.Host))
        {
            throw new ArgumentException("The Postgres DSN has no Host.", nameof(connectionString));
        }

        return new DatabaseEndpoint(builder.Host, builder.Port == 0 ? DefaultPostgresPort : builder.Port);
    }

    private static string RewritePostgres(string connectionString, DatabaseEndpoint endpoint)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString) { Host = endpoint.Host, Port = endpoint.Port };
        return builder.ConnectionString;
    }

    private static DatabaseEndpoint ReadSqlServer(string connectionString)
    {
        var (host, port) = SplitSqlServerDataSource(new SqlConnectionStringBuilder(connectionString).DataSource);
        return new DatabaseEndpoint(host, port);
    }

    private static string RewriteSqlServer(string connectionString, DatabaseEndpoint endpoint)
    {
        // Reading first so an unparseable Data Source is refused rather than silently overwritten.
        _ = ReadSqlServer(connectionString);
        var builder = new SqlConnectionStringBuilder(connectionString)
        {
            DataSource = $"{endpoint.Host},{endpoint.Port}",
        };
        return builder.ConnectionString;
    }

    private static (string Host, int Port) SplitSqlServerDataSource(string dataSource)
    {
        // Accepts "host", "host,port", and the "tcp:" protocol prefix. A named instance ("host\\SQLEXPRESS")
        // resolves its port through the browser service, so the proxy cannot stand in front of it.
        var value = dataSource.Trim();
        if (value.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase))
        {
            value = value[4..];
        }

        if (value.Length == 0 || value.Contains('\\'))
        {
            throw new ArgumentException(
                $"The SQL Server Data Source '{dataSource}' is not a host,port endpoint.", nameof(dataSource));
        }

        var comma = value.IndexOf(',');
        if (comma < 0)
        {
            return (value, DefaultSqlServerPort);
        }

        return (value[..comma], int.Parse(value[(comma + 1)..]));
    }

    private static DatabaseEndpoint ReadOracle(string connectionString)
    {
        var match = MatchOracleEzConnect(connectionString);
        var port = match.Groups["port"].Success ? int.Parse(match.Groups["port"].Value) : DefaultOraclePort;
        return new DatabaseEndpoint(match.Groups["host"].Value, port);
    }

    private static string RewriteOracle(string connectionString, DatabaseEndpoint endpoint)
    {
        var match = MatchOracleEzConnect(connectionString);
        var builder = new OracleConnectionStringBuilder(connectionString)
        {
            DataSource = $"{endpoint.Host}:{endpoint.Port}/{match.Groups["service"].Value}",
        };
        return builder.ConnectionString;
    }

    private static Match MatchOracleEzConnect(string connectionString)
    {
        var dataSource = new OracleConnectionStringBuilder(connectionString).DataSource;
        var match = OracleEzConnect().Match(dataSource.Trim());
        if (!match.Success)
        {
            throw new ArgumentException(
                $"The Oracle Data Source '{dataSource}' is not an EZConnect host:port/service endpoint. " +
                "A TNS descriptor or an alias hides the endpoint, so the proxy cannot stand in front of it.",
                nameof(connectionString));
        }

        return match;
    }

    // EZConnect: an optional "//", a host, an optional ":port", then "/service".
    [GeneratedRegex(@"^(?://)?(?<host>[^:/]+)(?::(?<port>\d+))?/(?<service>[^:/]+)$")]
    private static partial Regex OracleEzConnect();
}
