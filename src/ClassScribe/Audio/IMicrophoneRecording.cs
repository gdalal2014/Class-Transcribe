using ClassScribe.Audio.Microphones;

namespace ClassScribe.Audio;

/// <summary>
/// What a recording with your microphone says about it beyond its snapshot, for the run's lines, its status line, its
/// title and its end-of-class summary: which microphone is recorded now and why, how Windows processes its audio, which
/// app the track follows, and the gate that follows it. A recording that has your microphone implements this beside
/// <see cref="IRecording"/>; a recording of the class alone does not, and the run then asks nothing of it. Each member
/// answers for now, from whatever thread asks.
/// </summary>
public interface IMicrophoneRecording
{
    /// <summary>The app the microphone track follows, as the recording was built to follow it; null when none is followed, as in person.</summary>
    FollowRule? Follows { get; }

    /// <summary>
    /// The gate that follows the app, whose state, words, evidence and summary the run shows; null when the recording has
    /// none. In person, and with the class when no app is followed, a gate for <see cref="FollowRule.None"/> follows
    /// nothing but still counts the time on and off with V, which the end-of-class summary gives; without one that line
    /// is left out.
    /// </summary>
    MicrophoneGate? Gate { get; }

    /// <summary>The microphone recorded now, as Windows lists it; null while there is none to record.</summary>
    Microphone? Microphone { get; }

    /// <summary>
    /// The microphone the recording would record, as its last look picked it, while none is recorded: the track switched
    /// off with V, or not yet opened. The same as <see cref="Microphone"/> while one is recorded. Null when no look has
    /// picked one, as before the first look and while none is connected.
    /// </summary>
    Microphone? WouldRecord { get; }

    /// <summary>Why that microphone, in the words the choice was made with, such as Windows' default microphone; null when there is nothing to say.</summary>
    string? MicrophoneWhy { get; }

    /// <summary>How Windows processes the microphone's audio, as the open capture reports it; null while no capture is open, or when the capture cannot say.</summary>
    MicrophoneProcessing? Processing { get; }

    /// <summary>
    /// Whether the microphone recorded now is muted in Windows (the switch in Sound settings, and the microphone key), read
    /// from Windows when asked; null while none is recorded, or when Windows cannot say.
    /// </summary>
    bool? MutedInWindows { get; }

    /// <summary>
    /// The webcam the microphone belongs to (camera-first 3.4), and whether its picture is held with its frames arriving;
    /// null for a microphone of no webcam, and for a recording that cannot say. The webcam zeros rules take the place of
    /// the warning about only silence for such a microphone. It has no default body, so that every recording with the
    /// microphone must say, and one that stopped saying would not build rather than fall silent in a class.
    /// </summary>
    Camera.WebcamState? Webcam { get; }
}

/// <summary>
/// What a recording with the camera's picture says about it (teach-video 6.13): the camera as the run sees it, for the
/// title, the status line and the checks; and the way to let it go for the rest of the run, as when the disk is nearly
/// full. A recording without a camera does not implement it, or says null.
/// </summary>
public interface ICameraRecording
{
    /// <summary>The lesson's camera now; null when the recording records no picture.</summary>
    Camera.CameraTrackState? Camera { get; }

    /// <summary>Lets the camera go for the rest of the run, saying <paramref name="why"/>; the sound records on.</summary>
    void LetGoOfTheCamera(string why);
}
