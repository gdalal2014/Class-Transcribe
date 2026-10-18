using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Tests;

/// <summary>
/// A logger factory that keeps every message logged through it, by category, so that a test can inspect what the
/// classes a factory builds have said. The track set's factory makes a session, a chooser, a gate, a watch and the track
/// set itself, each with a logger of its own, and takes the factory rather than one logger per class. A provider added
/// gets every message as well, as the recorder's log does in the app, so that a test can follow the lines into a file.
/// </summary>
public sealed class TestLoggerFactory : ILoggerFactory
{
    private readonly ConcurrentQueue<(string Category, LogLevel Level, string Message)> _entries = new();
    private readonly ConcurrentQueue<ILoggerProvider> _providers = new();

    /// <summary>Every message logged, whatever its category or level, in order.</summary>
    public IReadOnlyList<string> Messages => [.. _entries.Select(entry => entry.Message)];

    /// <summary>The messages logged as warnings or errors, in order.</summary>
    public IReadOnlyList<string> Problems => [.. _entries.Where(entry => entry.Level >= LogLevel.Warning).Select(entry => entry.Message)];

    /// <summary>The messages logged by <typeparamref name="T"/>, in order.</summary>
    public IReadOnlyList<string> MessagesOf<T>() => [.. _entries.Where(entry => entry.Category == typeof(T).FullName).Select(entry => entry.Message)];

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    /// <summary>Hands every message from now on to <paramref name="provider"/> as well, besides keeping it.</summary>
    public void AddProvider(ILoggerProvider provider) => _providers.Enqueue(provider);

    /// <summary>Nothing to let go of: the providers added belong to whoever made them.</summary>
    public void Dispose()
    {
    }

    /// <summary>A logger of one category, which hands every message to the factory and to the providers added.</summary>
    private sealed class Logger(TestLoggerFactory factory, string category) : ILogger
    {
        /// <inheritdoc />
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        /// <inheritdoc />
        public bool IsEnabled(LogLevel logLevel) => true;

        /// <inheritdoc />
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            factory._entries.Enqueue((category, logLevel, formatter(state, exception)));
            foreach (var provider in factory._providers) provider.CreateLogger(category).Log(logLevel, eventId, state, exception, formatter);
        }
    }
}
