using BackWave.Core;

namespace BackWave.Tests;

public class CronExpressionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 10, 0, 0, 0, TimeSpan.Zero); // a Wednesday

    private static DateTimeOffset Next(string cron, DateTimeOffset after)
        => CronExpression.Parse(cron).NextAfter(after)!.Value;

    [Fact]
    public void EveryFifteenMinutes_StepsCorrectly()
    {
        Assert.Equal(T0.AddMinutes(15), Next("*/15 * * * *", T0));
        Assert.Equal(T0.AddMinutes(30), Next("*/15 * * * *", T0.AddMinutes(15)));
        Assert.Equal(T0.AddMinutes(15), Next("*/15 * * * *", T0.AddMinutes(14)));
    }

    [Fact]
    public void DailyAtTwo_FiresOncePerDay()
    {
        Assert.Equal(T0.AddHours(2), Next("0 2 * * *", T0));
        Assert.Equal(T0.AddDays(1).AddHours(2), Next("0 2 * * *", T0.AddHours(2)));
    }

    [Fact]
    public void MonthlyOnTheFirst_RollsToNextMonth()
    {
        Assert.Equal(new DateTimeOffset(2026, 7, 1, 14, 30, 0, TimeSpan.Zero), Next("30 14 1 * *", T0));
    }

    [Fact]
    public void WeeklyOnMonday_LandsOnMonday()
    {
        var next = Next("0 9 * * 1", T0);
        Assert.Equal(DayOfWeek.Monday, next.DayOfWeek);
        Assert.Equal(new DateTimeOffset(2026, 6, 15, 9, 0, 0, TimeSpan.Zero), next);
    }

    [Fact]
    public void SevenMeansSunday()
    {
        Assert.Equal(Next("0 9 * * 0", T0), Next("0 9 * * 7", T0));
    }

    [Fact]
    public void SecondsField_StepsBySeconds()
    {
        Assert.Equal(T0.AddSeconds(10), Next("*/10 * * * * *", T0));
        Assert.Equal(T0.AddSeconds(20), Next("*/10 * * * * *", T0.AddSeconds(10)));
    }

    [Fact]
    public void RestrictedDomAndDow_UseOrSemantics()
    {
        // Standard cron: "the 13th OR a Friday".
        var cron = CronExpression.Parse("0 0 13 * 5");
        var first = cron.NextAfter(T0)!.Value;  // Fri 2026-06-12
        var second = cron.NextAfter(first)!.Value; // Sat 2026-06-13
        Assert.Equal(new DateTimeOffset(2026, 6, 12, 0, 0, 0, TimeSpan.Zero), first);
        Assert.Equal(new DateTimeOffset(2026, 6, 13, 0, 0, 0, TimeSpan.Zero), second);
    }

    [Fact]
    public void ImpossibleDate_ReturnsNull()
    {
        Assert.Null(CronExpression.Parse("0 0 30 2 *").NextAfter(T0));
    }

    [Fact]
    public void FiveFieldForm_CanonicalizesToSixFields()
    {
        Assert.Equal("0 0 2 * * *", CronExpression.Parse("0 2 * * *").Canonical);
        Assert.Equal("30 0 2 * * *", CronExpression.Parse("30 0 2 * * *").Canonical);
    }

    [Theory]
    [InlineData("* * * *")]
    [InlineData("61 * * * *")]
    [InlineData("* 25 * * *")]
    [InlineData("a * * * *")]
    [InlineData("5-2 * * * *")]
    [InlineData("@daily")]
    public void InvalidExpressions_ThrowFormatException(string cron)
    {
        Assert.Throws<FormatException>(() => CronExpression.Parse(cron));
    }

    [Fact]
    public void Builder_CompilesToCron_AndOnlyToCron()
    {
        Assert.Equal(CronExpression.Parse("0 2 * * *").Canonical, Cron.Daily(2).Canonical);
        Assert.Equal(CronExpression.Parse("*/5 * * * *").Canonical, Cron.EveryMinutes(5).Canonical);
        Assert.Equal(CronExpression.Parse("15 * * * *").Canonical, Cron.Hourly(15).Canonical);
        Assert.Equal(CronExpression.Parse("0 9 * * 1").Canonical, Cron.Weekly(DayOfWeek.Monday, 9).Canonical);
        Assert.Equal(CronExpression.Parse("30 14 1 * *").Canonical, Cron.Monthly(1, 14, 30).Canonical);
        Assert.Equal(CronExpression.Parse("* * * * *").Canonical, Cron.EveryMinute().Canonical);
    }
}
