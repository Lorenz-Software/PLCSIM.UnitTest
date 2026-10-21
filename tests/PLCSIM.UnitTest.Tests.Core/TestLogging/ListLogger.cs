using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PLCSIM.UnitTest.Tests.Core.TestLogging;

internal sealed class ListLogger<T> : ILogger<T>
{
    private readonly List<LogEntry> entries = [];

    public IReadOnlyList<LogEntry> Entries => entries;

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
    {
        return null;
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return true;
    }

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
    }
}

internal sealed record LogEntry(
    LogLevel Level,
    string Message,
    Exception? Exception);

internal sealed class ListLoggerFactory<T> : ILoggerFactory
{
    public ListLogger<T> Logger { get; } = new();

    public ILogger CreateLogger(string categoryName)
    {
        return string.Equals(categoryName, typeof(T).FullName, StringComparison.Ordinal)
            ? Logger
            : NullLogger.Instance;
    }

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public void Dispose()
    {
    }
}
