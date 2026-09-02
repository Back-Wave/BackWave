using BackWave.Storage;
using Xunit.Abstractions;

namespace BackWave.Tests.Simulation;

/// <summary>
/// The CI slice of the real-store mode: the Simulator drives the shipped <c>SqliteJobStore</c>
/// instead of <c>InMemoryJobStore</c>. These cases prove the seam still works on every commit, and
/// they are sized to hold that proof inside a PR budget.
/// <para>
/// A real-store run costs about 49 times a reference-store run, so the searching happens on the
/// schedule, in <see cref="RealSqliteStoreSoakTests"/>. Both classes use <c>SqliteFile</c>, which
/// the spike measured 40 percent cheaper than shared-cache in-memory, because a private file pays no
/// shared-cache locking.
/// </para>
/// </summary>
public class RealSqliteStoreTests(ITestOutputHelper output)
{
    /// <summary>The job count the CI slice runs. Cost tracks store calls, so this is the budget knob.</summary>
    internal const int CiJobCount = 30;

    internal static SimulationOptions Options(ulong seed, SimStoreKind kind, int jobCount = 200) => new()
    {
        Seed = seed,
        JobCount = jobCount,
        StoreKind = kind,
    };

    /// <summary>
    /// The mode's whole claim: one seed replays byte-identically against the real adapter. Two seeds
    /// here, ten on the schedule.
    /// </summary>
    [Theory]
    [InlineData(1UL)]
    [InlineData(1337UL)]
    public void TheRealSqliteStore_ReplaysASeedByteIdentically(ulong seed)
    {
        var first = new Simulator(Options(seed, SimStoreKind.SqliteFile, CiJobCount)).Run();
        var second = new Simulator(Options(seed, SimStoreKind.SqliteFile, CiJobCount)).Run();

        var print = SimulationFingerprintTests.Fingerprint(first);
        output.WriteLine($"seed={seed} fingerprint={print}");
        Assert.Equal(print, SimulationFingerprintTests.Fingerprint(second));
    }

    /// <summary>
    /// The isolation axis, and the reason two parallel VOPR workers cannot corrupt each other. Twenty
    /// scopes built at once, each written to, each read back: no scope sees another's rows. Costs no
    /// plan, so it stays in the CI slice.
    /// </summary>
    [Fact]
    public async Task EveryScope_GetsItsOwnDatabase()
    {
        var scopes = Enumerable.Range(0, 20).Select(_ => SimStoreScope.Create(SimStoreKind.SqliteFile)).ToList();
        try
        {
            var ids = new List<Guid>();
            for (var i = 0; i < scopes.Count; i++)
            {
                var id = Guid.NewGuid();
                ids.Add(id);
                var job = new NewJob(id, "spike", ReadOnlyMemory<byte>.Empty, "default", DateTimeOffset.UnixEpoch);
                Assert.Equal(EnqueueResult.Ok, await scopes[i].Store.EnqueueAsync(job, DateTimeOffset.UnixEpoch));
            }

            for (var i = 0; i < scopes.Count; i++)
            {
                var all = await scopes[i].Store.ListJobsAsync(new JobQuery());
                Assert.Single(all);
                Assert.Equal(ids[i], all[0].JobId);
            }
        }
        finally
        {
            foreach (var scope in scopes)
            {
                scope.Dispose();
            }
        }
    }

    /// <summary>
    /// The seam the fence-dropping sabotage needs. <c>FenceDroppingStore</c> takes any
    /// <c>IJobStore</c> now, so the shipped Outcome-Provenance self-test still trips against the real
    /// adapter. A sabotage that stops tripping is how this seam fails silently, so one seed runs on
    /// every commit.
    /// </summary>
    [Fact]
    public void TheFenceDroppingSabotage_TripsAgainstTheRealStore()
    {
        var trip = Assert.Throws<SimulationInvariantException>(() =>
            new Simulator(new SimulationOptions
            {
                Seed = 521UL,
                IsolationCount = 40,
                CrashProbabilityPerPoll = 0,
                HeartbeatLossProbability = 0,
                MaxExecutionDuration = TimeSpan.FromSeconds(150),
                SabotageOutcomeFence = true,
                StoreKind = SimStoreKind.SqliteFile,
            }).Run());

        Assert.Equal(InvariantId.OutcomeProvenance, trip.InvariantId);
        output.WriteLine($"{trip.InvariantId}: {trip.Message}");
    }
}
