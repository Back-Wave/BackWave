using System.Transactions;
using BackWave.Storage;

namespace BackWave.Postgres.Tests;

/// <summary>
/// A plain enqueue inside an ambient <see cref="TransactionScope"/> throws, as it always has: the store
/// does not enlist a job in a scope it cannot commit with, so a scope rollback can never erase a job the
/// store already reported Ok and hinted.
/// </summary>
[Collection("postgres")]
public sealed class PostgresAmbientTransactionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_plain_enqueue_inside_an_ambient_transaction_scope_throws_and_writes_nothing()
    {
        await using var store = await PostgresTestDatabase.CreateFreshStoreAsync();
        var job = new NewJob(Guid.NewGuid(), "ambient", "{}"u8.ToArray(), "ambient", T0);

        using (new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
        {
            var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await store.EnqueueAsync(job, T0));
            Assert.Contains("A transaction is already in progress", thrown.Message);
        }

        Assert.Null(await store.GetJobAsync(job.JobId));
    }
}
