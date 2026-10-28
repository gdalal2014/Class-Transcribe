using ClassScribe.Audio.Microphones;

namespace ClassScribe.Storage;

/// <summary>
/// The microphone check's open log file: mic-check_&lt;local start time&gt;.log, opened by <see cref="MicCheckLog"/>. Each
/// line reaches the file as it is written, so the file is complete even when the window is closed, and the file is shared
/// for reading, so it can be opened while the check runs.
/// </summary>
public sealed class MicCheckLogFile : IMicCheckLogFile
{
    /// <summary>The file, flushed after every line.</summary>
    private readonly StreamWriter _file;

    private bool _disposed;

    /// <summary>A log over an open file.</summary>
    internal MicCheckLogFile(string path, StreamWriter file)
    {
        Path = path;
        _file = file;
    }

    /// <inheritdoc />
    public string Path { get; }

    /// <inheritdoc />
    public int TimedLines { get; private set; }

    /// <summary>Writes a timestamped line (see <see cref="MicCheckLog.Format"/>), counts it, and returns the text written, for the window to show.</summary>
    public string Write(MicCheckLine line)
    {
        var text = MicCheckLog.Format(line);
        _file.WriteLine(text);
        TimedLines++;
        return text;
    }

    /// <inheritdoc />
    public void Plain(string text) => _file.WriteLine(text);

    /// <summary>Closes the file.</summary>
    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        _file.Dispose();
    }
}
