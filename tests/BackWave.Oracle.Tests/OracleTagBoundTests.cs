using BackWave.Storage;

namespace BackWave.Oracle.Tests;

/// <summary>
/// The job_tags columns are VARCHAR2(256 CHAR), so the tag bounds can only be tightened on this
/// adapter: a wider bound is rejected when the store is created rather than failing the insert with
/// ORA-12899 later. No database is needed; the guard runs in the constructor.
/// </summary>
public sealed class OracleTagBoundTests
{
    private static OracleJobStore Store(StoreBounds bounds) => new(new OracleStoreOptions
    {
        ConnectionString = OracleTestDatabase.ConnectionString,
        Bounds = bounds,
    });

    [Fact]
    public void TagKeyBoundWiderThanColumn_IsRejectedWhenTheStoreIsCreated()
    {
        var exception = Assert.Throws<ArgumentException>(() => Store(new StoreBounds { MaxTagKeyLength = 257 }));
        Assert.Contains("MaxTagKeyLength", exception.Message);
        Assert.Contains("VARCHAR2(256 CHAR)", exception.Message);
    }

    [Fact]
    public void TagValueBoundWiderThanColumn_IsRejectedWhenTheStoreIsCreated()
    {
        var exception = Assert.Throws<ArgumentException>(() => Store(new StoreBounds { MaxTagValueLength = 257 }));
        Assert.Contains("MaxTagValueLength", exception.Message);
    }

    [Fact]
    public void TagBoundsAtOrBelowColumnWidth_AreAccepted()
    {
        var store = Store(new StoreBounds { MaxTagKeyLength = 256, MaxTagValueLength = 200 });
        Assert.Equal(256, store.Bounds.MaxTagKeyLength);
        Assert.Equal(200, store.Bounds.MaxTagValueLength);
    }
}
