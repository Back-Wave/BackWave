using System.Diagnostics.CodeAnalysis;
using BackWave.Observers;
using BackWave.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BackWave.Hosting;

// ADR 0020: this is the one cohesive AddObservers block — pump knobs and the observer list live
// together so they cannot drift apart. Apply() builds the Core ObserverDispatchOptions, registers the
// one-per-process ObserverDispatchService and the canonical registration list the dashboard reads, and
// runs EnsureDeliverableUnder so a misconfiguration fails at container composition, not at first tick.
/// <summary>
/// Configures the set of transition observers and the dispatch pump for a BackWave registration. An
/// instance is handed to the callback passed to <see cref="BackWaveBuilder.AddObservers"/>; register
/// observers with <see cref="Add{TObserver}"/> and, optionally, tune the pump with
/// <see cref="ConfigurePump"/>. A single background dispatcher per process drives every registered
/// observer; delivery is durable and at-least-once.
/// </summary>
public sealed class ObserverBuilder
{
    private readonly List<ObserverBinding> _bindings = [];
    private Action<ObserverPumpOptions>? _configurePump;

    /// <summary>
    /// Tunes the observer dispatch pump — batch size, lease duration, retry policy, poll interval, and
    /// the per-delivery timeout. Optional; sensible defaults apply when not called.
    /// </summary>
    /// <param name="configure">Callback that sets the pump options on the supplied instance.</param>
    /// <returns>The same builder, so calls can be chained.</returns>
    public ObserverBuilder ConfigurePump(Action<ObserverPumpOptions> configure)
    {
        _configurePump = configure;
        return this;
    }

    // The durable delivery cursor is keyed by id — keep it stable across deployments or deliveries
    // restart from the beginning.
    /// <summary>
    /// Registers one observer: the type <typeparamref name="TObserver"/> to invoke, a stable
    /// <paramref name="id"/> that names it, and the <paramref name="subscription"/> describing which
    /// job transitions it receives. The observer is resolved fresh from a dependency-injection scope
    /// per delivery, so it may take scoped dependencies (for example a database context).
    /// </summary>
    /// <typeparam name="TObserver">
    /// The observer type to invoke on each matching transition. Registered with a per-delivery (scoped)
    /// lifetime.
    /// </typeparam>
    /// <param name="id">
    /// A stable identifier for this observer. The durable delivery cursor is keyed by it, so it must be
    /// unique within the block and stable across restarts.
    /// </param>
    /// <param name="subscription">The set of transitions this observer receives.</param>
    /// <returns>The same builder, so calls can be chained.</returns>
    /// <exception cref="InvalidOperationException">
    /// An observer with the same <paramref name="id"/> was already registered in this block.
    /// </exception>
    public ObserverBuilder Add<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TObserver>(
        string id, ObserverSubscription subscription)
        where TObserver : class, ITransitionObserver
    {
        if (_bindings.Any(b => b.Registration.Id == id))
        {
            throw new InvalidOperationException($"Transition Observer '{id}' is configured twice.");
        }
        _bindings.Add(new ObserverBinding(new ObserverRegistration(id, subscription), typeof(TObserver)));
        return this;
    }

    internal void Apply(IServiceCollection services, JobHistoryPolicy historyPolicy)
    {
        if (_bindings.Count == 0)
        {
            // No Observers declared: register nothing — no pump polls, and the dashboard surface is
            // simply empty (the [] fallback). Zero cost when unused.
            return;
        }

        var pumpOptions = new ObserverPumpOptions();
        _configurePump?.Invoke(pumpOptions);

        var registrations = _bindings.Select(b => b.Registration).ToList();
        foreach (var registration in registrations)
        {
            // Composition-time guard (ADR 0020): a misconfiguration (Observer registered while Job
            // History Policy is Off) fails here, not at first tick.
            registration.EnsureDeliverableUnder(historyPolicy);
        }

        // Each Observer is resolved per delivery from a DI scope (ADR 0020) — scoped so it may take
        // a scoped dependency (a DbContext for its dedup write) the idempotent-subscriber pattern wants.
        foreach (var binding in _bindings)
        {
            services.AddScoped(binding.ObserverType);
        }

        // The canonical Observer list: one entry per Add, read by the pump and the dashboard alike,
        // so the registration set has exactly one home.
        services.AddSingleton<IReadOnlyList<ObserverRegistration>>(registrations);

        var bindings = _bindings.ToList();
        services.AddSingleton<IHostedService>(sp => new ObserverDispatchService(
            pumpOptions,
            bindings,
            sp.GetRequiredService<IJobStore>(),
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<ILogger<ObserverDispatchService>>(),
            sp.GetService<TimeProvider>()));
    }
}

// Pairs an ObserverRegistration (what the sans-IO Core delivers for) with the CLR type the Shell
// resolves from a per-delivery DI scope to get the actual callback (ADR 0020). ObserverType is
// annotated (param + property + field, so the guarantee survives the record's generated members)
// because DI constructs the observer per delivery — trimming must keep its public constructors.
internal sealed record ObserverBinding(
    ObserverRegistration Registration,
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
    [property: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
    [field: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
    Type ObserverType);
