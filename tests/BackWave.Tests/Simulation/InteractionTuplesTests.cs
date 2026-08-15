namespace BackWave.Tests.Simulation;

/// <summary>
/// The exploiter's denominator-free novelty signal (issue 0126, ADR 0025 decisions 4/5): co-occurring-Situation
/// interaction tuples. "New pair seen" with no complement and no fraction; lone Situations contribute nothing
/// (that is the config-space gradient's job), and pair ordering never doubles a tuple.
/// </summary>
public sealed class InteractionTuplesTests
{
    [Fact]
    public void EmptyOrSingleSituation_ContributesNoTuple()
    {
        var tuples = new InteractionTuples();

        Assert.False(tuples.Union(new HashSet<Situation>()));
        Assert.False(tuples.Union(new HashSet<Situation> { Situation.CrashRecovery }));
        Assert.Equal(0, tuples.Count);
    }

    [Fact]
    public void FirstCoOccurringPair_IsNovel_RepeatIsNot_OrderingDoesNotDouble()
    {
        var tuples = new InteractionTuples();

        Assert.True(tuples.Union(new HashSet<Situation> { Situation.CrashRecovery, Situation.MigrationFired }));
        Assert.Equal(1, tuples.Count);

        // The same pair, whatever the set's iteration order, is canonicalised low→high — not a second tuple.
        Assert.False(tuples.Union(new HashSet<Situation> { Situation.MigrationFired, Situation.CrashRecovery }));
        Assert.Equal(1, tuples.Count);
    }

    [Fact]
    public void NWaySet_AddsEveryNewPair_AndReportsGrowthOnlyWhenSomethingIsNew()
    {
        var tuples = new InteractionTuples();
        tuples.Union(new HashSet<Situation> { Situation.CrashRecovery, Situation.MigrationFired }); // 1 pair

        // {A,B,C} where A,B already co-seen contributes the two NEW pairs A-C and B-C.
        Assert.True(tuples.Union(new HashSet<Situation>
        {
            Situation.CrashRecovery, Situation.MigrationFired, Situation.DeadLetterReached,
        }));
        Assert.Equal(3, tuples.Count);

        // A strict subset of already-seen pairs grows nothing.
        Assert.False(tuples.Union(new HashSet<Situation> { Situation.CrashRecovery, Situation.DeadLetterReached }));
        Assert.Equal(3, tuples.Count);
    }
}
