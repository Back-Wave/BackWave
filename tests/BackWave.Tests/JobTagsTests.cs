using BackWave.Core;
using BackWave.Jobs;
using BackWave.Monitor;
using BackWave.Storage;
using BackWave.Storage.InMemory;

namespace BackWave.Tests;

/// <summary>
/// Job Tags spine (issue 0107, ADR 0022): an observational string-set attached at enqueue that
/// round-trips through the In-Memory Store and surfaces on a Monitor read. A Tag is a Label
/// (<c>Key=""</c>) or a Keyed Tag (<c>Key</c> + value); the set collapses duplicates and storage
/// never parses a separator.
/// </summary>
public class JobTagsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed record Fixture(BackWaveClient Client, BackWaveMonitor Monitor);

    private static Fixture CreateFixture()
    {
        var store = new InMemoryJobStore();
        var registry = new JobRegistry(
        [
            JobRegistration.Create<SendNewsletter, SendNewsletterHandler>(
                "send-newsletter", ClientJsonContext.Default.SendNewsletter),
        ]);
        return new Fixture(
            new BackWaveClient(store, registry, new FixedClock(T0)),
            new BackWaveMonitor(store, registry));
    }

    private static async Task<IReadOnlyList<JobTag>> TagsOf(BackWaveMonitor monitor, Guid jobId)
        => (await monitor.GetJobAsync(jobId))!.Tags;

    [Fact]
    public async Task EnqueueWithLabelAndKeyedTag_AreBothReturnedByAMonitorRead()
    {
        var (client, monitor) = CreateFixture();
        var tags = JobTags.Empty.WithLabel("urgent").WithTag("tenant", "acme");

        var jobId = await client.EnqueueAsync(new SendNewsletter("june"), T0, tags: tags);

        var read = await TagsOf(monitor, jobId);
        Assert.Contains(JobTag.Label("urgent"), read);
        Assert.Contains(JobTag.Keyed("tenant", "acme"), read);
        Assert.Equal(2, read.Count);
    }

    [Fact]
    public async Task MultipleValuesForOneKey_RoundTrip()
    {
        var (client, monitor) = CreateFixture();
        var tags = JobTags.Empty.WithTag("variant", "BRCA1").WithTag("variant", "TP53");

        var jobId = await client.EnqueueAsync(new SendNewsletter("genomics"), T0, tags: tags);

        var read = await TagsOf(monitor, jobId);
        Assert.Contains(JobTag.Keyed("variant", "BRCA1"), read);
        Assert.Contains(JobTag.Keyed("variant", "TP53"), read);
        Assert.Equal(2, read.Count);
    }

    [Fact]
    public async Task ReAddingAnIdenticalTag_CollapsesToOne()
    {
        var (client, monitor) = CreateFixture();
        var tags = JobTags.Empty
            .WithLabel("urgent")
            .WithLabel("urgent")
            .WithTag("tenant", "acme")
            .WithTag("tenant", "acme");

        var jobId = await client.EnqueueAsync(new SendNewsletter("june"), T0, tags: tags);

        var read = await TagsOf(monitor, jobId);
        Assert.Equal(2, read.Count);
    }

    [Fact]
    public void AnEmptyTagValue_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => JobTag.Label(""));
        Assert.Throws<ArgumentException>(() => JobTag.Keyed("tenant", ""));
        // An empty key is not a Label either — Labels are minted via JobTag.Label.
        Assert.Throws<ArgumentException>(() => JobTag.Keyed("", "acme"));
    }

    [Fact]
    public async Task AColonInsideALabel_IsPreservedVerbatim_StorageNeverParses()
    {
        var (client, monitor) = CreateFixture();
        var tags = JobTags.Empty.WithLabel("ratio 3:1");

        var jobId = await client.EnqueueAsync(new SendNewsletter("june"), T0, tags: tags);

        var read = await TagsOf(monitor, jobId);
        var only = Assert.Single(read);
        Assert.True(only.IsLabel);
        Assert.Equal("", only.Key);
        Assert.Equal("ratio 3:1", only.Value);
    }

    [Fact]
    public void JobTags_SetEquality_IsOrderIndependent()
    {
        var a = JobTags.Empty.WithLabel("urgent").WithTag("tenant", "acme");
        var b = JobTags.Empty.WithTag("tenant", "acme").WithLabel("urgent");

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }
}
