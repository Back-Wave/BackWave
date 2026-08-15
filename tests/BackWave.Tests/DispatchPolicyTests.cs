using System.Text.Json.Serialization;
using BackWave.Core;
using BackWave.Driver;
using BackWave.Jobs;
using BackWave.Storage;
using BackWave.Testing;
using BackWave.Storage.InMemory;
using Microsoft.Extensions.DependencyInjection;

namespace BackWave.Tests;

public sealed record QueuedWork(string Tag);

public sealed class QueuedWorkHandler(WorkRecorder recorder) : IJobHandler<QueuedWork>
{
    public Task HandleAsync(QueuedWork job, JobContext context, CancellationToken cancellationToken)
    {
        recorder.Ran.Add(job.Tag);
        return Task.CompletedTask;
    }
}

public sealed class WorkRecorder
{
    public List<string> Ran { get; } = [];
}

[JsonSerializable(typeof(QueuedWork))]
internal sealed partial class DispatchJsonContext : JsonSerializerContext;

public class DispatchPolicyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Swrr_RejectsEmptyAndSubUnitWeights()
    {
        // No queue at all is invalid...
        Assert.Throws<ArgumentException>(() => new SmoothWeightedRoundRobin([]));
        // ...and so is a non-empty set where *any* weight is below 1 — the guard is "at least one
        // queue AND every weight >= 1", not "all weights below 1" and not "empty AND has a bad weight".
        Assert.Throws<ArgumentException>(() => new SmoothWeightedRoundRobin([("a", 6), ("b", 0)]));
        Assert.Throws<ArgumentException>(() => new SmoothWeightedRoundRobin([("a", -1)]));
    }

    [Fact]
    public void Swrr_SixThreeOne_IsExactAndSmooth()
    {
        var swrr = new SmoothWeightedRoundRobin([("a", 6), ("b", 3), ("c", 1)]);
        var picks = new List<string>();
        for (var i = 0; i < 10; i++)
        {
            var order = swrr.NextOrder();
            picks.Add(order[0]);
            swrr.Charge(order[0]);
        }

        Assert.Equal(["a", "b", "a", "a", "b", "a", "c", "a", "b", "a"], picks);

        // And the pattern repeats exactly, period 10.
        for (var i = 0; i < 10; i++)
        {
            var order = swrr.NextOrder();
            Assert.Equal(picks[i], order[0]);
            swrr.Charge(order[0]);
        }
    }

    [Fact]
    public void Swrr_NginxClassic_FiveOneOne()
    {
        var swrr = new SmoothWeightedRoundRobin([("a", 5), ("b", 1), ("c", 1)]);
        var picks = new List<string>();
        for (var i = 0; i < 7; i++)
        {
            var order = swrr.NextOrder();
            picks.Add(order[0]);
            swrr.Charge(order[0]);
        }
        Assert.Equal(["a", "a", "b", "a", "c", "a", "a"], picks);
    }

    private sealed class Fixture
    {
        public InMemoryJobStore Store { get; } = new();
        public WorkRecorder Recorder { get; }
        private readonly JobRegistry _registry;
        private readonly IServiceProvider _services;

        public Fixture()
        {
            _services = new ServiceCollection()
                .AddSingleton<WorkRecorder>()
                .AddTransient<IJobHandler<QueuedWork>, QueuedWorkHandler>()
                .BuildServiceProvider();
            _registry = new JobRegistry(
            [
                JobRegistration.Create<QueuedWork, QueuedWorkHandler>(
                    "queued-work", DispatchJsonContext.Default.QueuedWork),
            ]);
            Recorder = _services.GetRequiredService<WorkRecorder>();
        }

        public async Task Enqueue(string queue, string tag, int count)
        {
            var client = new BackWaveClient(Store, _registry);
            for (var i = 0; i < count; i++)
            {
                await client.EnqueueAsync(new QueuedWork($"{tag}{i}"), dueTime: T0, queue: queue);
            }
        }

        public DeterministicPump NewWorkerGroup(string workerId, DispatchPolicy policy, int maxClaimBatch = 32)
            => new(
                new NodeDriver(new NodeOptions { WorkerId = workerId, Policy = policy, MaxClaimBatch = maxClaimBatch }),
                Store, _registry, _services);
    }

    [Fact]
    public async Task Strict_CriticalAlwaysPreemptsBulk()
    {
        var fixture = new Fixture();
        await fixture.Enqueue("bulk", "b", 3);
        await fixture.Enqueue("critical", "c", 3);

        var pump = fixture.NewWorkerGroup("node-1", new DispatchPolicy.Strict(["critical", "bulk"]));
        await pump.PumpAsync(T0);

        Assert.Equal(6, fixture.Recorder.Ran.Count);
        var criticalPositions = fixture.Recorder.Ran.Select((tag, i) => (tag, i)).Where(x => x.tag.StartsWith('c')).Select(x => x.i);
        var bulkPositions = fixture.Recorder.Ran.Select((tag, i) => (tag, i)).Where(x => x.tag.StartsWith('b')).Select(x => x.i);
        Assert.True(criticalPositions.Max() < bulkPositions.Min(),
            $"critical must run before bulk, got: {string.Join(",", fixture.Recorder.Ran)}");
    }

    [Fact]
    public async Task Strict_IsWorkConserving_WhenPriorityQueueIsEmpty()
    {
        var fixture = new Fixture();
        await fixture.Enqueue("bulk", "b", 3);

        var pump = fixture.NewWorkerGroup("node-1", new DispatchPolicy.Strict(["critical", "bulk"]));
        await pump.PumpAsync(T0);

        Assert.Equal(3, fixture.Recorder.Ran.Count);
    }

    [Fact]
    public async Task Weighted_FirstTenClaims_AreExactlySixThreeOne()
    {
        var fixture = new Fixture();
        await fixture.Enqueue("a", "a", 10);
        await fixture.Enqueue("b", "b", 10);
        await fixture.Enqueue("c", "c", 10);

        var pump = fixture.NewWorkerGroup(
            "node-1",
            new DispatchPolicy.Weighted([("a", 6), ("b", 3), ("c", 1)]),
            maxClaimBatch: 10);
        await pump.PumpAsync(T0);

        // Execution order mirrors claim order: every consecutive window of 10 claims
        // shares 6:3:1 exactly, and the whole backlog drains (work-conserving).
        Assert.Equal(30, fixture.Recorder.Ran.Count);
        var firstTen = fixture.Recorder.Ran.Take(10).ToList();
        Assert.Equal(6, firstTen.Count(t => t.StartsWith('a')));
        Assert.Equal(3, firstTen.Count(t => t.StartsWith('b')));
        Assert.Equal(1, firstTen.Count(t => t.StartsWith('c')));
    }

    [Fact]
    public async Task Weighted_IsWorkConserving_EmptyQueuesYieldTheirShare()
    {
        var fixture = new Fixture();
        await fixture.Enqueue("c", "c", 5); // only the weight-1 queue has work

        var pump = fixture.NewWorkerGroup(
            "node-1", new DispatchPolicy.Weighted([("a", 6), ("b", 3), ("c", 1)]));
        await pump.PumpAsync(T0);

        Assert.Equal(5, fixture.Recorder.Ran.Count);
    }

    [Fact]
    public async Task TwoWorkerGroupsInOneProcess_ServeDisjointQueues()
    {
        var fixture = new Fixture();
        await fixture.Enqueue("critical", "c", 2);
        await fixture.Enqueue("bulk", "b", 2);

        var criticalPool = fixture.NewWorkerGroup("critical-pool", new DispatchPolicy.Strict(["critical"]));
        var generalPool = fixture.NewWorkerGroup("general-pool", new DispatchPolicy.Weighted([("bulk", 1)]));

        await criticalPool.PumpAsync(T0);
        Assert.Equal(["c0", "c1"], fixture.Recorder.Ran);

        await generalPool.PumpAsync(T0);
        Assert.Equal(["c0", "c1", "b0", "b1"], fixture.Recorder.Ran);
    }

    [Fact]
    public async Task QueueDeclaredOnJobType_OverridableAtEnqueue()
    {
        var fixture = new Fixture();
        var client = new BackWaveClient(fixture.Store, new JobRegistry(
        [
            JobRegistration.Create<QueuedWork, QueuedWorkHandler>(
                "queued-work", DispatchJsonContext.Default.QueuedWork, queue: "emails"),
        ]));

        var byDefault = await client.EnqueueAsync(new QueuedWork("x"), dueTime: T0);
        var overridden = await client.EnqueueAsync(new QueuedWork("y"), dueTime: T0, queue: "reports");

        Assert.Equal("emails", (await fixture.Store.GetJobAsync(byDefault))!.Queue);
        Assert.Equal("reports", (await fixture.Store.GetJobAsync(overridden))!.Queue);
    }

    // --- Batched Weighted dispatch (issue 0040) ---

    [Fact]
    public async Task Weighted_FillsThePool_InOneClaimPerQueue_NotOnePerJob()
    {
        var store = new InMemoryJobStore();
        var counting = new ClaimCountingStore(store);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var services = new ServiceCollection()
            .AddSingleton<IJobHandler<QueuedWork>>(new GateHandler(gate.Task))
            .BuildServiceProvider();
        var registry = new JobRegistry(
            [JobRegistration.Create<QueuedWork, GateHandler>("queued-work", DispatchJsonContext.Default.QueuedWork)]);

        var client = new BackWaveClient(store, registry);
        foreach (var queue in new[] { "a", "b", "c" })
        {
            for (var i = 0; i < 12; i++)
            {
                await client.EnqueueAsync(new QueuedWork($"{queue}{i}"), dueTime: T0, queue: queue);
            }
        }

        var pump = new DeterministicPump(
            new NodeDriver(new NodeOptions
            {
                WorkerId = "node-1",
                Policy = new DispatchPolicy.Weighted([("a", 6), ("b", 3), ("c", 1)]),
                PoolSize = 12,
                MaxClaimBatch = 12,
            }),
            counting, registry, services);

        // The handlers block, so the pool fills in a single pass and never re-polls.
        await pump.PumpAsync(T0);

        Assert.Equal(3, counting.Claims); // O(Q): one claim per served Queue, not one per worker
        var leased = await store.ListJobsAsync(new JobQuery { State = JobState.Leased });
        Assert.Equal(12, leased.Count);   // the whole pool was filled

        gate.SetResult(); // release the blocked handlers
    }

    [Fact]
    public void WeightedAllocation_MatchesTheWeights_Deterministically_AndEqualsSingleStepping()
    {
        // The batch allocation IS the SWRR sequence: allocating N slots equals taking N single
        // NextOrder/Charge steps, so the long-run distribution matches the weights exactly.
        var batched = new SmoothWeightedRoundRobin([("a", 6), ("b", 3), ("c", 1)]);
        var allocation = batched.Allocate(1000);
        Assert.Equal([600, 300, 100], allocation);

        var stepwise = new SmoothWeightedRoundRobin([("a", 6), ("b", 3), ("c", 1)]);
        var counts = new Dictionary<string, int> { ["a"] = 0, ["b"] = 0, ["c"] = 0 };
        for (var i = 0; i < 1000; i++)
        {
            var winner = stepwise.NextOrder()[0];
            stepwise.Charge(winner);
            counts[winner]++;
        }
        Assert.Equal(allocation[0], counts["a"]);
        Assert.Equal(allocation[1], counts["b"]);
        Assert.Equal(allocation[2], counts["c"]);
    }

    [Fact]
    public async Task Weighted_AnUnderFillingQueue_ReflowsItsSlots_NoWorkerIdles()
    {
        var fixture = new Fixture();
        await fixture.Enqueue("a", "a", 2);   // the weight-6 queue can fill only 2 of its 6 slots
        await fixture.Enqueue("b", "b", 100);
        await fixture.Enqueue("c", "c", 100);

        var pump = fixture.NewWorkerGroup(
            "node-1", new DispatchPolicy.Weighted([("a", 6), ("b", 3), ("c", 1)]), maxClaimBatch: 10);
        await pump.PumpAsync(T0);

        // The pass still claims all ten — a's four unfilled slots reflow to the due queues.
        var firstTen = fixture.Recorder.Ran.Take(10).ToList();
        Assert.Equal(10, firstTen.Count);
        Assert.Equal(2, firstTen.Count(t => t.StartsWith('a')));
        Assert.Equal(8, firstTen.Count(t => t.StartsWith('b') || t.StartsWith('c')));
    }

    [Fact]
    public void WeightedAllocation_IsPure_SoADroppedPassStrandsNoCredit()
    {
        // Allocate only SIZES a pass; the persistent credit advances later, via AdvanceServed, as
        // each per-Queue batch is actually issued. So a pass that is sized but then dropped — a
        // re-poll Clears the plan, or an empty claim ends the chain before its tail batches drain —
        // must move no credit, leaving the next pass to size identically. Under the old
        // charge-at-allocation behaviour the first sizing already debited every slot, pushing the
        // dropped Queues into a deficit; here three back-to-back sizings without any AdvanceServed
        // are identical, proving the un-issued slots strand nothing.
        var swrr = new SmoothWeightedRoundRobin([("a", 6), ("b", 3), ("c", 1)]);

        var first = swrr.Allocate(7); // 7 is off the period-10 boundary, so a moved credit would show
        Assert.Equal(first, swrr.Allocate(7));
        Assert.Equal(first, swrr.Allocate(7));
    }

    [Fact]
    public void Weighted_InterruptedPasses_DoNotStarveDroppedQueues()
    {
        // A weighted pass enqueues one batch per Queue, issues the first, and chains the rest
        // one-per-completion. A re-poll (RequestPoll fires on every applied outcome) Clears the
        // plan, and an empty claim ends the chain early — either way the un-issued tail batches are
        // dropped. Run the SAME interruption schedule two ways: the fixed driver advances credit
        // (AdvanceServed) only for the batches it actually issues, while the old driver charged the
        // whole allocation up front (modelled by advancing every sized slot, dropped or not). Under
        // the old behaviour a repeatedly-dropped Queue is charged-but-unserved and runs a deficit,
        // so it is served less than its weight; under the fix it keeps its credit and is sized back
        // up, so the served distribution tracks the configured weights more closely.
        var fixedRr = new SmoothWeightedRoundRobin([("a", 6), ("b", 3), ("c", 1)]);
        var oldRr = new SmoothWeightedRoundRobin([("a", 6), ("b", 3), ("c", 1)]);
        var fixedServed = new long[3];
        var oldServed = new long[3];

        const int passes = 2000;
        const int perPass = 7; // off the period-10 boundary, so the charge-all deficit actually drifts
        for (var pass = 0; pass < passes; pass++)
        {
            var interrupt = pass % 4 == 0; // a re-poll drops the tail on one pass in four

            // Fixed: credit advances only for the batches actually issued.
            var fixedAlloc = fixedRr.Allocate(perPass);
            var issued = 0;
            for (var i = 0; i < fixedAlloc.Count; i++)
            {
                if (fixedAlloc[i] == 0)
                {
                    continue;
                }
                if (interrupt && issued > 0)
                {
                    break; // tail batch dropped before issue — never advanced, so no stranded credit
                }
                fixedRr.AdvanceServed(fixedAlloc[i]);
                fixedServed[i] += fixedAlloc[i];
                issued++;
            }

            // Old: the whole allocation is charged up front, but the dropped tail is still not served.
            var oldAlloc = oldRr.Allocate(perPass);
            oldRr.AdvanceServed(perPass); // charge-at-allocation: every sized slot, dropped or not
            var oldIssued = 0;
            for (var i = 0; i < oldAlloc.Count; i++)
            {
                if (oldAlloc[i] == 0)
                {
                    continue;
                }
                if (interrupt && oldIssued > 0)
                {
                    break;
                }
                oldServed[i] += oldAlloc[i];
                oldIssued++;
            }
        }

        double fixedTotal = fixedServed.Sum();
        double oldTotal = oldServed.Sum();
        double[] weightShare = [0.6, 0.3, 0.1];

        // The interruptions cost the dropped middle Queue 'b' service either way, but the fix keeps it
        // from running the old charge-at-allocation deficit: it is served strictly more under the fix.
        Assert.True(fixedServed[1] > oldServed[1],
            $"the dropped queue must not be starved: fixed b={fixedServed[1]} vs old b={oldServed[1]}");

        // And the whole served distribution sits closer to the configured 6:3:1 weights under the fix.
        var fixedDeviation = Enumerable.Range(0, 3).Sum(i => Math.Abs(fixedServed[i] / fixedTotal - weightShare[i]));
        var oldDeviation = Enumerable.Range(0, 3).Sum(i => Math.Abs(oldServed[i] / oldTotal - weightShare[i]));
        Assert.True(fixedDeviation < oldDeviation,
            $"charge-at-issue must track the weights better: fixed deviation {fixedDeviation:F4} vs old {oldDeviation:F4}");
    }
}

