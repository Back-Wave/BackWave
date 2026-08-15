using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using BackWave.Pro;

namespace BackWave;

/// <summary>
/// The BackWave Pro strongly-typed workflow-authoring surface, attached to the base enqueue client. With
/// the BackWave Pro package referenced, these methods light up on the same client used for ordinary
/// enqueues - start a reusable workflow definition, build a one-off inline graph, or append into an
/// existing workflow. Steps are referenced by their .NET type, so a mistyped or renamed step is a compile
/// error. Workflows are a Pro feature: referencing the package is the entire boundary.
/// </summary>
public static class TypedWorkflowClientExtensions
{
    /// <summary>
    /// Starts building an inline, one-off workflow with no Workflow Input. Chain steps with the returned
    /// builder's <c>Then</c>, then <c>EnqueueAsync</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The builder intentionally offers no <c>Delay</c> step and no <c>WaitFor</c> step. Both needs are
    /// met by the ordinary enqueue API, which keeps a workflow's shape honest and every member an ordinary
    /// job.
    /// </para>
    /// <para>
    /// <b>Delays.</b> For a fixed floor measured from when you schedule ("not before 9am"), enqueue the
    /// step with a future due time using the client's <c>EnqueueAsync(job, dueTime)</c>. For a delay
    /// measured from when an upstream step finishes ("an hour after the charge settles"), end that step's
    /// handler by enqueuing the next step at the current time plus the delay. This appends a new job
    /// rather than pausing an existing one, so no worker is held and the wait survives a restart.
    /// </para>
    /// <para>
    /// <b>Waiting for an event.</b> To wait on an external condition, poll from a step: the step checks
    /// the condition and, if it does not hold yet, re-enqueues itself with a future due time to try again
    /// later, backing off between attempts. Alternatively, let the event drive it - when the out-of-band
    /// event arrives, its handler enqueues the continuation step directly. Either way the wait is an
    /// ordinary future-due job, not a blocked worker.
    /// </para>
    /// <para>
    /// <b>Versioning.</b> A definition carries no version field. Treat "v1"/"v2" as a naming convention on
    /// the definition type and evolve by deployment discipline: when you rename or remove a step, keep the
    /// old step's handler registered until every in-flight instance that still references it has drained. A
    /// job whose step has no registered handler is not lost - it is quarantined for inspection and can be
    /// requeued once the handler is restored, so a premature removal fails loudly rather than silently
    /// dropping work.
    /// </para>
    /// </remarks>
    /// <param name="client">The enqueue client to build the workflow on.</param>
    /// <param name="name">An optional human-readable label for the workflow row; the trivial "run B after A" case needs none.</param>
    /// <returns>A builder for chaining steps, then enqueuing the graph.</returns>
    /// <example>
    /// <code>
    /// await client.Workflow("checkout")
    ///     .Then(new ChargeCard(orderId))
    ///     .Then(new SendReceipt(orderId))
    ///     .EnqueueAsync();
    /// </code>
    /// </example>
    public static TypedWorkflowBuilder Workflow(this BackWaveClient client, string? name = null)
        => new(client, name, inputJson: null, Guid.NewGuid(), isAppend: false);

    /// <summary>
    /// Starts building an inline, one-off workflow seeded with an immutable <b>Workflow Input</b>. The seed
    /// is baked into every member's payload and read inside a handler via
    /// <c>ctx.Input&lt;TInput&gt;(typeInfo)</c>; it is a constant, never mutated shared state.
    /// </summary>
    /// <typeparam name="TInput">The Workflow Input seed type.</typeparam>
    /// <param name="client">The enqueue client to build the workflow on.</param>
    /// <param name="seed">The immutable input value shared by every step.</param>
    /// <param name="seedTypeInfo">The source-generated serialization metadata for <typeparamref name="TInput"/>, keeping the path reflection-free.</param>
    /// <param name="name">An optional human-readable label for the workflow row.</param>
    /// <returns>A builder for chaining steps, then enqueuing the graph.</returns>
    /// <example>
    /// <code>
    /// await client.Workflow(new CheckoutSeed(orderId), AppJson.Default.CheckoutSeed, "checkout")
    ///     .Then(new ChargeCard(orderId))
    ///     .Then(new SendReceipt(orderId))
    ///     .EnqueueAsync();
    ///
    /// // Inside a step handler, read the seed back:
    /// var seed = ctx.Input(AppJson.Default.CheckoutSeed);
    /// </code>
    /// </example>
    public static TypedWorkflowBuilder Workflow<TInput>(
        this BackWaveClient client, TInput seed, JsonTypeInfo<TInput> seedTypeInfo, string? name = null)
        => new(client, name, JsonSerializer.SerializeToUtf8Bytes(seed, seedTypeInfo), Guid.NewGuid(), isAppend: false);

