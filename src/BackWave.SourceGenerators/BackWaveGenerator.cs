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
public sealed class BackWaveGenerator : IIncrementalGenerator
{
    private const string JobAttributeName = "BackWave.Jobs.JobAttribute";
    private const string RetryAttributeName = "BackWave.Jobs.RetryAttribute";
    private const string JsonSerializableAttributeName = "System.Text.Json.Serialization.JsonSerializableAttribute";

    /// <summary>Tracked-step names, used by the incrementality test to assert cached reuse.</summary>
    public const string ParseStep = "BackWaveParse";
    public const string ModelsStep = "BackWaveModels";
    public const string HandlersStep = "BackWaveHandlers";
    public const string EmitInputStep = "BackWaveEmitInput";

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
            .Select(static (result, _) => result.Diagnostic)
            .Where(static diagnostic => diagnostic is not null);
        context.RegisterSourceOutput(
            parseDiagnostics, static (spc, diagnostic) => spc.ReportDiagnostic(diagnostic!.ToDiagnostic()));

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
                JsonSerializableAttributeName,
                predicate: static (_, _) => true,
                transform: static (ctx, _) => ExtractJsonContext(ctx))
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
    /// </summary>
    private static JsonContextInfo? ExtractJsonContext(GeneratorAttributeSyntaxContext context)
    {
        if (context.TargetSymbol is not INamedTypeSymbol contextType)
        {
            return null;
        }

        var listed = ImmutableArray.CreateBuilder<string>();
        foreach (var attribute in context.Attributes)
        {
            if (attribute.ConstructorArguments.Length > 0
                && attribute.ConstructorArguments[0].Value is ITypeSymbol listedType)
            {
                listed.Add(listedType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
            }
        }

        return new JsonContextInfo(
            contextType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            new EquatableArray<string>(listed.ToImmutable()));
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

        return context.TargetSymbol switch
        {
            INamedTypeSymbol type => ParseRecordJob(
                type, wireName!, queue, labels, retryMaxAttempts, retryBackoffSeconds, location),
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
        INamedTypeSymbol type, string wireName, string queue, EquatableArray<string> labels,
        int retryMaxAttempts, EquatableArray<double> retryBackoffSeconds, LocationInfo? location)
    {
        var members = ParseMembers(type, location, out var failure);
        if (failure is not null)
        {
            return new ParseResult(null, failure);
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
        }, null);
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
        INamedTypeSymbol type, LocationInfo? location, out DiagnosticInfo? failure)
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
                if (property is null || !TryClassify(parameter.Type, out var kind, out var isNullable))
                {
                    failure = DiagnosticInfo.Create(
                        JobDiagnostics.UnsupportedPayloadMember, location,
                        parameter.Name, type.Name, parameter.Type.ToDisplayString());
                    return default;
                }
                claimed.Add(property.Name);
                members.Add(new PayloadMember
                {
                    Name = property.Name,
                    TypeFqn = parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    Kind = kind,
                    IsNullableValue = isNullable,
                    CtorPosition = parameter.Ordinal,
                    MissingLiteral = MissingLiteral(parameter, kind, isNullable),
                });
            }
        }

        foreach (var property in properties.Where(p => !claimed.Contains(p.Name)))
        {
            if (property.SetMethod is null)
            {
                continue; // computed property: not part of the wire shape
            }
            if (!TryClassify(property.Type, out var kind, out var isNullable))
            {
                failure = DiagnosticInfo.Create(
                    JobDiagnostics.UnsupportedPayloadMember, location,
                    property.Name, type.Name, property.Type.ToDisplayString());
                return default;
            }
            members.Add(new PayloadMember
            {
                Name = property.Name,
                TypeFqn = property.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                Kind = kind,
                IsNullableValue = isNullable,
                MissingLiteral = DefaultLiteral(kind, isNullable, property.Type),
            });
        }

        return members.ToImmutable();
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
            if (!TryClassify(parameter.Type, out var kind, out var isNullable))
            {
                return new ParseResult(null, DiagnosticInfo.Create(
                    JobDiagnostics.UnsupportedPayloadMember, location,
                    parameter.Name, recordName, parameter.Type.ToDisplayString()));
            }
            var memberName = char.ToUpperInvariant(parameter.Name[0]) + parameter.Name.Substring(1);
            callArguments.Add(memberName);
            members.Add(new PayloadMember
            {
                Name = memberName,
                TypeFqn = parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                Kind = kind,
                IsNullableValue = isNullable,
                CtorPosition = members.Count,
                MissingLiteral = MissingLiteral(parameter, kind, isNullable),
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
                Accessibility = containingType.DeclaredAccessibility == Accessibility.Public ? "public" : "internal",
                CallArguments = callArguments.ToImmutable(),
            },
            Location = location,
        }, null);
    }

    private static bool TryClassify(ITypeSymbol type, out MemberKind kind, out bool isNullableValue)
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
        return kind >= 0 && !(kind == MemberKind.String && isNullableValue);
    }

    private static string MissingLiteral(IParameterSymbol parameter, MemberKind kind, bool isNullable)
        => parameter.HasExplicitDefaultValue
            ? ExplicitLiteral(parameter.ExplicitDefaultValue, parameter.Type)
            : DefaultLiteral(kind, isNullable, parameter.Type);

    private static string DefaultLiteral(MemberKind kind, bool isNullable, ITypeSymbol type)
        => isNullable || kind == MemberKind.String ? "null" : "default";

    private static string ExplicitLiteral(object? value, ITypeSymbol type)
        => value switch
        {
            null => type.IsValueType && type.OriginalDefinition.SpecialType != SpecialType.System_Nullable_T
                ? "default"
                : "null",
            string s => SymbolDisplay.FormatLiteral(s, quote: true),
            bool b => b ? "true" : "false",
            decimal m => m.ToString(CultureInfo.InvariantCulture) + "m",
            float f => f.ToString("R", CultureInfo.InvariantCulture) + "f",
            double d => d.ToString("R", CultureInfo.InvariantCulture) + "d",
            _ when type.TypeKind == TypeKind.Enum || (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } n && n.TypeArguments[0].TypeKind == TypeKind.Enum)
                => $"({type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}){Convert.ToString(value, CultureInfo.InvariantCulture)}",
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "default",
        };

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

        // Resolve every workflow output type and Workflow Input seed to the JsonSerializerContext that
        // lists it, so the emitted codecs read from the consumer's own STJ metadata (any shape, AOT-safe).
        // A type listed by more than one context binds to the first by ordinal context FQN - a stable,
        // arbitrary pick, no diagnostic. These completeness diagnostics fire UNCONDITIONALLY, before the
        // emitted.Count gate, so a missing serializer is a build error even when no registry is emitted.
        var contextByType = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var jsonContext in jsonContexts
                     .Where(c => c is not null)
                     .Select(c => c!)
                     .OrderBy(c => c.ContextFqn, StringComparer.Ordinal))
        {
            foreach (var typeFqn in jsonContext.ListedTypeFqns)
            {
                if (!contextByType.ContainsKey(typeFqn))
                {
                    contextByType[typeFqn] = jsonContext.ContextFqn;
                }
            }
        }

        emitted = emitted
            .Select(job =>
            {
                if (job.OutputTypeFqn is not { } outputTypeFqn)
                {
                    return job;
                }
                if (contextByType.TryGetValue(outputTypeFqn, out var contextFqn))
                {
                    return job with { OutputContextFqn = contextFqn };
                }
                // Stash the offending type FQN in Properties so the BW0007 code fix recovers it
                // precisely: for an output the type to list is NOT the type at the diagnostic
                // location (that is the step), so the fix cannot read it back from the syntax.
                context.ReportDiagnostic(Diagnostic.Create(
                    JobDiagnostics.WorkflowTypeNotSerializable, job.Location?.ToLocation(),
                    ImmutableDictionary<string, string?>.Empty.Add("TypeFqn", outputTypeFqn),
                    outputTypeFqn, $"the Job Output of step '{job.WireName}'"));
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
            if (contextByType.TryGetValue(seed.TypeFqn, out var contextFqn))
            {
                seedCodecs.Add((seed.TypeFqn, contextFqn));
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
