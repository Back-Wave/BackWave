using BackWave.Benchmarks.Latency;
using BackWave.Benchmarks.Targets;

namespace BackWave.Benchmarks.Tests;

/// <summary>
/// Rerouting a target through the latency proxy means replacing one host and port inside its DSN and
/// changing nothing else. Losing a credential, a service name, or a driver option here would silently send
/// the diagnostic run at a different database than the one it claims to measure.
/// </summary>
public sealed class ConnectionStringEndpointTests
{
    private static readonly DatabaseEndpoint Proxy = new("127.0.0.1", 51234);

    [Fact]
    public void Oracle_keeps_its_credentials_and_service_name_when_the_endpoint_moves()
    {
        const string dsn = "User Id=backwave;Password=backwave;Data Source=db.internal:15212/FREEPDB1;";

        Assert.Equal(new DatabaseEndpoint("db.internal", 15212), ConnectionStringEndpoint.Read(DatabaseDriver.Oracle, dsn));

        var rewritten = ConnectionStringEndpoint.Rewrite(DatabaseDriver.Oracle, dsn, Proxy);

        Assert.Equal(Proxy, ConnectionStringEndpoint.Read(DatabaseDriver.Oracle, rewritten));
        Assert.Contains("127.0.0.1:51234/FREEPDB1", rewritten);
        Assert.Contains("backwave", rewritten);
        Assert.DoesNotContain("db.internal", rewritten);
    }

    [Fact]
    public void Oracle_defaults_to_the_listener_port_when_the_data_source_omits_it()
    {
        var endpoint = ConnectionStringEndpoint.Read(
            DatabaseDriver.Oracle, "User Id=u;Password=p;Data Source=//db.internal/FREEPDB1;");

        Assert.Equal(new DatabaseEndpoint("db.internal", 1521), endpoint);
    }

    [Fact]
    public void Oracle_refuses_a_data_source_that_hides_the_endpoint()
    {
        // A TNS alias or descriptor resolves elsewhere, so the proxy cannot stand in front of it. Rewriting
        // it anyway would produce a DSN that connects to the wrong place, or to nothing.
        Assert.Throws<ArgumentException>(() => ConnectionStringEndpoint.Read(
            DatabaseDriver.Oracle, "User Id=u;Password=p;Data Source=ORCLPDB_ALIAS;"));
    }

    [Fact]
    public void Postgres_and_sql_server_move_their_endpoints_and_keep_the_rest()
    {
        const string pg = "Host=db.internal;Port=5499;Username=u;Password=p;Database=d;Pooling=true";
        var rewrittenPg = ConnectionStringEndpoint.Rewrite(DatabaseDriver.Postgres, pg, Proxy);

        Assert.Equal(new DatabaseEndpoint("db.internal", 5499), ConnectionStringEndpoint.Read(DatabaseDriver.Postgres, pg));
        Assert.Equal(Proxy, ConnectionStringEndpoint.Read(DatabaseDriver.Postgres, rewrittenPg));
        Assert.Contains("Database=d", rewrittenPg);
        Assert.DoesNotContain("db.internal", rewrittenPg);

        const string mssql = "Server=db.internal,1433;User Id=sa;Password=p;Database=d;TrustServerCertificate=true";
        var rewrittenMssql = ConnectionStringEndpoint.Rewrite(DatabaseDriver.SqlServer, mssql, Proxy);

        Assert.Equal(new DatabaseEndpoint("db.internal", 1433), ConnectionStringEndpoint.Read(DatabaseDriver.SqlServer, mssql));
        Assert.Equal(Proxy, ConnectionStringEndpoint.Read(DatabaseDriver.SqlServer, rewrittenMssql));
        Assert.Contains("Initial Catalog=d", rewrittenMssql);
        Assert.DoesNotContain("db.internal", rewrittenMssql);
    }

    [Fact]
    public void Sql_server_refuses_a_named_instance_whose_port_the_browser_service_decides()
    {
        Assert.Throws<ArgumentException>(() => ConnectionStringEndpoint.Read(
            DatabaseDriver.SqlServer, @"Server=db.internal\SQLEXPRESS;User Id=sa;Password=p"));
    }

    [Fact]
    public void Every_target_the_registry_serves_can_be_rerouted_through_the_proxy()
    {
        // The dial is a harness feature, not an Oracle one. A target that declares no DSN variable, or a
        // driver whose endpoint cannot be rewritten, would fail only at the moment someone tried to use it.
        string[] names =
        [
            "postgres", "pg",
            "sqlserver", "mssql",
            "oracle", "ora",
            "hangfire-postgres", "hangfire-pg",
            "hangfire-sqlserver", "hangfire-mssql",
        ];

        foreach (var name in names)
        {
            var descriptor = BenchmarkTargetRegistry.Describe(name);

            Assert.False(string.IsNullOrWhiteSpace(descriptor.ConnectionStringEnvVar), $"{name} declares no DSN variable");
            Assert.StartsWith("BACKWAVE_", descriptor.ConnectionStringEnvVar);

            var rewritten = ConnectionStringEndpoint.Rewrite(descriptor.Driver, SampleDsn(descriptor.Driver), Proxy);
            Assert.Equal(Proxy, ConnectionStringEndpoint.Read(descriptor.Driver, rewritten));
        }
    }

    private static string SampleDsn(DatabaseDriver driver) => driver switch
    {
        DatabaseDriver.Postgres => "Host=db.internal;Port=5499;Username=u;Password=p;Database=d",
        DatabaseDriver.SqlServer => "Server=db.internal,1433;User Id=sa;Password=p;Database=d",
        DatabaseDriver.Oracle => "User Id=u;Password=p;Data Source=db.internal:15212/FREEPDB1;",
        _ => throw new ArgumentOutOfRangeException(nameof(driver), driver, "No sample DSN for this driver."),
    };
}
