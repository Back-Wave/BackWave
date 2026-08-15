using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BackWave.Core;
using BackWave.Driver;
using BackWave.Jobs;
using BackWave.Storage;
using BackWave.Testing;
using BackWave.Storage.InMemory;
using Microsoft.Extensions.DependencyInjection;

namespace BackWave.Tests;

/// <summary>The payload shape as the old deploy serialized it.</summary>
public sealed record SendEmail(string To);

/// <summary>The same Wire Name's payload after an additive change: a new optional property.</summary>
public sealed record SendEmailV2(string To, string? Subject = null);

public sealed class SendEmailHandler(EmailRecorder recorder) : IJobHandler<SendEmailV2>
{
    public Task HandleAsync(SendEmailV2 job, JobContext context, CancellationToken cancellationToken)
    {
        recorder.Seen.Add(job);
        return Task.CompletedTask;
    }
}

public sealed class EmailRecorder
{
    public List<SendEmailV2> Seen { get; } = [];
}

public sealed record AlwaysFails(string Name);

public sealed class AlwaysFailsHandler : IJobHandler<AlwaysFails>
{
    public Task HandleAsync(AlwaysFails job, JobContext context, CancellationToken cancellationToken)
        => throw new InvalidOperationException("handler ran and failed");
}

[JsonSerializable(typeof(SendEmail))]
[JsonSerializable(typeof(SendEmailV2))]
[JsonSerializable(typeof(AlwaysFails))]
internal sealed partial class QuarantineJsonContext : JsonSerializerContext;

