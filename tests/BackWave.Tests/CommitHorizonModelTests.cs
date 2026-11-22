using BackWave.Storage;
using Xunit.Abstractions;

namespace BackWave.Tests;

public class CommitHorizonModelTests(ITestOutputHelper output)
{
    private const int WriterCount = 3;
    private const int StatementsPerWriter = 2;
    private const int Steps = 3;

    [Fact]
    public void EveryInterleaving_KeepsOpenWritersAboveTheHorizon_AndAQuietStepCatchesUp()
    {
        var explorer = new Explorer();

        var failure = explorer.Explore(World.Initial);

        output.WriteLine($"{explorer.States} states; {string.Join(", ", explorer.Coverage.Select(c => $"{c.Key} {c.Value}"))}");
        Assert.True(failure is null, failure);
        foreach (var shape in Enum.GetValues<Shape>())
        {
            Assert.True(explorer.Coverage.GetValueOrDefault(shape) > 0, $"The scope never reached {shape}.");
        }
    }

    private enum Shape
    {
        BothEpochsFree,
        OnlyThePreviousEpochFree,
        PreviousEpochBusy,
        LateCommit,
        CandidateSettledWhileAWriterIsOpen,
    }

    private enum WriterPhase
    {
        Idle,
        Registered,
        Drawn,
        Ended,
    }

    private readonly record struct Writer(WriterPhase Phase, int Statements, int Epochs, long Low, long High)
    {
        public bool Open => Phase is WriterPhase.Registered or WriterPhase.Drawn;

        public bool RegisteredOn(int epoch) => (Epochs & (1 << epoch)) != 0;

        public ulong Code
            => (ulong)Phase | (ulong)Statements << 2 | (ulong)Epochs << 4 | (ulong)Low << 6 | (ulong)High << 9;
    }

    private enum StepPhase
    {
        Idle,
        ReadMax,
        ProbedOne,
        ProbedBoth,
    }

    private readonly record struct Step(StepPhase Phase, int Done, long MaxPosition, bool? PreviousFree, bool? CurrentFree);

    private sealed record World(Writer[] Writers, CommitHorizon Horizon, long NextPosition, long MaxCommitted, Step Step)
    {
        public static World Initial { get; } = new(
            new Writer[WriterCount], new CommitHorizon(0, null, 0), NextPosition: 1, MaxCommitted: 0, default);

        public ulong Key
        {
            get
            {
                Span<ulong> codes = stackalloc ulong[WriterCount];
                for (var i = 0; i < WriterCount; i++)
                {
                    codes[i] = Writers[i].Code;
                }
                codes.Sort();
                var key = 0UL;
                foreach (var code in codes)
                {
                    key = key << 12 | code;
                }
                key = key << 1 | (uint)Horizon.Epoch;
                key = key << 3 | (ulong)(Horizon.Candidate + 1 ?? 0);
                key = key << 3 | (ulong)Horizon.Horizon;
                key = key << 3 | (ulong)NextPosition;
                key = key << 3 | (ulong)MaxCommitted;
                key = key << 2 | (ulong)Step.Phase;
                key = key << 2 | (uint)Step.Done;
                key = key << 3 | (ulong)Step.MaxPosition;
                key = key << 2 | ProbeCode(Step.PreviousFree);
                return key << 2 | ProbeCode(Step.CurrentFree);
            }
        }

        public bool AnyOpen => Writers.Any(w => w.Open);

        public bool Free(int epoch) => !Writers.Any(w => w.Open && w.RegisteredOn(epoch));

        public World With(int index, Writer writer)
        {
            var writers = (Writer[])Writers.Clone();
            writers[index] = writer;
            return this with { Writers = writers };
        }

        private static ulong ProbeCode(bool? free) => free switch { null => 0, false => 1, true => 2 };
    }

    private sealed class Explorer
    {
        private readonly HashSet<ulong> _visited = [];
        private readonly List<string> _path = [];

        public Dictionary<Shape, int> Coverage { get; } = [];

        public int States => _visited.Count;

        public string? Explore(World initial)
        {
            _visited.Add(initial.Key);
            return Visit(initial);
        }

        private string? Visit(World world)
        {
            if (Broken(world) is { } failure)
            {
                var trace = _path.Select((move, n) => $"  {n + 1}. {move}");
                return string.Join(Environment.NewLine, [failure, .. trace]);
            }
            foreach (var (move, shape, next) in Moves(world))
            {
                if (!_visited.Add(next.Key))
                {
                    continue;
                }
                if (shape is { } reached)
                {
                    Coverage[reached] = Coverage.GetValueOrDefault(reached) + 1;
                }
                _path.Add(move);
                var found = Visit(next);
                _path.RemoveAt(_path.Count - 1);
                if (found is not null)
                {
                    return found;
                }
            }
            return null;
        }
    }

