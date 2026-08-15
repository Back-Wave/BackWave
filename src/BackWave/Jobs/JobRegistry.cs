using System.Text.Json.Serialization.Metadata;

namespace BackWave.Jobs;

/// <summary>
/// The set of all registered job types, keyed by Wire Name. Wire Names are mandatory,
/// explicit, and never derived from CLR type names.
/// </summary>
public sealed class JobRegistry
{
    private readonly Dictionary<string, JobRegistration> _byWireName;
    private readonly Dictionary<Type, JobRegistration> _byJobType;
    private readonly IReadOnlyDictionary<Type, JsonTypeInfo> _seedCodecs;

    /// <summary>
    /// Builds the registry from a set of job registrations, indexing them by Wire Name and CLR job
    /// type. Validates that every Wire Name is non-empty and that no Wire Name or job type repeats.
    /// </summary>
    /// <param name="registrations">
    /// The registrations to index — typically the source generator's emitted registrations, one per
    /// <c>[Job]</c>.
    /// </param>
    /// <param name="seedCodecs">
    /// Optional serialization metadata for workflow seed values, keyed by seed type, so a started
    /// workflow reads and writes its immutable seed with no caller-passed serializer. Typically the
    /// source generator's emitted map, one entry per seed type; null or empty when none are declared.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// A registration has an empty Wire Name, or two registrations share a Wire Name, or two share a
    /// job type.
    /// </exception>
    public JobRegistry(
        IEnumerable<JobRegistration> registrations,
        IReadOnlyDictionary<Type, JsonTypeInfo>? seedCodecs = null)
    {
        _byWireName = new Dictionary<string, JobRegistration>(StringComparer.Ordinal);
        _byJobType = [];
        _seedCodecs = seedCodecs ?? new Dictionary<Type, JsonTypeInfo>();

        foreach (var registration in registrations)
        {
            if (string.IsNullOrWhiteSpace(registration.WireName))
            {
                throw new InvalidOperationException(
                    $"Job type '{registration.JobType.Name}' has an empty Wire Name. Wire Names are mandatory and explicit.");
            }

            if (!_byWireName.TryAdd(registration.WireName, registration))
            {
                throw new InvalidOperationException(
                    $"Duplicate Wire Name '{registration.WireName}'. Wire Names must be unique.");
            }

            if (!_byJobType.TryAdd(registration.JobType, registration))
            {
                throw new InvalidOperationException(
                    $"Job type '{registration.JobType.Name}' is registered more than once.");
            }
        }
    }

    /// <summary>Every registration, ordered by Wire Name — the Job Manifest's source of truth.</summary>
    public IReadOnlyList<JobRegistration> Registrations
        => [.. _byWireName.Values.OrderBy(r => r.WireName, StringComparer.Ordinal)];

    // Returns a new registry with extra registrations folded in, preserving the same seed codecs and
    // reusing this ctor's duplicate-wire-name and duplicate-type validation. Hosting calls this to merge
    // registrations a host contributed through DI (for example a workflow gate added with AddWorkflowGate)
    // into a module-built registry. Preserving _seedCodecs is mandatory: it has no public getter, so a
    // rebuild from Registrations alone would drop it and a seed-aware gate could no longer read its input.
    internal JobRegistry WithAdditional(IEnumerable<JobRegistration> extra)
        => new(Registrations.Concat(extra), _seedCodecs);

    internal bool TryGetByWireName(string wireName, out JobRegistration registration)
        => _byWireName.TryGetValue(wireName, out registration!);

    /// <summary>
    /// Routes a claimed job to its registration and decoded payload, or to the Unroutable
    /// reason that sends it to Quarantined. The single owner of the quarantine taxonomy —
    /// every Shell routes through here, so test pumps and production nodes can never
    /// disagree on what quarantines or why.
    /// </summary>
    internal RouteResult Route(Storage.JobRecord job)
    {
        if (!_byWireName.TryGetValue(job.WireName, out var registration))
        {
            return new RouteResult.Unroutable($"no handler registered for wire name '{job.WireName}'");
        }

        try
        {
            return new RouteResult.Routed(registration, registration.Deserialize(job.Payload));
        }
        catch (Exception exception)
        {
            // The decode boundary: a payload that no longer parses becomes data here, so
            // deploy drift surfaces as a Quarantined job, never a retry storm.
            return new RouteResult.Unroutable(
                $"payload for wire name '{job.WireName}' no longer decodes: {exception.Message}");
        }
    }

    internal JobRegistration GetByJobType(Type jobType)
        => _byJobType.TryGetValue(jobType, out var registration)
            ? registration
            : throw new InvalidOperationException(
                $"Job type '{jobType.Name}' has no registration. Every job type must declare a Wire Name.");

    // Non-throwing twin of GetByJobType: the Pro workflow accessors resolve a step type to its Wire Name
    // and output codec off a JobContext-wired registry, and want a clean null for an unregistered type.
    internal JobRegistration? FindByJobType(Type jobType)
        => _byJobType.TryGetValue(jobType, out var registration) ? registration : null;

    // The seed codec for a Workflow Input type, or null when the type was never declared as a seed. The
    // Pro ctx.Input<TInput>() accessor resolves the metadata here off a JobContext-wired registry so the
    // caller passes no serializer; the enqueue-side StartWorkflow/Workflow(seed) overloads use it too.
    internal JsonTypeInfo? FindSeedCodec(Type seedType)
        => _seedCodecs.TryGetValue(seedType, out var typeInfo) ? typeInfo : null;
}
