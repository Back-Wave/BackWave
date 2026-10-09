using System;
using System.Collections.Generic;
using System.Linq;

namespace BackWave.SourceGenerators;

/// <summary>
/// Which JsonSerializerContext serves each type, so every caller applies the same rule: one context binds,
/// and the index never picks one silently when several list a type. A partial context can list types on
/// more than one part, so the contexts are distinct per type, in ordinal order for a stable message.
/// </summary>
internal sealed class JsonContextIndex
{
    private readonly Dictionary<string, List<string>> _contextsByType;
    private readonly Dictionary<string, List<ContextListing>> _unusableByType;
    private readonly Dictionary<string, string> _serializationOnlyByContext;

    public JsonContextIndex(IEnumerable<JsonContextInfo?> jsonContexts)
    {
        var contexts = jsonContexts.Where(c => c is not null).Select(c => c!).ToList();

        _contextsByType = contexts
            .SelectMany(c => c.ListedTypeFqns.Select(typeFqn => (TypeFqn: typeFqn, c.ContextFqn)))
            .GroupBy(listing => listing.TypeFqn, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.Select(listing => listing.ContextFqn)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(contextFqn => contextFqn, StringComparer.Ordinal)
                    .ToList(),
                StringComparer.Ordinal);

        // In ordinal context order, so the first unusable listing of a type names the context that BW0013 reports.
        _unusableByType = contexts
            .OrderBy(c => c.ContextFqn, StringComparer.Ordinal)
            .SelectMany(c => c.UnusableListings.Select(listing => (listing.TypeFqn, Listing: new ContextListing(c.ContextFqn, listing.Reason))))
            .GroupBy(listing => listing.TypeFqn, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(listing => listing.Listing).ToList(), StringComparer.Ordinal);

        // Every part of a partial context reads the attributes of the whole context, so the parts agree.
        _serializationOnlyByContext = contexts
            .Where(c => c.SerializationOnlyReason is not null)
            .GroupBy(c => c.ContextFqn, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().SerializationOnlyReason!, StringComparer.Ordinal);
    }

    /// <summary>
    /// Resolves <paramref name="typeFqn"/>. A usable listing wins over an unusable one, so a type is Unusable
    /// only when unusable listings alone name it.
    /// </summary>
    public JsonContextResolution Resolve(string typeFqn)
    {
        if (_contextsByType.TryGetValue(typeFqn, out var contexts))
        {
            return contexts.Count == 1 ? JsonContextResolution.Bound(contexts) : JsonContextResolution.Ambiguous(contexts);
        }
        return _unusableByType.TryGetValue(typeFqn, out var listings)
            ? JsonContextResolution.Unusable(listings[0].ContextFqn, listings[0].Reason)
            : JsonContextResolution.Missing;
    }

    /// <summary>
    /// Resolves <paramref name="typeFqn"/> for a delegated payload member: a class payload, or a method-sugar member
    /// type. As <see cref="Resolve"/>, but a context with a serialization-only listing or default is Unusable,
    /// because STJ can give that mode to any type that a listing reaches.
    /// </summary>
    public JsonContextResolution ResolveDelegated(string typeFqn)
    {
        var resolution = Resolve(typeFqn);
        return resolution.Kind == JsonContextResolutionKind.Bound
            && _serializationOnlyByContext.TryGetValue(resolution.ContextFqn!, out var reason)
            ? JsonContextResolution.Unusable(resolution.ContextFqn!, reason)
            : resolution;
    }

    private sealed record ContextListing(string ContextFqn, string Reason);
}

/// <summary>How a type resolved against the JsonSerializerContexts in the assembly.</summary>
internal enum JsonContextResolutionKind
{
    /// <summary>No context lists the type.</summary>
    Missing,

    /// <summary>Exactly one context lists the type.</summary>
    Bound,

    /// <summary>More than one context lists the type.</summary>
    Ambiguous,

    /// <summary>Only listings the generated codec cannot use name the type.</summary>
    Unusable,
}

/// <summary>The result of <see cref="JsonContextIndex.Resolve"/>.</summary>
internal readonly struct JsonContextResolution
{
    private readonly IReadOnlyList<string>? _contexts;

    private JsonContextResolution(
        JsonContextResolutionKind kind, IReadOnlyList<string>? contexts, string? contextFqn, string? reason)
    {
        Kind = kind;
        _contexts = contexts;
        ContextFqn = contextFqn;
        Reason = reason;
    }

    public JsonContextResolutionKind Kind { get; }

    /// <summary>The contexts that list the type, in ordinal order: one when Bound, more when Ambiguous, else none.</summary>
    public IReadOnlyList<string> Contexts => _contexts ?? Array.Empty<string>();

    /// <summary>The context that serves the type when Bound, or the context with the unusable listing when Unusable.</summary>
    public string? ContextFqn { get; }

    /// <summary>Why the listing is unusable, with its fix, when Unusable.</summary>
    public string? Reason { get; }

    public static JsonContextResolution Missing => default;

    public static JsonContextResolution Bound(IReadOnlyList<string> contexts)
        => new(JsonContextResolutionKind.Bound, contexts, contexts[0], reason: null);

    public static JsonContextResolution Ambiguous(IReadOnlyList<string> contexts)
        => new(JsonContextResolutionKind.Ambiguous, contexts, contextFqn: null, reason: null);

    public static JsonContextResolution Unusable(string contextFqn, string reason)
        => new(JsonContextResolutionKind.Unusable, contexts: null, contextFqn, reason);
}
