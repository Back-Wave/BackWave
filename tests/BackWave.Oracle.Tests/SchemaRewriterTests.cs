namespace BackWave.Oracle.Tests;

/// <summary>
/// Unit coverage for the schema-name rewrite/validation choke point: the construction-time validation
/// boundaries and the default-vs-custom <c>Rewrite</c> substitution. Pure unit tests - no database.
/// </summary>
public sealed class SchemaRewriterTests
{
    // Oracle identifiers cap at 128 characters (mirrors SchemaRewriter.MaxSchemaLength).
    private const int MaxSchemaLength = 128;

    [Fact]
    public void DefaultSchema_Rewrite_IsIdentityPassthrough()
    {
        var rewriter = new SchemaRewriter("backwave");
        const string sql = "SELECT * FROM backwave.jobs WHERE backwave.jobs.state = 0";
        Assert.Same(sql, rewriter.Rewrite(sql)); // the same reference, not merely an equal string
    }

    [Fact]
    public void CustomSchema_Rewrite_SubstitutesTheQualifier()
    {
        var rewriter = new SchemaRewriter("bw_alt");
        Assert.Equal("SELECT * FROM bw_alt.jobs", rewriter.Rewrite("SELECT * FROM backwave.jobs"));
    }

    [Fact]
    public void MaxLengthName_IsAccepted_AndRewrites()
    {
        var name = new string('a', MaxSchemaLength); // exactly at the cap - accepted
        var rewriter = new SchemaRewriter(name);
        Assert.Equal($"{name}.jobs", rewriter.Rewrite("backwave.jobs"));
    }

    [Fact]
    public void HintAlertName_DefaultSchema_IsBackwaveHints()
    {
        var rewriter = new SchemaRewriter("backwave");
        Assert.Equal("backwave_hints", rewriter.HintAlertName);
    }

    [Fact]
    public void HintAlertName_CustomShortSchema_AppendsHintsSuffix()
    {
        var rewriter = new SchemaRewriter("bw_alt");
        Assert.Equal("bw_alt_hints", rewriter.HintAlertName);
    }

    [Fact]
    public void HintAlertName_LongSchema_TruncatesToFitThirtyChars()
    {
        // DBMS_ALERT stores the alert name in a VARCHAR2(30) column, so the derived name must fit 30 chars.
        var rewriter = new SchemaRewriter(new string('a', MaxSchemaLength));
        Assert.True(rewriter.HintAlertName.Length <= 30, "the derived alert name must fit the VARCHAR2(30) column");
        Assert.EndsWith("_hints", rewriter.HintAlertName);
        Assert.Equal(new string('a', 24) + "_hints", rewriter.HintAlertName);
    }

    [Theory]
    [InlineData("")]          // empty
    [InlineData("has space")] // invalid character
    [InlineData("1leading")]  // leading digit
    public void InvalidName_IsRejected(string name)
        => Assert.Throws<ArgumentException>(() => new SchemaRewriter(name));

    [Fact]
    public void OverLengthName_IsRejected_EvenWhenOtherwiseValid()
    {
        // One past the cap and a valid identifier otherwise: only the length rule rejects it.
        var name = new string('a', MaxSchemaLength + 1);
        Assert.Throws<ArgumentException>(() => new SchemaRewriter(name));
    }
}
