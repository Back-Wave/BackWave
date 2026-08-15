namespace BackWave.Pro;

/// <summary>
/// A reusable <b>workflow definition</b>: a named, strongly-typed description of a multi-step operation
/// that can be started many times, each run getting a fresh workflow identity and fresh member job
/// identities. Implement <see cref="Build"/> to declare the graph - chain steps on the supplied builder
/// - using the immutable <b>seed</b> to shape the graph at build time and to construct each step's payload.
/// Start an instance with <c>client.StartWorkflow&lt;TWorkflow, TSeed&gt;(seed, seedTypeInfo)</c>.
/// </summary>
/// <typeparam name="TSeed">
/// The immutable build-time seed supplied when an instance is started. Use a small, purpose-built record;
/// it shapes the graph and each step's payload at build time. When started directly with
/// <c>StartWorkflow</c>, the same value also becomes the run's Workflow Input, readable inside a handler
/// via <c>ctx.Input&lt;TSeed&gt;()</c>; when this definition is spliced into another graph with
/// <c>ThenWorkflow</c>, the seed is build-time only and the spliced steps share the parent's Workflow
/// Input instead. It is never mutated - it is a constant seed, not shared state.
/// </typeparam>
/// <example>
/// <code>
/// public sealed class CheckoutWorkflow : IWorkflow&lt;CheckoutSeed&gt;
/// {
///     public void Build(TypedWorkflowBuilder builder, CheckoutSeed seed)
///     {
///         builder.Then(new ChargeCard(seed.OrderId))
///                .Then(new SendReceipt(seed.OrderId));
///     }
/// }
/// </code>
/// </example>
public interface IWorkflow<in TSeed>
{
    /// <summary>
    /// Declares this workflow's graph by chaining steps onto <paramref name="builder"/>. Called once per
    /// started instance, at construction, with the run's <paramref name="seed"/>. The method may branch on
    /// <paramref name="seed"/> to shape the graph (a build-time decision known at start), but must not
    /// perform I/O or depend on a step's runtime result - the graph is fully fixed here, before anything
    /// runs.
    /// </summary>
    /// <param name="builder">The workflow builder to add steps to; already seeded with this run's input.</param>
    /// <param name="seed">The immutable build-time seed for this instance.</param>
    void Build(TypedWorkflowBuilder builder, TSeed seed);
}
