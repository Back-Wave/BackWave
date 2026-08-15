using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace BackWave.SourceGenerators;

/// <summary>
/// A value-equal wrapper over <see cref="ImmutableArray{T}"/>. ImmutableArray's default
/// equality is reference equality on its backing array, which silently defeats incremental
/// caching the moment it appears in a pipeline model; this compares element-wise so cached
/// steps actually compare equal (issue 0043).
/// </summary>
internal readonly struct EquatableArray<T>(ImmutableArray<T> array)
    : IEquatable<EquatableArray<T>>, IReadOnlyList<T>
    where T : IEquatable<T>
{
    private readonly ImmutableArray<T> _array = array;

    public int Count => _array.IsDefault ? 0 : _array.Length;

    public T this[int index] => _array[index];

    public ImmutableArray<T> AsImmutableArray() => _array.IsDefault ? ImmutableArray<T>.Empty : _array;

    public bool Equals(EquatableArray<T> other)
    {
        if (_array.IsDefault)
        {
            return other._array.IsDefault;
        }
        if (other._array.IsDefault || _array.Length != other._array.Length)
        {
            return false;
        }
        for (var i = 0; i < _array.Length; i++)
        {
            if (!_array[i].Equals(other._array[i]))
            {
                return false;
            }
        }
        return true;
    }

    public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

    public override int GetHashCode()
    {
        if (_array.IsDefault)
        {
            return 0;
        }
        var hash = 17;
        foreach (var item in _array)
        {
            hash = (hash * 31) + (item?.GetHashCode() ?? 0);
        }
        return hash;
    }

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)AsImmutableArray()).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public static implicit operator EquatableArray<T>(ImmutableArray<T> array) => new(array);
}

/// <summary>
/// A value-equal stand-in for <see cref="Location"/>: a SourceTree-backed Location holds a
/// reference to the syntax tree and is never cache-equal across edits. We carry only the
/// value-equal coordinates in cached models and rebuild a Location lazily when a diagnostic
/// is actually reported.
/// </summary>
internal sealed record LocationInfo(string FilePath, TextSpan TextSpan, LinePositionSpan LineSpan)
{
    public Location ToLocation() => Location.Create(FilePath, TextSpan, LineSpan);

    public static LocationInfo? CreateFrom(Location location)
        => location.SourceTree is { } tree
            ? new LocationInfo(tree.FilePath, location.SourceSpan, location.GetLineSpan().Span)
            : null;
}

/// <summary>
/// A value-equal description of a diagnostic to report, kept out of the cached generation
/// models so they stay cache-equal. The actual <see cref="Diagnostic"/> is built only in the
/// reporting branch (issue 0043).
/// </summary>
internal sealed record DiagnosticInfo(
    DiagnosticDescriptor Descriptor, LocationInfo? Location, EquatableArray<string> MessageArgs)
{
    public static DiagnosticInfo Create(DiagnosticDescriptor descriptor, LocationInfo? location, params string[] messageArgs)
        => new(descriptor, location, new EquatableArray<string>(messageArgs.ToImmutableArray()));

    public Diagnostic ToDiagnostic()
        => Diagnostic.Create(
            Descriptor, Location?.ToLocation(), MessageArgs.AsImmutableArray().Cast<object?>().ToArray());
}

/// <summary>One IJobHandler&lt;T&gt; implementation discovered via the syntax provider (not the Compilation).</summary>
internal sealed record HandlerInfo(string JobTypeFqn, string HandlerFqn) : IEquatable<HandlerInfo>;

/// <summary>
/// One Workflow Input seed type (a BackWave.Pro.IWorkflowInput implementor) discovered via the syntax
/// provider. Carries a value-equal Location so a "not listed in any JsonSerializerContext" diagnostic
/// can point at the seed declaration without holding a live Location in the cached model.
/// </summary>
internal sealed record SeedTypeInfo(string TypeFqn, LocationInfo? Location) : IEquatable<SeedTypeInfo>;

/// <summary>
/// One JsonSerializerContext and the set of types listed on it via [JsonSerializable]. Discovered via
/// the attribute provider so the completeness check can prove every workflow output/seed type has a
/// serializer without walking the whole Compilation.
/// </summary>
internal sealed record JsonContextInfo(string ContextFqn, EquatableArray<string> ListedTypeFqns)
    : IEquatable<JsonContextInfo>;

/// <summary>The value-equal result of parsing one [Job] declaration: a model, or a diagnostic, or neither.</summary>
internal sealed record ParseResult(JobModel? Model, DiagnosticInfo? Diagnostic);
