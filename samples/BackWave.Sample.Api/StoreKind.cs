using BackWave.Postgres;
using BackWave.Sqlite;
using BackWave.SqlServer;
using BackWave.Storage;
using BackWave.Storage.InMemory;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace BackWave.Sample.Api;

/// <summary>
/// A behavior-carrying store selector for the sample: each kind knows how to build its
/// <see cref="IJobStore"/>, register (or skip) the EF unit of work, and bootstrap its schema.
/// A plain type-object hierarchy — Ardalis SmartEnum-style polymorphism without the dependency —
/// so <c>Program.cs</c> stays free of store-shaped <c>switch</c> statements.
/// </summary>
internal abstract class StoreKind
{
    public static readonly StoreKind InMemory = new InMemoryStore();
    public static readonly StoreKind Postgres = new PostgresStore();
    public static readonly StoreKind SqlServer = new SqlServerStore();

    /// <summary>The SQLite Embedded Adapter, co-resident: BackWave's tables live in the app's OWN file.</summary>
    public static readonly StoreKind Sqlite = new SqliteCoResidentStore();

    /// <summary>The SQLite Embedded Adapter, dedicated: BackWave on its own file, business data elsewhere.</summary>
    public static readonly StoreKind SqliteDedicated = new SqliteDedicatedStore();

    /// <summary>Resolves the configured kind (<c>BackWave:Store</c>), defaulting to In-Memory.</summary>
    public static StoreKind FromConfiguration(IConfiguration configuration)
    {
        var configured = configuration["BackWave:Store"] ?? nameof(InMemory);
        return configured.Equals(nameof(Postgres), StringComparison.OrdinalIgnoreCase) ? Postgres
            : configured.Equals(nameof(SqlServer), StringComparison.OrdinalIgnoreCase) ? SqlServer
            : configured.Equals(nameof(SqliteDedicated), StringComparison.OrdinalIgnoreCase) ? SqliteDedicated
            : configured.Equals(nameof(Sqlite), StringComparison.OrdinalIgnoreCase) ? Sqlite
            : InMemory;
    }

    public abstract string Name { get; }

    /// <summary>Whether <c>POST /tx</c> can run here, or must answer 409.</summary>
    public abstract bool SupportsTransactionalEnqueue { get; }

    /// <summary>
    /// Builds the store handed to <c>UseStore(...)</c>. The <paramref name="loggerFactory"/> comes from
    /// the <c>UseStore</c> factory overload's service provider and goes onto the store options: it is what
    /// turns on the schema-migration log (event 1302), which every adapter leaves OFF by default because
    /// the option is null unless set. Kinds that never migrate ignore it.
    /// </summary>
    public abstract IJobStore CreateStore(IConfiguration configuration, ILoggerFactory loggerFactory);

    /// <summary>Registers the EF unit of work for the Transactional Enqueue demo — relational only.</summary>
    public virtual void AddDbContext(IServiceCollection services, IConfiguration configuration)
    {
        // In-Memory has no relational unit of work to register.
    }

    /// <summary>
    /// Bootstraps the business table before the store migrates: EF EnsureCreated only creates it
    /// while the database has no tables, and the store's AutoMigrate would otherwise win the race.
    /// </summary>
    public virtual ValueTask EnsureDatabaseAsync(IServiceProvider services) => ValueTask.CompletedTask;

    /// <summary>
    /// Opts the tracer provider into this store's <c>db.*</c> round-trip spans, matching the
    /// per-adapter opt-in method on <c>BackWave.OpenTelemetry</c>. In-Memory ships no store-span
    /// source, so its override is a no-op.
    /// </summary>
    public virtual void AddAdapterTracing(TracerProviderBuilder tracing)
    {
        // InMemory: no shipped adapter source to subscribe.
    }

    /// <summary>Opts the meter provider into this store's store-fault meter (the metrics twin of
    /// <see cref="AddAdapterTracing"/>). In-Memory ships no store meter, so its override is a no-op.</summary>
    public virtual void AddAdapterMetrics(MeterProviderBuilder metrics)
    {
        // InMemory: no shipped adapter meter to subscribe.
    }

    public override string ToString() => Name;

    private sealed class InMemoryStore : StoreKind
    {
        public override string Name => "InMemory";
        public override bool SupportsTransactionalEnqueue => false;
        // No schema and no migration, so no LoggerFactory to hand it.
        public override IJobStore CreateStore(IConfiguration configuration, ILoggerFactory loggerFactory)
            => new InMemoryJobStore();
    }

    private abstract class RelationalStore : StoreKind
    {
        public override bool SupportsTransactionalEnqueue => true;

        public override async ValueTask EnsureDatabaseAsync(IServiceProvider services)
        {
            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SampleDbContext>();
            await db.Database.EnsureCreatedAsync();
        }
    }

    private sealed class PostgresStore : RelationalStore
    {
        private const string DefaultConnectionString =
            "Host=localhost;Port=5398;Username=backwave;Password=backwave;Database=backwave_sample";

        public override string Name => "Postgres";

        public override IJobStore CreateStore(IConfiguration configuration, ILoggerFactory loggerFactory)
            => new PostgresJobStore(new PostgresStoreOptions
            {
                ConnectionString = ConnectionString(configuration),
                AutoMigrate = true, // embedded Schema/*.sql self-applies on startup
                LoggerFactory = loggerFactory, // opts in to the migration log; null (the default) is silent
            });

