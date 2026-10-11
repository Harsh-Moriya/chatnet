using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace ChatNET.API.Tests.Infrastructure;

// Keeps everything the application logs as text, so a test can check what did and did not get
// written. Used to prove that message content never reaches the logs.
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _entries = new();

    public IReadOnlyList<string> Entries => _entries.ToArray();

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _entries);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger : ILogger
    {
        private readonly string _category;
        private readonly ConcurrentQueue<string> _entries;

        public CapturingLogger(string category, ConcurrentQueue<string> entries)
        {
            _category = category;
            _entries = entries;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        // The formatted message, the structured values and any exception are all captured: a
        // value hidden in a structured property or an exception message is still in the log.
        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var values = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? string.Join(" ", pairs.Select(p => $"{p.Key}={p.Value}"))
                : string.Empty;

            _entries.Enqueue($"[{logLevel}] {_category}: {formatter(state, exception)} {values} {exception}");
        }
    }
}
