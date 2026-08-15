using BackWave.Dashboard;
using Microsoft.Extensions.DependencyInjection;

namespace BackWave.Pro.Dashboard;

/// <summary>
/// Registration entry point for BackWave Pro's dashboard surfaces. Call
/// <see cref="AddBackWaveProDashboard"/> during startup, after the BackWave Pro registration that
/// evaluates the license, to light up the Pro additions to the BackWave dashboard. The additions
/// attach through the dashboard's own extension points — the base dashboard needs no change to show
/// them: the Workflows surface (its list, graph, and cancel action) and the unlicensed-Pro banner.
/// </summary>
public static class BackWaveProDashboardServiceCollectionExtensions
{
    /// <summary>
    /// Registers BackWave Pro's dashboard surfaces into the application's service container. Once
    /// registered, the BackWave dashboard gains the Workflows surface — the workflow list, the graph
    /// view, and the cancel-workflow action — and shows the unlicensed-Pro banner whenever the Pro
    /// license is not valid (and nothing extra once it is valid). The Workflows surface appears because
    /// the package is installed, not because of license state; the banner is presentation-only.
    /// Requires that BackWave Pro has already been registered (so the evaluated license is available to
    /// resolve).
    /// </summary>
    /// <param name="services">The service collection to register the Pro dashboard surfaces into.</param>
    /// <returns>The same <paramref name="services"/>, so registration calls can be chained.</returns>
    /// <example>
    /// <code>
    /// builder.Services
    ///     .AddBackWave(/* ... */)
    ///     .AddBackWavePro(builder.Configuration["BackWave:ProLicense"]);
    /// builder.Services.AddBackWaveProDashboard();
    /// </code>
    /// </example>
    public static IServiceCollection AddBackWaveProDashboard(this IServiceCollection services)
    {
        services.AddBackWaveDashboardExtension<WorkflowDashboardExtension>();
        return services.AddBackWaveDashboardExtension<UnlicensedProBanner>();
    }
}
