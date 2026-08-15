using System.Text.Json.Serialization;
using BackWave.Jobs;
using BackWave.Pro;
using BackWave.Storage;

namespace BackWave.Demo;

// The all-features Workflows v2 sample in the live demo (the "checkout" graph seeded and generated
// alongside the other shapes). One graph exercises every new v2 shape at once, so the dashboard graph
// view at /workflows/{id} renders each: a PARALLEL fan-out, a fan-in, a saga COMPENSATION side-branch, a
// seed-aware CONDITIONAL gate (which cancels the not-taken arm), a converge past the conditional, a
// spliced CHILD workflow, and typed Job Output feeding the gate. Each step is an ordinary [Job] payload
// wearing IWorkflowStep with a plain IJobHandler<T>; the handlers log and hold their lease briefly so the
// members are watchable moving through their states. Distinct wire names per stage keep every node legible.
//
//   price-order ─┬─> reserve-stock ──────────────────────┐
//                └─> notify-warehouse ─> confirm-pick ────┴─> authorize-charge ─> large-order-gate
//                                                                   │                    ├─(then)──────> express-ship ──┐
//                                                    (compensation) │                    └─(otherwise)─> standard-ship ─┤
//                                                                   v                                                   v
//                                                             refund-charge                              prepare-handoff (OnAnyTerminal)
//                                                                                                                       │
//                                          send-receipt <─ print-label <─ pack-parcel <───────────────────────────────┘
//                                                          (pack-parcel + print-label are spliced from FulfilmentWorkflow)
//
// The gate always cancels one arm, so the DERIVED Workflow status is Cancelled even when every step that
// ran Succeeded - read per-step state, not the rollup.

// ── Workflow Input + child seed ─────────────────────────────────────────────────

/// <summary>
/// The immutable Workflow Input for a checkout run. The seed-aware <c>large-order-gate</c> reads it
/// alongside <c>price-order</c>'s output to decide the express vs standard arm, so the threshold and the
/// expedite flag travel with the workflow rather than being baked into a step's payload.
/// </summary>
public sealed record CheckoutSeed(string OrderRef, bool Expedite, int ExpressThresholdCents) : IWorkflowInput;

/// <summary>
/// The build-time seed for the spliced <see cref="FulfilmentWorkflow"/> child. It shapes the child's graph
/// and constructs its step payloads at build time only; it is not a Workflow Input, so it needs no codec.
/// </summary>
public sealed record FulfilmentSeed(string OrderRef);

// ── Steps ──────────────────────────────────────────────────────────────────────

/// <summary>Workflow root: price the order and emit the total the conditional gate later reads.</summary>
[Job("price-order", Queue = "critical")]
public sealed record PriceOrder(string OrderRef, int Cents) : IWorkflowStep<OrderPrice>;

public sealed class PriceOrderHandler(ILogger<PriceOrderHandler> logger) : IJobHandler<PriceOrder>
{
    public async Task HandleAsync(PriceOrder job, JobContext context, CancellationToken cancellationToken)
    {
        logger.LogInformation("price-order: {OrderRef} = {Cents} cents (job {JobId})", job.OrderRef, job.Cents, context.JobId);
        await Task.Delay(330, cancellationToken);
        context.SetOutput<PriceOrder, OrderPrice>(new OrderPrice(job.Cents));
    }
}

/// <summary>Parallel branch A off price-order: reserve stock. Runs alongside the notify → confirm branch.</summary>
[Job("reserve-stock", Queue = "bulk")]
public sealed record ReserveStock(string OrderRef, int ItemCount) : IWorkflowStep;

public sealed class ReserveStockHandler(ILogger<ReserveStockHandler> logger) : IJobHandler<ReserveStock>
{
    public async Task HandleAsync(ReserveStock job, JobContext context, CancellationToken cancellationToken)
    {
        logger.LogInformation("reserve-stock: {OrderRef} reserving {ItemCount} items (job {JobId})", job.OrderRef, job.ItemCount, context.JobId);
        await Task.Delay(330, cancellationToken);
    }
}

/// <summary>Parallel branch B off price-order, step 1: notify the warehouse; confirm-pick follows it.</summary>
[Job("notify-warehouse", Queue = "bulk")]
public sealed record NotifyWarehouse(string OrderRef) : IWorkflowStep;

public sealed class NotifyWarehouseHandler(ILogger<NotifyWarehouseHandler> logger) : IJobHandler<NotifyWarehouse>
{
    public async Task HandleAsync(NotifyWarehouse job, JobContext context, CancellationToken cancellationToken)
    {
        logger.LogInformation("notify-warehouse: {OrderRef} (job {JobId})", job.OrderRef, context.JobId);
        await Task.Delay(330, cancellationToken);
    }
}

