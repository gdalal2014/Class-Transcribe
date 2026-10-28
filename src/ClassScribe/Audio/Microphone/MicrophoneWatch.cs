using ClassScribe.Audio.Timeline;
using ClassScribe.Desktop;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Audio.Microphones;

/// <summary>
/// Reads which apps have a microphone open, and whether the microphone is muted in Windows, from Windows' own lists.
/// Reading never opens a microphone: no audio client is ever asked for, and every object Windows hands over is released
/// as soon as it has been read.
/// </summary>
public interface IMicrophoneSessions
{
    /// <summary>
    /// The sessions on the microphone <paramref name="endpointId"/> and its mute, as they are now. A microphone that is
    /// not connected, or a list that cannot be read, gives a listing with its <see cref="MicrophoneSessionListing.Problem"/>
    /// and no sessions; nothing is thrown for them.
    /// </summary>
    MicrophoneSessionListing Read(string endpointId);

    /// <summary>
    /// The same for every active microphone, for the look that finds the followed app on another microphone than the
    /// recorded one, and for the microphone check. A list that cannot be read gives no listings and says why
    /// (<see cref="MicrophoneListings.Problem"/>), so that it is not taken for every microphone having gone; nothing is thrown.
    /// </summary>
    MicrophoneListings ReadAll();
}

/// <summary>
/// Reads Windows' record of which apps used the microphone and when: the record behind Settings, Privacy and security,
/// Microphone, Recent activity. It is per app, not per microphone, and Microsoft says desktop apps may be missing from it,
/// so it corroborates the sessions and names apps, and never decides anything on its own. The one place it weighs more:
/// when the followed app has a session on the recorded microphone whose state Windows will not give, as the Zoom app's
/// in a meeting may, and the app was not found taking sound from another microphone, the record's word that the app
/// uses the microphone now, or has closed it, stands in for that session's state. Following any app (auto), the app is
/// the session's own program, found in the record by its whole name.
/// </summary>
public interface IMicrophoneUsageRecord
{
    /// <summary>The record as it is now. A record that cannot be read gives a listing with its <see cref="UsageRecordListing.Problem"/>; nothing is thrown for it.</summary>
    UsageRecordListing Read();
}

