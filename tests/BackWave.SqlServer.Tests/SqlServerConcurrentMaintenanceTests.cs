using System.Diagnostics;
using System.Diagnostics.Metrics;
using BackWave.Core;
using BackWave.Storage;
using Microsoft.Data.SqlClient;

namespace BackWave.SqlServer.Tests;

/// <summary>
/// Lock-footprint regression tests. A fleet runs maintenance on every node at once, so the store's
/// set-based statements must lock only the rows of their own batch. When one of them scans
/// backwave.jobs instead, two sweeps cross-lock and SQL Server kills one with error 1205.
///
/// These tests count the deadlocks the store ABSORBS, not the ones that escape it. The bounded retry
/// inside the store swallows up to three losses per call, so an assertion that only watches the call
/// boundary passes on a footprint wide enough to deadlock - it merely costs the fleet a replay, which
/// is exactly the regression these tests exist to catch. StoreFaultCounter reads the losses off
/// backwave.store.faults, the one place an absorbed deadlock is observable from outside the store.
/// </summary>
[Collection("sqlserver")]
public sealed class SqlServerConcurrentMaintenanceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    // 96 leased jobs per round is wide enough that the optimizer costs a full scan of backwave.jobs
    // below a seek per row, which is the plan that exposes an oversized lock footprint. The first
    // round caches that plan, so every later round runs against the worst case. Both history policies
    // run: Off exercises the disposition UPDATEs alone, and the full rung adds the transition insert.
    [Theory]
    [InlineData(JobHistoryPolicy.TransitionsAndFailureDetail)]
    [InlineData(JobHistoryPolicy.Off)]
    public async Task Concurrent_lease_sweeps_never_deadlock(JobHistoryPolicy policy)
    {
        const int Rounds = 15, Jobs = 96, Sweepers = 6;
        var disposition = new RetryPolicy { MaxAttempts = 5, Backoff = _ => TimeSpan.FromMinutes(1) }.ToDisposition();
        using var faults = new StoreFaultCounter();
        var escaped = 0;

        for (var round = 0; round < Rounds; round++)
        {
            var store = await SqlServerTestDatabase.CreateFreshStoreAsync(policy);
            for (var i = 0; i < Jobs; i++)
            {
                await store.EnqueueAsync(new NewJob(Guid.NewGuid(), "t", "{}"u8.ToArray(), "default", T0), T0);
            }
            await store.ClaimAsync(new ClaimRequest("w", ["default"], Jobs, Lease, T0));

            var after = T0 + Lease + TimeSpan.FromSeconds(1);
            var results = await Task.WhenAll(Enumerable.Range(0, Sweepers).Select(_ => Task.Run(async () =>
            {
                try
                {
                    await store.ExpireLeasesAsync(after, maxJobs: Jobs, ["default"], disposition);
                    return 0;
                }
                catch (SqlException e) when (e.Number == 1205)
                {
                    return 1;
                }
            })));
            escaped += results.Sum();
        }

        // ExpireLeases writes the transition log BEFORE either disposition UPDATE, so the transaction
        // holds only U locks when the foreign-key check reads backwave.jobs, and U is compatible with
        // the S the check asks for. That reorder is what makes the sweep deadlock-free rather than
        // merely deadlock-tolerant, so the pin is zero losses, not zero escapes.
        Assert.Equal(0, faults.Absorbed);
        Assert.Equal(0, escaped);
        Assert.Equal(0, faults.Terminal);
    }

    // Every writer shares one transition-log INSERT, so its plan is compiled once and then reused. A
    // wide lease sweep compiles it at 96 rows, where the optimizer serves the foreign-key check to
    // backwave.jobs with a full scan, and every later caller inherits that plan whatever its own batch
    // size holds. This test poisons the plan cache that way on purpose, then reports the narrow outcome
    // batches a real fleet reports. That is the shape that lost a deadlock before the bounded retry.
    [Fact]
    public async Task Concurrent_outcome_reports_never_deadlock()
    {
        const int Poison = 96, Rounds = 30, Workers = 6, PerWorker = 16;
        var store = await SqlServerTestDatabase.CreateFreshStoreAsync(JobHistoryPolicy.TransitionsAndFailureDetail);
        var deadLetter = new RetryPolicy { MaxAttempts = 1, Backoff = _ => TimeSpan.FromMinutes(1) }.ToDisposition();

        for (var i = 0; i < Poison; i++)
        {
            await store.EnqueueAsync(new NewJob(Guid.NewGuid(), "t", "{}"u8.ToArray(), "poison", T0), T0);
        }
        await store.ClaimAsync(new ClaimRequest("sweeper", ["poison"], Poison, Lease, T0));
        // MaxAttempts 1 dead-letters the swept jobs, so they never return to the claimable set.
        await store.ExpireLeasesAsync(T0 + Lease + TimeSpan.FromSeconds(1), Poison, ["poison"], deadLetter);

        using var faults = new StoreFaultCounter();
        var escaped = 0;
        for (var round = 0; round < Rounds; round++)
        {
            var now = T0 + TimeSpan.FromHours(round + 1);
            for (var i = 0; i < Workers * PerWorker; i++)
            {
                await store.EnqueueAsync(new NewJob(Guid.NewGuid(), "t", "{}"u8.ToArray(), "default", now), now);
            }

            var batches = new List<OutcomeReport[]>();
            for (var w = 0; w < Workers; w++)
            {
                var worker = $"w{w}";
                var claimed = await store.ClaimAsync(new ClaimRequest(worker, ["default"], PerWorker, Lease, now));
                batches.Add([.. claimed.Select(job =>
                    new OutcomeReport(job.JobId, worker, job.Attempt, new JobOutcome.Success()))]);
            }

            var results = await Task.WhenAll(batches.Select(batch => Task.Run(async () =>
            {
                try
                {
                    await store.ReportOutcomesAsync(batch, now);
                    return 0;
                }
                catch (SqlException e) when (e.Number == 1205)
                {
                    return 1;
                }
            })));
            escaped += results.Sum();
        }

        Assert.Equal(0, faults.Absorbed);
        Assert.Equal(0, escaped);
        Assert.Equal(0, faults.Terminal);
    }

    // The hand-back a node runs on a clean stop, raced against the claims its peers still make. The
    // relinquish selects by lease_owner, which no index covers, so its UPDLOCK scan can hold locks on
    // rows outside its own batch. A claim that reaches for a Scheduled row inside that footprint then
    // closes a cycle. Expiry never reaches this window - it takes only Leases that already lapsed, which
    // no live claimer races - so the hand-back is the one maintenance path the two pins above leave
    // uncovered.
    //
    // Each worker revokes its NEIGHBOR's Leases, never its own, so every hand-back lands on a worker that
    // is mid-claim. MaxAttempts equals Passes, so the early passes reach the ready UPDATE and the last
    // pass reaches the dead-letter UPDATE with its per-row cause.
    [Theory]
    [InlineData(JobHistoryPolicy.TransitionsAndFailureDetail)]
    [InlineData(JobHistoryPolicy.Off)]
    public async Task Concurrent_relinquishes_and_claims_never_deadlock(JobHistoryPolicy policy)
    {
        const int Rounds = 10, Jobs = 96, Workers = 6, Batch = 16, Passes = 4, Ballast = 5000;
        var disposition = new RetryPolicy { MaxAttempts = Passes, Backoff = _ => TimeSpan.FromMinutes(1) }
            .ToDisposition();
        using var faults = new StoreFaultCounter();
        var escaped = 0;

        for (var round = 0; round < Rounds; round++)
        {
            var store = await SqlServerTestDatabase.CreateFreshStoreAsync(policy);
            await AddBallast(Ballast);
            for (var i = 0; i < Jobs; i++)
            {
                await store.EnqueueAsync(new NewJob(Guid.NewGuid(), "t", "{}"u8.ToArray(), "default", T0), T0);
            }

            var results = await Task.WhenAll(Enumerable.Range(0, Workers).Select(w => Task.Run(async () =>
            {
                var owner = $"w{w}";
                var neighbor = $"w{(w + 1) % Workers}";
                var lost = 0;
                for (var pass = 0; pass < Passes; pass++)
                {
                    try
                    {
                        await store.ClaimAsync(new ClaimRequest(owner, ["default"], Batch, Lease, T0));
                        await store.RelinquishLeasesAsync(neighbor, T0, disposition);
                    }
                    catch (SqlException e) when (e.Number == 1205)
                    {
                        lost++;
                    }
                }
                return lost;
            })));
            escaped += results.Sum();
        }

        // The hand-back writes the Transition Log before either disposition UPDATE, the same order the
        // sweep uses, so the pin is zero losses and not merely zero escapes. Off is here because it takes
        // the Transition Log out of the round entirely, which leaves the hand-back's own read as the only
        // thing left that can lock past its batch.
        Assert.Equal(0, faults.Absorbed);
        Assert.Equal(0, escaped);
        Assert.Equal(0, faults.Terminal);
    }

    // The pin above is a race: the plan has to lose it for the counter to move. This one is
    // deterministic, and it names the mechanism the race is made of - a hand-back must never take a
    // lock on a row it does not hand back.
    //
    // One worker holds a single Lease. A bystander worker holds every other Lease in the store. A rival
    // session takes an X lock on ONE bystander row and keeps it for the whole call. The hand-back for
    // the first worker then runs alone. A read that seeks ix_backwave_jobs_lease_owner never reads the
    // bystander row and returns at once. A read that scans the live Leases under UPDLOCK asks for a U
    // lock on that row and waits - the oversized footprint the deadlock is built from, caught here with
    // one writer and no timing window.
    [Fact]
    public async Task A_hand_back_never_locks_a_row_it_does_not_hand_back()
    {
        const int Bystanders = 200;
        var disposition = new RetryPolicy { MaxAttempts = 5, Backoff = _ => TimeSpan.FromMinutes(1) }.ToDisposition();
        var store = await SqlServerTestDatabase.CreateFreshStoreAsync();
        for (var i = 0; i <= Bystanders; i++)
        {
            await store.EnqueueAsync(new NewJob(Guid.NewGuid(), "t", "{}"u8.ToArray(), "default", T0), T0);
        }

        // The claim hands out oldest due first, so the bystander takes every job but the last one. It
        // takes them in passes, because one claim never returns more than Bounds.MaxClaimBatch rows.
        var bystanderLeases = new List<JobRecord>();
        while (bystanderLeases.Count < Bystanders)
        {
            var pass = await store.ClaimAsync(
                new ClaimRequest("bystander", ["default"], Bystanders - bystanderLeases.Count, Lease, T0));
            Assert.NotEmpty(pass);
            bystanderLeases.AddRange(pass);
        }

        var mine = await store.ClaimAsync(new ClaimRequest("mine", ["default"], 1, Lease, T0));
        Assert.Equal(Bystanders, bystanderLeases.Count);
        Assert.Single(mine);

        await using var rival = new SqlConnection(SqlServerTestDatabase.ConnectionString);
        await rival.OpenAsync();
        await using var rivalTx = (SqlTransaction)await rival.BeginTransactionAsync();

        Task<int> handBack;
        bool finishedWhileHeld;
        try
        {
            // XLOCK on one clustered row and nothing else, so the rival can never be half of a cycle.
            // The hand-back either avoids this row or waits on it, and waiting is the whole finding.
            await Execute(
                rival, rivalTx,
                "SELECT job_id FROM backwave.jobs WITH (XLOCK, ROWLOCK) WHERE job_id = @id",
                bystanderLeases[^1].JobId);

            handBack = Task.Run(() => store.RelinquishLeasesAsync("mine", T0, disposition).AsTask());
            finishedWhileHeld = await Task.WhenAny(handBack, Task.Delay(TimeSpan.FromSeconds(10))) == handBack;
        }
        finally
        {
            // Release the hostage whatever happened, so a blocked hand-back can finish and be awaited.
            await rivalTx.RollbackAsync();
        }

        var relinquished = await handBack.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.True(
            finishedWhileHeld,
            "The hand-back waited on a Lease held by another worker, so its UPDLOCK read reached past its "
            + "own batch. ix_backwave_jobs_lease_owner (schema v2) is missing, or the relinquish read no "
            + "longer seeks it.");
        Assert.Equal(1, relinquished);
    }

    // The two pins above are worth nothing unless a real absorbed deadlock would move the counter. This
    // provokes one deterministically and walks the whole chain: SQL Server picks the store's transaction
    // as the victim, the bounded retry replays it, StoreFaultCounter sees the loss, and the caller still
    // gets the outcome it asked for.
    //
    // The cycle. The store's outcome transaction takes X on job A, then parks at the report-outcome
    // failpoint - which sits between the terminal write and the child-latch lookup, so the lookup's read
    // of backwave.job_parents is still ahead of it. A rival session takes X on the (A -> child) edge that
    // lookup must read, then reaches for job A, which the store holds. Neither can move. The rival runs
    // at DEADLOCK_PRIORITY HIGH, so SQL Server kills the store - which is the whole point.
    [Fact]
    public async Task An_absorbed_deadlock_moves_the_counter_and_the_call_still_succeeds()
    {
        var parent = Guid.NewGuid();
        var atFailpoint = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var armed = 0;

        await SqlServerTestDatabase.CreateFreshStoreAsync();
        var store = new SqlServerJobStore(new SqlServerStoreOptions
        {
            ConnectionString = SqlServerTestDatabase.ConnectionString,
            // Park the FIRST pass only. The retry's replay has to run through to a commit for the call
            // to return at all, and by then the rival session is gone.
            FaultHook = async (name, _) =>
            {
                if (name == "report-outcome" && Interlocked.Exchange(ref armed, 1) == 0)
                {
                    atFailpoint.SetResult();
                    await release.Task;
                }
            },
        });

        await store.EnqueueAsync(new NewJob(parent, "t", "{}"u8.ToArray(), "default", T0), T0);
        var leased = await store.ClaimAsync(new ClaimRequest("w", ["default"], 1, Lease, T0));
        // A child edge is what gives the latch lookup a job_parents row to read. Without one the
        // failpoint still fires, but the lookup finds nothing to block on and no cycle forms.
        await store.EnqueueAsync(
            new NewJob(Guid.NewGuid(), "t", "{}"u8.ToArray(), "default", T0) { Parents = [parent] }, T0);

        using var faults = new StoreFaultCounter();
        var report = Task.Run(() => store.ReportOutcomesAsync(
            [new OutcomeReport(parent, "w", leased[0].Attempt, new JobOutcome.Success())], T0).AsTask());

        try
        {
            await using var rival = new SqlConnection(SqlServerTestDatabase.ConnectionString);
            await rival.OpenAsync();
            await Execute(rival, null, "SET DEADLOCK_PRIORITY HIGH");
            // The store now holds job A and is parked inside its transaction. Bounded, because an
            // unparked wait here would hang the whole run rather than fail this one test.
            await atFailpoint.Task.WaitAsync(TimeSpan.FromSeconds(30));

            await using var rivalTx = (SqlTransaction)await rival.BeginTransactionAsync();
            await Execute(rival, rivalTx,
                "SELECT child_id FROM backwave.job_parents WITH (XLOCK, ROWLOCK) WHERE parent_id = @id", parent);
            // Issued, deliberately not awaited: it blocks on the store's X on job A, which is the second
            // half of the cycle. The released store then reaches for the edge row above and closes it.
            var rivalBlocked = Execute(rival, rivalTx,
                "SELECT state FROM backwave.jobs WITH (XLOCK, ROWLOCK) WHERE job_id = @id", parent);
            release.SetResult();

            await rivalBlocked; // unblocks only once SQL Server kills the store and frees job A
            await rivalTx.RollbackAsync();
        }
        finally
        {
            // Whatever went wrong above, the store must not stay parked in its hook forever.
            release.TrySetResult();
        }

        // The caller is told nothing went wrong: the replay applied the outcome, and the deadlock shows
        // up only on the metric - which is exactly why the two tests above read it instead of the
        // call boundary.
        var results = await report;
        Assert.Equal(OutcomeResult.Applied, Assert.Single(results).Result);
        Assert.Equal(1, faults.Absorbed);
        Assert.Equal(0, faults.Terminal);
    }

    // The claim commits once per Queue, so the unit its bounded retry replays must be ONE Queue's
    // transaction. A wrap around the whole loop would re-run the Queues that already committed, and
    // their rows are Leased by then: the replay claims nothing for them, the caller never receives the
    // records, and the jobs sit Leased with their Attempt spent until the lease expires. That is the
    // defect this test pins, and nothing pinned it before.
    //
    // The cycle. The Queue transaction takes the queue-config application lock, then reads the
    // queue_limits row under UPDLOCK. A rival session holds X on that row, so the read blocks. The rival
    // then asks for the same application lock, which the store holds. Neither can move. The rival runs at
    // DEADLOCK_PRIORITY HIGH, so SQL Server kills the store with error 1205.
    //
    // The failpoint counts the Queue transactions that reach their commit. The first Queue reaches it
    // once, the second Queue dies before it and reaches it once on the replay. A whole-loop retry makes
    // that count three.
    [Fact]
    public async Task A_deadlocked_queue_replays_alone_and_the_queue_before_it_stays_claimed_once()
    {
        const string First = "alpha", Second = "beta";
        var firstJob = Guid.NewGuid();
        var secondJob = Guid.NewGuid();
        var commits = 0;

        await SqlServerTestDatabase.CreateFreshStoreAsync();
        var store = new SqlServerJobStore(new SqlServerStoreOptions
        {
            ConnectionString = SqlServerTestDatabase.ConnectionString,
            FaultHook = (name, _) =>
            {
                if (name == "claim")
                {
                    Interlocked.Increment(ref commits);
                }
                return Task.CompletedTask;
            },
        });

        await store.EnqueueAsync(new NewJob(firstJob, "t", "{}"u8.ToArray(), First, T0), T0);
        await store.EnqueueAsync(new NewJob(secondJob, "t", "{}"u8.ToArray(), Second, T0), T0);
        // The row the rival locks. Without a limit the Queue has no queue_limits row to read, and the
        // store skips both the lock and the read that the cycle needs.
        await store.SetConcurrencyLimitAsync(Second, 10, "test", T0);

        await using var rival = new SqlConnection(SqlServerTestDatabase.ConnectionString);
        await rival.OpenAsync();
        await Execute(rival, null, "SET DEADLOCK_PRIORITY HIGH");
        var rivalSession = (short)(await Scalar(rival, null, "SELECT @@SPID"))!;
        await using var rivalTx = (SqlTransaction)await rival.BeginTransactionAsync();
        await Execute(rivalTx.Connection, rivalTx,
            $"UPDATE backwave.queue_limits SET max_concurrent = max_concurrent WHERE queue = '{Second}'");

        using var faults = new StoreFaultCounter();
        var claim = Task.Run(() => store
            .ClaimAsync(new ClaimRequest("w", [First, Second], 10, Lease, T0)).AsTask());

        try
        {
            await WaitUntilBlockedBy(rivalSession);
            // Issued, deliberately not awaited: it blocks on the application lock the store took just
            // before it stalled on the row above, which closes the cycle.
            var rivalBlocked = Execute(rivalTx.Connection, rivalTx,
                """
                DECLARE @resource nvarchar(64) = CONVERT(nvarchar(64), HASHBYTES('SHA2_256', @queue), 2);
                DECLARE @rc int;
                EXEC @rc = sp_getapplock @Resource = @resource, @LockMode = 'Exclusive', @LockOwner = 'Transaction';
                """,
                queue: Second);
            await rivalBlocked.WaitAsync(TimeSpan.FromSeconds(60)); // granted once SQL Server kills the store
        }
        finally
        {
            // Whatever went wrong above, the replay must not stay blocked on this transaction forever.
            await rivalTx.RollbackAsync();
        }

        var result = await claim.WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(1, faults.Absorbed);
        Assert.Equal(0, faults.Terminal);
        Assert.Equal(2, Volatile.Read(ref commits));
        // Both Queues come back, each exactly once. Single throws on a duplicate and on an absence.
        Assert.Equal(2, result.Count);
        Assert.Equal(1, result.Single(job => job.JobId == firstJob).Attempt);
        Assert.Equal(1, result.Single(job => job.JobId == secondJob).Attempt);
        // The Transition Log is the record the caller cannot fake: one Leased entry means one claim.
        await using var reader = new SqlConnection(SqlServerTestDatabase.ConnectionString);
        await reader.OpenAsync();
        Assert.Equal(1, (int)(await Scalar(reader, null,
            "SELECT count(*) FROM backwave.job_transitions WHERE job_id = @id AND state = 2", firstJob))!);
    }

    // ── harness ─────────────────────────────────────────────────────────────────

    // One statement on the rival session. The Task comes back so a caller can issue a statement WITHOUT
    // awaiting it, which is how the blocked half of the cycle above is set up.
    private static async Task Execute(
        SqlConnection connection, SqlTransaction? transaction, string sql, Guid? id = null, string? queue = null,
        int? count = null)
    {
        await using var command = new SqlCommand(sql, connection, transaction);
        if (id is { } value)
        {
            command.Parameters.AddWithValue("id", value);
        }
        if (queue is not null)
        {
            command.Parameters.AddWithValue("queue", queue);
        }
        if (count is { } rows)
        {
            command.Parameters.AddWithValue("count", rows);
        }
        await command.ExecuteNonQueryAsync();
    }

    // The FK from job_transitions.job_id to jobs.job_id is checked by a plan the optimizer owns, and for
    // a batch that is large next to jobs it answers the check by scanning jobs - which asks for S on rows
    // the batch does not own. That exposure is older than the hand-back and no hint removes the scan. A
    // store that holds only the test's own jobs makes every batch large, so the deadlock test would
    // measure that plan instead of the lock footprint it names. This gives jobs a production-shaped row
    // count, so the check seeks and the batch locks only its own rows.
    //
    // The ballast sits on its own Queue, a year out, and never leaves Scheduled, so no claim, no sweep and
    // no hand-back in this class ever reads a ballast row. UPDATE STATISTICS is not decoration: it is what
    // invalidates the plan the empty table compiled, so the check is recompiled against the real count.
    private static async Task AddBallast(int rows)
    {
        await using var connection = new SqlConnection(SqlServerTestDatabase.ConnectionString);
        await connection.OpenAsync();
        await Execute(
            connection, transaction: null,
            """
            INSERT INTO backwave.jobs (job_id, wire_name, payload, queue, state, due_time)
            SELECT TOP (@count) NEWID(), 'ballast', 0x, 'ballast', 0, DATEADD(year, 1, SYSDATETIMEOFFSET())
            FROM sys.all_objects a CROSS JOIN sys.all_objects b;
            UPDATE STATISTICS backwave.jobs;
            """,
            count: rows);
    }

    // Polls until one session waits on a lock that <paramref name="session"/> holds. The rival closes the
    // deadlock cycle only after the store is already blocked, so this order is the whole setup.
    private static async Task WaitUntilBlockedBy(short session)
    {
        await using var watcher = new SqlConnection(SqlServerTestDatabase.ConnectionString);
        await watcher.OpenAsync();
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var blocked = (int)(await Scalar(watcher, null,
                $"SELECT count(*) FROM sys.dm_exec_requests WHERE blocking_session_id = {session}"))!;
            if (blocked > 0)
            {
                return;
            }
            await Task.Delay(25);
        }
        Assert.Fail($"No session blocked on {session} within 30 s, so the deadlock cycle was never set up.");
    }

    // One scalar query on a caller's session.
    private static async Task<object?> Scalar(
        SqlConnection connection, SqlTransaction? transaction, string sql, Guid? id = null)
    {
        await using var command = new SqlCommand(sql, connection, transaction);
        if (id is { } value)
        {
            command.Parameters.AddWithValue("id", value);
        }
        return await command.ExecuteScalarAsync();
    }

    /// <summary>
    /// Counts the store faults this test provokes, split transient (a deadlock the bounded retry
    /// absorbed) from terminal (anything the store gave up on).
    ///
    /// ATTRIBUTION. backwave.store.faults carries exactly one tag, backwave.store.fault_kind, so
    /// nothing on the measurement names error 1205. What makes "transient" mean "absorbed deadlock"
    /// here is the emit side: RetryOnDeadlockAsync is the only site that passes isTransient
    /// unconditionally, and every other site derives it from IsTransientStoreFault - a bare
    /// TimeoutException or DbException.IsTransient. Microsoft.Data.SqlClient 6.1.1 reports IsTransient
    /// false for the errors a healthy local server raises, 1205 and the -2 command timeout included, so
    /// a timeout in these tests counts TERMINAL and escapes as an unhandled SqlException rather than
    /// passing for a deadlock. Terminal is asserted zero alongside, which pins that split.
    ///
    /// If the store ever has to tell 1205 apart from another transient loss on the metric itself, the
    /// smallest change that would do it is a db.response.status_code tag carrying SqlException.Number -
    /// the OTel database convention's own attribute for a native error code - added where the counter
    /// is recorded in SqlServerDiagnostics.
    /// </summary>
    private sealed class StoreFaultCounter : IDisposable
    {
        private readonly Activity _marker;
        private readonly MeterListener _listener;
        private int _transient;
        private int _terminal;

        public StoreFaultCounter()
        {
            // The store's Meter is a process-wide static shared with every other test. This marker's
            // TraceId separates what this test provoked: the counter is recorded while the marker is
            // current (the store span, when a listener samples one, nests beneath it and inherits the
            // TraceId), and Task.Run below carries the ambient Activity with the execution context.
            _marker = new Activity("concurrent-maintenance");
            _marker.Start();
            _listener = new MeterListener
            {
                InstrumentPublished = (instrument, listener) =>
                {
                    if (instrument.Meter.Name == SqlServerDiagnostics.SourceName
                        && instrument.Name == "backwave.store.faults")
                    {
                        listener.EnableMeasurementEvents(instrument);
                    }
                },
            };
            _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            {
                if (Activity.Current?.TraceId != _marker.TraceId)
                {
                    return;
                }
                var kind = tags.ToArray()
                    .FirstOrDefault(tag => tag.Key == "backwave.store.fault_kind").Value as string;
                if (kind == "transient")
                {
                    Interlocked.Add(ref _transient, (int)value);
                }
                else
                {
                    Interlocked.Add(ref _terminal, (int)value);
                }
            });
            _listener.Start();
        }

        /// <summary>Deadlocks the store's bounded retry swallowed, which the caller never sees.</summary>
        public int Absorbed => Volatile.Read(ref _transient);

        /// <summary>Faults the store gave up on and rethrew.</summary>
        public int Terminal => Volatile.Read(ref _terminal);

        public void Dispose()
        {
            _listener.Dispose();
            _marker.Stop();
            _marker.Dispose();
        }
    }
}
