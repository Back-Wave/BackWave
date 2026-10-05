using System.Text.Json.Serialization;
using BackWave.Core;
using BackWave.Driver;
using BackWave.Jobs;
using BackWave.Monitor;
using BackWave.Storage;
using BackWave.Testing;
using BackWave.Storage.InMemory;
using Microsoft.Extensions.DependencyInjection;

namespace BackWave.Tests;

public sealed record InventorySync(string Region);

public sealed class InventorySyncHandler : IJobHandler<InventorySync>
{
    public Task HandleAsync(InventorySync job, JobContext context, CancellationToken cancellationToken)
        => Task.CompletedTask;
}

[JsonSerializable(typeof(InventorySync))]
internal sealed partial class MonitorJsonContext : JsonSerializerContext;

public class MonitorApiTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(
        BackWaveClient Client,
        BackWaveMonitor Monitor,
        DeterministicPump Pump,
        InMemoryJobStore Store);

    private static Fixture CreateFixture(StoreBounds? bounds = null)
    {
        var services = new ServiceCollection()
            .AddTransient<IJobHandler<InventorySync>, InventorySyncHandler>()
            .BuildServiceProvider();

        var registry = new JobRegistry(
        [
            JobRegistration.Create<InventorySync, InventorySyncHandler>(
                "inventory-sync", MonitorJsonContext.Default.InventorySync),
        ]);

        var store = new InMemoryJobStore(bounds);
        var driver = new NodeDriver(new NodeOptions
        {
            WorkerId = "node-1",
            Policy = new DispatchPolicy.Strict(["default", "reports"]),
            RetryPolicy = RetryPolicy.Default,
        });
        var pump = new DeterministicPump(driver, store, registry, services);

        return new Fixture(new BackWaveClient(store, registry), new BackWaveMonitor(store), pump, store);
    }

    [Fact]
    public async Task JobLifecycle_ObservablePurelyThroughTheMonitorApi()
    {
        var fixture = CreateFixture();

        var jobId = await fixture.Client.EnqueueAsync(new InventorySync("eu"), dueTime: T0);

        var pending = await fixture.Monitor.GetJobAsync(jobId);
        Assert.NotNull(pending);
        Assert.Equal(JobState.Scheduled, pending.State);
        Assert.Equal("inventory-sync", pending.WireName);
        Assert.Equal("default", pending.Queue);
        Assert.Equal(0, pending.Attempt);
        Assert.Equal(T0, pending.DueTime);

        await fixture.Pump.PumpAsync(T0);

        var done = await fixture.Monitor.GetJobAsync(jobId);
        Assert.NotNull(done);
        Assert.Equal(JobState.Succeeded, done.State);
        Assert.Equal(1, done.Attempt);
        Assert.Equal(T0, done.TerminalAt);
    }

    [Fact]
    public async Task ListJobs_FiltersByStateQueueAndWireName()
    {
        var fixture = CreateFixture();

        var defaultJob = await fixture.Client.EnqueueAsync(new InventorySync("us"), dueTime: T0);
        var reportsJob = await fixture.Client.EnqueueAsync(new InventorySync("apac"), dueTime: T0.AddHours(1), queue: "reports");

        await fixture.Pump.PumpAsync(T0); // runs only the default-queue job

        var succeeded = await fixture.Monitor.ListJobsAsync(new JobQuery { State = JobState.Succeeded });
        Assert.Equal([defaultJob], succeeded.Select(j => j.JobId));

        var stillScheduled = await fixture.Monitor.ListJobsAsync(new JobQuery { State = JobState.Scheduled, Queue = "reports" });
        Assert.Equal([reportsJob], stillScheduled.Select(j => j.JobId));

        var byWireName = await fixture.Monitor.ListJobsAsync(new JobQuery { WireName = "inventory-sync" });
        Assert.Equal(2, byWireName.Count);

        Assert.Empty(await fixture.Monitor.ListJobsAsync(new JobQuery { WireName = "no-such-wire-name" }));
    }

    [Fact]
    public async Task QueueDepths_CountByQueueAndState()
    {
        var fixture = CreateFixture();

        await fixture.Client.EnqueueAsync(new InventorySync("a"), dueTime: T0);
        await fixture.Client.EnqueueAsync(new InventorySync("b"), dueTime: T0.AddDays(1));
        await fixture.Client.EnqueueAsync(new InventorySync("c"), dueTime: T0, queue: "reports");

        await fixture.Pump.PumpAsync(T0); // job "a" and "c": Succeeded; "b": still Scheduled

        var depths = await fixture.Monitor.GetQueueDepthsAsync();
        Assert.Equal(
        [
            new QueueStateCount("default", JobState.Scheduled, 1),
            new QueueStateCount("default", JobState.Succeeded, 1),
            new QueueStateCount("reports", JobState.Succeeded, 1),
        ], depths);
    }

    [Fact]
    public async Task ScheduleStatus_ShowsNextDueAndMintedInstances()
    {
        var fixture = CreateFixture();

        await fixture.Client.UpsertRecurringAsync(
            "hourly-sync", Cron.Hourly(atMinute: 0), new InventorySync("eu"), now: T0);

        await fixture.Pump.PumpAsync(T0.AddHours(1)); // first tick mints and runs

        var status = Assert.Single(await fixture.Monitor.ListSchedulesAsync());
        Assert.Equal("hourly-sync", status.ScheduleId);
        Assert.Equal("inventory-sync", status.WireName);
        Assert.Equal(T0.AddHours(1), status.Cursor);
        Assert.Equal(T0.AddHours(2), status.NextDue);
        Assert.False(status.HasLiveInstance);

        var minted = await fixture.Monitor.ListJobsAsync(new JobQuery { ScheduleId = "hourly-sync" });
        var instance = Assert.Single(minted);
        Assert.Equal(JobState.Succeeded, instance.State);
        Assert.Equal("hourly-sync", instance.ScheduleId);
    }

    [Fact]
    public async Task ListJobs_PageIsBoundedByMaxMonitorPageSize()
    {
        var fixture = CreateFixture(new StoreBounds { MaxMonitorPageSize = 2 });

        await fixture.Client.EnqueueAsync(new InventorySync("a"), dueTime: T0);
        await fixture.Client.EnqueueAsync(new InventorySync("b"), dueTime: T0);
        await fixture.Client.EnqueueAsync(new InventorySync("c"), dueTime: T0);

        Assert.Equal(2, (await fixture.Monitor.ListJobsAsync()).Count);
        Assert.Single(await fixture.Monitor.ListJobsAsync(new JobQuery { MaxResults = 1 }));
    }


    [Fact]
    public async Task GetJobCount_CountsTheFilteredPopulation_PastThePageBound()
    {
        var fixture = CreateFixture(new StoreBounds { MaxMonitorPageSize = 2 });

        await fixture.Client.EnqueueAsync(new InventorySync("a"), dueTime: T0);
        await fixture.Client.EnqueueAsync(new InventorySync("b"), dueTime: T0);
        await fixture.Client.EnqueueAsync(new InventorySync("c"), dueTime: T0);
        await fixture.Client.EnqueueAsync(new InventorySync("d"), dueTime: T0.AddDays(1), queue: "reports");

        await fixture.Pump.PumpAsync(T0); // "a", "b", "c": Succeeded; "d": still Scheduled

        Assert.Equal(4, await fixture.Monitor.GetJobCountAsync());
        Assert.Equal(3, await fixture.Monitor.GetJobCountAsync(new JobQuery { State = JobState.Succeeded }));
        Assert.Equal(1, await fixture.Monitor.GetJobCountAsync(new JobQuery { Queue = "reports" }));
        Assert.Equal(3, await fixture.Monitor.GetJobCountAsync(new JobQuery { Queue = "default", MaxResults = 1 }));
        Assert.Equal(0, await fixture.Monitor.GetJobCountAsync(new JobQuery { WireName = "no-such-wire-name" }));
    }

    [Fact]
    public async Task GetJobCount_OnAStoreWithoutACountOverride_PagesThroughTheListing()
    {
        var inner = new InMemoryJobStore(new StoreBounds { MaxMonitorPageSize = 2 });
        var store = new ListingOnlyStore(inner);
        for (var i = 0; i < 5; i++)
        {
            await inner.EnqueueAsync(new NewJob(Guid.NewGuid(), "inventory-sync", "{}"u8.ToArray(), "default", T0), T0);
        }
        await inner.EnqueueAsync(new NewJob(Guid.NewGuid(), "inventory-sync", "{}"u8.ToArray(), "reports", T0), T0);
        var monitor = new BackWaveMonitor(store);

        // Paging fields on the query are ignored: the default resets the cursor and walks every page.
        Assert.Equal(5, await monitor.GetJobCountAsync(
            new JobQuery { Queue = "default", AfterSequence = 3, SortDirection = JobSortDirection.NewestFirst, MaxResults = 1 }));
        Assert.Equal(4, store.ListCalls); // pages of 2, 2, 1, then the empty page that ends the walk

        Assert.Equal(6, await monitor.GetJobCountAsync());
        Assert.Equal(0, await monitor.GetJobCountAsync(new JobQuery { Queue = "nowhere" }));
    }

    /// <summary>
    /// Forwards every member to the In-Memory Store except the filtered count, so the count falls back
    /// to the store contract's default, which pages through the listing. Counts the listing reads.
    /// </summary>
    private sealed class ListingOnlyStore(IJobStore inner) : IJobStore
    {
        public int ListCalls { get; private set; }

        public ValueTask<IReadOnlyList<JobRecord>> ClaimAsync(ClaimRequest request, CancellationToken cancellationToken = default)
            => inner.ClaimAsync(request, cancellationToken);

        public bool SupportsTransactionalEnqueue => inner.SupportsTransactionalEnqueue;

        public ValueTask<EnqueueResult> EnqueueAsync(
            NewJob job, DateTimeOffset now, System.Data.Common.DbTransaction? transaction = null, CancellationToken cancellationToken = default)
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
        {
            ListCalls++;
            return inner.ListJobsAsync(query, cancellationToken);
        }

        public ValueTask<IReadOnlyList<QueueStateCount>> CountJobsAsync(CancellationToken cancellationToken = default)
            => inner.CountJobsAsync(cancellationToken);

        public ValueTask<IReadOnlyList<TagFacet>> FacetAsync(
            string key, JobQuery? baseQuery = null, int maxResults = int.MaxValue, CancellationToken cancellationToken = default)
            => inner.FacetAsync(key, baseQuery, maxResults, cancellationToken);

        public ValueTask<IReadOnlyList<TagSuggestion>> SuggestTagsAsync(TagSuggestQuery query, CancellationToken cancellationToken = default)
            => inner.SuggestTagsAsync(query, cancellationToken);

        public ValueTask<IReadOnlyList<QueueSettings>> ListQueueSettingsAsync(CancellationToken cancellationToken = default)
            => inner.ListQueueSettingsAsync(cancellationToken);

        public ValueTask<DependencyEdges> GetDependencyEdgesAsync(Guid jobId, CancellationToken cancellationToken = default)
            => inner.GetDependencyEdgesAsync(jobId, cancellationToken);

        public ValueTask<WorkflowEnqueueResult> EnqueueWorkflowAsync(
            WorkflowDefinition workflow, DateTimeOffset now, System.Data.Common.DbTransaction? transaction = null,
            CancellationToken cancellationToken = default)
            => inner.EnqueueWorkflowAsync(workflow, now, transaction, cancellationToken);

        public ValueTask<IReadOnlyList<WorkflowSnapshot>> ListWorkflowsAsync(CancellationToken cancellationToken = default)
            => inner.ListWorkflowsAsync(cancellationToken);

        public ValueTask<WorkflowGraph?> GetWorkflowAsync(Guid workflowId, CancellationToken cancellationToken = default)
            => inner.GetWorkflowAsync(workflowId, cancellationToken);

        public ValueTask<int> PurgeTerminalAsync(
            TerminalStateClass stateClass, DateTimeOffset terminalBefore, int maxJobs, CancellationToken cancellationToken = default)
            => inner.PurgeTerminalAsync(stateClass, terminalBefore, maxJobs, cancellationToken);

        public ValueTask<ObserverClaim> ClaimObserverDeliveriesAsync(
            ObserverClaimRequest request, CancellationToken cancellationToken = default)
            => inner.ClaimObserverDeliveriesAsync(request, cancellationToken);

        public ValueTask ReportObserverDeliveriesAsync(
            ObserverDeliveryReport report, CancellationToken cancellationToken = default)
            => inner.ReportObserverDeliveriesAsync(report, cancellationToken);

        // Forwarded as well as the void twin: without this override the interface default answers
        // Unreported and flattens the inner store's fence verdict before the caller ever sees it.
        public ValueTask<ObserverReportOutcome> TryReportObserverDeliveriesAsync(
            ObserverDeliveryReport report, CancellationToken cancellationToken = default)
            => inner.TryReportObserverDeliveriesAsync(report, cancellationToken);

        public ValueTask<long> GetObserverCursorAsync(string observerId, CancellationToken cancellationToken = default)
            => inner.GetObserverCursorAsync(observerId, cancellationToken);

        public ValueTask<ObserverLag> GetObserverLagAsync(ObserverLagRequest request, CancellationToken cancellationToken = default)
            => inner.GetObserverLagAsync(request, cancellationToken);

        public ValueTask<IReadOnlyList<ObserverDeadLetterRecord>> ListObserverDeadLettersAsync(
            string observerId, CancellationToken cancellationToken = default)
            => inner.ListObserverDeadLettersAsync(observerId, cancellationToken);
    }
}
