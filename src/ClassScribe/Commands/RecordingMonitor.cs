using System.Globalization;
using ClassScribe.Audio;
using ClassScribe.Recorder;
using ClassScribe.Settings;
using ClassScribe.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClassScribe.Commands;

/// <summary>What one look at a running recording found (see <see cref="RecordingMonitor.Tick"/>).</summary>
/// <param name="Lines">The lines to log, in order, each with its level.</param>
/// <param name="Notes">Notes for the transcript, to go into the recording's marks file, such as a time the laptop slept.</param>
/// <param name="Attention">What needs attention now, with the count of events so far in the run.</param>
/// <param name="StopBecause">Why the recording stops by itself now, as <see cref="RunEndCause"/> names it; null while it goes on.</param>
/// <param name="Reopen">The track whose capture is to be replaced because no audio arrives from it, and why, for the log; null when none is.</param>
/// <param name="StopsAtUtc">
/// When the recording stops by itself, by the time of day: at the end of --minutes, or, from its warning on, at the safety
/// stop, whichever comes first; null when neither is due.
/// </param>
public sealed record MonitorTick(IReadOnlyList<(LogLevel Level, string Text)> Lines, IReadOnlyList<TimelineNote> Notes, Attention Attention,
    string? StopBecause, (Track Track, string Reason)? Reopen, DateTimeOffset? StopsAtUtc);

/// <summary>What the checks of a run need to know about it, beyond the recording itself.</summary>
/// <param name="Request">The run's request: its name, what it records, whether it is transcribed, and --minutes.</param>
/// <param name="Folder">The recording's folder, whose drive's free space is watched.</param>
/// <param name="StartedUtc">When the recording loop started, by the time of day.</param>
/// <param name="StartedTimestamp">When the recording loop started, on the steady clock (<see cref="TimeProvider.GetTimestamp"/>): --minutes and the safety stop count from here.</param>
/// <param name="Offset">The length the recording already had on disk when this run continued it; zero for a new one.</param>
public sealed record MonitorContext(RecordRequest Request, string Folder, DateTimeOffset StartedUtc, long StartedTimestamp, TimeSpan Offset);

/// <summary>
/// The checks made once a second while a class records (see <see cref="Tick"/>). They say what they find and leave acting
/// on it to the recording loop: the lines to log, the notes for the transcript, what needs attention, a stop, and a
/// capture to replace. All of it is timed on the steady clock (<see cref="TimeProvider.GetTimestamp"/>), so Windows setting
/// its time of day neither hurries nor holds up a check; only the times shown to Gulzar are read from the time of day.
/// One monitor watches one run.
/// </summary>
/// <remarks>
/// <para>What needs attention, most pressing first: nothing recorded for 15 seconds; files that cannot be written; audio
/// that stops arriving; a disk with room for less than 30 minutes; sound while paused for 30 seconds; a pause of 10
/// minutes or more; a time ClassScribe did not run, for 5 minutes after it. Each of these is an Alert. A silence as long
/// as Recording:SilenceWarningSeconds is a Notice. The title shows the most pressing one.</para>
/// <para>The count in the attention grows by one for each new event that is at least as pressing as what the title shows,
/// so that a window flashes once for each new Alert, and a Notice that begins under an Alert is not counted. A condition
/// first seen at a look began after the look before it, so it is counted from that look, at most a second early, or from
/// the end of a time ClassScribe did not run.</para>
/// </remarks>
public sealed class RecordingMonitor(FreezeWatch freezes, IFreeSpace space, IOptionsSnapshot<RecordingOptions> recording, TimeProvider time)
{
    /// <summary>How long nothing may be recorded, other than while paused, before it is an Alert. A device being replaced takes less.</summary>
    public static readonly TimeSpan NotRecordingAlertAfter = TimeSpan.FromSeconds(15);

    /// <summary>How long a capture that hands over audio all the time, silence included, as an app's does, may hand over none at all before it counts as stalled.</summary>
    private static readonly TimeSpan ContinuousArrivalLimit = TimeSpan.FromSeconds(5);

    /// <summary>How long a device's meter may show sound while no audio arrives from its capture before the capture counts as stalled.</summary>
    private static readonly TimeSpan MeterArrivalLimit = TimeSpan.FromSeconds(10);

    /// <summary>The least time between two warnings that audio has stopped arriving, each with a reopening of the capture.</summary>
    private static readonly TimeSpan ArrivalWarningSpacing = TimeSpan.FromSeconds(30);

