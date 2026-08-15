using BackWave.Jobs;

namespace BackWave.Testing;

/// <summary>
/// The Job Manifest snapshot helper: a committed file recording every registered Wire Name
/// and its payload type. Removing or renaming a Wire Name fails the verifying test — a
/// wire-format break caught in PR review, not production. Additive registration passes and
/// rewrites the manifest, so the new entry shows up in the PR diff.
/// </summary>
public static class JobManifest
{
    /// <summary>One line per registration, sorted by Wire Name — the canonical manifest form.</summary>
    public static IReadOnlyList<string> Render(JobRegistry registry)
        => [.. registry.Registrations.Select(r => $"{r.WireName} => {r.JobType.FullName}")];

    /// <summary>
    /// Verifies the registry against the manifest file at <paramref name="manifestPath"/>.
    /// Creates the file on first run; rewrites it on additive change; throws when a
    /// previously recorded Wire Name is missing or its payload type changed.
    /// </summary>
    public static void Verify(JobRegistry registry, string manifestPath)
    {
        var current = Render(registry);

        if (!File.Exists(manifestPath))
        {
            File.WriteAllLines(manifestPath, current);
            return;
        }

        var recorded = File.ReadAllLines(manifestPath)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();

        var broken = recorded.Except(current, StringComparer.Ordinal).ToList();
        if (broken.Count > 0)
        {
            throw new InvalidOperationException(
                "Job Manifest break: these recorded Wire Names are no longer registered (removed, " +
                "renamed, or their payload type changed) — in-flight jobs under them would quarantine. " +
                "Breaking payload changes get a new Wire Name; keep the old handler until drained.\n  " +
                string.Join("\n  ", broken));
        }

        if (current.Except(recorded, StringComparer.Ordinal).Any())
        {
            File.WriteAllLines(manifestPath, current); // additive: record it, visible in the diff
        }
    }
}
