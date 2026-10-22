using System.Runtime.ExceptionServices;
using ClassScribe.Audio.Microphones;
using ClassScribe.Audio.Timeline;
using ClassScribe.Audio.Windows;
using ClassScribe.Commands;
using ClassScribe.Recorder;
using ClassScribe.Storage;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Audio;

/// <summary>A capture of a microphone that was just opened: which microphone, and how Windows processes its audio.</summary>
/// <param name="Microphone">The microphone, as Windows lists it.</param>
/// <param name="Processing">The processing the capture ended up with: RAW, or Default when RAW was asked for but not available.</param>
public sealed record MicrophoneOpened(Microphone Microphone, MicrophoneProcessing Processing);

/// <summary>
/// The microphone provider as a recording sees it: everything passes through to the app's provider, and each capture
/// opened is noted, so that the recording knows which microphone its session records now and how, without a second
/// listing. The provider belongs to the app and is not disposed here.
/// </summary>
/// <param name="inner">The app's provider.</param>
/// <param name="beforeOpen">
/// Called before each capture is opened, with its microphone, on the thread that opens it: the webcam hold waits there for
/// the held camera's first frame (see <see cref="Camera.WebcamHold.BeforeOpen"/>); null for nothing to wait for.
/// </param>
public sealed class MicrophoneProviderTap(IMicrophoneProvider inner, Action<Microphone>? beforeOpen = null) : IMicrophoneProvider
{
    private readonly Lock _gate = new();
    private MicrophoneOpened? _last;

    /// <summary>Raised, on the thread that opens the capture, right after a capture of a microphone was opened.</summary>
    public event EventHandler<MicrophoneOpened>? Opened;

    /// <summary>The last capture opened: the microphone recorded now, and how; null before the first.</summary>
    public MicrophoneOpened? Last
    {
        get { lock (_gate) return _last; }
    }

    /// <inheritdoc />
    public event EventHandler? MicrophonesChanged
    {
        add => inner.MicrophonesChanged += value;
        remove => inner.MicrophonesChanged -= value;
    }

    /// <inheritdoc />
    public IReadOnlyList<Microphone> GetMicrophones() => inner.GetMicrophones();

    /// <summary>
    /// Opens the capture through the app's provider, after whatever must come first (a webcam's picture held, see the
    /// class), and notes which microphone it is of and what processing it got.
    /// </summary>
    public IAudioCapture CreateCapture(Microphone microphone, MicrophoneProcessing processing)
    {
        beforeOpen?.Invoke(microphone);
        var capture = inner.CreateCapture(microphone, processing);
        var used = processing;
        if (OperatingSystem.IsWindows() && capture is WasapiAudioCapture wasapi && wasapi.Processing is { } reported) used = reported;

        var opened = new MicrophoneOpened(microphone, used);
        lock (_gate) _last = opened;
        Opened?.Invoke(this, opened);
        return capture;
    }

    /// <inheritdoc />
    public bool? IsMuted(string id) => inner.IsMuted(id);

    /// <summary>Forgets the last capture opened: its track was left out of the run, so no microphone is recorded now.</summary>
    public void Forget()
    {
        lock (_gate) _last = null;
    }

    /// <summary>Nothing of its own to let go of: the provider belongs to the app.</summary>
    public void Dispose()
    {
    }
}

