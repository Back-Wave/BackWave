using System.Reflection;
using BackWave.Postgres;
using BackWave.Sqlite;
using BackWave.SqlServer;

namespace BackWave.SchemaGate.Tests;

/// <summary>
/// The additive-first schema-diff gate (ADR 0038, issue 0202) as a deterministic PR-battery test.
/// Diffs each adapter's shipped migration scripts and fails on any non-additive DDL that would break
/// an N-1 binary during a rolling deploy. No database, no Docker — pure resource reads + string
/// classification, so it runs on every PR.
/// </summary>
public sealed class SchemaGateTests
{
    // Each adapter's assembly, reached through a type it ships, so the gate reads the SAME embedded
    // scripts the migrator runs. SQLite is here too: it ships a single consolidated v1 script (no
    // vN-1 → vN step yet), which the gate treats as vacuously additive — its one CREATE-only script
    // introduces nothing destructive. When SQLite regains a real 0002, this same gate inspects it
    // with zero extra wiring.
    public static TheoryData<string, Assembly> Adapters() => new()
    {
        { "Postgres", typeof(PostgresMigrator).Assembly },
        { "SqlServer", typeof(SqlServerMigrator).Assembly },
        { "Sqlite", typeof(SqliteMigrator).Assembly },
    };

    [Theory]
    [MemberData(nameof(Adapters))]
    public void EveryShippedMigrationIsAdditive(string adapter, Assembly adapterAssembly)
    {
        var scripts = SchemaScripts.Load(adapterAssembly);
        Assert.NotEmpty(scripts); // a wiring smoke-check: the resources are actually reachable

        var findings = new List<string>();
        foreach (var script in scripts)
        {
            foreach (var change in AdditiveSchemaGate.Inspect(script.Sql))
            {
                findings.Add($"{adapter} {script.ResourceName} (v{script.Version}): {change.Kind} — {Excerpt(change.Statement)}");
            }
        }

        Assert.True(
            findings.Count == 0,
            $"Non-additive DDL breaks the ADR 0038 N-1 mixed-fleet contract. Land the destructive change in a " +
            $"LATER release; keep this one additive-first. Findings:{Environment.NewLine}{string.Join(Environment.NewLine, findings)}");
    }

    [Fact]
    public void Sqlite_ShipsSingleConsolidatedScript()
    {
        // SQLite ships one consolidated v1 script and no vN-1 → vN step yet — the posture that held
        // before (and again after) the short-lived 0002 Tag Suggest migration was folded back into v1.
        // There is no incremental step for the gate to police here; EveryShippedMigrationIsAdditive
        // still covers the single script (a CREATE-only script is vacuously additive). When SQLite
        // gains a real 0002, re-arm a first-incremental-step assertion alongside the upgrade harness.
        var scripts = SchemaScripts.Load(typeof(SqliteMigrator).Assembly);
        Assert.Single(scripts);
    }

    // ---- Sabotage self-tests: prove the gate turns RED on a synthetic non-additive migration. ----
    // The synthetic pairs are kept INLINE here — never added to a shipped Schema folder.

    [Fact]
    public void Sabotage_DropColumn_IsFlagged()
    {
        const string migration = """
            -- synthetic v9: hand-broken, drops a populated column an N-1 writer still fills.
            ALTER TABLE backwave.jobs DROP COLUMN payload;
            UPDATE backwave.schema_version SET version = 9;
            """;

        var changes = AdditiveSchemaGate.Inspect(migration);
        Assert.Contains(changes, c => c.Kind == SchemaChangeKind.DropColumn);
    }

    [Fact]
    public void Sabotage_DropTable_IsFlagged()
    {
        const string migration = "DROP TABLE backwave.job_tags; UPDATE backwave.schema_version SET version = 9;";
        Assert.Contains(AdditiveSchemaGate.Inspect(migration), c => c.Kind == SchemaChangeKind.DropTable);
    }

    [Fact]
    public void Sabotage_RenameColumn_IsFlagged()
    {
        Assert.Contains(
            AdditiveSchemaGate.Inspect("ALTER TABLE backwave.jobs RENAME COLUMN queue TO queue_name;"),
            c => c.Kind == SchemaChangeKind.Rename);
        Assert.Contains(
            AdditiveSchemaGate.Inspect("EXEC sp_rename 'backwave.jobs.queue', 'queue_name', 'COLUMN';"),
            c => c.Kind == SchemaChangeKind.Rename);
    }

    [Fact]
    public void Sabotage_NewNotNullColumnWithoutDefault_IsFlagged()
    {
        // PG shape and T-SQL shape (the latter also exercises the EXEC-unwrap path).
        Assert.Contains(
            AdditiveSchemaGate.Inspect("ALTER TABLE backwave.jobs ADD COLUMN tenant text NOT NULL;"),
            c => c.Kind == SchemaChangeKind.NotNullColumnWithoutDefault);
        Assert.Contains(
            AdditiveSchemaGate.Inspect("EXEC('ALTER TABLE backwave.jobs ADD tenant nvarchar(200) NOT NULL');"),
            c => c.Kind == SchemaChangeKind.NotNullColumnWithoutDefault);
    }

    [Fact]
    public void Sabotage_TightenedConstraint_IsFlagged()
    {
        Assert.Contains(
            AdditiveSchemaGate.Inspect("ALTER TABLE backwave.jobs ADD CONSTRAINT ck_queue CHECK (queue <> '');"),
            c => c.Kind == SchemaChangeKind.AddConstraint);
    }

    [Fact]
    public void Additive_NullableAndDefaultedAndNewIndex_Pass()
    {
        // The additive shapes the real scripts use must NOT be flagged.
        Assert.Empty(AdditiveSchemaGate.Inspect("ALTER TABLE backwave.jobs ADD COLUMN output bytea NULL;"));
        Assert.Empty(AdditiveSchemaGate.Inspect(
            "ALTER TABLE backwave.queue_limits ADD COLUMN paused boolean NOT NULL DEFAULT false;"));
        Assert.Empty(AdditiveSchemaGate.Inspect(
            "CREATE INDEX IF NOT EXISTS ix_x ON backwave.jobs (queue, due_time) WHERE state = 0;"));
        Assert.Empty(AdditiveSchemaGate.Inspect("CREATE TABLE backwave.workflows (workflow_id uuid PRIMARY KEY);"));
        // DROP INDEX is transparent to N-1 correctness — the documented allowed case (real v1→v2).
        Assert.Empty(AdditiveSchemaGate.Inspect("DROP INDEX IF EXISTS backwave.ix_backwave_jobs_leased;"));
    }

    private static string Excerpt(string statement)
    {
        var flat = string.Join(' ', statement.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return flat.Length <= 160 ? flat : flat[..160] + "…";
    }
}
