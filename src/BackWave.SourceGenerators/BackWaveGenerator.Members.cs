using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace BackWave.SourceGenerators;

/// <summary>Payload member classification: the scalar set, delegated members, and the types STJ cannot round-trip.</summary>
public sealed partial class BackWaveGenerator
{
    /// <summary>
    /// The member model for one payload member. A type outside the scalar set becomes a
    /// <see cref="MemberKind.Delegated"/> member, which <see cref="Emit"/> binds to a JsonSerializerContext.
    /// A type that does not resolve gives BW0004 here, because no context can list it. Diagnostics name the
    /// member by <paramref name="sourceName"/>, the name the user wrote.
    /// </summary>
    private static PayloadMember? ClassifyMember(
        string name, string sourceName, ITypeSymbol type, Location location, string payloadName,
        out DiagnosticInfo? failure)
    {
        failure = null;
        var locationInfo = LocationInfo.CreateFrom(location);
        if (TryClassifyScalar(type, out var kind, out var isNullable))
        {
            return new PayloadMember
            {
                Name = name,
                SourceName = sourceName,
                TypeFqn = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                Kind = kind,
                IsNullableValue = isNullable,
                MissingLiteral = DefaultLiteral(type),
                Location = locationInfo,
            };
        }

        if (ContainsErrorType(type))
        {
            failure = DiagnosticInfo.Create(
                JobDiagnostics.UnsupportedPayloadMember, locationInfo, sourceName, payloadName, DisplayFqn(type),
                "the type did not resolve when the BackWave generator ran (for example, another source generator " +
                "declares it) - declare the type in source, or " + JobDiagnostics.RegisterJobByHand);
            return null;
        }

        if (DescribeRejectedByStj(type) is { } rejected)
        {
            failure = DiagnosticInfo.Create(
                JobDiagnostics.UnsupportedPayloadMember, locationInfo, sourceName, payloadName, DisplayFqn(type),
                $"{rejected}, or {JobDiagnostics.RegisterJobByHand}");
            return null;
        }

        var infoType = type.IsReferenceType ? type.WithNullableAnnotation(NullableAnnotation.NotAnnotated) : type;
        return new PayloadMember
        {
            Name = name,
            SourceName = sourceName,
            TypeFqn = type.ToDisplayString(AnnotatedFormat),
            Kind = MemberKind.Delegated,
            MissingLiteral = DefaultLiteral(type),
            Delegation = new Delegation
            {
                TypeFqn = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                InfoTypeFqn = infoType.ToDisplayString(AnnotatedFormat),
                IsReferenceType = type.IsReferenceType,
                IsUnannotatedReference = IsUnannotatedReference(type),
                IsNullableValue = IsNullableValue(type),
                IsImmutableArray = IsImmutableArray(type),
            },
            Location = locationInfo,
        };
    }

    /// <summary>
    /// The member model for a constructor member whose parameter type differs from its property type. STJ always
    /// has metadata for the property type, but for the parameter type only when it binds this constructor, and it
    /// cannot read back some parameter types (IReadOnlySet). So the codec reads the property type and passes the
    /// value to the constructor, which needs an implicit conversion; else BW0004. A scalar parameter is BW0004 too,
    /// because the codec writes and reads a scalar member as the parameter type.
    /// </summary>
    private static PayloadMember? ClassifyAsPropertyType(
        IPropertySymbol property, IParameterSymbol parameter, Compilation compilation, string payloadName,
        out DiagnosticInfo? failure)
    {
        if (TryClassifyScalar(parameter.Type, out _, out _))
        {
            failure = DiagnosticInfo.Create(
                JobDiagnostics.UnsupportedPayloadMember, LocationInfo.CreateFrom(property.Locations[0]),
                property.Name, payloadName, DisplayFqn(property.Type),
                $"the constructor parameter type '{DisplayFqn(parameter.Type)}' is not the property type " +
                $"'{DisplayFqn(property.Type)}', so the codec cannot write the value that it passes to the constructor - " +
                "make the two types the same, or " + JobDiagnostics.RegisterJobByHand);
            return null;
        }
        if (!compilation.ClassifyConversion(property.Type, parameter.Type).IsImplicit)
        {
            failure = DiagnosticInfo.Create(
                JobDiagnostics.UnsupportedPayloadMember, LocationInfo.CreateFrom(property.Locations[0]),
                property.Name, payloadName, DisplayFqn(property.Type),
                $"the property type '{DisplayFqn(property.Type)}' does not convert to the constructor parameter type " +
                $"'{DisplayFqn(parameter.Type)}', so the codec cannot pass the value it reads back to the constructor - " +
                "make the two types the same, or " + JobDiagnostics.RegisterJobByHand);
            return null;
        }
        return ClassifyMember(property.Name, property.Name, property.Type, property.Locations[0], payloadName, out failure);
    }

