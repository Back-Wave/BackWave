using BackWave.Core;
using BackWave.Jobs;
using BackWave.Monitor;
using BackWave.Storage;
using BackWave.Storage.InMemory;

namespace BackWave.Tests;

/// <summary>
/// AND-ed tag filtering on <see cref="JobQuery"/> (issue 0109, ADR 0022 "Query surface"): the
/// In-Memory Store filters the job list by a set of <see cref="JobTagPredicate"/>s AND-ed together
/// and AND-composed with the scalar <c>State</c>/<c>Queue</c>/… filters. Three predicate kinds —
/// has-label, has key=value, has-key-any-value. OR is out of scope: a caller wanting OR runs two
/// queries (asserted here as the absence of a single query returning the union).
/// </summary>
public class JobTagFilterTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed record Fixture(InMemoryJobStore Store, BackWaveClient Client, BackWaveMonitor Monitor);

    private static Fixture CreateFixture()
    {
        var store = new InMemoryJobStore();
        var registry = new JobRegistry(
        [
            JobRegistration.Create<SendNewsletter, SendNewsletterHandler>(
                "send-newsletter", ClientJsonContext.Default.SendNewsletter),
        ]);
        return new Fixture(
            store,
            new BackWaveClient(store, registry, new FixedClock(T0)),
            new BackWaveMonitor(store, registry));
    }

    private static async Task<Guid> Enqueue(BackWaveClient client, string edition, JobTags tags)
        => await client.EnqueueAsync(new SendNewsletter(edition), T0, tags: tags);

    private static async Task<IReadOnlyList<Guid>> MatchingIds(BackWaveMonitor monitor, JobQuery query)
        => [.. (await monitor.ListJobsAsync(query)).Select(s => s.JobId)];

    [Fact]
    public async Task SingleHasLabelPredicate_ReturnsOnlyMatchingJobs()
    {
        var (_, client, monitor) = CreateFixture();
        var urgent = await Enqueue(client, "a", JobTags.Empty.WithLabel("urgent"));
        await Enqueue(client, "b", JobTags.Empty.WithLabel("routine"));
        await Enqueue(client, "c", JobTags.Empty);

        var matched = await MatchingIds(monitor, new JobQuery
        {
            TagPredicates = [JobTagPredicate.HasLabel("urgent")],
        });

        Assert.Equal([urgent], matched);
    }

    [Fact]
    public async Task HasKeyValuePredicate_FiltersByExactKeyAndValue()
    {
        var (_, client, monitor) = CreateFixture();
        var acme = await Enqueue(client, "a", JobTags.Empty.WithTag("tenant", "acme"));
        await Enqueue(client, "b", JobTags.Empty.WithTag("tenant", "globex"));
        // Same value under a different key must NOT match.
        await Enqueue(client, "c", JobTags.Empty.WithTag("owner", "acme"));

        var matched = await MatchingIds(monitor, new JobQuery
        {
            TagPredicates = [JobTagPredicate.HasKeyValue("tenant", "acme")],
        });

        Assert.Equal([acme], matched);
    }

    [Fact]
    public async Task HasKeyAnyValuePredicate_MatchesAnyValueUnderTheKey()
    {
        var (_, client, monitor) = CreateFixture();
        var brca = await Enqueue(client, "a", JobTags.Empty.WithTag("variant", "BRCA1"));
        var tp53 = await Enqueue(client, "b", JobTags.Empty.WithTag("variant", "TP53"));
        await Enqueue(client, "c", JobTags.Empty.WithTag("tenant", "acme"));

        var matched = await MatchingIds(monitor, new JobQuery
        {
            TagPredicates = [JobTagPredicate.HasKey("variant")],
        });

        Assert.Equal([brca, tp53], matched);
    }

    [Fact]
    public async Task MultiplePredicates_AndTogether()
    {
        var (_, client, monitor) = CreateFixture();
        var both = await Enqueue(client, "a",
            JobTags.Empty.WithLabel("urgent").WithTag("tenant", "acme"));
        // Each of these satisfies only one of the two predicates.
        await Enqueue(client, "b", JobTags.Empty.WithLabel("urgent"));
        await Enqueue(client, "c", JobTags.Empty.WithTag("tenant", "acme"));

        var matched = await MatchingIds(monitor, new JobQuery
        {
            TagPredicates =
            [
                JobTagPredicate.HasLabel("urgent"),
                JobTagPredicate.HasKeyValue("tenant", "acme"),
            ],
        });

        Assert.Equal([both], matched);
    }

    [Fact]
    public async Task TagPredicates_ComposeWithAStateFilter()
    {
        var (store, client, monitor) = CreateFixture();
        // Enqueue, then Claim, the "leased" urgent job FIRST so it is the only job out of Scheduled
        // when the others arrive — the State filter then discriminates the two urgent jobs.
        var leased = await Enqueue(client, "b", JobTags.Empty.WithLabel("urgent"));
        var claimed = await store.ClaimAsync(
            new ClaimRequest("worker-1", ["default"], MaxJobs: 1, TimeSpan.FromMinutes(5), T0));
        Assert.Equal(leased, Assert.Single(claimed).JobId);

        var scheduled = await Enqueue(client, "a", JobTags.Empty.WithLabel("urgent"));
        await Enqueue(client, "c", JobTags.Empty.WithLabel("routine"));

        var matched = await MatchingIds(monitor, new JobQuery
        {
            State = JobState.Scheduled,
            TagPredicates = [JobTagPredicate.HasLabel("urgent")],
        });

        Assert.Equal([scheduled], matched);
    }

    [Fact]
    public async Task EmptyPredicateList_MatchesEveryJob()
    {
        var (_, client, monitor) = CreateFixture();
        await Enqueue(client, "a", JobTags.Empty.WithLabel("urgent"));
        await Enqueue(client, "b", JobTags.Empty);

        var matched = await MatchingIds(monitor, new JobQuery());

        Assert.Equal(2, matched.Count);
    }

    [Fact]
    public async Task OrIsNotSupported_TwoPredicatesNarrowRatherThanUnion()
    {
        // Documents the AND-only contract (ADR 0022): combining two predicates can only narrow the
        // result — there is no single-query OR that returns the union. A caller wanting the union of
        // "urgent" OR "tenant=acme" must run two queries.
        var (_, client, monitor) = CreateFixture();
        var urgentOnly = await Enqueue(client, "a", JobTags.Empty.WithLabel("urgent"));
        var acmeOnly = await Enqueue(client, "b", JobTags.Empty.WithTag("tenant", "acme"));

        var anded = await MatchingIds(monitor, new JobQuery
        {
            TagPredicates =
            [
                JobTagPredicate.HasLabel("urgent"),
                JobTagPredicate.HasKeyValue("tenant", "acme"),
            ],
        });

        // No job carries both, so the AND is empty — proof the two predicates do not OR.
        Assert.Empty(anded);
        Assert.DoesNotContain(urgentOnly, anded);
        Assert.DoesNotContain(acmeOnly, anded);
    }
}
