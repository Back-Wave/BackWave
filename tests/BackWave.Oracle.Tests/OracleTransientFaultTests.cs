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
/// A network outage between a worker group and its Oracle database must degrade the group, not
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

    // A host that does not answer costs a full connect timeout on every attempt. The store holds its ready
    // gate while it migrates, so an enqueue waits for the claim's migration and then runs its own. The
    // retry budget bounds both: without it each call waits out all eight attempts (about 17 s here).
    [Fact]
    public async Task An_unreachable_host_holds_the_store_ready_gate_only_for_the_retry_budget()
    {
        var store = new OracleJobStore(new OracleStoreOptions
        {
            ConnectionString = $"User Id=backwave;Password=backwave;Connection Timeout=2;Data Source={Descriptor("192.0.2.1", 1521)}",
            AutoMigrate = true,
        });
        var elapsed = Stopwatch.StartNew();

        var claim = Assert.ThrowsAsync<OracleException>(async () =>
            await store.ClaimAsync(new ClaimRequest("w1", ["default"], 10, TimeSpan.FromMinutes(1), DateTimeOffset.UtcNow)));
        var enqueue = Assert.ThrowsAsync<OracleException>(async () =>
            await store.EnqueueAsync(new NewJob(Guid.NewGuid(), "gate-test", "{}"u8.ToArray(), "default", DateTimeOffset.UtcNow), DateTimeOffset.UtcNow));
        var faults = await Task.WhenAll(claim, enqueue);

        output.WriteLine($"both faulted after {elapsed.Elapsed.TotalSeconds:F1} s: ORA-{faults[0].Number}, ORA-{faults[1].Number}");
        Assert.All(faults, fault => Assert.True(store.IsTransientFault(fault), $"ORA-{fault.Number} must classify transient"));
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(20), $"the enqueue waited {elapsed.Elapsed.TotalSeconds:F1} s");
    }

    // On the store path the pump migrates again on its next poll, so the budget stops the retry. A caller
    // that migrates once has no next poll, so the public migrator keeps every attempt with no time limit.
    [Theory]
    [InlineData(1, 15, null, true)]
    [InlineData(7, 100, null, true)]
    [InlineData(8, 0, null, false)]
    [InlineData(1, 0, 5, true)]
    [InlineData(2, 6, 5, false)]
    [InlineData(8, 0, 5, false)]
    public void The_migrator_bounds_its_retry_by_time_only_when_a_budget_is_given(
        int attempt, int elapsedSeconds, int? budgetSeconds, bool retries)
    {
        var budget = budgetSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : (TimeSpan?)null;
        Assert.Equal(retries, OracleMigrator.ShouldRetry(attempt, TimeSpan.FromSeconds(elapsedSeconds), budget));
    }

    // Two migrators that create the same index at the same time can deadlock in the library cache. Oracle
    // rolls back one statement with ORA-04020, and the measured 16-node storm hit it about 1 run in 3. A
    // row deadlock (ORA-00060) gets the same retry. A real schema fault such as ORA-00942 still stops the
    // migration.
    [Theory]
    [InlineData(4020, true)]
    [InlineData(60, true)]
    [InlineData(12545, true)]
    [InlineData(942, false)]
    [InlineData(1031, false)]
    public void The_migrator_retries_deadlocks_and_connectivity_faults_only(int number, bool retries)
        => Assert.Equal(retries, OracleMigrator.IsTransient(number));

    // The store migrates on first use with a 5 s retry budget, and one DDL lock wait can last 30 s, so a
    // deadlock can reach the pump after the budget. The store must then mark it transient, or the group
    // halts. The database raises each code here, so the exception is the one ODP.NET gives.
    [Theory]
    [InlineData(4020)]
    [InlineData(60)]
    public async Task The_store_classifies_a_deadlock_transient(int number)
    {
        await using var connection = new OracleConnection(OracleTestDatabase.ConnectionString);
        await connection.OpenAsync();
        await using var raise = connection.CreateCommand();
        raise.CommandText = $"DECLARE e EXCEPTION; PRAGMA EXCEPTION_INIT(e, -{number}); BEGIN RAISE e; END;";
        var fault = await Assert.ThrowsAsync<OracleException>(() => raise.ExecuteNonQueryAsync());

        var store = new OracleJobStore(new OracleStoreOptions { ConnectionString = OracleTestDatabase.ConnectionString });
        Assert.Equal(number, fault.Number);
        Assert.True(store.IsTransientFault(fault), $"ORA-{fault.Number} must classify transient");
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
