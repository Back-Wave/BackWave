using BackWave.Hosting;

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
}
