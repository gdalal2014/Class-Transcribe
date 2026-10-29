using ClassScribe.Audio;

namespace ClassScribe.Camera;

/// <summary>
/// The media work of a lesson's video, the seam between the finish job and Media Foundation (see
/// <see cref="Windows.MediaFoundationLessonVideo"/>), so that the job's rules are tested with a fake: what a source holds,
/// writing the video, and checking it. It opens no device.
/// </summary>
public interface ILessonVideoMedia
{
    /// <summary>
    /// What the piece or older video at <paramref name="path"/> holds: its picture's size, its H.264 sequence header and its
    /// length. Throws an <see cref="InvalidDataException"/> for a file that holds no H.264 picture that can be read, and
    /// whatever Windows throws for one it cannot open.
    /// </summary>
    VideoSourceFacts Describe(string path);

    /// <summary>
    /// Writes the lesson's video as <paramref name="plan"/> says into <paramref name="path"/>, which must not be there yet:
    /// a plain MP4, not fragmented, its index (moov) after the data, where Windows' MP4 sink writes it though asked for it
    /// first; the picture passed through or encoded again, every frame lasting until the next and the last until the plan's
    /// end, and <paramref name="sound"/> read to its end, encoded AAC-LC mono at
    /// 128 kbit/s. The file is held open for writing, others allowed to read it, until this returns, so that the recording
    /// counts as being written meanwhile. How far it has got, from 0 to 1, goes to <paramref name="progress"/>. Throws when
    /// it cannot be written, and an <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/> is
    /// cancelled; either leaves the file for the caller to delete.
    /// </summary>
    void Write(LessonVideoPlan plan, MixedSound sound, string path, Action<double> progress, CancellationToken cancellationToken);

    /// <summary>
    /// Checks the video at <paramref name="path"/> as <paramref name="check"/> asks: it opens, it has a picture and a sound,
    /// its length matches, and a frame decodes at each place asked. Null when it passes; otherwise why not, in a few words.
    /// </summary>
    string? Check(string path, LessonVideoCheck check);
}