/// <summary>Your microphone's track as the track set owns it: its session with its source, the gate that decides its packets, and the watch that feeds the gate.</summary>
/// <param name="Session">The session that records the microphone: on its own track beside the class, or the main track in person.</param>
/// <param name="Source">The session's source, whose picks start and point the watch, and which is told which microphone the class app listens on.</param>
/// <param name="Tap">The provider the source opens captures through, which notes each one.</param>
/// <param name="Gate">The gate that decides which packets are written; it follows no app in person, and counts the time on and off with V.</param>
/// <param name="Watch">The watch that looks at the class app's microphone sessions and hands its looks to the gate; null when no app is followed.</param>
/// <param name="Follows">The app the track follows, as the recording was built to follow it; null in person.</param>
/// <param name="Webcam">
/// The webcam the picked microphone belongs to, which the hold keeps awake with Recording:WakeCameraMicrophone on (see
/// <see cref="Camera.WebcamHold"/>); null when no camera list is at hand, as for the tests that need none.
/// </param>
public sealed record MicrophoneTrack(RecordingSession Session, MicrophoneSource Source, MicrophoneProviderTap Tap, MicrophoneGate Gate,
    ClassMicrophoneWatch? Watch, FollowRule? Follows, Camera.WebcamHold? Webcam = null);

