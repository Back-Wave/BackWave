using BackWave.Torture;

namespace BackWave.Upgrade.Tests;

/// <summary>
/// The in-place upgrade contract (ADR 0038, issue 0202) running in the PR battery: every shipped prior
/// schema version must migrate in place — a populated, in-flight database carried across the real
/// production migration and then audited by the Torture oracle set (end-state + transition log +
/// conservation). Needs the dockerized Postgres and SQL Server, like the conformance suite; a run
/// against unreachable databases reports infrastructure (exit 2) and fails loudly rather than skipping.
///
/// All facts live in one class so they run serially — Postgres and SQL Server each drive a single
/// throwaway upgrade database, so the sabotage fact must not run concurrently with the clean one.
/// </summary>
[Collection("upgrade")]
public sealed class UpgradeHarnessTests
{
    // Short workload per prior version keeps the shipped-prior-version sweep battery-friendly while still
    // running a real concurrent workload across the freshly migrated schema. Both networked adapters are
    // re-baselined to a single consolidated v1, so their sweep (v1..v(current-1)) is legitimately empty and
    // the clean facts pass vacuously; the sabotage fact below still exercises the oracle end to end.
    private static readonly TimeSpan BatteryWorkload = TimeSpan.FromSeconds(3);

    [Fact]
    public async Task Postgres_EveryShippedPriorVersion_UpgradesInPlaceCleanly()
    {
        var exit = await UpgradeRun.RunAsync(new UpgradeOptions
        {
            Adapter = UpgradeAdapter.Postgres,
            WorkloadDuration = BatteryWorkload,
        });
        AssertClean(exit);
    }

    [Fact]
    public async Task SqlServer_EveryShippedPriorVersion_UpgradesInPlaceCleanly()
    {
        var exit = await UpgradeRun.RunAsync(new UpgradeOptions
        {
            Adapter = UpgradeAdapter.SqlServer,
            WorkloadDuration = BatteryWorkload,
        });
        AssertClean(exit);
    }

    [Fact]
    public async Task Sabotage_LosingAPopulatedJobDuringMigration_TurnsTheHarnessRed()
    {
        // Hand-break the migration on the consolidated v1 (the only shipped version, so the empty sweep
        // cannot exercise the oracle on its own): populate the base v1 fixture inventory, run the real
        // idempotent migrate-to-current, delete a populated fixture job, and prove the conservation oracle
        // goes RED. Proves the harness has teeth — a broken upgrade cannot pass green.
        var exit = await UpgradeRun.RunAsync(new UpgradeOptions
        {
            Adapter = UpgradeAdapter.Postgres,
            WorkloadDuration = TimeSpan.FromSeconds(2),
            OnlyPriorVersion = 1,
            Sabotage = true,
        });
        Assert.Equal(1, exit); // 1 = oracle violations; 2 would be infrastructure
    }

    private static void AssertClean(int exit)
    {
        Assert.False(exit == 2, "Databases unreachable — start them with: docker compose up -d postgres sqlserver");
        Assert.Equal(0, exit);
    }
}

/// <summary>Serializes the upgrade facts — they share one throwaway upgrade database per adapter.</summary>
[CollectionDefinition("upgrade", DisableParallelization = true)]
public sealed class UpgradeCollection;
