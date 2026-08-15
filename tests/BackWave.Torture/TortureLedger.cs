using System.Text.Json;
using BackWave.Storage;

namespace BackWave.Torture;

// Cross-run coverage ledger for the Torture Suite — the store-mode twin of the VOPR CoverageLedger.
// Every run appends one line (clean OR red) when BACKWAVE_TORTURE_LEDGER is set; `--stats` folds the
// append-only ledger into a citable Markdown summary (docs/torture-coverage.md). Unlike VOPR, whose
// unit is VIRTUAL cluster-time, the torture unit is REAL wall-clock on a live adapter: hours hammered,
// jobs audited, store ops exercised, and duplicate-key races provoked — split per adapter.

/// <summary>
/// One row in the cross-run Torture coverage ledger: a single run's durable contribution to total
/// tested coverage against a live adapter. Appended at a run's end (clean or red), it captures the
/// honest figures behind "we have hammered NN hours of real concurrent load across MM jobs".
/// </summary>
internal sealed record TortureLedgerEntry
{
    /// <summary>Wall-clock instant the run ended, ISO-8601 UTC.</summary>
    public required string TimestampUtc { get; init; }

    /// <summary>Commit the run was built from (empty if not supplied).</summary>
    public required string GitSha { get; init; }

    /// <summary>Adapter shape driven: <c>Postgres</c>, <c>SqlServer</c>, <c>Sqlite</c>, <c>SqliteMultiProcess</c>.</summary>
    public required string Adapter { get; init; }

    /// <summary>The run's workload seed (hex), so every random decision replays.</summary>
    public required string Seed { get; init; }

    /// <summary>The configured workload time box in seconds — the "hammer time" against the adapter.</summary>
    public required double DurationSeconds { get; init; }

    /// <summary>Actual elapsed seconds end to end: workload + drain + audit.</summary>
    public required double WallSeconds { get; init; }

    /// <summary>Concurrent synthetic clients in the run.</summary>
    public required int Clients { get; init; }

    /// <summary><c>clean</c> when every oracle held, else <c>violation</c>.</summary>
    public required string Verdict { get; init; }

    /// <summary>Jobs the end-state audit scanned.</summary>
    public required long JobsAudited { get; init; }

    /// <summary>Total store operations the clients issued (all journal entries).</summary>
    public required long TotalOps { get; init; }

    /// <summary>Enqueues that returned <c>Ok</c> (a genuinely new job accepted).</summary>
    public required long EnqueueOk { get; init; }

    /// <summary>Enqueues rejected as duplicate JobId — the collision-pool birth races.</summary>
    public required long DuplicateEnqueueAttempts { get; init; }

    /// <summary>Workflow appends rejected as a duplicate WorkflowId — the shared-workflow races.</summary>
    public required long DuplicateWorkflowAttempts { get; init; }

    /// <summary>Successful claims across all queues.</summary>
    public required long JobsClaimed { get; init; }

    /// <summary>Claims against the statically-limited governed queue (the I3 concurrency-cap surface).</summary>
    public required long GovernedQueueClaims { get; init; }

    /// <summary>Transient store faults the clients rode through (retry-path coverage).</summary>
    public required long TransientFaults { get; init; }

    /// <summary>Per-invariant violation counts. Empty on a clean run.</summary>
    public IReadOnlyDictionary<string, long> FindingsByInvariant { get; init; } = new Dictionary<string, long>();
}

/// <summary>The rolled-up totals for one adapter across every run of it.</summary>
internal sealed record AdapterRollup(
    string Adapter,
    int Runs,
    double HammerSeconds,
    double WallSeconds,
    long JobsAudited,
    long TotalOps,
    long DuplicateEnqueueAttempts,
    long DuplicateWorkflowAttempts,
    long Violations)
{
    public double HammerHours => HammerSeconds / 3600.0;
}

