using BackWave.Core;
using BackWave.Operations;
using BackWave.Storage;

namespace BackWave.Demo;

/// <summary>
/// Fills a fresh instance on boot so a visitor who lands seconds after a container recycle already
/// sees a busy, production-shaped dashboard: a Succeeded backlog and full Queue-depths table, a
/// sustained "Executing now" pool, a Scheduled future backlog, a sprinkle of Dead-Lettered and
/// Quarantined failures, Workflows across success/running/failed, a set of Recurring Schedules, and a
/// 'limited' Queue at its Concurrency Limit. From there the recurring <c>generate-workload</c> job
/// tops everything up every minute; this seed is only the cold-start baseline.
/// </summary>
public static class DemoSeed
{
    private static readonly Random Rng = Random.Shared;

    /// <summary>Runs the baseline seed against the live store. Called once at startup.</summary>
    public static async Task RunAsync(
        BackWaveClient client,
        IJobStore store,
        BackWaveOperator op,
        string actor,
        int completed = 800,
        int inFlight = 120,
        int scheduled = 160,
        int failures = 32,
        int quarantine = 10,
        int workflows = 20,
        int limited = 30)
    {
        var now = DateTimeOffset.UtcNow;

        // History: a large backlog of instant, Succeeded work across every Queue (delayMs = 0).
        for (var i = 0; i < completed; i++)
        {
            switch (i % 6)
            {
                case 0: await client.EnqueueAsync(new Greet(DemoWorkloads.Person(), 0), now); break;
                case 1: await client.EnqueueAsync(new Process($"batch-{i}", 0), now); break;
                case 2: await client.EnqueueAsync(new WeightedWork($"hi-{i}", 0), now, queue: "high"); break;
                case 3: await client.EnqueueAsync(new WeightedWork($"lo-{i}", 0), now, queue: "low"); break;
                case 4:
                    await client.EnqueueAsync(
                        new OrderNotification(DemoWorkloads.OrderRef(), DemoWorkloads.Email(), DemoWorkloads.Channel(),
                            Rng.Next(1, 6), DemoWorkloads.Amount(), false), now);
                    break;
                default:
                    await client.EnqueueAsync(new TaggedReport(DemoWorkloads.Tenant(), DemoWorkloads.Amount()), now,
                        tags: DemoWorkloads.TenantTags());
                    break;
            }
        }

        // Executing now: long-running jobs that hold their Lease for a good while so the pool is full
        // immediately; the overflow waits as Scheduled (a healthy-looking backlog).
        for (var i = 0; i < inFlight; i++)
        {
            var holdMs = Rng.Next(90_000, 240_000);
            switch (i % 4)
            {
                case 0: await client.EnqueueAsync(new Greet(DemoWorkloads.Person(), holdMs), now); break;
                case 1: await client.EnqueueAsync(new Process($"stream-{i}", holdMs), now); break;
                case 2: await client.EnqueueAsync(new WeightedWork($"hi-{i}", holdMs), now, queue: "high"); break;
                default: await client.EnqueueAsync(new WeightedWork($"lo-{i}", holdMs), now, queue: "low"); break;
            }
        }

        // Concurrency Limit: many 'limited-work' jobs (each ~2s, Queue capped at 1) so it sits at limit.
        for (var i = 0; i < limited; i++)
        {
            await client.EnqueueAsync(new LimitedWork($"slot-{i}"), now);
        }

        // Scheduled: future-due work over the next ~40 minutes; a slice is cancelled below.
        var futureIds = new List<Guid>();
        for (var i = 0; i < scheduled; i++)
        {
            var due = now.AddSeconds(Rng.Next(30, 2_400));
            var id = (i % 3) switch
            {
                0 => await client.EnqueueAsync(new Greet(DemoWorkloads.Person(), 0), due),
                1 => await client.EnqueueAsync(
                    new OrderNotification(DemoWorkloads.OrderRef(), DemoWorkloads.Email(), DemoWorkloads.Channel(),
                        Rng.Next(1, 6), DemoWorkloads.Amount(), false), due),
                _ => await client.EnqueueAsync(new TaggedReport(DemoWorkloads.Tenant(), DemoWorkloads.Amount()), due,
                    tags: DemoWorkloads.TenantTags()),
            };
            futureIds.Add(id);
        }

        // Cancelled: cancel a handful of future-scheduled jobs through the Operator, so the Cancelled
        // stat is non-zero and each cancel lands in the audit log with its actor.
        foreach (var id in futureIds.Take(Math.Min(15, futureIds.Count)))
        {
            await op.CancelJobAsync(id, actor);
        }

        // Dead-Lettered: half always-failing 'flaky', half order-notifications that throw. Both on
        // 'low' (3 attempts) and land in "Needs attention".
        for (var i = 0; i < failures; i++)
        {
            if (i % 2 == 0)
            {
                await client.EnqueueAsync(new Flaky($"nightly-export-{i}"), now);
            }
            else
            {
                await client.EnqueueAsync(
                    new OrderNotification(DemoWorkloads.OrderRef(), DemoWorkloads.Email(), DemoWorkloads.Channel(),
                        Rng.Next(1, 6), DemoWorkloads.Amount(), true), now);
            }
        }

        // Quarantined: unregistered Wire Names pushed straight through the store.
        for (var i = 0; i < quarantine; i++)
        {
            await store.EnqueueAsync(
                new NewJob(Guid.NewGuid(), DemoWorkloads.Ghost(), "{}"u8.ToArray(), i % 2 == 0 ? "critical" : "bulk", now),
                now: now);
        }

        // Workflows: a spread of shapes and outcomes so the tab has running, succeeded, and failed graphs,
        // including the all-features Workflows v2 checkout (alternating the express / standard arm).
        for (var i = 0; i < workflows; i++)
        {
            switch (i % 6)
            {
                case 0:
                case 1: await DemoWorkloads.OrderFulfillmentAsync(client, fail: false); break;
                case 2: await DemoWorkloads.OrderFulfillmentAsync(client, fail: true); break;
                case 3: await DemoWorkloads.FanOutFanInAsync(client); break;
                case 4: await DemoWorkloads.CheckoutAsync(client, expedite: i % 12 == 4); break;
                default: await DemoWorkloads.JobOutputAsync(client); break;
            }
        }

        // Recurring Schedules: a realistic operational set. A couple are triggered now so they show a
        // recorded run, and No-Overlap / Catch-Up variants exercise those columns. 'heartbeat' also
        // keeps minting fresh work every minute alongside the generator.
        await client.UpsertRecurringAsync(
            "nightly-billing-close", Cron.Daily(2), new TaggedReport("acme", 4820m), queue: "bulk");
        await client.UpsertRecurringAsync(
            "hourly-metrics-rollup", Cron.Hourly(), new Process("metrics-rollup", 0), queue: "bulk", noOverlap: true);
        await client.UpsertRecurringAsync(
            "warm-cache", Cron.EveryMinutes(5), new Greet("cache-warmer", 0), queue: "critical", catchUp: CatchUpPolicy.Coalesce);
        await client.UpsertRecurringAsync(
            "heartbeat", Cron.EveryMinute(), new Greet("heartbeat", 0), queue: "critical");
        await client.UpsertRecurringAsync(
            "weekly-digest", Cron.Weekly(DayOfWeek.Monday, 8), new OrderNotification("DIGEST", "ops@acme.example.com", "email", 0, 0m, false), queue: "low");
        await op.TriggerScheduleNowAsync("heartbeat", actor);
        await op.TriggerScheduleNowAsync("warm-cache", actor);
    }
}
