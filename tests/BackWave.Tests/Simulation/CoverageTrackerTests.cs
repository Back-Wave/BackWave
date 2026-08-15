using BackWave.Storage;

namespace BackWave.Tests.Simulation;

/// <summary>
/// The Coverage tracker (issue 0090, ADR 0018): transition-edge coverage against the legal-edge denominator
/// plus a curated open registry of named Situations, both derived POST-HOC from <see cref="SimulationResult"/>,
/// unioned across runs, and reported as the never-reached complement. These prove: a hitting run records its
/// edges/Situations; the union folds two runs together; the report is exactly the complement of the union; and
/// the edge denominator is <see cref="Simulator.LegalTransitionEdges"/>, not the universe of all state pairs.
/// </summary>
public sealed class CoverageTrackerTests
{
    private static SimulationResult ResultWithTimeline(
        params (JobState State, int Attempt)[] states)
    {
        var ts = DateTimeOffset.UnixEpoch;
        var timeline = states
            .Select((s, i) => (Timestamp: ts.AddSeconds(i), s.State, s.Attempt))
            .ToList();
        var jobId = Guid.NewGuid();
        return new SimulationResult(
            Seed: 1,
            Steps: 1,
            Crashes: 0,
            StaleOutcomes: 0,
            FinalJobs: [(jobId, states[^1].State, states[^1].Attempt)])
        {
            FinalTransitions = [(jobId, timeline)],
        };
    }

    [Fact]
    public void HittingRun_RecordsTheEdgesAndSituationsItExercises()
    {
        // A clean lifecycle Scheduled -> Leased -> Succeeded, with crashes recorded so a Situation fires too.
        var result = ResultWithTimeline(
            (JobState.Scheduled, 0),
            (JobState.Leased, 1),
            (JobState.Succeeded, 1)) with
        { Crashes = 2 };

        var hits = CoverageTracker.HitsOf(result);

        Assert.Contains((JobState.Scheduled, JobState.Leased), hits.Edges);
        Assert.Contains((JobState.Leased, JobState.Succeeded), hits.Edges);
        Assert.Equal(2, hits.Edges.Count);
        Assert.Contains(Situation.CrashRecovery, hits.Situations);
    }

    [Fact]
    public void HitsOf_DerivesEachSituationFromTheResultCounters()
    {
        var result = new SimulationResult(Seed: 1, Steps: 1, Crashes: 0, StaleOutcomes: 3, FinalJobs: [])
        {
            AckLosses = 1,
            Isolations = 1,
            LeasesExpired = 2,
            OperatorRequeues = 1,
            CooperativeCancels = 1,
        };

        var hits = CoverageTracker.HitsOf(result);

        Assert.Contains(Situation.StaleOutcomeFenced, hits.Situations);
        Assert.Contains(Situation.AckLossRetry, hits.Situations);
        Assert.Contains(Situation.IsolationDuringExecuting, hits.Situations);
        Assert.Contains(Situation.MigrationFired, hits.Situations);
        Assert.Contains(Situation.OperatorRequeue, hits.Situations);
        Assert.Contains(Situation.CooperativeCancel, hits.Situations);
        // No quarantine/dead-letter/observer happened, so those stay unhit.
        Assert.DoesNotContain(Situation.QuarantineReached, hits.Situations);
        Assert.DoesNotContain(Situation.DeadLetterReached, hits.Situations);
    }

    [Fact]
    public void HitsOf_LightsLimitAndBackpressureSituations_FromTheirCounters_AndNotOtherwise()
    {
        // issue 0124: the two formerly constant-false Situations are now lit from real SimulationResult
        // counters. A run with both counters positive hits both; a run with both zero hits neither.
        var saturated = new SimulationResult(Seed: 1, Steps: 1, Crashes: 0, StaleOutcomes: 0, FinalJobs: [])
        {
            LimitSaturations = 4,
            BackpressureIdleTicks = 7,
        };
        var hits = CoverageTracker.HitsOf(saturated);
        Assert.Contains(Situation.LimitSaturated, hits.Situations);
        Assert.Contains(Situation.BackpressureIdle, hits.Situations);

        var calm = new SimulationResult(Seed: 2, Steps: 1, Crashes: 0, StaleOutcomes: 0, FinalJobs: []);
        var calmHits = CoverageTracker.HitsOf(calm);
        Assert.DoesNotContain(Situation.LimitSaturated, calmHits.Situations);
        Assert.DoesNotContain(Situation.BackpressureIdle, calmHits.Situations);
    }

