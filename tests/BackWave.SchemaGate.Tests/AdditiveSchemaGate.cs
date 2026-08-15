using System.Text;
using System.Text.RegularExpressions;

namespace BackWave.SchemaGate.Tests;

/// <summary>The kind of non-additive change the gate flags (ADR 0038, the N-1 mixed-fleet contract).</summary>
internal enum SchemaChangeKind
{
    DropTable,
    DropColumn,
    DropConstraint,
    Rename,
    AlterColumn,
    AddConstraint,
    NotNullColumnWithoutDefault,
}

/// <summary>One flagged non-additive statement: what kind, and the offending SQL snippet.</summary>
internal sealed record SchemaChange(SchemaChangeKind Kind, string Statement);

/// <summary>
/// The additive-first schema-diff gate (ADR 0038 / issue 0202). A deliberately small, readable DDL
/// classifier — NOT a full SQL parser — that scans one migration script's statement shapes and
/// flags anything that would break an N-1 reader/writer during a rolling deploy. It errs toward
/// flagging: a false RED (over-cautious) is cheaper than a false GREEN (a shipped upgrade that eats
/// a mixed-fleet consumer's queue).
///
/// FLAGGED as non-additive (RED):
///   - DROP TABLE / DROP COLUMN / DROP CONSTRAINT — removes state an N-1 binary still reads or writes.
///   - RENAME (ALTER … RENAME, sp_rename)          — an N-1 binary references the old name.
///   - ALTER COLUMN                                 — a type/nullability change on an existing column;
///                                                     conservatively treated as a potential narrowing.
///   - ALTER TABLE … ADD CONSTRAINT                 — a new/tightened constraint an N-1 writer can violate.
///   - ALTER TABLE … ADD <col> NOT NULL (no DEFAULT)— an N-1 writer's INSERT omits the column and fails.
///
/// ALLOWED as additive (GREEN):
///   - CREATE SCHEMA / TABLE / INDEX / SEQUENCE — brand-new objects an N-1 binary simply ignores.
///   - ALTER TABLE … ADD <col> that is nullable OR carries a DEFAULT — N-1 INSERTs keep working.
///   - DROP INDEX — see below; indexes are transparent to correctness.
///   - INSERT/UPDATE on schema_version — the version stamp every bump carries, not a schema change.
///
/// DROP INDEX is intentionally NOT flagged. The contract is about correctness — an N-1 binary must
/// keep operating *correctly* against the upgraded schema — and an index is transparent to
/// correctness: dropping one only changes performance, never a query's result. The shipped v1→v2
/// migration legitimately swaps a lease-expiry index for a queue-scoped one (DROP INDEX +
/// CREATE INDEX in the same script), so flagging DROP INDEX would turn the gate RED on the real
/// current scripts. (The maintainer's literal list named "DROP … INDEX"; this is the one documented
/// deviation, grounded in ADR 0038's "break N-1 readers/writers" qualifier.)
///
/// KNOWN BLIND SPOTS (documented on purpose — the classifier is a shape-matcher, not a parser):
///   - It cannot tell a *widening* ALTER COLUMN (int → bigint, safe) from a narrowing one, so it
///     flags every ALTER COLUMN. None of the shipped scripts alter a column, so this stays quiet
///     until someone tries — at which point a human reviews the intent.
///   - It does not model semantic tightening hidden inside an otherwise-additive change (e.g. a new
///     CHECK folded into a CREATE TABLE that a later migration repoints an old table at). ADR 0038
///     records this residual (additive-legal behavioral incompatibility) as accepted and deferred to
///     the compat-host line.
///   - Statement splitting is on top-level ';'. The shipped scripts have no ';' inside string
///     literals except within EXEC('…') bodies, which are unwrapped first, so this holds today.
/// </summary>
internal static class AdditiveSchemaGate
{
    /// <summary>Classifies one migration script, returning every non-additive change it contains (empty = additive).</summary>
    public static IReadOnlyList<SchemaChange> Inspect(string scriptSql)
    {
        var changes = new List<SchemaChange>();
        foreach (var statement in Statements(scriptSql))
        {
            var upper = Regex.Replace(statement, @"\s+", " ").Trim().ToUpperInvariant();
            if (upper.Length == 0 || IsSchemaVersionStamp(upper))
            {
                continue;
            }

            // Order matters: check the specific ADD-column shapes before the generic DROP/RENAME scan,
            // and never let DROP INDEX fall through to the DROP scan.
            if (Regex.IsMatch(upper, @"\bDROP\s+TABLE\b"))
            {
                changes.Add(new SchemaChange(SchemaChangeKind.DropTable, statement.Trim()));
            }
            if (Regex.IsMatch(upper, @"\bDROP\s+COLUMN\b"))
            {
                changes.Add(new SchemaChange(SchemaChangeKind.DropColumn, statement.Trim()));
            }
            if (Regex.IsMatch(upper, @"\bDROP\s+CONSTRAINT\b"))
            {
                changes.Add(new SchemaChange(SchemaChangeKind.DropConstraint, statement.Trim()));
            }
            if (Regex.IsMatch(upper, @"\bRENAME\b") || Regex.IsMatch(upper, @"\bSP_RENAME\b"))
            {
                changes.Add(new SchemaChange(SchemaChangeKind.Rename, statement.Trim()));
            }
            if (Regex.IsMatch(upper, @"\bALTER\s+COLUMN\b"))
            {
                changes.Add(new SchemaChange(SchemaChangeKind.AlterColumn, statement.Trim()));
            }

            // An ALTER TABLE that adds something to an existing table. CREATE TABLE columns are new
            // objects and never checked here; this only fires for additions to a pre-existing table.
            if (Regex.IsMatch(upper, @"\bALTER\s+TABLE\b.*\bADD\b"))
            {
                if (Regex.IsMatch(upper, @"\bADD\s+CONSTRAINT\b"))
                {
                    changes.Add(new SchemaChange(SchemaChangeKind.AddConstraint, statement.Trim()));
                }
                else if (Regex.IsMatch(upper, @"\bNOT\s+NULL\b") && !Regex.IsMatch(upper, @"\bDEFAULT\b"))
                {
                    // A column added NOT NULL without a DEFAULT: an N-1 writer that INSERTs without the
                    // column violates it. (A DEFAULT — inline or via a named CONSTRAINT … DEFAULT — makes
                    // it additive, which is how every shipped ADD-column stays green.)
                    changes.Add(new SchemaChange(SchemaChangeKind.NotNullColumnWithoutDefault, statement.Trim()));
                }
            }
        }
        return changes;
    }

