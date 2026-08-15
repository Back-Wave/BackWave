using System.Data.Common;
using System.Text.Json.Serialization;
using BackWave.Core;
using BackWave.Jobs;
using BackWave.Storage;

namespace BackWave.Tests;

public sealed record SendNewsletter(string Edition);

public sealed class SendNewsletterHandler : IJobHandler<SendNewsletter>
{
    public Task HandleAsync(SendNewsletter job, JobContext context, CancellationToken cancellationToken)
        => Task.CompletedTask;
}

[JsonSerializable(typeof(SendNewsletter))]
internal sealed partial class ClientJsonContext : JsonSerializerContext;

/// <summary>
/// The client owns the clock (spec §1: stores take time as an input). The due time is
/// never a substitute for now — pretending a future-scheduled job is "due now" fired a
/// cluster-wide Wake-Up Hint per enqueue for work nobody could claim.
/// </summary>
public class BackWaveClientTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static JobRegistry Registry() => new(
    [
        JobRegistration.Create<SendNewsletter, SendNewsletterHandler>(
            "send-newsletter", ClientJsonContext.Default.SendNewsletter),
    ]);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Fact]
    public async Task EnqueueAsync_PassesTheClockNow_NeverTheDueTime()
    {
        var spy = new NowRecordingStore();
        var client = new BackWaveClient(spy, Registry(), new FixedClock(T0));

        await client.EnqueueAsync(new SendNewsletter("june"), dueTime: T0.AddHours(6));

        Assert.Equal(T0, spy.LastNow); // honest now: the clock, not the future due time
        Assert.Equal(T0.AddHours(6), spy.LastJob!.DueTime);
    }

    [Fact]
    public async Task EnqueueDependencyAsync_PassesTheClockNow_Too()
    {
        var spy = new NowRecordingStore();
        var client = new BackWaveClient(spy, Registry(), new FixedClock(T0));

        await client.EnqueueDependencyAsync(
            new SendNewsletter("follow-up"), parentId: Guid.NewGuid(), enqueuedAt: T0.AddHours(6));

        Assert.Equal(T0, spy.LastNow);
    }

    [Fact]
    public async Task EnqueueDependencyAsync_DefaultsTheStamp_ToTheInjectedClock()
    {
        var spy = new NowRecordingStore();
        var client = new BackWaveClient(spy, Registry(), new FixedClock(T0));

        // No caller-supplied "now": the injected TimeProvider governs, like plain enqueue.
        await client.EnqueueDependencyAsync(new SendNewsletter("follow-up"), parentId: Guid.NewGuid());

        Assert.Equal(T0, spy.LastNow);
        Assert.Equal(T0, spy.LastJob!.DueTime);
    }

    [Fact]
    public async Task UpsertRecurringAsync_DefaultsTheCursor_ToTheInjectedClock()
    {
        var spy = new NowRecordingStore();
        var client = new BackWaveClient(spy, Registry(), new FixedClock(T0));

        await client.UpsertRecurringAsync("nightly", Cron.Daily(2), new SendNewsletter("digest"));

        Assert.Equal(T0, spy.LastSchedule!.Cursor);
    }

    /// <summary>Records what the client passes to §5.1; everything else is unreachable from enqueue.</summary>
    private sealed class NowRecordingStore : IJobStore
    {
        public NewJob? LastJob { get; private set; }
        public DateTimeOffset? LastNow { get; private set; }
        public ScheduleRecord? LastSchedule { get; private set; }

        public bool SupportsTransactionalEnqueue => true;

        public ValueTask<EnqueueResult> EnqueueAsync(
            NewJob job, DateTimeOffset now, DbTransaction? transaction = null, CancellationToken cancellationToken = default)
        {
            LastJob = job;
            LastNow = now;
            return ValueTask.FromResult(EnqueueResult.Ok);
        }

        public ValueTask<IReadOnlyList<JobRecord>> ClaimAsync(ClaimRequest request, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public ValueTask<OutcomeResult> ReportOutcomeAsync(
            Guid jobId, string workerId, int attempt, JobOutcome outcome, DateTimeOffset now,
            string? failureDetail = null, JobTags? addedTags = null, ReadOnlyMemory<byte>? output = null,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public ValueTask<ReadOnlyMemory<byte>?> GetJobOutputAsync(Guid jobId, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public ValueTask<IReadOnlyList<HeartbeatResult>> HeartbeatAsync(
            string workerId, IReadOnlyList<Guid> jobIds, TimeSpan leaseDuration, DateTimeOffset now,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public ValueTask<int> ExpireLeasesAsync(
            DateTimeOffset now, int maxJobs, IReadOnlyList<string> queues, RetryDisposition disposition,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public ValueTask<CancelResult> CancelJobAsync(
            Guid jobId, string actor, DateTimeOffset now, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public ValueTask<RequeueResult> RequeueAsync(
            Guid jobId, string actor, DateTimeOffset now, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public ValueTask PauseQueueAsync(
            string queue, string actor, DateTimeOffset now, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public ValueTask ResumeQueueAsync(
            string queue, string actor, DateTimeOffset now, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public ValueTask<TriggerScheduleResult> TriggerScheduleNowAsync(
            string scheduleId, string actor, DateTimeOffset now, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public ValueTask<IReadOnlyList<OperatorAuditRecord>> ListAuditRecordsAsync(
            string target, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public ValueTask UpsertScheduleAsync(ScheduleRecord schedule, CancellationToken cancellationToken = default)
        {
            LastSchedule = schedule;
            return ValueTask.CompletedTask;
        }

        public ValueTask RemoveScheduleAsync(string scheduleId, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public ValueTask<IReadOnlyList<ScheduleSnapshot>> ListSchedulesAsync(CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public ValueTask<int> MintDueAsync(IReadOnlyList<MintDecision> decisions, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public ValueTask SetConcurrencyLimitAsync(string queue, int? limit, string actor, DateTimeOffset now, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public ValueTask<JobRecord?> GetJobAsync(Guid jobId, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public ValueTask<IReadOnlyList<JobTransition>> GetJobHistoryAsync(Guid jobId, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public ValueTask<IReadOnlyList<JobRecord>> ListJobsAsync(JobQuery query, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public ValueTask<IReadOnlyList<QueueStateCount>> CountJobsAsync(CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public ValueTask<IReadOnlyList<TagFacet>> FacetAsync(
            string key, JobQuery? baseQuery = null, int maxResults = int.MaxValue, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public ValueTask<IReadOnlyList<TagSuggestion>> SuggestTagsAsync(TagSuggestQuery query, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public ValueTask<WorkflowEnqueueResult> EnqueueWorkflowAsync(
            WorkflowDefinition workflow, DateTimeOffset now, DbTransaction? transaction = null,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public ValueTask<IReadOnlyList<WorkflowSnapshot>> ListWorkflowsAsync(CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public ValueTask<WorkflowGraph?> GetWorkflowAsync(Guid workflowId, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public ValueTask<IReadOnlyList<QueueSettings>> ListQueueSettingsAsync(CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public ValueTask<DependencyEdges> GetDependencyEdgesAsync(Guid jobId, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public ValueTask<int> PurgeTerminalAsync(
            TerminalStateClass stateClass, DateTimeOffset terminalBefore, int maxJobs,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public ValueTask<ObserverClaim> ClaimObserverDeliveriesAsync(
            ObserverClaimRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(ObserverClaim.None(request.ObserverId));

        public ValueTask ReportObserverDeliveriesAsync(
            ObserverDeliveryReport report, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask<long> GetObserverCursorAsync(string observerId, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(-1L);

        public ValueTask<ObserverLag> GetObserverLagAsync(ObserverLagRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new ObserverLag(-1L, 0, null));

        public ValueTask<IReadOnlyList<ObserverDeadLetterRecord>> ListObserverDeadLettersAsync(
            string observerId, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<ObserverDeadLetterRecord>>([]);
    }
}
