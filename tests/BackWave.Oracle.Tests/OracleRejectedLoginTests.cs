using BackWave.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Oracle.ManagedDataAccess.Client;

namespace BackWave.Oracle.Tests;

/// <summary>
/// The negative case for the outage tests: a fault that is not an outage must still halt the group. A user
/// that does not exist raises ORA-01017 on the live listener. The test does not use a wrong password for a
/// real user, because repeated failed logins can lock that account. This is an integration test: it needs
/// "docker compose up -d oracle".
/// </summary>
[Collection("oracle")]
public sealed class OracleRejectedLoginTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private const string Group = OracleTransientFaultTests.Group;

    [Fact]
    public async Task A_rejected_login_halts_the_worker_group()
    {
        var store = new OracleJobStore(new OracleStoreOptions
        {
            ConnectionString = new OracleConnectionStringBuilder(OracleTestDatabase.ConnectionString)
            {
                UserID = "bw_no_such_user",
                Password = "bw_no_such_user",
                ConnectionTimeout = 3,
            }.ConnectionString,
        });
        using var host = OracleTransientFaultTests.BuildHost(store);
        var health = host.Services.GetRequiredService<BackWaveHealth>();

        await host.StartAsync();
        try
        {
            // A listener fault on an early poll only degrades the group, so the wait ignores degrades and
            // ends at the halt.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while (DateTime.UtcNow < deadline && !health.HaltedGroups.ContainsKey(Group))
            {
                await Task.Delay(50);
            }

            Assert.True(health.HaltedGroups.TryGetValue(Group, out var halt), "a rejected login must halt the group");
            output.WriteLine($"halted: {halt.ExceptionType}: {halt.Message}");
            Assert.Contains("ORA-01017", halt.Message);
        }
        finally
        {
            await host.StopAsync();
        }
    }
}
