using System.Threading.Channels;
using BackWave.Diagnostics;
using BackWave.Observers;
using BackWave.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BackWave.Hosting;

// <summary>
// The production Shell that drives the sans-IO Observer Dispatch Core (ADR 0017, ADR 0020). One
// pump per process — the one-per-host shape of <see cref="BackWaveMetricsService"/>, not the
// one-per-group shape of <see cref="WorkerGroupService"/> — owning a single
// <see cref="ObserverDispatchDriver"/> over every registered Observer. Delivery stays leaderless
// and database-authoritative: the per-Observer cursor Lease serializes claims across nodes, so any
// number of these pumps may run and more pumps only mean faster delivery.
// <para>
// The Shell owns all I/O, threads, and the clock; the Core only decides. A <see cref="PeriodicTimer"/>
// raises <c>PollDue</c>, then the loop walks the Core's <c>Step</c> commands (claim → invoke →
// report → recurse on feedback events) exactly as the Simulator's deterministic <c>DriveObservers</c>
// twin does, over a single channel/event-loop. Claims and reports stay serialized on the loop
// (bounded store calls), but <c>InvokeBatch</c> is dispatched <b>off the loop</b> on a
// <see cref="Task.Run(Func{Task})"/> — exactly as <see cref="WorkerGroupService"/> runs job
// handlers — posting <c>BatchInvoked</c> back to the channel on completion. The Core's per-Observer
// in-flight guard (released only when <c>BatchInvoked</c> is processed) makes the concurrency
// <b>across</b> Observers, so one slow or hung Observer never starves another (ADR 0020);
// invocations <b>within</b> a batch stay sequential in log order (the cursor's contiguity relies on it).
// </para>
// <para>
// A hung callback is contained by <b>proceeding, not awaiting</b>: each delivery races against a
// <see cref="ObserverPumpOptions.DeliveryTimeout"/>; a timed-out delivery is recorded
// <c>Succeeded = false</c> while the loop moves on, and the orphaned callback's exception is observed
// so a token-ignoring callback that later throws cannot resurface as an
// <see cref="TaskScheduler.UnobservedTaskException"/> (ADR 0020).
// </para>
// </summary>
internal sealed class ObserverDispatchService : BackgroundService
{
    private readonly ObserverDispatchOptions _options;
    private readonly IReadOnlyDictionary<string, Type> _observerTypes;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _deliveryTimeout;
    private readonly IJobStore _store;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ObserverDispatchService> _logger;

    // The observer pump owns the clock, exactly as the job pump does: every tick and delivery
    // timestamp comes from here so a host-registered TimeProvider governs both pumps consistently.
    // Defaults to TimeProvider.System, leaving an unconfigured host byte-for-byte unchanged.
    private readonly TimeProvider _clock;

    // <summary>
    // The observer-worker identity: auto-derived, fresh per start, non-configurable, in a claim
    // space disjoint from job Leases (ADR 0020). A crashed process's id never resurfaces; its Lease
    // simply expires and another node re-claims.
    // </summary>
    private readonly string _workerId = $"{Environment.MachineName}:observers:{Guid.NewGuid():N}";

