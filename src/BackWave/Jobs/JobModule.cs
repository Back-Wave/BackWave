using System.Diagnostics.CodeAnalysis;

namespace BackWave.Jobs;

/// <summary>
/// One handler's dependency-injection mapping as plain types: <c>(typeof(IJobHandler&lt;T&gt;),
/// typeof(THandler))</c>. Deliberately container-free so it can live alongside the registrations; the
/// scoped registration happens when <c>UseJobs</c> wires the module into your service collection.
/// The implementation type is annotated so trimming keeps its public constructors — dependency
/// injection constructs the handler each time a job runs.
/// </summary>
/// <param name="ServiceType">The handler interface to register, e.g. <c>IJobHandler&lt;T&gt;</c>.</param>
/// <param name="ImplementationType">The concrete handler type that fulfills the service.</param>
public readonly record struct JobHandlerMapping(
    Type ServiceType,
    // param + property + field targets so the trim guarantee survives the record's generated
    // constructor and getter under IL-level analysis, not just the Roslyn analyzer.
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
    [property: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
    [field: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
    Type ImplementationType);

/// <summary>
/// One class that declares method-style <c>[Job]</c>s, wrapped so trimming keeps its public
/// constructors: the class is registered with a scoped lifetime and constructed by dependency
/// injection each time one of its jobs runs. Converts implicitly from <see cref="System.Type"/>,
/// so a module literal can list declaring classes as plain <c>typeof</c> expressions.
/// </summary>
/// <param name="Type">The declaring class to register.</param>
public readonly record struct JobContainingType(
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
    [property: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
    [field: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
    Type Type)
{
    /// <summary>Wraps <paramref name="type"/>, carrying the constructor-preservation guarantee with it.</summary>
    /// <param name="type">The declaring class to register.</param>
    /// <returns>The wrapped type.</returns>
    public static implicit operator JobContainingType(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] Type type) => new(type);
}

/// <summary>
/// A DI-free bundle of everything a host needs to wire up its <c>[Job]</c> declarations in one call:
/// the <see cref="Registrations"/> (the Job Manifest), the handler <see cref="Handlers"/> mappings, and
/// the <see cref="ContainingTypes"/> that method-sugar handlers depend on — so none of the lists can
/// drift apart. The source generator emits one of these as <c>BackWaveJobs.Module</c>; the Hosting
/// Shell's <c>UseJobs</c> registers the registry, an <c>AddScoped</c> per mapping, and a
/// <c>TryAddScoped</c> per containing type. Living in <c>BackWave.Jobs</c> (not Hosting) keeps the Core
/// AOT-compatible and forces no DI-container reference on generator-only consumers.
/// </summary>
public sealed class JobModule
{
    /// <summary>One registration per <c>[Job]</c>, ordered by Wire Name.</summary>
    public required IReadOnlyList<JobRegistration> Registrations { get; init; }

    /// <summary>One <c>(IJobHandler&lt;T&gt;, THandler)</c> mapping per <c>[Job]</c>, ordered by Wire Name.</summary>
    public required IReadOnlyList<JobHandlerMapping> Handlers { get; init; }

    /// <summary>
    /// The distinct classes that declare method-sugar <c>[Job]</c>s — the DI entry the generated
    /// <c>…Handler</c> constructor-injects and forwards to. <c>UseJobs</c> registers each scoped so a
    /// method-sugar job needs no hand-written registration. Empty when every job is a class-based
    /// handler or a static-method sugar (neither needs an instance of a declaring class).
    /// </summary>
    public required IReadOnlyList<JobContainingType> ContainingTypes { get; init; }

    /// <summary>
    /// Serialization metadata for workflow seed values, keyed by seed type, so a started workflow reads
    /// and writes its immutable Workflow Input with no caller-passed serializer. The source generator
    /// emits one entry per seed type; empty when the assembly declares no workflow seeds. Defaults to
    /// empty so an older generated module (which set no seeds) stays source-compatible.
    /// </summary>
    public IReadOnlyDictionary<Type, System.Text.Json.Serialization.Metadata.JsonTypeInfo> SeedCodecs { get; init; }
        = new Dictionary<Type, System.Text.Json.Serialization.Metadata.JsonTypeInfo>();

    /// <summary>Builds the job registry over <see cref="Registrations"/> and <see cref="SeedCodecs"/>.</summary>
    /// <returns>A registry indexing every registration by Wire Name and job type, carrying the seed codecs.</returns>
    public JobRegistry CreateRegistry() => new(Registrations, SeedCodecs);
}
