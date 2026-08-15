using System.Text.Json.Serialization;
using BackWave.Jobs;
using BackWave.Storage;
using BackWave.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace BackWave.Tests;

// ── Steps, handlers, and a recorder for the Workflows v2 escape hatches (issue 0270) ───────────
// Workflows v2 ships no .Delay step and no .WaitFor step. These plain jobs exercise the honest
// alternatives - a fixed-floor delay, a completion-anchored delay, a poll-from-a-step wait, and an
// external-enqueue trigger - end-to-end on the deterministic virtual-time harness. The escape hatches
// are built on the ordinary enqueue API, so no workflow primitive appears here.

/// <summary>Shared test state: the client handlers self-enqueue on, the timings, the condition, and a run log.</summary>
public sealed class EscapeHatchRecorder
{
    public BackWaveClient Client { get; set; } = null!;
    public TimeSpan Cooldown { get; set; } = TimeSpan.FromMinutes(15);
    public TimeSpan PollBackoff { get; set; } = TimeSpan.FromSeconds(30);
    public bool ConditionReady { get; set; }
    public List<string> Ran { get; } = [];
}

public sealed record FloorStep;
public sealed record WarmStep;
public sealed record FollowupStep;
public sealed record PollStep(int Poll, int MaxPolls);
public sealed record WebhookStep;

public sealed class FloorStepHandler(EscapeHatchRecorder recorder) : IJobHandler<FloorStep>
{
    public Task HandleAsync(FloorStep job, JobContext context, CancellationToken ct)
    {
        recorder.Ran.Add("floor");
        return Task.CompletedTask;
    }
}

public sealed class WarmStepHandler(EscapeHatchRecorder recorder) : IJobHandler<WarmStep>
{
    public async Task HandleAsync(WarmStep job, JobContext context, CancellationToken ct)
    {
        recorder.Ran.Add("warm");
        // Completion-anchored delay: as its last act, schedule the followup at completion + cooldown.
        await recorder.Client.EnqueueAsync(
            new FollowupStep(), dueTime: recorder.Client.Clock.GetUtcNow() + recorder.Cooldown, cancellationToken: ct);
    }
}

public sealed class FollowupStepHandler(EscapeHatchRecorder recorder) : IJobHandler<FollowupStep>
{
    public Task HandleAsync(FollowupStep job, JobContext context, CancellationToken ct)
    {
        recorder.Ran.Add("followup");
        return Task.CompletedTask;
    }
}

public sealed class PollStepHandler(EscapeHatchRecorder recorder) : IJobHandler<PollStep>
{
    public async Task HandleAsync(PollStep job, JobContext context, CancellationToken ct)
    {
        if (recorder.ConditionReady)
        {
            recorder.Ran.Add("polled-ready");
            return;
        }

        if (job.Poll >= job.MaxPolls)
        {
            recorder.Ran.Add("polled-gave-up");
            return;
        }

        // Poll-from-a-step: not ready yet, so re-enqueue self on a backoff and try again later.
        await recorder.Client.EnqueueAsync(
            new PollStep(job.Poll + 1, job.MaxPolls),
            dueTime: recorder.Client.Clock.GetUtcNow() + recorder.PollBackoff,
            cancellationToken: ct);
    }
}

public sealed class WebhookStepHandler(EscapeHatchRecorder recorder) : IJobHandler<WebhookStep>
{
    public Task HandleAsync(WebhookStep job, JobContext context, CancellationToken ct)
    {
        recorder.Ran.Add("webhook");
        return Task.CompletedTask;
    }
}

[JsonSerializable(typeof(FloorStep))]
[JsonSerializable(typeof(WarmStep))]
[JsonSerializable(typeof(FollowupStep))]
[JsonSerializable(typeof(PollStep))]
[JsonSerializable(typeof(WebhookStep))]
internal sealed partial class EscapeHatchJsonContext : JsonSerializerContext;

