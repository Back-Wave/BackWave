namespace BackWave.Pro;

/// <summary>
/// Marks a type as a <b>Workflow Input</b> seed: the immutable, set-once value supplied when a workflow
/// is started and baked into every member's payload, then read inside a handler via
/// <c>ctx.Input&lt;TInput&gt;()</c>. Wearing this marker is the explicit opt-in that lets BackWave wire the
/// seed's serialization for you: a started workflow reads and writes the seed with no hand-passed
/// serializer, provided the seed type is listed in one of your <c>JsonSerializerContext</c> declarations
/// (add <c>[JsonSerializable(typeof(YourSeed))]</c> there). A seed type that is marked but not listed is a
/// build error, so a missing serializer is caught at compile time rather than at run time.
/// </summary>
/// <remarks>
/// A seed is a constant, never mutated: it shapes the graph at build time and supplies each step's
/// payload, but it is not shared, accumulating state. Read an upstream step's <i>result</i> through Job
/// Output, never through the seed.
/// </remarks>
/// <example>
/// <code>
/// public sealed record CheckoutSeed(string OrderId, bool IsPremium) : IWorkflowInput;
///
/// // Listed in a JSON context you keep (any shape is supported):
/// [JsonSerializable(typeof(CheckoutSeed))]
/// internal sealed partial class AppJson : JsonSerializerContext;
///
/// // Started with no serializer argument:
/// await client.StartWorkflow&lt;CheckoutWorkflow, CheckoutSeed&gt;(new CheckoutSeed(orderId, isPremium: true));
///
/// // Read back inside a handler with no serializer argument:
/// var seed = ctx.Input&lt;CheckoutSeed&gt;();
/// </code>
/// </example>
public interface IWorkflowInput;
