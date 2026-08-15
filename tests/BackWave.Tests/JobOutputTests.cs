using System.Text.Json.Serialization;
using BackWave.Jobs;
using BackWave.Storage;
using BackWave.Storage.InMemory;

namespace BackWave.Tests;

public sealed record Receipt(string ChargeId, int AmountCents);

[JsonSerializable(typeof(Receipt))]
internal sealed partial class OutputJsonContext : JsonSerializerContext;

/// <summary>
/// Job Output spine (ADR 0026, issue 0131): a handler emits one opaque blob via
/// <see cref="JobContext.SetOutput"/>; it rides the <c>(workerId, attempt)</c> outcome fence as a
/// third rider (with Failure Detail and the Tag delta), persists to the job row <b>only on a
/// Succeeded outcome</b>, and reads back through the dedicated <c>GetJobOutputAsync</c>. The
/// success-side twin of Failure Detail — but functional data, so it lives on the row (History-Off
/// must not erase it) and over-limit output is REJECTED, never truncated. In-Memory Store only;
/// the adapters land in 0133, so this lives outside the shared Conformance Suite.
/// </summary>
public class JobOutputTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(60);

    private static NewJob Job() => new(
        Guid.NewGuid(), "charge", ReadOnlyMemory<byte>.Empty, "billing", T0);

    private static ClaimRequest Claim(string worker, DateTimeOffset now)
        => new(worker, ["billing"], 32, Lease, now);

    private static async Task<(InMemoryJobStore Store, JobRecord Claimed)> EnqueuedAndClaimed(
        InMemoryJobStore? store = null, string worker = "w1")
    {
        store ??= new InMemoryJobStore();
        var job = Job();
        Assert.Equal(EnqueueResult.Ok, await store.EnqueueAsync(job, T0));
        var claimed = await store.ClaimAsync(Claim(worker, T0));
        return (store, Assert.Single(claimed));
    }

    [Fact]
    public async Task SetOutputThenSucceed_RoundTripsThroughGetJobOutput()
    {
        var (store, claimed) = await EnqueuedAndClaimed();
        var blob = JobOutputCodec.Encode(new Receipt("ch_123", 4200), OutputJsonContext.Default.Receipt);

        var result = await store.ReportOutcomeAsync(
            claimed.JobId, "w1", claimed.Attempt, new JobOutcome.Success(), T0, output: blob);

        Assert.Equal(OutcomeResult.Applied, result);
        var stored = await store.GetJobOutputAsync(claimed.JobId);
        Assert.NotNull(stored);
        var decoded = JobOutputCodec.Decode(stored!.Value, OutputJsonContext.Default.Receipt);
        Assert.Equal(new Receipt("ch_123", 4200), decoded);
    }

    [Fact]
    public void TypedValue_RoundTripsThroughTheCodec()
    {
        // Producer shape == reader shape: the same JsonTypeInfo encodes and decodes.
        var blob = JobOutputCodec.Encode(new Receipt("ch_9", 100), OutputJsonContext.Default.Receipt);
        var decoded = JobOutputCodec.Decode(blob, OutputJsonContext.Default.Receipt);
        Assert.Equal(new Receipt("ch_9", 100), decoded);
    }

    [Fact]
    public async Task SetOutputThenFail_PersistsNoOutput()
    {
        var (store, claimed) = await EnqueuedAndClaimed();
        var blob = JobOutputCodec.Encode(new Receipt("ch_x", 1), OutputJsonContext.Default.Receipt);

        // Output is success-only: a graceful Failure still records its outcome but writes no output.
        var result = await store.ReportOutcomeAsync(
            claimed.JobId, "w1", claimed.Attempt, new JobOutcome.Failure(null, "boom"), T0, output: blob);

        Assert.Equal(OutcomeResult.Applied, result);
        var job = await store.GetJobAsync(claimed.JobId);
        Assert.Equal(JobState.DeadLettered, job!.State);
        Assert.Null(await store.GetJobOutputAsync(claimed.JobId));
    }

    [Fact]
    public async Task StaleLeaseOutcome_DoesNotApplyBufferedOutput()
    {
        var (store, claimed) = await EnqueuedAndClaimed();
        var blob = JobOutputCodec.Encode(new Receipt("ghost", 0), OutputJsonContext.Default.Receipt);

        // Wrong workerId → fenced out; the buffered output dies with the outcome (no split-brain output).
        var wrongWorker = await store.ReportOutcomeAsync(
            claimed.JobId, "impostor", claimed.Attempt, new JobOutcome.Success(), T0, output: blob);
        Assert.Equal(OutcomeResult.StaleLease, wrongWorker);

        // Wrong attempt → also fenced out.
        var wrongAttempt = await store.ReportOutcomeAsync(
            claimed.JobId, "w1", claimed.Attempt + 1, new JobOutcome.Success(), T0, output: blob);
        Assert.Equal(OutcomeResult.StaleLease, wrongAttempt);

        var job = await store.GetJobAsync(claimed.JobId);
        Assert.Equal(JobState.Leased, job!.State); // unchanged
        Assert.Null(await store.GetJobOutputAsync(claimed.JobId));
    }

    [Fact]
    public async Task OutputPersists_EvenWhenJobHistoryPolicyOff()
    {
        // Output is functional data on the job row, not a Transition Log entry, so History = Off
        // (no transitions recorded at all) must not erase it.
        var store = new InMemoryJobStore(historyPolicy: JobHistoryPolicy.Off);
        var (_, claimed) = await EnqueuedAndClaimed(store);
        var blob = JobOutputCodec.Encode(new Receipt("ch_off", 7), OutputJsonContext.Default.Receipt);

        await store.ReportOutcomeAsync(
            claimed.JobId, "w1", claimed.Attempt, new JobOutcome.Success(), T0, output: blob);

        Assert.Empty(await store.GetJobHistoryAsync(claimed.JobId)); // history truly off
        var stored = await store.GetJobOutputAsync(claimed.JobId);
        Assert.NotNull(stored);
        Assert.Equal(new Receipt("ch_off", 7), JobOutputCodec.Decode(stored!.Value, OutputJsonContext.Default.Receipt));
    }

    [Fact]
    public async Task OverMaxOutputBytes_IsRejectedNeverTruncated()
    {
        var store = new InMemoryJobStore(bounds: new StoreBounds { MaxOutputBytes = 16 });
        var (_, claimed) = await EnqueuedAndClaimed(store);
        var tooBig = new ReadOnlyMemory<byte>(new byte[17]);

        var ex = await Assert.ThrowsAsync<JobOutputTooLargeException>(async () =>
            await store.ReportOutcomeAsync(
                claimed.JobId, "w1", claimed.Attempt, new JobOutcome.Success(), T0, output: tooBig));
        Assert.Equal(17, ex.ActualBytes);
        Assert.Equal(16, ex.MaxOutputBytes);

        // The write is fully rejected — nothing partially applied, Effect-Once holds.
        var job = await store.GetJobAsync(claimed.JobId);
        Assert.Equal(JobState.Leased, job!.State);
        Assert.Null(await store.GetJobOutputAsync(claimed.JobId));
    }

    [Fact]
    public async Task OutputAtExactBound_IsAccepted()
    {
        var store = new InMemoryJobStore(bounds: new StoreBounds { MaxOutputBytes = 16 });
        var (_, claimed) = await EnqueuedAndClaimed(store);
        var atBound = new ReadOnlyMemory<byte>(new byte[16]);

        var result = await store.ReportOutcomeAsync(
            claimed.JobId, "w1", claimed.Attempt, new JobOutcome.Success(), T0, output: atBound);

        Assert.Equal(OutcomeResult.Applied, result);
        Assert.Equal(16, (await store.GetJobOutputAsync(claimed.JobId))!.Value.Length);
    }

    [Fact]
    public async Task GetJobOutput_NullForJobThatNeverSetOutput()
    {
        var (store, claimed) = await EnqueuedAndClaimed();
        await store.ReportOutcomeAsync(claimed.JobId, "w1", claimed.Attempt, new JobOutcome.Success(), T0);

        Assert.Null(await store.GetJobOutputAsync(claimed.JobId));
    }

    [Fact]
    public async Task GetJobOutput_NullForUnknownJob()
        => Assert.Null(await new InMemoryJobStore().GetJobOutputAsync(Guid.NewGuid()));

    [Fact]
    public async Task OutputDeletedWithJob_UnderRetention()
    {
        var store = new InMemoryJobStore();
        var (_, claimed) = await EnqueuedAndClaimed(store);
        var blob = JobOutputCodec.Encode(new Receipt("ch_r", 5), OutputJsonContext.Default.Receipt);
        await store.ReportOutcomeAsync(
            claimed.JobId, "w1", claimed.Attempt, new JobOutcome.Success(), T0, output: blob);
        Assert.NotNull(await store.GetJobOutputAsync(claimed.JobId));

        // Output is a column on the job row, so it is purged with the job for free.
        var purged = await store.PurgeTerminalAsync(
            TerminalStateClass.SucceededOrCancelled, T0.AddDays(1), 100);
        Assert.Equal(1, purged);
        Assert.Null(await store.GetJobOutputAsync(claimed.JobId));
    }

    [Fact]
    public async Task SetOutput_LastWriteWinsWithinAttempt()
    {
        var context = new JobContext { JobId = Guid.NewGuid(), Attempt = 1 };
        context.SetOutput(new Receipt("first", 1), OutputJsonContext.Default.Receipt);
        context.SetOutput(new Receipt("second", 2), OutputJsonContext.Default.Receipt);

        Assert.NotNull(context.BufferedOutput);
        Assert.Equal(
            new Receipt("second", 2),
            JobOutputCodec.Decode(context.BufferedOutput!.Value, OutputJsonContext.Default.Receipt));
    }
}
