using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Tests;

/// <summary>
/// A logger factory that keeps every message logged through it, by category, so that a test can inspect what the
/// classes a factory builds have said. The track set's factory makes a session, a chooser, a gate, a watch and the track
/// set itself, each with a logger of its own, and takes the factory rather than one logger per class.
/// </summary>
public sealed class TestLoggerFactory : ILoggerFactory
{
    private readonly ConcurrentQueue<(string Category, LogLevel Level, string Message)> _entries = new();

    /// <summary>Every message logged, whatever its category or level, in order.</summary>
    public IReadOnlyList<string> Messages => [.. _entries.Select(entry => entry.Message)];

    /// <summary>The messages logged as warnings or errors, in order.</summary>
    public IReadOnlyList<string> Problems => [.. _entries.Where(entry => entry.Level >= LogLevel.Warning).Select(entry => entry.Message)];

    /// <summary>The messages logged by <typeparamref name="T"/>, in order.</summary>
    public IReadOnlyList<string> MessagesOf<T>() => [.. _entries.Where(entry => entry.Category == typeof(T).FullName).Select(entry => entry.Message)];

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    /// <summary>Nothing is added: the factory keeps its own entries.</summary>
    public void AddProvider(ILoggerProvider provider)
    {
    }

    /// <summary>Nothing to let go of.</summary>
    public void Dispose()
    {
    }

    /// <summary>A logger of one category, which hands every message to the factory.</summary>
    private sealed class Logger(TestLoggerFactory factory, string category) : ILogger
    {
        /// <inheritdoc />
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        /// <inheritdoc />
        public bool IsEnabled(LogLevel logLevel) => true;

        /// <inheritdoc />
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            factory._entries.Enqueue((category, logLevel, formatter(state, exception)));
    }
}
