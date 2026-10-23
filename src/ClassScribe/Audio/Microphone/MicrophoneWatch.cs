using ClassScribe.Audio.Timeline;
using ClassScribe.Desktop;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Audio.Microphones;

/// <summary>Whether the followed app is taking sound from the recorded microphone, as one look saw it.</summary>
public enum AppListening
{
    /// <summary>It has no session on the microphone, or none that is active: nothing it could hear.</summary>
    No,

    /// <summary>At least one of its sessions on the microphone is active: it is taking sound, whether or not it does anything with it.</summary>
    Yes,

    /// <summary>
    /// The sessions could not all be read, and what could not be read may be the app's, so nothing is known: the list
    /// itself, a session whose process could not be read, or the app's own session whose state Windows would not give
    /// while its usage record says nothing of it.
    /// </summary>
    Unknown
}

/// <summary>The state of an app's session on a microphone, as Windows reports it (AudioSessionState).</summary>
public enum CaptureSessionState
{
    /// <summary>The app has the session, but none of its streams is running: no sound is being taken.</summary>
    Inactive = 0,

    /// <summary>At least one of the app's streams is running: sound is being taken from the microphone.</summary>
    Active = 1,

    /// <summary>The app has closed every stream of the session.</summary>
    Expired = 2
}

/// <summary>The roles for which Windows can make a microphone its default.</summary>
[Flags]
public enum MicrophoneRoles
{
    /// <summary>Not Windows' default for any role.</summary>
    None = 0,

    /// <summary>eConsole: Windows' default microphone for most apps, the one Sound settings show.</summary>
    Console = 1,

    /// <summary>eMultimedia: Windows' default microphone for music and video apps.</summary>
    Multimedia = 2,

    /// <summary>eCommunications: Windows' default microphone for calls, the one a class page uses.</summary>
    Communications = 4
}

/// <summary>One app's session on a microphone, as one look read it.</summary>
/// <param name="Key">The session's instance identifier, unique to the session, by which it is followed from one look to the next.</param>
/// <param name="EndpointId">The microphone the session is on.</param>
/// <param name="ProcessId">The process that has the session; 0 for a session that spans processes, such as Windows' system sounds.</param>
/// <param name="ImageName">The process's program without .exe, such as chrome; null when the process could not be looked at.</param>
/// <param name="SystemSounds">True for Windows' system sounds session, which is no app's.</param>
/// <param name="State">Whether the session is taking sound now.</param>
/// <param name="SessionMuted">The session's own mute flag, read for the log and never relied on; null when it could not be read.</param>
public sealed record CaptureSession(string Key, string EndpointId, int ProcessId, string? ImageName, bool SystemSounds, CaptureSessionState State,
    bool? SessionMuted);

/// <summary>
/// A session on a microphone that one look could not read in full, with what was read of it before the call that failed:
/// its process, read first so that a session whose other calls fail is still known by whose it is, then its state. The
/// watch reads it to tell whether what could not be read concerns the followed app at all.
/// </summary>
/// <param name="ProcessId">The process that has the session; 0 for a session that spans processes; null when not even that could be read.</param>
/// <param name="ImageName">The process's program without .exe, such as Zoom; null when the process could not be read or named.</param>
/// <param name="State">The session's state, when it was read before the call that failed; null otherwise.</param>
public sealed record UnreadCaptureSession(int? ProcessId, string? ImageName, CaptureSessionState? State);

/// <summary>What one look read about one microphone: the microphone itself, and every app's session on it.</summary>
/// <param name="EndpointId">The identifier Windows gave the microphone.</param>
/// <param name="Name">The name Windows shows for it; the identifier when the name could not be read.</param>
/// <param name="DefaultFor">The roles for which it is Windows' default.</param>
/// <param name="MutedInWindows">Its mute in Windows, the switch in Sound settings; null when that could not be read.</param>
/// <param name="VolumePercent">Its input volume in Windows, from 0 to 100; null when that could not be read.</param>
/// <param name="Sessions">Every session read on it.</param>
/// <param name="Complete">
/// True when every session on it was read, so that an app missing from <paramref name="Sessions"/> has no session; false
/// when one or more could not be read, or the list could not be read at all.
/// </param>
/// <param name="Problem">What could not be read, in words with the calls that failed and their result codes, or null when everything was.</param>
/// <param name="Unread">
/// While <paramref name="Complete"/> is false: the sessions that could not be read in full, with what was read of each,
/// when the list of sessions itself was read; null when it was not, so that what is missing is not known. Nothing is
/// unread from a complete listing, whatever this holds.
/// </param>
public sealed record MicrophoneSessionListing(string EndpointId, string Name, MicrophoneRoles DefaultFor, bool? MutedInWindows, int? VolumePercent,
    IReadOnlyList<CaptureSession> Sessions, bool Complete, string? Problem, IReadOnlyList<UnreadCaptureSession>? Unread = null);

