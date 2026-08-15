using BackWave.Core;
using BackWave.Driver;
using BackWave.Storage;

namespace BackWave.Tests;

/// <summary>
/// The poll/maintenance cadence split (issue 0039) lives in the Driver: every poll claims,
/// but lease expiry, schedule load/mint, and retention purges fire only once per
/// MaintenanceInterval. A zero interval keeps the historical "sweep every poll" behaviour.
/// </summary>
public class NodeDriverCadenceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static NodeDriver Driver(TimeSpan maintenanceInterval) => new(new NodeOptions
    {
        WorkerId = "w1",
        Policy = new DispatchPolicy.Strict(["default"]),
        MaintenanceInterval = maintenanceInterval,
    });

    [Fact]
    public void TheFirstPoll_AlwaysSweepsAndClaims()
    {
        var commands = Driver(TimeSpan.FromSeconds(10)).Step(new NodeEvent.PollDue(T0));

        Assert.Contains(commands, c => c is Command.ExpireLeases);
        Assert.Contains(commands, c => c is Command.LoadSchedules);
        Assert.Contains(commands, c => c is Command.ClaimBatch);
    }

    [Fact]
    public void APollWithinTheMaintenanceInterval_ClaimsOnly_NoSweep()
    {
        var driver = Driver(TimeSpan.FromSeconds(10));
        driver.Step(new NodeEvent.PollDue(T0)); // first poll sweeps

        var commands = driver.Step(new NodeEvent.PollDue(T0.AddSeconds(3))); // still inside the interval

        // The hint/re-poll fast path: a single claim, no maintenance round-trips.
        Assert.IsType<Command.ClaimBatch>(Assert.Single(commands));
    }

    [Fact]
    public void APollPastTheMaintenanceInterval_SweepsAgain()
    {
        var driver = Driver(TimeSpan.FromSeconds(10));
        driver.Step(new NodeEvent.PollDue(T0));

        var commands = driver.Step(new NodeEvent.PollDue(T0.AddSeconds(10))); // interval elapsed

        Assert.Contains(commands, c => c is Command.ExpireLeases);
        Assert.Contains(commands, c => c is Command.LoadSchedules);
    }

    [Fact]
    public void AZeroInterval_SweepsOnEveryPoll_TheHistoricalBehaviour()
    {
        var driver = Driver(TimeSpan.Zero);
        driver.Step(new NodeEvent.PollDue(T0));

        var commands = driver.Step(new NodeEvent.PollDue(T0)); // even a same-instant re-poll

        Assert.Contains(commands, c => c is Command.ExpireLeases);
        Assert.Contains(commands, c => c is Command.LoadSchedules);
    }

    [Fact]
    public void MintPlanner_WithACache_ParsesEachCronOncePerVersion_NotPerPoll()
    {
        var cache = new CronCache();
        ScheduleSnapshot[] schedules =
        [
            new(ScheduleRow("hourly", "0 * * * *"), HasLiveInstance: false),
            new(ScheduleRow("daily", "0 2 * * *"), HasLiveInstance: false),
        ];

        for (var poll = 0; poll < 50; poll++)
        {
            MintPlanner.Plan(schedules, T0.AddMinutes(poll), cronCache: cache);
        }

        Assert.Equal(2, cache.ParseCount); // two distinct crons, parsed once each across 50 polls
    }

    [Fact]
    public void CronCache_ResolvesRepeatedKeysFromMemory_CountingDistinctParses()
    {
        var cache = new CronCache();

        Assert.True(cache.TryResolve("0 * * * *", null, out _, out _, out _));
        Assert.True(cache.TryResolve("0 * * * *", null, out _, out _, out _));               // hit
        Assert.True(cache.TryResolve("0 * * * *", "America/New_York", out _, out _, out _));  // new key (zone)
        Assert.False(cache.TryResolve("nonsense", null, out _, out _, out var error));        // cached failure too

        Assert.NotNull(error);
        Assert.Equal(3, cache.ParseCount); // two distinct valid keys + one invalid, each parsed once
    }

    private static ScheduleRecord ScheduleRow(string id, string cron) => new()
    {
        ScheduleId = id,
        Cron = CronExpression.Parse(cron).Canonical,
        WireName = "w",
        Payload = ReadOnlyMemory<byte>.Empty,
        Queue = "default",
        Cursor = T0,
    };
}
