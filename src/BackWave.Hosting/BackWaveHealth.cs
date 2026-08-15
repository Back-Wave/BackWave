using System.Collections.Concurrent;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace BackWave.Hosting;

// Fail-stop vs. degraded split: ADR-0007 and its transient-fault amendment.
/// <summary>
/// The shared fail-stop state for every worker group in the host. When a worker group hits an
/// invariant violation it stops permanently — its leases lapse so healthy nodes inherit the work —
/// and this state turns unhealthy so the registered health check can page an operator while the rest
/// of the host keeps serving traffic. A transient store fault (for example a connection blip) is
/// treated differently: the group keeps running and is recorded as degraded rather than halted,
/// retrying on the next poll. Resolve this from the container to inspect health programmatically, or
/// register <see cref="BackWaveHealthCheck"/> to surface it through the standard health-check pipeline.
/// </summary>
public sealed class BackWaveHealth
{
    // Health is bookkept per Pump (a group may run several — its Pumps count) but surfaced at group
    // altitude (ADR 0037). Keying by (group, pump) stops one Pump's clean cycle from clearing a
    // sibling's degraded mark, and stops one Pump's halt from reading as the whole group down. A group
    // counts as wholly halted only once every one of its Pumps has halted; until then it is
    // partially halted — still serving through its surviving Pumps. A single-Pump group (the default)
    // has exactly one Pump, so a halt is always a whole-group halt: behaviour is unchanged from one Pump.
    private readonly ConcurrentDictionary<(string Group, string Pump), HaltState> _halted = new();
    private readonly ConcurrentDictionary<(string Group, string Pump), string> _degraded = new();
    private readonly ConcurrentDictionary<string, int> _groupPumpCount = new(StringComparer.Ordinal);

    /// <summary>
    /// <see langword="true"/> while no worker group has wholly halted; <see langword="false"/> once
    /// every pump of some group hits an invariant violation and stops, taking the group fully down. A
    /// group that is only degraded, or only partially halted (some pumps still claiming and executing),
    /// does not flip this — its surviving pumps keep the group serving.
    /// </summary>
    public bool IsHealthy => !_halted.Keys.Select(k => k.Group).Distinct().Any(IsWhollyHalted);

    /// <summary>
    /// The groups that have wholly halted — every one of their pumps stopped on an invariant violation —
    /// keyed by worker group name, each mapped to one of the violations that stopped it (its exception
    /// type and message). A group with surviving pumps appears in <see cref="PartiallyHaltedGroups"/>
    /// instead. Empty while no group is fully down.
    /// </summary>
    public IReadOnlyDictionary<string, HaltState> HaltedGroups => HaltsByGroup(wholly: true);

    /// <summary>
    /// The groups that have partially halted — at least one pump stopped on an invariant violation but
    /// others are still claiming and executing — keyed by worker group name, each mapped to one of the
    /// violations that stopped a pump. The group keeps serving through its surviving pumps (their leases
    /// inherit the halted pump's work), so this is a diagnostic signal, not a whole-group fail-stop.
    /// A group whose every pump has halted appears in <see cref="HaltedGroups"/> instead.
    /// </summary>
    public IReadOnlyDictionary<string, HaltState> PartiallyHaltedGroups => HaltsByGroup(wholly: false);

    /// <summary>
    /// The groups currently degraded by a transient store fault — at least one of their pumps hit a
    /// recoverable fault (for example a connection blip) — keyed by worker group name, each mapped to a
    /// short description of one such fault. These groups are still running and claiming work, retrying
    /// each poll — visible for diagnostics, but not a fail-stop condition.
    /// </summary>
    public IReadOnlyDictionary<string, string> DegradedGroups
    {
        get
        {
            var byGroup = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (key, description) in _degraded)
            {
                byGroup.TryAdd(key.Group, description);
            }
            return byGroup;
        }
    }

    internal void ReportHalted(string workerGroup, string pump, int groupPumpCount, Exception exception)
    {
        _groupPumpCount[workerGroup] = groupPumpCount;
        _halted[(workerGroup, pump)] = new HaltState(
            exception.GetType().FullName ?? exception.GetType().Name, exception.Message);
        _degraded.TryRemove((workerGroup, pump), out _); // a halt supersedes this pump's degraded mark
    }

    internal void ReportDegraded(string workerGroup, string pump, Exception exception) =>
        _degraded[(workerGroup, pump)] = $"{exception.GetType().Name}: {exception.Message}";

    internal void ReportRecovered(string workerGroup, string pump) =>
        _degraded.TryRemove((workerGroup, pump), out _); // clears only this pump's mark, never a sibling's

    private bool IsWhollyHalted(string group) =>
        _halted.Count(k => k.Key.Group == group) >= _groupPumpCount.GetValueOrDefault(group, 1);

    private Dictionary<string, HaltState> HaltsByGroup(bool wholly)
    {
        var byGroup = new Dictionary<string, HaltState>(StringComparer.Ordinal);
        foreach (var groupHalts in _halted.GroupBy(k => k.Key.Group))
        {
            if (IsWhollyHalted(groupHalts.Key) == wholly)
            {
                byGroup[groupHalts.Key] = groupHalts.First().Value;
            }
        }
        return byGroup;
    }
}

/// <summary>
/// The cause of a halted worker group: the full name of the exception type that stopped it, retained
/// alongside the exception's message.
/// </summary>
/// <param name="ExceptionType">The full name of the exception type that halted the group.</param>
/// <param name="Message">The exception's message.</param>
public sealed record HaltState(string ExceptionType, string Message)
{
    /// <summary>Renders the cause as <c>ExceptionType: Message</c>.</summary>
    /// <returns>The exception type and message joined by a colon.</returns>
    public override string ToString() => $"{ExceptionType}: {Message}";
}

/// <summary>
/// A health check that reports unhealthy once any worker group has halted. Register it with the
/// standard health-check pipeline, for example
/// <c>services.AddHealthChecks().AddCheck&lt;BackWaveHealthCheck&gt;("backwave")</c>, so an
/// orchestrator or load balancer can observe a fail-stop.
/// </summary>
public sealed class BackWaveHealthCheck(BackWaveHealth health) : IHealthCheck
{
    /// <summary>
    /// Reports healthy while every worker group is running, or unhealthy — naming each halted group
    /// and its cause — once any group has fail-stopped.
    /// </summary>
    /// <param name="context">The health-check context supplied by the pipeline.</param>
    /// <param name="cancellationToken">Unused; the check reads in-memory state and never blocks.</param>
    /// <returns>
    /// A completed task with a healthy result when no group has halted, or an unhealthy result whose
    /// description lists the halted groups and their causes.
    /// </returns>
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        => Task.FromResult(health.IsHealthy
            ? HealthCheckResult.Healthy("All Worker Groups running.")
            : HealthCheckResult.Unhealthy(
                "Worker Group fail-stop: " + string.Join("; ",
                    health.HaltedGroups.Select(g => $"{g.Key} ({g.Value})"))));
}