/// <summary>What one look read about every active microphone at once: each microphone's listing, or why the list could not be read.</summary>
/// <param name="Listings">Every active microphone with its sessions; empty when the list could not be read, and when no microphone is active.</param>
/// <param name="Problem">Why the list could not be read, in words with the result code, when it could not (Windows' audio service restarting, a stale enumerator); null when it was read, however many microphones it holds.</param>
public sealed record MicrophoneListings(IReadOnlyList<MicrophoneSessionListing> Listings, string? Problem);

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

/// <summary>One app's entry in Windows' record of which apps used the microphone, and when.</summary>
/// <param name="Key">The entry's key below the record's key, such as NonPackaged\C:#Program Files#Google#Chrome#Application#chrome.exe.</param>
/// <param name="App">A short name: the program's file name (chrome.exe) for a desktop app, the package family name (MSTeams_8wekyb3d8bbwe) for a Store app.</param>
/// <param name="Path">The program's full path for a desktop app; null for a Store app.</param>
/// <param name="StartFileTime">When the app last opened the microphone, as a Windows FILETIME in UTC; null when the record does not say.</param>
/// <param name="StopFileTime">When it last closed it, the same way; 0 while it has it open; null when the record does not say.</param>
public sealed record UsageEntry(string Key, string App, string? Path, long? StartFileTime, long? StopFileTime)
{
    /// <summary>Whether Windows has recorded any use of the microphone by this app.</summary>
    public bool HasUsage => StartFileTime is > 0 || StopFileTime is > 0;

    /// <summary>Whether Windows counts the app as using the microphone now: a start time, and a stop time of 0 or none.</summary>
    public bool InUse => StartFileTime is > 0 && (StopFileTime is null or 0);

    /// <summary>The app's name as the follow rule matches names: the file name without .exe for a desktop app, the package family name for a Store app.</summary>
    public string ProcessName => App.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? App[..^4] : App;
}

/// <summary>One reading of Windows' microphone usage record.</summary>
/// <param name="Access">The record's own value: whether apps may use the microphone at all (Allow or Deny); null when it is not set.</param>
/// <param name="DesktopAccess">The same for desktop apps; null when it is not set.</param>
/// <param name="Apps">Every app's entry, used or not.</param>
/// <param name="Problem">Why the record could not be read, or null; when set, the other values mean nothing.</param>
public sealed record UsageRecordListing(string? Access, string? DesktopAccess, IReadOnlyList<UsageEntry> Apps, string? Problem);

/// <summary>
/// Reads Windows' record of which apps used the microphone and when: the record behind Settings, Privacy and security,
/// Microphone, Recent activity. It is per app, not per microphone, and Microsoft says desktop apps may be missing from it,
/// so it corroborates the sessions and names apps, and never decides anything on its own. The one place it weighs more:
/// when the followed app has a session on the recorded microphone whose state Windows will not give, as the Zoom app's
/// in a meeting may, and the app was not found taking sound from another microphone, the record's word that the app
/// uses the microphone now, or has closed it, stands in for that session's state.
/// </summary>
public interface IMicrophoneUsageRecord
{
    /// <summary>The record as it is now. A record that cannot be read gives a listing with its <see cref="UsageRecordListing.Problem"/>; nothing is thrown for it.</summary>
    UsageRecordListing Read();
}

