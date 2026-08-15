using BackWave.Core;
using BackWave.Storage;
using CsCheck;

namespace BackWave.Tests;

/// <summary>
/// Property-based coverage of the pure recurring-schedule kernels (CronExpression / ZonedCron /
/// MintPlanner). These are deterministic functions over a huge input space — exactly where the
/// example-based suites in <see cref="ScheduleHardeningTests"/> and <see cref="CronExpressionTests"/>
/// under-explore. Each property pins a fixed CsCheck seed so the battery is deterministic; CsCheck
/// prints the failing seed on any regression so it can be replayed. Iteration counts are kept small
/// enough that the whole file runs in well under a second.
/// </summary>
public class SchedulePropertyTests
{
    // Pinned so the battery is reproducible run-to-run. CsCheck derives every subsequent iteration
    // deterministically from this and prints the seed of any failing case for one-line replay.
    private const string Seed = "0N0XIzNsQ0O2";

    // A fixed UTC anchor (a Wednesday) that generated instants are offset from.
    private static readonly DateTimeOffset Base = new(2026, 6, 10, 0, 0, 0, TimeSpan.Zero);

    // Real IANA zones with transitions on both hemispheres' calendars: NY/London spring forward in
    // March and fall back in October/November; Sydney is inverted (fall back April, spring forward
    // October). ResolveZone uses TimeZoneInfo.FindSystemTimeZoneById, so these must exist on the host.
    private static readonly TimeZoneInfo[] Zones =
    [
        TimeZoneInfo.FindSystemTimeZoneById("America/New_York"),
        TimeZoneInfo.FindSystemTimeZoneById("Europe/London"),
        TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney"),
    ];

    // Broad UTC windows that each bracket one 2026 DST transition for the zone above, with a day of
    // slack on both sides — the same windows the hand-written DST facts sweep.
    private static readonly (DateTimeOffset From, DateTimeOffset To)[][] TransitionWindows =
    [
        [(Utc(2026, 3, 7), Utc(2026, 3, 9, 12)), (Utc(2026, 10, 31), Utc(2026, 11, 2, 12))],   // New York
        [(Utc(2026, 3, 28), Utc(2026, 3, 30, 12)), (Utc(2026, 10, 24), Utc(2026, 10, 26, 12))], // London
        [(Utc(2026, 4, 3), Utc(2026, 4, 6, 12)), (Utc(2026, 10, 2), Utc(2026, 10, 5, 12))],     // Sydney
    ];

    private static DateTimeOffset Utc(int y, int mo, int d, int h = 0, int mi = 0)
        => new(y, mo, d, h, mi, 0, TimeSpan.Zero);

    // --- A cron field carried alongside the set it denotes, so a property has an independent oracle
    //     (the set) that never touches the parser under test. ---

    private sealed record CronField(string Text, HashSet<int> Set, bool Restricted);

    /// <summary>Generates a cron field over [min,max]: either "*" (whole range) or a small explicit value list.</summary>
    private static Gen<CronField> Field(int min, int max) =>
        from star in Gen.Bool
        from vals in Gen.Int[min, max].Array[1, 4]
        select star
            ? new CronField("*", Enumerable.Range(min, max - min + 1).ToHashSet(), Restricted: false)
            : ExplicitField(vals);

    private static CronField ExplicitField(int[] vals)
    {
        var set = vals.ToHashSet();
        return new CronField(string.Join(',', set.OrderBy(v => v)), set, Restricted: true);
    }

    /// <summary>The independent match oracle: does this instant satisfy the six generated field sets?</summary>
    private static bool OracleMatches(
        DateTimeOffset t, CronField sec, CronField min, CronField hour, CronField dom, CronField month, CronField dow)
    {
        if (!month.Set.Contains(t.Month) || !hour.Set.Contains(t.Hour)
            || !min.Set.Contains(t.Minute) || !sec.Set.Contains(t.Second))
        {
            return false;
        }
        var d = dom.Set.Contains(t.Day);
        var w = dow.Set.Contains((int)t.DayOfWeek);
        // Standard cron: both day fields restricted → OR; otherwise AND.
        return dom.Restricted && dow.Restricted ? d || w : d && w;
    }

    // === Round-trip ===================================================================

