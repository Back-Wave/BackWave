using System.Diagnostics;
using System.Text.Json.Serialization;
using BackWave.Core;
using BackWave.Jobs;
using BackWave.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace BackWave.Testing.Tests;

public sealed record SendInvoice(string OrderId);

public sealed class SendInvoiceHandler(InvoiceLog log) : IJobHandler<SendInvoice>
{
    public Task HandleAsync(SendInvoice job, JobContext context, CancellationToken cancellationToken)
    {
        log.Sent.Add((job.OrderId, context.Attempt));
        return log.FailFirstAttempt && context.Attempt == 1
            ? throw new InvalidOperationException("transient gateway error")
            : Task.CompletedTask;
    }
}

public sealed class InvoiceLog
{
    public bool FailFirstAttempt { get; set; }
    public List<(string OrderId, int Attempt)> Sent { get; } = [];
}

[JsonSerializable(typeof(SendInvoice))]
internal sealed partial class HarnessJsonContext : JsonSerializerContext;

public class BackWaveHarnessTests
{
    private static (BackWaveHarness Harness, InvoiceLog Log) CreateHarness()
    {
        var services = new ServiceCollection()
            .AddSingleton<InvoiceLog>()
            .AddTransient<IJobHandler<SendInvoice>, SendInvoiceHandler>()
            .BuildServiceProvider();
        var registry = new JobRegistry(
        [
            JobRegistration.Create<SendInvoice, SendInvoiceHandler>(
                "send-invoice", HarnessJsonContext.Default.SendInvoice),
        ]);
        return (new BackWaveHarness(registry, services), services.GetRequiredService<InvoiceLog>());
    }

    [Fact]
    public async Task UserStyleTest_Enqueue_AdvanceThreeDays_AssertViaMonitor()
    {
        var (harness, log) = CreateHarness();

        var dueNow = await harness.EnqueueAsync(new SendInvoice("order-1"));
        var dueInTwoDays = await harness.EnqueueAsync(new SendInvoice("order-2"), delay: TimeSpan.FromDays(2));

        await harness.AdvanceAsync(TimeSpan.FromDays(3));

        Assert.Equal(JobState.Succeeded, (await harness.Monitor.GetJobAsync(dueNow))!.State);
        Assert.Equal(JobState.Succeeded, (await harness.Monitor.GetJobAsync(dueInTwoDays))!.State);
        Assert.Equal([("order-1", 1), ("order-2", 1)], log.Sent);
    }

    [Fact]
    public async Task Advance_ProcessesRetries_AtTheirBackoffInstants()
    {
        var (harness, log) = CreateHarness();
        log.FailFirstAttempt = true;

        var jobId = await harness.EnqueueAsync(new SendInvoice("flaky-order"));
        await harness.AdvanceAsync(TimeSpan.FromMinutes(1));

        var job = await harness.Monitor.GetJobAsync(jobId);
        Assert.Equal(JobState.Succeeded, job!.State);
        Assert.Equal(2, job.Attempt);
        Assert.Equal([("flaky-order", 1), ("flaky-order", 2)], log.Sent);
    }

    [Fact]
    public async Task Advance_HonorsAPerTypeRetryOverride_NotTheSlowerGroupBackoff()
    {
        // The registration carries a [Retry]-equivalent override: 3 attempts, 10s backoff. The group
        // policy is far slower (60s). If the harness ignored the override, attempt 2 would not run until
        // 60s. Advancing only 15s and finding a succeeded attempt 2 proves the 10s override reached the node.
        var services = new ServiceCollection()
            .AddSingleton<InvoiceLog>()
            .AddTransient<IJobHandler<SendInvoice>, SendInvoiceHandler>()
            .BuildServiceProvider();
        var registry = new JobRegistry(
        [
            JobRegistration.Create<SendInvoice, SendInvoiceHandler>(
                "send-invoice", HarnessJsonContext.Default.SendInvoice,
                retry: RetryDisposition.FromIntervals(3, [TimeSpan.FromSeconds(10)])),
        ]);
        var harness = new BackWaveHarness(registry, services, new BackWaveHarnessOptions
        {
            RetryPolicy = new RetryPolicy { MaxAttempts = 3, Backoff = _ => TimeSpan.FromSeconds(60) },
        });
        var log = services.GetRequiredService<InvoiceLog>();
        log.FailFirstAttempt = true;

        var jobId = await harness.EnqueueAsync(new SendInvoice("flaky-order"));
        await harness.AdvanceAsync(TimeSpan.FromSeconds(15));

        var job = await harness.Monitor.GetJobAsync(jobId);
        Assert.Equal(JobState.Succeeded, job!.State);
        Assert.Equal(2, job.Attempt);
        Assert.Equal([("flaky-order", 1), ("flaky-order", 2)], log.Sent);
    }

