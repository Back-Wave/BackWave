using BackWave.Diagnostics;
using BackWave.Hosting;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace BackWave.Hosting.Tests;

// Per-pump health bookkeeping surfaced at group altitude (issue 0192): a group may run several Pumps
// (its Pumps count), each with a distinct worker identity. Health is keyed per Pump internally so one
// Pump's clean cycle never clears a sibling's degraded mark and one Pump's halt never reads as the whole
// group down — while the public surface (HaltedGroups / DegradedGroups / IsHealthy) stays group-keyed.
public sealed class BackWaveHealthTests
{
    private static InvalidOperationException Boom(string message = "invariant violated") => new(message);

    [Fact]
    public void SinglePumpGroup_HaltReadsAsWholeGroupHalt_AndFlipsUnhealthy()
    {
        var health = new BackWaveHealth();

        health.ReportHalted("emails", "emails:pump-0", groupPumpCount: 1, Boom());

        // Default single-Pump behaviour is unchanged: one halt is a whole-group halt.
        Assert.True(health.HaltedGroups.ContainsKey("emails"));
        Assert.False(health.PartiallyHaltedGroups.ContainsKey("emails"));
        Assert.False(health.IsHealthy);
    }

    [Fact]
    public void MultiPumpGroup_OnePumpHalt_DoesNotReadAsWholeGroupHalt()
    {
        var health = new BackWaveHealth();

        // One of two Pumps fail-stops; its sibling is still claiming and executing.
        health.ReportHalted("emails", "emails:pump-0", groupPumpCount: 2, Boom());

        // Bug #2 fixed: a single Pump halting must not mark the whole group halted, and the surviving
        // sibling keeps the host healthy.
        Assert.False(health.HaltedGroups.ContainsKey("emails"));
        Assert.True(health.PartiallyHaltedGroups.ContainsKey("emails"));
        Assert.True(health.IsHealthy);

        // Once every Pump has halted the group reads wholly halted and the host turns unhealthy.
        health.ReportHalted("emails", "emails:pump-1", groupPumpCount: 2, Boom());
        Assert.True(health.HaltedGroups.ContainsKey("emails"));
        Assert.False(health.PartiallyHaltedGroups.ContainsKey("emails"));
        Assert.False(health.IsHealthy);
    }

    [Fact]
    public void MultiPumpGroup_SiblingCleanCycle_DoesNotClearAnotherPumpsDegradedMark()
    {
        var health = new BackWaveHealth();

        // Pump 0 hits a transient store blip and is marked degraded.
        health.ReportDegraded("emails", "emails:pump-0", new TimeoutException("store blip"));
        Assert.True(health.DegradedGroups.ContainsKey("emails"));

        // Pump 1 completes a clean cycle. Bug #1 fixed: its recovery clears only its own mark, never
        // Pump 0's, so the group stays degraded while Pump 0 is still struggling.
        health.ReportRecovered("emails", "emails:pump-1");
        Assert.True(health.DegradedGroups.ContainsKey("emails"));

        // Pump 0 finally recovers — now the group's degraded mark clears.
        health.ReportRecovered("emails", "emails:pump-0");
        Assert.False(health.DegradedGroups.ContainsKey("emails"));
    }

    [Fact]
    public void PumpHalt_SupersedesItsOwnDegradedMark_ButNotASiblings()
    {
        var health = new BackWaveHealth();

        health.ReportDegraded("emails", "emails:pump-0", new TimeoutException("store blip"));
        health.ReportDegraded("emails", "emails:pump-1", new TimeoutException("store blip"));

        // Pump 0 escalates to a halt: its own degraded mark is superseded, but Pump 1 stays degraded.
        health.ReportHalted("emails", "emails:pump-0", groupPumpCount: 2, Boom());

        Assert.True(health.DegradedGroups.ContainsKey("emails")); // Pump 1 still degraded
        Assert.True(health.PartiallyHaltedGroups.ContainsKey("emails"));
        Assert.True(health.IsHealthy); // Pump 1 alive

        health.ReportRecovered("emails", "emails:pump-1");
        Assert.False(health.DegradedGroups.ContainsKey("emails"));
    }

