using BackWave.Core;
using BackWave.Storage;

namespace BackWave.Tests;

/// <summary>
/// The named DST / Catch-Up scenario suites. ZonedCron and MintPlanner are pure, so the
/// suites drive them directly with explicit instants — a year of zone behavior with no
/// wall clock anywhere.
/// </summary>
public class ScheduleHardeningTests
{
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    // A second zone whose transitions run on the opposite calendar (southern hemisphere):
    // DST ends (fall back) in April and starts (spring forward) in October.
    private static readonly TimeZoneInfo Sydney = TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney");

    private static DateTimeOffset Utc(int y, int mo, int d, int h, int mi = 0)
        => new(y, mo, d, h, mi, 0, TimeSpan.Zero);

    // --- DST suite: 2am America/New_York runs exactly once per night across both transitions ---

    [Fact]
    public void SpringForward_SkippedTwoAm_FiresOnceAtFirstValidInstant()
    {
        // US spring forward: Sunday 2026-03-08, 02:00 EST → 03:00 EDT.
        var cron = CronExpression.Parse("0 2 * * *");

        var mar7 = ZonedCron.NextAfter(cron, Utc(2026, 3, 6, 12), NewYork);
        var mar8 = ZonedCron.NextAfter(cron, mar7!.Value, NewYork);
        var mar9 = ZonedCron.NextAfter(cron, mar8!.Value, NewYork);

        Assert.Equal(Utc(2026, 3, 7, 7), mar7);  // 2:00 EST (UTC-5)
        Assert.Equal(Utc(2026, 3, 8, 7), mar8);  // 2:00 skipped → fires at 3:00 EDT (UTC-4)
        Assert.Equal(Utc(2026, 3, 9, 6), mar9);  // 2:00 EDT (UTC-4)
    }

    [Fact]
    public void FallBack_AmbiguousOneAm_FiresFirstOccurrenceOnly()
    {
        // US fall back: Sunday 2026-11-01, 02:00 EDT → 01:00 EST. 1:00 occurs twice.
        var cron = CronExpression.Parse("0 1 * * *");

        var oct31 = ZonedCron.NextAfter(cron, Utc(2026, 10, 30, 12), NewYork);
        var nov1 = ZonedCron.NextAfter(cron, oct31!.Value, NewYork);
        var nov2 = ZonedCron.NextAfter(cron, nov1!.Value, NewYork);

        Assert.Equal(Utc(2026, 10, 31, 5), oct31); // 1:00 EDT (UTC-4)
        Assert.Equal(Utc(2026, 11, 1, 5), nov1);   // ambiguous 1:00 → FIRST occurrence (EDT)
        Assert.Equal(Utc(2026, 11, 2, 6), nov2);   // 1:00 EST (UTC-5) — second occurrence never fired
    }

    [Fact]
    public void TwoAmNewYork_AcrossAFullYear_FiresExactlyOncePerNight()
    {
        var cron = CronExpression.Parse("0 2 * * *");
        var cursor = Utc(2026, 1, 1, 12);
        var end = Utc(2027, 1, 1, 12);

        var count = 0;
        DateTimeOffset? previous = null;
        while (ZonedCron.NextAfter(cron, cursor, NewYork) is { } tick && tick < end)
        {
            if (previous is { } p)
            {
                var gap = tick - p;
                Assert.True(gap >= TimeSpan.FromHours(23) && gap <= TimeSpan.FromHours(25),
                    $"tick gap {gap} around {tick:O}");
            }
            previous = tick;
            cursor = tick;
            count++;
        }

        Assert.Equal(365, count);
    }

    // --- DST remap: a cursor inside the fall-back hour never returns a spurious null (issue 0030) ---

    [Fact]
    public void SubHourCron_CursorInsideFallBackHour_ReturnsNextUtcOccurrence_NeverNull()
    {
        // The empirically verified failing case: a fixed iteration bound exhausted mid-hour
        // and returned null, stalling the schedule forever. 06:30Z is 01:30 EST, inside the
        // repeated hour; the next every-minute tick is 02:00 EST = 07:00Z.
        var next = ZonedCron.NextAfter(CronExpression.Parse("* * * * *"), Utc(2026, 11, 1, 6, 30), NewYork);
        Assert.Equal(Utc(2026, 11, 1, 7, 0), next);
    }

