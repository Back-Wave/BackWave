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
    public List<LogRecord> Records { get; } = [];

    public bool Enabled { get; set; } = true;
}

internal sealed class CapturingLogger(LogCapture capture) : ILogger
{
    // The pump drives one execution at a time and scopes open/close in order, so a simple stack is enough
    // for these single-job tests.
    private readonly List<object?> _scopes = [];

    public IDisposable BeginScope<TState>(TState state) where TState : notnull
    {
        _scopes.Add(state);
        return new Pop(_scopes);
    }

    public bool IsEnabled(LogLevel logLevel) => capture.Enabled;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var scope = new List<KeyValuePair<string, object?>>();
        foreach (var open in _scopes)
        {
            if (open is IEnumerable<KeyValuePair<string, object?>> pairs)
            {
                scope.AddRange(pairs);
            }
        }
        capture.Records.Add(new LogRecord(logLevel, eventId.Id, formatter(state, exception), scope));
    }

    private sealed class Pop(List<object?> scopes) : IDisposable
    {
        public void Dispose() => scopes.RemoveAt(scopes.Count - 1);
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
