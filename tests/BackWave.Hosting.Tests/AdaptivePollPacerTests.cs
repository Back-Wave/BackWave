using System.Data.Common;
using System.Diagnostics;
using BackWave.Core;
using BackWave.Driver;
using BackWave.Jobs;
using BackWave.Monitor;
using BackWave.Storage;
using BackWave.Storage.InMemory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BackWave.Hosting.Tests;

/// <summary>
/// Direct coverage for the shipped adaptive idle-poll pacer (PollPacerAsync / WakePoll /
/// UpdatePollBackoff), which the Simulator only mirrors by hand. One test proves the real pacer
/// stretches its idle poll cadence and snaps back to the floor when work appears; the other proves a
/// forced shutdown that races the parked pacer does not fault the pump into the fail-stop path.
/// </summary>
public class AdaptivePollPacerTests
{
    private static readonly TimeSpan Floor = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan Ceiling = TimeSpan.FromMilliseconds(800);
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);

    private static JobRegistry PingRegistry() => new(
    [
        JobRegistration.Create<PingJob, PingHandler>("ping", HostingJsonContext.Default.PingJob, "default"),
    ]);

    private static WorkerGroupOptions AdaptiveGroup(string name) => new()
    {
        Name = name,
        Policy = new DispatchPolicy.Strict(["default"]),
        PollInterval = Floor,
        MaxPollInterval = Ceiling,                       // MaxPollInterval > PollInterval ⇒ adaptive on
        HeartbeatInterval = TimeSpan.FromMinutes(10),    // never fires during the test
        MaintenanceInterval = TimeSpan.FromMinutes(10),  // keep the idle claim the only store traffic
    };

    [Fact]
    public async Task AdaptivePacer_StretchesIdlePollCadence_ThenSnapsBackToTheFloor_WhenWorkAppears()
    {
        var inner = new InMemoryJobStore();
        var store = new ClaimRecordingStore(inner);
        var registry = PingRegistry();
        var services = new ServiceCollection();
        services.AddSingleton<PingRecorder>();
        services.AddTransient<IJobHandler<PingJob>, PingHandler>();
        await using var provider = services.BuildServiceProvider();

        var service = new WorkerGroupService(
            AdaptiveGroup("adaptive"),
            store,
            registry,
            provider.GetRequiredService<IServiceScopeFactory>(),
            new BackWaveHealth(),
            NullLogger<WorkerGroupService>.Instance);

        await service.StartAsync(CancellationToken.None);
        try
        {
            // (1) Idle: with no work and no next-due signal the pacer grows its delay geometrically, so
            // the spacing between successive idle claims widens well past the floor. Wait until an idle
            // gap has stretched most of the way to the ceiling - the observable proof of back-off.
            await WaitUntil(
                () => store.MaxGap() >= Ceiling * 0.6,
                "the idle pacer to stretch its poll cadence toward the ceiling");
            var stretchedGap = store.MaxGap();
            var idlePolls = store.ClaimCount;

            // Fixed-cadence polling over that same span (one claim every Floor) would have issued dozens
            // of claims; the back-off keeps the idle claim count low. Geometric growth from the floor
            // reaches the ceiling in a single-digit number of polls.
            Assert.True(idlePolls < 20, $"expected few idle polls under back-off, saw {idlePolls}.");

            // (2) Work appears. A poll claims the due-now job, which resets the delay to the floor. The
            // job is picked up within the ceiling-bounded back-off even though no Wake-Up Hint is fired.
            var monitor = new BackWaveMonitor(inner);
            var client = new BackWaveClient(store, registry);
            var firstId = await client.EnqueueAsync(new PingJob("wake"), dueTime: DateTimeOffset.UtcNow);
            await WaitUntil(
                async () => (await monitor.GetJobAsync(firstId))?.State == JobState.Succeeded,
                "the enqueued job to be claimed and run within the bounded back-off");

            // (3) Snap-back: with the pacer restored to the floor, a second due-now job is picked up far
            // faster than the widened idle gap it would have waited under the stretched cadence.
            var enqueuedAt = Stopwatch.GetTimestamp();
            var secondId = await client.EnqueueAsync(new PingJob("wake-again"), dueTime: DateTimeOffset.UtcNow);
            await WaitUntil(
                async () => (await monitor.GetJobAsync(secondId))?.State == JobState.Succeeded,
                "the second job to be picked up at the restored floor cadence");
            var pickupLatency = Stopwatch.GetElapsedTime(enqueuedAt);

            Assert.True(
                pickupLatency < stretchedGap,
                $"expected floor-cadence pickup ({pickupLatency}) below the stretched idle gap ({stretchedGap}).");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ForcedShutdown_WhileTheAdaptivePacerIsParked_DoesNotFailStopOrHaltTheGroup()
    {
        var store = new ClaimRecordingStore(new InMemoryJobStore());
        var logs = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var health = new BackWaveHealth();

        var service = new WorkerGroupService(
            AdaptiveGroup("teardown"),
            store,
            new JobRegistry([]),
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            health,
            loggerFactory.CreateLogger<WorkerGroupService>());

        await service.StartAsync(CancellationToken.None);

        // Let the idle pacer stretch until it is parked in a long wait on the wake latch - the window in
        // which a forced teardown races disposal of that latch against the parked wait.
        await WaitUntil(
            () => store.MaxGap() >= Floor * 8,
            "the idle pacer to reach a long parked wait");

        // Force the shutdown mid-loop: Dispose cancels the pump and, in the same breath, disposes the
        // wake latch the pacer is parked on. The parked wait throws ObjectDisposedException; the fix
        // recognizes that as teardown (the token is already cancelled) and does NOT fault the pump, so
        // no spurious WorkerGroupFailStopped Critical is logged and health is never reported halted.
        service.Dispose();

        // Give the pump a beat to unwind and surface any (erroneous) fail-stop.
        await Task.Delay(250);

        Assert.True(health.IsHealthy, "a forced shutdown must not report the group halted.");
        Assert.False(health.HaltedGroups.ContainsKey("teardown"));
        Assert.DoesNotContain(logs.Entries, e => e.Level == LogLevel.Critical);
        Assert.DoesNotContain(logs.Entries, e => e.EventId == 2001); // WorkerGroupFailStopped

        // Residual gap: which exception the parked wait observes (ObjectDisposedException from the latch
        // vs OperationCanceledException from the token) is timing dependent, so this test cannot force
        // the disposal branch on every run. It drives the documented race window - a parked long wait,
        // then synchronous disposal - and asserts the teardown stays quiet, verifying the fix without
        // over-claiming determinism.
    }

    private static async Task WaitUntil(Func<bool> condition, string description) =>
        await WaitUntil(() => ValueTask.FromResult(condition()), description);

    private static async Task WaitUntil(Func<ValueTask<bool>> condition, string description)
    {
        var deadline = DateTimeOffset.UtcNow + TestTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }
            await Task.Delay(10);
        }
        Assert.Fail($"Timed out waiting for {description}.");
    }
}

