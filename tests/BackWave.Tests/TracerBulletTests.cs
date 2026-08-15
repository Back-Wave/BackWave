using BackWave.Driver;
using BackWave.Jobs;
using BackWave.Storage;
using BackWave.Testing;
using BackWave.Storage.InMemory;
using Microsoft.Extensions.DependencyInjection;

namespace BackWave.Tests;

[Job("send-welcome-email")]
public sealed record SendWelcomeEmail(string Email);

public sealed class SendWelcomeEmailHandler(ExecutionRecorder recorder) : IJobHandler<SendWelcomeEmail>
{
    public Task HandleAsync(SendWelcomeEmail job, JobContext context, CancellationToken cancellationToken)
    {
        recorder.Executions.Add((job.Email, context.Attempt));
        return Task.CompletedTask;
    }
}

public sealed class ExecutionRecorder
{
    public List<(string Email, int Attempt)> Executions { get; } = [];
}

public class TracerBulletTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(
        BackWaveClient Client,
        DeterministicPump Pump,
        IJobStore Store,
        ExecutionRecorder Recorder);

    private static Fixture CreateFixture()
    {
        var services = new ServiceCollection()
            .AddSingleton<ExecutionRecorder>()
            .AddTransient<IJobHandler<SendWelcomeEmail>, SendWelcomeEmailHandler>()
            .BuildServiceProvider();

        // The [Job]-generated registry — the hand-written registration this slice replaced.
        var registry = Generated.BackWaveJobs.CreateRegistry();

        var store = new InMemoryJobStore();
        var driver = new NodeDriver(new NodeOptions { WorkerId = "node-1", Policy = new Core.DispatchPolicy.Strict(["default"]) });
        var pump = new DeterministicPump(driver, store, registry, services);
        var client = new BackWaveClient(store, registry);

        return new Fixture(client, pump, store, services.GetRequiredService<ExecutionRecorder>());
    }

    [Fact]
    public async Task EnqueuedJob_RunsOnce_AndSucceeds()
    {
        var fixture = CreateFixture();

        var jobId = await fixture.Client.EnqueueAsync(new SendWelcomeEmail("ada@example.test"), dueTime: T0);
        await fixture.Pump.PumpAsync(T0);

        Assert.Equal([("ada@example.test", 1)], fixture.Recorder.Executions);

        var job = await fixture.Store.GetJobAsync(jobId);
        Assert.NotNull(job);
        Assert.Equal(JobState.Succeeded, job.State);
        Assert.Equal(1, job.Attempt);
        Assert.Equal(T0, job.TerminalAt);
    }

    [Fact]
    public async Task FutureDueJob_DoesNotRunEarly_RunsWhenTimeAdvances()
    {
        var fixture = CreateFixture();
        var dueAt = T0.AddHours(1);

        var jobId = await fixture.Client.EnqueueAsync(new SendWelcomeEmail("grace@example.test"), dueTime: dueAt);

        await fixture.Pump.PumpAsync(T0);
        Assert.Empty(fixture.Recorder.Executions);
        Assert.Equal(JobState.Scheduled, (await fixture.Store.GetJobAsync(jobId))!.State);

        await fixture.Pump.PumpAsync(dueAt);
        Assert.Equal([("grace@example.test", 1)], fixture.Recorder.Executions);
        Assert.Equal(JobState.Succeeded, (await fixture.Store.GetJobAsync(jobId))!.State);
    }

    [Fact]
    public async Task SameRun_IsDeterministic()
    {
        var first = CreateFixture();
        var second = CreateFixture();

        foreach (var fixture in new[] { first, second })
        {
            await fixture.Client.EnqueueAsync(new SendWelcomeEmail("one@example.test"), dueTime: T0);
            await fixture.Client.EnqueueAsync(new SendWelcomeEmail("two@example.test"), dueTime: T0);
            await fixture.Pump.PumpAsync(T0);
        }

        Assert.Equal(first.Recorder.Executions, second.Recorder.Executions);
    }
}
