namespace BackWave.Hosting;

/// <summary>
/// What a worker group's pump does when a call to the job store fails with a fault that is not
/// recognized as transient. Set it through <see cref="WorkerGroupOptions.UnclassifiedStoreFaults"/>.
/// <para>
/// A store fault is transient when the provider marks it so (<see cref="System.Data.Common.DbException.IsTransient"/>),
/// when it is a <see cref="System.TimeoutException"/>, or when the store adapter classifies it as transient.
/// A pump always retries a transient fault on the next poll. This setting decides only what happens to a
/// store fault that matches none of those.
/// </para>
/// </summary>
public enum UnclassifiedStoreFaultAction
{
    /// <summary>
    /// Fail-stop the pump, as for an invariant violation (the default). The pump stops claiming, its leases
    /// lapse so healthy nodes inherit the work, the health check reports the group, and the pump stays
    /// stopped until the process restarts. Choose this when an unrecognized store fault more likely means a
    /// defect than an outage.
    /// </summary>
    Halt,

    /// <summary>
    /// Keep the pump running and retry on the next poll, exactly as for a transient fault. The health check
    /// reports the group as degraded, and each such fault is logged at Warning. Choose this when a database
    /// failover can raise provider faults that no classifier recognizes and must not stop every pump in the
    /// cluster at once.
    /// <para>
    /// The same limits apply here as to a transient fault. If heartbeat calls keep failing, the pump's leases
    /// lapse and another Attempt of the job can start while the first handler still runs. The Effect-Once
    /// fence keeps the stored outcome correct, but the handler's side effects can happen twice. A poll cycle
    /// in which every store call succeeds clears the degraded mark, so the health check can read Healthy
    /// while one kind of store call keeps failing. Watch the Warning log as well as the health state.
    /// </para>
    /// <para>
    /// The source of the fault decides, not its type. A fault raised inside a store call degrades, and that
    /// includes an adapter's own argument check. A named invariant violation, a fault in the Driver or in the
    /// pump's own code, and any fault that does not come from a store call still fail-stop the pump. A Job
    /// Output or Job Tag rejection also fail-stops when the pump cannot match it to a row of its batch, or
    /// when it repeats past its retry bound. A failed maintenance sweep retries at the next maintenance
    /// interval, not on the next poll. A store
    /// defect that fails every call also does not stop the pump. It stays degraded and retries on every poll.
    /// </para>
    /// </summary>
    Degrade,
}