    /// <summary>
    /// Starts building an inline, one-off workflow seeded with an immutable <b>Workflow Input</b>,
    /// resolving the seed's serialization for you: the seed type is a <see cref="IWorkflowInput"/> listed in
    /// one of your <c>JsonSerializerContext</c> declarations, so you pass no serializer. The seed is baked
    /// into every member's payload and read inside a handler via <c>ctx.Input&lt;TInput&gt;()</c>.
    /// </summary>
    /// <typeparam name="TInput">The Workflow Input seed type - a <see cref="IWorkflowInput"/> listed in a JSON context.</typeparam>
    /// <param name="client">The enqueue client to build the workflow on.</param>
    /// <param name="seed">The immutable input value shared by every step.</param>
    /// <param name="name">An optional human-readable label for the workflow row.</param>
    /// <returns>A builder for chaining steps, then enqueuing the graph.</returns>
    /// <exception cref="InvalidOperationException">
    /// <typeparamref name="TInput"/> has no registered seed codec - it was not listed in a
    /// <c>JsonSerializerContext</c>. Add <c>[JsonSerializable(typeof(TInput))]</c> there, or use the overload
    /// that takes an explicit <see cref="JsonTypeInfo{T}"/>.
    /// </exception>
    /// <example>
    /// <code>
    /// await client.Workflow(new CheckoutSeed(orderId), "checkout")
    ///     .Then(new ChargeCard(orderId))
    ///     .Then(new SendReceipt(orderId))
    ///     .EnqueueAsync();
    /// </code>
    /// </example>
    public static TypedWorkflowBuilder Workflow<TInput>(
        this BackWaveClient client, TInput seed, string? name = null)
        where TInput : IWorkflowInput
        => new(client, name, JsonSerializer.SerializeToUtf8Bytes(seed, ResolveSeedCodec<TInput>(client)),
            Guid.NewGuid(), isAppend: false);

    /// <summary>
    /// Starts building an append into an existing workflow: steps added to the returned builder may name
    /// existing members as parents (via a fan-in <c>Then</c>'s <c>afterExisting</c> argument), and the whole
    /// batch is enqueued atomically. The existing workflow's members are never rewritten. The target
    /// workflow must already exist or the enqueue fails.
    /// </summary>
    /// <remarks>
    /// Appended members carry <b>no Workflow Input</b>. An append does not re-bake the target workflow's
    /// seed into the new members, so a step added here has no seed in its payload even when the workflow it
    /// joins was started with one. A handler for an appended step that reads the Workflow Input accessor
    /// therefore throws at run time; give an appended step the constant data it needs through its own payload
    /// instead. Reading an upstream step's output still works normally.
    /// </remarks>
    /// <param name="client">The enqueue client to build the append on.</param>
    /// <param name="workflowId">The id of the existing workflow to append into.</param>
    /// <returns>A builder whose added steps become new members of the existing workflow.</returns>
    /// <exception cref="InvalidOperationException">
    /// Raised at run time, not by this method: because an appended member carries no Workflow Input, a
    /// handler for an appended step that reads the Workflow Input accessor throws. Supply the data through
    /// the step's own payload instead.
    /// </exception>
    public static TypedWorkflowBuilder WorkflowAppend(this BackWaveClient client, Guid workflowId)
        => new(client, name: null, inputJson: null, workflowId, isAppend: true);

    /// <summary>
    /// Starts a fresh instance of a reusable <see cref="IWorkflow{TSeed}"/> definition: a brand-new
    /// workflow id and fresh member job ids are minted, the definition's <c>Build</c> is run against the
    /// supplied <paramref name="seed"/>, and the whole graph is enqueued atomically.
    /// </summary>
    /// <typeparam name="TWorkflow">The workflow definition type to instantiate.</typeparam>
    /// <typeparam name="TInput">The definition's Workflow Input seed type.</typeparam>
    /// <param name="client">The enqueue client to start the workflow on.</param>
    /// <param name="seed">The immutable Workflow Input for this instance.</param>
    /// <param name="seedTypeInfo">The source-generated serialization metadata for <typeparamref name="TInput"/>, keeping the path reflection-free.</param>
    /// <param name="name">An optional human-readable label for the workflow row.</param>
    /// <param name="transaction">Optional. When supplied, the workflow commits or rolls back atomically with your own writes; the storage adapter must support transactional enqueue.</param>
    /// <param name="cancellationToken">Cancels the enqueue request.</param>
    /// <returns>The started workflow's id.</returns>
    /// <exception cref="InvalidWorkflowException">The definition built an empty or cyclic graph.</exception>
    /// <exception cref="NotSupportedException">A <paramref name="transaction"/> was supplied but the storage adapter does not support transactional enqueue.</exception>
    /// <exception cref="InvalidOperationException">The store rejected the workflow.</exception>
    /// <example>
    /// <code>
    /// var id = await client.StartWorkflow&lt;CheckoutWorkflow, CheckoutSeed&gt;(
    ///     new CheckoutSeed(orderId), AppJson.Default.CheckoutSeed);
    /// </code>
    /// </example>
    public static ValueTask<Guid> StartWorkflow<TWorkflow, TInput>(
        this BackWaveClient client,
        TInput seed,
        JsonTypeInfo<TInput> seedTypeInfo,
        string? name = null,
        DbTransaction? transaction = null,
        CancellationToken cancellationToken = default)
        where TWorkflow : IWorkflow<TInput>, new()
    {
        var builder = client.Workflow(seed, seedTypeInfo, name);
        new TWorkflow().Build(builder, seed);
        return builder.EnqueueAsync(transaction, cancellationToken);
    }

