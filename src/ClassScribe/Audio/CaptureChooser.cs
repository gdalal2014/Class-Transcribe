namespace ClassScribe.Audio;

/// <summary>
/// Decides what to record, following <see cref="SourceChoice"/>. An app chosen by name is recorded
/// from the process that has its sound open. A device chosen by name is recorded and no other.
/// Otherwise the device the class is playing through is found with Windows' level meters: a device
/// that is playing is kept, and one that has gone quiet is left for another only once the sound there
/// has lasted a moment, so a short notification sound elsewhere does not pull the recording away.
/// Those moments are timed on the steady clock, so Windows setting its time of day does not shorten them.
/// Each look also notes how loud its pick plays (<see cref="RecordedLevel"/>).
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
    private long _candidateSince;

    /// <summary>True when an app is recorded rather than a device.</summary>
    public bool IsApp => choice.App is not null;

    /// <summary>The name the device was chosen by, or null when no device is chosen.</summary>
    public string? ChosenDevice => choice.Device;

    /// <summary>
    /// How loud Windows' meter showed, at the last look (<see cref="Choose"/>), the device or app that look picked: the
    /// one recorded, or the one the recording moves to. From 0 (silence) to 1. Null before the first look, and when the
    /// last look failed or found nothing to record. Together with the time since the capture last handed over audio, it
    /// shows a capture that has stopped working while what it records still plays.
    /// </summary>
    public float? RecordedLevel { get; private set; }

    /// <summary>What the recording is waiting for while nothing can be recorded, in words for the log.</summary>
    public string WaitingFor => choice.App is { } app ? $"\"{app}\" to play sound"
        : choice.Device is { } device ? $"the output device \"{device}\" to connect"
        : "an output device";

    /// <summary>
    /// One look: picks what to record from what the provider lists, or returns null when there is nothing to record yet,
    /// and notes how loud the pick plays (<see cref="RecordedLevel"/>). <paramref name="recordedId"/> is the device or
    /// app being recorded, null before the first one, and <paramref name="sinceRecordedSound"/> how long ago it last
    /// carried sound, null if it never has.
    /// </summary>
    public CaptureTarget? Choose(ILoopbackCaptureProvider provider, string? recordedId, TimeSpan? sinceRecordedSound)
    {
        // A look that fails leaves no level behind: what it would have read is not known.
        RecordedLevel = null;
        if (IsApp)
        {
            var app = ChooseApp(provider.GetPlayingApps(), recordedId);
            RecordedLevel = app?.Level;
            return app is null ? null : CaptureTarget.ForApp(app);
        }

        var device = ChooseDevice(provider.GetOutputDevices(), recordedId, sinceRecordedSound);
        RecordedLevel = device?.Level;
        return device is null ? null : CaptureTarget.ForDevice(device);
    }

    /// <summary>
    /// Picks the device to record from those connected, or returns null when there is none to record.
    /// <paramref name="sinceRecordedSound"/> is how long ago the recorded device last carried sound, null if it never has.
    /// </summary>
    public OutputDevice? ChooseDevice(IReadOnlyList<OutputDevice> devices, string? recordedId, TimeSpan? sinceRecordedSound)
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

        var quiet = sinceRecordedSound is not { } sinceSound || sinceSound >= LeaveAfter;
        if (!quiet || playing is null)
        {
            _candidateId = null;
            return recorded;
        }

        var now = time.GetTimestamp();
        if (playing.Id != _candidateId)
        {
            _candidateId = playing.Id;
            _candidateSince = now;
            return recorded;
        }

        if (time.GetElapsedTime(_candidateSince, now) < SustainFor) return recorded;

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
