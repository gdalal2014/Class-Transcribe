using Microsoft.Extensions.Logging;

namespace ClassScribe.Recorder;

/// <summary>One line of the recorder's log, numbered, so that a window can ask for the lines after the last one it showed.</summary>
/// <param name="Seq">The line's number: 1 for the first line of the log, and one more for each line after it.</param>
/// <param name="Utc">When the line was written.</param>
/// <param name="Level">How much the line matters, as it was logged.</param>
/// <param name="Text">
/// The line as the console would show it, without the line break at its end. A line about a failure goes on over one more
/// line for each exception in its chain, as it does in the console.
/// </param>
public sealed record LogEntry(long Seq, DateTimeOffset Utc, LogLevel Level, string Text);