/// <summary>Parallel branch B, step 2: confirm the pick. This branch's tip fans in to authorize-charge.</summary>
[Job("confirm-pick", Queue = "bulk")]
public sealed record ConfirmPick(string OrderRef) : IWorkflowStep;

public sealed class ConfirmPickHandler(ILogger<ConfirmPickHandler> logger) : IJobHandler<ConfirmPick>
{
    public async Task HandleAsync(ConfirmPick job, JobContext context, CancellationToken cancellationToken)
    {
        logger.LogInformation("confirm-pick: {OrderRef} (job {JobId})", job.OrderRef, context.JobId);
        await Task.Delay(330, cancellationToken);
    }
}

/// <summary>
/// Fan-in over both parallel branches: authorize the charge once reserve-stock AND confirm-pick Succeed.
/// Emits the charge id the refund-charge compensation would reverse on a failure.
/// </summary>
[Job("authorize-charge", Queue = "critical")]
public sealed record AuthorizeCharge(string OrderRef, int Cents) : IWorkflowStep<ChargeResult>;

public sealed class AuthorizeChargeHandler(ILogger<AuthorizeChargeHandler> logger) : IJobHandler<AuthorizeCharge>
{
    public async Task HandleAsync(AuthorizeCharge job, JobContext context, CancellationToken cancellationToken)
    {
        logger.LogInformation("authorize-charge: {OrderRef} authorizing {Cents} cents (job {JobId})", job.OrderRef, job.Cents, context.JobId);
        await Task.Delay(330, cancellationToken);
        context.SetOutput<AuthorizeCharge, ChargeResult>(new ChargeResult($"ch_{job.OrderRef}", job.Cents));
    }
}

/// <summary>
/// Saga compensation guarding authorize-charge: it always becomes reachable once the charge is terminal, and
/// its handler reads the charge's decided state to decide whether to undo. It no-ops when the charge
/// Succeeded (the happy path) and refunds only when it did not - so the undo always runs but usually does
/// nothing.
/// </summary>
[Job("refund-charge", Queue = "critical")]
public sealed record RefundCharge(string OrderRef) : IWorkflowStep;

public sealed class RefundChargeHandler(ILogger<RefundChargeHandler> logger) : IJobHandler<RefundCharge>
{
    public async Task HandleAsync(RefundCharge job, JobContext context, CancellationToken cancellationToken)
    {
        var charge = await context.Output<AuthorizeCharge, ChargeResult>(cancellationToken);
        if (charge.AncestorState == JobState.Succeeded)
        {
            logger.LogInformation("refund-charge: {OrderRef} charge settled, nothing to undo (job {JobId})", job.OrderRef, context.JobId);
            return;
        }

        logger.LogWarning("refund-charge: {OrderRef} reversing charge {ChargeId} (job {JobId})", job.OrderRef, charge.Output?.ChargeId, context.JobId);
        await Task.Delay(330, cancellationToken);
    }
}

/// <summary>Conditional "then" arm: the express shipping path, taken when the gate enters.</summary>
[Job("express-ship", Queue = "high")]
public sealed record ExpressShip(string OrderRef) : IWorkflowStep;

public sealed class ExpressShipHandler(ILogger<ExpressShipHandler> logger) : IJobHandler<ExpressShip>
{
    public async Task HandleAsync(ExpressShip job, JobContext context, CancellationToken cancellationToken)
    {
        logger.LogInformation("express-ship: {OrderRef} (job {JobId})", job.OrderRef, context.JobId);
        await Task.Delay(330, cancellationToken);
    }
}

/// <summary>Conditional "otherwise" arm: the standard shipping path, cancelled when the gate enters.</summary>
[Job("standard-ship", Queue = "high")]
public sealed record StandardShip(string OrderRef) : IWorkflowStep;

public sealed class StandardShipHandler(ILogger<StandardShipHandler> logger) : IJobHandler<StandardShip>
{
    public async Task HandleAsync(StandardShip job, JobContext context, CancellationToken cancellationToken)
    {
        logger.LogInformation("standard-ship: {OrderRef} (job {JobId})", job.OrderRef, context.JobId);
        await Task.Delay(330, cancellationToken);
    }
}