    /// <summary>How long a time ClassScribe did not run stays in the title after it is found.</summary>
    private static readonly TimeSpan FreezeAlertFor = TimeSpan.FromMinutes(5);

    /// <summary>How often a pause is recalled while it lasts.</summary>
    private static readonly TimeSpan PauseReminderEvery = TimeSpan.FromMinutes(5);

    /// <summary>How long a pause may last before it is an Alert.</summary>
    private static readonly TimeSpan PauseAlertAfter = TimeSpan.FromMinutes(10);

    /// <summary>How long sound may keep arriving while paused before the class counts as playing unrecorded.</summary>
    private static readonly TimeSpan PlayingWhilePausedAfter = TimeSpan.FromSeconds(30);

    /// <summary>How often the free space is read again after the start.</summary>
    private static readonly TimeSpan SpaceCheckEvery = TimeSpan.FromMinutes(5);

    /// <summary>At the start, room for less recording than this is warned about.</summary>
    private static readonly TimeSpan SpaceWarningBelow = TimeSpan.FromHours(3);

    /// <summary>Room for less recording than this is an Alert.</summary>
    private static readonly TimeSpan SpaceAlertBelow = TimeSpan.FromMinutes(30);

    /// <summary>How long before the safety stop it is warned about.</summary>
    private static readonly TimeSpan SafetyWarningBefore = TimeSpan.FromMinutes(15);

    /// <summary>What an hour of one track takes on disk: 48 kHz, 16-bit, mono, about 346 MB.</summary>
    private const long TrackBytesPerHour = 48_000L * 2 * 3600;

    /// <summary>What an hour of the upload copy takes on disk: 16 kHz, 16-bit, mono, about 115 MB.</summary>
    private const long UploadBytesPerHour = 16_000L * 2 * 3600;

    /// <summary>The words for a recording whose files cannot be written.</summary>
    private const string CannotWriteText = "The recording cannot be written to disk now; nothing is saved until it can be again. The errors above say why";

    /// <summary>The words for sound that keeps arriving while paused.</summary>
    private const string PlayingWhilePausedText = "The class seems to be playing while paused; P records again";

    /// <summary>True once the first look has taken its bearings from the run's context.</summary>
    private bool _begun;

    /// <summary>When the last look was made, on the steady clock.</summary>
    private long _lastLook;

    /// <summary>
    /// Since when ClassScribe has run without a freeze: the start, or the look that found the last freeze. No audio arrives
    /// while ClassScribe does not run, so the silence and the wait for audio count from here at most.
    /// </summary>
    private long _awakeSince;

    /// <summary>When the last status line was given.</summary>
    private long _lastStatus;

    /// <summary>When the last silence warning was given, or null.</summary>
    private long? _lastSilenceWarning;

    /// <summary>Since when nothing has been recorded, other than while paused; null while recording.</summary>
    private long? _notRecordingSince;

    /// <summary>Since when the recorded device's meter has shown sound at every look, or null while it does not.</summary>
    private long? _meterSoundSince;

    /// <summary>When the last warning that audio has stopped arriving was given, or null.</summary>
    private long? _lastArrivalWarning;

    /// <summary>True from a look that found audio had stopped arriving until audio has flowed again for as long.</summary>
    private bool _stalled;

    /// <summary>Since when audio has flowed again after a stall, or null.</summary>
    private long? _flowingSince;

    /// <summary>The words of the last warning that audio has stopped arriving, and the title's words for it.</summary>
    private (string Short, string Text) _stall = ("", "");

    /// <summary>Since when the recording has been paused, or null while it is not.</summary>
    private long? _pausedSince;

    /// <summary>How many of this pause's reminders have been given.</summary>
    private int _pauseReminders;

    /// <summary>Since when sound has kept arriving during this pause, or null.</summary>
    private long? _soundWhilePausedSince;

    /// <summary>True once the class playing during this stretch of sound while paused has been said.</summary>
    private bool _playingWhilePausedSaid;

    /// <summary>When the free space was last read, or null before the first look.</summary>
    private long? _lastSpaceCheck;

    /// <summary>The words of the last reading of the free space, when it found room for less than 30 minutes; null otherwise.</summary>
    private string? _spaceAlert;

    /// <summary>True once the safety stop has been warned about.</summary>
    private bool _safetyWarned;

    /// <summary>True once a stop has been said, so that it is said once.</summary>
    private bool _stopSaid;

