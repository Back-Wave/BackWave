using BackWave.Observers;
using BackWave.Storage;
using BackWave.Storage.InMemory;

namespace BackWave.Tests;

/// <summary>
/// Transition Observer rich context (§0079, ADR 0017): the <see cref="ObserverContext"/> the Shell
/// hands an observer carries the transition facts eagerly, the (capture-gated) Failure Detail, and a
/// <b>lazy</b> payload accessor that reads on first touch and reports the purged case honestly. The
/// Job History Policy ≥ Transitions dependency is enforced at registration.
/// </summary>
public class ObserverContextTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(1);

    private static ObserverClaimedDelivery Delivery(Guid jobId, JobState state, string? failureDetail = null) =>
        new(Position: 0, jobId, Ordinal: 0, WireName: "pay-job", Queue: "default",
            state, Attempt: 0, Timestamp: T0, failureDetail, DeliveryAttempt: 1);

    [Fact]
    public async Task Payload_IsReadLazily_OnlyWhenTouched_AndMemoized()
    {
        var reads = 0;
        var accessor = new ObserverPayloadAccessor(_ =>
        {
            reads++;
            return ValueTask.FromResult(ObserverPayload.Present(new byte[] { 1, 2, 3 }));
        });

        Assert.Equal(0, reads); // a delivery that never reaches for the payload pays no read cost

        var first = await accessor.GetAsync();
        var second = await accessor.GetAsync();

        Assert.Equal(1, reads); // first touch reads; repeated touches are memoized
        Assert.True(first.Available);
        Assert.Equal(new byte[] { 1, 2, 3 }, first.Bytes.ToArray());
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task Payload_FromDelivery_ReadsTheJobRow()
    {
        var store = new InMemoryJobStore();
        var jobId = Guid.NewGuid();
        await store.EnqueueAsync(new NewJob(jobId, "pay-job", new byte[] { 7, 8, 9 }, "default", T0), T0);

        var payload = await ObserverContext.FromDelivery(Delivery(jobId, JobState.Scheduled), store).Payload.GetAsync();

        Assert.True(payload.Available);
        Assert.Equal(new byte[] { 7, 8, 9 }, payload.Bytes.ToArray());
    }

    [Fact]
    public async Task Payload_FromDelivery_ReportsNotAvailable_WhenJobPurged()
    {
        var store = new InMemoryJobStore();
        // No job with this id exists — the lazy read raced (and lost to) the retention sweep.
        var payload = await ObserverContext.FromDelivery(Delivery(Guid.NewGuid(), JobState.DeadLettered), store).Payload.GetAsync();

        Assert.False(payload.Available);
        Assert.Equal(ObserverPayload.NotAvailable, payload);
    }

    [Fact]
    public async Task FailureDetail_IsCaptured_WhenPolicyKeepsIt()
    {
        var detail = await DeadLetterAndClaimDetail(JobHistoryPolicy.TransitionsAndFailureDetail);
        Assert.Equal("boom: stack trace", detail);
    }

    [Fact]
    public async Task FailureDetail_IsNull_WhenCaptureDisabledByPolicy()
    {
        var detail = await DeadLetterAndClaimDetail(JobHistoryPolicy.Transitions);
        Assert.Null(detail);
    }

    /// <summary>Drive one job Leased→DeadLettered with a Failure Detail, then claim the observer delivery for it.</summary>
    private static async Task<string?> DeadLetterAndClaimDetail(JobHistoryPolicy policy)
    {
        var store = new InMemoryJobStore(historyPolicy: policy);
        var jobId = Guid.NewGuid();
        await store.EnqueueAsync(new NewJob(jobId, "pay-job", ReadOnlyMemory<byte>.Empty, "default", T0), T0);
        var claimed = await store.ClaimAsync(new ClaimRequest("node-a", ["default"], 32, Lease, T0));
        var job = Assert.Single(claimed);
        // Failure with no next-due-time dead-letters; the detail rides only the failing transition (§5.12).
        await store.ReportOutcomeAsync(
            jobId, "node-a", job.Attempt, new JobOutcome.Failure(NextDueTime: null, "boom"), T0, failureDetail: "boom: stack trace");

        var claim = await store.ClaimObserverDeliveriesAsync(new ObserverClaimRequest(
            "o1", [JobState.DeadLettered], WireName: null, Queue: null, "node-a", MaxRows: 32, Lease, T0));

        var delivery = Assert.Single(claim.Deliveries);
        Assert.Equal(JobState.DeadLettered, delivery.State);
        return delivery.FailureDetail;
    }

    [Fact]
    public void EnsureDeliverableUnder_Throws_WhenHistoryOff()
    {
        var registration = new ObserverRegistration("o1", new ObserverSubscription([JobState.Succeeded]));

        var ex = Assert.Throws<InvalidOperationException>(() => registration.EnsureDeliverableUnder(JobHistoryPolicy.Off));
        Assert.Contains("o1", ex.Message);
        Assert.Contains("History Policy", ex.Message);
    }

    [Theory]
    [InlineData(JobHistoryPolicy.Transitions)]
    [InlineData(JobHistoryPolicy.TransitionsAndFailureDetail)]
    public void EnsureDeliverableUnder_Passes_WhenHistoryRecordsTransitions(JobHistoryPolicy policy)
    {
        var registration = new ObserverRegistration("o1", new ObserverSubscription([JobState.Succeeded]));
        registration.EnsureDeliverableUnder(policy); // does not throw
    }
}
