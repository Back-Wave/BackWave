using BackWave.Jobs;
using BackWave.Pro;

namespace BackWave.Demo;

// Workflow steps for the live demo. A step is an ordinary [Job] payload record wearing IWorkflowStep, so
// the strongly-typed workflow builder can chain it by its .NET type. Each has an ordinary IJobHandler<T>;
// the handlers deliberately log and delay so the graph at /workflows/{id} shows members moving through
// their states. Distinct wire names keep every node legible in the graph view.

// ── Order-fulfillment: validate ─> {charge, reserve} ─> pack ─> notify ──────────────

/// <summary>Workflow root: gate the order before any fan-out work begins.</summary>
[Job("validate-order", Queue = "critical")]
public sealed record ValidateOrder(string OrderRef) : IWorkflowStep;

public sealed class ValidateOrderHandler(ILogger<ValidateOrderHandler> logger) : IJobHandler<ValidateOrder>
{
    public async Task HandleAsync(ValidateOrder job, JobContext context, CancellationToken cancellationToken)
    {
        logger.LogInformation("validate-order: {OrderRef} (job {JobId})", job.OrderRef, context.JobId);
        await Task.Delay(400, cancellationToken);
    }
}

/// <summary>
/// Fan-out branch A: charge the card. With <see cref="ChargePayment.Fail"/> it Dead-Letters - its
/// on-success dependents Cancel and the Workflow projects Failed (failure dominates) in the graph view.
/// </summary>
[Job("charge-payment", Queue = "critical")]
public sealed record ChargePayment(string OrderRef, decimal Amount, bool Fail) : IWorkflowStep;

public sealed class ChargePaymentHandler(ILogger<ChargePaymentHandler> logger) : IJobHandler<ChargePayment>
{
    public async Task HandleAsync(ChargePayment job, JobContext context, CancellationToken cancellationToken)
    {
        if (job.Fail)
        {
            logger.LogWarning("charge-payment: {OrderRef} declined on attempt {Attempt}", job.OrderRef, context.Attempt);
            throw new InvalidOperationException($"charge-payment '{job.OrderRef}' declined (attempt {context.Attempt})");
        }

        logger.LogInformation("charge-payment: {OrderRef} charged {Amount:C} (job {JobId})", job.OrderRef, job.Amount, context.JobId);
        await Task.Delay(400, cancellationToken);
    }
}

/// <summary>Fan-out branch B: reserve stock. Runs in parallel with charge-payment once validate succeeds.</summary>
[Job("reserve-inventory", Queue = "bulk")]
public sealed record ReserveInventory(string OrderRef, int ItemCount) : IWorkflowStep;

public sealed class ReserveInventoryHandler(ILogger<ReserveInventoryHandler> logger) : IJobHandler<ReserveInventory>
{
    public async Task HandleAsync(ReserveInventory job, JobContext context, CancellationToken cancellationToken)
    {
        logger.LogInformation("reserve-inventory: {OrderRef} reserving {ItemCount} items (job {JobId})", job.OrderRef, job.ItemCount, context.JobId);
        await Task.Delay(400, cancellationToken);
    }
}

/// <summary>Fan-in: pack the shipment only after BOTH charge-payment and reserve-inventory Succeed.</summary>
[Job("pack-shipment", Queue = "critical")]
public sealed record PackShipment(string OrderRef) : IWorkflowStep;

public sealed class PackShipmentHandler(ILogger<PackShipmentHandler> logger) : IJobHandler<PackShipment>
{
    public async Task HandleAsync(PackShipment job, JobContext context, CancellationToken cancellationToken)
    {
        logger.LogInformation("pack-shipment: {OrderRef} packed (job {JobId})", job.OrderRef, context.JobId);
        await Task.Delay(400, cancellationToken);
    }
}

/// <summary>
/// A structured "document" job whose flat fields render as readable JSON in the payload card. On 'low'
/// (Weighted group, MaxAttempts=3) so the <see cref="OrderNotification.Fail"/> path Dead-Letters quickly:
/// when true it throws every attempt, exercising Failure Detail capture and a multi-entry timeline. Used
/// both as the terminal step of the order-fulfillment workflow and as a standalone job.
/// </summary>
[Job("order-notification", Queue = "low")]
public sealed record OrderNotification(
    string OrderRef, string CustomerEmail, string Channel, int ItemCount, decimal TotalAmount, bool Fail) : IWorkflowStep;

