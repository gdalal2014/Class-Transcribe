using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClassScribe.Recorder;

/// <summary>
/// The recorder's logger provider. Each line is written in the console's plain format (see
/// <see cref="PlainConsoleFormatter"/>: the time, a label for a warning or an error, the message, and a line for each
/// exception) into the run's <see cref="RecorderLog"/>, and nowhere else: the recorder has no console, and the windows that
/// attach to it show these lines as the console would. Which lines are written follows the Logging settings, as for the
/// console.
/// </summary>
/// <remarks>
/// Register it in the container as an <see cref="ILoggerProvider"/>, with the log as a singleton. The log takes no logger,
/// so nothing that takes one, such as the window link that follows the log, can depend on itself through it.
/// </remarks>
public sealed class RecorderLogProvider(RecorderLog log) : ILoggerProvider
{
    /// <summary>The console's plain format. It keeps nothing from one line to the next, so one serves every logger.</summary>
    private static readonly PlainConsoleFormatter Format = new();

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new Logger(log, categoryName);

    /// <summary>Lets go of nothing: the log belongs to whoever made it, and outlives its loggers.</summary>
    public void Dispose()
    {
    }

    /// <summary>
    /// The text the console's plain format writes for <paramref name="entry"/>, without the line break at its end; null when
    /// it writes nothing, as for an empty message without an exception.
    /// </summary>
    internal static string? TextOf<TState>(in LogEntry<TState> entry)
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        Format.Write(in entry, null, writer);
        var text = writer.ToString();
        if (text.Length == 0) return null;

        return text.EndsWith(writer.NewLine, StringComparison.Ordinal) ? text[..^writer.NewLine.Length] : text;
    }

    /// <summary>The text the console's plain format writes for a message of the log's own, such as its note about a failure.</summary>
    internal static string Text(LogLevel level, string message, Exception? exception = null) =>
        TextOf(new LogEntry<string>(level, typeof(RecorderLog).FullName!, default, message, exception, static (state, _) => state)) ?? message;

    /// <summary>A logger for one category, writing into the log.</summary>
    private sealed class Logger(RecorderLog log, string category) : ILogger
    {
        /// <summary>Starts no scope: the plain format shows none.</summary>
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        /// <summary>Every level but None: the Logging settings have chosen the levels before a line gets here.</summary>
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        /// <summary>Writes the entry's text into the log, unless the plain format writes nothing for it.</summary>
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            if (TextOf(new LogEntry<TState>(logLevel, category, eventId, state, exception, formatter)) is { } text)
                log.Write(logLevel, text);
        }
    }
}
