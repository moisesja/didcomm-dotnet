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
    private readonly ConcurrentQueue<(LogLevel Level, string Text)> _records = new();
    private readonly ConcurrentQueue<(string Category, LogLevel Level, string Text)> _categorized = new();

    /// <summary>Every formatted entry, each followed by its exception's <c>ToString()</c> when there is one.</summary>
    public IReadOnlyCollection<string> Entries => _entries;

    /// <summary>The same entries with their levels, for tests that assert a level.</summary>
    public IReadOnlyCollection<(LogLevel Level, string Text)> Records => _records;

    /// <summary>The same entries with the category of the logger that wrote each, for tests that tell the library's lines from the framework's.</summary>
    public IReadOnlyCollection<(string Category, LogLevel Level, string Text)> CategorizedRecords => _categorized;

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _entries, _records, _categorized);

    public void Dispose()
    {
    }

    private sealed class Logger : ILogger
    {
        private readonly string _category;
        private readonly ConcurrentQueue<string> _entries;
        private readonly ConcurrentQueue<(LogLevel Level, string Text)> _records;
        private readonly ConcurrentQueue<(string Category, LogLevel Level, string Text)> _categorized;

        public Logger(
            string category,
            ConcurrentQueue<string> entries,
            ConcurrentQueue<(LogLevel Level, string Text)> records,
            ConcurrentQueue<(string Category, LogLevel Level, string Text)> categorized)
        {
            _category = category;
            _entries = entries;
            _records = records;
            _categorized = categorized;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var text = formatter(state, exception) + (exception is null ? string.Empty : Environment.NewLine + exception);
            _records.Enqueue((logLevel, text));
            _categorized.Enqueue((_category, logLevel, text));
            _entries.Enqueue(text);
        }
    }
}