/// <summary>
/// Converge past the conditional: depends on BOTH shipping arms with OnAnyTerminal, so it releases once one
/// arm Succeeded and the other reached the terminal cancelled state.
/// </summary>
[Job("prepare-handoff", Queue = "critical")]
public sealed record PrepareHandoff(string OrderRef) : IWorkflowStep;

public sealed class PrepareHandoffHandler(ILogger<PrepareHandoffHandler> logger) : IJobHandler<PrepareHandoff>
{
    public async Task HandleAsync(PrepareHandoff job, JobContext context, CancellationToken cancellationToken)
    {
        logger.LogInformation("prepare-handoff: {OrderRef} (job {JobId})", job.OrderRef, context.JobId);
        await Task.Delay(330, cancellationToken);
    }
}

/// <summary>Child-workflow step 1 (spliced by FulfilmentWorkflow): pack the parcel.</summary>
[Job("pack-parcel", Queue = "critical")]
public sealed record PackParcel(string OrderRef) : IWorkflowStep;

public sealed class PackParcelHandler(ILogger<PackParcelHandler> logger) : IJobHandler<PackParcel>
{
    public async Task HandleAsync(PackParcel job, JobContext context, CancellationToken cancellationToken)
    {
        logger.LogInformation("pack-parcel: {OrderRef} (job {JobId})", job.OrderRef, context.JobId);
        await Task.Delay(330, cancellationToken);
    }
}

/// <summary>Child-workflow step 2 (spliced by FulfilmentWorkflow): print the shipping label.</summary>
[Job("print-label", Queue = "critical")]
public sealed record PrintLabel(string OrderRef) : IWorkflowStep;

public sealed class PrintLabelHandler(ILogger<PrintLabelHandler> logger) : IJobHandler<PrintLabel>
{
    public async Task HandleAsync(PrintLabel job, JobContext context, CancellationToken cancellationToken)
    {
        logger.LogInformation("print-label: {OrderRef} (job {JobId})", job.OrderRef, context.JobId);
        await Task.Delay(330, cancellationToken);
    }
}

/// <summary>Terminal step: send the receipt once the spliced child's print-label completes.</summary>
[Job("send-receipt", Queue = "critical")]
public sealed record SendReceipt(string OrderRef) : IWorkflowStep;

public sealed class SendReceiptHandler(ILogger<SendReceiptHandler> logger) : IJobHandler<SendReceipt>
{
    public async Task HandleAsync(SendReceipt job, JobContext context, CancellationToken cancellationToken)
    {
        logger.LogInformation("send-receipt: {OrderRef} (job {JobId})", job.OrderRef, context.JobId);
        await Task.Delay(330, cancellationToken);
    }
}

// ── Conditional gate + child workflow definition ─────────────────────────────────

/// <summary>
/// The seed-aware gate deciding the shipping arm. It reads price-order's output and the CheckoutSeed: enter
/// the express ("then") arm when the order is flagged expedite, or when the priced total clears the seed's
/// threshold; otherwise the standard arm runs. Absence is handled - a price-order that emitted nothing
/// falls through to the standard arm unless expedite forced express.
/// </summary>
public sealed class LargeOrderGate : IWorkflowGate<PriceOrder, OrderPrice, CheckoutSeed>
{
    public bool Enter(DependencyOutput<OrderPrice> observed, CheckoutSeed input)
        => input.Expedite || (observed.HasOutput && observed.Output!.Cents >= input.ExpressThresholdCents);
}

/// <summary>
/// A reusable child workflow spliced into the checkout graph with <c>ThenWorkflow</c>: pack the parcel, then
/// print the label. Splicing grafts these as flat members of the one checkout graph, not a nested run, so
/// they share the parent's workflow row, derived status, and retention unit.
/// </summary>
public sealed class FulfilmentWorkflow : IWorkflow<FulfilmentSeed>
{
    public void Build(TypedWorkflowBuilder builder, FulfilmentSeed seed)
        => builder.Then(new PackParcel(seed.OrderRef)).Then(new PrintLabel(seed.OrderRef));
}

// ── JSON context ─────────────────────────────────────────────────────────────────

/// <summary>
/// The source-generated serialization metadata for the checkout scenario: the CheckoutSeed Workflow Input
/// codec (wired because CheckoutSeed is an IWorkflowInput listed here) and the gate step codec that
/// AddWorkflowGate needs to register large-order-gate.
/// </summary>
[JsonSerializable(typeof(CheckoutSeed))]
[JsonSerializable(typeof(WorkflowGate<LargeOrderGate, PriceOrder, OrderPrice, CheckoutSeed>))]
internal sealed partial class CheckoutJsonContext : JsonSerializerContext;
