using System.Globalization;
using ClassScribe.Audio.Timeline;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Audio.Microphones;

/// <summary>What the gate says about one of the microphone's packets.</summary>
public enum GateVerdict
{
    /// <summary>Write it: the looks on both sides of its stamp found the followed app listening and Windows not muting.</summary>
    Write,

    /// <summary>Drop it: a look on either side found the gate closed, the looks on either side were too far apart, or it waited too long for one.</summary>
    Drop,

    /// <summary>Hold it: the look after its stamp has not happened yet.</summary>
    Undecided
}

/// <summary>Decides, packet by packet, which of the microphone's packets are written.</summary>
public interface IPacketGate
{
    /// <summary>The verdict for a packet whose first frame was recorded at <paramref name="stamp100ns"/> on the performance counter, in 100-nanosecond units.</summary>
    GateVerdict Judge(long stamp100ns);

    /// <summary>Raised when something changed the verdicts, such as a look arriving: the held packets are judged again.</summary>
    event EventHandler? Changed;
}

/// <summary>The microphone track's run, in numbers, for the end-of-class summary.</summary>
/// <param name="Heard">How long your voice was being recorded: the gate open with the microphone on.</param>
/// <param name="Stretches">How many stretches that was.</param>
/// <param name="Muted">How long the microphone was on but the followed app was not listening, had not opened it, used another one, or could not be read.</param>
/// <param name="Off">How long the microphone was switched off with V, or not yet switched on with it.</param>
/// <param name="MutedInWindows">How long the microphone was muted in Windows, whatever the app was doing: that mute closes the gate before anything else is looked at.</param>
/// <param name="Flickers">How many times the app's listening changed and changed back within a second, which the log leaves out.</param>
/// <param name="Cycles">How many times the app stopped listening for at least a second, while its process lived on, and then listened again: what an app whose mute Windows can see does on mute and unmute.</param>
public sealed record GateSummary(TimeSpan Heard, int Stretches, TimeSpan Muted, TimeSpan Off, TimeSpan MutedInWindows, int Flickers, int Cycles);

/// <summary>
/// The gate that makes the microphone track follow the class app: a packet is written only when the look before its stamp
/// and the look after it, within a second of each other, both found the app taking sound from the recorded microphone,
/// Windows not muting it, and the microphone on (V). Nothing from after a mute and nothing from before an unmute gets
/// through, at the price of up to one look's interval of heard audio at each edge. Pure: no timer, no Windows; the looks
/// come from <see cref="ClassMicrophoneWatch"/>.
/// </summary>
/// <param name="appMute">Whether the class app lets go of the microphone when you mute in it, which decides whether the microphone starts on.</param>
/// <param name="rule">Which app is followed; with none, the gate is open whenever the microphone is on.</param>
/// <param name="time">The clock, for how long a packet has waited and for the summary.</param>
/// <param name="logger">The log.</param>
public sealed class MicrophoneGate(ClassAppMute appMute, FollowRule rule, TimeProvider time, ILogger<MicrophoneGate> logger) : IPacketGate
{
    /// <summary>How long a packet may wait for the look after its stamp before it is dropped: the looks are failing.</summary>
    public static readonly TimeSpan LongestWait = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The known looks on both sides of a packet decide it only when they are this close together. Further apart, whether
    /// unknown looks or a stall of the watch lie between them, a mute and an unmute could hide between them, and the
    /// packet is dropped; so is a packet that a late look would otherwise have decided after more than <see cref="LongestWait"/>.
    /// </summary>
    public static readonly TimeSpan LongestGap = TimeSpan.FromSeconds(1);

    /// <summary>After this long on the clock without a look that could read the sessions, the last look being one that could not, nothing is written and the state says so.</summary>
    public static readonly TimeSpan UnreadableAfter = TimeSpan.FromSeconds(1);

    /// <summary>How long the usage record may say the app has closed the microphone while its session is still active before the gate follows the record.</summary>
    public static readonly TimeSpan RecordDisagreement = TimeSpan.FromSeconds(2);

