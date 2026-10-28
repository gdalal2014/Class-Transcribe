namespace ClassScribe.Audio;

/// <summary>
/// Where a recording session's audio comes from: what can be recorded, which of it to record at each look, how to open a
/// capture of it, and the words for it in the session's messages. A session looks every <see cref="CheckInterval"/>, and
/// at once when the source says something has <see cref="Changed"/>. The session owns its source and disposes it.
/// </summary>
public interface ICaptureSource : IDisposable
{
    /// <summary>
    /// Raised when something that may change what is recorded has changed, such as a device plugged in or a new default
    /// device. It can also be raised when nothing changed for the recording, so listeners look again rather than act on it.
    /// </summary>
    event EventHandler? Changed;

    /// <summary>The words for what this source records, for the session's messages.</summary>
    SourceWording Wording { get; }

    /// <summary>How often the session looks by itself at what can be recorded: once a second for what the computer plays.</summary>
    TimeSpan CheckInterval { get; }

    /// <summary>What the recording waits for while nothing can be recorded, in words for the log, such as "an output device".</summary>
    string WaitingFor { get; }

    /// <summary>
    /// How loud Windows' meter showed, at the last look (<see cref="Choose"/>), what that look picked, from 0 (silence) to 1;
    /// null before the first look, and when the last look failed or found nothing to record.
    /// </summary>
    float? RecordedLevel { get; }

    /// <summary>
    /// One look: picks what to record now, or returns null when there is nothing to record. <paramref name="recordedId"/> is
    /// what is recorded now, null when nothing is, and <paramref name="sinceRecordedSound"/> how long ago it last carried
    /// sound, null if it never has. A look that fails throws.
    /// </summary>
    CaptureTarget? Choose(string? recordedId, TimeSpan? sinceRecordedSound);

    /// <summary>Opens a capture of <paramref name="target"/>, not started yet.</summary>
    IAudioCapture Open(CaptureTarget target);

    /// <summary>
    /// Says why the recording moves from what it recorded, named <paramref name="from"/>, to <paramref name="chosen"/>, in
    /// a line for the log, such as: Sound is playing on "Headphones"; recording it instead of "Speakers".
    /// </summary>
    string DescribeMove(CaptureTarget chosen, string from);
}
