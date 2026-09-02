using System.Diagnostics;
using BackWave.Storage;
using Xunit.Abstractions;

namespace BackWave.Tests.Simulation;

/// <summary>
/// The searching half of the real-store mode, and its cost measurements. Excluded from PR CI
/// (Category=Soak), run on a schedule. <see cref="RealSqliteStoreTests"/> holds the fast proof that
/// the seam still works.
/// <para>
/// These cases run the full 200-job plan against the real adapter, which costs about 49 times a
/// reference-store run. That is why they are here and not in the slice.
/// </para>
/// </summary>
[Trait("Category", "Soak")]
public class RealSqliteStoreSoakTests(ITestOutputHelper output)
{
    private static SimulationOptions Options(ulong seed, SimStoreKind kind, int jobCount = 200) =>
        RealSqliteStoreTests.Options(seed, kind, jobCount);

    /// <summary>The whole pinned battery, replayed twice each against the real adapter.</summary>
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
        var first = new Simulator(Options(seed, SimStoreKind.SqliteFile)).Run();
        var second = new Simulator(Options(seed, SimStoreKind.SqliteFile)).Run();

        var firstPrint = SimulationFingerprintTests.Fingerprint(first);
        output.WriteLine($"seed={seed} steps={first.Steps} fingerprint={firstPrint}");
        Assert.Equal(firstPrint, SimulationFingerprintTests.Fingerprint(second));
    }

    /// <summary>
    /// The cost axis, re-measured on every run so a regression in store cost is visible. Reports the
    /// wall-clock cost of one plan on each store kind, plus the cost of building and releasing an
    /// empty store.
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
            alone[seed] = SimulationFingerprintTests.Fingerprint(
                new Simulator(Options(seed, SimStoreKind.SqliteFile, jobCount: 60)).Run());
        }

        var together = new Dictionary<ulong, string>();
        Parallel.ForEach(alone.Keys, seed =>
        {
            var print = SimulationFingerprintTests.Fingerprint(
                new Simulator(Options(seed, SimStoreKind.SqliteFile, jobCount: 60)).Run());
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
        var options = Options(1337UL, SimStoreKind.SqliteFile, jobCount: 80) with
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
            SimulationFingerprintTests.Fingerprint(generated),
            SimulationFingerprintTests.Fingerprint(replayed));
    }

    /// <summary>
    /// The differential preview that the store-oracle ticket builds on. The same seed on the
    /// reference store and on the real adapter: does the whole run agree? This is not the lockstep
    /// oracle. It only names whether an end-state divergence exists at all, and for which seed.
    /// </summary>
    [Theory]
    [InlineData(1UL)]
    [InlineData(2UL)]
    [InlineData(3UL)]
    [InlineData(1337UL)]
    public void TheTwoStores_AgreeOnTheWholeRun(ulong seed)
    {
        var reference = SimulationFingerprintTests.Fingerprint(
            new Simulator(Options(seed, SimStoreKind.InMemory, jobCount: 60)).Run());
        var real = SimulationFingerprintTests.Fingerprint(
            new Simulator(Options(seed, SimStoreKind.SqliteFile, jobCount: 60)).Run());

        output.WriteLine($"seed={seed} inmemory={reference} sqlite={real} agree={reference == real}");
        Assert.Equal(reference, real);
    }

    /// <summary>The second sabotage seed. The slice runs the first on every commit.</summary>
    [Fact]
    public void TheFenceDroppingSabotage_TripsAgainstTheRealStore()
    {
        var trip = Assert.Throws<SimulationInvariantException>(() =>
            new Simulator(new SimulationOptions
            {
                Seed = 522UL,
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
