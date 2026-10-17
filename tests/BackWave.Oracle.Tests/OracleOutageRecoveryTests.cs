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
/// After an outage a worker group on Oracle must claim and run jobs again without a process
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
        await OutageTestSupport.LockUserAsync(Schema);
        OracleConnection.ClearPool(new OracleConnection(SchemaConnectionString));
        await OutageTestSupport.DisconnectSessionsAsync(Schema);
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
            var cause = await OutageTestSupport.WaitForDegradedAsync(health, Group, "the killed sessions to mark the group degraded");
            output.WriteLine($"degraded: {cause}");
            Assert.True(OracleTransientFaultTests.NamesTransientCode(cause), $"the degraded cause must name a transient code: {cause}");

            // Each failed poll drops one dead session from the pool. Wait until none is left, so the
            // enqueue below cannot take one either.
            await OutageTestSupport.WaitForAsync(async () => await CountKilledSessionsAsync() == 0, health, Group,
                "the pool to drop every killed session");

            // Recovery: a job enqueued after the outage is claimed and run by the same group.
            var after = await client.EnqueueAsync(new RecoveryJob(2), dueTime: DateTimeOffset.UtcNow);
            await WaitForSucceededAsync(store, health, after);
            OutageTestSupport.AssertNotHalted(health, Group);
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

    private static Task WaitForSucceededAsync(OracleJobStore store, BackWaveHealth health, Guid jobId) =>
        OutageTestSupport.WaitForAsync(async () => (await store.GetJobAsync(jobId))?.State == JobState.Succeeded, health,
            Group, $"job {jobId} to reach Succeeded");
}

public sealed record RecoveryJob(int Index);

public sealed class RecoveryHandler : IJobHandler<RecoveryJob>
{
    public Task HandleAsync(RecoveryJob job, JobContext context, CancellationToken cancellationToken) => Task.CompletedTask;
}

[JsonSerializable(typeof(RecoveryJob))]
internal sealed partial class OracleRecoveryJsonContext : JsonSerializerContext;
