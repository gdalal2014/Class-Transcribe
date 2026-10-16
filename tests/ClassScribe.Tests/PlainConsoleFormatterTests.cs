using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClassScribe.Tests;

/// <summary>The console log format.</summary>
[TestFixture]
public class PlainConsoleFormatterTests
{
    /// <summary>Formats one log entry and returns the text that was written.</summary>
    private static string Format(LogLevel level, string message, Exception? exception = null)
    {
        using var writer = new StringWriter();
        var entry = new LogEntry<string>(level, "Category", new EventId(0), message, exception, (state, _) => state);
        new PlainConsoleFormatter().Write(in entry, null, writer);
        return writer.ToString();
    }

    /// <summary>An information line is the time of day and the message, and nothing else.</summary>
    [Test]
    public void Information_is_the_time_and_the_message()
    {
        var line = Format(LogLevel.Information, "Recording to C:\\Classes");

        Assert.That(line, Does.Match(@"^\d\d:\d\d:\d\d  Recording to C:\\Classes\r?\n$"));
    }

    /// <summary>The clock reads hh:mm:ss with colons whatever the regional settings of the machine are.</summary>
    [Test, SetCulture("fi-FI")]   // a region that writes the time as 14.05.00
    public void The_clock_looks_the_same_under_any_regional_settings()
    {
        var line = Format(LogLevel.Information, "Recording");

        Assert.That(line, Does.Match(@"^\d\d:\d\d:\d\d  Recording"));
    }

    /// <summary>Warnings and errors carry a label that stands out; information has none.</summary>
    [Test]
    public void Warnings_and_errors_are_labelled()
    {
        Assert.That(Format(LogLevel.Warning, "No sound"), Does.Contain("  WARNING  No sound"));
        Assert.That(Format(LogLevel.Error, "Upload failed"), Does.Contain("  ERROR  Upload failed"));
    }

    /// <summary>An exception adds one line per exception in its chain, its type and message, and no stack trace.</summary>
    [Test]
    public void Each_exception_in_the_chain_gets_one_line()
    {
        var failure = new InvalidOperationException("Could not upload.", new IOException("Connection reset."));

        var text = Format(LogLevel.Error, "Upload failed", failure);

        Assert.That(text, Does.Contain("InvalidOperationException: Could not upload."));
        Assert.That(text, Does.Contain("IOException: Connection reset."));
        Assert.That(text.Split('\n', StringSplitOptions.RemoveEmptyEntries), Has.Length.EqualTo(3));
    }
}
