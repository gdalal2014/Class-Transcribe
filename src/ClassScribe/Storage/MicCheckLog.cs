using System.Diagnostics;
using System.Globalization;
using System.Text;
using ClassScribe.Audio.Microphones;

namespace ClassScribe.Storage;

/// <summary>
/// The microphone check's log file: mic-check_&lt;local start time&gt;.log in the mic-check folder under the recordings
/// folder, or in the folder given with --log-folder. Its name carries no Z, unlike a recording's time stamp, so that it
/// never passes for a recording and the mic-check folder lists none. Each line reaches the file as it is written, so the
/// file is complete even when the window is closed, and the file is shared for reading, so it can be opened while the
/// check runs. The header and the cost line are MicWatch's, so that a log reads the same whichever wrote it.
/// </summary>
public sealed class MicCheckLog : IDisposable
{
    /// <summary>How the local start time is written into the file's name: sortable, without colons, and without a recording's Z.</summary>
    public const string StampFormat = "yyyy-MM-dd_HH-mm-ss";

    /// <summary>How a line's time is written: local time to the millisecond.</summary>
    private const string TimeFormat = "HH:mm:ss.fff";

    /// <summary>The file, flushed after every line.</summary>
    private readonly StreamWriter _file;

    private bool _disposed;

    /// <summary>A log over an open file.</summary>
    private MicCheckLog(string path, StreamWriter file)
    {
        Path = path;
        _file = file;
    }

    /// <summary>The log file's full path.</summary>
    public string Path { get; }

    /// <summary>How many timestamped lines (changes, marks, notes and problems) have been written.</summary>
    public int TimedLines { get; private set; }

    /// <summary>
    /// Creates a new log file named after <paramref name="started"/>, the local start time, in <paramref name="folder"/>, which
    /// is made when it is not there; a file of that name already there, from a check started within the same second, is never
    /// overwritten: the next name is taken. The file system's exceptions, an <see cref="IOException"/> or an
    /// <see cref="UnauthorizedAccessException"/> among them, go to the caller, which reports them.
    /// </summary>
    public static MicCheckLog Create(string folder, DateTimeOffset started)
    {
        Directory.CreateDirectory(folder);
        var stem = $"{FolderNames.MicCheck}_{started.ToString(StampFormat, CultureInfo.InvariantCulture)}";
        for (var attempt = 1; ; attempt++)
        {
            var path = System.IO.Path.Combine(folder, attempt == 1 ? $"{stem}.log" : $"{stem}_{attempt}.log");
            try
            {
                var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                return new MicCheckLog(path, new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true });
            }
            catch (IOException) when (File.Exists(path) && attempt < 100)
            {
                // Deliberately not rethrown: another check started within the same second took this name, and the next name is
                // tried, so that no log is ever overwritten. Any other failure, and the hundredth of these, goes to the caller.
            }
        }
    }

    /// <summary>A timestamped line as the log shows it: the local time to the millisecond, the kind in a column of its own, and the text.</summary>
    public static string Format(MicCheckLine line) => $"{line.At.ToString(TimeFormat, CultureInfo.InvariantCulture)}  {line.Kind,-8} {line.Text}";

    /// <summary>
    /// The header: what the check is and its build, when it started in local time with the zone, the Windows version and the
    /// check's own process id <paramref name="processId"/>, what it reads and that it opens nothing, that it looks every
    /// <paramref name="interval"/> and writes changes only, what the session states mean, the log's <paramref name="path"/>,
    /// how to stop it (by itself after <paramref name="stopAfter"/>, when given), and a blank line.
    /// </summary>
    public static IReadOnlyList<string> HeaderLines(DateTimeOffset started, int processId, string path, TimeSpan interval, TimeSpan? stopAfter)
    {
        var sign = started.Offset < TimeSpan.Zero ? "-" : "+";
        return
        [
            $"ClassScribe microphone check (build {BuildInfo.Build}): watches what the apps on this computer do with the microphone.",
            $"Started {started.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} local time (UTC{sign}{started.Offset.Duration().ToString(@"hh\:mm", CultureInfo.InvariantCulture)}), " +
            $"Windows {Environment.OSVersion.Version}, ClassScribe's own process id {processId}.",
            "It only reads Windows' lists of microphones and their sessions, and Windows' microphone usage record. It never opens a microphone, records no sound and sends nothing anywhere.",
            $"It checks every {interval.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture)} ms and writes a line only when something changes; the first lines show how things are at the start.",
            "Session states: Active = the app is taking sound from the microphone now; Inactive = open but not taking sound; Expired = closed.",
            $"Log file: {path}",
            stopAfter is { } limit
                ? $"Stops by itself after {Seconds(limit)}; Q or Ctrl+C stops it sooner. M writes a numbered mark."
                : "Press Q or Ctrl+C to stop. M writes a numbered mark.",
            ""
        ];
    }

    /// <summary>
    /// What the run cost the process itself: processor time, peak memory, open handles and garbage collections, so that a long
    /// log shows whether the check stayed light and whether its memory was collected.
    /// </summary>
    public static string CostLine()
    {
        using var self = Process.GetCurrentProcess();
        var cpu = self.TotalProcessorTime.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture);
        var peak = (self.PeakWorkingSet64 / (1024.0 * 1024)).ToString("0.0", CultureInfo.InvariantCulture);
        var managed = (GC.GetTotalMemory(forceFullCollection: false) / (1024.0 * 1024)).ToString("0.0", CultureInfo.InvariantCulture);
        return $"The check's own cost: {cpu} ms of processor time; peak working set {peak} MB; {self.HandleCount} handles open now; " +
            $"managed heap {managed} MB after {GC.CollectionCount(0)} gen 0, {GC.CollectionCount(1)} gen 1 and {GC.CollectionCount(2)} gen 2 garbage collections.";
    }

    /// <summary>A length in whole seconds, in words: "1 second", "60 seconds".</summary>
    public static string Seconds(TimeSpan length)
    {
        var seconds = (long)Math.Round(length.TotalSeconds);
        return seconds == 1 ? "1 second" : string.Create(CultureInfo.InvariantCulture, $"{seconds} seconds");
    }

    /// <summary>Writes a timestamped line (see <see cref="Format"/>), counts it, and returns the text written, for the window to show.</summary>
    public string Write(MicCheckLine line)
    {
        var text = Format(line);
        _file.WriteLine(text);
        TimedLines++;
        return text;
    }

    /// <summary>Writes a line as it is, such as a line of the header or the footer.</summary>
    public void Plain(string text) => _file.WriteLine(text);

    /// <summary>Closes the file.</summary>
    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        _file.Dispose();
    }
}
