using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;

namespace BackWave.Benchmarks.Latency;

/// <summary>
/// A loopback TCP proxy that prices the network hop the harness would pay against a remote database.
/// It listens on an ephemeral loopback port, forwards every byte to the real database, and holds each
/// inbound segment for the configured delay before handing it to the client.
/// <para>
/// The whole delay is charged on the inbound leg rather than split across both. A client blocks on the
/// response, so one request plus one response costs exactly one delay, which is the round-trip price the
/// dial is meant to model. Charging half each way would need a sub-millisecond wait that the task timer
/// cannot deliver.
/// </para>
/// <para>
/// Inbound segments are held by arrival time, not one after another: a reader loop stamps each segment
/// with its due time and a writer loop releases it then. A large response therefore pays the delay once,
/// the way a high-bandwidth link with a long wire does, instead of once per TCP segment.
/// </para>
/// </summary>
public sealed class LatencyProxy : IAsyncDisposable
{
    private const int BufferSize = 64 * 1024;

    private readonly TcpListener _listener;
    private readonly string _upstreamHost;
    private readonly int _upstreamPort;
    private readonly TimeSpan _delay;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _acceptLoop;

    private long _heldSegments;
    private long _heldTicks;

    private LatencyProxy(TcpListener listener, string upstreamHost, int upstreamPort, TimeSpan delay)
    {
        _listener = listener;
        _upstreamHost = upstreamHost;
        _upstreamPort = upstreamPort;
        _delay = delay;
        _acceptLoop = AcceptLoopAsync(_shutdown.Token);
    }

    /// <summary>The loopback port the harness connects to. Assigned by the OS when the proxy starts.</summary>
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>How many inbound segments have been held so far. Zero after a run means the dial did nothing.</summary>
    public long HeldSegments => Interlocked.Read(ref _heldSegments);

    /// <summary>
    /// The mean delay actually applied to a held segment. The task timer overshoots a short wait, so this
    /// runs a little above the requested delay; report it beside the setting rather than assume they match.
    /// </summary>
    public TimeSpan MeanHold
    {
        get
        {
            var segments = Interlocked.Read(ref _heldSegments);
            return segments == 0 ? TimeSpan.Zero : TimeSpan.FromTicks(Interlocked.Read(ref _heldTicks) / segments);
        }
    }

    /// <summary>
    /// Starts a proxy in front of <paramref name="upstreamHost"/>:<paramref name="upstreamPort"/> that adds
    /// <paramref name="delay"/> to every round trip. The listener is bound before this returns, so the port
    /// is ready to be written into a connection string.
    /// </summary>
    public static LatencyProxy Start(string upstreamHost, int upstreamPort, TimeSpan delay)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return new LatencyProxy(listener, upstreamHost, upstreamPort, delay);
    }

    /// <summary>Stops accepting, tears down the open connections, and releases the listener.</summary>
    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        try
        {
            await _acceptLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected: shutdown cancels the accept loop.
        }

        _shutdown.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        var connections = new List<Task>();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                connections.RemoveAll(c => c.IsCompleted);
                connections.Add(ForwardConnectionAsync(client, cancellationToken));
            }
        }
        catch (OperationCanceledException)
        {
            // Expected: shutdown cancels the pending accept.
        }
        catch (SocketException)
        {
            // Expected: the listener was stopped out from under the pending accept.
        }
        finally
        {
            await Task.WhenAll(connections).ConfigureAwait(false);
        }
    }

    private async Task ForwardConnectionAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using var _ = client;
        using var upstream = new TcpClient();
        using var connection = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        client.NoDelay = true;
        try
        {
            await upstream.ConnectAsync(_upstreamHost, _upstreamPort, connection.Token).ConfigureAwait(false);
            upstream.NoDelay = true;

            // Outbound (harness to database) is forwarded as it arrives; the whole hop is charged inbound.
            var outbound = PumpAsync(client.GetStream(), upstream.GetStream(), delayed: false, connection.Token);
            var inbound = PumpAsync(upstream.GetStream(), client.GetStream(), delayed: true, connection.Token);

            // One side closing ends the connection. Cancel the other before the sockets go away, so it
            // unwinds on its own token instead of on a disposed stream.
            await Task.WhenAny(outbound, inbound).ConfigureAwait(false);
            await connection.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(outbound, inbound).ConfigureAwait(false);
        }
        catch (Exception failure) when (IsConnectionTeardown(failure))
        {
            // Expected: shutdown, or the database refused or dropped the connection.
        }
    }

    // A pump owns its own failures: the peer closing, the socket going away, or shutdown all end this
    // direction and nothing else. Letting them escape would fault the sibling pump's await instead.
    private async Task PumpAsync(
        NetworkStream source, NetworkStream destination, bool delayed, CancellationToken cancellationToken)
    {
        try
        {
            await CopyAsync(source, destination, delayed, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure) when (IsConnectionTeardown(failure))
        {
            // Expected: this direction is done.
        }
    }

    private static bool IsConnectionTeardown(Exception failure)
        => failure is OperationCanceledException or IOException or SocketException or ObjectDisposedException;

    private async Task CopyAsync(
        NetworkStream source, NetworkStream destination, bool delayed, CancellationToken cancellationToken)
    {
        if (!delayed)
        {
            await source.CopyToAsync(destination, BufferSize, cancellationToken).ConfigureAwait(false);
            return;
        }

        // Reader stamps, writer releases. Keeping them apart is what makes the delay a property of the wire
        // and not of the response size: segments that arrive together leave together, one delay later.
        var segments = Channel.CreateUnbounded<HeldSegment>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

        var writer = WriteHeldSegmentsAsync(segments.Reader, destination, cancellationToken);
        try
        {
            var buffer = new byte[BufferSize];
            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                await segments.Writer.WriteAsync(
                    new HeldSegment(buffer[..read], Stopwatch.GetTimestamp()), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            segments.Writer.TryComplete();
            await writer.ConfigureAwait(false);
        }
    }

    private async Task WriteHeldSegmentsAsync(
        ChannelReader<HeldSegment> segments, NetworkStream destination, CancellationToken cancellationToken)
    {
        await foreach (var segment in segments.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            var remaining = _delay - Stopwatch.GetElapsedTime(segment.ArrivedAt);
            if (remaining > TimeSpan.Zero)
            {
                // Rounded up, because Task.Delay truncates a TimeSpan to whole milliseconds. A segment
                // always spends a little time in the channel, so the remainder at the 1ms floor is always
                // just under 1ms and would truncate to no wait at all. Overshooting by under a millisecond
                // is honest and is reported in the achieved mean; waiting zero is not.
                var wait = TimeSpan.FromMilliseconds(Math.Ceiling(remaining.TotalMilliseconds));
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            }

            Interlocked.Increment(ref _heldSegments);
            Interlocked.Add(ref _heldTicks, Stopwatch.GetElapsedTime(segment.ArrivedAt).Ticks);
            await destination.WriteAsync(segment.Payload, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>One inbound segment and the instant it reached the proxy, which is what its release is timed from.</summary>
    private readonly record struct HeldSegment(byte[] Payload, long ArrivedAt);
}
