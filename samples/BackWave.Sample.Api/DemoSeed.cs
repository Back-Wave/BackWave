using BackWave;
using BackWave.Core;
using BackWave.Generated;
using BackWave.Jobs;
using BackWave.Operations;
using BackWave.Pro;
using BackWave.Storage;

namespace BackWave.Sample.Api;

/// <summary>
/// Fills a fresh sample instance with a large, varied, production-shaped workload so the dashboard
/// looks like a busy real deployment for marketing screenshots. It seeds every panel at once: a big
/// Succeeded backlog and full Queue-depths table, a sustained "Executing now" pool (long-running
/// jobs that hold their Lease for a minute or two), a Scheduled future backlog, a realistic sprinkle
/// of Dead-Lettered and Quarantined failures, several Workflows across success/running/failed, a set
/// of Recurring Schedules, tenant-tagged reports for the facets, and a "limited" Queue sitting at its
/// Concurrency Limit. Terminal jobs are retained for 24h (Succeeded) / 14d (failures) with no drain
/// pump in the sample, so everything it enqueues stays on screen while you take shots.
///
/// It is safe to call more than once — a second call tops up the in-flight pool and adds more history.
/// </summary>
public static class DemoSeed
{
    // Realistic-looking label pools so the tables don't read as obviously synthetic.
    private static readonly string[] Tenants =
        ["acme", "globex", "initech", "umbrella", "hooli", "wonka", "stark", "wayne"];
    private static readonly string[] People =
        ["ava", "liam", "noah", "mia", "omar", "zoe", "priya", "kai", "sana", "leo", "ruby", "theo"];
    private static readonly string[] Channels = ["email", "sms", "push", "webhook"];
    private static readonly string[] GhostWireNames =
        ["legacy-invoice-v1", "sync-crm", "ghost-job", "retired-report", "orphaned-webhook", "v0-notifier"];

    private static readonly Random Rng = Random.Shared;

    /// <summary>Runs the full seed against the live store and returns a summary of what it created.</summary>
    public static async Task<object> RunAsync(
        BackWaveClient client,
        IJobStore store,
        BackWaveOperator op,
        string actor,
        int completed,
        int inFlight,
        int scheduled,
        int failures,
        int quarantine,
        int workflows,
        int limited)
    {
        var now = DateTimeOffset.UtcNow;

        // ── History: a large backlog of instant, Succeeded work spread across every Queue. This is
        //    what fills the Succeeded stat and the bulk of the Queue-depths table. delayMs = 0 so each
        //    one completes almost immediately as the pools chew through them.
        for (var i = 0; i < completed; i++)
        {
            switch (i % 6)
            {
                case 0:
                    await client.EnqueueAsync(new Greet(Person(), 0), now);
                    break;
                case 1:
                    await client.EnqueueAsync(new Process($"batch-{i}", 0), now);
                    break;
                case 2:
                    await client.EnqueueAsync(new WeightedWork($"hi-{i}", 0), now, queue: "high");
                    break;
                case 3:
                    await client.EnqueueAsync(new WeightedWork($"lo-{i}", 0), now, queue: "low");
                    break;
                case 4:
                    await client.EnqueueAsync(
                        new OrderNotification(OrderRef(), Email(), Channel(), Rng.Next(1, 6), Amount(), false), now);
                    break;
                default:
                    // Tenant-tagged reports drive the Tag pills, /monitor/tagged, and /monitor/facet.
                    var tenant = Tenant();
                    var tags = JobTags.Empty.WithTag("tenant", tenant);
                    if (Rng.Next(4) == 0)
                    {
                        tags = tags.WithLabel("priority");
                    }

                    await client.EnqueueAsync(new TaggedReport(tenant, Amount()), now, tags: tags);
                    break;
            }
        }

        // ── Executing now: long-running jobs that hold their Lease for a good while, so a screenshot
        //    catches a full pool. The Worker Groups auto-heartbeat (~every 20s), so a multi-minute hold
        //    keeps its Lease renewed rather than lapsing; the pool stays full for the whole window while
        //    the overflow waits as Scheduled (a healthy-looking backlog).
        for (var i = 0; i < inFlight; i++)
        {
            var holdMs = Rng.Next(90_000, 240_000);
            switch (i % 4)
            {
                case 0: await client.EnqueueAsync(new Greet(Person(), holdMs), now); break;
                case 1: await client.EnqueueAsync(new Process($"stream-{i}", holdMs), now); break;
                case 2: await client.EnqueueAsync(new WeightedWork($"hi-{i}", holdMs), now, queue: "high"); break;
                default: await client.EnqueueAsync(new WeightedWork($"lo-{i}", holdMs), now, queue: "low"); break;
            }
        }

        // ── Concurrency Limit demo: many 'limited-work' jobs (each sleeps ~2s, Queue capped at 1) so
        //    the Queue sits at its limit with a visible backlog — lights up the "queues at concurrency
        //    limit" health card.
        for (var i = 0; i < limited; i++)
        {
            await client.EnqueueAsync(new LimitedWork($"slot-{i}"), now);
        }

        // ── Scheduled: future-due work spread over the next ~40 minutes. Populates the Scheduled stat
        //    and the future side of the Queue-depths table. A slice of the ids is cancelled below.
        var futureIds = new List<Guid>();
        for (var i = 0; i < scheduled; i++)
        {
            var due = now.AddSeconds(Rng.Next(30, 2_400));
            var id = (i % 3) switch
            {
                0 => await client.EnqueueAsync(new Greet(Person(), 0), due),
                1 => await client.EnqueueAsync(
                    new OrderNotification(OrderRef(), Email(), Channel(), Rng.Next(1, 6), Amount(), false), due),
                _ => await client.EnqueueAsync(new TaggedReport(Tenant(), Amount()), due, tags: JobTags.Empty.WithTag("tenant", Tenant())),
            };
            futureIds.Add(id);
        }

        // ── Cancelled: cancel a handful of the future-scheduled jobs through the Operator, so the
        //    Cancelled stat is non-zero and each cancel lands in the audit log with its actor.
        var cancelled = 0;
        foreach (var id in futureIds.Take(Math.Min(15, futureIds.Count)))
        {
            await op.CancelJobAsync(id, actor);
            cancelled++;
        }

        // ── Dead-Lettered: half always-failing 'flaky', half order-notifications that throw. Both run
        //    on 'low' (3 attempts, ~1.5s) and land in "Needs attention".
        for (var i = 0; i < failures; i++)
        {
            if (i % 2 == 0)
            {
                await client.EnqueueAsync(new Flaky($"nightly-export-{i}"), now);
            }
            else
            {
                await client.EnqueueAsync(
                    new OrderNotification(OrderRef(), Email(), Channel(), Rng.Next(1, 6), Amount(), true), now);
            }
        }

        // ── Quarantined: unregistered Wire Names pushed straight through the store — no handler can
        //    route them, so the pump Quarantines each one (a routing failure, not an execution one).
        for (var i = 0; i < quarantine; i++)
        {
            await store.EnqueueAsync(
                new NewJob(Guid.NewGuid(), Ghost(), "{}"u8.ToArray(), i % 2 == 0 ? "critical" : "bulk", now),
                now: now);
        }

        // ── Workflows: a spread of shapes and outcomes so the Workflows tab has running, succeeded,
        //    and failed graphs. The failing order-fulfillment ones also cascade Cancels onto their
        //    downstream members (failure dominates), adding to the Cancelled stat.
        for (var i = 0; i < workflows; i++)
        {
            switch (i % 5)
            {
                case 0:
                case 1:
                    await OrderFulfillment(client, fail: false);
                    break;
                case 2:
                    await OrderFulfillment(client, fail: true);
                    break;
                case 3:
                    await FanOutFanIn(client);
                    break;
                default:
                    await JobOutput(client);
                    break;
            }
        }

        // ── Recurring Schedules: a realistic operational set. A couple are triggered now so they show
        //    a recorded run, and No-Overlap / Catch-Up variants exercise those columns.
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

        return new
        {
            seeded = new
            {
                completed,
                inFlight,
                scheduled,
                cancelled,
                deadLettered = failures,
                quarantined = quarantine,
                workflows,
                limited,
                schedules = 5,
            },
            note =
                "Dashboard seeded. Give the workers ~30–60s to churn, then open /backwave — the Overview stat row, "
                + "Queue depths, Executing now, and Needs attention are all populated. The long-running in-flight jobs "
                + "hold their Lease for a couple of minutes (auto-heartbeated), so the Executing-now pool stays full "
                + "for that window; call this again to top it back up before another screenshot.",
        };
    }

