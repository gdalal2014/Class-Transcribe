namespace ClassScribe.Audio;

/// <summary>What a finished recording produced.</summary>
/// <param name="Folder">The folder the segment files are in.</param>
/// <param name="SegmentPaths">The segment files this session wrote, in order.</param>
/// <param name="Duration">How much audio they hold, the silence written to keep a track on the clock included.</param>
/// <param name="HeardSound">
/// True when audible sound reached the files: false when they hold only silence, or nothing, so that there is nothing to
/// transcribe. Sound heard only while paused is not in them, so it does not count.
/// </param>
/// <param name="Corrections">How many packets were padded or trimmed to keep the track on the clock, for the end-of-class summary; zero for a track not on the clock.</param>
public sealed record RecordingResult(string Folder, IReadOnlyList<string> SegmentPaths, TimeSpan Duration, bool HeardSound, int Corrections = 0);

/// <summary>A snapshot of a running recording. How long ago something happened is measured on the steady clock.</summary>
/// <param name="Captured">How much audio the segment files hold, the silence written to keep a track on the clock included.</param>
/// <param name="SinceLastSound">
/// How long ago audible sound last arrived; counted from the start, or from the end of the last pause, while none has since.
/// </param>
/// <param name="DeviceName">The device, app or microphone captured last; empty before the first capture.</param>
/// <param name="SegmentCount">How many segment files the recording has so far.</param>
/// <param name="Paused">True while paused.</param>
/// <param name="WaitingFor">
/// What is awaited while nothing can be recorded yet, in words for the log; null while a capture is open, and while the
/// track is switched off (<see cref="Off"/>), when nothing is awaited.
/// </param>
/// <param name="SinceLastPacket">
/// How long ago the open capture last handed over any audio, silence included, whether it was kept or not (it is not
/// while paused); until its first packet, how long ago it was opened. Null while no capture is open.
/// </param>
/// <param name="DeliversContinuously">
/// True when the open capture hands over audio all the time, as an app's sound and a microphone do, so that a growing
/// <paramref name="SinceLastPacket"/> means it has stopped working. False for an output device, which hands over nothing
/// while nothing plays, and while no capture is open.
/// </param>
/// <param name="RecordedLevel">
/// How loud Windows' meter showed the recorded device or app at the last look, from 0 to 1. Null while no capture is
/// open, and when the last look read no level: when the look failed, when it did not find what is recorded among what
/// plays, as once the recorded app has closed, and for a microphone, which has no meter. The looks go on while paused.
/// </param>
/// <param name="SoundWhilePaused">
/// True while paused when audible sound keeps arriving from the capture, though none of it is written: the class may be
/// playing unrecorded. A short quiet, such as between two sentences, does not end it. Always false while recording.
/// </param>
/// <param name="WriteFailing">
/// True while writing the segment files fails, as on a full disk or a drive that has gone: from a write that failed until
/// a write works again, the pause given to a disk that keeps failing included.
/// </param>
/// <param name="Off">True while the track is switched off with V (see <c>RecordingSession.SetEnabled</c>): no capture is open, and none is opened.</param>
/// <param name="OnlyZeros">
/// True when the open capture has handed over nothing but exact zeros for <c>RecordingSession.OnlyZerosAfter</c>,
/// since it was opened or since the last other sample, as a microphone muted in Windows or blocked by its privacy
/// settings gives. False before its first packet, and while no capture is open.
/// </param>
/// <param name="HeardSound">True once audible sound has reached the segment files in this run; sound heard only while paused does not count.</param>
/// <param name="RawZerosFor">
/// How long the open capture has handed over nothing but exact zeros at the device's own depth, before ClassScribe's 16-bit
/// conversion, since it was opened or since its last sample other than zero (camera-first 3.4): Windows' faint residue in
/// a quiet room is not zero here, though the conversion rounds it to zero. Packets Windows flags as silent count as neither.
/// Null while no capture is open, and before its first packet.
/// </param>
/// <param name="RawSoundSinceOpened">True once the open capture has handed over a sample other than zero, at the device's own depth, since it was opened.</param>
/// <param name="WindowsSilent">True when Windows flagged the open capture's last packet as silent: Windows' silence, such as a muted or blocked microphone, not the device's.</param>
/// <param name="CaptureNumber">How many captures this session has opened so far: a new number is a new capture.</param>
public sealed record RecordingStatus(TimeSpan Captured, TimeSpan SinceLastSound, string DeviceName, int SegmentCount, bool Paused, string? WaitingFor,
    TimeSpan? SinceLastPacket = null, bool DeliversContinuously = false, float? RecordedLevel = null, bool SoundWhilePaused = false,
    bool WriteFailing = false, bool Off = false, bool OnlyZeros = false, bool HeardSound = false, TimeSpan? RawZerosFor = null, bool RawSoundSinceOpened = false,
    bool WindowsSilent = false, int CaptureNumber = 0);
