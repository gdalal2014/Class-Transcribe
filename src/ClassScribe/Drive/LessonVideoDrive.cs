using Microsoft.Extensions.Options;

namespace ClassScribe.Drive;

/// <summary>
/// What becomes of a lesson's video with Google Drive, as the run's settings have it (teach-video 6.9 and 8, build plan
/// amendments 5 and 47, Gulzar's answers N18 B and N19 C): it goes up only once Drive is set up, with Drive:LessonVideos on
/// (off by default) and Drive:Upload sending audio, since it holds the same sound; only then are the camera's pieces kept
/// until free-space finds the video safe on Drive, and in every other case they are deleted once the video passes its
/// check. At a lesson's start with Drive set up, a line says where its video goes, and how to turn lesson videos on when
/// they are off. One per run.
/// </summary>
/// <param name="store">The Google client and sign-in, which say whether Drive is set up.</param>
/// <param name="options">The run's Drive settings.</param>
public sealed class LessonVideoDrive(IGoogleCredentialStore store, IOptionsSnapshot<DriveOptions> options)
{
    /// <summary>The start line with Drive:LessonVideos on and the audio going up.</summary>
    public const string GoesUpLine =
        "The lesson's video goes to Google Drive after the class (Drive:LessonVideos); its camera pieces stay on this laptop until free-space finds the video safe there.";

    /// <summary>The start line with Drive:LessonVideos off, the default: how to turn it on, with this very program's settings set.</summary>
    public static string StaysHereLine =>
        $"The lesson's video stays on this laptop; to send lesson videos to Google Drive too, run: {Commands.CommandLine.SettingsSet("Drive:LessonVideos", "true")}. Its camera pieces are deleted once the video passes its check.";

    /// <summary>The start line with Drive:LessonVideos on and Drive:Upload WithoutAudio, which keeps the video's sound off Drive too.</summary>
    public const string WithoutAudioLine =
        "The lesson's video stays on this laptop: it holds the lesson's sound, which Drive:Upload WithoutAudio keeps off Google Drive. Its camera pieces are deleted once the video passes its check.";

    /// <summary>True once Drive is set up: a Google client is kept, signed in or not, or the store cannot be read, as the uploads take it.</summary>
    public bool IsSetUp => store.Read() is not { Credentials: null, Problem: null };

    /// <summary>
    /// True when a lesson's video goes to Drive, and so its camera's pieces are kept until free-space finds it there: Drive
    /// set up, Drive:LessonVideos on and Drive:Upload sending audio. Otherwise they go once the video passes its check.
    /// </summary>
    public bool KeepsPieces => options.Value.LessonVideos && options.Value.Upload == DriveUploadScope.Everything && IsSetUp;

    /// <summary>The line a lesson's start says of its video and Drive; null before Drive is set up, when nothing goes there.</summary>
    public string? StartLine()
    {
        if (!IsSetUp) return null;
        if (!options.Value.LessonVideos) return StaysHereLine;

        return options.Value.Upload == DriveUploadScope.Everything ? GoesUpLine : WithoutAudioLine;
    }
}
