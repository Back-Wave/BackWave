using System.Text.Json.Serialization;
using BackWave.Core;
using BackWave.Driver;
using BackWave.Jobs;
using BackWave.Operations;
using BackWave.Storage;
using BackWave.Testing;
using BackWave.Storage.InMemory;
using Microsoft.Extensions.DependencyInjection;

namespace BackWave.Tests;

public sealed record SyncInventory(string Sku);

/// <summary>Waits on a test-controlled gate, so executions can stay in flight across pump calls.</summary>
public sealed class SyncInventoryHandler(ExecutionGate gate) : IJobHandler<SyncInventory>
{
    public async Task HandleAsync(SyncInventory job, JobContext context, CancellationToken cancellationToken)
    {
        gate.Started.Add(context.Attempt);
        try
        {
            await gate.Release.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            gate.FinallyRan = true;
        }
    }
}

public sealed class ExecutionGate
{
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public List<int> Started { get; } = [];
    public bool FinallyRan { get; set; }
}

[JsonSerializable(typeof(SyncInventory))]
internal sealed partial class LeaseJsonContext : JsonSerializerContext;

public class LeaseTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(60);

    private static readonly RetryPolicy Policy = new()
    {
        MaxAttempts = 2,
        Backoff = _ => TimeSpan.FromMinutes(1),
    };

    private sealed record Node(NodeDriver Driver, DeterministicPump Pump);

    private sealed class Cluster
    {
        public InMemoryJobStore Store { get; } = new();
        public BackWaveClient Client { get; }
        public ExecutionGate Gate { get; }
        private readonly JobRegistry _registry;
        private readonly IServiceProvider _services;

        public Cluster()
        {
            _services = new ServiceCollection()
                .AddSingleton<ExecutionGate>()
                .AddTransient<IJobHandler<SyncInventory>, SyncInventoryHandler>()
                .BuildServiceProvider();
            _registry = new JobRegistry(
            [
                JobRegistration.Create<SyncInventory, SyncInventoryHandler>(
                    "sync-inventory", LeaseJsonContext.Default.SyncInventory),
            ]);
            Gate = _services.GetRequiredService<ExecutionGate>();
            Client = new BackWaveClient(Store, _registry);
        }

        public Node AddNode(string workerId)
        {
            var driver = new NodeDriver(new NodeOptions
            {
                WorkerId = workerId,
                Policy = new Core.DispatchPolicy.Strict(["default"]),
                LeaseDuration = Lease,
                RetryPolicy = Policy,
            });
            return new Node(driver, new DeterministicPump(driver, Store, _registry, _services));
        }
    }

    [Fact]
    public async Task ExpiredLease_IsRecoveredByAnotherNode_ExpiryCountsAsAttempt()
    {
        var cluster = new Cluster();
        var crashed = cluster.AddNode("node-a");
        var healthy = cluster.AddNode("node-b");

        var jobId = await cluster.Client.EnqueueAsync(new SyncInventory("sku-1"), dueTime: T0);

        // Node A claims and starts executing, then "crashes": it never heartbeats again.
        await crashed.Pump.PumpAsync(T0);
        Assert.Equal([1], cluster.Gate.Started);

        // Before the Lease expires, node B sees nothing claimable (invariant I1).
        await healthy.Pump.PumpAsync(T0.AddSeconds(30));
        Assert.Equal([1], cluster.Gate.Started);

        // After expiry, B's poll disposes the Lease: attempt 1 < ceiling 2, so the job
        // reschedules at the backoff instant — and only then can B claim it.
        var afterExpiry = T0 + Lease + TimeSpan.FromSeconds(1);
        await healthy.Pump.PumpAsync(afterExpiry);
        var rescheduled = await cluster.Store.GetJobAsync(jobId);
        Assert.Equal(JobState.Scheduled, rescheduled!.State);
        Assert.Equal(afterExpiry + TimeSpan.FromMinutes(1), rescheduled.DueTime);

        cluster.Gate.Release.SetResult();
        await healthy.Pump.PumpAsync(rescheduled.DueTime);
        Assert.Equal([1, 2], cluster.Gate.Started);
        Assert.Equal(JobState.Succeeded, (await cluster.Store.GetJobAsync(jobId))!.State);
    }

    [Fact]
    public async Task RepeatedWorkerKiller_DeadLettersThroughTheNormalCeiling()
    {
        var cluster = new Cluster();
        var node = cluster.AddNode("node-a");

        var jobId = await cluster.Client.EnqueueAsync(new SyncInventory("sku-2"), dueTime: T0);

        // Attempt 1 claims and hangs; its Lease expires and the job reschedules.
        await node.Pump.PumpAsync(T0);
        var afterFirstExpiry = T0 + Lease + TimeSpan.FromSeconds(1);
        await node.Pump.PumpAsync(afterFirstExpiry);

        // Attempt 2 claims and hangs; this expiry hits the ceiling → Dead-Lettered.
        var secondDue = afterFirstExpiry + TimeSpan.FromMinutes(1);
        await node.Pump.PumpAsync(secondDue);
        Assert.Equal([1, 2], cluster.Gate.Started);

        await node.Pump.PumpAsync(secondDue + Lease + TimeSpan.FromSeconds(1));
        var job = await cluster.Store.GetJobAsync(jobId);
        Assert.Equal(JobState.DeadLettered, job!.State);
        Assert.Equal(2, job.Attempt);
        Assert.Contains("attempt ceiling", job.TerminalCause);
    }

    [Fact]
    public async Task HeartbeatRenewal_KeepsTheLeaseAlive()
    {
        var cluster = new Cluster();
        var node = cluster.AddNode("node-a");

        var jobId = await cluster.Client.EnqueueAsync(new SyncInventory("sku-3"), dueTime: T0);
        await node.Pump.PumpAsync(T0);

        // Renew at T0+40s; at T0+70s the original expiry (T0+60s) has passed but the
        // renewed one (T0+100s) has not — the job must still be leased to node-a.
        await node.Pump.HeartbeatAsync(T0.AddSeconds(40));
        await node.Pump.PumpAsync(T0.AddSeconds(70));

        var job = await cluster.Store.GetJobAsync(jobId);
        Assert.Equal(JobState.Leased, job!.State);
        Assert.Equal("node-a", job.LeaseOwner);
        Assert.Equal([1], cluster.Gate.Started);
    }

    [Fact]
    public async Task CancellingAnExecutingJob_IsCooperative_FinallyRuns()
    {
        var cluster = new Cluster();
        var node = cluster.AddNode("node-a");

        var jobId = await cluster.Client.EnqueueAsync(new SyncInventory("sku-4"), dueTime: T0);
        await node.Pump.PumpAsync(T0);
        Assert.Equal([1], cluster.Gate.Started);

        // Operator cancel of an executing job: flag only — nothing observable yet.
        var result = await new BackWaveOperator(cluster.Store).CancelJobAsync(jobId, "operator-cancel", T0.AddSeconds(5));
        Assert.Equal(CancelResult.CancellationRequested, result);
        Assert.False(cluster.Gate.FinallyRan);

        // The next heartbeat round-trip fires the handler's CancellationToken.
        await node.Pump.HeartbeatAsync(T0.AddSeconds(10));

        var job = await cluster.Store.GetJobAsync(jobId);
        Assert.Equal(JobState.Cancelled, job!.State);
        Assert.Equal("operator-cancel", job.TerminalCause);
        Assert.True(cluster.Gate.FinallyRan);
    }

    [Fact]
    public async Task CancellingAPendingJob_CancelsImmediately_NeverRuns()
    {
        var cluster = new Cluster();
        var node = cluster.AddNode("node-a");

        var jobId = await cluster.Client.EnqueueAsync(new SyncInventory("sku-5"), dueTime: T0.AddHours(1));

        var result = await new BackWaveOperator(cluster.Store).CancelJobAsync(jobId, "operator-cancel", T0);
        Assert.Equal(CancelResult.CancelledImmediately, result);

        await node.Pump.PumpAsync(T0.AddHours(2));
        Assert.Empty(cluster.Gate.Started);
        Assert.Equal(JobState.Cancelled, (await cluster.Store.GetJobAsync(jobId))!.State);
    }
}
