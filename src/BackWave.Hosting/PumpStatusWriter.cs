using BackWave.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BackWave.Hosting;

internal sealed class PumpStatusWriter(
    IJobStore store, BackWaveHealth health, ILogger<PumpStatusWriter> logger, TimeProvider clock)
    : IHostedLifecycleService, IDisposable
{
    internal static readonly TimeSpan WriteInterval = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan ReapAfter = TimeSpan.FromHours(1);
    internal const int MaxReap = 100;
    internal static readonly TimeSpan StopWriteBudget = TimeSpan.FromSeconds(2);

    /// <summary>The least step between two reports from one process, coarse enough to survive a store that keeps only milliseconds.</summary>
    internal static readonly TimeSpan ReportedAtStep = TimeSpan.FromMilliseconds(1);

    private readonly CancellationTokenSource _loopStopped = new();
    private readonly object _reportedAtGate = new();
    private DateTimeOffset _lastReportedAt = DateTimeOffset.MinValue;
    private Task _loop = Task.CompletedTask;
    private bool _failureLogged;

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken)
    {
        _loop = RunAsync(_loopStopped.Token);
        return Task.CompletedTask;
    }

    public async Task StoppingAsync(CancellationToken cancellationToken)
    {
        await _loopStopped.CancelAsync().ConfigureAwait(false);
        using var budget = StopWriteBudgetFrom(cancellationToken);
        await _loop.WaitAsync(budget.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        await WriteOnceAsync(HostPhase.Stopping, budget.Token).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StoppedAsync(CancellationToken cancellationToken)
    {
        using var budget = StopWriteBudgetFrom(cancellationToken);
        await WriteOnceAsync(HostPhase.Stopped, budget.Token).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (!_loopStopped.IsCancellationRequested)
        {
            _loopStopped.Cancel();
        }
        _loopStopped.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(WriteInterval, clock);
        try
        {
            do
            {
                await WriteOnceAsync(HostPhase.Running, cancellationToken).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static DateTimeOffset NextReportedAt(DateTimeOffset now, DateTimeOffset previous)
    {
        var next = previous + ReportedAtStep;
        return now > next ? now : next;
    }

    private static CancellationTokenSource StopWriteBudgetFrom(CancellationToken hostStopToken)
    {
        var budget = CancellationTokenSource.CreateLinkedTokenSource(hostStopToken);
        budget.CancelAfter(StopWriteBudget);
        return budget;
    }

    private async Task WriteOnceAsync(HostPhase hostPhase, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        DateTimeOffset reportedAt;
        lock (_reportedAtGate)
        {
            reportedAt = _lastReportedAt = NextReportedAt(now, _lastReportedAt);
        }
        var statuses = health.PumpStatuses(now, reportedAt, WriteInterval, hostPhase);
        if (statuses.Count == 0)
        {
            return;
        }
        try
        {
            await store.WritePumpStatusesAsync(statuses, now - ReapAfter, MaxReap, cancellationToken).ConfigureAwait(false);
            if (_failureLogged)
            {
                _failureLogged = false;
                HostingLog.PumpStatusWriteRecovered(logger);
            }
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (!_failureLogged)
            {
                _failureLogged = true;
                HostingLog.PumpStatusWriteFailed(logger, exception);
            }
        }
    }
}