        public override void AddDbContext(IServiceCollection services, IConfiguration configuration)
            => services.AddDbContext<SampleDbContext>(options => options.UseNpgsql(ConnectionString(configuration)));

        public override void AddAdapterTracing(TracerProviderBuilder tracing) => tracing.AddBackWavePostgresInstrumentation();

        public override void AddAdapterMetrics(MeterProviderBuilder metrics) => metrics.AddBackWavePostgresInstrumentation();

        private static string ConnectionString(IConfiguration configuration)
            => configuration["BackWave:Postgres:ConnectionString"] ?? DefaultConnectionString;
    }

    private sealed class SqlServerStore : RelationalStore
    {
        private const string DefaultConnectionString =
            "Server=localhost,14331;Database=backwave_sample;User Id=sa;Password=BackWave!Passw0rd;TrustServerCertificate=true";

        public override string Name => "SqlServer";

        public override IJobStore CreateStore(IConfiguration configuration, ILoggerFactory loggerFactory)
            => new SqlServerJobStore(new SqlServerStoreOptions
            {
                ConnectionString = ConnectionString(configuration),
                AutoMigrate = true,
                LoggerFactory = loggerFactory, // opts in to the migration log; null (the default) is silent
            });

        public override void AddDbContext(IServiceCollection services, IConfiguration configuration)
            => services.AddDbContext<SampleDbContext>(options => options.UseSqlServer(ConnectionString(configuration)));

        public override void AddAdapterTracing(TracerProviderBuilder tracing) => tracing.AddBackWaveSqlServerInstrumentation();

        public override void AddAdapterMetrics(MeterProviderBuilder metrics) => metrics.AddBackWaveSqlServerInstrumentation();

        private static string ConnectionString(IConfiguration configuration)
            => configuration["BackWave:SqlServer:ConnectionString"] ?? DefaultConnectionString;
    }

    /// <summary>
    /// The SQLite <b>Embedded Adapter</b> (no server, no Docker — just a local file). Two deployments:
    /// <see cref="SqliteCoResidentStore"/> puts BackWave's tables in the application's OWN database file
    /// so a Transactional Enqueue commits a job atomically with a business write (the headline feature);
    /// <see cref="SqliteDedicatedStore"/> gives BackWave its own file and forgoes that. Paths resolve
    /// under the content root so the sample writes its <c>.db</c> files somewhere predictable.
    /// </summary>
    private abstract class SqliteStore : RelationalStore
    {
        /// <summary>Where the BackWave <c>backwave_*</c> tables live.</summary>
        protected abstract string BackWaveDataSource(IConfiguration configuration);

        /// <summary>Where the sample's business table lives (same file co-resident, a different one dedicated).</summary>
        protected abstract string BusinessDataSource(IConfiguration configuration);

        public override IJobStore CreateStore(IConfiguration configuration, ILoggerFactory loggerFactory)
            => new SqliteJobStore(new SqliteStoreOptions
            {
                ConnectionString = $"Data Source={BackWaveDataSource(configuration)}",
                AutoMigrate = true, // embedded Schema/0001_initial.sql self-applies on startup
                LoggerFactory = loggerFactory, // opts in to the migration log; null (the default) is silent
            });

        public override void AddDbContext(IServiceCollection services, IConfiguration configuration)
            => services.AddDbContext<SampleDbContext>(
                options => options.UseSqlite($"Data Source={BusinessDataSource(configuration)}"));

        // Both SQLite deployments (co-resident and dedicated) emit on the one BackWave.Sqlite source.
        public override void AddAdapterTracing(TracerProviderBuilder tracing) => tracing.AddBackWaveSqliteInstrumentation();

        public override void AddAdapterMetrics(MeterProviderBuilder metrics) => metrics.AddBackWaveSqliteInstrumentation();

        /// <summary>Resolves a (possibly relative) configured path under the app base directory.</summary>
        protected static string Resolve(IConfiguration configuration, string key, string fallback)
        {
            var configured = configuration[key] ?? fallback;
            return Path.IsPathRooted(configured)
                ? configured
                : Path.Combine(AppContext.BaseDirectory, configured);
        }
    }

    private sealed class SqliteCoResidentStore : SqliteStore
    {
        public override string Name => "SqliteCoResident";

        // Co-resident: one file holds BOTH the business table and BackWave's tables, so the EF
        // transaction in POST /tx and the job commit atomically (the same-file guard verifies it).
        protected override string BackWaveDataSource(IConfiguration configuration)
            => Resolve(configuration, "BackWave:Sqlite:DataSource", "backwave-sample.db");

        protected override string BusinessDataSource(IConfiguration configuration)
            => BackWaveDataSource(configuration);
    }

    private sealed class SqliteDedicatedStore : SqliteStore
    {
        public override string Name => "SqliteDedicated";

        // Dedicated: BackWave on its own file, business data in a SEPARATE file. The two files cannot
        // share a transaction, so this deployment forgoes Transactional Enqueue — POST /tx answers 409.
        public override bool SupportsTransactionalEnqueue => false;

        protected override string BackWaveDataSource(IConfiguration configuration)
            => Resolve(configuration, "BackWave:Sqlite:DataSource", "backwave-dedicated.db");

        protected override string BusinessDataSource(IConfiguration configuration)
            => Resolve(configuration, "BackWave:Sqlite:BusinessDataSource", "business-dedicated.db");
    }
}
