using ClassScribe.Audio.Microphones;

namespace ClassScribe.Commands;

/// <summary>Where a run is, as the window's title, its taskbar button and the view of the run show it.</summary>
public enum RecorderPhase
{
    /// <summary>Starting: taking the lock, making the files, opening the capture.</summary>
    Starting,

    /// <summary>Recording.</summary>
    Recording,

    /// <summary>Paused with P: nothing is recorded until P is pressed again.</summary>
    Paused,

    /// <summary>Nothing can be recorded now: waiting for the device or app, or the capture keeps failing.</summary>
    NotRecording,

    /// <summary>Stopping: closing the capture and finishing the files.</summary>
    Stopping,

    /// <summary>Making the smaller copy that is uploaded.</summary>
    Preparing,

    /// <summary>Uploading to ElevenLabs (see <see cref="RecorderState.UploadPercent"/>).</summary>
    Uploading,

    /// <summary>Uploaded; waiting for ElevenLabs' transcript.</summary>
    Transcribing,

    /// <summary>Done: the recording, or its transcript, is saved.</summary>
    Done,

    /// <summary>Ended with a failure; the audio recorded so far is kept.</summary>
    Failed,

    /// <summary>The transcription was cancelled with Ctrl+C; the audio is kept.</summary>
    Cancelled
}

/// <summary>How much a run needs looking at.</summary>
public enum AttentionLevel
{
    /// <summary>Nothing needs attention.</summary>
    None,

    /// <summary>Worth a look, such as no sound for a while: the title says so and the taskbar button turns yellow.</summary>
    Notice,

    /// <summary>
    /// Needs attention now, such as nothing being recorded: the title starts with "!", the taskbar button turns red, the
    /// window flashes, and with no window showing the run, the recorder reopens one.
    /// </summary>
    Alert
}

/// <summary>What needs attention in a run now.</summary>
/// <param name="Level">How much it needs looking at.</param>
/// <param name="Short">A few words for the title, such as NOT RECORDING (waiting for "chrome"); empty when nothing needs attention.</param>
/// <param name="Text">The whole sentence, as the log says it; empty when nothing needs attention.</param>
/// <param name="Count">
/// How many times something has needed attention in this run. It grows with every new event, not with each state that
/// repeats one; a window flashes once for each new Alert, and never for a Notice.
/// </param>
public sealed record Attention(AttentionLevel Level, string Short, string Text, int Count)
{
    /// <summary>Nothing needs attention, and nothing has yet.</summary>
    public static Attention None { get; } = new(AttentionLevel.None, "", "", 0);
}

/// <summary>
/// Your microphone in a run. The last three fields came with the gate that follows the class app; a state from a build
/// without them reads with their defaults, and a build without them passes them over.
/// </summary>
/// <param name="Name">The microphone's name, as Windows shows it.</param>
/// <param name="On">False while it is switched off with V.</param>
/// <param name="Capturing">True while its capture is open and hands over audio.</param>
/// <param name="Problem">What is wrong with it, in words, such as that it is muted in Windows; null when nothing is.</param>
/// <param name="Follows">The app whose microphone use the track follows, by the name part, such as chrome; null when none is followed, as in person.</param>
/// <param name="Gate">Why the microphone is or is not being recorded now; <see cref="MicrophoneGateState.Unfollowed"/> when no app is followed.</param>
/// <param name="Why">The status line's words for <paramref name="Gate"/>, such as "chrome is not listening"; null while recording, and when no app is followed.</param>
public sealed record MicrophoneState(string Name, bool On, bool Capturing, string? Problem,
    string? Follows = null, MicrophoneGateState Gate = MicrophoneGateState.Unfollowed, string? Why = null);

/// <summary>A run as a window shows it: sent to every window on every change and once a second.</summary>
/// <param name="Phase">Where the run is.</param>
/// <param name="Name">The recording's name, such as Kalam.</param>
/// <param name="Position">Where the recording is.</param>
/// <param name="Source">What the class is recorded from now, by name, such as Speakers (Realtek(R) Audio) or chrome; null before the first capture.</param>
/// <param name="WaitingFor">What the recording waits for while nothing can be recorded, in words; null while it records.</param>
/// <param name="Microphone">Your microphone, with the class or in person; null when the run records none.</param>
/// <param name="UploadPercent">How much of the upload is done, from 0 to 100, while uploading; null otherwise.</param>
/// <param name="StopsAtUtc">When the run stops by itself (--minutes, or the safety stop); null when it does not.</param>
/// <param name="Attention">What needs attention now.</param>
/// <param name="InWindow">True when the run records in this window rather than in the background: closing the window ends it.</param>
/// <param name="StopsAtText">
/// <see cref="StopsAtUtc"/> as the title shows it, in the recorder's local time of day, such as 20:30; null when the run
/// does not stop by itself. Written once by the run, whose clock knows the time zone, for the title, which has none.
/// </param>
public sealed record RecorderState(RecorderPhase Phase, string Name, TimeSpan Position, string? Source, string? WaitingFor,
    MicrophoneState? Microphone, int? UploadPercent, DateTimeOffset? StopsAtUtc, Attention Attention, bool InWindow, string? StopsAtText = null);

/// <summary>Takes a run's state on every change and once a second, for the window's title and taskbar, or for the windows that show the run.</summary>
public interface IRecorderStatusSink
{
    /// <summary>Passes <paramref name="state"/> on.</summary>
    void Publish(RecorderState state);
}