/// <summary>
/// What one look of the watch found, on the clock the microphone's packets are stamped with. The gate judges each packet
/// by the look before its stamp and the look after it.
/// </summary>
/// <param name="Timestamp100ns">When the look began, on the performance counter in 100-nanosecond units, as the packets are stamped.</param>
/// <param name="Listening">Whether the followed app is taking sound from the recorded microphone.</param>
/// <param name="MutedInWindows">Whether the recorded microphone is muted in Windows; null when that could not be read, which counts as not muted.</param>
/// <param name="ListeningApps">The apps taking sound from the recorded microphone, by name, ClassScribe's own processes and Windows' sounds left out.</param>
/// <param name="OtherMicrophone">The name of another microphone the followed app is taking sound from, when it has no active session on the recorded one; null otherwise.</param>
/// <param name="Problem">What could not be read, when something could not; null otherwise.</param>
/// <param name="Holds">True when the followed app has a session on the recorded microphone that has not expired, active or not: it holds the microphone open.</param>
/// <param name="ProcessAlive">
/// While the followed app is not taking sound, whether the process that last did still runs: false when it has ended, so
/// that its session going is no mute of his; null while it is taking sound, before it ever did, and when no app is named.
/// </param>
/// <param name="UsageInUse">
/// What Windows' usage record says about the followed app: true when it has the microphone open now, false when it has
/// closed it, null when the app is absent from the record or in it without any recorded use, the record could not be
/// read, or no app is named.
/// </param>
/// <param name="RecordedMicrophone">The name of the recorded microphone, for the words that name it; null when it could not be read.</param>
/// <param name="OtherMicrophoneId">The endpoint id of <paramref name="OtherMicrophone"/>, for the chooser that moves the recording to it; null when there is none.</param>
public sealed record WatchLook(long Timestamp100ns, AppListening Listening, bool? MutedInWindows, IReadOnlyList<string> ListeningApps,
    string? OtherMicrophone, string? Problem, bool Holds = false, bool? ProcessAlive = null, bool? UsageInUse = null, string? RecordedMicrophone = null,
    string? OtherMicrophoneId = null);

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
        }
        else
        {
            mine = sessions.Read(endpointId);
        }

        var apps = mine.Sessions.Where(session => !session.SystemSounds && session.ProcessId > 0 && !own.IsOwn(session.ProcessId, session.ImageName)).ToList();
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

        // The followed app's own session whose state Windows would not give, as the Zoom app's in a meeting may: Windows'
        // usage record stands in for its state, unless the app was found listening on another microphone, which would
        // explain the record's word as well. A record that says nothing of the app leaves nothing known.
        var usage = ReadUsage();
        var byRecord = active.Count == 0 && stateless.Count > 0 && rule is FollowRule.Named && _otherMicrophone is null && usage is not null;
        var listening = active.Count > 0 ? AppListening.Yes
            : unread is null ? AppListening.Unknown
            : stateless.Count == 0 ? AppListening.No
            : byRecord ? (usage == true ? AppListening.Yes : AppListening.No)
            : AppListening.Unknown;
        if (byRecord && listening == AppListening.Yes) active.AddRange(stateless.Select(session => (session.ProcessId!.Value, session.ImageName)));
        NoteProblem(mine, listening, byRecord, stateless.Count > 0);
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
    /// that has lasted a second. When the followed app's own session could not be read (<paramref name="followedUnread"/>)
    /// and Windows' usage record decides instead (<paramref name="byRecord"/>), that is said once, until the session can
    /// be read again.
    /// </summary>
    private void NoteProblem(MicrophoneSessionListing mine, AppListening listening, bool byRecord, bool followedUnread)
    {
        if (!followedUnread)
        {
            _recordDecidesLogged = false;
        }
        else if (byRecord && !_recordDecidesLogged)
        {
            _recordDecidesLogged = true;
            logger.LogInformation("Windows would not give the state of {App}'s session on \"{Name}\" ({Problem}); until it does, Windows' microphone usage record says whether {Followed} is listening",
                AppWords, mine.Name, mine.Problem ?? "no reason given", AppWords);
        }

        if (mine.Problem is not { } problem)
        {
            _lookProblemLogged = false;
            return;
        }

        if (listening == AppListening.Unknown || followedUnread || _lookProblemLogged) return;

        _lookProblemLogged = true;
        logger.LogInformation("The microphone watch could not read everything about \"{Name}\" ({Problem}); the sessions it read decide, and a mute in Windows it cannot read counts as off",
            mine.Name, problem);
    }

    /// <summary>
    /// What Windows' usage record says about the followed app: open now, closed, or nothing. Only an entry with a recorded
    /// use says anything: Windows keeps entries without times for most apps that once asked for the microphone, and such
    /// an entry is neutral, as an app absent from the record is. A record that cannot be read is logged at Debug, once per
    /// failure, and says nothing: the sessions alone decide.
    /// </summary>
    private bool? ReadUsage()
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
        if (rule is not FollowRule.Named named) return null;

        var entries = listing.Apps.Where(entry => entry.HasUsage && named.Matches(entry.ProcessName)).ToList();
        return entries.Count == 0 ? null : entries.Any(entry => entry.InUse);
    }

    /// <summary>The followed app by name, or "an app" when any app is followed, as the gate words it.</summary>
    private string AppWords => rule is FollowRule.Named named ? named.NamePart : "an app";

    /// <summary>The followed app in words for the log.</summary>
    private string FollowedWords => rule switch
    {
        FollowRule.Named named => $"\"{named.NamePart}\"",
        FollowRule.Any => "any app",
        _ => "no app"
    };
}
