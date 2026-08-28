using System.Text.RegularExpressions;

namespace BackWave.Postgres.Tests;

/// <summary>
/// Guards the seam the round-trip budgets are measured through: every statement the Postgres adapter
/// executes goes through one of the counted wrappers in PostgresRoundTrips.
///
/// Without this the budgets are self-deceiving. A new bare <c>command.ExecuteNonQueryAsync(...)</c> in
/// the store is a real round trip that the counter never sees, so the pinned numbers in
/// PostgresRoundTripBudgetTests keep passing while the adapter gets slower - the exact failure those
/// budgets exist to catch. The seam is the only thing standing between the two, and a seam nothing
/// enforces is a convention that survives until the next person who has not read the comment.
///
/// This reads the adapter's source off disk rather than its IL because the fix it wants to provoke is
/// a source edit ("call the wrapper"), and the offending file and line are what makes that fix obvious.
/// It needs no database, so it gates every build of the suite rather than only the runs with Postgres up.
/// </summary>
public sealed class PostgresRoundTripSeamTests
{
    // The Npgsql methods that actually leave for the server. The adapter calling one of these directly
    // is a round trip no budget can see.
    private static readonly Regex BareExecute = new(
        @"\.(ExecuteNonQueryAsync|ExecuteReaderAsync|ExecuteScalarAsync)\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // The only way past this gate, and deliberately a per-call-site one: a statement that belongs to no
    // store operation cannot land in any operation's budget, but that has to be argued for one call at a
    // time. A file-wide or project-wide opt-out would grow to cover the next call added beside it.
    private const string ExemptionMarker = "uncounted round trip:";

    // The wrappers live here, and only here, so this is the one file where calling the driver directly
    // is the point rather than the bug.
    private const string WrapperFile = "PostgresRoundTrips.cs";

    [Fact]
    public void EveryAdapterStatementGoesThroughACountedWrapper()
    {
        var offenders = new List<string>();

        foreach (var file in AdapterSources())
        {
            var lines = File.ReadAllLines(file);
            for (var line = 0; line < lines.Length; line++)
            {
                if (IsComment(lines[line]) || !BareExecute.IsMatch(lines[line]))
                {
                    continue;
                }
                if (IsCountedWrapperBody(file, lines, line) || HasExemption(lines, line))
                {
                    continue;
                }

                offenders.Add($"  {Path.GetFileName(file)}({line + 1}): {lines[line].Trim()}");
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"""
             {offenders.Count} Postgres statement(s) bypass the counted round-trip wrappers, so they are
             invisible to the budgets in PostgresRoundTripBudgetTests:

             {string.Join(Environment.NewLine, offenders)}

             Call ExecuteNonQueryCountedAsync / ExecuteReaderCountedAsync / ExecuteScalarCountedAsync
             instead. If the statement genuinely belongs to no store operation - it runs on its own
             session, outside every operation the budgets measure - put a comment directly above the
             call that starts with "{ExemptionMarker}" and says why.
             """);
    }

    // Every .cs the adapter compiles, build output excluded: obj/ holds generated copies of these same
    // files, which would report each hit twice under a path nobody edits.
    private static IEnumerable<string> AdapterSources()
    {
        var adapter = AdapterDirectory();
        Assert.True(
            adapter is not null,
            $"Postgres adapter sources not found: no directory above {AppContext.BaseDirectory} holds src/BackWave.Postgres.");

        return Directory.EnumerateFiles(adapter!, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .OrderBy(path => path, StringComparer.Ordinal);
    }

    // Found by walking up from the test binary until a directory holds the adapter. Not this file's own
    // [CallerFilePath]: a CI build sets ContinuousIntegrationBuild, which makes the compiler rewrite every
    // embedded source path to a deterministic "/_" root that exists on no disk, so the anchor resolved
    // locally and failed on every CI run. The walk searches for the adapter itself rather than counting
    // directory levels, so it does not move with the TFM or the configuration either.
    private static string? AdapterDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var adapter = Path.Combine(dir.FullName, "src", "BackWave.Postgres");
            if (Directory.Exists(adapter))
            {
                return adapter;
            }
        }

        return null;
    }

    // A wrapper body is a driver call that the line above it already counted. Checking for the count
    // rather than trusting the file keeps a fourth, uncounted helper added to PostgresRoundTrips.cs from
    // inheriting the exemption.
    private static bool IsCountedWrapperBody(string file, string[] lines, int line)
    {
        if (!Path.GetFileName(file).Equals(WrapperFile, StringComparison.Ordinal))
        {
            return false;
        }

        for (var above = line - 1; above >= 0; above--)
        {
            var text = lines[above].Trim();
            if (text.Length == 0)
            {
                continue;
            }
            return text.StartsWith("CountStatement()", StringComparison.Ordinal);
        }

        return false;
    }

    // The marker is looked for across the whole comment block above the call, so the reason is free to
    // run to the length it needs rather than being squeezed onto one line.
    private static bool HasExemption(string[] lines, int line)
    {
        for (var above = line - 1; above >= 0 && IsComment(lines[above]); above--)
        {
            var text = lines[above].Trim();
            var marker = text.IndexOf(ExemptionMarker, StringComparison.Ordinal);
            if (marker >= 0 && text[(marker + ExemptionMarker.Length)..].Trim().Length > 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsComment(string line)
    {
        var text = line.TrimStart();
        return text.StartsWith("//", StringComparison.Ordinal)
            || text.StartsWith("*", StringComparison.Ordinal)
            || text.StartsWith("/*", StringComparison.Ordinal);
    }
}
