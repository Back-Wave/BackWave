using System.Data.Common;
using System.Diagnostics;
using BackWave.Diagnostics;
using BackWave.Jobs;
using BackWave.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BackWave;

/// <summary>
/// The enqueue surface for scheduling background work: enqueue a job now or in the future,
/// chain a job to run after another finishes, and define recurring (cron) schedules. Enqueuing
/// "now" is just a due time of the current instant — the same mechanism and code path as
/// future-scheduled work.
/// </summary>
/// <param name="store">The storage adapter the jobs are written to.</param>
/// <param name="registry">
/// The registry of known job types, keyed by Wire Name; the client serializes each job through
/// its registration and routes it to the registered Queue.
/// </param>
/// <param name="clock">
/// The time source. Defaults to the system clock; a deterministic test harness can supply a
/// virtual clock so enqueue times are reproducible. A future due time always defers a job — it is
/// never treated as "due now".
/// </param>
/// <param name="loggerFactory">
/// Optional. Supplies the logger the client writes structured enqueue events to. Null (the default)
/// disables logging with no allocation - enqueue behaves identically either way.
/// </param>
public sealed class BackWaveClient(
    IJobStore store, JobRegistry registry, TimeProvider? clock = null, ILoggerFactory? loggerFactory = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    // The logs pillar is emit-only: a null factory yields a NullLogger whose IsEnabled is always false,
    // so the source-generated catalog calls below never format or allocate. Enqueue is never a decision
    // input to the logger, so this cannot perturb behaviour.
    private readonly ILogger _log = loggerFactory?.CreateLogger("BackWave") ?? NullLogger.Instance;

    /// <summary>
    /// Enqueues a job to become eligible to run at <paramref name="dueTime"/>. Pass the current
    /// instant to run it as soon as a worker is free; pass a future instant to defer it. When a
    /// <paramref name="transaction"/> is supplied, the job is written atomically with your own
    /// database writes in the same transaction — it exists only if your transaction commits, so no
    /// outbox pattern is needed.
    /// </summary>
    /// <typeparam name="TJob">The registered job payload type; its registration controls serialization, Queue, and default tags.</typeparam>
    /// <param name="job">The job payload to enqueue. Serialized through its registration.</param>
    /// <param name="dueTime">When the job becomes eligible to run. A future time defers it; the current instant runs it as soon as possible.</param>
    /// <param name="queue">The Queue to enqueue into. Null uses the job type's registered Queue.</param>
    /// <param name="tags">Optional tags attached at enqueue. The job type's default tags are always added on top of these.</param>
    /// <param name="transaction">
    /// Optional. When supplied, the job commits or rolls back atomically with your own writes on the
    /// same transaction. The storage adapter must support transactional enqueue.
    /// </param>
    /// <param name="cancellationToken">Cancels the enqueue request.</param>
    /// <returns>The new job's id, for later tracking or for use as a dependency parent.</returns>
    /// <exception cref="NotSupportedException">A <paramref name="transaction"/> was supplied but the storage adapter does not support transactional enqueue.</exception>
    /// <exception cref="ArgumentException">The serialized payload exceeds the store's maximum payload size; store a reference (an id or blob key) instead of the data itself.</exception>
    /// <exception cref="InvalidOperationException">The store rejected the enqueue for another reason (for example, a duplicate job id).</exception>
    /// <example>
    /// <code>
    /// var jobId = await client.EnqueueAsync(new SendReceipt(orderId), DateTimeOffset.UtcNow);
    /// </code>
    /// </example>
    public async ValueTask<Guid> EnqueueAsync<TJob>(
        TJob job,
        DateTimeOffset dueTime,
        string? queue = null,
        JobTags? tags = null,
        DbTransaction? transaction = null,
        CancellationToken cancellationToken = default)
        where TJob : notnull
    {
        if (transaction is not null && !store.SupportsTransactionalEnqueue)
        {
            throw new NotSupportedException(
                "This storage adapter does not support Transactional Enqueue " +
                "(SupportsTransactionalEnqueue is false); enqueue without a transaction instead.");
        }

        var registration = registry.GetByJobType(typeof(TJob));
        var jobId = Guid.NewGuid();
        var payload = registration.Serialize(job);
        var targetQueue = queue ?? registration.Queue;
        // Type-default Tags are additive only (ADR 0022): they always union into the caller's
        // Tags, never subtracted. Set semantics collapse an identical Tag; merge order is moot.
        var mergedTags = MergeTags(registration.DefaultTags, tags);

        using var activity = BackWaveDiagnostics.StartSend(registration.WireName, targetQueue, jobId);
        var result = await store.EnqueueAsync(
            new NewJob(
                jobId,
                registration.WireName,
                payload,
                targetQueue,
                dueTime)
            {
                // The traceparent travels with the payload: the execution span becomes a
                // child of this enqueue, even hours later on another node.
                TraceContext = activity?.Id ?? Activity.Current?.Id,
                Tags = mergedTags,
            },
            now: _clock.GetUtcNow(),
            transaction,
            cancellationToken).ConfigureAwait(false);

        if (result == EnqueueResult.Ok)
        {
            BackWaveDiagnostics.RecordEnqueued(registration.WireName, targetQueue);
            BackWaveLog.JobEnqueued(_log, jobId, registration.WireName, targetQueue);
        }
        return result switch
        {
            EnqueueResult.Ok => jobId,
            EnqueueResult.PayloadTooLarge => throw new ArgumentException(
                $"Payload for wire name '{registration.WireName}' is {payload.Length} bytes, " +
                $"which exceeds the MaxPayloadBytes bound. Store a reference (id, blob key) instead of the data itself.",
                nameof(job)),
            _ => throw new InvalidOperationException($"Enqueue failed: {result}."),
        };
    }

    /// <summary>
    /// Enqueues a job that waits until another job (<paramref name="parentId"/>) reaches a terminal
    /// state before it becomes eligible to run. By default it runs only if the parent succeeded
    /// ("send the receipt after the charge succeeds"); set <paramref name="mode"/> to run once the
    /// parent reaches any terminal state, success or not ("release the hold either way").
    /// </summary>
    /// <typeparam name="TJob">The registered job payload type.</typeparam>
    /// <param name="job">The dependent job's payload. Serialized through its registration.</param>
    /// <param name="parentId">The id of the job this one waits on (typically the return value of an earlier enqueue).</param>
    /// <param name="enqueuedAt">When the dependent was created. Defaults to the current instant; a released dependent becomes due at release time if that is later.</param>
    /// <param name="mode">Whether to run only on the parent's success, or once the parent reaches any terminal state.</param>
    /// <param name="queue">The Queue to enqueue into. Null uses the job type's registered Queue.</param>
    /// <param name="tags">Optional tags attached at enqueue. The job type's default tags are always added on top of these.</param>
    /// <param name="cancellationToken">Cancels the enqueue request.</param>
    /// <returns>The new dependent job's id.</returns>
    /// <exception cref="ArgumentException">No job exists with the given <paramref name="parentId"/>.</exception>
    /// <exception cref="InvalidOperationException">The store rejected the enqueue for another reason.</exception>
    /// <example>
    /// <code>
    /// var chargeId  = await client.EnqueueAsync(new ChargeCard(orderId), DateTimeOffset.UtcNow);
    /// var receiptId = await client.EnqueueDependencyAsync(new SendReceipt(orderId), chargeId);
    /// </code>
    /// </example>
    public async ValueTask<Guid> EnqueueDependencyAsync<TJob>(
        TJob job,
        Guid parentId,
        DateTimeOffset? enqueuedAt = null,
        DependencyMode mode = DependencyMode.OnSuccess,
        string? queue = null,
        JobTags? tags = null,
        CancellationToken cancellationToken = default)
        where TJob : notnull
    {
        // The client owns the clock (§1): default the stamp to the injected TimeProvider so a
        // caller never hand-rolls wall-clock time and break Virtual Time testability. Pass an
        // explicit instant only when a caller legitimately needs one.
        var at = enqueuedAt ?? _clock.GetUtcNow();
        var registration = registry.GetByJobType(typeof(TJob));
        var jobId = Guid.NewGuid();
        var targetQueue = queue ?? registration.Queue;
        var mergedTags = MergeTags(registration.DefaultTags, tags);

        using var activity = BackWaveDiagnostics.StartSend(registration.WireName, targetQueue, jobId);
        var result = await store.EnqueueAsync(
            new NewJob(
                jobId,
                registration.WireName,
                registration.Serialize(job),
                targetQueue,
                at) // a released Dependency becomes due at release time if later
            {
                Parents = [parentId],
                Mode = mode,
                TraceContext = activity?.Id ?? Activity.Current?.Id,
                Tags = mergedTags,
            },
            now: _clock.GetUtcNow(),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (result == EnqueueResult.Ok)
        {
            BackWaveDiagnostics.RecordEnqueued(registration.WireName, targetQueue);
            BackWaveLog.JobEnqueued(_log, jobId, registration.WireName, targetQueue);
        }
        return result switch
        {
            EnqueueResult.Ok => jobId,
            EnqueueResult.UnknownParent => throw new ArgumentException(
                $"Parent job {parentId} does not exist.", nameof(parentId)),
            _ => throw new InvalidOperationException($"Enqueue failed: {result}."),
        };
    }

    /// <summary>
    /// Defines a recurring schedule that mints a new <typeparamref name="TJob"/> instance on every
    /// cron tick, or redefines an existing schedule with the same <paramref name="scheduleId"/>.
    /// The schedule starts watching from <paramref name="now"/>, so only ticks in the future are
    /// minted — defining a schedule does not back-fill past occurrences.
    /// </summary>
    /// <typeparam name="TJob">The registered job payload type the schedule mints.</typeparam>
    /// <param name="scheduleId">A stable id for the schedule; re-using it redefines that schedule.</param>
    /// <param name="cron">The cron expression that defines the tick times.</param>
    /// <param name="template">The payload template; each minted instance carries a copy of it.</param>
    /// <param name="now">The instant the schedule begins watching from. Defaults to the current instant; only ticks after it are minted.</param>
    /// <param name="queue">The Queue minted instances go to. Null uses the job type's registered Queue.</param>
    /// <param name="timeZone">The IANA time-zone id the cron evaluates in (for example "America/New_York"). Null evaluates in UTC.</param>
    /// <param name="catchUp">What to do about ticks missed while the system was down: skip them, or mint a single make-up run.</param>
    /// <param name="noOverlap">When true, a tick is skipped while a previously minted instance is still running.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A task that completes once the schedule has been stored.</returns>
    /// <exception cref="ArgumentException">The <paramref name="timeZone"/> id cannot be resolved on this host.</exception>
    /// <example>
    /// <code>
    /// await client.UpsertRecurringAsync(
    ///     "nightly-report",
    ///     Cron.Daily(hour: 2),
    ///     new GenerateReport(),
    ///     timeZone: "America/New_York");
    /// </code>
    /// </example>
    public ValueTask UpsertRecurringAsync<TJob>(
        string scheduleId,
        Core.CronExpression cron,
        TJob template,
        DateTimeOffset? now = null,
        string? queue = null,
        string? timeZone = null,
        CatchUpPolicy catchUp = CatchUpPolicy.Skip,
        bool noOverlap = false,
        CancellationToken cancellationToken = default)
        where TJob : notnull
    {
        // Validate at the door (same "reject loudly, never silently ignore" posture as
        // Enqueue): an unresolvable zone id never reaches storage to fail-stop a pump later.
        // The cron is already a parsed CronExpression, so only the zone can be bad here.
        if (!Core.ScheduleValidation.TryResolve(cron.Canonical, timeZone, out _, out _, out var error))
        {
            throw new ArgumentException(error, nameof(timeZone));
        }

        // The client owns the clock (§1): a new schedule's Cursor defaults to the injected
        // TimeProvider, matching plain enqueue, so only future ticks mint and Virtual Time
        // harnesses stay deterministic without a caller-passed instant.
        var resolvedNow = now ?? _clock.GetUtcNow();
        var registration = registry.GetByJobType(typeof(TJob));
        return store.UpsertScheduleAsync(
            new ScheduleRecord
            {
                ScheduleId = scheduleId,
                Cron = cron.Canonical,
                WireName = registration.WireName,
                Payload = registration.Serialize(template),
                Queue = queue ?? registration.Queue,
                Cursor = resolvedNow,
                TimeZoneId = timeZone,
                CatchUp = catchUp,
                NoOverlap = noOverlap,
            },
            cancellationToken);
    }

    /// <summary>
    /// Removes a recurring schedule so it mints no further instances. Instances it has already
    /// minted are left untouched and run to completion. Removing an unknown schedule is a no-op.
    /// </summary>
    /// <param name="scheduleId">The id of the schedule to remove.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A task that completes once the schedule has been removed.</returns>
    public ValueTask RemoveRecurringAsync(string scheduleId, CancellationToken cancellationToken = default)
        => store.RemoveScheduleAsync(scheduleId, cancellationToken);

    // The Core dependencies BackWave Pro's relocated Workflow extension methods drive through: the
    // store the graph is written to, the registry the builder serializes against, and the clock that
    // stamps enqueue times. Internal — the Pro package reaches them via InternalsVisibleTo, so the
    // free Core exposes no Workflow API.
    internal IJobStore Store => store;
    internal JobRegistry Registry => registry;
    internal TimeProvider Clock => _clock;

    // Unions a job type's default Tags with the caller-supplied ones. Additive only: the defaults
    // are always present — there is no way to drop one at enqueue. Set semantics collapse identical
    // Tags, so an overlapping default appears exactly once.
    private static JobTags MergeTags(JobTags defaultTags, JobTags? supplied)
        => supplied is null || supplied.Count == 0
            ? defaultTags
            : JobTags.From(defaultTags.Concat(supplied));
}
