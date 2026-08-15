using System.Text.Json.Serialization;
using BackWave.Core;
using BackWave.Driver;
using BackWave.Jobs;
using BackWave.Storage;
using BackWave.Storage.InMemory;
using BackWave.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BackWave.Tests;

// The reclaiming side of a lost lease (event 1204): when a sweep disposes leases whose owner stopped
// heartbeating, the reclaiming node logs the count. Event 1202 (LeaseLost) covers the losing side; this
// is its counterpart. Emitted once per sweep and only when the sweep actually reclaimed something, so a
// quiet sweep stays silent. Reached only through the pump's ExpireLeases command, never the Core factory
// path - it is one of the seams the logs-pillar acceptance table now names.

public sealed record ReclaimWork(string Note);

// Hangs on a gate so an execution stays in flight across pump calls, letting its lease lapse.
public sealed class ReclaimWorkHandler(ExecutionGate gate) : IJobHandler<ReclaimWork>
{
    public async Task HandleAsync(ReclaimWork job, JobContext context, CancellationToken cancellationToken)
        => await gate.Release.Task.WaitAsync(cancellationToken);
}

[JsonSerializable(typeof(ReclaimWork))]
internal sealed partial class ReclaimJsonContext : JsonSerializerContext;

public class LeaseReclaimLogTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(60);

    private static readonly RetryPolicy Policy = new()
    {
        MaxAttempts = 2,
        Backoff = _ => TimeSpan.FromMinutes(1),
    };

    private sealed class Fixture
    {
        public InMemoryJobStore Store { get; } = new();
        public BackWaveClient Client { get; }
        public ExecutionGate Gate { get; }
        private readonly JobRegistry _registry;
        private readonly IServiceProvider _services;

        public Fixture()
        {
            _services = new ServiceCollection()
                .AddSingleton<ExecutionGate>()
                .AddTransient<IJobHandler<ReclaimWork>, ReclaimWorkHandler>()
                .BuildServiceProvider();
            _registry = new JobRegistry(
            [
                JobRegistration.Create<ReclaimWork, ReclaimWorkHandler>(
                    "reclaim-work", ReclaimJsonContext.Default.ReclaimWork),
            ]);
            Gate = _services.GetRequiredService<ExecutionGate>();
            Client = new BackWaveClient(Store, _registry);
        }

        public DeterministicPump AddNode(string workerId, ILogger? logger = null)
        {
            var driver = new NodeDriver(new NodeOptions
            {
                WorkerId = workerId,
                Policy = new DispatchPolicy.Strict(["default"]),
                LeaseDuration = Lease,
                RetryPolicy = Policy,
            });
            return new DeterministicPump(
                driver, Store, _registry, _services, consumerGroup: workerId, logger: logger);
        }
    }

    [Fact]
    public async Task SweepThatReclaimsAnExpiredLease_LogsTheReclaimedCountOnce()
    {
        var capture = new LogCapture();
        var fixture = new Fixture();
        var crashed = fixture.AddNode("node-a");
        var healthy = fixture.AddNode(
            "node-b", new CapturingLoggerFactory(capture).CreateLogger("BackWave.Testing"));

        await fixture.Client.EnqueueAsync(new ReclaimWork("x"), dueTime: T0);

        // Node A claims and starts executing, then "crashes" - it never heartbeats again.
        await crashed.PumpAsync(T0);

        // A sweep before expiry reclaims nothing, so it must stay silent.
        await healthy.PumpAsync(T0.AddSeconds(30));
        Assert.DoesNotContain(capture.Records, r => r.EventId == 1204);

        // After expiry, node B's sweep disposes the lapsed lease and logs the reclaim.
        await healthy.PumpAsync(T0 + Lease + TimeSpan.FromSeconds(1));

        var reclaimed = Assert.Single(capture.Records, r => r.EventId == 1204);
        Assert.Equal(LogLevel.Information, reclaimed.Level);
        Assert.Contains("node-b", reclaimed.Message);
        Assert.Contains("reclaimed 1", reclaimed.Message);

        fixture.Gate.Release.SetResult();
    }

    [Fact]
    public async Task LosingWorker_WhoseLeaseWasReclaimed_LogsLeaseLostOnItsNextHeartbeat()
    {
        var capture = new LogCapture();
        var fixture = new Fixture();
        // The losing node carries the capturing logger this time: 1202 is the losing side (contrast the
        // reclaiming side's 1204), emitted when the node discovers its lease was stolen.
        var losing = fixture.AddNode(
            "node-a", new CapturingLoggerFactory(capture).CreateLogger("BackWave.Testing"));
        var healthy = fixture.AddNode("node-b");

        var jobId = await fixture.Client.EnqueueAsync(new ReclaimWork("x"), dueTime: T0);

        // Node A claims and parks in the gated handler, then goes silent (never heartbeats on its own).
        await losing.PumpAsync(T0);

        // After expiry, node B's sweep disposes the lapsed lease and hands the work to redelivery.
        await healthy.PumpAsync(T0 + Lease + TimeSpan.FromSeconds(1));

        // Node A only learns its lease is gone when it next heartbeats: the store reports the lease not
        // renewed, the Driver abandons the in-flight Attempt, and the losing side logs 1202 (Warning).
        await losing.HeartbeatAsync(T0 + Lease + TimeSpan.FromSeconds(1));

        var lost = Assert.Single(capture.Records, r => r.EventId == 1202);
        Assert.Equal(LogLevel.Warning, lost.Level);
        Assert.Equal(jobId, ScopeValue(lost, "job_id"));
        Assert.Equal("reclaim-work", ScopeValue(lost, "wire_name"));

        fixture.Gate.Release.SetResult();
    }

    private static object? ScopeValue(LogRecord record, string key)
        => record.Scope.First(kv => kv.Key == key).Value;

    [Fact]
    public async Task SweepWithNoExpiredLeases_EmitsNoReclaimLog()
    {
        var capture = new LogCapture();
        var fixture = new Fixture();
        var node = fixture.AddNode(
            "node-a", new CapturingLoggerFactory(capture).CreateLogger("BackWave.Testing"));

        // No work is enqueued, so every sweep reclaims nothing across a long horizon.
        await node.PumpAsync(T0);
        await node.PumpAsync(T0 + Lease + TimeSpan.FromMinutes(5));

        Assert.DoesNotContain(capture.Records, r => r.EventId == 1204);
    }
}
