using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using BackWave.Benchmarks.Latency;

namespace BackWave.Benchmarks.Tests;

/// <summary>
/// The proxy that prices the network hop (bench-0265). It has to charge one delay per round trip - not per
/// call, and not per TCP segment - and it has to hand every byte through unchanged. Get either wrong and a
/// latency-profile run measures the proxy instead of the adapter.
/// </summary>
public sealed class LatencyProxyTests
{
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(50);

    [Fact]
    public async Task Every_round_trip_pays_the_delay_once()
    {
        // Round-trip COUNT is the thing the dial exists to price, so N sequential exchanges must cost N
        // delays. The direct baseline is measured on the same sockets so the comparison is the proxy alone.
        await using var echo = await EchoServer.StartAsync();
        const int trips = 5;

        var direct = await TimeRoundTripsAsync(echo.Port, trips);

        await using var proxy = LatencyProxy.Start("127.0.0.1", echo.Port, Delay);
        var proxied = await TimeRoundTripsAsync(proxy.Port, trips);

        var added = proxied - direct;
        Assert.True(
            added >= Delay * trips * 0.8,
            $"expected roughly {(Delay * trips).TotalMilliseconds}ms added over {trips} round trips, " +
            $"got {added.TotalMilliseconds:N0}ms (direct {direct.TotalMilliseconds:N0}ms, proxied {proxied.TotalMilliseconds:N0}ms)");
        Assert.Equal(trips, (int)Math.Min(proxy.HeldSegments, trips));
    }

    [Fact]
    public async Task The_lowest_dial_setting_still_costs_a_whole_millisecond_per_round_trip()
    {
        // The floor is the trap. A segment always spends a moment in the channel, so the wait left at a
        // 1ms setting is always just under 1ms - and Task.Delay truncates that to no wait at all. The dial
        // would then read as engaged and cost nothing, which is the one failure it must not have.
        await using var echo = await EchoServer.StartAsync();
        const int trips = 50;
        var floor = LatencyProfile.MinimumRoundTrip;

        var direct = await TimeRoundTripsAsync(echo.Port, trips);

        await using var proxy = LatencyProxy.Start("127.0.0.1", echo.Port, floor);
        var proxied = await TimeRoundTripsAsync(proxy.Port, trips);

        var added = proxied - direct;
        Assert.True(
            added >= floor * trips * 0.8,
            $"expected roughly {(floor * trips).TotalMilliseconds}ms added over {trips} round trips at the " +
            $"{floor.TotalMilliseconds:0}ms floor, got {added.TotalMilliseconds:N0}ms " +
            $"(direct {direct.TotalMilliseconds:N0}ms, proxied {proxied.TotalMilliseconds:N0}ms)");
        Assert.True(
            proxy.MeanHold >= floor,
            $"the mean hold was {proxy.MeanHold.TotalMilliseconds:N3}ms, under the {floor.TotalMilliseconds:0}ms setting");
    }

    [Fact]
    public async Task One_large_response_pays_the_delay_once_not_once_per_segment()
    {
        // A wire with latency delays a burst of segments together; it does not delay each one after the
        // last. Charging per segment would make every large read cost a multiple of the setting, which
        // would swamp the round-trip signal the dial is measuring.
        await using var echo = await EchoServer.StartAsync();
        await using var proxy = LatencyProxy.Start("127.0.0.1", echo.Port, Delay);

        var payload = Payload(4 * 1024 * 1024);
        var stopwatch = Stopwatch.StartNew();
        var echoed = await ExchangeAsync(proxy.Port, payload);
        stopwatch.Stop();

        Assert.Equal(payload.Length, echoed.Length);
        Assert.True(proxy.HeldSegments > 4, $"expected the response to span many segments, held {proxy.HeldSegments}");
        Assert.True(
            stopwatch.Elapsed < Delay * 5,
            $"a {payload.Length / (1024 * 1024)}MB response took {stopwatch.ElapsedMilliseconds}ms across " +
            $"{proxy.HeldSegments} segments; per-segment charging would cost about " +
            $"{proxy.HeldSegments * Delay.TotalMilliseconds:N0}ms");
    }

