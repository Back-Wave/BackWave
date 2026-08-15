using Microsoft.AspNetCore.Http;

namespace BackWave.Dashboard;

/// <summary>
/// Configures the BackWave dashboard. Authorization is delegation, never ownership: each
/// permission is a callback you supply, so BackWave never sees your users or roles — it asks
/// your host whether the current request may do a thing.
/// </summary>
/// <remarks>
/// Reading is one permission — <see cref="AuthorizeView"/> — and defaults to <b>allow</b>, so
/// the dashboard works the moment you mount it (reading is harmless and keeps local development
/// frictionless). The four write permissions (requeue, cancel, pause/resume queue, trigger
/// schedule) and the sensitive-data permission all default to <b>deny</b>. So a host that mounts
/// the dashboard without configuring authorization gets a safe, read-only dashboard, and every
/// write capability must be consciously granted, one action at a time. A control renders only
/// when its permission passes, so default-deny also means the buttons simply do not appear until
/// you opt in.
/// </remarks>
public sealed record BackWaveDashboardOptions
{
    /// <summary>
    /// The View Permission: may this request see the dashboard at all? Defaults to allow —
    /// suitable for local development only; production hosts delegate to their own
    /// authorization here. A denied request gets 403 and no dashboard bytes.
    /// </summary>
    public Func<HttpContext, ValueTask<bool>> AuthorizeView { get; init; }
        = _ => ValueTask.FromResult(true);

    /// <summary>
    /// May this request see raw content that may carry secrets or PII — job payload bytes? Held
    /// separate from <see cref="AuthorizeView"/> so a reader can be granted the dashboard without
    /// being granted that content. This is a read gate, not a write action, so it needs no
    /// antiforgery token — but it is default-<b>deny</b> like the write permissions: you must
    /// consciously grant it. Honored only while <see cref="ExposeSensitiveData"/> (and the
    /// environment kill-switch) leave exposure on; you can switch payloads off entirely regardless
    /// of this permission. Defaults to denying every request.
    /// </summary>
    public Func<HttpContext, ValueTask<bool>> AuthorizeViewSensitiveData { get; init; }
        = _ => ValueTask.FromResult(false);

    /// <summary>
    /// Whether the host allows sensitive content (payload bytes today) to leave storage for the
    /// dashboard at all. Defaults to <b>true</b>; set to <b>false</b> for a host that never wants
    /// payloads surfaced regardless of who holds <see cref="AuthorizeViewSensitiveData"/>. The
    /// effective switch also honors an environment kill-switch: the
    /// <c>BACKWAVE_DASHBOARD_DISABLE_SENSITIVE_DATA</c> environment variable, set to a truthy
    /// value (<c>1</c>/<c>true</c>/<c>yes</c>/<c>on</c>), forces exposure off no matter this flag.
    /// Effective exposure = <c>ExposeSensitiveData &amp;&amp; !envKill</c>.
    /// </summary>
    public bool ExposeSensitiveData { get; init; } = true;

    /// <summary>
    /// May this request requeue a job — move a dead-lettered or quarantined job back to scheduled
    /// so it runs again? A write action: it is antiforgery-protected and recorded to the audit
    /// trail. Defaults to denying every request, so the Requeue control does not render until you
    /// grant it.
    /// </summary>
    public Func<HttpContext, ValueTask<bool>> AuthorizeRequeue { get; init; }
        = _ => ValueTask.FromResult(false);

    /// <summary>
    /// May this request cancel a job — request cooperative cancellation of a non-terminal job? A
    /// write action: antiforgery-protected and audited. Defaults to denying every request, so the
    /// Cancel control does not render until you grant it.
    /// </summary>
    public Func<HttpContext, ValueTask<bool>> AuthorizeCancel { get; init; }
        = _ => ValueTask.FromResult(false);

    /// <summary>
    /// May this request pause or resume a queue? A write action: antiforgery-protected and
    /// audited. Defaults to denying every request, so the Pause/Resume control does not render
    /// until you grant it.
    /// </summary>
    public Func<HttpContext, ValueTask<bool>> AuthorizePauseQueue { get; init; }
        = _ => ValueTask.FromResult(false);

    /// <summary>
    /// May this request trigger a recurring schedule — mint one instance of it to run now,
    /// without waiting for its next due time? A write action: antiforgery-protected and audited.
    /// Defaults to denying every request, so the Trigger control does not render until you grant
    /// it.
    /// </summary>
    public Func<HttpContext, ValueTask<bool>> AuthorizeTriggerSchedule { get; init; }
        = _ => ValueTask.FromResult(false);

    /// <summary>
    /// Resolves the acting operator's identity, stamped into every Operator Action's audit
    /// record. Defaults to the authenticated user name, falling back to "dashboard". BackWave
    /// owns no users, so the host shapes this.
    /// </summary>
    public Func<HttpContext, string> ResolveActor { get; init; }
        = context => context.User.Identity?.Name ?? "dashboard";

    /// <summary>
    /// How often live views re-render and push over their Server-Sent Events connection.
    /// Each open page holds one connection and the server re-reads the Monitor at this cadence,
    /// pushing only when the rendered fragment changed — so a faster interval costs Monitor
    /// reads, not bandwidth. Defaults to 4 seconds; raise it to ease load, lower it for a
    /// snappier feel.
    /// </summary>
    public TimeSpan LiveRefreshInterval { get; init; } = TimeSpan.FromSeconds(4);

    /// <summary>
    /// The effective sensitive-data exposure switch: <see cref="ExposeSensitiveData"/> AND the
    /// absence of the <c>BACKWAVE_DASHBOARD_DISABLE_SENSITIVE_DATA</c> environment kill-switch. A
    /// host that never wants payloads leaving storage can flip either; both must agree before any
    /// payload bytes are read for the dashboard.
    /// </summary>
    public bool SensitiveDataExposureEnabled => ExposeSensitiveData && !SensitiveDataKilledByEnv();

    /// <summary>The environment-variable kill-switch name; a truthy value forces exposure off.</summary>
    internal const string DisableSensitiveDataEnvVar = "BACKWAVE_DASHBOARD_DISABLE_SENSITIVE_DATA";

    private static bool SensitiveDataKilledByEnv()
    {
        var raw = Environment.GetEnvironmentVariable(DisableSensitiveDataEnvVar)?.Trim();
        return raw is "1"
            || string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "yes", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "on", StringComparison.OrdinalIgnoreCase);
    }
}
