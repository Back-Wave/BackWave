using BackWave.Monitor;
using BackWave.Storage;
using BackWave.Storage.InMemory;

namespace BackWave.Tests;

public sealed class PumpStatusTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static PumpStatus Status(PumpState state, int intervalSeconds = 30, string workerGroup = "workers", string workerId = "host:workers:a") => new()
    {
        WorkerId = workerId,
        WorkerGroup = workerGroup,
        State = state,
        ReportedAt = T0,
        ReportInterval = TimeSpan.FromSeconds(intervalSeconds),
    };

    [Theory]
    [InlineData(PumpState.Running, 30, 90_000, false, false)]
    [InlineData(PumpState.Running, 30, 90_001, true, false)]
    [InlineData(PumpState.Degraded, 30, 90_001, true, false)]
    [InlineData(PumpState.Halted, 30, 90_001, true, false)]
    [InlineData(PumpState.Unknown, 30, 90_001, true, false)]
    [InlineData(PumpState.Running, 60, 120_001, false, false)]
    [InlineData(PumpState.Running, 60, 180_001, true, false)]
    [InlineData(PumpState.Stopping, 30, 90_000, false, false)]
    [InlineData(PumpState.Stopping, 30, 90_001, false, true)]
    [InlineData(PumpState.Stopped, 30, 0, false, true)]
    [InlineData(PumpState.Stopped, 30, 36_000_000, false, true)]
    public void TheReaderVerdict_FollowsThreeMissedReportsOfTheRowsOwnInterval(
        PumpState state, int intervalSeconds, int ageMilliseconds, bool stale, bool stopped)
    {
        var status = Status(state, intervalSeconds);
        var now = T0.AddMilliseconds(ageMilliseconds);

        Assert.Equal(stale, status.IsStaleAt(now));
        Assert.Equal(stopped, status.IsStoppedAt(now));
    }

    [Fact]
    public void PumpState_PersistedValues_ArePinned()
    {
        Assert.Equal(-1, (int)PumpState.Unknown);
        Assert.Equal(0, (int)PumpState.Running);
        Assert.Equal(1, (int)PumpState.Degraded);
        Assert.Equal(2, (int)PumpState.Halted);
        Assert.Equal(3, (int)PumpState.Stopping);
        Assert.Equal(4, (int)PumpState.Stopped);
    }

    [Fact]
    public async Task Monitor_ListsPumpStatuses_ByWorkerGroupThenWorkerId_Ordinal()
    {
        var store = new InMemoryJobStore();
        await store.WritePumpStatusesAsync(
            [
                Status(PumpState.Running, workerGroup: "b", workerId: "host:b:1"),
                Status(PumpState.Running, workerGroup: "a", workerId: "host:a:b"),
                Status(PumpState.Running, workerGroup: "a", workerId: "host:a:B"),
            ],
            reapBefore: T0.AddHours(-1), maxReap: 100);

        var listed = await new BackWaveMonitor(store).ListPumpStatusesAsync();

        Assert.Equal(["host:a:B", "host:a:b", "host:b:1"], listed.Select(s => s.WorkerId));
    }
}
