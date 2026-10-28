using System.Globalization;
using ClassScribe.Desktop;

namespace ClassScribe.Audio.Microphones;

/// <summary>
/// The microphone check's change tracker, MicWatch's inside ClassScribe: it compares each look with the one before and
/// answers with one line per change, as the log and the window show them. A microphone or a session that appeared,
/// changed or went; an app whose use of the microphone started or stopped in Windows' usage record; a source that could
/// not be read, once, and a note when it can be read again. The first look writes everything, marked "at start", as the
/// baseline; a changed value is followed by "(was ...)" with the value before. It is pure: it reads nothing itself, and
/// takes what the readers found.
/// </summary>
/// <param name="names">Names the process behind a session, for its program's path on the session's first line, and the check's own program.</param>
/// <param name="own">Tells ClassScribe's own processes: a recorder beside the check holds a microphone, and is labelled as such rather than taken for a stranger.</param>
/// <param name="ownProcessId">The check's own process, which must never have a session or be using the microphone by the usage record.</param>
/// <param name="zone">The laptop's time zone, in which the usage record's times are shown, each as the clock showed it at that moment.</param>
public sealed class MicrophoneChangeTracker(IProcessNames names, IOwnProcesses own, int ownProcessId, TimeZoneInfo zone)
{
    /// <summary>How the microphones' list is named in the ERROR and NOTE lines.</summary>
    private const string MicrophonesSource = "Windows' list of microphones";

    /// <summary>How the usage record is named in the ERROR and NOTE lines.</summary>
    private const string RecordSource = "Windows' microphone usage record";

    /// <summary>What the SELF lines ask for.</summary>
    private const string SaySo = "This should never happen; please say so when you send the log.";

    /// <summary>The microphones at the last readable look, by endpoint id; null before the first.</summary>
    private Dictionary<string, MicrophoneFacts>? _microphones;

    /// <summary>The microphones whose sessions could not all be read at the last look, by endpoint id.</summary>
    private readonly HashSet<string> _incomplete = new(StringComparer.Ordinal);

    /// <summary>The sessions being followed, by instance identifier.</summary>
    private readonly Dictionary<string, TrackedSession> _sessions = new(StringComparer.Ordinal);

    /// <summary>The usage record's entries at the last readable look, by key; null before the first.</summary>
    private Dictionary<string, UsageEntry>? _usage;

    /// <summary>The record's two access settings at the last readable look.</summary>
    private string? _access, _desktopAccess;

    /// <summary>The problem last reported for each source, so that a problem that repeats is written once.</summary>
    private string? _microphonesProblem, _recordProblem;

    /// <summary>The number given to the last new session.</summary>
    private int _lastSessionNumber;

    /// <summary>
    /// True when, at the last look, another ClassScribe process held a microphone open: the recorder of a class recorded
    /// beside the check, whose use of the microphone the usage record then shows under the same program as the check's.
    /// </summary>
    private bool _otherOwnHoldsMicrophone;

    /// <summary>
    /// The same for the look before the last: the record can show the recorder's use a look before its session is listed,
    /// or after its session has gone, as when the recorder moves to another microphone between two looks.
    /// </summary>
    private bool _otherOwnHeldMicrophoneBefore;

    /// <summary>The check's own program, by path, once looked up; the look-up is made at the first use.</summary>
    private string? _ownPath;

    private bool _ownPathLookedUp;

    /// <summary>The lines for what changed among the microphones and their sessions since the last look, at <paramref name="at"/>.</summary>
    public IReadOnlyList<MicCheckLine> Update(IReadOnlyList<MicrophoneSessionListing> microphones, DateTimeOffset at)
    {
        var lines = new List<MicCheckLine>();
        NoteReadable(ref _microphonesProblem, MicrophonesSource, at, lines);

        var first = _microphones is null;
        var current = new Dictionary<string, MicrophoneFacts>(StringComparer.Ordinal);
        foreach (var microphone in microphones)
        {
            var facts = FactsOf(microphone);
            current[microphone.EndpointId] = facts;
            if (first)
                lines.Add(Line(at, MicCheckLine.Mic, $"{facts.Name}: at start: {Describe(facts, null)}"));
            else if (!_microphones!.TryGetValue(microphone.EndpointId, out var before))
                lines.Add(Line(at, MicCheckLine.Mic, $"{facts.Name}: became active: {Describe(facts, null)}"));
            else if (before != facts)
                lines.Add(Line(at, MicCheckLine.Mic, $"{facts.Name}: changed: {Describe(facts, before)}"));
        }

        if (first && microphones.Count == 0) lines.Add(Line(at, MicCheckLine.Mic, "No microphone is active at start."));
        if (!first)
        {
            foreach (var gone in _microphones!.Values.Where(microphone => !current.ContainsKey(microphone.EndpointId)))
                lines.Add(Line(at, MicCheckLine.Mic, $"{gone.Name}: no longer active (unplugged, disabled or removed)"));
        }

        _microphones = current;
        _incomplete.Clear();
        foreach (var microphone in microphones.Where(microphone => !microphone.Complete)) _incomplete.Add(microphone.EndpointId);
        UpdateSessions(microphones, current, first, at, lines);
        return lines;
    }

