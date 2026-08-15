using System.Reflection;

namespace BackWave.SchemaGate.Tests;

/// <summary>One shipped schema script: its ordinal (0001 → 1) and its raw, un-rewritten SQL text.</summary>
internal sealed record SchemaScript(int Version, string ResourceName, string Sql);

/// <summary>
/// Reads an adapter's shipped schema scripts straight out of its embedded resources — the same
/// enumeration the real migrator uses (every <c>.sql</c> resource, Ordinal-sorted so 0001 precedes
/// 0002…). Location-independent: no file-system layout assumptions, so it keeps working if the
/// scripts move. The gate diffs consecutive scripts, and since each shipped script is the
/// self-contained incremental migration for one version bump (0002 carries only the v1→v2 delta,
/// 0003 only v2→v3, …), inspecting a single script's own DDL is exactly inspecting the vN-1 → vN
/// change the additive-first contract governs.
/// </summary>
internal static class SchemaScripts
{
    public static IReadOnlyList<SchemaScript> Load(Assembly adapterAssembly)
    {
        var names = adapterAssembly.GetManifestResourceNames()
            .Where(name => name.EndsWith(".sql", StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        var scripts = new List<SchemaScript>(names.Count);
        var ordinal = 0;
        foreach (var name in names)
        {
            using var stream = adapterAssembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            scripts.Add(new SchemaScript(++ordinal, name, reader.ReadToEnd()));
        }
        return scripts;
    }
}
