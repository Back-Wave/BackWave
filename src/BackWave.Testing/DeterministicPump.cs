using System.Diagnostics;
using BackWave.Diagnostics;
using BackWave.Driver;
using BackWave.Jobs;
using BackWave.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BackWave.Testing;

/// <summary>
/// A minimal deterministic Shell for tests: feeds events to the Node Driver and executes
/// its Commands against the store at explicit instants the caller supplies. Handlers may
/// be long-running — their executions stay in flight across pump calls, and cooperative
/// cancellation reaches them through the CancellationToken the pump owns.
/// </summary>
internal sealed class DeterministicPump(
    NodeDriver driver, IJobStore store, JobRegistry registry, IServiceProvider services,
    TimeProvider? clock = null, string consumerGroup = "backwave", ILogger? logger = null)
{
    // The clock the execute edge measures messaging.process.duration against. Defaults to system time; a
    // caller (e.g. the Simulator) passes its virtual clock so the histogram reads virtual time. Pure
    // side-effect emit — nothing in the Driver or Core reads it, so it never perturbs determinism.
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    // The worker-group name stamped as messaging.consumer.group.name on the receive/process spans.
    private readonly string _consumerGroup = consumerGroup;

    // The logs pillar (optional): defaults to a no-op NullLogger so simulation stays silent and
    // byte-identical - a log is never a decision input, so a captured logger changes nothing but the
    // emitted signal. The [LoggerMessage] catalog guards on IsEnabled, so the NullLogger path allocates
    // nothing.
    private readonly ILogger _log = logger ?? NullLogger.Instance;

    private sealed record InFlight(
        JobRecord Job, Task<ExecutionOutcome> Execution, CancellationTokenSource Cancellation, JobContext Context,
        Activity? Process);

    private readonly List<InFlight> _inFlight = [];

    // The pending settlement of each in-flight Attempt's process telemetry, keyed by (JobId, Attempt):
    // its held-open process span (possibly null) plus the Wire Name and Queue the settling report needs
    // to tag the dead-letter counter, so the settling outcome can land a retry-scheduled / dead-lettered
    // event on the span before it stops (the same Shell-side keyed-stash mechanism as the buffers below).
    // Drained by ReportOutcomeBatch; the abandon path closes its own span directly, since an abandoned
    // Attempt never reports an outcome.
    private readonly Dictionary<(Guid JobId, int Attempt), (Activity? Span, string WireName, string Queue)> _pendingProcessOutcomes = [];

    // Failure Detail stashed at the execution edge, keyed by (JobId, Attempt), the same Shell-side
    // mechanism the production pump uses (storage-contract §5.12, ADR 0011) — never routed through
    // the Core. Drained by ReportOutcome: passed to the store on a Failure, removed otherwise.
    private readonly Dictionary<(Guid JobId, int Attempt), string> _failureDetail = [];

    // Runtime Job Tags (ADR 0022) the handler buffered in its JobContext, stashed at the execution
    // edge — same Shell-side mechanism as Failure Detail. Drained by ReportOutcome and passed as the
    // Tag delta that rides the fenced outcome write (success and graceful-failure both).
    private readonly Dictionary<(Guid JobId, int Attempt), JobTags> _bufferedTags = [];

    // Opaque Job Output (ADR 0026) the handler emitted via ctx.SetOutput, stashed at the execution edge
    // the same Shell-side way as Tags. Drained by ReportOutcome and passed on the fenced outcome write;
    // the store persists it only on a Success outcome (a superseded or failed Attempt discards it).
    private readonly Dictionary<(Guid JobId, int Attempt), ReadOnlyMemory<byte>> _bufferedOutput = [];

    // Polls for due work at `now` and drains everything that can finish.
    public Task PumpAsync(DateTimeOffset now) => DriveAsync(new NodeEvent.PollDue(now), now);

    // Heartbeats the in-flight executions at `now`.
    public Task HeartbeatAsync(DateTimeOffset now) => DriveAsync(new NodeEvent.HeartbeatDue(now), now);

    private async Task DriveAsync(NodeEvent first, DateTimeOffset now)
    {
        var events = new Queue<NodeEvent>();
        events.Enqueue(first);

        while (true)
        {
            while (events.TryDequeue(out var nodeEvent))
            {
                foreach (var command in driver.Step(nodeEvent))
                {
                    await ExecuteAsync(command, events, now);
                }
            }

            // A signaled cancellation is cooperative: wait for that handler to unwind —
            // its finally blocks run, then its outcome event is collected like any other.
            foreach (var cancelling in _inFlight.Where(f => f.Cancellation.IsCancellationRequested))
            {
                await cancelling.Execution;
            }

            // Give started executions a chance to finish synchronously, then turn every
            // completed one into its outcome event. Repeat until nothing more completes.
            await Task.Yield();
            var completed = _inFlight.Where(f => f.Execution.IsCompleted).ToList();
            if (completed.Count == 0)
            {
                return;
            }

            foreach (var flight in completed)
            {
                _inFlight.Remove(flight);
                // The execution left the pool: release its worker slot (backwave.worker.slots.active).
                BackWaveDiagnostics.RecordWorkerSlotReleased(flight.Job);
                var verdict = await flight.Execution;
                // Hold the process span open, keyed like the buffers below, so the settling outcome can
                // land its retry-scheduled / dead-lettered event before the span stops; carry the Wire Name
                // and Queue alongside so the dead-letter counter can tag the destination at the report edge.
                _pendingProcessOutcomes[(flight.Job.JobId, flight.Job.Attempt)] = (flight.Process, flight.Job.WireName, flight.Job.Queue);
                // Stash the buffered runtime Tags (ADR 0022) so ReportOutcome flushes them onto the
                // fenced write — on both the success and gracefully-failed paths.
                if (flight.Context.BufferedTags.Count > 0)
                {
                    _bufferedTags[(flight.Job.JobId, flight.Job.Attempt)] = flight.Context.BufferedTags;
                }
                // Stash any buffered Job Output (ADR 0026) the same way - set even on the failure path
                // (a handler may SetOutput then throw); the store drops it for any non-Success outcome.
                if (flight.Context.BufferedOutput is { } bufferedOutput)
                {
                    _bufferedOutput[(flight.Job.JobId, flight.Job.Attempt)] = bufferedOutput;
                }
                // The execution edge already classified this Attempt (RunHandlerAsync) and the telemetry
                // it emitted rode that same verdict, so the outcome event and the metrics can never
                // disagree about what a cancellation meant.
                events.Enqueue(verdict.Failure is { } exception
                    ? StashFailureAndReport(flight.Job, exception, now)
                    : verdict.IsCancelled
                        ? new NodeEvent.ExecutionCancelled(flight.Job, "operator-cancel", now)
                        : new NodeEvent.ExecutionSucceeded(flight.Job, now));
            }
        }
    }

    // Stashes the failing Attempt's Failure Detail Shell-side (storage-contract §5.12), keyed by
    // (JobId, Attempt), then reports the plain failure event — the Core only ever sees the bounded
    // Error message.
    private NodeEvent StashFailureAndReport(JobRecord job, Exception exception, DateTimeOffset now)
    {
        _failureDetail[(job.JobId, job.Attempt)] =
            $"{exception.GetType().FullName}: {exception.Message}\n{exception.StackTrace}";
        return new NodeEvent.ExecutionFailed(job, exception.Message, now);
    }

    private async ValueTask ExecuteAsync(Command command, Queue<NodeEvent> events, DateTimeOffset now)
    {
        switch (command)
        {
            case Command.ExpireLeases expire:
                var reclaimed = await store.ExpireLeasesAsync(now, expire.MaxJobs, expire.Queues, expire.Disposition);
                if (reclaimed > 0)
                {
                    BackWaveLog.LeasesReclaimed(_log, _consumerGroup, reclaimed);
                }
                break;

            case Command.PurgeTerminal purge:
                var purged = await store.PurgeTerminalAsync(purge.StateClass, purge.TerminalBefore, purge.MaxJobs);
                events.Enqueue(new NodeEvent.PurgeCompleted(purge, purged, now));
                break;

            case Command.LoadSchedules:
                var schedules = await store.ListSchedulesAsync();
                if (schedules.Count > 0)
                {
                    events.Enqueue(new NodeEvent.SchedulesLoaded(schedules, now));
                }
                break;

            case Command.MintDue mint:
                var minted = await store.MintDueAsync(mint.Decisions);
                events.Enqueue(new NodeEvent.MintCompleted(minted, now)); // the Driver decides whether to re-poll
                break;

            case Command.RequestPoll repoll:
                // The Driver asked to poll again now; the pump only obeys (no scheduling judgment).
                events.Enqueue(new NodeEvent.PollDue(repoll.Now));
                break;

            case Command.ClaimBatch claim:
                using (var claimActivity = BackWaveDiagnostics.StartReceive(claim.WorkerId, _consumerGroup))
                {
                    var jobs = await store.ClaimAsync(
                        new ClaimRequest(claim.WorkerId, claim.Queues, claim.MaxJobs, claim.LeaseDuration, now));
                    BackWaveDiagnostics.RecordClaimed(claimActivity, jobs, now);
                    foreach (var job in jobs)
                    {
                        // A claim is the start of an Attempt: its Lease is now held (Trace).
                        BackWaveLog.LeaseAcquired(_log, job.JobId, job.WireName, job.Attempt, job.Queue);
                    }
                    // Always report the claim's completion, an empty result included: the Driver reserved this
                    // batch's slots at issue and frees them here, so an empty claim must still land or the
                    // reservation would strand and wedge the pool.
                    events.Enqueue(new NodeEvent.ClaimCompleted(jobs, now));
                }
                break;

            case Command.ExecuteJob execute:
                switch (registry.Route(execute.Job))
                {
                    case RouteResult.Unroutable unroutable:
                        events.Enqueue(new NodeEvent.ExecutionUnroutable(execute.Job, unroutable.Reason, now));
                        break;
                    case RouteResult.Routed routed:
                        var cancellation = new CancellationTokenSource();
                        var context = new JobContext
                        {
                            JobId = execute.Job.JobId,
                            Attempt = execute.Job.Attempt,
                            // The read side of Job Output (ADR 0026): the handler may pull a transitive
                            // ancestor's output, resolved through the Storage Contract above the boundary.
                            DependencyResolver = new StoreDependencyResolver(store),
                            // Raw bytes so the Pro workflow layer can read a baked Workflow Input envelope.
                            Payload = execute.Job.Payload,
                            // The registry so a Pro workflow accessor can resolve a step type to its Wire
                            // Name and output codec (typed ctx.Output<TStep,TOut> / ctx.SetOutput).
                            Registry = registry,
                            // The running step's Wire Name so the typed SetOutput accessor can reject a
                            // handler that tries to emit output for a step other than the one it runs.
                            RunningWireName = routed.Registration.WireName,
                        };
                        // Open the process span here, then restore the ambient Activity: the span is held
                        // open past handler return (settled on the outcome report), so it must not leak
                        // into the pump's drive loop as the current Activity. RunHandlerAsync re-scopes it
                        // as current for the handler body only.
                        var ambient = Activity.Current;
                        var activity = BackWaveDiagnostics.StartProcess(execute.Job, _consumerGroup);
                        Activity.Current = ambient;
                        var execution = RunHandlerAsync(routed.Registration, execute.Job, routed.Payload, context, activity, cancellation.Token);
                        _inFlight.Add(new InFlight(execute.Job, execution, cancellation, context, activity));
                        // The execution entered the pool: occupy a worker slot (backwave.worker.slots.active),
                        // released when the flight completes or is abandoned.
                        BackWaveDiagnostics.RecordWorkerSlotOccupied(execute.Job);
                        break;
                }
                break;

            case Command.ReportOutcomeBatch batch:
                // The Driver coalesces terminal outcomes (ADR 0035) and flushes them as one command. For
                // each row, drain the Shell-stashed Failure Detail (passed to the store only on a Failure)
                // and runtime Tag delta keyed by (JobId, Attempt), then apply the whole batch in one fenced
                // store write. The Driver decides whether each applied outcome warrants a re-poll.
                var reports = new List<OutcomeReport>(batch.Outcomes.Count);
                foreach (var outcome in batch.Outcomes)
                {
                    _failureDetail.Remove((outcome.JobId, outcome.Attempt), out var rowDetail);
                    _bufferedTags.Remove((outcome.JobId, outcome.Attempt), out var rowTags);
                    var rowHasOutput = _bufferedOutput.Remove((outcome.JobId, outcome.Attempt), out var rowOutput);
                    // Settle the held-open process span: a Failure lands retry-scheduled or dead-lettered,
                    // then the span stops. This is the authoritative disposition - the Driver computed the
                    // next-due time this row carries.
                    if (_pendingProcessOutcomes.Remove((outcome.JobId, outcome.Attempt), out var span))
                    {
                        BackWaveDiagnostics.CompleteProcess(span.Span, outcome.Outcome, span.WireName, span.Queue);
                        LogSettlement(outcome, span.WireName, span.Queue);
                    }
                    reports.Add(new OutcomeReport(outcome.JobId, outcome.WorkerId, outcome.Attempt, outcome.Outcome)
                    {
                        FailureDetail = outcome.Outcome is JobOutcome.Failure ? rowDetail : null,
                        AddedTags = rowTags,
                        // Cast the null arm to the nullable type: ReadOnlyMemory<byte> converts implicitly
                        // from byte[] (and null → byte[]), so an un-cast `: null` would give a non-null
                        // EMPTY blob, persisting a 0-byte output on every silent success instead of none.
                        Output = rowHasOutput ? rowOutput : (ReadOnlyMemory<byte>?)null,
                    });
                }
                foreach (var rowResult in await store.ReportOutcomesAsync(reports, now))
                {
                    events.Enqueue(new NodeEvent.OutcomeReported(rowResult.JobId, rowResult.Result, now));
                }
                break;

            case Command.Heartbeat heartbeat:
                var results = await store.HeartbeatAsync(
                    heartbeat.WorkerId, heartbeat.JobIds, heartbeat.LeaseDuration, now);
                events.Enqueue(new NodeEvent.HeartbeatCompleted(results, now));
                break;

            case Command.SignalCancellation signal:
                _inFlight.Single(f => f.Job.JobId == signal.JobId).Cancellation.Cancel();
                break;

            case Command.AbandonExecution abandon:
                var lost = _inFlight.SingleOrDefault(f => f.Job.JobId == abandon.JobId);
                if (lost is not null)
                {
                    _inFlight.Remove(lost);
                    // The execution left the pool: release its worker slot (backwave.worker.slots.active).
                    BackWaveDiagnostics.RecordWorkerSlotReleased(lost.Job);
                    // The Lease lapsed and was reclaimed: this Attempt reports no outcome, so close its
                    // process span here with a lease-lost event rather than leaking it open.
                    BackWaveDiagnostics.RecordLeaseLost(lost.Process);
                    using (BackWaveLog.BeginJobScope(
                        _log, lost.Job.JobId, lost.Job.WireName, lost.Job.Attempt, lost.Job.Queue))
                    {
                        BackWaveLog.LeaseLost(_log);
                    }
                    await lost.Cancellation.CancelAsync();
                }
                break;
        }
    }

    private async Task<ExecutionOutcome> RunHandlerAsync(
        JobRegistration registration, JobRecord job, object payload, JobContext context, Activity? activity, CancellationToken cancellationToken)
    {
        // Monotonic start for the messaging.process.duration histogram, read off the injected clock so it
        // measures virtual time under simulation — a side-effect emit that never re-enters the Core.
        var executionStart = _clock.GetTimestamp();
        // Scope the process span as the current Activity for the handler body only (so a handler that
        // reads Activity.Current sees it), then restore. The span is NOT disposed here: it is held open
        // and settled when the outcome reports (or on abandon), so its turning point reads on its timeline.
        var previous = Activity.Current;
        Activity.Current = activity;
        // The claim/execute log scope stamps job_id / wire_name / attempt / queue onto every event under
        // it; the settlement events (retry/dead-letter) re-open an identical scope at the report edge.
        using var scope = BackWaveLog.BeginJobScope(_log, job.JobId, job.WireName, job.Attempt, job.Queue);
        try
        {
            BackWaveLog.ExecutionStarted(_log);
            // schedule.delay: drift from the job's scheduled (due) time to this actual execution start,
            // read off the injected clock so it measures virtual time under the harness.
            BackWaveDiagnostics.RecordScheduleDelay(job, _clock.GetUtcNow());
            await registration.Execute(services, payload, context, cancellationToken);
            BackWaveDiagnostics.RecordExecuted(activity, job, ExecutionOutcome.Success);
            BackWaveDiagnostics.RecordJobDuration(job, _clock.GetElapsedTime(executionStart), ExecutionOutcome.Success);
            BackWaveLog.ExecutionCompleted(_log);
            return ExecutionOutcome.Success;
        }
        catch (Exception exception)
        {
            // The execution boundary: handler exceptions become data here, so the Driver and Core never
            // see exception control flow. Classify BEFORE recording and return that one verdict, so the
            // record edge is told what happened rather than guessing it from the exception type: only a
            // cancellation this pump signalled is an operator cancel; a handler's own OCE (e.g. an
            // internal timeout) is a plain failure and is counted as one.
            var verdict = exception is OperationCanceledException && cancellationToken.IsCancellationRequested
                ? ExecutionOutcome.Cancelled
                : ExecutionOutcome.Failed(exception);
            BackWaveDiagnostics.RecordExecuted(activity, job, verdict);
            BackWaveDiagnostics.RecordJobDuration(job, _clock.GetElapsedTime(executionStart), verdict);
            BackWaveLog.ExecutionCompleted(_log);
            return verdict;
        }
        finally
        {
            Activity.Current = previous;
        }
    }

    // Logs a settled outcome under a re-opened job scope (the execution scope closed when the handler
    // returned): a Failure with a next-due time is a retry (Information), one without is a Dead-Letter
    // (Error). A success or superseded outcome adds no settlement event - the ExecutionCompleted log
    // already covers it.
    private void LogSettlement(ReportedOutcome outcome, string wireName, string queue)
    {
        if (outcome.Outcome is not JobOutcome.Failure failure)
        {
            return;
        }
        using (BackWaveLog.BeginJobScope(_log, outcome.JobId, wireName, outcome.Attempt, queue))
        {
            if (failure.NextDueTime is { } nextDue)
            {
                BackWaveLog.RetryScheduled(_log, nextDue);
            }
            else
            {
                BackWaveLog.DeadLettered(_log);
            }
        }
    }
}
