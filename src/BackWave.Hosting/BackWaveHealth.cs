using System.Collections.Concurrent;
using BackWave.Diagnostics;
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
    private readonly ConcurrentDictionary<(string Group, string Pump), DegradedState> _degraded = new();
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
            foreach (var (key, state) in _degraded)
            {
                byGroup.TryAdd(key.Group, $"{state.ExceptionType}: {state.Message}");
            }
            return byGroup;
        }
    }

    // The same groups, carrying the exception TYPE name alone. The health check renders this one,
    // because a provider's message names the host, the database and the login that failed, and a
    // description-rendering response writer puts a health-check description on an unauthenticated
    // probe endpoint. The message stays available through DegradedGroups, which an operator reads
    // in process rather than over the wire.
    internal IReadOnlyDictionary<string, string> DegradedGroupTypes
    {
        get
        {
            var byGroup = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (key, state) in _degraded)
            {
                byGroup.TryAdd(key.Group, state.ExceptionType);
            }
            return byGroup;
        }
    }

    internal void ReportHalted(string workerGroup, string pump, int groupPumpCount, Exception exception)
    {
        _groupPumpCount[workerGroup] = groupPumpCount;
        // The trigger id is read off the exception here rather than passed in, so the halt call site
        // cannot record one id and log another.
        _halted[(workerGroup, pump)] = new HaltState(
            exception.GetType().FullName ?? exception.GetType().Name, exception.Message)
        {
            Trigger = (exception as InvariantViolationException)?.Trigger,
        };
        _degraded.TryRemove((workerGroup, pump), out _); // a halt supersedes this pump's degraded mark
    }

    internal void ReportDegraded(string workerGroup, string pump, Exception exception) =>
        _degraded[(workerGroup, pump)] = new DegradedState(exception.GetType().Name, exception.Message);

    internal void ReportRecovered(string workerGroup, string pump) =>
        _degraded.TryRemove((workerGroup, pump), out _); // clears only this pump's mark, never a sibling's

    /// <summary>One pump's transient-fault mark, split so the health check can render the type alone.</summary>
    private sealed record DegradedState(string ExceptionType, string Message);

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
/// alongside the exception's message and - when a named invariant check raised the halt - the stable
/// trigger id that names which invariant broke.
/// <para>
/// <see cref="Trigger"/> is an init property and NOT a third constructor parameter. The package ships
/// on nuget.org, and a record's constructor, its <c>Deconstruct</c>, and its <c>Equals</c> all follow
/// the primary-constructor list, so adding a parameter there breaks every compiled caller at load
/// time. An init property adds the value and keeps the two-element constructor and deconstruction
/// binary-compatible: <c>new HaltState(type, message)</c> and <c>var (type, message) = haltState;</c>
/// both still work.
/// </para>
/// </summary>
/// <param name="ExceptionType">The full name of the exception type that halted the group.</param>
/// <param name="Message">The exception's message.</param>
public sealed record HaltState(string ExceptionType, string Message)
{
    /// <summary>
    /// The invariant a named check found broken, or <see langword="null"/> when the group stopped on a
    /// fault no check named. The same id the halt log's <c>invariant_trigger</c> parameter carries.
    /// </summary>
    public InvariantTrigger? Trigger { get; init; }

    /// <summary>Renders the cause as <c>ExceptionType: Message</c>.</summary>
    /// <returns>The exception type and message joined by a colon.</returns>
    /// <remarks>
    /// The trigger is deliberately left out. This rendering is what the health check puts in its
    /// description, which an operator's response writer may serialize into a probe payload; keeping it
    /// byte-identical keeps the trigger id out of every serialized artifact, so renaming a trigger costs
    /// one metric tag value and one log parameter and nothing that outlives the process. Read
    /// <see cref="Trigger"/> when you want the id.
    /// </remarks>
    public override string ToString() => $"{ExceptionType}: {Message}";
}

/// <summary>
/// A health check that reports unhealthy once a worker group has wholly halted, and degraded while a
/// group is still serving but impaired - partially halted, or marked degraded by a transient store
/// fault. Register it with the standard health-check pipeline, for example
/// <c>services.AddHealthChecks().AddCheck&lt;BackWaveHealthCheck&gt;("backwave")</c>, so an
/// orchestrator or load balancer can observe a fail-stop.
/// </summary>
/// <remarks>
/// <b>Breaking behavioural change.</b> This check previously returned only healthy or unhealthy and
/// never read <see cref="BackWaveHealth.DegradedGroups"/>, so a transient store fault - a connection
/// blip, a failover, a command timeout - reported <see cref="HealthStatus.Healthy"/>. It now reports
/// <see cref="HealthStatus.Degraded"/>. A readiness probe configured to fail on
/// <see cref="HealthStatus.Degraded"/> will restart pods on faults it used to ride out; map degraded to
/// success in your probe if that is not what you want. A wholly halted group is still the only
/// <see cref="HealthStatus.Unhealthy"/> result.
/// </remarks>
public sealed class BackWaveHealthCheck(BackWaveHealth health) : IHealthCheck
{
    /// <summary>
    /// Reports unhealthy - naming each halted group and its cause - once a group has wholly
    /// fail-stopped; degraded while a group is partially halted or marked degraded by a transient store
    /// fault; and healthy only when every group is running clean.
    /// </summary>
    /// <param name="context">The health-check context supplied by the pipeline.</param>
    /// <param name="cancellationToken">Unused; the check reads in-memory state and never blocks.</param>
    /// <returns>
    /// A completed task with an unhealthy result whose description lists the wholly halted groups and
    /// their causes; otherwise a degraded result naming the partially halted and degraded groups with
    /// their exception TYPE names only; or a healthy result when there are none.
    /// </returns>
    /// <remarks>
    /// <b>Breaking behavioural change:</b> a transient store fault used to report
    /// <see cref="HealthStatus.Healthy"/> and now reports <see cref="HealthStatus.Degraded"/>. See the
    /// remarks on <see cref="BackWaveHealthCheck"/>.
    /// </remarks>
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (!health.IsHealthy)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                "Worker Group fail-stop: " + string.Join("; ",
                    health.HaltedGroups.Select(g => $"{g.Key} ({g.Value})"))));
        }
        // Impaired but serving: a partially halted group is still claiming through its surviving pumps,
        // and a degraded group is retrying each poll. Neither is a fail-stop, and neither is clean.
        // The group name and the exception TYPE name only. This description is what a stock
        // response writer serializes onto a probe endpoint, and a provider's message carries the host,
        // the database and the login it failed to reach. HaltedGroups, PartiallyHaltedGroups and
        // DegradedGroups still carry the full message for an operator reading them in process.
        var impaired = health.PartiallyHaltedGroups.Select(g => $"{g.Key} ({g.Value.ExceptionType})")
            .Concat(health.DegradedGroupTypes.Select(g => $"{g.Key} ({g.Value})"))
            .ToList();
        return Task.FromResult(impaired.Count == 0
            ? HealthCheckResult.Healthy("All Worker Groups running.")
            : HealthCheckResult.Degraded(
                "Worker Group degraded: " + string.Join("; ", impaired)));
    }
}
