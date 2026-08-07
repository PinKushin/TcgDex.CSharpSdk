namespace TcgDex.Tests.Diagnostics;

using Microsoft.Extensions.Logging;

/// <summary>A captured log message.</summary>
internal sealed record LogEntry(LogLevel Level, int EventId, string Message, Exception? Exception);

/// <summary>
/// Captures what the SDK logs, and counts how often a message was actually
/// formatted.
/// </summary>
/// <remarks>
/// The formatter count is the interesting part: it proves that a disabled level
/// costs nothing beyond an <c>IsEnabled</c> check, rather than building a string
/// and throwing it away.
/// </remarks>
internal sealed class RecordingLogger(LogLevel minimum) : ILogger, ILoggerProvider
{
    internal List<LogEntry> Entries { get; } = [];

    /// <summary>
    /// Category names the SDK asked for. Consumers filter on this, so it is
    /// part of the observable surface rather than an implementation detail.
    /// </summary>
    internal List<string> Categories { get; } = [];

    /// <summary>How many times a message was materialised into a string.</summary>
    internal int FormatterInvocations { get; private set; }

    internal ILoggerFactory Factory => new RecordingLoggerFactory(this);

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => minimum != LogLevel.None && logLevel >= minimum;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        Guard.NotNull(formatter);

        FormatterInvocations++;
        Entries.Add(new LogEntry(logLevel, eventId.Id, formatter(state, exception), exception));
    }

    public ILogger CreateLogger(string categoryName) => this;

    public void Dispose()
    {
        // Nothing to release; the recorded entries outlive the provider so tests
        // can assert on them.
    }

    private sealed class RecordingLoggerFactory(RecordingLogger logger) : ILoggerFactory
    {
        public void AddProvider(ILoggerProvider provider)
        {
            // Single fixed provider by design.
        }

        public ILogger CreateLogger(string categoryName)
        {
            logger.Categories.Add(categoryName);
            return logger;
        }

        public void Dispose()
        {
            // Nothing to release.
        }
    }
}
