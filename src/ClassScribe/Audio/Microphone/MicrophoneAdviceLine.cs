namespace ClassScribe.Audio.Microphones;

/// <summary>One line of advice at the start of a recording with the microphone.</summary>
/// <param name="Text">The line.</param>
/// <param name="Warning">True for a warning about something to change, which the run also shows as a Notice; false for a line that says what is recorded and how.</param>
public sealed record MicrophoneAdviceLine(string Text, bool Warning);

/// <summary>How a recording with the microphone starts, for the advice about it.</summary>
/// <param name="Mode">The class and the microphone, or the microphone alone in person. The class alone gets no advice.</param>
/// <param name="Microphone">The microphone chosen; null while there is none to record yet (none connected, or the named one missing), when the lines about one come from <see cref="MicrophoneAdvice.ForMicrophone"/> once it is chosen.</param>
/// <param name="Used">How Windows processes its audio: RAW, or Default.</param>
/// <param name="RawRefusal">Why RAW was not available when it was asked for and Default is used instead, such as 0x88890027; null otherwise.</param>
/// <param name="Why">Why that microphone was chosen, as the chooser said it; null to leave it out.</param>
/// <param name="WaitingFor">What the recording waits for while there is no microphone, as the chooser says it, such as the microphone "OBSBOT"; null for a microphone, plainly.</param>
/// <param name="ClassDeviceName">The device the class plays on at the start, with the class; null when not known.</param>
/// <param name="ClassHeardInTheRoom">True when that device is one the room hears, so that the microphone records the class a second time.</param>
/// <param name="Follows">Which app's microphone use the microphone track follows, with the class; null or None when V alone switches it.</param>
/// <param name="AppMute">Whether the followed app lets go of the microphone when you mute in it.</param>
/// <param name="Off">
/// True when the track starts switched off, as it does while the followed app's mute cannot be seen: <paramref name="Microphone"/>
/// is then the one the recording would record once V switches it on, nothing is opened yet, and <paramref name="Used"/> is
/// what will be asked for.
/// </param>
public sealed record MicrophoneStart(RecordingMode Mode, Microphone? Microphone, MicrophoneProcessing Used, string? RawRefusal = null, string? Why = null,
    string? WaitingFor = null, string? ClassDeviceName = null, bool ClassHeardInTheRoom = false, FollowRule? Follows = null, ClassAppMute AppMute = ClassAppMute.Unknown,
    bool Off = false);
