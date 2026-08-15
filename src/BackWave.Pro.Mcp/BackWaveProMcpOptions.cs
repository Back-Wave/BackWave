using Microsoft.AspNetCore.Http;

namespace BackWave.Pro.Mcp;

// Shape fixed by the mcp-0004 grilling (dashboard-mirror callbacks + AuthorizeSetConcurrencyLimit);
// XML doc language mirrors BackWaveDashboardOptions so a host wiring both surfaces reads one policy.
/// <summary>
/// Configures the BackWave MCP server. Authorization is delegation, never ownership: each
/// permission is a callback you supply, so BackWave never sees your users or roles — it asks
/// your host whether the current request may do a thing.
/// </summary>
/// <remarks>
/// Reading is one permission — <see cref="AuthorizeView"/> — and defaults to <b>allow</b>, so
/// the MCP server works the moment you mount it (reading is harmless and keeps local development
/// frictionless). The write permissions (requeue, cancel, pause/resume queue, trigger schedule,
/// set concurrency limit) and the sensitive-data permission all default to <b>deny</b>. So a host
/// that mounts the server without configuring authorization presents a safe, read-only tool
/// surface, and every write capability must be consciously granted, one action at a time. A tool
/// whose permission is denied for the current request is hidden from the client's tool list, and
/// calling it directly returns a tool-execution error — so default-deny also means denied tools
/// simply do not appear until you opt in.
/// </remarks>
public sealed class BackWaveProMcpOptions
{
    /// <summary>
    /// The View Permission: may this request see the MCP tool surface at all? Defaults to allow —
    /// suitable for local development only; production hosts delegate to their own authorization
    /// here. A denied request sees an empty tool list, and any direct tool call it makes returns
    /// a tool-execution error.
    /// </summary>
    public Func<HttpContext, ValueTask<bool>> AuthorizeView { get; set; }
        = _ => ValueTask.FromResult(true);

    /// <summary>
    /// May this request read raw content that may carry secrets or PII — job payload and output
    /// bytes? Held separate from <see cref="AuthorizeView"/> so a reader can be granted the tool
    /// surface without being granted that content. This is a read gate, not a write action, but it
    /// is default-<b>deny</b> like the write permissions: you must consciously grant it. Honored
    /// only while <see cref="ExposeSensitiveData"/> (and the environment kill-switch) leave
    /// exposure on; you can switch payloads off entirely regardless of this permission. Defaults
    /// to denying every request.
    /// </summary>
    public Func<HttpContext, ValueTask<bool>> AuthorizeViewSensitiveData { get; set; }
        = _ => ValueTask.FromResult(false);

    /// <summary>
    /// Whether the host allows sensitive content (job payload and output bytes) to leave storage
    /// for the MCP server at all. Defaults to <b>true</b>; set to <b>false</b> for a host that
    /// never wants payloads surfaced regardless of who holds
    /// <see cref="AuthorizeViewSensitiveData"/>. The effective switch also honors an environment
    /// kill-switch: the <c>BACKWAVE_MCP_DISABLE_SENSITIVE_DATA</c> environment variable, set to a
    /// truthy value (<c>1</c>/<c>true</c>/<c>yes</c>/<c>on</c>), forces exposure off no matter
    /// this flag. Effective exposure = <c>ExposeSensitiveData &amp;&amp; !envKill</c>.
    /// </summary>
    public bool ExposeSensitiveData { get; set; } = true;

    /// <summary>
    /// May this request requeue a job — move a dead-lettered or quarantined job back to scheduled
    /// so it runs again? A write action, recorded to the audit trail with the actor
    /// <see cref="ResolveActor"/> supplies. Defaults to denying every request, so the requeue tool
    /// does not appear until you grant it.
    /// </summary>
    public Func<HttpContext, ValueTask<bool>> AuthorizeRequeue { get; set; }
        = _ => ValueTask.FromResult(false);

    /// <summary>
    /// May this request cancel a job — request cooperative cancellation of a non-terminal job?
    /// Also gates cancelling a whole workflow, which fans the per-job cancel out over the
    /// workflow's members. A write action, recorded to the audit trail. Defaults to denying every
    /// request, so the cancel tools do not appear until you grant it.
    /// </summary>
    public Func<HttpContext, ValueTask<bool>> AuthorizeCancel { get; set; }
        = _ => ValueTask.FromResult(false);

    /// <summary>
    /// May this request pause or resume a queue? One permission gates both directions. A write
    /// action, recorded to the audit trail. Defaults to denying every request, so the pause and
    /// resume tools do not appear until you grant it.
    /// </summary>
    public Func<HttpContext, ValueTask<bool>> AuthorizePauseQueue { get; set; }
        = _ => ValueTask.FromResult(false);

    /// <summary>
    /// May this request trigger a recurring schedule — mint one instance of it to run now, without
    /// waiting for its next due time? A write action, recorded to the audit trail. Defaults to
    /// denying every request, so the trigger tool does not appear until you grant it.
    /// </summary>
    public Func<HttpContext, ValueTask<bool>> AuthorizeTriggerSchedule { get; set; }
        = _ => ValueTask.FromResult(false);

    /// <summary>
    /// May this request set a queue's cluster-wide concurrency limit — the ceiling on how many of
    /// that queue's jobs run at once across every node? A write action, recorded to the audit
    /// trail. Defaults to denying every request, so the limit tool does not appear until you
    /// grant it.
    /// </summary>
    public Func<HttpContext, ValueTask<bool>> AuthorizeSetConcurrencyLimit { get; set; }
        = _ => ValueTask.FromResult(false);

    /// <summary>
    /// Resolves the acting operator's identity, stamped into every write action's audit record.
    /// Defaults to the authenticated user name, falling back to "mcp". BackWave owns no users, so
    /// the host shapes this — for example from an API key or bearer token your own middleware
    /// authenticated in front of the mount. Identity a connecting MCP client asserts about itself
    /// is not used: it is unauthenticated.
    /// </summary>
    public Func<HttpContext, string> ResolveActor { get; set; }
        = context => context.User.Identity?.Name ?? "mcp";

    /// <summary>
    /// The effective sensitive-data exposure switch: <see cref="ExposeSensitiveData"/> AND the
    /// absence of the <c>BACKWAVE_MCP_DISABLE_SENSITIVE_DATA</c> environment kill-switch. A host
    /// that never wants payloads leaving storage can flip either; both must agree before any
    /// payload bytes are read for the MCP server.
    /// </summary>
    public bool SensitiveDataExposureEnabled => ExposeSensitiveData && !SensitiveDataKilledByEnv();

    /// <summary>The environment-variable kill-switch name; a truthy value forces exposure off.</summary>
    internal const string DisableSensitiveDataEnvVar = "BACKWAVE_MCP_DISABLE_SENSITIVE_DATA";

    // Same truthy parsing as the dashboard's BACKWAVE_DASHBOARD_DISABLE_SENSITIVE_DATA; the two
    // surfaces deliberately read per-surface variables (mcp-0004).
    private static bool SensitiveDataKilledByEnv()
    {
        var raw = Environment.GetEnvironmentVariable(DisableSensitiveDataEnvVar)?.Trim();
        return raw is "1"
            || string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "yes", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "on", StringComparison.OrdinalIgnoreCase);
    }
}
