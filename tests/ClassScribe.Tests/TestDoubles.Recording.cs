using ClassScribe.Audio;
using ClassScribe.Commands;
using ClassScribe.Storage;

namespace ClassScribe.Tests;

/// <summary>
/// A recording a test drives by hand, for the code that runs a recording (the recording loop and the record command)
/// without capture sessions. The test says how much has been recorded (<see cref="Written"/> or <see cref="Advance"/>),
/// what the main track looks like at the next check (<see cref="Main"/>), and what stopping finds; the fake notes every
/// call made on it, in order (<see cref="Calls"/>). It records what its <see cref="Mode"/> names, the class alone unless
/// made otherwise: with the class and the microphone, the microphone has a track of its own; in person the microphone is
/// the main track and there is no other. Its state is kept under a lock, so the code under test may drive it from
/// another thread while the test reads it.
/// </summary>
public sealed class FakeRecording : IRecording
{
    private readonly Lock _gate = new();
    private readonly List<string> _calls = [];
    private readonly List<(Track Track, string Reason)> _reopened = [];
    private RecordingFiles? _files;
    private TimeSpan _offset;
    private TimeSpan _written;
    private bool _started;
    private bool _paused;
    private bool _stopped;
    private bool _disposed;
    private bool? _microphoneOn;
    private TrackSnapshot _main = Track();
    private TrackSnapshot? _microphone;
    private Exception? _flushFailure;
    private bool _heardAnything = true;
    private IReadOnlyList<string> _mainSegments = [];
    private IReadOnlyList<string> _microphoneSegments = [];

    /// <summary>A recording of the class alone, or, with <paramref name="hasMicrophone"/>, of the class and the microphone, which starts switched on.</summary>
    public FakeRecording(bool hasMicrophone = false)
        : this(hasMicrophone ? RecordingMode.ClassAndMicrophone : RecordingMode.Class)
    {
    }

    /// <summary>
    /// A recording of what <paramref name="mode"/> names. With the class and the microphone, the microphone has a track of
    /// its own; in person it is the main track, and there is no microphone track beside it. Either way the microphone
    /// starts switched on and hands over audio all the time, silence included, as a microphone does.
    /// </summary>
    public FakeRecording(RecordingMode mode)
    {
        Mode = mode;
        switch (mode)
        {
            case RecordingMode.ClassAndMicrophone:
                _microphoneOn = true;
                _microphone = Track("Microphone Array", deliversContinuously: true);
                break;
            case RecordingMode.Microphone:
                _microphoneOn = true;
                _main = Track("Microphone Array", deliversContinuously: true);
                break;
        }
    }

    /// <summary>What the recording holds: the class alone, the class and the microphone, or the microphone alone in person.</summary>
    public RecordingMode Mode { get; }

    /// <summary>
    /// True when the recording has your microphone, on a track of its own beside the class or as the main track in person,
    /// so that V switches it off and on.
    /// </summary>
    public bool HasMicrophone => Mode != RecordingMode.Class;

    /// <summary>When set, <see cref="Start"/> throws this, as a recording whose every track is in a format ClassScribe cannot read does.</summary>
    public Exception? StartFailure { get; init; }

    /// <summary>When set, <see cref="Flush"/> throws this, as a disk that has failed would; null makes it work again.</summary>
    public Exception? FlushFailure
    {
        get { lock (_gate) return _flushFailure; }
        set { lock (_gate) _flushFailure = value; }
    }

    /// <summary>What <see cref="Stop"/> reports as heard: true unless the test says nothing audible arrived.</summary>
    public bool HeardAnything
    {
        get { lock (_gate) return _heardAnything; }
        set { lock (_gate) _heardAnything = value; }
    }

    /// <summary>The main track's segment files <see cref="Stop"/> reports; none unless the test names some.</summary>
    public IReadOnlyList<string> MainSegments
    {
        get { lock (_gate) return _mainSegments; }
        set { lock (_gate) _mainSegments = value; }
    }