    /// <summary>The last time ClassScribe did not run: when it was found, its number in the run, and its words.</summary>
    private (long FoundAt, int Number, string Short, string Text)? _freeze;

    /// <summary>How many times ClassScribe did not run in this run.</summary>
    private int _freezes;

    /// <summary>How many events have needed attention in this run (see <see cref="Attention.Count"/>).</summary>
    private int _count;

    /// <summary>The conditions that needed attention at the last look, by name.</summary>
    private HashSet<string> _active = [];

    /// <summary>Starts the watch for a time ClassScribe did not run, with a timer of its own (see <see cref="FreezeWatch.Start"/>); disposing the result stops the timer.</summary>
    public IDisposable Start() => freezes.Start();

    /// <summary>
    /// One look at the recording, once a second: a time ClassScribe did not run; nothing recorded; the silence warning;
    /// audio that stops arriving; a pause left on; the free space, at the first look and then every 5 minutes; --minutes
    /// and the safety stop; the status line at its interval, which says when the recording stops by itself; and from all of
    /// that, what needs attention. The first look takes its bearings from <paramref name="context"/>.
    /// </summary>
    public MonitorTick Tick(RecordingSnapshot snapshot, MonitorContext context)
    {
        var now = time.GetTimestamp();
        var options = recording.Value;
        if (!_begun)
        {
            _begun = true;
            _lastLook = _awakeSince = _lastStatus = context.StartedTimestamp;
        }

        var lines = new List<(LogLevel Level, string Text)>();
        var notes = new List<TimelineNote>();
        var main = snapshot.Main;

        foreach (var freeze in freezes.Tick())
        {
            var line = FreezeLine(freeze);
            lines.Add((LogLevel.Warning, line));
            notes.Add(new TimelineNote(snapshot.Position, FreezeNote(freeze)));
            _freeze = (now, ++_freezes, FreezeShort(freeze), line);

            // No audio arrives while ClassScribe does not run, so the waits for it start again now.
            _awakeSince = now;
        }

        // Nothing recorded, other than while paused, which is a choice; a track switched off with V records nothing by choice too.
        if (snapshot.Paused || main.Capturing || main.Off)
            _notRecordingSince = null;
        else
            _notRecordingSince ??= Math.Max(_lastLook, _awakeSince);

        // Nothing is heard while ClassScribe does not run either, so the silence counts from the end of the last freeze at
        // most: the time asleep is no sign that the class does not reach the recording.
        var silenceAfter = TimeSpan.FromSeconds(Math.Max(1, options.SilenceWarningSeconds));
        var silence = Shorter(main.SinceLastSound, time.GetElapsedTime(_awakeSince, now));
        var silent = main.Capturing && !snapshot.Paused && silence >= silenceAfter;
        var silenceText = $"No sound for {DescribeLength(silence)}. Check that the class is playing through \"{main.Source}\" and that Windows is not muted";
        if (silent && (_lastSilenceWarning is not { } warned || time.GetElapsedTime(warned, now) >= silenceAfter))
        {
            _lastSilenceWarning = now;
            lines.Add((LogLevel.Warning, silenceText));
        }

        var reopen = WatchArrival(main, snapshot.Paused, now, lines);
        var (pausedFor, playingWhilePaused) = WatchPause(snapshot, now, lines);

        if (_lastSpaceCheck is not { } lastCheck || time.GetElapsedTime(lastCheck, now) >= SpaceCheckEvery)
        {
            var first = _lastSpaceCheck is null;
            _lastSpaceCheck = now;
            CheckSpace(context, first, lines);
        }

        // The stops are worked out before the status line, which says when the recording stops by itself, and said after it.
        List<(LogLevel Level, string Text)> stopLines = [];
        var (stopBecause, stopsAt) = WatchStops(snapshot, context, options, now, stopLines);
        if (time.GetElapsedTime(_lastStatus, now) >= TimeSpan.FromSeconds(Math.Max(1, options.StatusIntervalSeconds)))
        {
            _lastStatus = now;
            lines.Add((LogLevel.Information, StatusLine(snapshot, stopsAt)));
        }

        lines.AddRange(stopLines);

        List<(string Name, AttentionLevel Level, string Short, string Text)> conditions = [];
        var notRecording = _notRecordingSince is { } waitingSince ? time.GetElapsedTime(waitingSince, now) : TimeSpan.Zero;
        if (notRecording >= NotRecordingAlertAfter)
            conditions.Add(("NotRecording", AttentionLevel.Alert, NotRecordingShort(context.Request.Source.Class),
                main.WaitingFor is { } awaited
                    ? $"Nothing has been recorded for {DescribeLength(notRecording)}; waiting for {awaited}"
                    : $"Nothing has been recorded for {DescribeLength(notRecording)}"));
        if (main.WriteFailing) conditions.Add(("CannotWrite", AttentionLevel.Alert, "CANNOT WRITE", CannotWriteText));
        if (_stalled) conditions.Add(("NoAudio", AttentionLevel.Alert, _stall.Short, _stall.Text));
        if (_spaceAlert is { } spaceText) conditions.Add(("DiskNearlyFull", AttentionLevel.Alert, "DISK NEARLY FULL", spaceText));
        if (playingWhilePaused) conditions.Add(("PlayingWhilePaused", AttentionLevel.Alert, "PLAYING WHILE PAUSED", PlayingWhilePausedText));
        if (pausedFor >= PauseAlertAfter)
            conditions.Add(("LongPause", AttentionLevel.Alert, string.Create(CultureInfo.InvariantCulture, $"PAUSED {(int)pausedFor.TotalMinutes} MIN"), PauseText(pausedFor)));
        if (_freeze is { } frozen && time.GetElapsedTime(frozen.FoundAt, now) < FreezeAlertFor)
            conditions.Add((string.Create(CultureInfo.InvariantCulture, $"Freeze{frozen.Number}"), AttentionLevel.Alert, frozen.Short, frozen.Text));
        if (silent)
            conditions.Add(("NoSound", AttentionLevel.Notice, string.Create(CultureInfo.InvariantCulture, $"no sound {Math.Max(1, (int)silence.TotalMinutes)} min"), silenceText));

        _lastLook = now;
        return new MonitorTick(lines, notes, Attend(conditions), stopBecause, reopen, stopsAt);
    }

