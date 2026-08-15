namespace BackWave.Tests.Simulation;

/// <summary>
/// The exploiter's two trace operators (issue 0126, ADR 0025 decision 6): <see cref="TraceMutator.Flip"/>
/// (1–4 stacked outcome inversions, blind-biased toward injecting on already-exercised axes, operator-axis
/// pinned) and <see cref="TraceMutator.Splice"/> (uniform crossover within a same-frozen-Scenario family).
/// These prove the operators' shape in isolation; the replay/veto/retention behaviour is in
/// <see cref="CoverageGuidedSwarmTests"/>.
/// </summary>
public sealed class TraceMutatorTests
{
    private static Scenario CleanScenario(ulong seed) => Scenario.FromOptions(SwarmConfig.FromSeed(seed));

    /// <summary>A parent whose map exercises the <c>store</c> axis (one active decision) with false entries on
    /// both that axis (steered injection targets) and an un-exercised <c>crash</c> axis, plus a pinned operator.</summary>
    private static Plan ParentMap() => new()
    {
        Scenario = CleanScenario(1),
        FaultMap =
        [
            new FaultEntry("store", "n0:claim", 0, true),    // exercised: store has an active decision
            new FaultEntry("store", "n1:claim", 0, false),   // false-on-exercised → steered target
            new FaultEntry("store", "n2:claim", 0, false),   // false-on-exercised → steered target
            new FaultEntry("crash", "n0:t0", 0, false),      // false-on-UN-exercised → base weight only
            new FaultEntry("operator", "t0", 0, true),       // pinned — must never flip
        ],
    };

    [Fact]
    public void Flip_KeepsShape_NeverTouchesOperatorAxis_AndStacksOneToFourDistinctFlips()
    {
        var parent = ParentMap();
        var rng = new DeterministicRandom(42);

        var minChanged = int.MaxValue;
        var maxChanged = 0;
        for (var i = 0; i < 500; i++)
        {
            var mutant = TraceMutator.Flip(parent, rng);

            // Flips edit outcomes in place — same length, same addresses, only Fault bits move.
            Assert.Equal(parent.FaultMap.Count, mutant.FaultMap.Count);
            Assert.Equal(parent.Scenario, mutant.Scenario); // world frozen
            Assert.Null(mutant.Failure);

            var changed = 0;
            for (var e = 0; e < parent.FaultMap.Count; e++)
            {
                Assert.Equal(parent.FaultMap[e].Axis, mutant.FaultMap[e].Axis);
                Assert.Equal(parent.FaultMap[e].Id, mutant.FaultMap[e].Id);
                if (parent.FaultMap[e].Fault != mutant.FaultMap[e].Fault)
                {
                    changed++;
                    Assert.NotEqual("operator", mutant.FaultMap[e].Axis); // the pinned axis never moves
                }
            }

            Assert.InRange(changed, 1, 4); // 1..4 net flips (distinct indices, ≤ 4 candidates here)
            minChanged = Math.Min(minChanged, changed);
            maxChanged = Math.Max(maxChanged, changed);
        }

        Assert.Equal(1, minChanged); // the low end is exercised
        Assert.True(maxChanged >= 3, $"never stacked more than {maxChanged} flips — the stack range is too narrow");
    }

    [Fact]
    public void Flip_BlindBias_InjectsFarMoreOnExercisedAxesThanOff_WithNoCuratedMap()
    {
        var parent = ParentMap();
        var rng = new DeterministicRandom(7);

        var flipsByAddress = new Dictionary<string, int>();
        for (var i = 0; i < 4000; i++)
        {
            var mutant = TraceMutator.Flip(parent, rng);
            for (var e = 0; e < parent.FaultMap.Count; e++)
            {
                if (parent.FaultMap[e].Fault != mutant.FaultMap[e].Fault)
                {
                    var key = parent.FaultMap[e].Id;
                    flipsByAddress[key] = flipsByAddress.GetValueOrDefault(key) + 1;
                }
            }
        }

        // The two false-on-store (exercised) targets are weighted up; the false-on-crash (un-exercised) is base.
        var steered = flipsByAddress.GetValueOrDefault("n1:claim") + flipsByAddress.GetValueOrDefault("n2:claim");
        var offAxis = flipsByAddress.GetValueOrDefault("n0:t0");
        Assert.True(steered > offAxis * 3, $"steering too weak: steered={steered} offAxis={offAxis}");
        Assert.Equal(0, flipsByAddress.GetValueOrDefault("t0")); // operator never flipped
    }

    [Fact]
    public void Splice_AcrossDifferentScenarios_IsDead()
    {
        var a = new Plan { Scenario = CleanScenario(1), FaultMap = [new FaultEntry("store", "x", 0, true)] };
        var b = new Plan { Scenario = CleanScenario(2), FaultMap = [new FaultEntry("store", "x", 0, false)] };

        Assert.Null(TraceMutator.Splice(a, b, new DeterministicRandom(1)));
    }

    [Fact]
    public void Splice_WithinFamily_IsUniformCrossover_OverSharedAddressesOnly()
    {
        var scenario = CleanScenario(9);
        var a = new Plan
        {
            Scenario = scenario,
            FaultMap =
            [
                new FaultEntry("store", "x", 0, true),
                new FaultEntry("crash", "y", 0, false),
                new FaultEntry("store", "z", 0, true), // a-only address
            ],
        };
        var b = new Plan
        {
            Scenario = scenario,
            FaultMap =
            [
                new FaultEntry("store", "x", 0, false),
                new FaultEntry("crash", "y", 0, true),
                new FaultEntry("heartbeat", "w", 0, true), // b-only address — ignored
            ],
        };

        // Across many coin flips, the shared differing address x takes BOTH parents' outcomes at least once.
        var sawAFalse = false;
        var sawATrue = false;
        var rng = new DeterministicRandom(3);
        for (var i = 0; i < 200; i++)
        {
            var spliced = TraceMutator.Splice(a, b, rng);
            Assert.NotNull(spliced);
            Assert.Equal(scenario, spliced.Scenario);
            Assert.Equal(a.FaultMap.Count, spliced.FaultMap.Count); // a's shape; b-only address never appears
            Assert.DoesNotContain(spliced.FaultMap, e => e.Id == "w");

            var x = spliced.FaultMap.Single(e => e.Id == "x");
            sawATrue |= x.Fault;     // a's outcome
            sawAFalse |= !x.Fault;   // b's outcome
            Assert.True(spliced.FaultMap.Single(e => e.Id == "z").Fault); // a-only → always a's outcome
        }

        Assert.True(sawATrue && sawAFalse, "x never mixed — crossover is not uniform");
    }
}
