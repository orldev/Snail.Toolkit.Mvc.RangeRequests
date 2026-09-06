using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Snail.Toolkit.Mvc.RangeRequests.Tests;

/// <summary>
/// Keeps every log entry the server produced, so a test can assert on the level it was written at.
/// </summary>
internal sealed class RecordingLogs : ILoggerProvider
{
    private readonly ConcurrentQueue<(LogLevel Level, string Message)> _entries = new();

    internal IReadOnlyList<(LogLevel Level, string Message)> Entries => [.. _entries];

    public ILogger CreateLogger(string categoryName) => new Recorder(_entries);

    public void Dispose()
    {
    }

    private sealed class Recorder(ConcurrentQueue<(LogLevel Level, string Message)> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => entries.Enqueue((logLevel, formatter(state, exception)));
    }
}