    internal ObserverDispatchService(
        ObserverPumpOptions pump,
        IReadOnlyList<ObserverBinding> bindings,
        IJobStore store,
        IServiceScopeFactory scopeFactory,
        ILogger<ObserverDispatchService> logger,
        TimeProvider? clock = null)
    {
        _store = store;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
        _pollInterval = pump.PollInterval;
        _deliveryTimeout = pump.DeliveryTimeout;
        _observerTypes = bindings.ToDictionary(b => b.Registration.Id, b => b.ObserverType, StringComparer.Ordinal);
        _options = new ObserverDispatchOptions
        {
            WorkerId = _workerId,
            Observers = bindings.Select(b => b.Registration).ToList(),
            MaxBatch = pump.MaxBatch,
            LeaseDuration = pump.LeaseDuration,
            DeliveryRetryPolicy = pump.DeliveryRetryPolicy,
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var driver = new ObserverDispatchDriver(_options);
        var events = Channel.CreateUnbounded<ObserverEvent>(new UnboundedChannelOptions { SingleReader = true });

        _ = TickAsync(_pollInterval, stoppingToken, events.Writer, _clock);

        try
        {
            await foreach (var observerEvent in events.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                foreach (var command in driver.Step(observerEvent))
                {
                    await ExecuteAsync(command, events.Writer, stoppingToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown: in-flight claims lapse and other nodes re-claim the cursor.
        }
        catch (Exception exception)
        {
            // Fail-soft (ADR 0020): observer delivery is self-healing, so a non-cancellation
            // fault — a dead ticker's loud channel completion, or a throw out of the Core's Step —
            // stops THIS pump but never fail-stops the host. Unlike WorkerGroupService there is no
            // health to red; the cursor Lease simply lapses and another node re-claims, so a
            // low-probability fault here must not take the process down. Logged at Error, not Critical:
            // it is lower severity than a worker-group invariant halt, but still loud (the dead-ticker
            // path stays visible). Fall through to the finally — no rethrow — so the BackgroundService
            // task completes normally and the host keeps serving.
            HostingLog.ObserverPumpFaulted(_logger, exception);
        }
        finally
        {
            events.Writer.TryComplete();
        }
    }

    // <summary>The poll ticker: raises a <c>PollDue</c> each interval, the only thing that times the Core.</summary>
    private static async Task TickAsync(
        TimeSpan interval, CancellationToken cancellationToken, ChannelWriter<ObserverEvent> events, TimeProvider clock)
    {
        try
        {
            using var timer = new PeriodicTimer(interval);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                events.TryWrite(new ObserverEvent.PollDue(clock.GetUtcNow()));
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception exception)
        {
            // A dead ticker stops the pump loudly rather than idling with no polls forever.
            events.TryComplete(exception);
        }
    }

    private async ValueTask ExecuteAsync(
        ObserverCommand command, ChannelWriter<ObserverEvent> events, CancellationToken stoppingToken)
    {
        var now = _clock.GetUtcNow();
        switch (command)
        {
            case ObserverCommand.ClaimBatch claim:
                try
                {
                    var subscription = claim.Subscription;
                    var batch = await _store.ClaimObserverDeliveriesAsync(
                        new ObserverClaimRequest(
                            claim.ObserverId, subscription.States, subscription.WireName, subscription.Queue,
                            claim.WorkerId, claim.MaxRows, claim.LeaseDuration, now),
                        stoppingToken).ConfigureAwait(false);
                    events.TryWrite(new ObserverEvent.BatchClaimed(claim.ObserverId, batch.Deliveries, now));
                }
                catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                {
                    // The claim faulted at the Shell edge: no batch came back. Tell the Core the
                    // round-trip aborted so it releases the in-flight guard and re-claims next poll —
                    // a transient fault is retried, never an indefinite stall (§0078).
                    HostingLog.ObserverClaimFaulted(_logger, claim.ObserverId, exception);
                    events.TryWrite(new ObserverEvent.DeliveryAborted(claim.ObserverId, now));
                }
                break;

            case ObserverCommand.InvokeBatch invoke:
                // Dispatch OFF the loop (ADR 0020): the callback is the one unbounded thing, so it
                // runs on a Task.Run — exactly as WorkerGroupService.StartExecution runs job handlers —
                // posting BatchInvoked back to the loop on completion. The loop is free to claim and
                // report for OTHER Observers meanwhile; the Core's per-Observer in-flight guard keeps
                // THIS Observer from re-claiming until that BatchInvoked is processed. The per-delivery
                // DI scope is opened INSIDE the off-loop task. Task.Run gets CancellationToken.None so
                // a shutdown never abandons an in-flight batch before its event posts.
                _ = Task.Run(() => InvokeBatchAsync(invoke, events, stoppingToken), CancellationToken.None);
                break;

            case ObserverCommand.ReportBatch report:
                // The dead-letter count is attributed at the report edge (0081). The outcome carries
                // only the log Position, so wire_name/queue aren't cheaply available — observer_id alone.
                foreach (var outcome in report.Outcomes)
                {
                    if (outcome.Disposition == ObserverDeliveryDisposition.DeadLettered)
                    {
                        BackWaveDiagnostics.RecordObserverDeliveryDeadLettered(report.ObserverId);
                        BackWaveLog.ObserverDeliveryDeadLettered(_logger, report.ObserverId);
                    }
                }
                try
                {
                    await _store.ReportObserverDeliveriesAsync(
                        new ObserverDeliveryReport(report.ObserverId, report.WorkerId, report.Outcomes, now),
                        stoppingToken).ConfigureAwait(false);
                    events.TryWrite(new ObserverEvent.BatchReported(report.ObserverId, now));
                }
                catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                {
                    // The report faulted: the cursor stands un-advanced, so the claimed rows redeliver
                    // on a later claim (at-least-once). The in-flight guard was already released at
                    // BatchInvoked; signal the abort defensively and stop this round-trip.
                    HostingLog.ObserverReportFaulted(_logger, report.ObserverId, exception);
                    events.TryWrite(new ObserverEvent.DeliveryAborted(report.ObserverId, now));
                }
                break;

            case ObserverCommand.RequestPoll repoll:
                // The Core asked to poll again now: a batch just drained, so more rows may be claimable.
                events.TryWrite(new ObserverEvent.PollDue(repoll.Now));
                break;
        }
    }

    // <summary>
    // Invoke the host callback for each claimed row, in log order (the cursor's contiguity relies
    // on it). Runs OFF the event loop on a <see cref="Task.Run(Func{Task})"/> so a slow or hung
    // callback never stalls another Observer's claim/report on the loop; it posts a single
    // <c>BatchInvoked</c> back when the whole batch resolves. A throw is contained here and turned
    // into <c>Succeeded = false</c> — it never escapes to fail-stop the pump (§0077); the Core then
    // decides retry-with-backoff vs dead-letter. Each delivery resolves its Observer from a fresh DI
    // scope opened INSIDE this task (ADR 0020). The delivery counters (0081) are emitted at this same
    // edge the Simulator records them.
    // </summary>
    private async Task InvokeBatchAsync(
        ObserverCommand.InvokeBatch invoke, ChannelWriter<ObserverEvent> events, CancellationToken stoppingToken)
    {
        var observerType = _observerTypes[invoke.ObserverId];
        var results = new List<ObserverInvocationResult>(invoke.Deliveries.Count);
        foreach (var delivery in invoke.Deliveries)
        {
            BackWaveDiagnostics.RecordObserverDeliveryAttempted(invoke.ObserverId, delivery.WireName, delivery.Queue);
            // observer.dispatch.duration: egress latency measured around the invocation, off the injected
            // clock (a timed-out delivery still records - at least the delivery-timeout it consumed).
            var dispatchStart = _clock.GetTimestamp();
            var succeeded = await InvokeOneAsync(invoke.ObserverId, observerType, delivery, stoppingToken)
                .ConfigureAwait(false);
            BackWaveDiagnostics.RecordObserverDispatchDuration(
                invoke.ObserverId, _clock.GetElapsedTime(dispatchStart), delivery.WireName, delivery.Queue);
            if (succeeded)
            {
                BackWaveDiagnostics.RecordObserverDeliverySucceeded(invoke.ObserverId, delivery.WireName, delivery.Queue);
            }
            results.Add(new ObserverInvocationResult(delivery.Position, succeeded, delivery.DeliveryAttempt));
        }
        events.TryWrite(new ObserverEvent.BatchInvoked(invoke.ObserverId, results, _clock.GetUtcNow()));
    }

    // <summary>
    // Invoke one delivery, contained by <see cref="ObserverPumpOptions.DeliveryTimeout"/>. The
    // callback gets a token cancelled at the deadline and we race it against a delay
    // (<see cref="Task.WhenAny(Task,Task)"/>). Three outcomes:
    // <list type="bullet">
    // <item>Returns before the deadline → success.</item>
    // <item>Throws (including a cooperative <see cref="OperationCanceledException"/> on the token) →
    //   a normal failure, <c>Succeeded = false</c>.</item>
    // <item>The deadline wins first → a hung (token-ignoring) callback: we record
    //   <c>Succeeded = false</c> and RETURN WITHOUT AWAITING it (proceed-not-await, ADR 0020). The
    //   orphaned task's exception is observed in the background so a later throw cannot escape as an
    //   <see cref="TaskScheduler.UnobservedTaskException"/>; the leak is logged loudly — it is the
    //   subscriber's bug. The Core, seeing the failed result, retries-with-backoff then dead-letters.</item>
    // </list>
    // </summary>
    private async Task<bool> InvokeOneAsync(
        string observerId, Type observerType, ObserverClaimedDelivery delivery, CancellationToken stoppingToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        cts.CancelAfter(_deliveryTimeout);

        // The callback runs in its own DI scope (ADR 0020). Scope disposal is tied to the callback's
        // lifetime, not this method's: a hung callback keeps its scope alive in the background task,
        // and a returning/throwing callback disposes it inline — both handled in RunCallbackAsync.
        var callbackTask = RunCallbackAsync(observerType, delivery, cts.Token);

        // Race the callback against the deadline. The delay observes the SAME token so it completes
        // (rather than leaking a timer) the instant the callback returns or throws.
        var timeoutTask = Task.Delay(Timeout.Infinite, cts.Token);
        var winner = await Task.WhenAny(callbackTask, timeoutTask).ConfigureAwait(false);

        if (winner == callbackTask)
        {
            // The callback finished first (returned or threw). Cancel the deadline so the Task.Delay
            // completes now rather than lingering on its timer, and observe its OCE so disposing the
            // CTS below cannot leave the delay's cancellation unobserved.
            cts.Cancel();
            ObserveCancelled(timeoutTask);

            // Surface the callback's result; a throw — including a cooperative OCE on the deadline
            // token — is a contained failure (§0077). The linked CTS also fires on shutdown, so an OCE
            // while stopping is likewise just a failure here.
            try
            {
                await callbackTask.ConfigureAwait(false);
                return true;
            }
            catch (Exception exception)
            {
                HostingLog.ObserverCallbackFaulted(_logger, observerId, exception);
                return false;
            }
        }

        // The deadline won: the callback is still running and ignored its token for DeliveryTimeout.
        // PROCEED WITHOUT AWAITING IT (ADR 0020) — awaiting would wedge this Observer's in-flight guard
        // and cascade the wedge to every node that re-claims the Lease (§0078). Observe the orphan in
        // the background so a later throw can never surface as an UnobservedTaskException, and log the
        // leak loudly: it is the subscriber's bug (a callback that does not honor its CancellationToken).
        HostingLog.ObserverCallbackTimedOut(_logger, observerId, _deliveryTimeout);
        ObserveOrphan(callbackTask, observerId);
        return false;
    }

    // <summary>
    // Resolve the Observer from a fresh DI scope (opened INSIDE the off-loop task, ADR 0020) and run
    // its callback to completion. The scope is disposed only after the callback settles — so a hung,
    // leaked callback keeps its own scope alive until it (eventually) returns, never disposed out from
    // under it.
    // </summary>
    private async Task RunCallbackAsync(Type observerType, ObserverClaimedDelivery delivery, CancellationToken token)
    {
        using var scope = _scopeFactory.CreateScope();
        var observer = (ITransitionObserver)scope.ServiceProvider.GetRequiredService(observerType);
        var context = ObserverContext.FromDelivery(delivery, _store);
        await observer.OnTransitionAsync(context, token).ConfigureAwait(false);
    }

    // <summary>
    // Observe the deadline <see cref="Task.Delay(int,CancellationToken)"/> once the callback has won
    // the race: it is cancelled, never awaited, and we touch its result so it leaves nothing for the
    // finalizer to surface.
    // </summary>
    private static void ObserveCancelled(Task timeoutTask) =>
        _ = timeoutTask.ContinueWith(
            static t => _ = t.Exception, // touch to observe; a cancelled delay carries no fault anyway.
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    // <summary>
    // Observe a leaked (timed-out, still-running) callback's eventual completion so its exception is
    // never left unobserved (no <see cref="TaskScheduler.UnobservedTaskException"/>). A token-ignoring
    // callback that later throws is logged, not rethrown — the delivery already reported failed.
    // </summary>
    private void ObserveOrphan(Task callbackTask, string observerId) =>
        _ = callbackTask.ContinueWith(
            t => HostingLog.ObserverLeakedCallbackFaulted(_logger, observerId, t.Exception!),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
}
