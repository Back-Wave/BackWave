using BackWave.OpenTelemetry;

namespace OpenTelemetry.Metrics;

/// <summary>
/// Registers BackWave's meters on an OpenTelemetry <see cref="MeterProviderBuilder"/>. The Core
/// job-lifecycle instruments (throughput, latency, queue depth, worker saturation, observer delivery)
/// come in with one call; each storage adapter's store-fault meter opts in separately, matching the
/// per-adapter split of the trace sources.
/// </summary>
public static class BackWaveMeterProviderBuilderExtensions
{
    /// <summary>
    /// Subscribes the meter provider to BackWave's Core job-lifecycle instruments - the sent, consumed,
    /// failed, and dead-lettered counters, the process-duration, schedule-delay, and queue-wait
    /// histograms, and the queue-depth and worker-slot gauges. Call an adapter method such as
    /// <see cref="AddBackWavePostgresInstrumentation"/> as well to also collect that adapter's store meter.
    /// </summary>
    /// <param name="builder">The meter provider builder to register the BackWave meter on.</param>
    /// <returns>The same <paramref name="builder"/>, so calls can be chained.</returns>
    /// <exception cref="System.ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    /// <example>
    /// Wire BackWave's job metrics into an ASP.NET Core host, then add the Postgres store meter on top:
    /// <code>
    /// builder.Services.AddOpenTelemetry()
    ///     .WithMetrics(metrics => metrics
    ///         .AddBackWaveInstrumentation()
    ///         .AddBackWavePostgresInstrumentation()
    ///         .AddOtlpExporter());
    /// </code>
    /// </example>
    public static MeterProviderBuilder AddBackWaveInstrumentation(this MeterProviderBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddMeter(BackWaveSourceNames.Core);
    }

    /// <summary>
    /// Subscribes the meter provider to the BackWave Postgres adapter's store meter - the store-fault
    /// counter tagged transient versus terminal. Pair it with <see cref="AddBackWaveInstrumentation"/> to
    /// also collect the job-lifecycle instruments.
    /// </summary>
    /// <param name="builder">The meter provider builder to register the Postgres adapter meter on.</param>
    /// <returns>The same <paramref name="builder"/>, so calls can be chained.</returns>
    /// <exception cref="System.ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    /// <example>
    /// <code>
    /// builder.Services.AddOpenTelemetry()
    ///     .WithMetrics(metrics => metrics
    ///         .AddBackWaveInstrumentation()
    ///         .AddBackWavePostgresInstrumentation());
    /// </code>
    /// </example>
    public static MeterProviderBuilder AddBackWavePostgresInstrumentation(this MeterProviderBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddMeter(BackWaveSourceNames.Postgres);
    }

    /// <summary>
    /// Subscribes the meter provider to the BackWave SQL Server adapter's store meter - the store-fault
    /// counter tagged transient versus terminal. Pair it with <see cref="AddBackWaveInstrumentation"/> to
    /// also collect the job-lifecycle instruments.
    /// </summary>
    /// <param name="builder">The meter provider builder to register the SQL Server adapter meter on.</param>
    /// <returns>The same <paramref name="builder"/>, so calls can be chained.</returns>
    /// <exception cref="System.ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    /// <example>
    /// <code>
    /// builder.Services.AddOpenTelemetry()
    ///     .WithMetrics(metrics => metrics
    ///         .AddBackWaveInstrumentation()
    ///         .AddBackWaveSqlServerInstrumentation());
    /// </code>
    /// </example>
    public static MeterProviderBuilder AddBackWaveSqlServerInstrumentation(this MeterProviderBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddMeter(BackWaveSourceNames.SqlServer);
    }

    /// <summary>
    /// Subscribes the meter provider to the BackWave SQLite adapter's store meter - the store-fault
    /// counter tagged transient versus terminal. Pair it with <see cref="AddBackWaveInstrumentation"/> to
    /// also collect the job-lifecycle instruments.
    /// </summary>
    /// <param name="builder">The meter provider builder to register the SQLite adapter meter on.</param>
    /// <returns>The same <paramref name="builder"/>, so calls can be chained.</returns>
    /// <exception cref="System.ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    /// <example>
    /// <code>
    /// builder.Services.AddOpenTelemetry()
    ///     .WithMetrics(metrics => metrics
    ///         .AddBackWaveInstrumentation()
    ///         .AddBackWaveSqliteInstrumentation());
    /// </code>
    /// </example>
    public static MeterProviderBuilder AddBackWaveSqliteInstrumentation(this MeterProviderBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddMeter(BackWaveSourceNames.Sqlite);
    }

    /// <summary>
    /// Subscribes the meter provider to the BackWave Oracle adapter's store meter - the store-fault
    /// counter tagged transient versus terminal. Pair it with <see cref="AddBackWaveInstrumentation"/> to
    /// also collect the job-lifecycle instruments.
    /// </summary>
    /// <param name="builder">The meter provider builder to register the Oracle adapter meter on.</param>
    /// <returns>The same <paramref name="builder"/>, so calls can be chained.</returns>
    /// <exception cref="System.ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    /// <example>
    /// <code>
    /// builder.Services.AddOpenTelemetry()
    ///     .WithMetrics(metrics => metrics
    ///         .AddBackWaveInstrumentation()
    ///         .AddBackWaveOracleInstrumentation());
    /// </code>
    /// </example>
    public static MeterProviderBuilder AddBackWaveOracleInstrumentation(this MeterProviderBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddMeter(BackWaveSourceNames.Oracle);
    }
}
