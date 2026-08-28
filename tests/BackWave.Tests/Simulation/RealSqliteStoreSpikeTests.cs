using System.Diagnostics;
using BackWave.Storage;
using Xunit.Abstractions;

namespace BackWave.Tests.Simulation;

/// <summary>
/// dst-0001 spike: drives the REAL SQLite adapter with the deterministic Simulator and measures what
/// that costs. Every test here is evidence for the spike report, not a shipped guarantee.
/// </summary>
public class RealSqliteStoreSpikeTests(ITestOutputHelper output)
{
    private static SimulationOptions Options(ulong seed, SimStoreKind kind, int jobCount = 200) => new()
    {
        Seed = seed,
        JobCount = jobCount,
        StoreKind = kind,
    };

    [Fact]
    public void TheRealSqliteStore_Runs_UnderTheDeterministicLoop()
    {
        var result = new Simulator(Options(1337UL, SimStoreKind.SqliteMemory)).Run();

        Assert.Equal(200, result.FinalJobs.Count);
        Assert.Equal(200, result.Succeeded + result.DeadLettered + result.Cancelled);
        output.WriteLine($"steps={result.Steps} crashes={result.Crashes} stale={result.StaleOutcomes}");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheRealSqliteStore_ReplaysTheSameSeedByteIdentically(bool fileBacked)
    {
        var kind = fileBacked ? SimStoreKind.SqliteFile : SimStoreKind.SqliteMemory;
        var first = new Simulator(Options(1337UL, kind)).Run();
        var second = new Simulator(Options(1337UL, kind)).Run();

        Assert.Equal(
            SpikeFingerprintTests.Fingerprint(first),
            SpikeFingerprintTests.Fingerprint(second));
    }

    [Theory]
    [InlineData(1UL)]
    [InlineData(2UL)]
    [InlineData(3UL)]
    [InlineData(1337UL)]
    [InlineData(0xDEADBEEFUL)]
    [InlineData(42_424_242UL)]
    [InlineData(987_654_321UL)]
    [InlineData(0x5EED_5EED_5EEDUL)]
    [InlineData(2026_06_10UL)]
    [InlineData(ulong.MaxValue)]
    public void TheRealSqliteStore_ReplaysEveryBatterySeedByteIdentically(ulong seed)
    {
        var first = new Simulator(Options(seed, SimStoreKind.SqliteMemory)).Run();
        var second = new Simulator(Options(seed, SimStoreKind.SqliteMemory)).Run();

        var firstPrint = SpikeFingerprintTests.Fingerprint(first);
        output.WriteLine($"seed={seed} fingerprint={firstPrint}");
        Assert.Equal(firstPrint, SpikeFingerprintTests.Fingerprint(second));
    }

    /// <summary>
    /// The cost axis. Reports the wall-clock cost of one plan on each store kind, plus the cost of
    /// building and releasing an empty store (schema migration, connection setup, teardown).
    /// </summary>
    [Fact]
    public void TheCostOfARealStore_IsMeasured()
    {
        foreach (var kind in new[] { SimStoreKind.InMemory, SimStoreKind.SqliteMemory, SimStoreKind.SqliteFile })
        {
            // Warm the provider and the JIT before timing.
            using (var warm = SimStoreScope.Create(kind))
            {
                _ = warm.Store;
            }
            var setup = Stopwatch.StartNew();
            for (var i = 0; i < 20; i++)
            {
                using var scope = SimStoreScope.Create(kind);
                _ = scope.Store;
            }
            setup.Stop();

            _ = new Simulator(Options(1UL, kind, jobCount: 50)).Run();
            var plan = Stopwatch.StartNew();
            foreach (var seed in new ulong[] { 1UL, 2UL, 3UL, 1337UL, 99UL })
            {
                _ = new Simulator(Options(seed, kind)).Run();
            }
            plan.Stop();

            output.WriteLine(
                $"{kind}: scope create+dispose {setup.Elapsed.TotalMilliseconds / 20:F2} ms, "
                + $"whole plan (200 jobs) {plan.Elapsed.TotalMilliseconds / 5:F1} ms");
        }
    }

    /// <summary>
    /// The isolation axis. Twenty scopes built at once, each written to, each read back: no scope
    /// sees another's rows. This is the shape two parallel VOPR workers take.
    /// </summary>
    [Fact]
    public async Task EveryScope_GetsItsOwnDatabase()
    {
        var scopes = Enumerable.Range(0, 20).Select(_ => SimStoreScope.Create(SimStoreKind.SqliteMemory)).ToList();
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
    /// Two Simulators running at the same time on two threads, the shape of two parallel VOPR
    /// workers. Each must produce the fingerprint its seed produces alone.
    /// </summary>
    [Fact]
    public void TwoParallelWorkers_NeverShareAStore()
    {
        var alone = new Dictionary<ulong, string>();
        foreach (var seed in new ulong[] { 1UL, 2UL, 3UL, 1337UL })
        {
            alone[seed] = SpikeFingerprintTests.Fingerprint(
                new Simulator(Options(seed, SimStoreKind.SqliteMemory, jobCount: 60)).Run());
        }

        var together = new Dictionary<ulong, string>();
        Parallel.ForEach(alone.Keys, seed =>
        {
            var print = SpikeFingerprintTests.Fingerprint(
                new Simulator(Options(seed, SimStoreKind.SqliteMemory, jobCount: 60)).Run());
            lock (together)
            {
                together[seed] = print;
            }
        });

        Assert.Equal(alone, together);
    }

    /// <summary>
    /// The store-fault axis. A wrapped real store still records its fault decisions in the Fault Map,
    /// and replaying that map reproduces the run exactly.
    /// </summary>
    [Fact]
    public void AWrappedRealStore_StillRecordsAReplayableFaultMap()
    {
        var options = Options(1337UL, SimStoreKind.SqliteMemory, jobCount: 80) with
        {
            StoreFaultProbability = 0.05,
        };

        var generatePlan = FaultPlan.Generate(options.Seed);
        var generated = new Simulator(options, generatePlan).Run();
        var map = generatePlan.ToFaultMap();

        Assert.NotEmpty(map);
        Assert.Contains(map, e => e.Axis == "store");
        Assert.Contains(map, e => e is { Axis: "store", Fault: true });
        var injected = map.Count(e => e is { Axis: "store", Fault: true });
        output.WriteLine($"fault map entries={map.Count}, store faults injected={injected}");

        var replayed = new Simulator(options, FaultPlan.Replay(options.Seed, map)).Run();

        Assert.Equal(
            SpikeFingerprintTests.Fingerprint(generated),
            SpikeFingerprintTests.Fingerprint(replayed));
    }

    /// <summary>
    /// The seam the fence-dropping sabotage needs. It now takes any <c>IJobStore</c>, so the
    /// Outcome-Provenance oracle self-test still trips against the real adapter.
    /// </summary>
    [Theory]
    [InlineData(521UL)]
    [InlineData(522UL)]
    public void TheFenceDroppingSabotage_TripsAgainstTheRealStore(ulong seed)
    {
        var trip = Assert.Throws<SimulationInvariantException>(() =>
            new Simulator(new SimulationOptions
            {
                Seed = seed,
                IsolationCount = 40,
                CrashProbabilityPerPoll = 0,
                HeartbeatLossProbability = 0,
                MaxExecutionDuration = TimeSpan.FromSeconds(150),
                SabotageOutcomeFence = true,
                StoreKind = SimStoreKind.SqliteMemory,
            }).Run());

        Assert.Equal(InvariantId.OutcomeProvenance, trip.InvariantId);
        output.WriteLine($"seed={seed} {trip.InvariantId}: {trip.Message}");
    }

    /// <summary>
    /// The dst-0002 preview. The same seed on the reference store and on the real adapter: does the
    /// whole run agree? This is not the lockstep oracle. It only names whether an end-state divergence
    /// exists at all, and for which seed.
    /// </summary>
    [Theory]
    [InlineData(1UL)]
    [InlineData(2UL)]
    [InlineData(3UL)]
    [InlineData(1337UL)]
    public void TheTwoStores_AgreeOnTheWholeRun(ulong seed)
    {
        var reference = SpikeFingerprintTests.Fingerprint(
            new Simulator(Options(seed, SimStoreKind.InMemory, jobCount: 60)).Run());
        var real = SpikeFingerprintTests.Fingerprint(
            new Simulator(Options(seed, SimStoreKind.SqliteMemory, jobCount: 60)).Run());

        output.WriteLine($"seed={seed} inmemory={reference} sqlite={real} agree={reference == real}");
        Assert.Equal(reference, real);
    }

    /// <summary>
    /// The schema-migration cost, measured on its own. <c>SqliteJobStore</c> migrates lazily on its
    /// first call, so the scope-create number in <see cref="TheCostOfARealStore_IsMeasured"/> excludes
    /// it. This test pays it, by creating a store and making one call.
    /// </summary>
    [Fact]
    public async Task TheSchemaMigrationCost_IsMeasured()
    {
        foreach (var kind in new[] { SimStoreKind.SqliteMemory, SimStoreKind.SqliteFile })
        {
            using (var warm = SimStoreScope.Create(kind))
            {
                await warm.Store.ListJobsAsync(new JobQuery());
            }
            var clock = Stopwatch.StartNew();
            for (var i = 0; i < 20; i++)
            {
                using var scope = SimStoreScope.Create(kind);
                await scope.Store.ListJobsAsync(new JobQuery());
            }
            clock.Stop();
            output.WriteLine($"{kind}: create + migrate + first call + dispose {clock.Elapsed.TotalMilliseconds / 20:F2} ms");
        }
    }
}
