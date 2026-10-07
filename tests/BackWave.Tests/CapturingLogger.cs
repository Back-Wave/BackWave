using Microsoft.Extensions.Logging;

namespace BackWave.Tests;

// A minimal in-memory logger capture for the logs-pillar tests: it records each entry's level, event id,
// formatted message, and the flattened key/value pairs of the scopes open when it was logged. Toggling
// Enabled off makes IsEnabled return false, which drives the [LoggerMessage] source-generator's guard so
// no entry is produced - the same path a NullLogger takes.

internal sealed record LogRecord(
    LogLevel Level, int EventId, string Message, IReadOnlyList<KeyValuePair<string, object?>> Scope);

internal sealed class LogCapture
{
    private readonly List<LogRecord> _records = [];

    // A handler the pump abandoned unwinds on a pool thread and can log while the test reads, so a read
    // takes a copy under the same lock the logger writes under.
    public IReadOnlyList<LogRecord> Records
    {
        get
        {
            lock (_records)
            {
                return [.. _records];
            }
        }
    }

    public void Add(LogRecord record)
    {
        lock (_records)
        {
            _records.Add(record);
        }
    }

    public bool Enabled { get; set; } = true;
}

internal sealed class CapturingLogger(LogCapture capture) : ILogger
{
    // Scopes follow the async flow that opened them, as in a real logging provider, so a handler that
    // unwinds on a pool thread neither sees nor closes the scopes of the code that drives the pump.
    private readonly LoggerExternalScopeProvider _scopes = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => _scopes.Push(state);

    public bool IsEnabled(LogLevel logLevel) => capture.Enabled;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var scope = new List<KeyValuePair<string, object?>>();
        _scopes.ForEachScope(
            (open, collected) =>
            {
                if (open is IEnumerable<KeyValuePair<string, object?>> pairs)
                {
                    collected.AddRange(pairs);
                }
            },
            scope);
        capture.Add(new LogRecord(logLevel, eventId.Id, formatter(state, exception), scope));
    }
}

internal sealed class CapturingLoggerFactory(LogCapture capture) : ILoggerFactory
{
    // One shared logger for every category, so the client and the pump write into the same capture.
    private readonly CapturingLogger _logger = new(capture);

    public ILogger CreateLogger(string categoryName) => _logger;

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public void Dispose()
    {
    }
}
