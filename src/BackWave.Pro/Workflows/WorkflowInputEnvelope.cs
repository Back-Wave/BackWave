using System.Text.Json;
using System.Text.Json.Nodes;
using BackWave.Diagnostics;

namespace BackWave.Pro;

/// <summary>
/// Bakes above-boundary workflow metadata into a member's payload bytes at enqueue and reads it back
/// inside a handler. Reserved properties may ride alongside the step's own JSON object: the immutable
/// Workflow Input seed, the member's parent-step wire names (so a trace reader can reconstruct DAG edges
/// from the otherwise-flat workflow trace), and the parent steps' send-span trace contexts (so a fan-in
/// member's process span can LINK to each upstream step). Because each is an extra property on the step's own JSON
/// object, a member's payload still deserializes to the step type unchanged (the reserved properties are
/// unknown fields the step decoder skips), and a member with <b>neither</b> a seed nor any parents keeps a
/// payload byte-identical to a standalone enqueue of the same step - a reserved property is applied only
/// when present. This is pure above-boundary transport: no new storage field, op, or below-boundary surface.
/// </summary>
internal static class WorkflowInputEnvelope
{
    // The reserved root-property namespace. A step payload is always a JSON object (a record), so appending
    // a property keeps the object shape the step decoder tolerates while carrying the metadata alongside.
    // Splice refuses to run on a step whose own payload already claims a property in this namespace.
    private const string ReservedPrefix = "$backwave.";
    private const string InputKey = "$backwave.workflowInput";
    // The parent-wire-names key is shared with Core, which reads it back at execute to emit the
    // backwave.workflow.after span tag; keep the two in lockstep by referencing the one constant.
    private const string AfterKey = BackWaveDiagnostics.WorkflowAfterPayloadKey;
    // The parent send-span trace contexts, read back by Core at process-start to LINK a fan-in member's
    // process span to each upstream step. Shared with Core the same way as AfterKey.
    private const string AfterTraceKey = BackWaveDiagnostics.WorkflowAfterTracePayloadKey;

    // The UTF-8 bytes of InputKey, derived from the constant itself so the raw-byte fast-path probe in
    // TryExtract can never drift from the key Splice writes.
    private static readonly byte[] InputKeyUtf8 = System.Text.Encoding.UTF8.GetBytes(InputKey);

    /// <summary>
    /// Returns <paramref name="stepPayload"/> with the optional Workflow Input seed and the member's
    /// parent-step wire names spliced in under their reserved keys. When there is no seed and no parent,
    /// the payload is returned unchanged (byte-identical to a standalone enqueue). The caller parses the
    /// seed once for the whole batch and passes it as <paramref name="seedTemplate"/>; each member gets a
    /// deep clone, so the seed bytes are tokenized once rather than re-parsed per member. Assumes
    /// <paramref name="stepPayload"/> is a JSON object (every <c>[Job]</c> record serializes to one).
    /// </summary>
    internal static ReadOnlyMemory<byte> Splice(
        ReadOnlyMemory<byte> stepPayload,
        JsonNode? seedTemplate,
        bool hasSeed,
        IReadOnlyList<string> afterWireNames,
        IReadOnlyList<string> afterTraceContexts,
        string stepWireName)
    {
        if (!hasSeed && afterWireNames.Count == 0 && afterTraceContexts.Count == 0)
        {
            // Nothing to carry: leave the step's own bytes exactly as a standalone enqueue would produce.
            return stepPayload;
        }

        var node = JsonNode.Parse(stepPayload.Span)
            ?? throw new InvalidOperationException("A workflow step payload deserialized to null and cannot carry workflow metadata.");
        if (node is not JsonObject obj)
        {
            throw new InvalidOperationException(
                "A workflow step payload must be a JSON object to carry workflow metadata; a non-object payload is unsupported.");
        }

        // Fail fast if the step's own payload already claims the reserved namespace: splicing metadata under
        // a colliding key would silently overwrite the consumer's own data, and a strict step decoder would
        // then reject our injected property. The namespace is reserved for above-boundary workflow transport.
        foreach (var property in obj)
        {
            if (property.Key.StartsWith(ReservedPrefix, StringComparison.Ordinal))
            {
                throw new InvalidWorkflowException(
                    $"Workflow step '{stepWireName}' has a payload property '{property.Key}' in the reserved " +
                    $"'{ReservedPrefix}' namespace, which BackWave uses to carry workflow metadata. Rename the " +
                    "property so the step payload does not collide with the reserved namespace.");
            }
        }

        if (hasSeed)
        {
            obj[InputKey] = seedTemplate?.DeepClone();
        }
        if (afterWireNames.Count > 0)
        {
            var array = new JsonArray();
            foreach (var wireName in afterWireNames)
            {
                array.Add(wireName);
            }
            obj[AfterKey] = array;
        }
        if (afterTraceContexts.Count > 0)
        {
            var array = new JsonArray();
            foreach (var traceContext in afterTraceContexts)
            {
                array.Add(traceContext);
            }
            obj[AfterTraceKey] = array;
        }
        return JsonSerializer.SerializeToUtf8Bytes(obj);
    }

    /// <summary>
    /// Extracts the baked Workflow Input from a member's raw payload bytes, deserialized with
    /// <paramref name="typeInfo"/>. Returns <see langword="false"/> when no seed was baked.
    /// </summary>
    internal static bool TryExtract<TInput>(
        ReadOnlyMemory<byte> payload,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TInput> typeInfo,
        out TInput input)
    {
        // Fast path: most payloads are not seeded workflow members. A raw byte probe for the reserved
        // key skips any JSON parse when the seed cannot be present (the same idiom Core uses for the
        // parent-wire-names key). Splice writes the key unescaped, so the probe cannot false-negative.
        if (payload.Span.IndexOf(InputKeyUtf8) < 0)
        {
            input = default!;
            return false;
        }

        // Slow path (the probe can false-positive on key bytes inside step data): a pooled read-only
        // JsonDocument parse, deserializing just the seed element - no mutable JsonNode DOM.
        using var document = JsonDocument.Parse(payload);
        if (document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty(InputKey, out var seed)
            && seed.ValueKind != JsonValueKind.Null)
        {
            input = seed.Deserialize(typeInfo)
                ?? throw new InvalidOperationException("The baked Workflow Input deserialized to null.");
            return true;
        }

        input = default!;
        return false;
    }
}
