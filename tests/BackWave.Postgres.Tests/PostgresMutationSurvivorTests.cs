using BackWave.Core;
using BackWave.Storage;
using Npgsql;

namespace BackWave.Postgres.Tests;

/// <summary>
/// Postgres-only teeth for Round-1 mutation survivors that no adapter-agnostic conformance fact
/// can pin (issue 0236): the pg_notify Wake-Up Hint gating on enqueue and mint, the LISTEN
/// reconnect-forever loop, and the transactional-enqueue capability flag. Each asserts a
/// behaviour that lives entirely in the Postgres adapter's NOTIFY/LISTEN machinery.
/// </summary>
[Collection("postgres")]
public sealed class PostgresMutationSurvivorTests
{
    // The default-schema hint channel pg_notify publishes to (SchemaRewriter: "backwave_hints").
    private const string HintChannel = "backwave_hints";

    private static NewJob Job(string queue, DateTimeOffset dueTime, IReadOnlyList<Guid>? parents = null) =>
        new(Guid.NewGuid(), "mutation-probe", "{}"u8.ToArray(), queue, dueTime)
        {
            Parents = parents ?? [],
        };

    private static ScheduleRecord Schedule(string id, string queue, DateTimeOffset cursor) => new()
    {
        ScheduleId = id,
        Cron = CronExpression.Parse("0 3 * * *").Canonical,
        WireName = "mutation-probe",
        Payload = "{}"u8.ToArray(),
        Queue = queue,
        Cursor = cursor,
    };

    // ── pg_notify Wake-Up Hint: MintDueAsync (PostgresJobStore.cs:1603, :1607) ─────

    /// <summary>
    /// MintDueAsync minting at least one instance must publish exactly one wake hint carrying the
    /// schedule's queue. Kills the mint-hint gate mutants (> 0 negated/removed) and the
    /// dropped-PublishHintAsync mutant.
    /// </summary>
    [Fact]
    public async Task MintDue_MintingAtLeastOne_PublishesWakeHintOnQueueChannel()
    {
        await using var store = await PostgresTestDatabase.CreateFreshStoreAsync();
        var cursor = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await store.UpsertScheduleAsync(Schedule("mint-hit", "mint-q", cursor));
        var tick = cursor.AddDays(1).AddHours(3);

        await using var listener = await HintListener.StartAsync();

        var minted = await store.MintDueAsync(
            [new MintDecision("mint-hit", ExpectedCursor: cursor, NewCursor: tick, Ticks: [tick], SkippedTicks: [])]);
        Assert.Equal(1, minted);

        var hints = await listener.DrainAsync();
        Assert.Equal(["mint-q"], hints);
    }

    /// <summary>
    /// MintDueAsync that advances the cursor but mints nothing (every insert hits ON CONFLICT) must
    /// publish NO wake hint. Kills the > 0 -> >= 0 widening that would fire a spurious hint on a
    /// zero-mint decision.
    /// </summary>
    [Fact]
    public async Task MintDue_MintingNothing_PublishesNoWakeHint()
    {
        await using var store = await PostgresTestDatabase.CreateFreshStoreAsync();
        var cursor = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await store.UpsertScheduleAsync(Schedule("mint-miss", "mint-q", cursor));
        var tick = cursor.AddDays(1).AddHours(3);

        // First mint creates the instance and advances the cursor to `tick`.
        Assert.Equal(1, await store.MintDueAsync(
            [new MintDecision("mint-miss", ExpectedCursor: cursor, NewCursor: tick, Ticks: [tick], SkippedTicks: [])]));

        await using var listener = await HintListener.StartAsync();

        // A fresh decision whose cursor fence PASSES (expects the now-current cursor) but whose only
        // tick is the already-minted one: the fence advances, the insert hits ON CONFLICT, so
        // mintedForDecision stays 0 — the exact path the >= 0 mutant would wrongly hint from.
        var minted = await store.MintDueAsync(
            [new MintDecision("mint-miss", ExpectedCursor: tick, NewCursor: tick.AddSeconds(1), Ticks: [tick], SkippedTicks: [])]);
        Assert.Equal(0, minted);

        var hints = await listener.DrainAsync();
        Assert.Empty(hints);
    }

    // ── pg_notify Wake-Up Hint: EnqueueCoreAsync (PostgresJobStore.cs:283) ─────────

    /// <summary>
    /// Enqueuing a Scheduled job due exactly at `now` must publish exactly one wake hint for its
    /// queue. Kills the DueTime &lt;= now -> DueTime &lt; now narrowing that would drop the boundary hint.
    /// </summary>
    [Fact]
    public async Task Enqueue_DueExactlyNowScheduled_PublishesExactlyOneWakeHint()
    {
        await using var store = await PostgresTestDatabase.CreateFreshStoreAsync();
        var now = DateTimeOffset.UtcNow;

        await using var listener = await HintListener.StartAsync();

        Assert.Equal(EnqueueResult.Ok, await store.EnqueueAsync(Job("due-now-q", dueTime: now), now));

        var hints = await listener.DrainAsync();
        Assert.Equal(["due-now-q"], hints);
    }

