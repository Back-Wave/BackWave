using BackWave.Pro;
using BackWave.Storage;

namespace BackWave.Demo;

/// <summary>
/// Shared building blocks for the synthetic workload: realistic-looking label pools, small random
/// pickers, and the three Workflow shapes (order-fulfillment, fan-out/fan-in, job-output). Both the
/// boot seed (<see cref="DemoSeed"/>) and the continuous generator (<see cref="DemoJobs"/>) draw on
/// these so the Workflows tab stays populated and the tables never read as obviously synthetic.
/// </summary>
internal static class DemoWorkloads
{
    private static readonly string[] Tenants =
        ["acme", "globex", "initech", "umbrella", "hooli", "wonka", "stark", "wayne"];
    private static readonly string[] People =
        ["ava", "liam", "noah", "mia", "omar", "zoe", "priya", "kai", "sana", "leo", "ruby", "theo"];
    private static readonly string[] Channels = ["email", "sms", "push", "webhook"];
    private static readonly string[] GhostWireNames =
        ["legacy-invoice-v1", "sync-crm", "ghost-job", "retired-report", "orphaned-webhook", "v0-notifier"];

    private static readonly Random Rng = Random.Shared;

    public static string Person() => People[Rng.Next(People.Length)];
    public static string Tenant() => Tenants[Rng.Next(Tenants.Length)];
    public static string Channel() => Channels[Rng.Next(Channels.Length)];
    public static string Ghost() => GhostWireNames[Rng.Next(GhostWireNames.Length)];
    public static string OrderRef() => $"ORD-{Rng.Next(1000, 9999)}";
    public static string Email() => $"{Person()}@{Tenant()}.example.com";
    public static decimal Amount() => Math.Round((decimal)(Rng.NextDouble() * 4900 + 20), 2);
    public static int Items() => Rng.Next(1, 6);

    /// <summary>A tenant tag, sometimes with a 'priority' Label — drives the Tag pills and facets.</summary>
    public static JobTags TenantTags()
    {
        var tags = JobTags.Empty.WithTag("tenant", Tenant());
        return Rng.Next(4) == 0 ? tags.WithLabel("priority") : tags;
    }

    /// <summary>Diamond + tail: validate → {charge, reserve} → pack → notify. ?fail Dead-Letters charge.</summary>
    public static async Task OrderFulfillmentAsync(BackWaveClient client, bool fail)
    {
        var orderRef = OrderRef();
        var amount = Amount();
        var items = Items();
        await client.Workflow($"order-fulfillment {orderRef}")
            .Then(new ValidateOrder(orderRef))
            .Then(new ChargePayment(orderRef, amount, fail))                       // fan-out branch A of validate
            .Then(new ReserveInventory(orderRef, items), after: [typeof(ValidateOrder)]) // fan-out branch B of validate
            .Then(new PackShipment(orderRef), after: [typeof(ChargePayment), typeof(ReserveInventory)]) // fan-in
            .Then(new OrderNotification(orderRef, Email(), "email", items, amount, false)) // tail on pack
            .EnqueueAsync();
    }

    /// <summary>The canonical a → {b1, b2} → c fan-out/fan-in shape.</summary>
    public static async Task FanOutFanInAsync(BackWaveClient client)
    {
        var label = $"{Person()}-{Rng.Next(100, 999)}";
        await client.Workflow($"fan-out/fan-in {label}")
            .Then(new DiamondA($"a-{label}", 600))
            .Then(new DiamondB1($"b1-{label}", 600))                              // child of a
            .Then(new DiamondB2($"b2-{label}", 600), after: [typeof(DiamondA)])   // also a child of a
            .Then(new DiamondC($"c-{label}", 600), after: [typeof(DiamondB1), typeof(DiamondB2)]) // fan-in
            .EnqueueAsync();
    }

    /// <summary>Job Output diamond: ingest → {enrich, score} → publish, where publish pulls its ancestors.</summary>
    public static async Task JobOutputAsync(BackWaveClient client)
    {
        var datasetRef = $"ds-{Rng.Next(1000, 9999)}";
        await client.Workflow($"job-output {datasetRef}")
            .Then(new Ingest(datasetRef))
            .Then(new Enrich(datasetRef))                                         // child of ingest
            .Then(new Score(datasetRef), after: [typeof(Ingest)])                // also a child of ingest
            .Then(new Publish(datasetRef), after: [typeof(Enrich), typeof(Score)]) // fan-in
            .EnqueueAsync();
    }

    /// <summary>
    /// The all-features Workflows v2 graph: every v2 shape in one checkout. A PARALLEL fan-out (reserve-stock
    /// alongside notify-warehouse → confirm-pick), a fan-in authorize-charge, a saga COMPENSATION side-branch
    /// (refund-charge, which no-ops when the charge settled), a seed-aware CONDITIONAL large-order-gate (which
    /// cancels the not-taken shipping arm), an OnAnyTerminal converge at prepare-handoff, a spliced CHILD
    /// workflow (pack-parcel → print-label), and a terminal send-receipt. Because the gate always cancels one
    /// arm, the derived Workflow status renders Cancelled even though every step that ran Succeeded.
    /// <paramref name="expedite"/> forces the express arm; otherwise the priced total decides.
    /// </summary>
    public static async Task CheckoutAsync(BackWaveClient client, bool expedite)
    {
        var orderRef = OrderRef();
        var cents = (int)Math.Round(Amount() * 100);
        await client.Workflow(new CheckoutSeed(orderRef, expedite, ExpressThresholdCents: 100_000), name: $"checkout {orderRef}")
            .Then(new PriceOrder(orderRef, cents))
            .Parallel(
                WorkflowBranch.Step(new ReserveStock(orderRef, Items())),
                WorkflowBranch.Do(b => b.Then(new NotifyWarehouse(orderRef)).Then(new ConfirmPick(orderRef))))
            .Then(new AuthorizeCharge(orderRef, cents))
            .WithCompensation(new RefundCharge(orderRef))
            .If<LargeOrderGate, PriceOrder, OrderPrice, CheckoutSeed>(
                then: b => b.Then(new ExpressShip(orderRef)),
                otherwise: b => b.Then(new StandardShip(orderRef)))
            .Then(new PrepareHandoff(orderRef), mode: DependencyMode.OnAnyTerminal)
            .ThenWorkflow<FulfilmentWorkflow, FulfilmentSeed>(new FulfilmentSeed(orderRef))
            .Then(new SendReceipt(orderRef))
            .EnqueueAsync();
    }
}