    /// <summary>
    /// How long a length is, in words that read at a glance: in seconds up to two minutes, then in whole minutes, and from
    /// an hour on in hours and minutes, such as "2 hours 30 minutes".
    /// </summary>
    public static string DescribeLength(TimeSpan length)
    {
        var seconds = (long)length.TotalSeconds;
        if (seconds <= 120) return Count(seconds, "second");

        var minutes = seconds / 60;
        if (minutes < 60) return Count(minutes, "minute");

        var hours = Count(minutes / 60, "hour");
        return minutes % 60 == 0 ? hours : $"{hours} {Count(minutes % 60, "minute")}";
    }

    /// <summary>A number with its unit, singular for one and plural otherwise, such as "1 minute" or "30 minutes".</summary>
    private static string Count(long number, string unit) =>
        string.Create(CultureInfo.InvariantCulture, $"{number} {unit}{(number == 1 ? "" : "s")}");

    /// <summary>The shorter of two lengths.</summary>
    private static TimeSpan Shorter(TimeSpan first, TimeSpan second) => first < second ? first : second;

    /// <summary>A pause's reminder, for one that has lasted <paramref name="paused"/>.</summary>
    private static string PauseText(TimeSpan paused) => $"Still paused, for {DescribeLength(paused)} now; P records again";

    /// <summary>
    /// The title's words for nothing recorded: what the run waits for, by the name it was given, in as few words as a title
    /// needs, so that the class's name still fits in a terminal's tab: NOT RECORDING (waiting for "chrome"), NOT RECORDING
    /// (waiting for "Pixel Buds"), or NOT RECORDING (waiting for an output device). The log line says it in full. In person,
    /// with no class source, the words are NOT RECORDING alone.
    /// </summary>
    private static string NotRecordingShort(ClassSource? source) => source switch
    {
        ClassSource.App app => $"NOT RECORDING (waiting for \"{app.ProcessName}\")",
        ClassSource.Device device => $"NOT RECORDING (waiting for \"{device.NamePart}\")",
        ClassSource.Automatic => "NOT RECORDING (waiting for an output device)",
        _ => "NOT RECORDING"
    };

    /// <summary>A number of bytes as the free space is given: in gigabytes to one decimal from 1 GB on, in whole megabytes below.</summary>
    private static string Size(long bytes) => bytes >= 1_000_000_000
        ? string.Create(CultureInfo.InvariantCulture, $"{bytes / 1e9:0.0} GB")
        : string.Create(CultureInfo.InvariantCulture, $"{bytes / 1e6:0} MB");

