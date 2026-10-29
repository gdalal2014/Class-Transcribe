using ClassScribe.Recorder;
using ClassScribe.Storage;

namespace ClassScribe.Commands;

/// <summary>
/// A lesson's run, with the camera, as the report at the start, the starter and attach speak of it (teach-video 6.9): its run
/// file says Transcribing from the stop of its sound until its video is made, so a lesson found there with only its video
/// left to make (record-only, or its transcript saved already) was having its video made, not its transcript; and its
/// video's state comes from the files alone.
/// </summary>
public static class LessonRuns
{
    /// <summary>True for a run of a lesson with the camera.</summary>
    public static bool IsLesson(RunRecord run) => run.Request.Source.Camera is not null;

    /// <summary>
    /// True for a lesson's run whose only work left after the stop is its video: record-only, or to be transcribed with its
    /// transcript saved already, as the run waits for the video before it writes its outcome; its files are read through
    /// <paramref name="recordings"/>.
    /// </summary>
    public static bool OnlyItsVideoLeft(RunRecord run, IRecordingFolder recordings) =>
        IsLesson(run) && (!run.Request.Transcribe || FilesOf(run) is { } files && recordings.HasTranscript(files));

    /// <summary>The run's recording, once its files are known; null before.</summary>
    public static RecordingFiles? FilesOf(RunRecord run) =>
        run is { Folder: { Length: > 0 } folder, BaseName: { Length: > 0 } baseName } ? new RecordingFiles(folder, baseName) : null;

    /// <summary>
    /// True when <paramref name="run"/>'s lesson has its video still to make, by its files, and nothing is making it now: pieces
    /// without a video, or a piece newer than the video (see <see cref="IClassFolders.LessonVideoStateOf"/>), read through
    /// <paramref name="folders"/>.
    /// </summary>
    public static bool VideoNotMade(RunRecord run, IClassFolders folders) =>
        IsLesson(run) && FilesOf(run) is { } files && folders.LessonVideoStateOf(files) == LessonVideoState.NotMade && !folders.IsLessonVideoBeingMade(files);

    /// <summary>The command that makes <paramref name="run"/>'s lesson video, free, by one of its files, read through <paramref name="recordings"/>, printed by <paramref name="commandLine"/>.</summary>
    public static string FinishVideoCommand(RunRecord run, IRecordingFolder recordings, CommandLine commandLine) =>
        commandLine.FinishVideo(FilesOf(run) is { } files ? recordings.FindSegments(files).FirstOrDefault() ?? files.Folder : run.Folder ?? run.Request.OutputRoot);

    /// <summary>The words of a lesson whose video's making was cut off (see <see cref="OnlyItsVideoLeft"/>): its sound and pieces are kept, and how to make it, printed by <paramref name="commandLine"/>.</summary>
    public static string VideoNotFinished(RunRecord run, IRecordingFolder recordings, CommandLine commandLine) =>
        $"The lesson video of \"{run.Request.Name}\" was not finished; its sound and its camera pieces are kept. To make it, free, run: {FinishVideoCommand(run, recordings, commandLine)}";
}
