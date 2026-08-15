using BackWave.Sqlite.Internal;
using Microsoft.Data.Sqlite;

namespace BackWave.Sqlite.Tests;

/// <summary>Unit tests for the pure connection-string normalizer — no DB (issue 0092).</summary>
public sealed class SqliteConnectionStringNormalizerTests
{
    [Fact]
    public void Forces_foreign_keys_on_even_when_the_user_turned_them_off()
    {
        var normalized = SqliteConnectionStringNormalizer.Normalize(
            "Data Source=app.db;Foreign Keys=False", TimeSpan.FromSeconds(5));

        var builder = new SqliteConnectionStringBuilder(normalized);
        Assert.True(builder.ForeignKeys);
    }

    [Fact]
    public void Forces_default_timeout_from_busy_timeout_and_pool_only()
    {
        var normalized = SqliteConnectionStringNormalizer.Normalize(
            "Data Source=app.db;Default Timeout=1;Pooling=False", TimeSpan.FromSeconds(30));

        var builder = new SqliteConnectionStringBuilder(normalized);
        Assert.Equal(30, builder.DefaultTimeout);
        Assert.True(builder.Pooling);
    }

    [Fact]
    public void Sub_second_busy_timeout_clamps_up_to_one_second()
    {
        var normalized = SqliteConnectionStringNormalizer.Normalize(
            "Data Source=app.db", TimeSpan.FromMilliseconds(200));

        var builder = new SqliteConnectionStringBuilder(normalized);
        Assert.Equal(1, builder.DefaultTimeout);
    }

    [Fact]
    public void Preserves_the_data_source()
    {
        var normalized = SqliteConnectionStringNormalizer.Normalize(
            "Data Source=/tmp/some/app.db", TimeSpan.FromSeconds(5));

        var builder = new SqliteConnectionStringBuilder(normalized);
        Assert.Equal("/tmp/some/app.db", builder.DataSource);
    }
}
