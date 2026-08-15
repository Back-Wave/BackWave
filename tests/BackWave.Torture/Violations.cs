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

/// <summary>One confirmed oracle violation — a torture failure is always a bug, never noise.</summary>
internal sealed record TortureViolation(string Invariant, string Message, Guid? JobId = null);
