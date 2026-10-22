using ClassScribe.Commands;
using ClassScribe.YouTube;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClassScribe.Audio;

/// <summary>
/// Decides what to record, following the class source a run's request names. An app is recorded from the process that
/// has its sound open. A device named by part of its name is recorded and no other. Otherwise the device the class is
/// playing through is found with Windows' level meters: a device that is playing is kept, and one that has gone quiet is
/// left for another only once the sound there has lasted a moment, so a short notification sound elsewhere does not pull
/// the recording away. Those moments are timed on the steady clock, so Windows setting its time of day does not shorten
/// them. Each look also notes how loud its pick plays (<see cref="RecordedLevel"/>).
/// </summary>
/// <remarks>
/// A YouTube video's run (design 4.3), whose <paramref name="video"/> follows the window the batch opened, records that
/// window's browser process and everything it started, through process loopback, from before the video plays, so that its
/// first second is not lost; its level is the loudest session in that tree. It fails closed: with the window gone it picks
/// nothing, never another Chrome by name, which could be Gulzar's everyday one. An ordinary recording of an app leaves out
/// every process of a live video window of ClassScribe's, so that a class never records a video window left open.
/// </remarks>
/// <param name="time">The clock the moments are timed on.</param>
/// <param name="source">What the run's request names to record.</param>
/// <param name="logger">Where the chooser says what it found.</param>
/// <param name="video">The run's video window; null, as for the tests that need none, when no video window is known of.</param>
public sealed class CaptureChooser(TimeProvider time, ClassSource source, ILogger<CaptureChooser> logger, VideoPlayback? video = null)
{
    /// <summary>A device or app playing at or above this level (out of 1) counts as having sound. It is the recorder's own threshold, about -50 dB.</summary>
    public const float SoundLevel = 100f / short.MaxValue;

    /// <summary>A device that was playing is only left once it has been quiet for this long.</summary>
    public static readonly TimeSpan LeaveAfter = TimeSpan.FromSeconds(2);

    /// <summary>Sound on another device has to last this long, from one look to the next, before the recording moves there.</summary>
    public static readonly TimeSpan SustainFor = TimeSpan.FromSeconds(1);

    private string? _candidateId;
    private long _candidateSince;

    /// <summary>True once the recorded app's closing has been said, until the app has sound open again.</summary>
    private bool _closedSaid;

    /// <summary>
    /// A chooser for what <paramref name="choice"/> names at this moment, as a run's request would take it, without a log.
    /// Kept for the record command's tests, which still make their session this way; to be removed once they make it from
    /// a class source.
    /// </summary>
    public CaptureChooser(TimeProvider time, SourceChoice choice)
        : this(time, choice.Current.Class ?? new ClassSource.Automatic(), NullLogger<CaptureChooser>.Instance)
    {
    }

    /// <summary>What this chooser records: the device the class plays on, one device by part of its name, or one app.</summary>
    public ClassSource Source => source;

    /// <summary>True when an app is recorded rather than a device.</summary>
    public bool IsApp => source is ClassSource.App;

    /// <summary>The part of a name the device to record was chosen by, or null when no device is named.</summary>
    public string? ChosenDevice => (source as ClassSource.Device)?.NamePart;

    /// <summary>
    /// How loud Windows' meter showed, at the last look (<see cref="Choose"/>), the device or app that look picked: the
    /// one recorded, or the one the recording moves to. From 0 (silence) to 1. Null before the first look, and when the
    /// last look failed or found nothing to record. Together with the time since the capture last handed over audio, it
    /// shows a capture that has stopped working while what it records still plays.
    /// </summary>
    public float? RecordedLevel { get; private set; }

    /// <summary>What the recording is waiting for while nothing can be recorded, in words for the log.</summary>
    public string WaitingFor => video is { IsVideo: true }
        ? VideoPlayback.WindowName
        : source switch
        {
            ClassSource.App app => $"\"{app.ProcessName}\" to play sound",
            ClassSource.Device device => $"the output device \"{device.NamePart}\" to connect",
            _ => "an output device"
        };

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
        if (video is { IsVideo: true } window) return ChooseVideo(provider, window);

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
        if (ChosenDevice is { } name) return devices.FirstOrDefault(device => Matches(device.Name, name));

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
    /// Picks the process to record the app from: the one being recorded while it still has sound open, otherwise the one
    /// playing loudest. Null when no process of the app has sound open, or no app is to be recorded. When the app was
    /// being recorded (<paramref name="recordedId"/> is set) and no process of it has sound open any more, it has closed:
    /// that is said once, and again only after the app has had sound open in between. The capture already open is left
    /// to record its silence until the app plays again.
    /// </summary>
    public PlayingApp? ChooseApp(IReadOnlyList<PlayingApp> apps, string? recordedId)
    {
        if (source is not ClassSource.App { ProcessName: var name }) return null;

        var matching = apps.Where(app => Matches(app.Name, name)).ToList();

        // A video window of ClassScribe's, left open, is never a class's: its processes are left out.
        if (matching.Count > 0 && video?.VideoWindowsProcesses() is { Count: > 0 } videoWindows)
            matching.RemoveAll(app => videoWindows.Contains(app.ProcessId));

        if (matching.Count > 0)
        {
            _closedSaid = false;
        }
        else if (recordedId is not null && !_closedSaid)
        {
            _closedSaid = true;
            logger.LogWarning("\"{App}\" has closed; recording silence until it plays again", name);
        }

        return matching.FirstOrDefault(app => CaptureTarget.ForApp(app).Id == recordedId)
            ?? matching.OrderByDescending(app => app.Level).FirstOrDefault();
    }

    /// <summary>
    /// A YouTube video's pick (see the remarks): the window's browser process, whose tree process loopback records, from
    /// before the video plays; how loud the loudest session of that tree plays, or no level while none plays. Nothing once the
    /// window is gone: the run then stops (design 4.4, rule 1), and never records another Chrome.
    /// </summary>
    private CaptureTarget? ChooseVideo(ILoopbackCaptureProvider provider, VideoPlayback window)
    {
        var tree = window.Tree();
        if (tree.Count == 0 || window.RootProcessId is not { } root) return null;

        RecordedLevel = provider.GetPlayingApps().Where(app => tree.Contains(app.ProcessId)).Select(app => (float?)app.Level).Max();
        return new CaptureTarget($"process:{root}", VideoPlayback.WindowName, root);
    }

    /// <summary>True when the name is the one chosen, or contains it, in any case.</summary>
    private static bool Matches(string candidate, string chosen) =>
        string.Equals(candidate, chosen, StringComparison.OrdinalIgnoreCase) || candidate.Contains(chosen, StringComparison.OrdinalIgnoreCase);
}
