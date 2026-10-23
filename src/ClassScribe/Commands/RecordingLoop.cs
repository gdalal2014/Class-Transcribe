using System.Collections.Concurrent;
using System.Globalization;
using ClassScribe.Audio;
using ClassScribe.Audio.Microphones;
using ClassScribe.Recorder;
using ClassScribe.Storage;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Commands;

/// <summary>
/// The loop that runs while a class records, in the window or in the background recorder alike. It acts on the keys: Q
/// pressed twice stops, P pauses or resumes, M marks the moment, V switches the microphone off or on, and Esc, in a run
/// recording in the window, says how to stop it. Once a second it has the recording checked (see
/// <see cref="RecordingMonitor"/>) and acts on what the checks find: it logs their lines, writes their notes into the
/// marks file, has a stalled capture replaced, and stops the recording when it stops by itself; the checks' first look
/// comes at the start, so that their lines about the microphone lead. At the start, once a second, and at once on P, V
/// and the stop, it publishes the run's state, for the window's title and taskbar or for the windows that show the run,
/// with your microphone's state and the words of the gate that follows the class app when the recording has one (see
/// <see cref="IMicrophoneRecording"/>); every 10 seconds it writes the run file's heartbeat. Ctrl+C is the signals' to act on (see
/// <see cref="ConsoleUserSignals"/>), which apply the same two presses as Q. The loop ends when the stop is asked for,
/// whoever asks, and the run file then says what ended the recording. Each run has a loop of its own.
/// </summary>
/// <remarks>
/// The loop ticks once a second: the next second starts as soon as one ends, before that second's work, and a key
/// pressed meanwhile does not put it off, so neither the work nor the keys lose a second or move the next. The intervals
/// are measured on the steady clock (<see cref="TimeProvider.GetTimestamp"/>), so Windows setting its time of day neither
/// stops nor hurries anything. The capture runs on threads of its own and never waits for the loop, so a second's work
/// that fails is logged and the next second tries again: the class goes on recording, and the keys and the stop still work.
/// </remarks>
public sealed class RecordingLoop(IConsoleUi ui, IUserSignals signals, ClassMarks marks, RecordingMonitor monitor, IRecorderStatusSink status,
    TimeProvider time, ILogger<RecordingLoop> logger)
{
    /// <summary>
    /// How soon after a first Q a second one stops the recording, and likewise for Ctrl+C (see
    /// <see cref="ConsoleUserSignals"/>). A single press, made by mistake, does not.
    /// </summary>
    public static readonly TimeSpan SecondPressWithin = TimeSpan.FromSeconds(5);

    /// <summary>How often the run file's heartbeat is written while recording.</summary>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(10);

    /// <summary>What the log says when a second's work fails.</summary>
    private const string TickFailure = "Could not check on the recording; it goes on, and the next second checks again";

    /// <summary>
    /// What the log says when acting on a key fails. The key may have done its part before the failure, as P pausing before
    /// the window could show it, so the line says no more than that something failed.
    /// </summary>
    private const string KeyFailure = "Something failed while acting on a key; the recording goes on";

    /// <summary>What the log says when the state the run stops in cannot be shown.</summary>
    private const string StopFailure = "Could not show that the recording is stopping; it stops all the same";

    /// <summary>The notes the recording raised, from whatever thread, waiting to go into the marks file at the next second.</summary>
    private readonly ConcurrentQueue<TimelineNote> _noted = new();

    /// <summary>1 once the loop has run, since each run has a loop of its own.</summary>
    private int _ran;

    private IRecording _recording = null!;
    private RecordingFiles _files = null!;
    private RecordRequest _request = null!;
    private RunHandle? _run;
    private MonitorContext _context = null!;

    /// <summary>True for a run recording in this window rather than in the background: closing the window ends it.</summary>
    private bool _inWindow;

    /// <summary>When Q was last pressed without stopping the recording, on the steady clock, or null.</summary>
    private long? _firstQ;

    /// <summary>When the run file's heartbeat was last written, on the steady clock: the start until the first.</summary>
    private long _lastHeartbeat;

    /// <summary>What needs attention, as the last check found.</summary>
    private Attention _attention = Attention.None;

    /// <summary>When the recording stops by itself, as the last check found, or from --minutes before the first.</summary>
    private DateTimeOffset? _stopsAt;

    /// <summary>What the loop itself stopped the recording for, as <see cref="RunEndCause"/> names it, or null.</summary>
    private string? _endedBy;

    /// <summary>
    /// True while each second's work keeps failing, the states published at the start and at the stop included, so that
    /// its failure is logged as an error once rather than every second.
    /// </summary>
    private bool _tickFailing;

    /// <summary>
    /// True while acting on the keys keeps failing, so that a key held down does not log an error each time it repeats. It
    /// is the keys' own: a key is pressed on purpose, so it fails as an error even while the seconds' work keeps failing.
    /// </summary>
    private bool _keyFailing;

    /// <summary>
    /// Runs the loop on <paramref name="recording"/>, already started into <paramref name="files"/> at
    /// <paramref name="offset"/>, for <paramref name="request"/>, until the stop is asked for, and returns the state the
    /// run ends the loop with: stopping, with the count of what needed attention, for the states that follow it.
    /// <paramref name="run"/> is the run's file, whose heartbeat the loop writes and in which it notes what ended the
    /// recording; null for a run without one, which records in the window. --minutes and the safety stop count from now.
    /// </summary>
    /// <exception cref="InvalidOperationException">The loop has run already: each run has a loop of its own.</exception>
    public async Task<RecorderState> RunAsync(IRecording recording, RecordingFiles files, RecordRequest request, RunHandle? run, TimeSpan offset)
    {
        if (Interlocked.Exchange(ref _ran, 1) == 1)
            throw new InvalidOperationException("A recording loop runs one run; each run has a loop of its own.");

        _recording = recording;
        _files = files;
        _request = request;
        _run = run;
        _inWindow = run?.Current.Home != RunHome.Separate;
        _context = new MonitorContext(request, files.Folder, time.GetUtcNow(), time.GetTimestamp(), offset, recording as IMicrophoneRecording, recording as ICameraRecording);
        _lastHeartbeat = _context.StartedTimestamp;
        _stopsAt = request.Minutes is { } minutes ? _context.StartedUtc + TimeSpan.FromMinutes(minutes) : null;

        recording.NoteWorthy += OnNoteWorthy;
        try
        {
            using (monitor.Start(_context))
            {
                // The first second's work comes at once: the checks' first look gives the lines about the microphone, and
                // the state goes to the window before anyone waits a second for it.
                Safely(Tick, TickFailure, ref _tickFailing);
                await WaitForStopAsync();
            }
        }
        finally
        {
            recording.NoteWorthy -= OnNoteWorthy;
            WriteNotes();
        }

        return End();
    }

    /// <summary>
    /// Waits for the stop. Meanwhile it acts on the keys, when someone can type, and does each second's work (see
    /// <see cref="Tick"/>). A read that answers without a key although nothing stopped it means that the keys have run out,
    /// as when their source has closed: reading again would only spin, so no more keys are read, and the recording goes on.
    /// </summary>
    private async Task WaitForStopAsync()
    {
        using var stopReading = new CancellationTokenSource();
        using var stopTicking = new CancellationTokenSource();
        Task<ConsoleKeyInfo?>? nextKey = null;
        var keysLeft = ui.IsInteractive;
        var tick = Task.Delay(FreezeWatch.TickInterval, time, stopTicking.Token);
        try
        {
            while (!signals.StopRequested.IsCompleted)
            {
                if (keysLeft) nextKey ??= ui.ReadKeyAsync(stopReading.Token);
                if (nextKey is null)
                    await Task.WhenAny(signals.StopRequested, tick);
                else
                    await Task.WhenAny(signals.StopRequested, tick, nextKey);

                var ticked = tick.IsCompleted;
                if (ticked) tick = Task.Delay(FreezeWatch.TickInterval, time, stopTicking.Token);

                if (nextKey is { IsCompleted: true })
                {
                    var key = await nextKey;
                    nextKey = null;
                    if (key is { } pressed)
                    {
                        Safely(() => HandleKey(pressed), KeyFailure, ref _keyFailing);
                    }
                    else
                    {
                        keysLeft = false;
                        logger.LogDebug("No more keys can be read; the recording goes on without them");
                    }
                }

                if (signals.StopRequested.IsCompleted) break;
                if (ticked) Safely(Tick, TickFailure, ref _tickFailing);
            }
        }
        finally
        {
            // A cancelled read answers null within moments, so this only waits for it to let go of the keyboard.
            stopTicking.Cancel();
            stopReading.Cancel();
            if (nextKey is not null) await nextKey;
        }
    }

    /// <summary>
    /// A second's work: has the recording checked, logs what the checks say, writes their notes and the recording's own
    /// into the marks file, has a stalled capture replaced, stops the recording when it stops by itself, writes the
    /// heartbeat when it is due, and publishes the state.
    /// </summary>
    private void Tick()
    {
        var snapshot = _recording.GetSnapshot();
        var found = monitor.Tick(snapshot, _context);
        foreach (var (level, text) in found.Lines) logger.Log(level, "{Line}", text);
        foreach (var note in found.Notes) AddNote(note);
        WriteNotes();

        if (found.Reopen is { } reopen) _recording.Reopen(reopen.Track, reopen.Reason);
        if (found.LetGoOfTheCamera is { } why) _context.Camera?.LetGoOfTheCamera(why);
        _attention = found.Attention;
        _stopsAt = found.StopsAtUtc;
        if (found.StopBecause is { } cause) Stop(cause);

        var now = time.GetTimestamp();
        if (_run is not null && time.GetElapsedTime(_lastHeartbeat, now) >= HeartbeatInterval)
        {
            _lastHeartbeat = now;
            _run.Heartbeat(snapshot.Position);
        }

        Publish(snapshot);
    }

    /// <summary>
    /// Acts on a key pressed while recording: Q stops when it follows another Q within <see cref="SecondPressWithin"/>,
    /// timed on the steady clock; P pauses or resumes; M marks the moment; V switches the microphone off or on; Esc, in a
    /// run recording in this window, says how to stop it. Other keys do nothing, and so does any key held with Ctrl or
    /// Alt, such as a shortcut meant for another program (Ctrl+Q, Ctrl+P, Ctrl+V). Ctrl+C, which the console also hands
    /// over as a key while one is read, is the signals' to act on, through the console's own Ctrl+C handler, so it is
    /// never counted twice.
    /// </summary>
    private void HandleKey(ConsoleKeyInfo key)
    {
        if ((key.Modifiers & (ConsoleModifiers.Control | ConsoleModifiers.Alt)) != 0) return;

        switch (key.Key)
        {
            case ConsoleKey.Q when _firstQ is { } first && time.GetElapsedTime(first) <= SecondPressWithin:
                _firstQ = null;
                Stop(RunEndCause.Q);
                break;
            case ConsoleKey.Q:
                _firstQ = time.GetTimestamp();
                logger.LogInformation("Press Q again within {Seconds} seconds to stop", SecondPressWithin.TotalSeconds);
                break;
            case ConsoleKey.P:
                TogglePause();
                break;
            case ConsoleKey.M:
                Mark();
                break;
            case ConsoleKey.V:
                ToggleMicrophone();
                break;
            case ConsoleKey.Escape when _inWindow:
                logger.LogInformation("This recording runs in this window; Q twice stops it");
                break;
        }
    }

    /// <summary>Pauses the recording, or resumes it when it is paused, and shows that at once.</summary>
    private void TogglePause()
    {
        if (_recording.GetSnapshot().Paused)
        {
            _recording.Resume();
            logger.LogInformation("Resumed");
        }
        else
        {
            _recording.Pause();
            logger.LogInformation("Paused. Nothing is recorded until you press P again");
        }

        Publish(_recording.GetSnapshot());
    }

    /// <summary>Writes the current position in the recording to its marks file, so that it shows in the transcript.</summary>
    private void Mark()
    {
        var position = _recording.Position;
        var shown = Durations.Format(position);
        try
        {
            var number = marks.Add(_files, position);
            logger.LogInformation("Mark {Number} at {Position}", number, shown);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Deliberately not rethrown: a mark that cannot be saved must not end the recording.
            logger.LogError(ex, "Could not save the mark at {Position}", shown);
        }
    }

    /// <summary>
    /// Switches the microphone off when it is on and on when it is off, and shows that at once; says so when the recording
    /// has none. Switched on with the class app followed, the line adds what the gate says the microphone still waits for,
    /// when it does: on arms the gate, and your voice is recorded only while the app holds the microphone.
    /// </summary>
    private void ToggleMicrophone()
    {
        if (_recording.SetMicrophone(MicrophoneTrack(_recording.GetSnapshot()) is { Off: true }) is not { } on)
        {
            logger.LogInformation("There is no microphone in this recording");
            return;
        }

        if (!on)
            logger.LogInformation("Microphone off; V switches it on again");
        else if (_context.Microphone?.Gate?.Why is { } why)
            logger.LogInformation("Microphone on; {Why}", why);
        else
            logger.LogInformation("Microphone on");
        Publish(_recording.GetSnapshot());
    }

    /// <summary>
    /// Ends the loop: notes what ended the recording when nobody has, writes the heartbeat with where the recording
    /// stopped, and publishes the state the run stops in. A stop the loop did not make came from Gulzar, by Ctrl+C pressed
    /// twice or from another window, unless whoever asked for it named the cause, as the handler of Windows ending the
    /// session does.
    /// </summary>
    private RecorderState End()
    {
        if (_endedBy is null && _run is { Current.EndedBy: null }) _run.SetEndedBy(RunEndCause.Q);

        var snapshot = _recording.GetSnapshot();
        _attention = _attention with { Level = AttentionLevel.None, Short = "", Text = "" };
        var stopping = StateOf(snapshot, RecorderPhase.Stopping);
        _run?.Heartbeat(snapshot.Position);
        Safely(() => status.Publish(stopping), StopFailure, ref _tickFailing);
        return stopping;
    }

    /// <summary>The recording stops for <paramref name="cause"/>: the run file says so first, should the process end before the run does.</summary>
    private void Stop(string cause)
    {
        _endedBy = cause;
        _run?.SetEndedBy(cause);
        signals.RequestStop();
    }

    /// <summary>Publishes the run's state as <paramref name="snapshot"/> shows the recording.</summary>
    private void Publish(RecordingSnapshot snapshot) => status.Publish(StateOf(snapshot));

    /// <summary>
    /// The run's state as <paramref name="snapshot"/> shows the recording: paused, recording, or not recording while it
    /// waits for something to record; or <paramref name="phase"/> when given. A track switched off with V records nothing by
    /// choice, so it counts as recording.
    /// </summary>
    private RecorderState StateOf(RecordingSnapshot snapshot, RecorderPhase? phase = null)
    {
        var main = snapshot.Main;
        var microphone = MicrophoneTrack(snapshot);
        return new RecorderState(
            phase ?? (snapshot.Paused ? RecorderPhase.Paused : main.Capturing || main.Off ? RecorderPhase.Recording : RecorderPhase.NotRecording),
            _request.Name,
            snapshot.Position,
            string.IsNullOrEmpty(main.Source) ? null : main.Source,
            main.Capturing ? null : main.WaitingFor,
            microphone is null ? null : MicrophoneStateOf(microphone),
            UploadPercent: null,
            _stopsAt,
            _attention,
            _inWindow,
            _stopsAt is { } stopsAt ? TimeZoneInfo.ConvertTime(stopsAt, time.LocalTimeZone).ToString("HH:mm", CultureInfo.InvariantCulture) : null,
            _context.Camera?.Camera is { } camera ? new CameraState(camera.Name, camera.Capturing, camera.Concern?.Text, camera.Size, camera.Dark) : null);
    }

    /// <summary>
    /// Your microphone's state, from its <paramref name="track"/>: its name, whether it is on and capturing, what it waits
    /// for while it cannot be recorded, and, when the recording follows the class app, which app and the gate's state and
    /// words; without a gate the microphone is unfollowed, or off when switched off with V.
    /// </summary>
    private MicrophoneState MicrophoneStateOf(TrackSnapshot track)
    {
        var details = _context.Microphone;
        var described = details?.Gate?.Describe();   // the state and its words under one lock, so they belong together
        var state = described?.State ?? (track.Off ? MicrophoneGateState.Off : MicrophoneGateState.Unfollowed);
        return new MicrophoneState(track.Source, On: !track.Off, track.Capturing, Problem: track.Capturing || track.Off ? null : track.WaitingFor,
            MicrophoneWords.Followed(details?.Follows), state, described?.Why);
    }

    /// <summary>Your microphone's track: the main track in person, the microphone's own beside the class, or null when the recording has none.</summary>
    private TrackSnapshot? MicrophoneTrack(RecordingSnapshot snapshot) =>
        _request.Source.Mode == RecordingMode.Microphone ? snapshot.Main : snapshot.Microphone;

    /// <summary>Keeps a note the recording raised, from whatever thread, for the marks file at the next second.</summary>
    private void OnNoteWorthy(object? sender, TimelineNote note) => _noted.Enqueue(note);

    /// <summary>Writes the notes the recording raised since the last second into the marks file.</summary>
    private void WriteNotes()
    {
        while (_noted.TryDequeue(out var note)) AddNote(note);
    }

    /// <summary>Writes <paramref name="note"/> into the recording's marks file, so that the transcript shows it as a heading.</summary>
    private void AddNote(TimelineNote note)
    {
        if (string.IsNullOrWhiteSpace(note.Text)) return;

        try
        {
            marks.AddNote(_files, note.Position, note.Text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Deliberately not rethrown: a note that cannot be saved must not end the recording; the log keeps what it said.
            logger.LogError(ex, "Could not save the note at {Position} in the marks file: {Note}", Durations.Format(note.Position), note.Text);
        }
    }

    /// <summary>
    /// Does a piece of the loop's work: each second's work, or acting on a key, each kind with a flag of its own,
    /// <paramref name="failing"/>. A failure goes no further. It is logged, with <paramref name="failure"/>, as an error the
    /// first time, and only for diagnosis while that kind of work keeps failing; once it works again, its next failure is
    /// an error again.
    /// </summary>
    private void Safely(Action work, string failure, ref bool failing)
    {
        try
        {
            work();
            failing = false;
        }
        catch (Exception ex)
        {
            // Deliberately not rethrown: the capture runs on threads of its own and keeps the class recording whatever
            // happens here, so a failure in the loop's own work, such as a window whose title cannot be set, must not end
            // the class. The next second tries again, and the keys and the stop still work.
            if (failing)
                logger.LogDebug(ex, "{Failure}", failure);
            else
                logger.LogError(ex, "{Failure}", failure);
            failing = true;
        }
    }
}