    private static string? Broken(World world)
    {
        var horizon = world.Horizon.Horizon;
        foreach (var writer in world.Writers)
        {
            if (writer.Open && writer.Low != 0 && writer.Low <= horizon)
            {
                return $"Safety: an open writer holds Position {writer.Low}, at or below the horizon {horizon}.";
            }
        }

        if (!world.AnyOpen && world.Step.Phase == StepPhase.Idle)
        {
            var state = world.Horizon;
            var quiet = state.Advance(world.MaxCommitted, world.Free(state.PreviousEpoch), world.Free(state.Epoch));
            if (quiet.Horizon != world.MaxCommitted)
            {
                return $"Liveness: with no writer open, one step left the horizon at {quiet.Horizon}, not at P = {world.MaxCommitted}.";
            }
        }
        return null;
    }

    private static IEnumerable<(string Move, Shape? Shape, World Next)> Moves(World world)
    {
        for (var i = 0; i < world.Writers.Length; i++)
        {
            var writer = world.Writers[i];
            if (writer.Phase == WriterPhase.Idle
                || (writer.Phase == WriterPhase.Drawn && writer.Statements < StatementsPerWriter))
            {
                for (var epoch = 0; epoch < 2; epoch++)
                {
                    yield return ($"writer {i} registers on epoch {epoch}", null, world.With(i, writer with
                    {
                        Phase = WriterPhase.Registered,
                        Epochs = writer.Epochs | (1 << epoch),
                    }));
                }
            }
            if (writer.Phase == WriterPhase.Registered)
            {
                var position = world.NextPosition;
                yield return ($"writer {i} draws Position {position}", null, world.With(i, writer with
                {
                    Phase = WriterPhase.Drawn,
                    Statements = writer.Statements + 1,
                    Low = writer.Low == 0 ? position : writer.Low,
                    High = position,
                }) with { NextPosition = position + 1 });
            }
            if (writer.Phase == WriterPhase.Drawn)
            {
                var ended = writer with { Phase = WriterPhase.Ended };
                yield return (
                    $"writer {i} commits Positions {writer.Low}..{writer.High}",
                    writer.Low < world.MaxCommitted ? Shape.LateCommit : null,
                    world.With(i, ended) with { MaxCommitted = Math.Max(world.MaxCommitted, writer.High) });
                yield return ($"writer {i} rolls back", null, world.With(i, ended));
            }
        }

        var step = world.Step;
        var horizon = world.Horizon;
        switch (step.Phase)
        {
            case StepPhase.Idle when step.Done < Steps:
                yield return ($"step reads P = {world.MaxCommitted}", null, world with
                {
                    Step = step with { Phase = StepPhase.ReadMax, MaxPosition = world.MaxCommitted },
                });
                break;
            case StepPhase.ReadMax:
                yield return Probe(world, horizon.PreviousEpoch);
                yield return Probe(world, horizon.Epoch);
                break;
            case StepPhase.ProbedOne:
                yield return Probe(world, step.PreviousFree is null ? horizon.PreviousEpoch : horizon.Epoch);
                break;
            case StepPhase.ProbedBoth:
                var previousFree = step.PreviousFree!.Value;
                var currentFree = step.CurrentFree!.Value;
                var next = horizon.Advance(step.MaxPosition, previousFree, currentFree);
                Shape shape = (previousFree, currentFree) switch
                {
                    (true, true) => Shape.BothEpochsFree,
                    (true, false) when next.Horizon > horizon.Horizon && world.AnyOpen
                        => Shape.CandidateSettledWhileAWriterIsOpen,
                    (true, false) => Shape.OnlyThePreviousEpochFree,
                    _ => Shape.PreviousEpochBusy,
                };
                yield return ($"step advances to {next}", shape, world with
                {
                    Horizon = next,
                    Step = new Step(StepPhase.Idle, step.Done + 1, 0, null, null),
                });
                break;
        }
    }

    private static (string Move, Shape? Shape, World Next) Probe(World world, int epoch)
    {
        var step = world.Step;
        var free = world.Free(epoch);
        var probed = epoch == world.Horizon.Epoch ? step with { CurrentFree = free } : step with { PreviousFree = free };
        probed = probed with { Phase = step.Phase == StepPhase.ReadMax ? StepPhase.ProbedOne : StepPhase.ProbedBoth };
        return ($"step probes epoch {epoch}: {(free ? "free" : "busy")}", null, world with { Step = probed });
    }
}
