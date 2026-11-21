using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace BackWave.Postgres.Tests;

public sealed class WakeUpHintCoalescerTests
{
    private static readonly TimeSpan WrongSendGrace = TimeSpan.FromMilliseconds(200);

    [Fact]
    public async Task RequestsDuringASend_JoinOneTrailingSend_ThatStartsAfterIt()
    {
        var sends = new GatedSends();
        await using var coalescer = new WakeUpHintCoalescer(sends.SendAsync, new CountingLogger());

        coalescer.Request("a");
        var first = await sends.NextAsync();
        Assert.Equal(["a"], first.Queues);

        coalescer.Request("a");
        coalescer.Request("b");
        coalescer.Request("b");
        await Task.Delay(WrongSendGrace);
        Assert.False(sends.AnyStarted);

        first.Release.SetResult();
        var trailing = await sends.NextAsync();
        Assert.Equal(["a", "b"], trailing.Queues.Order());

        trailing.Release.SetResult();
        await Task.Delay(WrongSendGrace);
        Assert.False(sends.AnyStarted);
    }

    [Fact]
    public async Task ARequestAfterTheLoopGoesIdle_StartsANewSend()
    {
        var sends = new GatedSends();
        await using var coalescer = new WakeUpHintCoalescer(sends.SendAsync, new CountingLogger());

        coalescer.Request("a");
        (await sends.NextAsync()).Release.SetResult();
        await Task.Delay(WrongSendGrace);

        coalescer.Request("a");
        var next = await sends.NextAsync();
        Assert.Equal(["a"], next.Queues);
        next.Release.SetResult();
    }

    [Fact]
    public async Task AFailedSend_StillLeavesTheTrailingSend_AndWarnsOncePerStore()
    {
        var sends = new GatedSends();
        var logger = new CountingLogger();
        await using var coalescer = new WakeUpHintCoalescer(sends.SendAsync, logger);

        coalescer.Request("q");
        var first = await sends.NextAsync();
        coalescer.Request("q");
        first.Release.SetException(new InvalidOperationException("channel down"));

        var trailing = await sends.NextAsync();
        trailing.Release.SetException(new InvalidOperationException("channel down"));
        await Task.Delay(WrongSendGrace);

        coalescer.Request("q");
        (await sends.NextAsync()).Release.SetResult();

        Assert.Equal(1, logger.Warnings);
    }

    [Fact]
    public async Task Dispose_FirstSendsTheHintsAlreadyRequested()
    {
        var sends = new GatedSends();
        var coalescer = new WakeUpHintCoalescer(sends.SendAsync, new CountingLogger());

        coalescer.Request("a");
        var first = await sends.NextAsync();
        coalescer.Request("b");

        var disposing = coalescer.DisposeAsync().AsTask();
        first.Release.SetResult();
        var owed = await sends.NextAsync();
        Assert.Equal(["b"], owed.Queues);
        owed.Release.SetResult();

        await disposing.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, sends.Started);
    }

    [Fact]
    public async Task Dispose_CancelsASendThatOutlastsTheFlush_AndStartsNoMore()
    {
        var sends = new GatedSends();
        var logger = new CountingLogger();
        var coalescer = new WakeUpHintCoalescer(sends.SendAsync, logger);

        coalescer.Request("q");
        await sends.NextAsync();
        coalescer.Request("q");

        await coalescer.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, sends.Started);
        Assert.True(sends.AllCancelled);

        coalescer.Request("q");
        await Task.Delay(WrongSendGrace);
        Assert.Equal(1, sends.Started);
        Assert.Equal(0, logger.Warnings);
    }

    private sealed class GatedSends
    {
        private readonly Channel<(IReadOnlyCollection<string> Queues, TaskCompletionSource Release)> _started =
            Channel.CreateUnbounded<(IReadOnlyCollection<string> Queues, TaskCompletionSource Release)>();
        private readonly List<Task> _sends = [];

        public bool AnyStarted => _started.Reader.TryPeek(out _);

        public int Started
        {
            get
            {
                lock (_sends)
                {
                    return _sends.Count;
                }
            }
        }

        public bool AllCancelled
        {
            get
            {
                lock (_sends)
                {
                    return _sends.All(send => send.IsCanceled);
                }
            }
        }

        public Task SendAsync(IReadOnlyCollection<string> queues, CancellationToken cancellationToken)
        {
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var send = release.Task.WaitAsync(cancellationToken);
            lock (_sends)
            {
                _sends.Add(send);
            }
            _started.Writer.TryWrite((queues, release));
            return send;
        }

        public async Task<(IReadOnlyCollection<string> Queues, TaskCompletionSource Release)> NextAsync()
            => await _started.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class CountingLogger : ILogger
    {
        private int _warnings;

        public int Warnings => Volatile.Read(ref _warnings);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Interlocked.Increment(ref _warnings);
            }
        }
    }
}
