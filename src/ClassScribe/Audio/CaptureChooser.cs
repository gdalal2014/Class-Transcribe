namespace ClassScribe.Audio;

/// <summary>
/// Decides what to record, following <see cref="SourceChoice"/>. An app chosen by name is recorded
/// from the process that has its sound open. A device chosen by name is recorded and no other.
/// Otherwise the device the class is playing through is found with Windows' level meters: a device
/// that is playing is kept, and one that has gone quiet is left for another only once the sound there
/// has lasted a moment, so a short notification sound elsewhere does not pull the recording away.
/// </summary>
public sealed class CaptureChooser(TimeProvider time, SourceChoice choice)
{
    /// <summary>A device or app playing at or above this level (out of 1) counts as having sound. It is the recorder's own threshold, about -50 dB.</summary>
    public const float SoundLevel = 100f / short.MaxValue;

    /// <summary>A device that was playing is only left once it has been quiet for this long.</summary>
    public static readonly TimeSpan LeaveAfter = TimeSpan.FromSeconds(2);

    /// <summary>Sound on another device has to last this long, from one look to the next, before the recording moves there.</summary>
    public static readonly TimeSpan SustainFor = TimeSpan.FromSeconds(1);

    private string? _candidateId;
    private DateTimeOffset _candidateSince;

    /// <summary>True when an app is recorded rather than a device.</summary>
    public bool IsApp => choice.App is not null;

    /// <summary>The name the device was chosen by, or null when no device is chosen.</summary>
    public string? ChosenDevice => choice.Device;

    /// <summary>What the recording is waiting for while nothing can be recorded, in words for the log.</summary>
    public string WaitingFor => choice.App is { } app ? $"\"{app}\" to play sound"
        : choice.Device is { } device ? $"the output device \"{device}\" to connect"
        : "an output device";

    /// <summary>
    /// Picks what to record from what the provider lists, or returns null when there is nothing to record yet.
    /// <paramref name="recordedId"/> is the device or app being recorded, null before the first one, and
    /// <paramref name="recordedLastSound"/> when it last carried sound, null if it never has.
    /// </summary>
    public CaptureTarget? Choose(ILoopbackCaptureProvider provider, string? recordedId, DateTimeOffset? recordedLastSound)
    {
        if (IsApp)
            return ChooseApp(provider.GetPlayingApps(), recordedId) is { } app ? CaptureTarget.ForApp(app) : null;

        return ChooseDevice(provider.GetOutputDevices(), recordedId, recordedLastSound) is { } device ? CaptureTarget.ForDevice(device) : null;
    }

    /// <summary>Picks the device to record from those connected, or returns null when there is none to record.</summary>
    public OutputDevice? ChooseDevice(IReadOnlyList<OutputDevice> devices, string? recordedId, DateTimeOffset? recordedLastSound)
    {
        if (choice.Device is { } name) return devices.FirstOrDefault(device => Matches(device.Name, name));

        var playing = devices
            .Where(device => device.Level >= SoundLevel && device.Id != recordedId)
            .OrderByDescending(device => device.Level)
            .FirstOrDefault();
        var recorded = recordedId is null ? null : devices.FirstOrDefault(device => device.Id == recordedId);
        if (recorded is null)
        {
            // Starting, or the recorded device is gone: take the sound where it is, or else the default device.
            _candidateId = null;
            return playing ?? devices.FirstOrDefault(device => device.IsDefault) ?? devices.FirstOrDefault();
        }

        var now = time.GetUtcNow();
        var quiet = recordedLastSound is not { } lastSound || now - lastSound >= LeaveAfter;
        if (!quiet || playing is null)
        {
            _candidateId = null;
            return recorded;
        }

        if (playing.Id != _candidateId)
        {
            _candidateId = playing.Id;
            _candidateSince = now;
            return recorded;
        }

        if (now - _candidateSince < SustainFor) return recorded;

        _candidateId = null;
        return playing;
    }

    /// <summary>
    /// Picks the process to record the chosen app from: the one being recorded while it still has sound
    /// open, otherwise the one playing loudest. Null when no process of the app has sound open, or no app is chosen.
    /// </summary>
    public PlayingApp? ChooseApp(IReadOnlyList<PlayingApp> apps, string? recordedId)
    {
        if (choice.App is not { } name) return null;

        var matching = apps.Where(app => Matches(app.Name, name)).ToList();
        return matching.FirstOrDefault(app => CaptureTarget.ForApp(app).Id == recordedId)
            ?? matching.OrderByDescending(app => app.Level).FirstOrDefault();
    }

    /// <summary>True when the name is the one chosen, or contains it, in any case.</summary>
    private static bool Matches(string candidate, string chosen) =>
        string.Equals(candidate, chosen, StringComparison.OrdinalIgnoreCase) || candidate.Contains(chosen, StringComparison.OrdinalIgnoreCase);
}