    [Fact]
    public void CronCanonical_IsAParseFixedPoint_AndPreservesSemantics()
    {
        (from sec in Field(0, 59)
         from min in Field(0, 59)
         from hour in Field(0, 23)
         from dom in Field(1, 31)
         from month in Field(1, 12)
         from dow in Field(0, 6)
         from offset in Gen.Int[0, 400_000]
         select (sec, min, hour, dom, month, dow, offset))
        .Sample(x =>
        {
            var text = $"{x.sec.Text} {x.min.Text} {x.hour.Text} {x.dom.Text} {x.month.Text} {x.dow.Text}";
            var canonical = CronExpression.Parse(text).Canonical;

            // Canonical is a parse fixed point: reparsing it yields the identical canonical form.
            Assert.Equal(canonical, CronExpression.Parse(canonical).Canonical);

            // ...and the reparse is behaviourally identical (Canonical drops no information).
            var from = Base.AddMinutes(x.offset);
            Assert.Equal(CronExpression.Parse(text).NextAfter(from), CronExpression.Parse(canonical).NextAfter(from));
        }, seed: Seed, iter: 200);
    }

    [Fact]
    public void FluentBuilders_RoundTripThroughCanonicalCron()
    {
        (from hour in Gen.Int[0, 23]
         from minute in Gen.Int[0, 59]
         from dom in Gen.Int[1, 28]
         from step in Gen.Int[1, 59]
         from day in Gen.Enum<DayOfWeek>()
         select (hour, minute, dom, step, day))
        .Sample(x =>
        {
            foreach (var expr in new[]
                     {
                         Cron.EveryMinute(), Cron.EveryMinutes(x.step), Cron.Hourly(x.minute),
                         Cron.Daily(x.hour, x.minute), Cron.Weekly(x.day, x.hour, x.minute),
                         Cron.Monthly(x.dom, x.hour, x.minute),
                     })
            {
                // Every builder compiles to cron and only to cron: its canonical form is a parse fixed point.
                Assert.Equal(expr.Canonical, CronExpression.Parse(expr.Canonical).Canonical);
            }
        }, seed: Seed, iter: 200);
    }

    // === Next-occurrence: monotonicity, containment, no-skip ==========================

    [Fact]
    public void NextAfter_EqualsBruteForceMinuteScan_ForMinuteAndHourCrons()
    {
        // For crons restricted only on minute and hour (every day matches), the next occurrence is
        // exactly the smallest matching minute strictly after the cursor — computed here by an
        // independent minute-by-minute scan. Equality proves monotonicity, containment, and that no
        // valid tick is skipped between the cursor and the returned occurrence, all at once.
        (from min in Field(0, 59)
         from hour in Field(0, 23)
         from offset in Gen.Int[0, 2 * 24 * 60]
         select (min, hour, offset))
        .Sample(x =>
        {
            var expr = CronExpression.Parse($"{x.min.Text} {x.hour.Text} * * *");
            var from = Base.AddMinutes(x.offset);
            Assert.Equal(BruteForceNextMinute(from, x.min.Set, x.hour.Set), expr.NextAfter(from));
        }, seed: Seed, iter: 200);
    }

    private static DateTimeOffset? BruteForceNextMinute(DateTimeOffset from, HashSet<int> minutes, HashSet<int> hours)
    {
        var t = new DateTimeOffset(from.UtcTicks - (from.UtcTicks % TimeSpan.TicksPerMinute), TimeSpan.Zero)
            .AddMinutes(1);
        var bound = t.AddDays(2); // a daily-satisfiable cron always fires within 24h
        for (; t <= bound; t = t.AddMinutes(1))
        {
            if (hours.Contains(t.Hour) && minutes.Contains(t.Minute))
            {
                return t;
            }
        }
        return null;
    }

    [Fact]
    public void NextAfter_SequenceIsStrictlyIncreasing_AndEveryTickMatchesTheExpression()
    {
        // Full six-field crons (day-of-month, month, day-of-week too). Walk the occurrence sequence
        // and check: strictly increasing (never repeats, never goes backward) and every emitted
        // instant satisfies the independent field-set oracle (containment).
        (from sec in Field(0, 59)
         from min in Field(0, 59)
         from hour in Field(0, 23)
         from dom in Field(1, 31)
         from month in Field(1, 12)
         from dow in Field(0, 6)
         from offset in Gen.Int[0, 400_000]
         select (sec, min, hour, dom, month, dow, offset))
        .Sample(x =>
        {
            var expr = CronExpression.Parse(
                $"{x.sec.Text} {x.min.Text} {x.hour.Text} {x.dom.Text} {x.month.Text} {x.dow.Text}");
            var cursor = Base.AddMinutes(x.offset);
            DateTimeOffset? prev = null;
            for (var i = 0; i < 8 && expr.NextAfter(cursor) is { } tick; i++)
            {
                Assert.True(prev is null || tick > prev, $"non-monotonic tick {tick:O}");
                Assert.True(OracleMatches(tick, x.sec, x.min, x.hour, x.dom, x.month, x.dow),
                    $"emitted tick {tick:O} does not match the expression");
                prev = tick;
                cursor = tick;
            }
        }, seed: Seed, iter: 150);
    }

