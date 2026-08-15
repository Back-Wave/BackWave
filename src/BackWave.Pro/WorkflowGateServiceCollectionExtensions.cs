using System.Text.Json.Serialization.Metadata;
using BackWave.Jobs;
using Microsoft.Extensions.DependencyInjection;

namespace BackWave.Pro;

/// <summary>
/// One-call registration for the conditional gate a workflow's <c>.If</c> branch lowers to. A gate is an
/// ordinary job - a step type plus its handler - so it must be registered like any other job before a
/// workflow that uses it can run. <c>AddWorkflowGate</c> collapses that to a single call: it registers the
/// gate's handler and contributes the gate's job registration, which BackWave folds into the running job
/// set at startup. Register one gate per distinct conditional a workflow uses, after <c>AddBackWave</c>.
/// </summary>
public static class WorkflowGateServiceCollectionExtensions
{
    /// <summary>
    /// Registers a conditional workflow gate - its handler and its job registration - in one call, so a
    /// workflow whose <c>.If</c> branch uses this gate can route it at run time. The gate step carries no
    /// output of its own and needs no tags, so only its serialization metadata and, optionally, a queue
    /// name are required. The handler is registered scoped: BackWave opens a fresh scope per attempt and
    /// resolves the handler from it, so the handler's scoped dependencies resolve and dispose per attempt,
    /// matching how a job module's handlers are registered.
    /// </summary>
    /// <typeparam name="TGate">The predicate type that decides which arm of the branch runs.</typeparam>
    /// <typeparam name="TStep">The ancestor step whose output the predicate reads.</typeparam>
    /// <typeparam name="TOut">The output value type <typeparamref name="TStep"/> declares it produces.</typeparam>
    /// <param name="services">The service collection to register the gate into.</param>
    /// <param name="wireName">
    /// The Wire Name the gate is registered under - its stable, explicit identity in the job set. It must
    /// be unique across every registered job; a value that collides with another job's Wire Name fails the
    /// job set's validation at startup, when the contributed registrations are folded in.
    /// </param>
    /// <param name="gateTypeInfo">
    /// The serialization metadata for the gate step, read from the application's JSON serializer context
    /// after the gate type is declared serializable on it.
    /// </param>
    /// <param name="queue">
    /// The Queue the gate runs on. Defaults to <c>"default"</c>, matching a worker group that declares no
    /// explicit queue.
    /// </param>
    /// <returns>The same service collection, so registration calls can be chained.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> or <paramref name="gateTypeInfo"/> is <see langword="null"/>.</exception>
    /// <example>
    /// Declare the gate step serializable on the application's JSON context, then register it after
    /// <c>AddBackWave</c>:
    /// <code>
    /// [JsonSerializable(typeof(WorkflowGate&lt;LargeOrder, PriceOrder, OrderTotal&gt;))]
    /// public partial class AppJsonContext : JsonSerializerContext;
    ///
    /// builder.Services
    ///     .AddBackWave(bw => bw
    ///         .UseStore(new PostgresJobStore(connectionString))
    ///         .UseJobs(AppJobs.Module)
    ///         .AddWorkerGroup(new WorkerGroupOptions { Name = "default" }))
    ///     .AddBackWavePro();
    /// builder.Services.AddWorkflowGate&lt;LargeOrder, PriceOrder, OrderTotal&gt;(
    ///     "large-order-gate", AppJsonContext.Default.WorkflowGateLargeOrderPriceOrderOrderTotal);
    /// </code>
    /// </example>
    public static IServiceCollection AddWorkflowGate<TGate, TStep, TOut>(
        this IServiceCollection services,
        string wireName,
        JsonTypeInfo<WorkflowGate<TGate, TStep, TOut>> gateTypeInfo,
        string queue = "default")
        where TGate : IWorkflowGate<TStep, TOut>, new()
        where TStep : IWorkflowStep<TOut>
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(gateTypeInfo);

