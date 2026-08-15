using BackWave.Core;
using BackWave.Driver;
using BackWave.Storage;

namespace BackWave.Tests;

/// <summary>
/// Charge-at-issue fairness (the SWRR credit fix), driven through the REAL <see cref="NodeDriver"/>
/// rather than a hand-rolled model of its interrupt logic. A Weighted pass enqueues one claim batch
/// per Queue, issues the first, and chains the rest one-per-completion; an empty mid-pass claim ends
/// the chain, leaving the tail batches un-issued. The driver advances SWRR credit only for batches it
/// actually issues (<c>ChargeIssued</c> at the issue edge), so a repeatedly-dropped tail Queue is
/// never charged for work it did not serve and keeps tracking its configured weight. Reverting the
/// driver to charge-at-allocation (advancing the whole sized pass up front) re-introduces the deficit
/// and this test fails.
/// </summary>
public class NodeDriverWeightedFairnessTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static JobRecord Job(string queue) => new()
    {
        JobId = Guid.NewGuid(),
        WireName = "weighted-fairness",
        Payload = "{}"u8.ToArray(),
        Queue = queue,
        State = JobState.Leased,
        DueTime = T0,
        Attempt = 1,
    };

    [Fact]
    public void Weighted_RealDriver_DroppingTheTailEachInterruptedPass_DoesNotStarveTheDroppedQueue()
    {
        var queues = new[] { "a", "b", "c" };
        var index = new Dictionary<string, int>(StringComparer.Ordinal) { ["a"] = 0, ["b"] = 1, ["c"] = 2 };
        var driver = new NodeDriver(new NodeOptions
        {
            WorkerId = "w1",
            Policy = new DispatchPolicy.Weighted([("a", 6), ("b", 3), ("c", 1)]),
            PoolSize = 1000,   // never throttles within a pass; the pool drains fully each pass
            MaxClaimBatch = 7, // 7 slots/pass — off the period-10 boundary so a charge-all deficit drifts
        });

        var served = new long[3];
        var now = T0;

        // Drive 2000 passes the way the pump does: a PollDue starts a claim pass; each non-empty claim
        // returns its full batch (every Queue has unbounded backlog) and chains the next batch; on one
        // pass in four the second batch comes back empty, ending the chain so the remaining tail
        // batches are never issued — the exact re-poll / empty-claim drop the fix targets.
        const int passes = 2000;
        for (var pass = 0; pass < passes; pass++)
        {
            now = now.AddSeconds(1);
            var interrupt = pass % 4 == 0;
            var issuedThisPass = 0;

            var work = new Queue<NodeEvent>();
            work.Enqueue(new NodeEvent.PollDue(now));
            while (work.Count > 0)
            {
                foreach (var command in driver.Step(work.Dequeue()))
                {
                    switch (command)
                    {
                        case Command.ClaimBatch claim:
                            // The batch is already charged (at issue). Decide what the store returns:
                            // the first batch always serves; on an interrupted pass every later batch
                            // comes back empty, so the chain stops and the un-issued tail is dropped.
                            var primary = claim.Queues[0];
                            var drop = interrupt && issuedThisPass >= 1;
                            issuedThisPass++;
                            if (!drop)
                            {
                                var jobs = new JobRecord[claim.MaxJobs];
                                for (var i = 0; i < jobs.Length; i++)
                                {
                                    jobs[i] = Job(primary);
                                }
                                served[index[primary]] += claim.MaxJobs;
                                work.Enqueue(new NodeEvent.ClaimCompleted(jobs, now));
                            }
                            break;

                        case Command.ExecuteJob execute:
                            // Finish the job immediately so the pool frees and the outcome buffer
                            // flushes — the next PollDue then sizes a clean pass (executing == 0).
                            work.Enqueue(new NodeEvent.ExecutionSucceeded(execute.Job, now));
                            break;

                        // Everything else (ExpireLeases, LoadSchedules, ReportOutcomeBatch, Heartbeat,
                        // RequestPoll, …) is store/clock work the credit accounting does not depend on.
                    }
                }
            }
        }

        // The schedule is fully deterministic (no RNG reaches the credit accounting), so these counts
        // reproduce exactly. The fix serves the dropped tail Queue 'c' 1125; charge-at-allocation,
        // over-charging 'c' for the un-issued batches it never served, starves it to 1000.
        Assert.True(served[2] >= 1080,
            $"the repeatedly-dropped tail Queue must keep tracking its weight, not run a charge-at-allocation " +
            $"deficit: c served {served[2]} (charge-at-issue serves 1125; charge-at-allocation starves it to 1000)");

        // And the whole served distribution sits closer to the configured 6:3:1 weights: the fix
        // deviates 0.094 from the weight shares, charge-at-allocation 0.144.
        double total = served.Sum();
        double[] weightShare = [0.6, 0.3, 0.1];
        var deviation = Enumerable.Range(0, 3).Sum(i => Math.Abs(served[i] / total - weightShare[i]));
        Assert.True(deviation < 0.12,
            $"charge-at-issue must track the weights better than the charge-at-allocation deficit: " +
            $"deviation {deviation:F4} (charge-at-issue 0.094, charge-at-allocation 0.144)");
    }
}
