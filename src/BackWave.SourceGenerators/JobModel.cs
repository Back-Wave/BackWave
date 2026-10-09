using System;

namespace BackWave.SourceGenerators;

/// <summary>One serializable member of a job payload (a property, or a method-sugar data parameter).</summary>
internal sealed record PayloadMember : IEquatable<PayloadMember>
{
    /// <summary>Property name on the payload record — also the JSON property name.</summary>
    public required string Name { get; init; }

    /// <summary>
    /// The member name as the user wrote it, which diagnostics show: the property name, or the method-sugar
    /// parameter name (Name is the generated record property, so "recipients" becomes "Recipients").
    /// </summary>
    public required string SourceName { get; init; }

    /// <summary>
    /// Fully qualified (global::) type for emission. For a <see cref="MemberKind.Delegated"/> member it keeps
    /// the nullable reference annotations, because it declares the generated method-sugar record parameter.
    /// </summary>
    public required string TypeFqn { get; init; }

    public required MemberKind Kind { get; init; }

    /// <summary>Nullable&lt;T&gt; value type (string nullability is handled unconditionally).</summary>
    public bool IsNullableValue { get; init; }

    /// <summary>Zero-based constructor position, or -1 for an init/set property.</summary>
    public int CtorPosition { get; init; } = -1;

    /// <summary>The literal used when the JSON omits this member (tolerant decode).</summary>
    public required string MissingLiteral { get; init; }

    /// <summary>
    /// For a constructor member whose parameter type differs from its property type: the parameter type, with
    /// its nullable reference annotation. The reader local takes this type, so a missing member reads as the
    /// parameter default. Null when the two types are the same.
    /// </summary>
    public string? ParameterTypeFqn { get; init; }

    /// <summary>
    /// With <see cref="ParameterTypeFqn"/>: true for a reference parameter type with no nullable annotation. It
    /// can hold null, but its name has no '?'.
    /// </summary>
    public bool ParameterIsUnannotatedReference { get; init; }

    /// <summary>
    /// The facts the codec needs to hand the member to STJ. Set exactly when Kind is
    /// <see cref="MemberKind.Delegated"/>.
    /// </summary>
    public Delegation? Delegation { get; init; }

    /// <summary>Where a diagnostic about this member points: the constructor parameter, property, or method parameter.</summary>
    public LocationInfo? Location { get; init; }
}

/// <summary>The facts about a <see cref="MemberKind.Delegated"/> member type, read from its symbol.</summary>
internal sealed record Delegation : IEquatable<Delegation>
{
    /// <summary>
    /// The type with no nullable reference annotation, which is the form a typeof(...) and a [JsonSerializable]
    /// listing use.
    /// </summary>
    public required string TypeFqn { get; init; }

    /// <summary>
    /// The JsonTypeInfo&lt;T&gt; type argument. It keeps the nested nullable reference annotations but not the
    /// top-level one.
    /// </summary>
    public required string InfoTypeFqn { get; init; }

    public required bool IsReferenceType { get; init; }

    /// <summary>A reference type with no nullable annotation: it can hold null, but its name has no '?'.</summary>
    public required bool IsUnannotatedReference { get; init; }

    /// <summary>A Nullable&lt;T&gt; value type.</summary>
    public required bool IsNullableValue { get; init; }

    /// <summary>An ImmutableArray&lt;T&gt;, or a Nullable&lt;T&gt; of one.</summary>
    public required bool IsImmutableArray { get; init; }

    /// <summary>
    /// The JsonSerializerContext (global::-qualified) that serves the type metadata. Null until resolved at emit
    /// time.
    /// </summary>
    public string? ContextFqn { get; init; }

    /// <summary>True for a reference type or a Nullable&lt;T&gt;.</summary>
    public bool CanBeNull => IsReferenceType || IsNullableValue;
}

internal enum MemberKind
{
    String,
    Boolean,
    Number,
    Guid,
    DateTime,
    DateTimeOffset,
    Enum,

    /// <summary>Outside the scalar set: the codec hands this member to STJ through the consumer's JsonSerializerContext.</summary>
    Delegated,
}

/// <summary>Everything the emitter needs for one [Job] declaration. Value-equal for incremental caching.</summary>
internal sealed record JobModel : IEquatable<JobModel>
{
    public required string WireName { get; init; }
    public required string Queue { get; init; }

    /// <summary>Default Tag Labels declared on [Job] (ADR 0022, additive). Empty when none; declaration order preserved.</summary>
    public required EquatableArray<string> Labels { get; init; }

    /// <summary>Attempt ceiling declared on [Retry] (ADR 0051), or 0 when the type carries no [Retry].</summary>
    public int RetryMaxAttempts { get; init; }

    /// <summary>Backoff intervals in seconds declared on [Retry]; empty when none. Declaration order preserved.</summary>
    public required EquatableArray<double> RetryBackoffSeconds { get; init; }

    /// <summary>Payload type, global::-qualified. For method sugar this type is generated.</summary>
    public required string JobTypeFqn { get; init; }

    /// <summary>Payload type name without namespace (used for hint names and generated members).</summary>
    public required string JobTypeName { get; init; }

    /// <summary>Namespace of the payload type ("" for global).</summary>
    public required string Namespace { get; init; }

    /// <summary>Handler type, global::-qualified. Null until resolved (or generated).</summary>
    public string? HandlerTypeFqn { get; init; }

    /// <summary>
    /// The Job Output type this step produces (the <c>TOut</c> of <c>IWorkflowStep&lt;TOut&gt;</c>),
    /// global::-qualified. Null when the [Job] type is not a workflow step or produces no output. Set at
    /// parse time.
    /// </summary>
    public string? OutputTypeFqn { get; init; }

    /// <summary>
    /// The JsonSerializerContext (global::-qualified) whose [JsonSerializable] entry serves this step's
    /// <see cref="OutputTypeFqn"/> - the context the emitted output codec reads from. Null until resolved
    /// at emit time (and stays null when the output type is listed in no context, which is a build error).
    /// </summary>
    public string? OutputContextFqn { get; init; }

    public required EquatableArray<PayloadMember> Members { get; init; }

    /// <summary>Set when [Job] sat on a method: the record + handler are generated too.</summary>
    public MethodSugar? Sugar { get; init; }

    /// <summary>Where diagnostics about this declaration point — value-equal coordinates, not a Location.</summary>
    public required LocationInfo? Location { get; init; }
}

/// <summary>The [Job]-on-a-method extras: who to call and how.</summary>
internal sealed record MethodSugar : IEquatable<MethodSugar>
{
    public required string ContainingTypeFqn { get; init; }
    public required string MethodName { get; init; }
    public required bool IsStatic { get; init; }

    /// <summary>Accessibility keyword for the generated record/handler ("public" or "internal").</summary>
    public required string Accessibility { get; init; }

    /// <summary>Argument list for the call, in declared parameter order: member names, "context", or "cancellationToken".</summary>
    public required EquatableArray<string> CallArguments { get; init; }
}
