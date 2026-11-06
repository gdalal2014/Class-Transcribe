namespace ClassScribe.Audio.Microphones;

/// <summary>Whether the followed app is taking sound from the recorded microphone, as one look saw it.</summary>
public enum AppListening
{
    /// <summary>It has no session on the microphone, or none that is active: nothing it could hear.</summary>
    No,

    /// <summary>At least one of its sessions on the microphone is active: it is taking sound, whether or not it does anything with it.</summary>
    Yes,

    /// <summary>
    /// The sessions could not all be read, and what could not be read may be the app's, so nothing is known: the list
    /// itself, a session whose process could not be read, or the app's own session whose state Windows would not give
    /// while its usage record says nothing of it.
    /// </summary>
    Unknown
}

/// <summary>The state of an app's session on a microphone, as Windows reports it (AudioSessionState).</summary>
public enum CaptureSessionState
{
    /// <summary>The app has the session, but none of its streams is running: no sound is being taken.</summary>
    Inactive = 0,

    /// <summary>At least one of the app's streams is running: sound is being taken from the microphone.</summary>
    Active = 1,

    /// <summary>The app has closed every stream of the session.</summary>
    Expired = 2
}

/// <summary>The roles for which Windows can make a microphone its default.</summary>
[Flags]
public enum MicrophoneRoles
{
    /// <summary>Not Windows' default for any role.</summary>
    None = 0,

    /// <summary>eConsole: Windows' default microphone for most apps, the one Sound settings show.</summary>
    Console = 1,

    /// <summary>eMultimedia: Windows' default microphone for music and video apps.</summary>
    Multimedia = 2,

    /// <summary>eCommunications: Windows' default microphone for calls, the one a class page uses.</summary>
    Communications = 4
}

/// <summary>One app's session on a microphone, as one look read it.</summary>
/// <param name="Key">The session's instance identifier, unique to the session, by which it is followed from one look to the next.</param>
/// <param name="EndpointId">The microphone the session is on.</param>
/// <param name="ProcessId">The process that has the session; 0 for a session that spans processes, such as Windows' system sounds.</param>
/// <param name="ImageName">The process's program without .exe, such as chrome; null when the process could not be looked at.</param>
/// <param name="SystemSounds">True for Windows' system sounds session, which is no app's.</param>
/// <param name="State">Whether the session is taking sound now.</param>
/// <param name="SessionMuted">The session's own mute flag, read for the log and never relied on; null when it could not be read.</param>
public sealed record CaptureSession(string Key, string EndpointId, int ProcessId, string? ImageName, bool SystemSounds, CaptureSessionState State,
    bool? SessionMuted);

/// <summary>
/// A session on a microphone that one look could not read in full, with what was read of it before the call that failed:
/// its process, read first so that a session whose other calls fail is still known by whose it is, then its state. The
/// watch reads it to tell whether what could not be read concerns the followed app at all.
/// </summary>
/// <param name="ProcessId">The process that has the session; 0 for a session that spans processes; null when not even that could be read.</param>
/// <param name="ImageName">The process's program without .exe, such as Zoom; null when the process could not be read or named.</param>
/// <param name="State">The session's state, when it was read before the call that failed; null otherwise.</param>
public sealed record UnreadCaptureSession(int? ProcessId, string? ImageName, CaptureSessionState? State);

/// <summary>What one look read about one microphone: the microphone itself, and every app's session on it.</summary>
/// <param name="EndpointId">The identifier Windows gave the microphone.</param>
/// <param name="Name">The name Windows shows for it; the identifier when the name could not be read.</param>
/// <param name="DefaultFor">The roles for which it is Windows' default.</param>
/// <param name="MutedInWindows">Its mute in Windows, the switch in Sound settings; null when that could not be read.</param>
/// <param name="VolumePercent">Its input volume in Windows, from 0 to 100; null when that could not be read.</param>
/// <param name="Sessions">Every session read on it.</param>
/// <param name="Complete">
/// True when every session on it was read, so that an app missing from <paramref name="Sessions"/> has no session; false
/// when one or more could not be read, or the list could not be read at all.
/// </param>
/// <param name="Problem">What could not be read, in words with the calls that failed and their result codes, or null when everything was.</param>
/// <param name="Unread">
/// While <paramref name="Complete"/> is false: the sessions that could not be read in full, with what was read of each,
/// when the list of sessions itself was read; null when it was not, so that what is missing is not known. Nothing is
/// unread from a complete listing, whatever this holds.
/// </param>
public sealed record MicrophoneSessionListing(string EndpointId, string Name, MicrophoneRoles DefaultFor, bool? MutedInWindows, int? VolumePercent,
    IReadOnlyList<CaptureSession> Sessions, bool Complete, string? Problem, IReadOnlyList<UnreadCaptureSession>? Unread = null);

/// <summary>What one look read about every active microphone at once: each microphone's listing, or why the list could not be read.</summary>
/// <param name="Listings">Every active microphone with its sessions; empty when the list could not be read, and when no microphone is active.</param>
/// <param name="Problem">Why the list could not be read, in words with the result code, when it could not (Windows' audio service restarting, a stale enumerator); null when it was read, however many microphones it holds.</param>
public sealed record MicrophoneListings(IReadOnlyList<MicrophoneSessionListing> Listings, string? Problem);
