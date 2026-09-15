using BackWave.Diagnostics;
using BackWave.Storage;

namespace BackWave.Tests;

/// <summary>
/// Pins the one rule every store applies when its claim-lease fence refuses an observer report, at the
/// edge that matters: the 30 s skew allowance. A report whose belief sits exactly at the allowance is a
/// lapse, one tick past it is a contradiction, and a reporter that did not say what it believed is
/// never counted. The hosted tests prove each side from far away - this is the boundary itself.
/// </summary>
public class ObserverFenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ClockSkewAllowance_IsThirtySeconds()
    {
        // The number the boundary rows below lean on. A change here is a change to what a healthy fleet
        // is allowed to look like, and must move the rows with it.
        Assert.Equal(TimeSpan.FromSeconds(30), Invariant.ClockSkewAllowance);
    }

    [Theory]
    [InlineData(0L, false)]   // exactly at the allowance: a lapse, and the allowance is spent in full
    [InlineData(1L, true)]    // one tick past it: the reporter still believed its lease was live
    [InlineData(-1L, false)]  // one tick short of it: a lapse
    public void IsContradiction_TurnsOnlyAtTheSkewAllowance(long ticksPastAllowance, bool expected)
    {
        var believed = Now + Invariant.ClockSkewAllowance + TimeSpan.FromTicks(ticksPastAllowance);
        var report = new ObserverDeliveryReport("obs", "w1", [], Now) { BelievedLeaseExpiry = believed };

        Assert.Equal(expected, ObserverFence.IsContradiction(report));
    }

    [Fact]
    public void IsContradiction_IsFalse_WhenTheReporterDidNotSayWhatItBelieved()
    {
        // The belief is a lower bound, so silence errs toward not counting and never toward a false alarm.
        var report = new ObserverDeliveryReport("obs", "w1", [], Now);

        Assert.False(ObserverFence.IsContradiction(report));
    }
}
