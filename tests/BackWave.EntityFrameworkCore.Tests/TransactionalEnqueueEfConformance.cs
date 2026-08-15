using System.Text.Json.Serialization;
using BackWave.Jobs;
using BackWave.Storage;
using Microsoft.EntityFrameworkCore;

namespace BackWave.EntityFrameworkCore.Tests;

// ── Scope note (issue 0205) ─────────────────────────────────────────────────
// BackWave.EntityFrameworkCore is a transactional-enqueue SHIM: it owns no IJobStore and adds a
// single DbContext-aware overload that reads the context's ambient transaction and hands it to the
// underlying adapter's EnqueueAsync. So the full 117-fact Storage Contract Conformance Suite does
// NOT apply through this package — only the transactional-enqueue clause family does, and it must be
// certified through the DbContext path a consumer actually uses (not just the raw adapter). This
// abstract base mirrors that clause family with the EF DbContext as the transaction owner; the
// provider subclasses run it against live Postgres and SQL Server. Do NOT "fix" this by wiring the
// whole ConformanceSuite here — the shim's surface is one overload, and this file is its contract.

/// <summary>A stand-in business entity written in the same unit of work as the enqueue.</summary>
public sealed class Order
{
    public Guid Id { get; set; }
    public required string Customer { get; set; }
}

/// <summary>The consumer's own DbContext — the unit of work the enqueue joins.</summary>
public sealed class OrdersDbContext(DbContextOptions<OrdersDbContext> options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.Entity<Order>().ToTable("orders", "app");
}

public sealed record OrderConfirmation(Guid OrderId);

public sealed class OrderConfirmationHandler : IJobHandler<OrderConfirmation>
{
    public Task HandleAsync(OrderConfirmation job, JobContext context, CancellationToken cancellationToken)
        => Task.CompletedTask;
}

[JsonSerializable(typeof(OrderConfirmation))]
internal sealed partial class EfJsonContext : JsonSerializerContext;

/// <summary>
/// The transactional-enqueue clause family, run through the EF Core <see cref="DbContext"/> path a
/// consumer actually uses (not the raw adapter): the business write and the job commit or roll back
/// together, savepoints scope the job like any other row, and an enqueue with no ambient transaction
/// has a defined, loud result. Provider subclasses bind this to a live database.
/// </summary>
public abstract class TransactionalEnqueueEfConformance : IAsyncLifetime
{
    /// <summary>The fixed instant every test starts from.</summary>
    protected static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly JobRegistry Registry = new(
    [
        JobRegistration.Create<OrderConfirmation, OrderConfirmationHandler>(
            "order-confirmation", EfJsonContext.Default.OrderConfirmation),
    ]);

    private IJobStore _store = null!;
    private BackWaveClient _client = null!;

    // ── Provider seam ───────────────────────────────────────────────────────
    // A subclass binds these to a live Postgres or SQL Server test database dedicated to the EF
    // suite (it shares a server, never tables, with the adapter conformance suite).

    /// <summary>Ensures the dedicated EF test database exists, the business schema and the BackWave
    /// schema are migrated, and every table this suite touches is empty. Called before each test.</summary>
    protected abstract ValueTask ResetDatabaseAsync();

    /// <summary>Opens a fresh <see cref="OrdersDbContext"/> on the provider under test.</summary>
    protected abstract OrdersDbContext CreateContext();

    /// <summary>
    /// Builds a store on the dedicated EF test database. When <paramref name="faultOnEnqueue"/> is
    /// true, the store throws mid-enqueue (after the job row is written, before the caller commits) —
    /// the sabotage that must leave nothing behind.
    /// </summary>
    protected abstract IJobStore CreateStore(bool faultOnEnqueue = false);

    /// <summary>The exception the fault-armed store throws mid-enqueue.</summary>
    protected sealed class EnqueueSabotage() : Exception("sabotaged mid-enqueue");

    /// <summary>Arms <see cref="CreateStore"/>'s fault hook: throws <see cref="EnqueueSabotage"/> at
    /// the "enqueue" failpoint, which the adapters fire after the job row and before the caller commits.</summary>
    protected static Func<string, CancellationToken, Task> SabotageEnqueue { get; } =
        (name, _) => name == "enqueue" ? throw new EnqueueSabotage() : Task.CompletedTask;

    public async Task InitializeAsync()
    {
        await ResetDatabaseAsync();
        _store = CreateStore();
        _client = new BackWaveClient(_store, Registry);
    }

