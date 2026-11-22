using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using BackWave.Driver;

namespace BackWave.Hosting;

internal sealed class StoreLane
{
    private readonly CancellationTokenSource _cancelled = new();
    private Exception? _storeFault;
    private Ending? _ending;

    public Task Running { get; private set; } = Task.CompletedTask;

    public bool NoteStoreFault(Exception fault)
    {
        _storeFault = fault;
        return false;
    }

    public void Start(
        Func<CancellationToken, Task<NodeEvent>> call, Func<NodeEvent> unanswered, ChannelWriter<NodeEvent> events) =>
        Running = RunAsync(call, unanswered, events);

    /// <summary>Cancels the call in flight and every call started after it.</summary>
    public void Cancel() => _cancelled.Cancel();

    public Ending? Take() => Interlocked.Exchange(ref _ending, null);

    private async Task RunAsync(
        Func<CancellationToken, Task<NodeEvent>> call, Func<NodeEvent> unanswered, ChannelWriter<NodeEvent> events)
    {
        NodeEvent landed;
        try
        {
            landed = await call(_cancelled.Token).ConfigureAwait(false);
            _ending = Ending.Answered;
        }
        catch (Exception fault)
        {
            landed = unanswered();
            _ending = new Ending(ExceptionDispatchInfo.Capture(fault), ReferenceEquals(fault, _storeFault));
        }
        events.TryWrite(landed);
    }

    public sealed record Ending(ExceptionDispatchInfo? Fault, bool FromStore)
    {
        public static readonly Ending Answered = new(null, false);
    }
}
