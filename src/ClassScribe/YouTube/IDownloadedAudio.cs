using ClassScribe.Storage;

namespace ClassScribe.YouTube;

/// <summary>
/// A downloaded video's sound, as ClassScribe's own WAV (architecture 4.3; design 3.7): the staged file FFmpeg made turned
/// into the recording's parts, within 2 seconds of the video's length or nothing kept, and the clean-up of what an earlier
/// batch left half done. One per batch.
/// </summary>
public interface IDownloadedAudio
{
    /// <summary>
    /// Writes the audio of <paramref name="staged"/> as the main track of <paramref name="target"/>, the video
    /// <paramref name="title"/>, <paramref name="expected"/> long, and returns its length. The staged file is left where it is.
    /// </summary>
    /// <exception cref="IncompleteAudioException">The audio is more than 2 seconds from the video's length; nothing of it is kept.</exception>
    TimeSpan Convert(string staged, RecordingFiles target, TimeSpan expected, string title, CancellationToken cancellationToken);

    /// <summary>
    /// Cleans up what an earlier batch left half done, as the next batch begins, under its lock: the files of a conversion a
    /// crash cut short, with a line that says so, and the staging folders untouched for more than 7 days. What cannot be
    /// deleted now is said and left for the next batch.
    /// </summary>
    void CleanUp();
}
