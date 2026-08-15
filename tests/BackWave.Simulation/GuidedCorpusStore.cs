using System.Security.Cryptography;
using System.Text;

namespace BackWave.Tests.Simulation;

/// <summary>
/// Cross-session persistence for the coverage-guided explorer's <see cref="Corpus"/> (the compounding-depth
/// store): a directory of clean, coverage-advancing <see cref="Plan"/>s, one JSON file per Plan. Distinct from
/// <see cref="PlanStore"/> — that is the bug sink (failing Plans, keyed by <see cref="InvariantId"/>); this holds
/// only clean corpus Plans. Wiring it up lets each guided cycle <see cref="CoverageGuidedSwarm.Seed">reseed</see>
/// from the previous cycle's corpus instead of re-warming from empty (the v1 corpus was in-memory only).
///
/// <para>Files are named by a content hash of the serialized Plan, so re-saving an unchanged corpus is idempotent
/// (same Plan → same filename → no duplicate) and each cycle only appends the Plans it newly discovered. There is
/// no eviction — the corpus grows as coverage grows, and naturally plateaus as coverage saturates.</para>
///
/// <para>Thread-unsafe by contract: <see cref="Seed"/>/save run single-threaded around the worker pool
/// (reseed before <see cref="CoverageGuidedSwarm.Run"/> starts its threads; save after they join), never
/// concurrently with the workers.</para>
/// </summary>
internal sealed class GuidedCorpusStore
{
    private readonly string _dir;

    public GuidedCorpusStore(string dir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dir);
        _dir = dir;
        Directory.CreateDirectory(_dir);
    }

    /// <summary>The directory this store reads corpus Plans from and writes them to.</summary>
    public string Dir => _dir;

    /// <summary>Every persisted corpus Plan, read back from disk (empty if the directory has none).</summary>
    public IReadOnlyList<Plan> LoadAll() =>
        Directory.Exists(_dir)
            ? Directory.EnumerateFiles(_dir, "*.json")
                .OrderBy(p => p, StringComparer.Ordinal)
                .Select(p => PlanJson.Deserialize(File.ReadAllText(p)))
                .ToList()
            : [];

    /// <summary>
    /// Persists <paramref name="plans"/> as the corpus, one file per Plan named by content hash. A Plan whose
    /// file already exists (byte-identical content) is skipped, so saving an accumulated corpus each cycle only
    /// writes the newly discovered Plans rather than rewriting the whole set.
    /// </summary>
    public void SaveAll(IReadOnlyList<Plan> plans)
    {
        ArgumentNullException.ThrowIfNull(plans);
        foreach (var plan in plans)
        {
            var json = PlanJson.Serialize(plan);
            var path = Path.Combine(_dir, ContentName(json));
            if (!File.Exists(path))
            {
                File.WriteAllText(path, json);
            }
        }
    }

    /// <summary>A stable, collision-resistant filename derived from the Plan's serialized content.</summary>
    private static string ContentName(string json) =>
        $"{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)))[..32]}.json";
}
