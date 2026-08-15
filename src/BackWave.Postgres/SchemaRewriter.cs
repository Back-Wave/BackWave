using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace BackWave.Postgres;

// Rewrites the adapter's canonical 'backwave' schema qualifier to a configured schema name so a
// deployment can place BackWave's objects under a custom Postgres schema (ADR 0040). Every query in
// the store — and every DDL script the migrator runs — is authored against the literal 'backwave'
// schema; this is the single choke point that swaps in the configured name.
//
// The default name ('backwave') is a pure passthrough: Rewrite returns its argument unchanged, so a
// store using the default pays nothing and emits byte-identical SQL to before this feature existed. A
// custom name substitutes on first sight of each distinct query string and caches the result, so each
// query is transformed at most once per store instance. The name is validated to a strict identifier
// pattern at construction, so the substituted — and UNQUOTED — identifier can never be an injection or
// quoting hazard: an invalid name fails fast rather than reaching the database.
internal sealed partial class SchemaRewriter
{
    internal const string DefaultSchema = "backwave";

    // Postgres truncates identifiers at 63 bytes; reject longer names rather than silently colliding.
    private const int MaxSchemaLength = 63;

    private readonly string _schema;
    private readonly bool _isDefault;
    private readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.Ordinal);

    public SchemaRewriter(string schemaName)
    {
        if (string.IsNullOrEmpty(schemaName) || schemaName.Length > MaxSchemaLength
            || !IdentifierPattern().IsMatch(schemaName))
        {
            throw new ArgumentException(
                $"'{schemaName}' is not a valid Postgres schema name. Use 1–{MaxSchemaLength} characters: " +
                "a letter or underscore followed by letters, digits, or underscores.",
                nameof(schemaName));
        }

        _schema = schemaName;
        _isDefault = string.Equals(schemaName, DefaultSchema, StringComparison.Ordinal);
        HintChannel = _isDefault ? "backwave_hints" : schemaName + "_hints";
    }

    // The LISTEN/NOTIFY channel for Wake-Up Hints, namespaced to the schema so two BackWave
    // deployments in one database never cross-talk. Default schema keeps the historical channel name.
    public string HintChannel { get; }

    // Returns the SQL with the 'backwave' schema qualifier replaced by the configured name. Identity
    // (and free) for the default schema; cached per distinct query string otherwise.
    public string Rewrite(string sql)
        => _isDefault
            ? sql
            : _cache.GetOrAdd(
                sql,
                static (s, schema) => s.Replace(DefaultSchema, schema, StringComparison.Ordinal),
                _schema);

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex IdentifierPattern();
}
