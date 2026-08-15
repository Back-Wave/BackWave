using System.Collections.Concurrent;

namespace BackWave.Tests.Simulation;

/// <summary>
/// The working corpus on disk (PRD 0004, ADR 0018): a directory of failing <see cref="Plan"/>s, one JSON
/// file per <see cref="InvariantId"/>. The VOPR Runner (issue 0087) writes the FIRST failure of each ID and
/// the regression fixtures (issue 0089) read them back. Dedup is by invariant ID — the key the Seed
/// Minimizer and the Runner already match "same failure" on (never message text, Seed, or step count) — so
/// a chaos run that trips one bug a thousand ways leaves exactly one file.
///
/// Thread-safe: the Runner's <see cref="Environment.ProcessorCount"/> workers share one store. First-write
/// wins per ID; concurrent same-ID attempts persist once. The corpus is a working artifact, never checked
/// in (the runner points it at an out-of-tree path; tests use a temp dir).
/// </summary>
internal sealed class PlanStore
{
    private readonly string _corpusDir;

    // Tracks which invariant IDs have already been persisted, so concurrent workers dedup without re-writing.
    private readonly ConcurrentDictionary<InvariantId, byte> _persisted = new();

    public PlanStore(string corpusDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(corpusDir);
        _corpusDir = corpusDir;
        Directory.CreateDirectory(_corpusDir);
    }

    /// <summary>The corpus directory this store writes to and reads from.</summary>
    public string CorpusDir => _corpusDir;

    /// <summary>True once a Plan for <paramref name="id"/> has been persisted by this store.</summary>
    public bool Has(InvariantId id) => _persisted.ContainsKey(id);

    /// <summary>
    /// Persists <paramref name="plan"/> as the canonical repro for its failure ID — but only the FIRST time
    /// that ID is seen. Returns true if this call wrote the file (a new unique failure), false if the ID was
    /// already persisted (a deduped repeat the caller should tally, not re-write). Requires a failing Plan.
    /// </summary>
    public bool Save(Plan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var id = plan.Failure?.InvariantId
            ?? throw new ArgumentException("PlanStore only persists failing Plans (Failure.InvariantId required).", nameof(plan));

        // First-write-wins per ID. TryAdd is the dedup gate; only the winner touches disk.
        if (!_persisted.TryAdd(id, 0))
        {
            return false;
        }

        File.WriteAllText(PathFor(id), PlanJson.Serialize(plan));
        return true;
    }

    /// <summary>The on-disk path a Plan for <paramref name="id"/> is stored at (named stably by ID).</summary>
    public string PathFor(InvariantId id) => Path.Combine(_corpusDir, $"{id}.json");

    /// <summary>Reads a single Plan JSON file back, full-fidelity (PlanJson round-trips every knob).</summary>
    public static Plan Load(string path) => PlanJson.Deserialize(File.ReadAllText(path));

    /// <summary>Every persisted Plan in the corpus, read back from disk.</summary>
    public IReadOnlyList<Plan> LoadAll() =>
        Directory.Exists(_corpusDir)
            ? Directory.EnumerateFiles(_corpusDir, "*.json").OrderBy(p => p, StringComparer.Ordinal).Select(Load).ToList()
            : [];
}
