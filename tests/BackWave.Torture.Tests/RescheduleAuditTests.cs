using BackWave.Storage.InMemory;

namespace BackWave.Torture.Tests;

/// <summary>
/// The two reschedule checks of the post-drain audit, fed hand-made journals. Each check gets a hit,
/// and then each near miss its rules exclude, built as the hit plus the one entry (or the one changed
/// field) that must silence it - so a near miss that stays silent cannot be a check that never fires.
/// The store is empty, so the checks that compare the journal against the store report too; each fact
/// reads only the one invariant it is about.
/// </summary>
public sealed class RescheduleAuditTests
{
    private const ulong Seed = 0x2A;
    private const string Holder = "c00";
    private const string Rescheduler = "c01";
    private const string Other = "c02";
    private static readonly long Base = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).UtcTicks;
    private static readonly Guid Job = Guid.Parse("5a1d0d1e-0000-4000-8000-000000000001");

    // ---- ReschedulePlacedInLease --------------------------------------------------------------------
    //
    // The lease is definitely live from the claim's return (10 ms) to its expiry (1000 ms); the hit
    // reschedules inside that window, from 100 ms to 110 ms.

    private static JournalEntry LeaseClaim() => Claim(Holder, attempt: 1, t0: 0, t1: 10, leaseExpiry: 1000, now: 0);

    [Fact]
    public async Task PlacedInLease_RescheduleInsideTheLiveLease_IsReported()
    {
        var found = await AuditAsync(TortureInvariant.ReschedulePlacedInLease,
            LeaseClaim(), Reschedule(Rescheduler, t0: 100, t1: 110, due: 2000));

        var violation = Assert.Single(found);
        Assert.Equal(Job, violation.JobId);
    }

    [Fact]
    public async Task PlacedInLease_RescheduleThatStartedBeforeTheClaimReturned_IsNotReported()
        => Assert.Empty(await AuditAsync(TortureInvariant.ReschedulePlacedInLease,
            LeaseClaim(), Reschedule(Rescheduler, t0: 5, t1: 110, due: 2000)));

    [Fact]
    public async Task PlacedInLease_RescheduleThatReturnedAfterTheLeaseExpired_IsNotReported()
        => Assert.Empty(await AuditAsync(TortureInvariant.ReschedulePlacedInLease,
            LeaseClaim(), Reschedule(Rescheduler, t0: 990, t1: 1010, due: 2000)));

    [Fact]
    public async Task PlacedInLease_LeaseCutByAnOutcome_IsNotReported()
        => Assert.Empty(await AuditAsync(TortureInvariant.ReschedulePlacedInLease,
            LeaseClaim(),
            Entry(Holder, Ops.Outcome, t0: 50, t1: 60) with { Attempt = 1, Result = "Applied", Detail = "Succeeded" },
            Reschedule(Rescheduler, t0: 100, t1: 110, due: 2000)));

    [Fact]
    public async Task PlacedInLease_LeaseCutByARelinquishOfTheHolder_IsNotReported()
        => Assert.Empty(await AuditAsync(TortureInvariant.ReschedulePlacedInLease,
            LeaseClaim(),
            Entry(Other, Ops.RelinquishRequested, t0: 50, t1: 50) with { JobId = null, Detail = WorkerId(Holder) },
            Entry(Other, Ops.Relinquish, t0: 50, t1: 60) with { JobId = null, Result = "1", Detail = WorkerId(Holder) },
            Reschedule(Rescheduler, t0: 100, t1: 110, due: 2000)));

    [Fact]
    public async Task PlacedInLease_LeaseCutByAFaultOfTheHolder_IsNotReported()
        => Assert.Empty(await AuditAsync(TortureInvariant.ReschedulePlacedInLease,
            LeaseClaim(),
            Entry(Holder, Ops.TransientFault, t0: 200, t1: 200) with { JobId = null, Result = Ops.Outcome },
            Reschedule(Rescheduler, t0: 100, t1: 110, due: 2000)));

    [Fact]
    public async Task PlacedInLease_LeaseCutByAnotherClaimOfTheJob_IsNotReported()
        => Assert.Empty(await AuditAsync(TortureInvariant.ReschedulePlacedInLease,
            LeaseClaim(),
            Claim(Other, attempt: 2, t0: 50, t1: 60, leaseExpiry: 80, now: 50),
            Reschedule(Rescheduler, t0: 100, t1: 110, due: 2000)));

    [Fact]
    public async Task PlacedInLease_LeaseCutByAHeartbeatThatDidNotRenew_IsNotReported()
        => Assert.Empty(await AuditAsync(TortureInvariant.ReschedulePlacedInLease,
            LeaseClaim(),
            Entry(Holder, Ops.Heartbeat, t0: 50, t1: 60) with { Attempt = 1, Result = "Lost" },
            Reschedule(Rescheduler, t0: 100, t1: 110, due: 2000)));

    [Fact]
    public async Task PlacedInLease_LeaseCutByACancelThatTookTheJobAtOnce_IsNotReported()
        => Assert.Empty(await AuditAsync(TortureInvariant.ReschedulePlacedInLease,
            LeaseClaim(),
            Entry(Other, Ops.Cancel, t0: 50, t1: 60) with { Result = "CancelledImmediately" },
            Reschedule(Rescheduler, t0: 100, t1: 110, due: 2000)));

    // ---- ClaimedBeforeRescheduledDue ----------------------------------------------------------------
    //
    // The hit reschedules the job from 100 ms to 110 ms, to a due time of 2000 ms; a claim that starts
    // at 200 ms then leases attempt 1 with a now of 200 ms.

    private static JournalEntry Rescheduled() => Reschedule(Rescheduler, t0: 100, t1: 110, due: 2000);

    private static JournalEntry EarlyClaim(int attempt = 1, long t0 = 200, long now = 200)
        => Claim(Holder, attempt, t0, t1: t0 + 10, leaseExpiry: t0 + 5000, now);

    [Fact]
    public async Task BeforeRescheduledDue_ClaimAfterTheRescheduleBeforeItsDueTime_IsReported()
    {
        var found = await AuditAsync(TortureInvariant.ClaimedBeforeRescheduledDue, Rescheduled(), EarlyClaim());

        var violation = Assert.Single(found);
        Assert.Equal(Job, violation.JobId);
    }

    [Fact]
    public async Task BeforeRescheduledDue_LaterRescheduleInTheWindow_IsNotReported()
        => Assert.Empty(await AuditAsync(TortureInvariant.ClaimedBeforeRescheduledDue,
            Rescheduled(), Reschedule(Other, t0: 150, t1: 160, due: 0), EarlyClaim()));

    [Fact]
    public async Task BeforeRescheduledDue_RequeueInTheWindow_IsNotReported()
        => Assert.Empty(await AuditAsync(TortureInvariant.ClaimedBeforeRescheduledDue,
            Rescheduled(),
            Entry(Other, Ops.RequeueRequested, t0: 150, t1: 150),
            Entry(Other, Ops.Requeue, t0: 150, t1: 160) with { Result = "Requeued" },
            EarlyClaim()));

    [Fact]
    public async Task BeforeRescheduledDue_RequeueThatReturnedBeforeTheReschedule_IsStillReported()
        => Assert.Single(await AuditAsync(TortureInvariant.ClaimedBeforeRescheduledDue,
            Entry(Other, Ops.RequeueRequested, t0: 50, t1: 50),
            Entry(Other, Ops.Requeue, t0: 50, t1: 60) with { Result = "Requeued" },
            Rescheduled(), EarlyClaim()));

    [Fact]
    public async Task BeforeRescheduledDue_EnqueueOfTheSameIdInTheWindow_IsNotReported()
        => Assert.Empty(await AuditAsync(TortureInvariant.ClaimedBeforeRescheduledDue,
            Rescheduled(), Entry(Other, Ops.Enqueue, t0: 150, t1: 160) with { Result = "Ok" }, EarlyClaim()));

    [Fact]
    public async Task BeforeRescheduledDue_FaultNamingTheJobInTheWindow_IsNotReported()
        => Assert.Empty(await AuditAsync(TortureInvariant.ClaimedBeforeRescheduledDue,
            Rescheduled(),
            Entry(Other, Ops.Expire, t0: 130, t1: 140) with { JobId = null, Result = "0" },
            Entry(Other, Ops.TransientFault, t0: 150, t1: 150) with { Result = Ops.Requeue },
            EarlyClaim()));

    [Fact]
    public async Task BeforeRescheduledDue_RetryAttemptWhosePreviousAttemptWasClaimedBeforeTheReschedule_IsReported()
        => Assert.Single(await AuditAsync(TortureInvariant.ClaimedBeforeRescheduledDue,
            Claim(Other, attempt: 1, t0: 10, t1: 20, leaseExpiry: 50, now: 10),
            Rescheduled(), EarlyClaim(attempt: 2)));

    [Fact]
    public async Task BeforeRescheduledDue_RetryAttemptWithNoPreviousAttemptClaimedBeforeTheReschedule_IsNotReported()
        => Assert.Empty(await AuditAsync(TortureInvariant.ClaimedBeforeRescheduledDue,
            Claim(Other, attempt: 1, t0: 90, t1: 105, leaseExpiry: 120, now: 90),
            Rescheduled(), EarlyClaim(attempt: 2)));

    [Fact]
    public async Task BeforeRescheduledDue_ClaimThatStartedBeforeTheRescheduleReturned_IsNotReported()
        => Assert.Empty(await AuditAsync(TortureInvariant.ClaimedBeforeRescheduledDue,
            Rescheduled(), EarlyClaim(t0: 105)));

    [Fact]
    public async Task BeforeRescheduledDue_ClaimsNowWithinTheOneMillisecondMargin_IsNotReported()
        => Assert.Empty(await AuditAsync(TortureInvariant.ClaimedBeforeRescheduledDue,
            Rescheduled(), EarlyClaim(now: 1999)));

    [Fact]
    public async Task BeforeRescheduledDue_ClaimsNowOneTickPastTheMargin_IsReported()
    {
        var claim = EarlyClaim() with { OpNow = At(1999) - 1 };

        Assert.Single(await AuditAsync(TortureInvariant.ClaimedBeforeRescheduledDue, Rescheduled(), claim));
    }

    // ---- helpers ------------------------------------------------------------------------------------

    private static async Task<IReadOnlyList<TortureViolation>> AuditAsync(string invariant, params JournalEntry[] journal)
    {
        var auditor = new Auditor(
            new InMemoryJobStore(), new KeySpace(Seed), new TortureOptions { Adapter = TortureAdapter.Sqlite, Seed = Seed });
        var sink = new ViolationSink(TorturePhase.PostDrain);
        await auditor.AuditAsync(journal, sink, CancellationToken.None);
        return [.. sink.Snapshot().Where(v => v.Invariant == invariant)];
    }

    private static long At(long milliseconds) => Base + milliseconds * TimeSpan.TicksPerMillisecond;

    private static string WorkerId(string client) => $"torture-{Seed:x8}-{client}";

    private static JournalEntry Entry(string client, string op, long t0, long t1)
        => new() { Client = client, Op = op, T0 = At(t0), T1 = At(t1), JobId = Job };

    private static JournalEntry Claim(string client, int attempt, long t0, long t1, long leaseExpiry, long now)
        => Entry(client, Ops.Claim, t0, t1) with
        {
            Attempt = attempt, Queue = "tq-gen-0", Wire = "torture.work.0",
            LeaseExpiry = At(leaseExpiry), DueTime = At(now), OpNow = At(now),
        };

    private static JournalEntry Reschedule(string client, long t0, long t1, long due)
        => Entry(client, Ops.Reschedule, t0, t1) with { Result = "Rescheduled", DueTime = At(due), OpNow = At(t0) };
}
