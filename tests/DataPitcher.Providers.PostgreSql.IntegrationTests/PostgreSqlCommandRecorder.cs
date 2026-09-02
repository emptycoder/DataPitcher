using System.Collections.Concurrent;
using DataPitcher.Core.Schema;
using Microsoft.Extensions.Logging;

namespace DataPitcher.Providers.PostgreSql.IntegrationTests;

// A driver database-command execution log, not a store method-call counter. Wired into the
// target NpgsqlDataSource via UseLoggerFactory so every command Npgsql actually sends to
// PostgreSQL is captured, regardless of how many store-interface calls produced it.
public sealed class PostgreSqlCommandRecorder : ILoggerFactory, ILogger
{
    private readonly ConcurrentQueue<string> _messages = [];

    public ILogger CreateLogger(string categoryName) => this;

    public void AddProvider(ILoggerProvider provider) { }

    public void Dispose() { }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        _messages.Enqueue(formatter(state, exception));

    // Npgsql logs each command twice at Debug level (an "Executing command" line and a
    // "Command execution completed" line for the same statement). Counting only the
    // issuance line therefore counts physical commands sent, not log lines emitted.
    public int Count(string tag, TableDefinition? table = null) =>
        _messages.Count(message => message.StartsWith("Executing command", StringComparison.Ordinal) &&
            message.Contains(tag, StringComparison.Ordinal) &&
            (table is null || message.Contains(PostgreSqlIdentifier.Qualified(table.Schema, table.Name), StringComparison.Ordinal)));

    public bool AnyContains(string value) => _messages.Any(message => message.Contains(value, StringComparison.Ordinal));

    public bool AnyContainsLargeInList(int threshold) =>
        _messages.Any(message => ContainsLargeInList(message, threshold));

    private static bool ContainsLargeInList(string message, int threshold)
    {
        var searchStart = 0;
        while (true)
        {
            var index = message.IndexOf(" IN (", searchStart, StringComparison.OrdinalIgnoreCase);
            if (index < 0) return false;
            var close = message.IndexOf(')', index);
            if (close < 0) return false;
            if (message[(index + 5)..close].Split(',').Length > threshold) return true;
            searchStart = close + 1;
        }
    }
}