    /// <summary>
    /// The lines for the microphones' list failing to be read at all, for <paramref name="problem"/>: an ERROR line the first
    /// time and whenever the problem changes, nothing while it repeats. The next readable look adds a NOTE that the list can
    /// be read again.
    /// </summary>
    public IReadOnlyList<MicCheckLine> MicrophonesUnreadable(string problem, DateTimeOffset at) => Unreadable(ref _microphonesProblem, MicrophonesSource, problem, at);

    /// <summary>
    /// The lines for what changed in Windows' microphone usage record since the last look, at <paramref name="at"/>; a record
    /// that could not be read (its <see cref="UsageRecordListing.Problem"/>) is an ERROR line once, as <see cref="UsageRecordUnreadable"/> gives it.
    /// </summary>
    public IReadOnlyList<MicCheckLine> Update(UsageRecordListing record, DateTimeOffset at)
    {
        if (record.Problem is { } problem) return UsageRecordUnreadable(problem, at);

        var lines = new List<MicCheckLine>();
        NoteReadable(ref _recordProblem, RecordSource, at, lines);

        var first = _usage is null;
        if (first || record.Access != _access || record.DesktopAccess != _desktopAccess)
        {
            lines.Add(Line(at, MicCheckLine.Privacy, "Windows settings: " +
                Field("microphone access:", record.Access ?? "not set", first ? null : _access ?? "not set") + "; " +
                Field("desktop apps:", record.DesktopAccess ?? "not set", first ? null : _desktopAccess ?? "not set")));
        }

        _access = record.Access;
        _desktopAccess = record.DesktopAccess;

        var current = new Dictionary<string, UsageEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in record.Apps) current[app.Key] = app;

        foreach (var app in record.Apps.Where(app => app.HasUsage))
        {
            if (first)
                lines.Add(Line(at, MicCheckLine.Privacy, $"{app.App}: at start: {DescribeUsage(app, at)}{Where(app)}"));
            else if (!_usage!.TryGetValue(app.Key, out var before) || !before.HasUsage)
                lines.Add(Line(at, MicCheckLine.Privacy, $"{app.App}: first use recorded: {DescribeUsage(app, at)}{Where(app)}"));
            else if (before == app)
                continue;
            else
                lines.Add(Line(at, MicCheckLine.Privacy, $"{app.App}: {DescribeChange(before, app, at)}"));

            // The record keeps each program's last use for ever, so a past use is the recorder's, from any class it recorded;
            // only a use going on now, with no recorder holding a microphone at this look or the one before, is the check's own.
            if (app.InUse && IsOwnProgram(app) && !_otherOwnHoldsMicrophone && !_otherOwnHeldMicrophoneBefore)
            {
                lines.Add(Line(at, MicCheckLine.Self,
                    $"ClassScribe's own program is using the microphone now, by Windows' microphone usage record, and no other ClassScribe process holds a microphone. {SaySo}"));
            }
        }

        if (first)
        {
            var unused = record.Apps.Where(app => !app.HasUsage).Select(app => app.App).ToList();
            if (unused.Count > 0) lines.Add(Line(at, MicCheckLine.Privacy, $"listed with no use recorded: {string.Join(", ", unused)}"));
        }
        else
        {
            foreach (var gone in _usage!.Values.Where(app => app.HasUsage && !current.ContainsKey(app.Key)))
                lines.Add(Line(at, MicCheckLine.Privacy, $"{gone.App}: its entry was removed from the record"));
        }

