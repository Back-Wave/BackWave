using Microsoft.Extensions.Logging;

namespace BackWave.Sqlite.Tests;

// A minimal in-memory logger capture for the adapter's logs-pillar assertions: it records each entry's
// level, event id, and formatted message. The [LoggerMessage] catalog guards on IsEnabled, so leaving
// Enabled true is enough to observe an emitted event.

internal sealed record LogRecord(LogLevel Level, int EventId, string Message);

internal sealed class LogCapture
{
    public List<LogRecord> Records { get; } = [];
}

internal sealed class CapturingLoggerFactory(LogCapture capture) : ILoggerFactory
{
    // One shared logger for every category, so whichever category the store logs under is captured.
    private readonly CapturingLogger _logger = new(capture);

    public ILogger CreateLogger(string categoryName) => _logger;

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(LogCapture capture) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => capture.Records.Add(new LogRecord(logLevel, eventId.Id, formatter(state, exception)));
    }
}
