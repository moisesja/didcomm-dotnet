using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace MediatorWallet;

/// <summary>One captured log record: category, level, event, the message template and its named values.</summary>
internal sealed record CapturedLog(
    DateTimeOffset Time,
    string Category,
    LogLevel Level,
    int EventId,
    string? EventName,
    string? Template,
    IReadOnlyDictionary<string, string?> Values,
    string Message,
    string? ExceptionType)
{
    public JsonObject ToJson()
    {
        var values = new JsonObject();
        foreach (var (key, value) in Values)
            values[key] = value;
        return new JsonObject
        {
            ["time"] = Time.ToString("O"),
            ["category"] = Category,
            ["level"] = Level.ToString(),
            ["event_id"] = EventId,
            ["event_name"] = EventName,
            ["template"] = Template,
            ["values"] = values,
            ["message"] = Message,
            ["exception"] = ExceptionType,
        };
    }
}

/// <summary>
/// A logger provider that keeps every record (every level) in memory, so a command can assert on
/// the exact record the library writes (for example the Debug record "Coordinate Mediation version
/// negotiated by query {RequestId}: {Version}."), and with <c>--log-out</c> also appends each one
/// as a JSON line (template + named values), written as it happens so a killed process keeps them.
/// </summary>
internal sealed class CaptureLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<CapturedLog> _records = new();
    private readonly StreamWriter? _out;
    private readonly object _gate = new();

    public CaptureLoggerProvider(string? path)
    {
        if (path is not null)
            _out = new StreamWriter(path, append: true) { AutoFlush = true };
    }

    public IReadOnlyList<CapturedLog> Records => _records.ToArray();

    public ILogger CreateLogger(string categoryName) => new CaptureLogger(this, categoryName);

    public void Dispose()
    {
        lock (_gate)
            _out?.Dispose();
    }

    private void Add(CapturedLog record)
    {
        _records.Enqueue(record);
        if (_out is null)
            return;
        var line = record.ToJson().ToJsonString();
        lock (_gate)
            _out.WriteLine(line);
    }

    private sealed class CaptureLogger(CaptureLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            string? template = null;
            var values = new Dictionary<string, string?>(StringComparer.Ordinal);
            if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
            {
                foreach (var (key, value) in pairs)
                {
                    if (key == "{OriginalFormat}")
                        template = value?.ToString();
                    else
                        values[key] = value?.ToString();
                }
            }

            owner.Add(new CapturedLog(DateTimeOffset.UtcNow, category, logLevel, eventId.Id, eventId.Name, template, values,
                formatter(state, exception), exception?.GetType().FullName));
        }
    }
}
