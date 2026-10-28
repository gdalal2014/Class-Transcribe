namespace ClassScribe.Audio;

/// <summary>How a track's packets are placed in its files.</summary>
public enum TrackPlacement
{
    /// <summary>
    /// Appended as they arrive, as ClassScribe has always recorded the class alone: silence while nothing plays is not
    /// written, and a position is the audio written so far.
    /// </summary>
    Contiguous,

    /// <summary>
    /// Where the recording clock puts them, by their stamps, so that the class and the microphone line up in the files
    /// themselves: the time nothing arrives for is filled with silence, and a position is time on the clock.
    /// </summary>
    ClockAligned
}

/// <summary>Which track a session writes, and how its packets are placed.</summary>
/// <param name="Track">The track, which names the files: the main track's .wav files, or the microphone's .mic.wav files.</param>
/// <param name="Placement">Appended as they come, or placed on the recording clock.</param>
/// <param name="NoteGapsLongerThan">
/// For a track placed <see cref="TrackPlacement.Contiguous"/>: a stretch at least this long with nothing arriving, which is
/// left out of the files, is noted through <see cref="RecordingSession.NoteWorthy"/>; zero notes nothing. Not used on
/// the clock, where such a stretch is silence in the files.
/// </param>
public sealed record TrackSpec(Track Track, TrackPlacement Placement, TimeSpan NoteGapsLongerThan = default);