    [Fact]
    public void DistinctGroups_HaltAndDegradeIndependently()
    {
        var health = new BackWaveHealth();

        health.ReportHalted("emails", "emails:pump-0", groupPumpCount: 1, Boom());
        health.ReportDegraded("reports", "reports:pump-0", new TimeoutException("store blip"));

        Assert.True(health.HaltedGroups.ContainsKey("emails"));
        Assert.False(health.HaltedGroups.ContainsKey("reports"));
        Assert.True(health.DegradedGroups.ContainsKey("reports"));
        Assert.False(health.DegradedGroups.ContainsKey("emails"));
        Assert.False(health.IsHealthy); // emails is wholly halted
    }

    [Fact]
    public void NamedInvariantHalt_CarriesTheTriggerId_WithoutPuttingItInToString()
    {
        var health = new BackWaveHealth();

        health.ReportHalted(
            "emails", "emails:pump-0", groupPumpCount: 1,
            new InvariantViolationException(InvariantTrigger.ClaimedJobTerminal, "claimed a terminal job"));

        // The id travels as the typed component, read off the exception so the halt log and the health
        // report can never name different triggers.
        var halt = health.HaltedGroups["emails"];
        Assert.Equal(InvariantTrigger.ClaimedJobTerminal, halt.Trigger);

        // ...and deliberately NOT in the rendering, which is what a probe payload may serialize. Keeping
        // it out is what makes a trigger rename cost nothing that outlives the process.
        Assert.DoesNotContain(nameof(InvariantTrigger.ClaimedJobTerminal), halt.ToString());
        Assert.Equal($"{typeof(InvariantViolationException).FullName}: claimed a terminal job", halt.ToString());
    }

    [Fact]
    public void UnnamedHalt_LeavesTheTriggerNull()
    {
        var health = new BackWaveHealth();

        // The negative catch-all still halts on a fault no check named; there is no id to record.
        health.ReportHalted("emails", "emails:pump-0", groupPumpCount: 1, Boom());

        Assert.Null(health.HaltedGroups["emails"].Trigger);
    }

    // --- The health check's three-way result (issue dst-0011) ---

    private static HealthStatus StatusOf(BackWaveHealth health) =>
        new BackWaveHealthCheck(health)
            .CheckHealthAsync(new HealthCheckContext()).GetAwaiter().GetResult().Status;

    [Fact]
    public void HealthCheck_CleanGroups_ReportHealthy()
    {
        var health = new BackWaveHealth();

        health.ReportRecovered("emails", "emails:pump-0");

        Assert.Equal(HealthStatus.Healthy, StatusOf(health));
    }

    [Fact]
    public void HealthCheck_DegradedGroup_ReportsDegraded_NotHealthy()
    {
        var health = new BackWaveHealth();

        // BREAKING behavioural change: a transient store fault used to report Healthy because the check
        // never read DegradedGroups at all. The group is still claiming, so it is not a fail-stop either.
        health.ReportDegraded("emails", "emails:pump-0", new TimeoutException("store blip"));

        Assert.Equal(HealthStatus.Degraded, StatusOf(health));
    }

    [Fact]
    public void HealthCheck_PartiallyHaltedGroup_ReportsDegraded()
    {
        var health = new BackWaveHealth();

        // One Pump of two stopped; the survivor keeps the group serving, so it pages as impaired, not down.
        health.ReportHalted("emails", "emails:pump-0", groupPumpCount: 2, Boom());

        Assert.Equal(HealthStatus.Degraded, StatusOf(health));
    }

    [Fact]
    public void HealthCheck_WhollyHaltedGroup_ReportsUnhealthy()
    {
        var health = new BackWaveHealth();

        health.ReportHalted("emails", "emails:pump-0", groupPumpCount: 1, Boom());

        // A wholly halted group stays the only Unhealthy result - the one an orchestrator takes out of
        // rotation on. A degraded sibling never escalates it.
        Assert.Equal(HealthStatus.Unhealthy, StatusOf(health));

        health.ReportDegraded("reports", "reports:pump-0", new TimeoutException("store blip"));
        Assert.Equal(HealthStatus.Unhealthy, StatusOf(health));
    }
}
