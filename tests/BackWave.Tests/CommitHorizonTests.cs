using BackWave.Storage;

namespace BackWave.Tests;

public class CommitHorizonTests
{
    [Theory]
    [InlineData(0, 40L, 10L, 50L, 50L)]
    [InlineData(1, null, 10L, 50L, 50L)]
    [InlineData(0, 40L, 60L, 50L, 60L)]
    public void BothEpochsFree_MoveTheHorizonUpToP_AndKeepTheEpochAndCandidate(
        int epoch, long? candidate, long horizon, long maxPosition, long expectedHorizon)
    {
        var state = new CommitHorizon(epoch, candidate, horizon);

        var next = state.Advance(maxPosition, previousEpochFree: true, currentEpochFree: true);

        Assert.Equal(new CommitHorizon(epoch, candidate, expectedHorizon), next);
    }

    [Theory]
    [InlineData(0, 40L, 10L, 50L, 1, 40L)]
    [InlineData(1, 40L, 10L, 50L, 0, 40L)]
    [InlineData(0, null, 10L, 50L, 1, 10L)]
    [InlineData(0, 40L, 60L, 50L, 1, 60L)]
    public void OnlyThePreviousEpochFree_SettlesTheCandidate_AndChangesTheEpoch(
        int epoch, long? candidate, long horizon, long maxPosition, int expectedEpoch, long expectedHorizon)
    {
        var state = new CommitHorizon(epoch, candidate, horizon);

        var next = state.Advance(maxPosition, previousEpochFree: true, currentEpochFree: false);

        Assert.Equal(new CommitHorizon(expectedEpoch, maxPosition, expectedHorizon), next);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void APreviousEpochThatIsStillBusy_ChangesNothing(bool previousEpochFree, bool currentEpochFree)
    {
        var state = new CommitHorizon(1, 40L, 10L);

        Assert.Equal(state, state.Advance(50L, previousEpochFree, currentEpochFree));
    }
}
