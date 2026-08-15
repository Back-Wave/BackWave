using System.Text.Json.Serialization;
using BackWave.Driver;
using BackWave.Jobs;
using BackWave.Operations;
using BackWave.Storage;
using BackWave.Testing;
using BackWave.Storage.InMemory;
using Microsoft.Extensions.DependencyInjection;

namespace BackWave.Tests;

public sealed record ChargeOrder(string OrderId);
public sealed record SendReceipt(string OrderId);
public sealed record ReleaseHold(string OrderId);

public sealed class DependencyRecorder
{
    public List<string> Ran { get; } = [];
    public bool ChargeFails { get; set; }
}

public sealed class ChargeOrderHandler(DependencyRecorder recorder) : IJobHandler<ChargeOrder>
{
    public Task HandleAsync(ChargeOrder job, JobContext context, CancellationToken cancellationToken)
    {
        recorder.Ran.Add($"charge:{job.OrderId}");
        return recorder.ChargeFails
            ? throw new InvalidOperationException("card declined")
            : Task.CompletedTask;
    }
}

public sealed class SendReceiptHandler(DependencyRecorder recorder) : IJobHandler<SendReceipt>
{
    public Task HandleAsync(SendReceipt job, JobContext context, CancellationToken cancellationToken)
    {
        recorder.Ran.Add($"receipt:{job.OrderId}");
        return Task.CompletedTask;
    }
}

public sealed class ReleaseHoldHandler(DependencyRecorder recorder) : IJobHandler<ReleaseHold>
{
    public Task HandleAsync(ReleaseHold job, JobContext context, CancellationToken cancellationToken)
    {
        recorder.Ran.Add($"release:{job.OrderId}");
        return Task.CompletedTask;
    }
}

[JsonSerializable(typeof(ChargeOrder))]
[JsonSerializable(typeof(SendReceipt))]
[JsonSerializable(typeof(ReleaseHold))]
internal sealed partial class DependencyJsonContext : JsonSerializerContext;

