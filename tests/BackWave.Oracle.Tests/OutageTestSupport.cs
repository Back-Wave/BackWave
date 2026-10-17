using BackWave.Hosting;
using Oracle.ManagedDataAccess.Client;

namespace BackWave.Oracle.Tests;

/// <summary>The health waits and the private-user teardown that the outage tests share.</summary>
internal static class OutageTestSupport
{
    // A halt of one pump counts too, so the check reads the partly halted groups as well.
    public static void AssertNotHalted(BackWaveHealth health, string group)
    {
        var halted = health.HaltedGroups.TryGetValue(group, out var halt)
            || health.PartiallyHaltedGroups.TryGetValue(group, out halt);
        Assert.False(halted, $"the outage fail-stopped the group: {halt?.ExceptionType}: {halt?.Message}");
    }

    // Polls the condition and fails the test the moment the group halts, so a fail-stop reports its cause
    // instead of a timeout.
    public static async Task WaitForAsync(
        Func<Task<bool>> condition, BackWaveHealth health, string group, string description,
        TimeSpan? interval = null)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            AssertNotHalted(health, group);
            if (await condition())
            {
                return;
            }
            await Task.Delay(interval ?? TimeSpan.FromMilliseconds(50));
        }
        AssertNotHalted(health, group);
        Assert.Fail($"Timed out waiting for: {description}");
    }

    // Samples the health state until the group is degraded. The next clean poll clears the mark, so the
    // sample interval stays well under the poll interval.
    public static async Task<string> WaitForDegradedAsync(BackWaveHealth health, string group, string description)
    {
        var cause = "";
        await WaitForAsync(() => Task.FromResult(health.DegradedGroups.TryGetValue(group, out cause!)), health,
            group, description, TimeSpan.FromMilliseconds(5));
        return cause;
    }

    public static async Task LockUserAsync(string user)
    {
        await using var admin = new OracleConnection(OracleTestDatabase.SystemConnectionString);
        await admin.OpenAsync();
        await using var lockUser = admin.CreateCommand();
        lockUser.CommandText = $"ALTER USER {user} ACCOUNT LOCK";
        await lockUser.ExecuteNonQueryAsync();
    }

    // Ends every session of the private user, a killed one too, and waits until none is left. A killed
    // session holds its process until its client calls again, and the client is gone at teardown.
    public static async Task DisconnectSessionsAsync(string user)
    {
        await using var admin = new OracleConnection(OracleTestDatabase.SystemConnectionString);
        await admin.OpenAsync();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (true)
        {
            var sessions = new List<string>();
            await using (var select = admin.CreateCommand())
            {
                select.CommandText = "SELECT sid, serial# FROM v$session WHERE username = :username";
                select.Parameters.Add(new OracleParameter("username", user.ToUpperInvariant()));
                await using var reader = await select.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    sessions.Add($"{reader.GetInt32(0)},{reader.GetInt32(1)}");
                }
            }
            if (sessions.Count == 0 || DateTime.UtcNow > deadline)
            {
                return;
            }

            foreach (var session in sessions)
            {
                await using var disconnect = admin.CreateCommand();
                disconnect.CommandText = $"ALTER SYSTEM DISCONNECT SESSION '{session}' IMMEDIATE";
                try
                {
                    await disconnect.ExecuteNonQueryAsync();
                }
                catch (OracleException exception) when (exception.Number is 31 or 30)
                {
                }
            }
            await Task.Delay(200);
        }
    }
}
