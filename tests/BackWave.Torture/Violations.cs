namespace BackWave.Torture;

/// <summary>
/// Torture oracle identifiers — the InvariantId family ported to quiescent/journal form (issue
/// 0200), plus the client-observation checks only a live-adapter run can express.
/// </summary>
internal static class TortureInvariant
{
    // End-state + Transition Log audit (store-side).
    public const string LegalInitialState = "LegalInitialState";
    public const string LegalTransition = "LegalTransition";
    public const string AttemptMonotonic = "AttemptMonotonic";
    public const string AttemptCeiling = "AttemptCeiling";
    public const string TerminalStable = "TerminalStable";
    public const string TerminalTimestamp = "TerminalTimestamp";
    public const string LeaseOwnerCleared = "LeaseOwnerCleared";
    public const string LeaseOwnerPresent = "LeaseOwnerPresent";
    public const string QuarantineNotExecuted = "QuarantineNotExecuted";
    public const string NoAwaitingParentOrphan = "NoAwaitingParentOrphan";
    public const string CancelProvenance = "CancelProvenance";
    public const string DrainLiveness = "DrainLiveness";
    public const string DuplicateTagRows = "DuplicateTagRows";
    public const string DuplicateEdgeRows = "DuplicateEdgeRows";

    // Client-side observation journal cross-checks.
    public const string NoDoubleExecution = "NoDoubleExecution";
    public const string NoOverlap = "NoOverlap";
    public const string OutcomeProvenance = "OutcomeProvenance";
    public const string SlotDoubleRelease = "SlotDoubleRelease";
    public const string ConcurrencyLimit = "ConcurrencyLimit";
    public const string DuplicateEnqueueAccepted = "DuplicateEnqueueAccepted";
    public const string DuplicateWorkflowAccepted = "DuplicateWorkflowAccepted";
    public const string EnqueueDurability = "EnqueueDurability";
    public const string TagDurability = "TagDurability";
    public const string RawStoreException = "RawStoreException";
    public const string ClientCrash = "ClientCrash";
}

/// <summary>The run phase a finding was detected in - a mid-run hit and a post-drain hit differ.</summary>
internal static class TorturePhase
{
    public const string Workload = "workload";
    public const string Drain = "drain";
    public const string PostDrain = "post-drain";
}

/// <summary>One confirmed oracle violation — a torture failure is always a bug, never noise.</summary>
internal sealed record TortureViolation(string Invariant, string Message, Guid? JobId = null)
{
    /// <summary>When the sink recorded it (UTC) - a mid-run finding sits near its cause in the journal.</summary>
    public DateTimeOffset DetectedAt { get; init; }

    /// <summary>Which phase found it: workload (mid-run pass), drain, or post-drain.</summary>
    public string Phase { get; init; } = TorturePhase.PostDrain;
}

/// <summary>
/// One phase's collecting sink. The mid-run pass writes findings while the synthetic clients are
/// still running, so the list is locked; every finding is stamped with the phase and the instant it
/// was found on the way in, which costs the call sites nothing.
/// </summary>
internal sealed class ViolationSink(string phase)
{
    private readonly object _gate = new();
    private readonly List<TortureViolation> _violations = [];

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _violations.Count;
            }
        }
    }

    public void Add(TortureViolation violation)
    {
        var stamped = violation with { DetectedAt = DateTimeOffset.UtcNow, Phase = phase };
        lock (_gate)
        {
            _violations.Add(stamped);
        }
    }

    public void AddRange(IEnumerable<TortureViolation> violations)
    {
        foreach (var violation in violations)
        {
            Add(violation);
        }
    }

    public IReadOnlyList<TortureViolation> Snapshot()
    {
        lock (_gate)
        {
            return [.. _violations];
        }
    }
}
