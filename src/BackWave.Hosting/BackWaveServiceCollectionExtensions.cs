using BackWave.Jobs;
using BackWave.Monitor;
using BackWave.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BackWave.Hosting;

/// <summary>
/// Registration entry point for BackWave. Adds BackWave to an application's dependency-injection
/// container so its background jobs run inside the host.
/// </summary>
public static class BackWaveServiceCollectionExtensions
{
    /// <summary>
    /// Registers BackWave in the application's service container: the job store, the job registry,
    /// the client used to enqueue work, the monitoring API, the fail-stop health state, and the
    /// background worker pumps for each worker group (one per group by default, or more when a group
    /// sets a higher pump count). Call this once during startup.
    /// </summary>
    /// <param name="services">The service collection to register BackWave into.</param>
    /// <param name="configure">
    /// Callback that configures BackWave on the supplied builder. At minimum it must call
    /// <see cref="BackWaveBuilder.UseStore(IJobStore)"/> (or its factory overload), supply the jobs
    /// via <see cref="BackWaveBuilder.UseJobs(JobModule)"/> or
    /// <see cref="BackWaveBuilder.UseRegistry(JobRegistry)"/>, and add at least one worker group with
    /// <see cref="BackWaveBuilder.AddWorkerGroup(WorkerGroupOptions)"/>.
    /// </param>
    /// <returns>The same service collection, so registration calls can be chained.</returns>
    /// <exception cref="InvalidOperationException">
    /// No store was configured (<see cref="BackWaveBuilder.UseStore(IJobStore)"/> was never called),
    /// no job registry was configured (<see cref="BackWaveBuilder.UseJobs(JobModule)"/> or
    /// <see cref="BackWaveBuilder.UseRegistry(JobRegistry)"/> was never called), or the same worker
    /// group name was added twice.
    /// </exception>
    /// <example>
    /// <code>
    /// builder.Services.AddBackWave(bw => bw
    ///     .UseStore(new PostgresJobStore(connectionString))
    ///     .UseJobs(BackWaveJobs.Module)
    ///     .AddWorkerGroup(new WorkerGroupOptions
    ///     {
    ///         Name = "default",
    ///         Policy = new DispatchPolicy.Strict("emails", "reports"),
    ///     }));
    /// </code>
    /// </example>
    public static IServiceCollection AddBackWave(
        this IServiceCollection services, Action<BackWaveBuilder> configure)
    {
        var builder = new BackWaveBuilder();
        configure(builder);
        builder.Apply(services);
        return services;
    }
}

/// <summary>
/// Fluent builder for a BackWave registration. An instance is handed to the callback passed to
/// <see cref="BackWaveServiceCollectionExtensions.AddBackWave"/>; configure the store, jobs, history
/// policy, worker groups, and observers on it. Every method returns the same builder so calls can be
/// chained.
/// </summary>
public sealed class BackWaveBuilder
{
    private Func<IServiceProvider, IJobStore>? _store;
    private Func<IServiceProvider, JobRegistry>? _registry;
    private JobHistoryPolicy _historyPolicy = JobHistoryPolicy.TransitionsAndFailureDetail;
    private readonly List<WorkerGroupOptions> _workerGroups = [];
    private readonly List<JobHandlerMapping> _handlerMappings = [];
    private readonly List<JobContainingType> _containingTypes = [];
    private ObserverBuilder? _observers;
    private readonly List<Action<IServiceCollection>> _serviceConfigurations = [];

    /// <summary>
    /// Sets the job store BackWave reads from and writes to — the durable backing for all jobs,
    /// schedules, and history. Required: registration fails without it. Each storage adapter
    /// (for example Postgres, SQL Server, or SQLite) provides a store implementation.
    /// </summary>
    /// <param name="store">The store instance to use for the lifetime of the host.</param>
    /// <returns>The same builder, so calls can be chained.</returns>
    /// <example>
    /// <code>
    /// bw.UseStore(new PostgresJobStore(connectionString));
    /// </code>
    /// </example>
    public BackWaveBuilder UseStore(IJobStore store) => UseStore(_ => store);

