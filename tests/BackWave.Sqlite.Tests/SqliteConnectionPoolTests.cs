using System.Collections.Concurrent;
using BackWave.Sqlite.Internal;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace BackWave.Sqlite.Tests;

public sealed class SqliteConnectionPoolTests
{
    [Fact]
    public async Task Concurrent_opens_on_the_store_pool_never_share_a_native_connection()
    {
        const int workers = 16;
        const int waves = 10000;
        var path = Path.Combine(Path.GetTempPath(), $"backwave_sqlite_pool_{Guid.NewGuid():N}.db");
        var connectionString = SqliteConnectionStringNormalizer.Normalize($"Data Source={path}", TimeSpan.FromSeconds(5));
        var connections = new SqliteConnection?[workers];
        var errors = new ConcurrentQueue<Exception>();
        var shared = false;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using var barrier = new Barrier(workers + 1);
        await using var poolKey = new SqliteConnection(connectionString);

        var threads = Enumerable.Range(0, workers).Select(index => Task.Factory.StartNew(
            () =>
            {
                for (var wave = 0; wave < waves; wave++)
                {
                    barrier.SignalAndWait(timeout.Token);
                    try
                    {
                        connections[index] = new SqliteConnection(connectionString);
                        connections[index]!.Open();
                    }
                    catch (Exception exception)
                    {
                        errors.Enqueue(exception);
                    }

                    try
                    {
                        barrier.SignalAndWait(timeout.Token);
                        barrier.SignalAndWait(timeout.Token);
                    }
                    finally
                    {
                        try
                        {
                            connections[index]?.Dispose();
                        }
                        catch (Exception exception)
                        {
                            errors.Enqueue(exception);
                        }
                    }

                    barrier.SignalAndWait(timeout.Token);
                    if (shared || !errors.IsEmpty)
                    {
                        return;
                    }
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();

        try
        {
            for (var wave = 0; wave < waves && !shared && errors.IsEmpty; wave++)
            {
                // An empty pool makes each open create a connection, and a checkout that finds the pool
                // empty can scan for leaked connections, which is where a live connection can be taken.
                SqliteConnection.ClearPool(poolKey);
                barrier.SignalAndWait(timeout.Token);
                barrier.SignalAndWait(timeout.Token);
                shared = connections.Select(c => c?.Handle).OfType<sqlite3>().Distinct().Count() != workers;
                barrier.SignalAndWait(timeout.Token);
                barrier.SignalAndWait(timeout.Token);
            }
        }
        finally
        {
            timeout.Cancel();
            await Task.WhenAll(threads).ContinueWith(_ => { }, TaskScheduler.Default);
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                try { File.Delete(path + suffix); } catch (IOException) { }
            }
        }

        Assert.Empty(errors);
        Assert.False(shared, "Two open SqliteConnections shared one native connection.");
    }
}
