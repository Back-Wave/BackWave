using BackWave.Storage;
using Microsoft.AspNetCore.Http;

namespace BackWave.Dashboard;

/// <summary>
/// Round-trips a job list's <see cref="JobTagPredicate"/>s through the URL query string,
/// for the Jobs page's click-to-filter pills and facet links. The encoding is deliberately chosen so
/// it <b>can never collide with a colon (or any other character) inside a Tag value</b> — storage
/// never parses a Tag, so neither does this: there is <b>no in-band separator to split on</b>.
/// <list type="bullet">
/// <item><b>has-label</b> — one <c>tl=&lt;value&gt;</c> param. The value is a single, whole
/// percent-encoded query value, so a Label like <c>ratio 3:1</c> rides through verbatim; the colon is
/// ordinary data and is never treated as a key/value separator.</item>
/// <item><b>has key=value</b> — a matched pair of params, <c>tk=&lt;key&gt;</c> and <c>tv=&lt;value&gt;</c>,
/// associated <em>positionally</em>: the i-th <c>tk</c> pairs with the i-th <c>tv</c>. Key and value are
/// each their own whole query value, so neither a colon nor an <c>=</c> inside either half can ever be
/// mistaken for structure — there is nothing to split. (A <c>tk</c> with no matching <c>tv</c> is dropped.)</item>
/// </list>
/// has-key-any-value (<see cref="JobTagPredicate.HasKey"/>) is not surfaced by a pill or facet click,
/// so it has no URL form here. Each clicked pill/facet ANDs its predicate onto whatever filter is
/// already active (the page composes these with State/Queue/WireName).
/// </summary>
internal static class TagFilterUrl
{
    public const string LabelParam = "tl";
    public const string KeyParam = "tk";
    public const string ValueParam = "tv";

    /// <summary>The tag predicates carried by a request's query string, has-label first then has-key=value.</summary>
    public static IReadOnlyList<JobTagPredicate> Parse(IQueryCollection query)
    {
        var predicates = new List<JobTagPredicate>();
        foreach (var value in query[LabelParam])
        {
            if (!string.IsNullOrEmpty(value))
            {
                predicates.Add(JobTagPredicate.HasLabel(value));
            }
        }
        // Keyed Tags pair the i-th tk with the i-th tv positionally — no in-band separator. A lone
        // tk (or tv) at the tail with no partner carries no predicate and is ignored.
        var keys = query[KeyParam];
        var values = query[ValueParam];
        var pairs = Math.Min(keys.Count, values.Count);
        for (var i = 0; i < pairs; i++)
        {
            var key = keys[i];
            var value = values[i];
            if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(value))
            {
                predicates.Add(JobTagPredicate.HasKeyValue(key, value));
            }
        }
        return predicates;
    }

    /// <summary>The query-string fragments (already percent-encoded) for these predicates, e.g.
    /// <c>["tl=urgent", "tk=tenant&amp;tv=acme"]</c>. has-key-any-value predicates are skipped (no UI surface).</summary>
    public static IEnumerable<string> ToQueryParts(IReadOnlyList<JobTagPredicate> predicates)
    {
        foreach (var predicate in predicates)
        {
            if (predicate.Value is not { } value)
            {
                continue; // has-key-any-value: no pill/facet produces it, so it has no URL form.
            }
            yield return predicate.Key.Length == 0
                ? $"{LabelParam}={Uri.EscapeDataString(value)}"
                : $"{KeyParam}={Uri.EscapeDataString(predicate.Key)}&{ValueParam}={Uri.EscapeDataString(value)}";
        }
    }

    /// <summary>The query-string fragment for one Tag rendered as a pill/facet entry — a Label by its
    /// value, a Keyed Tag by its (key, value). The single-Tag inverse of <see cref="Parse"/>.</summary>
    public static string ToQueryPart(JobTag tag)
        => tag.IsLabel
            ? $"{LabelParam}={Uri.EscapeDataString(tag.Value)}"
            : $"{KeyParam}={Uri.EscapeDataString(tag.Key)}&{ValueParam}={Uri.EscapeDataString(tag.Value)}";
}
