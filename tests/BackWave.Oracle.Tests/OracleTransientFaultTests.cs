using System.Diagnostics;
using BackWave.Core;
using BackWave.Hosting;
using BackWave.Jobs;
using BackWave.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Oracle.ManagedDataAccess.Client;

namespace BackWave.Oracle.Tests;

/// <summary>
/// Issue #12: a network outage between a worker group and its Oracle database must degrade the group, not
/// fail-stop it. ODP.NET raises these faults while it opens a connection, so each case points a real store
/// at an endpoint that cannot answer and needs no database.
/// </summary>
public sealed class OracleTransientFaultTests
{
    // Each endpoint reproduces one outage the way ODP.NET reports it to the pump.
    public static TheoryData<string, int> Outages => new()
    {
        // The host name stops resolving (a DNS or network outage). DNS never resolves the .invalid domain.
        { Descriptor("bw-no-such-host.invalid", 1521), 12545 },
        // The host answers but nothing listens on the port (the database is down).
        { Descriptor("127.0.0.1", 1), 50201 },
        // The host does not answer at all, so the connect times out. 192.0.2.1 is in TEST-NET-1, which no
        // network routes.
        { Descriptor("192.0.2.1", 1521), 50000 },
    };

    [Theory]
    [MemberData(nameof(Outages))]
    public async Task A_network_outage_raises_a_fault_the_adapter_classifies_transient(string dataSource, int expected)
    {
        var store = Store(dataSource);

        var fault = await Assert.ThrowsAsync<OracleException>(async () =>
            await store.ClaimAsync(new ClaimRequest("w1", ["default"], 10, TimeSpan.FromMinutes(1), DateTimeOffset.UtcNow)));

        Assert.Equal(expected, fault.Number);
        Assert.True(store.IsTransientFault(fault), $"ORA-{fault.Number} must classify transient");
    }

    [Theory]
    [MemberData(nameof(Outages))]
    public async Task A_network_outage_degrades_the_worker_group_instead_of_halting_it(string dataSource, int expected)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddBackWave(backwave => backwave
            .UseStore(Store(dataSource))
            .UseRegistry(new JobRegistry([]))
            .AddWorkerGroup(new WorkerGroupOptions
            {
                Name = "outage-workers",
                Policy = new DispatchPolicy.Strict(["default"]),
                PollInterval = TimeSpan.FromMilliseconds(200),
                LeaseDuration = TimeSpan.FromSeconds(30),
            }));
        using var host = builder.Build();
        var health = host.Services.GetRequiredService<BackWaveHealth>();

        await host.StartAsync();
        try
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while (DateTime.UtcNow < deadline
                   && !health.DegradedGroups.ContainsKey("outage-workers")
                   && !health.HaltedGroups.ContainsKey("outage-workers"))
            {
                await Task.Delay(50);
            }

            Assert.False(health.HaltedGroups.TryGetValue("outage-workers", out var halt),
                $"the outage fail-stopped the group: {halt?.ExceptionType}: {halt?.Message}");
            Assert.True(health.DegradedGroups.TryGetValue("outage-workers", out var cause), "the outage must mark the group degraded");
            Assert.Contains($"ORA-{expected}", cause);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    // A host that does not answer costs a full connect timeout on every attempt, so the migrator's retry
    // stops on its time budget instead of waiting out all eight attempts (about 17 s here).
    [Fact]
    public async Task An_unreachable_host_fails_the_migration_once_the_retry_budget_is_spent()
    {
        var connectionString =
            $"User Id=backwave;Password=backwave;Connection Timeout=2;Data Source={Descriptor("192.0.2.1", 1521)}";
        var elapsed = Stopwatch.StartNew();

        var fault = await Assert.ThrowsAsync<OracleException>(() => OracleMigrator.MigrateAsync(connectionString));

        Assert.Equal(50000, fault.Number);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(10), $"the migration took {elapsed.Elapsed.TotalSeconds:F1} s");
    }

    // A slow fault, for example a TNS connect timeout, can use the whole time budget on the first attempt.
    // A caller that migrates once at startup has no next poll, so the first three attempts always run.
    [Theory]
    [InlineData(1, 0, true)]
    [InlineData(1, 15, true)]
    [InlineData(2, 30, true)]
    [InlineData(3, 4, true)]
    [InlineData(3, 45, false)]
    [InlineData(7, 4, true)]
    [InlineData(8, 0, false)]
    public void The_migrator_retries_three_attempts_before_the_budget_applies(int attempt, int elapsedSeconds, bool retries)
    {
        Assert.Equal(retries, OracleMigrator.ShouldRetry(attempt, TimeSpan.FromSeconds(elapsedSeconds)));
    }

    private static string Descriptor(string host, int port) =>
        $"(DESCRIPTION=(CONNECT_TIMEOUT=2)(ADDRESS=(PROTOCOL=TCP)(HOST={host})(PORT={port}))"
        + "(CONNECT_DATA=(SERVICE_NAME=FREEPDB1)))";

    private static OracleJobStore Store(string dataSource) => new(new OracleStoreOptions
    {
        ConnectionString = $"User Id=backwave;Password=backwave;Connection Timeout=3;Data Source={dataSource}",
    });
}
