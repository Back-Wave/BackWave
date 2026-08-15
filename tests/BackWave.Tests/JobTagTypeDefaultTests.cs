using System.Text.Json.Serialization;
using BackWave.Core;
using BackWave.Jobs;
using BackWave.Monitor;
using BackWave.Storage;
using BackWave.Storage.InMemory;

namespace BackWave.Tests;

/// <summary>
/// [Job] type-default Tags (issue 0108, ADR 0022 "Authorship"): a job type declares default
/// <b>Labels</b> that union <i>additively</i> into the per-enqueue Tags — always present, never
/// subtractable at enqueue. Only Labels are expressible as a type default (the attribute takes
/// compile-time constants, and a Keyed Tag would need a parsed separator, which the structural
/// Label-vs-Keyed-Tag distinction forbids — keyed-tag defaults are deliberately deferred).
/// </summary>
public class JobTagTypeDefaultTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    public sealed record Reindex(string Tenant);

    public sealed class ReindexHandler : IJobHandler<Reindex>
    {
        public Task HandleAsync(Reindex job, JobContext context, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed record Fixture(BackWaveClient Client, BackWaveMonitor Monitor);

    private static Fixture CreateFixture()
    {
        var store = new InMemoryJobStore();
        // Mirror the source-generated registration: the [Job] attribute's Labels flow into
        // DefaultTags. Here we pass them explicitly so the test does not depend on a generated
        // assembly, exercising the same JobRegistration.DefaultTags path the generator emits.
        var registry = new JobRegistry(
        [
            JobRegistration.Create<Reindex, ReindexHandler>(
                "reindex", TypeDefaultJsonContext.Default.Reindex, labels: ["urgent", "maintenance"]),
        ]);
        return new Fixture(
            new BackWaveClient(store, registry, new FixedClock(T0)),
            new BackWaveMonitor(store, registry));
    }

    private static async Task<IReadOnlyList<JobTag>> TagsOf(BackWaveMonitor monitor, Guid jobId)
        => (await monitor.GetJobAsync(jobId))!.Tags;

    [Fact]
    public async Task TypeDefaultLabel_IsAppliedOnEnqueue_WithoutTheCallerSpecifyingIt()
    {
        var (client, monitor) = CreateFixture();

        var jobId = await client.EnqueueAsync(new Reindex("acme"), T0);

        var read = await TagsOf(monitor, jobId);
        Assert.Contains(JobTag.Label("urgent"), read);
        Assert.Contains(JobTag.Label("maintenance"), read);
        Assert.Equal(2, read.Count);
    }

    [Fact]
    public async Task EnqueueSuppliedTags_UnionWithTypeDefaults_IdenticalOnesCollapse()
    {
        var (client, monitor) = CreateFixture();
        // "urgent" overlaps a type default; "tenant:acme" is new.
        var supplied = JobTags.Empty.WithLabel("urgent").WithTag("tenant", "acme");

        var jobId = await client.EnqueueAsync(new Reindex("acme"), T0, tags: supplied);

        var read = await TagsOf(monitor, jobId);
        Assert.Contains(JobTag.Label("urgent"), read);
        Assert.Contains(JobTag.Label("maintenance"), read);
        Assert.Contains(JobTag.Keyed("tenant", "acme"), read);
        // urgent (default ∪ supplied collapses to one) + maintenance + tenant:acme = 3.
        Assert.Equal(3, read.Count);
    }

    [Fact]
    public async Task TypeDefault_IsAdditiveOnly_StillPresentWhenCallerPassesADisjointSet()
    {
        var (client, monitor) = CreateFixture();
        // A caller can only ever add Tags — there is no API to drop a type default. Passing a
        // disjoint set leaves every default intact.
        var supplied = JobTags.Empty.WithLabel("nightly");

        var jobId = await client.EnqueueAsync(new Reindex("acme"), T0, tags: supplied);

        var read = await TagsOf(monitor, jobId);
        Assert.Contains(JobTag.Label("urgent"), read);
        Assert.Contains(JobTag.Label("maintenance"), read);
        Assert.Contains(JobTag.Label("nightly"), read);
        Assert.Equal(3, read.Count);
    }

    [Fact]
    public async Task DependencyEnqueue_AlsoUnionsTypeDefaults()
    {
        var (client, monitor) = CreateFixture();
        var parentId = await client.EnqueueAsync(new Reindex("acme"), T0);

        var childId = await client.EnqueueDependencyAsync(
            new Reindex("acme"), parentId, enqueuedAt: T0, tags: JobTags.Empty.WithLabel("followup"));

        var read = await TagsOf(monitor, childId);
        Assert.Contains(JobTag.Label("urgent"), read);
        Assert.Contains(JobTag.Label("maintenance"), read);
        Assert.Contains(JobTag.Label("followup"), read);
        Assert.Equal(3, read.Count);
    }
}

[JsonSerializable(typeof(JobTagTypeDefaultTests.Reindex))]
internal sealed partial class TypeDefaultJsonContext : JsonSerializerContext;
