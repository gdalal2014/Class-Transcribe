using System.Globalization;
using ClassScribe.Audio.Microphones;
using ClassScribe.Audio.Timeline;
using ClassScribe.Recorder;
using ClassScribe.Settings;
using ClassScribe.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClassScribe.Audio;

/// <summary>
/// Records what its source picks into WAV segment files: for the class, the output device it plays through, the one
/// chosen by name, or one app's sound (see <see cref="LoopbackSource"/> and <see cref="CaptureChooser"/>); for your
/// microphone, the one chosen (see <see cref="MicrophoneSource"/>). The header of the open segment is rewritten every few
/// seconds, so a crash keeps the audio. A new segment is started when the recording moves to another device, when the
/// capture or the disk fails, or when a segment grows too large. The status also shows whether audio keeps arriving
/// (see <see cref="GetStatus"/>), so that a capture that stays open but no longer delivers, which raises no error of its
/// own, can be found and replaced (see <see cref="Reopen"/>). While paused nothing is written, but the session still
/// looks at what can be recorded, follows a move to another device, and measures the sound that arrives, so that a class
/// playing on during a pause shows in the status.
/// </summary>
/// <remarks>
/// <para>Two locks, never taken in the other order: the lifecycle semaphore allows one start, stop or restart at a time,
/// and the gate guards the state the capture thread shares. A capture is never released while the gate is held, because
/// releasing it waits for the capture thread. Every interval is measured on the steady clock
/// (<see cref="TimeProvider.GetTimestamp"/>), so Windows setting its time of day neither stretches nor shortens one.</para>
/// <para>The session looks at what can be recorded every <see cref="ICaptureSource.CheckInterval"/> by itself, and at once
/// when the source says something has changed. Windows announces some changes; the regular look catches the rest, such as
/// a browser sent to another device in the per-app sound settings, which no announcement covers. The same look opens a
/// capture that could not be opened before and looks again while there is nothing to record, so it is the one way
/// anything is tried again: no failure schedules a retry of its own. The messages say how often that is.</para>
/// <para>A track placed <see cref="TrackPlacement.Contiguous"/> appends packets as they come, and silence while nothing
/// arrives is not written. A track placed <see cref="TrackPlacement.ClockAligned"/> puts each packet where its stamp
/// falls on the recording clock the run's tracks share (<see cref="RecordingClock"/>, <see cref="TrackPlacer"/>): a
/// timer fills what nothing arrives for with silence, a second at a time and never more than a second in one write, so
/// the files stay on the clock whatever happens; a packet stamped in a pause or a time the laptop slept is dropped; a
/// packet far ahead of its track asks for a look for such a time first (see <see cref="SetFreezeLook"/>); and the
/// microphone's packets can be held for the gate that follows the class app to decide them (see <see cref="SetGate"/>).
/// The session owns its source, and disposes it.</para>
/// </remarks>
/// <param name="source">Where the audio comes from: what can be recorded, and how to open a capture of it.</param>
/// <param name="spec">Which track is written and how its packets are placed.</param>
/// <param name="clock">The recording clock, for a track placed on it; not needed for a contiguous track.</param>
/// <param name="options">The run's recording settings.</param>
/// <param name="time">The clock every interval is measured on.</param>
/// <param name="logger">The log.</param>
public sealed class RecordingSession(
    ICaptureSource source,
    TrackSpec spec,
    RecordingClock? clock,
    IOptionsSnapshot<RecordingOptions> options,
    TimeProvider time,
    ILogger<RecordingSession> logger) : IDisposable
{
    /// <summary>A session that records the class alone on its main track, appending packets as they come, as ClassScribe has always recorded.</summary>
    public RecordingSession(
        ICaptureSource source,
        IOptionsSnapshot<RecordingOptions> options,
        TimeProvider time,
        ILogger<RecordingSession> logger)
        : this(source, new TrackSpec(Track.Main, TrackPlacement.Contiguous), null, options, time, logger)
    {
    }

    /// <summary>
    /// A session that records what the computer plays, as <paramref name="chooser"/> picks it from what
    /// <paramref name="provider"/> lists. Kept for the record command's tests, which still make their session this way;
    /// to be removed once they make it from a capture source.
    /// </summary>
    public RecordingSession(
        ILoopbackCaptureProvider provider,
        CaptureChooser chooser,
        IOptionsSnapshot<RecordingOptions> options,
        TimeProvider time,
        ILogger<RecordingSession> logger)
        : this(new LoopbackSource(provider, chooser), options, time, logger)
    {
    }

    /// <summary>Samples at or above this level (out of 32767) count as audible sound.</summary>
    private const int SoundThreshold = 100;

    /// <summary>The sample rate of silence written to keep a track on the clock before any capture has opened on it: 16 kHz, the upload's own rate.</summary>
    private const int PaddingRate = 16_000;

    /// <summary>How often the timer fills what nothing has arrived for on a track that follows the clock (see <see cref="PadToClock"/>).</summary>
    public static readonly TimeSpan PaddingInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The longest a packet is held for the gate to decide it; a packet held longer is dropped, as the gate itself drops a
    /// packet it cannot decide within a second, so that the held packets never grow whatever the gate does.
    /// </summary>
    public static readonly TimeSpan LongestHold = TimeSpan.FromSeconds(2);

    /// <summary>After this long with nothing but exact zeros from the open capture, the status says so (see <see cref="RecordingStatus.OnlyZeros"/>).</summary>
    public static readonly TimeSpan OnlyZerosAfter = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The webcam zeros rule at the start (camera-first 3.4): a webcam's microphone that has handed over only exact zeros,
    /// at its own depth, for this long since its capture opened. Awake, the OBSBOT's zeros after an open lasted 4 seconds
    /// (finding 4), so a quiet start has room; a camera asleep when the class begins sends zeros from the first packet.
    /// </summary>
    public static readonly TimeSpan WebcamZerosAtStart = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The webcam zeros rule after sound: only exact zeros for this long after the microphone has carried sound. A quiet
    /// room's runs were at most 18.7 seconds in 12 minutes (finding 4); a camera that falls asleep stays silent to the end.
    /// </summary>
    public static readonly TimeSpan WebcamZerosAfterSound = TimeSpan.FromMinutes(2);

    /// <summary>The least time between two replacements of a capture that keeps ending.</summary>
    private static readonly TimeSpan RestartSpacing = TimeSpan.FromSeconds(2);

    /// <summary>When writing fails twice in a row, audio is dropped for this long before a new segment file is tried.</summary>
    private static readonly TimeSpan WriteRetryDelay = TimeSpan.FromSeconds(30);

    /// <summary>
    /// While paused, sound counts as still arriving until this long after the last audible packet, so that the short quiet
    /// between two sentences does not end it (see <see cref="RecordingStatus.SoundWhilePaused"/>).
    /// </summary>
    private static readonly TimeSpan PausedSoundGap = TimeSpan.FromSeconds(5);

    /// <summary>While captures keep failing, or there is still nothing to record, that is said at most this often.</summary>
    private static readonly TimeSpan ReportInterval = TimeSpan.FromSeconds(30);

    /// <summary>How long <see cref="Stop()"/> waits for a device change in progress before it stops without it (see <see cref="StopWithoutWaiting"/>).</summary>
    private static readonly TimeSpan StopWaitLimit = TimeSpan.FromSeconds(20);

    private readonly RecordingOptions _options = options.Value;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly List<string> _segments = [];

    /// <summary>The recording clock, for a track placed on it; null for a contiguous track, which needs none.</summary>
    private readonly RecordingClock? _clock = spec.Placement == TrackPlacement.ClockAligned
        ? clock ?? throw new ArgumentException("A track that follows the recording clock needs the clock.", nameof(clock))
        : null;

    /// <summary>The packets held for the gate to decide, oldest first (see <see cref="SetGate"/>).</summary>
    private readonly List<HeldPacket> _held = [];

    private IAudioCapture? _capture;
    private IAudioCapture? _leftBehind;
    private ITimer? _deviceCheck;
    private ITimer? _padding;
    private bool _captureEnded;
    private WavSegmentWriter? _writer;
    private RecordingResult? _result;
    private byte[] _convertBuffer = [];
    private byte[] _zeros = [];
    private RecordingFiles? _files;
    private string _folder = "";
    private string _deviceName = "";
    private TimeSpan _closedDuration;
    private long _lastFlush;
    private long _lastSound;
    private long? _captureSound;
    private long? _writePauseStarted;
    private int _writeFailures;
    private long? _lastEndedRestart;
    private long? _lastReport;
    private bool _started;
    private bool _paused;
    private volatile bool _stopping;
    private int _restartQueued;

    /// <summary>Keeps the track on the clock, for a track placed on it; made at <see cref="Start(RecordingFiles, TimeSpan)"/>, null for a contiguous track.</summary>
    private TrackPlacer? _placer;

    /// <summary>Decides which packets are written, when one is set (see <see cref="SetGate"/>); null writes every packet at once.</summary>
    private IPacketGate? _packetGate;

    /// <summary>Looks for a time ClassScribe did not run, when a packet or the timer finds the track far behind the clock (see <see cref="SetFreezeLook"/>).</summary>
    private Func<Freeze?>? _freezeLook;

    /// <summary>False while switched off with V: no capture is open, and none is opened.</summary>
    private bool _enabled = true;

    /// <summary>The last capture's sample rate, at which silence is written while no part is open; zero before any capture.</summary>
    private int _lastRate;

    /// <summary>The frames of the open part reported to the placer, which the part's length on disk is checked against when it closes.</summary>
    private long _partFrames;

    /// <summary>How many of the microphone's packets the gate dropped, for the log at the stop.</summary>
    private int _droppedByGate;

    /// <summary>Whether the padding timer has failed, which is logged as an error once and later only for diagnosis.</summary>
    private bool _paddingFailed;

    /// <summary>For a contiguous track that notes gaps: where the last stamped packet ended, on the steady clock in 100-nanosecond units; null when nothing to count from.</summary>
    private long? _lastPacketEnd;

    /// <summary>The capture <see cref="Reopen"/> asked to replace at the next look, until that is done.</summary>
    private IAudioCapture? _reopen;

    /// <summary>Why <see cref="_reopen"/> is to be replaced, for the log.</summary>
    private string? _reopenReason;

    /// <summary>When the open capture last handed over audio, or when it was opened if it has not yet, on the steady clock.</summary>
    private long _lastPacket;

    /// <summary>When the open capture was opened, on the steady clock.</summary>
    private long _captureOpened;

    /// <summary>True once the open capture has handed over a packet.</summary>
    private bool _anyPacket;

    /// <summary>When the open capture last handed over a sample other than an exact zero, on the steady clock; null while it has handed over only zeros.</summary>
    private long? _lastNonZero;

    /// <summary>When the open capture last handed over a sample other than zero at the device's own depth, before the conversion; null while none.</summary>
    private long? _lastRawNonZero;

    /// <summary>Whether Windows flagged the open capture's last packet as silent.</summary>
    private bool _lastPacketSilent;

    /// <summary>How many captures this session has opened.</summary>
    private int _captureCount;

    /// <summary>Whether the open capture hands over audio all the time (see <see cref="IAudioCapture.DeliversContinuously"/>).</summary>
    private bool _deliversContinuously;

    /// <summary>How loud Windows' meter showed what is recorded at the last look, or null when that look read no level.</summary>
    private float? _recordedLevel;

    /// <summary>When audible sound last arrived during the pause under way, on the steady clock; null when none has since it began.</summary>
    private long? _pausedSound;

    /// <summary>True once audible sound has been written to the segment files.</summary>
    private bool _heardSound;

    /// <summary>True from a write to the segment files that failed until one that worked.</summary>
    private bool _writeFailing;

    /// <summary>
    /// Raised, on the capture thread, for something worth a note in the transcript: on a contiguous track, a stretch of at
    /// least the track spec's <see cref="TrackSpec.NoteGapsLongerThan"/> with nothing arriving, which the files leave out.
    /// The note's position is what this session has written so far, as <see cref="RecordingStatus.Captured"/> counts it;
    /// whoever continues a recording adds its offset.
    /// </summary>
    public event EventHandler<TimelineNote>? NoteWorthy;

    private long MaxSegmentBytes => Math.Max(1, _options.MaxSegmentMegabytes) * 1024L * 1024L;

    private TimeSpan FlushInterval => TimeSpan.FromSeconds(Math.Max(1, _options.FlushSeconds));

    /// <summary>How often the session tries again, in words for the log, such as "every second" or "every 10 seconds".</summary>
    private string EveryLook => source.CheckInterval == TimeSpan.FromSeconds(1)
        ? "every second"
        : string.Create(CultureInfo.InvariantCulture, $"every {source.CheckInterval.TotalSeconds:0.#} seconds");

    /// <summary>Starts capturing into the segment files of <paramref name="files"/>, from the next free part on, for a track that had nothing on disk before (see <see cref="Start(RecordingFiles, TimeSpan)"/>).</summary>
    public void Start(RecordingFiles files) => Start(files, TimeSpan.Zero);

    /// <summary>
    /// Starts capturing into the segment files of <paramref name="files"/>, from the next free part on. When there is
    /// nothing to record yet, or the capture cannot be opened yet, the recording waits and the regular look keeps trying;
    /// only a device in a format this app cannot read makes the start fail. <paramref name="existingLength"/> is how
    /// much this track already holds on disk when the run continues a recording, zero for a new one: a track on the
    /// clock carries on from there, and is padded up to the clock's offset when it is shorter.
    /// </summary>
    public void Start(RecordingFiles files, TimeSpan existingLength)
    {
        _lifecycle.Wait();
        try
        {
            if (_started)
                throw new InvalidOperationException("This recording session has already been started.");

            _files = files;
            _folder = files.Folder;
            Directory.CreateDirectory(files.Folder);
            _lastSound = time.GetTimestamp();
            bool enabled;
            lock (_gate)
            {
                // Started and the switch read together: a V before this point only set the switch (see SetEnabled).
                _started = true;
                if (_clock is { } recordingClock) _placer = new TrackPlacer(recordingClock, time, existingLength);
                enabled = _enabled;
            }

            source.Changed += OnDevicesChanged;
            try
            {
                if (!enabled)
                {
                    // Switched off before the start: nothing is opened until V, but the look is made all the same, so that
                    // whoever follows the source's pick (the watch on the class app's microphone sessions) has a microphone
                    // to follow from the start, and the pick is fresh when V switches the track on.
                    logger.LogInformation("{What} starts switched off (V switches it on)", Capitalised(source.Wording.Thing));
                    Look(recordedId: null, sinceRecordedSound: null);
                }
                else if (Look(recordedId: null, sinceRecordedSound: null) is { } target)
                {
                    StartCapture(target);
                }
                else
                {
                    // Nothing to record yet: a chosen device not connected, a chosen app not playing, or no
                    // device at all. The regular look keeps looking and starts the capture once it is there.
                    _lastReport = time.GetTimestamp();
                    logger.LogWarning("Nothing to record yet; waiting for {What}", source.WaitingFor);
                }
            }
            catch (NotSupportedException ex)
            {
                // A device in a format this app cannot read stays that way, so the run ends now rather than
                // waiting for something that will not happen.
                logger.LogError(ex, "Could not start capturing {What}", source.Wording.Capturing);
                source.Changed -= OnDevicesChanged;
                throw;
            }
            catch (Exception ex)
            {
                // Deliberately not rethrown: Windows may still be setting up a device that was just connected.
                // The regular look tries again, as it does when this happens later in the class.
                _lastReport = time.GetTimestamp();
                logger.LogWarning(ex, "Could not start capturing {What} yet; trying again {Every}", source.Wording.Capturing, EveryLook);
            }

            _deviceCheck = time.CreateTimer(_ => QueueRestart(), null, source.CheckInterval, source.CheckInterval);
            if (_placer is not null) _padding = time.CreateTimer(_ => PadToClock(), null, PaddingInterval, PaddingInterval);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>Stops capturing, finalises the files and returns what was recorded; a track on the clock is padded to the position now (see <see cref="Stop(TimeSpan?)"/>).</summary>
    public RecordingResult Stop() => Stop(null);

    /// <summary>
    /// Stops capturing, finalises the files and returns what was recorded. A track on the clock has its held packets
    /// judged once more, then is padded to <paramref name="endPosition"/>, so that the run's tracks end together when
    /// each is given the same end, or to the clock's position now when none is given. A device change that Windows
    /// holds up for longer than <see cref="StopWaitLimit"/> is not waited for. Safe to call twice: a second call returns
    /// the same result at once.
    /// </summary>
    public RecordingResult Stop(TimeSpan? endPosition)
    {
        lock (_gate)
        {
            // Already stopped: even a device change that Windows is still holding up is not waited for again.
            if (_result is not null) return _result;
        }

        if (!EnterLifecycleToStop()) return StopWithoutWaiting(endPosition);
        try
        {
            IAudioCapture? capture;
            lock (_gate)
            {
                if (_result is not null) return _result;
                _stopping = true;
                capture = _capture;
            }

            source.Changed -= OnDevicesChanged;
            _deviceCheck?.Dispose();
            _padding?.Dispose();

            try
            {
                // Waits for the capture thread, so the last buffers are still written.
                ReleaseCapture(capture);
            }
            catch (Exception ex)
            {
                // Deliberately not rethrown: the audio is already in the segment files, and a device that
                // will not shut down cleanly must not stand between the recording and its transcript.
                logger.LogError(ex, "The audio device did not shut down cleanly; the recorded audio is kept");
            }

            lock (_gate)
            {
                _capture = null;
                return _result = FinishLocked(endPosition);
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>
    /// Reports how much has been captured, how long ago sound was last heard and, while a capture is open, whether audio
    /// keeps arriving from it: how long ago it last handed over any, whether it is a kind that hands over audio all the
    /// time, how loud Windows' meter showed what it records at the last look, and whether it has handed over nothing but
    /// zeros for a while. It also says whether sound keeps arriving while paused, whether writing the files fails, and
    /// whether the track is switched off.
    /// </summary>
    public RecordingStatus GetStatus()
    {
        lock (_gate)
        {
            var now = time.GetTimestamp();
            var captured = _closedDuration + OpenPartLengthLocked();
            var open = _capture is not null;
            return new RecordingStatus(
                captured, time.GetElapsedTime(_lastSound, now), _deviceName, _segments.Count, _paused, open || !_enabled ? null : source.WaitingFor,
                SinceLastPacket: open ? time.GetElapsedTime(_lastPacket, now) : null,
                DeliversContinuously: open && _deliversContinuously,
                RecordedLevel: open ? _recordedLevel : null,
                SoundWhilePaused: _paused && _pausedSound is { } heard && time.GetElapsedTime(heard, now) <= PausedSoundGap,
                WriteFailing: _writeFailing,
                Off: !_enabled,
                OnlyZeros: open && _anyPacket && time.GetElapsedTime(_lastNonZero ?? _captureOpened, now) >= OnlyZerosAfter,
                HeardSound: _heardSound,
                RawZerosFor: open && _anyPacket ? time.GetElapsedTime(_lastRawNonZero ?? _captureOpened, now) : null,
                RawSoundSinceOpened: open && _lastRawNonZero is not null,
                WindowsSilent: open && _lastPacketSilent,
                CaptureNumber: _captureCount);
        }
    }

    /// <summary>
    /// Has the open capture replaced at the next look, as a failed one is: released, its file closed,
    /// and what the source picks then opened into a new file. This is for a capture that stays open but has stopped
    /// delivering audio although it should, which raises no error of its own. <paramref name="reason"/> goes into the
    /// log. It applies to the capture open now: with none open, or once the recording has stopped, it does nothing, and
    /// a capture replaced for another reason before the next look is not replaced again for it. It works during a
    /// pause as well, so that the recording can go on when the pause ends.
    /// </summary>
    public void Reopen(string reason)
    {
        lock (_gate)
        {
            if (_stopping || _capture is null)
            {
                logger.LogDebug("No capture is open to replace ({Reason})", reason);
                return;
            }

            _reopen = _capture;
            _reopenReason = reason;
        }
    }

    /// <summary>
    /// Stops keeping captured audio until <see cref="Resume"/>. The capture itself keeps running and the regular look goes
    /// on, so a move of the sound to another device during the break is still followed, and the sound that arrives is
    /// still measured, though not written (see <see cref="RecordingStatus.SoundWhilePaused"/>). On a track that follows
    /// the clock, the clock's own pause decides which packets fall inside the pause (see <see cref="RecordingClock.Pause"/>):
    /// a packet stamped before it but delivered during it still belongs before it.
    /// </summary>
    public void Pause()
    {
        lock (_gate)
        {
            if (_paused) return;
            _paused = true;
            _pausedSound = null;

            // A break can be long, so the file on disk is brought up to date now rather than at the next save.
            try
            {
                _writer?.Flush();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Deliberately not rethrown: what reached the disk stays readable, and the next audio after
                // the break goes to a new file, as after any other write failure.
                logger.LogError(ex, "Could not bring the recording on disk up to date for the pause");
                _writeFailing = true;
                CloseSegmentLocked(failureReported: true);
            }
        }
    }

    /// <summary>Keeps captured audio again after <see cref="Pause"/>.</summary>
    public void Resume()
    {
        lock (_gate)
        {
            if (!_paused) return;
            _paused = false;
            _pausedSound = null;

            // The silence warning counts from now: the quiet of the break itself is no cause for one, nor for a note.
            _lastSound = time.GetTimestamp();
            _lastPacketEnd = null;
        }
    }

    /// <summary>
    /// Switches the track off or on: V. Off releases the capture at the next look, which comes at once, and opens no
    /// other, so that Windows' microphone icon goes out unless another app holds the microphone; the open part stays,
    /// and a track on the clock is kept on it by the timer, so nothing moves. The looks go on while off, picking without
    /// opening. On opens a capture again at once, and its packets land at their clock positions. Switching to the state
    /// already held changes nothing. Before <see cref="Start(RecordingFiles, TimeSpan)"/> only the state is kept, and
    /// nothing is looked at, opened or said: the start reads it, and a track switched off by then starts without a capture.
    /// </summary>
    public void SetEnabled(bool on)
    {
        lock (_gate)
        {
            if (_enabled == on) return;
            _enabled = on;
            if (!_started) return;
        }

        if (on)
            logger.LogInformation("Switching {What} on (V)", source.Wording.Thing);
        else
            logger.LogInformation("Switching {What} off (V); its capture is released{AndThen}", source.Wording.Thing, _clock is null ? "" : " and the track keeps its place on the clock");

        QueueRestart();
    }

    /// <summary>
    /// Sets the gate that decides which packets of a track on the clock are written, or takes it away with null, which
    /// writes every packet at once, as without one. With a gate, a packet it cannot decide yet is held, and judged again
    /// when the gate says it has changed, when the next packet arrives, every second, and once more at the stop; a
    /// packet that arrives while others are held waits behind them, so that the packets are written in their order; a
    /// packet it drops is not written, and its time is silence at its place, so positions do not move. A packet held
    /// longer than <see cref="LongestHold"/> is dropped. Only a track on the clock can have a gate.
    /// </summary>
    public void SetGate(IPacketGate? gate)
    {
        if (_clock is null) throw new InvalidOperationException("Only a track that follows the recording clock can have a gate.");

        IPacketGate? old;
        lock (_gate)
        {
            old = _packetGate;
            if (ReferenceEquals(old, gate)) return;
            _packetGate = gate;
        }

        if (old is not null) old.Changed -= OnGateChanged;
        if (gate is not null) gate.Changed += OnGateChanged;
        lock (_gate) JudgeHeldLocked(final: false);
    }

    /// <summary>
    /// Sets what looks for a time ClassScribe did not run, such as <see cref="FreezeWatch.CheckNow"/>, called when a
    /// packet or the timer finds a track on the clock further behind than the timer leaves it, which only a sleep or a
    /// hold-up does: the look cuts such a time from the clock before anything is placed or padded, so that a sleep is
    /// never padded with silence. The look's answer is for the log; what it does to the clock is what counts. Without
    /// one, a gap that far is written as it is, up to a minute.
    /// </summary>
    public void SetFreezeLook(Func<Freeze?>? look)
    {
        lock (_gate) _freezeLook = look;
    }

    /// <summary>
    /// Writes the current header to disk right away. Used when the process is about to exit. A failure is logged
    /// and handed on. The open file stays open: a failed save leaves its writer as it was, so the audio that still
    /// arrives goes after the audio already in it, and the next save, at the latest when the recording stops,
    /// brings the header up to date. Closing the file instead would leave its header behind for good.
    /// </summary>
    public void Flush()
    {
        lock (_gate)
        {
            if (_writer is not { } writer) return;
            try
            {
                writer.Flush();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogError(ex, "Could not bring {Segment} on disk up to date; the audio written to it is kept", Path.GetFileName(writer.Path));
                throw;
            }
        }
    }

    /// <summary>Stops the recording if it is still running, lets go of the gate, then lets go of its source.</summary>
    public void Dispose()
    {
        try
        {
            if (_started && _result is null) Stop();
        }
        finally
        {
            IPacketGate? gate;
            lock (_gate)
            {
                gate = _packetGate;
                _packetGate = null;
            }

            if (gate is not null) gate.Changed -= OnGateChanged;
            source.Dispose();
        }
    }

    /// <summary>
    /// Takes the lifecycle lock for <see cref="Stop()"/>. Returns false, without it, when a device change has
    /// held it for <see cref="StopWaitLimit"/>: a driver that has stopped answering must not leave Q unanswered.
    /// </summary>
    private bool EnterLifecycleToStop()
    {
        if (_lifecycle.Wait(0)) return true;

        using var patience = new CancellationTokenSource(StopWaitLimit, time);
        try
        {
            _lifecycle.Wait(patience.Token);
            return true;
        }
        catch (OperationCanceledException) when (patience.IsCancellationRequested)
        {
            // Deliberately not rethrown: running out of patience is the answer here, not a failure. The
            // caller stops without the lock.
            return false;
        }
    }

    /// <summary>
    /// Ends the recording while a device change is stuck inside Windows, so that Q is answered and the
    /// transcript can follow. The open file is finalised and nothing more is recorded. The capture the
    /// change is working on is left to it, because releasing a capture that Windows is still busy with could
    /// take the process down. It is released once: by the change, if the change releases it on its way out
    /// (see <see cref="ClaimLocked"/>), or else as soon as the change is over (see <see cref="ReleaseLeftBehind"/>).
    /// </summary>
    private RecordingResult StopWithoutWaiting(TimeSpan? endPosition)
    {
        RecordingResult result;
        lock (_gate)
        {
            if (_result is not null) return _result;
            _stopping = true;
            _leftBehind = _capture;
            _capture = null;   // whatever it still delivers is ignored from here on
            result = _result = FinishLocked(endPosition);
        }

        logger.LogError(
            "Windows has not finished changing the audio device for {Seconds} seconds, so the recording stops without waiting for it. Everything recorded so far is kept",
            StopWaitLimit.TotalSeconds);
        source.Changed -= OnDevicesChanged;
        _deviceCheck?.Dispose();
        _padding?.Dispose();

        // The change may have ended in the meantime, and then nobody else is left to release the capture.
        if (_lifecycle.Wait(0))
        {
            _lifecycle.Release();
            ReleaseLeftBehind();
        }

        return result;
    }

    /// <summary>
    /// Finishes the recording: on a track on the clock, judges the held packets once more and pads the track to
    /// <paramref name="endPosition"/>, or to the clock's position now; then closes the open part and sums up. Call with
    /// <see cref="_gate"/> held.
    /// </summary>
    private RecordingResult FinishLocked(TimeSpan? endPosition)
    {
        if (_placer is { } placer)
        {
            JudgeHeldLocked(final: true);
            PadLocked(placer, endPosition ?? _clock!.Now());
            if (_droppedByGate > 0) logger.LogDebug("The gate dropped {Packets} of {What}", Count(_droppedByGate, "packet"), source.Wording.Capturing);
        }

        CloseSegmentLocked();
        return new RecordingResult(_folder, [.. _segments], _closedDuration, _heardSound, _placer?.Corrections ?? 0);
    }

    /// <summary>Opens a capture of <paramref name="target"/>. Its segment file is created when its first audio arrives.</summary>
    private void StartCapture(CaptureTarget target)
    {
        var capture = source.Open(target);
        try
        {
            if (!PcmConverter.IsSupported(capture.Format))
                throw new NotSupportedException($"\"{capture.DeviceName}\" uses an audio format this app cannot read ({capture.Format}).");

            capture.DataAvailable += OnDataAvailable;
            capture.Stopped += OnCaptureStopped;
            bool recording;
            lock (_gate)
            {
                // The recording only stops while a capture is being opened after Stop gave up waiting for it.
                recording = !_stopping;
                if (recording)
                {
                    _capture = capture;
                    _captureEnded = false;
                    _captureSound = null;
                    _deviceName = capture.DeviceName;
                    _deliversContinuously = capture.DeliversContinuously;
                    _lastRate = capture.Format.SampleRate;
                    _anyPacket = false;
                    _lastNonZero = null;
                    _lastRawNonZero = null;
                    _lastPacketSilent = false;
                    _captureCount++;

                    // Until its first packet, the time without audio counts from now.
                    _captureOpened = _lastPacket = time.GetTimestamp();
                }
            }

            if (recording) capture.Start();
        }
        catch
        {
            lock (_gate)
            {
                if (ReferenceEquals(_capture, capture)) _capture = null;
                ClaimLocked(capture);
            }

            ReleaseCapture(capture);
            throw;
        }

        bool kept;
        lock (_gate)
        {
            kept = ReferenceEquals(_capture, capture);
            if (!kept) ClaimLocked(capture);
        }

        if (!kept)
        {
            // The recording was stopped while Windows held this capture up, so it has nothing left to record into.
            ReleaseQuietly(capture);
            return;
        }

        logger.LogInformation("Capturing \"{Device}\" at {SampleRate} Hz", capture.DeviceName, capture.Format.SampleRate);
    }

    /// <summary>
    /// Notes that audio has arrived, converts it to mono 16-bit and measures it, then writes it: on a contiguous track
    /// appended to the open segment, unless the recording is paused, when it is only measured, so that sound the class
    /// plays then shows and a device that keeps playing is kept; on a track on the clock, where its stamp puts it, the
    /// clock's pause deciding what falls inside a pause, after the gate has let it through when there is one. Runs on
    /// the capture thread.
    /// </summary>
    private void OnDataAvailable(object? sender, AudioDataEventArgs e)
    {
        if (e.ByteCount <= 0) return;

        TimelineNote? note = null;
        lock (_gate)
        {
            if (_capture is null || !ReferenceEquals(sender, _capture)) return;

            // Audio that is not kept still shows that the capture works.
            var now = time.GetTimestamp();
            _lastPacket = now;
            _anyPacket = true;

            var format = _capture.Format;

            // At the device's own depth, before the conversion rounds Windows' faint residue to zero (camera-first 3.4); a
            // packet Windows flags as silent is Windows' silence, not the device's, and counts as neither.
            _lastPacketSilent = e.Flags.HasFlag(PacketFlags.Silent);
            if (!_lastPacketSilent && PcmConverter.AnyNonZero(e.Buffer.AsSpan(0, e.ByteCount), format)) _lastRawNonZero = now;

            var needed = e.ByteCount / format.BytesPerFrame * 2;
            if (_convertBuffer.Length < needed) _convertBuffer = new byte[needed];

            var written = PcmConverter.ToMono16(e.Buffer.AsSpan(0, e.ByteCount), format, _convertBuffer);
            var mono = _convertBuffer.AsSpan(0, written);
            if (mono.IsEmpty) return;

            // The device carries sound whether it is kept or not: the look keeps a device that plays.
            var peak = PcmConverter.Peak(mono);
            if (peak > 0) _lastNonZero = now;
            var audible = peak >= SoundThreshold;
            if (audible) _captureSound = now;

            if (_paused)
            {
                if (audible) _pausedSound = now;
                if (_placer is null) return;   // a contiguous track writes nothing while paused; on the clock, the clock decides
            }
            else if (audible)
            {
                _lastSound = now;
            }

            if (_placer is null)
            {
                note = NoteGapLocked(e.Qpc100ns, mono.Length / 2, format.SampleRate);
                WriteLocked(mono, format.SampleRate, audible, now);
            }
            else
            {
                TakeLocked(mono, e.Qpc100ns, format.SampleRate, audible, now);
            }
        }

        if (note is not null) NoteWorthy?.Invoke(this, note);
    }

    /// <summary>
    /// Takes a packet for a track on the clock: notes its stamp as the recording's origin if it is the first of any track,
    /// written or not; judges the packets held so far; then, with a gate, holds the packet while any packet before it is
    /// still held or the gate cannot decide it, drops it when the gate says so, and otherwise places it. Call with
    /// <see cref="_gate"/> held.
    /// </summary>
    private void TakeLocked(ReadOnlySpan<byte> mono, long? stamp, int sampleRate, bool audible, long now)
    {
        var frames = mono.Length / 2;
        var arrival = MediaTime.Now(time);
        var judged = stamp ?? arrival - MediaTime.FromFrames(frames, sampleRate).Ticks;
        _clock!.Begin(judged);
        JudgeHeldLocked(final: false);

        if (_packetGate is { } gate)
        {
            // A packet never overtakes one still held: it waits behind them and is judged with them, in order. The watch's
            // thread records a look without this lock, so one can land between the judgement of the held packets and this
            // one's; judged on its own, this packet would then be written first and the held ones trimmed away behind it.
            switch (_held.Count > 0 ? GateVerdict.Undecided : gate.Judge(judged))
            {
                case GateVerdict.Undecided:
                    _held.Add(new HeldPacket(mono.ToArray(), stamp, judged, arrival, sampleRate, audible));
                    return;
                case GateVerdict.Drop:
                    _droppedByGate++;
                    return;
            }
        }

        PlaceLocked(stamp, mono, sampleRate, audible, now);
    }

    /// <summary>
    /// Judges the packets held for the gate, oldest first: a packet the gate now writes is placed, one it drops is dropped,
    /// and one it still cannot decide waits on with those after it, unless it has waited longer than <see cref="LongestHold"/>,
    /// or this is the stop (<paramref name="final"/>), when it is dropped. Call with <see cref="_gate"/> held.
    /// </summary>
    private void JudgeHeldLocked(bool final)
    {
        if (_held.Count == 0) return;

        var now = time.GetTimestamp();
        var nowUnits = MediaTime.Now(time);
        var gate = _packetGate;
        var decided = 0;
        foreach (var packet in _held)
        {
            var verdict = gate is null ? GateVerdict.Write : gate.Judge(packet.Judged);
            if (verdict == GateVerdict.Undecided)
            {
                if (!final && nowUnits - packet.Judged <= LongestHold.Ticks) break;   // this one and those after it wait on
                verdict = GateVerdict.Drop;
            }

            decided++;
            if (verdict == GateVerdict.Write)
                PlaceLocked(packet.Stamp, packet.Mono, packet.SampleRate, packet.Audible, now, packet.Arrival);
            else
                _droppedByGate++;
        }

        _held.RemoveRange(0, decided);
    }

    /// <summary>
    /// Places a packet on a track on the clock, as the placer decides by its stamp (see <see cref="TrackPlacer.Place"/>):
    /// written as it is, after a little silence, or cut at the front; dropped inside a pause or a cut; and, when it is far
    /// ahead of the track, placed again after a look for a time ClassScribe did not run, which cuts such a time from the
    /// clock first. A packet whose stamp Windows flagged as wrong (<paramref name="stamp"/> null) is placed by the time it
    /// arrived: now, or <paramref name="arrival"/> for a packet the gate held, so that the hold does not move it. Call with
    /// <see cref="_gate"/> held.
    /// </summary>
    private void PlaceLocked(long? stamp, ReadOnlySpan<byte> mono, int sampleRate, bool audible, long now, long? arrival = null)
    {
        var placer = _placer!;
        var frames = mono.Length / 2;
        var length = MediaTime.FromFrames(frames, sampleRate);
        var placement = placer.Place(stamp, length, arrival);
        if (placement.Kind == PlacementKind.CheckFreeze)
        {
            LookForFreezeLocked(placement.Gap);
            placement = placer.Place(stamp, length, arrival, freezeChecked: true);
        }

        switch (placement.Kind)
        {
            case PlacementKind.Append:
                WriteLocked(mono, sampleRate, audible, now);
                break;
            case PlacementKind.Pad:
                logger.LogDebug("Writing {Milliseconds} ms of silence before a packet that far ahead of the track", placement.Gap.TotalMilliseconds);
                if (WriteSilenceLocked(placement.Gap, sampleRate, now)) WriteLocked(mono, sampleRate, audible, now);
                break;
            case PlacementKind.Trim:
                var skip = (int)Math.Min(frames, MediaTime.ToFrames(placement.Trimmed, sampleRate));
                logger.LogDebug("Dropping {Milliseconds} ms from the front of a packet that far behind the track", placement.Trimmed.TotalMilliseconds);
                if (skip < frames) WriteLocked(mono[(skip * 2)..], sampleRate, audible, now);
                break;
            case PlacementKind.Drop:
                break;   // stamped inside a pause or a time cut out: measured above, not written
            case PlacementKind.LongGap:
                logger.LogInformation("Writing {Seconds} s of silence before a packet that arrived that far ahead of the track; no time ClassScribe did not run was found",
                    placement.Gap.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture));
                if (WriteSilenceLocked(placement.Gap, sampleRate, now)) WriteLocked(mono, sampleRate, audible, now);
                break;
            case PlacementKind.TooFarAhead:
                logger.LogWarning("A packet arrived {Seconds} s ahead of the track, further than expected, and no time ClassScribe did not run was found; it is written as it comes",
                    placement.Gap.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture));
                WriteLocked(mono, sampleRate, audible, now);
                break;
        }
    }

    /// <summary>
    /// Looks for a time ClassScribe did not run, through what <see cref="SetFreezeLook"/> set, because the track is
    /// <paramref name="behind"/> the clock, further than the timer leaves it: a sleep or a hold-up found is cut from the
    /// clock by the look itself. Call with <see cref="_gate"/> held.
    /// </summary>
    private void LookForFreezeLocked(TimeSpan behind)
    {
        var seconds = behind.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture);
        if (_freezeLook is not { } look)
        {
            logger.LogDebug("The track is {Seconds} s behind the clock, and nothing looks for a time ClassScribe did not run", seconds);
            return;
        }

        if (look() is { } freeze)
            logger.LogDebug("The track is {Seconds} s behind the clock: a time ClassScribe did not run, from {From:HH:mm:ss} to {To:HH:mm:ss} UTC, was cut out of it", seconds, freeze.FromUtc, freeze.ToUtc);
        else
            logger.LogDebug("The track is {Seconds} s behind the clock, and no time ClassScribe did not run was found", seconds);
    }

    /// <summary>
    /// The timer's tick, once a second, on a track on the clock: judges the packets held for the gate, then fills what
    /// nothing has arrived for, up to a second and a half behind the clock, in chunks of at most a second, taking and
    /// releasing the gate for each, so that a capture thread never waits for more than one chunk's write. A track further
    /// behind than the timer leaves it means the process did not run: a look for that time comes first, so that a sleep
    /// is cut out rather than padded. Runs on a thread of the timer's.
    /// </summary>
    private void PadToClock()
    {
        try
        {
            var lookedForFreeze = false;
            while (true)
            {
                lock (_gate)
                {
                    if (_stopping || _placer is not { } placer) return;

                    JudgeHeldLocked(final: false);
                    var due = placer.Shortfall(placer.PaddingTarget());
                    if (due <= TimeSpan.Zero) return;

                    if (due > TrackPlacer.InlineGapLimit && !lookedForFreeze)
                    {
                        lookedForFreeze = true;
                        LookForFreezeLocked(due);
                        continue;
                    }

                    if (!WriteSilenceChunkLocked(due, time.GetTimestamp())) return;
                }
            }
        }
        catch (Exception ex)
        {
            // Deliberately not rethrown: this runs on a timer, where an exception would end the process, and the
            // recording with it. A failed write is handled inside and never reaches here; what can is the look for a time
            // ClassScribe did not run, which reads the computer's clocks. The next tick tries again, and the track is
            // padded then.
            bool first;
            lock (_gate)
            {
                first = !_paddingFailed;
                _paddingFailed = true;
            }

            if (first)
                logger.LogError(ex, "Could not keep {What} on the clock this second; trying again at the next", source.Wording.Capturing);
            else
                logger.LogDebug(ex, "Could not keep {What} on the clock this second again", source.Wording.Capturing);
        }
    }

    /// <summary>Pads a track on the clock up to <paramref name="target"/>, in chunks, as the stop does. Call with <see cref="_gate"/> held.</summary>
    private void PadLocked(TrackPlacer placer, TimeSpan target)
    {
        var now = time.GetTimestamp();
        for (var due = placer.Shortfall(target); due > TimeSpan.Zero; due = placer.Shortfall(target))
        {
            if (!WriteSilenceChunkLocked(due, now)) return;
        }
    }

    /// <summary>
    /// Writes one chunk of silence, at most <see cref="TrackPlacer.PaddingChunk"/> of the <paramref name="due"/>, into the
    /// open part at its rate, or into a new part at the last capture's rate, or at 16 kHz when there has been none. False
    /// when nothing could be written, or nothing is left to write. Call with <see cref="_gate"/> held.
    /// </summary>
    private bool WriteSilenceChunkLocked(TimeSpan due, long now)
    {
        var rate = _writer?.SampleRate ?? (_lastRate > 0 ? _lastRate : PaddingRate);
        var chunk = due < TrackPlacer.PaddingChunk ? due : TrackPlacer.PaddingChunk;
        var frames = MediaTime.ToFrames(chunk, rate);
        return frames > 0 && WriteLocked(Zeros(frames), rate, audible: false, now);
    }

    /// <summary>Writes <paramref name="gap"/> of silence at <paramref name="sampleRate"/>, in chunks of at most <see cref="TrackPlacer.PaddingChunk"/>. False when a write failed. Call with <see cref="_gate"/> held.</summary>
    private bool WriteSilenceLocked(TimeSpan gap, int sampleRate, long now)
    {
        var frames = MediaTime.ToFrames(gap, sampleRate);
        var chunkFrames = MediaTime.ToFrames(TrackPlacer.PaddingChunk, sampleRate);
        while (frames > 0)
        {
            var count = Math.Min(frames, chunkFrames);
            if (!WriteLocked(Zeros(count), sampleRate, audible: false, now)) return false;
            frames -= count;
        }

        return true;
    }

    /// <summary>Mono 16-bit silence of <paramref name="frames"/> frames, from a buffer kept for it that is never written to.</summary>
    private ReadOnlySpan<byte> Zeros(long frames)
    {
        var bytes = checked((int)(frames * 2));
        if (_zeros.Length < bytes) _zeros = new byte[bytes];
        return _zeros.AsSpan(0, bytes);
    }

    /// <summary>
    /// Appends mono 16-bit audio to the open segment, opening the next one first when there is none, or when the open
    /// one is at another rate (silence padded before a capture opened): a part holds one rate. While the disk is being
    /// given its pause after failing twice, nothing is written. A write that fails is handled here: the device is fine,
    /// so the capture keeps running, what is on disk stays usable, and the recording continues in a fresh file. True when
    /// the audio was written. Call with <see cref="_gate"/> held.
    /// </summary>
    private bool WriteLocked(ReadOnlySpan<byte> mono, int sampleRate, bool audible, long now)
    {
        // While the disk keeps failing, the audio is dropped until the next attempt is due.
        if (_writer is null && _writePauseStarted is { } pauseStarted && time.GetElapsedTime(pauseStarted, now) < WriteRetryDelay) return false;

        try
        {
            if (_writer is { } open && open.SampleRate != sampleRate) CloseSegmentLocked();
            var writer = _writer ?? OpenSegmentLocked(sampleRate);
            writer.Write(mono);
            var frames = mono.Length / 2;
            _partFrames += frames;
            _placer?.Wrote(frames, sampleRate);
            _writeFailing = false;
            if (audible) _heardSound = true;

            if (time.GetElapsedTime(_lastFlush, now) >= FlushInterval)
            {
                writer.Flush();
                _lastFlush = now;
                _writeFailures = 0;   // the disk has just shown that it works
            }

            // The next audio to arrive opens the next segment.
            if (writer.DataBytes >= MaxSegmentBytes) CloseSegmentLocked();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Deliberately not rethrown: the device is fine, so the capture keeps running, and what is
            // already on disk stays usable. The recording continues in a fresh file, because a write
            // that failed half-way can leave this one cut in the middle of a sample.
            _writeFailing = true;
            if (++_writeFailures == 1)
            {
                logger.LogError(ex, "Could not write the recording to {Folder}; a moment of audio is lost. Continuing in a new file", _folder);
            }
            else
            {
                // A disk that fails again at once (a full one, typically) is given a pause, so this does not fill the folder with stubs.
                _writePauseStarted = now;
                logger.LogError(ex, "Still cannot write the recording to {Folder}; audio is being lost. Trying again in {Seconds} seconds", _folder, WriteRetryDelay.TotalSeconds);
            }

            CloseSegmentLocked(failureReported: true);
            return false;
        }
    }

    /// <summary>
    /// On a contiguous track that notes gaps: measures, from the packets' stamps, how much time passed between the end
    /// of the last packet and this one, which the files leave out, and returns a note for the transcript when it is at
    /// least the track spec's <see cref="TrackSpec.NoteGapsLongerThan"/>. A packet without a stamp tells nothing about
    /// time, so the count starts afresh after it. Call with <see cref="_gate"/> held.
    /// </summary>
    private TimelineNote? NoteGapLocked(long? stamp, int frames, int sampleRate)
    {
        if (spec.NoteGapsLongerThan <= TimeSpan.Zero) return null;
        if (stamp is not { } at)
        {
            _lastPacketEnd = null;
            return null;
        }

        TimelineNote? note = null;
        if (_lastPacketEnd is { } end && at - end >= spec.NoteGapsLongerThan.Ticks)
        {
            note = new TimelineNote(_closedDuration + OpenPartLengthLocked(), GapWords(new TimeSpan(at - end)));
            logger.LogInformation("{Note} (at {Position})", note.Text, Durations.Format(note.Position));
        }

        _lastPacketEnd = at + MediaTime.FromFrames(frames, sampleRate).Ticks;
        return note;
    }

    /// <summary>The note for a stretch of <paramref name="leftOut"/> with nothing playing: about so many seconds, minutes, or hours and minutes.</summary>
    private static string GapWords(TimeSpan leftOut)
    {
        var seconds = (long)Math.Round(leftOut.TotalSeconds);
        var minutes = (long)Math.Round(leftOut.TotalMinutes);
        string amount;
        bool one;
        if (seconds < 60)
        {
            amount = Count(seconds, "second");
            one = seconds == 1;
        }
        else if (minutes < 60)
        {
            amount = Count(minutes, "minute");
            one = minutes == 1;
        }
        else
        {
            var hours = minutes / 60;
            var rest = minutes % 60;
            amount = rest == 0 ? Count(hours, "hour") : $"{Count(hours, "hour")} {Count(rest, "minute")}";
            one = hours == 1 && rest == 0;
        }

        return $"About {amount} with nothing playing {(one ? "was" : "were")} left out here";
    }

    /// <summary>A count with its unit, in the singular for one: 1 minute, 3 minutes.</summary>
    private static string Count(long count, string unit) => string.Create(CultureInfo.InvariantCulture, $"{count} {unit}{(count == 1 ? "" : "s")}");

    /// <summary>The words with their first letter in capitals, for the start of a line.</summary>
    private static string Capitalised(string words) => words.Length == 0 ? words : char.ToUpperInvariant(words[0]) + words[1..];

    /// <summary>Notes that the capture ended without having been asked to, and has it replaced. Runs on the capture thread.</summary>
    private void OnCaptureStopped(object? sender, CaptureStoppedEventArgs e)
    {
        lock (_gate)
        {
            if (_stopping || _captureEnded || !ReferenceEquals(sender, _capture)) return;
            _captureEnded = true;
        }

        if (e.Error is null)
            logger.LogWarning("Audio capture stopped by itself; restarting it");
        else
            logger.LogWarning(e.Error, "Audio capture failed; restarting it");

        QueueRestart();
    }

    /// <summary>Has what can be recorded looked at again. Runs on a thread that belongs to Windows, or to the source.</summary>
    private void OnDevicesChanged(object? sender, EventArgs e) => QueueRestart();

    /// <summary>Judges the held packets again: the gate says a look has arrived, or V was pressed. Runs on the watch's thread, or the loop's.</summary>
    private void OnGateChanged(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            if (_stopping) return;
            JudgeHeldLocked(final: false);
        }
    }

    /// <summary>
    /// Schedules one restart check on the thread pool. Requests made before it begins are merged into it.
    /// The check itself decides whether anything needs doing, so asking too often costs next to nothing.
    /// </summary>
    private void QueueRestart()
    {
        if (_stopping || Interlocked.Exchange(ref _restartQueued, 1) == 1) return;
        _ = Task.Run(RestartAsync);
    }

    /// <summary>
    /// Runs one restart check. It schedules nothing further: when the check could not finish the job, the
    /// regular look tries again, so there is never more than one check waiting to run.
    /// </summary>
    private async Task RestartAsync()
    {
        await _lifecycle.WaitAsync();

        // The check's own time, read once before anything that can be slow, so that how long Windows takes
        // to answer does not change what the check decides.
        var now = time.GetTimestamp();
        try
        {
            Interlocked.Exchange(ref _restartQueued, 0);
            if (_stopping) return;

            if (CheckDevices(now)) _lastReport = null;
        }
        catch (Exception ex)
        {
            // This is a background task with nobody to rethrow to, so the failure is logged, now and then
            // rather than at every try, and the next regular look tries again. Once the recording has ended
            // (Stop gave up waiting for this check), nothing is tried again and the failure no longer matters to it.
            if (_stopping)
                logger.LogDebug(ex, "The device change the recording stopped without waiting for has failed");
            else if (TimeToReport(now))
                logger.LogWarning(ex, "Cannot capture {What} right now; trying again {Every}", source.Wording.Thing, EveryLook);
        }
        finally
        {
            _lifecycle.Release();

            // A capture that a stopped recording could not wait for is released once the change that held it is over.
            if (_stopping) ReleaseLeftBehind();
        }
    }

    /// <summary>
    /// Replaces the capture when there is none, when it has ended, when <see cref="Reopen"/> asked for it, or when the
    /// source picks something else, as when the sound moves to another device. Otherwise it does nothing, so several
    /// notifications about one change cost a single restart. Each look also notes how loud what is recorded plays (see
    /// <see cref="Look"/>). All of this goes on while paused too, so that the recording is ready on the right device when
    /// the pause ends; the sound measured during the pause keeps a device that plays on. When Windows no longer lists what
    /// is recorded, the source picks as it does at the start, and the recording moves to its pick: for the class, the
    /// device with sound or else the default one, another device the chosen name fits, or another process of the app.
    /// When the source picks nothing (no device fits the chosen name, the app has closed, or Windows lists no device at
    /// all), the capture is kept: a device's capture ends by itself once the device is really gone, and an app's records
    /// silence until the app plays again. While the track is switched off (V), the capture is released, its part kept
    /// open, and nothing is opened; the look is still made, so that the source's pick stays fresh for whoever follows it
    /// and for V. <paramref name="now"/> is when the check began, on the steady clock. Returns true when the session is
    /// settled, false when a later check has to try again.
    /// </summary>
    /// <remarks>
    /// Looking at the devices and releasing a capture are calls Windows can hold up. When the recording was
    /// stopped without waiting meanwhile (see <see cref="StopWithoutWaiting"/>), the check ends as soon as such
    /// a call returns: nothing it found is acted on and nothing more is opened for a recording that has ended.
    /// </remarks>
    private bool CheckDevices(long now)
    {
        IAudioCapture? old;
        bool ended;
        bool enabled;
        string? reopen;
        long? captureSound;
        lock (_gate)
        {
            old = _capture;
            ended = _captureEnded;
            enabled = _enabled;
            reopen = old is not null && ReferenceEquals(_reopen, old) ? _reopenReason : null;
            captureSound = _captureSound;
        }

        if (!enabled)
        {
            if (old is not null)
            {
                logger.LogDebug("Releasing the capture of \"{Device}\": switched off (V)", old.DeviceName);
                Replace(old, closeSegment: false);
            }

            // Looked at, not opened: the pick goes to whoever follows it, and is fresh when V switches the track on.
            Look(recordedId: null, sinceRecordedSound: null);
            return true;
        }

        if (ended)
        {
            // A capture that ends again right after being replaced must not become a tight loop of restarts.
            if (_lastEndedRestart is { } last && time.GetElapsedTime(last, now) < RestartSpacing)
            {
                logger.LogDebug("The capture ended again right after it was started; it is replaced once {Seconds} seconds have passed", RestartSpacing.TotalSeconds);
                return false;
            }

            _lastEndedRestart = now;
            Replace(old);
            old = null;
        }
        else if (old is not null && reopen is not null)
        {
            // Whoever asked has said why to the user; here it is only noted.
            logger.LogDebug("Replacing the capture of \"{Device}\" as asked: {Reason}", old.DeviceName, reopen);
            Replace(old);
            old = null;
        }

        if (old is null)
        {
            var target = Look(recordedId: null, sinceRecordedSound: null);
            if (_stopping) return true;
            if (target is null)
            {
                if (TimeToReport(now)) logger.LogWarning("Still nothing to record; waiting for {What}", source.WaitingFor);
                return false;
            }

            StartCapture(target);
            return true;
        }

        var sinceSound = captureSound is { } heard ? time.GetElapsedTime(heard) : (TimeSpan?)null;
        var chosen = Look(old.DeviceId, sinceSound);
        if (_stopping || chosen is null || string.Equals(chosen.Id, old.DeviceId, StringComparison.OrdinalIgnoreCase)) return true;

        logger.LogInformation("{Move}", source.DescribeMove(chosen, old.DeviceName));

        Replace(old);
        if (_stopping) return true;

        StartCapture(chosen);
        return true;
    }

    /// <summary>
    /// Has the source look at what can be recorded and pick from it, and keeps how loud its pick plays for the status
    /// (see <see cref="ICaptureSource.RecordedLevel"/>). A look that fails leaves no level, since what it would have
    /// read is not known; the failure itself is handed on.
    /// </summary>
    private CaptureTarget? Look(string? recordedId, TimeSpan? sinceRecordedSound)
    {
        float? level = null;
        try
        {
            var target = source.Choose(recordedId, sinceRecordedSound);
            level = source.RecordedLevel;
            return target;
        }
        finally
        {
            lock (_gate) _recordedLevel = level;
        }
    }

    /// <summary>
    /// True when a problem that lasts is due to be reported by the check that began at <paramref name="now"/>:
    /// at once, then every <see cref="ReportInterval"/> while it lasts. A settled check starts the count afresh.
    /// </summary>
    private bool TimeToReport(long now)
    {
        if (_lastReport is { } last && time.GetElapsedTime(last, now) < ReportInterval) return false;

        _lastReport = now;
        return true;
    }

    /// <summary>
    /// Releases the capture and, unless <paramref name="closeSegment"/> is false, closes its segment, so the next capture
    /// starts a new one; a track switched off keeps its part open. A replacement <see cref="Reopen"/> asked of it is then
    /// done, whatever the reason it was replaced.
    /// </summary>
    private void Replace(IAudioCapture? old, bool closeSegment = true)
    {
        try
        {
            // Waits for the capture thread, so the old device's last buffers still reach its segment.
            ReleaseCapture(old);
        }
        finally
        {
            lock (_gate)
            {
                _capture = null;
                _captureEnded = false;
                if (ReferenceEquals(_reopen, old))
                {
                    _reopen = null;
                    _reopenReason = null;
                }

                ClaimLocked(old);
                if (closeSegment) CloseSegmentLocked();
            }
        }
    }

    /// <summary>Stops and disposes a capture. Never call this with <see cref="_gate"/> held.</summary>
    private void ReleaseCapture(IAudioCapture? capture)
    {
        if (capture is null) return;

        // Detached first: from here on the capture ends because it was asked to, which is nothing to react to.
        capture.Stopped -= OnCaptureStopped;
        try
        {
            capture.Stop();
            capture.Dispose();
        }
        finally
        {
            capture.DataAvailable -= OnDataAvailable;
        }
    }

    /// <summary>Releases the capture <see cref="StopWithoutWaiting"/> had to leave behind, if there is one and nobody has claimed it.</summary>
    private void ReleaseLeftBehind()
    {
        IAudioCapture? capture;
        lock (_gate)
        {
            capture = _leftBehind;
            _leftBehind = null;
        }

        if (capture is not null) ReleaseQuietly(capture);
    }

    /// <summary>
    /// Claims the release of <paramref name="capture"/> for the caller, which releases it itself. If
    /// <see cref="StopWithoutWaiting"/> left it behind, it is no longer left to <see cref="ReleaseLeftBehind"/>,
    /// so it is released once. Call with <see cref="_gate"/> held.
    /// </summary>
    private void ClaimLocked(IAudioCapture? capture)
    {
        if (capture is not null && ReferenceEquals(_leftBehind, capture)) _leftBehind = null;
    }

    /// <summary>Releases a capture that belongs to a recording that has already stopped.</summary>
    private void ReleaseQuietly(IAudioCapture capture)
    {
        try
        {
            ReleaseCapture(capture);
        }
        catch (Exception ex)
        {
            // Deliberately not rethrown: the recording is over and its result handed out; this only tidies up after it.
            logger.LogWarning(ex, "The audio device did not shut down cleanly");
        }
    }

    /// <summary>Creates the next segment file of the track. Call with <see cref="_gate"/> held.</summary>
    private WavSegmentWriter OpenSegmentLocked(int sampleRate)
    {
        // The next number is normally free. A file that is already there is never overwritten, so its number is skipped.
        var number = _segments.Count + 1;
        string path;
        while (File.Exists(path = _files!.Segment(number, spec.Track))) number++;

        var writer = new WavSegmentWriter(path, sampleRate);
        _writer = writer;
        _partFrames = 0;
        _segments.Add(path);
        _lastFlush = time.GetTimestamp();
        return writer;
    }

    /// <summary>How long the open part's audio lasts, exactly from its frames; zero without one. Call with <see cref="_gate"/> held.</summary>
    private TimeSpan OpenPartLengthLocked() => _writer is { } writer ? MediaTime.FromFrames(writer.DataBytes / 2, writer.SampleRate) : TimeSpan.Zero;

    /// <summary>
    /// Finalises the open segment. Call with <see cref="_gate"/> held. This never throws: a segment that
    /// cannot be finalised (its header locked or its drive gone, say) keeps what reached the disk, and losing
    /// its last moments must not cost the rest of the recording. Only audio that reached the disk is counted,
    /// so the captured length, and the marks set from it, keep matching what will be transcribed; a file left
    /// with no audio in it is removed. On a track on the clock, the placer is told when the part holds more or
    /// less than it was told was written, so that the track's place on the clock is the file's: a defensive
    /// correction, since only a write or a close that failed half-way through a sample's bytes leaves a part so,
    /// which the tests cannot bring about. <paramref name="failureReported"/> says the caller has already reported
    /// the failure that brought it here.
    /// </summary>
    private void CloseSegmentLocked(bool failureReported = false)
    {
        if (_writer is null) return;

        var writer = _writer;
        var name = Path.GetFileName(writer.Path);
        _writer = null;
        var reported = _partFrames;
        _partFrames = 0;
        long audioBytes;
        try
        {
            writer.Dispose();
            audioBytes = writer.DataBytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Deliberately not rethrown (see above). The header is behind, so what counts is what the file
            // holds on disk, which is what transcribing it reads.
            if (failureReported)
                logger.LogDebug(ex, "Could not finish writing {Segment}", name);
            else
                logger.LogError(ex, "Could not finish writing {Segment}; the audio that reached the disk is kept", name);

            if (AudioBytesOnDisk(writer.Path) is not { } onDisk) return;   // cannot be looked at: left as it is, and not counted
            audioBytes = onDisk;
        }

        var correction = audioBytes / 2 - reported;
        if (correction != 0 && _placer is { } placer)
        {
            logger.LogDebug("{Segment} holds {Frames} frames {MoreOrFewer} than the track was counted as written; the track's place on the clock follows the file",
                name, Math.Abs(correction), correction > 0 ? "more" : "fewer");
            placer.Wrote(correction, writer.SampleRate);
        }

        _closedDuration += MediaTime.FromFrames(audioBytes / 2, writer.SampleRate);
        if (audioBytes > 0) return;

        // No audio reached the file, because the first write to it failed: nothing in it is worth keeping.
        _segments.Remove(writer.Path);
        try
        {
            File.Delete(writer.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Deliberately not rethrown: a file with no audio is harmless where it is, and it is no longer part of the recording.
            logger.LogWarning(ex, "Could not remove the empty file {Segment}", name);
        }
    }

    /// <summary>
    /// The bytes of audio a segment file holds on disk, in whole samples, which is what transcribing it reads;
    /// none when the file is not there. Null when the file cannot be looked at.
    /// </summary>
    private long? AudioBytesOnDisk(string path)
    {
        try
        {
            var audio = Math.Max(0, new FileInfo(path).Length - WavHeader.Size);
            return audio - audio % 2;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // Deliberately not rethrown: a file that is not there holds no audio, which is the answer.
            logger.LogDebug(ex, "{Segment} is not on disk", Path.GetFileName(path));
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Deliberately not rethrown: the drive may have gone with the failed write. Nothing is known about
            // the file then, so it is left as it is.
            logger.LogDebug(ex, "Could not look at {Segment} on disk", Path.GetFileName(path));
            return null;
        }
    }

    /// <summary>A packet held for the gate to decide: its audio as mono 16-bit, its stamp, the stamp it is judged by, and when it arrived.</summary>
    /// <param name="Mono">The packet's audio, converted.</param>
    /// <param name="Stamp">The packet's stamp as Windows gave it; null when Windows flagged it as wrong.</param>
    /// <param name="Judged">The stamp the gate judges it by: the stamp, or, without one, the time it arrived less its length.</param>
    /// <param name="Arrival">When it arrived, on the clock the stamps are on, by which a packet without a stamp is placed once the gate writes it.</param>
    /// <param name="SampleRate">The packet's sample rate.</param>
    /// <param name="Audible">Whether the packet carries audible sound.</param>
    private sealed record HeldPacket(byte[] Mono, long? Stamp, long Judged, long Arrival, int SampleRate, bool Audible);
}
