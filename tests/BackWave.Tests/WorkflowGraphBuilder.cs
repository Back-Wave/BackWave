using BackWave.Storage;

namespace BackWave.Tests;

/// <summary>
/// Test helper for the below-boundary Workflow spine: constructs prepared <see cref="WorkflowDefinition"/>
/// members directly from typed job payloads, so a spine test drives the store's atomic enqueue op without
/// going through the above-boundary typed authoring builder. Each member is byte-identical to what the
/// authoring surface lowers to - the job's registered wire name, queue, serialized payload, and default
/// tags - keeping these tests focused on the storage contract (cancel, restart, retention, append) rather
/// than on graph authoring.
/// </summary>
internal static class WorkflowGraphBuilder
{
    /// <summary>Prepares one workflow member from a typed job payload, resolving its wire name, queue, and payload through the client's registry.</summary>
    internal static NewJob Member<TJob>(
        BackWaveClient client, Guid jobId, TJob job, DateTimeOffset dueTime, params Guid[] parents)
        where TJob : notnull
    {
        var registration = client.Registry.GetByJobType(typeof(TJob));
        return new NewJob(jobId, registration.WireName, registration.Serialize(job), registration.Queue, dueTime)
        {
            Parents = parents,
            Tags = registration.DefaultTags,
        };
    }
}