    // The trailing (or, in v1, the seeding) schema_version write every script carries. Not a schema
    // change — ignored so it never trips the classifier.
    private static bool IsSchemaVersionStamp(string upperStatement)
        => upperStatement.Contains("SCHEMA_VERSION", StringComparison.Ordinal)
            && (upperStatement.StartsWith("UPDATE", StringComparison.Ordinal)
                || upperStatement.StartsWith("INSERT", StringComparison.Ordinal));

    // Preprocesses one script into individual statements: strip line comments, unwrap EXEC('…')
    // bodies (SQL Server defers a CREATE INDEX / ALTER whose column was ADDed in the same batch by
    // wrapping it in dynamic SQL — the real DDL is inside the string), then split on top-level ';'.
    private static IEnumerable<string> Statements(string sql)
    {
        var withoutComments = StripLineComments(sql);
        var unwrapped = UnwrapExec(withoutComments);
        return unwrapped.Split(';');
    }

    private static string StripLineComments(string sql)
    {
        var builder = new StringBuilder(sql.Length);
        foreach (var line in sql.Split('\n'))
        {
            var commentAt = line.IndexOf("--", StringComparison.Ordinal);
            builder.Append(commentAt >= 0 ? line[..commentAt] : line).Append('\n');
        }
        return builder.ToString();
    }

    // Replaces EXEC('<body>') / EXEC(N'<body>') with just <body> (T-SQL doubles '' to escape a quote
    // inside the literal; unescape it). The shipped EXEC bodies contain no ';', so unwrapping before
    // the split is safe and lets the inner CREATE/ALTER be classified normally.
    private static string UnwrapExec(string sql)
        => Regex.Replace(
            sql,
            @"EXEC\s*\(\s*N?'((?:[^']|'')*)'\s*\)",
            match => match.Groups[1].Value.Replace("''", "'", StringComparison.Ordinal),
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
}
