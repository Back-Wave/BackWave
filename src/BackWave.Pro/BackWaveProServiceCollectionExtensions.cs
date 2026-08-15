using BackWave.Pro.Licensing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace BackWave.Pro;

/// <summary>
/// Registration entry point for BackWave Pro — the revenue-gated add-on tier. Call
/// <see cref="AddBackWavePro"/> once during startup, after <c>AddBackWave</c>, to enable the Pro
/// features and evaluate the Pro license. Referencing this package is the entire boundary: Pro features
/// are available because the package is present, never because of the license. BackWave Pro is free to
/// use for organizations under $1M in annual revenue; above that a license is required, on the honor
/// system.
/// </summary>
public static class BackWaveProServiceCollectionExtensions
{
    /// <summary>
    /// Registers BackWave Pro in the application's service container and evaluates the supplied license
    /// fully offline (a signature check against a public key embedded in this package — no network
    /// call). Evaluation always soft-fails: a missing, malformed, or out-of-term license logs a single
    /// startup warning and nothing more. Features behave identically in every license state — the
    /// license only governs the warning and the Pro dashboard's banner, not what runs.
    /// </summary>
    /// <param name="services">The service collection to register BackWave Pro into.</param>
    /// <param name="license">
    /// The license string, or <see langword="null"/> (the default) for free use. Free use is correct
    /// for organizations under $1M in annual revenue; above that, pass the license string issued at
    /// purchase.
    /// </param>
    /// <returns>The same service collection, so registration calls can be chained.</returns>
    /// <example>
    /// <code>
    /// builder.Services
    ///     .AddBackWave(bw => bw
    ///         .UseStore(new PostgresJobStore(connectionString))
    ///         .UseJobs(BackWaveJobs.Module)
    ///         .AddWorkerGroup(new WorkerGroupOptions { Name = "default" }))
    ///     .AddBackWavePro(builder.Configuration["BackWave:ProLicense"]);
    /// </code>
    /// </example>
    public static IServiceCollection AddBackWavePro(this IServiceCollection services, string? license = null)
    {
        services.TryAddSingleton(ProLicense.Evaluate(license));
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, ProLicenseWarningService>());
        return services;
    }
}
