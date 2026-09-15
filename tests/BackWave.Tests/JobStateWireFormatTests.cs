using System.Text.RegularExpressions;
using BackWave.Storage;

namespace BackWave.Tests;

// JobState's numbers are the storage wire format: every adapter writes (int)state into an int column, and
// the claim and lease-expiry partial indexes hard-code the number in their predicates. No other gate is
// asymmetric enough to catch a renumber - the schema-diff gate sees no schema change because the column
// stays int, and the upgrade harness and the conformance suite both write and read with the same code, so
// both sides agree on the wrong number. These tests are the asymmetric side: they hold the numbers that
// rows already sitting in customer databases were written with.

public class JobStateWireFormatTests
{
    // The number each member has been persisted as since the first shipped schema. A row written before
    // this test existed carries these and nothing rewrites it, so an entry only ever changes alongside a
    // migration that rewrites the rows.
    private static readonly Dictionary<string, int> PersistedValues = new(StringComparer.Ordinal)
    {
        [nameof(JobState.Scheduled)] = 0,
        [nameof(JobState.AwaitingParent)] = 1,
        [nameof(JobState.Leased)] = 2,
        [nameof(JobState.Succeeded)] = 3,
        [nameof(JobState.Cancelled)] = 4,
        [nameof(JobState.DeadLettered)] = 5,
        [nameof(JobState.Quarantined)] = 6,
    };

    // The partial indexes whose predicates filter on a JobState number, and the state each one filters on.
    // The .sql files are the only sites that cannot name the member - they are static embedded resources,
    // so no cast can stand in for the literal and this test is the only guard available to them.
    private static readonly Dictionary<string, JobState> IndexPredicates = new(StringComparer.Ordinal)
    {
        ["ix_backwave_jobs_claim"] = JobState.Scheduled,
        ["ix_backwave_jobs_leased_queue"] = JobState.Leased,
        ["ix_backwave_jobs_lease_owner"] = JobState.Leased,
    };

    // Postgres, SQL Server, and SQLite each carry the claim and leased-queue predicates, SQL Server carries
    // the lease-owner one as well, and Oracle carries none, because it has no partial index. Pinned so that
    // dropping a predicate, or adding an adapter that needs one, is a deliberate edit here rather than a
    // silent loss of coverage.
    private const int GuardedPredicateCount = 7;