    [Fact]
    public void OutcomeBufferDroppedOnCrash_LightsFromItsCounter_AndNotOtherwise()
    {
        // ADR 0035: a crash that discarded a non-empty outcome buffer lights the buffer-loss Situation.
        var dropped = new SimulationResult(Seed: 1, Steps: 1, Crashes: 1, StaleOutcomes: 0, FinalJobs: [])
        {
            OutcomeBufferDropped = 2,
        };
        Assert.Contains(Situation.OutcomeBufferDroppedOnCrash, CoverageTracker.HitsOf(dropped).Situations);

        // A crash that dropped nothing buffered does not light it.
        var noDrop = new SimulationResult(Seed: 2, Steps: 1, Crashes: 1, StaleOutcomes: 0, FinalJobs: []);
        Assert.DoesNotContain(Situation.OutcomeBufferDroppedOnCrash, CoverageTracker.HitsOf(noDrop).Situations);
    }

    [Theory]
    [InlineData(1UL)]
    [InlineData(4UL)]
    public void ACrashEnabledRun_ExercisesTheBufferLossWindow(ulong seed)
    {
        // ADR 0035: prove the buffer-loss-on-crash window is actually reached — a representative
        // crash-enabled run drops at least one buffered outcome and lights the coverage Situation.
        var result = new Simulator(new SimulationOptions
        {
            Seed = seed,
            CrashProbabilityPerPoll = 0.02,
            HeartbeatLossProbability = 0,
        }).Run();

        Assert.True(result.OutcomeBufferDropped > 0, "expected a crash to discard a non-empty outcome buffer");
        Assert.Contains(Situation.OutcomeBufferDroppedOnCrash, CoverageTracker.HitsOf(result).Situations);
    }

    [Fact]
    public void NoSituationHasAConstantFalsePredicate()
    {
        // issue 0124: after lighting LimitSaturated/BackpressureIdle, no Situation may remain a poison
        // (constant-false) target the guided search would chase forever. Every predicate must return true
        // for SOME SimulationResult — proven here by lighting each from a result built to satisfy it.
        var everything = new SimulationResult(
            Seed: 1, Steps: 1, Crashes: 1, StaleOutcomes: 1,
            FinalJobs:
            [
                (Guid.NewGuid(), JobState.Quarantined, 1),
                (Guid.NewGuid(), JobState.DeadLettered, 3),
            ])
        {
            AckLosses = 1,
            Isolations = 1,
            LeasesExpired = 1,
            OperatorRequeues = 1,
            CooperativeCancels = 1,
            LimitSaturations = 1,
            BackpressureIdleTicks = 1,
            OutcomeBufferDropped = 1,
            MintedJobs = [("sched", DateTimeOffset.UnixEpoch, JobState.Succeeded)],
            ObserverDeliveries = new Dictionary<string, (int Total, int Unique, int DeadLettered)>
            {
                ["obs"] = (Total: 2, Unique: 1, DeadLettered: 0),
            },
        };

        foreach (var (situation, predicate) in CoverageTracker.SituationPredicates)
        {
            Assert.True(predicate(everything), $"Situation {situation} has a constant-false predicate (unreachable target)");
        }
    }