/// <summary>A handler that records nothing and blocks until the gate is released — holds a filled pool.</summary>
internal sealed class GateHandler(Task gate) : IJobHandler<QueuedWork>
{
    public async Task HandleAsync(QueuedWork job, JobContext context, CancellationToken cancellationToken)
        => await gate;
}

/// <summary>Wraps the In-Memory Store and counts Claim round-trips — the §0040 cost assertion.</summary>
internal sealed class ClaimCountingStore(IJobStore inner) : IJobStore
{
    private int _claims;
    public int Claims => Volatile.Read(ref _claims);

    public ValueTask<IReadOnlyList<JobRecord>> ClaimAsync(ClaimRequest request, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _claims);
        return inner.ClaimAsync(request, cancellationToken);
    }

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
        => inner.ListJobsAsync(query, cancellationToken);

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

    public ValueTask<long> GetObserverCursorAsync(string observerId, CancellationToken cancellationToken = default)
        => inner.GetObserverCursorAsync(observerId, cancellationToken);

    public ValueTask<ObserverLag> GetObserverLagAsync(ObserverLagRequest request, CancellationToken cancellationToken = default)
        => inner.GetObserverLagAsync(request, cancellationToken);

    public ValueTask<IReadOnlyList<ObserverDeadLetterRecord>> ListObserverDeadLettersAsync(
        string observerId, CancellationToken cancellationToken = default)
        => inner.ListObserverDeadLettersAsync(observerId, cancellationToken);
}
