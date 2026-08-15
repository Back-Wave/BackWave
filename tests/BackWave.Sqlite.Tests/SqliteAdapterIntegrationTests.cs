using BackWave.Sqlite.Internal;
using BackWave.Storage;
using Microsoft.Data.Sqlite;

namespace BackWave.Sqlite.Tests;

/// <summary>
/// Adapter-level integration tests for the two SQLite-specific seams wired into
/// <see cref="SqliteJobStore"/>: the co-resident same-file guard (issue 0095) and the in-process
/// Wake-Up Hint hub (issue 0097). The deep modules behind them are unit-tested in isolation
/// (<see cref="SqliteSameFileGuardTests"/>, <see cref="WakeUpHintHubTests"/>); these prove the store
/// invokes them at the right moments against a real temp-file store.
/// </summary>
public sealed class SqliteAdapterIntegrationTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static string NormalizedConnectionString(string path)
        => SqliteConnectionStringNormalizer.Normalize($"Data Source={path}", TimeSpan.FromSeconds(5));

    // ── 0095: same-file guard fires through the adapter ──────────────────────────

    [Fact]
    public async Task Transactional_enqueue_on_a_different_file_throws_naming_both_paths()
    {
        await using var temp = TempSqliteStore.Create();
        var otherPath = Path.Combine(Path.GetTempPath(), $"backwave_sqlite_other_{Guid.NewGuid():N}.db");

        // A caller connection attached to a DIFFERENT file than BackWave is configured with: the job
        // would commit invisibly into the wrong database, so the guard must refuse loudly at wire-up.
        await using var caller = new SqliteConnection(NormalizedConnectionString(otherPath));
        await caller.OpenAsync();
        await using var transaction = caller.BeginTransaction(deferred: true);

        var job = new NewJob(Guid.NewGuid(), "demo", new byte[] { 1 }, "default", T0);
        var exception = await Assert.ThrowsAsync<SqliteSameFileMismatchException>(
            async () => await temp.Store.EnqueueAsync(job, T0, transaction));

        Assert.Contains(Path.GetFileNameWithoutExtension(temp.Path), exception.Message);
        Assert.Contains(Path.GetFileNameWithoutExtension(otherPath), exception.Message);

        try { File.Delete(otherPath); } catch (IOException) { }
    }

    [Fact]
    public async Task Transactional_enqueue_on_the_same_file_is_accepted()
    {
        await using var temp = TempSqliteStore.Create();
        // Force first-use migration before the caller grabs the write lock.
        await temp.Store.CountJobsAsync();

        await using var caller = new SqliteConnection(NormalizedConnectionString(temp.Path));
        await caller.OpenAsync();
        var jobId = Guid.NewGuid();
        await using (var transaction = caller.BeginTransaction(deferred: true))
        {
            Assert.Equal(EnqueueResult.Ok,
                await temp.Store.EnqueueAsync(new NewJob(jobId, "demo", new byte[] { 1 }, "default", T0), T0, transaction));
            await transaction.CommitAsync();
        }

        Assert.NotNull(await temp.Store.GetJobAsync(jobId));
    }

    // ── 0097: hint fires on the adapter-owned commit, silent on the transactional path ──

    [Fact]
    public async Task Adapter_owned_enqueue_publishes_a_hint_for_the_queue()
    {
        await using var temp = TempSqliteStore.Create();
        var hinted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var subscription = await temp.Store.SubscribeAsync(queue => hinted.TrySetResult(queue));

        await temp.Store.EnqueueAsync(new NewJob(Guid.NewGuid(), "demo", default, "orders", T0), T0);

        var queue = await hinted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("orders", queue);
    }

    [Fact]
    public async Task Future_dated_enqueue_does_not_hint()
    {
        await using var temp = TempSqliteStore.Create();
        var hinted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var subscription = await temp.Store.SubscribeAsync(queue => hinted.TrySetResult(queue));

        // Not yet due: waking a pump now would be pointless, so no hint fires (§8).
        await temp.Store.EnqueueAsync(new NewJob(Guid.NewGuid(), "demo", default, "orders", T0.AddHours(1)), T0);

        await Assert.ThrowsAsync<TimeoutException>(async () => await hinted.Task.WaitAsync(TimeSpan.FromMilliseconds(300)));
    }

    [Fact]
    public async Task Transactional_enqueue_stays_silent_even_after_commit()
    {
        await using var temp = TempSqliteStore.Create();
        await temp.Store.CountJobsAsync();
        var hinted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var subscription = await temp.Store.SubscribeAsync(queue => hinted.TrySetResult(queue));

        await using var caller = new SqliteConnection(NormalizedConnectionString(temp.Path));
        await caller.OpenAsync();
        await using (var transaction = caller.BeginTransaction(deferred: true))
        {
            await temp.Store.EnqueueAsync(new NewJob(Guid.NewGuid(), "demo", default, "orders", T0), T0, transaction);
            await transaction.CommitAsync();
        }

        // The caller owns the commit; ADO.NET exposes no commit callback, so a hint here would race
        // the user's commit and could wake the pump to an invisible row. The adapter stays silent —
        // polling carries the work (ADR 0005). Correctness is unaffected: the job IS committed.
        await Assert.ThrowsAsync<TimeoutException>(async () => await hinted.Task.WaitAsync(TimeSpan.FromMilliseconds(300)));
        Assert.Equal(1, (await temp.Store.CountJobsAsync()).Sum(c => c.Count));
    }

    [Fact]
    public async Task Hints_can_be_disabled()
    {
        await using var store = new SqliteJobStore(new SqliteStoreOptions
        {
            ConnectionString = $"Data Source={Path.Combine(Path.GetTempPath(), $"backwave_sqlite_{Guid.NewGuid():N}.db")}",
            AutoMigrate = true,
            EnableInProcessHints = false,
        });
        var hinted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var subscription = await store.SubscribeAsync(queue => hinted.TrySetResult(queue));

        await store.EnqueueAsync(new NewJob(Guid.NewGuid(), "demo", default, "orders", T0), T0);

        await Assert.ThrowsAsync<TimeoutException>(async () => await hinted.Task.WaitAsync(TimeSpan.FromMilliseconds(300)));
    }
}