/// <summary>
/// A recording of the class and your microphone together, or of your microphone alone in person, as the record command
/// and the recording loop drive it. Every track follows one recording clock, so the tracks line up in the files
/// themselves: a position is time on that clock, a mark gets it, a pause and a time the laptop slept are left out of it,
/// and at the stop every track is padded to one position, so they end together. Each session has its own capture, files
/// and failure handling: a microphone that fails never stops the class, nor the other way round, and only when neither
/// track can be recorded does the start fail; a microphone whose start failed is left out of the run, and the recording
/// then describes no microphone. With the class, the microphone's packets go through the gate that follows
/// the class app: the watch starts on the microphone the session's first look picks, whether or not a capture is opened
/// (a track that starts switched off is still looked at), follows it when the recording moves to another, hands each
/// look to the gate, and tells the source which microphone the class app listens on, so that the recording moves there.
/// V switches the microphone off and on, and arms the gate with it. It owns its sessions and the watch.
/// </summary>
/// <remarks>
/// A lesson in person may record the camera's picture too (teach-video 6.6 to 6.8): its camera track starts once the sound's
/// tracks have started, is told of every pause, resume and cut (a cut only nudges it, taking no lock), and stops after the
/// sound's tracks have stopped, been padded to the end and flushed, at that same end, within its own 15 seconds; the sound
/// never waits on the camera before that, and a camera that fails costs the picture only.
/// </remarks>
/// <param name="clock">The clock every track is placed on, shared with nothing outside this recording.</param>
/// <param name="classTrack">The class's session, on the main track; null in person.</param>
/// <param name="microphone">Your microphone's track: beside the class, or the main track in person; null when the class is recorded alone, which a track set never is.</param>
/// <param name="freezes">Finds the times ClassScribe did not run, which are cut out of the clock.</param>
/// <param name="logger">The log.</param>
/// <param name="camera">
/// Starts the lesson's camera track into the recording's files at the run's offset, on the clock; null for a recording
/// without the camera's picture.
/// </param>
public sealed class TrackSet(RecordingClock clock, RecordingSession? classTrack, MicrophoneTrack? microphone, FreezeWatch freezes, ILogger<TrackSet> logger,
    Func<RecordingFiles, TimeSpan, RecordingClock, Camera.ICameraTrack>? camera = null)
    : IRecording, IMicrophoneRecording, ICameraRecording
{
    /// <summary>The lesson's camera track once started; null without a camera, or before the start.</summary>
    private Camera.ICameraTrack? _camera;

    /// <inheritdoc />
    public Camera.CameraTrackState? Camera => Volatile.Read(ref _camera)?.State;

    /// <inheritdoc />
    public void LetGoOfTheCamera(string why) => Volatile.Read(ref _camera)?.GiveUp(why);

    /// <summary>What a track that could not be started recorded: nothing.</summary>
    private static readonly RecordingResult Nothing = new(Folder: "", SegmentPaths: [], Duration: TimeSpan.Zero, HeardSound: false);

    private readonly RecordingSession _main = classTrack ?? microphone?.Session
        ?? throw new ArgumentException("A track set records the class and your microphone, or your microphone alone.", nameof(microphone));

    private readonly Lock _gate = new();

    /// <summary>Held for the whole of a stop, so that a second stop waits for the first and answers the same.</summary>
    private readonly Lock _stopping = new();

    /// <summary>The sessions whose start failed, which are left out of the run from then on.</summary>
    private readonly HashSet<RecordingSession> _failed = [];

    private RecordingSummary? _summary;
    private bool _started;
    private bool _stopRequested;
    private bool _watchRunning;

    /// <summary>The position on the clock now: what a mark gets, and where the status line says the recording is.</summary>
    public TimeSpan Position => clock.Now();

    /// <summary>The app the microphone's track follows, as the recording was built to follow it; null in person.</summary>
    public FollowRule? Follows => microphone?.Follows;

    /// <summary>The gate that decides the microphone's packets, whose state, words and numbers the run shows; null when the recording has no microphone, or its track was left out.</summary>
    public MicrophoneGate? Gate => MicrophoneLeftOut ? null : microphone?.Gate;

    /// <summary>The microphone recorded now, the one whose capture was opened last; null before any was opened, and once its track was left out.</summary>
    public Microphone? Microphone => microphone?.Tap.Last?.Microphone;

    /// <summary>
    /// The microphone the last look picked, which is recorded now or would be once V switches the track on; null before
    /// the first look, when the last look picked none, and once the track was left out.
    /// </summary>
    public Microphone? WouldRecord => MicrophoneLeftOut ? null : microphone?.Source.LastPick?.Chosen;

    /// <summary>
    /// Why that microphone, in the chooser's words at its last look, such as Windows' default microphone or the microphone
    /// the class app uses: for the one recorded now, or for the one the last look picked while none is recorded. Null
    /// when there is nothing to say: before the first look, or while the last look picked another than the one recorded
    /// now, and the run then leaves the reason out of its line.
    /// </summary>
    public string? MicrophoneWhy =>
        (Microphone ?? WouldRecord) is { } named && microphone?.Source.LastPick is { Chosen: { } chosen } pick && chosen.Id == named.Id ? pick.Why : null;

    /// <summary>How Windows processes the microphone's audio, as the last capture opened reports it; null before any was opened, and once its track was left out.</summary>
    public MicrophoneProcessing? Processing => microphone?.Tap.Last?.Processing;

    /// <summary>Whether the microphone recorded now is muted in Windows, read from Windows when asked; null while none is recorded, or when Windows cannot say.</summary>
    public bool? MutedInWindows => Microphone is { } recorded ? microphone!.Tap.IsMuted(recorded.Id) : null;

    /// <summary>
    /// The webcam the picked microphone belongs to, and its hold; null for a microphone of no webcam, when the recording
    /// has no camera list, and once the microphone's track was left out.
    /// </summary>
    public Camera.WebcamState? Webcam => MicrophoneLeftOut ? null : microphone?.Webcam?.State;

    /// <summary>The watch on the class app's microphone sessions; null when no app is followed. For the tests.</summary>
    internal ClassMicrophoneWatch? Watch => microphone?.Watch;

    /// <summary>Raised for something worth a note in the transcript, by either track's session.</summary>
    public event EventHandler<TimelineNote>? NoteWorthy
    {
        add
        {
            foreach (var session in Sessions) session.NoteWorthy += value;
        }
        remove
        {
            foreach (var session in Sessions) session.NoteWorthy -= value;
        }
    }

    /// <summary>True in person: the microphone is the main track, and there is no track beside it.</summary>
    private bool InPerson => classTrack is null;

    /// <summary>The microphone's own track beside the class; null in person, where the microphone is the main track.</summary>
    private RecordingSession? Second => classTrack is not null ? microphone?.Session : null;

    /// <summary>Every session of the recording: the main track's, and the microphone's beside the class.</summary>
    private IEnumerable<RecordingSession> Sessions
    {
        get
        {
            yield return _main;
            if (Second is { } second) yield return second;
        }
    }

    /// <summary>The sessions that are part of the run: those whose start did not fail.</summary>
    private IEnumerable<RecordingSession> Running
    {
        get
        {
            lock (_gate) return Sessions.Where(session => !_failed.Contains(session)).ToList();
        }
    }

    /// <summary>True once the microphone's session could not be started and its track was left out of the run.</summary>
    private bool MicrophoneLeftOut
    {
        get
        {
            if (microphone is not { } track) return false;
            lock (_gate) return _failed.Contains(track.Session);
        }
    }

    /// <summary>
    /// Each track to start, in order: its session, which track it writes, and the words for it and for the other track
    /// in the messages; the other is null in person, where there is no other.
    /// </summary>
    private IEnumerable<(RecordingSession Session, Track Track, string What, string? Other)> Tracks
    {
        get
        {
            if (classTrack is { } theClass) yield return (theClass, Track.Main, "The class", "the microphone");
            if (microphone is { } track) yield return (track.Session, InPerson ? Track.Main : Track.Microphone, "The microphone", InPerson ? null : "the class");
        }
    }

    /// <summary>
    /// Starts recording into <paramref name="files"/> at <paramref name="offset"/>: the length the recording already has
    /// on disk when this run continues it, zero for a new one. The clock counts on from the offset; each track carries on
    /// from its own length on disk and is padded up to the offset when it is shorter. Each session is started on its own:
    /// a track whose device is in a format ClassScribe cannot read is logged and left out, and the other carries on; only
    /// when every track fails does the start fail, with <see cref="NotSupportedException"/>. A microphone left out takes
    /// its watch with it, which its first look had started before the format was refused, and is described no more. A
    /// microphone whose gate starts disarmed (the class app's mute cannot be seen) starts switched off, for V to switch
    /// on; its session still looks, so the watch starts on the microphone it would record.
    /// </summary>
    public void Start(RecordingFiles files, TimeSpan offset)
    {
        lock (_gate)
        {
            if (_started) throw new InvalidOperationException("This recording has already been started.");
            _started = true;
        }

        clock.SetOffset(offset);
        freezes.Frozen += OnFrozen;
        foreach (var session in Sessions) session.SetFreezeLook(freezes.CheckNow);
        if (microphone is { } track)
        {
            track.Session.SetGate(track.Gate);
            if (!track.Gate.Armed) track.Session.SetEnabled(false);
            track.Source.Picked += OnMicrophonePicked;
            track.Tap.Opened += OnMicrophoneOpened;
            if (track.Watch is { } watch) watch.Looked += OnLooked;
        }

        var tracks = Tracks.ToList();
        var failures = 0;
        foreach (var (session, trackName, what, other) in tracks)
        {
            try
            {
                session.Start(files, LengthOnDisk(files, trackName, offset));
            }
            catch (NotSupportedException ex)
            {
                failures++;
                lock (_gate) _failed.Add(session);
                if (microphone is { } mic && ReferenceEquals(session, mic.Session)) LeaveOutMicrophone(mic);
                if (failures == tracks.Count)
                {
                    logger.LogError(ex, "{What} cannot be recorded: {Reason}", what, ex.Message);
                    throw;
                }

                // Deliberately not rethrown: this track's device is in a format ClassScribe cannot read, which stays that
                // way, but the other track can still be recorded, and a class is worth more than a missing microphone or
                // a missing class track. The track is left out of the run; the run fails only when no track can be recorded.
                logger.LogError(ex, "{What} cannot be recorded: {Reason}; {Other} is still being recorded", what, ex.Message, other);
            }
        }

        StartTheCamera(files, offset);
    }

    /// <summary>
    /// Starts the lesson's camera track, once the sound records, its piece from <paramref name="offset"/>. A camera track that
    /// cannot be started costs the picture only: the sound records on.
    /// </summary>
    private void StartTheCamera(RecordingFiles files, TimeSpan offset)
    {
        if (camera is null) return;

        try
        {
            var track = camera(files, offset, clock);
            Volatile.Write(ref _camera, track);
            if (clock.IsPaused) track.Pause();
        }
        catch (Exception ex)
        {
            // Deliberately not rethrown: the camera's picture is the lesson's extra, and its track failing to start must not
            // cost the sound, which is recording already; the line says why the picture is not recorded.
            logger.LogError(ex, "The camera's picture cannot be recorded: {Reason}; the sound records on", ex.Message);
        }
    }

    /// <summary>
    /// Stops recording and says what this run recorded. A last look at the class app's microphone sessions is made, so
    /// that the microphone's held packets are judged by it, and the watch is stopped; then the sessions stop in parallel,
    /// each padded to the one position taken from the clock, so the tracks end together, and each bounded by its own
    /// patience with a device change Windows holds up. The length is the longer track's; something audible on either
    /// track counts as heard. Safe to call twice: the second call waits for the first and answers the same.
    /// </summary>
    public RecordingSummary Stop()
    {
        lock (_stopping)
        {
            lock (_gate)
            {
                if (_summary is not null) return _summary;
                _stopRequested = true;
            }

            LastLook();
            StopWatch();
            var end = clock.Now();
            var (main, second) = StopTracks(end);
            freezes.Frozen -= OnFrozen;

            // The sound is stopped, padded to the end and flushed first; then the camera has its time, at most its 15
            // seconds, to end its piece at that same end.
            StopTheCamera(end);

            // The webcam's picture is held until the microphone has stopped, and let go before anything is uploaded.
            microphone?.Webcam?.Stop();

            var length = second is null || second.Duration <= main.Duration ? main.Duration : second.Duration;
            var summary = new RecordingSummary(length, main.SegmentPaths, second?.SegmentPaths ?? [], main.HeardSound || second is { HeardSound: true });
            lock (_gate) _summary = summary;

            if (!Running.Any())
            {
                logger.LogDebug("No track of this run could be started, so there is nothing to sum up");
                return summary;
            }

            // The parts are the main track's, as the transcript counts them; the microphone's files beside the class are named apart.
            var parts = Count(main.SegmentPaths.Count, "part");
            if (second is { SegmentPaths.Count: > 0 } files) parts += $" and {Count(files.SegmentPaths.Count, "microphone file")}";
            var corrections = main.Corrections + (second?.Corrections ?? 0);
            logger.LogInformation("Recorded {Length} in this run: {Paused} paused, {Cut} left out while the laptop slept or ClassScribe was held up, {Parts}, {Corrections} for drift",
                Durations.Format(length), Durations.Format(clock.TimePaused), Durations.Format(clock.TimeCut), parts, Count(corrections, "correction"));
            return summary;
        }
    }

    /// <summary>
    /// Pauses the clock and every track: positions stay where they are, packets stamped meanwhile are dropped, and the files
    /// are brought up to date; the camera's picture leaves the pause out too, and its camera is let go, so its light goes off.
    /// </summary>
    public void Pause()
    {
        clock.Pause();
        foreach (var session in Running) session.Pause();
        Volatile.Read(ref _camera)?.Pause();
    }

    /// <summary>Records again after <see cref="Pause"/>: the pause becomes a stretch left out of the clock, and positions carry on from where they stopped; the camera is taken again.</summary>
    public void Resume()
    {
        clock.Resume();
        foreach (var session in Running) session.Resume();
        Volatile.Read(ref _camera)?.Resume();
    }

    /// <summary>
    /// Stops the camera's picture at <paramref name="end"/>, where the sound ends, waiting at most its track's own limit; a
    /// camera that fails to stop costs its picture's last moments only.
    /// </summary>
    private void StopTheCamera(TimeSpan end)
    {
        if (Volatile.Read(ref _camera) is not { } track) return;

        try
        {
            track.StopAsync(end, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Deliberately not rethrown: the sound is stopped and saved already; a camera that fails at its stop costs its piece's
            // last moments only, which the line says, and the run goes on to its transcript.
            logger.LogError(ex, "The camera's picture could not be stopped cleanly: {Reason}; its piece is kept as far as it was written", ex.Message);
        }
    }

    /// <summary>
    /// Switches your microphone off or on (V): off releases its capture and disarms the gate, keeping the track's place
    /// on the clock; on makes a fresh look at the class app's sessions first, so the first packets are judged by it,
    /// arms the gate and opens the microphone again. In person the microphone is the main track, and V switches that.
    /// Returns whether it is on now, or null when the recording has no microphone, or its track was left out of the run.
    /// </summary>
    public bool? SetMicrophone(bool on)
    {
        if (microphone is not { } track || MicrophoneLeftOut) return null;

        if (on)
        {
            LastLook();
            track.Gate.Arm(true);
            track.Session.SetEnabled(true);
        }
        else
        {
            track.Session.SetEnabled(false);
            track.Gate.Arm(false);
        }

        return on;
    }

    /// <summary>
    /// The recording as its sessions see it now: the position and the pause from the clock, the main track, and the
    /// microphone's track beside the class, which is left out once its start failed: a track that is not recorded has
    /// nothing to wait for, and nothing to say.
    /// </summary>
    public RecordingSnapshot GetSnapshot()
    {
        var main = Snapshot(_main.GetStatus());
        var second = Second is { } microphoneTrack && !MicrophoneLeftOut ? Snapshot(microphoneTrack.GetStatus()) : null;
        return new RecordingSnapshot(clock.Now(), clock.IsPaused, main, second);
    }

    /// <summary>
    /// Has the capture of <paramref name="track"/> replaced at its session's next look (see <see cref="RecordingSession.Reopen"/>).
    /// In person there is no microphone track beside the main one, so a reopen of it changes nothing, which the log notes.
    /// </summary>
    public void Reopen(Track track, string reason)
    {
        var session = track == Track.Main ? _main : Second;
        if (session is null)
        {
            logger.LogDebug("This recording has no microphone track to reopen ({Reason})", reason);
            return;
        }

        session.Reopen(reason);
    }

    /// <summary>Brings every track's file on disk up to date now, as before the process ends. Every track is tried; the first failure is logged and handed on.</summary>
    public void Flush()
    {
        Exception? failure = null;
        foreach (var session in Running)
        {
            try
            {
                session.Flush();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Kept for after the other track has been flushed, then rethrown as it was: a class track that cannot be
                // saved must not keep the microphone's from being saved, nor the other way round.
                failure ??= ex;
            }
        }

        if (failure is null) return;

        logger.LogError(failure, "Could not bring every track on disk up to date; what was saved is kept");
        ExceptionDispatchInfo.Throw(failure);
    }

    /// <summary>Stops the recording if it still runs, and lets go of the watch and the sessions, each with its source.</summary>
    public void Dispose()
    {
        try
        {
            bool stop;
            lock (_gate) stop = _started && _summary is null;
            if (stop) Stop();
        }
        finally
        {
            freezes.Frozen -= OnFrozen;
            Volatile.Read(ref _camera)?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            if (microphone is { } track)
            {
                track.Source.Picked -= OnMicrophonePicked;
                track.Tap.Opened -= OnMicrophoneOpened;
                if (track.Watch is { } watch)
                {
                    watch.Looked -= OnLooked;
                    watch.Dispose();
                }

                track.Webcam?.Dispose();
            }

            foreach (var session in Sessions)
            {
                bool failed;
                lock (_gate) failed = _failed.Contains(session);

                // A session whose start failed is not disposed: disposing it would stop it, and the stop would pad its
                // never-written track up to the clock. Only its source is let go of; the class's source holds nothing.
                if (!failed) session.Dispose();
                else if (microphone is { } mic && ReferenceEquals(session, mic.Session)) mic.Source.Dispose();
            }
        }
    }

    /// <summary>How much <paramref name="track"/> already holds on disk when the run continues a recording at <paramref name="offset"/>; zero for a new recording.</summary>
    private static TimeSpan LengthOnDisk(RecordingFiles files, Track track, TimeSpan offset) =>
        offset > TimeSpan.Zero ? files.FindSegments(track).Aggregate(TimeSpan.Zero, (length, segment) => length + ClassFolders.MeasureSegment(segment)) : TimeSpan.Zero;

    /// <summary>Stops the running sessions in parallel, each padded to <paramref name="end"/>, and returns what each recorded: the main track's, and the track beside it when there is one.</summary>
    private (RecordingResult Main, RecordingResult? Second) StopTracks(TimeSpan end)
    {
        var second = Second;
        var mainStop = Task.Run(() => StopTrack(_main, end));
        var secondStop = second is null ? null : Task.Run(() => StopTrack(second, end));

        // Both stops run to their ends before the first failure, as it was thrown, is handed on.
        Task all = secondStop is null ? mainStop : Task.WhenAll(mainStop, secondStop);
        all.GetAwaiter().GetResult();
        return (mainStop.Result, secondStop?.Result);
    }

    /// <summary>Stops <paramref name="session"/> at <paramref name="end"/>; a session whose start failed recorded nothing and is left alone.</summary>
    private RecordingResult StopTrack(RecordingSession session, TimeSpan end)
    {
        bool failed;
        lock (_gate) failed = _failed.Contains(session);
        return failed ? Nothing : session.Stop(end);
    }

    /// <summary>
    /// A last look at the class app's microphone sessions, on this thread, while the watch runs: before V switches the
    /// microphone on, so the first packets are judged by a fresh look, and at the stop, so the held packets are judged.
    /// </summary>
    private void LastLook()
    {
        ClassMicrophoneWatch? watch;
        lock (_gate) watch = _watchRunning ? microphone?.Watch : null;
        if (watch is null) return;

        try
        {
            watch.LookNow();
        }
        catch (Exception ex)
        {
            // Deliberately not rethrown: a look that fails here must keep neither V from switching the microphone nor the
            // stop from finishing the tracks. The gate judges the held packets by the looks it has, and the watch's own
            // thread, which makes the same look four times a second, reports a reader that fails and stops.
            logger.LogWarning(ex, "Could not look at the class app's microphone sessions now; the looks so far decide");
        }
    }

    /// <summary>
    /// Leaves the microphone's track out of the run after its start failed: its first look had picked a microphone,
    /// which started the watch, and its capture had opened before the format was refused. The watch no longer feeds the
    /// gate and is stopped, a pick or a capture later is no longer noted, and the capture noted is forgotten, so that
    /// the recording describes no microphone from here on.
    /// </summary>
    private void LeaveOutMicrophone(MicrophoneTrack track)
    {
        track.Source.Picked -= OnMicrophonePicked;
        track.Tap.Opened -= OnMicrophoneOpened;
        if (track.Watch is { } watch) watch.Looked -= OnLooked;
        StopWatch();
        track.Tap.Forget();
        track.Webcam?.Stop();
    }

    /// <summary>Stops the watch's thread, if it runs.</summary>
    private void StopWatch()
    {
        ClassMicrophoneWatch? watch;
        lock (_gate)
        {
            watch = _watchRunning ? microphone?.Watch : null;
            _watchRunning = false;
        }

        watch?.Stop();
    }

    /// <summary>
    /// A look of the microphone's session picked a microphone, opened or not (the track may be switched off): the watch
    /// starts on it, or follows the recording to it when the pick moved, so that the class app's sessions on the
    /// microphone that is or would be recorded are watched from the first look on. Runs on the thread that looked, the
    /// session's start or a thread pool's.
    /// </summary>
    private void OnMicrophonePicked(object? sender, MicrophonePick pick)
    {
        if (pick.Chosen is not { } chosen) return;

        FollowWithTheWatch(chosen.Id);

        // The webcam's hold follows the pick too, before the capture opens, so that the camera is awake when it does.
        microphone?.Webcam?.Follow(chosen);
    }

    /// <summary>
    /// A capture of a microphone was opened: the watch follows the recording to it, when it is not the one the pick
    /// pointed it at already. Runs on the thread that opened the capture, the session's start or its look.
    /// </summary>
    private void OnMicrophoneOpened(object? sender, MicrophoneOpened opened) => FollowWithTheWatch(opened.Microphone.Id);

    /// <summary>Starts the watch on the microphone <paramref name="endpointId"/>, or points it there when it runs on another; nothing once a stop was asked for.</summary>
    private void FollowWithTheWatch(string endpointId)
    {
        if (microphone?.Watch is not { } watch) return;

        lock (_gate)
        {
            if (_stopRequested) return;
            if (!_watchRunning)
            {
                watch.Start(endpointId);
                _watchRunning = true;
            }
            else if (!string.Equals(watch.EndpointId, endpointId, StringComparison.OrdinalIgnoreCase))
            {
                watch.Follow(endpointId);
            }
        }
    }

    /// <summary>
    /// A look of the watch: the gate takes it, and the source is told which microphone the class app listens on, when it
    /// is another than the recorded one, so that the recording moves there. Runs on the watch's thread, or on the thread
    /// of a last look.
    /// </summary>
    private void OnLooked(object? sender, WatchLook look)
    {
        if (microphone is not { } track) return;

        track.Gate.Record(look);
        track.Source.FollowApp(look.OtherMicrophoneId);
    }

    /// <summary>
    /// A time ClassScribe did not run was found: it is cut out of the clock, so no track is padded across it. Runs while
    /// the tick or the look that found it holds the watch, so it only cuts and logs, and never calls into a session, whose
    /// capture thread may be waiting for that look.
    /// </summary>
    private void OnFrozen(object? sender, Freeze freeze)
    {
        clock.Cut(freeze);

        // The camera's track hears of the cut at once; the nudge takes no lock and never calls into a session.
        Volatile.Read(ref _camera)?.Nudge();
        logger.LogDebug("Cut {Seconds} s out of the recording clock: the laptop {What} from {From:HH:mm:ss} to {To:HH:mm:ss}",
            (freeze.ToUtc - freeze.FromUtc).TotalSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture),
            freeze.Slept ? "slept" : "held ClassScribe up", freeze.FromUtc.ToLocalTime(), freeze.ToUtc.ToLocalTime());
    }

    /// <summary>A session's status as a track of the snapshot.</summary>
    private static TrackSnapshot Snapshot(RecordingStatus status) => new(
        Source: status.DeviceName,
        WaitingFor: status.WaitingFor,
        Written: status.Captured,
        SinceLastSound: status.SinceLastSound,
        SinceLastPacket: status.SinceLastPacket,
        DeliversContinuously: status.DeliversContinuously,
        RecordedLevel: status.RecordedLevel,
        SoundWhilePaused: status.SoundWhilePaused,
        Capturing: status.WaitingFor is null && !status.Off,
        Off: status.Off,
        WriteFailing: status.WriteFailing,
        OnlyZeros: status.OnlyZeros,
        HeardSound: status.HeardSound,
        RawZerosFor: status.RawZerosFor,
        RawSoundSinceOpened: status.RawSoundSinceOpened,
        WindowsSilent: status.WindowsSilent,
        CaptureNumber: status.CaptureNumber);

    /// <summary>A count with its noun: 1 part, 3 parts, 0 corrections.</summary>
    private static string Count(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";
}
