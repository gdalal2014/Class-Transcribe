namespace ClassScribe.Audio.Microphones;

/// <summary>
/// What one look of the watch found, on the clock the microphone's packets are stamped with. The gate judges each packet
/// by the look before its stamp and the look after it.
/// </summary>
/// <param name="Timestamp100ns">When the look began, on the performance counter in 100-nanosecond units, as the packets are stamped.</param>
/// <param name="Listening">Whether the followed app is taking sound from the recorded microphone.</param>
/// <param name="MutedInWindows">Whether the recorded microphone is muted in Windows; null when that could not be read, which counts as not muted.</param>
/// <param name="ListeningApps">The apps taking sound from the recorded microphone, by name, ClassScribe's own processes and Windows' sounds left out.</param>
/// <param name="OtherMicrophone">The name of another microphone the followed app is taking sound from, when it has no active session on the recorded one; null otherwise.</param>
/// <param name="Problem">What could not be read, when something could not; null otherwise.</param>
/// <param name="Holds">True when the followed app has a session on the recorded microphone that has not expired, active or not: it holds the microphone open.</param>
/// <param name="ProcessAlive">
/// While the followed app is not taking sound, whether the process that last did still runs: false when it has ended, so
/// that its session going is no mute of his; null while it is taking sound, before it ever did, and when no app is named.
/// </param>
/// <param name="UsageInUse">
/// What Windows' usage record says about the followed app: true when it has the microphone open now, false when it has
/// closed it, null when the app is absent from the record or in it without any recorded use, the record could not be
/// read, or no app is named.
/// </param>
/// <param name="RecordedMicrophone">The name of the recorded microphone, for the words that name it; null when it could not be read.</param>
/// <param name="OtherMicrophoneId">The endpoint id of <paramref name="OtherMicrophone"/>, for the chooser that moves the recording to it; null when there is none.</param>
public sealed record WatchLook(long Timestamp100ns, AppListening Listening, bool? MutedInWindows, IReadOnlyList<string> ListeningApps,
    string? OtherMicrophone, string? Problem, bool Holds = false, bool? ProcessAlive = null, bool? UsageInUse = null, string? RecordedMicrophone = null,
    string? OtherMicrophoneId = null);
