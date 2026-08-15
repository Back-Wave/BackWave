using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Threading.Channels;
using BackWave.Diagnostics;
using BackWave.Driver;
using BackWave.Jobs;
using BackWave.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BackWave.Hosting;

// <summary>
// The thin per-node pump (ADR-0006): a hosted service that feeds events to the sans-I/O
// Node Driver and executes its Commands through the Storage Contract. All I/O, clocks,
// and threads live here — the Driver only decides. Fail-stop (ADR-0007): an invariant
// violation halts this Worker Group (its Leases lapse, healthy nodes inherit the work,
// the health check goes red) but never crashes the host process.
// </summary>
internal sealed class WorkerGroupService(
    WorkerGroupOptions options,
    IJobStore store,
    JobRegistry registry,
    IServiceScopeFactory scopeFactory,
    BackWaveHealth health,
    ILogger<WorkerGroupService> logger,
    TimeProvider? clock = null) : BackgroundService
{
    // The pump owns the clock (§1): every instant stamped onto a Command (Claim, Heartbeat,
    // ExpireLeases, outcome reports) and every tick timestamp comes from here, so a host-registered
    // TimeProvider governs the pump exactly as it already governs the client and operator. Defaults
    // to TimeProvider.System — GetUtcNow() then equals _clock.GetUtcNow(), so an unconfigured host
    // is byte-for-byte unchanged. A test can register two offset clocks to model cross-node skew.
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    private sealed record InFlight(JobRecord Job, CancellationTokenSource Cancellation)
    {
        // <summary>
        // Set before signalling an operator cancel — the only OCE source that may go
        // terminal Cancelled. Every other cancellation is a failure or a lapsed Lease.
        // </summary>
        public volatile bool OperatorCancelRequested;
    }

    private readonly ConcurrentDictionary<Guid, InFlight> _inFlight = new();

    // <summary>
    // Failure Detail (§5.12, ADR 0011) stashed at the execution edge, keyed by (JobId, Attempt):
    // the full exception type/message/stack captured when a handler throws. Held HERE in the
    // Shell, never on a NodeEvent or Command, so it never crosses into the deterministic Core —
    // it only learns the Attempt failed. Drained by ReportOutcome (passed to the store on a
    // Failure, removed on any other outcome) so the map never leaks.
    // </summary>
    private readonly ConcurrentDictionary<(Guid JobId, int Attempt), string> _failureDetail = new();

    // <summary>
    // Runtime Job Tags (ADR 0022) the handler buffered in its <see cref="JobContext"/>, stashed at
    // the execution edge keyed by (JobId, Attempt) — exactly like Failure Detail and, like it, held
    // HERE in the Shell so they never reach a NodeEvent/Command or the deterministic Core. Drained by
    // ReportOutcome and passed to the store as the Tag delta that rides the fenced outcome write; a
    // gracefully-failed Attempt still flushes its Tags, so the stash is set on the success AND failure
    // paths and removed once reported.
    // </summary>
    private readonly ConcurrentDictionary<(Guid JobId, int Attempt), JobTags> _bufferedTags = new();

    // <summary>
    // The opaque <b>Job Output</b> blob (ADR 0026) the handler buffered in its <see cref="JobContext"/>
    // via <c>SetOutput</c>, stashed at the execution edge keyed by (JobId, Attempt) — exactly like the
    // Tag delta and held HERE in the Shell so it never reaches a NodeEvent/Command or the deterministic
    // Core. Drained by ReportOutcome and passed to the store, which persists it only on a Succeeded
    // outcome (it rides the same fence as the Tag delta and Failure Detail). Stashed whenever the
    // handler set output so a graceful failure still drains cleanly; removed once reported.
    // </summary>
    private readonly ConcurrentDictionary<(Guid JobId, int Attempt), ReadOnlyMemory<byte>> _bufferedOutput = new();

    // <summary>
    // The pending settlement of each in-flight Attempt's process telemetry, keyed by (JobId, Attempt)
    // like the buffers above: its held-open process span (possibly null - the dead-letter METRIC is
    // independent of tracing, so an entry is stashed even with no ActivityListener) plus the Wire Name
    // and Queue the settling report needs to tag the dead-letter counter. The settling outcome lands a
    // retry-scheduled / dead-lettered event on the span before it stops. Stashed only when an outcome
    // WILL report; an abandoned Attempt (a lost Lease, which reports nothing) closes its own span in the
    // execution task with a lease-lost event instead. Drained by ReportOutcome so the map never leaks.
    // </summary>
    private readonly ConcurrentDictionary<(Guid JobId, int Attempt), (Activity? Span, string WireName, string Queue)> _pendingProcessOutcomes = new();
    private readonly string _workerId = $"{Environment.MachineName}:{options.Name}:{Guid.NewGuid():N}";

    // <summary>0 = no poll pending, 1 = one queued; coalesces timer + hint polls (issue 0039).</summary>
    private int _pollQueued;

    // <summary>
    // The classification boundary (ADR-0007 amendment): a transient store fault retries, an
    // invariant violation (and anything unclassifiable) fail-stops. Both adapters surface
    // provider-transient conditions — connection reset, failover, deadlock victim, command
    // timeout — through <see cref="DbException.IsTransient"/>, so that one flag plus
    // <see cref="TimeoutException"/> is the whole transient set.
    // </summary>
    private bool IsTransientStoreFault(Exception exception) => exception switch
    {
        DbException { IsTransient: true } => true,
        TimeoutException => true,
        // An adapter may recognize provider-specific transient faults the generic IsTransient flag
        // misses (e.g. the SQLite adapter's SQLITE_BUSY/SQLITE_LOCKED) — consult it without the host
        // taking a dependency on any provider package (issue 0098, mirrors the IWakeUpHintSource probe).
        _ => store is IStoreFaultClassifier classifier && classifier.IsTransientFault(exception),
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await PumpAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown: in-flight Leases lapse and other nodes inherit the work.
        }
        catch (Exception exception)
        {
            // Fail-stop (ADR-0007): an invariant violation — or any fault we can't classify
            // as transient — halts this group only. No outcome reports, no more heartbeats:
            // the Leases lapse and healthy nodes inherit. The host process keeps serving.
            // One Critical log names the dead invariant (type, message, stack); the health
            // state retains the exception type, not just its message.
            HostingLog.WorkerGroupFailStopped(logger, options.Name, exception);
            // Per-pump health, surfaced at group altitude (ADR 0037): this Pump halts under its own
            // worker identity, so a sibling Pump's clean cycle never clears it and the group reads
            // wholly halted only once all options.Pumps Pumps are down.
            health.ReportHalted(options.Name, _workerId, options.Pumps, exception);
        }
        finally
        {
            foreach (var flight in _inFlight.Values)
            {
                // _inFlight.Values is a snapshot; a flight's completing task may dispose its linked
                // CTS on the threadpool before this runs (the same race the command sites guard).
                // Tolerate it — throwing here would fault ExecuteAsync and, under StopHost, crash the host.
                try { flight.Cancellation.Cancel(); }
                catch (ObjectDisposedException) { }
            }
        }
    }

    private async Task PumpAsync(CancellationToken stoppingToken)
    {
        var driver = new NodeDriver(new NodeOptions
        {
            WorkerId = _workerId,
            Policy = options.Policy,
            MaxClaimBatch = options.MaxClaimBatch,
            MaxOutcomeBatch = options.MaxOutcomeBatch ?? options.MaxClaimBatch, // coalesce a claim batch's worth of outcomes
            PoolSize = options.PoolSize, // the Driver subtracts in-flight work from claims
            LeaseDuration = options.LeaseDuration,
            RetryPolicy = options.RetryPolicy,
            Retention = options.Retention,
            MaintenanceInterval = options.MaintenanceInterval, // the Driver throttles the sweep (0039)
        });
        var events = Channel.CreateUnbounded<NodeEvent>(new UnboundedChannelOptions { SingleReader = true });

        // Report this pump's configured pool size behind the backwave.worker.slots.capacity gauge, so a
        // reader can compare it to backwave.worker.slots.active for headroom. Disposed when the pump stops.
        using var slotCapacity = BackWaveDiagnostics.RegisterWorkerSlotCapacity(options.Name, options.PoolSize);

        // Poll coalescing (issue 0039): the timer and every Wake-Up Hint share one pending-poll
        // slot, so a burst of hints while a poll is already queued collapses to a single extra
        // claim pass instead of one full cycle each. The slot is released when the reader picks
        // the poll up (below), so a hint arriving mid-cycle still re-arms the next poll.
        void RequestPoll()
        {
            // Backpressure: a full pool claims nothing more until something finishes.
            if (_inFlight.Count < options.PoolSize && Interlocked.Exchange(ref _pollQueued, 1) == 0)
            {
                events.Writer.TryWrite(new NodeEvent.PollDue(_clock.GetUtcNow()));
            }
        }

        _ = TickAsync(options.PollInterval, stoppingToken, events.Writer, RequestPoll);
        _ = TickAsync(options.HeartbeatInterval ?? options.LeaseDuration / 3, stoppingToken, events.Writer,
            () => events.Writer.TryWrite(new NodeEvent.HeartbeatDue(_clock.GetUtcNow())));

        // Wake-Up Hints (ADR-0005): a hint is only ever an earlier poll, coalesced through the
        // same slot as the timer. Polling remains the sole correctness mechanism — if the hint
        // channel dies, latency degrades to the poll interval, nothing else.
        var servedQueues = options.Policy.Queues.ToHashSet(StringComparer.Ordinal);
        IAsyncDisposable? hints = null;
        if (store is IWakeUpHintSource hintSource)
        {
            hints = await hintSource.SubscribeAsync(queue =>
            {
                if (servedQueues.Contains(queue))
                {
                    RequestPoll();
                }
            }, stoppingToken).ConfigureAwait(false);
        }

        try
        {
            await foreach (var nodeEvent in events.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                // Release the pending-poll slot as the poll is taken up: hints that arrive while
                // this cycle runs re-arm the next poll rather than being lost to coalescing.
                if (nodeEvent is NodeEvent.PollDue)
                {
                    Interlocked.Exchange(ref _pollQueued, 0);
                }
                try
                {
                    foreach (var command in driver.Step(nodeEvent))
                    {
                        await ExecuteAsync(command, events.Writer, stoppingToken).ConfigureAwait(false);
                    }
                    // A clean cycle clears THIS Pump's prior degraded mark — the store is reachable
                    // again — without touching a sibling Pump's mark on the same group.
                    health.ReportRecovered(options.Name, _workerId);
                }
                catch (Exception exception) when (IsTransientStoreFault(exception))
                {
                    // A transient store fault (connection reset, failover blip, deadlock
                    // victim, timeout) is not an invariant violation (ADR-0007 amendment):
                    // stay running-but-degraded and retry on the next tick. The poll interval
                    // is the backoff cadence — polling is the sole correctness mechanism, so a
                    // skipped cycle costs latency, nothing else (ADR-0005).
                    BackWaveLog.StoreFaultTransientRetry(logger, options.Name, exception);
                    health.ReportDegraded(options.Name, _workerId, exception);
                }
            }
        }
        finally
        {
            // Pump gone: close the channel so the tickers' writes no-op instead of
            // filling an unread buffer until host shutdown.
            events.Writer.TryComplete();
            if (hints is not null)
            {
                await hints.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task TickAsync(
        TimeSpan interval, CancellationToken cancellationToken, ChannelWriter<NodeEvent> events, Action tick)
    {
        try
        {
            using var timer = new PeriodicTimer(interval);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                tick();
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception exception)
        {
            // Fail-stop visibility (ADR-0007): a dead ticker halts the group loudly —
            // failing the pump loop — instead of letting it idle with no polls forever.
            events.TryComplete(exception);
        }
    }

    private async ValueTask ExecuteAsync(
        Command command, ChannelWriter<NodeEvent> events, CancellationToken stoppingToken)
    {
        var now = _clock.GetUtcNow();
        switch (command)
        {
            case Command.ExpireLeases expire:
                var reclaimed = await store.ExpireLeasesAsync(now, expire.MaxJobs, expire.Queues, expire.Disposition, stoppingToken).ConfigureAwait(false);
                if (reclaimed > 0)
                {
                    BackWaveLog.LeasesReclaimed(logger, options.Name, reclaimed);
                }
                break;

            case Command.PurgeTerminal purge:
                var purged = await store.PurgeTerminalAsync(
                    purge.StateClass, purge.TerminalBefore, purge.MaxJobs, stoppingToken).ConfigureAwait(false);
                events.TryWrite(new NodeEvent.PurgeCompleted(purge, purged, now));
                break;

            case Command.LoadSchedules:
                var schedules = await store.ListSchedulesAsync(stoppingToken).ConfigureAwait(false);
                if (schedules.Count > 0)
                {
                    events.TryWrite(new NodeEvent.SchedulesLoaded(schedules, now));
                }
                break;

            case Command.MintDue mint:
                var minted = await store.MintDueAsync(mint.Decisions, stoppingToken).ConfigureAwait(false);
                events.TryWrite(new NodeEvent.MintCompleted(minted, now)); // the Driver decides whether to re-poll
                break;

            case Command.RequestPoll repoll:
                // The Driver asked to poll again now; the pump only obeys (ADR-0008) — same
                // re-poll the deterministic harness runs, so the two pumps never diverge.
                events.TryWrite(new NodeEvent.PollDue(repoll.Now));
                break;

            case Command.ClaimBatch claim:
                using (var claimActivity = BackWaveDiagnostics.StartReceive(claim.WorkerId, options.Name))
                {
                    var jobs = await store.ClaimAsync(
                        new ClaimRequest(claim.WorkerId, claim.Queues, claim.MaxJobs, claim.LeaseDuration, now),
                        stoppingToken).ConfigureAwait(false);
                    BackWaveDiagnostics.RecordClaimed(claimActivity, jobs, now);
                    foreach (var job in jobs)
                    {
                        // A claim is the start of an Attempt: its Lease is now held (Trace).
                        BackWaveLog.LeaseAcquired(logger, job.JobId, job.WireName, job.Attempt, job.Queue);
                    }
                    if (jobs.Count > 0)
                    {
                        events.TryWrite(new NodeEvent.ClaimCompleted(jobs, now));
                    }
                }
                break;

            case Command.ExecuteJob execute:
                StartExecution(execute.Job, events, stoppingToken);
                break;

            case Command.ReportOutcomeBatch batch:
                // The Driver coalesces terminal outcomes (ADR 0035) and flushes them as one command. For
                // each row, drain the Shell-stashed Failure Detail (§5.12, ADR 0011) / runtime Tag delta
                // (ADR 0022) / Job Output (ADR 0026) keyed by (JobId, Attempt) — exactly as the single
                // report did — and apply the whole batch in one fenced store write so the pump stays
                // single-writer. Failure Detail rides only a Failure; Tags and Output always travel and the
                // store keeps them only when the row applies (Output only on Success). Each row is fenced
                // independently; the store returns one result per row, and the Driver re-polls on each
                // applied outcome (a released Dependency may be due this instant).
                var reports = new List<OutcomeReport>(batch.Outcomes.Count);
                foreach (var outcome in batch.Outcomes)
                {
                    _failureDetail.TryRemove((outcome.JobId, outcome.Attempt), out var rowDetail);
                    _bufferedTags.TryRemove((outcome.JobId, outcome.Attempt), out var rowTags);
                    var rowHasOutput = _bufferedOutput.TryRemove((outcome.JobId, outcome.Attempt), out var rowOutput);
                    // Settle the held-open process span: a Failure lands retry-scheduled or dead-lettered
                    // (the disposition the Driver computed into this row's next-due time), then it stops.
                    if (_pendingProcessOutcomes.TryRemove((outcome.JobId, outcome.Attempt), out var span))
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
                var batchResults = await store.ReportOutcomesAsync(reports, now, stoppingToken).ConfigureAwait(false);
                foreach (var rowResult in batchResults)
                {
                    events.TryWrite(new NodeEvent.OutcomeReported(rowResult.JobId, rowResult.Result, now));
                }
                break;

            case Command.Heartbeat heartbeat:
                var results = await store.HeartbeatAsync(
                    heartbeat.WorkerId, heartbeat.JobIds, heartbeat.LeaseDuration, now, stoppingToken).ConfigureAwait(false);
                events.TryWrite(new NodeEvent.HeartbeatCompleted(results, now));
                break;

            case Command.SignalCancellation signal:
                if (_inFlight.TryGetValue(signal.JobId, out var cancelling))
                {
                    cancelling.OperatorCancelRequested = true;
                    try
                    {
                        // The completing execution task may have disposed its linked CTS on the
                        // threadpool concurrently; a disposed source makes Cancel() throw — there is
                        // simply nothing left to cancel, so tolerate it rather than fail-stop the pump.
                        cancelling.Cancellation.Cancel();
                    }
                    catch (ObjectDisposedException) { }
                }
                break;

            case Command.AbandonExecution abandon:
                if (_inFlight.TryRemove(abandon.JobId, out var lost))
                {
                    try
                    {
                        // The Lease is gone: stop applying effects. As with SignalCancellation, the
                        // completing task may have disposed its linked CTS on the threadpool concurrently,
                        // so a disposed source means there is nothing left to cancel — tolerate it.
                        lost.Cancellation.Cancel();
                    }
                    catch (ObjectDisposedException) { }
                }
                break;
        }
    }

    private void StartExecution(JobRecord job, ChannelWriter<NodeEvent> events, CancellationToken stoppingToken)
    {
        switch (registry.Route(job))
        {
            case RouteResult.Unroutable unroutable:
                events.TryWrite(new NodeEvent.ExecutionUnroutable(job, unroutable.Reason, _clock.GetUtcNow()));
                break;

            case RouteResult.Routed routed:
                var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                var flight = new InFlight(job, cancellation);
                _inFlight[job.JobId] = flight;
                // The execution entered the pool: occupy a worker slot (backwave.worker.slots.active),
                // released in the run task below once the execution ends by any path. Tagged with this
                // group's name so active joins to the per-group capacity gauge for headroom.
                BackWaveDiagnostics.RecordWorkerSlotOccupied(job, options.Name);
                _ = Task.Run(async () =>
                {
                    // Assigned inside the try below: the job is already in _inFlight, so anything that
                    // throws before the try (even telemetry) would skip the failure path, leak the pool
                    // slot, and heartbeat the lease forever. Nothing may run outside it.
                    Activity? activity = null;
                    // The claim/execute log scope (job_id / wire_name / attempt / queue). Null-assigned
                    // here (cannot throw), opened inside the try alongside the span, and disposed in the
                    // try's finally so it wraps every execution log below; the settlement events re-open an
                    // identical scope at the report edge.
                    IDisposable? jobScope = null;
                    NodeEvent? outcome;
                    // Hoisted so a handler that throws still flushes the Tags it buffered before
                    // throwing (ADR 0022: a gracefully failed Attempt keeps its Tags).
                    var context = new JobContext
                    {
                        JobId = job.JobId,
                        Attempt = job.Attempt,
                        // The read side of Job Output (ADR 0026): the handler may pull a transitive
                        // ancestor's output, resolved through the Storage Contract above the boundary.
                        DependencyResolver = new StoreDependencyResolver(store),
                        // Raw bytes so the Pro workflow layer can read a baked Workflow Input envelope.
                        Payload = job.Payload,
                        // The registry so a Pro workflow accessor can resolve a step type to its Wire Name
                        // and output codec (typed ctx.Output<TStep,TOut> / ctx.SetOutput).
                        Registry = registry,
                        // The running step's Wire Name so the typed SetOutput accessor can reject a
                        // handler that tries to emit output for a step other than the one it runs.
                        RunningWireName = routed.Registration.WireName,
                    };
                    // Monotonic start for the messaging.process.duration histogram, read off the pump's
                    // injected clock so it measures virtual time under a test clock and system time in
                    // production — a pure side-effect emit that never re-enters the deterministic Core.
                    var executionStart = _clock.GetTimestamp();
                    // One outer try/finally guards the WHOLE run: the worker slot was occupied before this
                    // task started, so its release and the linked CTS dispose must run whatever throws below -
                    // including an unguarded telemetry/logging throw inside a catch body or the scope-dispose
                    // finally of the execution block, which would otherwise escape the task before the
                    // settlement block and leave the saturation gauge stuck non-zero for the process lifetime
                    // while faulting this fire-and-forget Task.Run unobserved.
                    try
                    {
                        try
                        {
                            // The execution span starts inside the try: it parses the payload for the
                            // workflow-after tag, and a telemetry throw must route through the failure
                            // path below like any handler exception, not wedge the job.
                            activity = BackWaveDiagnostics.StartProcess(job, options.Name);
                            jobScope = BackWaveLog.BeginJobScope(logger, job.JobId, job.WireName, job.Attempt, job.Queue);
                            BackWaveLog.ExecutionStarted(logger);
                            // schedule.delay: drift from the job's scheduled (due) time to this actual execution
                            // start, read off the injected clock (virtual under a test clock, system in prod).
                            BackWaveDiagnostics.RecordScheduleDelay(job, _clock.GetUtcNow());
                            // A DI scope per Attempt (ADR 0021): the handler resolves from the scoped
                            // provider, so a scoped dependency (e.g. a DbContext backing an idempotent
                            // dedup write) resolves, and any transient-/scoped-IDisposable in the handler's
                            // graph is disposed when the Attempt ends instead of being captured by the root
                            // container for the process lifetime (the captive-dependency leak). The scope
                            // wraps exactly this handler invocation; the outcome NodeEvent below is written
                            // and applied on the event loop, outside it. This mirrors the Observer pump's
                            // per-delivery scope (ADR 0020) and never reaches the Core — determinism is
                            // untouched. The `using` disposes the scope on success, throw, or cancellation.
                            using var scope = scopeFactory.CreateScope();
                            await routed.Registration
                                .Execute(scope.ServiceProvider, routed.Payload, context, cancellation.Token)
                                .ConfigureAwait(false);
                            BackWaveDiagnostics.RecordExecuted(activity, job, ExecutionOutcome.Success);
                            BackWaveDiagnostics.RecordJobDuration(
                                job, _clock.GetElapsedTime(executionStart), ExecutionOutcome.Success);
                            BackWaveLog.ExecutionCompleted(logger);
                            outcome = new NodeEvent.ExecutionSucceeded(job, _clock.GetUtcNow());
                        }
                        catch (OperationCanceledException cancelled)
                        {
                            // Classify ONCE, here, and drive both the telemetry and the outcome NodeEvent
                            // from that single verdict so the two can never disagree. Only an operator
                            // cancel goes terminal Cancelled. Shutdown reports nothing - the Lease lapses
                            // and another node inherits. A handler's own cancellation (e.g. an HttpClient
                            // timeout surfacing as TaskCanceledException) is neither: it is a plain failure
                            // that retries, counts, and stashes its Failure Detail exactly like any other
                            // throw - the record edge is TOLD that, it does not re-derive it.
                            var operatorCancel = flight.OperatorCancelRequested;
                            var shutdown = !operatorCancel && stoppingToken.IsCancellationRequested;
                            var verdict = operatorCancel || shutdown
                                ? ExecutionOutcome.Cancelled
                                : ExecutionOutcome.Failed(cancelled);
                            BackWaveDiagnostics.RecordExecuted(activity, job, verdict);
                            BackWaveDiagnostics.RecordJobDuration(job, _clock.GetElapsedTime(executionStart), verdict);
                            BackWaveLog.ExecutionCompleted(logger);
                            if (verdict.Failure is not null)
                            {
                                // A handler-raised cancellation fails like any other exception, so it stashes
                                // its Failure Detail like one. An operator cancel or a shutdown is not a
                                // failure and stashes none.
                                _failureDetail[(job.JobId, job.Attempt)] = FailureDetail(cancelled);
                            }
                            outcome = operatorCancel
                                ? new NodeEvent.ExecutionCancelled(job, "operator-cancel", _clock.GetUtcNow())
                                : shutdown
                                    ? null
                                    : new NodeEvent.ExecutionFailed(job, cancelled.Message, _clock.GetUtcNow());
                        }
                        catch (Exception exception)
                        {
                            // The execution boundary: handler exceptions become data here.
                            var verdict = ExecutionOutcome.Failed(exception);
                            BackWaveDiagnostics.RecordExecuted(activity, job, verdict);
                            BackWaveDiagnostics.RecordJobDuration(job, _clock.GetElapsedTime(executionStart), verdict);
                            BackWaveLog.ExecutionCompleted(logger);
                            // Failure Detail (§5.12): stash the FULL exception text Shell-side, keyed by
                            // (JobId, Attempt), to be written onto the failing transition when the
                            // outcome reports. Only the bounded NodeEvent.Error crosses into the Core.
                            _failureDetail[(job.JobId, job.Attempt)] = FailureDetail(exception);
                            outcome = new NodeEvent.ExecutionFailed(job, exception.Message, _clock.GetUtcNow());
                        }
                        finally
                        {
                            // Close the execution scope: the settlement events (retry/dead-letter) run later on
                            // the event loop and re-open an identical scope of their own.
                            jobScope?.Dispose();
                        }
                        // Settle this Attempt's outcome and telemetry. A throw anywhere here - or in a catch
                        // body or the scope-dispose finally above - unwinds to the outer finally, which always
                        // releases the worker slot and disposes the CTS, so neither can leak.
                        //
                        // Remove only this task's own entry (reference equality): if the job was abandoned
                        // and re-claimed by this same pump at a higher Attempt while this task was still
                        // unwinding, a newer flight has replaced this entry - a JobId-only remove would evict
                        // the new attempt and write a stale outcome for it. The keyed overload no-ops then.
                        var stillMine = _inFlight.TryRemove(new KeyValuePair<Guid, InFlight>(job.JobId, flight));
                        if (stillMine && outcome is not null)
                        {
                            // Stash the buffered runtime Tags (ADR 0022) so the imminent ReportOutcome
                            // flushes them onto the fenced write - on both the success and the
                            // gracefully-failed paths. Abandoned executions report nothing, so they stash
                            // nothing (the Tags die with the lost Lease, which is correct).
                            if (context.BufferedTags.Count > 0)
                            {
                                _bufferedTags[(job.JobId, job.Attempt)] = context.BufferedTags;
                            }
                            // Stash any buffered Job Output (ADR 0026) the handler emitted so ReportOutcome
                            // flushes it onto the fenced write. Set even on the failure path (the handler may
                            // SetOutput then throw) - the store drops it for any non-Success outcome.
                            if (context.BufferedOutput is { } bufferedOutput)
                            {
                                _bufferedOutput[(job.JobId, job.Attempt)] = bufferedOutput;
                            }
                            // Hold the process span open, keyed like the buffers above, so ReportOutcome can
                            // land its retry-scheduled / dead-lettered event before the span stops, and carry the
                            // Wire Name and Queue alongside so the dead-letter counter can tag the destination at
                            // the report edge. Stashed even when the span is null (no ActivityListener): the
                            // dead-letter METRIC is independent of tracing, so the report edge must still reach it.
                            _pendingProcessOutcomes[(job.JobId, job.Attempt)] = (activity, job.WireName, job.Queue);
                            events.TryWrite(outcome); // abandoned executions report nothing - the fence would reject them
                        }
                        else
                        {
                            // No outcome will report for this Attempt: either its Lease was reclaimed (the
                            // keyed remove missed because an abandon already evicted the flight) or the node
                            // is shutting down. Close the span now - with a lease-lost event when abandoned -
                            // rather than leaking it open waiting for a report that never comes.
                            if (!stillMine)
                            {
                                BackWaveDiagnostics.RecordLeaseLost(activity);
                                using (BackWaveLog.BeginJobScope(logger, job.JobId, job.WireName, job.Attempt, job.Queue))
                                {
                                    BackWaveLog.LeaseLost(logger);
                                }
                            }
                            else
                            {
                                BackWaveDiagnostics.CloseProcess(activity);
                            }
                        }
                    }
                    finally
                    {
                        // The execution left the pool by every path above (and every throwing path through the
                        // execution and settlement blocks): release its worker slot (backwave.worker.slots.active),
                        // balancing the occupy in StartExecution so the counter returns to zero at drain, then
                        // dispose the linked CTS.
                        BackWaveDiagnostics.RecordWorkerSlotReleased(job, options.Name);
                        cancellation.Dispose();
                    }
                }, CancellationToken.None);
                break;
        }
    }

    // The Failure Detail (§5.12, ADR 0011) text a failing Attempt stashes Shell-side: the throwing
    // exception's full type, message, and stack. Every failure path formats it identically - a
    // handler-raised cancellation included, since that is a failure like any other.
    private static string FailureDetail(Exception exception) =>
        $"{exception.GetType().FullName}: {exception.Message}\n{exception.StackTrace}";

    // Logs a settled outcome under a re-opened job scope (the execution scope closed when the handler
    // returned, and the outcome settles later on the event loop): a Failure with a next-due time is a
    // retry (Information), one without is a Dead-Letter (Error). A success or superseded outcome adds no
    // settlement event - the ExecutionCompleted log already covers it.
    private void LogSettlement(ReportedOutcome outcome, string wireName, string queue)
    {
        if (outcome.Outcome is not JobOutcome.Failure failure)
        {
            return;
        }
        using (BackWaveLog.BeginJobScope(logger, outcome.JobId, wireName, outcome.Attempt, queue))
        {
            if (failure.NextDueTime is { } nextDue)
            {
                BackWaveLog.RetryScheduled(logger, nextDue);
            }
            else
            {
                BackWaveLog.DeadLettered(logger);
            }
        }
    }
}