    [Fact]
    public async Task Bytes_cross_the_proxy_intact_and_in_order()
    {
        await using var echo = await EchoServer.StartAsync();
        await using var proxy = LatencyProxy.Start("127.0.0.1", echo.Port, TimeSpan.FromMilliseconds(1));

        var payload = Payload(1024 * 1024);

        Assert.Equal(payload, await ExchangeAsync(proxy.Port, payload));
    }

    [Fact]
    public async Task Connections_are_delayed_independently_of_each_other()
    {
        // The harness runs a whole connection pool through one proxy. If the delay serialized across
        // connections the proxy would become the bottleneck and the run would measure it, not the adapter.
        await using var echo = await EchoServer.StartAsync();
        await using var proxy = LatencyProxy.Start("127.0.0.1", echo.Port, Delay);
        const int connections = 8;

        var stopwatch = Stopwatch.StartNew();
        await Task.WhenAll(Enumerable.Range(0, connections).Select(_ => ExchangeAsync(proxy.Port, Payload(64))));
        stopwatch.Stop();

        Assert.True(
            stopwatch.Elapsed < Delay * connections / 2,
            $"{connections} concurrent round trips took {stopwatch.ElapsedMilliseconds}ms; " +
            $"serialized they would cost about {connections * Delay.TotalMilliseconds:N0}ms");
    }

    private static byte[] Payload(int size)
    {
        var payload = new byte[size];
        new Random(20260822).NextBytes(payload);
        return payload;
    }

    private static async Task<TimeSpan> TimeRoundTripsAsync(int port, int trips)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        client.NoDelay = true;
        var stream = client.GetStream();
        var request = Payload(8);
        var response = new byte[request.Length];

        var stopwatch = Stopwatch.StartNew();
        for (var trip = 0; trip < trips; trip++)
        {
            await stream.WriteAsync(request);
            await stream.ReadExactlyAsync(response);
        }

        return stopwatch.Elapsed;
    }

    // Writes the payload and reads the echo concurrently: a payload larger than the socket buffers
    // deadlocks if the client waits for the whole write to finish before it starts reading.
    private static async Task<byte[]> ExchangeAsync(int port, byte[] payload)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        client.NoDelay = true;
        var stream = client.GetStream();

        var echoed = new byte[payload.Length];
        var read = stream.ReadExactlyAsync(echoed).AsTask();
        await stream.WriteAsync(payload);
        await read;
        return echoed;
    }

    /// <summary>A loopback echo server: whatever it is sent, it sends straight back.</summary>
    private sealed class EchoServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _shutdown = new();
        private readonly Task _acceptLoop;

        private EchoServer(TcpListener listener)
        {
            _listener = listener;
            _acceptLoop = AcceptAsync(_shutdown.Token);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public static Task<EchoServer> StartAsync()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return Task.FromResult(new EchoServer(listener));
        }

        public async ValueTask DisposeAsync()
        {
            await _shutdown.CancelAsync();
            _listener.Stop();
            try
            {
                await _acceptLoop;
            }
            catch (OperationCanceledException)
            {
                // Expected: shutdown.
            }

            _shutdown.Dispose();
        }

        private async Task AcceptAsync(CancellationToken cancellationToken)
        {
            var clients = new List<Task>();
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(cancellationToken);
                    clients.Add(EchoAsync(client, cancellationToken));
                }
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                // Expected: shutdown.
            }
            catch (SocketException)
            {
                // Expected: the listener was stopped under the pending accept.
            }
            finally
            {
                await Task.WhenAll(clients);
            }
        }

        private static async Task EchoAsync(TcpClient client, CancellationToken cancellationToken)
        {
            using var _ = client;
            client.NoDelay = true;
            try
            {
                var stream = client.GetStream();
                await stream.CopyToAsync(stream, cancellationToken);
            }
            catch (Exception failure)
                when (failure is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
            {
                // Expected: the peer closed, or shutdown.
            }
        }
    }
}
