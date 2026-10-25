using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace TenantHub.IntegrationTests.Infrastructure;

/// <summary>
/// Everything the API logged, for assertions about what must (or must never) appear. Scopes are recorded as entries
/// of their own (category <c>scope</c>): they carry the request path, which JSON logs include in production.
/// </summary>
public sealed class CapturedLogs : ILoggerProvider
{
    private readonly ConcurrentQueue<(string Category, LogLevel Level, string Message)> _entries = new();

    public IReadOnlyList<(string Category, LogLevel Level, string Message)> Entries => [.. _entries];

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _entries);

    public void Dispose()
    {
    }

    private sealed class Logger(string category, ConcurrentQueue<(string, LogLevel, string)> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            entries.Enqueue(("scope", LogLevel.None, state.ToString() ?? ""));
            return null;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            entries.Enqueue((category, logLevel, formatter(state, exception) + (exception is null ? "" : " " + exception)));
    }
}
