using BackWave.Diagnostics;
using BackWave.Storage;

namespace BackWave.Tests;

// The audit detail's text is a storage wire format: every SQL adapter writes and reads it through the same
// codec, so the conformance suite cannot see a change to it. This test is the asymmetric side: it holds the
// text that rows already in customer databases were written with.

public class OperatorAuditDetailWireFormatTests
{
    private const string Pinned = """{"from":"2026-01-01T01:00:00.0000000Z","to":"2026-01-01T03:30:00.1234567Z"}""";

    private static readonly RescheduleAuditDetail Detail = new(
        new DateTimeOffset(2026, 1, 1, 1, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 1, 1, 3, 30, 0, TimeSpan.Zero).AddTicks(1_234_567));

    [Fact]
    public void A_reschedule_encodes_to_the_pinned_text_in_utc()
    {
        var offsetFrom = Detail.From.ToOffset(TimeSpan.FromHours(2));

        Assert.Equal(Pinned, OperatorAuditDetailCodec.Encode(Detail with { From = offsetFrom }));
    }

    [Fact]
    public void The_pinned_text_decodes_to_the_same_instants()
    {
        Assert.Equal(Detail, OperatorAuditDetailCodec.Decode(OperatorAction.Reschedule, "job", Pinned));
    }

    [Fact]
    public void No_detail_encodes_and_decodes_as_null()
    {
        Assert.Null(OperatorAuditDetailCodec.Encode(null));
        Assert.Null(OperatorAuditDetailCodec.Decode(OperatorAction.Reschedule, "job", null));
    }

    [Fact]
    public void Text_on_an_action_that_keeps_no_detail_reads_as_none()
    {
        // A newer node may add a detail to another action; a node one version behind still reads the trail.
        Assert.Null(OperatorAuditDetailCodec.Decode(OperatorAction.Cancel, "job", Pinned));
    }

    [Fact]
    public void Extra_properties_are_ignored()
    {
        var withExtra = """{"from":"2026-01-01T01:00:00.0000000Z","to":"2026-01-01T03:30:00.1234567Z","by":"x"}""";

        Assert.Equal(Detail, OperatorAuditDetailCodec.Decode(OperatorAction.Reschedule, "job", withExtra));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"from":"2026-01-01T01:00:00.0000000Z"}""")]
    [InlineData("""{"from":"2026-01-01T01:00:00.0000000Z","to":42}""")]
    [InlineData("""{"from":"yesterday","to":"2026-01-01T03:30:00.1234567Z"}""")]
    [InlineData("""{"from":"2026-01-01T01:00:00.0000000","to":"2026-01-01T03:30:00.1234567Z"}""")]
    public void A_malformed_reschedule_detail_halts_the_read(string stored)
    {
        var violation = Assert.Throws<InvariantViolationException>(
            () => OperatorAuditDetailCodec.Decode(OperatorAction.Reschedule, "job", stored));

        Assert.Equal(InvariantTrigger.MalformedAuditDetailStored, violation.Trigger);
    }
}
