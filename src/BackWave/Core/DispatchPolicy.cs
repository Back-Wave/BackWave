namespace BackWave.Core;

/// <summary>
/// A Worker Group's rule for choosing which queue to claim from next. Priority is expressed
/// here, by the policy — never as a per-job property. Both policies are work-conserving: a
/// worker never idles while any served queue has due work. Use <see cref="Strict"/> for a
/// fixed priority order, or <see cref="Weighted"/> for proportional sharing across queues.
/// </summary>
public abstract record DispatchPolicy
{
    private DispatchPolicy() { }

    /// <summary>
    /// The queues this policy serves, in declaration order. A worker claims only from these
    /// queues, and the policy decides which to try first on each pass.
    /// </summary>
    public abstract IReadOnlyList<string> Queues { get; }

    /// <summary>
    /// Serves queues in strict priority order: an earlier queue is always tried before a later
    /// one, and a later queue is reached only when every earlier queue has no due work. A
    /// continuously busy high-priority queue can therefore starve the tail indefinitely — this
    /// is the deliberate trade-off of strict priority.
    /// </summary>
    /// <param name="Queues">The queues to serve, highest priority first.</param>
    public sealed record Strict(IReadOnlyList<string> Queues) : DispatchPolicy
    {
        /// <summary>
        /// Creates a strict-priority policy from queue names listed highest priority first, so a
        /// two-queue policy reads as naturally as <c>new DispatchPolicy.Strict("emails", "reports")</c>.
        /// </summary>
        /// <param name="queues">The queues to serve, highest priority first.</param>
        public Strict(params string[] queues) : this((IReadOnlyList<string>)queues) { }

        /// <summary>The queues this policy serves, highest priority first.</summary>
        public override IReadOnlyList<string> Queues { get; } = Queues;
    }

    /// <summary>
    /// Shares claim opportunity across queues in proportion to their weights using smooth
    /// weighted round-robin. Weights such as 6:3:1 are honoured exactly and deterministically,
    /// with no random clumping — the higher-weight queue is served more often, but every queue
    /// keeps making progress. A queue with no due work yields its turn to the others.
    /// </summary>
    /// <param name="Weights">Each served queue paired with its relative weight (higher serves more often).</param>
    public sealed record Weighted(IReadOnlyList<(string Queue, int Weight)> Weights) : DispatchPolicy
    {
        /// <summary>The served queues, in the order their weights were declared.</summary>
        public override IReadOnlyList<string> Queues { get; } = [.. Weights.Select(w => w.Queue)];
    }
}

/// <summary>
/// The classic smooth weighted round-robin selector, adapted for work conservation: the
/// claim order is every Queue by descending credit, and the Queue that actually served is
/// the one charged. Credits are clamped at the total weight so an empty Queue cannot bank
/// an unbounded burst. Fully deterministic — ties break by declaration order.
/// </summary>
internal sealed class SmoothWeightedRoundRobin
{
    private readonly string[] _queues;
    private readonly long[] _weights;
    private readonly long[] _credit;
    private readonly long _totalWeight;

    public SmoothWeightedRoundRobin(IReadOnlyList<(string Queue, int Weight)> weights)
    {
        if (weights.Count == 0 || weights.Any(w => w.Weight < 1))
        {
            throw new ArgumentException("Weighted dispatch needs at least one queue, all weights >= 1.", nameof(weights));
        }
        _queues = [.. weights.Select(w => w.Queue)];
        _weights = [.. weights.Select(w => (long)w.Weight)];
        _credit = new long[_queues.Length];
        _totalWeight = _weights.Sum();
    }

    /// <summary>Advances one selection step and returns all Queues, highest credit first.</summary>
    public IReadOnlyList<string> NextOrder()
    {
        for (var i = 0; i < _credit.Length; i++)
        {
            _credit[i] = Math.Min(_credit[i] + _weights[i], _totalWeight);
        }
        return [.. Enumerable.Range(0, _queues.Length)
            .OrderByDescending(i => _credit[i])
            .ThenBy(i => i)
            .Select(i => _queues[i])];
    }

    /// <summary>Charges the Queue that actually served a claim.</summary>
    public void Charge(string queue)
    {
        var index = Array.IndexOf(_queues, queue);
        if (index >= 0)
        {
            _credit[index] -= _totalWeight;
        }
    }

    /// <summary>
    /// Sizes a batched pass: tallies how many of <paramref name="slots"/> claim slots each Queue
    /// would win, returned in declaration order, computed against a SCRATCH COPY of the credits.
    /// This is PURE — it never mutates the persistent credit, so a pass that is sized but then
    /// dropped (interrupted by a re-poll, or ended by an empty claim before its per-Queue batches
    /// drain) strands no credit. The persistent credit advances only through <see cref="AdvanceServed"/>,
    /// called as each batch is actually issued. The counts are identical to single-stepping
    /// <see cref="NextOrder"/> then <see cref="Charge"/> of each winner from the current credit.
    /// </summary>
    public IReadOnlyList<int> Allocate(int slots)
    {
        var counts = new int[_queues.Length];
        // Scratch copy: sizing must not move persistent credit — that is deferred to issue time.
        var credit = (long[])_credit.Clone();
        for (var slot = 0; slot < slots; slot++)
        {
            var winner = 0;
            for (var i = 0; i < credit.Length; i++)
            {
                credit[i] = Math.Min(credit[i] + _weights[i], _totalWeight);
            }
            // Highest credit wins; ties break by declaration order (strictly-greater keeps the lower index).
            for (var i = 1; i < credit.Length; i++)
            {
                if (credit[i] > credit[winner])
                {
                    winner = i;
                }
            }
            credit[winner] -= _totalWeight;
            counts[winner]++;
        }
        return counts;
    }

    /// <summary>
    /// Advances the persistent credit by <paramref name="slots"/> served slots — one real
    /// <see cref="NextOrder"/>/<see cref="Charge"/> step each. Called as each batch is issued, so
    /// only slots actually served move credit: a fully-consumed pass ends in exactly the credit a
    /// per-slot pump would (the same sequence <see cref="Allocate"/> simulated on its scratch copy),
    /// while a pass whose tail batches are dropped advances only the issued slots and leaves the
    /// dropped Queues at the credit they had — never charged for work they did not get to serve.
    /// </summary>
    public void AdvanceServed(int slots)
    {
        for (var slot = 0; slot < slots; slot++)
        {
            // Inline the single NextOrder()/Charge step that AdvanceServed reads — advance every
            // credit, scan for the max, charge it — without NextOrder's per-call sort+allocation:
            // only the highest-credit Queue is ever read, so building a fully ordered array each
            // slot is wasted GC pressure proportional to throughput on the weighted path. Same scan
            // as Allocate runs on its scratch copy, here against the persistent credit.
            var winner = 0;
            for (var i = 0; i < _credit.Length; i++)
            {
                _credit[i] = Math.Min(_credit[i] + _weights[i], _totalWeight);
            }
            // Highest credit wins; ties break by declaration order (strictly-greater keeps the lower index).
            for (var i = 1; i < _credit.Length; i++)
            {
                if (_credit[i] > _credit[winner])
                {
                    winner = i;
                }
            }
            _credit[winner] -= _totalWeight;
        }
    }
}