public class QuarantineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(
        BackWaveClient Client,
        DeterministicPump Pump,
        IJobStore Store,
        EmailRecorder Recorder);

    private static Fixture CreateFixture()
    {
        var services = new ServiceCollection()
            .AddSingleton<EmailRecorder>()
            .AddTransient<IJobHandler<SendEmailV2>, SendEmailHandler>()
            .AddTransient<IJobHandler<AlwaysFails>, AlwaysFailsHandler>()
            .BuildServiceProvider();

        var registry = new JobRegistry(
        [
            JobRegistration.Create<SendEmailV2, SendEmailHandler>(
                "send-email", QuarantineJsonContext.Default.SendEmailV2),
            JobRegistration.Create<AlwaysFails, AlwaysFailsHandler>(
                "always-fails", QuarantineJsonContext.Default.AlwaysFails),
        ]);

        var store = new InMemoryJobStore();
        var driver = new NodeDriver(new NodeOptions
        {
            WorkerId = "node-1",
            Policy = new DispatchPolicy.Strict(["default"]),
            RetryPolicy = new RetryPolicy { MaxAttempts = 1, Backoff = _ => TimeSpan.FromMinutes(1) },
        });
        var pump = new DeterministicPump(driver, store, registry, services);

        return new Fixture(new BackWaveClient(store, registry), pump, store, services.GetRequiredService<EmailRecorder>());
    }

    [Fact]
    public async Task UnregisteredWireName_Quarantines_WithoutRetryStorm()
    {
        var fixture = CreateFixture();

        // Deploy drift: an old deploy enqueued a job whose handler no longer ships.
        var jobId = Guid.NewGuid();
        var result = await fixture.Store.EnqueueAsync(
            new NewJob(jobId, "ghost-job", "{}"u8.ToArray(), "default", T0), now: T0);
        Assert.Equal(EnqueueResult.Ok, result);

        await fixture.Pump.PumpAsync(T0);

        var job = await fixture.Store.GetJobAsync(jobId);
        Assert.NotNull(job);
        Assert.Equal(JobState.Quarantined, job.State);
        Assert.Equal(1, job.Attempt);
        Assert.Equal(T0, job.TerminalAt);
        Assert.Contains("ghost-job", job.TerminalCause);

        // No retry storm: Quarantined is terminal, so more time runs nothing.
        await fixture.Pump.PumpAsync(T0.AddHours(1));
        job = await fixture.Store.GetJobAsync(jobId);
        Assert.Equal(JobState.Quarantined, job!.State);
        Assert.Equal(1, job.Attempt);
    }

    [Fact]
    public async Task UndecodablePayload_Quarantines_WithCauseRecorded()
    {
        var fixture = CreateFixture();

        var jobId = Guid.NewGuid();
        var result = await fixture.Store.EnqueueAsync(
            new NewJob(jobId, "send-email", Encoding.UTF8.GetBytes("not json"), "default", T0), now: T0);
        Assert.Equal(EnqueueResult.Ok, result);

        await fixture.Pump.PumpAsync(T0);

        var job = await fixture.Store.GetJobAsync(jobId);
        Assert.NotNull(job);
        Assert.Equal(JobState.Quarantined, job.State);
        Assert.Contains("send-email", job.TerminalCause);
        Assert.Contains("no longer decodes", job.TerminalCause);
        Assert.Empty(fixture.Recorder.Seen);
    }

    [Fact]
    public async Task OldPayload_DecodesAfterAddingOptionalProperty()
    {
        var fixture = CreateFixture();

        // The payload as the old deploy wrote it, decoded by the new shape's JsonTypeInfo.
        var v1Payload = JsonSerializer.SerializeToUtf8Bytes(
            new SendEmail("old@example.com"), QuarantineJsonContext.Default.SendEmail);
        var jobId = Guid.NewGuid();
        var result = await fixture.Store.EnqueueAsync(
            new NewJob(jobId, "send-email", v1Payload, "default", T0), now: T0);
        Assert.Equal(EnqueueResult.Ok, result);

        await fixture.Pump.PumpAsync(T0);

        Assert.Equal(JobState.Succeeded, (await fixture.Store.GetJobAsync(jobId))!.State);
        var seen = Assert.Single(fixture.Recorder.Seen);
        Assert.Equal("old@example.com", seen.To);
        Assert.Null(seen.Subject);
    }

    [Fact]
    public async Task OversizedPayload_FailsAtEnqueue_NamingBoundAndSize()
    {
        var fixture = CreateFixture();

        var oversized = new SendEmailV2(To: new string('x', StoreBounds.Default.MaxPayloadBytes + 1));
        var exception = await Assert.ThrowsAsync<ArgumentException>(
            async () => await fixture.Client.EnqueueAsync(oversized, dueTime: T0));

        Assert.Contains("MaxPayloadBytes", exception.Message);
        Assert.Matches(@"\d+ bytes", exception.Message);
        Assert.Contains("send-email", exception.Message);
    }

    [Fact]
    public async Task Quarantined_And_DeadLettered_AreDistinctStates()
    {
        var fixture = CreateFixture();

        // One job that ran and exhausted its attempts, one that could never be routed.
        var deadLetteredId = await fixture.Client.EnqueueAsync(new AlwaysFails("ran-and-failed"), dueTime: T0);
        var quarantinedId = Guid.NewGuid();
        await fixture.Store.EnqueueAsync(
            new NewJob(quarantinedId, "ghost-job", "{}"u8.ToArray(), "default", T0), now: T0);

        await fixture.Pump.PumpAsync(T0);

        var deadLettered = await fixture.Store.GetJobAsync(deadLetteredId);
        var quarantined = await fixture.Store.GetJobAsync(quarantinedId);
        Assert.Equal(JobState.DeadLettered, deadLettered!.State);
        Assert.Equal(JobState.Quarantined, quarantined!.State);
    }

    [Fact]
    public void EmptyWireName_IsRejectedAtRegistration()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => new JobRegistry(
        [
            JobRegistration.Create<SendEmailV2, SendEmailHandler>(
                "", QuarantineJsonContext.Default.SendEmailV2),
        ]));

        Assert.Contains("Wire Name", exception.Message);
        Assert.Contains(nameof(SendEmailV2), exception.Message);
    }
}
