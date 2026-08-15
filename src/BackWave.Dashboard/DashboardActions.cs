namespace BackWave.Dashboard;

// Each write affordance maps to its own default-deny permission on BackWaveDashboardOptions (ADR 0010).
/// <summary>
/// Per-request write affordances handed to the dashboard's views: which Operator Actions the
/// caller may take (each its own default-deny Permission) and the antiforgery token to stamp into
/// every action form. Computed once per request; a view renders a control only when its flag is
/// set, so a view-only caller sees the dashboard but none of the controls. Surfaced as a render
/// parameter on the dashboard's components; not something a host constructs.
/// </summary>
public sealed record DashboardActions
{
    /// <summary>Whether the caller may requeue a dead-lettered or quarantined job.</summary>
    public required bool CanRequeue { get; init; }

    /// <summary>Whether the caller may cancel a non-terminal job.</summary>
    public required bool CanCancel { get; init; }

    /// <summary>Whether the caller may pause or resume a queue.</summary>
    public required bool CanPauseQueue { get; init; }

    /// <summary>Whether the caller may trigger a recurring schedule to run now.</summary>
    public required bool CanTriggerSchedule { get; init; }

    /// <summary>
    /// The ViewSensitiveData Permission: may the caller see raw content that may carry secrets or
    /// PII (job payload bytes today). A READ gate, not an Operator Action, so it carries no
    /// antiforgery token — but it is its own default-deny Permission, held separate from View.
    /// </summary>
    public required bool CanViewSensitiveData { get; init; }

    /// <summary>The antiforgery form field name every action form must post under.</summary>
    public required string AntiforgeryFieldName { get; init; }

    /// <summary>The antiforgery request token every action form must carry.</summary>
    public required string AntiforgeryToken { get; init; }

    /// <summary>The default read-only posture: no actions, no antiforgery dependency touched.</summary>
    public static readonly DashboardActions None = new()
    {
        CanRequeue = false,
        CanCancel = false,
        CanPauseQueue = false,
        CanTriggerSchedule = false,
        CanViewSensitiveData = false,
        AntiforgeryFieldName = "",
        AntiforgeryToken = "",
    };
}