    /// <summary>
    /// The status line: the position in the recording and what it records, "(paused)" while paused, or what it waits for;
    /// and, when the recording stops by itself at <paramref name="stopsAt"/>, when that is, in local time.
    /// </summary>
    private string StatusLine(RecordingSnapshot snapshot, DateTimeOffset? stopsAt)
    {
        var captured = Durations.Format(snapshot.Position);
        var stops = stopsAt is { } at ? $"; stops by itself at {LocalTime(at, "HH:mm")}" : "";
        return snapshot.Main.WaitingFor is { } waitingFor
            ? $"Recording: {captured} captured; waiting for {waitingFor}{stops}"
            : $"Recording: {captured} captured from \"{snapshot.Main.Source}\"{(snapshot.Paused ? " (paused)" : "")}{stops}";
    }

    /// <summary>
    /// What needs attention, from the conditions found now, most pressing first: the first of them, with the count grown by
    /// each that is new since the last look and at least as pressing as the first.
    /// </summary>
    private Attention Attend(List<(string Name, AttentionLevel Level, string Short, string Text)> conditions)
    {
        var shown = conditions.Count == 0 ? AttentionLevel.None : conditions[0].Level;
        foreach (var condition in conditions)
        {
            if (!_active.Contains(condition.Name) && condition.Level >= shown) _count++;
        }

        _active = [.. conditions.Select(condition => condition.Name)];
        return conditions.Count == 0
            ? new Attention(AttentionLevel.None, "", "", _count)
            : new Attention(conditions[0].Level, conditions[0].Short, conditions[0].Text, _count);
    }

    /// <summary>
    /// Watches that audio keeps arriving from <paramref name="track"/>'s open capture although it should, and has the capture
    /// reopened when it does not. Silence is audio too: what counts is whether the capture hands over anything at all, never
    /// how loud it is, so a quiet class is no stall. A capture that hands over audio all the time, silence included, as an
    /// app's does, should never hand over nothing for <see cref="ContinuousArrivalLimit"/>. A device's capture hands over audio
    /// whenever something plays on it, so it is stalled when the device's meter has shown sound at every look for
    /// <see cref="MeterArrivalLimit"/> while nothing arrived; a look with the meter quiet starts that count again. The time
    /// before the last freeze does not count. A stall is warned about, with a reopening, at most once per
    /// <see cref="ArrivalWarningSpacing"/>, and needs attention until audio has flowed again for as long as the limit: a
    /// new capture that has not delivered yet is no sign that audio is back, and nor is a time with no capture open at all.
    /// Returns the track to reopen, or null.
    /// </summary>
    private (Track Track, string Reason)? WatchArrival(TrackSnapshot track, bool paused, long now, List<(LogLevel Level, string Text)> lines)
    {
        if (track.SinceLastPacket is not { } sinceLastPacket)
        {
            // No capture is open: nothing is awaited from one, and no audio has come back either, so a stall stays one.
            _meterSoundSince = null;
            _flowingSince = null;
            return null;
        }

        TimeSpan missing;
        var limit = ContinuousArrivalLimit;
        var nothingArrived = Shorter(sinceLastPacket, time.GetElapsedTime(_awakeSince, now));
        if (track.DeliversContinuously)
        {
            _meterSoundSince = null;
            missing = nothingArrived;
        }
        else
        {
            if (track.RecordedLevel is { } level && level >= CaptureChooser.SoundLevel)
                _meterSoundSince ??= now;
            else
                _meterSoundSince = null;

            var playing = _meterSoundSince is { } since ? time.GetElapsedTime(since, now) : TimeSpan.Zero;
            missing = Shorter(nothingArrived, playing);
            limit = MeterArrivalLimit;
        }

        if (missing < limit)
        {
            if (!_stalled) return null;

            _flowingSince ??= now;
            if (time.GetElapsedTime(_flowingSince.Value, now) >= limit)
            {
                _stalled = false;
                _flowingSince = null;
            }

            return null;
        }

        _stalled = true;
        _flowingSince = null;
        if (_lastArrivalWarning is { } last && time.GetElapsedTime(last, now) < ArrivalWarningSpacing) return null;

        _lastArrivalWarning = now;
        var length = DescribeLength(missing);
        var text = !track.DeliversContinuously
            ? $"No audio has arrived from \"{track.Source}\" for {length} although Windows shows sound playing there; the recording has fallen behind the clock. Reopening it"
            : paused
                ? $"No audio has arrived from \"{track.Source}\" for {length} although it should. Reopening it, so that the recording can go on when you press P again"
                : $"No audio has arrived from \"{track.Source}\" for {length} although it should; the recording has fallen behind the clock. Reopening it";
        _stall = ($"NO AUDIO from \"{track.Source}\"", text);
        lines.Add((LogLevel.Warning, text));
        return (Track.Main, $"no audio has arrived for {length}");
    }