    /// <summary>
    /// Starts a fresh instance of a reusable <see cref="IWorkflow{TSeed}"/> definition, resolving the
    /// seed's serialization for you: the seed type is a <see cref="IWorkflowInput"/> listed in one of your
    /// <c>JsonSerializerContext</c> declarations, so you pass no serializer. A brand-new workflow id and
    /// fresh member job ids are minted, the definition's <c>Build</c> runs against the supplied
    /// <paramref name="seed"/>, and the whole graph is enqueued atomically.
    /// </summary>
    /// <typeparam name="TWorkflow">The workflow definition type to instantiate.</typeparam>
    /// <typeparam name="TInput">The definition's Workflow Input seed type - a <see cref="IWorkflowInput"/> listed in a JSON context.</typeparam>
    /// <param name="client">The enqueue client to start the workflow on.</param>
    /// <param name="seed">The immutable Workflow Input for this instance.</param>
    /// <param name="name">An optional human-readable label for the workflow row.</param>
    /// <param name="transaction">Optional. When supplied, the workflow commits or rolls back atomically with your own writes; the storage adapter must support transactional enqueue.</param>
    /// <param name="cancellationToken">Cancels the enqueue request.</param>
    /// <returns>The started workflow's id.</returns>
    /// <exception cref="InvalidWorkflowException">The definition built an empty or cyclic graph.</exception>
    /// <exception cref="NotSupportedException">A <paramref name="transaction"/> was supplied but the storage adapter does not support transactional enqueue.</exception>
    /// <exception cref="InvalidOperationException">
    /// The store rejected the workflow, or <typeparamref name="TInput"/> has no registered seed codec - it
    /// was not listed in a <c>JsonSerializerContext</c>. Add <c>[JsonSerializable(typeof(TInput))]</c> there,
    /// or use the overload that takes an explicit <see cref="JsonTypeInfo{T}"/>.
    /// </exception>
    /// <example>
    /// <code>
    /// var id = await client.StartWorkflow&lt;CheckoutWorkflow, CheckoutSeed&gt;(new CheckoutSeed(orderId));
    /// </code>
    /// </example>
    public static ValueTask<Guid> StartWorkflow<TWorkflow, TInput>(
        this BackWaveClient client,
        TInput seed,
        string? name = null,
        DbTransaction? transaction = null,
        CancellationToken cancellationToken = default)
        where TWorkflow : IWorkflow<TInput>, new()
        where TInput : IWorkflowInput
    {
        var builder = client.Workflow(seed, name);
        new TWorkflow().Build(builder, seed);
        return builder.EnqueueAsync(transaction, cancellationToken);
    }

    // Resolves a Workflow Input seed type to its generator-registered codec off the client's registry, so
    // the seedTypeInfo-free Workflow/StartWorkflow overloads pass no serializer. Throws a guiding message
    // when the type was never listed in a JsonSerializerContext (so no codec was emitted for it).
    private static JsonTypeInfo<TInput> ResolveSeedCodec<TInput>(BackWaveClient client)
        where TInput : IWorkflowInput
    {
        if (client.Registry.FindSeedCodec(typeof(TInput)) is not JsonTypeInfo<TInput> typeInfo)
        {
            throw new InvalidOperationException(
                $"Workflow Input type '{typeof(TInput).Name}' has no registered seed codec. Mark it with " +
                $"IWorkflowInput and list it in a JsonSerializerContext ([JsonSerializable(typeof(" +
                $"{typeof(TInput).Name}))]) so BackWave can wire its serialization, or start the workflow " +
                "with the overload that takes an explicit JsonTypeInfo.");
        }

        return typeInfo;
    }
}