    [Fact]
    public void SubHourCron_CursorInsideFallBackHour_Sydney_ReturnsNextUtcOccurrence()
    {
        // Sydney fall back 2026-04-05: 03:00 AEDT (UTC+11) → 02:00 AEST (UTC+10). 16:30Z is
        // 02:30 AEST, inside the repeated hour; the next every-minute tick is 03:00 AEST = 17:00Z.
        var next = ZonedCron.NextAfter(CronExpression.Parse("* * * * *"), Utc(2026, 4, 4, 16, 30), Sydney);
        Assert.Equal(Utc(2026, 4, 4, 17, 0), next);
    }

    [Fact]
    public void SubHourCron_CursorAtSpringForwardGapEdge_FiresAtFirstValidInstant()
    {
        // NY spring forward 2026-03-08: 02:00 EST → 03:00 EDT. 06:59Z is 01:59 EST; the next
        // every-minute tick would be 02:00 (skipped) and fires at 03:00 EDT = 07:00Z.
        var ny = ZonedCron.NextAfter(CronExpression.Parse("* * * * *"), Utc(2026, 3, 8, 6, 59), NewYork);
        Assert.Equal(Utc(2026, 3, 8, 7, 0), ny);

        // Sydney spring forward 2026-10-04: 02:00 AEST → 03:00 AEDT. 15:59Z is 01:59 AEST; the
        // next every-minute tick would be 02:00 (skipped) and fires at 03:00 AEDT = 16:00Z.
        var syd = ZonedCron.NextAfter(CronExpression.Parse("* * * * *"), Utc(2026, 10, 3, 15, 59), Sydney);
        Assert.Equal(Utc(2026, 10, 3, 16, 0), syd);
    }

    [Theory]
    [InlineData("* * * * *")]   // sub-hour
    [InlineData("0 * * * *")]   // hourly
    [InlineData("0 2 * * *")]   // daily at 2am
    public void EveryMinuteAndCoarserCrons_NeverReturnNull_AcrossEitherTransition_BothZones(string cron)
    {
        var expression = CronExpression.Parse(cron);
        // Sweep the two NY transitions and the two Sydney transitions; a null anywhere is the
        // stall bug. Each window brackets a transition with a day of slack on both sides.
        foreach (var (zone, from, to) in new[]
                 {
                     (NewYork, Utc(2026, 3, 7, 0), Utc(2026, 3, 9, 12)),
                     (NewYork, Utc(2026, 10, 31, 0), Utc(2026, 11, 2, 12)),
                     (Sydney, Utc(2026, 4, 3, 0), Utc(2026, 4, 6, 12)),
                     (Sydney, Utc(2026, 10, 2, 0), Utc(2026, 10, 5, 12)),
                 })
        {
            var cursor = from;
            DateTimeOffset? previous = null;
            while (ZonedCron.NextAfter(expression, cursor, zone) is { } tick && tick < to)
            {
                Assert.True(previous is null || tick > previous, $"non-monotonic tick {tick:O} in {zone.Id}");
                previous = tick;
                cursor = tick;
            }
            Assert.NotNull(previous); // the cron produced at least one tick in the window
        }
    }

    [Fact]
    public void FallBackAmbiguousHour_FiresExactlyOnce_NoSecondOccurrence()
    {
        // NY fall back: the ambiguous wall hour 01:00–01:59 maps (first occurrence, EDT) to
        // 05:00–05:59Z. Its second occurrence (EST) would be 06:00–06:59Z — an every-minute
        // cron must produce no tick there, or the repeated hour double-fires.
        var cron = CronExpression.Parse("* * * * *");
        var cursor = Utc(2026, 11, 1, 4, 59);
        var secondOccurrenceWindowStart = Utc(2026, 11, 1, 6, 0);
        var secondOccurrenceWindowEnd = Utc(2026, 11, 1, 7, 0);

        var ticks = new List<DateTimeOffset>();
        while (ZonedCron.NextAfter(cron, cursor, NewYork) is { } tick && tick < Utc(2026, 11, 1, 7, 30))
        {
            ticks.Add(tick);
            cursor = tick;
        }

        Assert.DoesNotContain(ticks, t => t >= secondOccurrenceWindowStart && t < secondOccurrenceWindowEnd);
        Assert.Equal(ticks.Count, ticks.Distinct().Count()); // never the same instant twice
        Assert.Contains(Utc(2026, 11, 1, 7, 0), ticks);       // 02:00 EST fires right after the empty window
    }

    // --- Catch-Up suite: an outage produces nothing (Skip) or exactly one make-up (Coalesce) ---

    private static readonly DateTimeOffset T0 = Utc(2026, 6, 1, 0);