    // === Catch-Up Policy conservation (MintPlanner) ===================================

    // interval minutes ∈ {1,5,15,60}, an outage of up to 10 hours, and a random policy.
    private static readonly Gen<(int Interval, int NowOffset, CatchUpPolicy Policy)> ConservationGen =
        from intervalIndex in Gen.Int[0, 3]
        from nowOffset in Gen.Int[0, 600]
        from policy in Gen.Enum<CatchUpPolicy>()
        select (new[] { 1, 5, 15, 60 }[intervalIndex], nowOffset, policy);

    [Fact]
    public void CatchUp_MintedPlusSkipped_ConservesEveryDueTick_ExactlyOnce()
    {
        ConservationGen.Sample(x =>
        {
            var cron = Cron.EveryMinutes(x.Interval).Canonical;
            var now = Base.AddMinutes(x.NowOffset);
            var threshold = MintPlanner.MissedTickThreshold;
            var resolved = ResolvedTicks(cron, Base, now);
            var decisions = MintPlanner.Plan([Snapshot(cron, Base, x.Policy)], now);

            if (resolved.Count == 0)
            {
                Assert.Empty(decisions);
                return;
            }
            var d = Assert.Single(decisions);
            Assert.Equal(Base, d.ExpectedCursor);
            Assert.Equal(resolved[^1], d.NewCursor);
            AssertConservation(resolved, now, threshold, x.Policy, d.Ticks, d.SkippedTicks);
        }, seed: Seed, iter: 200);
    }

    [Fact]
    public void NoOverlap_WithALiveInstance_SkipsEveryDueTick_MintsNone()
    {
        (from intervalIndex in Gen.Int[0, 3]
         from nowOffset in Gen.Int[1, 600]
         select (Interval: new[] { 1, 5, 15, 60 }[intervalIndex], NowOffset: nowOffset))
        .Sample(x =>
        {
            var cron = Cron.EveryMinutes(x.Interval).Canonical;
            var now = Base.AddMinutes(x.NowOffset);
            var resolved = ResolvedTicks(cron, Base, now);
            var decisions = MintPlanner.Plan(
                [Snapshot(cron, Base, CatchUpPolicy.Skip, noOverlap: true, live: true)], now);

            if (resolved.Count == 0)
            {
                Assert.Empty(decisions);
                return;
            }
            var d = Assert.Single(decisions);
            Assert.Empty(d.Ticks);
            Assert.Equal(resolved, d.SkippedTicks); // every tick recorded as visibly skipped, in order
        }, seed: Seed, iter: 100);
    }

    /// <summary>The correct partition of resolved ticks into (minted, skipped) per the Catch-Up Policy.</summary>
    private static (List<DateTimeOffset> Mint, List<DateTimeOffset> Skipped) ExpectedPartition(
        IReadOnlyList<DateTimeOffset> resolved, DateTimeOffset now, TimeSpan threshold, CatchUpPolicy policy)
    {
        var missed = resolved.Where(t => now - t > threshold).ToList();
        var fresh = resolved.Where(t => now - t <= threshold).ToList();
        return policy switch
        {
            CatchUpPolicy.Coalesce when missed.Count > 0 => ([missed[^1], .. fresh], missed[..^1]),
            _ => (fresh, missed),
        };
    }

    private static void AssertConservation(
        IReadOnlyList<DateTimeOffset> resolved, DateTimeOffset now, TimeSpan threshold, CatchUpPolicy policy,
        IReadOnlyList<DateTimeOffset> mint, IReadOnlyList<DateTimeOffset> skipped)
    {
        var (expectedMint, expectedSkipped) = ExpectedPartition(resolved, now, threshold, policy);
        Assert.Equal(expectedMint, mint);
        Assert.Equal(expectedSkipped, skipped);

        // Structural invariants independent of the oracle: exact partition of the due set, in order.
        Assert.Empty(mint.Intersect(skipped));
        Assert.Equal(resolved.OrderBy(t => t), mint.Concat(skipped).OrderBy(t => t));
        AssertStrictlyIncreasing(mint);
        AssertStrictlyIncreasing(skipped);
    }

    private static void AssertStrictlyIncreasing(IReadOnlyList<DateTimeOffset> ticks)
    {
        for (var i = 1; i < ticks.Count; i++)
        {
            Assert.True(ticks[i] > ticks[i - 1], "ticks not strictly increasing");
        }
    }

