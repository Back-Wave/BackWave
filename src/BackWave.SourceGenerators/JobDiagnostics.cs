using Microsoft.CodeAnalysis;

namespace BackWave.SourceGenerators;

internal static class JobDiagnostics
{
    private const string Category = "BackWave";

    /// <summary>The last fix in a payload diagnostic: register the job without the generator.</summary>
    public const string RegisterJobByHand = "register this job by hand with JobRegistration.Create";

    public static readonly DiagnosticDescriptor EmptyWireName = new(
        "BW0001",
        "Wire Name is missing",
        "[Job] requires a non-empty Wire Name — Wire Names are mandatory and explicit, never derived from CLR names",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor DuplicateWireName = new(
        "BW0002",
        "Duplicate Wire Name",
        "Wire Name '{0}' is declared by both '{1}' and '{2}' — Wire Names must be unique",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor MissingHandler = new(
        "BW0003",
        "No handler for [Job] type",
        "No IJobHandler<{0}> implementation was found in this compilation for [Job] type '{0}'",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>
    /// A payload member that the generated codec cannot serialize: its type, or the shape of the constructor
    /// parameter and property that carry it. The fourth argument is the reason and the fix.
    /// </summary>
    public static readonly DiagnosticDescriptor UnsupportedPayloadMember = new(
        "BW0004",
        "Unsupported payload member type",
        "Member '{0}' of job payload '{1}' (type '{2}') is not supported by the generated codec: {3}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor InvalidJobMethod = new(
        "BW0005",
        "Invalid [Job] method shape",
        "[Job] method '{0}' must be public and return Task, and must not be generic or in a generic type; " +
        "data parameters come first, " +
        "with optional JobContext and CancellationToken parameters",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor DuplicateJobType = new(
        "BW0006",
        "Duplicate generated job type",
        "Two [Job] declarations resolve to the same payload type '{0}' — payload type names and " +
        "method-sugar job names must be unique within a namespace",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor InvalidRetryCeiling = new(
        "BW0008",
        "Invalid [Retry] attempt ceiling",
        $"[Retry] attempt ceiling is {{0}} - the ceiling must be from 1 to {BackWaveGenerator.MaxAttemptCeiling}. " +
        "Remove [Retry] to inherit the Worker Group policy, or give a ceiling in that range.",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor InvalidRetryBackoff = new(
        "BW0009",
        "Invalid [Retry] backoff intervals",
        $"[Retry] backoff intervals are invalid: {{0}}. Declare at least 1 interval and at most " +
        $"{BackWaveGenerator.MaxBackoffIntervals}, and make every interval 0 or more seconds.",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor RetryWithoutJob = new(
        "BW0010",
        "[Retry] with no [Job]",
        "[Retry] on '{0}' has no [Job], so the retry override is ignored - the same silent drop the " +
        "loud-failure design prevents. Add [Job] to this type or method, or remove [Retry].",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor WorkflowTypeNotSerializable = new(
        "BW0007",
        "Workflow type is not listed in any JsonSerializerContext",
        "Workflow type '{0}' ({1}) is not listed in any JsonSerializerContext - add " +
        "[JsonSerializable(typeof({0}))] to a JsonSerializerContext so BackWave can wire its serialization, " +
        "or register it by hand with an explicit JsonTypeInfo",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>
    /// System.Text.Json attributes on a delegated payload member's property. The third argument names the
    /// attributes, and the fifth argument is the fix, which depends on whether each attribute can also go on a type.
    /// </summary>
    public static readonly DiagnosticDescriptor JsonAttributeOnDelegatedMember = new(
        "BW0011",
        "System.Text.Json attribute on a delegated payload member",
        "Member '{0}' of job payload '{1}' has {2} - the generated codec serializes " +
        "this member with the metadata of its type '{3}', not of the property. {4}.",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>
    /// A type that more than one JsonSerializerContext lists. The second argument says what needs the type, and
    /// the fourth argument is the other fix, which depends on whether a job or a Workflow Input seed needs it.
    /// </summary>
    public static readonly DiagnosticDescriptor AmbiguousJsonContext = new(
        "BW0012",
        "Type is listed in more than one JsonSerializerContext",
        "Type '{0}' ({1}) is listed in more than one JsonSerializerContext: {2}. The contexts can serialize " +
        "it differently, so BackWave does not pick one - list the type in only one JsonSerializerContext, or {3}.",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>
    /// A type that a JsonSerializerContext lists, where the generated codec cannot use that listing. The second
    /// argument says what needs the type, and the fourth argument is the reason and the fix.
    /// </summary>
    public static readonly DiagnosticDescriptor UnusableJsonContext = new(
        "BW0013",
        "JsonSerializerContext listing cannot serve the generated codec",
        "Type '{0}' ({1}) is listed by JsonSerializerContext '{2}', which the generated codec cannot use: {3}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>
    /// A generic [Job] payload type, or one nested in a generic type. The argument is the type name.
    /// </summary>
    public static readonly DiagnosticDescriptor GenericJobType = new(
        "BW0014",
        "Generic [Job] type",
        "[Job] type '{0}' must not be generic or nested in a generic type: a queued job names one concrete payload type",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>
    /// A type-level [JsonConverter] on a payload with delegated members. An error, not a warning: STJ generates no
    /// metadata for the member types of a converted type, so the delegated members would fail at run time.
    /// </summary>
    public static readonly DiagnosticDescriptor JsonConverterOnDelegatingPayload = new(
        "BW0016",
        "[JsonConverter] on a [Job] payload with delegated members",
        "Job payload '{0}' has members that the generated codec hands to System.Text.Json, so it must not have " +
        "a type-level [JsonConverter] - the generated codec writes the members itself, so the converter never " +
        "runs. Remove the converter, or " + RegisterJobByHand + ".",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>
    /// Delegated payload members whose metadata no JsonSerializerContext serves. The first argument names the
    /// members, and the third is the type to list. The code fix reads that type from Properties["TypeFqn"].
    /// </summary>
    public static readonly DiagnosticDescriptor UnlistedPayloadType = new(
        "BW0017",
        "Type the job codec needs is not listed in any JsonSerializerContext",
        "No JsonSerializerContext in this assembly lists type '{2}', which the generated codec needs for {0} of " +
        "job payload '{1}'. Add [JsonSerializable(typeof({2}))] to a JsonSerializerContext in this assembly, or " +
        RegisterJobByHand + ".",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>
    /// System.Text.Json attributes on a scalar payload member's property. A warning, not an error: the job still
    /// round-trips, but the attributes do not change the wire shape. The third argument names the attributes, and
    /// the fourth is "attribute" or "attributes".
    /// </summary>
    public static readonly DiagnosticDescriptor JsonAttributeOnScalarMember = new(
        "BW0018",
        "System.Text.Json attribute on a scalar payload member",
        "Member '{0}' of job payload '{1}' has {2} - the generated codec writes this " +
        "member itself and does not read System.Text.Json attributes. Remove the {3}, or " + RegisterJobByHand + ".",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    /// <summary>
    /// A type-level [JsonConverter] on a payload with only scalar members. A warning, not an error: the job still
    /// round-trips, but the converter never runs.
    /// </summary>
    public static readonly DiagnosticDescriptor JsonConverterOnScalarPayload = new(
        "BW0019",
        "[JsonConverter] on a [Job] payload with only scalar members",
        "Job payload '{0}' has a type-level [JsonConverter], which has no effect - the generated codec writes " +
        "the members itself, so the converter never runs. Remove the converter, or register this job by hand " +
        "with JobRegistration.Create.",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);
}
