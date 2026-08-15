using Xunit.Abstractions;

namespace BackWave.Tests.Simulation;

/// <summary>
/// The nightly randomized soak: excluded from PR CI (Category=Soak), run on a schedule
/// with fresh seeds. Every seed is printed before its run so any failure replays exactly —
/// pin it as a new InlineData in SimulatorTests when that happens.
///
/// The regime knobs are pure functions of the seed, so each run stays reproducible while the
/// corpus spans the surface: some seeds stay calm (the baseline path keeps getting explored),
/// others race operator actions, inject store faults, and seed a schedule the operator can
/// trigger — so the fresh-seed search reaches the operator surface and the P2 oracles
/// (legal-transition, execute-once, audit-completeness) under novel interleavings.
/// </summary>
[Trait("Category", "Soak")]
public class SoakTests(ITestOutputHelper output)
{
    [Fact]
    public void RandomizedSoak_ManySeeds_LongRuns()
    {
        var entropy = (ulong)Random.Shared.NextInt64();
        output.WriteLine($"soak entropy base: {entropy}");

        for (var i = 0UL; i < 25; i++)
        {
            var seed = entropy + i;

            // Derive the regime from the seed: a third of seeds stay calm; the rest race operator
            // actions, and an overlapping subset also inject store faults. One seed in three seeds a
            // schedule so the operator's TriggerScheduleNow path fires under the soak too.
            var operatorActions = seed % 3 == 0 ? 0 : (int)(40 + seed % 120);
            var storeFaultProbability = seed % 4 == 0 ? 0.0 : 0.05 * (1 + seed % 4);
            var withSchedule = seed % 3 == 1;

            output.WriteLine(
                $"running seed {seed} (ops={operatorActions}, storeFault={storeFaultProbability:0.00}, schedule={withSchedule})");

            var result = new Simulator(new SimulationOptions
            {
                Seed = seed,
                WorkloadDuration = TimeSpan.FromHours(8),
                DrainAllowance = TimeSpan.FromHours(2),
                JobCount = 500,
                OperatorActionCount = operatorActions,
                StoreFaultProbability = storeFaultProbability,
                Schedules = withSchedule
                    ? [new SeededSchedule { Id = "soak-hourly", Cron = "0 * * * *" }]
                    : [],
            }).Run();

            output.WriteLine(
                $"seed {seed}: {result.Steps} steps, {result.Crashes} crashes, {result.StaleOutcomes} stale, "
                + $"{result.OperatorCancels} cancels, {result.OperatorRequeues} requeues, "
                + $"{result.QueuePauses} pauses, {result.ScheduleTriggers} triggers");
        }
    }
}
