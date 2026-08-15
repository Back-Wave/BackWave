using System.Text.Json.Serialization;
using BackWave.Core;
using BackWave.Driver;
using BackWave.Jobs;
using BackWave.Storage;
using BackWave.Testing;
using BackWave.Storage.InMemory;
using Microsoft.Extensions.DependencyInjection;

namespace BackWave.Tests;

public sealed record ChargeCard(string OrderId);

/// <summary>Fails until the configured Attempt, recording every try it sees.</summary>
public sealed class ChargeCardHandler(AttemptRecorder recorder) : IJobHandler<ChargeCard>
{
    public Task HandleAsync(ChargeCard job, JobContext context, CancellationToken cancellationToken)
    {
        recorder.Attempts.Add(context.Attempt);
        if (context.Attempt >= recorder.SucceedOnAttempt)
        {
            return Task.CompletedTask;
        }
        throw recorder.FailWithTaskCanceled
            ? new TaskCanceledException($"http timeout (attempt {context.Attempt})")
            : new InvalidOperationException($"gateway timeout (attempt {context.Attempt})");
    }
}

public sealed class AttemptRecorder
{
    public int SucceedOnAttempt { get; set; } = int.MaxValue;

    /// <summary>Simulate a handler-internal cancellation (e.g. an HttpClient timeout).</summary>
    public bool FailWithTaskCanceled { get; set; }

    public List<int> Attempts { get; } = [];
}

[JsonSerializable(typeof(ChargeCard))]
internal sealed partial class FailureJsonContext : JsonSerializerContext;

public class FailurePathTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly RetryPolicy OneMinuteThreeAttempts = new()
    {
        MaxAttempts = 3,
        Backoff = _ => TimeSpan.FromMinutes(1),
    };

    private sealed record Fixture(
        BackWaveClient Client,
        DeterministicPump Pump,
        IJobStore Store,
        AttemptRecorder Recorder);

    private static Fixture CreateFixture(RetryPolicy policy)
    {
        var services = new ServiceCollection()
            .AddSingleton<AttemptRecorder>()
            .AddTransient<IJobHandler<ChargeCard>, ChargeCardHandler>()
            .BuildServiceProvider();

        var registry = new JobRegistry(
        [
            JobRegistration.Create<ChargeCard, ChargeCardHandler>(
                "charge-card", FailureJsonContext.Default.ChargeCard),
        ]);

        var store = new InMemoryJobStore();
        var driver = new NodeDriver(new NodeOptions
        {
            WorkerId = "node-1",
            Policy = new Core.DispatchPolicy.Strict(["default"]),
            RetryPolicy = policy,
        });
        var pump = new DeterministicPump(driver, store, registry, services);

        return new Fixture(new BackWaveClient(store, registry), pump, store, services.GetRequiredService<AttemptRecorder>());
    }

    [Fact]
    public async Task FailedAttempts_RetryAtBackoffInstants_NotBefore()
    {
        var fixture = CreateFixture(OneMinuteThreeAttempts);
        fixture.Recorder.SucceedOnAttempt = 2;

        var jobId = await fixture.Client.EnqueueAsync(new ChargeCard("order-1"), dueTime: T0);

        await fixture.Pump.PumpAsync(T0);
        Assert.Equal([1], fixture.Recorder.Attempts);
        Assert.Equal(JobState.Scheduled, (await fixture.Store.GetJobAsync(jobId))!.State);

        // Not due again until T0 + 1 minute.
        await fixture.Pump.PumpAsync(T0.AddSeconds(59));
        Assert.Equal([1], fixture.Recorder.Attempts);

        await fixture.Pump.PumpAsync(T0.AddMinutes(1));
        Assert.Equal([1, 2], fixture.Recorder.Attempts);
        Assert.Equal(JobState.Succeeded, (await fixture.Store.GetJobAsync(jobId))!.State);
    }

    [Fact]
    public async Task ExhaustedAttemptCeiling_DeadLetters_WithCauseRecorded()
    {
        var fixture = CreateFixture(OneMinuteThreeAttempts);

        var jobId = await fixture.Client.EnqueueAsync(new ChargeCard("order-2"), dueTime: T0);

        await fixture.Pump.PumpAsync(T0);
        await fixture.Pump.PumpAsync(T0.AddMinutes(1));
        await fixture.Pump.PumpAsync(T0.AddMinutes(2));

        Assert.Equal([1, 2, 3], fixture.Recorder.Attempts);

        var job = await fixture.Store.GetJobAsync(jobId);
        Assert.NotNull(job);
        Assert.Equal(JobState.DeadLettered, job.State);
        Assert.Equal(3, job.Attempt);
        Assert.Equal(T0.AddMinutes(2), job.TerminalAt);
        Assert.Contains("gateway timeout", job.TerminalCause);

        // Dead-Lettered is terminal: more time changes nothing without an Operator Action.
        await fixture.Pump.PumpAsync(T0.AddHours(1));
        Assert.Equal([1, 2, 3], fixture.Recorder.Attempts);
    }

    [Fact]
    public async Task HandlerInternalCancellation_IsAFailureThatRetries_NeverAnOperatorCancel()
    {
        var fixture = CreateFixture(OneMinuteThreeAttempts);
        fixture.Recorder.SucceedOnAttempt = 2;
        fixture.Recorder.FailWithTaskCanceled = true;

        var jobId = await fixture.Client.EnqueueAsync(new ChargeCard("order-3"), dueTime: T0);

        // An OCE nobody requested (an HttpClient timeout, an internal deadline) is a plain
        // failure: the job retries at the backoff instant instead of dying as Cancelled.
        await fixture.Pump.PumpAsync(T0);
        Assert.Equal(JobState.Scheduled, (await fixture.Store.GetJobAsync(jobId))!.State);

        await fixture.Pump.PumpAsync(T0.AddMinutes(1));
        Assert.Equal([1, 2], fixture.Recorder.Attempts);
        Assert.Equal(JobState.Succeeded, (await fixture.Store.GetJobAsync(jobId))!.State);
    }

    [Fact]
    public async Task DefaultPolicy_BacksOffExponentially()
    {
        var policy = RetryPolicy.Default;
        Assert.Equal(TimeSpan.FromSeconds(2), RetryPolicy.DefaultBackoff(1));
        Assert.Equal(TimeSpan.FromSeconds(8), RetryPolicy.DefaultBackoff(3));
        Assert.Equal(TimeSpan.FromMinutes(5), RetryPolicy.DefaultBackoff(60));

        Assert.Equal(T0.AddSeconds(2), policy.NextAttemptAt(1, T0));
        Assert.Null(policy.NextAttemptAt(10, T0));
        await Task.CompletedTask;
    }
}