    /// <summary>A change of the app's listening that reverts within this time is a flicker: counted, not logged.</summary>
    public static readonly TimeSpan Flicker = TimeSpan.FromSeconds(1);

    /// <summary>The app must have stopped listening for this long, and then listened again, for that to count as a cycle.</summary>
    public static readonly TimeSpan CycleGap = TimeSpan.FromSeconds(1);

    /// <summary>How far back looks are kept for judging packets.</summary>
    public static readonly TimeSpan LookMemory = TimeSpan.FromSeconds(2);

    private readonly Lock _gate = new();

    /// <summary>The looks kept, oldest first.</summary>
    private readonly List<Seen> _looks = [];

    /// <summary>The latest look that could read the sessions, kept beyond <see cref="LookMemory"/> for the state.</summary>
    private Seen? _lastKnown;

    /// <summary>The latest look of all.</summary>
    private Seen? _newest;

    /// <summary>When the first look came, for how long the sessions have been unreadable before any look read them.</summary>
    private long? _firstLook;

    private bool _armed = rule is FollowRule.None || appMute == ClassAppMute.Visible;

    /// <summary>Where the time since the last change of state has been counted up to, on the clock.</summary>
    private long _since = MediaTime.Now(time);

    private long _heard;
    private long _muted;
    private long _off;
    private long _mutedInWindows;
    private int _stretches = rule is FollowRule.None ? 1 : 0;
    private int _flickers;
    private int _cycles;

    /// <summary>When the usage record started saying the app had closed the microphone while its session was still active; null while the two agree.</summary>
    private long? _usageStoppedSince;

    private bool _usageDisagreementLogged;

    /// <summary>Whether the last known look found the app listening, and since when; null before the first known look.</summary>
    private bool? _listening;

    private long _listeningSince;

    /// <summary>True once the app's listening has changed from what the first known look found: the first stretch is no flicker.</summary>
    private bool _listeningChanged;

    private bool _everListened;

    /// <summary>When the app stopped listening after having listened; null while it listens, and before it ever did.</summary>
    private long? _gapSince;

    /// <summary>Whether the app's process has been alive at every look of the current gap.</summary>
    private bool _gapAlive;

    private bool _evidenceRaised;

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <summary>
    /// Raised once per run, when the followed app is first seen to let go of the microphone and take it again while
    /// <c>Recording:ClassAppMute</c> is Unknown: the evidence that its mute is visible to Windows, in words for the log.
    /// </summary>
    public event EventHandler<string>? Evidence;

    /// <summary>True while the microphone is on (V): starts true when the gate can follow the app, false when it cannot.</summary>
    public bool Armed
    {
        get { lock (_gate) return _armed; }
    }

    /// <summary>Why the microphone track is or is not being written now.</summary>
    public MicrophoneGateState State
    {
        get { lock (_gate) return Describe(MediaTime.Now(time)).State; }
    }

    /// <summary>The status line's words for <see cref="State"/>, such as "chrome is not listening"; null while recording, and when no app is followed.</summary>
    public string? Why
    {
        get { lock (_gate) return Describe(MediaTime.Now(time)).Why; }
    }

    /// <summary>
    /// <see cref="State"/> and <see cref="Why"/> together, read under one lock, so that a look arriving between two reads
    /// cannot pair one state with another state's words.
    /// </summary>
    public (MicrophoneGateState State, string? Why) Describe()
    {
        lock (_gate) return Describe(MediaTime.Now(time));
    }