    /// <summary>
    /// Sets the job store via a factory resolved from the application's service provider — use this
    /// overload when the store depends on other registered services. Required: registration fails
    /// without a store.
    /// </summary>
    /// <param name="factory">
    /// Builds the store from the service provider. Invoked once, when the store is first resolved.
    /// </param>
    /// <returns>The same builder, so calls can be chained.</returns>
    /// <example>
    /// <code>
    /// bw.UseStore(sp => new PostgresJobStore(sp.GetRequiredService&lt;IConfiguration&gt;()["Db"]));
    /// </code>
    /// </example>
    public BackWaveBuilder UseStore(Func<IServiceProvider, IJobStore> factory)
    {
        _store = factory;
        return this;
    }

    /// <summary>
    /// Sets a hand-built job registry — the catalog mapping each job type to its handler. Most
    /// applications use <see cref="UseJobs(JobModule)"/> with the generated module instead; reach for
    /// this only when building the registry by hand. Required: registration fails without a registry
    /// (whether from this method or from <see cref="UseJobs(JobModule)"/>).
    /// </summary>
    /// <param name="registry">The registry mapping job types to handlers.</param>
    /// <returns>The same builder, so calls can be chained.</returns>
    public BackWaveBuilder UseRegistry(JobRegistry registry) => UseRegistry(_ => registry);

    /// <summary>
    /// Sets the job registry via a factory resolved from the application's service provider — use
    /// this overload when the registry depends on other registered services. Required: registration
    /// fails without a registry (whether from this method or from <see cref="UseJobs(JobModule)"/>).
    /// </summary>
    /// <param name="factory">
    /// Builds the registry from the service provider. Invoked once, when the registry is first resolved.
    /// </param>
    /// <returns>The same builder, so calls can be chained.</returns>
    public BackWaveBuilder UseRegistry(Func<IServiceProvider, JobRegistry> factory)
    {
        _registry = factory;
        return this;
    }

    // Lifetime rationale (ADR 0021): scoped == resolved once per Attempt, so transient is a wash and
    // singleton re-creates the captive-dependency foot-gun. Declaring classes use TryAddScoped (issue
    // 0106) so a host pre-registration wins; handlers use AddScoped. The pump honors whatever lifetime
    // ends up registered.
    /// <summary>
    /// Registers a generated job module (for example <c>BackWaveJobs.Module</c>) in one call: the job
    /// registry, one handler registration per <c>[Job]</c>, and one registration per class that
    /// declares method-style <c>[Job]</c>s. This is the usual way to wire up jobs and is preferred over
    /// building a registry by hand. Everything registers with a per-job (scoped) lifetime, so a handler
    /// is resolved once each time a job runs and its scoped dependencies (for example a database
    /// context) are created and disposed per run. Classes that declare method-style jobs register only
    /// if you have not already registered them yourself, so a deliberate custom registration wins.
    /// </summary>
    /// <param name="module">
    /// The generated module exposing the discovered jobs, their handlers, and the registry factory.
    /// </param>
    /// <returns>The same builder, so calls can be chained.</returns>
    /// <example>
    /// <code>
    /// bw.UseJobs(BackWaveJobs.Module);
    /// </code>
    /// </example>
    public BackWaveBuilder UseJobs(JobModule module)
    {
        UseRegistry(module.CreateRegistry());
        _handlerMappings.AddRange(module.Handlers);
        _containingTypes.AddRange(module.ContainingTypes);
        return this;
    }

    // The Monitor now reads the effective history policy straight from the store (the single source
    // of truth), so this setting no longer feeds it and the two can no longer drift. It still supplies
    // the policy to the composition-time Observer-deliverability guard, which must run before the store
    // can be resolved (§5.12, ADR 0011).
    /// <summary>
    /// Declares the job history policy used to validate observer registration at startup: registering
    /// an observer while history recording is off fails fast, because there would then be no
    /// transitions to deliver. Pass the same value the store is configured to record with. The
    /// monitoring API no longer needs this — it reads the effective policy directly from the store, so
    /// the dashboard's "history disabled" state can never disagree with the store. Defaults to
    /// recording full history, so most applications never call this.
    /// </summary>
    /// <param name="policy">The history policy used to validate observer registration at startup.</param>
    /// <returns>The same builder, so calls can be chained.</returns>
    public BackWaveBuilder UseHistoryPolicy(JobHistoryPolicy policy)
    {
        _historyPolicy = policy;
        return this;
    }

