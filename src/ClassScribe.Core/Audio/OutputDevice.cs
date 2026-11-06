namespace ClassScribe.Audio;

/// <summary>An output device Windows can play through right now.</summary>
/// <param name="Id">Windows' identifier for the device.</param>
/// <param name="Name">The name Windows shows for it.</param>
/// <param name="IsDefault">True for the device Windows plays through unless an app picks another.</param>
/// <param name="Level">How loud the device is playing at this moment, from 0 (silence) to 1, as Windows' level meter reports it.</param>
/// <param name="HeardInTheRoom">
/// True for a device the room hears, so that a microphone recording beside it records the class a second time: speakers,
/// a line-level output, S/PDIF, or a display's own audio, by the form factor Windows gives the device. False for
/// headphones and headsets, and when the form factor is not known.
/// </param>
public sealed record OutputDevice(string Id, string Name, bool IsDefault, float Level, bool HeardInTheRoom = false);

/// <summary>An app that has sound open on an output device.</summary>
/// <param name="ProcessId">The process that plays the sound.</param>
/// <param name="Name">The process name, such as chrome or msedge.</param>
/// <param name="DeviceName">The output device the app plays through.</param>
/// <param name="Level">How loud the app is playing at this moment, from 0 (silence) to 1, as Windows' level meter for its sound reports it.</param>
/// <param name="DeviceId">Windows' identifier for the device the app plays through, which says whether that device is heard in the room; empty when not known.</param>
public sealed record PlayingApp(int ProcessId, string Name, string DeviceName, float Level, string DeviceId = "");

/// <summary>
/// What the apps listing could not read in full: one audio session on an output device, or all of a device's sessions
/// when Windows would not list them.
/// </summary>
/// <param name="DeviceName">The output device.</param>
/// <param name="ProcessName">The app the session belongs to, when Windows named its process; null otherwise, and for a whole device.</param>
/// <param name="Reason">Which call failed and why, such as "GetState: The parameter is incorrect. (0x80070057)".</param>
/// <param name="WholeDevice">True when none of the device's sessions could be listed, rather than one session not read.</param>
public sealed record UnreadAudioSession(string DeviceName, string? ProcessName, string Reason, bool WholeDevice = false)
{
    /// <summary>
    /// What could not be read, with the reason, in words for a line: <c>Zoom's audio session on "Speakers" (GetState: ...)</c>,
    /// <c>an audio session on "Speakers" (GetSession: ...)</c> or <c>the audio sessions on "Headphones" (Activate: ...)</c>.
    /// </summary>
    public string Describe()
    {
        var what = WholeDevice ? "the audio sessions" : ProcessName is { } name ? $"{name}'s audio session" : "an audio session";
        return $"{what} on \"{DeviceName}\" ({Reason})";
    }
}

/// <summary>The apps with sound open, as one look listed them, and what that look could not read.</summary>
/// <param name="Apps">The apps, as <c>ILoopbackCaptureProvider.GetPlayingApps</c> lists them.</param>
/// <param name="Unread">The sessions, or the devices' lists of sessions, that could not be read in full; empty when everything was.</param>
public sealed record PlayingAppsListing(IReadOnlyList<PlayingApp> Apps, IReadOnlyList<UnreadAudioSession> Unread);

/// <summary>What a capture records: an output device, or the sound of one app and its child processes.</summary>
/// <param name="Id">The device's identifier, or <c>process:</c> and the app's process id.</param>
/// <param name="Name">The device's or the app's name, for the log.</param>
/// <param name="ProcessId">The app's process, or null for a device.</param>
public sealed record CaptureTarget(string Id, string Name, int? ProcessId = null)
{
    /// <summary>The target that records an output device.</summary>
    public static CaptureTarget ForDevice(OutputDevice device) => new(device.Id, device.Name);

    /// <summary>The target that records one app's sound, from the process that has it open.</summary>
    public static CaptureTarget ForApp(PlayingApp app) => new($"process:{app.ProcessId}", app.Name, app.ProcessId);
}
