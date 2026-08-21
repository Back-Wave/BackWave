using System.Diagnostics;
using BackWave.Core;
using BackWave.Hosting;
using BackWave.Jobs;
using BackWave.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Oracle.ManagedDataAccess.Client;

namespace BackWave.Oracle.Tests;

/// <summary>
/// Proves the Oracle Wake-Up Hint (issue 0257): with <see cref="OracleStoreOptions.EnableWakeUpHints"/>
/// on, a due enqueue on one connection wakes an idle pump on a separate connection through DBMS_ALERT,
/// far sooner than the poll interval. The paired control confirms that with the hint off the same pump
/// waits out the poll, so the fast claim is the hint and nothing else. These are integration tests: they
/// need "docker compose up -d oracle" and an EXECUTE grant on SYS.DBMS_ALERT (the tests add it).
/// </summary>
[Collection("oracle")]
public sealed class OracleWakeUpHintTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private const string Queue = "orders";
    private const int ProbeIndex = 1000;

    // A poll interval long enough that polling alone cannot explain a sub-second claim: only a hint can.
    private static readonly TimeSpan LongPollInterval = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Enqueue_wakes_an_idle_pump_far_faster_than_the_poll_interval()
    {
        await OracleTestDatabase.CreateFreshStoreAsync();
        await GrantAlertAccessAsync();
        var recorder = new ConcurrencyRecorder();

        // The consumer node: its pump waits on DBMS_ALERT while it polls every 5 s.
        var consumerStore = NewStore(enableWakeUpHints: true);
        using var consumer = BuildHost(consumerStore, recorder, WakeGroup());
        await consumer.StartAsync();

        // Let the pump reach its idle state and the DBMS_ALERT waiter register before the signal fires.
        await Task.Delay(TimeSpan.FromMilliseconds(750));

        // The producer node: a separate store (separate connections) enqueues the probe and signals.
        using var producer = BuildHost(NewStore(enableWakeUpHints: true), new ConcurrencyRecorder(), group: null);
        var producerClient = producer.Services.GetRequiredService<BackWaveClient>();

        recorder.ArmProbe();
        var stopwatch = Stopwatch.StartNew();
        await producerClient.EnqueueAsync(new OrderJob(ProbeIndex), dueTime: DateTimeOffset.UtcNow, queue: Queue);
        await WaitForAsync(() => recorder.ProbeSeen, TimeSpan.FromSeconds(10), "the hinted pump to claim the probe");
        stopwatch.Stop();
        await consumer.StopAsync();

        output.WriteLine($"hinted claim latency={stopwatch.ElapsedMilliseconds} ms, PollInterval={LongPollInterval.TotalSeconds} s");

        // A claim inside a fraction of the 5 s poll interval can only be the DBMS_ALERT wake.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2),
            $"hinted claim latency {stopwatch.ElapsedMilliseconds} ms must be far below the {LongPollInterval.TotalSeconds} s poll interval");
    }

    [Fact]
    public async Task Idle_pump_without_the_hint_waits_for_the_poll()
    {
        await OracleTestDatabase.CreateFreshStoreAsync();
        await GrantAlertAccessAsync();
        var recorder = new ConcurrencyRecorder();

        // The same 5 s poll interval, but the hint is off: the pump has no wake path but polling.
        var store = NewStore(enableWakeUpHints: false);
        using var host = BuildHost(store, recorder, WakeGroup());
        await host.StartAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(750));

        var client = host.Services.GetRequiredService<BackWaveClient>();
        recorder.ArmProbe();
        await client.EnqueueAsync(new OrderJob(ProbeIndex), dueTime: DateTimeOffset.UtcNow, queue: Queue);

        // The first poll fires one interval after start, so a probe enqueued now is not claimed within 2 s.
        await Task.Delay(TimeSpan.FromSeconds(2));
        var seenWithinTwoSeconds = recorder.ProbeSeen;

        // Polling is still correct: the probe is claimed on the next poll, not lost.
        await WaitForAsync(() => recorder.ProbeSeen, TimeSpan.FromSeconds(10), "the poll to eventually claim the probe");
        await host.StopAsync();

        output.WriteLine($"no-hint: seen within 2 s={seenWithinTwoSeconds} (expected False), claimed on the poll=True");
        Assert.False(seenWithinTwoSeconds,
            "without a hint an idle pump must wait for the poll, not claim within 2 s");
    }

    /// <summary>
    /// Proves the fail-safe: with the wake hint on but the EXECUTE grant on SYS.DBMS_ALERT missing, the
    /// enqueue must still succeed and commit, and the pump must still drain the job on the poll interval.
    /// A missing grant degrades to polling; it never fails the enqueue.
    /// </summary>
    [Fact]
    public async Task Missing_grant_does_not_fail_the_enqueue_and_the_pump_drains_on_the_poll()
    {
        await OracleTestDatabase.CreateFreshStoreAsync();
        try
        {
            await RevokeAlertAccessAsync();
            var recorder = new ConcurrencyRecorder();

            var store = NewStore(enableWakeUpHints: true);
            using var host = BuildHost(store, recorder, WakeGroup());
            await host.StartAsync();
            await Task.Delay(TimeSpan.FromMilliseconds(750));

            var client = host.Services.GetRequiredService<BackWaveClient>();
            recorder.ArmProbe();

            // The core assertion: a missing EXECUTE grant must degrade to polling, not fail the enqueue.
            await client.EnqueueAsync(new OrderJob(ProbeIndex), dueTime: DateTimeOffset.UtcNow, queue: Queue);

            await WaitForAsync(() => recorder.ProbeSeen, TimeSpan.FromSeconds(15),
                "the poll to drain the probe despite the missing grant");
            await host.StopAsync();
        }
        finally
        {
            // Restore the grant so the other tests in this serialized collection still pass.
            await GrantAlertAccessAsync();
        }
    }

    /// <summary>
    /// Proves the subscriber's reconnect loop re-establishes the DBMS_ALERT registration and hints resume
    /// after an initial channel fault: the loop opens, fails its first REGISTER on the revoked grant, waits
    /// out its reconnect delay, then re-opens and re-REGISTERs once the grant is restored - and a hint wakes
    /// the pump again. A dead loop or an inverted _faultLogged re-arm would fail this.
    /// </summary>
    [Fact]
    public async Task Subscriber_reconnects_and_hints_flow_again_after_an_initial_channel_fault()
    {
        await OracleTestDatabase.CreateFreshStoreAsync();
        try
        {
            // Start with the channel faulted: the subscriber's first REGISTER fails with no EXECUTE grant.
            await RevokeAlertAccessAsync();
            var recorder = new ConcurrencyRecorder();

            // A 60 s isolated poll group so no scheduled poll can fire in the window: the first poll is
            // anchored to pump start at t=0 and lands at t=60 s, long after this test's ~15 s enqueue and
            // ~18 s WaitForAsync deadline. Only a recovered hint can wake the pump before that first poll.
            var store = NewStore(enableWakeUpHints: true);
            using var host = BuildHost(store, recorder, IsolatedWakeGroup());
            await host.StartAsync();

            // Still inside the subscriber's first reconnect delay after its failed REGISTER.
            await Task.Delay(TimeSpan.FromSeconds(1));

            // Restore the channel; the next reconnect (at ~5 s) will REGISTER successfully and re-arm.
            await GrantAlertAccessAsync();

            // Wait well past one successful reconnect+register after the grant. ReconnectDelay = 5 s and the
            // grant was restored at ~1.2 s, but the first OpenAsync can be several seconds on a cold or loaded
            // pool; a 14 s wait leaves ~8-9 s of slack over a slow open + the 5 s reconnect + the re-open, so
            // the channel is healthy well before the enqueue at ~15 s from start.
            await Task.Delay(TimeSpan.FromSeconds(14));

            var client = host.Services.GetRequiredService<BackWaveClient>();
            recorder.ArmProbe();
            var stopwatch = Stopwatch.StartNew();
            await client.EnqueueAsync(new OrderJob(ProbeIndex), dueTime: DateTimeOffset.UtcNow, queue: Queue);
            // Two independent conditions hold at enqueue: (1) registration is healthy - the enqueue at ~15 s
            // is well past the channel becoming healthy even if the first open took several seconds; (2) no
            // scheduled poll can claim the probe - the next poll is at t=60 s, strictly later than this ~18 s
            // WaitForAsync deadline. So a dead reconnect loop times out here and fails; only a recovered hint
            // can satisfy it.
            await WaitForAsync(() => recorder.ProbeSeen, TimeSpan.FromSeconds(3),
                "the recovered hint channel to wake the pump");
            stopwatch.Stop();
            await host.StopAsync();

            output.WriteLine($"recovered hint latency={stopwatch.ElapsedMilliseconds} ms, PollInterval={IsolatedPollInterval.TotalSeconds} s");

            // A claim inside the 3 s window can only be the hint: the first 60 s poll is at t=60 s, far past it.
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3),
                $"recovered hint latency {stopwatch.ElapsedMilliseconds} ms must arrive inside the wait window and far below the {IsolatedPollInterval.TotalSeconds} s poll interval, proving the subscriber re-registered after the fault");
        }
        finally
        {
            // Restore the grant so the other tests in this serialized collection still pass.
            await GrantAlertAccessAsync();
        }
    }

    private static OracleJobStore NewStore(bool enableWakeUpHints) => new(new OracleStoreOptions
    {
        ConnectionString = OracleTestDatabase.ConnectionString,
        EnableWakeUpHints = enableWakeUpHints,
    });

    private static WorkerGroupOptions WakeGroup() => new()
    {
        Name = "orders-workers",
        Policy = new DispatchPolicy.Strict([Queue]),
        PoolSize = 20,
        PollInterval = LongPollInterval,
        // Polling-only pacing: the fixed poll cadence is the sole latency mechanism when the hint is off.
        MaxPollInterval = TimeSpan.Zero,
        LeaseDuration = TimeSpan.FromMinutes(2),
    };

    // A poll interval so long no scheduled poll can fire inside a single test's window, used only by the
    // reconnect test. It anchors the first poll to pump start at t=0, so with this cadence the first poll
    // lands at t=60 s - far past that test's ~15 s enqueue and ~18 s WaitForAsync deadline. That removes all
    // poll-boundary coupling: only a recovered hint can wake the pump inside the window, never a poll.
    private static readonly TimeSpan IsolatedPollInterval = TimeSpan.FromSeconds(60);

    private static WorkerGroupOptions IsolatedWakeGroup() => new()
    {
        Name = "orders-workers",
        Policy = new DispatchPolicy.Strict([Queue]),
        PoolSize = 20,
        PollInterval = IsolatedPollInterval,
        MaxPollInterval = TimeSpan.Zero,
        LeaseDuration = TimeSpan.FromMinutes(2),
    };

    private static IHost BuildHost(IJobStore store, ConcurrencyRecorder recorder, WorkerGroupOptions? group)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(recorder);
        builder.Services.AddTransient<IJobHandler<OrderJob>, OrderHandler>();
        builder.Services.AddBackWave(backwave =>
        {
            backwave
                .UseStore(store)
                .UseRegistry(new JobRegistry(
                [
                    JobRegistration.Create<OrderJob, OrderHandler>("order", OracleLoadJsonContext.Default.OrderJob, Queue),
                ]));
            if (group is not null)
            {
                backwave.AddWorkerGroup(group);
            }
        });
        return builder.Build();
    }

    // Grants the connecting user EXECUTE on SYS.DBMS_ALERT, the one out-of-band grant the wake hint needs.
    private static async Task GrantAlertAccessAsync()
    {
        var sysConnectionString = new OracleConnectionStringBuilder(OracleTestDatabase.ConnectionString)
        {
            UserID = "sys",
            Password = "backwave",
            DBAPrivilege = "SYSDBA",
        }.ConnectionString;

        await using var sys = new OracleConnection(sysConnectionString);
        await sys.OpenAsync();
        await using var grant = sys.CreateCommand();
        grant.CommandText = "GRANT EXECUTE ON SYS.DBMS_ALERT TO backwave";
        await grant.ExecuteNonQueryAsync();
    }

    // Revokes the connecting user's EXECUTE on SYS.DBMS_ALERT, so the wake hint has no channel to signal on.
    private static async Task RevokeAlertAccessAsync()
    {
        var sysConnectionString = new OracleConnectionStringBuilder(OracleTestDatabase.ConnectionString)
        {
            UserID = "sys",
            Password = "backwave",
            DBAPrivilege = "SYSDBA",
        }.ConnectionString;

        await using var sys = new OracleConnection(sysConnectionString);
        await sys.OpenAsync();
        await using var revoke = sys.CreateCommand();
        revoke.CommandText = "REVOKE EXECUTE ON SYS.DBMS_ALERT FROM backwave";
        await revoke.ExecuteNonQueryAsync();
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout, string description)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }
            await Task.Delay(25);
        }
        Assert.Fail($"Timed out waiting for: {description}");
    }
}