    /// <summary>
    /// Adds a worker group. Each group runs one or more background pumps — its
    /// <see cref="WorkerGroupOptions.Pumps"/> count, one by default — polling the queues it serves and
    /// executing the jobs it claims. Add more than one group to give different queues independent
    /// concurrency, polling, and retry settings; raise a group's pump count to fan out a single queue
    /// across more independent claim loops for throughput. At least one group is required for jobs to run.
    /// </summary>
    /// <param name="options">
    /// The group's configuration: its unique name, which queues it serves, its pump count, and its
    /// concurrency, polling, lease, retry, and retention settings.
    /// </param>
    /// <returns>The same builder, so calls can be chained.</returns>
    /// <exception cref="InvalidOperationException">
    /// A worker group with the same <see cref="WorkerGroupOptions.Name"/> was already added, or the
    /// group's <see cref="WorkerGroupOptions.Pumps"/> count is less than one.
    /// </exception>
    /// <example>
    /// <code>
    /// bw.AddWorkerGroup(new WorkerGroupOptions
    /// {
    ///     Name = "emails",
    ///     Policy = new DispatchPolicy.Strict("emails"),
    ///     PoolSize = 16,
    /// });
    /// </code>
    /// </example>
    public BackWaveBuilder AddWorkerGroup(WorkerGroupOptions options)
    {
        if (_workerGroups.Any(g => g.Name == options.Name))
        {
            throw new InvalidOperationException($"Worker Group '{options.Name}' is configured twice.");
        }
        if (options.Pumps < 1)
        {
            throw new InvalidOperationException(
                $"Worker Group '{options.Name}' must run at least one Pump (Pumps = {options.Pumps}).");
        }
        _workerGroups.Add(options);
        return this;
    }

    // ADR 0020: the pump knobs (ConfigurePump) and the Observer list (Add) live in one cohesive block
    // so they cannot drift apart; one hosted ObserverDispatchService per process drives them all.
    /// <summary>
    /// Registers transition observers — callbacks invoked when jobs change state — in one block. The
    /// callback configures both the observer list and, optionally, the dispatch pump's settings, so
    /// they stay together. A single background dispatcher per process drives every registered observer.
    /// Calling this more than once replaces the previous block.
    /// </summary>
    /// <param name="configure">
    /// Callback that registers observers (and optional pump settings) on the supplied builder.
    /// </param>
    /// <returns>The same builder, so calls can be chained.</returns>
    /// <example>
    /// <code>
    /// bw.AddObservers(obs => obs
    ///     .Add&lt;AuditObserver&gt;("audit", ObserverSubscription.AllTransitions));
    /// </code>
    /// </example>
    public BackWaveBuilder AddObservers(Action<ObserverBuilder> configure)
    {
        var observers = new ObserverBuilder();
        configure(observers);
        _observers = observers;
        return this;
    }

    /// <summary>
    /// Registers additional services into the same container this BackWave registration configures,
    /// applied when the registration is built. This is the seam a separately-installed BackWave
    /// extension package uses to contribute its own services from inside the <c>AddBackWave</c> block —
    /// so a host configures everything in one place rather than adding a second, easy-to-forget call
    /// alongside it. The dashboard's live-metrics collector is registered this way. The callbacks run in
    /// the order added, after BackWave's own core services, so a contributed service can depend on them.
    /// </summary>
    /// <param name="configure">
    /// A callback that registers services into the application's service collection. Invoked once, when
    /// the BackWave registration is applied.
    /// </param>
    /// <returns>The same builder, so calls can be chained.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is <see langword="null"/>.</exception>
    /// <example>
    /// <code>
    /// bw.ConfigureServices(services =&gt; services.AddSingleton&lt;IMyDashboardWidget, MyWidget&gt;());
    /// </code>
    /// </example>
    public BackWaveBuilder ConfigureServices(Action<IServiceCollection> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _serviceConfigurations.Add(configure);
        return this;
    }

