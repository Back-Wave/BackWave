using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
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
/// A worker group on Oracle that boots while its database refuses connections must degrade, not halt, and
/// run jobs once the database answers. The store connects through a loopback port that nothing listens on,
/// so ODP.NET gets a refused connect. A TCP forwarder then opens that port in front of the real database,
/// which models the database coming back. The group runs as a private bw_refused user, so the teardown
/// reaches this test's sessions and no other user's.
/// </summary>
[Collection("oracle")]
public sealed class OracleRefusedConnectionRecoveryTests(Xunit.Abstractions.ITestOutputHelper output) : IAsyncLifetime
{
    private const string Schema = "bw_refused";
    private const string Group = "refused-workers";

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    private static readonly int Port = ReserveFreePort();

    // The store reaches the database only through the forwarder port. A minimum pool size of zero stops the
    // pool from opening a fresh session after ClearPool, which would block the DROP USER at teardown. The
    // short connect timeout keeps a connect that hangs from outlasting the test.
    private static readonly string SchemaConnectionString =
        new OracleConnectionStringBuilder(OracleTestDatabase.ConnectionString)
        {
            UserID = Schema,
            Password = Schema,
            DataSource = $"127.0.0.1:{Port}/{Upstream.Service}",
            MinPoolSize = 0,
            ConnectionTimeout = 3,
        }.ConnectionString;

    private TcpForwarder? _forwarder;

    public Task InitializeAsync() => OracleTestDatabase.ResetUserAsync(Schema);