    private static readonly SymbolDisplayFormat AnnotatedFormat = SymbolDisplayFormat.FullyQualifiedFormat
        .AddMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    /// <summary>The fully qualified type without the global:: alias, for a diagnostic message.</summary>
    internal static string DisplayFqn(string typeFqn) => typeFqn.Replace("global::", "");

    private static string DisplayFqn(ITypeSymbol type)
        => DisplayFqn(type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));

    private static bool ContainsErrorType(ITypeSymbol type)
        => type switch
        {
            { TypeKind: TypeKind.Error } => true,
            IArrayTypeSymbol array => ContainsErrorType(array.ElementType),
            INamedTypeSymbol named => named.TypeArguments.Any(ContainsErrorType),
            _ => false,
        };

    /// <summary>
    /// Why System.Text.Json cannot round-trip the type, its array elements, or its type arguments; else null. These
    /// are the types that STJ maps to its unsupported-type converter, which throws at run time, pointers and ref
    /// structs, which cannot be a JsonTypeInfo type argument, value tuples, whose fields STJ ignores, object, which
    /// STJ reads back as a JsonElement, stacks, which STJ reads back in reverse order, multi-dimensional arrays,
    /// interfaces and abstract classes that STJ cannot create, and collections that STJ cannot fill.
    /// </summary>
    private static string? DescribeRejectedByStj(ITypeSymbol type)
    {
        if (type is IPointerTypeSymbol or IFunctionPointerTypeSymbol)
        {
            return RejectedByDesign($"pointers such as '{DisplayFqn(type)}'");
        }
        if (type is IArrayTypeSymbol array)
        {
            return array.Rank > 1
                ? $"System.Text.Json cannot write multi-dimensional arrays such as '{DisplayFqn(type)}' - use a " +
                    "jagged array instead"
                : DescribeRejectedByStj(array.ElementType);
        }
        if (type.IsRefLikeType)
        {
            return RejectedByDesign($"ref structs such as '{DisplayFqn(type)}'");
        }
        if (type.SpecialType is SpecialType.System_IntPtr or SpecialType.System_UIntPtr)
        {
            return RejectedByDesign($"native-sized integers such as '{DisplayFqn(type)}'");
        }
        if (type.TypeKind == TypeKind.Delegate
            || type.SpecialType is SpecialType.System_Delegate or SpecialType.System_MulticastDelegate)
        {
            return RejectedByDesign($"delegates such as '{DisplayFqn(type)}'");
        }
        if (type.IsTupleType)
        {
            return $"System.Text.Json ignores the fields of tuples such as '{DisplayFqn(type)}', so the value would " +
                "not round-trip - use a record instead";
        }
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (current.ToDisplayString() == "System.Reflection.MemberInfo")
            {
                return RejectedByDesign($"reflection types such as '{DisplayFqn(type)}'");
            }
        }
        if (type.ToDisplayString() == "System.Runtime.Serialization.SerializationInfo")
        {
            return RejectedByDesign($"'{DisplayFqn(type)}'");
        }
        if (type.SpecialType == SpecialType.System_Object || type.TypeKind == TypeKind.Dynamic)
        {
            return $"System.Text.Json reads '{DisplayFqn(type)}' back as a JsonElement, so the value would not " +
                "round-trip - use a concrete type or JsonElement instead";
        }
        if (type is INamedTypeSymbol stack && IsStack(stack))
        {
            return $"System.Text.Json reads stacks such as '{DisplayFqn(type)}' back in reverse order, so the value " +
                "would not round-trip - use a list or an array instead";
        }
        if (type is INamedTypeSymbol { IsAbstract: true, TypeKind: TypeKind.Interface or TypeKind.Class } abstractType
            && !StjReadsAbstractType(abstractType))
        {
            var kind = type.TypeKind == TypeKind.Interface ? "interfaces" : "abstract classes";
            return $"System.Text.Json cannot create {kind} such as '{DisplayFqn(type)}' when it reads the value " +
                "back - annotate the type with [JsonPolymorphic] and [JsonDerivedType], or use a concrete type instead";
        }
        if (type is INamedTypeSymbol { IsAbstract: false, TypeKind: TypeKind.Class or TypeKind.Struct } collection
            && DescribeUnreadableCollection(collection) is { } unreadable)
        {
            return unreadable;
        }
        return type is INamedTypeSymbol named
            ? named.TypeArguments.Select(DescribeRejectedByStj).FirstOrDefault(r => r is not null)
            : null;
    }

    private static string RejectedByDesign(string rejected)
        => $"System.Text.Json rejects {rejected} by design - use a supported type instead";

    private static bool IsStack(INamedTypeSymbol type)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (current.OriginalDefinition.ToDisplayString()
                is "System.Collections.Generic.Stack<T>"
                or "System.Collections.Concurrent.ConcurrentStack<T>"
                or "System.Collections.Immutable.ImmutableStack<T>"
                or "System.Collections.Immutable.IImmutableStack<T>")
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Why System.Text.Json cannot read back the concrete collection type; else null. STJ calls a public
    /// parameterless constructor and fills the collection through ICollection&lt;T&gt; or
    /// IDictionary&lt;TKey, TValue&gt;, and it has its own code for the queues and the immutable collections.
    /// Only the type itself counts, not its type arguments.
    /// </summary>
    private static string? DescribeUnreadableCollection(INamedTypeSymbol type)
    {
        if (type.SpecialType == SpecialType.System_String
            || !type.AllInterfaces.Any(i => i.SpecialType == SpecialType.System_Collections_IEnumerable)
            || type.ContainingNamespace?.ToDisplayString() == "System.Text.Json.Nodes"
            || HasTypeLevelJsonConverter(type))
        {
            return null;
        }

        var interfaces = new HashSet<string>(
            type.AllInterfaces.Select(i => i.OriginalDefinition.ToDisplayString()), StringComparer.Ordinal);
        if (!interfaces.Contains("System.Collections.Generic.IEnumerable<T>"))
        {
            return $"System.Text.Json cannot round-trip non-generic collections such as '{DisplayFqn(type)}' - use a " +
                "generic collection instead";
        }
        if (IsQueue(type)
            || (type.ContainingType is null
                && type.ContainingNamespace?.ToDisplayString() == "System.Collections.Immutable"))
        {
            return null;
        }

        var fillable = type.OriginalDefinition.ToDisplayString() != "System.ArraySegment<T>"
            && (interfaces.Contains("System.Collections.Generic.ICollection<T>")
                || interfaces.Contains("System.Collections.Generic.IDictionary<TKey, TValue>"));
        var creatable = type.IsValueType
            || type.InstanceConstructors.Any(c => c.Parameters.IsEmpty && c.DeclaredAccessibility == Accessibility.Public);
        return fillable && creatable
            ? null
            : $"System.Text.Json writes collections such as '{DisplayFqn(type)}' but cannot read them back - use a " +
                "list, an array, or a dictionary instead";
    }

    private static bool IsQueue(INamedTypeSymbol type)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (current.OriginalDefinition.ToDisplayString()
                is "System.Collections.Generic.Queue<T>" or "System.Collections.Concurrent.ConcurrentQueue<T>")
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// True when System.Text.Json can read back a value of the interface or abstract class: a collection interface
    /// it maps to a concrete collection, a JsonNode type, or a type that declares its own polymorphism or converter.
    /// Only the type itself counts, not the types of its properties.
    /// </summary>
    private static bool StjReadsAbstractType(INamedTypeSymbol type)
        => StjCollectionInterfaces.Contains(type.OriginalDefinition.ToDisplayString())
            || type.ContainingNamespace?.ToDisplayString() == "System.Text.Json.Nodes"
            || HasTypeLevelJsonConverter(type)
            || type.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString()
                is "System.Text.Json.Serialization.JsonPolymorphicAttribute"
                or "System.Text.Json.Serialization.JsonDerivedTypeAttribute");

    /// <summary>True when the type itself carries [JsonConverter] or an attribute derived from it.</summary>
    private static bool HasTypeLevelJsonConverter(INamedTypeSymbol type)
        => type.GetAttributes().Any(a => IsJsonConverterAttribute(a.AttributeClass));

    /// <summary>True for JsonConverterAttribute and every attribute derived from it.</summary>
    private static bool IsJsonConverterAttribute(INamedTypeSymbol? attributeClass)
    {
        for (; attributeClass is not null; attributeClass = attributeClass.BaseType)
        {
            if (attributeClass.ToDisplayString() == "System.Text.Json.Serialization.JsonConverterAttribute")
            {
                return true;
            }
        }
        return false;
    }

    // IReadOnlySet<T> is missing on purpose: STJ writes it but throws when it reads it back.
    private static readonly HashSet<string> StjCollectionInterfaces = new(StringComparer.Ordinal)
    {
        "System.Collections.Generic.IEnumerable<T>",
        "System.Collections.Generic.ICollection<T>",
        "System.Collections.Generic.IList<T>",
        "System.Collections.Generic.IReadOnlyCollection<T>",
        "System.Collections.Generic.IReadOnlyList<T>",
        "System.Collections.Generic.ISet<T>",
        "System.Collections.Generic.IDictionary<TKey, TValue>",
        "System.Collections.Generic.IReadOnlyDictionary<TKey, TValue>",
        "System.Collections.Immutable.IImmutableList<T>",
        "System.Collections.Immutable.IImmutableSet<T>",
        "System.Collections.Immutable.IImmutableDictionary<TKey, TValue>",
        "System.Collections.Immutable.IImmutableQueue<T>",
    };

    /// <summary>
    /// BW0011 when a delegated class-payload member carries System.Text.Json attributes on its property, an
    /// attribute derived from [JsonConverter] included,
    /// else null. The codec serializes the member with the metadata of its type, so the attributes have no effect.
    /// On a scalar member they have no effect either, but the job still round-trips, so the BW0018
    /// warning goes to warnings and the job is still emitted. One diagnostic names every attribute on the member.
    /// </summary>
    private static DiagnosticInfo? DetectJsonAttribute(
        PayloadMember? member, IPropertySymbol property, string payloadName,
        ImmutableArray<DiagnosticInfo>.Builder warnings)
    {
        if (member is null)
        {
            return null;
        }

        var attributes = property.GetAttributes()
            .Select(a => a.AttributeClass)
            .Where(c => c?.ContainingNamespace?.ToDisplayString() == "System.Text.Json.Serialization"
                || IsJsonConverterAttribute(c))
            .Select(c => c!)
            .ToList();
        if (attributes.Count == 0)
        {
            return null;
        }

        var found = $"{JoinAttributeNames(attributes)}, which {(attributes.Count == 1 ? "has" : "have")} no effect";
        if (member.Kind != MemberKind.Delegated)
        {
            warnings.Add(DiagnosticInfo.Create(
                JobDiagnostics.JsonAttributeOnScalarMember, member.Location,
                member.SourceName, payloadName, found, attributes.Count == 1 ? "attribute" : "attributes"));
            return null;
        }
        var typeName = DisplayFqn(member.Delegation!.TypeFqn);
        var toMove = attributes.Where(IsValidOnType).ToList();
        var toRemove = attributes.Where(a => !IsValidOnType(a)).ToList();
        var settings = toMove.Count == 1 ? "setting" : "settings";
        var fix = toMove.Count == 0 ? $"Remove the {(toRemove.Count == 1 ? "attribute" : "attributes")}"
            : toRemove.Count == 0 ? $"Move the {settings} to the type '{typeName}' if you own it"
            : $"Remove {JoinAttributeNames(toRemove)}, move the {settings} of {JoinAttributeNames(toMove)} to the " +
              $"type '{typeName}' if you own it";
        return DiagnosticInfo.Create(
            JobDiagnostics.JsonAttributeOnDelegatedMember, member.Location,
            member.SourceName, payloadName, found, typeName,
            fix + ", or " + JobDiagnostics.RegisterJobByHand);
    }

    /// <summary>The attribute names in brackets, joined for a sentence: "[A] and [B]", "[A], [B], and [C]".</summary>
    private static string JoinAttributeNames(IReadOnlyList<INamedTypeSymbol> attributes)
    {
        var names = attributes.Select(a =>
        {
            var name = a.Name.EndsWith("Attribute", StringComparison.Ordinal)
                ? a.Name.Substring(0, a.Name.Length - "Attribute".Length)
                : a.Name;
            return $"[{name}]";
        }).ToList();
        return names.Count switch
        {
            1 => names[0],
            2 => $"{names[0]} and {names[1]}",
            _ => $"{string.Join(", ", names.Take(names.Count - 1))}, and {names[names.Count - 1]}",
        };
    }

    /// <summary>
    /// True when the attribute's AttributeUsage lets it go on a class or struct, read from the nearest
    /// AttributeUsage in its base chain.
    /// </summary>
    private static bool IsValidOnType(INamedTypeSymbol attributeClass)
    {
        const int classOrStruct = (int)(AttributeTargets.Class | AttributeTargets.Struct);
        for (var current = attributeClass; current is not null; current = current.BaseType)
        {
            var usage = current.GetAttributes().FirstOrDefault(
                a => a.AttributeClass?.ToDisplayString() == "System.AttributeUsageAttribute");
            if (usage is { ConstructorArguments.Length: 1 } && usage.ConstructorArguments[0].Value is int targets)
            {
                return (targets & classOrStruct) != 0;
            }
        }
        return false;
    }

    private static bool TryClassifyScalar(ITypeSymbol type, out MemberKind kind, out bool isNullableValue)
    {
        isNullableValue = false;
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
        {
            isNullableValue = true;
            type = nullable.TypeArguments[0];
        }

        if (type.TypeKind == TypeKind.Enum)
        {
            kind = MemberKind.Enum;
            return true;
        }

        kind = type.SpecialType switch
        {
            SpecialType.System_String => MemberKind.String,
            SpecialType.System_Boolean => MemberKind.Boolean,
            SpecialType.System_Byte or SpecialType.System_SByte
                or SpecialType.System_Int16 or SpecialType.System_UInt16
                or SpecialType.System_Int32 or SpecialType.System_UInt32
                or SpecialType.System_Int64 or SpecialType.System_UInt64
                or SpecialType.System_Single or SpecialType.System_Double
                or SpecialType.System_Decimal => MemberKind.Number,
            SpecialType.System_DateTime => MemberKind.DateTime,
            _ => type.ToDisplayString() switch
            {
                "System.Guid" => MemberKind.Guid,
                "System.DateTimeOffset" => MemberKind.DateTimeOffset,
                _ => (MemberKind)(-1),
            },
        };
        return kind >= 0;
    }

    /// <summary>
    /// True when code in another assembly can name the type: the type, each type that contains it, and each type
    /// argument or array element are public. A generated record is public only when every type it names is.
    /// </summary>
    private static bool IsPublicOutsideTheAssembly(ITypeSymbol type)
        => type switch
        {
            IArrayTypeSymbol array => IsPublicOutsideTheAssembly(array.ElementType),
            INamedTypeSymbol named => named.DeclaredAccessibility == Accessibility.Public
                && (named.ContainingType is null || IsPublicOutsideTheAssembly(named.ContainingType))
                && named.TypeArguments.All(IsPublicOutsideTheAssembly),
            _ => false,
        };

    private static bool CanBeNull(ITypeSymbol type) => type.IsReferenceType || IsNullableValue(type);

    private static bool IsNullableValue(ITypeSymbol type)
        => type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T;

    /// <summary>True for a reference type with no nullable annotation: it can hold null, but its name has no '?'.</summary>
    private static bool IsUnannotatedReference(ITypeSymbol type)
        => type.IsReferenceType && type.NullableAnnotation != NullableAnnotation.Annotated;

    /// <summary>True for an ImmutableArray&lt;T&gt;, or a Nullable&lt;T&gt; of one.</summary>
    private static bool IsImmutableArray(ITypeSymbol type)
        => (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable
                ? nullable.TypeArguments[0]
                : type)
            .OriginalDefinition.ToDisplayString() == "System.Collections.Immutable.ImmutableArray<T>";

    private static string MissingLiteral(IParameterSymbol parameter)
        => parameter.HasExplicitDefaultValue
            ? ExplicitLiteral(parameter.ExplicitDefaultValue, parameter.Type)
            : DefaultLiteral(parameter.Type);

    private static string DefaultLiteral(ITypeSymbol type) => CanBeNull(type) ? "null" : "default";

    private static string ExplicitLiteral(object? value, ITypeSymbol type)
        => value switch
        {
            null => type.IsValueType && type.OriginalDefinition.SpecialType != SpecialType.System_Nullable_T
                ? "default"
                : "null",
            string s => SymbolDisplay.FormatLiteral(s, quote: true),
            char c => SymbolDisplay.FormatLiteral(c, quote: true),
            bool b => b ? "true" : "false",
            decimal m => m.ToString(CultureInfo.InvariantCulture) + "m",
            float f => float.IsNaN(f) ? "float.NaN"
                : float.IsPositiveInfinity(f) ? "float.PositiveInfinity"
                : float.IsNegativeInfinity(f) ? "float.NegativeInfinity"
                : f.ToString("R", CultureInfo.InvariantCulture) + "f",
            double d => double.IsNaN(d) ? "double.NaN"
                : double.IsPositiveInfinity(d) ? "double.PositiveInfinity"
                : double.IsNegativeInfinity(d) ? "double.NegativeInfinity"
                : d.ToString("R", CultureInfo.InvariantCulture) + "d",
            _ when type.TypeKind == TypeKind.Enum || (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } n && n.TypeArguments[0].TypeKind == TypeKind.Enum)
                => $"({type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)})({Convert.ToString(value, CultureInfo.InvariantCulture)})",
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "default",
        };
}
