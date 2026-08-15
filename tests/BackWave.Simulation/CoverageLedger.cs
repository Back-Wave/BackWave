using System.Text.Json;

namespace BackWave.Tests.Simulation;

/// <summary>
/// One row in the cross-run VOPR coverage ledger: the durable, append-only record of a single discovery run's
/// contribution to total tested coverage. Written by the console at a run's graceful end (gated behind the
/// <c>VOPR_LEDGER</c> path), it captures the honest figures needed to say "we have run the equivalent of XX
/// cluster-years across NN findings" — the summed VIRTUAL (simulated) cluster time, not the per-sim ceiling.
/// </summary>
internal sealed record LedgerEntry
{
    /// <summary>Wall-clock instant the run ended, ISO-8601 UTC. Stamped by the console (the harness has no clock).</summary>
    public required string TimestampUtc { get; init; }

    /// <summary>Discovery engine: <c>random</c>, <c>guided</c>, or <c>radioactive</c>.</summary>
    public required string Mode { get; init; }

    /// <summary>Wall-clock seconds the run took.</summary>
    public required double WallSeconds { get; init; }

    /// <summary>Total simulations executed in the run.</summary>
    public required long Sims { get; init; }

    /// <summary>Summed VIRTUAL (simulated) cluster-seconds across the run's clean sims — the honest coverage figure.</summary>
    public required double VirtualSeconds { get; init; }

    /// <summary>The run's entropy base (hex), so the whole seed stream replays.</summary>
    public required string EntropyBase { get; init; }

    /// <summary>Per-<c>InvariantId</c> trip counts (the run's findings). Empty on a clean run.</summary>
    public IReadOnlyDictionary<string, long> FindingsByInvariant { get; init; } = new Dictionary<string, long>();

    /// <summary>Optional commit the run was built from, for provenance (null if not supplied).</summary>
    public string? GitSha { get; init; }
}

/// <summary>The rolled-up totals across every <see cref="LedgerEntry"/> — the headline numbers.</summary>
internal sealed record LedgerSummary(
    int Runs,
    double TotalWallSeconds,
    long TotalSims,
    double TotalVirtualSeconds,
    IReadOnlyDictionary<string, long> FindingsByInvariant)
{
    // The Gregorian mean year (365.2425 d) in seconds — the divisor turning virtual-seconds into "cluster-years".
    private const double SecondsPerYear = 31_556_952.0;

    public double EquivalentYears => TotalVirtualSeconds / SecondsPerYear;
    public double TotalWallHours => TotalWallSeconds / 3600.0;

    /// <summary>Distinct invariants ever tripped across all runs (each is one finding to triage).</summary>
    public int DistinctFindings => FindingsByInvariant.Count;

    /// <summary>Total trip events across all runs (≥ <see cref="DistinctFindings"/> when an invariant repeats).</summary>
    public long TotalFindingEvents => FindingsByInvariant.Values.Sum();
}

/// <summary>
/// The append-only cross-run coverage ledger (a JSONL file, one <see cref="LedgerEntry"/> per line). It is the
/// source of truth for cumulative VOPR coverage: every run appends, nothing is ever rewritten, so the totals only
/// grow. <see cref="Summarize"/> folds it to the headline and <see cref="ToMarkdown"/> stamps a citable summary.
/// </summary>
internal static class CoverageLedger
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    /// <summary>Appends one entry as a single JSON line, creating the file and its directory if needed.</summary>
    public static void Append(string ledgerPath, LedgerEntry entry)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(ledgerPath));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        File.AppendAllText(ledgerPath, JsonSerializer.Serialize(entry, Json) + Environment.NewLine);
    }

    /// <summary>Reads every entry; returns empty if the ledger does not exist yet. Skips blank lines.</summary>
    public static IReadOnlyList<LedgerEntry> Read(string ledgerPath)
    {
        if (!File.Exists(ledgerPath))
        {
            return [];
        }
        return File.ReadLines(ledgerPath)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonSerializer.Deserialize<LedgerEntry>(line, Json)!)
            .ToList();
    }

    /// <summary>Folds the entries to cumulative totals, merging per-invariant finding counts.</summary>
    public static LedgerSummary Summarize(IReadOnlyList<LedgerEntry> entries)
    {
        var findings = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            foreach (var (id, count) in entry.FindingsByInvariant)
            {
                findings[id] = findings.GetValueOrDefault(id) + count;
            }
        }
        return new LedgerSummary(
            entries.Count,
            entries.Sum(e => e.WallSeconds),
            entries.Sum(e => e.Sims),
            entries.Sum(e => e.VirtualSeconds),
            findings);
    }

    /// <summary>
    /// Renders the summary as a small, citable Markdown document (the tracked <c>docs/vopr-coverage.md</c>).
    /// <paramref name="generatedUtc"/> is stamped by the caller (the harness has no clock).
    /// </summary>
    public static string ToMarkdown(LedgerSummary s, string generatedUtc)
    {
        var lines = new List<string>
        {
            "# VOPR coverage ledger",
            "",
            "Cumulative deterministic-simulation coverage across every recorded VOPR run. \"Equivalent cluster-time\"",
            "is the sum of each simulation's actual *virtual* (simulated) cluster time — not a per-sim ceiling — so a",
            "converging run counts only the time it truly spanned. Auto-generated from the local run ledger; do not edit.",
            "",
            $"- **Equivalent cluster-time tested:** {s.EquivalentYears:N1} years ({s.TotalVirtualSeconds / 3600.0:N0} cluster-hours)",
            $"- **Simulations:** {s.TotalSims:N0} across {s.Runs:N0} run(s)",
            $"- **Wall-clock invested:** {s.TotalWallHours:N1} hours",
            $"- **Distinct invariants surfaced:** {s.DistinctFindings} ({s.TotalFindingEvents:N0} total trip event(s))",
            "",
        };
        if (s.DistinctFindings == 0)
        {
            lines.Add("No invariant has ever tripped across the recorded runs.");
        }
        else
        {
            lines.Add("| Invariant | Trip events |");
            lines.Add("| --- | --- |");
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