    // === Sabotage self-test: prove the conservation property has teeth ================

    [Fact]
    public void Sabotage_BrokenCoalesceMakeUp_IsCaughtByConservation()
    {
        // A deliberately-broken minting kernel: on Coalesce it collapses to the EARLIEST missed tick
        // (missed[0]) instead of the correct LATEST one (missed[^1]) — a one-line off-by-one. The
        // product is NOT touched; the mutation lives only in SabotagedPartition below. The
        // conservation property must reject it, so we run the SAME generator through the mutant and
        // assert the property FAILS. If this ever stops throwing, the property has lost its teeth.
        var caught = false;
        try
        {
            ConservationGen.Sample(x =>
            {
                var cron = Cron.EveryMinutes(x.Interval).Canonical;
                var now = Base.AddMinutes(x.NowOffset);
                var threshold = MintPlanner.MissedTickThreshold;
                var resolved = ResolvedTicks(cron, Base, now);
                if (resolved.Count == 0)
                {
                    return;
                }
                var (mint, skipped) = SabotagedPartition(resolved, now, threshold, x.Policy);
                AssertConservation(resolved, now, threshold, x.Policy, mint, skipped);
            }, seed: Seed, iter: 200);
        }
        catch (Exception)
        {
            caught = true;
        }
        Assert.True(caught, "the sabotaged minting kernel was NOT caught by the conservation property");
    }

    // Mutant of ExpectedPartition: picks the wrong make-up tick on Coalesce. Never used by product.
    private static (List<DateTimeOffset> Mint, List<DateTimeOffset> Skipped) SabotagedPartition(
        IReadOnlyList<DateTimeOffset> resolved, DateTimeOffset now, TimeSpan threshold, CatchUpPolicy policy)
    {
        var missed = resolved.Where(t => now - t > threshold).ToList();
        var fresh = resolved.Where(t => now - t <= threshold).ToList();
        return policy switch
        {
            CatchUpPolicy.Coalesce when missed.Count > 0 => ([missed[0], .. fresh], missed[1..]), // BUG: [0] not [^1]
            _ => (fresh, missed),
        };
    }

    // === DST / time-zone properties ===================================================

    [Fact]
    public void DailyCron_FiresExactlyOncePerLocalDay_AcrossAYearSpanningDst()
    {
        // For any zone and any wall-clock time-of-day, a daily cron fires exactly once on each local
        // calendar day of 2026 (365 days) — no day lost to a spring-forward gap, none double-fired by
        // a fall-back repeat. Grouping by local date makes this robust to UTC-window edge alignment.
        (from zoneIndex in Gen.Int[0, Zones.Length - 1]
         from hour in Gen.Int[0, 23]
         from minute in Gen.Int[0, 59]
         select (zoneIndex, hour, minute))
        .Sample(x =>
        {
            var zone = Zones[x.zoneIndex];
            var cron = CronExpression.Parse($"{x.minute} {x.hour} * * *");
            var cursor = Utc(2025, 12, 31);
            var stop = Utc(2027, 1, 2);
            var localDays = new HashSet<DateOnly>();
            while (ZonedCron.NextAfter(cron, cursor, zone) is { } tick && tick < stop)
            {
                cursor = tick;
                var local = TimeZoneInfo.ConvertTime(tick, zone);
                if (local.Year == 2026)
                {
                    // Add returns false on a repeat: a fall-back double-fire is caught right here.
                    Assert.True(localDays.Add(DateOnly.FromDateTime(local.DateTime)),
                        $"two fires on the same local day around {tick:O} in {zone.Id}");
                }
            }
            Assert.Equal(365, localDays.Count);
        }, seed: Seed, iter: 40);
    }

    [Fact]
    public void ZonedNextAfter_IsNeverSpuriouslyNull_AndStaysMonotonic_AcrossTransitions()
    {
        // Hourly and daily crons swept across each zone's two 2026 transitions: the sequence must be
        // strictly increasing and must never stall on a spurious null inside the DST fall-back hour.
        (from zoneIndex in Gen.Int[0, Zones.Length - 1]
         from daily in Gen.Bool
         from hour in Gen.Int[0, 23]
         from minute in Gen.Int[0, 59]
         select (zoneIndex, daily, hour, minute))
        .Sample(x =>
        {
            var zone = Zones[x.zoneIndex];
            var cron = CronExpression.Parse(x.daily ? $"{x.minute} {x.hour} * * *" : $"{x.minute} * * * *");
            foreach (var (from, to) in TransitionWindows[x.zoneIndex])
            {
                var cursor = from;
                DateTimeOffset? prev = null;
                while (ZonedCron.NextAfter(cron, cursor, zone) is { } tick && tick < to)
                {
                    Assert.True(prev is null || tick > prev, $"non-monotonic tick {tick:O} in {zone.Id}");
                    prev = tick;
                    cursor = tick;
                }
                Assert.NotNull(prev); // produced at least one tick — never a spurious null stall
            }
        }, seed: Seed, iter: 80);
    }