        services.AddScoped<IJobHandler<WorkflowGate<TGate, TStep, TOut>>,
            WorkflowGateHandler<TGate, TStep, TOut>>();
        services.AddSingleton<JobRegistration>(
            JobRegistration.Create<WorkflowGate<TGate, TStep, TOut>, WorkflowGateHandler<TGate, TStep, TOut>>(
                wireName, gateTypeInfo, queue));
        return services;
    }

    /// <summary>
    /// Registers a seed-aware conditional workflow gate - its handler and its job registration - in one
    /// call, so a workflow whose <c>.If</c> branch uses this gate can route it at run time. This overload
    /// is for a gate whose predicate reads the workflow's immutable Workflow Input seed in addition to the
    /// observed ancestor's output. The gate step carries no output of its own and needs no tags, so only
    /// its serialization metadata and, optionally, a queue name are required. The handler is registered
    /// scoped: BackWave opens a fresh scope per attempt and resolves the handler from it, so the handler's
    /// scoped dependencies resolve and dispose per attempt, matching how a job module's handlers are
    /// registered.
    /// </summary>
    /// <typeparam name="TGate">The predicate type that decides which arm of the branch runs.</typeparam>
    /// <typeparam name="TStep">The ancestor step whose output the predicate reads.</typeparam>
    /// <typeparam name="TOut">The output value type <typeparamref name="TStep"/> declares it produces.</typeparam>
    /// <typeparam name="TInput">The Workflow Input seed type the predicate reads; the type the workflow was started with.</typeparam>
    /// <param name="services">The service collection to register the gate into.</param>
    /// <param name="wireName">
    /// The Wire Name the gate is registered under - its stable, explicit identity in the job set. It must
    /// be unique across every registered job; a value that collides with another job's Wire Name fails the
    /// job set's validation at startup, when the contributed registrations are folded in.
    /// </param>
    /// <param name="gateTypeInfo">
    /// The serialization metadata for the gate step, read from the application's JSON serializer context
    /// after the gate type is declared serializable on it.
    /// </param>
    /// <param name="queue">
    /// The Queue the gate runs on. Defaults to <c>"default"</c>, matching a worker group that declares no
    /// explicit queue.
    /// </param>
    /// <returns>The same service collection, so registration calls can be chained.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> or <paramref name="gateTypeInfo"/> is <see langword="null"/>.</exception>
    /// <example>
    /// Declare the seed-aware gate step serializable on the application's JSON context, then register it
    /// after <c>AddBackWave</c>:
    /// <code>
    /// [JsonSerializable(typeof(WorkflowGate&lt;OverThreshold, PriceOrder, OrderTotal, CheckoutSeed&gt;))]
    /// public partial class AppJsonContext : JsonSerializerContext;
    ///
    /// builder.Services.AddWorkflowGate&lt;OverThreshold, PriceOrder, OrderTotal, CheckoutSeed&gt;(
    ///     "over-threshold-gate",
    ///     AppJsonContext.Default.WorkflowGateOverThresholdPriceOrderOrderTotalCheckoutSeed);
    /// </code>
    /// </example>
    public static IServiceCollection AddWorkflowGate<TGate, TStep, TOut, TInput>(
        this IServiceCollection services,
        string wireName,
        JsonTypeInfo<WorkflowGate<TGate, TStep, TOut, TInput>> gateTypeInfo,
        string queue = "default")
        where TGate : IWorkflowGate<TStep, TOut, TInput>, new()
        where TStep : IWorkflowStep<TOut>
        where TInput : IWorkflowInput
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(gateTypeInfo);

        services.AddScoped<IJobHandler<WorkflowGate<TGate, TStep, TOut, TInput>>,
            WorkflowGateHandler<TGate, TStep, TOut, TInput>>();
        services.AddSingleton<JobRegistration>(
            JobRegistration.Create<WorkflowGate<TGate, TStep, TOut, TInput>, WorkflowGateHandler<TGate, TStep, TOut, TInput>>(
                wireName, gateTypeInfo, queue));
        return services;
    }
}