/// <summary>
/// Watches, four times a second on a thread of its own, whether the class app has the recorded microphone open and is
/// taking sound from it, and whether the microphone is muted in Windows, and hands each look to whoever listens: the gate
/// that decides which of the microphone's packets are written. It reads Windows' own lists and never opens a microphone.
/// Every ten seconds the look covers every microphone, to find the followed app taking sound from another one.
/// </summary>
/// <param name="sessions">Reads the sessions on a microphone and its mute.</param>
/// <param name="record">Reads Windows' microphone usage record, which corroborates the sessions.</param>
/// <param name="names">Names processes, to tell whether the app's process still runs once its session has gone.</param>
/// <param name="rule">Which app to follow: by name, any app, or none.</param>
/// <param name="own">Tells ClassScribe's own processes, whose sessions never count as an app's.</param>
/// <param name="time">The clock the looks are stamped on, the same as the packets'.</param>
/// <param name="logger">The log.</param>
public sealed class ClassMicrophoneWatch(IMicrophoneSessions sessions, IMicrophoneUsageRecord record, IProcessNames names, FollowRule rule,
    IOwnProcesses own, TimeProvider time, ILogger<ClassMicrophoneWatch> logger) : IDisposable
{
    /// <summary>How often the watch looks: the longest a mute or an unmute can go unseen, and the longest a packet waits to be judged.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(250);

    /// <summary>How often a look covers every microphone rather than the recorded one alone.</summary>
    public static readonly TimeSpan AllMicrophonesInterval = TimeSpan.FromSeconds(10);

    /// <summary>How long Stop waits for the thread.</summary>
    public static readonly TimeSpan StopWait = TimeSpan.FromSeconds(5);

    /// <summary>The name of the watch's thread.</summary>
    public const string ThreadName = "ClassScribe microphone watch";

    /// <summary>Held by one look at a time, and around the fields the looks and the callers share.</summary>
    private readonly Lock _gate = new();

    private string? _endpointId;

    /// <summary>When the last look over every microphone began; null when the next look must be one.</summary>
    private long? _lastAllLook;

    /// <summary>Another microphone the followed app was taking sound from at the last look over every microphone, by id and name; null when none.</summary>
    private (string Id, string Name)? _otherMicrophone;

    /// <summary>
    /// Following any app: the programs, by name, that the last look over every microphone found taking sound from another
    /// microphone than the recorded one, whose word in the usage record that one explains. Empty for any other rule.
    /// </summary>
    private HashSet<string> _appsElsewhere = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The process behind the followed app's last active session, whose life says whether a gap is a mute or the app closing.</summary>
    private int? _lastActivePid;

    private bool _recordProblemLogged;
    private bool _lookProblemLogged;

    /// <summary>True once it has been said that Windows' usage record decides for the followed app's unreadable session, until that session can be read again.</summary>
    private bool _recordDecidesLogged;
    private Thread? _thread;
    private CancellationTokenSource? _stopping;
    private Exception? _failure;

    /// <summary>Raised after each look, on the watch's thread, or on the caller's for <see cref="LookNow"/>.</summary>
    public event EventHandler<WatchLook>? Looked;

    /// <summary>Raised once, on the watch's thread, when the watch stops because of a failure it did not expect (see <see cref="Failure"/>).</summary>
    public event EventHandler<Exception>? Failed;

    /// <summary>The failure that stopped the watch, or null while it runs or was stopped on purpose.</summary>
    public Exception? Failure
    {
        get { lock (_gate) return _failure; }
    }

    /// <summary>The microphone the watch reads, by endpoint id; null before <see cref="Start"/> or <see cref="Follow"/>.</summary>
    public string? EndpointId
    {
        get { lock (_gate) return _endpointId; }
    }

    /// <summary>Starts looking at the microphone <paramref name="endpointId"/>, with a first look at once on the thread.</summary>
    public void Start(string endpointId)
    {
        lock (_gate)
        {
            if (_thread is not null) throw new InvalidOperationException("The microphone watch has already been started.");

            Point(endpointId);
            _stopping = new CancellationTokenSource();
            var token = _stopping.Token;
            _thread = new Thread(() => Run(token)) { Name = ThreadName, IsBackground = true };
            if (OperatingSystem.IsWindows()) _thread.SetApartmentState(ApartmentState.MTA);
            _thread.Start();
        }

        logger.LogDebug("Watching the microphone {EndpointId} for {Followed}, every {Interval} ms", endpointId, FollowedWords, Interval.TotalMilliseconds);
    }

    /// <summary>Reads the microphone <paramref name="endpointId"/> from the next look on: the recording moved to it.</summary>
    public void Follow(string endpointId)
    {
        lock (_gate) Point(endpointId);
    }

    /// <summary>
    /// Looks now, on the caller's thread, and returns what was found, after handing it to <see cref="Looked"/>: at Stop, so
    /// that every held packet is judged, and when V arms the microphone, so that the first packets are judged by a fresh
    /// look. After a failure (see <see cref="Failure"/>) nothing is read and the look says so.
    /// </summary>
    public WatchLook LookNow()
    {
        WatchLook look;
        lock (_gate)
        {
            look = _failure is { } failure ? FailureLook(failure) : Look();
        }

        Looked?.Invoke(this, look);
        return look;
    }

    /// <summary>Stops the thread and waits for it, up to <see cref="StopWait"/>.</summary>
    public void Stop()
    {
        Thread? thread;
        CancellationTokenSource? stopping;
        lock (_gate)
        {
            thread = _thread;
            stopping = _stopping;
            _thread = null;
            _stopping = null;
        }

        if (thread is null || stopping is null) return;

        stopping.Cancel();
        if (!thread.Join(StopWait))
            logger.LogWarning("The microphone watch did not stop within {Seconds} seconds; it is left to end by itself", StopWait.TotalSeconds);
        stopping.Dispose();
        logger.LogDebug("The microphone watch has stopped");
    }

    /// <summary>Stops the thread.</summary>
    public void Dispose() => Stop();

    /// <summary>Points the looks at <paramref name="endpointId"/>, with a fresh look over every microphone next. Call with the lock held.</summary>
    private void Point(string endpointId)
    {
        _endpointId = endpointId;
        _lastAllLook = null;
        _otherMicrophone = null;
        _appsElsewhere.Clear();
    }

    /// <summary>The thread: a look at once, then one every <see cref="Interval"/> until stopped.</summary>
    private void Run(CancellationToken stopping)
    {
        try
        {
            using var wake = new ManualResetEventSlim(false);
            using var timer = time.CreateTimer(_ => wake.Set(), null, Interval, Interval);
            while (true)
            {
                WatchLook look;
                lock (_gate) look = Look();
                Looked?.Invoke(this, look);

                wake.Wait(stopping);
                wake.Reset();
            }
        }
        catch (OperationCanceledException)
        {
            // Deliberately not rethrown: Stop ends the wait this way, and that is the thread's normal end.
        }
        catch (Exception ex)
        {
            // Deliberately not rethrown: this runs on a thread of its own, where an exception would end the process, and the
            // class with it. The watch stops instead: the failure is logged and handed on, and a last look that says nothing can
            // be read goes to the gate, which then drops every packet, so that nothing is recorded that the class app was not
            // listening to. The class is still recorded.
            WatchLook look;
            lock (_gate)
            {
                _failure = ex;
                look = FailureLook(ex);
            }

            logger.LogError(ex, "The microphone watch stopped: {Reason}; your voice is not recorded for the rest of this run", ex.Message);
            Looked?.Invoke(this, look);
            Failed?.Invoke(this, ex);
        }
    }

    /// <summary>A look that says the watch has stopped with <paramref name="failure"/>, stamped now.</summary>
    private WatchLook FailureLook(Exception failure) =>
        new(MediaTime.Now(time), AppListening.Unknown, null, [], null, $"the microphone watch stopped: {failure.Message}");

    /// <summary>One look at the recorded microphone, or at every microphone when one is due. Call with the lock held.</summary>
    private WatchLook Look()
    {
        if (_endpointId is not { } endpointId)
            throw new InvalidOperationException("No microphone to watch has been named: Start or Follow names it.");

        var timestamp = MediaTime.Now(time);
        MicrophoneSessionListing mine;
        if (_lastAllLook is null || timestamp - _lastAllLook.Value >= AllMicrophonesInterval.Ticks)
        {
            // A list that could not be read is empty here, and the recorded microphone is read on its own, which says why when that fails too.
            var listings = sessions.ReadAll().Listings;
            mine = listings.FirstOrDefault(listing => listing.EndpointId == endpointId) ?? sessions.Read(endpointId);
            _lastAllLook = timestamp;
            var elsewhere = rule is FollowRule.Named named
                ? listings.FirstOrDefault(listing => listing.EndpointId != endpointId && listing.Sessions.Any(session => IsFollowed(session, named) && session.State == CaptureSessionState.Active))
                : null;
            _otherMicrophone = elsewhere is null ? null : (elsewhere.EndpointId, elsewhere.Name);
            _appsElsewhere = rule is FollowRule.Any
                ? listings.Where(listing => listing.EndpointId != endpointId)
                    .SelectMany(listing => listing.Sessions)
                    .Where(session => IsAppsSession(session) && session.State == CaptureSessionState.Active)
                    .Select(session => session.ImageName)
                    .OfType<string>()
                    .ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new(StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            mine = sessions.Read(endpointId);
        }

        var apps = mine.Sessions.Where(IsAppsSession).ToList();
        var followed = rule switch
        {
            FollowRule.Named named => apps.Where(session => IsFollowed(session, named)).ToList(),
            FollowRule.Any => apps,
            _ => []
        };
        var active = followed.Where(session => session.State == CaptureSessionState.Active).Select(session => (session.ProcessId, session.ImageName)).ToList();

        // The sessions that could not be read in full and may be the followed app's; null when what is missing is not known.
        var unread = UnreadOfFollowed(mine);
        if (unread is not null) active.AddRange(unread.Where(session => session.State == CaptureSessionState.Active).Select(session => (session.ProcessId!.Value, session.ImageName)));
        var stateless = unread?.Where(session => session.State is null).ToList() ?? [];
        var holds = followed.Any(session => session.State != CaptureSessionState.Expired) || stateless.Count > 0 ||
            unread?.Any(session => session.State is CaptureSessionState.Active or CaptureSessionState.Inactive) == true;

        // The followed app's own session whose state Windows would not give, as the Zoom app's in a meeting may, or following
        // any app, any app's: Windows' usage record stands in for its state (see ByRecord), unless the app was found
        // listening on another microphone, which would explain the record's word as well. A record that says nothing of the
        // app leaves nothing known.
        var usageRecord = ReadRecord();
        var usage = rule is FollowRule.Named followedApp ? WordOf(usageRecord, followedApp.Matches) : null;
        var byRecord = active.Count == 0 && stateless.Count > 0 ? ByRecord(stateless, usageRecord, usage) : (Word: null, Listening: []);
        var listening = active.Count > 0 ? AppListening.Yes
            : unread is null ? AppListening.Unknown
            : stateless.Count == 0 ? AppListening.No
            : byRecord.Word is { } word ? (word ? AppListening.Yes : AppListening.No)
            : AppListening.Unknown;
        active.AddRange(byRecord.Listening.Select(session => (session.ProcessId!.Value, session.ImageName)));
        NoteProblem(mine, listening, byRecord.Word is not null, stateless);
        var listeningApps = apps.Where(session => session.State == CaptureSessionState.Active).Select(session => (session.ProcessId, session.ImageName))
            .Concat(active)
            .Select(session => session.ImageName ?? $"process {session.ProcessId}")
            .Distinct()
            .ToList();

        bool? processAlive = null;
        if (rule is FollowRule.Named)
        {
            if (active.Count > 0) _lastActivePid = active[0].ProcessId;
            else if (_lastActivePid is { } pid) processAlive = names.Describe(pid) is not null;
        }

        var other = active.Count == 0 ? _otherMicrophone : null;
        return new WatchLook(timestamp, listening, mine.MutedInWindows, listeningApps, other?.Name, mine.Problem,
            holds, processAlive, usage, mine.Name, other?.Id);
    }

    /// <summary>True when the session is the followed app's, by the process's name.</summary>
    private static bool IsFollowed(CaptureSession session, FollowRule.Named named) => session.ImageName is { } name && named.Matches(name);

    /// <summary>True when the session is an app's: one process's, neither Windows' sounds nor ClassScribe's own.</summary>
    private bool IsAppsSession(CaptureSession session) => !session.SystemSounds && session.ProcessId > 0 && !own.IsOwn(session.ProcessId, session.ImageName);

    /// <summary>
    /// What Windows' usage record says of <paramref name="stateless"/>, the sessions on the recorded microphone whose state
    /// Windows would not give, as the Zoom app's in a meeting may: whether they count as listening (true), not (false) or
    /// nothing is known (null), with those that count as listening. Following one app, its word (<paramref name="usage"/>)
    /// stands for them all, unless the app was found taking sound from another microphone, which would explain the word as
    /// well. Following any app, each session is judged by its own program's entry in <paramref name="usageRecord"/>, found
    /// by its whole name, unless that program was found taking sound from another microphone: one in use is enough for
    /// listening, and not listening needs every one of them closed; a session whose program could not be named, or that the
    /// record says nothing of, leaves nothing known otherwise.
    /// </summary>
    private (bool? Word, IReadOnlyList<UnreadCaptureSession> Listening) ByRecord(List<UnreadCaptureSession> stateless, UsageRecordListing? usageRecord, bool? usage)
    {
        if (rule is FollowRule.Named)
        {
            var word = _otherMicrophone is null ? usage : null;
            return (word, word == true ? stateless : []);
        }

        if (rule is not FollowRule.Any) return (null, []);

        var words = stateless.Select(session => (Session: session, Word: session.ImageName is { } program && !_appsElsewhere.Contains(program)
            ? WordOf(usageRecord, name => name.Equals(program, StringComparison.OrdinalIgnoreCase))
            : null)).ToList();
        var listening = words.Where(judged => judged.Word == true).Select(judged => judged.Session).ToList();
        return listening.Count > 0 ? (true, listening)
            : words.All(judged => judged.Word == false) ? (false, [])
            : (null, []);
    }

    /// <summary>
    /// The sessions on the recorded microphone that could not be read in full and are, or may be, the followed app's:
    /// none when the listing is complete, or when each of them is known by its process to be another app's, ClassScribe's
    /// own, or no single process's; null when what could not be read is not known (the list itself, or a session whose
    /// process could not be read), since any app may hide there. A session whose process was named but not its program
    /// counts as another's, as a session read in full without a name does.
    /// </summary>
    private List<UnreadCaptureSession>? UnreadOfFollowed(MicrophoneSessionListing listing)
    {
        if (listing.Complete) return [];
        if (listing.Unread is not { } unread || unread.Any(session => session.ProcessId is null)) return null;

        return [.. unread.Where(session => session.ProcessId > 0 && !own.IsOwn(session.ProcessId.Value, session.ImageName) && rule switch
        {
            FollowRule.Named named => session.ImageName is { } name && named.Matches(name),
            FollowRule.Any => true,
            _ => false
        })];
    }

    /// <summary>
    /// Logs, once until it clears, what a look could not read about the microphone although it read enough of the
    /// sessions to know whether the app is listening: the mute in Windows, most likely, which then counts as off, or
    /// another app's session. A look that knows nothing is not logged here: the gate reports it, with the reason, once
    /// that has lasted a second. When the state of the followed app's own session, or following any app any app's, could
    /// not be read (<paramref name="stateless"/>) and Windows' usage record decides instead (<paramref name="byRecord"/>),
    /// that is said once, naming the app, until every session's state can be read again.
    /// </summary>
    private void NoteProblem(MicrophoneSessionListing mine, AppListening listening, bool byRecord, IReadOnlyList<UnreadCaptureSession> stateless)
    {
        if (stateless.Count == 0)
        {
            _recordDecidesLogged = false;
        }
        else if (byRecord && !_recordDecidesLogged)
        {
            _recordDecidesLogged = true;
            var app = rule is FollowRule.Named named
                ? named.NamePart
                : string.Join(" and ", stateless.Select(session => session.ImageName).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase));
            logger.LogInformation("Windows would not give the state of {App}'s session on \"{Name}\" ({Problem}); until it does, Windows' microphone usage record says whether {Followed} is listening",
                app, mine.Name, mine.Problem ?? "no reason given", app);
        }

        if (mine.Problem is not { } problem)
        {
            _lookProblemLogged = false;
            return;
        }

        if (listening == AppListening.Unknown || stateless.Count > 0 || _lookProblemLogged) return;

        _lookProblemLogged = true;
        logger.LogInformation("The microphone watch could not read everything about \"{Name}\" ({Problem}); the sessions it read decide, and a mute in Windows it cannot read counts as off",
            mine.Name, problem);
    }

    /// <summary>
    /// Windows' usage record as it is now; null when it cannot be read, which is logged at Debug, once per failure, and
    /// then says nothing: the sessions alone decide.
    /// </summary>
    private UsageRecordListing? ReadRecord()
    {
        var listing = record.Read();
        if (listing.Problem is { } problem)
        {
            if (!_recordProblemLogged)
            {
                _recordProblemLogged = true;
                logger.LogDebug("Windows' microphone usage record could not be read ({Problem}); the sessions alone decide", problem);
            }

            return null;
        }

        _recordProblemLogged = false;
        return listing;
    }

    /// <summary>
    /// What <paramref name="usageRecord"/> says about the app whose entries <paramref name="matches"/> picks by name: open
    /// now, closed, or nothing. Only an entry with a recorded use says anything: Windows keeps entries without times for
    /// most apps that once asked for the microphone, and such an entry is neutral, as an app absent from the record is. A
    /// record that could not be read (null) says nothing.
    /// </summary>
    private static bool? WordOf(UsageRecordListing? usageRecord, Func<string, bool> matches)
    {
        if (usageRecord is null) return null;

        var entries = usageRecord.Apps.Where(entry => entry.HasUsage && matches(entry.ProcessName)).ToList();
        return entries.Count == 0 ? null : entries.Any(entry => entry.InUse);
    }

    /// <summary>The followed app in words for the log.</summary>
    private string FollowedWords => rule switch
    {
        FollowRule.Named named => $"\"{named.NamePart}\"",
        FollowRule.Any => "any app",
        _ => "no app"
    };
}