    [Theory]
    [InlineData(0)] // New York
    [InlineData(1)] // London
    [InlineData(2)] // Sydney
    public void EveryMinuteCron_AcrossBothTransitions_NoDoubleFire_NoNull(int zoneIndex)
    {
        // A sub-hour cron is the sharpest DST probe. Sweep the full transition windows and map each
        // UTC tick back to its wall-clock minute: a spring-forward gap minute maps once to the first
        // valid instant, and a fall-back ambiguous minute fires only its first occurrence — so every
        // wall-clock minute must appear at most once. A double-fire shows up as a repeated wall minute.
        var zone = Zones[zoneIndex];
        var cron = CronExpression.Parse("* * * * *");
        foreach (var (from, to) in TransitionWindows[zoneIndex])
        {
            var walls = new List<DateTime>();
            var cursor = from;
            DateTimeOffset? prev = null;
            while (ZonedCron.NextAfter(cron, cursor, zone) is { } tick && tick < to)
            {
                Assert.True(prev is null || tick > prev, $"non-monotonic tick {tick:O} in {zone.Id}");
                walls.Add(TimeZoneInfo.ConvertTime(tick, zone).DateTime);
                prev = tick;
                cursor = tick;
            }
            Assert.NotNull(prev);
            Assert.Equal(walls.Count, walls.Distinct().Count()); // no wall minute fires twice
        }
    }

    // === Rejection totality ===========================================================

    [Fact]
    public void Parse_RejectsInvalidExpressions_WithOnlyFormatOrArgumentException()
    {
        // Cron-shaped-but-broken strings plus arbitrary garbage. Parse may legitimately succeed for
        // some; when it fails it must fail with a defined exception, never leak an undefined type.
        Gen.OneOf(
                Gen.Int[0, 4].Select(n => string.Join(' ', Enumerable.Repeat("*", n))),          // wrong field count
                Gen.Int[60, 500].Select(v => $"{v} * * * *"),                                     // out-of-range value
                Gen.String[Gen.Char['a', 'z'], 1, 3].Select(s => $"{s} * * * *"),                 // non-numeric field
                (from lo in Gen.Int[2, 59] from d in Gen.Int[1, 1] select $"{lo}-{lo - d} * * * *"), // reversed range
                Gen.String)                                                                        // pure garbage
            .Sample(s =>
            {
                try
                {
                    CronExpression.Parse(s);
                }
                catch (FormatException)
                {
                }
                catch (ArgumentException)
                {
                }
                // Any other exception type escapes and fails the property.
            }, seed: Seed, iter: 300);
    }

    [Fact]
    public void ScheduleValidation_TryResolve_IsTotal_NeverThrows()
    {
        // The mint planner relies on TryResolve being total: no input string for cron or zone id may
        // throw — a poisoned schedule row must resolve to a clean false, never fail-stop the planner.
        (from cron in Gen.String from zoneId in Gen.String.Null() select (cron, zoneId))
            .Sample(x =>
            {
                _ = ScheduleValidation.TryResolve(x.cron, x.zoneId, out _, out _, out _);
            }, seed: Seed, iter: 300);
    }

    // === Shared builders ==============================================================

    private static List<DateTimeOffset> ResolvedTicks(string cron, DateTimeOffset cursor, DateTimeOffset now)
    {
        var expr = CronExpression.Parse(cron);
        var ticks = new List<DateTimeOffset>();
        var c = cursor;
        while (ticks.Count < MintPlanner.MaxTicksPerPoll && ZonedCron.NextAfter(expr, c, null) is { } t && t <= now)
        {
            ticks.Add(t);
            c = t;
        }
        return ticks;
    }

    private static ScheduleSnapshot Snapshot(
        string cron, DateTimeOffset cursor, CatchUpPolicy policy, bool noOverlap = false, bool live = false)
        => new(
            new ScheduleRecord
            {
                ScheduleId = "s",
                Cron = cron,
                WireName = "w",
                Payload = ReadOnlyMemory<byte>.Empty,
                Queue = "default",
                Cursor = cursor,
                CatchUp = policy,
                NoOverlap = noOverlap,
            },
            HasLiveInstance: live);
}