    private static ScheduleSnapshot Hourly(CatchUpPolicy catchUp, bool noOverlap = false, bool live = false)
        => new(
            new ScheduleRecord
            {
                ScheduleId = "hourly",
                Cron = CronExpression.Parse("0 * * * *").Canonical,
                WireName = "work",
                Payload = ReadOnlyMemory<byte>.Empty,
                Queue = "default",
                Cursor = T0,
                CatchUp = catchUp,
                NoOverlap = noOverlap,
            },
            HasLiveInstance: live);

    [Fact]
    public void Skip_AfterAnOutage_MintsNothingForMissedTicks_AndRecordsThem()
    {
        var now = T0.AddHours(4).AddMinutes(30); // ticks T0+1h..T0+4h all missed
        var decision = Assert.Single(MintPlanner.Plan([Hourly(CatchUpPolicy.Skip)], now));

        Assert.Empty(decision.Ticks);
        Assert.Equal([T0.AddHours(1), T0.AddHours(2), T0.AddHours(3), T0.AddHours(4)], decision.SkippedTicks);
        Assert.Equal(T0.AddHours(4), decision.NewCursor); // missed means missed — never revisited
    }

    [Fact]
    public void Coalesce_AfterAnOutage_MintsExactlyOneMakeUpRun()
    {
        var now = T0.AddHours(4).AddMinutes(30);
        var decision = Assert.Single(MintPlanner.Plan([Hourly(CatchUpPolicy.Coalesce)], now));

        Assert.Equal([T0.AddHours(4)], decision.Ticks); // one make-up: the latest missed tick
        Assert.Equal([T0.AddHours(1), T0.AddHours(2), T0.AddHours(3)], decision.SkippedTicks);
    }

    [Fact]
    public void NormalOperation_AFreshTick_MintsUnderBothPolicies()
    {
        var now = T0.AddHours(1).AddSeconds(10); // within the missed-tick threshold
        foreach (var policy in new[] { CatchUpPolicy.Skip, CatchUpPolicy.Coalesce })
        {
            var decision = Assert.Single(MintPlanner.Plan([Hourly(policy)], now));
            Assert.Equal([T0.AddHours(1)], decision.Ticks);
            Assert.Empty(decision.SkippedTicks);
        }
    }

    // --- No-Overlap: skip while a previous instance is non-terminal, visibly ---

    [Fact]
    public void NoOverlap_WithALiveInstance_SkipsTheTickVisibly()
    {
        var now = T0.AddHours(1).AddSeconds(10);
        var decision = Assert.Single(MintPlanner.Plan([Hourly(CatchUpPolicy.Skip, noOverlap: true, live: true)], now));

        Assert.Empty(decision.Ticks);
        Assert.Equal([T0.AddHours(1)], decision.SkippedTicks);
    }

    [Fact]
    public void NoOverlap_WithoutALiveInstance_MintsNormally()
    {
        var now = T0.AddHours(1).AddSeconds(10);
        var decision = Assert.Single(MintPlanner.Plan([Hourly(CatchUpPolicy.Skip, noOverlap: true)], now));

        Assert.Equal([T0.AddHours(1)], decision.Ticks);
        Assert.Empty(decision.SkippedTicks);
    }

    [Fact]
    public async Task SkippedTicks_AreRecordedOnTheSchedule_AndVisibleViaMonitorReads()
    {
        var store = new Storage.InMemory.InMemoryJobStore();
        await store.UpsertScheduleAsync(Hourly(CatchUpPolicy.Skip, noOverlap: true).Schedule);

        // Tick 1 mints; the instance stays non-terminal, so tick 2 must skip — visibly.
        var t1 = T0.AddHours(1).AddSeconds(10);
        await store.MintDueAsync(MintPlanner.Plan(await store.ListSchedulesAsync(), t1));
        Assert.Single(await store.ListJobsAsync(new JobQuery { ScheduleId = "hourly" }));

        var t2 = T0.AddHours(2).AddSeconds(10);
        await store.MintDueAsync(MintPlanner.Plan(await store.ListSchedulesAsync(), t2));

        var snapshot = Assert.Single(await store.ListSchedulesAsync());
        Assert.Single(await store.ListJobsAsync(new JobQuery { ScheduleId = "hourly" }));
        Assert.Equal([T0.AddHours(2)], snapshot.Schedule.SkippedTicks);
        Assert.Equal(T0.AddHours(2), snapshot.Schedule.Cursor);
    }
}