    internal void Apply(IServiceCollection services)
    {
        if (_store is null)
        {
            throw new InvalidOperationException("AddBackWave requires UseStore(...): BackWave has no default storage.");
        }
        if (_registry is null)
        {
            throw new InvalidOperationException(
                "AddBackWave requires UseRegistry(...): pass BackWaveJobs.CreateRegistry() (generated) or a hand-built JobRegistry.");
        }

        services.AddSingleton(_store);
        // A host may contribute extra JobRegistrations through DI - for example a workflow gate registered
        // with AddWorkflowGate - which are folded into the module (or hand-built) registry here. With none
        // contributed the base registry is returned unchanged, so a consumer that never contributes one
        // gets byte-for-byte the prior single-registration.
        services.AddSingleton<JobRegistry>(sp =>
        {
            var baseRegistry = _registry!(sp);
            var contributed = sp.GetServices<JobRegistration>().ToList();
            return contributed.Count == 0 ? baseRegistry : baseRegistry.WithAdditional(contributed);
        });
        // Handlers from UseJobs(JobModule) register scoped (ADR 0021): the pump opens a DI scope per
        // Attempt and resolves the handler from it, so its scoped dependencies resolve and dispose
        // per Attempt. A host may still register a handler itself for a different lifetime.
        foreach (var mapping in _handlerMappings)
        {
            services.AddScoped(mapping.ServiceType, mapping.ImplementationType);
        }
        // The classes that declare method-sugar [Job]s — the DI entry the generated handler injects —
        // register scoped too (ADR 0021 amendment, issue 0106): a declaring class is a per-Attempt unit
        // of work and may hold a scoped DbContext. TryAdd, not Add: it is the user's own type, so a host
        // that pre-registers it (the rare deliberate singleton-with-state case) wins.
        foreach (var containingType in _containingTypes)
        {
            services.TryAddScoped(containingType.Type);
        }
        services.AddSingleton<BackWaveClient>(sp =>
            // Pass the container's TimeProvider (if one is registered — e.g. the testing
            // harness's Virtual Time) so a host-registered clock actually governs the client.
            new BackWaveClient(
                sp.GetRequiredService<IJobStore>(),
                sp.GetRequiredService<JobRegistry>(),
                sp.GetService<TimeProvider>(),
                // A host always registers logging; pass its factory so enqueue events flow to the logs
                // pillar. Absent one, the client's ctor defaults to a no-op NullLogger.
                sp.GetService<Microsoft.Extensions.Logging.ILoggerFactory>()));
        // The Monitor reads the effective history policy from the store itself (the single source of
        // truth), so it can never drift from what the store actually records.
        services.AddSingleton<BackWaveMonitor>(sp =>
            new BackWaveMonitor(
                sp.GetRequiredService<IJobStore>(), sp.GetRequiredService<JobRegistry>()));
        services.AddSingleton<Operations.BackWaveOperator>(sp =>
            // Same clock contract as the client: a host-registered TimeProvider (e.g. Virtual
            // Time) governs the now an Operator Action records.
            new Operations.BackWaveOperator(
                sp.GetRequiredService<IJobStore>(),
                sp.GetService<TimeProvider>()));
        services.AddSingleton<BackWaveHealth>();
        services.AddSingleton<BackWaveHealthCheck>();
        services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(sp =>
            new BackWaveMetricsService(sp.GetRequiredService<IJobStore>()));

        // A group runs its Pumps count of independent pump loops (default 1). Each WorkerGroupService is
        // one self-contained Pump — its own Driver, PoolSize pool, and unique worker identity (the Guid
        // in WorkerGroupService._workerId) — so N of them claim disjoint rows under SKIP LOCKED with no
        // added coordination (ADR 0037). Default 1 is byte-for-byte the prior single-pump registration.
        foreach (var group in _workerGroups)
        {
            for (var pump = 0; pump < group.Pumps; pump++)
            {
                services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(sp => new WorkerGroupService(
                    group,
                    sp.GetRequiredService<IJobStore>(),
                    sp.GetRequiredService<JobRegistry>(),
                    sp.GetRequiredService<IServiceScopeFactory>(),
                    sp.GetRequiredService<BackWaveHealth>(),
                    sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<WorkerGroupService>>(),
                    // Same clock contract as the client and operator: a host-registered TimeProvider
                    // (e.g. two offset clocks modelling cross-node skew) governs the pump's stamps;
                    // absent one, the ctor defaults to TimeProvider.System and behavior is unchanged.
                    sp.GetService<TimeProvider>()));
            }
        }

        // One pump per process over every registered Observer (ADR 0020), plus the canonical
        // Observer list the dashboard reads. The composition-time EnsureDeliverableUnder guard runs
        // here, against the declared Job History Policy.
        _observers?.Apply(services, _historyPolicy);

        // Registrations contributed by extension packages from inside the AddBackWave block (via
        // ConfigureServices) — e.g. the dashboard's live-metrics collector. Applied last so they can
        // depend on the core services registered above.
        foreach (var configure in _serviceConfigurations)
        {
            configure(services);
        }
    }
}
