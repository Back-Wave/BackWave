namespace BackWave.Torture;

/// <summary>
/// The deterministic identifier universe of one torture run, derived entirely from the seed so
/// every client — across tasks AND across OS processes — engineers collisions against the same
/// keys without any coordination channel. Collision pressure is the whole point: duplicate JobIds,
/// duplicate WorkflowIds, and first-config races on shared fresh queue names are how the
/// 0178/0193/0194/0195 bug class gets a chance to fire.
/// </summary>
internal sealed class KeySpace(ulong seed)
{
    private const ulong JobIdStream = 0xA11CE_0001;
    private const ulong WorkflowIdStream = 0xA11CE_0002;

    public ulong Seed { get; } = seed;

    /// <summary>The statically-limited queue whose concurrency limit is set once, before the workload, and audited.</summary>
    public string GovernedQueue => "tq-governed";

    /// <summary>Ordinary work queues.</summary>
    public IReadOnlyList<string> GeneralQueues { get; } = ["tq-gen-0", "tq-gen-1", "tq-gen-2", "tq-gen-3"];

    /// <summary>Queues that pause/resume/limit-set ops race against claims — the first-config-race surface.</summary>
    public IReadOnlyList<string> ConfigQueues { get; } = ["tq-cfg-0", "tq-cfg-1", "tq-cfg-2", "tq-cfg-3", "tq-cfg-4", "tq-cfg-5"];

    public IReadOnlyList<string> AllQueues { get; } =
        ["tq-governed", "tq-gen-0", "tq-gen-1", "tq-gen-2", "tq-gen-3", "tq-cfg-0", "tq-cfg-1", "tq-cfg-2", "tq-cfg-3", "tq-cfg-4", "tq-cfg-5"];

    /// <summary>Routable wire names — clients "execute" these and report Success/Failure/Cancelled.</summary>
    public IReadOnlyList<string> RoutableWires { get; } =
        ["torture.work.0", "torture.work.1", "torture.work.2", "torture.work.3", "torture.work.4", "torture.work.5"];

    /// <summary>
    /// Designated-unroutable wire names. Clients always report these Unroutable and never execute
    /// them, which is what makes the QuarantineNotExecuted audit sound: a Quarantined job whose wire
    /// name is not in this set is a violation, and so is a journaled execution of one that is.
    /// </summary>
    public IReadOnlyList<string> UnroutableWires { get; } = ["torture.unroutable.0", "torture.unroutable.1"];

    public bool IsUnroutable(string wireName) => wireName.StartsWith("torture.unroutable.", StringComparison.Ordinal);

    /// <summary>The small shared tag vocabulary — small on purpose, so tag rows collide.</summary>
    public IReadOnlyList<Storage.JobTag> TagVocabulary { get; } =
    [
        Storage.JobTag.Label("hot"),
        Storage.JobTag.Label("cold"),
        Storage.JobTag.Label("batch"),
        Storage.JobTag.Keyed("tenant", "1"),
        Storage.JobTag.Keyed("tenant", "2"),
        Storage.JobTag.Keyed("tenant", "3"),
    ];

    /// <summary>
    /// The i-th collision JobId. Clients pick indices from a small window that slides with elapsed
    /// wall time, so concurrent clients keep racing the same *fresh* ids for the whole run instead
    /// of exhausting a fixed pool into permanent Duplicate results.
    /// </summary>
    public Guid CollisionJobId(int index) => DeterministicGuid(JobIdStream, (ulong)index);

    /// <summary>The i-th collision WorkflowId — same sliding-window idea as <see cref="CollisionJobId"/>.</summary>
    public Guid CollisionWorkflowId(int index) => DeterministicGuid(WorkflowIdStream, (ulong)index);

    private Guid DeterministicGuid(ulong stream, ulong index)
    {
        var bytes = new byte[16];
        var a = SplitMix64.Next(Seed ^ stream ^ (index * 0x9E3779B97F4A7C15UL));
        var b = SplitMix64.Next(a);
        BitConverter.TryWriteBytes(bytes.AsSpan(0, 8), a);
        BitConverter.TryWriteBytes(bytes.AsSpan(8, 8), b);
        return new Guid(bytes);
    }
}

/// <summary>SplitMix64 — the seed-derivation primitive (stateless, so cross-process derivation is trivial).</summary>
internal static class SplitMix64
{
    public static ulong Next(ulong state)
    {
        var z = state + 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}