    /// <summary>
    /// Watches a pause: a reminder every <see cref="PauseReminderEvery"/>, and sound that keeps arriving during it, said once
    /// when it has lasted <see cref="PlayingWhilePausedAfter"/>. Returns how long the recording has been paused (zero while
    /// it is not) and whether the class seems to be playing unrecorded.
    /// </summary>
    private (TimeSpan PausedFor, bool PlayingWhilePaused) WatchPause(RecordingSnapshot snapshot, long now, List<(LogLevel Level, string Text)> lines)
    {
        if (!snapshot.Paused)
        {
            _pausedSince = null;
            _pauseReminders = 0;
            _soundWhilePausedSince = null;
            _playingWhilePausedSaid = false;
            return (TimeSpan.Zero, false);
        }

        _pausedSince ??= Math.Max(_lastLook, _awakeSince);
        var paused = time.GetElapsedTime(_pausedSince.Value, now);
        var reminders = (int)(paused.Ticks / PauseReminderEvery.Ticks);
        if (reminders > _pauseReminders)
        {
            _pauseReminders = reminders;
            lines.Add((LogLevel.Warning, PauseText(PauseReminderEvery * reminders)));
        }

        if (!snapshot.Main.SoundWhilePaused)
        {
            _soundWhilePausedSince = null;
            _playingWhilePausedSaid = false;
            return (paused, false);
        }

        _soundWhilePausedSince ??= Math.Max(_lastLook, _awakeSince);
        if (time.GetElapsedTime(_soundWhilePausedSince.Value, now) < PlayingWhilePausedAfter) return (paused, false);

        if (!_playingWhilePausedSaid)
        {
            _playingWhilePausedSaid = true;
            lines.Add((LogLevel.Warning, PlayingWhilePausedText));
        }

        return (paused, true);
    }

    /// <summary>
    /// Reads the free space of the recording's drive and says how much recording still fits there, counting each track
    /// and the upload copy: room for less than 30 minutes needs attention until a later reading finds more, and at the
    /// <paramref name="first"/> reading, room for less than 3 hours is warned about. A drive that cannot be read is passed
    /// over.
    /// </summary>
    private void CheckSpace(MonitorContext context, bool first, List<(LogLevel Level, string Text)> lines)
    {
        if (space.AvailableBytes(context.Folder) is not { } bytes)
        {
            _spaceAlert = null;
            return;
        }

        var tracks = context.Request.Source.Mode == RecordingMode.ClassAndMicrophone ? 2 : 1;
        var fits = TimeSpan.FromHours((double)Math.Max(0, bytes) / (tracks * TrackBytesPerHour + UploadBytesPerHour));
        var drive = Path.GetPathRoot(context.Folder) is { Length: > 0 } root ? root : context.Folder;
        if (fits < SpaceAlertBelow)
        {
            _spaceAlert = fits < TimeSpan.FromMinutes(1)
                ? $"Less than a minute of recording still fits on {drive} ({Size(bytes)} free). Free some space there now, or the recording will stop being saved"
                : $"Only about {DescribeLength(fits)} of recording still fit on {drive} ({Size(bytes)} free). Free some space there now, or the recording will stop being saved";
            lines.Add((LogLevel.Warning, _spaceAlert));
            return;
        }

        _spaceAlert = null;
        if (first && fits < SpaceWarningBelow)
            lines.Add((LogLevel.Warning, $"Only about {DescribeLength(fits)} of recording fit on {drive} ({Size(bytes)} free). Free some space there before a long class"));
    }