    // The `-- States: 0 Scheduled, ...` gloss each schema carries above its jobs table, which is the one
    // comment that has to spell the numbers out: it is the only documentation a DBA reading the canonical
    // schema artifact gets for an int column. Matched only inside the gloss, so an unrelated enum's own
    // ordinals are never read as JobState's.
    private static readonly Regex OrdinalGloss = new(@"\b(\d+)\s+([A-Za-z]\w*)", RegexOptions.Compiled);
    private static readonly Regex StateLiteral = new(@"\bstate\s*=\s*(\d+)", RegexOptions.Compiled);
    private static readonly Regex IndexName = new(@"\bCREATE\s+INDEX\s+(?:IF\s+NOT\s+EXISTS\s+)?(\w+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    [Fact]
    public void EveryMember_KeepsThePersistedNumberItsRowsWereWrittenWith()
    {
        var renumbered = PersistedValues
            .Where(pin => (int)Enum.Parse<JobState>(pin.Key) != pin.Value)
            .Select(pin => $"JobState.{pin.Key} is now {(int)Enum.Parse<JobState>(pin.Key)}, but its rows were written as {pin.Value}")
            .ToList();

        Assert.True(
            renumbered.Count == 0,
            $"""
             {renumbered.Count} JobState member(s) were renumbered:

             {string.Join(Environment.NewLine, renumbered)}

             A JobState number is a persisted wire format, not an implementation detail: every adapter
             writes (int)state into an int column, and the claim and lease-expiry partial indexes filter on
             the number. Renumbering migrates nothing - every row already in a customer database reads back
             as a different state, and those two indexes stop matching the rows the claim path needs.

             Restore the numbers above. To add a state, give it the next free number and add it here; to
             retire one, leave its number reserved rather than reusing it.
             """);
    }

    [Fact]
    public void EveryMember_IsPinned_SoANewOneCannotShipUnguarded()
    {
        var unpinned = Enum.GetNames<JobState>()
            .Where(name => !PersistedValues.ContainsKey(name))
            .ToList();
        var stale = PersistedValues.Keys
            .Where(name => !Enum.IsDefined(typeof(JobState), name))
            .ToList();

        Assert.True(
            unpinned.Count == 0 && stale.Count == 0,
            $"""
             The pinned set no longer matches JobState.

             Not pinned: {(unpinned.Count == 0 ? "(none)" : string.Join(", ", unpinned))}
             Pinned but gone: {(stale.Count == 0 ? "(none)" : string.Join(", ", stale))}

             A JobState number is a persisted wire format, so every member needs an entry in
             PersistedValues - an unpinned member is one that can be renumbered without a test noticing.
             """);
    }

    [Fact]
    public void EverySchemaPredicate_FiltersOnTheNumberItsStateStillHas()
    {
        var offenders = new List<string>();
        var guarded = 0;
        var source = SourceDirectory();

        foreach (var file in SchemaFiles())
        {
            var lines = File.ReadAllLines(file);
            for (var line = 0; line < lines.Length; line++)
            {
                var match = StateLiteral.Match(lines[line]);
                if (!match.Success)
                {
                    continue;
                }

                var literal = int.Parse(match.Groups[1].Value);
                var where = $"{Path.GetRelativePath(source, file)}:{line + 1}";

                if (lines[line].TrimStart().StartsWith("--", StringComparison.Ordinal))
                {
                    offenders.Add($"{where}: a comment spells a JobState number out, which goes stale with nothing to catch it - name the state instead");
                    continue;
                }

                var index = OwningIndex(lines, line);
                if (index is null || !IndexPredicates.TryGetValue(index, out var expected))
                {
                    offenders.Add($"{where}: 'state = {literal}' belongs to no pinned index ({index ?? "no CREATE INDEX above it"}) - add it to IndexPredicates");
                    continue;
                }

                guarded++;
                if (literal != (int)expected)
                {
                    offenders.Add($"{where}: {index} filters on state = {literal}, but JobState.{expected} is {(int)expected}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0 && guarded == GuardedPredicateCount,
            $"""
             The shipped schemas no longer agree with JobState ({guarded} of {GuardedPredicateCount} predicates guarded):

             {(offenders.Count == 0 ? "(no mismatched literals)" : string.Join(Environment.NewLine, offenders))}

             A JobState number is a persisted wire format and a .sql file cannot name the member, so these
             literals are the one place the number is copied by hand. A predicate that filters on the wrong
             number costs no correctness - it costs the index: the claim path falls back to scanning the
             live table.
             """);
    }

    [Fact]
    public void EverySchemaOrdinalGloss_NamesTheStateItsNumberStillHas()
    {
        var offenders = new List<string>();
        var source = SourceDirectory();

        foreach (var file in SchemaFiles())
        {
            var lines = File.ReadAllLines(file);
            var gloss = false;
            for (var line = 0; line < lines.Length; line++)
            {
                var text = lines[line].TrimStart();
                if (!text.StartsWith("--", StringComparison.Ordinal))
                {
                    gloss = false;
                    continue;
                }

                if (text.Contains("States:", StringComparison.Ordinal))
                {
                    gloss = true;
                }
                else if (!gloss)
                {
                    continue;
                }

                var where = $"{Path.GetRelativePath(source, file)}:{line + 1}";
                foreach (Match match in OrdinalGloss.Matches(text))
                {
                    var named = match.Groups[2].Value;
                    var literal = int.Parse(match.Groups[1].Value);

                    if (!PersistedValues.ContainsKey(named))
                    {
                        offenders.Add($"{where}: the states gloss reads '{match.Value}', but {named} is not a JobState member");
                        continue;
                    }

                    var actual = (int)Enum.Parse<JobState>(named);
                    if (literal != actual)
                    {
                        offenders.Add($"{where}: the states gloss reads '{match.Value}', but JobState.{named} is {actual}");
                    }
                }

                // The gloss is one sentence, so it ends where that sentence does.
                if (text.TrimEnd().EndsWith(".", StringComparison.Ordinal))
                {
                    gloss = false;
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"""
             The states gloss in {offenders.Count} schema line(s) no longer agrees with JobState:

             {string.Join(Environment.NewLine, offenders)}

             The gloss is what a DBA reads to interpret the int column, and it is the only place a number
             is written without the `state = N` form the predicate guard catches. Correct the gloss, or
             drop the numbers from it and leave the names.
             """);
    }

    // The CREATE INDEX that owns the predicate line: a predicate always trails its own statement, so the
    // walk stops at the statement above rather than reaching back and adopting an unrelated index.
    private static string? OwningIndex(string[] lines, int line)
    {
        for (var above = line; above >= 0; above--)
        {
            var match = IndexName.Match(lines[above]);
            if (match.Success)
            {
                return match.Groups[1].Value;
            }

            if (above < line && lines[above].Contains(';', StringComparison.Ordinal))
            {
                return null;
            }
        }

        return null;
    }

    // Every shipped schema, so an adapter added later is scanned without being listed here. Found by
    // walking up from the test binary until a directory holds src/, not by this file's [CallerFilePath]:
    // a CI build sets ContinuousIntegrationBuild, which rewrites every embedded source path to a
    // deterministic "/_" root that exists on no disk.
    private static IEnumerable<string> SchemaFiles()
    {
        var files = Directory.EnumerateFiles(SourceDirectory(), "*.sql", SearchOption.AllDirectories)
            .Where(path => path.Contains($"{Path.DirectorySeparatorChar}Schema{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(files);
        return files;
    }

    private static string SourceDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var source = Path.Combine(dir.FullName, "src");
            if (Directory.Exists(Path.Combine(source, "BackWave")))
            {
                return source;
            }
        }

        throw new DirectoryNotFoundException(
            $"BackWave sources not found: no directory above {AppContext.BaseDirectory} holds src/BackWave.");
    }
}
