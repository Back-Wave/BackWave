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
