using BackWave.Benchmarks.ScaleOut;

namespace BackWave.Benchmarks.Tests;

/// <summary>
/// The scale-out sweep axis is parsed from a comma list; these pin its shape so the swept node counts are
/// reproducible and a bad axis (empty, zero, non-numeric) fails loudly instead of charting nonsense.
/// </summary>
public sealed class NodeCountListTests
{
    [Fact]
    public void Parses_the_canonical_curve_in_order()
    {
        Assert.Equal(new[] { 1, 2, 4, 8 }, NodeCountList.Parse("1,2,4,8"));
    }

    [Fact]
    public void Preserves_the_order_given_rather_than_sorting()
    {
        Assert.Equal(new[] { 8, 4, 2, 1 }, NodeCountList.Parse("8,4,2,1"));
    }

    [Fact]
    public void Ignores_whitespace_and_a_trailing_comma()
    {
        Assert.Equal(new[] { 1, 2, 4 }, NodeCountList.Parse(" 1, 2 ,4, "));
    }

    [Fact]
    public void A_single_count_is_a_valid_curve()
    {
        Assert.Equal(new[] { 4 }, NodeCountList.Parse("4"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" , ")]
    public void Rejects_an_empty_list(string raw)
    {
        Assert.Throws<ArgumentException>(() => NodeCountList.Parse(raw));
    }

    [Theory]
    [InlineData("1,0,2")]
    [InlineData("1,-2")]
    [InlineData("1,two")]
    public void Rejects_a_non_positive_or_non_numeric_entry(string raw)
    {
        Assert.Throws<ArgumentException>(() => NodeCountList.Parse(raw));
    }
}
