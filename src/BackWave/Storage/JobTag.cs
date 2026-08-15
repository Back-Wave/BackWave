using System.Collections;

namespace BackWave.Storage;

/// <summary>
/// One job tag: an observational annotation the scheduling core never reads. A tag is structurally
/// one of two kinds, told apart by <see cref="Key"/> — never by parsing a separator. A <b>label</b>
/// is a bare string and has the empty string as its <see cref="Key"/>; a <b>keyed tag</b> has a
/// non-empty <see cref="Key"/> carrying a <see cref="Value"/>. A value is never empty, so an empty
/// key unambiguously identifies a label, and a colon inside a label is ordinary data rather than a
/// separator.
/// </summary>
public sealed record JobTag
{
    /// <summary>The dimension of a keyed tag, or the empty string for a label. This is what distinguishes the two kinds.</summary>
    public string Key { get; }

    /// <summary>The label text (for a label) or the dimension's value (for a keyed tag). Never empty.</summary>
    public string Value { get; }

    private JobTag(string key, string value)
    {
        Key = key;
        Value = value;
    }

    /// <summary>True when this is a label, identified by an empty <see cref="Key"/>.</summary>
    public bool IsLabel => Key.Length == 0;

    /// <summary>
    /// Creates a label: a bare string tag. A colon inside the text is ordinary data, never a separator.
    /// </summary>
    /// <param name="value">The label text. Must be non-empty.</param>
    /// <returns>A label tag carrying <paramref name="value"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="value"/> is null or empty.</exception>
    public static JobTag Label(string value) => new(string.Empty, RequireValue(value));

    /// <summary>
    /// Creates a keyed tag: a non-empty key carrying a string value.
    /// </summary>
    /// <param name="key">The tag's dimension. Must be non-empty.</param>
    /// <param name="value">The value under that dimension. Must be non-empty.</param>
    /// <returns>A keyed tag pairing <paramref name="key"/> with <paramref name="value"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="key"/> is null or empty, or <paramref name="value"/> is null or empty.</exception>
    public static JobTag Keyed(string key, string value) => new(RequireKey(key), RequireValue(value));

    private static string RequireValue(string value)
        => string.IsNullOrEmpty(value)
            ? throw new ArgumentException("A Job Tag value must be non-empty.", nameof(value))
            : value;

    private static string RequireKey(string key)
        => string.IsNullOrEmpty(key)
            ? throw new ArgumentException(
                "A Keyed Tag key must be non-empty; use JobTag.Label for a bare label.", nameof(key))
            : key;
}

/// <summary>
/// A job's tags as a set: re-adding an identical tag is a no-op, iteration is in first-seen order,
/// and equality is set equality (order-independent). Built fluently with intent-declaring methods so
/// a colon is never parsed — use <see cref="WithLabel"/> for a label and <see cref="WithTag"/> for a
/// keyed tag.
/// </summary>
public sealed class JobTags : IReadOnlyList<JobTag>, IEquatable<JobTags>
{
    /// <summary>The empty tag set, used as the default for an untagged job.</summary>
    public static readonly JobTags Empty = new([]);

    private readonly JobTag[] _tags;

    private JobTags(JobTag[] tags) => _tags = tags;

    /// <summary>The number of tags in the set.</summary>
    public int Count => _tags.Length;

    /// <summary>The tag at <paramref name="index"/> in first-seen order.</summary>
    /// <param name="index">The zero-based position.</param>
    /// <returns>The tag at that position.</returns>
    /// <exception cref="IndexOutOfRangeException"><paramref name="index"/> is outside the set.</exception>
    public JobTag this[int index] => _tags[index];

    /// <summary>Returns this set plus a label, collapsing an identical tag already present.</summary>
    /// <param name="value">The label text. Must be non-empty.</param>
    /// <returns>A new set including the label; the same contents if it was already present.</returns>
    /// <exception cref="ArgumentException"><paramref name="value"/> is null or empty.</exception>
    public JobTags WithLabel(string value) => With(JobTag.Label(value));

    /// <summary>Returns this set plus a keyed tag, collapsing an identical tag already present.</summary>
    /// <param name="key">The tag's dimension. Must be non-empty.</param>
    /// <param name="value">The value under that dimension. Must be non-empty.</param>
    /// <returns>A new set including the keyed tag; the same contents if it was already present.</returns>
    /// <exception cref="ArgumentException"><paramref name="key"/> or <paramref name="value"/> is null or empty.</exception>
    public JobTags WithTag(string key, string value) => With(JobTag.Keyed(key, value));

    /// <summary>Returns this set plus <paramref name="tag"/>; a no-op when the tag is already present.</summary>
    /// <param name="tag">The tag to add.</param>
    /// <returns>A new set including the tag, or the same set when it was already present.</returns>
    public JobTags With(JobTag tag) => Contains(tag) ? this : new([.. _tags, tag]);

    /// <summary>Builds a set from <paramref name="tags"/>, collapsing duplicates and preserving first-seen order.</summary>
    /// <param name="tags">The tags to include.</param>
    /// <returns>A set containing each distinct tag, in the order first encountered.</returns>
    public static JobTags From(IEnumerable<JobTag> tags)
    {
        var result = Empty;
        foreach (var tag in tags)
        {
            result = result.With(tag);
        }
        return result;
    }

    /// <summary>Whether <paramref name="tag"/> is in the set.</summary>
    /// <param name="tag">The tag to look for.</param>
    /// <returns>True when the set already contains an equal tag.</returns>
    public bool Contains(JobTag tag) => Array.IndexOf(_tags, tag) >= 0;

    /// <inheritdoc/>
    public IEnumerator<JobTag> GetEnumerator() => ((IEnumerable<JobTag>)_tags).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => _tags.GetEnumerator();

    /// <summary>Set equality: true when both sets hold the same tags, regardless of order.</summary>
    /// <param name="other">The set to compare with.</param>
    /// <returns>True when the two sets contain exactly the same tags.</returns>
    public bool Equals(JobTags? other)
        => other is not null
            && _tags.Length == other._tags.Length
            && Array.TrueForAll(_tags, other.Contains);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as JobTags);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        // Order-independent so set-equal instances hash equally: XOR of element hashes.
        var hash = 0;
        foreach (var tag in _tags)
        {
            hash ^= tag.GetHashCode();
        }
        return hash;
    }
}
