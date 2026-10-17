using ClassScribe.Commands;
using ClassScribe.Settings;
using ClassScribe.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClassScribe.Audio;

/// <summary>
/// A recording of the class alone, as the record command and the recording loop drive it: one session, which writes the
/// class's audio as it arrives and skips the time when nothing plays, as ClassScribe has always recorded. Its position is
/// the length the recording had when this run started it, when continuing one, plus what this run has written; a mark
/// set now gets it. It owns its session.
/// </summary>
public sealed class ClassRecording(RecordingSession session, ILogger<ClassRecording> logger) : IRecording
{
    private readonly Lock _gate = new();
    private TimeSpan _offset;
    private bool _started;

    /// <summary>The offset the recording was started at plus what this run has written.</summary>
    public TimeSpan Position
    {
        get
        {
            var written = session.GetStatus().Captured;
            lock (_gate) return _offset + written;
        }
    }

    /// <summary>
    /// Never raised yet: a recording of the class alone has nothing of its own to note. A long stretch with nothing playing,
    /// left out of the recording, is to be noted from a later build on.
    /// </summary>
    public event EventHandler<TimelineNote>? NoteWorthy
    {
        add { }
        remove { }
    }

    /// <summary>
    /// Starts recording into <paramref name="files"/>, its next free part when the recording has parts already, at
    /// <paramref name="offset"/>: the length the recording has on disk when this run continues it, zero for a new one. A
    /// recording is started once; a device in a format ClassScribe cannot read makes the start fail.
    /// </summary>
    public void Start(RecordingFiles files, TimeSpan offset)
    {
        lock (_gate)
        {
            if (_started) throw new InvalidOperationException("This recording has already been started.");
            _started = true;
            _offset = offset;
        }

        session.Start(files);
    }

    /// <summary>
    /// Stops recording and says what this run recorded: its length, without the earlier parts of a recording it continued,
    /// its files, no microphone files, and whether any sound reached them.
    /// </summary>
    public RecordingSummary Stop()
    {
        var result = session.Stop();
        return new RecordingSummary(result.Duration, result.SegmentPaths, MicrophoneSegments: [], result.HeardSound);
    }

    /// <inheritdoc />
    public void Pause() => session.Pause();

    /// <inheritdoc />
    public void Resume() => session.Resume();

    /// <summary>The class track as its session sees it now; there is no microphone track.</summary>
    public RecordingSnapshot GetSnapshot()
    {
        var status = session.GetStatus();
        TimeSpan offset;
        lock (_gate) offset = _offset;

        var main = new TrackSnapshot(
            Source: status.DeviceName,
            WaitingFor: status.WaitingFor,
            Written: status.Captured,
            SinceLastSound: status.SinceLastSound,
            SinceLastPacket: status.SinceLastPacket,
            DeliversContinuously: status.DeliversContinuously,
            RecordedLevel: status.RecordedLevel,
            SoundWhilePaused: status.SoundWhilePaused,
            Capturing: status.WaitingFor is null,
            Off: false,
            WriteFailing: status.WriteFailing,
            OnlyZeros: false);
        return new RecordingSnapshot(offset + status.Captured, status.Paused, main, Microphone: null);
    }

    /// <summary>
    /// Has the class's capture replaced at the session's next look (see <see cref="RecordingSession.Reopen"/>). There is no
    /// microphone track, so a reopen of one changes nothing; the log notes it.
    /// </summary>
    public void Reopen(Track track, string reason)
    {
        if (track != Track.Main)
        {
            logger.LogDebug("This recording has no microphone track to reopen ({Reason})", reason);
            return;
        }

        session.Reopen(reason);
    }

    /// <inheritdoc />
    public void Flush() => session.Flush();

    /// <summary>Stops the recording if it still runs, and lets go of its session.</summary>
    public void Dispose() => session.Dispose();
}

/// <summary>
/// Makes recordings of the class alone, from a run's source plan: a session on what the computer plays, which records the
/// device the class plays on, one device by part of its name, or one app, as the plan's class source names it. Recording
/// the microphone comes with a later build, so a plan that asks for it is refused. The settings are those of the run's
/// own scope.
/// </summary>
public sealed class ClassRecordingFactory(
    ILoopbackCaptureProvider provider,
    IOptionsSnapshot<RecordingOptions> options,
    TimeProvider time,
    ILogger<RecordingSession> sessionLogger,
    ILogger<CaptureChooser> chooserLogger,
    ILogger<ClassRecording> logger) : IRecordingFactory
{
    /// <summary>
    /// A recording, not started yet, of the class from what <paramref name="source"/> names (see
    /// <see cref="CreateSession"/>). A plan with the microphone is refused with a <see cref="NotSupportedException"/>.
    /// </summary>
    public IRecording Create(SourcePlan source) => new ClassRecording(CreateSession(source), logger);

    /// <summary>
    /// A session, not started yet, that records the class from what <paramref name="source"/> names: its class source, or
    /// the device the class plays on when it names none. Nothing is looked at or opened before the session starts. A plan
    /// with the microphone, with the class or in person, is refused with a <see cref="NotSupportedException"/>.
    /// </summary>
    public RecordingSession CreateSession(SourcePlan source)
    {
        if (source.Mode != RecordingMode.Class)
        {
            logger.LogError("Cannot record {Mode}: this build of ClassScribe records the class alone", source.Mode);
            throw new NotSupportedException(
                "This build of ClassScribe records the class alone: recording your microphone, with the class or in person, comes with a later build.");
        }

        var chooser = new CaptureChooser(time, source.Class ?? new ClassSource.Automatic(), chooserLogger);
        return new RecordingSession(new LoopbackSource(provider, chooser), options, time, sessionLogger);
    }
}