/// <summary>
/// Workflows v2 refusal guidance (issue 0270): the delay and wait escape hatches that stand in for the
/// intentionally-absent .Delay / .WaitFor steps, proven to actually work over the ordinary enqueue API.
/// </summary>
public class WorkflowsV2EscapeHatchTests
{
    private static BackWaveHarness NewHarness(out EscapeHatchRecorder recorder)
    {
        var services = new ServiceCollection()
            .AddSingleton<EscapeHatchRecorder>()
            .AddTransient<IJobHandler<FloorStep>, FloorStepHandler>()
            .AddTransient<IJobHandler<WarmStep>, WarmStepHandler>()
            .AddTransient<IJobHandler<FollowupStep>, FollowupStepHandler>()
            .AddTransient<IJobHandler<PollStep>, PollStepHandler>()
            .AddTransient<IJobHandler<WebhookStep>, WebhookStepHandler>()
            .BuildServiceProvider();
        var registry = new JobRegistry(
        [
            JobRegistration.Create<FloorStep, FloorStepHandler>("eh-floor", EscapeHatchJsonContext.Default.FloorStep),
            JobRegistration.Create<WarmStep, WarmStepHandler>("eh-warm", EscapeHatchJsonContext.Default.WarmStep),
            JobRegistration.Create<FollowupStep, FollowupStepHandler>("eh-followup", EscapeHatchJsonContext.Default.FollowupStep),
            JobRegistration.Create<PollStep, PollStepHandler>("eh-poll", EscapeHatchJsonContext.Default.PollStep),
            JobRegistration.Create<WebhookStep, WebhookStepHandler>("eh-webhook", EscapeHatchJsonContext.Default.WebhookStep),
        ]);
        recorder = services.GetRequiredService<EscapeHatchRecorder>();
        var harness = new BackWaveHarness(registry, services, new BackWaveHarnessOptions
        {
            RetryPolicy = new Core.RetryPolicy { MaxAttempts = 1 },
        });
        // The handlers self-enqueue on the harness's client (same store + virtual clock).
        recorder.Client = harness.Client;
        return harness;
    }

    [Fact]
    public async Task FixedFloorDelay_StepDefersUntilItsDueTime()
    {
        var h = NewHarness(out var recorder);
        var delay = TimeSpan.FromMinutes(30);

        await h.EnqueueAsync(new FloorStep(), delay: delay);

        // Before the floor: nothing runs, no matter how much other activity there is.
        await h.AdvanceAsync(delay - TimeSpan.FromMinutes(1));
        Assert.Empty(recorder.Ran);

        // Past the floor: it runs.
        await h.AdvanceAsync(TimeSpan.FromMinutes(2));
        Assert.Equal(["floor"], recorder.Ran);
    }

    [Fact]
    public async Task CompletionAnchoredDelay_FollowupRunsACooldownAfterUpstreamCompletes()
    {
        var h = NewHarness(out var recorder);
        recorder.Cooldown = TimeSpan.FromMinutes(15);

        await h.EnqueueAsync(new WarmStep());
        await h.AdvanceAsync(TimeSpan.Zero);
        // Stage A ran and scheduled the followup, but the cooldown has not elapsed.
        Assert.Equal(["warm"], recorder.Ran);

        await h.AdvanceAsync(TimeSpan.FromMinutes(14));
        Assert.Equal(["warm"], recorder.Ran);

        await h.AdvanceAsync(TimeSpan.FromMinutes(2));
        Assert.Equal(["warm", "followup"], recorder.Ran);
    }

    [Fact]
    public async Task PollFromAStep_ReEnqueuesUntilTheConditionHolds()
    {
        var h = NewHarness(out var recorder);
        recorder.PollBackoff = TimeSpan.FromSeconds(30);

        await h.EnqueueAsync(new PollStep(1, 5));
        await h.AdvanceAsync(TimeSpan.Zero); // poll 1: not ready -> re-enqueue
        Assert.Empty(recorder.Ran);

        // Several backoff cycles pass while the condition still does not hold.
        await h.AdvanceAsync(TimeSpan.FromSeconds(90));
        Assert.Empty(recorder.Ran);

        // The external condition becomes true; the next poll observes it and continues.
        recorder.ConditionReady = true;
        await h.AdvanceAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(["polled-ready"], recorder.Ran);
    }

    [Fact]
    public async Task ExternalEnqueue_TriggerRunsTheContinuationDirectly()
    {
        var h = NewHarness(out var recorder);

        // No poller: an out-of-band event simply enqueues the continuation, which then runs.
        await h.EnqueueAsync(new WebhookStep());
        await h.AdvanceAsync(TimeSpan.Zero);

        Assert.Equal(["webhook"], recorder.Ran);
    }
}
