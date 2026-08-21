using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace BackWave.Oracle;

// Rewrites the adapter's canonical 'backwave' schema qualifier to a configured schema name so a
// deployment can place BackWave's objects under a custom Oracle schema. Every query in the
// store - and every DDL script the migrator runs - is authored against the literal 'backwave' schema;
// this is the single choke point that swaps in the configured name. On Oracle a schema is the owning
// user, so the configured name is the user that owns the tables.
//
// The default name ('backwave') is a pure passthrough: Rewrite returns its argument unchanged, so a
// store using the default pays nothing and emits byte-identical SQL. A custom name substitutes on first
// sight of each distinct query string and caches the result, so each query is transformed at most once
// per store instance. The name is validated to a strict identifier pattern at construction, so the
// substituted - and UNQUOTED - identifier can never be an injection or quoting hazard.
internal sealed partial class SchemaRewriter
{
    internal const string DefaultSchema = "backwave";

    // Oracle identifiers cap at 128 characters (12.2+); reject longer names rather than silently truncating.
    private const int MaxSchemaLength = 128;

    // DBMS_ALERT stores an alert name in a VARCHAR2(30) column, so the Wake-Up Hint channel name must fit
    // 30 characters. It is derived from the schema so two BackWave deployments in different schemas of one
    // database wake their own pumps. An alert name is database-wide, not schema-scoped; a long schema name
    // is truncated to fit, and if two truncated names collide the only effect is a spurious cross-wake,
    // which is harmless - a hint is advisory and the woken pump finds no work.
    private const int MaxAlertNameLength = 30;
    private const string AlertSuffix = "_hints";

    private readonly string _schema;
    private readonly bool _isDefault;
    private readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.Ordinal);

    // The DBMS_ALERT name for Wake-Up Hints, namespaced to the schema (default 'backwave_hints').
    public string HintAlertName { get; }

    public SchemaRewriter(string schemaName)
    {
        if (string.IsNullOrEmpty(schemaName) || schemaName.Length > MaxSchemaLength
            || !IdentifierPattern().IsMatch(schemaName))
        {
            throw new ArgumentException(
                $"'{schemaName}' is not a valid Oracle schema name. Use 1-{MaxSchemaLength} characters: " +
                "a letter or underscore followed by letters, digits, or underscores.",
                nameof(schemaName));
        }

        _schema = schemaName;
        _isDefault = string.Equals(schemaName, DefaultSchema, StringComparison.Ordinal);

        var maxSchemaPart = MaxAlertNameLength - AlertSuffix.Length;
        var alertSchemaPart = schemaName.Length <= maxSchemaPart ? schemaName : schemaName[..maxSchemaPart];
        HintAlertName = alertSchemaPart + AlertSuffix;
    }

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
