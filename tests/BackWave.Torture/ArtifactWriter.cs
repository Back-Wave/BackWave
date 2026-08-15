using System.Text.Json;
using BackWave.Storage;

namespace BackWave.Torture;

/// <summary>
/// On violation, dumps the full artifact bundle: run options + seed, the merged ops journal, the
/// violations, a store-surface dump (every job row + its Transition Log + queue settings +
/// workflows), and the target's raw dump (raw table JSON, or the SQLite file itself). Repro is
/// best-effort by design; the bundle is what makes hand-diagnosis possible. File findings as
/// torture-NNNN (ADR 0039).
/// </summary>
internal static class ArtifactWriter
{
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    public static async Task<string> WriteAsync(
        TortureOptions options,
        IReadOnlyList<JournalEntry> journal,
        IReadOnlyList<TortureViolation> violations,
        Auditor auditor,
        ITortureTarget target,
        WorkloadStats stats,
        CancellationToken cancellationToken)
    {
        var dir = Path.Combine(
            options.ArtifactsDir,
            $"torture-{target.Name}-{options.Seed:x16}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}");
        Directory.CreateDirectory(dir);

        await File.WriteAllTextAsync(Path.Combine(dir, "run.json"), JsonSerializer.Serialize(new
        {
            Adapter = target.Name,
            Seed = $"0x{options.Seed:x16}",
            SeedDecimal = options.Seed,
            options.Clients,
            options.Processes,
            options.MaxAttempts,
            options.GovernedLimit,
            DurationSeconds = options.Duration.TotalSeconds,
            DrainBoundSeconds = options.DrainBound.TotalSeconds,
            Stats = stats.Snapshot(),
        }, Pretty), cancellationToken);

        await File.WriteAllTextAsync(
            Path.Combine(dir, "violations.json"), JsonSerializer.Serialize(violations, Pretty), cancellationToken);

        var ordered = journal.OrderBy(e => e.T0).ToList();
        var merged = new Journal();
        foreach (var entry in ordered)
        {
            merged.Record(entry);
        }
        await merged.WriteAsync(Path.Combine(dir, "journal.jsonl"));

        await File.WriteAllTextAsync(Path.Combine(dir, "store-dump.json"), JsonSerializer.Serialize(new
        {
            Jobs = auditor.ScannedJobs.Select(j => new
            {
                j.JobId, j.WireName, j.Queue, State = j.State.ToString(), j.Attempt,
                j.DueTime, j.LeaseOwner, j.LeaseExpiry, j.CancelRequested, j.TerminalAt, j.TerminalCause,
                j.ParentsRemaining, j.Sequence, j.WorkflowId,
                Tags = j.Tags.Select(t => t.Key.Length == 0 ? t.Value : $"{t.Key}={t.Value}"),
                History = auditor.Histories.TryGetValue(j.JobId, out var h)
                    ? h.Select(t => new { t.Ordinal, t.Timestamp, State = t.State.ToString(), t.Attempt, t.FailureDetail })
                    : null,
            }),
        }, Pretty), cancellationToken);

        try
        {
            await target.RawDumpAsync(dir, cancellationToken);
        }
        catch (Exception exception)
        {
            await File.WriteAllTextAsync(
                Path.Combine(dir, "raw-dump-failed.txt"), exception.ToString(), cancellationToken);
        }

        return dir;
    }
}

/// <summary>Coverage stats a clean run reports: ops issued per kind, results, and collisions engineered.</summary>
internal sealed class WorkloadStats(IReadOnlyList<JournalEntry> journal, KeySpace keys)
{
    public Dictionary<string, object> Snapshot()
    {
        var byOp = journal.GroupBy(e => e.Op).ToDictionary(g => g.Key, g => g.Count());
        var enqueues = journal.Where(e => e.Op == Ops.Enqueue).ToList();
        var claims = journal.Where(e => e.Op == Ops.Claim).ToList();

        return new Dictionary<string, object>
        {
            ["opsByKind"] = byOp,
            ["enqueueResults"] = Results(enqueues),
            ["outcomeResults"] = Results(journal.Where(e => e.Op == Ops.Outcome)),
            ["workflowResults"] = Results(journal.Where(e => e.Op == Ops.Workflow)),
            ["cancelResults"] = Results(journal.Where(e => e.Op == Ops.Cancel)),
            ["requeueResults"] = Results(journal.Where(e => e.Op == Ops.Requeue)),
            ["jobsClaimed"] = claims.Count,
            ["governedQueueClaims"] = claims.Count(e => e.Queue == keys.GovernedQueue),
            ["collisions"] = new Dictionary<string, int>
            {
                ["duplicateEnqueueAttempts"] = enqueues.Count(e => e.Result == nameof(EnqueueResult.Duplicate)),
                ["duplicateWorkflowAttempts"] = journal.Count(e =>
                    e.Op == Ops.Workflow && e.Result == nameof(WorkflowEnqueueResult.DuplicateWorkflow)),
                ["duplicateWorkflowMembers"] = journal.Count(e =>
                    e.Op == Ops.Workflow && e.Result == nameof(WorkflowEnqueueResult.DuplicateMember)),
                ["configQueueOps"] = journal.Count(e =>
                    e.Op is Ops.Pause or Ops.Resume or Ops.Limit),
                ["transientFaults"] = journal.Count(e => e.Op == Ops.TransientFault),
            },
        };
    }

    public string Render()
    {
        var snapshot = Snapshot();
        var lines = new List<string>();
        foreach (var (key, value) in snapshot)
        {
            if (value is Dictionary<string, int> dict)
            {
                lines.Add($"  {key}:");
                lines.AddRange(dict.OrderByDescending(kv => kv.Value).Select(kv => $"    {kv.Key}: {kv.Value}"));
            }
            else
            {
                lines.Add($"  {key}: {value}");
            }
        }
        return string.Join(Environment.NewLine, lines);
    }

    private static Dictionary<string, int> Results(IEnumerable<JournalEntry> entries)
        => entries.Where(e => e.Result is not null)
            .GroupBy(e => e.Result!)
            .ToDictionary(g => g.Key, g => g.Count());
}
