using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace DidComm.InteropTests.Transports;

/// <summary>
/// Records every log line a host writes, with its exception text appended the way a real sink
/// writes it, so a test can show a value never reached the logs.
/// </summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _entries = new();

    /// <summary>Every formatted entry, each followed by its exception's <c>ToString()</c> when there is one.</summary>
    public IReadOnlyCollection<string> Entries => _entries;

    public ILogger CreateLogger(string categoryName) => new Logger(_entries);

    public void Dispose()
    {
    }

    private sealed class Logger : ILogger
    {
        private readonly ConcurrentQueue<string> _entries;

        public Logger(ConcurrentQueue<string> entries) => _entries = entries;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => _entries.Enqueue(formatter(state, exception) + (exception is null ? string.Empty : Environment.NewLine + exception));
    }
}