    /// <summary>
    /// Enqueuing a Scheduled job that is not yet due must publish NO wake hint. Kills the
    /// &amp;&amp; -> || relaxation of the "Scheduled AND due-now" guard, which would hint every enqueue.
    /// </summary>
    [Fact]
    public async Task Enqueue_FutureDueScheduled_PublishesNoWakeHint()
    {
        await using var store = await PostgresTestDatabase.CreateFreshStoreAsync();
        var now = DateTimeOffset.UtcNow;

        await using var listener = await HintListener.StartAsync();

        Assert.Equal(EnqueueResult.Ok, await store.EnqueueAsync(Job("future-q", dueTime: now.AddHours(1)), now));

        var hints = await listener.DrainAsync();
        Assert.Empty(hints);
    }

    /// <summary>
    /// Enqueuing a job that lands in AwaitingParent (an unresolved parent) must publish NO wake hint,
    /// even when the child's own DueTime is already past. Kills the &amp;&amp; -> || relaxation of the
    /// state guard, which would hint a not-yet-runnable dependent.
    /// </summary>
    [Fact]
    public async Task Enqueue_AwaitingParent_PublishesNoWakeHint()
    {
        await using var store = await PostgresTestDatabase.CreateFreshStoreAsync();
        var now = DateTimeOffset.UtcNow;

        // Parent enqueued BEFORE the listener exists, so its own (legitimate) due-now hint is not seen.
        var parent = Job("await-q", dueTime: now);
        Assert.Equal(EnqueueResult.Ok, await store.EnqueueAsync(parent, now));

        await using var listener = await HintListener.StartAsync();

        // Child is due now but gated on the still-Scheduled parent -> AwaitingParent, no hint.
        var child = Job("await-q", dueTime: now, parents: [parent.JobId]);
        Assert.Equal(EnqueueResult.Ok, await store.EnqueueAsync(child, now));

        var hints = await listener.DrainAsync();
        Assert.Empty(hints);
    }

    // ── LISTEN reconnect-forever loop (PostgresJobStore.cs:2873) ───────────────────

    /// <summary>
    /// After the store's LISTEN backend is terminated, the subscription must reconnect and deliver a
    /// later enqueue's hint. Kills the catch-filter mutant that would fault the loop on the first
    /// channel disruption, leaving the hint channel permanently dead.
    /// </summary>
    [Fact]
    public async Task SubscribeAsync_AfterListenBackendKilled_ReconnectsAndDeliversLaterHints()
    {
        await using var store = await PostgresTestDatabase.CreateFreshStoreAsync();
        var delivered = new SemaphoreSlim(0);
        await using var subscription = await store.SubscribeAsync(_ => delivered.Release());

        var initialPids = await WaitForListenBackendsAsync(atLeast: 1, previous: new HashSet<int>());

        // Baseline: a hint reaches the live subscription.
        Assert.Equal(EnqueueResult.Ok,
            await store.EnqueueAsync(Job("reconnect-q", dueTime: DateTimeOffset.UtcNow), DateTimeOffset.UtcNow));
        Assert.True(await delivered.WaitAsync(TimeSpan.FromSeconds(10)), "the initial hint was never delivered");

        // Sever the hint channel. Reconnection (ReconnectDelay ~5 s) must bring a NEW LISTEN backend up.
        await KillBackendsAsync(initialPids);
        var reconnectedPids = await WaitForListenBackendsAsync(atLeast: 1, previous: initialPids, timeout: TimeSpan.FromSeconds(20));
        Assert.True(reconnectedPids.Except(initialPids).Any(), "the LISTEN subscription never reconnected");

        // Drain any release from delivery churn, then prove a fresh hint arrives on the new channel.
        while (await delivered.WaitAsync(0)) { }
        Assert.Equal(EnqueueResult.Ok,
            await store.EnqueueAsync(Job("reconnect-q", dueTime: DateTimeOffset.UtcNow), DateTimeOffset.UtcNow));
        Assert.True(await delivered.WaitAsync(TimeSpan.FromSeconds(10)),
            "no hint was delivered after the LISTEN backend was killed — the subscription did not reconnect");
    }

    // ── Transactional-enqueue capability flag (PostgresJobStore.cs:92) ─────────────

    /// <summary>
    /// The Postgres store advertises transactional enqueue. Pins the capability flag so the three
    /// transactional-enqueue conformance tests can never silently skip vacuously.
    /// </summary>
    [Fact]
    public async Task SupportsTransactionalEnqueue_IsTrue()
    {
        await using var store = await PostgresTestDatabase.CreateFreshStoreAsync();
        Assert.True(store.SupportsTransactionalEnqueue);
    }

    // ── Cross-process unlimited-queue staleness bound (PostgresJobStore.cs:447) ────

