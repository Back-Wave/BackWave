namespace BackWave.Sqlite.Internal;

/// <summary>
/// The pure value codec between the store's CLR types and their SQLite column
/// encodings. No DB, no state — unit-tested in isolation.
/// <list type="bullet">
/// <item><see cref="DateTimeOffset"/> ↔ <c>INTEGER</c> UTC ticks: an instant, offset dropped. The
///   encoding is monotonic in the instant, so a numeric column comparison orders chronologically —
///   which the claim/expiry/retention range predicates rely on.</item>
/// <item><see cref="Guid"/> ↔ <c>TEXT</c> in canonical lowercase "D" form, so the same id always
///   produces the same string for PRIMARY KEY / UNIQUE / FK identity.</item>
/// <item>enum ↔ <c>INTEGER</c> by underlying int value.</item>
/// </list>
/// </summary>
internal static class SqliteValueCodec
{
    /// <summary>Encodes an instant as UTC ticks (offset dropped; chronological order preserved).</summary>
    public static long ToTicks(DateTimeOffset value) => value.UtcTicks;

    /// <summary>Decodes UTC ticks back to an instant with zero offset.</summary>
    public static DateTimeOffset FromTicks(long ticks) => new(ticks, TimeSpan.Zero);

    /// <summary>Encodes a Guid as canonical lowercase "D" text.</summary>
    public static string ToText(Guid id) => id.ToString("D");

    /// <summary>Decodes canonical Guid text.</summary>
    public static Guid ToGuid(string text) => Guid.Parse(text);

    /// <summary>Encodes an enum as its underlying int.</summary>
    public static int ToInt<TEnum>(TEnum value) where TEnum : struct, Enum
        => Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Decodes an int to an enum value.</summary>
    public static TEnum ToEnum<TEnum>(long value) where TEnum : struct, Enum
        => (TEnum)Enum.ToObject(typeof(TEnum), value);
}
