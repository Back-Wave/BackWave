using System.Text.Json.Serialization.Metadata;
using BackWave.Storage;

namespace BackWave.Jobs;

/// <summary>
/// Execution context visible to a handler during one Attempt. A handler may tag the running job via
/// <see cref="AddLabel"/> / <see cref="AddTag"/>; the Tags <b>buffer here</b> and flush as a delta on
/// the Attempt's outcome write, so they commit atomically with the outcome rather than as a separate
/// store operation. It may likewise emit one opaque <b>Job Output</b> blob via <see cref="SetOutput"/>,
/// buffered the same way and persisted only on a successful outcome. Because it holds those mutable
/// buffers it is a class, not an immutable record.
/// </summary>
public sealed class JobContext
{
    /// <summary>The id of the job this Attempt is running.</summary>
    public required Guid JobId { get; init; }

    /// <summary>The current Attempt number, starting at 1 for the first execution try.</summary>
    public required int Attempt { get; init; }

    // Wired by the Shell from the storage layer. Injected as an abstraction so the accessor stays
    // testable and a handler never touches edges.
    /// <summary>
    /// Resolves a transitive Dependency ancestor's <b>Job Output</b> for
    /// <see cref="GetDependencyOutputAsync"/>. Null for a context built purely to buffer the write-side
    /// <see cref="SetOutput"/> or Tag delta, in which case the read accessor is unavailable.
    /// </summary>
    internal IDependencyResolver? DependencyResolver { get; init; }

    // Wired by the pump from JobRecord.Payload so a Pro workflow handler can read the baked Workflow
    // Input envelope out of the raw bytes (the seed is spliced into every member's payload at enqueue).
    /// <summary>
    /// The raw stored payload bytes of the job this Attempt is running, exactly as enqueued. Null for a
    /// context built purely to buffer a write-side <see cref="SetOutput"/> or Tag delta rather than for
    /// handler execution. An ordinary handler never needs this - it receives the already-deserialized
    /// payload - but the workflow layer reads its baked Workflow Input out of these bytes.
    /// </summary>
    internal ReadOnlyMemory<byte>? Payload { get; init; }

    // Wired by the pump so a Pro workflow accessor can resolve a step .NET type to its registered Wire
    // Name and output codec (the typed ctx.Output<TStep,TOut> / ctx.SetOutput<TStep,TOut> path), without
    // the context holding a client. Null on a context not built for handler execution.
    /// <summary>
    /// The Job Registry for the running node, or null on a context not built for handler execution. Lets
    /// the workflow layer map a step type to its Wire Name and registered output codec; an ordinary
    /// handler never needs it.
    /// </summary>
    internal JobRegistry? Registry { get; init; }

    // Wired by the pump from the routed registration's Wire Name so the typed SetOutput accessor can
    // verify a handler only emits its OWN step's output - a handler for step A must not buffer a value
    // under step B's output codec, which a reader would then decode with the wrong shape. Null on a
    // context not built for handler execution; a hand-built context that leaves it null skips the check.
    /// <summary>
    /// The Wire Name of the step this Attempt is running, or null on a context not built for handler
    /// execution. Lets the workflow layer confirm a handler emits Job Output only for its own step type.
    /// </summary>
    internal string? RunningWireName { get; init; }

    private JobTags _bufferedTags = JobTags.Empty;

    /// <summary>
    /// The runtime Tags this handler has buffered so far — the delta the Shell flushes onto the
    /// Attempt's fenced outcome write. Set semantics, so an identical Tag added twice collapses.
    /// </summary>
    public JobTags BufferedTags => _bufferedTags;

    /// <summary>Tags the running job with a Label (a bare string); idempotent (set semantics).</summary>
    /// <param name="value">The label text. Must be non-empty. A colon inside it is ordinary data, never a separator.</param>
    /// <exception cref="ArgumentException"><paramref name="value"/> is null or empty.</exception>
    public void AddLabel(string value) => _bufferedTags = _bufferedTags.With(JobTag.Label(value));

    /// <summary>Tags the running job with a Keyed Tag; idempotent (set semantics).</summary>
    /// <param name="key">The tag's dimension. Must be non-empty.</param>
    /// <param name="value">The value under that dimension. Must be non-empty.</param>
    /// <exception cref="ArgumentException"><paramref name="key"/> is null or empty, or <paramref name="value"/> is null or empty.</exception>
    public void AddTag(string key, string value) => _bufferedTags = _bufferedTags.With(JobTag.Keyed(key, value));

