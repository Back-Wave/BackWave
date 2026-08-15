namespace BackWave.Pro;

/// <summary>
/// Marks a job payload type as a <b>workflow step</b>: an ordinary job that may be composed into a
/// workflow graph and referenced by its .NET type. A step is a normal <c>[Job]</c> payload record
/// that additionally wears this marker; its handler stays an ordinary <c>IJobHandler&lt;TStep&gt;</c>,
/// so one handler model serves the job whether or not it runs inside a workflow. Wearing the marker is
/// the explicit opt-in that lets a type be chained with the workflow builder's <c>Then</c> - a job
/// that does not implement it cannot be added to a workflow, which is what makes a mistyped or
/// refactored step reference a compile error instead of a runtime one. Implement the generic
/// <see cref="IWorkflowStep{TOut}"/> instead when the step produces a Job Output later steps read.
/// </summary>
/// <remarks>
/// A step's payload record must tolerate unknown JSON properties on deserialization (the default). When a
/// step runs inside a workflow, BackWave splices small reserved properties into the payload - the Workflow
/// Input seed and the step's dependency wire names - under a root-property namespace beginning
/// <c>$backwave.</c>. Two consequences follow: a step record configured for strict decoding (System.Text.Json
/// <c>JsonUnmappedMemberHandling.Disallow</c>) is unsupported inside a workflow, because it rejects those
/// injected properties; and a step's own payload must not declare a property in the <c>$backwave.</c>
/// namespace - enqueuing a workflow whose step payload claims one fails fast rather than silently colliding.
/// </remarks>
/// <example>
/// <code>
/// [Job("charge-card")]
/// public sealed record ChargeCard(string OrderId) : IWorkflowStep;
///
/// public sealed class ChargeCardHandler : IJobHandler&lt;ChargeCard&gt;
/// {
///     public Task HandleAsync(ChargeCard job, JobContext ctx, CancellationToken ct) =&gt; /* ... */;
/// }
/// </code>
/// </example>
public interface IWorkflowStep;

/// <summary>
/// Marks a job payload type as a <b>workflow step that produces a Job Output</b> of type
/// <typeparamref name="TOut"/>. It extends the output-less <see cref="IWorkflowStep"/>, so it is
/// chainable everywhere a step is, and additionally declares - once, on the step's own contract - the
/// type of the value the step emits. A downstream step reads that output type-checked against this
/// declaration, so a producer and every reader share one compiler-locked shape and no reader can
/// assert a type the producer never wrote.
/// </summary>
/// <typeparam name="TOut">The type of the Job Output this step emits.</typeparam>
/// <example>
/// <code>
/// [Job("generate-invoice")]
/// public sealed record GenerateInvoice(string OrderId) : IWorkflowStep&lt;InvoiceResult&gt;;
/// </code>
/// </example>
public interface IWorkflowStep<TOut> : IWorkflowStep;
