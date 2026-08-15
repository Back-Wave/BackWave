using BackWave.Storage;
using BackWave.Storage.InMemory;

namespace BackWave.Tests;

/// <summary>
/// Runtime Job Tags (ADR 0022, issue 0110): a handler's buffered Tags flush as a delta on the
/// Attempt's outcome write, riding the (workerId, attempt) fence (Effect-Once). These drive the
/// In-Memory Store's <see cref="IJobStore.ReportOutcomeAsync"/> directly — the adapters don't
/// persist Tags yet (0111), so this lives outside the shared Conformance Suite.
/// </summary>
public class JobTagRuntimeTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(60);

    private static NewJob Job() => new(
        Guid.NewGuid(), "work", ReadOnlyMemory<byte>.Empty, "sendgrid", T0);

    private static ClaimRequest Claim(string worker, DateTimeOffset now)
        => new(worker, ["sendgrid"], 32, Lease, now);

    private static async Task<(InMemoryJobStore Store, JobRecord Claimed)> EnqueuedAndClaimed(string worker = "w1")
    {
        var store = new InMemoryJobStore();
        var job = Job();
        Assert.Equal(EnqueueResult.Ok, await store.EnqueueAsync(job, T0));
        var claimed = await store.ClaimAsync(Claim(worker, T0));
        return (store, Assert.Single(claimed));
    }

    [Fact]
    public async Task TagAddedOnSuccess_PresentAfterTerminalTransition()
    {
        var (store, claimed) = await EnqueuedAndClaimed();
        var delta = JobTags.Empty.WithTag("variant", "BRCA1");

        var result = await store.ReportOutcomeAsync(
            claimed.JobId, "w1", claimed.Attempt, new JobOutcome.Success(), T0, addedTags: delta);

        Assert.Equal(OutcomeResult.Applied, result);
        var job = await store.GetJobAsync(claimed.JobId);
        Assert.Equal(JobState.Succeeded, job!.State);
        Assert.Contains(JobTag.Keyed("variant", "BRCA1"), job.Tags);
    }

    [Fact]
    public async Task TagDeltaOnGracefulFailure_Persists()
    {
        var (store, claimed) = await EnqueuedAndClaimed();
        var delta = JobTags.Empty.WithLabel("saw-variant");

        // A Failure is still an outcome, so its buffered Tags persist (ADR 0022).
        var result = await store.ReportOutcomeAsync(
            claimed.JobId, "w1", claimed.Attempt, new JobOutcome.Failure(null, "boom"), T0, addedTags: delta);

        Assert.Equal(OutcomeResult.Applied, result);
        var job = await store.GetJobAsync(claimed.JobId);
        Assert.Equal(JobState.DeadLettered, job!.State);
        Assert.Contains(JobTag.Label("saw-variant"), job.Tags);
    }

    [Fact]
    public async Task StaleLeaseOutcome_DoesNotApplyBufferedTags()
    {
        var (store, claimed) = await EnqueuedAndClaimed();
        var delta = JobTags.Empty.WithLabel("ghost");

        // Wrong workerId → fenced out; the buffered Tags die with the outcome.
        var wrongWorker = await store.ReportOutcomeAsync(
            claimed.JobId, "impostor", claimed.Attempt, new JobOutcome.Success(), T0, addedTags: delta);
        Assert.Equal(OutcomeResult.StaleLease, wrongWorker);

        // Wrong attempt → also fenced out.
        var wrongAttempt = await store.ReportOutcomeAsync(
            claimed.JobId, "w1", claimed.Attempt + 1, new JobOutcome.Success(), T0, addedTags: delta);
        Assert.Equal(OutcomeResult.StaleLease, wrongAttempt);

        var job = await store.GetJobAsync(claimed.JobId);
        Assert.Equal(JobState.Leased, job!.State); // unchanged
        Assert.DoesNotContain(JobTag.Label("ghost"), job.Tags);
    }

    [Fact]
    public async Task TagsAcrossAttempts_UnionOnTheJob()
    {
        var (store, first) = await EnqueuedAndClaimed();

        // Attempt 1: retryable failure that reschedules, carrying its Tag.
        var retryAt = T0.AddSeconds(5);
        await store.ReportOutcomeAsync(
            first.JobId, "w1", first.Attempt, new JobOutcome.Failure(retryAt, "transient"), T0,
            addedTags: JobTags.Empty.WithTag("variant", "BRCA1"));

        // Attempt 2: re-claim and succeed, carrying a second Tag.
        var second = Assert.Single(await store.ClaimAsync(Claim("w1", retryAt)));
        await store.ReportOutcomeAsync(
            second.JobId, "w1", second.Attempt, new JobOutcome.Success(), retryAt,
            addedTags: JobTags.Empty.WithTag("variant", "TP53"));

        var job = await store.GetJobAsync(first.JobId);
        Assert.Contains(JobTag.Keyed("variant", "BRCA1"), job!.Tags);
        Assert.Contains(JobTag.Keyed("variant", "TP53"), job.Tags);
    }

    [Fact]
    public async Task ReAddingSameTag_IsIdempotent()
    {
        var (store, first) = await EnqueuedAndClaimed();

        // Attempt 1: fail-retry with a Tag.
        var retryAt = T0.AddSeconds(5);
        await store.ReportOutcomeAsync(
            first.JobId, "w1", first.Attempt, new JobOutcome.Failure(retryAt, "transient"), T0,
            addedTags: JobTags.Empty.WithLabel("urgent"));

        // Attempt 2: succeed re-adding the SAME Tag — set semantics collapse it.
        var second = Assert.Single(await store.ClaimAsync(Claim("w1", retryAt)));
        await store.ReportOutcomeAsync(
            second.JobId, "w1", second.Attempt, new JobOutcome.Success(), retryAt,
            addedTags: JobTags.Empty.WithLabel("urgent"));

        var job = await store.GetJobAsync(first.JobId);
        Assert.Single(job!.Tags);
        Assert.Contains(JobTag.Label("urgent"), job.Tags);
    }

    [Fact]
    public async Task RuntimeDelta_UnionsOntoEnqueueTags()
    {
        var store = new InMemoryJobStore();
        var job = Job() with { Tags = JobTags.Empty.WithLabel("from-enqueue") };
        await store.EnqueueAsync(job, T0);
        var claimed = Assert.Single(await store.ClaimAsync(Claim("w1", T0)));

        await store.ReportOutcomeAsync(
            claimed.JobId, "w1", claimed.Attempt, new JobOutcome.Success(), T0,
            addedTags: JobTags.Empty.WithLabel("from-handler"));

        var read = await store.GetJobAsync(job.JobId);
        Assert.Contains(JobTag.Label("from-enqueue"), read!.Tags);
        Assert.Contains(JobTag.Label("from-handler"), read.Tags);
    }
}