public sealed class OrderNotificationHandler(ILogger<OrderNotificationHandler> logger) : IJobHandler<OrderNotification>
{
    public Task HandleAsync(OrderNotification job, JobContext context, CancellationToken cancellationToken)
    {
        if (job.Fail)
        {
            logger.LogWarning("order-notification: {OrderRef} failing on attempt {Attempt}", job.OrderRef, context.Attempt);
            throw new InvalidOperationException(
                $"order-notification '{job.OrderRef}' could not reach {job.CustomerEmail} via {job.Channel} (attempt {context.Attempt})");
        }

        logger.LogInformation(
            "order-notification: {OrderRef} → {CustomerEmail} via {Channel} ({ItemCount} items, {TotalAmount:C})",
            job.OrderRef, job.CustomerEmail, job.Channel, job.ItemCount, job.TotalAmount);
        return Task.CompletedTask;
    }
}

// ── Job Output diamond: ingest ─> {enrich, score} ─> publish (River's LoadDeps) ─────
// A handler emits an opaque blob via SetOutput and a descendant PULLS the output of its transitive
// ancestors (never injected: pull, never push).

/// <summary>Job Output root: emits a <see cref="DatasetSummary"/>; descendants pull it on demand.</summary>
[Job("ingest", Queue = "critical")]
public sealed record Ingest(string DatasetRef) : IWorkflowStep;

public sealed class IngestHandler(ILogger<IngestHandler> logger) : IJobHandler<Ingest>
{
    public async Task HandleAsync(Ingest job, JobContext context, CancellationToken cancellationToken)
    {
        logger.LogInformation("ingest: {DatasetRef} (job {JobId})", job.DatasetRef, context.JobId);
        await Task.Delay(400, cancellationToken);
        context.SetOutput(new DatasetSummary(job.DatasetRef, RowCount: 1_000), DemoOutputJsonContext.Default.DatasetSummary);
    }
}

/// <summary>Fan-out branch A: pulls its parent <c>ingest</c>'s output, then emits its own.</summary>
[Job("enrich", Queue = "critical")]
public sealed record Enrich(string DatasetRef) : IWorkflowStep;

public sealed class EnrichHandler(ILogger<EnrichHandler> logger) : IJobHandler<Enrich>
{
    public async Task HandleAsync(Enrich job, JobContext context, CancellationToken cancellationToken)
    {
        var ingest = await context.GetDependencyOutputAsync(
            "ingest", DemoOutputJsonContext.Default.DatasetSummary, cancellationToken);
        var rows = ingest.HasOutput ? ingest.Output!.RowCount : 0;
        logger.LogInformation("enrich: read ingest output ({Rows} rows, ancestor {State}) (job {JobId})",
            rows, ingest.AncestorState, context.JobId);
        await Task.Delay(400, cancellationToken);
        context.SetOutput(new EnrichedResult(rows, $"enriched {job.DatasetRef}"), DemoOutputJsonContext.Default.EnrichedResult);
    }
}

/// <summary>Fan-out branch B: also pulls <c>ingest</c>'s output; on 'bulk' so it parallels <c>enrich</c>.</summary>
[Job("score", Queue = "bulk")]
public sealed record Score(string DatasetRef) : IWorkflowStep;

public sealed class ScoreHandler(ILogger<ScoreHandler> logger) : IJobHandler<Score>
{
    public async Task HandleAsync(Score job, JobContext context, CancellationToken cancellationToken)
    {
        var ingest = await context.GetDependencyOutputAsync(
            "ingest", DemoOutputJsonContext.Default.DatasetSummary, cancellationToken);
        var value = ingest.HasOutput ? ingest.Output!.RowCount / 100.0 : 0;
        logger.LogInformation("score: scored {DatasetRef} = {Value} (job {JobId})", job.DatasetRef, value, context.JobId);
        await Task.Delay(400, cancellationToken);
        context.SetOutput(new ScoreResult(value), DemoOutputJsonContext.Default.ScoreResult);
    }
}

/// <summary>
/// Fan-in: pulls its direct parents <c>enrich</c> + <c>score</c> AND, transitively, their shared
/// grandparent <c>ingest</c> - the headline LoadDeps move (the scope is transitive ancestors).
/// </summary>
[Job("publish", Queue = "critical")]
public sealed record Publish(string DatasetRef) : IWorkflowStep;

