using BackWave.Storage;

namespace BackWave.SqlServer.Tests;

/// <summary>
/// The job_tags columns are nvarchar(200), so the tag bounds can only be tightened on this adapter:
/// a wider bound is rejected when the store is created rather than clipped by a sized bind later.
/// No database is needed; the guard runs in the constructor.
/// </summary>
public sealed class SqlServerTagBoundTests
{
    private static SqlServerJobStore Store(StoreBounds bounds) => new(new SqlServerStoreOptions
    {
        ConnectionString = SqlServerTestDatabase.ConnectionString,
        Bounds = bounds,
    });

    [Fact]
    public void TagKeyBoundWiderThanColumn_IsRejectedWhenTheStoreIsCreated()
    {
        var exception = Assert.Throws<ArgumentException>(() => Store(new StoreBounds { MaxTagKeyLength = 201 }));
        Assert.Contains("MaxTagKeyLength", exception.Message);
        Assert.Contains("nvarchar(200)", exception.Message);
    }

    [Fact]
    public void TagValueBoundWiderThanColumn_IsRejectedWhenTheStoreIsCreated()
    {
        var exception = Assert.Throws<ArgumentException>(() => Store(new StoreBounds { MaxTagValueLength = 201 }));
        Assert.Contains("MaxTagValueLength", exception.Message);
    }

    [Fact]
    public void TagBoundsAtOrBelowColumnWidth_AreAccepted()
    {
        var store = Store(new StoreBounds { MaxTagKeyLength = 200, MaxTagValueLength = 50 });
        Assert.Equal(200, store.Bounds.MaxTagKeyLength);
        Assert.Equal(50, store.Bounds.MaxTagValueLength);
    }
}