    /// <summary>Switches the microphone on or off: V.</summary>
    public void Arm(bool on)
    {
        lock (_gate)
        {
            if (_armed == on) return;

            Transition(MediaTime.Now(time), () => _armed = on);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Takes a look from the watch, keeps it for judging packets, and raises <see cref="Changed"/>.</summary>
    public void Record(WatchLook look)
    {
        if (rule is FollowRule.None) return;

        string? evidence = null;
        lock (_gate)
        {
            Transition(look.Timestamp100ns, () =>
            {
                var known = look.Listening != AppListening.Unknown;
                var byRecord = known && ClosedByUsageRecord(look);
                var open = known && look.Listening == AppListening.Yes && look.MutedInWindows != true && !byRecord;
                var seen = new Seen(look, known, open, byRecord);
                Insert(seen);
                _firstLook ??= look.Timestamp100ns;
                if (_newest is null || look.Timestamp100ns >= _newest.Timestamp) _newest = seen;
                if (known)
                {
                    if (_lastKnown is null || look.Timestamp100ns >= _lastKnown.Timestamp) _lastKnown = seen;
                    evidence = NoteListening(look);
                }

                Prune();
            });
        }

        Changed?.Invoke(this, EventArgs.Empty);
        if (evidence is not null) Evidence?.Invoke(this, evidence);
    }

    /// <inheritdoc />
    public GateVerdict Judge(long stamp100ns)
    {
        lock (_gate)
        {
            if (!_armed) return GateVerdict.Drop;
            if (rule is FollowRule.None) return GateVerdict.Write;
            var now = MediaTime.Now(time);
            if (IsUnreadable(now) && stamp100ns > (_lastKnown?.Timestamp ?? long.MinValue)) return GateVerdict.Drop;

            // The known looks on either side of the stamp; unknown looks between them do not decide, only their distance does.
            Seen? knownBefore = null;
            Seen? knownAfter = null;
            foreach (var seen in _looks)
            {
                if (!seen.Known) continue;
                if (seen.Timestamp <= stamp100ns) knownBefore = seen;
                if (seen.Timestamp >= stamp100ns)
                {
                    knownAfter = seen;
                    break;
                }
            }

            if (knownAfter is null) return now - stamp100ns > LongestWait.Ticks ? GateVerdict.Drop : GateVerdict.Undecided;
            if (knownBefore is null) return GateVerdict.Drop;
            if (knownAfter.Timestamp - knownBefore.Timestamp > LongestGap.Ticks) return GateVerdict.Drop;

            return knownBefore.Open && knownAfter.Open ? GateVerdict.Write : GateVerdict.Drop;
        }
    }

    /// <summary>The run so far, in numbers.</summary>
    public GateSummary Summary()
    {
        lock (_gate)
        {
            Accrue(MediaTime.Now(time));
            return new GateSummary(TimeSpan.FromTicks(_heard), _stretches, TimeSpan.FromTicks(_muted), TimeSpan.FromTicks(_off),
                TimeSpan.FromTicks(_mutedInWindows), _flickers, _cycles);
        }
    }

    /// <summary>Counts the time up to <paramref name="at"/> against the state before <paramref name="change"/>, applies it, and counts a new stretch of recording. Call with the lock held.</summary>
    private void Transition(long at, Action change)
    {
        Accrue(at);
        var wasHeard = IsHeard(Describe(at).State);
        change();
        if (!wasHeard && IsHeard(Describe(at).State)) _stretches++;
    }

    /// <summary>
    /// Adds the time from the last count to <paramref name="until"/> to the current state's total. The one change of state
    /// that happens on the clock rather than at a look or at V, the sessions becoming unreadable, splits the count at the
    /// moment it happened. Call with the lock held.
    /// </summary>
    private void Accrue(long until)
    {
        if (until <= _since) return;
        if (UnreadableFrom() is { } from && _since < from && from < until) AccrueTo(from);

        AccrueTo(until);
    }

    /// <summary>Adds the time from the last count to <paramref name="until"/> to the total of the state at <paramref name="until"/>. Call with the lock held.</summary>
    private void AccrueTo(long until)
    {
        var elapsed = until - _since;
        _since = until;
        var state = Describe(until).State;
        if (IsHeard(state)) _heard += elapsed;
        else if (state == MicrophoneGateState.Off) _off += elapsed;
        else if (state == MicrophoneGateState.MutedInWindows) _mutedInWindows += elapsed;
        else _muted += elapsed;
    }

    /// <summary>True for the states in which your voice is being recorded.</summary>
    private static bool IsHeard(MicrophoneGateState state) => state is MicrophoneGateState.Recording or MicrophoneGateState.Unfollowed;

    /// <summary>
    /// Whether Windows' usage record has said for more than <see cref="RecordDisagreement"/> that the app has closed the
    /// microphone while its session is still active: then the stricter one wins, and the disagreement is logged once.
    /// Call with the lock held.
    /// </summary>
    private bool ClosedByUsageRecord(WatchLook look)
    {
        if (look.Listening != AppListening.Yes || look.UsageInUse != false)
        {
            _usageStoppedSince = null;
            _usageDisagreementLogged = false;
            return false;
        }

        _usageStoppedSince ??= look.Timestamp100ns;
        var closed = look.Timestamp100ns - _usageStoppedSince.Value > RecordDisagreement.Ticks;
        if (closed && !_usageDisagreementLogged)
        {
            _usageDisagreementLogged = true;
            logger.LogDebug("Windows' usage record says {App} has closed the microphone, but its session has stayed active for more than {Seconds} seconds; the record wins and nothing is written",
                AppWords, RecordDisagreement.TotalSeconds);
        }

        return closed;
    }

    /// <summary>
    /// Notes what a known look found about the app's listening: a change that reverts within a second is a flicker; the app
    /// listening again after a gap of at least a second, its process alive throughout, is a cycle, and the first cycle while
    /// the class app's mute is unknown is evidence, returned in words. Call with the lock held.
    /// </summary>
    private string? NoteListening(WatchLook look)
    {
        var listening = look.Listening == AppListening.Yes;
        var at = look.Timestamp100ns;
        if (_listening is { } last)
        {
            if (last != listening)
            {
                if (_listeningChanged && at - _listeningSince < Flicker.Ticks) _flickers++;
                _listening = listening;
                _listeningSince = at;
                _listeningChanged = true;
            }
        }
        else
        {
            _listening = listening;
            _listeningSince = at;
        }

        string? evidence = null;
        if (listening)
        {
            if (_gapSince is { } gapStart && _gapAlive && at - gapStart >= CycleGap.Ticks)
            {
                _cycles++;
                if (appMute == ClassAppMute.Unknown && !_evidenceRaised)
                {
                    _evidenceRaised = true;
                    evidence = $"{AppWords} let go of the microphone at {TimeOfDay(gapStart)} and took it again at {TimeOfDay(at)}. " +
                        "If that was you muting and unmuting in the class page, your mute is visible to Windows: " +
                        "set Recording:ClassAppMute to Visible and ClassScribe will follow it by itself.";
                }
            }

            _gapSince = null;
            _everListened = true;
        }
        else if (_everListened)
        {
            if (_gapSince is null)
            {
                _gapSince = at;
                _gapAlive = look.ProcessAlive != false;
            }
            else
            {
                _gapAlive &= look.ProcessAlive != false;
            }
        }

        return evidence;
    }

    /// <summary>The time of day, on the laptop's clock, at <paramref name="timestamp100ns"/> on the steady clock.</summary>
    private string TimeOfDay(long timestamp100ns)
    {
        var ago = TimeSpan.FromTicks(MediaTime.Now(time) - timestamp100ns);
        return (time.GetLocalNow() - ago).ToString("HH:mm:ss", CultureInfo.InvariantCulture);
    }

    /// <summary>Keeps the looks in time order. Call with the lock held.</summary>
    private void Insert(Seen seen)
    {
        var index = _looks.Count;
        while (index > 0 && _looks[index - 1].Timestamp > seen.Timestamp) index--;
        _looks.Insert(index, seen);
    }

    /// <summary>Forgets the looks older than <see cref="LookMemory"/> before the newest. Call with the lock held.</summary>
    private void Prune()
    {
        if (_newest is null) return;

        var keepFrom = _newest.Timestamp - LookMemory.Ticks;
        var stale = 0;
        while (stale < _looks.Count && _looks[stale].Timestamp < keepFrom) stale++;
        if (stale > 0) _looks.RemoveRange(0, stale);
    }

    /// <summary>
    /// The moment on the clock from which the sessions count as unreadable: <see cref="UnreadableAfter"/> after the last
    /// look that read them (or the first look, when none has), while the newest look is one that could not. Null while
    /// the newest look read them, and before any look: the clock alone makes the gate unreadable, since a watch that has
    /// failed sends no look after its last one. Call with the lock held.
    /// </summary>
    private long? UnreadableFrom() =>
        _newest is { Known: false } ? (_lastKnown?.Timestamp ?? _firstLook ?? _newest.Timestamp) + UnreadableAfter.Ticks : null;

    /// <summary>True when, at <paramref name="now"/> on the clock, no look has read the sessions for more than <see cref="UnreadableAfter"/>. Call with the lock held.</summary>
    private bool IsUnreadable(long now) => UnreadableFrom() is { } from && now > from;

    /// <summary>The state and its words at <paramref name="now"/> on the clock, from the microphone's switch and the looks. Call with the lock held.</summary>
    private (MicrophoneGateState State, string? Why) Describe(long now)
    {
        if (!_armed) return (MicrophoneGateState.Off, OffWords);
        if (rule is FollowRule.None) return (MicrophoneGateState.Unfollowed, null);
        if (_newest is null) return (MicrophoneGateState.NotOpened, WaitingWords);
        if (IsUnreadable(now)) return (MicrophoneGateState.Unreadable, $"cannot read {SessionWords}: {_newest.Look.Problem ?? "no reason given"}");
        if (_lastKnown is not { } seen) return (MicrophoneGateState.NotOpened, WaitingWords);

        var look = seen.Look;
        if (look.MutedInWindows == true) return (MicrophoneGateState.MutedInWindows, "muted in Windows");
        if (look.Listening == AppListening.Yes)
        {
            return seen.ByRecord
                ? (MicrophoneGateState.NotListening, $"{AppWords} is not listening (Windows' usage record says it closed the microphone)")
                : (MicrophoneGateState.Recording, null);
        }

        if (look.Holds) return (MicrophoneGateState.NotListening, NotListeningWords);
        if (look.OtherMicrophone is { } other)
            return (MicrophoneGateState.OtherMicrophone, $"{AppWords} uses \"{other}\"; ClassScribe records \"{look.RecordedMicrophone ?? "the microphone"}\"");
        return (MicrophoneGateState.NotOpened, WaitingWords);
    }

    /// <summary>The followed app by name, or "an app" when any app is followed.</summary>
    private string AppWords => rule is FollowRule.Named named ? named.NamePart : "an app";

    /// <summary>The words for the microphone switched off.</summary>
    private string OffWords => appMute == ClassAppMute.Visible || rule is FollowRule.None
        ? "off (V switches it on)"
        : $"off (V records your voice while {AppWords} holds the microphone)";

    /// <summary>The words for the app not having opened the microphone.</summary>
    private string WaitingWords => $"waiting for {AppWords} to open the microphone";

    /// <summary>The words for the app holding the microphone without listening.</summary>
    private string NotListeningWords => rule is FollowRule.Named named ? $"{named.NamePart} is not listening" : "no app is listening";

    /// <summary>What cannot be read, in the words for the unreadable state.</summary>
    private string SessionWords => rule is FollowRule.Named named ? $"{named.NamePart}'s microphone session" : "the microphone's sessions";

    /// <summary>A look as the gate keeps it: whether it read the sessions, whether it found the gate open, and whether the usage record closed it.</summary>
    private sealed record Seen(WatchLook Look, bool Known, bool Open, bool ByRecord)
    {
        /// <summary>When the look began, on the clock.</summary>
        public long Timestamp => Look.Timestamp100ns;
    }
}
