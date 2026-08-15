namespace BackWave.Jobs;

/// <summary>The outcome of routing a claimed job through the Job Registry.</summary>
internal abstract record RouteResult
{
    private RouteResult() { }

    /// <summary>The job has a registered handler and its payload decoded.</summary>
    public sealed record Routed(JobRegistration Registration, object Payload) : RouteResult;

    /// <summary>No registered handler, or the payload no longer decodes — Quarantine it.</summary>
    public sealed record Unroutable(string Reason) : RouteResult;
}