/// <summary>The rolled-up totals across every <see cref="TortureLedgerEntry"/> — the headline numbers.</summary>
internal sealed record TortureLedgerSummary(
    int Runs,
    double TotalHammerSeconds,
    double TotalWallSeconds,
    long TotalJobsAudited,
    long TotalOps,
    long TotalDuplicateEnqueueAttempts,
    long TotalDuplicateWorkflowAttempts,
    IReadOnlyList<AdapterRollup> ByAdapter,
    IReadOnlyDictionary<string, long> FindingsByInvariant)
{
    public double TotalHammerHours => TotalHammerSeconds / 3600.0;
    public double TotalWallHours => TotalWallSeconds / 3600.0;

    /// <summary>Distinct invariants ever tripped across all runs (each is one finding to triage).</summary>
    public int DistinctFindings => FindingsByInvariant.Count;

    /// <summary>Total trip events across all runs.</summary>
    public long TotalFindingEvents => FindingsByInvariant.Values.Sum();
}

/// <summary>
/// The append-only cross-run Torture coverage ledger (a JSONL file, one <see cref="TortureLedgerEntry"/>
/// per line). Every run appends; nothing is ever rewritten, so totals only grow. <see cref="Summarize"/>
/// folds it to the headline and <see cref="ToMarkdown"/> stamps a citable summary.
/// </summary>
internal static class TortureLedger
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    /// <summary>Builds one entry from a finished run's materials (called from the run orchestrator).</summary>
    public static TortureLedgerEntry BuildEntry(
        TortureOptions options,
        string gitSha,
        IReadOnlyList<JournalEntry> journal,
        KeySpace keys,
        long jobsAudited,
        IReadOnlyList<TortureViolation> violations,
        double wallSeconds)
    {
        var findings = violations
            .GroupBy(v => v.Invariant.ToString())
            .ToDictionary(g => g.Key, g => (long)g.Count(), StringComparer.Ordinal);

        return new TortureLedgerEntry
        {
            TimestampUtc = DateTimeOffset.UtcNow.ToString("o"),
            GitSha = gitSha,
            // The adapter SHAPE (not the target's storage name) — SqliteMultiProcess and Sqlite share a
            // SqliteTarget, so keying on the shape keeps the four rows distinct in the per-adapter rollup.
            Adapter = options.Adapter.ToString(),
            Seed = $"0x{options.Seed:x16}",
            DurationSeconds = options.Duration.TotalSeconds,
            WallSeconds = wallSeconds,
            Clients = options.Clients,
            Verdict = violations.Count == 0 ? "clean" : "violation",
            JobsAudited = jobsAudited,
            TotalOps = journal.Count,
            EnqueueOk = journal.Count(e => e.Op == Ops.Enqueue && e.Result == nameof(EnqueueResult.Ok)),
            DuplicateEnqueueAttempts = journal.Count(e => e.Op == Ops.Enqueue && e.Result == nameof(EnqueueResult.Duplicate)),
            DuplicateWorkflowAttempts = journal.Count(e =>
                e.Op == Ops.Workflow && e.Result == nameof(WorkflowEnqueueResult.DuplicateWorkflow)),
            JobsClaimed = journal.Count(e => e.Op == Ops.Claim),
            GovernedQueueClaims = journal.Count(e => e.Op == Ops.Claim && e.Queue == keys.GovernedQueue),
            TransientFaults = journal.Count(e => e.Op == Ops.TransientFault),
            FindingsByInvariant = findings,
        };
    }

    /// <summary>Appends one entry as a single JSON line, creating the file and its directory if needed.</summary>
    public static void Append(string ledgerPath, TortureLedgerEntry entry)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(ledgerPath));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        File.AppendAllText(ledgerPath, JsonSerializer.Serialize(entry, Json) + Environment.NewLine);
    }

    /// <summary>Reads every entry; returns empty if the ledger does not exist yet. Skips blank lines.</summary>
    public static IReadOnlyList<TortureLedgerEntry> Read(string ledgerPath)
    {
        if (!File.Exists(ledgerPath))
        {
            return [];
        }
        return File.ReadLines(ledgerPath)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonSerializer.Deserialize<TortureLedgerEntry>(line, Json)!)
            .ToList();
    }

    /// <summary>Folds the entries to cumulative totals, split per adapter and merged per invariant.</summary>
    public static TortureLedgerSummary Summarize(IReadOnlyList<TortureLedgerEntry> entries)
    {
        var findings = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            foreach (var (id, count) in entry.FindingsByInvariant)
            {
                findings[id] = findings.GetValueOrDefault(id) + count;
            }
        }

        var byAdapter = entries
            .GroupBy(e => e.Adapter, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new AdapterRollup(
                g.Key,
                g.Count(),
                g.Sum(e => e.DurationSeconds),
                g.Sum(e => e.WallSeconds),
                g.Sum(e => e.JobsAudited),
                g.Sum(e => e.TotalOps),
                g.Sum(e => e.DuplicateEnqueueAttempts),
                g.Sum(e => e.DuplicateWorkflowAttempts),
                g.Sum(e => e.FindingsByInvariant.Values.Sum())))
            .ToList();

        return new TortureLedgerSummary(
            entries.Count,
            entries.Sum(e => e.DurationSeconds),
            entries.Sum(e => e.WallSeconds),
            entries.Sum(e => e.JobsAudited),
            entries.Sum(e => e.TotalOps),
            entries.Sum(e => e.DuplicateEnqueueAttempts),
            entries.Sum(e => e.DuplicateWorkflowAttempts),
            byAdapter,
            findings);
    }

    /// <summary>
    /// Renders the summary as a small, citable Markdown document (the tracked <c>docs/torture-coverage.md</c>).
    /// <paramref name="generatedUtc"/> is stamped by the caller.
    /// </summary>
    public static string ToMarkdown(TortureLedgerSummary s, string generatedUtc)
    {
        var lines = new List<string>
        {
            "# Torture Suite coverage ledger",
            "",
            "Cumulative store-mode coverage across every recorded Torture run (issue 0200 / ADR 0039). Unlike the",
            "VOPR ledger's virtual cluster-time, the unit here is REAL wall-clock concurrent load on a live adapter:",
            "hours hammered, jobs audited, store operations exercised, and duplicate-key birth races provoked.",
            "Auto-generated from the local run ledger; do not edit.",
            "",
            $"- **Real load hammered:** {s.TotalHammerHours:N1} hours across {s.Runs:N0} run(s) ({s.TotalWallHours:N1} wall-hours incl. drain + audit)",
            $"- **Jobs audited:** {s.TotalJobsAudited:N0}",
            $"- **Store operations exercised:** {s.TotalOps:N0}",
            $"- **Duplicate-key races provoked:** {s.TotalDuplicateEnqueueAttempts:N0} enqueue, {s.TotalDuplicateWorkflowAttempts:N0} workflow",
            $"- **Distinct invariants surfaced:** {s.DistinctFindings} ({s.TotalFindingEvents:N0} total trip event(s))",
            "",
            "## By adapter",
            "",
            "| Adapter | Runs | Hammer-hrs | Jobs audited | Store ops | Dup races | Violations |",
            "| --- | ---: | ---: | ---: | ---: | ---: | ---: |",
        };
        foreach (var a in s.ByAdapter)
        {
            lines.Add(
                $"| {a.Adapter} | {a.Runs:N0} | {a.HammerHours:N1} | {a.JobsAudited:N0} | {a.TotalOps:N0} "
                + $"| {a.DuplicateEnqueueAttempts + a.DuplicateWorkflowAttempts:N0} | {a.Violations:N0} |");
        }
        lines.Add("");
        if (s.DistinctFindings == 0)
        {
            lines.Add("No oracle has ever tripped across the recorded runs.");
        }
        else
        {
            lines.Add("## Findings");
            lines.Add("");
            lines.Add("| Invariant | Trip events |");
            lines.Add("| --- | ---: |");
            foreach (var (id, count) in s.FindingsByInvariant.OrderByDescending(kvp => kvp.Value))
            {
                lines.Add($"| {id} | {count:N0} |");
            }
        }
        lines.Add("");
        lines.Add($"_Generated {generatedUtc}._");
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }
}
