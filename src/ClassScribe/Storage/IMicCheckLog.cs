using ClassScribe.Audio.Microphones;

namespace ClassScribe.Storage;

/// <summary>
/// The microphone check's log files (architecture 4.3): opens a new one, named after the check's local start time in the
/// folder given, and gives the two lines of a log that only the machine can give, the header's and the cost line's.
/// </summary>
public interface IMicCheckLog
{
    /// <summary>
    /// Creates a new log file named after <paramref name="started"/>, the local start time, in <paramref name="folder"/>, which
    /// is made when it is not there; a file of that name already there, from a check started within the same second, is never
    /// overwritten: the next name is taken. The file system's exceptions, an <see cref="System.IO.IOException"/> or an
    /// <see cref="UnauthorizedAccessException"/> among them, go to the caller, which reports them.
    /// </summary>
    IMicCheckLogFile Create(string folder, DateTimeOffset started);

    /// <summary>
    /// The header: what the check is and its build, when it started in local time with the zone, the Windows version and the
    /// check's own process id <paramref name="processId"/>, what it reads and that it opens nothing, that it looks every
    /// <paramref name="interval"/> and writes changes only, what the session states mean, the log's <paramref name="path"/>,
    /// how to stop it (by itself after <paramref name="stopAfter"/>, when given), and a blank line.
    /// </summary>
    IReadOnlyList<string> HeaderLines(DateTimeOffset started, int processId, string path, TimeSpan interval, TimeSpan? stopAfter);

    /// <summary>
    /// What the run cost the process itself: processor time, peak memory, open handles and garbage collections, so that a long
    /// log shows whether the check stayed light and whether its memory was collected.
    /// </summary>
    string CostLine();
}

/// <summary>
/// One open log of the microphone check: each line reaches the file as it is written, so the file is complete even when
/// the window is closed, and the file is shared for reading, so it can be opened while the check runs. Disposing it closes
/// the file.
/// </summary>
public interface IMicCheckLogFile : IDisposable
{
    /// <summary>The log file's full path.</summary>
    string Path { get; }

    /// <summary>How many timestamped lines (changes, marks, notes and problems) have been written.</summary>
    int TimedLines { get; }

    /// <summary>Writes a timestamped line, counts it, and returns the text written, for the window to show.</summary>
    string Write(MicCheckLine line);

    /// <summary>Writes a line as it is, such as a line of the header or the footer.</summary>
    void Plain(string text);
}
