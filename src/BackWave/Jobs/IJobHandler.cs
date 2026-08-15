namespace BackWave.Jobs;

/// <summary>
/// Executes one Attempt of a job. Under at-least-once delivery, every handler may run more than once
/// for the same job (a crash or lease lapse after the work but before the outcome is recorded leads
/// to a retry); making the work idempotent is the author's responsibility.
/// </summary>
/// <typeparam name="TJob">The job payload type this handler processes.</typeparam>
public interface IJobHandler<in TJob>
{
    /// <summary>
    /// Runs the job's work for one Attempt. Returning normally signals success; throwing signals
    /// failure, which schedules a retry until the job's attempt ceiling is reached.
    /// </summary>
    /// <param name="job">The deserialized job payload.</param>
    /// <param name="context">
    /// The per-Attempt execution context — exposes the job id and Attempt number, buffers Tags and
    /// the optional output, and reads ancestor output.
    /// </param>
    /// <param name="cancellationToken">
    /// Signaled when the Attempt is being torn down (for example, on shutdown or lease loss). Honor
    /// it to stop promptly.
    /// </param>
    /// <returns>A task that completes when the Attempt's work is done.</returns>
    Task HandleAsync(TJob job, JobContext context, CancellationToken cancellationToken);
}
