namespace BackWave.Storage;

// The one definition of the Tag Suggest folding + ordering rules (ADR 0042). The reference store uses
// this directly; every adapter reproduces the SAME rules in SQL (engine lower() for the ASCII fold, an
// ordinal/binary collation for the tiebreak) so the Conformance Suite's ASCII-CI, lexicographic-order,
// and cursor-walk clauses hold identically across all four stores. Only ASCII case folding is pinned;
// folding beyond ASCII is left to whatever lower() the store applies.
internal static class TagSuggestFold
{
    // Case-fold ASCII only: A–Z map to a–z, every other char is left untouched. This deliberately
    // matches what SQLite's built-in lower() and a 'C'-collated Postgres lower() do to ASCII, so the
    // reference store and the adapters agree on the guaranteed cases.
    public static string Lower(string s)
    {
        char[]? buffer = null;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c is >= 'A' and <= 'Z')
            {
                buffer ??= s.ToCharArray();
                buffer[i] = (char)(c + 32);
            }
        }

        return buffer is null ? s : new string(buffer);
    }

    // Lexicographic order used for both the ORDER BY and the keyset cursor: by the ASCII-folded token
    // first, with the canonical (ordinal) token as the stable tiebreak that keeps case-variants that
    // fold equal (for example "Acme"/"acme") in a total, cursor-walkable order.
    public static int Compare(string a, string b)
    {
        var folded = string.CompareOrdinal(Lower(a), Lower(b));
        return folded != 0 ? folded : string.CompareOrdinal(a, b);
    }

    // ASCII case-insensitive prefix test; foldedPrefix must already be Lower()-ed. An empty prefix
    // matches everything.
    public static bool PrefixMatch(string token, string foldedPrefix)
        => Lower(token).StartsWith(foldedPrefix, StringComparison.Ordinal);
}
