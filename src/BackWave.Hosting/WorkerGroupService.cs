using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using BackWave.Diagnostics;
using BackWave.Driver;
using BackWave.Jobs;
using BackWave.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
    TimeProvider? clock = null,
    IOptions<HostOptions>? hostOptions = null) : BackgroundService
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

        // <summary>
        // The fire-and-forget task running this Attempt, assigned the instant Task.Run hands it back (the
        // task closes over this record, so it cannot be a ctor argument). The clean-stop hand-back awaits
        // these under its budget before relinquishing, so a handler that does not honor its cancellation
        // token cannot still be running while another node claims the very job it is running. The run
        // task owns every failure path itself, so this only ever completes - awaiting it is a wait, not
        // a rethrow. Written and read on the pump loop alone.
        // </summary>
        public Task Execution = Task.CompletedTask;

        // <summary>
        // The instant this pump believes its Lease on the job runs until: the expiry the claim handed
        // back, pushed out again by every renewed heartbeat. Written on the pump loop (claim, heartbeat)
        // and read on the threadpool as the execution settles, so it is held in ticks - a long reads and
        // writes atomically where a 16-byte DateTimeOffset would tear. Zero means the claim named no
        // expiry, which reads as "no belief" at the fence check: only a positive belief can contradict.
        // </summary>
        private long _leaseExpiryTicks = Job.LeaseExpiry?.UtcTicks ?? 0;

        public DateTimeOffset? LeaseExpiry
        {
            get => Volatile.Read(ref _leaseExpiryTicks) is var ticks and not 0
                ? new DateTimeOffset(ticks, TimeSpan.Zero)
                : null;
            set => Volatile.Write(ref _leaseExpiryTicks, value?.UtcTicks ?? 0);
        }
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
    // and Queue the settling report needs to tag the dead-letter counter, plus the Lease expiry this pump
    // still believed the Attempt held as it settled (the fence check at the report edge reads it). The
    // settling outcome lands a retry-scheduled / dead-lettered event on the span before it stops. Stashed
    // only when an outcome WILL report; an abandoned Attempt (a lost Lease, which reports nothing) closes
    // its own span in the execution task with a lease-lost event instead. Drained by ReportOutcome so the
    // map never leaks.
    // </summary>
    private readonly ConcurrentDictionary<(Guid JobId, int Attempt), (Activity? Span, string WireName, string Queue, DateTimeOffset? LeaseExpiry)> _pendingProcessOutcomes = new();
    private readonly string _workerId = $"{Environment.MachineName}:{options.Name}:{Guid.NewGuid():N}";

    // <summary>0 = no poll pending, 1 = one queued; coalesces timer + hint polls (issue 0039).</summary>
    private int _pollQueued;

    // <summary>
    // Adaptive idle poll backoff. Active only when
    // <see cref="WorkerGroupOptions.MaxPollInterval"/> is greater than
    // <see cref="WorkerGroupOptions.PollInterval"/>. The pacer (PollPacerAsync) sleeps for the current
    // delay, held in ticks so it can be read and written atomically (a TimeSpan cannot). A claim outcome
    // updates it: work found or a due-now report snaps it back to the floor, an empty poll with a future
    // next-due sleeps to that instant, an empty poll with no next-due grows the delay toward the ceiling in step with how long the group has been idle.
    // Byte-for-byte unchanged when the feature is off - the fixed TickAsync ticker runs instead.
    // </summary>
    private long _pollDelayTicks;
    // <summary>Wakes the adaptive pacer early (a claim that must reset the floor) without a busy
    // wait; a 0..1 semaphore so a burst of releases coalesces to one wake, mirroring _pollQueued.</summary>
    private readonly SemaphoreSlim _pollWake = new(0, 1);
    // <summary>The instant the group last went idle (its first empty poll with no next-due hint), or null while
    // busy. The idle ramp grows the delay by how long the group has actually been idle, not by how many empty
    // polls have fired, so a drain tail's burst of empty re-polls cannot saturate the delay to the ceiling.
    // Single pump thread touches it (the reader loop), so a plain field needs no synchronization.</summary>
    private DateTimeOffset? _idleSince;

    // Adaptive backoff runs only when a strictly larger ceiling is set: at or below the floor,
    // the fixed-cadence ticker governs polling exactly as before.
    private bool AdaptivePoll => options.MaxPollInterval > options.PollInterval;

    // <summary>
    // Cancelled when the token the host passed to StopAsync fires, which is the instant the host stops
    // waiting for this pump. Every hand-back budget links to it, so the hand-back is CLAMPED at the
    // host's real deadline instead of being orphaned past it: BackgroundService.StopAsync returns the
    // moment that token fires and leaves ExecuteAsync running, so an unlinked hand-back would keep
    // writing to the store while the host tears the process down around it.
    // The link is what makes the clamp below hold at all. HostOptions.ShutdownTimeout governs the
    // whole stop, and the host stops its hosted services ONE AT A TIME unless
    // HostOptions.ServicesStopConcurrently is set - so N pumps each clamped to four fifths of the
    // host's window are additive against a window they share. The ceiling bounds one pump; this
    // token is what bounds all of them together.
    // </summary>
    private readonly CancellationTokenSource _hostStopped = new();
    private CancellationTokenRegistration _hostStopLink;

    // <summary>
    // What the hand-back may actually spend: the configured ShutdownBudget, clamped so it cannot eat the
    // host's whole stop window. HostOptions.ShutdownTimeout is how long the host waits for its hosted
    // services to stop before it stops waiting - a hand-back that spends all of it turns the clean stop
    // it exists to serve into a kill, mid-relinquish. Four fifths of the host's window is the ceiling,
    // leaving the last fifth for the host to finish its own shutdown after the leases are given back.
    // This ceiling is PER PUMP, and the host's window is shared by every pump in every group, so it is
    // an upper bound on one hand-back and not on their sum - _hostStopped is what bounds the sum.
    // Absent the option (a pump constructed directly, as the tests do) the configured budget stands.
    // </summary>
    private TimeSpan EffectiveShutdownBudget
    {
        get
        {
            var hostTimeout = hostOptions?.Value.ShutdownTimeout ?? TimeSpan.Zero;
            var ceiling = hostTimeout > TimeSpan.Zero ? hostTimeout * 0.8 : TimeSpan.MaxValue;
            return options.ShutdownBudget < ceiling ? options.ShutdownBudget : ceiling;
        }
    }

    // <summary>
    // Capture the host's stop token on the way past. It is the only handle on the host's actual
    // deadline: the stoppingToken the pump holds is cancelled at the START of the stop and says
    // nothing about when the host gives up waiting. Registered rather than stored, so the pump thread
    // reads a thread-safe CancellationTokenSource instead of a field written by the stopping thread.
    // An already-cancelled token runs the callback inline here, which is correct - a host that is
    // already out of time gets no hand-back at all.
    // </summary>
    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _hostStopLink = cancellationToken.Register(
            static state => ((CancellationTokenSource)state!).Cancel(), _hostStopped);
        return base.StopAsync(cancellationToken);
    }

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
            // Normal shutdown: the pump hands its Leases back on the way out, so other nodes
            // inherit the work at once instead of waiting for the Leases to lapse.
        }
        catch (Exception exception)
        {
            // Fail-stop (ADR-0007): an invariant violation — or any fault we can't classify
            // as transient — halts this group only. No outcome reports, no more heartbeats:
            // the Leases lapse and healthy nodes inherit. The host process keeps serving.
            // One Critical log names the dead invariant (type, message, stack); the health
            // state retains the exception type, not just its message.
            //
            // A named check adds its trigger id to both. The id is read off the exception on each side,
            // so the log and the health report cannot name different invariants; a halt no check named
            // (the negative catch-all below) reads as unclassified on both.
            var trigger = (exception as InvariantViolationException)?.Trigger;
            HostingLog.WorkerGroupFailStopped(
                logger, options.Name, trigger?.ToString() ?? HostingLog.UnclassifiedTrigger, exception);
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
            RetryOverrides = registry.RetryOverrides, // per-job-type [Retry] overrides, loud-failure path (0051)
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

        if (AdaptivePoll)
        {
            // Start idle backoff at the floor and let claim outcomes stretch it toward the ceiling.
            Volatile.Write(ref _pollDelayTicks, options.PollInterval.Ticks);
            _ = PollPacerAsync(stoppingToken, events.Writer, RequestPoll);
        }
        else
        {
            _ = TickAsync(options.PollInterval, stoppingToken, events.Writer, RequestPoll);
        }
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

        // The fault this pump is fail-stopping on, or null on a clean stop. The hand-back in the finally
        // below cannot tell the two apart on its own: a violation raised in cycle while shutdown is
        // ALREADY running unwinds with stoppingToken cancelled, which is exactly what a clean stop looks
        // like from there. Carried out explicitly so a halting pump writes nothing more to the store.
        Exception? halting = null;
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
                    var commands = driver.Step(nodeEvent);
                    var issued = 0;
                    try
                    {
                        foreach (var command in commands)
                        {
                            await ExecuteAsync(command, events.Writer, stoppingToken).ConfigureAwait(false);
                            issued++;
                        }
                    }
                    catch
                    {
                        // A fault abandons the rest of this cycle's commands, but the Driver already
                        // reserved the pool slots of every ClaimBatch it planned - it frees them only when
                        // that batch's ClaimCompleted lands. Land an empty completion for each claim this
                        // cycle never ran, so an abandoned cycle cannot strand a reservation and wedge the
                        // pool shut. A maintenance sweep runs ahead of the claim on the same poll, so this
                        // is the ordinary path whenever the store is unreachable, not a corner case.
                        ReleaseUnissuedClaims(commands, issued, events.Writer, _clock.GetUtcNow());
                        throw;
                    }
                    // A cycle that ran at least one command clears THIS Pump's prior degraded mark - the
                    // store answered, so it is reachable again - without touching a sibling Pump's mark on
                    // the same group. A cycle that ran none proves nothing about the store and must leave
                    // the mark alone: a faulted claim lands an empty ClaimCompleted to free its reservation,
                    // and a poll whose pool is already full issues no claim. Both reach here without a
                    // round-trip, so clearing on them would erase the mark microseconds after the fault set
                    // it and a group whose store is down would read healthy.
                    if (issued > 0)
                    {
                        health.ReportRecovered(options.Name, _workerId);
                    }
                }
                catch (InvariantViolationException)
                {
                    // The positive trigger set, added beside the negative catch-all below rather than
                    // folded into it: a named check proved an invariant already broken, so no store's
                    // own fault classifier gets a say in whether it is retryable. Rethrown to the single
                    // halt call site in ExecuteAsync, which stops this group exactly as it does today.
                    throw;
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
        catch (ChannelClosedException closed) when (closed.InnerException is { } carried)
        {
            // The event channel is how a fault raised OFF the pump loop reaches it: a dead ticker, a dead
            // pacer, and an invariant violation thrown inside a handler all complete the writer with their
            // own exception, and the reader raises it here once the queued events ahead of it have drained.
            // The channel wraps it, but the halt site in ExecuteAsync - and the health report it writes -
            // both read the trigger off the exception's own type, so unwrap it: left wrapped, every fault
            // carried this way would read as unclassified on both surfaces.
            halting = carried;
            ExceptionDispatchInfo.Capture(carried).Throw();
        }
        catch (Exception exception) when (exception is not OperationCanceledException
            || !stoppingToken.IsCancellationRequested)
        {
            // Everything that is not the clean-stop cancellation is a halt by the time it gets here: the
            // loop above already swallowed the transient store faults, so what is left reaches the
            // fail-stop site in ExecuteAsync. Recorded, not handled - the rethrow leaves the exception
            // and its stack exactly as the halt site would have seen them.
            halting = exception;
            throw;
        }
        finally
        {
            // The pump loop has exited, so this group claims nothing more: give back what it holds. This
            // runs BEFORE the writer is completed, because the hand-back reads the events the stopping
            // executions are still writing and flushes the Driver's buffered outcomes through the
            // ordinary command path, which writes their OutcomeReported events back to this very
            // channel - a completed writer would refuse every one of them.
            try
            {
                await HandBackAsync(driver, events, stoppingToken, halting).ConfigureAwait(false);
            }
            finally
            {
                // Its own finally, because the hand-back rethrows a named violation on its way out. Left in
                // the same block, that throw would skip both lines below: the channel would stay open for
                // the tickers to fill until host shutdown, and the Wake-Up Hint subscription would leak on
                // exactly the path - a halt - that most needs the process left tidy.
                //
                // Pump gone: close the channel so the tickers' writes no-op instead of
                // filling an unread buffer until host shutdown.
                events.Writer.TryComplete();
                if (hints is not null)
                {
                    await hints.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    // <summary>
    // The clean-stop hand-back: wait out the executions this pump still has running, settle whatever
    // those executions wrote on their way out, report the outcomes the Driver had buffered, then
    // relinquish the Leases it still holds so their jobs return to the queue now instead of waiting out
    // the whole Lease duration on a node that is gone. All four steps spend ONE allowance, in that order -
    // four independent timeouts could sum past the host's own shutdown timeout and turn a clean stop
    // into a kill - and the allowance is split so the relinquish keeps a reserved slice of it.
    // stoppingToken is already cancelled by the time the pump exits, so these budgets are the
    // only tokens that can permit this work.
    // The wait comes FIRST, and the drain of the event channel immediately after it, because a handler
    // that finishes during the wait writes its outcome to a channel the pump loop has already stopped
    // reading: nothing else would ever take it, and the relinquish below would then return a job that
    // had genuinely succeeded to Scheduled at the same Attempt, so it runs a second time. Waiting also
    // has to precede the relinquish for the older reason - no job may be handed to another node while
    // this one is still running it.
    // One attempt, no retry: any failure (the budget running out included) is logged at Warning and
    // degrades to the Leases lapsing exactly as they do today, and never blocks the host from exiting.
    // </summary>
    private async Task HandBackAsync(
        NodeDriver driver, Channel<NodeEvent> events, CancellationToken stoppingToken, Exception? halting)
    {
        // Clean stops only. A pump that exits any other way is fail-stopping, and a halted pump writes
        // nothing more to the store: its Leases lapse and healthy nodes inherit the work. The halting
        // fault is carried in rather than inferred from the token, because a violation raised in cycle
        // during shutdown unwinds with stoppingToken already cancelled and is indistinguishable here.
        var allowance = EffectiveShutdownBudget;
        if (halting is not null || !stoppingToken.IsCancellationRequested || allowance <= TimeSpan.Zero)
        {
            return;
        }

        // The relinquish gets a slice of the allowance that no step before it can spend. The settle waits
        // on work this pump does not govern - a handler that ignores its token eats every millisecond it
        // is given - and the relinquish is the one step the whole hand-back exists to perform. On one
        // shared token an unresponsive handler starves it, the store call gets an already-cancelled
        // token, and the Leases lapse: precisely the outcome this feature removes. The reserve is one
        // fifth, so the settle still keeps the bulk, and its clock starts only when the settle is over, so
        // the two together can never exceed the allowance.
        var reserve = allowance / 5;
        try
        {
            try
            {
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(_hostStopped.Token);
                budget.CancelAfter(allowance - reserve);
                await SettleFinishedExecutionsAsync(driver, events, budget.Token).ConfigureAwait(false);
            }
            catch (InvariantViolationException)
            {
                // Ahead of the catch below for the reason the pump loop has the same pair: a named check
                // proved an impossible state, and no store's fault classifier gets a say in whether that
                // is retryable. Past the relinquish as well as past the log, because a halting group
                // writes nothing more to the store.
                throw;
            }
            catch (Exception exception)
            {
                // The settle is best-effort and its own budget can run out mid-call, so it raises
                // OperationCanceledException as an ORDINARY outcome. Caught HERE, in its own block, and
                // not in the blanket catch below: sharing one block let a settle that ran out of time
                // skip the relinquish entirely, so the reserve slice protected the relinquish from
                // running out of budget but not from the control flow of the step before it - and the
                // Leases lapsed anyway, which is the outcome this whole method exists to remove.
                BackWaveLog.ShutdownHandBackFailed(logger, options.Name, exception);
            }

            // Fenced store-side on this pump's worker identity and the Leased state, so anything the
            // settle above managed to report is already out of reach. Linked to the host's stop token as
            // well as to the reserve, so the host's deadline cuts this off rather than leaving it
            // writing to the store after the host stopped waiting.
            using var relinquishBudget = CancellationTokenSource.CreateLinkedTokenSource(_hostStopped.Token);
            relinquishBudget.CancelAfter(reserve);
            var relinquished = await store.RelinquishLeasesAsync(
                _workerId, _clock.GetUtcNow(), options.RetryPolicy.ToDisposition(), relinquishBudget.Token)
                .ConfigureAwait(false);
            if (relinquished > 0)
            {
                BackWaveLog.LeasesRelinquished(logger, options.Name, relinquished);
            }
        }
        catch (InvariantViolationException)
        {
            // A named check proved an impossible state while giving the work back - every adapter raises
            // one out of the relinquish path. Swallowed below it would read as a routine shutdown
            // hiccup, leave the group green, and lose the trigger nothing else records. Thrown from this
            // finally it supersedes the shutdown cancellation the pump was unwinding and reaches the one
            // halt call site in ExecuteAsync, which classifies it off the exception exactly as it does an
            // in-cycle violation.
            throw;
        }
        catch (Exception exception)
        {
            BackWaveLog.ShutdownHandBackFailed(logger, options.Name, exception);
        }
    }

    // <summary>
    // The settle: wait the in-flight executions out, step the terminal events they wrote on their way
    // out, then flush whatever outcomes that left in the Driver's buffer. Everything the hand-back does
    // BEFORE the relinquish, under the budget the relinquish does not get.
    // A named violation raised in here leaves through this method, so the caller can keep it clear of
    // the relinquish. Every other fault is the caller's to log.
    // </summary>
    private async Task SettleFinishedExecutionsAsync(
        NodeDriver driver, Channel<NodeEvent> events, CancellationToken budget)
    {
        await DrainInFlightAsync(budget).ConfigureAwait(false);

        // Everything those executions wrote as they finished, through the ordinary command path, so
        // an outcome that landed after the pump loop exited settles exactly as one that landed a
        // millisecond earlier. Each execution writes its event BEFORE its task completes, so the wait
        // above is what guarantees they are all sitting in the reader by the time this loop starts.
        //
        // Only the four terminal execution events are stepped. Every other event the channel still
        // holds is DISCARDED, because the Driver answers most of them by starting work this pump must
        // not start: a ClaimCompleted returns one ExecuteJob per claimed job and can issue the next
        // weighted ClaimBatch behind it, SchedulesLoaded mints, PurgeCompleted re-sweeps, and a poll or
        // heartbeat tick claims or renews. Stepping those would run new Attempts after the wait that
        // was supposed to end them, and each new Attempt would write another event to step. Discarding
        // a ClaimCompleted strands nothing: its jobs are Leased to this worker and untouched, so the
        // relinquish is exactly what hands them back.
        while (events.Reader.TryRead(out var nodeEvent))
        {
            if (nodeEvent is not (NodeEvent.ExecutionSucceeded or NodeEvent.ExecutionFailed
                or NodeEvent.ExecutionCancelled or NodeEvent.ExecutionUnroutable))
            {
                continue;
            }
            foreach (var command in driver.Step(nodeEvent))
            {
                await ExecuteAsync(command, events.Writer, budget).ConfigureAwait(false);
            }
        }

        // A handler that raised a named violation while the wait above ran completed the writer with
        // that exception, and the pump loop that would have raised it exited before this method
        // started: nothing else reads the channel now. Read the completion here, so a violation
        // during the drain reaches the same halt site as one raised in cycle instead of being lost.
        // Ahead of the flush and the relinquish, because a halting group writes nothing more to the
        // store - the same reason the in-cycle path never flushes the buffer after a violation.
        if (events.Reader.Completion is { IsFaulted: true } completion
            && completion.Exception?.InnerException is { } carried)
        {
            ExceptionDispatchInfo.Capture(carried).Throw();
        }

        // Through the ordinary command path too, so the buffered Failure Detail, Tags, Output, and
        // held-open spans settle exactly as they do on a poll-tick flush. Last, because the drain
        // above is what puts the final outcomes into the Driver's buffer in the first place.
        if (driver.DrainBufferedOutcomes() is { } pending)
        {
            await ExecuteAsync(pending, events.Writer, budget).ConfigureAwait(false);
        }
    }

    // <summary>
    // Wait out the executions this pump still has in flight, under the hand-back's budget. Their tokens
    // are linked to stoppingToken, so the handlers were signalled the instant the stop began - but until
    // now nothing waited for them, and a handler that does not honor its token would still be running
    // while the relinquish below hands its job to another node, which would then run the same Attempt
    // concurrently instead of after the Lease lapsed.
    // Best-effort, like the rest of the hand-back: a handler that outlasts the budget is logged and its
    // Lease relinquished anyway, so this can never hold the host open past it. That is a real widening,
    // not the status quo: before the hand-back the Lease stayed held for its full duration, so a peer
    // could not take the job until it lapsed - usually after the process was already gone. A relinquish
    // past this budget hands the job over while the local handler is still running it, so the concurrent
    // Attempt is one this step CREATES. At-least-once already permits it, and the alternative is the
    // whole feature going quiet whenever one handler is slow, so the overlap is the accepted price.
    // </summary>
    private async Task DrainInFlightAsync(CancellationToken budget)
    {
        var running = _inFlight.Values.Select(flight => flight.Execution).ToArray();
        if (running.Length == 0)
        {
            return;
        }
        try
        {
            await Task.WhenAll(running).WaitAsync(budget).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // The count is what is STILL running, not what the drain set out to wait for: a handler that
            // returned inside the budget is not the one an operator goes looking for. A handler can also
            // return between the timeout and this count, and then there is nothing to warn about.
            var stillRunning = running.Count(execution => !execution.IsCompleted);
            if (stillRunning > 0)
            {
                BackWaveLog.ShutdownDrainIncomplete(logger, options.Name, stillRunning, exception);
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

    // The adaptive replacement for the fixed poll ticker, used only while
    // AdaptivePoll is set. It sleeps for the current backoff delay, then requests a poll - but the sleep
    // ends early when _pollWake is released, which happens only on a floor reset (WakePoll from
    // UpdatePollBackoff when work is claimed or due-now pressure is reported), so the return to full cadence
    // takes effect at once. A Wake-Up Hint does not touch _pollWake; it wakes the pump through RequestPoll,
    // which writes a PollDue to the channel the reader drains independently of this sleep. The delay never
    // drops below PollInterval, so the poll cadence keeps its correctness floor even if an update races. A
    // dead pacer fails the pump loop, exactly as a dead ticker does.
    private async Task PollPacerAsync(CancellationToken cancellationToken, ChannelWriter<NodeEvent> events, Action tick)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var delayTicks = Math.Max(options.PollInterval.Ticks, Volatile.Read(ref _pollDelayTicks));
                // A satisfied wait (true) means an early wake: skip straight to the poll. A timeout (false)
                // is the normal backoff expiry. Either way the next action is one poll request.
                await _pollWake.WaitAsync(TimeSpan.FromTicks(delayTicks), cancellationToken).ConfigureAwait(false);
                tick();
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
            // Teardown raced the pacer: _pollWake was disposed while a wait was parked. This is
            // normal shutdown, not a fault - do not fault the pump into the fail-stop path.
        }
        catch (Exception exception)
        {
            events.TryComplete(exception);
        }
    }

    // Fold a claim outcome into the idle backoff delay (adaptive mode only). Work claimed, or a store that
    // reports due-now pressure it withheld, resets to the floor and wakes the pacer now. An empty poll with
    // a future next-due sleeps to that instant (clamped to the floor/ceiling). An empty poll with no
    // next-due grows the delay toward the ceiling in step with how long the group has been idle. The value
    // is advisory: it changes only WHEN the pump polls, never WHETHER a due job is claimed, so polling stays
    // the sole correctness mechanism.
    private void UpdatePollBackoff(bool claimedWork, DateTimeOffset? nextDue, DateTimeOffset now)
    {
        var floor = options.PollInterval.Ticks;
        var ceiling = options.MaxPollInterval.Ticks;

        if (claimedWork || (nextDue is { } due && due <= now))
        {
            // Busy again, or work is already due but was withheld (concurrency limit, batch cap, full pool):
            // return to the floor and poll promptly so the backlog drains at full cadence.
            _idleSince = null;
            Volatile.Write(ref _pollDelayTicks, floor);
            WakePoll();
            return;
        }

        long nextTicks;
        if (nextDue is { } next)
        {
            // Sleep to the next scheduled instant, but never past the ceiling or below the floor.
            var untilDue = (next - now).Ticks;
            nextTicks = Math.Clamp(untilDue, floor, ceiling);
        }
        else
        {
            // Nothing scheduled and no hint of when: grow the delay in step with elapsed idle time, not with
            // the count of empty polls. Anchoring on the first idle instant makes a drain tail's burst of
            // empty re-polls - which all arrive within a few milliseconds - grow the delay by only those few
            // milliseconds, instead of doubling it once per poll straight to the ceiling. A genuinely idle
            // group still climbs to the ceiling because its idle span keeps widening.
            _idleSince ??= now;
            nextTicks = Math.Clamp((now - _idleSince.Value).Ticks, floor, ceiling);
        }
        Volatile.Write(ref _pollDelayTicks, nextTicks);
    }

    // Release the pacer's wake latch, coalescing a burst to a single wake (mirrors _pollQueued). A full
    // latch already means "wake pending", so an extra release is a no-op, not an error.
    private void WakePoll()
    {
        try { _pollWake.Release(); }
        catch (SemaphoreFullException) { }
        catch (ObjectDisposedException)
        {
            // Teardown raced the wake: _pollWake was disposed while a wake raced shutdown. This is
            // normal shutdown, not a fault - do not fault the pump into the fail-stop path.
        }
    }

    // Frees the pool slots of every ClaimBatch a fault left un-issued. The Driver reserves a batch's slots
    // when it plans the batch and frees them when that batch's ClaimCompleted lands, so the contract is one
    // completion per planned batch - an empty result included. A cycle that faults part-way keeps that
    // contract here: commands[issued] is the command that faulted and everything after it never ran.
    private static void ReleaseUnissuedClaims(
        IReadOnlyList<Command> commands, int issued, ChannelWriter<NodeEvent> events, DateTimeOffset now)
    {
        for (var index = issued; index < commands.Count; index++)
        {
            if (commands[index] is Command.ClaimBatch)
            {
                events.TryWrite(new NodeEvent.ClaimCompleted([], now));
            }
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
                    var request = new ClaimRequest(claim.WorkerId, claim.Queues, claim.MaxJobs, claim.LeaseDuration, now);
                    // ClaimBatchAsync (a Default Interface Method) returns the claimed jobs plus an advisory
                    // next-due instant; an adapter that does not override it reports NextDue = null, so the
                    // backoff simply falls back to the idle-time ramp. Only the adaptive pacer consumes
                    // NextDue, so when the feature is off we take the plain ClaimAsync path and issue exactly
                    // the single claim query the runtime issued before this feature existed.
                    // A fault here needs no completion of its own: the batch counts as un-issued, and the
                    // pump loop lands its empty ClaimCompleted as it abandons the cycle.
                    var result = AdaptivePoll
                        ? await store.ClaimBatchAsync(request, stoppingToken).ConfigureAwait(false)
                        : new ClaimResult(
                            await store.ClaimAsync(request, stoppingToken).ConfigureAwait(false), NextDue: null);
                    var jobs = result.Jobs;
                    BackWaveDiagnostics.RecordClaimed(claimActivity, jobs, now);
                    foreach (var job in jobs)
                    {
                        // A claim leases a row the store matched as Scheduled and left Leased, so a terminal
                        // state here means the row was settled behind the claim's own UPDATE. Executing it
                        // would re-run an Attempt whose effect already landed, so halt before dispatch.
                        if (job.State.IsTerminal())
                        {
                            throw Invariant.Halt(
                                InvariantTrigger.ClaimedJobTerminal,
                                $"Claim for worker '{claim.WorkerId}' returned job {job.JobId} " +
                                $"({job.WireName}, attempt {job.Attempt}) in terminal state {job.State}.");
                        }
                        // A claim is the start of an Attempt: its Lease is now held (Trace).
                        BackWaveLog.LeaseAcquired(logger, job.JobId, job.WireName, job.Attempt, job.Queue);
                    }
                    // Always report the claim's completion, an empty result included: the Driver reserved this
                    // batch's slots against the pool at issue and frees them here, so a claim that returns
                    // nothing must still land or the reservation would strand and wedge the pool.
                    events.TryWrite(new NodeEvent.ClaimCompleted(jobs, now));
                    if (AdaptivePoll)
                    {
                        UpdatePollBackoff(jobs.Count > 0, result.NextDue, now);
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
                // The held-open process span for each row - and, on it, the Lease expiry this pump still
                // believed that row held as its execution settled - kept positionally alongside the report
                // it belongs to (the Storage Contract pairs results to reports by position too). Null where
                // the execution stashed nothing. Read by the settlement below and by the fence check.
                var pendingSpans =
                    new (Activity? Span, string WireName, string Queue, DateTimeOffset? LeaseExpiry)?[batch.Outcomes.Count];
                foreach (var outcome in batch.Outcomes)
                {
                    _failureDetail.TryRemove((outcome.JobId, outcome.Attempt), out var rowDetail);
                    _bufferedTags.TryRemove((outcome.JobId, outcome.Attempt), out var rowTags);
                    var rowHasOutput = _bufferedOutput.TryRemove((outcome.JobId, outcome.Attempt), out var rowOutput);
                    if (_pendingProcessOutcomes.TryRemove((outcome.JobId, outcome.Attempt), out var span))
                    {
                        // Indexed by reports.Count, which is this row's position the instant before it is
                        // appended below. Stashed, not settled: the settlement happens once the batch has
                        // been applied, off the row the store actually took.
                        pendingSpans[reports.Count] = span;
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
                IReadOnlyList<OutcomeReportResult> batchResults;
                bool[] fencedByDesign;
                try
                {
                    (batchResults, fencedByDesign) =
                        await ApplyOutcomesAsync(reports, now, stoppingToken).ConfigureAwait(false);
                }
                finally
                {
                    // Settle the held-open process spans from the row as APPLIED, not as the handler left
                    // it. ApplyOutcomesAsync rewrites a row whose Job Output the store rejected into a
                    // terminal Failure, and a span closed on the original Success would record a
                    // dead-lettered job as a success: no "dead-lettered" span event, no
                    // backwave.jobs.dead_lettered increment, and no Dead-Lettered log, so an operator
                    // alerting on any of the three is blind to the whole class. A Failure lands
                    // retry-scheduled or dead-lettered (the disposition the Driver computed into this row's
                    // next-due time), then it stops.
                    // In a finally, because a store fault on the apply would otherwise leave every span in
                    // the batch open forever - the pump already took them out of _pendingProcessOutcomes.
                    for (var i = 0; i < reports.Count; i++)
                    {
                        if (pendingSpans[i] is { } settling)
                        {
                            BackWaveDiagnostics.CompleteProcess(
                                settling.Span, reports[i].Outcome, settling.WireName, settling.Queue);
                            LogSettlement(reports[i], settling.WireName, settling.Queue);
                        }
                    }
                }
                // The Storage Contract is one result per input row, in input order, and the Driver pairs the
                // two by position. A short or long answer means some row's outcome is silently unreported or
                // misattributed, so stop before the Driver acts on a mispaired result.
                if (batchResults.Count != reports.Count)
                {
                    throw Invariant.Halt(
                        InvariantTrigger.OutcomeBatchCountMismatch,
                        $"Outcome batch of {reports.Count} row(s) came back with {batchResults.Count} result(s).");
                }
                for (var i = 0; i < batchResults.Count; i++)
                {
                    var rowResult = batchResults[i];
                    if (rowResult.Result is OutcomeResult.StaleLease)
                    {
                        // The store's identity fence refused this write. Two ordinary races end here and
                        // neither is a broken invariant: a Lease that lapsed before its outcome could be
                        // reported (another node has since taken the job over, or this pump's own sweep
                        // expired it), and a Job Output re-apply, where the rows the store already settled
                        // on the rejected pass are fenced out by design - those rows only, so the check
                        // below stays live for every other row in the batch. Both are logged at Debug and
                        // counted nowhere - the invariant ledger is the surface a promotion rule reads FOR
                        // ZEROS, so a healthy fleet that increments it makes the trigger meaningless.
                        //
                        // What no legal race produces is a fence that refuses a Lease this pump still
                        // believes it holds: the store answered "not this worker's" about a Lease whose
                        // expiry - as the claim set it and every renewed heartbeat pushed it out - is still
                        // in the future by the pump's own clock. Only that contradiction is counted. The
                        // belief is a lower bound (it advances only on renewals this pump saw), so the test
                        // errs toward silence and never toward a false alarm.
                        //
                        // The clocks are the other reason it errs toward silence. The expiry is stamped
                        // from THIS pump's clock and compared against it, but the node that expired the
                        // Lease read its own, and the fleet is bound to no common time source. A peer that
                        // runs a few seconds ahead expires a lapsed Lease legally, and this pump still sees
                        // a future expiry. Only a window wider than any credible skew is a contradiction,
                        // so one skewed node cannot make the zero-counter non-zero on a healthy fleet.
                        if (!fencedByDesign[i] && pendingSpans[i]?.LeaseExpiry is { } until
                            && now + Invariant.ClockSkewAllowance < until)
                        {
                            var detail =
                                $"Outcome {reports[i].Outcome} for job {rowResult.JobId} (attempt {reports[i].Attempt}, " +
                                $"worker '{reports[i].WorkerId}') was refused by the store's identity fence, but this " +
                                $"pump's Lease on it runs to {until:o} and it is only {now:o}.";
                            // 2003, not the 1601 Invariant.Degrade writes: the two ids split by ALTITUDE,
                            // and 16xx is reserved for invariant sites that degrade BELOW the Hosting
                            // boundary. This is the group-altitude counterpart, and it is the only one of
                            // the pair that names the worker group - nothing puts the group on a log scope.
                            // Emitting both double-pages an operator who follows that split and inflates a
                            // count of 1601 by the whole pump fence family. The counter is what the
                            // promotion rule reads, so it is recorded directly here rather than left to
                            // Invariant.Degrade, which cannot write 1601 without breaking the band.
                            HostingLog.WorkerGroupDegradedByInvariant(
                                logger, options.Name, InvariantTrigger.OutcomeFenceRejected.ToString(), detail);
                            BackWaveDiagnostics.RecordInvariantViolation(
                                InvariantTrigger.OutcomeFenceRejected, InvariantAction.Degrade);
                        }
                        else
                        {
                            BackWaveLog.OutcomeFencedOut(logger, rowResult.JobId, reports[i].Attempt);
                        }
                    }
                    events.TryWrite(new NodeEvent.OutcomeReported(rowResult.JobId, rowResult.Result, now));
                }
                break;

            case Command.Heartbeat heartbeat:
                var results = await store.HeartbeatAsync(
                    heartbeat.WorkerId, heartbeat.JobIds, heartbeat.LeaseDuration, now, stoppingToken).ConfigureAwait(false);
                // The Storage Contract is one result per requested job. A short answer would leave a job whose
                // Lease was silently not renewed looking renewed, so the Driver would keep executing past its
                // Lease; stop instead.
                if (results.Count != heartbeat.JobIds.Count)
                {
                    throw Invariant.Halt(
                        InvariantTrigger.HeartbeatBatchCountMismatch,
                        $"Heartbeat for {heartbeat.JobIds.Count} job(s) came back with {results.Count} result(s).");
                }
                foreach (var renewal in results)
                {
                    // Keep this pump's belief about its own Leases current: a renewed heartbeat pushed the
                    // store-side expiry out by the duration it just asked for. The outcome fence check
                    // reads this to tell an ordinary lapsed Lease from one it still holds; without it, a
                    // job that outlives its original Lease window would look lapsed to us the whole time.
                    if (renewal.Renewed && _inFlight.TryGetValue(renewal.JobId, out var renewed))
                    {
                        renewed.LeaseExpiry = now + heartbeat.LeaseDuration;
                    }
                }
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

    // <summary>
    // Applies an outcome batch, dead-lettering a row whose Job Output the store rejects rather than letting
    // that rejection reach the fail-stop catch. An over-cap blob (rejected loudly, never truncated) is a
    // defect in ONE user handler, not an invariant violation, so it must cost that job and nothing else:
    // the offending row is rewritten as a terminal Failure carrying the rejection as its cause and Failure
    // Detail - with its Output dropped - and the batch is applied again. A store that pre-scans the whole
    // batch has written nothing yet; one that applies row by row has already settled the rows ahead of the
    // rejection, and those simply fence out as StaleLease on the re-apply, which costs a re-poll and no
    // more. Each pass clears one distinct row's Output, so a row can never be rejected twice and the loop
    // is bounded by the row count; anything past that bound - or a rejection naming a job this batch never
    // sent - is unclassifiable and fail-stops the group exactly as before.
    // FencedByDesign marks, per row, whether a rejected pass could already have settled that row, so the
    // caller's fence check knows which StaleLease answers this method's own re-apply produced. Per row and
    // not per batch: only the rows AHEAD of a rejection can have been settled by the pass that threw, and
    // on a store that pre-scans the whole batch not even those - so a batch-wide flag would silence the
    // fence for rows nothing re-applied, and a genuine contradiction riding alongside an over-cap Output
    // would go uncounted.
    // </summary>
    private async ValueTask<(IReadOnlyList<OutcomeReportResult> Results, bool[] FencedByDesign)> ApplyOutcomesAsync(
        List<OutcomeReport> reports, DateTimeOffset now, CancellationToken stoppingToken)
    {
        var fencedByDesign = new bool[reports.Count];
        for (var pass = 0; ; pass++)
        {
            try
            {
                return (await store.ReportOutcomesAsync(reports, now, stoppingToken).ConfigureAwait(false), fencedByDesign);
            }
            catch (JobOutputTooLargeException rejected) when (pass < reports.Count)
            {
                var index = reports.FindIndex(report => report.JobId == rejected.JobId);
                if (index < 0)
                {
                    throw;
                }
                for (var settled = 0; settled < index; settled++)
                {
                    fencedByDesign[settled] = true;
                }
                HostingLog.JobOutputRejected(
                    logger, options.Name, rejected.JobId, rejected.ActualBytes, rejected.MaxOutputBytes);
                var row = reports[index];
                reports[index] = new OutcomeReport(
                    row.JobId, row.WorkerId, row.Attempt,
                    new JobOutcome.Failure(
                        null,
                        $"Job Output rejected: {rejected.ActualBytes} bytes exceeds the "
                            + $"{rejected.MaxOutputBytes}-byte MaxOutputBytes cap."))
                {
                    FailureDetail = FailureDetail(rejected),
                    AddedTags = row.AddedTags,
                };
            }
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
                // Kept on the flight so the clean-stop hand-back can wait this execution out before it
                // relinquishes the Lease. Assigned right after Task.Run hands the task back rather than
                // before the _inFlight insert above, because the task closes over the flight record - the
                // sliver between the two leaves a hand-back that lands inside it waiting on nothing, which
                // is exactly today's behaviour and no worse.
                flight.Execution = Task.Run(async () =>
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
                        catch (InvariantViolationException violation)
                        {
                            // Ahead of the catch-all, because this one is not a handler exception that
                            // becomes job data: a named check proved an impossible state INSIDE the
                            // handler's call - a Dependency read whose workflow row is gone, an enqueue the
                            // store refused as cyclic. No Attempt of this job, or any other, can be trusted
                            // afterwards and no retry can fix it, so it halts the group instead
                            // of degrading into one more failed Attempt.
                            //
                            // The run is fire-and-forget, so a faulted task would reach nobody. The event
                            // channel is the carrier, exactly as it already is for a dead ticker or a dead
                            // pacer: completing the writer with the violation makes the pump's own read
                            // throw it, and PumpAsync unwraps it to the one halt call site in ExecuteAsync
                            // with its trigger intact. Events already queued drain ahead of it, so outcomes
                            // this pump had in hand still settle. This Attempt reports nothing - the group
                            // is halting and its Leases lapse - and the outer finally still releases the
                            // worker slot and disposes the linked CTS.
                            BackWaveDiagnostics.RecordHalted(activity, violation);
                            events.TryComplete(violation);
                            outcome = null;
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
                            // The Lease expiry travels with it: the report edge needs what this pump
                            // believed about its own Lease as the Attempt settled, and the flight record
                            // that holds that belief is removed just above.
                            _pendingProcessOutcomes[(job.JobId, job.Attempt)] =
                                (activity, job.WireName, job.Queue, flight.LeaseExpiry);
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
    private void LogSettlement(OutcomeReport outcome, string wireName, string queue)
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

    public override void Dispose()
    {
        // Cancel the pump (base) BEFORE disposing the wake latch the pacer awaits: if teardown races a
        // parked pacer wait, that wait then sees an already-cancelled token, so the resulting
        // ObjectDisposedException is recognized as normal shutdown instead of faulting the pump.
        base.Dispose();
        _pollWake.Dispose();
        _hostStopLink.Dispose();
        _hostStopped.Dispose();
    }
}
