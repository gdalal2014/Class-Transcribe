namespace ClassScribe.Audio;

/// <summary>Something worth a note in the transcript at a position in the recording, such as a time the laptop slept.</summary>
/// <param name="Position">Where in the recording, counted as <see cref="IRecording.Position"/> counts.</param>
/// <param name="Text">The note in words, such as "The laptop slept from 19:42 to 19:50; nothing was recorded then".</param>
public sealed record TimelineNote(TimeSpan Position, string Text);

/// <summary>One track of a running recording, as the checks made once a second see it. How long ago something happened is measured on the steady clock.</summary>
/// <param name="Source">The device, app or microphone the track records, by name; empty before its first capture.</param>
/// <param name="WaitingFor">What the track waits for while nothing can be recorded, in words for the log; null while it captures.</param>
/// <param name="Written">How much audio this run has written to the track's files.</param>
/// <param name="SinceLastSound">
/// How long ago audible sound last arrived on the track; counted from the start, or from the end of the last pause, while
/// none has since.
/// </param>
/// <param name="SinceLastPacket">
/// How long ago the open capture last handed over any audio, silence included; until its first packet, how long ago it
/// was opened. Null while no capture is open.
/// </param>
/// <param name="DeliversContinuously">
/// True when the open capture hands over audio all the time, silence included, as an app's sound and a microphone do, so
/// that a growing <paramref name="SinceLastPacket"/> means it has stopped working. False for an output device, which hands
/// over nothing while nothing plays, and while no capture is open.
/// </param>
/// <param name="RecordedLevel">How loud Windows' meter showed what the track records at the last look, from 0 to 1; null when that look read no level.</param>
/// <param name="SoundWhilePaused">True when audible sound keeps arriving while the recording is paused: the class may be playing unrecorded.</param>
/// <param name="Capturing">True while a capture is open and hands its audio to the track.</param>
/// <param name="Off">True while the track is switched off (V switches the microphone); it keeps its place on the clock.</param>
/// <param name="WriteFailing">True while writing the track's files fails, as on a full disk.</param>
/// <param name="OnlyZeros">True when the track has received nothing but exact zeros for a while, as from a microphone muted in Windows.</param>
/// <param name="HeardSound">True once audible sound has reached the track's files in this run; sound heard only while paused does not count.</param>
/// <param name="RawZerosFor">
/// How long the track's capture has handed over only exact zeros at the device's own depth, since it opened or since its last
/// other sample (see <see cref="RecordingStatus.RawZerosFor"/>); null while it captures nothing, and before its first packet.
/// </param>
/// <param name="RawSoundSinceOpened">True once the track's capture has handed over a sample other than zero, at the device's own depth, since it opened.</param>
/// <param name="WindowsSilent">True when Windows flagged the capture's last packet as silent: Windows' silence, not the device's.</param>
/// <param name="CaptureNumber">How many captures the track has opened so far: a new number is a new capture.</param>
public sealed record TrackSnapshot(string Source, string? WaitingFor, TimeSpan Written, TimeSpan SinceLastSound, TimeSpan? SinceLastPacket,
    bool DeliversContinuously, float? RecordedLevel, bool SoundWhilePaused, bool Capturing, bool Off, bool WriteFailing, bool OnlyZeros, bool HeardSound = false,
    TimeSpan? RawZerosFor = null, bool RawSoundSinceOpened = false, bool WindowsSilent = false, int CaptureNumber = 0);

/// <summary>A running recording as the checks made once a second see it.</summary>
/// <param name="Position">Where the recording is now: what a mark set now gets (see <see cref="IRecording.Position"/>).</param>
/// <param name="Paused">True while paused with P.</param>
/// <param name="Main">The main track: the class, or your microphone in person.</param>
/// <param name="Microphone">Your microphone, recorded with the class; null when the recording has no such track.</param>
public sealed record RecordingSnapshot(TimeSpan Position, bool Paused, TrackSnapshot Main, TrackSnapshot? Microphone);

/// <summary>What a run recorded, once it has stopped.</summary>
/// <param name="Length">How much audio this run recorded; the earlier parts of a recording it continued are not counted.</param>
/// <param name="MainSegments">The main track's segment files this run wrote, in order.</param>
/// <param name="MicrophoneSegments">The microphone track's segment files this run wrote, in order; empty without one.</param>
/// <param name="HeardAnything">False when nothing audible arrived during the whole run: there is nothing to transcribe.</param>
public sealed record RecordingSummary(TimeSpan Length, IReadOnlyList<string> MainSegments, IReadOnlyList<string> MicrophoneSegments, bool HeardAnything);
