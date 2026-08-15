namespace BackWave.Storage;

/// <summary>
/// The global <b>job history policy</b>: how much of a job's transition log a store records. It is a
/// configurable capability rather than a universal guarantee — a host opts up or down, trading
/// observability for less write amplification and storage growth on high-throughput or cost-sensitive
/// deployments.
/// <para>
/// The policy is a <b>ladder</b> — each rung adds to the one below, never sideways:
/// <list type="bullet">
/// <item><see cref="Off"/> — record nothing: no transition rows from any operation.</item>
/// <item><see cref="Transitions"/> — record the transition rows, but no failure detail.</item>
/// <item><see cref="TransitionsAndFailureDetail"/> — the full log: transition rows plus the
///   (clamped) failure detail on a failing transition.</item>
/// </list>
/// Failure detail is the inner rung: you cannot capture detail without recording the transition it
/// hangs on, so it sits strictly above <see cref="Transitions"/>.
/// </para>
/// <para>
/// The policy <b>gates writes, never schema</b>: the transition table always exists, so changing the
/// policy is a configuration change, never a migration. It is an input to a run — the same inputs
/// plus the same policy produce the same result — so it never affects determinism.
/// </para>
/// </summary>
public enum JobHistoryPolicy
{
    /// <summary>Record nothing — no transition rows are written by any operation.</summary>
    Off,

    /// <summary>Record the transition rows, but never any failure detail (it is forced to null).</summary>
    Transitions,

    /// <summary>
    /// The full log (the default): transition rows plus the clamped failure detail on a failing
    /// transition. The dashboard timeline works out of the box at this rung.
    /// </summary>
    TransitionsAndFailureDetail,
}

/// <summary>
/// Resolves the <b>effective</b> job history policy from the configured policy and the failure-detail
/// environment kill-switch. The kill-switch gates <b>recording</b> of failure detail (not viewing):
/// when it is set, an effective <see cref="JobHistoryPolicy.TransitionsAndFailureDetail"/> is
/// downgraded to <see cref="JobHistoryPolicy.Transitions"/> so transitions still record but detail
/// capture is suppressed. This guards against stack traces — which routinely carry secrets or PII —
/// landing in the host's own database.
/// </summary>
public static class JobHistoryPolicyResolver
{
    /// <summary>The name of the environment variable that, when set truthy, suppresses failure-detail capture (downgrading the top rung).</summary>
    public const string DisableFailureDetailEnvVar = "BACKWAVE_DISABLE_FAILURE_DETAIL";

    /// <summary>
    /// Computes the effective policy a store should enforce: the configured policy, with the top rung
    /// downgraded to <see cref="JobHistoryPolicy.Transitions"/> when the failure-detail kill-switch is
    /// set in the environment.
    /// </summary>
    /// <param name="configured">The policy the host configured.</param>
    /// <returns>The effective policy after applying the kill-switch.</returns>
    public static JobHistoryPolicy Resolve(JobHistoryPolicy configured)
        => configured == JobHistoryPolicy.TransitionsAndFailureDetail && FailureDetailKilledByEnv()
            ? JobHistoryPolicy.Transitions
            : configured;

    /// <summary>
    /// Whether the failure-detail kill-switch is currently set truthy in the environment (any of
    /// <c>1</c>, <c>true</c>, <c>yes</c>, or <c>on</c>, case-insensitive).
    /// </summary>
    /// <returns>True when the kill-switch is on.</returns>
    public static bool FailureDetailKilledByEnv()
    {
        var raw = Environment.GetEnvironmentVariable(DisableFailureDetailEnvVar)?.Trim();
        return raw is not null
            && (raw.Equals("1", StringComparison.Ordinal)
                || raw.Equals("true", StringComparison.OrdinalIgnoreCase)
                || raw.Equals("yes", StringComparison.OrdinalIgnoreCase)
                || raw.Equals("on", StringComparison.OrdinalIgnoreCase));
    }
}
