using System.IO;
using ClassScribe.Runs;
using ClassScribe.Storage;

namespace ClassScribe.Recorder;

/// <summary>
/// A run file as a window finds it: the run as its file last says, and whether the process that records it still runs.
/// Together they say what a start of ClassScribe does about the run: attach to a recording now, mention a transcription
/// now, or report an unexpected end, an interrupted transcription or an outcome nobody has seen. A run that has been dealt
/// with (continued, transcribed, forgotten, or its outcome shown) is marked seen (see <c>IRunJournal.MarkSeen</c>) and
/// is reported no more.
/// </summary>
/// <param name="Record">The run as its file last says.</param>
/// <param name="IsAlive">True while the process the file names runs, the very one that started at the time written down.</param>
public sealed record RunView(RunRecord Record, bool IsAlive)
{
    /// <summary>
    /// The run records now: its process is starting to record, recording or stopping. The menu, and record with the same
    /// name, show it; another recording waits for it.
    /// </summary>
    public bool IsRecordingNow => IsAlive && Record.State is RunState.Starting or RunState.Recording or RunState.Stopping;

    /// <summary>The run's process transcribes its recording now, in the background; the recording itself is complete.</summary>
    public bool IsTranscribingNow => IsAlive && Record.State == RunState.Transcribing;

    /// <summary>
    /// The recording ended unexpectedly and has not been dealt with: its file still says recording or stopping, and its
    /// process is gone. Its audio is kept, to continue or to transcribe.
    /// </summary>
    public bool EndedUnexpectedly => !IsAlive && !Record.Seen && Record.State is RunState.Recording or RunState.Stopping;

    /// <summary>
    /// The transcription was interrupted and has not been dealt with: the file still says transcribing, and the process is
    /// gone. The recording is complete, to transcribe again.
    /// </summary>
    public bool TranscriptionInterrupted => !IsAlive && !Record.Seen && Record.State == RunState.Transcribing;

    /// <summary>
    /// The run is over with an outcome nobody has seen: finished, or a recorder that failed. A start refused because
    /// another recording was running has nothing to report: the window that started it dealt with it at once.
    /// </summary>
    public bool HasUnseenOutcome => !Record.Seen && Record.Outcome is not null && Record.State is RunState.Finished or RunState.Failed;

    /// <summary>A start of ClassScribe reports the run: it ended unexpectedly, its transcription was interrupted, or its outcome is unseen.</summary>
    public bool NeedsReport => EndedUnexpectedly || TranscriptionInterrupted || HasUnseenOutcome;

    /// <summary>The run is a YouTube video's, one of a batch's (see <see cref="VideoRequest"/>): never a class, and never continued as one.</summary>
    public bool IsVideo => Record.Request.Video is not null;

    /// <summary>
    /// True when <paramref name="run"/> records, or recorded, <paramref name="files"/>: its run file names the same
    /// recording, by its folder and its base name, in any letter case, as Windows takes such names for one.
    /// </summary>
    public static bool IsRunOf(RunRecord run, RecordingFiles files) =>
        run is { Folder: { } folder, BaseName: { } baseName }
        && string.Equals(baseName, files.BaseName, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Path.TrimEndingDirectorySeparator(folder), Path.TrimEndingDirectorySeparator(files.Folder), StringComparison.OrdinalIgnoreCase);
}
