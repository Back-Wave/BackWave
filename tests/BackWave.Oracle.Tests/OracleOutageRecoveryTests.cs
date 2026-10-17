using System.Text.Json.Serialization;
using BackWave.Core;
using BackWave.Hosting;
using BackWave.Jobs;
using BackWave.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Oracle.ManagedDataAccess.Client;

namespace BackWave.Oracle.Tests;

/// <summary>
/// Issue #12: after an outage a worker group on Oracle must claim and run jobs again without a process
/// restart. The outage is the database killing every session of the group while the pool still holds
/// them, so the next claim runs on a dead connection. The group runs as a private bw_recover user, so the
/// kill reaches this test's sessions and no other user's.
/// </summary>
[Collection("oracle")]
public sealed class OracleOutageRecoveryTests(Xunit.Abstractions.ITestOutputHelper output) : IAsyncLifetime
{
    private const string Schema = "bw_recover";
    private const string Group = "recovery-workers";

    // Pooling stays on (the default): the dead pooled session is the outage this test models. A minimum
    // pool size of zero stops the pool from opening a fresh session after ClearPool, which would block
    // the DROP USER at teardown.
    private static readonly string SchemaConnectionString =
        new OracleConnectionStringBuilder(OracleTestDatabase.ConnectionString)
        {
            UserID = Schema,
            Password = Schema,
            MinPoolSize = 0,
        }.ConnectionString;

    public Task InitializeAsync() => OracleTestDatabase.ResetUserAsync(Schema);

    public async Task DisposeAsync()
    {
        // Lock the user, so the pool cannot open a new session, then end every session it still has, so
        // the DROP USER does not wait on them.
        await LockUserAsync();
        OracleConnection.ClearPool(new OracleConnection(SchemaConnectionString));
        await DisconnectSessionsAsync();
        await OracleTestDatabase.DropUserAsync(Schema);
    }