    /// <summary>
    /// A queue observed unlimited and cached by one store instance must stop being trusted once the
    /// staleness window elapses, so a concurrency limit set by ANOTHER instance is eventually
    /// enforced. Kills the elapsed-vs-refresh mutants that would trust an aged unlimited stamp forever.
    /// </summary>
    [Fact]
    public async Task IsCachedUnlimited_AgedStamp_EventuallyHonorsACrossProcessLimit()
    {
        const string queue = "shared-limit-q";
        var now = DateTimeOffset.UtcNow;

        await using var storeA = await PostgresTestDatabase.CreateFreshStoreAsync();
        await using var storeB = new PostgresJobStore(
            new PostgresStoreOptions { ConnectionString = PostgresTestDatabase.ConnectionString });

        // Three due jobs on an as-yet-unlimited queue.
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(EnqueueResult.Ok, await storeA.EnqueueAsync(Job(queue, dueTime: now), now));
        }

        // A claims one and caches the queue as "unlimited" (no queue_limits row exists yet).
        var first = await ClaimAsync(storeA, queue, now);
        Assert.Single(first);

        // B (a separate process) imposes a limit of 1. A's generation is untouched, so ONLY the
        // elapsed-time clause of IsCachedUnlimited can catch this — the mutated line.
        await storeB.SetConcurrencyLimitAsync(queue, limit: 1, actor: "op", now);

        // Before the window elapses A still trusts its stale unlimited stamp and over-claims.
        var stale = await ClaimAsync(storeA, queue, now);
        Assert.Single(stale);

        // Past the staleness window A must re-read the row: limit 1, two already leased -> no slots.
        await Task.Delay(TimeSpan.FromMilliseconds(5_500));
        var afterExpiry = await ClaimAsync(storeA, queue, now);
        Assert.Empty(afterExpiry);
    }

    private static ValueTask<IReadOnlyList<JobRecord>> ClaimAsync(
        PostgresJobStore store, string queue, DateTimeOffset now) =>
        store.ClaimAsync(new ClaimRequest("worker", [queue], MaxJobs: 1, LeaseDuration: TimeSpan.FromMinutes(5), now));

    // ── pg_stat_activity helpers for the LISTEN backends ──────────────────────────

    private static async Task<IReadOnlySet<int>> ListenBackendPidsAsync()
    {
        await using var dataSource = NpgsqlDataSource.Create(PostgresTestDatabase.ConnectionString);
        await using var command = dataSource.CreateCommand(
            "SELECT pid FROM pg_stat_activity WHERE query ILIKE 'LISTEN%'");
        var pids = new HashSet<int>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            pids.Add(reader.GetInt32(0));
        }
        return pids;
    }

    private static async Task<IReadOnlySet<int>> WaitForListenBackendsAsync(
        int atLeast, IReadOnlySet<int> previous, TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));
        while (DateTimeOffset.UtcNow < deadline)
        {
            var pids = await ListenBackendPidsAsync();
            if (pids.Except(previous).Count() >= atLeast)
            {
                return pids;
            }
            await Task.Delay(100);
        }
        Assert.Fail("the expected LISTEN backend never appeared in pg_stat_activity");
        return new HashSet<int>();
    }

    private static async Task KillBackendsAsync(IReadOnlySet<int> pids)
    {
        if (pids.Count == 0)
        {
            return;
        }
        await using var dataSource = NpgsqlDataSource.Create(PostgresTestDatabase.ConnectionString);
        await using var command = dataSource.CreateCommand(
            "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE pid = ANY(@pids)");
        command.Parameters.AddWithValue("pids", pids.ToArray());
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// A dedicated, single physical connection that LISTENs on the hint channel and records the
    /// payloads (queue names) of every NOTIFY it receives.
    /// </summary>
    private sealed class HintListener : IAsyncDisposable
    {
        private readonly NpgsqlConnection _connection;
        private readonly List<string> _payloads = [];

        private HintListener(NpgsqlConnection connection) => _connection = connection;

        public static async Task<HintListener> StartAsync()
        {
            // A LISTEN connection must never be pooled: a returned connection can carry a lingering
            // LISTEN registration and buffered NOTIFYs into the next test that reuses the physical
            // backend. A dedicated, unpooled backend sees only the notifications sent while it listens.
            var unpooled = new NpgsqlConnectionStringBuilder(PostgresTestDatabase.ConnectionString)
            {
                Pooling = false,
            }.ConnectionString;
            var connection = new NpgsqlConnection(unpooled);
            await connection.OpenAsync();
            var listener = new HintListener(connection);
            connection.Notification += (_, args) =>
            {
                if (args.Channel == HintChannel)
                {
                    lock (listener._payloads)
                    {
                        listener._payloads.Add(args.Payload);
                    }
                }
            };
            await using var listen = new NpgsqlCommand($"LISTEN {HintChannel}", connection);
            await listen.ExecuteNonQueryAsync();
            return listener;
        }

        /// <summary>
        /// Collects every hint delivered until the channel goes quiet for <paramref name="quietWindow"/>,
        /// so a "no hint" case returns empty and a single hint returns exactly one payload.
        /// </summary>
        public async Task<IReadOnlyList<string>> DrainAsync(TimeSpan? quietWindow = null)
        {
            var window = quietWindow ?? TimeSpan.FromSeconds(1);
            while (await _connection.WaitAsync(window))
            {
            }
            lock (_payloads)
            {
                return _payloads.ToArray();
            }
        }

        public async ValueTask DisposeAsync() => await _connection.DisposeAsync();
    }
}
