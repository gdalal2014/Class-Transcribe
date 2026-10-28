namespace ClassScribe.Audio;

/// <summary>
/// What a WAV file ClassScribe wrote says of itself, read from its header (architecture 4.3): the length of its audio,
/// which the upload's time limit and the batches' plans need. The reader over the header lives in Infrastructure.
/// </summary>
public interface IAudioFileFacts
{
    /// <summary>
    /// The length of the audio in a WAV file ClassScribe wrote, or null for any other file. Its header tells the file
    /// apart and gives the rate; the audio is read to the end of the file, as the preparer reads it, whatever the sizes
    /// in the header say after a crash.
    /// </summary>
    TimeSpan? AudioLength(string path);
}