        _usage = current;
        return lines;
    }

    /// <summary>
    /// The lines for the usage record failing to be read, for <paramref name="problem"/>: an ERROR line the first time and
    /// whenever the problem changes, nothing while it repeats. The next readable look adds a NOTE that it can be read again.
    /// </summary>
    public IReadOnlyList<MicCheckLine> UsageRecordUnreadable(string problem, DateTimeOffset at) => Unreadable(ref _recordProblem, RecordSource, problem, at);

    /// <summary>The lines for the sessions that appeared, changed or went, and notes whether another ClassScribe process holds a microphone.</summary>
    private void UpdateSessions(IReadOnlyList<MicrophoneSessionListing> listings, Dictionary<string, MicrophoneFacts> current, bool first, DateTimeOffset at,
        List<MicCheckLine> lines)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var otherOwnHolds = false;
        foreach (var listing in listings)
        {
            foreach (var session in listing.Sessions)
            {
                seen.Add(session.Key);
                var otherOwn = IsOtherOwn(session);
                if (otherOwn && session.State != CaptureSessionState.Expired) otherOwnHolds = true;

                if (!_sessions.TryGetValue(session.Key, out var tracked))
                {
                    var (name, path) = Name(session, otherOwn);
                    var endpointName = current.TryGetValue(session.EndpointId, out var microphone) ? microphone.Name : listing.Name;
                    tracked = new TrackedSession(++_lastSessionNumber, name, endpointName, session);
                    _sessions[session.Key] = tracked;
                    var program = path is null ? "" : $"; program {path}";
                    lines.Add(Line(at, MicCheckLine.Session, $"{Label(tracked)}: {(first ? "at start" : "appeared")}: {Describe(session, null)}{program}; session id {session.Key}"));
                    if (session.ProcessId == ownProcessId)
                        lines.Add(Line(at, MicCheckLine.Self, $"ClassScribe's own process has a session on this microphone. {SaySo}"));
                }
                else if (tracked.Last != session)
                {
                    lines.Add(Line(at, MicCheckLine.Session, $"{Label(tracked)}: changed: {Describe(session, tracked.Last)}"));
                    _sessions[session.Key] = tracked with { Last = session };
                }
            }
        }

        foreach (var (key, tracked) in _sessions.Where(pair => !seen.Contains(pair.Key)).ToList())
        {
            // A session on a microphone whose sessions could not all be read at this look may still be there.
            if (_incomplete.Contains(tracked.Last.EndpointId)) continue;

            _sessions.Remove(key);
            lines.Add(Line(at, MicCheckLine.Session, $"{Label(tracked)}: gone (Windows no longer lists it)"));
        }

        _otherOwnHeldMicrophoneBefore = _otherOwnHoldsMicrophone;
        _otherOwnHoldsMicrophone = otherOwnHolds;
    }

    /// <summary>
    /// The lines for a source failing to be read: an ERROR line when the problem is new, nothing while it repeats. A problem
    /// that ends in a full stop, as Windows' messages do, loses it, so that the line's own full stop is not a second one.
    /// </summary>
    private static IReadOnlyList<MicCheckLine> Unreadable(ref string? lastProblem, string source, string problem, DateTimeOffset at)
    {
        var lines = problem == lastProblem
            ? []
            : new List<MicCheckLine> { Line(at, MicCheckLine.Error, $"{source} could not be read: {problem.TrimEnd().TrimEnd('.')}. The check keeps trying.") };
        lastProblem = problem;
        return lines;
    }

    /// <summary>Adds the NOTE that a source can be read again, when a problem with it was reported, and clears the problem.</summary>
    private static void NoteReadable(ref string? lastProblem, string source, DateTimeOffset at, List<MicCheckLine> lines)
    {
        if (lastProblem is null) return;

        lines.Add(Line(at, MicCheckLine.Note, $"{source} can be read again."));
        lastProblem = null;
    }

    /// <summary>True when the session belongs to another process of ClassScribe's program than the check's own, such as the recorder of a class.</summary>
    private bool IsOtherOwn(CaptureSession session) =>
        session.ProcessId != ownProcessId && !session.SystemSounds && session.ProcessId > 0 && own.IsOwn(session.ProcessId, session.ImageName);

    /// <summary>
    /// How a session's process is named in its lines, and its program's path for its first line: Windows' own sounds as such;
    /// another ClassScribe process as a recording in progress; otherwise the program behind the process id, or the name the
    /// reader gave when the process has ended since, or a process that cannot be looked at.
    /// </summary>
    private (string Name, string? Path) Name(CaptureSession session, bool otherOwn)
    {
        if (session.SystemSounds) return ("Windows' system sounds", null);

        var image = names.Describe(session.ProcessId);
        var name = image?.Name ?? session.ImageName;
        if (otherOwn) return ($"{name ?? ProgramFiles.Name}, a recording in progress", image?.Path);
        return name is null ? ("a process that cannot be looked at", null) : (name, image?.Path);
    }

    /// <summary>True when a usage entry names the check's own program, by its path.</summary>
    private bool IsOwnProgram(UsageEntry app)
    {
        if (!_ownPathLookedUp)
        {
            _ownPathLookedUp = true;
            _ownPath = names.Describe(ownProcessId)?.Path;
        }

        return app.Path is not null && _ownPath is not null && string.Equals(app.Path, _ownPath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>What a look read about a microphone itself, apart from its sessions, which is what its lines compare.</summary>
    private static MicrophoneFacts FactsOf(MicrophoneSessionListing microphone) =>
        new(microphone.EndpointId, microphone.Name, microphone.DefaultFor, microphone.MutedInWindows, microphone.VolumePercent, microphone.Problem);

    /// <summary>The microphone's state as a line: defaults, mute in Windows, input volume and any problem, with "(was ...)" after what changed.</summary>
    private static string Describe(MicrophoneFacts now, MicrophoneFacts? before)
    {
        var parts = new List<string>
        {
            Field("Windows default:", Roles(now.DefaultFor), before is null ? null : Roles(before.DefaultFor)),
            Field("muted in Windows:", YesNo(now.Muted), before is null ? null : YesNo(before.Muted)),
            Field("input volume:", Percent(now.Volume), before is null ? null : Percent(before.Volume))
        };
        if (before is not null && before.Name != now.Name) parts.Add($"its name changed (was {before.Name})");
        if (now.Problem is not null) parts.Add("problem: " + now.Problem);
        else if (before?.Problem is not null) parts.Add("the earlier problem has cleared");
        return string.Join("; ", parts);
    }

    /// <summary>The session's state as a line: its state and its own mute, with "(was ...)" after what changed.</summary>
    private static string Describe(CaptureSession now, CaptureSession? before) => string.Join("; ",
        Field("state:", now.State.ToString(), before?.State.ToString()),
        Field("session muted:", YesNo(now.SessionMuted), before is null ? null : YesNo(before.SessionMuted)));

    /// <summary>An app's use of the microphone, for its first line.</summary>
    private string DescribeUsage(UsageEntry app, DateTimeOffset at)
    {
        if (app.InUse) return $"USING THE MICROPHONE NOW, since {Time(app.StartFileTime, at)}";
        if (app.StartFileTime is > 0 && app.StopFileTime is > 0)
            return $"not using the microphone; last use {Time(app.StartFileTime, at)} to {Time(app.StopFileTime, at, sameDayAs: app.StartFileTime)} ({Duration(app.StartFileTime, app.StopFileTime)})";

        return $"not using the microphone; recorded start {Time(app.StartFileTime, at)}, stop {Time(app.StopFileTime, at)}";
    }

    /// <summary>What changed in an app's use of the microphone between two looks.</summary>
    private string DescribeChange(UsageEntry before, UsageEntry now, DateTimeOffset at)
    {
        if (now.InUse && !before.InUse) return $"STARTED using the microphone at {Time(now.StartFileTime, at)}";
        if (now.InUse) return $"started again at {Time(now.StartFileTime, at)}, with no stop seen in between (in use since {Time(before.StartFileTime, at)} before)";
        if (before.InUse && now.StartFileTime is > 0)
            return $"STOPPED using the microphone at {Time(now.StopFileTime, at)}, after {Duration(now.StartFileTime, now.StopFileTime)} (from {Time(now.StartFileTime, at)})";

        if (now.StartFileTime is > 0 && now.StopFileTime is > 0)
            return $"used the microphone briefly between two checks: {Time(now.StartFileTime, at)} to {Time(now.StopFileTime, at)} ({Duration(now.StartFileTime, now.StopFileTime)})";

        return $"its times changed: start {Time(now.StartFileTime, at)} (was {Time(before.StartFileTime, at)}), stop {Time(now.StopFileTime, at)} (was {Time(before.StopFileTime, at)})";
    }

    /// <summary>"; program C:\..." for a desktop app, nothing for a Store app.</summary>
    private static string Where(UsageEntry app) => app.Path is null ? "" : $"; program {app.Path}";

    /// <summary>"#3 chrome (pid 1234) on Microphone Array (...)": how a session is named in its lines.</summary>
    private static string Label(TrackedSession session) => $"#{session.Number} {session.ProcessName} (pid {session.Last.ProcessId}) on {session.EndpointName}";

    /// <summary>"label value", followed by "(was before)" when the value changed.</summary>
    private static string Field(string label, string now, string? before) => before is null || before == now ? $"{label} {now}" : $"{label} {now} (was {before})";

    /// <summary>The roles as a list, or "no".</summary>
    private static string Roles(MicrophoneRoles roles)
    {
        if (roles == MicrophoneRoles.None) return "no";

        var parts = new List<string>();
        if (roles.HasFlag(MicrophoneRoles.Console)) parts.Add("Console");
        if (roles.HasFlag(MicrophoneRoles.Multimedia)) parts.Add("Multimedia");
        if (roles.HasFlag(MicrophoneRoles.Communications)) parts.Add("Communications");
        return string.Join(", ", parts);
    }

    /// <summary>"YES" for muted, so mutes stand out; "no" for not muted; "unknown" when unread.</summary>
    private static string YesNo(bool? muted) => muted switch
    {
        true => "YES",
        false => "no",
        null => "unknown"
    };

    /// <summary>A percentage, or "unknown" when unread.</summary>
    private static string Percent(int? percent) => percent is { } value ? $"{value}%" : "unknown";

    /// <summary>
    /// A FILETIME as local time to the millisecond, as the laptop's clock showed it at that moment; "none" when absent. The
    /// date is left out when the time is on the day of <paramref name="at"/>, the look's local time, or on the same day as
    /// <paramref name="sameDayAs"/> when that is given.
    /// </summary>
    private string Time(long? fileTime, DateTimeOffset at, long? sameDayAs = null)
    {
        if (fileTime is not > 0) return "none";

        try
        {
            var local = Local(fileTime.Value);
            var reference = sameDayAs is > 0 ? Local(sameDayAs.Value).Date : at.Date;
            var format = local.Date == reference ? "HH:mm:ss.fff" : "yyyy-MM-dd HH:mm:ss.fff";
            return local.ToString(format, CultureInfo.InvariantCulture);
        }
        catch (ArgumentOutOfRangeException)
        {
            // Deliberately not rethrown: a value Windows wrote that is no FILETIME at all is shown as it is, for the log that
            // gets sent, rather than ending a diagnostic over one registry value.
            return $"(raw value {fileTime})";
        }
    }

    /// <summary>
    /// A FILETIME, in UTC, as a moment in the laptop's zone, with the offset the zone had at that moment: a time on the other
    /// side of a daylight saving change reads as the clock showed it then, not an hour off.
    /// </summary>
    private DateTimeOffset Local(long fileTime) => TimeZoneInfo.ConvertTime(new DateTimeOffset(DateTime.FromFileTimeUtc(fileTime), TimeSpan.Zero), zone);

    /// <summary>The time between two FILETIMEs, such as "16.4 s", "3 min 05 s" or "2 h 27 min 33 s".</summary>
    private static string Duration(long? start, long? stop)
    {
        if (start is not > 0 || stop is not > 0 || stop < start) return "length unknown";

        var length = TimeSpan.FromTicks(stop.Value - start.Value);
        if (length.TotalSeconds < 60) return length.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s";
        if (length.TotalHours < 1) return string.Create(CultureInfo.InvariantCulture, $"{(int)length.TotalMinutes} min {length.Seconds:00} s");
        return string.Create(CultureInfo.InvariantCulture, $"{(int)length.TotalHours} h {length.Minutes:00} min {length.Seconds:00} s");
    }

    /// <summary>A line of <paramref name="kind"/> at <paramref name="at"/>.</summary>
    private static MicCheckLine Line(DateTimeOffset at, string kind, string text) => new(at, kind, text);

    /// <summary>What a look read about one microphone itself, which its lines compare: its name, defaults, mute, volume and problem.</summary>
    private sealed record MicrophoneFacts(string EndpointId, string Name, MicrophoneRoles DefaultFor, bool? Muted, int? Volume, string? Problem);

    /// <summary>A session being followed: its number in this run, its process's name and microphone's name, and what the last look read.</summary>
    private sealed record TrackedSession(int Number, string ProcessName, string EndpointName, CaptureSession Last);
}