    public async Task DisposeAsync()
    {
        if (_store is IAsyncDisposable disposable)
        {
            await disposable.DisposeAsync();
        }
    }

    // ── §5.1 Transactional Enqueue, through the EF unit of work ───────────────

    /// <summary>
    /// Clause 5.1 (commit): committing the caller's transaction publishes the business write and the
    /// job together — the job lands Scheduled and is claimable through the normal pipeline, and was
    /// invisible to the rest of the cluster until the commit.
    /// </summary>
    [Fact]
    public async Task Commit_PublishesTheBusinessWriteAndTheJob_Atomically()
    {
        var orderId = Guid.NewGuid();
        Guid jobId;
        await using (var context = CreateContext())
        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            context.Orders.Add(new Order { Id = orderId, Customer = "grace" });
            await context.SaveChangesAsync();

            jobId = await _client.EnqueueAsync(new OrderConfirmation(orderId), context, dueTime: T0);

            // Invisible to committed reads until the unit of work commits.
            Assert.Null(await _store.GetJobAsync(jobId));

            await transaction.CommitAsync();
        }

        await using var check = CreateContext();
        Assert.Single(check.Orders, o => o.Id == orderId);
        Assert.Equal(JobState.Scheduled, (await _store.GetJobAsync(jobId))!.State);