    private static async Task OrderFulfillment(BackWaveClient client, bool fail)
    {
        var orderRef = OrderRef();
        var amount = Amount();
        var items = Rng.Next(1, 6);
        await client.Workflow($"order-fulfillment {orderRef}")
            .Then(new ValidateOrder(orderRef))
            .Then(new ChargePayment(orderRef, amount, fail))                       // fan-out branch A of validate
            .Then(new ReserveInventory(orderRef, items), after: [typeof(ValidateOrder)]) // fan-out branch B of validate
            .Then(new PackShipment(orderRef), after: [typeof(ChargePayment), typeof(ReserveInventory)]) // fan-in
            .Then(new OrderNotification(orderRef, Email(), "email", items, amount, false)) // tail on pack
            .EnqueueAsync();
    }

    private static async Task FanOutFanIn(BackWaveClient client)
    {
        var label = $"{Person()}-{Rng.Next(100, 999)}";
        await client.Workflow($"fan-out/fan-in {label}")
            .Then(new DiamondA($"a-{label}", 600))
            .Then(new DiamondB1($"b1-{label}", 600))                              // child of a
            .Then(new DiamondB2($"b2-{label}", 600), after: [typeof(DiamondA)])   // also a child of a
            .Then(new DiamondC($"c-{label}", 600), after: [typeof(DiamondB1), typeof(DiamondB2)]) // fan-in
            .EnqueueAsync();
    }

    private static async Task JobOutput(BackWaveClient client)
    {
        var datasetRef = $"ds-{Rng.Next(1000, 9999)}";
        await client.Workflow($"job-output {datasetRef}")
            .Then(new Ingest(datasetRef))
            .Then(new Enrich(datasetRef))                                         // child of ingest
            .Then(new Score(datasetRef), after: [typeof(Ingest)])                // also a child of ingest
            .Then(new Publish(datasetRef), after: [typeof(Enrich), typeof(Score)]) // fan-in
            .EnqueueAsync();
    }

    private static string Person() => People[Rng.Next(People.Length)];
    private static string Tenant() => Tenants[Rng.Next(Tenants.Length)];
    private static string Channel() => Channels[Rng.Next(Channels.Length)];
    private static string Ghost() => GhostWireNames[Rng.Next(GhostWireNames.Length)];
    private static string OrderRef() => $"ORD-{Rng.Next(1000, 9999)}";
    private static string Email() => $"{Person()}@{Tenant()}.example.com";
    private static decimal Amount() => Math.Round((decimal)(Rng.NextDouble() * 4900 + 20), 2);
}
