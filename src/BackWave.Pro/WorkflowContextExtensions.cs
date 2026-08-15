using System.Text.Json.Serialization.Metadata;
using BackWave.Pro;

namespace BackWave.Jobs;

/// <summary>
/// BackWave Pro workflow accessors on the handler's <see cref="JobContext"/>. With the BackWave Pro
/// package referenced, these light up inside any job handler and give a workflow step typed access to the
/// data a workflow makes available - starting with the immutable <b>Workflow Input</b> seed.
/// </summary>
public static class WorkflowContextExtensions
{
    /// <summary>
    /// Reads the immutable <b>Workflow Input</b> seed baked into this step's payload when its workflow was
    /// started, resolving the seed's serialization for you: the seed type is a <see cref="IWorkflowInput"/>
    /// listed in one of your <c>JsonSerializerContext</c> declarations, so you pass no serializer. The seed
    /// is workflow-wide constant data, kept distinct from the step's own payload; it is never mutated, so
    /// this is a plain read, not a pull that can be absent.
    /// </summary>
    /// <typeparam name="TInput">The Workflow Input seed type - the same type passed when the workflow was started.</typeparam>
    /// <param name="context">The handler's execution context.</param>
    /// <returns>The Workflow Input seed for this run.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// This context was not built for handler execution; the job carries no Workflow Input (its workflow was
    /// started with no seed, or it is not a workflow member); or <typeparamref name="TInput"/> has no
    /// registered seed codec (it was not listed in a <c>JsonSerializerContext</c>).
    /// </exception>
    /// <example>
    /// <code>
    /// public sealed record CheckoutSeed(string OrderId, bool IsPremium) : IWorkflowInput;
    ///
    /// public Task HandleAsync(ChargeCard job, JobContext ctx, CancellationToken ct)
    /// {
    ///     var seed = ctx.Input&lt;CheckoutSeed&gt;();
    ///     // ... use seed.OrderId ...
    /// }
    /// </code>
    /// </example>
    public static TInput Input<TInput>(this JobContext context)
        where TInput : IWorkflowInput
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Registry is not { } registry)
        {
            throw new InvalidOperationException(
                "This JobContext has no job registry wired, so it cannot resolve a Workflow Input seed " +
                "codec. The typed Input accessor is available only on a handler-execution context.");
        }

        if (registry.FindSeedCodec(typeof(TInput)) is not JsonTypeInfo<TInput> typeInfo)
        {
            throw new InvalidOperationException(
                $"Workflow Input type '{typeof(TInput).Name}' has no registered seed codec. Mark it with " +
                $"IWorkflowInput and list it in a JsonSerializerContext ([JsonSerializable(typeof(" +
                $"{typeof(TInput).Name}))]) so BackWave can wire its serialization, or read it with the " +
                "overload that takes an explicit JsonTypeInfo.");
        }

        return context.Input(typeInfo);
    }

    /// <summary>
    /// Reads the immutable <b>Workflow Input</b> seed baked into this step's payload when its workflow was
    /// started. The seed is workflow-wide constant data, kept distinct from the step's own payload; it is
    /// never mutated, so this is a plain read, not a pull that can be absent.
    /// </summary>
    /// <typeparam name="TInput">The Workflow Input seed type - the same type passed when the workflow was started.</typeparam>
    /// <param name="context">The handler's execution context.</param>
    /// <param name="typeInfo">The source-generated serialization metadata for <typeparamref name="TInput"/>, keeping the path reflection-free.</param>
    /// <returns>The Workflow Input seed for this run.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// This context was not built for handler execution, or the job carries no Workflow Input (its
    /// workflow was started with no seed, or it is not a workflow member).
    /// </exception>
    public static TInput Input<TInput>(this JobContext context, JsonTypeInfo<TInput> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Payload is not { } payload)
        {
            throw new InvalidOperationException(
                "This JobContext has no payload wired, so it cannot read Workflow Input. " +
                "Input is available only on a handler-execution context.");
        }

        return WorkflowInputEnvelope.TryExtract(payload, typeInfo, out var input)
            ? input
            : throw new InvalidOperationException(
                "This job carries no Workflow Input; its workflow was started without a seed, " +
                "or the job is not a workflow member.");
    }

    /// <summary>
    /// Emits this step's <b>Job Output</b> - one value a downstream step may later read - checked at
    /// compile time against the output type the step declares it produces, so a step can never emit a
    /// value a reader would fail to deserialize. The value is buffered on this attempt and persisted only
    /// if the attempt succeeds; a superseded or failed attempt discards it, and a later successful attempt
    /// overwrites it (last write wins).
    /// </summary>
    /// <typeparam name="TStep">The step type emitting the output - the running step's own type - declaring the output type it produces.</typeparam>
    /// <typeparam name="TOut">The output value type <typeparamref name="TStep"/> declares it produces.</typeparam>
    /// <param name="context">The handler's execution context.</param>
    /// <param name="value">The output value to emit.</param>
    /// <example>
    /// <code>
    /// [Job("generate-invoice")]
    /// public sealed record GenerateInvoice(string OrderId) : IWorkflowStep&lt;InvoiceResult&gt;;
    ///
    /// public sealed class GenerateInvoiceHandler : IJobHandler&lt;GenerateInvoice&gt;
    /// {
    ///     public Task HandleAsync(GenerateInvoice job, JobContext ctx, CancellationToken ct)
    ///     {
    ///         ctx.SetOutput&lt;GenerateInvoice, InvoiceResult&gt;(new InvoiceResult(job.OrderId, 4200));
    ///         return Task.CompletedTask;
    ///     }
    /// }
    /// </code>
    /// </example>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// This context was not built for handler execution; or <typeparamref name="TStep"/> is not a
    /// registered job type; or no output codec for <typeparamref name="TOut"/> was registered with it; or
    /// <typeparamref name="TStep"/> is not the step this handler is running (a step may emit only its own
    /// output).
    /// </exception>
    public static void SetOutput<TStep, TOut>(this JobContext context, TOut value)
        where TStep : IWorkflowStep<TOut>
    {
        ArgumentNullException.ThrowIfNull(context);
        var (wireName, outputTypeInfo) = ResolveStep<TStep, TOut>(context);
        // A step may emit only its OWN output. Writing under another step's type would buffer the value
        // through the wrong output codec, and a reader pulling that step would decode it with the wrong
        // shape. Both real pumps set the running Wire Name; a hand-built context that leaves it null
        // (a unit test buffering output in isolation) skips the check.
        if (context.RunningWireName is { } running && running != wireName)
        {
            throw new InvalidOperationException(
                $"SetOutput was called for step type '{typeof(TStep).Name}' (wire name '{wireName}'), but " +
                $"this handler is running a different step (wire name '{running}'). A step may emit only " +
                "its own Job Output; otherwise the value would be buffered under the wrong step's codec and " +
                "a reader would decode it with the wrong shape. Call SetOutput with the type of the step " +
                "this handler runs.");
        }
        context.SetOutput(value, outputTypeInfo);
    }

    /// <summary>
    /// <b>Pulls</b> the <b>Job Output</b> of an ancestor step named by its .NET type - no string handle
    /// and no passed serializer - typed exactly to the output that step declares it produces. The read
    /// happens only when called (nothing is pre-loaded) and returns the ancestor's terminal state
    /// alongside its deserialized output. <b>Absence is normal</b>: an ancestor that failed, was
    /// cancelled, or succeeded emitting nothing - and a non-ancestor sibling on a parallel branch, which
    /// has no happens-before relationship - all resolve to a clean "no output" result rather than a throw.
    /// Whether <typeparamref name="TStep"/> is really an ancestor is a runtime fact, not a compile-time
    /// proof.
    /// </summary>
    /// <typeparam name="TStep">The ancestor step type to read, declaring the output type it produces. It must appear at most once among this job's ancestors: ancestor output is read by step type, and a step type used more than once resolves to several ancestors, so the read is ambiguous and throws. There is no by-name read that picks one of several same-type ancestors; structure the workflow so at most one ancestor of the reader is this step type.</typeparam>
    /// <typeparam name="TOut">The output value type <typeparamref name="TStep"/> declares it produces.</typeparam>
    /// <param name="context">The handler's execution context.</param>
    /// <param name="cancellationToken">Signaled to abandon the read.</param>
    /// <returns>
    /// The ancestor's terminal state with its deserialized output; an empty result (with no output) when
    /// the ancestor emitted nothing or the step is not an ancestor of this job.
    /// </returns>
    /// <example>
    /// <code>
    /// public sealed class SendReceiptHandler : IJobHandler&lt;SendReceipt&gt;
    /// {
    ///     public async Task HandleAsync(SendReceipt job, JobContext ctx, CancellationToken ct)
    ///     {
    ///         var invoice = await ctx.Output&lt;GenerateInvoice, InvoiceResult&gt;(ct);
    ///         if (invoice.HasOutput)
    ///         {
    ///             // use invoice.Output ...
    ///         }
    ///     }
    /// }
    /// </code>
    /// </example>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// This context was not built for handler execution, or <typeparamref name="TStep"/> is not a
    /// registered job type, or no output codec for <typeparamref name="TOut"/> was registered with it, or
    /// <typeparamref name="TStep"/> appears more than once among this job's ancestors (an ambiguous read).
    /// </exception>
    public static ValueTask<DependencyOutput<TOut>> Output<TStep, TOut>(
        this JobContext context, CancellationToken cancellationToken = default)
        where TStep : IWorkflowStep<TOut>
    {
        ArgumentNullException.ThrowIfNull(context);
        var (wireName, outputTypeInfo) = ResolveStep<TStep, TOut>(context);
        return context.GetDependencyOutputAsync(wireName, outputTypeInfo, cancellationToken);
    }

    // Resolves a step .NET type, off the JobContext-wired registry, to its Wire Name (the handle the
    // stored ancestor is matched by) and the JsonTypeInfo the [Job] output codec was registered with. The
    // TStep : IWorkflowStep<TOut> constraint is the compile-time guard; the registered codec's runtime
    // type must line up with TOut, which it does whenever the output metadata registered for the step is
    // the metadata for the type the step declares it produces.
    //
    // The output codec is registered BY STEP TYPE on JobRegistration.OutputTypeInfo, so these accessors
    // take no serializer. The [Job] source generator emits that field for a step implementing
    // IWorkflowStep<TOut> by referencing the output type's entry in one of the assembly's
    // JsonSerializerContext declarations; a step whose output type is not listed in a context is a build
    // error, so a missing codec is caught at compile time. (A hand-built registration may still pass
    // outputTypeInfo to JobRegistration.Create directly as an escape hatch.)
    //
    // AMBIGUITY: a step type used more than once in a workflow yields the SAME Wire Name for each member,
    // so a typed read of a repeated step type resolves against several ancestors and the underlying pull
    // rejects the ambiguity with a throw. Typed Output targets a step type used at most once among the
    // reader's ancestors. There is no name-based read that could pick one of several same-type ancestors:
    // member names are not persisted (only the Wire Name is), so repeats share a single read key. Reading
    // such an output means structuring the workflow so at most one ancestor of the reader is that type.
    private static (string WireName, JsonTypeInfo<TOut> OutputTypeInfo) ResolveStep<TStep, TOut>(JobContext context)
        where TStep : IWorkflowStep<TOut>
    {
        if (context.Registry is not { } registry)
        {
            throw new InvalidOperationException(
                "This JobContext has no job registry wired, so it cannot resolve a workflow step's Job " +
                "Output. The typed Output accessors are available only on a handler-execution context.");
        }

        if (registry.FindByJobType(typeof(TStep)) is not { } registration)
        {
            throw new InvalidOperationException(
                $"Step type '{typeof(TStep).Name}' is not a registered job type, so its Job Output " +
                "cannot be resolved. Register the step as a job before reading or writing its output.");
        }

        if (registration.OutputTypeInfo is not JsonTypeInfo<TOut> outputTypeInfo)
        {
            throw new InvalidOperationException(
                $"Step type '{typeof(TStep).Name}' has no registered Job Output codec for type " +
                $"'{typeof(TOut).Name}'. Register the step's output serialization metadata with it so its " +
                "output can be read and written without passing a serializer.");
        }

        return (registration.WireName, outputTypeInfo);
    }
}
