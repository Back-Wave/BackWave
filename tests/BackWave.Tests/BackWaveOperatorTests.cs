using BackWave.Operations;
using BackWave.Storage;
using BackWave.Storage.InMemory;

namespace BackWave.Tests;

/// <summary>
/// The Operator Actions surface (§5.8). The store-level semantics are pinned by the Conformance
/// Suite; here we pin what the surface itself adds — it owns the clock (§1), defaulting the
/// instant an action records to the injected TimeProvider, and forwards faithfully to the store.
/// </summary>
public sealed class BackWaveOperatorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static NewJob Job(DateTimeOffset due) => new(Guid.NewGuid(), "op-job", "{}"u8.ToArray(), "default", due);

    [Fact]
    public async Task ActionsDefaultTheRecordedInstant_ToTheInjectedClock()
    {
        var store = new InMemoryJobStore();
        var operator_ = new BackWaveOperator(store, new FixedClock(T0));
        var job = Job(T0.AddHours(1));
        await store.EnqueueAsync(job, now: T0);

        // No explicit now: the surface stamps the action with the clock's instant.
        Assert.Equal(CancelResult.CancelledImmediately, await operator_.CancelJobAsync(job.JobId, "alice"));

        var audit = Assert.Single(await operator_.ListAuditRecordsAsync(job.JobId.ToString()));
        Assert.Equal(T0, audit.RecordedAt);
        Assert.Equal("alice", audit.Actor);
        Assert.Equal("alice", (await store.GetJobAsync(job.JobId))!.TerminalCause);
    }

    [Fact]
    public async Task PauseAndResume_GoThroughTheSurface_AndGovernClaiming()
    {
        var store = new InMemoryJobStore();
        var operator_ = new BackWaveOperator(store, new FixedClock(T0));
        await store.EnqueueAsync(Job(T0), now: T0);

        await operator_.PauseQueueAsync("default", "alice");
        Assert.Empty(await store.ClaimAsync(new ClaimRequest("w1", ["default"], 8, TimeSpan.FromMinutes(1), T0)));

        await operator_.ResumeQueueAsync("default", "alice");
        Assert.Single(await store.ClaimAsync(new ClaimRequest("w1", ["default"], 8, TimeSpan.FromMinutes(1), T0)));
    }

    [Fact]
    public async Task SetConcurrencyLimit_GoesThroughTheSurface_GovernsClaiming_AndIsAudited()
    {
        var store = new InMemoryJobStore();
        var operator_ = new BackWaveOperator(store, new FixedClock(T0));
        await store.EnqueueAsync(Job(T0), now: T0);
        await store.EnqueueAsync(Job(T0), now: T0);

        // Cap of 1: one claim takes one job; the second slot only frees when the first lease ends.
        await operator_.SetConcurrencyLimitAsync("default", 1, "alice");
        Assert.Single(await store.ClaimAsync(new ClaimRequest("w1", ["default"], 8, TimeSpan.FromMinutes(1), T0)));
        Assert.Empty(await store.ClaimAsync(new ClaimRequest("w2", ["default"], 8, TimeSpan.FromMinutes(1), T0)));

        // Audited like every other operator action, stamped with the injected clock's instant.
        var audit = Assert.Single(await operator_.ListAuditRecordsAsync("default"));
        Assert.Equal(OperatorAction.SetConcurrencyLimit, audit.Action);
        Assert.Equal("alice", audit.Actor);
        Assert.Equal(T0, audit.RecordedAt);

        // Null clears the cap: the parked job claims immediately.
        await operator_.SetConcurrencyLimitAsync("default", null, "alice");
        Assert.Single(await store.ClaimAsync(new ClaimRequest("w2", ["default"], 8, TimeSpan.FromMinutes(1), T0)));
    }

    [Fact]
    public async Task Requeue_ForwardsResult_AndIsRejectedForLiveJobs()
    {
        var store = new InMemoryJobStore();
        var operator_ = new BackWaveOperator(store, new FixedClock(T0));
        var job = Job(T0);
        await store.EnqueueAsync(job, now: T0);

        // A Scheduled job is not requeueable; the surface returns the store's verdict.
        Assert.Equal(RequeueResult.NotRequeueable, await operator_.RequeueAsync(job.JobId, "alice"));
    }
}
