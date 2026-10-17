# BackWave.EntityFrameworkCore

EF Core integration for [BackWave](https://backwave.app). Enqueue a job inside your ambient unit of
work, and it commits or rolls back atomically with your `DbContext` changes. There's no outbox to poll
and nothing to reconcile between two writes.

```csharp
using BackWave.EntityFrameworkCore; // the DbContext enqueue overload

await using var transaction = await db.Database.BeginTransactionAsync();

db.Orders.Add(order);
await db.SaveChangesAsync();

// The job rides on the DbContext's open transaction: it lands only if the order does.
var jobId = await client.EnqueueAsync(new SendInvoice(order.Id), db, dueTime: DateTimeOffset.UtcNow);

await transaction.CommitAsync(); // order + job commit together
```

If the transaction rolls back, the job is never enqueued. If it commits, the job is durably queued in
the same atomic step as the write that justifies it.

## Reschedule in the same transaction

Move a waiting job's due time in the same unit of work as your own change. The new due time commits
or rolls back with your `DbContext` changes.

```csharp
using BackWave.EntityFrameworkCore; // the DbContext reschedule overload

await using var tx = await db.Database.BeginTransactionAsync();
campaign.SendAt = newSendAt;
await db.SaveChangesAsync();

var result = await backWaveOperator.RescheduleAsync(campaign.SendJobId, newSendAt, userId, db);
if (result == RescheduleResult.NotReschedulable)
{
    await tx.RollbackAsync(); // the campaign is already sending or sent
    return;
}
await tx.CommitAsync(); // the campaign date and the job's due time commit together
```

Keep the transaction short. The job row stays locked until you commit. On SQLite, the transaction
holds the database write lock.

## Notes

- Requires an **open transaction** on the `DbContext`; the extension reads it and throws if there is none.
- Works with any relational BackWave adapter whose tables live in the same database as your `DbContext`
  (**BackWave.Postgres**, **BackWave.SqlServer**, or a co-resident **BackWave.Sqlite** file).
- For an enqueue that is *not* part of a unit of work, call `BackWaveClient.EnqueueAsync` directly.
- For a reschedule that is *not* part of a unit of work, call `BackWaveOperator.RescheduleAsync` directly.

Full documentation: https://backwave.app