    /// <summary>The microphone track's segment files <see cref="Stop"/> reports; none unless the test names some.</summary>
    public IReadOnlyList<string> MicrophoneSegments
    {
        get { lock (_gate) return _microphoneSegments; }
        set { lock (_gate) _microphoneSegments = value; }
    }

    /// <summary>The files the recording was started into, or null before <see cref="Start"/>.</summary>
    public RecordingFiles? Files
    {
        get { lock (_gate) return _files; }
    }

    /// <summary>Where the recording was started: the length the files already had, zero for a new recording.</summary>
    public TimeSpan Offset
    {
        get { lock (_gate) return _offset; }
    }

    /// <summary>
    /// How much this run has recorded: <see cref="Position"/> is the offset plus this, and the main track's snapshot shows
    /// it as written. The test sets it, or moves it on with <see cref="Advance"/>.
    /// </summary>
    public TimeSpan Written
    {
        get { lock (_gate) return _written; }
        set { lock (_gate) _written = value; }
    }

    /// <summary>
    /// How the main track looks at the next check, apart from what it has written, which follows <see cref="Written"/>, and,
    /// in person, whether it is switched off, which follows V.
    /// </summary>
    public TrackSnapshot Main
    {
        get { lock (_gate) return _main; }
        set { lock (_gate) _main = value; }
    }

    /// <summary>
    /// How the microphone track beside the class looks at the next check, or null without one, as in person, where the
    /// microphone is the main track. Its written length follows <see cref="Written"/>, and whether it is off follows V.
    /// </summary>
    public TrackSnapshot? Microphone
    {
        get { lock (_gate) return _microphone; }
        set { lock (_gate) _microphone = value; }
    }

    /// <summary>True while paused with <see cref="Pause"/>.</summary>
    public bool Paused
    {
        get { lock (_gate) return _paused; }
    }

    /// <summary>Whether the microphone is switched on now; null without one.</summary>
    public bool? MicrophoneOn
    {
        get { lock (_gate) return _microphoneOn; }
    }

    /// <summary>True once <see cref="Start"/> has returned without failing.</summary>
    public bool Started
    {
        get { lock (_gate) return _started; }
    }

    /// <summary>True once <see cref="Stop"/> was called.</summary>
    public bool Stopped
    {
        get { lock (_gate) return _stopped; }
    }

    /// <summary>True once <see cref="Dispose"/> was called.</summary>
    public bool Disposed
    {
        get { lock (_gate) return _disposed; }
    }

    /// <summary>Every call made on the recording, in order, such as "Start", "Pause", "Resume", "Reopen Main", "Flush", "Stop" and "Dispose".</summary>
    public IReadOnlyList<string> Calls
    {
        get { lock (_gate) return [.. _calls]; }
    }

    /// <summary>Each capture asked to be replaced, with the reason given, in order.</summary>
    public IReadOnlyList<(Track Track, string Reason)> Reopened
    {
        get { lock (_gate) return [.. _reopened]; }
    }

    /// <inheritdoc />
    public TimeSpan Position
    {
        get { lock (_gate) return _offset + _written; }
    }

    /// <inheritdoc />
    public event EventHandler<TimelineNote>? NoteWorthy;

    /// <summary>
    /// A track as a check sees it: by default one that captures from "Fake Speakers", has just heard sound and has just
    /// received audio, with nothing wrong. Name only what a test needs to differ. A track that does not capture has no
    /// open capture, so it has no time since its last packet, whatever <paramref name="sinceLastPacket"/> says.
    /// </summary>
    public static TrackSnapshot Track(
        string source = "Fake Speakers",
        string? waitingFor = null,
        TimeSpan written = default,
        TimeSpan sinceLastSound = default,
        TimeSpan sinceLastPacket = default,
        bool deliversContinuously = false,
        float? recordedLevel = null,
        bool soundWhilePaused = false,
        bool capturing = true,
        bool off = false,
        bool writeFailing = false,
        bool onlyZeros = false) =>
        new(source, waitingFor, written, sinceLastSound, capturing ? sinceLastPacket : null, deliversContinuously, recordedLevel, soundWhilePaused, capturing, off,
            writeFailing, onlyZeros);

