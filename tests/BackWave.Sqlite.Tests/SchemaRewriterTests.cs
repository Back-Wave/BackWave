namespace BackWave.Sqlite.Tests;

/// <summary>
/// Unit coverage for the table-prefix rewrite/validation choke point (ADR 0040): the construction-time
/// validation boundaries and the default-vs-custom <c>Rewrite</c> substitution. SQLite has no schemas,
/// so the choke point swaps the canonical <c>backwave</c> table-name root for the configured prefix.
/// Pure unit tests — no database.
/// </summary>
public sealed class SchemaRewriterTests
{
    // Mirrors SchemaRewriter.MaxPrefixLength.
    private const int MaxPrefixLength = 64;

    [Fact]
    public void DefaultPrefix_Rewrite_IsIdentityPassthrough()
    {
        var rewriter = new SchemaRewriter("backwave");
        const string sql = "SELECT * FROM backwave_jobs WHERE backwave_jobs.state = 0";
        Assert.Same(sql, rewriter.Rewrite(sql)); // the same reference, not merely an equal string
    }

    [Fact]
    public void CustomPrefix_Rewrite_SubstitutesTheRoot()
    {
        var rewriter = new SchemaRewriter("bw_alt");
        Assert.Equal("SELECT * FROM bw_alt_jobs", rewriter.Rewrite("SELECT * FROM backwave_jobs"));
    }

    [Fact]
    public void MaxLengthPrefix_IsAccepted_AndRewrites()
    {
        var prefix = new string('a', MaxPrefixLength); // exactly at the cap — accepted
        var rewriter = new SchemaRewriter(prefix);
        Assert.Equal($"{prefix}_jobs", rewriter.Rewrite("backwave_jobs"));
    }

    [Theory]
    [InlineData("")]          // empty
    [InlineData("has space")] // invalid character
    [InlineData("1leading")]  // leading digit
    public void InvalidPrefix_IsRejected(string prefix)
        => Assert.Throws<ArgumentException>(() => new SchemaRewriter(prefix));

    [Fact]
    public void OverLengthPrefix_IsRejected_EvenWhenOtherwiseValid()
    {
        // One past the cap and a valid identifier otherwise: only the length rule rejects it.
        var prefix = new string('a', MaxPrefixLength + 1);
        Assert.Throws<ArgumentException>(() => new SchemaRewriter(prefix));
    }
}
