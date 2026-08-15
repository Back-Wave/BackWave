namespace BackWave.Tests.Simulation;

/// <summary>
/// One planned Node Isolation episode: node <see cref="Node"/> is cut off from the Storage Contract at
/// <see cref="StartAt"/> for <see cref="Duration"/>. A <b>healing</b> episode (issue 0068) heals at
/// <c>StartAt + Duration</c> — the heal-into-stale-write race that proves Effect-Once at the Storage
/// Contract boundary (ADR 0013). A <b>permanent</b> episode (<see cref="Permanent"/>, issue 0069) models
/// permanent node loss: it never heals, so its <see cref="Duration"/> is unused and its work must
/// migrate to a survivor via Lease expiry. Permanent loss is the same mechanism, not a separate "node
/// death" path.
/// </summary>
internal sealed record IsolationEpisode(int Node, DateTimeOffset StartAt, TimeSpan Duration, bool Permanent = false);

/// <summary>
/// The Isolation Scheduler (issue 0068): the deep module that owns Node Isolation's fault budget and
/// draws episodes from a dedicated <c>Seed ^ "ISOLATION"</c> rng stream, independent of the run's main
/// interleaving. It plans healing-isolation episodes and answers <see cref="IsIsolated"/>; the
/// Simulator emits each episode's start/heal onto the virtual-time queue.
///
/// It encapsulates the <b>N−1 fault budget</b>: it never isolates the last reachable node, so at least
/// one node always reaches the store and full convergence stays a <i>required</i> liveness property
/// with no "is liveness even possible?" branch (ADR 0006, ADR 0013). Concurrent-isolation capping falls
/// out of that single rule — and so does the permanent-loss cap (issue 0069): a permanent loss is an
/// isolation that never heals, so permanently-lost nodes accumulate in the isolated set and, once N−1 of
/// them are lost, <see cref="TryBegin"/> refuses every further begin, keeping the last node reachable
/// forever. There is no separate permanent counter; the one budget rule governs both.
/// </summary>
internal sealed class IsolationScheduler(ulong seed, int nodeCount)
{
    private readonly DeterministicRandom _rng = new(seed ^ 0x49534F4C4154494FUL); // "ISOLATIO": its own stream
    private readonly HashSet<int> _isolated = [];

    /// <summary>Nodes currently cut off from the store.</summary>
    public int IsolatedCount => _isolated.Count;

    public bool IsIsolated(int node) => _isolated.Contains(node);

    /// <summary>
    /// Draws <paramref name="count"/> candidate episodes from the isolation stream: a node, a start
    /// instant within the workload window, a duration in <c>[minDuration, maxDuration]</c>, and — when
    /// <paramref name="permanentLossProbability"/> is positive — a per-episode permanent-loss flag. A zero
    /// count makes no draws, so the existing seed battery stays byte-identical; a zero permanent
    /// probability short-circuits the permanent draw, so the healing-only regimes are byte-identical too.
    /// The N−1 budget is enforced live at <see cref="TryBegin"/>, not here: a candidate whose start would
    /// isolate the last reachable node is a deterministic no-op rather than a re-draw, so the draw stream
    /// never branches on the live isolation set.
    /// </summary>
    public IReadOnlyList<IsolationEpisode> Plan(
        int count, DateTimeOffset start, TimeSpan window, TimeSpan minDuration, TimeSpan maxDuration,
        double permanentLossProbability = 0)
    {
        var episodes = new List<IsolationEpisode>(count);
        for (var i = 0; i < count; i++)
        {
            var node = _rng.Next(nodeCount);
            var at = start + _rng.NextTimeSpan(window);
            var duration = minDuration + _rng.NextTimeSpan(maxDuration - minDuration);
            var permanent = permanentLossProbability > 0 && _rng.NextDouble() < permanentLossProbability;
            episodes.Add(new IsolationEpisode(node, at, duration, permanent));
        }
        return episodes;
    }

    /// <summary>
    /// Begins isolating <paramref name="node"/> if the N−1 budget allows — at least one node must stay
    /// reachable. Returns false (a deterministic no-op) when the node is already isolated or isolating it
    /// would cut off the last reachable node.
    /// </summary>
    public bool TryBegin(int node)
    {
        if (_isolated.Contains(node) || _isolated.Count >= nodeCount - 1)
        {
            return false;
        }
        _isolated.Add(node);
        return true;
    }

    /// <summary>Heals <paramref name="node"/>; it resumes reaching the store on its next step.</summary>
    public void Heal(int node) => _isolated.Remove(node);
}
