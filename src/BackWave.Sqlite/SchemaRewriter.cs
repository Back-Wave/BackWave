using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace BackWave.Sqlite;

// Rewrites the adapter's canonical 'backwave' table-name prefix to a configured prefix so a
// deployment can namespace BackWave's tables in a shared SQLite file (ADR 0040). SQLite has no
// schemas, so every table and index is named '{prefix}_…'; this is the single choke point that swaps
// the canonical 'backwave' root for the configured one, in both store queries and the DDL scripts the
// migrator runs.
//
// The default prefix ('backwave') is a pure passthrough: Rewrite returns its argument unchanged, so a
// store using the default pays nothing and emits byte-identical SQL to before this feature existed. A
// custom prefix substitutes on first sight of each distinct query string and caches the result, so
// each query is transformed at most once per store instance. The prefix is validated to a strict
// identifier pattern at construction, so the substituted — and UNQUOTED — identifier can never be an
// injection or quoting hazard: an invalid prefix fails fast rather than reaching the database.
internal sealed partial class SchemaRewriter
{
    internal const string DefaultPrefix = "backwave";

    private const int MaxPrefixLength = 64;

    private readonly string _prefix;
    private readonly bool _isDefault;
    private readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.Ordinal);

    public SchemaRewriter(string tablePrefix)
    {
        if (string.IsNullOrEmpty(tablePrefix) || tablePrefix.Length > MaxPrefixLength
            || !IdentifierPattern().IsMatch(tablePrefix))
        {
            throw new ArgumentException(
                $"'{tablePrefix}' is not a valid SQLite table prefix. Use 1–{MaxPrefixLength} characters: " +
                "a letter or underscore followed by letters, digits, or underscores.",
                nameof(tablePrefix));
        }

        _prefix = tablePrefix;
        _isDefault = string.Equals(tablePrefix, DefaultPrefix, StringComparison.Ordinal);
    }

    // Returns the SQL with the 'backwave' table-name prefix replaced by the configured one. Identity
    // (and free) for the default prefix; cached per distinct query string otherwise.
    public string Rewrite(string sql)
        => _isDefault
            ? sql
            : _cache.GetOrAdd(
                sql,
                static (s, prefix) => s.Replace(DefaultPrefix, prefix, StringComparison.Ordinal),
                _prefix);

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex IdentifierPattern();
}
