using ClassScribe.Application.Audio;
using ClassScribe.Application.Audio.Microphones;
using ClassScribe.Application.Camera;
using ClassScribe.Core.Audio;
using ClassScribe.Core.Audio.Microphones;
using ClassScribe.Core.Runs;
using ClassScribe.Core.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClassScribe.Tests;

/// <summary>
/// A recording with your microphone, as the run asks it beyond the snapshot (see <see cref="IMicrophoneRecording"/>),
/// over a <see cref="FakeRecording"/> a test drives by hand (<see cref="Inner"/>): the test sets which microphone is
/// recorded and why, how Windows processes it, which app the track follows and the gate, and changes them while the
/// loop runs. V switches the inner recording's microphone and arms the gate together, as a recording with a gate does.
/// Its state is kept under a lock, so the code under test may read it from another thread while the test changes it.
/// </summary>
public sealed class FakeMicrophoneRecording : IRecording, IMicrophoneRecording
{
    private readonly Lock _gate = new();
    private FollowRule? _follows;
    private MicrophoneGate? _microphoneGate;
    private Microphone? _microphone;
    private Microphone? _wouldRecord;
    private string? _why;
    private string? _insteadOf;
    private bool _waiting;
    private (Microphone To, string Why, string? InsteadOf)? _move;
    private MicrophoneProcessing? _processing;
    private bool? _mutedInWindows;
    private WebcamState? _webcam;

    /// <summary>
    /// A recording of the class and your microphone, or of what <paramref name="mode"/> names, recording
    /// <paramref name="microphone"/> (the laptop's array unless given) as Windows' default microphone, without Windows'
    /// sound processing, following no app and with no gate until the test gives it one.
    /// </summary>
    public FakeMicrophoneRecording(RecordingMode mode = RecordingMode.ClassAndMicrophone, Microphone? microphone = null)
    {
        Inner = new FakeRecording(mode);
        _microphone = microphone ?? LaptopMicrophones.Array;
        _why = "Windows' default microphone";
        _processing = MicrophoneProcessing.Raw;
    }

    /// <summary>The recording the test drives: what has been recorded, how the tracks look at the next check, and what stopping finds.</summary>
    public FakeRecording Inner { get; }

    /// <inheritdoc />
    public FollowRule? Follows
    {
        get { lock (_gate) return _follows; }
        set { lock (_gate) _follows = value; }
    }

    /// <inheritdoc />
    public MicrophoneGate? Gate
    {
        get { lock (_gate) return _microphoneGate; }
    }

    /// <inheritdoc />
    public Microphone? Microphone
    {
        get { lock (_gate) return _microphone; }
        set { lock (_gate) _microphone = value; }
    }

    /// <summary>The microphone the recording would record while none is recorded, as the test sets it: null unless set, as a recording whose look has picked none.</summary>
    public Microphone? WouldRecord
    {
        get { lock (_gate) return _wouldRecord; }
        set { lock (_gate) _wouldRecord = value; }
    }

    /// <summary>Why the microphone named is recorded, as the test sets it: Windows' default microphone unless set.</summary>
    public string? Why
    {
        get { lock (_gate) return _why; }
        set { lock (_gate) _why = value; }
    }

    /// <summary>The name asked for that the microphone named is recorded in place of, because the class app uses it, as the test sets it: null unless set.</summary>
    public string? InsteadOf
    {
        get { lock (_gate) return _insteadOf; }
        set { lock (_gate) _insteadOf = value; }
    }

    /// <summary>True when the microphone named is only waited for (gone, with nothing standing in), as the test sets it: false unless set.</summary>
    public bool Waiting
    {
        get { lock (_gate) return _waiting; }
        set { lock (_gate) _waiting = value; }
    }

    /// <summary>
    /// The microphone recorded now, or else the one it would record or waits for, with <see cref="Why"/>,
    /// <see cref="InsteadOf"/> and <see cref="Waiting"/>, read together under the lock; a move set with
    /// <see cref="MovesAfterTheNextRead"/> happens right after this read returns its answer.
    /// </summary>
    public NamedMicrophone? Named
    {
        get
        {
            lock (_gate)
            {
                var named = (_microphone ?? _wouldRecord) is { } microphone ? new NamedMicrophone(microphone, _why, _insteadOf, _waiting) : null;
                if (_move is { } move)
                {
                    _move = null;
                    (_wouldRecord, _why, _insteadOf) = move;
                }

                return named;
            }
        }
    }

