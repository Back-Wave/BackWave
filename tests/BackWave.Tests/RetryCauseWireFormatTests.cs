using BackWave.Storage;

namespace BackWave.Tests;

// RetryCause's numbers are a storage wire format: every adapter writes (int)cause into a nullable int
// column. The conformance suite and the upgrade harness write and read with the same code, so both sides
// agree on a wrong number. This test is the asymmetric side: it holds the numbers that rows already in
// customer databases were written with.

public class RetryCauseWireFormatTests
{
    // An entry only ever changes alongside a migration that rewrites the rows.
    private static readonly Dictionary<string, int> PersistedValues = new(StringComparer.Ordinal)
    {
        [nameof(RetryCause.HandlerFailed)] = 1,
        [nameof(RetryCause.LeaseExpired)] = 2,
    };

    [Fact]
    public void EveryMember_IsPinned_AndKeepsThePersistedNumberItsRowsWereWrittenWith()
    {
        var actual = Enum.GetValues<RetryCause>().ToDictionary(cause => cause.ToString(), cause => (int)cause, StringComparer.Ordinal);

        Assert.True(
            actual.Count == PersistedValues.Count && actual.All(pair => PersistedValues.TryGetValue(pair.Key, out var pinned) && pinned == pair.Value),
            $"""
             RetryCause no longer matches its pinned wire numbers.

             Now:    {string.Join(", ", actual.Select(pair => $"{pair.Key}={pair.Value}"))}
             Pinned: {string.Join(", ", PersistedValues.Select(pair => $"{pair.Key}={pair.Value}"))}

             A RetryCause number is persisted, so renumbering migrates nothing - every row already in a
             customer database reads back as a different cause. To add a cause, give it the next free number
             and pin it here; to retire one, leave its number reserved rather than reusing it.
             """);
    }
}