    /// <inheritdoc />
    public void Start(RecordingFiles files, TimeSpan offset)
    {
        lock (_gate)
        {
            _calls.Add("Start");
            if (StartFailure is not null) throw StartFailure;

            _files = files;
            _offset = offset;
            _started = true;
        }
    }

    /// <inheritdoc />
    public RecordingSummary Stop()
    {
        lock (_gate)
        {
            _calls.Add("Stop");
            _stopped = true;
            return new RecordingSummary(_written, _mainSegments, _microphoneSegments, _heardAnything);
        }
    }

    /// <inheritdoc />
    public void Pause()
    {
        lock (_gate)
        {
            _calls.Add("Pause");
            _paused = true;
        }
    }

    /// <inheritdoc />
    public void Resume()
    {
        lock (_gate)
        {
            _calls.Add("Resume");
            _paused = false;
        }
    }

    /// <inheritdoc />
    public bool? SetMicrophone(bool on)
    {
        lock (_gate)
        {
            _calls.Add(on ? "Microphone on" : "Microphone off");
            if (_microphoneOn is null) return null;

            _microphoneOn = on;
            return on;
        }
    }

    /// <inheritdoc />
    public RecordingSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            var main = _main with { Written = _written };
            return new RecordingSnapshot(
                _offset + _written,
                _paused,
                Mode == RecordingMode.Microphone ? main with { Off = _microphoneOn == false } : main,
                _microphone is { } microphone ? microphone with { Written = _written, Off = _microphoneOn == false } : null);
        }
    }

    /// <inheritdoc />
    public void Reopen(Track track, string reason)
    {
        lock (_gate)
        {
            _calls.Add($"Reopen {track}");
            _reopened.Add((track, reason));
        }
    }

    /// <inheritdoc />
    public void Flush()
    {
        lock (_gate)
        {
            _calls.Add("Flush");
            if (_flushFailure is not null) throw _flushFailure;
        }
    }

    /// <summary>Moves the recording on by <paramref name="by"/>, as a run records; while paused nothing is recorded, so it stays where it is.</summary>
    public void Advance(TimeSpan by)
    {
        lock (_gate)
        {
            if (!_paused) _written += by;
        }
    }

    /// <summary>Raises <see cref="NoteWorthy"/>, as a recording does for something worth a note in the transcript.</summary>
    public void Note(TimeSpan position, string text) => NoteWorthy?.Invoke(this, new TimelineNote(position, text));

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            _calls.Add("Dispose");
            _disposed = true;
        }
    }
}

/// <summary>
/// Makes <see cref="FakeRecording"/>s: the ones the test queued first, in order, then new ones of the plan's mode; and
/// notes each plan it was asked for.
/// </summary>
public sealed class FakeRecordingFactory : IRecordingFactory
{
    private readonly Lock _gate = new();
    private readonly Queue<FakeRecording> _queued = new();
    private readonly List<FakeRecording> _created = [];
    private readonly List<SourcePlan> _plans = [];

    /// <summary>The recordings made so far, in order.</summary>
    public IReadOnlyList<FakeRecording> Created
    {
        get { lock (_gate) return [.. _created]; }
    }

    /// <summary>The plans the recordings were made for, in order.</summary>
    public IReadOnlyList<SourcePlan> Plans
    {
        get { lock (_gate) return [.. _plans]; }
    }

    /// <summary>Queues <paramref name="recording"/> as the next one made, and returns it.</summary>
    public FakeRecording Enqueue(FakeRecording recording)
    {
        lock (_gate) _queued.Enqueue(recording);
        return recording;
    }

    /// <inheritdoc />
    public IRecording Create(SourcePlan source)
    {
        lock (_gate)
        {
            var recording = _queued.TryDequeue(out var queued) ? queued : new FakeRecording(source.Mode);
            _plans.Add(source);
            _created.Add(recording);
            return recording;
        }
    }
}