    /// <summary>
    /// Watches the two stops a recording makes by itself. --minutes counts on the steady clock from the start, so time paused
    /// or asleep counts, and Windows setting its time of day does not. The safety stop (Recording:StopAfterHours, 0 for none)
    /// comes once the recording has run that long, or reached that length with the parts of it recorded before this run,
    /// whichever comes first: ElevenLabs takes at most 10 hours of audio in one file, and a recording left running by mistake
    /// must not run all night. It is warned about once, 15 minutes before. Returns why the recording stops now, if it does,
    /// and when it stops by the time of day, when that is due.
    /// </summary>
    private (string? StopBecause, DateTimeOffset? StopsAtUtc) WatchStops(RecordingSnapshot snapshot, MonitorContext context, RecordingOptions options, long now,
        List<(LogLevel Level, string Text)> lines)
    {
        string? stopBecause = null;
        DateTimeOffset? stopsAt = null;
        var ran = time.GetElapsedTime(context.StartedTimestamp, now);
        var utcNow = time.GetUtcNow();

        if (context.Request.Minutes is { } minutes)
        {
            var limit = TimeSpan.FromMinutes(minutes);
            stopsAt = utcNow + (limit - ran);
            if (ran >= limit)
            {
                stopBecause = RunEndCause.Minutes;
                if (!_stopSaid)
                    lines.Add((LogLevel.Information, $"The time given with --minutes {minutes.ToString("0.###", CultureInfo.InvariantCulture)} is up; stopping the recording"));
            }
        }

        if (options.StopAfterHours > 0)
        {
            var limit = TimeSpan.FromHours(options.StopAfterHours);
            var left = Shorter(limit - ran, limit - snapshot.Position);
            if (left <= TimeSpan.Zero)
            {
                if (stopBecause is null && !_stopSaid)
                    lines.Add((LogLevel.Warning,
                        $"This recording stops now, after {DescribeLength(limit)} (Recording:StopAfterHours): ElevenLabs takes at most 10 hours of audio in one file. Start a new recording to record more"));
                stopBecause ??= RunEndCause.SafetyStop;
            }
            else if (left <= SafetyWarningBefore && !_safetyWarned)
            {
                _safetyWarned = true;
                lines.Add((LogLevel.Warning,
                    $"This recording stops by itself in {DescribeLength(left)}, after {DescribeLength(limit)} (Recording:StopAfterHours): ElevenLabs takes at most 10 hours of audio in one file"));
            }

            if (_safetyWarned || left <= TimeSpan.Zero)
            {
                var safetyAt = utcNow + (left > TimeSpan.Zero ? left : TimeSpan.Zero);
                stopsAt = stopsAt is { } minutesAt && minutesAt < safetyAt ? minutesAt : safetyAt;
            }
        }

        if (stopBecause is not null) _stopSaid = true;
        return (stopBecause, stopsAt);
    }

    /// <summary>The line for a time ClassScribe did not run, from the last look before it to the first after it, in local time.</summary>
    private string FreezeLine(Freeze freeze) => freeze.Slept
        ? $"The laptop slept from {LocalTime(freeze.FromUtc)} to {LocalTime(freeze.ToUtc)}; nothing was recorded then"
        : $"ClassScribe was held up from {LocalTime(freeze.FromUtc)} to {LocalTime(freeze.ToUtc)} (Windows paused it); nothing was recorded then";

    /// <summary>
    /// The transcript's note for a time ClassScribe did not run, in UTC, as the transcript gives the time the recording
    /// started and the time it was taken up again.
    /// </summary>
    private static string FreezeNote(Freeze freeze)
    {
        var from = freeze.FromUtc.UtcDateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        var to = freeze.ToUtc.UtcDateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        return freeze.Slept
            ? $"Not recorded: the laptop slept from {from} to {to} UTC"
            : $"Not recorded: ClassScribe was held up from {from} to {to} UTC (Windows paused it)";
    }

    /// <summary>The title's words for a time ClassScribe did not run, in local time: to the minute for a sleep, to the second for a hold-up.</summary>
    private string FreezeShort(Freeze freeze) => freeze.Slept
        ? $"SLEPT {LocalTime(freeze.FromUtc, "HH:mm")} to {LocalTime(freeze.ToUtc, "HH:mm")}"
        : $"HELD UP {LocalTime(freeze.FromUtc)} to {LocalTime(freeze.ToUtc)}";

    /// <summary>A moment as the local time of day, to the second unless <paramref name="format"/> says otherwise, such as 19:42:10.</summary>
    private string LocalTime(DateTimeOffset moment, string format = "HH:mm:ss") =>
        TimeZoneInfo.ConvertTime(moment, time.LocalTimeZone).ToString(format, CultureInfo.InvariantCulture);
}
