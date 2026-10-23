using ClassScribe.Audio;
using ClassScribe.Camera;
using ClassScribe.Desktop;
using ClassScribe.Drive;
using ClassScribe.Settings;
using ClassScribe.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClassScribe.Commands;

/// <summary>What finish-video, menu choice 3's V and the report's V are asked: a lesson's folder or one of its files.</summary>
/// <param name="Path">The lesson: its folder (its newest lesson with the camera is meant) or one of its files, as a full path.</param>
/// <param name="Open">False to open nothing at the end (--no-open).</param>
public sealed record FinishVideoRequest(string Path, bool Open = true);

/// <summary>
/// finish-video, menu choice 3's V and the report's V (teach-video 6.9): a lesson's video made again, for free, from its
/// camera pieces and its sound, in this window, through a camera process's finish job (see <see cref="LessonVideoMaker"/>),
/// whose lines it shows. Pieces still there are used, and positions before the first of them take an older video's
/// picture; the older video is replaced only once the new one has passed its check. It is refused, with exit 1, for a path
/// that names no lesson, one being recorded or having its video made now (a live run's is refused by the runner), one
/// without camera pieces, and one whose sound is on Google Drive only (after free-space). The camera's pieces are kept for
/// Drive only when the video goes there (see <see cref="LessonVideoDrive"/>). The laptop is kept awake meanwhile; Ctrl+C
/// cancels, exit 130. Once made, the folder opens as Recording:OpenFolderWhenDone says, and what is new goes up to Drive.
/// No folders.lock is taken and no move journal is read: neither exists in this build (build-plan amendment 7 waits for
/// step 4, which adds both).
/// </summary>
public sealed class FinishVideoCommand(
    ClassFolders folders,
    LessonVideoMaker maker,
    LessonVideoDrive lessonDrive,
    IDriveAutoUpload drive,
    IUserSignals signals,
    IPowerRequests power,
    IPowerSource powerSource,
    IDesktop desktop,
    IOptionsSnapshot<RecordingOptions> recording,
    ILogger<FinishVideoCommand> logger)
{
    /// <summary>The words when the lesson's sound is on Google Drive alone, so its video cannot be made again on this laptop.</summary>
    public const string SoundOnDriveOnly = "The sound of this lesson is on Google Drive only, so its video cannot be made again here.";

    /// <summary>Makes the video of the lesson <paramref name="request"/> names, and returns the exit code: 0 made, 1 refused or not made, 130 cancelled.</summary>
    public async Task<int> RunAsync(FinishVideoRequest request)
    {
        if (LessonAt(request.Path) is not { } files)
        {
            logger.LogError("{Path} names no lesson with the camera's pieces to make a video from", request.Path);
            return 1;
        }

        if (Refusal(files) is { } refused)
        {
            logger.LogError("{Refused}", refused);
            return 1;
        }

        logger.LogInformation("Making the lesson's video of {Recording}, free: nothing is uploaded or billed. Ctrl+C cancels it; its pieces are kept either way", files.BaseName);
        LessonVideoResult result;
        using (power.Hold(StayAwake.LessonVideoReason, () => StayAwake.Needs(recording.Value.KeepScreenOn, powerSource)))
            result = await maker.MakeAsync(files, lessonDrive.KeepsPieces, signals.Cancellation);

        switch (result.Outcome)
        {
            case LessonVideoOutcome.Made:
                if (request.Open && recording.Value.OpenFolderWhenDone) desktop.OpenFolder(files.Folder);
                await drive.UploadAsync(files);
                return 0;
            case LessonVideoOutcome.Cancelled:
                return 130;
            default:
                return 1;
        }
    }

    /// <summary>
    /// The lesson <paramref name="path"/> names: one of its files, or in a folder (with its date folders) the newest recording
    /// that has camera pieces or a lesson's video; null when it names none.
    /// </summary>
    private static RecordingFiles? LessonAt(string path)
    {
        if (File.Exists(path)) return RecordingFiles.TryParse(path);
        if (!Directory.Exists(path)) return null;

        return ClassFolders.RecordingsByAnyFileIn(path)
            .FirstOrDefault(files => files.FindCameraPieces().Count > 0 || File.Exists(files.LessonVideo));
    }

    /// <summary>Why the video of <paramref name="files"/> cannot be made now, in words; null when it can.</summary>
    private string? Refusal(RecordingFiles files)
    {
        if (ClassFolders.IsLessonVideoBeingMade(files)) return $"The video of {files.BaseName} is being made now; it appears in {files.Folder} once it has passed its check.";
        if (ClassFolders.IsBeingWritten(files)) return $"{files.BaseName} is being recorded now; make its video once it has stopped.";
        if (files.FindCameraPieces().Count == 0)
        {
            return File.Exists(files.LessonVideo)
                ? $"{files.BaseName} has its video, {Path.GetFileName(files.LessonVideo)}, and no camera pieces left to make it again from."
                : $"{files.BaseName} has no camera pieces, so it has no picture to make a video of.";
        }

        if (files.FindSegments().Count > 0 || files.FindSegments(Track.Microphone).Count > 0) return null;

        return folders.AudioOnDriveOf(files) is not null ? SoundOnDriveOnly : $"The sound of {files.BaseName} is not on this laptop, so its video cannot be made here.";
    }
}
