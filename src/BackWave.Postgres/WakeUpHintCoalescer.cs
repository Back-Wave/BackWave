using BackWave.Diagnostics;
using Microsoft.Extensions.Logging;

namespace BackWave.Postgres;

// Postgres commits NOTIFY-carrying transactions one at a time cluster-wide, so a NOTIFY per enqueue caps
// enqueue at that commit rate.
internal sealed class WakeUpHintCoalescer(
    Func<IReadOnlyCollection<string>, CancellationToken, Task> send, ILogger logger) : IAsyncDisposable
{
    // A store disposed right after its last enqueue would otherwise drop that enqueue's hint.
    private static readonly TimeSpan DisposeFlushBudget = TimeSpan.FromSeconds(1);

    private readonly object _gate = new();
    private readonly HashSet<string> _pending = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stop = new();
    private TaskCompletionSource? _drained;
    private bool _sending;
    private bool _disposed;
    private int _failureLogged;

    // A Queue requested while a send is in flight joins the next send, which starts after the current
    // one ends, so every request is covered by a send that starts after it.
    public void Request(string queue)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _pending.Add(queue);
            if (_sending)
            {
                return;
            }
            _sending = true;
        }
        StartSendLoopOutsideCallerFlow();
    }

    // Unsafe, so the loop inherits no ExecutionContext: its NOTIFY is no child of the enqueue's Activity
    // and no statement of the enqueue's round-trip scope.
    private void StartSendLoopOutsideCallerFlow() =>
        ThreadPool.UnsafeQueueUserWorkItem(
            static coalescer => _ = coalescer.SendLoopAsync(), this, preferLocal: false);

    private async Task SendLoopAsync()
    {
        while (true)
        {
            string[] queues;
            lock (_gate)
            {
                if (_pending.Count == 0 || _stop.IsCancellationRequested)
                {
                    _sending = false;
                    _drained?.TrySetResult();
                    return;
                }
                queues = [.. _pending];
                _pending.Clear();
            }
            try
            {
                await send(queues, _stop.Token).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                if (!_stop.IsCancellationRequested && Interlocked.Exchange(ref _failureLogged, 1) == 0)
                {
                    BackWaveLog.WakeHintChannelUnavailable(logger, "postgresql", exception);
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task drained;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_sending)
            {
                _drained.SetResult();
            }
            drained = _drained.Task;
        }
        await Task.WhenAny(drained, Task.Delay(DisposeFlushBudget)).ConfigureAwait(false);
        await _stop.CancelAsync().ConfigureAwait(false);
        await drained.ConfigureAwait(false);
        _stop.Dispose();
    }
}