    /// <summary>
    /// The single opaque <b>Job Output</b> blob this handler has buffered, already serialized by
    /// <see cref="JobOutputCodec"/> — the value flushed onto the Attempt's outcome write. Null until
    /// <see cref="SetOutput"/> is called; the store persists it <b>only</b> on a successful outcome (a
    /// superseded or failed Attempt discards it).
    /// </summary>
    public ReadOnlyMemory<byte>? BufferedOutput { get; private set; }

    /// <summary>
    /// Emits this Attempt's <b>Job Output</b>: one opaque value a transitive Dependency descendant may
    /// later pull. Serialized immediately through <see cref="JobOutputCodec"/> (the same JSON serializer
    /// as the payload, so producer shape equals reader shape) and buffered here — last write wins within
    /// the Attempt. It is not written through immediately; it commits atomically with the outcome and
    /// persists only on success. The store rejects (never truncates) output over its size limit at write
    /// time.
    /// </summary>
    /// <typeparam name="T">The output value type.</typeparam>
    /// <param name="value">The output value to emit.</param>
    /// <param name="typeInfo">
    /// The source-generated serialization metadata for <typeparamref name="T"/>, keeping the path
    /// reflection-free.
    /// </param>
    public void SetOutput<T>(T value, JsonTypeInfo<T> typeInfo)
        => BufferedOutput = JobOutputCodec.Encode(value, typeInfo);

    /// <summary>
    /// <b>Pulls</b> the <b>Job Output</b> of one of this job's transitive Dependency ancestors. The
    /// handle is a node name for a fellow Workflow member (resolved against this job's ancestor set, so
    /// a non-ancestor sibling is unresolvable — the scope guarantee) or a <see cref="Guid"/>-shaped
    /// string for a raw Dependency. <b>Lazy</b>: the read happens only when called, so no speculative IO
    /// occurs. Returns the ancestor's terminal state alongside the deserialized output; <b>absence is
    /// normal</b> — a failed, cancelled, or discarded ancestor, or a succeeded one that emitted nothing,
    /// returns <see cref="DependencyOutput{T}.HasOutput"/> = false (no throw). When the handle resolves
    /// to no ancestor at all the result is likewise empty (and carries
    /// <see cref="JobState.AwaitingParent"/> as the "unresolved" sentinel). The blob deserializes with
    /// the same <see cref="JsonTypeInfo{T}"/> shape the producer wrote.
    /// </summary>
    /// <typeparam name="T">The ancestor's output value type.</typeparam>
    /// <param name="nameOrJobId">
    /// The ancestor's Workflow node name, or a <see cref="Guid"/>-shaped string naming a raw Dependency.
    /// </param>
    /// <param name="typeInfo">
    /// The source-generated deserialization metadata for <typeparamref name="T"/>, matching the shape
    /// the producer wrote.
    /// </param>
    /// <param name="cancellationToken">Signaled to abandon the read.</param>
    /// <returns>
    /// The ancestor's terminal state with its deserialized output; an empty result (with
    /// <see cref="DependencyOutput{T}.HasOutput"/> = false) when the ancestor emitted nothing or the
    /// handle resolves to no ancestor.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// This context has no dependency resolver wired (it was not built for handler execution, so
    /// ancestor output cannot be read); or <paramref name="nameOrJobId"/> is a node name matching more
    /// than one of this job's ancestors, so reading its output by that name is ambiguous.
    /// </exception>
    public async ValueTask<DependencyOutput<T>> GetDependencyOutputAsync<T>(
        string nameOrJobId, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken = default)
    {
        if (DependencyResolver is null)
        {
            throw new InvalidOperationException(
                "This JobContext has no DependencyResolver wired, so it cannot read ancestor output. " +
                "GetDependencyOutputAsync is available only on a handler-execution context.");
        }

        var resolved = await DependencyResolver
            .ResolveAsync(JobId, nameOrJobId, cancellationToken).ConfigureAwait(false);
        if (resolved is null)
        {
            // The handle named no ancestor (a non-ancestor sibling, or an unknown id) — unresolvable
            // by scope, returned as a clean absence rather than a throw.
            return new DependencyOutput<T>(JobState.AwaitingParent, HasOutput: false, Output: default);
        }

        // A zero-length blob counts as absence, exactly like null. Shipped v1.2.0 persisted a non-null
        // EMPTY Output blob on every silent success (a write-side cast bug, since fixed), so a store
        // upgraded in place can still carry stale 0-byte rows - decoding one would throw JsonException.
        return resolved.Output is { Length: > 0 } blob
            ? new DependencyOutput<T>(resolved.AncestorState, HasOutput: true, JobOutputCodec.Decode(blob, typeInfo))
            : new DependencyOutput<T>(resolved.AncestorState, HasOutput: false, Output: default);
    }
}
