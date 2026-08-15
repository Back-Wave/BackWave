using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;

namespace BackWave.Sqlite;

// The SQLite adapter's own OpenTelemetry surface: one ActivitySource and one Meter, both named
// "BackWave.Sqlite" and versioned with the package version, kept separate from the Core "BackWave"
// source so an operator can silence the (chatty) store spans without losing the job-lifecycle spans.
//
// Modelled on the OTel DATABASE semantic conventions: each wrapped store round-trip is a CLIENT span
// carrying db.system = "sqlite", db.operation.name (claim/enqueue/complete/fail/expire_leases), and
// db.collection.name (the effective jobs table, honoring a custom TablePrefix). We never force a root: each
// span inherits whatever Core span is ambient at its call site - the PRODUCER "send" span for enqueue,
// the CLIENT "receive" span for claim - while the background lease sweep, running under no job span, is
// simply a root.
//
// Everything here lives above the determinism boundary: pure emit at the Shell edge, nothing the Core
// or Simulator reads back. Built on the base class library only, so a round-trip costs nothing until a
// tracer/meter provider subscribes to "BackWave.Sqlite".
internal static class SqliteDiagnostics
{
    internal const string SourceName = "BackWave.Sqlite";

    // The db.system value for this provider, per the OTel database conventions.
    private const string DbSystemValue = "sqlite";

    // db.* semantic-convention attribute keys (convention-borrowed; the values are ours).
    private const string DbSystemKey = "db.system";
    private const string DbOperationKey = "db.operation.name";
    private const string DbCollectionKey = "db.collection.name";

    // The package version (MinVer's AssemblyInformationalVersion, minus any +build metadata) as the
    // telemetry scope version, so a collector records which adapter build produced a signal.
    private static readonly string Version = ResolveScopeVersion();

    internal static readonly ActivitySource ActivitySource = new(SourceName, Version);
    internal static readonly Meter Meter = new(SourceName, Version);

    // The metric half of the store-fault log event: one count per faulted store round-trip, tagged
    // transient (the worker degrades-and-retries) vs terminal (the worker fail-stops).
    private static readonly Counter<long> StoreFaults = Meter.CreateCounter<long>(
        "backwave.store.faults", "{fault}",
        "Store round-trips that faulted, tagged transient (retryable) vs terminal.");

    // Opens a CLIENT span for one store round-trip. Null when no listener is subscribed, so the caller
    // pays nothing. Left un-rooted on purpose: it inherits whatever Core span is ambient at the call site
    // (the "send" span for enqueue, the "receive" span for claim), or is a root when none is (the sweep).
    internal static Activity? StartStore(string operation, string collection)
    {
        var activity = ActivitySource.StartActivity(operation, ActivityKind.Client);
        if (activity is not null)
        {
            activity.DisplayName = $"{operation} {collection}";
            activity.SetTag(DbSystemKey, DbSystemValue);
            activity.SetTag(DbOperationKey, operation);
            activity.SetTag(DbCollectionKey, collection);
        }
        return activity;
    }

    // Records a faulted store round-trip: increments backwave.store.faults tagged transient/terminal and
    // marks the (possibly null) span in error per the OTel convention. Telemetry-only; the caller
    // rethrows so the host's own classification and retry/fail-stop decision are untouched.
    internal static void RecordStoreFault(Activity? activity, Exception exception, bool isTransient)
    {
        StoreFaults.Add(1, new KeyValuePair<string, object?>(
            "backwave.store.fault_kind", isTransient ? "transient" : "terminal"));
        if (activity is not null)
        {
            activity.SetTag("error.type", exception.GetType().FullName ?? exception.GetType().Name);
            activity.SetStatus(ActivityStatusCode.Error, exception.Message);
        }
    }

    private static string ResolveScopeVersion()
    {
        var informational = typeof(SqliteDiagnostics).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(informational))
        {
            return "0.0.0";
        }
        // .NET appends "+{commit-sha}" to the informational version; the scope version is the semver.
        var plus = informational.IndexOf('+');
        return plus < 0 ? informational : informational[..plus];
    }
}