    [Fact]
    public async Task A_worker_group_claims_and_runs_jobs_again_after_its_pooled_sessions_are_killed()
    {
        var store = new OracleJobStore(new OracleStoreOptions
        {
            ConnectionString = SchemaConnectionString,
            SchemaName = Schema,
            AutoMigrate = true,
        });
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddTransient<IJobHandler<RecoveryJob>, RecoveryHandler>();
        builder.Services.AddBackWave(backwave => backwave
            .UseStore(store)
            .UseRegistry(new JobRegistry(
            [
                JobRegistration.Create<RecoveryJob, RecoveryHandler>("recovery", OracleRecoveryJsonContext.Default.RecoveryJob),
            ]))
            .AddWorkerGroup(new WorkerGroupOptions
            {
                Name = Group,
                Policy = new DispatchPolicy.Strict(["default"]),
                PollInterval = TimeSpan.FromMilliseconds(200),
                MaxPollInterval = TimeSpan.Zero,
                LeaseDuration = TimeSpan.FromSeconds(30),
            }));
        using var host = builder.Build();
        var health = host.Services.GetRequiredService<BackWaveHealth>();
        var client = host.Services.GetRequiredService<BackWaveClient>();

        await host.StartAsync();
        try
        {
            // Baseline: the group runs a job before the outage.
            var before = await client.EnqueueAsync(new RecoveryJob(1), dueTime: DateTimeOffset.UtcNow);
            await WaitForSucceededAsync(store, health, before);

            var killed = await KillSessionsAsync();
            output.WriteLine($"killed {killed} {Schema} sessions");
            Assert.True(killed > 0, $"the group must hold at least one {Schema} session to kill");

            // The next poll takes a killed session from the pool. The group must degrade, not halt.
            var cause = await WaitForDegradedAsync(health);
            output.WriteLine($"degraded: {cause}");
            Assert.Contains("ORA-00028", cause);

            // Each failed poll drops one dead session from the pool. Wait until none is left, so the
            // enqueue below cannot take one either.
            await WaitForAsync(async () => await CountKilledSessionsAsync() == 0, health, "the pool to drop every killed session");

            // Recovery: a job enqueued after the outage is claimed and run by the same group.
            var after = await client.EnqueueAsync(new RecoveryJob(2), dueTime: DateTimeOffset.UtcNow);
            await WaitForSucceededAsync(store, health, after);
            AssertNotHalted(health);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    // Kills every session of the private user as SYSTEM. The filter is the username alone, so no session
    // of another user or test is touched. The outage kill is not IMMEDIATE on purpose: IMMEDIATE closes
    // the socket, and ODP.NET then drops the session from the pool before it hands it out. A plain kill
    // keeps the socket open, so the pool hands out the dead session and its next call raises the fault.
    private static async Task<int> KillSessionsAsync()
    {
        await using var admin = new OracleConnection(OracleTestDatabase.SystemConnectionString);
        await admin.OpenAsync();

        var sessions = new List<string>();
        await using (var select = admin.CreateCommand())
        {
            select.CommandText = "SELECT sid, serial# FROM v$session WHERE username = :username AND status <> 'KILLED'";
            select.Parameters.Add(new OracleParameter("username", Schema.ToUpperInvariant()));
            await using var reader = await select.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                sessions.Add($"{reader.GetInt32(0)},{reader.GetInt32(1)}");
            }
        }

        foreach (var session in sessions)
        {
            await using var kill = admin.CreateCommand();
            kill.CommandText = $"ALTER SYSTEM KILL SESSION '{session}'";
            try
            {
                await kill.ExecuteNonQueryAsync();
            }
            // ORA-00031: the session is busy, so Oracle marks it and ends it on its next call.
            // ORA-00030: the session ended after the select.
            catch (OracleException exception) when (exception.Number is 31 or 30)
            {
            }
        }
        return sessions.Count;
    }

    private static async Task LockUserAsync()
    {
        await using var admin = new OracleConnection(OracleTestDatabase.SystemConnectionString);
        await admin.OpenAsync();
        await using var lockUser = admin.CreateCommand();
        lockUser.CommandText = $"ALTER USER {Schema} ACCOUNT LOCK";
        await lockUser.ExecuteNonQueryAsync();
    }

    // Ends every session of the private user, a killed one too, and waits until none is left. A killed
    // session holds its process until its client calls again, and the client is gone at teardown.
    private static async Task DisconnectSessionsAsync()
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
                select.Parameters.Add(new OracleParameter("username", Schema.ToUpperInvariant()));
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

    // A killed session stays in v$session until its client makes a call and is told it is dead.
    private static async Task<int> CountKilledSessionsAsync()
    {
        await using var admin = new OracleConnection(OracleTestDatabase.SystemConnectionString);
        await admin.OpenAsync();
        await using var count = admin.CreateCommand();
        count.CommandText = "SELECT count(*) FROM v$session WHERE username = :username AND status = 'KILLED'";
        count.Parameters.Add(new OracleParameter("username", Schema.ToUpperInvariant()));
        return Convert.ToInt32(await count.ExecuteScalarAsync());
    }

    // Samples the health state until the group is degraded. The next clean poll clears the mark, so the
    // sample interval stays well under the poll interval.
    private static async Task<string> WaitForDegradedAsync(BackWaveHealth health)
    {
        var cause = "";
        await WaitForAsync(() => Task.FromResult(health.DegradedGroups.TryGetValue(Group, out cause!)), health,
            "the killed sessions to mark the group degraded", TimeSpan.FromMilliseconds(5));
        return cause;
    }

    private static Task WaitForSucceededAsync(OracleJobStore store, BackWaveHealth health, Guid jobId) =>
        WaitForAsync(async () => (await store.GetJobAsync(jobId))?.State == JobState.Succeeded, health,
            $"job {jobId} to reach Succeeded");

    // Polls the condition and fails the test the moment the group halts, so a fail-stop reports its cause
    // instead of a timeout.
    private static async Task WaitForAsync(
        Func<Task<bool>> condition, BackWaveHealth health, string description, TimeSpan? interval = null)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            AssertNotHalted(health);
            if (await condition())
            {
                return;
            }
            await Task.Delay(interval ?? TimeSpan.FromMilliseconds(50));
        }
        AssertNotHalted(health);
        Assert.Fail($"Timed out waiting for: {description}");
    }

    private static void AssertNotHalted(BackWaveHealth health) =>
        Assert.False(health.HaltedGroups.TryGetValue(Group, out var halt),
            $"the outage fail-stopped the group: {halt?.ExceptionType}: {halt?.Message}");
}

public sealed record RecoveryJob(int Index);

public sealed class RecoveryHandler : IJobHandler<RecoveryJob>
{
    public Task HandleAsync(RecoveryJob job, JobContext context, CancellationToken cancellationToken) => Task.CompletedTask;
}

[JsonSerializable(typeof(RecoveryJob))]
internal sealed partial class OracleRecoveryJsonContext : JsonSerializerContext;
