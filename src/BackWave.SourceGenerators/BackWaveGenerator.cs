using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BackWave.SourceGenerators;

/// <summary>
/// Emits the job registry, payload serialization, and [Job] method sugar. Generated code is
/// exactly what a user would write by hand: explicit Utf8JsonWriter/Reader serialization
/// (tolerant of unknown and missing JSON properties), handler dispatch through DI, and a
/// BackWaveJobs.CreateRegistry() entry point. No reflection, no expression trees — the
/// output is NativeAOT- and trim-clean by construction.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed partial class BackWaveGenerator : IIncrementalGenerator
{
    private const string JobAttributeName = "BackWave.Jobs.JobAttribute";
    private const string RetryAttributeName = "BackWave.Jobs.RetryAttribute";

    /// <summary>Tracked-step names, used by the incrementality test to assert cached reuse.</summary>
    public const string ParseStep = "BackWaveParse";
    public const string ModelsStep = "BackWaveModels";
    public const string HandlersStep = "BackWaveHandlers";
    public const string EmitInputStep = "BackWaveEmitInput";
    public const string JsonContextsStep = "BackWaveJsonContexts";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Parse each [Job] declaration into a value-equal ParseResult. No Compilation in the
        // pipeline — discovery flows entirely through incremental providers (issue 0043).
        var parseResults = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                JobAttributeName,
                predicate: static (_, _) => true,
                transform: static (ctx, _) => Parse(ctx))
            .WithTrackingName(ParseStep);

        var jobModels = parseResults
            .Select(static (result, _) => result.Model)
            .Where(static model => model is not null)
            .Collect()
            .WithTrackingName(ModelsStep);

        // Handler discovery via a syntax/attribute provider, not a walk of the whole
        // Compilation's namespace tree: a handler in an unchanged file stays cached.
        var handlers = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, _) => node is ClassDeclarationSyntax { BaseList: not null },
                transform: static (ctx, _) => ExtractHandlers(ctx))
            .Where(static handlersInClass => handlersInClass.Count > 0)
            .Collect()
            .WithTrackingName(HandlersStep);

        // Diagnostics travel in their own pipeline branch — never inside the cached models.
        var parseDiagnostics = parseResults
            .SelectMany(static (result, _) => result.Diagnostic is null
                ? result.Warnings.AsImmutableArray()
                : result.Warnings.AsImmutableArray().Insert(0, result.Diagnostic));
        context.RegisterSourceOutput(
            parseDiagnostics, static (spc, diagnostic) => spc.ReportDiagnostic(diagnostic.ToDiagnostic()));

        // [Retry] is read only inside the [Job] pipeline above, so a [Retry] on a type or method with no
        // [Job] would be silently ignored - the same silent drop the loud-failure design prevents. This
        // branch visits every [Retry] target and reports BW0010 when the target carries no [Job] (ADR 0051).
        var orphanRetryDiagnostics = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                RetryAttributeName,
                predicate: static (_, _) => true,
                transform: static (ctx, _) => DetectOrphanRetry(ctx))
            .Where(static diagnostic => diagnostic is not null);
        context.RegisterSourceOutput(
            orphanRetryDiagnostics, static (spc, diagnostic) => spc.ReportDiagnostic(diagnostic!.ToDiagnostic()));

        // Workflow Input seed types (BackWave.Pro.IWorkflowInput implementors), found by syntax so an
        // unchanged file stays cached - never a walk of the whole Compilation. Each is a seed whose codec
        // the generator wires (and whose absence from every JsonSerializerContext is a build error).
        var seedTypes = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, _) => node is TypeDeclarationSyntax { BaseList: not null },
                transform: static (ctx, _) => ExtractSeedType(ctx))
            .Where(static seed => seed is not null)
            .Collect();

        // Every JsonSerializerContext and the types it lists via [JsonSerializable], found through the
        // attribute provider. The completeness check resolves each workflow output/seed type to a listing
        // context here, so a missing serializer is caught at compile time rather than at run time.
        var jsonContexts = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                JsonSerializerContextRule.JsonSerializableAttributeName,
                predicate: static (_, _) => true,
                transform: static (ctx, _) => ExtractJsonContext(ctx))
            .WithTrackingName(JsonContextsStep)
            .Where(static jsonContext => jsonContext is not null)
            .Collect();

        var emitInput = jobModels.Combine(handlers).Combine(seedTypes).Combine(jsonContexts)
            .WithTrackingName(EmitInputStep);
        context.RegisterSourceOutput(emitInput, static (spc, input) =>
            Emit(spc, input.Left.Left.Left, input.Left.Left.Right, input.Left.Right, input.Right));
    }

    /// <summary>
    /// A BW0010 diagnostic when a [Retry] target carries no [Job], else null. [Retry] is meaningful only
    /// on a [Job] type or method; without a [Job] the override is silently ignored (ADR 0051).
    /// </summary>
    private static DiagnosticInfo? DetectOrphanRetry(GeneratorAttributeSyntaxContext context)
    {
        var hasJob = context.TargetSymbol.GetAttributes().Any(
            a => a.AttributeClass?.ToDisplayString() == JobAttributeName);
        if (hasJob)
        {
            return null;
        }

        var location = LocationInfo.CreateFrom(context.TargetNode.GetLocation());
        return DiagnosticInfo.Create(JobDiagnostics.RetryWithoutJob, location, context.TargetSymbol.Name);
    }

    /// <summary>
    /// A Workflow Input seed model when this type declaration implements BackWave.Pro.IWorkflowInput,
    /// else null. Records are TypeDeclarationSyntax, so record-based seeds are covered.
    /// </summary>
    private static SeedTypeInfo? ExtractSeedType(GeneratorSyntaxContext context)
    {
        if (context.SemanticModel.GetDeclaredSymbol(context.Node) is not INamedTypeSymbol type)
        {
            return null;
        }

        foreach (var implemented in type.AllInterfaces)
        {
            if (implemented is { Name: "IWorkflowInput", TypeArguments.Length: 0 }
                && implemented.ContainingNamespace.ToDisplayString() == "BackWave.Pro")
            {
                return new SeedTypeInfo(
                    type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    LocationInfo.CreateFrom(context.Node.GetLocation()));
            }
        }
        return null;
    }

    /// <summary>
    /// The JsonSerializerContext-derived class and every type it lists via [JsonSerializable]. All
    /// applications on this declaration are read; a first constructor argument is the listed type's
    /// typeof(...) - its symbol becomes the global::-qualified FQN the completeness check matches on.
    /// A listing the generated codec cannot use is kept apart with its reason, so it never binds a codec.
    /// A [JsonSerializable] class that is not a JsonSerializerContext gives null.
    /// </summary>
    private static JsonContextInfo? ExtractJsonContext(GeneratorAttributeSyntaxContext context)
    {
        if (context.TargetSymbol is not INamedTypeSymbol contextType
            || !JsonSerializerContextRule.IsJsonSerializerContext(contextType))
        {
            return null;
        }

        var compilation = context.SemanticModel.Compilation;
        var listed = ImmutableArray.CreateBuilder<string>();
        var unusable = ImmutableArray.CreateBuilder<UnusableListing>();
        foreach (var attribute in context.Attributes)
        {
            if (attribute.ConstructorArguments.Length > 0
                && attribute.ConstructorArguments[0].Value is ITypeSymbol listedType)
            {
                var typeFqn = listedType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                if (JsonSerializerContextRule.UnusableReason(contextType, compilation, attribute) is { } reason)
                {
                    unusable.Add(new UnusableListing(typeFqn, reason));
                    continue;
                }
                listed.Add(typeFqn);
            }
        }

        return new JsonContextInfo(
            contextType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            new EquatableArray<string>(listed.ToImmutable()),
            new EquatableArray<UnusableListing>(unusable.ToImmutable()),
            JsonSerializerContextRule.SerializationOnlyReason(contextType));
    }

    private static ParseResult Parse(GeneratorAttributeSyntaxContext context)
    {
        var attribute = context.Attributes[0];
        var wireName = attribute.ConstructorArguments.Length > 0
            ? attribute.ConstructorArguments[0].Value as string
            : null;
        var queue = attribute.NamedArguments
            .Where(a => a.Key == "Queue")
            .Select(a => a.Value.Value as string)
            .FirstOrDefault() ?? "default";
        var labels = ExtractLabels(attribute);
        var retryAttribute = context.TargetSymbol.GetAttributes().FirstOrDefault(
            a => a.AttributeClass?.ToDisplayString() == RetryAttributeName);
        var (retryMaxAttempts, retryBackoffSeconds) = ExtractRetry(retryAttribute);
        var location = LocationInfo.CreateFrom(context.TargetNode.GetLocation());

        if (string.IsNullOrWhiteSpace(wireName))
        {
            return new ParseResult(null, DiagnosticInfo.Create(JobDiagnostics.EmptyWireName, location));
        }

        // A [Retry] ceiling outside 1..MaxAttemptCeiling is a mistake, not "no override". Fail loudly here
        // instead of silently dropping it at the emit gate, or letting a huge literal reach FromIntervals
        // and allocate at startup instead of failing as a diagnostic (ADR 0051).
        if (retryAttribute is not null && (retryMaxAttempts < 1 || retryMaxAttempts > MaxAttemptCeiling))
        {
            return new ParseResult(null, DiagnosticInfo.Create(
                JobDiagnostics.InvalidRetryCeiling, location,
                retryMaxAttempts.ToString(CultureInfo.InvariantCulture)));
        }

        // The backoff list has the same compile-time constants as the ceiling, so catch the same mistakes
        // here rather than at the registration-time FromIntervals throw (ADR 0051): empty, over 20, or negative.
        if (retryAttribute is not null && DescribeInvalidBackoff(retryBackoffSeconds) is { } backoffProblem)
        {
            return new ParseResult(null, DiagnosticInfo.Create(
                JobDiagnostics.InvalidRetryBackoff, location, backoffProblem));
        }

        // The generated code names the payload type and calls the handler method with no type arguments.
        switch (context.TargetSymbol)
        {
            case INamedTypeSymbol type when IsOrIsInGenericType(type):
                return new ParseResult(null, DiagnosticInfo.Create(JobDiagnostics.GenericJobType, location, type.Name));
            case IMethodSymbol method when method.IsGenericMethod || IsOrIsInGenericType(method.ContainingType):
                return new ParseResult(null, DiagnosticInfo.Create(JobDiagnostics.InvalidJobMethod, location, method.Name));
        }

        return context.TargetSymbol switch
        {
            INamedTypeSymbol type => ParseRecordJob(
                type, context.SemanticModel.Compilation, wireName!, queue, labels, retryMaxAttempts, retryBackoffSeconds, location),
            IMethodSymbol method => ParseMethodJob(
                method, wireName!, queue, labels, retryMaxAttempts, retryBackoffSeconds, location),
            _ => new ParseResult(null, null),
        };
    }

    // Mirrors Core.RetryDisposition.MaxBackoffIntervals. The generator cannot reference the runtime type,
    // so the bound is duplicated here; FromIntervals enforces the same value at registration time (ADR 0051).
    internal const int MaxBackoffIntervals = 20;

    // Mirrors Core.RetryDisposition.MaxAttemptCeiling. Duplicated for the same reason as MaxBackoffIntervals;
    // FromIntervals enforces the same value at registration time (ADR 0051).
    internal const int MaxAttemptCeiling = 1000;

    /// <summary>
    /// The reason a [Retry] backoff list is invalid, or null when it is valid. Mirrors the bounds that
    /// RetryDisposition.FromIntervals enforces at registration time (ADR 0051): at least one interval, at
    /// most <see cref="MaxBackoffIntervals"/>, none negative.
    /// </summary>
    private static string? DescribeInvalidBackoff(EquatableArray<double> backoffSeconds)
    {
        if (backoffSeconds.Count == 0)
        {
            return "the list is empty";
        }

        if (backoffSeconds.Count > MaxBackoffIntervals)
        {
            return $"the list has {backoffSeconds.Count} intervals";
        }

        foreach (var seconds in backoffSeconds)
        {
            // Validate the TimeSpan, not the raw double, so this gate agrees exactly with FromIntervals.
            // FromIntervals checks TimeSpan.FromSeconds(seconds), which the emitted code runs. A NaN, an
            // infinity, or an out-of-range magnitude fails that conversion (the generated code would throw
            // or not compile), and a sub-tick value that FromSeconds rounds toward zero is judged the same.
            TimeSpan interval;
            try
            {
                interval = TimeSpan.FromSeconds(seconds);
            }
            catch (Exception ex) when (ex is OverflowException or ArgumentException)
            {
                return $"an interval of {seconds.ToString(CultureInfo.InvariantCulture)} seconds is out of range";
            }

            if (interval < TimeSpan.Zero)
            {
                return $"an interval is {seconds.ToString(CultureInfo.InvariantCulture)} seconds";
            }
        }

        return null;
    }

    /// <summary>
    /// The [Retry] override values (ADR 0051): the attempt ceiling plus the backoff intervals in seconds,
    /// in declaration order. Returns (0, empty) when the target carries no [Retry]. [Retry] is a separate
    /// attribute from [Job], so the caller looks it up on the symbol, not on context.Attributes.
    /// </summary>
    private static (int MaxAttempts, EquatableArray<double> BackoffSeconds) ExtractRetry(AttributeData? attribute)
    {
        var empty = new EquatableArray<double>(ImmutableArray<double>.Empty);
        if (attribute is null || attribute.ConstructorArguments.Length == 0)
        {
            return (0, empty);
        }

        var maxAttempts = attribute.ConstructorArguments[0].Value is int ceiling ? ceiling : 0;
        if (attribute.ConstructorArguments.Length < 2
            || attribute.ConstructorArguments[1].Kind != TypedConstantKind.Array
            || attribute.ConstructorArguments[1].IsNull)
        {
            return (maxAttempts, empty);
        }

        var seconds = attribute.ConstructorArguments[1].Values
            .Select(v => Convert.ToDouble(v.Value, CultureInfo.InvariantCulture))
            .ToImmutableArray();
        return (maxAttempts, new EquatableArray<double>(seconds));
    }

    /// <summary>
    /// The [Job] attribute's Labels (default Tag Labels, ADR 0022): bare constant strings in
    /// declaration order, dropping null/empty entries. Only Labels are expressible — a Keyed Tag
    /// would require parsing a separator, which the structural distinction forbids (deferred).
    /// </summary>
    private static EquatableArray<string> ExtractLabels(AttributeData attribute)
    {
        var argument = attribute.NamedArguments
            .Where(a => a.Key == "Labels")
            .Select(a => a.Value)
            .FirstOrDefault();
        if (argument.Kind != TypedConstantKind.Array || argument.IsNull)
        {
            return new EquatableArray<string>(ImmutableArray<string>.Empty);
        }

        var labels = argument.Values
            .Select(v => v.Value as string)
            .Where(s => !string.IsNullOrEmpty(s))
            .Select(s => s!)
            .ToImmutableArray();
        return new EquatableArray<string>(labels);
    }

    private static ParseResult ParseRecordJob(
        INamedTypeSymbol type, Compilation compilation, string wireName, string queue, EquatableArray<string> labels,
        int retryMaxAttempts, EquatableArray<double> retryBackoffSeconds, LocationInfo? location)
    {
        var warnings = ImmutableArray.CreateBuilder<DiagnosticInfo>();
        var members = ParseMembers(type, compilation, warnings, out var failure);
        if (failure is not null)
        {
            return new ParseResult(null, failure, warnings.ToImmutable());
        }

        // STJ generates no metadata for the member types of a type with a type-level converter, so a delegated
        // member would fail at run time. The converter would never run anyway: the generated codec writes the
        // members itself. A scalar-only payload needs no STJ metadata, so there the converter is only a warning.
        if (HasTypeLevelJsonConverter(type))
        {
            if (members.Any(m => m.Kind == MemberKind.Delegated))
            {
                return new ParseResult(null, DiagnosticInfo.Create(
                    JobDiagnostics.JsonConverterOnDelegatingPayload, location, type.Name), warnings.ToImmutable());
            }
            warnings.Add(DiagnosticInfo.Create(JobDiagnostics.JsonConverterOnScalarPayload, location, type.Name));
        }

        return new ParseResult(new JobModel
        {
            WireName = wireName,
            Queue = queue,
            Labels = labels,
            RetryMaxAttempts = retryMaxAttempts,
            RetryBackoffSeconds = retryBackoffSeconds,
            JobTypeFqn = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            JobTypeName = type.Name,
            Namespace = type.ContainingNamespace.IsGlobalNamespace
                ? ""
                : type.ContainingNamespace.ToDisplayString(),
            Members = members,
            OutputTypeFqn = OutputTypeFqn(type),
            Location = location,
        }, null, warnings.ToImmutable());
    }

    /// <summary>
    /// The Job Output type a step declares it produces - the TOut of a BackWave.Pro.IWorkflowStep&lt;TOut&gt;
    /// the [Job] record implements - global::-qualified, or null when the type is not an output-producing
    /// workflow step. The generic marker lives in BackWave.Pro, so a Core-only consumer's [Job]s never
    /// match and carry no output codec.
    /// </summary>
    private static string? OutputTypeFqn(INamedTypeSymbol type)
    {
        foreach (var implemented in type.AllInterfaces)
        {
            if (implemented is { Name: "IWorkflowStep", TypeArguments.Length: 1 }
                && implemented.ContainingNamespace.ToDisplayString() == "BackWave.Pro")
            {
                return implemented.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            }
        }
        return null;
    }

    /// <summary>Maps a payload type's single richest public constructor plus settable extras.</summary>
    private static EquatableArray<PayloadMember> ParseMembers(
        INamedTypeSymbol type, Compilation compilation, ImmutableArray<DiagnosticInfo>.Builder warnings,
        out DiagnosticInfo? failure)
    {
        failure = null;

        var ctor = type.InstanceConstructors
            .Where(c => c.DeclaredAccessibility == Accessibility.Public)
            .Where(c => !(c.Parameters.Length == 1
                && SymbolEqualityComparer.Default.Equals(c.Parameters[0].Type, type))) // record copy ctor
            .OrderByDescending(c => c.Parameters.Length)
            .FirstOrDefault();

        var properties = type.GetMembers()
            .OfType<IPropertySymbol>()
            .Where(p => p is { IsStatic: false, IsIndexer: false, DeclaredAccessibility: Accessibility.Public })
            .Where(p => p.GetMethod is not null)
            .ToList();

        var members = ImmutableArray.CreateBuilder<PayloadMember>();
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (ctor is not null)
        {
            foreach (var parameter in ctor.Parameters)
            {
                var property = properties.FirstOrDefault(
                    p => string.Equals(p.Name, parameter.Name, StringComparison.OrdinalIgnoreCase));
                if (property is null)
                {
                    failure = DiagnosticInfo.Create(
                        JobDiagnostics.UnsupportedPayloadMember, LocationInfo.CreateFrom(parameter.Locations[0]),
                        parameter.Name, type.Name, DisplayFqn(parameter.Type),
                        "no public property has the name of this constructor parameter, so the codec cannot read it back - " +
                        "add the property, or " + JobDiagnostics.RegisterJobByHand);
                    return default;
                }
                var asPropertyType = !SymbolEqualityComparer.Default.Equals(parameter.Type, property.Type);
                var member = asPropertyType
                    ? ClassifyAsPropertyType(property, parameter, compilation, type.Name, out failure)
                    : ClassifyMember(property.Name, property.Name, parameter.Type, property.Locations[0], type.Name, out failure);
                failure ??= DetectJsonAttribute(member, property, type.Name, warnings);
                if (failure is not null)
                {
                    return default;
                }
                claimed.Add(property.Name);
                members.Add(member! with
                {
                    CtorPosition = parameter.Ordinal,
                    MissingLiteral = MissingLiteral(parameter),
                    ParameterTypeFqn = asPropertyType ? parameter.Type.ToDisplayString(AnnotatedFormat) : null,
                    ParameterIsUnannotatedReference = asPropertyType && IsUnannotatedReference(parameter.Type),
                });
            }
        }

        foreach (var property in properties.Where(p => !claimed.Contains(p.Name)))
        {
            if (property.SetMethod is null)
            {
                continue; // computed property: not part of the wire shape
            }
            var member = ClassifyMember(property.Name, property.Name, property.Type, property.Locations[0], type.Name, out failure);
            failure ??= DetectJsonAttribute(member, property, type.Name, warnings);
            if (failure is not null)
            {
                return default;
            }
            members.Add(member!);
        }

        return members.ToImmutable();
    }

    private static bool IsOrIsInGenericType(INamedTypeSymbol? type)
    {
        for (; type is not null; type = type.ContainingType)
        {
            if (type.IsGenericType)
            {
                return true;
            }
        }
        return false;
    }

    private static ParseResult ParseMethodJob(
        IMethodSymbol method, string wireName, string queue, EquatableArray<string> labels,
        int retryMaxAttempts, EquatableArray<double> retryBackoffSeconds, LocationInfo? location)
    {
        var returnsTask = method.ReturnType is INamedTypeSymbol { Name: "Task", ContainingNamespace.Name: "Tasks" };
        if (method.DeclaredAccessibility != Accessibility.Public || !returnsTask)
        {
            return new ParseResult(null, DiagnosticInfo.Create(JobDiagnostics.InvalidJobMethod, location, method.Name));
        }

        // The generated payload record's name, computed up front so an unsupported parameter can name
        // the payload type the consumer will see rather than the Wire Name.
        var recordName = method.Name.EndsWith("Async", StringComparison.Ordinal)
            ? method.Name.Substring(0, method.Name.Length - "Async".Length)
            : method.Name;

        var members = ImmutableArray.CreateBuilder<PayloadMember>();
        var callArguments = ImmutableArray.CreateBuilder<string>();
        foreach (var parameter in method.Parameters)
        {
            var typeName = parameter.Type.ToDisplayString();
            if (typeName == "BackWave.Jobs.JobContext")
            {
                callArguments.Add("context");
                continue;
            }
            if (typeName == "System.Threading.CancellationToken")
            {
                callArguments.Add("cancellationToken");
                continue;
            }
            var memberName = char.ToUpperInvariant(parameter.Name[0]) + parameter.Name.Substring(1);
            var member = ClassifyMember(
                memberName, parameter.Name, parameter.Type, parameter.Locations[0], recordName, out var failure);
            if (failure is not null)
            {
                return new ParseResult(null, failure);
            }
            callArguments.Add(memberName);
            members.Add(member! with
            {
                CtorPosition = members.Count,
                MissingLiteral = MissingLiteral(parameter),
            });
        }

        var containingType = method.ContainingType;
        var ns = containingType.ContainingNamespace.IsGlobalNamespace
            ? ""
            : containingType.ContainingNamespace.ToDisplayString();

        return new ParseResult(new JobModel
        {
            WireName = wireName,
            Queue = queue,
            Labels = labels,
            RetryMaxAttempts = retryMaxAttempts,
            RetryBackoffSeconds = retryBackoffSeconds,
            JobTypeFqn = ns.Length == 0 ? $"global::{recordName}" : $"global::{ns}.{recordName}",
            JobTypeName = recordName,
            Namespace = ns,
            HandlerTypeFqn = ns.Length == 0 ? $"global::{recordName}Handler" : $"global::{ns}.{recordName}Handler",
            Members = members.ToImmutable(),
            Sugar = new MethodSugar
            {
                ContainingTypeFqn = containingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                MethodName = method.Name,
                IsStatic = method.IsStatic,
                Accessibility = IsPublicOutsideTheAssembly(containingType)
                    && method.Parameters.All(p => IsPublicOutsideTheAssembly(p.Type)) ? "public" : "internal",
                CallArguments = callArguments.ToImmutable(),
            },
            Location = location,
        }, null);
    }

    private static void Emit(
        SourceProductionContext context,
        ImmutableArray<JobModel?> declarations,
        ImmutableArray<EquatableArray<HandlerInfo>> handlerArrays,
        ImmutableArray<SeedTypeInfo?> seedTypes,
        ImmutableArray<JsonContextInfo?> jsonContexts)
    {
        var jobs = declarations.Where(j => j is not null).Select(j => j!).ToList();

        // Duplicate Wire Names are compile errors (the registry would throw at runtime).
        foreach (var group in jobs.GroupBy(j => j.WireName, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            var names = group.Select(j => j.JobTypeName).ToList();
            foreach (var job in group.Skip(1))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    JobDiagnostics.DuplicateWireName, job.Location?.ToLocation(), group.Key, names[0], job.JobTypeName));
            }
        }
        jobs = jobs
            .GroupBy(j => j.WireName, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(j => j.WireName, StringComparer.Ordinal)
            .ToList();

        // Resolve handlers for record jobs from the syntax-discovered set; method-sugar
        // handlers are generated. First implementation per job type wins, in source order.
        var handlersByJobType = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var handlersInClass in handlerArrays)
        {
            foreach (var handler in handlersInClass)
            {
                if (!handlersByJobType.ContainsKey(handler.JobTypeFqn))
                {
                    handlersByJobType[handler.JobTypeFqn] = handler.HandlerFqn;
                }
            }
        }
        var registered = new List<JobModel>();
        foreach (var job in jobs)
        {
            if (job.Sugar is not null)
            {
                registered.Add(job);
                continue;
            }
            if (!handlersByJobType.TryGetValue(job.JobTypeFqn, out var handlerFqn))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    JobDiagnostics.MissingHandler, job.Location?.ToLocation(), job.JobTypeName));
                continue;
            }
            registered.Add(job with { HandlerTypeFqn = handlerFqn });
        }

        // Two [Job]s that resolve to the same fully qualified payload type — e.g. method-sugar
        // Send() jobs in two classes of one namespace, or a record job and a method-sugar job
        // that collide — would emit duplicate generated types (CS0101) and a duplicate source
        // hint (AddSource throws). Flag the collision with a clean diagnostic and emit one each.
        var emitted = new List<JobModel>();
        var seenTypeFqns = new HashSet<string>(StringComparer.Ordinal);
        foreach (var job in registered)
        {
            if (seenTypeFqns.Add(job.JobTypeFqn))
            {
                emitted.Add(job);
                continue;
            }
            context.ReportDiagnostic(Diagnostic.Create(
                JobDiagnostics.DuplicateJobType, job.Location?.ToLocation(), job.JobTypeFqn));
        }

        var jsonContextIndex = new JsonContextIndex(jsonContexts);

        emitted = emitted
            .Select(job => ResolveDelegatedMembers(context, job, jsonContextIndex))
            .Where(job => job is not null)
            .Select(job => job!)
            .ToList();

        // Resolve every workflow output type and Workflow Input seed to the JsonSerializerContext that
        // lists it, so the emitted codecs read from the consumer's own STJ metadata (any shape, AOT-safe).
        // A type listed by more than one context is BW0012: the contexts can apply different options, and a
        // silent pick would change the wire format when a context is added. These completeness diagnostics
        // fire UNCONDITIONALLY, before the emitted.Count gate, so a missing or ambiguous serializer is a build
        // error even when no registry is emitted. A type that only unusable listings name gives BW0013, which
        // says why, instead of "not listed".
        emitted = emitted
            .Select(job =>
            {
                if (job.OutputTypeFqn is not { } outputTypeFqn)
                {
                    return job;
                }
                var resolution = jsonContextIndex.Resolve(outputTypeFqn);
                var usage = $"the Job Output of step '{job.WireName}'";
                switch (resolution.Kind)
                {
                    case JsonContextResolutionKind.Bound:
                        return job with { OutputContextFqn = resolution.ContextFqn };
                    case JsonContextResolutionKind.Ambiguous:
                        ReportAmbiguousContext(
                            context, job.Location, outputTypeFqn, usage, resolution.Contexts, JobDiagnostics.RegisterJobByHand);
                        return job;
                    case JsonContextResolutionKind.Unusable:
                        ReportUnusableListing(context, job.Location, outputTypeFqn, usage, resolution);
                        return job;
                }
                // Stash the offending type FQN in Properties so the BW0007 code fix recovers it
                // precisely: for an output the type to list is NOT the type at the diagnostic
                // location (that is the step), so the fix cannot read it back from the syntax.
                context.ReportDiagnostic(Diagnostic.Create(
                    JobDiagnostics.WorkflowTypeNotSerializable, job.Location?.ToLocation(),
                    ImmutableDictionary<string, string?>.Empty.Add("TypeFqn", outputTypeFqn),
                    outputTypeFqn, usage));
                return job;
            })
            .ToList();

        // Resolve seed codecs (deduped - a partial seed record yields one syntax node per part). An
        // unresolved seed is the same build error as an unresolved output, pointed at the seed declaration.
        var seedCodecs = new List<(string TypeFqn, string ContextFqn)>();
        var seenSeeds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var seed in seedTypes
                     .Where(s => s is not null)
                     .Select(s => s!)
                     .OrderBy(s => s.TypeFqn, StringComparer.Ordinal))
        {
            if (!seenSeeds.Add(seed.TypeFqn))
            {
                continue;
            }
            var resolution = jsonContextIndex.Resolve(seed.TypeFqn);
            switch (resolution.Kind)
            {
                case JsonContextResolutionKind.Bound:
                    seedCodecs.Add((seed.TypeFqn, resolution.ContextFqn!));
                    continue;
                case JsonContextResolutionKind.Ambiguous:
                    ReportAmbiguousContext(
                        context, seed.Location, seed.TypeFqn, "a Workflow Input seed", resolution.Contexts,
                        "remove IWorkflowInput from the type and pass its JsonTypeInfo explicitly to start the " +
                        "workflow and to read the seed");
                    continue;
                case JsonContextResolutionKind.Unusable:
                    ReportUnusableListing(context, seed.Location, seed.TypeFqn, "a Workflow Input seed", resolution);
                    continue;
            }
            context.ReportDiagnostic(Diagnostic.Create(
                JobDiagnostics.WorkflowTypeNotSerializable, seed.Location?.ToLocation(),
                ImmutableDictionary<string, string?>.Empty.Add("TypeFqn", seed.TypeFqn),
                seed.TypeFqn, "a Workflow Input seed"));
        }

        foreach (var job in emitted)
        {
            context.AddSource(HintName(job), JobEmitter.EmitJob(job));
        }
        if (emitted.Count > 0)
        {
            context.AddSource("BackWave.Jobs.g.cs", JobEmitter.EmitRegistry(emitted, seedCodecs));
        }
    }

    /// <summary>
    /// Binds each delegated member to the JsonSerializerContext that serves its metadata. A class payload binds
    /// every member to the context that lists the payload, because STJ generates metadata for every type that a
    /// listed type reaches. Listings of the member types in other contexts do not count, so that a new listing
    /// cannot change the wire format of queued jobs. The STJ generator cannot see a generated method-sugar record,
    /// so a sugar job binds each member to the context that lists the member type. A context that a member binds
    /// to gives BW0013 when it has any serialization-only listing or default, because STJ gives each type the mode
    /// of the first listing that reaches it: once at the payload for a class payload, and once at the first member
    /// that binds to it for method sugar. A type without a listing gives BW0017, a type with only unusable
    /// listings gives BW0013, and a type that more than one context lists gives BW0012. Then the job is not
    /// emitted, and this method returns null.
    /// </summary>
    private static JobModel? ResolveDelegatedMembers(
        SourceProductionContext context,
        JobModel job,
        JsonContextIndex jsonContextIndex)
    {
        var delegated = job.Members.Where(m => m.Kind == MemberKind.Delegated).ToList();
        if (delegated.Count == 0)
        {
            return job;
        }

        if (job.Sugar is null)
        {
            var payload = jsonContextIndex.ResolveDelegated(job.JobTypeFqn);
            switch (payload.Kind)
            {
                case JsonContextResolutionKind.Bound:
                    return job with
                    {
                        Members = job.Members
                            .Select(m => m.Delegation is { } delegation
                                ? m with { Delegation = delegation with { ContextFqn = payload.ContextFqn } }
                                : m)
                            .ToImmutableArray(),
                    };
                case JsonContextResolutionKind.Ambiguous:
                    ReportAmbiguousContext(
                        context, job.Location, job.JobTypeFqn, $"the payload of job '{job.WireName}'", payload.Contexts,
                        JobDiagnostics.RegisterJobByHand);
                    return null;
                case JsonContextResolutionKind.Unusable:
                    ReportUnusableListing(context, job.Location, job.JobTypeFqn, $"the payload of job '{job.WireName}'", payload);
                    return null;
                default:
                    ReportUnlisted(context, job.Location, job, delegated, job.JobTypeFqn);
                    return null;
            }
        }

        var resolved = true;
        var members = ImmutableArray.CreateBuilder<PayloadMember>(job.Members.Count);
        var unlisted = new List<PayloadMember>();
        // One report per cause, as for BW0017: BW0012 names the type, and BW0013 names the context.
        var ambiguousTypes = new HashSet<string>(StringComparer.Ordinal);
        var rejectedContexts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in job.Members)
        {
            if (member.Delegation is not { } delegation)
            {
                members.Add(member);
                continue;
            }
            var memberResolution = jsonContextIndex.ResolveDelegated(delegation.TypeFqn);
            switch (memberResolution.Kind)
            {
                case JsonContextResolutionKind.Bound:
                    members.Add(member with { Delegation = delegation with { ContextFqn = memberResolution.ContextFqn } });
                    continue;
                case JsonContextResolutionKind.Ambiguous:
                    if (ambiguousTypes.Add(delegation.TypeFqn))
                    {
                        ReportAmbiguousContext(
                            context, member.Location, delegation.TypeFqn,
                            $"member '{member.SourceName}' of job payload '{job.JobTypeName}'", memberResolution.Contexts,
                            SugarAmbiguousFix);
                    }
                    break;
                case JsonContextResolutionKind.Unusable:
                    if (rejectedContexts.Add(memberResolution.ContextFqn!))
                    {
                        ReportUnusableListing(
                            context, member.Location, delegation.TypeFqn,
                            $"the type of member '{member.SourceName}' of job payload '{job.JobTypeName}'", memberResolution);
                    }
                    break;
                default:
                    unlisted.Add(member);
                    break;
            }
            resolved = false;
        }

        foreach (var sameType in unlisted.GroupBy(m => m.Delegation!.TypeFqn, StringComparer.Ordinal))
        {
            ReportUnlisted(context, sameType.First().Location, job, sameType.ToList(), sameType.Key);
        }
        return resolved ? job with { Members = members.ToImmutable() } : null;
    }

    /// <summary>
    /// BW0017 for the type that the unlisted members need listed: the class payload, or for method sugar the
    /// member type. The code fix reads the type from Properties["TypeFqn"].
    /// </summary>
    private static void ReportUnlisted(
        SourceProductionContext context, LocationInfo? location, JobModel job, IReadOnlyList<PayloadMember> members,
        string typeToList)
        => context.ReportDiagnostic(Diagnostic.Create(
            JobDiagnostics.UnlistedPayloadType, location?.ToLocation(),
            ImmutableDictionary<string, string?>.Empty.Add("TypeFqn", typeToList),
            (members.Count == 1 ? "member " : "members ") + string.Join(", ", members.Select(m => $"'{m.SourceName}'")),
            job.JobTypeName, DisplayFqn(typeToList)));

    /// <summary>
    /// The other BW0012 fix for a method-sugar member. A class payload binds its members to the context that lists
    /// the payload, so other listings of the member type do not count.
    /// </summary>
    private const string SugarAmbiguousFix =
        "put [Job] on a payload record and list that record in one JsonSerializerContext, or " +
        JobDiagnostics.RegisterJobByHand;

    /// <summary>BW0012 for a type that more than one JsonSerializerContext lists.</summary>
    private static void ReportAmbiguousContext(
        SourceProductionContext context, LocationInfo? location, string typeFqn, string usage, IReadOnlyList<string> contexts,
        string otherFix)
        => context.ReportDiagnostic(Diagnostic.Create(
            JobDiagnostics.AmbiguousJsonContext, location?.ToLocation(),
            DisplayFqn(typeFqn), usage, string.Join(", ", contexts.Select(DisplayFqn)), otherFix));

    /// <summary>BW0013 for a type that only listings the generated codec cannot use name.</summary>
    private static void ReportUnusableListing(
        SourceProductionContext context, LocationInfo? location, string typeFqn, string usage, JsonContextResolution unusable)
        => context.ReportDiagnostic(Diagnostic.Create(
            JobDiagnostics.UnusableJsonContext, location?.ToLocation(),
            DisplayFqn(typeFqn), usage, DisplayFqn(unusable.ContextFqn!), unusable.Reason));

    /// <summary>
    /// A unique, file-name-safe source hint per job, keyed on the fully qualified payload type.
    /// The bare type name is not enough: two same-named types in different namespaces (e.g.
    /// Acme.Foo.Order and Acme.Bar.Order) are legitimate and must not collide on one hint. A
    /// short stable hash of the FQN guards against sanitized-name aliasing (Foo.Bar vs Foo_Bar).
    /// </summary>
    private static string HintName(JobModel job)
    {
        const string globalPrefix = "global::";
        var fqn = job.JobTypeFqn.StartsWith(globalPrefix, StringComparison.Ordinal)
            ? job.JobTypeFqn.Substring(globalPrefix.Length)
            : job.JobTypeFqn;
        var sanitized = new System.Text.StringBuilder(fqn.Length);
        foreach (var c in fqn)
        {
            sanitized.Append(char.IsLetterOrDigit(c) ? c : '_');
        }
        return $"BackWave.{sanitized}.{StableHash(fqn):x8}.g.cs";
    }

    /// <summary>A deterministic FNV-1a hash — Object.GetHashCode is not stable across runs.</summary>
    private static uint StableHash(string value)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (var c in value)
            {
                hash ^= c;
                hash *= 16777619;
            }
            return hash;
        }
    }

    /// <summary>
    /// The IJobHandler&lt;T&gt; mappings for one class declaration, found through the syntax
    /// provider (not a walk of the whole Compilation): job-type FQN → this handler's FQN.
    /// </summary>
    private static EquatableArray<HandlerInfo> ExtractHandlers(GeneratorSyntaxContext context)
    {
        if (context.SemanticModel.GetDeclaredSymbol(context.Node)
            is not INamedTypeSymbol { TypeKind: TypeKind.Class, IsAbstract: false } type)
        {
            return default;
        }

        var handlers = ImmutableArray.CreateBuilder<HandlerInfo>();
        foreach (var implemented in type.AllInterfaces)
        {
            if (implemented is { Name: "IJobHandler", TypeArguments.Length: 1 }
                && implemented.ContainingNamespace.ToDisplayString() == "BackWave.Jobs")
            {
                handlers.Add(new HandlerInfo(
                    implemented.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)));
            }
        }
        return handlers.ToImmutable();
    }
}
