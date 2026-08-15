using BackWave.Sqlite.Internal;
using BackWave.Storage;

namespace BackWave.Sqlite.Tests;

/// <summary>Unit tests for the pure value codec — no DB (issue 0092).</summary>
public sealed class SqliteValueCodecTests
{
    [Fact]
    public void UtcTicks_round_trips_the_instant_dropping_offset()
    {
        var instant = new DateTimeOffset(2026, 6, 18, 13, 30, 45, TimeSpan.FromHours(5));

        var decoded = SqliteValueCodec.FromTicks(SqliteValueCodec.ToTicks(instant));

        // Same instant in UTC; the original +05:00 offset is intentionally dropped.
        Assert.Equal(instant.UtcDateTime, decoded.UtcDateTime);
        Assert.Equal(TimeSpan.Zero, decoded.Offset);
    }

    [Fact]
    public void UtcTicks_encoding_preserves_chronological_order()
    {
        // A spread of instants in assorted offsets, deliberately out of chronological order.
        var instants = new[]
        {
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2025, 12, 31, 23, 0, 0, TimeSpan.FromHours(-5)),
            new DateTimeOffset(2026, 6, 18, 12, 0, 0, TimeSpan.FromHours(9)),
            new DateTimeOffset(2024, 2, 29, 6, 30, 0, TimeSpan.FromHours(2)),
        };

        var byInstant = instants.OrderBy(i => i.UtcDateTime).ToArray();
        var byTicks = instants.OrderBy(SqliteValueCodec.ToTicks).ToArray();

        Assert.Equal(byInstant, byTicks);
    }

    [Fact]
    public void Guid_round_trips_through_canonical_lowercase_text()
    {
        var id = Guid.NewGuid();

        var text = SqliteValueCodec.ToText(id);

        Assert.Equal(id.ToString("D"), text);
        Assert.Equal(text, text.ToLowerInvariant());
        Assert.Equal(id, SqliteValueCodec.ToGuid(text));
    }

    [Fact]
    public void Enum_round_trips_through_int()
    {
        Assert.Equal((int)JobState.Quarantined, SqliteValueCodec.ToInt(JobState.Quarantined));
        Assert.Equal(JobState.Leased, SqliteValueCodec.ToEnum<JobState>(SqliteValueCodec.ToInt(JobState.Leased)));
        Assert.Equal(DependencyMode.OnAnyTerminal, SqliteValueCodec.ToEnum<DependencyMode>(SqliteValueCodec.ToInt(DependencyMode.OnAnyTerminal)));
    }
}
