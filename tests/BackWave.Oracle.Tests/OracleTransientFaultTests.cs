using System.Diagnostics;
using System.Text.RegularExpressions;
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
public sealed class OracleTransientFaultTests(Xunit.Abstractions.ITestOutputHelper output)
{
    // Each endpoint reproduces one outage the way ODP.NET reports it to the pump. The code in each comment
    // is the one measured here. A network can report the same outage with another code, so the tests
    // assert that the code is transient, not that it is this one.
    public static TheoryData<string> Outages => new()
    {
        // The host name stops resolving (a DNS or network outage). DNS never resolves the .invalid domain.
        // ORA-12545.
        Descriptor("bw-no-such-host.invalid", 1521),
        // The host answers but nothing listens on the port (the database is down). ORA-50201.
        Descriptor("127.0.0.1", 1),
        // The host does not answer at all, so the connect times out. 192.0.2.1 is in TEST-NET-1, which no
        // network routes. ORA-50000.
        Descriptor("192.0.2.1", 1521),
    };

    [Theory]
    [MemberData(nameof(Outages))]
    public async Task A_network_outage_raises_a_fault_the_adapter_classifies_transient(string dataSource)
    {
        var store = Store(dataSource);

        var fault = await Assert.ThrowsAsync<OracleException>(async () =>
            await store.ClaimAsync(new ClaimRequest("w1", ["default"], 10, TimeSpan.FromMinutes(1), DateTimeOffset.UtcNow)));

        output.WriteLine($"ORA-{fault.Number}: {fault.Message}");
        Assert.True(store.IsTransientFault(fault), $"ORA-{fault.Number} must classify transient");
    }

    [Theory]
    [MemberData(nameof(Outages))]
    public async Task A_network_outage_degrades_the_worker_group_instead_of_halting_it(string dataSource)
    {
        using var host = BuildHost(Store(dataSource));
        var health = host.Services.GetRequiredService<BackWaveHealth>();

        await host.StartAsync();
        try
        {
            await WaitForDegradedOrHaltedAsync(health);

            AssertNotHalted(health);
            Assert.True(health.DegradedGroups.TryGetValue(Group, out var cause), "the outage must mark the group degraded");
            output.WriteLine($"degraded: {cause}");
            Assert.True(NamesTransientCode(cause), $"the degraded cause must name a transient code: {cause}");

            // Later polls hit the same outage. Watch three poll intervals: none of them may halt the group.
            var watchEnd = DateTime.UtcNow + 3 * PollInterval;
            while (DateTime.UtcNow < watchEnd)
            {
                AssertNotHalted(health);
                await Task.Delay(50);
            }
            AssertNotHalted(health);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    // The negative case: a fault that is not an outage must still halt the group. A user that does not
    // exist raises ORA-01017 on the live listener. The test does not use a wrong password for a real user,
    // because repeated failed logins can lock that account. This is an integration test: it needs
    // "docker compose up -d oracle".
    [Fact]
    public async Task A_rejected_login_halts_the_worker_group()
    {
        var store = new OracleJobStore(new OracleStoreOptions
        {
            ConnectionString = "User Id=bw_no_such_user;Password=bw_no_such_user;Connection Timeout=3;"
                + "Data Source=localhost:15210/FREEPDB1",
        });
        using var host = BuildHost(store);
        var health = host.Services.GetRequiredService<BackWaveHealth>();

        await host.StartAsync();
        try
        {
            await WaitForDegradedOrHaltedAsync(health);

            Assert.True(health.HaltedGroups.TryGetValue(Group, out var halt), "a rejected login must halt the group");
            output.WriteLine($"halted: {halt.ExceptionType}: {halt.Message}");
            Assert.Contains("ORA-01017", halt.Message);
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

        output.WriteLine($"ORA-{fault.Number}: {fault.Message}");
        Assert.True(OracleFaultCodes.IsConnectivityFault(fault.Number), $"ORA-{fault.Number} must be transient");
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

    private const string Group = "outage-workers";
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    private static IHost BuildHost(OracleJobStore store)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddBackWave(backwave => backwave
            .UseStore(store)
            .UseRegistry(new JobRegistry([]))
            .AddWorkerGroup(new WorkerGroupOptions
            {
                Name = Group,
                Policy = new DispatchPolicy.Strict(["default"]),
                PollInterval = PollInterval,
                LeaseDuration = TimeSpan.FromSeconds(30),
            }));
        return builder.Build();
    }

    private static async Task WaitForDegradedOrHaltedAsync(BackWaveHealth health)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline
               && !health.DegradedGroups.ContainsKey(Group)
               && !health.HaltedGroups.ContainsKey(Group))
        {
            await Task.Delay(50);
        }
    }

    // A halt of one pump counts too, so the check reads the partly halted groups as well.
    private static void AssertNotHalted(BackWaveHealth health)
    {
        var halted = health.HaltedGroups.TryGetValue(Group, out var halt)
            || health.PartiallyHaltedGroups.TryGetValue(Group, out halt);
        Assert.False(halted, $"the outage fail-stopped the group: {halt?.ExceptionType}: {halt?.Message}");
    }

    // True when the cause names an ORA or ODP.NET code that the adapter classifies transient.
    internal static bool NamesTransientCode(string cause)
    {
        var code = Regex.Match(cause, @"ORA-(\d+)");
        return code.Success && OracleFaultCodes.IsConnectivityFault(int.Parse(code.Groups[1].Value));
    }

    private static string Descriptor(string host, int port) =>
        $"(DESCRIPTION=(CONNECT_TIMEOUT=2)(ADDRESS=(PROTOCOL=TCP)(HOST={host})(PORT={port}))"
        + "(CONNECT_DATA=(SERVICE_NAME=FREEPDB1)))";

    private static OracleJobStore Store(string dataSource) => new(new OracleStoreOptions
    {
        ConnectionString = $"User Id=backwave;Password=backwave;Connection Timeout=3;Data Source={dataSource}",
    });
}
