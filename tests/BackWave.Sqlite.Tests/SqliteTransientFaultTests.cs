using System.Text.Json.Serialization;
using BackWave.Core;
using BackWave.Hosting;
using BackWave.Jobs;
using BackWave.Sqlite.Internal;
using BackWave.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BackWave.Sqlite.Tests;

public sealed record BusyProbe(string Name);

public sealed class BusyProbeHandler(BusyRecorder recorder) : IJobHandler<BusyProbe>
{
    public Task HandleAsync(BusyProbe job, JobContext context, CancellationToken cancellationToken)
    {
        recorder.Handled.TrySetResult(job.Name);
        return Task.CompletedTask;
    }
}

public sealed class BusyRecorder
{
    public TaskCompletionSource<string> Handled { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

[JsonSerializable(typeof(BusyProbe))]
internal sealed partial class BusyJsonContext : JsonSerializerContext;

/// <summary>
/// Issue 0098: residual <c>SQLITE_BUSY</c>/<c>SQLITE_LOCKED</c> (write-lock contention surviving the
/// busy-timeout) is a transient store fault — the Worker Group degrades-and-retries rather than
/// fail-stopping (ADR 0007 amendment). The adapter owns the classification (<see cref="IStoreFaultClassifier"/>)
/// so the host stays adapter-agnostic.
/// </summary>
public sealed class SqliteTransientFaultTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Busy_and_locked_classify_transient_other_errors_do_not()
    {
        var store = new SqliteJobStore(new SqliteStoreOptions { ConnectionString = "Data Source=:memory:" });

        Assert.True(store.IsTransientFault(new SqliteException("busy", 5)));
        Assert.True(store.IsTransientFault(new SqliteException("locked", 6)));
        // Wrapped at depth — Microsoft.Data.Sqlite sometimes nests the provider error.
        Assert.True(store.IsTransientFault(new InvalidOperationException("x", new SqliteException("busy", 5))));
        // Anything else is left to the host's default classification (i.e. fail-stop).
        Assert.False(store.IsTransientFault(new SqliteException("constraint", 19)));
        Assert.False(store.IsTransientFault(new InvalidOperationException("invariant violated")));
    }

    [Fact]
    public async Task A_contended_claim_raises_a_fault_the_adapter_classifies_transient()
    {
        var path = Path.Combine(Path.GetTempPath(), $"backwave_sqlite_busy_{Guid.NewGuid():N}.db");
        await using var store = new SqliteJobStore(new SqliteStoreOptions
        {
            ConnectionString = $"Data Source={path}",
            AutoMigrate = true,
            BusyTimeout = TimeSpan.FromMilliseconds(250), // error fast under contention, for the test
        });
        try
        {
            // A committed, due job — so the claim's lock-free peek finds work and proceeds to BEGIN IMMEDIATE.
            await store.EnqueueAsync(new NewJob(Guid.NewGuid(), "busy-probe", default, "default", T0), T0);

            // Hold the single write lock from a separate connection, simulating heavy multi-process
            // contention that survives the busy-timeout.
            await using var hog = new SqliteConnection(
                SqliteConnectionStringNormalizer.Normalize($"Data Source={path}", TimeSpan.FromSeconds(5)));
            await hog.OpenAsync();
            await using var lockHeld = hog.BeginTransaction(deferred: false); // BEGIN IMMEDIATE: grabs the writer
            await using (var write = new SqliteCommand(
                "INSERT INTO backwave_operator_audit (actor, action, target, recorded_at) VALUES ('t', 0, 't', 0)", hog, (SqliteTransaction)lockHeld))
            {
                await write.ExecuteNonQueryAsync();
            }

            var fault = await Assert.ThrowsAnyAsync<SqliteException>(async () =>
                await store.ClaimAsync(new ClaimRequest("w1", ["default"], 10, TimeSpan.FromMinutes(1), T0)));

            Assert.Contains(fault.SqliteErrorCode, new[] { 5, 6 });
            Assert.True(store.IsTransientFault(fault), "the adapter must classify the contended-claim fault as transient");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                try { File.Delete(path + suffix); } catch (IOException) { }
            }
        }
    }

    [Fact]
    public async Task Worker_group_degrades_then_recovers_under_write_lock_contention()
    {
        var path = Path.Combine(Path.GetTempPath(), $"backwave_sqlite_busygrp_{Guid.NewGuid():N}.db");
        await using var store = new SqliteJobStore(new SqliteStoreOptions
        {
            ConnectionString = $"Data Source={path}",
            AutoMigrate = true,
            BusyTimeout = TimeSpan.FromMilliseconds(250),
        });

        var recorder = new BusyRecorder();
        var provider = new ServiceCollection()
            .AddSingleton(recorder)
            .AddTransient<IJobHandler<BusyProbe>, BusyProbeHandler>()
            .BuildServiceProvider();
        var registry = new JobRegistry(
        [
            JobRegistration.Create<BusyProbe, BusyProbeHandler>("busy-probe", BusyJsonContext.Default.BusyProbe),
        ]);
        var health = new BackWaveHealth();
        var service = new WorkerGroupService(
            new WorkerGroupOptions
            {
                Name = "busy-workers",
                Policy = new DispatchPolicy.Strict(["default"]),
                PollInterval = TimeSpan.FromMilliseconds(200),
                LeaseDuration = TimeSpan.FromSeconds(30),
            },
            store, registry,
            provider.GetRequiredService<IServiceScopeFactory>(),
            health, NullLogger<WorkerGroupService>.Instance);

        var client = new BackWaveClient(store, registry);

        try
        {
            await client.EnqueueAsync(new BusyProbe("contended"), DateTimeOffset.UtcNow);

            // Hold the write lock so the pump's claim hits SQLITE_BUSY every tick.
            await using var hog = new SqliteConnection(
                SqliteConnectionStringNormalizer.Normalize($"Data Source={path}", TimeSpan.FromSeconds(5)));
            await hog.OpenAsync();
            var lockHeld = (SqliteTransaction)hog.BeginTransaction(deferred: false);
            await using (var write = new SqliteCommand(
                "INSERT INTO backwave_operator_audit (actor, action, target, recorded_at) VALUES ('t', 0, 't', 0)", hog, lockHeld))
            {
                await write.ExecuteNonQueryAsync();
            }

            await service.StartAsync(CancellationToken.None);

            // The group must record itself degraded — running and retrying — never halted (fail-stop).
            await WaitUntilAsync(() => health.DegradedGroups.ContainsKey("busy-workers"), TimeSpan.FromSeconds(5));
            Assert.False(health.HaltedGroups.ContainsKey("busy-workers"), "contention must not fail-stop the group");
            Assert.False(recorder.Handled.Task.IsCompleted, "nothing should run while the lock is held");

            // Release the lock: the very next clean poll claims and runs the job, and the group recovers.
            await lockHeld.RollbackAsync();
            await hog.CloseAsync();

            var handled = await recorder.Handled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("contended", handled);
            await WaitUntilAsync(() => !health.DegradedGroups.ContainsKey("busy-workers"), TimeSpan.FromSeconds(5));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                try { File.Delete(path + suffix); } catch (IOException) { }
            }
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }
            await Task.Delay(50);
        }
        Assert.True(condition(), "condition was not met within the timeout");
    }
}
