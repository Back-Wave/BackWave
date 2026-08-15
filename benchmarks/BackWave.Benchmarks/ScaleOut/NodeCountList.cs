namespace BackWave.Benchmarks.ScaleOut;

/// <summary>
/// Parses the <c>--scale-out</c> node-count sweep — a comma list like <c>1,2,4,8</c> — into the ordered set
/// of Node counts the curve is measured at. Pure and order-preserving (the curve is charted in the order
/// given), so the swept axis is reproducible and not sensitive to whitespace or a trailing comma.
/// </summary>
public static class NodeCountList
{
    /// <summary>
    /// Parses a comma-separated list of positive Node counts, preserving the order they were written in.
    /// </summary>
    /// <param name="raw">The raw flag value, e.g. <c>"1,2,4,8"</c>. Whitespace and empty entries (such as a
    /// trailing comma) are ignored.</param>
    /// <returns>The ordered Node counts to sweep; never empty.</returns>
    /// <exception cref="ArgumentException">No counts were found, or an entry was not a positive integer.</exception>
    public static IReadOnlyList<int> Parse(string raw)
    {
        var counts = new List<int>();
        foreach (var token in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(token, out var count) || count < 1)
            {
                throw new ArgumentException(
                    $"Invalid node count '{token}' in '{raw}'. Each entry must be a positive integer.");
            }

            counts.Add(count);
        }

        if (counts.Count == 0)
        {
            throw new ArgumentException($"No node counts found in '{raw}'. Expected a comma list like '1,2,4,8'.");
        }

        return counts;
    }
}
