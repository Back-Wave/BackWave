using Npgsql;

namespace BackWave.Postgres.Tests;

/// <summary>
/// A dedicated, single physical connection that LISTENs on the hint channel and records the
/// payloads (queue names) of every NOTIFY it receives.
/// </summary>
internal sealed class HintListener : IAsyncDisposable
{
    // The default-schema hint channel pg_notify publishes to (SchemaRewriter: "backwave_hints").
    private const string HintChannel = "backwave_hints";

    private readonly NpgsqlConnection _connection;
    private readonly List<string> _payloads = [];

    private HintListener(NpgsqlConnection connection) => _connection = connection;

    public static async Task<HintListener> StartAsync()
    {
        // A LISTEN connection must never be pooled: a returned connection can carry a lingering
        // LISTEN registration and buffered NOTIFYs into the next test that reuses the physical
        // backend. A dedicated, unpooled backend sees only the notifications sent while it listens.
        var unpooled = new NpgsqlConnectionStringBuilder(PostgresTestDatabase.ConnectionString)
        {
            Pooling = false,
        }.ConnectionString;
        var connection = new NpgsqlConnection(unpooled);
        await connection.OpenAsync();
        var listener = new HintListener(connection);
        connection.Notification += (_, args) =>
        {
            if (args.Channel == HintChannel)
            {
                lock (listener._payloads)
                {
                    listener._payloads.Add(args.Payload);
                }
            }
        };
        await using var listen = new NpgsqlCommand($"LISTEN {HintChannel}", connection);
        await listen.ExecuteNonQueryAsync();
        return listener;
    }

    /// <summary>
    /// Collects every hint delivered until the channel goes quiet for <paramref name="quietWindow"/>,
    /// so a "no hint" case returns empty and a single hint returns exactly one payload.
    /// </summary>
    public async Task<IReadOnlyList<string>> DrainAsync(TimeSpan? quietWindow = null)
    {
        var window = quietWindow ?? TimeSpan.FromSeconds(1);
        while (await _connection.WaitAsync(window))
        {
        }
        lock (_payloads)
        {
            return _payloads.ToArray();
        }
    }

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();
}