/// <summary>
/// Wraps the In-Memory Store and records the wall-clock instant of every claim so a test can measure
/// the pacer's idle poll cadence. It deliberately does NOT override <c>ClaimBatchAsync</c> (so the
/// default interface method reports <c>NextDue = null</c>, keeping the pacer on its geometric back-off)
/// and does NOT implement <c>IWakeUpHintSource</c> (so nothing but the pacer drives the poll cadence).
/// </summary>
internal sealed class ClaimRecordingStore(IJobStore inner) : IJobStore
{
    private readonly object _sync = new();
    private readonly List<long> _claimTimestamps = [];
    private int _claimCount;

    public int ClaimCount => Volatile.Read(ref _claimCount);

    /// <summary>The widest gap observed so far between two consecutive claims.</summary>
    public TimeSpan MaxGap()
    {
        lock (_sync)
        {
            var max = TimeSpan.Zero;
            for (var i = 1; i < _claimTimestamps.Count; i++)
            {
                var gap = Stopwatch.GetElapsedTime(_claimTimestamps[i - 1], _claimTimestamps[i]);
                if (gap > max)
                {
                    max = gap;
                }
            }
            return max;
        }
    }

    public async ValueTask<IReadOnlyList<JobRecord>> ClaimAsync(ClaimRequest request, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            _claimTimestamps.Add(Stopwatch.GetTimestamp());
        }
        Interlocked.Increment(ref _claimCount);
        return await inner.ClaimAsync(request, cancellationToken).ConfigureAwait(false);
    }

    // ── Everything else delegates straight through ───────────────────────────────
    public bool SupportsTransactionalEnqueue => inner.SupportsTransactionalEnqueue;

    public ValueTask<EnqueueResult> EnqueueAsync(
        NewJob job, DateTimeOffset now, DbTransaction? transaction = null, CancellationToken cancellationToken = default)
        => inner.EnqueueAsync(job, now, transaction, cancellationToken);

    public ValueTask<OutcomeResult> ReportOutcomeAsync(
        Guid jobId, string workerId, int attempt, JobOutcome outcome, DateTimeOffset now,
        string? failureDetail = null, JobTags? addedTags = null, ReadOnlyMemory<byte>? output = null,
        CancellationToken cancellationToken = default)
        => inner.ReportOutcomeAsync(jobId, workerId, attempt, outcome, now, failureDetail, addedTags, output, cancellationToken);

    public ValueTask<ReadOnlyMemory<byte>?> GetJobOutputAsync(Guid jobId, CancellationToken cancellationToken = default)
        => inner.GetJobOutputAsync(jobId, cancellationToken);

    public ValueTask<IReadOnlyList<HeartbeatResult>> HeartbeatAsync(
        string workerId, IReadOnlyList<Guid> jobIds, TimeSpan leaseDuration, DateTimeOffset now, CancellationToken cancellationToken = default)
        => inner.HeartbeatAsync(workerId, jobIds, leaseDuration, now, cancellationToken);

    public ValueTask<int> ExpireLeasesAsync(
        DateTimeOffset now, int maxJobs, IReadOnlyList<string> queues, RetryDisposition disposition, CancellationToken cancellationToken = default)
        => inner.ExpireLeasesAsync(now, maxJobs, queues, disposition, cancellationToken);

    public ValueTask<CancelResult> CancelJobAsync(Guid jobId, string actor, DateTimeOffset now, CancellationToken cancellationToken = default)
        => inner.CancelJobAsync(jobId, actor, now, cancellationToken);

    public ValueTask<RequeueResult> RequeueAsync(Guid jobId, string actor, DateTimeOffset now, CancellationToken cancellationToken = default)
        => inner.RequeueAsync(jobId, actor, now, cancellationToken);

    public ValueTask PauseQueueAsync(string queue, string actor, DateTimeOffset now, CancellationToken cancellationToken = default)
        => inner.PauseQueueAsync(queue, actor, now, cancellationToken);

    public ValueTask ResumeQueueAsync(string queue, string actor, DateTimeOffset now, CancellationToken cancellationToken = default)
        => inner.ResumeQueueAsync(queue, actor, now, cancellationToken);

    public ValueTask<TriggerScheduleResult> TriggerScheduleNowAsync(string scheduleId, string actor, DateTimeOffset now, CancellationToken cancellationToken = default)
        => inner.TriggerScheduleNowAsync(scheduleId, actor, now, cancellationToken);

    public ValueTask<IReadOnlyList<OperatorAuditRecord>> ListAuditRecordsAsync(string target, CancellationToken cancellationToken = default)
        => inner.ListAuditRecordsAsync(target, cancellationToken);

    public ValueTask UpsertScheduleAsync(ScheduleRecord schedule, CancellationToken cancellationToken = default)
        => inner.UpsertScheduleAsync(schedule, cancellationToken);

    public ValueTask RemoveScheduleAsync(string scheduleId, CancellationToken cancellationToken = default)
        => inner.RemoveScheduleAsync(scheduleId, cancellationToken);

    public ValueTask<IReadOnlyList<ScheduleSnapshot>> ListSchedulesAsync(CancellationToken cancellationToken = default)
        => inner.ListSchedulesAsync(cancellationToken);

    public ValueTask<int> MintDueAsync(IReadOnlyList<MintDecision> decisions, CancellationToken cancellationToken = default)
        => inner.MintDueAsync(decisions, cancellationToken);

    public ValueTask SetConcurrencyLimitAsync(string queue, int? limit, string actor, DateTimeOffset now, CancellationToken cancellationToken = default)
        => inner.SetConcurrencyLimitAsync(queue, limit, actor, now, cancellationToken);

    public ValueTask<JobRecord?> GetJobAsync(Guid jobId, CancellationToken cancellationToken = default)
        => inner.GetJobAsync(jobId, cancellationToken);

    public ValueTask<IReadOnlyList<JobTransition>> GetJobHistoryAsync(Guid jobId, CancellationToken cancellationToken = default)
        => inner.GetJobHistoryAsync(jobId, cancellationToken);

    public ValueTask<IReadOnlyList<JobRecord>> ListJobsAsync(JobQuery query, CancellationToken cancellationToken = default)
        => inner.ListJobsAsync(query, cancellationToken);

    public ValueTask<IReadOnlyList<QueueStateCount>> CountJobsAsync(CancellationToken cancellationToken = default)
        => inner.CountJobsAsync(cancellationToken);

    public ValueTask<IReadOnlyList<TagFacet>> FacetAsync(
        string key, JobQuery? baseQuery = null, int maxResults = int.MaxValue, CancellationToken cancellationToken = default)
        => inner.FacetAsync(key, baseQuery, maxResults, cancellationToken);

    public ValueTask<IReadOnlyList<TagSuggestion>> SuggestTagsAsync(TagSuggestQuery query, CancellationToken cancellationToken = default)
        => inner.SuggestTagsAsync(query, cancellationToken);

    public ValueTask<WorkflowEnqueueResult> EnqueueWorkflowAsync(
        WorkflowDefinition workflow, DateTimeOffset now, DbTransaction? transaction = null,
        CancellationToken cancellationToken = default)
        => inner.EnqueueWorkflowAsync(workflow, now, transaction, cancellationToken);

    public ValueTask<IReadOnlyList<WorkflowSnapshot>> ListWorkflowsAsync(CancellationToken cancellationToken = default)
        => inner.ListWorkflowsAsync(cancellationToken);

    public ValueTask<WorkflowGraph?> GetWorkflowAsync(Guid workflowId, CancellationToken cancellationToken = default)
        => inner.GetWorkflowAsync(workflowId, cancellationToken);

    public ValueTask<IReadOnlyList<QueueSettings>> ListQueueSettingsAsync(CancellationToken cancellationToken = default)
        => inner.ListQueueSettingsAsync(cancellationToken);

    public ValueTask<DependencyEdges> GetDependencyEdgesAsync(Guid jobId, CancellationToken cancellationToken = default)
        => inner.GetDependencyEdgesAsync(jobId, cancellationToken);

    public ValueTask<int> PurgeTerminalAsync(
        TerminalStateClass stateClass, DateTimeOffset terminalBefore, int maxJobs, CancellationToken cancellationToken = default)
        => inner.PurgeTerminalAsync(stateClass, terminalBefore, maxJobs, cancellationToken);

    public ValueTask<ObserverClaim> ClaimObserverDeliveriesAsync(
        ObserverClaimRequest request, CancellationToken cancellationToken = default)
        => inner.ClaimObserverDeliveriesAsync(request, cancellationToken);

    public ValueTask ReportObserverDeliveriesAsync(
        ObserverDeliveryReport report, CancellationToken cancellationToken = default)
        => inner.ReportObserverDeliveriesAsync(report, cancellationToken);

    public ValueTask<long> GetObserverCursorAsync(string observerId, CancellationToken cancellationToken = default)
        => inner.GetObserverCursorAsync(observerId, cancellationToken);

    public ValueTask<ObserverLag> GetObserverLagAsync(ObserverLagRequest request, CancellationToken cancellationToken = default)
        => inner.GetObserverLagAsync(request, cancellationToken);

    public ValueTask<IReadOnlyList<ObserverDeadLetterRecord>> ListObserverDeadLettersAsync(
        string observerId, CancellationToken cancellationToken = default)
        => inner.ListObserverDeadLettersAsync(observerId, cancellationToken);
}
