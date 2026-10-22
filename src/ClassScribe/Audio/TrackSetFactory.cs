using ClassScribe.Audio.Microphones;
using ClassScribe.Audio.Timeline;
using ClassScribe.Commands;
using ClassScribe.Desktop;
using ClassScribe.Recorder;
using ClassScribe.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClassScribe.Audio;

/// <summary>
/// Makes the recording a run's source plan asks for. The class alone is a <see cref="ClassRecording"/>, which appends the
/// class's audio as it comes, as ClassScribe has always recorded. The class and your microphone are a <see cref="TrackSet"/>
/// of two sessions on one recording clock, with the gate that follows the class app and the watch that feeds it: the app
/// recorded with --app, or else the app Recording:MicrophoneFollows names, unless the plan names another; auto follows any
/// app that holds the microphone, and none leaves V alone to switch it. In person it is a track set of the microphone
/// alone, following no app. Nothing is looked at or opened before the recording starts. The settings are those of the
/// run's own scope, and the freeze watch is the run's own, shared with the checks made once a second.
/// </summary>
/// <param name="loopback">Lists and opens what the computer plays, for the class's track.</param>
/// <param name="microphones">Lists and opens the microphones.</param>
/// <param name="sessions">Reads which apps have a microphone open, for the watch.</param>
/// <param name="usage">Reads Windows' microphone usage record, for the watch.</param>
/// <param name="names">Names processes, for the watch.</param>
/// <param name="own">Tells ClassScribe's own processes, whose microphone sessions never count as an app's.</param>
/// <param name="freezes">The run's watch for times ClassScribe did not run, which the recording clock cuts out.</param>
/// <param name="options">The run's recording settings.</param>
/// <param name="transcript">The run's transcript settings: how long a stretch with nothing playing must be for a heading, in a recording of the class alone.</param>
/// <param name="time">The clock everything is measured on.</param>
/// <param name="loggers">Makes a logger for each part of the recording.</param>
/// <param name="video">
/// The run's YouTube video window, which a video's run records and a class's leaves out (see <see cref="CaptureChooser"/>); null
/// when none is known of, as for the tests that need none.
/// </param>
/// <param name="webcams">
/// Makes each recording's webcam hold: which webcam the microphone belongs to, and with Recording:WakeCameraMicrophone on,
/// its picture held open, shared, while the microphone records (see <see cref="Camera.WebcamHold"/>); null for none, as for
/// the tests that need none.
/// </param>
public sealed class TrackSetFactory(
    ILoopbackCaptureProvider loopback,
    IMicrophoneProvider microphones,
    IMicrophoneSessions sessions,
    IMicrophoneUsageRecord usage,
    IProcessNames names,
    IOwnProcesses own,
    FreezeWatch freezes,
    IOptionsSnapshot<RecordingOptions> options,
    IOptionsSnapshot<TranscriptOptions> transcript,
    TimeProvider time,
    ILoggerFactory loggers,
    YouTube.VideoPlayback? video = null,
    Camera.WebcamHolds? webcams = null) : IRecordingFactory
{
    /// <summary>A recording, not started yet, of what <paramref name="source"/> names, on a recording clock of its own.</summary>
    public IRecording Create(SourcePlan source) => Create(source, new RecordingClock(time));

    /// <summary>
    /// A recording, not started yet, of what <paramref name="source"/> names, placed on <paramref name="clock"/>, which belongs
    /// to it alone; the class alone needs no clock and ignores it. A plan with a camera is refused with a
    /// <see cref="Camera.CameraNotBuiltException"/>: the window refuses one before anything records, and this build has no
    /// camera track to put beside the sound (slice 7 adds it, through <see cref="Camera.ICameraRecorder"/>).
    /// </summary>
    public IRecording Create(SourcePlan source, RecordingClock clock)
    {
        if (source.Camera is not null) throw new Camera.CameraNotBuiltException(Camera.CameraRecorderNotBuilt.Words);

        var settings = options.Value;
        switch (source.Mode)
        {
            case RecordingMode.Class:
                var gaps = TimeSpan.FromSeconds(Math.Max(0, transcript.Value.ShowGapsLongerThanSeconds));
                return new ClassRecording(ClassSession(source, new TrackSpec(Track.Main, TrackPlacement.Contiguous, gaps), clock: null), loggers.CreateLogger<ClassRecording>());

            case RecordingMode.ClassAndMicrophone:
                var rule = FollowRuleFor(source, settings);
                var theClass = ClassSession(source, new TrackSpec(Track.Main, TrackPlacement.ClockAligned), clock);
                return new TrackSet(clock, theClass, MicrophoneTrack(source, Track.Microphone, rule, settings, clock), freezes, loggers.CreateLogger<TrackSet>());

            case RecordingMode.Microphone:
                return new TrackSet(clock, classTrack: null, MicrophoneTrack(source, Track.Main, follows: null, settings, clock), freezes, loggers.CreateLogger<TrackSet>());

            default:
                throw new ArgumentOutOfRangeException(nameof(source), source.Mode, "Not a recording mode ClassScribe knows.");
        }
    }

    /// <summary>A session, not started yet, that records the class from what <paramref name="source"/> names, or the device the class plays on when it names none, writing as <paramref name="spec"/> says.</summary>
    private RecordingSession ClassSession(SourcePlan source, TrackSpec spec, RecordingClock? clock)
    {
        var chooser = new CaptureChooser(time, source.Class ?? new ClassSource.Automatic(), loggers.CreateLogger<CaptureChooser>(), video);
        return new RecordingSession(new LoopbackSource(loopback, chooser), spec, clock, options, time, loggers.CreateLogger<RecordingSession>());
    }

    /// <summary>
    /// Your microphone's track, not started yet: a session on <paramref name="track"/> over the microphone the plan or
    /// Recording:Microphone names (empty for Windows' default, never a Bluetooth one), with the processing the settings
    /// ask for; the gate for <paramref name="follows"/>, or for no app when null, armed at the start as the class app's
    /// mute allows; and the watch that feeds the gate, when an app is followed.
    /// </summary>
    private MicrophoneTrack MicrophoneTrack(SourcePlan source, Track track, FollowRule? follows, RecordingOptions settings, RecordingClock clock)
    {
        var hold = webcams?.Create(settings.WakeCameraMicrophone);
        var tap = new MicrophoneProviderTap(microphones, hold is null ? null : hold.BeforeOpen);
        var chooser = new MicrophoneChooser(source.Microphone?.NamePart ?? settings.Microphone, time, loggers.CreateLogger<MicrophoneChooser>());
        var microphoneSource = new MicrophoneSource(tap, chooser, settings.MicrophoneProcessing);
        var session = new RecordingSession(microphoneSource, new TrackSpec(track, TrackPlacement.ClockAligned), clock, options, time, loggers.CreateLogger<RecordingSession>());

        var rule = follows ?? new FollowRule.None();
        var gate = new MicrophoneGate(settings.ClassAppMute, rule, time, loggers.CreateLogger<MicrophoneGate>());
        var watch = rule is FollowRule.None
            ? null
            : new ClassMicrophoneWatch(sessions, usage, names, rule, own, time, loggers.CreateLogger<ClassMicrophoneWatch>());
        return new MicrophoneTrack(session, microphoneSource, tap, gate, watch, follows, hold);
    }

    /// <summary>
    /// Which app the microphone's track follows with the class: what the plan says when it says (--mic-follows, or menu
    /// choice 7); else the app the class is recorded from in app mode; else what Recording:MicrophoneFollows names. A
    /// setting that names nothing, which the settings check refuses, leaves V alone to switch the microphone.
    /// </summary>
    private FollowRule FollowRuleFor(SourcePlan source, RecordingOptions settings)
    {
        if (source.Microphone?.Follows is { } chosen) return chosen;
        if (source.Class is ClassSource.App app) return new FollowRule.Named(app.ProcessName);
        if (FollowRule.TryParse(settings.MicrophoneFollows, out var rule)) return rule;

        loggers.CreateLogger<TrackSetFactory>().LogWarning("Recording:MicrophoneFollows names no app, so the microphone is recorded whenever V is on");
        return new FollowRule.None();
    }
}