        var claimed = await _store.ClaimAsync(new ClaimRequest("w1", ["default"], 1, TimeSpan.FromMinutes(1), T0));
        Assert.Equal(jobId, Assert.Single(claimed).JobId);
    }

    /// <summary>
    /// Clause 5.1 (rollback): rolling back the caller's transaction means the job never existed —
    /// neither the business write nor the job lands, closing the outbox crash window with no orphan.
    /// </summary>
    [Fact]
    public async Task Rollback_DiscardsTheBusinessWriteAndTheJob_Together()
    {
        Guid jobId;
        await using (var context = CreateContext())
        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            context.Orders.Add(new Order { Id = Guid.NewGuid(), Customer = "ada" });
            await context.SaveChangesAsync();

            jobId = await _client.EnqueueAsync(new OrderConfirmation(Guid.NewGuid()), context, dueTime: T0);

            await transaction.RollbackAsync();
        }

        await using var check = CreateContext();
        Assert.Empty(check.Orders);
        Assert.Null(await _store.GetJobAsync(jobId)); // no orphan job survives the rollback
    }

    /// <summary>
    /// Nested-transaction behavior: the job is plain transactional data that honors savepoints. A job
    /// enqueued before a savepoint survives a rollback to that savepoint; one enqueued after it is
    /// erased with everything else past the savepoint — exactly as an ordinary row would be.
    /// </summary>
    [Fact]
    public async Task UnderASavepoint_TheJobHonorsTheSavepointBoundary_LikeAnyRow()
    {
        Guid kept;
        Guid rolledBack;
        await using (var context = CreateContext())
        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            kept = await _client.EnqueueAsync(new OrderConfirmation(Guid.NewGuid()), context, dueTime: T0);

            await transaction.CreateSavepointAsync("after_first");
            rolledBack = await _client.EnqueueAsync(new OrderConfirmation(Guid.NewGuid()), context, dueTime: T0);
            await transaction.RollbackToSavepointAsync("after_first");

            await transaction.CommitAsync();
        }

        Assert.Equal(JobState.Scheduled, (await _store.GetJobAsync(kept))!.State); // before the savepoint: survives
        Assert.Null(await _store.GetJobAsync(rolledBack)); // after the savepoint: erased
    }

    /// <summary>
    /// Enqueue-outside-transaction: with no ambient transaction the shim throws loudly and names the
    /// fix rather than silently degrading to a plain, non-atomic enqueue — and writes nothing.
    /// </summary>
    [Fact]
    public async Task WithoutAnOpenTransaction_ThrowsLoudly_AndNamesTheFix()
    {
        await using var context = CreateContext();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await _client.EnqueueAsync(new OrderConfirmation(Guid.NewGuid()), context, dueTime: T0));

        Assert.Contains("BeginTransaction", exception.Message);
        Assert.Empty(context.Orders); // nothing enqueued, nothing written
    }

    /// <summary>
    /// Enqueue-after-dispose: once the transaction is disposed the context has no ambient transaction,
    /// so the shim gives the same loud, defined result as the no-transaction case — it never enqueues
    /// against a dead transaction.
    /// </summary>
    [Fact]
    public async Task AfterTheTransactionIsDisposed_ThrowsLoudly_AndWritesNothing()
    {
        await using var context = CreateContext();
        var transaction = await context.Database.BeginTransactionAsync();
        await transaction.DisposeAsync(); // rolls back and clears the ambient transaction

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await _client.EnqueueAsync(new OrderConfirmation(Guid.NewGuid()), context, dueTime: T0));

        Assert.Contains("BeginTransaction", exception.Message);
        Assert.Empty(await _store.ClaimAsync(new ClaimRequest("w1", ["default"], 1, TimeSpan.FromMinutes(1), T0)));
    }

    /// <summary>
    /// Rollback-mid-enqueue sabotage: a fault fires after the job row is written but before the caller
    /// commits. The caller's rollback must leave nothing — no orphan job, no business write.
    /// </summary>
    [Fact]
    public async Task Sabotage_FaultBeforeCommit_LeavesNoJobAndNoBusinessWrite()
    {
        var sabotaged = CreateStore(faultOnEnqueue: true);
        try
        {
            var client = new BackWaveClient(sabotaged, Registry);
            await using (var context = CreateContext())
            await using (var transaction = await context.Database.BeginTransactionAsync())
            {
                context.Orders.Add(new Order { Id = Guid.NewGuid(), Customer = "hopper" });
                await context.SaveChangesAsync();

                await Assert.ThrowsAsync<EnqueueSabotage>(async () =>
                    await client.EnqueueAsync(new OrderConfirmation(Guid.NewGuid()), context, dueTime: T0));

                await transaction.RollbackAsync();
            }
        }
        finally
        {
            if (sabotaged is IAsyncDisposable disposable)
            {
                await disposable.DisposeAsync();
            }
        }

        await using var check = CreateContext();
        Assert.Empty(check.Orders); // the business write rolled back with the failed enqueue
        Assert.Empty(await _store.ClaimAsync(new ClaimRequest("w1", ["default"], 8, TimeSpan.FromMinutes(1), T0)));
    }

    // ── The shim's honest escape hatches ──────────────────────────────────────

    /// <summary>
    /// The plain, non-transactional enqueue stays available on the base client API — the separately
    /// named opt-in, with no DbContext overload, so a non-atomic enqueue is always a deliberate choice.
    /// </summary>
    [Fact]
    public async Task NonTransactionalEnqueue_RemainsAvailable_ViaTheBaseClientApi()
    {
        var jobId = await _client.EnqueueAsync(new OrderConfirmation(Guid.NewGuid()), dueTime: T0);
        Assert.Equal(JobState.Scheduled, (await _store.GetJobAsync(jobId))!.State);
    }

    /// <summary>
    /// The EF integration owns no IJobStore and never intercepts ReportOutcomesAsync, so an
    /// EF-configured store IS the provider store and inherits its native batch primitive unchanged:
    /// enqueue two jobs through the EF unit of work, claim them, then report a batch mixing a live row
    /// with a stale one (wrong attempt) — the per-row fence and the input-order results are exactly
    /// the provider's.
    /// </summary>
    [Fact]
    public async Task EfConfiguredStore_InheritsTheProviderBatchOutcomePrimitive()
    {
        Guid first;
        Guid second;
        await using (var context = CreateContext())
        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            first = await _client.EnqueueAsync(new OrderConfirmation(Guid.NewGuid()), context, dueTime: T0);
            second = await _client.EnqueueAsync(new OrderConfirmation(Guid.NewGuid()), context, dueTime: T0);
            await transaction.CommitAsync();
        }

        var claimed = await _store.ClaimAsync(new ClaimRequest("w1", ["default"], 2, TimeSpan.FromMinutes(1), T0));
        var live = claimed.Single(j => j.JobId == first);
        var stale = claimed.Single(j => j.JobId == second);

        var results = await _store.ReportOutcomesAsync(
        [
            new OutcomeReport(live.JobId, "w1", live.Attempt, new JobOutcome.Success()),
            new OutcomeReport(stale.JobId, "w1", stale.Attempt + 1, new JobOutcome.Success()),
        ], T0);

        Assert.Equal(new OutcomeReportResult(live.JobId, OutcomeResult.Applied), results[0]);
        Assert.Equal(new OutcomeReportResult(stale.JobId, OutcomeResult.StaleLease), results[1]);
        Assert.Equal(JobState.Succeeded, (await _store.GetJobAsync(first))!.State);
        Assert.Equal(JobState.Leased, (await _store.GetJobAsync(second))!.State); // untouched by the fenced row
    }
}