public sealed class PublishHandler(ILogger<PublishHandler> logger) : IJobHandler<Publish>
{
    public async Task HandleAsync(Publish job, JobContext context, CancellationToken cancellationToken)
    {
        var ingest = await context.GetDependencyOutputAsync(
            "ingest", DemoOutputJsonContext.Default.DatasetSummary, cancellationToken);
        var enriched = await context.GetDependencyOutputAsync(
            "enrich", DemoOutputJsonContext.Default.EnrichedResult, cancellationToken);
        var score = await context.GetDependencyOutputAsync(
            "score", DemoOutputJsonContext.Default.ScoreResult, cancellationToken);

        logger.LogInformation(
            "publish: {DatasetRef} - ingest(transitive)={IngestRows} rows [{IngestState}], enrich={EnrichRows} rows, score={Score} (job {JobId})",
            job.DatasetRef,
            ingest.HasOutput ? ingest.Output!.RowCount : 0, ingest.AncestorState,
            enriched.HasOutput ? enriched.Output!.EnrichedRows : 0,
            score.HasOutput ? score.Output!.Value : 0,
            context.JobId);
        await Task.Delay(400, cancellationToken);
    }
}

// ── Fan-out / fan-in diamond: a ─> {b1, b2} ─> c ────────────────────────────────────
// The canonical River shape. The strongly-typed builder references steps by type and joins by type, so a
// fan-in needs each node to be a distinct step type (one type per position), each holding its lease
// briefly so the parallel middle is visible in the graph.

/// <summary>Diamond root.</summary>
[Job("diamond-a", Queue = "critical")]
public sealed record DiamondA(string Label, int DelayMs) : IWorkflowStep;

public sealed class DiamondAHandler(ILogger<DiamondAHandler> logger) : IJobHandler<DiamondA>
{
    public async Task HandleAsync(DiamondA job, JobContext context, CancellationToken cancellationToken)
    {
        logger.LogInformation("diamond-a: {Label} (job {JobId})", job.Label, context.JobId);
        if (job.DelayMs > 0)
        {
            await Task.Delay(job.DelayMs, cancellationToken);
        }
    }
}

/// <summary>Diamond left branch, a child of the root that runs parallel to the right branch.</summary>
[Job("diamond-b1", Queue = "critical")]
public sealed record DiamondB1(string Label, int DelayMs) : IWorkflowStep;

public sealed class DiamondB1Handler(ILogger<DiamondB1Handler> logger) : IJobHandler<DiamondB1>
{
    public async Task HandleAsync(DiamondB1 job, JobContext context, CancellationToken cancellationToken)
    {
        logger.LogInformation("diamond-b1: {Label} (job {JobId})", job.Label, context.JobId);
        if (job.DelayMs > 0)
        {
            await Task.Delay(job.DelayMs, cancellationToken);
        }
    }
}

/// <summary>Diamond right branch, a child of the root that runs parallel to the left branch.</summary>
[Job("diamond-b2", Queue = "critical")]
public sealed record DiamondB2(string Label, int DelayMs) : IWorkflowStep;

public sealed class DiamondB2Handler(ILogger<DiamondB2Handler> logger) : IJobHandler<DiamondB2>
{
    public async Task HandleAsync(DiamondB2 job, JobContext context, CancellationToken cancellationToken)
    {
        logger.LogInformation("diamond-b2: {Label} (job {JobId})", job.Label, context.JobId);
        if (job.DelayMs > 0)
        {
            await Task.Delay(job.DelayMs, cancellationToken);
        }
    }
}

/// <summary>Diamond join: runs only after BOTH branches complete.</summary>
[Job("diamond-c", Queue = "critical")]
public sealed record DiamondC(string Label, int DelayMs) : IWorkflowStep;

public sealed class DiamondCHandler(ILogger<DiamondCHandler> logger) : IJobHandler<DiamondC>
{
    public async Task HandleAsync(DiamondC job, JobContext context, CancellationToken cancellationToken)
    {
        logger.LogInformation("diamond-c: {Label} (job {JobId})", job.Label, context.JobId);
        if (job.DelayMs > 0)
        {
            await Task.Delay(job.DelayMs, cancellationToken);
        }
    }
}
