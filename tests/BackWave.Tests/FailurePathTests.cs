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

public sealed record SendWebhook(string Url);

/// <summary>Always fails: exercises the retry schedule, never the success path.</summary>
public sealed class SendWebhookHandler : IJobHandler<SendWebhook>
{
    public Task HandleAsync(SendWebhook job, JobContext context, CancellationToken cancellationToken)
        => throw new InvalidOperationException($"webhook down (attempt {context.Attempt})");
}

[JsonSerializable(typeof(ChargeCard))]
[JsonSerializable(typeof(SendWebhook))]
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
            RetryOverrides = registry.RetryOverrides,
        });
        var pump = new DeterministicPump(driver, store, registry, services);

        return new Fixture(new BackWaveClient(store, registry), pump, store, services.GetRequiredService<AttemptRecorder>());
    }

    private sealed record TwoTypeFixture(BackWaveClient Client, DeterministicPump Pump, IJobStore Store);

    /// <summary>
    /// One node runs two job types: charge-card inherits the group RetryPolicy; send-webhook carries a
    /// per-job [Retry] override (5 attempts, 10s backoff) via <see cref="JobRegistration.Create"/>.
    /// </summary>
    private static TwoTypeFixture CreateTwoTypeFixture()
    {
        var services = new ServiceCollection()
            .AddSingleton<AttemptRecorder>()
            .AddTransient<IJobHandler<ChargeCard>, ChargeCardHandler>()
            .AddTransient<IJobHandler<SendWebhook>, SendWebhookHandler>()
            .BuildServiceProvider();

        var registry = new JobRegistry(
        [
            JobRegistration.Create<ChargeCard, ChargeCardHandler>(
                "charge-card", FailureJsonContext.Default.ChargeCard),
            JobRegistration.Create<SendWebhook, SendWebhookHandler>(
                "send-webhook", FailureJsonContext.Default.SendWebhook,
                retry: Core.RetryDisposition.FromIntervals(5, [TimeSpan.FromSeconds(10)])),
        ]);

        var store = new InMemoryJobStore();
        var driver = new NodeDriver(new NodeOptions
        {
            WorkerId = "node-1",
            Policy = new Core.DispatchPolicy.Strict(["default"]),
            RetryPolicy = OneMinuteThreeAttempts,
            RetryOverrides = registry.RetryOverrides,
        });
        var pump = new DeterministicPump(driver, store, registry, services);

        return new TwoTypeFixture(new BackWaveClient(store, registry), pump, store);
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

    [Fact]
    public async Task PerJobRetry_SchedulesOnItsOwnBackoff_WhileTheGroupJobKeepsTheGroupBackoff()
    {
        var fixture = CreateTwoTypeFixture();

        var groupJob = await fixture.Client.EnqueueAsync(new ChargeCard("order-4"), dueTime: T0);
        var perJob = await fixture.Client.EnqueueAsync(new SendWebhook("https://hook"), dueTime: T0);

        // Both fail their first attempt at T0.
        await fixture.Pump.PumpAsync(T0);
        Assert.Equal(1, (await fixture.Store.GetJobAsync(groupJob))!.Attempt);
        Assert.Equal(1, (await fixture.Store.GetJobAsync(perJob))!.Attempt);

        // At T0 + 10s the per-job backoff (10s) is due; the group backoff (1 min) is not.
        await fixture.Pump.PumpAsync(T0.AddSeconds(10));
        Assert.Equal(1, (await fixture.Store.GetJobAsync(groupJob))!.Attempt);
        Assert.Equal(2, (await fixture.Store.GetJobAsync(perJob))!.Attempt);

        // At T0 + 1 min the group job takes its second attempt; the per-job kept its own 10s cadence.
        await fixture.Pump.PumpAsync(T0.AddMinutes(1));
        Assert.Equal(2, (await fixture.Store.GetJobAsync(groupJob))!.Attempt);
    }

    [Fact]
    public async Task PerJobRetry_DeadLettersAtItsOwnCeiling_NotTheSmallerGroupCeiling()
    {
        var fixture = CreateTwoTypeFixture();

        // send-webhook always fails; its [Retry] override allows 5 attempts, the group allows 3.
        var perJob = await fixture.Client.EnqueueAsync(new SendWebhook("https://hook"), dueTime: T0);

        // Attempts 1, 2, 3 at the override's 10s cadence.
        await fixture.Pump.PumpAsync(T0);
        await fixture.Pump.PumpAsync(T0.AddSeconds(10));
        await fixture.Pump.PumpAsync(T0.AddSeconds(20));

        // At attempt 3 the group ceiling would dead-letter; the override must keep the job alive.
        var atThree = await fixture.Store.GetJobAsync(perJob);
        Assert.Equal(3, atThree!.Attempt);
        Assert.Equal(JobState.Scheduled, atThree.State);

        await fixture.Pump.PumpAsync(T0.AddSeconds(30));
        await fixture.Pump.PumpAsync(T0.AddSeconds(40));

        // Attempt 5 hits the override ceiling and dead-letters.
        var atFive = await fixture.Store.GetJobAsync(perJob);
        Assert.Equal(5, atFive!.Attempt);
        Assert.Equal(JobState.DeadLettered, atFive.State);
    }

    [Fact]
    public void FromIntervals_RepeatsTheLastInterval_WhenShorterThanTheCeiling()
    {
        // 5 attempts need 4 backoff steps; the list has 2, so steps 3 and 4 repeat the last (5s).
        var disposition = RetryDisposition.FromIntervals(5, [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5)]);

        Assert.Equal(T0.AddSeconds(1), disposition.NextAttemptAt(1, T0));
        Assert.Equal(T0.AddSeconds(5), disposition.NextAttemptAt(2, T0));
        Assert.Equal(T0.AddSeconds(5), disposition.NextAttemptAt(3, T0));
        Assert.Equal(T0.AddSeconds(5), disposition.NextAttemptAt(4, T0));
        Assert.Null(disposition.NextAttemptAt(5, T0));
    }

    [Fact]
    public void FromIntervals_RejectsAnEmptyBackoffList()
    {
        Assert.Throws<ArgumentException>(() => RetryDisposition.FromIntervals(3, []));
    }

    [Fact]
    public void FromIntervals_RejectsMoreThanTwentyIntervals()
    {
        var intervals = new TimeSpan[RetryDisposition.MaxBackoffIntervals + 1];
        Array.Fill(intervals, TimeSpan.FromSeconds(1));

        var error = Assert.Throws<ArgumentException>(() => RetryDisposition.FromIntervals(50, intervals));
        Assert.Contains("20", error.Message);
    }

    [Fact]
    public void FromIntervals_RejectsANegativeInterval()
    {
        Assert.Throws<ArgumentException>(
            () => RetryDisposition.FromIntervals(3, [TimeSpan.FromSeconds(-1)]));
    }

    [Fact]
    public void FromIntervals_RejectsACeilingBelowOne()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => RetryDisposition.FromIntervals(0, [TimeSpan.FromSeconds(1)]));
    }

    [Fact]
    public void FromIntervals_RejectsACeilingAboveTheCap()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(
            () => RetryDisposition.FromIntervals(RetryDisposition.MaxAttemptCeiling + 1, [TimeSpan.FromSeconds(1)]));
        Assert.Contains(RetryDisposition.MaxAttemptCeiling.ToString(), error.Message);
    }
}
