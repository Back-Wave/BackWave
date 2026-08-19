namespace BackWave.OpenTelemetry;

// The source/meter names BackWave and its adapters emit on. Kept here as a single source of truth for
// both the tracer and meter registration extensions. Subscription is BY NAME, so this package holds no
// project reference to Core or the adapters - these strings must stay in lockstep with the SourceName
// constants those assemblies declare (a drift shows up as spans that never arrive).
internal static class BackWaveSourceNames
{
    // The Core job-lifecycle source (BackWave.Diagnostics.BackWaveDiagnostics.SourceName).
    internal const string Core = "BackWave";

    // The per-adapter store-round-trip sources (each adapter's *Diagnostics.SourceName).
    internal const string Postgres = "BackWave.Postgres";
    internal const string SqlServer = "BackWave.SqlServer";
    internal const string Sqlite = "BackWave.Sqlite";
    internal const string Oracle = "BackWave.Oracle";
}
