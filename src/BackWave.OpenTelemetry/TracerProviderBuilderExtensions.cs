using BackWave.OpenTelemetry;

namespace OpenTelemetry.Trace;

/// <summary>
/// Registers BackWave's trace sources on an OpenTelemetry <see cref="TracerProviderBuilder"/>. The Core
/// job-lifecycle spans (enqueue, claim, execute) come in with one call; each storage adapter's store
/// round-trip spans opt in separately, so an operator who does not want the chattier store spans never
/// collects them.
/// </summary>
public static class BackWaveTracerProviderBuilderExtensions
{
    /// <summary>
    /// Subscribes the tracer provider to BackWave's Core job-lifecycle spans - the enqueue (send), claim
    /// (receive), and execution (process) activities. Call an adapter method such as
    /// <see cref="AddBackWavePostgresInstrumentation"/> as well to also collect that adapter's store spans.
    /// </summary>
    /// <param name="builder">The tracer provider builder to register the BackWave source on.</param>
    /// <returns>The same <paramref name="builder"/>, so calls can be chained.</returns>
    /// <exception cref="System.ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    /// <example>
    /// Wire BackWave's job spans into an ASP.NET Core host, then add the Postgres store spans on top:
    /// <code>
    /// builder.Services.AddOpenTelemetry()
    ///     .WithTracing(tracing => tracing
    ///         .AddBackWaveInstrumentation()
    ///         .AddBackWavePostgresInstrumentation()
    ///         .AddOtlpExporter());
    /// </code>
    /// </example>
    public static TracerProviderBuilder AddBackWaveInstrumentation(this TracerProviderBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddSource(BackWaveSourceNames.Core);
    }

    /// <summary>
    /// Subscribes the tracer provider to the BackWave Postgres adapter's store spans - one CLIENT span per
    /// store round-trip (claim, enqueue, complete, and so on), nested under the Core claim span. Pair it
    /// with <see cref="AddBackWaveInstrumentation"/> to also collect the job-lifecycle spans.
    /// </summary>
    /// <param name="builder">The tracer provider builder to register the Postgres adapter source on.</param>
    /// <returns>The same <paramref name="builder"/>, so calls can be chained.</returns>
    /// <exception cref="System.ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    /// <example>
    /// <code>
    /// builder.Services.AddOpenTelemetry()
    ///     .WithTracing(tracing => tracing
    ///         .AddBackWaveInstrumentation()
    ///         .AddBackWavePostgresInstrumentation());
    /// </code>
    /// </example>
    public static TracerProviderBuilder AddBackWavePostgresInstrumentation(this TracerProviderBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddSource(BackWaveSourceNames.Postgres);
    }

    /// <summary>
    /// Subscribes the tracer provider to the BackWave SQL Server adapter's store spans - one CLIENT span per
    /// store round-trip (claim, enqueue, complete, and so on), nested under the Core claim span. Pair it
    /// with <see cref="AddBackWaveInstrumentation"/> to also collect the job-lifecycle spans.
    /// </summary>
    /// <param name="builder">The tracer provider builder to register the SQL Server adapter source on.</param>
    /// <returns>The same <paramref name="builder"/>, so calls can be chained.</returns>
    /// <exception cref="System.ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    /// <example>
    /// <code>
    /// builder.Services.AddOpenTelemetry()
    ///     .WithTracing(tracing => tracing
    ///         .AddBackWaveInstrumentation()
    ///         .AddBackWaveSqlServerInstrumentation());
    /// </code>
    /// </example>
    public static TracerProviderBuilder AddBackWaveSqlServerInstrumentation(this TracerProviderBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddSource(BackWaveSourceNames.SqlServer);
    }

    /// <summary>
    /// Subscribes the tracer provider to the BackWave SQLite adapter's store spans - one CLIENT span per
    /// store round-trip (claim, enqueue, complete, and so on), nested under the Core claim span. Pair it
    /// with <see cref="AddBackWaveInstrumentation"/> to also collect the job-lifecycle spans.
    /// </summary>
    /// <param name="builder">The tracer provider builder to register the SQLite adapter source on.</param>
    /// <returns>The same <paramref name="builder"/>, so calls can be chained.</returns>
    /// <exception cref="System.ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    /// <example>
    /// <code>
    /// builder.Services.AddOpenTelemetry()
    ///     .WithTracing(tracing => tracing
    ///         .AddBackWaveInstrumentation()
    ///         .AddBackWaveSqliteInstrumentation());
    /// </code>
    /// </example>
    public static TracerProviderBuilder AddBackWaveSqliteInstrumentation(this TracerProviderBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddSource(BackWaveSourceNames.Sqlite);
    }
}
