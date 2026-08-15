using System.Diagnostics.CodeAnalysis;
using BackWave.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace BackWave.Dashboard;

/// <summary>
/// Extension methods that mount the BackWave dashboard on an application's request pipeline and let
/// separately-installed packages contribute surfaces to it.
/// </summary>
public static class BackWaveDashboardExtensions
{
    /// <summary>
    /// Registers a dashboard extension so a separately-installed package can contribute surfaces —
    /// extra navigation entries and a banner above the page content — without the base dashboard
    /// depending on it. The dashboard resolves every registered extension at render time. Call this
    /// once per extension during service registration; registering none leaves the dashboard
    /// rendering exactly as it does on its own.
    /// </summary>
    /// <typeparam name="TExtension">
    /// The extension implementation to register. It is resolved from the container, so it may take
    /// constructor dependencies to drive what it contributes.
    /// </typeparam>
    /// <param name="services">The service collection to register the extension into.</param>
    /// <returns>The same <paramref name="services"/>, so registration calls can be chained.</returns>
    /// <example>
    /// <code>
    /// services.AddBackWaveDashboardExtension&lt;MyDashboardExtension&gt;();
    /// </code>
    /// </example>
    public static IServiceCollection AddBackWaveDashboardExtension<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TExtension>(
        this IServiceCollection services)
        where TExtension : class, IDashboardExtension
    {
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IDashboardExtension, TExtension>());
        return services;
    }

    /// <summary>
    /// Opts the dashboard into its live, in-process metrics panel: per-second throughput
    /// (enqueued / processed / failed) with sparklines, plus Top Endpoints and Faulting Endpoints
    /// by job type. A meter listener subscribes to the counters BackWave already emits and
    /// accumulates them into a bounded, fixed-window in-memory ring buffer inside this process —
    /// no storage, no configuration, and no cost until you call this.
    /// </summary>
    /// <remarks>
    /// The panel is <b>per-node and ephemeral</b>: it reflects only throughput on the node hosting
    /// this dashboard and resets when the process restarts. It is a live glance, not a system of
    /// record — retained history, cross-node aggregation, and exact distributions remain the job of
    /// your metrics stack (Prometheus, Grafana, and the like), which the same counters already feed.
    /// The dashboard works without this call; the panel then renders a short note explaining how to
    /// enable it. Register this in the same host that runs your BackWave workers, so the listener
    /// observes that process's job telemetry.
    /// </remarks>
    /// <param name="services">The service collection to register the metrics collector into.</param>
    /// <returns>The same <paramref name="services"/>, so registration calls can be chained.</returns>
    /// <example>
    /// <code>
    /// builder.Services.AddBackWave(/* ... */);
    /// builder.Services.AddBackWaveDashboardMetrics();
    ///
    /// var app = builder.Build();
    /// app.UseBackWaveDashboard("/backwave");
    /// </code>
    /// </example>
    public static IServiceCollection AddBackWaveDashboardMetrics(this IServiceCollection services)
    {
        // One shared instance behind both registrations: the singleton the dashboard resolves per
        // request, and the hosted service that instantiates it at host start so its meter listener is
        // live from the first job — not only from the first dashboard page view.
        services.TryAddSingleton<DashboardMetricsCollector>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<DashboardMetricsCollector>());
        return services;
    }

    /// <summary>
    /// Opts the dashboard into its live, in-process metrics panel from inside the <c>AddBackWave</c>
    /// block, next to <c>AddWorkerGroup</c> and <c>AddObservers</c> — so the whole BackWave setup lives
    /// in one place. This is the same registration as
    /// <see cref="AddBackWaveDashboardMetrics(IServiceCollection)"/>, expressed on the builder.
    /// </summary>
    /// <remarks>
    /// The panel is <b>per-node and ephemeral</b>: it reflects only throughput on the node hosting this
    /// dashboard and resets when the process restarts. It is a live glance, not a system of record —
    /// retained history, cross-node aggregation, and exact distributions remain the job of your metrics
    /// stack, which the same counters already feed. The dashboard works without this call; the panel
    /// then renders a short note explaining how to enable it.
    /// </remarks>
    /// <param name="builder">The BackWave builder to register the metrics collector through.</param>
    /// <returns>The same <paramref name="builder"/>, so calls can be chained.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    /// <example>
    /// <code>
    /// builder.Services.AddBackWave(backwave =&gt;
    /// {
    ///     backwave.UseStore(/* ... */).UseJobs(MyJobs.Module);
    ///     backwave.AddWorkerGroup(/* ... */);
    ///     backwave.AddDashboardMetrics();
    /// });
    /// </code>
    /// </example>
    public static BackWaveBuilder AddDashboardMetrics(this BackWaveBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.ConfigureServices(services => services.AddBackWaveDashboardMetrics());
    }

    /// <summary>
    /// Mounts the dashboard as middleware on the host's own request pipeline — operational
    /// visibility into your jobs with no separate service to run. The dashboard is read-only by
    /// default: every byte it renders is read through the BackWave monitor that
    /// <c>AddBackWave</c> registers, never through direct storage access. Mount it after your
    /// authentication and authorization middleware so the permission callbacks on
    /// <paramref name="options"/> see an authenticated request.
    /// </summary>
    /// <param name="app">The application's request pipeline builder to mount the dashboard on.</param>
    /// <param name="pathPrefix">
    /// The URL path the dashboard is served under. Must start with <c>'/'</c>. Defaults to
    /// <c>"/backwave"</c>. Pass <c>"/"</c> to mount the dashboard at the site root — it then runs
    /// terminally against the whole pipeline and builds its links from the root.
    /// </param>
    /// <param name="options">
    /// Dashboard configuration — chiefly the authorization callbacks. When <c>null</c>, defaults
    /// are used: viewing is allowed, and every write action and sensitive-data view is denied, so
    /// the dashboard is safe and read-only until you opt in.
    /// </param>
    /// <returns>The same <paramref name="app"/>, so calls can be chained.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="pathPrefix"/> is empty or does not start with <c>'/'</c>.
    /// </exception>
    /// <example>
    /// <code>
    /// app.UseAuthentication();
    /// app.UseAuthorization();
    /// app.UseBackWaveDashboard("/jobs", new BackWaveDashboardOptions
    /// {
    ///     AuthorizeView = ctx => ValueTask.FromResult(ctx.User.IsInRole("ops")),
    ///     AuthorizeRequeue = ctx => ValueTask.FromResult(ctx.User.IsInRole("ops-admin")),
    /// });
    /// </code>
    /// </example>
    public static IApplicationBuilder UseBackWaveDashboard(
        this IApplicationBuilder app, string pathPrefix = "/backwave", BackWaveDashboardOptions? options = null)
    {
        if (string.IsNullOrEmpty(pathPrefix) || !pathPrefix.StartsWith('/'))
        {
            throw new ArgumentException(
                $"The dashboard path prefix must start with '/' (got '{pathPrefix}').", nameof(pathPrefix));
        }

        var resolved = options ?? new BackWaveDashboardOptions();

        // A "/" prefix mounts the dashboard at the site root. There is no path segment to branch on
        // (ASP.NET's Map rejects a "/" pathMatch), so run it terminally against the whole pipeline;
        // the request PathBase stays empty, which is exactly the root basePath the dashboard and its
        // extensions build links from.
        if (pathPrefix == "/")
        {
            app.Run(context => DashboardRequestHandler.HandleAsync(context, resolved));
            return app;
        }

        return app.Map(pathPrefix, branch =>
            branch.Run(context => DashboardRequestHandler.HandleAsync(context, resolved)));
    }
}
