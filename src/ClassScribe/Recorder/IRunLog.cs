using Microsoft.Extensions.Logging;

namespace ClassScribe.Recorder;

/// <summary>
/// The log of the process that records (architecture 4.3): what every logger writes, through the logging provider that
/// fills it, as the use cases reach it. The lines go to a file: the recording's own log beside its audio, once the run has
/// named it (<see cref="WriteFileTo"/>), with the lines written before it; until then, in a recorder, the start-up file
/// (<see cref="StartupFileName"/> in ClassScribe's own folder), where a failure to start is found. Writing a line never
/// waits, not for the disk and not for a window: the files are written in the background, and a line the file cannot take
/// is counted (<see cref="LostFileLines"/>), for the run to report at its end; code that must know every line so far has
/// reached its file calls <see cref="Flush"/>. The recorder runs one recording, so its recording's log gets every line since
/// the process started; a window logs other work before and between the recordings it runs itself, so it marks where each
/// run begins (<see cref="KeepFromNow"/>). The log notes its own failures in itself, once each: the first file that refuses
/// lines, which <see cref="FirstFileFailure"/> also keeps.
/// </summary>
public interface IRunLog
{
    /// <summary>The start-up file's name. The recorder keeps it in ClassScribe's own folder.</summary>
    const string StartupFileName = "recorder.log";

    /// <summary>
    /// The recording's log the lines go to now, as <see cref="WriteFileTo"/> named it; null when none is named. An upload of
    /// the recording reads it to know that its log is still being written by this process.
    /// </summary>
    string? NamedFile { get; }

    /// <summary>
    /// How many lines did not reach the file they were for: lines a full queue could not take, lines the disk refused, and
    /// lines written after the log was disposed. They are still in memory for the windows, while they are among the last
    /// lines the log holds.
    /// </summary>
    int LostFileLines { get; }

    /// <summary>
    /// Why a file first refused lines: the file and Windows' message, as in "Could not write the log file &lt;path&gt;:
    /// There is not enough space on the disk."; null while no file has refused any. The log notes that failure itself, as
    /// an error line, once. The run names this with <see cref="LostFileLines"/> at its end as well, because a window that
    /// records shows what the run logs, but not the log's own lines.
    /// </summary>
    string? FirstFileFailure { get; }

    /// <summary>
    /// Adds a line: numbered, stamped with the time, held in memory for the windows, queued for the file it goes to, and
    /// handed on to whoever follows the log. Never waits for the disk or for a follower, so the capture threads can log too.
    /// </summary>
    /// <param name="level">How much the line matters.</param>
    /// <param name="text">The line as the console would show it, without a line break at its end.</param>
    void Write(LogLevel level, string text);

    /// <summary>
    /// Marks where the next recording's log begins: none of the lines written before this call is carried over into it.
    /// A window calls this when a run in it begins, so that what the window did before, such as the transcription of
    /// another class an hour earlier or the menu between two runs, stays out of the recording's log, while the run's own
    /// first lines, written before its folder was known, are carried over. The recorder does not call it: it runs one
    /// recording, and every line since its process started belongs to that recording. While a recording's log is named,
    /// its lines go there whatever this says, and <see cref="EndFile"/> marks the next start itself.
    /// </summary>
    void KeepFromNow();

    /// <summary>
    /// Names the recording's log, &lt;base&gt;.log beside the recording: from now on the lines go there, added at its end, so
    /// that a run continuing the recording adds to what the earlier run wrote. The lines written before, from the latest of
    /// the start, the end of the last recording's log and the mark of <see cref="KeepFromNow"/>, go there first, as far as
    /// they are still held; the start-up file has those already. Naming another file while one is named moves the lines
    /// after this to the new one.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The log is disposed.</exception>
    void WriteFileTo(string path);

    /// <summary>
    /// Ends the recording's log, as a run does when it is over, and lets go of the file: the lines after this go to the
    /// start-up file, when there is one, and are kept for the next recording's log, unless <see cref="KeepFromNow"/> marks
    /// a later start. Nothing happens when no recording's log is named, or the log is disposed.
    /// </summary>
    void EndFile();

    /// <summary>
    /// Waits until every line written before this call has reached its file or been counted as lost, for at most
    /// <paramref name="patience"/>. Returns false when that time ran out first, as it does while the disk is stalled.
    /// </summary>
    bool Flush(TimeSpan patience);

    /// <summary>
    /// The last <paramref name="count"/> lines that are not empty of the start-up log at <paramref name="path"/>, oldest
    /// first, when the file was last written at <paramref name="writtenSince"/> or after, as a window reads the last lines of
    /// a recorder that ended before it answered; empty when the file was written before that, there is no such file, or no
    /// lines are asked for (<paramref name="count"/> 0 or less). The file is read while the recorder may still write it. A
    /// file that cannot be read throws its <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/>.
    /// </summary>
    IReadOnlyList<string> LastStartupLines(string path, DateTimeOffset writtenSince, int count);
}
