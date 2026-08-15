using System.Text;
using BackWave.Storage;

namespace BackWave.Tests;

public class StoreBoundsTests
{
    [Fact]
    public void ClampFailureDetail_PassesThroughNullAndWithinCap()
    {
        var bounds = new StoreBounds { MaxFailureDetailBytes = 5 };

        Assert.Null(bounds.ClampFailureDetail(null));
        Assert.Equal("abcde", bounds.ClampFailureDetail("abcde")); // exactly at the cap: kept whole
        Assert.Equal("abc", bounds.ClampFailureDetail("abc"));
    }

    [Fact]
    public void ClampFailureDetail_TruncatesOnACompleteCodePoint_NeverMidCharacter()
    {
        // "ééé" is three 2-byte code points (0xC3 0xA9 each) = 6 UTF-8 bytes. A 5-byte cap lands the
        // cut in the middle of the third 'é'. The clamp must back up off the continuation byte and
        // return the two whole characters — not a byte-exact slice that splits the code point (which
        // would decode to a U+FFFD replacement char) and not an over-eager back-up to empty.
        var bounds = new StoreBounds { MaxFailureDetailBytes = 5 };

        var clamped = bounds.ClampFailureDetail("ééé");

        Assert.Equal("éé", clamped);
        Assert.DoesNotContain('�', clamped!);
        Assert.True(Encoding.UTF8.GetByteCount(clamped!) <= bounds.MaxFailureDetailBytes);
    }
}
