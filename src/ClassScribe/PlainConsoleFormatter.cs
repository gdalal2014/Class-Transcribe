using System.Globalization;
using ClassScribe.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;

namespace ClassScribe;

/// <summary>
/// Console log format for someone watching the window: the time, the message, and one line per
/// exception in the chain. Set Logging:Console:FormatterName to "simple" for full stack traces.
/// </summary>
public sealed class PlainConsoleFormatter() : ConsoleFormatter(FormatterName)
{
    /// <summary>The name this formatter is registered under: the plain format the settings name by default.</summary>
    public const string FormatterName = DefaultSettings.PlainFormatterName;

    /// <inheritdoc />
    public override void Write<TState>(in LogEntry<TState> logEntry, IExternalScopeProvider? scopeProvider, TextWriter textWriter)
    {
        var message = logEntry.Formatter?.Invoke(logEntry.State, logEntry.Exception);
        if (string.IsNullOrEmpty(message) && logEntry.Exception is null) return;

        var label = logEntry.LogLevel switch
        {
            LogLevel.Warning => "WARNING  ",
            LogLevel.Error or LogLevel.Critical => "ERROR  ",
            _ => ""
        };

        // Invariant, so the clock reads 14:05:00 whatever the regional settings of the machine are.
        var clock = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        textWriter.WriteLine($"{clock}  {label}{message}");
        for (var exception = logEntry.Exception; exception is not null; exception = exception.InnerException)
            textWriter.WriteLine($"          {exception.GetType().Name}: {exception.Message}");
    }
}