    public async Task DisposeAsync()
    {
        // Lock the user, so the pool cannot open a new session, then end every session it still has, so
        // the DROP USER does not wait on them.
        await OutageTestSupport.LockUserAsync(Schema);
        OracleConnection.ClearPool(new OracleConnection(SchemaConnectionString));
        await OutageTestSupport.DisconnectSessionsAsync(Schema);
        await OracleTestDatabase.DropUserAsync(Schema);
        if (_forwarder is not null)
        {
            await _forwarder.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_worker_group_that_boots_while_connections_are_refused_runs_jobs_once_the_database_answers()
    {
        var store = new OracleJobStore(new OracleStoreOptions
        {
            ConnectionString = SchemaConnectionString,
            SchemaName = Schema,
            AutoMigrate = true,
        });
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddTransient<IJobHandler<RefusedJob>, RefusedHandler>();
        builder.Services.AddBackWave(backwave => backwave
            .UseStore(store)
            .UseRegistry(new JobRegistry(
            [
                JobRegistration.Create<RefusedJob, RefusedHandler>("refused", OracleRefusedJsonContext.Default.RefusedJob),
            ]))
            .AddWorkerGroup(new WorkerGroupOptions
            {
                Name = Group,
                Policy = new DispatchPolicy.Strict(["default"]),
                PollInterval = PollInterval,
                MaxPollInterval = TimeSpan.Zero,
                LeaseDuration = TimeSpan.FromSeconds(30),
            }));
        using var host = builder.Build();
        var health = host.Services.GetRequiredService<BackWaveHealth>();
        var client = host.Services.GetRequiredService<BackWaveClient>();

        await host.StartAsync();
        try
        {
            // The port is closed, so the first poll's migration gets a refused connect. The group must
            // degrade, not halt: the host degrades a group only on a fault the store calls transient.
            var cause = await OutageTestSupport.WaitForDegradedAsync(health, Group, "the refused connect to mark the group degraded");
            output.WriteLine($"degraded while refused: {cause}");
            Assert.Matches(@"ORA-\d+", cause);

            // An enqueue while the port is closed raises the same fault to the caller. The store must call it
            // transient, so the caller can retry it as the group does.
            var fault = await Assert.ThrowsAsync<OracleException>(
                async () => await client.EnqueueAsync(new RefusedJob(0), dueTime: DateTimeOffset.UtcNow));
            output.WriteLine($"enqueue while refused: ORA-{fault.Number:D5}");
            Assert.True(store.IsTransientFault(fault), $"ORA-{fault.Number:D5} must be transient: {fault.Message}");

            // Each poll runs the migration again and gets refused again. The enqueue above outlasted at
            // least one more of those polls. None of them may halt the group.
            await WatchNotHaltedAsync(health, 3 * PollInterval);

            // The database answers again.
            _forwarder = TcpForwarder.Start(Port, Upstream.Host, Upstream.Port);

            // Recovery: a job enqueued after the database answers is claimed and run by the same group.
            var first = await client.EnqueueAsync(new RefusedJob(1), dueTime: DateTimeOffset.UtcNow);
            await WaitForSucceededAsync(store, health, first);
            OutageTestSupport.AssertNotHalted(health, Group);

            // A drop mid-session: the forwarder closes every live connection, so no pooled session is
            // usable. The job is due after the drop, so the group must claim it on a fresh session. The pool
            // discards an idle connection with a closed socket when it hands it out, so the drop can pass
            // without a fault; a call in flight at the drop gets ORA-12537.
            var second = await client.EnqueueAsync(new RefusedJob(2), dueTime: DateTimeOffset.UtcNow.AddSeconds(1));
            var dropped = _forwarder.DropConnections();
            output.WriteLine($"dropped {dropped} live connections");
            Assert.True(dropped > 0, "the group must hold at least one live connection to drop");
            var dropCause = await WaitForSucceededAsync(store, health, second);
            output.WriteLine($"degraded after drop: {dropCause ?? "never"}");
            OutageTestSupport.AssertNotHalted(health, Group);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    // Binds a listener on port 0 to get a free loopback port, then stops it, so nothing listens there until
    // the forwarder starts. Only a port that nothing holds gets a refused connect (ORA-50201): on macOS a
    // bound socket that does not listen makes the connect wait for its timeout (ORA-50000). Another
    // process can take the port in the meantime, but then the connect is not refused or the forwarder
    // cannot bind, so the test fails and never passes by mistake.
    private static int ReserveFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    // The real database endpoint, from a host:port/service data source.
    private static class Upstream
    {
        private static readonly string DataSource =
            new OracleConnectionStringBuilder(OracleTestDatabase.ConnectionString).DataSource;

        public static readonly string Host = DataSource[..DataSource.IndexOf(':')];
        public static readonly int Port =
            int.Parse(DataSource[(DataSource.IndexOf(':') + 1)..DataSource.IndexOf('/')]);
        public static readonly string Service = DataSource[(DataSource.IndexOf('/') + 1)..];
    }

    private static async Task WatchNotHaltedAsync(BackWaveHealth health, TimeSpan duration)
    {
        var deadline = DateTime.UtcNow + duration;
        while (DateTime.UtcNow < deadline)
        {
            OutageTestSupport.AssertNotHalted(health, Group);
            await Task.Delay(TimeSpan.FromMilliseconds(20));
        }
        OutageTestSupport.AssertNotHalted(health, Group);
    }

    // Returns the first degraded cause seen while waiting, or null when the group never degraded.
    private static async Task<string?> WaitForSucceededAsync(OracleJobStore store, BackWaveHealth health, Guid jobId)
    {
        string? cause = null;
        await OutageTestSupport.WaitForAsync(async () =>
        {
            if (cause is null && health.DegradedGroups.TryGetValue(Group, out var degraded))
            {
                cause = degraded;
            }
            return (await store.GetJobAsync(jobId))?.State == JobState.Succeeded;
        }, health, Group, $"job {jobId} to reach Succeeded", TimeSpan.FromMilliseconds(5));
        return cause;
    }

    // Accepts each client on a loopback port and pumps its bytes both ways to the upstream endpoint.
    private sealed class TcpForwarder : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly string _upstreamHost;
        private readonly int _upstreamPort;
        private readonly CancellationTokenSource _stopping = new();
        private readonly ConcurrentDictionary<Task, (TcpClient Client, TcpClient Upstream)> _connections = new();
        private readonly Task _accepting;

        private TcpForwarder(int port, string upstreamHost, int upstreamPort)
        {
            _upstreamHost = upstreamHost;
            _upstreamPort = upstreamPort;
            _listener = new TcpListener(IPAddress.Loopback, port);
            _listener.Start();
            _accepting = AcceptAsync();
        }

        public static TcpForwarder Start(int port, string upstreamHost, int upstreamPort) =>
            new(port, upstreamHost, upstreamPort);

        // Closes every live connection and returns how many there were. The listener stays open, so a
        // new connect still reaches the database.
        public int DropConnections()
        {
            var live = _connections.Values.ToList();
            foreach (var (client, upstream) in live)
            {
                client.Dispose();
                upstream.Dispose();
            }
            return live.Count;
        }

        public async ValueTask DisposeAsync()
        {
            _stopping.Cancel();
            _listener.Stop();
            DropConnections();
            await Task.WhenAll(_connections.Keys.Append(_accepting));
            _stopping.Dispose();
        }

        private async Task AcceptAsync()
        {
            try
            {
                while (true)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stopping.Token);
                    var upstream = new TcpClient();
                    var completed = new TaskCompletionSource();
                    _connections[completed.Task] = (client, upstream);
                    _ = PumpAsync(client, upstream, completed);
                }
            }
            catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException)
            {
            }
        }

        // Pumps both ways until either side closes, then closes both, so the other side sees the drop too.
        private async Task PumpAsync(TcpClient client, TcpClient upstream, TaskCompletionSource completed)
        {
            try
            {
                await upstream.ConnectAsync(_upstreamHost, _upstreamPort, _stopping.Token);
                var clientStream = client.GetStream();
                var upstreamStream = upstream.GetStream();
                var copies = new[]
                {
                    clientStream.CopyToAsync(upstreamStream, _stopping.Token),
                    upstreamStream.CopyToAsync(clientStream, _stopping.Token),
                };
                await Task.WhenAny(copies);
                client.Dispose();
                upstream.Dispose();
                await Task.WhenAll(copies);
            }
            catch (Exception exception) when (exception is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
            {
            }
            finally
            {
                client.Dispose();
                upstream.Dispose();
                _connections.TryRemove(completed.Task, out _);
                completed.SetResult();
            }
        }
    }
}

public sealed record RefusedJob(int Index);

public sealed class RefusedHandler : IJobHandler<RefusedJob>
{
    public Task HandleAsync(RefusedJob job, JobContext context, CancellationToken cancellationToken) => Task.CompletedTask;
}

[JsonSerializable(typeof(RefusedJob))]
internal sealed partial class OracleRefusedJsonContext : JsonSerializerContext;
