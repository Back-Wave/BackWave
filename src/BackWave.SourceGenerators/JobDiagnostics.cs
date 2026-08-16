using Microsoft.CodeAnalysis;

namespace BackWave.SourceGenerators;

internal static class JobDiagnostics
{
    private const string Category = "BackWave";

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

    public static readonly DiagnosticDescriptor UnsupportedPayloadMember = new(
        "BW0004",
        "Unsupported payload member type",
        "Member '{0}' of job payload '{1}' has type '{2}', which generated serialization does not support — " +
        "register this job by hand with JobRegistration.Create and your own JsonTypeInfo",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor InvalidJobMethod = new(
        "BW0005",
        "Invalid [Job] method shape",
        "[Job] method '{0}' must be public and return Task; data parameters come first, " +
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
}