public class DependencyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private sealed class Fixture
    {
        public InMemoryJobStore Store { get; } = new();
        public BackWaveClient Client { get; }
        public DeterministicPump Pump { get; }
        public DependencyRecorder Recorder { get; }

        public Fixture()
        {
            var services = new ServiceCollection()
                .AddSingleton<DependencyRecorder>()
                .AddTransient<IJobHandler<ChargeOrder>, ChargeOrderHandler>()
                .AddTransient<IJobHandler<SendReceipt>, SendReceiptHandler>()
                .AddTransient<IJobHandler<ReleaseHold>, ReleaseHoldHandler>()
                .BuildServiceProvider();
            var registry = new JobRegistry(
            [
                JobRegistration.Create<ChargeOrder, ChargeOrderHandler>(
                    "charge-order", DependencyJsonContext.Default.ChargeOrder),
                JobRegistration.Create<SendReceipt, SendReceiptHandler>(
                    "send-receipt", DependencyJsonContext.Default.SendReceipt),
                JobRegistration.Create<ReleaseHold, ReleaseHoldHandler>(
                    "release-hold", DependencyJsonContext.Default.ReleaseHold),
            ]);
            Recorder = services.GetRequiredService<DependencyRecorder>();
            Client = new BackWaveClient(Store, registry);
            var driver = new NodeDriver(new NodeOptions
            {
                WorkerId = "node-1",
                Policy = new Core.DispatchPolicy.Strict(["default"]),
                RetryPolicy = new Core.RetryPolicy { MaxAttempts = 1 },
            });
            Pump = new DeterministicPump(driver, Store, registry, services);
        }
    }

    [Fact]
    public async Task OnSuccess_RunsAfterParentSucceeds()
    {
        var fixture = new Fixture();
        var parent = await fixture.Client.EnqueueAsync(new ChargeOrder("o1"), dueTime: T0);
        var child = await fixture.Client.EnqueueDependencyAsync(new SendReceipt("o1"), parent, enqueuedAt: T0);

        Assert.Equal(JobState.AwaitingParent, (await fixture.Store.GetJobAsync(child))!.State);

        await fixture.Pump.PumpAsync(T0);
        Assert.Equal(["charge:o1", "receipt:o1"], fixture.Recorder.Ran);
        Assert.Equal(JobState.Succeeded, (await fixture.Store.GetJobAsync(child))!.State);
    }

    [Fact]
    public async Task OnSuccess_ParentDeadLetters_ChildCancelledWithCause()
    {
        var fixture = new Fixture();
        fixture.Recorder.ChargeFails = true;

        var parent = await fixture.Client.EnqueueAsync(new ChargeOrder("o2"), dueTime: T0);
        var child = await fixture.Client.EnqueueDependencyAsync(new SendReceipt("o2"), parent, enqueuedAt: T0);

        await fixture.Pump.PumpAsync(T0);

        Assert.Equal(JobState.DeadLettered, (await fixture.Store.GetJobAsync(parent))!.State);
        var childJob = await fixture.Store.GetJobAsync(child);
        Assert.Equal(JobState.Cancelled, childJob!.State);
        Assert.Equal("parent-failure:DeadLettered", childJob.TerminalCause);
        Assert.DoesNotContain("receipt:o2", fixture.Recorder.Ran);
    }

    [Fact]
    public async Task OnAnyTerminal_RunsEvenWhenParentDeadLetters()
    {
        var fixture = new Fixture();
        fixture.Recorder.ChargeFails = true;

        var parent = await fixture.Client.EnqueueAsync(new ChargeOrder("o3"), dueTime: T0);
        var child = await fixture.Client.EnqueueDependencyAsync(
            new ReleaseHold("o3"), parent, enqueuedAt: T0, DependencyMode.OnAnyTerminal);

        await fixture.Pump.PumpAsync(T0);

        Assert.Equal(JobState.DeadLettered, (await fixture.Store.GetJobAsync(parent))!.State);
        Assert.Equal(JobState.Succeeded, (await fixture.Store.GetJobAsync(child))!.State);
        Assert.Contains("release:o3", fixture.Recorder.Ran);
    }

    [Fact]
    public async Task OnAnyTerminal_RunsWhenParentIsCancelled()
    {
        var fixture = new Fixture();
        var parent = await fixture.Client.EnqueueAsync(new ChargeOrder("o4"), dueTime: T0.AddHours(1));
        var child = await fixture.Client.EnqueueDependencyAsync(
            new ReleaseHold("o4"), parent, enqueuedAt: T0, DependencyMode.OnAnyTerminal);

        await new BackWaveOperator(fixture.Store).CancelJobAsync(parent, "operator-cancel", T0);
        await fixture.Pump.PumpAsync(T0);

        Assert.Equal(["release:o4"], fixture.Recorder.Ran);
        Assert.Equal(JobState.Succeeded, (await fixture.Store.GetJobAsync(child))!.State);
    }

    [Fact]
    public async Task DependencyOfAnAlreadyTerminalParent_ResolvesAtEnqueue()
    {
        var fixture = new Fixture();
        var parent = await fixture.Client.EnqueueAsync(new ChargeOrder("o5"), dueTime: T0);
        await fixture.Pump.PumpAsync(T0);
        Assert.Equal(JobState.Succeeded, (await fixture.Store.GetJobAsync(parent))!.State);

        var child = await fixture.Client.EnqueueDependencyAsync(
            new SendReceipt("o5"), parent, enqueuedAt: T0.AddMinutes(1));
        Assert.Equal(JobState.Scheduled, (await fixture.Store.GetJobAsync(child))!.State);

        await fixture.Pump.PumpAsync(T0.AddMinutes(1));
        Assert.Contains("receipt:o5", fixture.Recorder.Ran);
    }

    [Fact]
    public async Task FailureCascades_ThroughChainsOfOnSuccessDependencies()
    {
        var fixture = new Fixture();
        fixture.Recorder.ChargeFails = true;

        var a = await fixture.Client.EnqueueAsync(new ChargeOrder("o6"), dueTime: T0);
        var b = await fixture.Client.EnqueueDependencyAsync(new SendReceipt("o6"), a, enqueuedAt: T0);
        var c = await fixture.Client.EnqueueDependencyAsync(new SendReceipt("o6-followup"), b, enqueuedAt: T0);

        await fixture.Pump.PumpAsync(T0);

        Assert.Equal(JobState.Cancelled, (await fixture.Store.GetJobAsync(b))!.State);
        var grandchild = await fixture.Store.GetJobAsync(c);
        Assert.Equal(JobState.Cancelled, grandchild!.State);
        Assert.Equal("parent-failure:Cancelled", grandchild.TerminalCause);
    }

    [Fact]
    public async Task UnknownParent_IsRejectedAtEnqueue()
    {
        var fixture = new Fixture();
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Client
            .EnqueueDependencyAsync(new SendReceipt("o7"), Guid.NewGuid(), enqueuedAt: T0)
            .AsTask());
    }
}