    [Fact]
    public void Union_AcrossTwoRunsHittingDifferentThings_IsBoth()
    {
        var runA = ResultWithTimeline(
            (JobState.Scheduled, 0),
            (JobState.Leased, 1),
            (JobState.Succeeded, 1)) with
        { StaleOutcomes = 1 };

        // Last state Quarantined -> the computed Quarantined counter is 1, lighting QuarantineReached.
        var runB = ResultWithTimeline(
            (JobState.Leased, 1),
            (JobState.Quarantined, 1)) with
        { AckLosses = 1 };

        var tracker = new CoverageTracker();
        tracker.Union(runA);
        tracker.Union(runB);

        var report = tracker.Report();

        // Edges from BOTH runs are reached.
        Assert.DoesNotContain((JobState.Scheduled, JobState.Leased), report.NeverReachedEdges);
        Assert.DoesNotContain((JobState.Leased, JobState.Succeeded), report.NeverReachedEdges);
        Assert.DoesNotContain((JobState.Leased, JobState.Quarantined), report.NeverReachedEdges);
        Assert.Equal(3, report.EdgesHit);

        // Situations from BOTH runs are hit.
        Assert.DoesNotContain(Situation.StaleOutcomeFenced, report.NeverHitSituations);
        Assert.DoesNotContain(Situation.QuarantineReached, report.NeverHitSituations);
        Assert.DoesNotContain(Situation.AckLossRetry, report.NeverHitSituations);
    }

    [Fact]
    public void Report_NeverReachedSet_EqualsTheComplementOfTheUnion()
    {
        var run = ResultWithTimeline(
            (JobState.Scheduled, 0),
            (JobState.Leased, 1),
            (JobState.Succeeded, 1)) with
        { StaleOutcomes = 1, Crashes = 1 };

        var hits = CoverageTracker.HitsOf(run);

        var tracker = new CoverageTracker();
        tracker.Union(run);
        var report = tracker.Report();

        // never-reached edges == legal edges MINUS the union of hit edges
        var expectedNeverEdges = Simulator.LegalTransitionEdges.Where(e => !hits.Edges.Contains(e)).ToHashSet();
        Assert.Equal(expectedNeverEdges, report.NeverReachedEdges.ToHashSet());
        Assert.Equal(Simulator.LegalTransitionEdges.Count, report.EdgesHit + report.NeverReachedEdges.Count);

        // never-hit Situations == registered Situations MINUS the union of hit Situations
        var allSituations = Enum.GetValues<Situation>();
        var expectedNeverSituations = allSituations.Where(s => !hits.Situations.Contains(s)).ToHashSet();
        Assert.Equal(expectedNeverSituations, report.NeverHitSituations.ToHashSet());
        Assert.Equal(allSituations.Length, report.SituationsHit + report.NeverHitSituations.Count);
    }

    [Fact]
    public void EdgeCoverage_IsMeasuredAgainstTheLegalSet_NotAllStatePairs()
    {
        // An ILLEGAL pair (Succeeded -> Leased: a terminal coming back to life) is traversed in the timeline.
        // Coverage must IGNORE it — the legal-transition oracle owns that failure — so it is neither counted
        // as a hit nor expands the denominator.
        var run = ResultWithTimeline(
            (JobState.Scheduled, 0),
            (JobState.Leased, 1),
            (JobState.Succeeded, 1),
            (JobState.Leased, 1)); // illegal Succeeded -> Leased

        var hits = CoverageTracker.HitsOf(run);
        Assert.DoesNotContain((JobState.Succeeded, JobState.Leased), hits.Edges);

        var tracker = new CoverageTracker();
        tracker.Union(run);
        var report = tracker.Report();

        // Denominator is exactly the legal-edge set on the Simulator.
        Assert.Equal(Simulator.LegalTransitionEdges.Count, report.LegalEdgeCount);
        Assert.Equal(11, report.LegalEdgeCount);
        // Every reported edge (hit or never-reached) is a member of the legal set — never the illegal pair.
        var allReported = report.NeverReachedEdges.Concat(hits.Edges).ToHashSet();
        Assert.Subset(Simulator.LegalTransitionEdges.ToHashSet(), allReported);
        Assert.DoesNotContain((JobState.Succeeded, JobState.Leased), allReported);
    }

    [Fact]
    public void EmptyTracker_Report_HasEveryLegalEdgeAndSituationInTheComplement()
    {
        var report = new CoverageTracker().Report();

        Assert.Equal(0, report.EdgesHit);
        Assert.Equal(Simulator.LegalTransitionEdges.Count, report.NeverReachedEdges.Count);
        Assert.Equal(0, report.SituationsHit);
        Assert.Equal(Enum.GetValues<Situation>().Length, report.NeverHitSituations.Count);
    }
}