    /// <summary>
    /// Moves the pick of a microphone that records nothing yet to <paramref name="to"/>, for <paramref name="why"/>, passing
    /// over <paramref name="insteadOf"/>, right after the next read of <see cref="Named"/>: as the look on another thread
    /// moved it on 7 November 2026, just after the run first asked which microphone it would record.
    /// </summary>
    public void MovesAfterTheNextRead(Microphone to, string why, string? insteadOf = null)
    {
        lock (_gate) _move = (to, why, insteadOf);
    }

    /// <inheritdoc />
    public MicrophoneProcessing? Processing
    {
        get { lock (_gate) return _processing; }
        set { lock (_gate) _processing = value; }
    }

    /// <summary>Whether Windows mutes the microphone recorded now, as the test sets it: null, as Windows not asked, unless set.</summary>
    public bool? MutedInWindows
    {
        get { lock (_gate) return _mutedInWindows; }
        set { lock (_gate) _mutedInWindows = value; }
    }

    /// <summary>The webcam the microphone belongs to and its hold, as the test sets it: null, as a microphone of no webcam, unless set.</summary>
    public WebcamState? Webcam
    {
        get { lock (_gate) return _webcam; }
        set { lock (_gate) _webcam = value; }
    }

    /// <inheritdoc />
    public TimeSpan Position => Inner.Position;

    /// <inheritdoc />
    public event EventHandler<TimelineNote>? NoteWorthy
    {
        add => Inner.NoteWorthy += value;
        remove => Inner.NoteWorthy -= value;
    }

    /// <summary>
    /// Follows <paramref name="rule"/> through a gate made for it, with <paramref name="appMute"/> saying whether the app's
    /// mute can be seen, on <paramref name="time"/>: the microphone starts switched on or off as the gate starts armed or
    /// not, as a recording with a gate does. Returns the gate, for the test to feed looks to.
    /// </summary>
    public MicrophoneGate Follow(FollowRule rule, ClassAppMute appMute, TimeProvider time)
    {
        var gate = new MicrophoneGate(appMute, rule, time, NullLogger<MicrophoneGate>.Instance);
        lock (_gate)
        {
            _follows = rule;
            _microphoneGate = gate;
        }

        Inner.SetMicrophone(gate.Armed);
        return gate;
    }

    /// <inheritdoc />
    public void Start(RecordingFiles files, TimeSpan offset) => Inner.Start(files, offset);

    /// <inheritdoc />
    public RecordingSummary Stop() => Inner.Stop();

    /// <inheritdoc />
    public void Pause() => Inner.Pause();

    /// <inheritdoc />
    public void Resume() => Inner.Resume();

    /// <summary>Switches the inner recording's microphone, and arms or disarms the gate with it, as a recording with a gate does on V.</summary>
    public bool? SetMicrophone(bool on)
    {
        var result = Inner.SetMicrophone(on);
        Gate?.Arm(on);
        return result;
    }

    /// <inheritdoc />
    public RecordingSnapshot GetSnapshot() => Inner.GetSnapshot();

    /// <inheritdoc />
    public void Reopen(Track track, string reason) => Inner.Reopen(track, reason);

    /// <inheritdoc />
    public void Flush() => Inner.Flush();

    /// <inheritdoc />
    public void Dispose() => Inner.Dispose();
}

/// <summary>Makes one <see cref="FakeMicrophoneRecording"/> the test prepared, whatever plan is asked for, and notes the plan.</summary>
public sealed class FakeMicrophoneRecordingFactory(FakeMicrophoneRecording recording) : IRecordingFactory
{
    private readonly Lock _gate = new();
    private readonly List<SourcePlan> _plans = [];

    /// <summary>The plans the recording was asked for, in order.</summary>
    public IReadOnlyList<SourcePlan> Plans
    {
        get { lock (_gate) return [.. _plans]; }
    }

    /// <inheritdoc />
    public IRecording Create(SourcePlan source)
    {
        lock (_gate) _plans.Add(source);
        return recording;
    }
}
