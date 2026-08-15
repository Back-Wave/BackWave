using BackWave.Core;
using BackWave.Driver;
using BackWave.Storage;

namespace BackWave.Tests;

/// <summary>
/// The re-poll decision lives in the Driver (issue 0042), not in either pump: an applied
/// outcome or a productive mint emits a <see cref="Command.RequestPoll"/>. This guards the
/// two pumps from diverging on "should I poll again now?" — both only execute the command.
/// </summary>
public class NodeDriverRePollTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static NodeDriver Driver() => new(new NodeOptions
    {
        WorkerId = "w1",
        Policy = new DispatchPolicy.Strict(["default"]),
    });

    [Fact]
    public void AnAppliedOutcome_EmitsARePoll_AtTheOutcomeInstant()
    {
        var commands = Driver().Step(new NodeEvent.OutcomeReported(Guid.NewGuid(), OutcomeResult.Applied, T0));
        var repoll = Assert.IsType<Command.RequestPoll>(Assert.Single(commands));
        Assert.Equal(T0, repoll.Now);
    }

    [Fact]
    public void AStaleOutcome_EmitsNoRePoll()
        => Assert.Empty(Driver().Step(new NodeEvent.OutcomeReported(Guid.NewGuid(), OutcomeResult.StaleLease, T0)));

    [Fact]
    public void AProductiveMint_EmitsARePoll_ABarrenMintDoesNot()
    {
        Assert.IsType<Command.RequestPoll>(Assert.Single(Driver().Step(new NodeEvent.MintCompleted(3, T0))));
        Assert.Empty(Driver().Step(new NodeEvent.MintCompleted(0, T0)));
    }
}
