using System.Data;
using System.Data.Common;
using System.Text.Json.Serialization;
using BackWave.Core;
using BackWave.Driver;
using BackWave.Jobs;
using BackWave.Storage;
using BackWave.Testing;
using BackWave.Storage.InMemory;
using Microsoft.Extensions.DependencyInjection;

namespace BackWave.Tests;

public sealed record OrderReceipt(string OrderId);

public sealed class OrderReceiptHandler(ReceiptRecorder recorder) : IJobHandler<OrderReceipt>
{
    public Task HandleAsync(OrderReceipt job, JobContext context, CancellationToken cancellationToken)
    {
        recorder.Sent.Add(job.OrderId);
        return Task.CompletedTask;
    }
}

public sealed class ReceiptRecorder
{
    public List<string> Sent { get; } = [];
}

[JsonSerializable(typeof(OrderReceipt))]
internal sealed partial class TransactionalJsonContext : JsonSerializerContext;

public class TransactionalEnqueueTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(
        BackWaveClient Client,
        DeterministicPump Pump,
        InMemoryJobStore Store,
        ReceiptRecorder Recorder);

    private static Fixture CreateFixture(IJobStore? storeOverride = null)
    {
        var services = new ServiceCollection()
            .AddSingleton<ReceiptRecorder>()
            .AddTransient<IJobHandler<OrderReceipt>, OrderReceiptHandler>()
            .BuildServiceProvider();

        var registry = new JobRegistry(
        [
            JobRegistration.Create<OrderReceipt, OrderReceiptHandler>(
                "order-receipt", TransactionalJsonContext.Default.OrderReceipt),
        ]);

        var store = new InMemoryJobStore();
        var driver = new NodeDriver(new NodeOptions
        {
            WorkerId = "node-1",
            Policy = new DispatchPolicy.Strict(["default"]),
            RetryPolicy = RetryPolicy.Default,
        });
        var pump = new DeterministicPump(driver, storeOverride ?? store, registry, services);

        return new Fixture(
            new BackWaveClient(storeOverride ?? store, registry),
            pump, store, services.GetRequiredService<ReceiptRecorder>());
    }

    [Fact]
    public void SupportsTransactionalEnqueue_IsTrueForInMemoryStore()
        => Assert.True(new InMemoryJobStore().SupportsTransactionalEnqueue);

    [Fact]
    public async Task Rollback_MeansTheJobNeverExisted()
    {
        var fixture = CreateFixture();

        Guid jobId;
        using (var transaction = fixture.Store.BeginTransaction())
        {
            jobId = await fixture.Client.EnqueueAsync(
                new OrderReceipt("order-1"), dueTime: T0, transaction: transaction);

            // Invisible while the transaction is open: not claimable, not in Monitor reads.
            Assert.Null(await fixture.Store.GetJobAsync(jobId));
            await fixture.Pump.PumpAsync(T0);
            Assert.Empty(fixture.Recorder.Sent);

            transaction.Rollback();
        }

        // No trace: never claimable, never visible, however much time passes.
        Assert.Null(await fixture.Store.GetJobAsync(jobId));
        await fixture.Pump.PumpAsync(T0.AddHours(1));
        Assert.Empty(fixture.Recorder.Sent);
    }

    [Fact]
    public async Task DisposeWithoutCommit_IsRollback()
    {
        var fixture = CreateFixture();

        Guid jobId;
        using (var transaction = fixture.Store.BeginTransaction())
        {
            jobId = await fixture.Client.EnqueueAsync(
                new OrderReceipt("order-2"), dueTime: T0, transaction: transaction);
        }

        Assert.Null(await fixture.Store.GetJobAsync(jobId));
        await fixture.Pump.PumpAsync(T0);
        Assert.Empty(fixture.Recorder.Sent);
    }

    [Fact]
    public async Task Commit_MakesTheJobClaimableThroughTheNormalPipeline()
    {
        var fixture = CreateFixture();

        Guid jobId;
        using (var transaction = fixture.Store.BeginTransaction())
        {
            jobId = await fixture.Client.EnqueueAsync(
                new OrderReceipt("order-3"), dueTime: T0, transaction: transaction);
            transaction.Commit();
        }

        await fixture.Pump.PumpAsync(T0);

        Assert.Equal(["order-3"], fixture.Recorder.Sent);
        Assert.Equal(JobState.Succeeded, (await fixture.Store.GetJobAsync(jobId))!.State);
    }

    [Fact]
    public async Task DuplicateWithinTransaction_RejectedAtTheCallSite()
    {
        var fixture = CreateFixture();
        var job = new NewJob(Guid.NewGuid(), "order-receipt", "{}"u8.ToArray(), "default", T0);

        using var transaction = fixture.Store.BeginTransaction();
        Assert.Equal(EnqueueResult.Ok, await fixture.Store.EnqueueAsync(job, now: T0, transaction));
        Assert.Equal(EnqueueResult.Duplicate, await fixture.Store.EnqueueAsync(job, now: T0, transaction));
    }

    [Fact]
    public async Task CommittedTransaction_RejectsEveryFurtherUse()
    {
        var fixture = CreateFixture();
        var job = new NewJob(Guid.NewGuid(), "order-receipt", "{}"u8.ToArray(), "default", T0);

        var committed = fixture.Store.BeginTransaction();
        committed.Commit();

        // A completed transaction is spent: it stays marked completed and rejects a second commit, a
        // rollback, and any late enqueue — the "already committed or rolled back" guard, not a silent
        // no-op that would let a caller re-drive a used scope.
        Assert.Throws<InvalidOperationException>(() => committed.Commit());
        Assert.Throws<InvalidOperationException>(() => committed.Rollback());
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await fixture.Store.EnqueueAsync(job, now: T0, committed));
    }

    [Fact]
    public async Task RolledBackTransaction_RejectsEveryFurtherUse()
    {
        var fixture = CreateFixture();
        var job = new NewJob(Guid.NewGuid(), "order-receipt", "{}"u8.ToArray(), "default", T0);

        var rolledBack = fixture.Store.BeginTransaction();
        rolledBack.Rollback();

        Assert.Throws<InvalidOperationException>(() => rolledBack.Rollback());
        Assert.Throws<InvalidOperationException>(() => rolledBack.Commit());
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await fixture.Store.EnqueueAsync(job, now: T0, rolledBack));
    }

    [Fact]
    public async Task ForeignTransaction_RejectedWithClearError()
    {
        var fixture = CreateFixture();
        var job = new NewJob(Guid.NewGuid(), "order-receipt", "{}"u8.ToArray(), "default", T0);

        using var foreign = new ForeignTransaction();
        var exception = await Assert.ThrowsAsync<ArgumentException>(
            async () => await fixture.Store.EnqueueAsync(job, now: T0, foreign));
        Assert.Contains("BeginTransaction", exception.Message);
    }

    [Fact]
    public async Task NonSupportingAdapter_SurfacesClearErrorFromTheClient()
    {
        var fixture = CreateFixture(new NonTransactionalStore());

        using var transaction = new ForeignTransaction();
        var exception = await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await fixture.Client.EnqueueAsync(new OrderReceipt("order-4"), dueTime: T0, transaction: transaction));
        Assert.Contains("SupportsTransactionalEnqueue", exception.Message);
    }

    private sealed class ForeignTransaction : DbTransaction
    {
        public override IsolationLevel IsolationLevel => IsolationLevel.Unspecified;
        protected override DbConnection? DbConnection => null;
        public override void Commit() { }
        public override void Rollback() { }
    }

    /// <summary>
    /// A stand-in for a future adapter that cannot enlist in a caller's transaction
    /// (spec §6). The client's capability check throws before any member is reached.
    /// </summary>
    private sealed class NonTransactionalStore : IJobStore
    {
        public bool SupportsTransactionalEnqueue => false;

        public ValueTask<EnqueueResult> EnqueueAsync(
            NewJob job, DateTimeOffset now, DbTransaction? transaction = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

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
            => throw new NotImplementedException();

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