    [Fact]
    public async Task Advance_ReleasesDependencies_WhenTheParentGoesTerminal()
    {
        var (harness, log) = CreateHarness();

        var parent = await harness.EnqueueAsync(new SendInvoice("parent"), delay: TimeSpan.FromHours(1));
        var child = await harness.EnqueueDependencyAsync(new SendInvoice("child"), parent);

        await harness.AdvanceAsync(TimeSpan.FromMinutes(30));
        Assert.Equal(JobState.AwaitingParent, (await harness.Monitor.GetJobAsync(child))!.State);

        await harness.AdvanceAsync(TimeSpan.FromHours(1));
        Assert.Equal(JobState.Succeeded, (await harness.Monitor.GetJobAsync(child))!.State);
        Assert.Equal([("parent", 1), ("child", 1)], log.Sent);
    }

    [Fact]
    public async Task RecurringSchedule_ASimulatedYearOfTicks_RunsInVirtualTimeWithoutSleeping()
    {
        var (harness, log) = CreateHarness();
        await harness.UpsertRecurringAsync("nightly-invoice", Cron.Daily(hour: 3), new SendInvoice("nightly"));

        var stopwatch = Stopwatch.StartNew();
        await harness.AdvanceAsync(TimeSpan.FromDays(365));
        stopwatch.Stop();

        Assert.Equal(365, log.Sent.Count);
        var depths = await harness.Monitor.GetQueueDepthsAsync();
        Assert.Equal(new QueueStateCount("default", JobState.Succeeded, 365), Assert.Single(depths));
        // Advancing a virtual year must not cost real wall-clock: if the harness ever slept in real
        // time this would take ~365 days, so a few seconds is a generous ceiling that only trips on a
        // real regression, never on cold-CI JIT or runner contention (a hard sub-second bound flaked).
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10),
            $"a year of ticks took {stopwatch.ElapsedMilliseconds} ms");

        var schedule = Assert.Single(await harness.Monitor.ListSchedulesAsync());
        Assert.Equal(harness.Now.Date.AddHours(3), schedule.NextDue!.Value.UtcDateTime);
    }

    [Fact]
    public async Task Retention_PurgesSucceededAfterItsWindow_KeepsDeadLetteredLonger()
    {
        var services = new ServiceCollection()
            .AddSingleton<InvoiceLog>()
            .AddTransient<IJobHandler<SendInvoice>, SendInvoiceHandler>()
            .BuildServiceProvider();
        var registry = new JobRegistry(
        [
            JobRegistration.Create<SendInvoice, SendInvoiceHandler>(
                "send-invoice", HarnessJsonContext.Default.SendInvoice),
        ]);
        var harness = new BackWaveHarness(registry, services, new BackWaveHarnessOptions
        {
            Retention = RetentionPolicy.Default, // 24h Succeeded, 14d Dead-Lettered
            RetryPolicy = new RetryPolicy { MaxAttempts = 1, Backoff = _ => TimeSpan.FromMinutes(1) },
        });
        var log = services.GetRequiredService<InvoiceLog>();
        log.FailFirstAttempt = true; // MaxAttempts 1: dead-letters on the first failure

        var deadLettered = await harness.EnqueueAsync(new SendInvoice("doomed"));
        await harness.RunDueAsync();
        log.FailFirstAttempt = false;
        var succeeded = await harness.EnqueueAsync(new SendInvoice("fine"), delay: TimeSpan.FromHours(12));

        // The retention clock starts at the terminal instant: the succeeded job finishes
        // 12h in, so at +25h it is only 13h terminal and must still be visible.
        await harness.AdvanceAsync(TimeSpan.FromHours(25));
        Assert.NotNull(await harness.Monitor.GetJobAsync(succeeded));
        Assert.NotNull(await harness.Monitor.GetJobAsync(deadLettered));

        // Past 24h terminal for the succeeded job; the dead-lettered one keeps its 14 days.
        await harness.AdvanceAsync(TimeSpan.FromHours(12));
        Assert.Null(await harness.Monitor.GetJobAsync(succeeded));
        Assert.NotNull(await harness.Monitor.GetJobAsync(deadLettered));

        await harness.AdvanceAsync(TimeSpan.FromDays(14));
        Assert.Null(await harness.Monitor.GetJobAsync(deadLettered));
    }

    [Fact]
    public async Task RollbackInsideTheHarness_LeavesNoTrace()
    {
        var (harness, log) = CreateHarness();

        Guid jobId;
        using (var transaction = harness.BeginTransaction())
        {
            jobId = await harness.EnqueueAsync(new SendInvoice("never-saved"), transaction: transaction);
            transaction.Rollback();
        }

        await harness.AdvanceAsync(TimeSpan.FromDays(1));

        Assert.Null(await harness.Monitor.GetJobAsync(jobId));
        Assert.Empty(log.Sent);
        Assert.Empty(await harness.Monitor.GetQueueDepthsAsync());
    }
}
