using System.Diagnostics;
using System.Globalization;
using System.Text;
using ClassScribe.Audio.Microphones;

namespace ClassScribe.Storage;

/// <summary>
/// The microphone check's log files, the adapter of <see cref="IMicCheckLog"/>: mic-check_&lt;local start time&gt;.log in
/// the mic-check folder under the recordings folder, or in the folder given with --log-folder. Its name carries no Z,
/// unlike a recording's time stamp, so that it never passes for a recording and the mic-check folder lists none. Each line
/// reaches the file as it is written, so the file is complete even when the window is closed, and the file is shared for
/// reading, so it can be opened while the check runs (see <see cref="MicCheckLogFile"/>). The header and the cost line
/// are MicWatch's, so that a log reads the same whichever wrote it.
/// </summary>
public sealed class MicCheckLog : IMicCheckLog
{
    /// <summary>How the local start time is written into the file's name: sortable, without colons, and without a recording's Z.</summary>
    public const string StampFormat = "yyyy-MM-dd_HH-mm-ss";

    /// <summary>How a line's time is written: local time to the millisecond.</summary>
    private const string TimeFormat = "HH:mm:ss.fff";

    /// <inheritdoc />
    public IMicCheckLogFile Create(string folder, DateTimeOffset started)
    {
        Directory.CreateDirectory(folder);
        var stem = $"{FolderNames.MicCheck}_{started.ToString(StampFormat, CultureInfo.InvariantCulture)}";
        for (var attempt = 1; ; attempt++)
        {
            var path = Path.Combine(folder, attempt == 1 ? $"{stem}.log" : $"{stem}_{attempt}.log");
            try
            {
                var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                return new MicCheckLogFile(path, new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true });
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

    /// <inheritdoc />
    public IReadOnlyList<string> HeaderLines(DateTimeOffset started, int processId, string path, TimeSpan interval, TimeSpan? stopAfter)
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
                ? $"Stops by itself after {Durations.Seconds(limit)}; Q or Ctrl+C stops it sooner. M writes a numbered mark."
                : "Press Q or Ctrl+C to stop. M writes a numbered mark.",
            ""
        ];
    }

    /// <inheritdoc />
    public string CostLine()
    {
        using var self = Process.GetCurrentProcess();
        var cpu = self.TotalProcessorTime.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture);
        var peak = (self.PeakWorkingSet64 / (1024.0 * 1024)).ToString("0.0", CultureInfo.InvariantCulture);
        var managed = (GC.GetTotalMemory(forceFullCollection: false) / (1024.0 * 1024)).ToString("0.0", CultureInfo.InvariantCulture);
        return $"The check's own cost: {cpu} ms of processor time; peak working set {peak} MB; {self.HandleCount} handles open now; " +
            $"managed heap {managed} MB after {GC.CollectionCount(0)} gen 0, {GC.CollectionCount(1)} gen 1 and {GC.CollectionCount(2)} gen 2 garbage collections.";
    }
}
