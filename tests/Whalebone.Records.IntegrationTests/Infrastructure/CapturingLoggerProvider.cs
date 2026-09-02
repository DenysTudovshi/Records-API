using System.Collections.Concurrent;

using Microsoft.Extensions.Logging;

namespace Whalebone.Records.IntegrationTests.Infrastructure;

/// <summary>
/// One captured log entry, kept in every form a sink could write it out in.
/// </summary>
/// <remarks>
/// Storing only the rendered message would make an assertion about what is absent from the logs
/// close to worthless: plenty of entries carry a value only in a state slot or in an enclosing
/// scope, and a structured sink writes all of them. The request path, for one, exists nowhere but
/// the hosting scope.
/// </remarks>
public sealed record CapturedLog(
    string Category,
    LogLevel Level,
    string Message,
    IReadOnlyList<string> StateValues,
    IReadOnlyList<string> ScopeValues,
    string? Exception)
{
    /// <summary>Every piece of text this entry could put in front of a reader.</summary>
    public IEnumerable<string> AllText()
    {
        yield return Category;
        yield return Message;

        foreach (var value in StateValues)
        {
            yield return value;
        }

        foreach (var value in ScopeValues)
        {
            yield return value;
        }

        if (Exception is not null)
        {
            yield return Exception;
        }
    }
}

/// <summary>Records every log line the application writes, so a test can assert on their contents.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private readonly ConcurrentQueue<CapturedLog> _entries = new();

    private IExternalScopeProvider? _scopes;

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);

    public IReadOnlyList<CapturedLog> Snapshot() => _entries.ToArray();

    public void Clear() => _entries.Clear();

    public void Dispose()
    {
        // Nothing owned.
    }

    private sealed class CapturingLogger(CapturingLoggerProvider provider, string category) : ILogger
    {
        /// <summary>Null on purpose: scopes arrive through <see cref="ISupportExternalScope"/>.</summary>
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        /// <summary>Always on. What this provider records is decided by its filter, not by itself.</summary>
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            var scopeValues = new List<string>();
            provider._scopes?.ForEachScope(
                (scope, values) => values.AddRange(Flatten(scope)),
                scopeValues);

            provider._entries.Enqueue(new CapturedLog(
                category,
                logLevel,
                formatter(state, exception),
                Flatten(state).ToArray(),
                scopeValues,
                exception?.ToString()));
        }

        /// <summary>
        /// Reference-type nullability is erased at runtime, so this one pattern matches both the
        /// framework's FormattedLogValues and the KeyValuePair&lt;string, object&gt; shape that
        /// LoggerMessage.DefineScope produces.
        /// </summary>
        private static IEnumerable<string> Flatten(object? state) => state switch
        {
            IEnumerable<KeyValuePair<string, object?>> pairs =>
                pairs.Select(pair => pair.Value?.ToString() ?? string.Empty),
            _ => [state?.ToString() ?? string.Empty],
        };
    }
}
