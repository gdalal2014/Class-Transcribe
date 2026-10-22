using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using ClassScribe.Audio;
using ClassScribe.Audio.Microphones;
using ClassScribe.Recorder;
using ClassScribe.Settings;
using ClassScribe.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Condition = (string Name, ClassScribe.Commands.AttentionLevel Level, string Short, string Text);

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
/// <param name="Microphone">
/// What the recording says about your microphone beyond its snapshot (see <see cref="IMicrophoneRecording"/>); null for a
/// recording that has no microphone, or one that cannot say more about it.
/// </param>
public sealed record MonitorContext(RecordRequest Request, string Folder, DateTimeOffset StartedUtc, long StartedTimestamp, TimeSpan Offset,
    IMicrophoneRecording? Microphone = null);

/// <summary>
/// What a recording with your microphone says about it beyond its snapshot, for the run's lines, its status line, its
/// title and its end-of-class summary: which microphone is recorded now and why, how Windows processes its audio, which
/// app the track follows, and the gate that follows it. A recording that has your microphone implements this beside
/// <see cref="IRecording"/>; a recording of the class alone does not, and the run then asks nothing of it. Each member
/// answers for now, from whatever thread asks.
/// </summary>
public interface IMicrophoneRecording
{
    /// <summary>The app the microphone track follows, as the recording was built to follow it; null when none is followed, as in person.</summary>
    FollowRule? Follows { get; }

    /// <summary>
    /// The gate that follows the app, whose state, words, evidence and summary the run shows; null when the recording has
    /// none. In person, and with the class when no app is followed, a gate for <see cref="FollowRule.None"/> follows
    /// nothing but still counts the time on and off with V, which the end-of-class summary gives; without one that line
    /// is left out.
    /// </summary>
    MicrophoneGate? Gate { get; }

    /// <summary>The microphone recorded now, as Windows lists it; null while there is none to record.</summary>
    Microphone? Microphone { get; }

    /// <summary>
    /// The microphone the recording would record, as its last look picked it, while none is recorded: the track switched
    /// off with V, or not yet opened. The same as <see cref="Microphone"/> while one is recorded. Null when no look has
    /// picked one, as before the first look and while none is connected.
    /// </summary>
    Microphone? WouldRecord { get; }

    /// <summary>Why that microphone, in the words the choice was made with, such as Windows' default microphone; null when there is nothing to say.</summary>
    string? MicrophoneWhy { get; }

    /// <summary>How Windows processes the microphone's audio, as the open capture reports it; null while no capture is open, or when the capture cannot say.</summary>
    MicrophoneProcessing? Processing { get; }

    /// <summary>
    /// Whether the microphone recorded now is muted in Windows (the switch in Sound settings, and the microphone key), read
    /// from Windows when asked; null while none is recorded, or when Windows cannot say.
    /// </summary>
    bool? MutedInWindows { get; }

    /// <summary>
    /// The webcam the microphone belongs to (camera-first 3.4), and whether its picture is held with its frames arriving;
    /// null for a microphone of no webcam, and for a recording that cannot say. The webcam zeros rules take the place of
    /// the warning about only silence for such a microphone.
    /// </summary>
    Camera.WebcamState? Webcam => null;
}

/// <summary>The words for the app the microphone track follows, as the run's lines, status line and summary use them.</summary>
public static class MicrophoneWords
{
    /// <summary>The app as a name after "following": chrome, or "any app"; null when none is followed.</summary>
    public static string? Followed(FollowRule? rule) => rule switch
    {
        FollowRule.Named named => named.NamePart,
        FollowRule.Any => "any app",
        _ => null
    };

    /// <summary>The app as the subject of a sentence: chrome, "an app" when any app is followed, "the class app" when none is.</summary>
    public static string App(FollowRule? rule) => rule switch
    {
        FollowRule.Named named => named.NamePart,
        FollowRule.Any => "an app",
        _ => "the class app"
    };
}

/// <summary>
/// The checks made once a second while a class records (see <see cref="Tick"/>). They say what they find and leave acting
/// on it to the recording loop: the lines to log, the notes for the transcript, what needs attention, a stop, and a
/// capture to replace. All of it is timed on the steady clock (<see cref="TimeProvider.GetTimestamp"/>), so Windows setting
/// its time of day neither hurries nor holds up a check; only the times shown to Gulzar are read from the time of day.
/// One monitor watches one run. The checks about your microphone are made beside the class's in the modes that record
/// it (see <see cref="MicrophoneChecks"/>). A YouTube video's run also ends by itself when its video does (see
/// <see cref="VideoEndWatch"/>), before the safety stop.
/// </summary>
/// <remarks>
/// <para>What needs attention, most pressing first: nothing recorded for 15 seconds; files that cannot be written; audio
/// that stops arriving; a disk with room for less than 30 minutes; sound while paused for 30 seconds; a pause of 10
/// minutes or more; a time ClassScribe did not run, for 5 minutes after it; then the microphone's: its sessions
/// unreadable for 10 seconds, and in person only exact zeros from it, which the recording reports after 2 minutes of
/// them. Each of these is an Alert. A silence as
/// long as Recording:SilenceWarningSeconds is a Notice, and so are the class app taking sound from a Bluetooth microphone,
/// while it lasts (see <see cref="BluetoothMicrophoneWatch"/>), the microphone's lesser troubles and the warnings
/// about it. The title shows the most pressing one.</para>
/// <para>The count in the attention grows by one for each new event that is at least as pressing as what the title shows,
/// so that a window flashes once for each new Alert, and a Notice that begins under an Alert is not counted. A condition
/// first seen at a look began after the look before it, so it is counted from that look, at most a second early, or from
/// the end of a time ClassScribe did not run.</para>
/// </remarks>
/// <param name="freezes">The run's watch for times ClassScribe did not run.</param>
/// <param name="space">The free space of the recording's drive.</param>
/// <param name="recording">The run's recording settings.</param>
/// <param name="advice">The advice about your microphone.</param>
/// <param name="outputs">Windows' output devices and apps with sound, for the echo check.</param>
/// <param name="bluetooth">The watch for the class app on a Bluetooth microphone.</param>
/// <param name="time">The clock everything is timed on.</param>
/// <param name="video">
/// The run's YouTube video window: a video's run ends by the rules of <see cref="VideoEndWatch"/>, its silence is warned about
/// in its own words, and the Bluetooth watch, which is for a class app, is left out. Null, as for the tests that need none,
/// for a run of the class alone.
/// </param>
/// <param name="youTube">The run's YouTube settings, for the longest a video is recorded for; null for the default.</param>
public sealed class RecordingMonitor(FreezeWatch freezes, IFreeSpace space, IOptionsSnapshot<RecordingOptions> recording, MicrophoneAdvice advice,
    ILoopbackCaptureProvider outputs, BluetoothMicrophoneWatch bluetooth, TimeProvider time, YouTube.VideoPlayback? video = null,
    IOptionsSnapshot<YouTubeOptions>? youTube = null)
{
    /// <summary>How long nothing may be recorded, other than while paused, before it is an Alert. A device being replaced takes less.</summary>
    public static readonly TimeSpan NotRecordingAlertAfter = TimeSpan.FromSeconds(15);

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

    /// <summary>At the start, room for less recording than this is warned about; a YouTube batch keeps this much room for a class.</summary>
    public static readonly TimeSpan SpaceWarningBelow = TimeSpan.FromHours(3);

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

    /// <summary>The checks about your microphone, in the modes that record it.</summary>
    private readonly MicrophoneChecks _microphone = new(advice, outputs, time);

    /// <summary>The watch that audio keeps arriving on the main track.</summary>
    private readonly ArrivalWatch _mainArrival = new(time);

    /// <summary>The watch that audio keeps arriving on the microphone track, beside the class.</summary>
    private readonly ArrivalWatch _microphoneArrival = new(time);

    /// <summary>The apps taking sound from a Bluetooth microphone, as the last look that read found them, each with the warning given for it.</summary>
    private readonly List<(BluetoothMicrophoneUse Use, string Text)> _bluetoothUses = [];

    /// <summary>True once the first look has taken its bearings from the run's context.</summary>
    private bool _begun;

    /// <summary>How a YouTube video's recording ends by itself, for a video's run; null for a class's.</summary>
    private VideoEndWatch? _videoEnds;

    /// <summary>Why a video's recording stopped by itself, once it did, so that it is said once.</summary>
    private string? _videoStopped;

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

    /// <summary>
    /// Starts the checks for the run <paramref name="context"/> describes: the watch for a time ClassScribe did not run,
    /// with a timer of its own (see <see cref="FreezeWatch.Start"/>), and the ear for the evidence the microphone's gate
    /// reports, when the recording has one. Disposing the result stops both.
    /// </summary>
    public IDisposable Start(MonitorContext context) => new Started(freezes.Start(), _microphone.Listen(context.Microphone?.Gate));

    /// <summary>
    /// One look at the recording, once a second: a time ClassScribe did not run; the microphone's lines (the advice at the
    /// start, and its checks since); the class app on a Bluetooth microphone, every 10 seconds from the start; nothing
    /// recorded; the silence warning; audio that stops arriving; a pause left on; the free space, at the first look and then
    /// every 5 minutes; --minutes and the safety stop; the status line at its interval, which says when the recording stops
    /// by itself and, with the class and your microphone, how the microphone is doing; and from all of that, what needs
    /// attention. The first look takes its bearings from <paramref name="context"/>.
    /// </summary>
    public MonitorTick Tick(RecordingSnapshot snapshot, MonitorContext context)
    {
        var now = time.GetTimestamp();
        var options = recording.Value;
        if (!_begun)
        {
            _begun = true;
            _lastLook = _awakeSince = _lastStatus = context.StartedTimestamp;
            if (context.Request.Video is not null && video is { IsVideo: true } window) _videoEnds = new VideoEndWatch(window, time);
        }

        var lines = new List<(LogLevel Level, string Text)>();
        var notes = new List<TimelineNote>();
        var main = snapshot.Main;
        var mode = context.Request.Source.Mode;

        foreach (var freeze in freezes.Tick())
        {
            var line = FreezeLine(freeze);
            lines.Add((LogLevel.Warning, line));
            notes.Add(new TimelineNote(snapshot.Position, FreezeNote(freeze)));
            _freeze = (now, ++_freezes, FreezeShort(freeze), line);

            // No audio arrives while ClassScribe does not run, so the waits for it start again now.
            _awakeSince = now;
        }

        // A condition first seen now began after the last look, or after the last freeze, whichever is later.
        var began = Math.Max(_lastLook, _awakeSince);

        // The microphone's lines first, so that at the first look the advice about it leads.
        List<Condition> microphoneConditions = [];
        _microphone.Tick(snapshot, context, options, now, began, lines, microphoneConditions);

        // Then the class app on a Bluetooth microphone, which can take the class's sound where it cannot be recorded.
        List<Condition> bluetoothConditions = [];
        WatchBluetoothMicrophones(context, now, lines, bluetoothConditions);

        // Nothing recorded, other than while paused, which is a choice; a track switched off with V records nothing by choice too.
        if (snapshot.Paused || main.Capturing || main.Off)
            _notRecordingSince = null;
        else
            _notRecordingSince ??= began;

        // Nothing is heard while ClassScribe does not run either, so the silence counts from the end of the last freeze at
        // most: the time asleep is no sign that the class does not reach the recording.
        var silenceAfter = TimeSpan.FromSeconds(Math.Max(1, options.SilenceWarningSeconds));
        var silence = Shorter(main.SinceLastSound, time.GetElapsedTime(_awakeSince, now));

        // A video's run warns of its silence in its own words, since its silence ends it (see VideoEndWatch).
        var silent = _videoEnds is null && main.Capturing && !snapshot.Paused && silence >= silenceAfter;
        var silenceText = SilenceText(main, mode, silence);
        if (silent && (_lastSilenceWarning is not { } warned || time.GetElapsedTime(warned, now) >= silenceAfter))
        {
            _lastSilenceWarning = now;
            lines.Add((LogLevel.Warning, silenceText));
        }

        var reopen = _mainArrival.Watch(main, Track.Main, snapshot.Paused, now, _awakeSince, lines);
        if (mode == RecordingMode.ClassAndMicrophone && snapshot.Microphone is { } microphone)
        {
            // The microphone beside the class is watched too; switched off with V it has no capture to watch, and a stall
            // from before V is forgotten, so that the next capture starts afresh.
            if (microphone.Off)
                _microphoneArrival.Reset();
            else
                reopen ??= _microphoneArrival.Watch(microphone, Track.Microphone, snapshot.Paused, now, _awakeSince, lines);
        }

        var (pausedFor, playingWhilePaused) = WatchPause(snapshot, now, lines);

        if (_lastSpaceCheck is not { } lastCheck || time.GetElapsedTime(lastCheck, now) >= SpaceCheckEvery)
        {
            var first = _lastSpaceCheck is null;
            _lastSpaceCheck = now;
            CheckSpace(context, first, lines);
        }

        // The stops are worked out before the status line, which says when the recording stops by itself, and said after it.
        // A video's own ends come before the safety stop, and only one stop is said.
        List<(LogLevel Level, string Text)> stopLines = [];
        List<Condition> videoConditions = [];
        var videoStop = WatchTheVideo(snapshot, context, silenceAfter, now, stopLines, videoConditions);
        List<(LogLevel Level, string Text)> safetyLines = [];
        var (stopBecause, stopsAt) = WatchStops(snapshot, context, options, now, safetyLines);
        if (videoStop is not null) stopBecause = videoStop;
        else stopLines.AddRange(safetyLines);
        if (time.GetElapsedTime(_lastStatus, now) >= TimeSpan.FromSeconds(Math.Max(1, options.StatusIntervalSeconds)))
        {
            _lastStatus = now;
            lines.Add((LogLevel.Information, StatusLine(snapshot, stopsAt) + _microphone.StatusClause(snapshot, context)));
        }

        lines.AddRange(stopLines);

        List<Condition> conditions = [];
        var notRecording = _notRecordingSince is { } waitingSince ? time.GetElapsedTime(waitingSince, now) : TimeSpan.Zero;
        if (notRecording >= NotRecordingAlertAfter)
            conditions.Add(("NotRecording", AttentionLevel.Alert, NotRecordingShort(context.Request.Source.Class),
                main.WaitingFor is { } awaited
                    ? $"Nothing has been recorded for {DescribeLength(notRecording)}; waiting for {awaited}"
                    : $"Nothing has been recorded for {DescribeLength(notRecording)}"));
        if (main.WriteFailing) conditions.Add(("CannotWrite", AttentionLevel.Alert, "CANNOT WRITE", CannotWriteText));
        if (_mainArrival.Stalled) conditions.Add(("NoAudio", AttentionLevel.Alert, _mainArrival.Stall.Short, _mainArrival.Stall.Text));
        if (_spaceAlert is { } spaceText) conditions.Add(("DiskNearlyFull", AttentionLevel.Alert, "DISK NEARLY FULL", spaceText));
        if (playingWhilePaused) conditions.Add(("PlayingWhilePaused", AttentionLevel.Alert, "PLAYING WHILE PAUSED", PlayingWhilePausedText));
        if (pausedFor >= PauseAlertAfter)
            conditions.Add(("LongPause", AttentionLevel.Alert, string.Create(CultureInfo.InvariantCulture, $"PAUSED {(int)pausedFor.TotalMinutes} MIN"), PauseText(pausedFor)));
        if (_freeze is { } frozen && time.GetElapsedTime(frozen.FoundAt, now) < FreezeAlertFor)
            conditions.Add((string.Create(CultureInfo.InvariantCulture, $"Freeze{frozen.Number}"), AttentionLevel.Alert, frozen.Short, frozen.Text));
        if (silent)
            conditions.Add(("NoSound", AttentionLevel.Notice, string.Create(CultureInfo.InvariantCulture, $"no sound {Math.Max(1, (int)silence.TotalMinutes)} min"), silenceText));
        conditions.AddRange(videoConditions);
        conditions.AddRange(bluetoothConditions);
        if (_microphoneArrival.Stalled) conditions.Add(("MicrophoneNoAudio", AttentionLevel.Notice, "no audio from the microphone", _microphoneArrival.Stall.Text));
        conditions.AddRange(microphoneConditions);

        _lastLook = now;

        // The class's conditions come before the microphone's at each level; an Alert of the microphone's still outranks a Notice.
        return new MonitorTick(lines, notes, Attend([.. conditions.OrderByDescending(condition => condition.Level)]), stopBecause, reopen, stopsAt);
    }

    /// <summary>
    /// The class app on a Bluetooth microphone, in the modes that record the class (see <see cref="BluetoothMicrophoneWatch"/>):
    /// at the first look and every 10 seconds after, a warning for each app newly found taking sound from a Bluetooth
    /// microphone (a headset's or earbuds'), which names the app, the microphone and what to change in the app, a line when it lets go, and a Notice
    /// for each while it lasts. The apps that count are the app recorded in app mode and the app the microphone follows
    /// when one is named; in device mode with no app followed, any app, since any could be the class's (see <see cref="WatchedApps"/>).
    /// In person there is no class to lose, so nothing is watched.
    /// </summary>
    private void WatchBluetoothMicrophones(MonitorContext context, long now, List<(LogLevel Level, string Text)> lines, List<Condition> conditions)
    {
        // In person there is no class to lose; in a YouTube video's window no class app listens on a microphone.
        if (context.Request.Source.Mode == RecordingMode.Microphone || context.Request.Video is not null) return;

        if (bluetooth.Look(WatchedApps(context), now) is { Problem: null } look)
        {
            foreach (var ended in look.Ended)
            {
                _bluetoothUses.RemoveAll(entry => entry.Use == ended);
                lines.Add((LogLevel.Information, $"{ended.App} has let go of the Bluetooth microphone \"{ended.Microphone}\""));
            }

            foreach (var began in look.Began)
            {
                var text = BluetoothText(began, look.BuiltInMicrophone);
                _bluetoothUses.Add((began, text));
                lines.Add((LogLevel.Warning, text));
            }
        }

        foreach (var (use, text) in _bluetoothUses)
            conditions.Add(($"BluetoothMicrophone {use.App} {use.Microphone}", AttentionLevel.Notice, $"{use.App} on a Bluetooth microphone", text));
    }

    /// <summary>
    /// The apps whose sessions on a Bluetooth microphone matter to the class: the app recorded in app mode, and the app the
    /// microphone follows when one is named; empty, which means any app, in device mode with no app followed.
    /// </summary>
    private static IReadOnlyList<FollowRule.Named> WatchedApps(MonitorContext context)
    {
        List<FollowRule.Named> apps = [];
        if (context.Request.Source.Class is ClassSource.App recorded) apps.Add(new FollowRule.Named(recorded.ProcessName));

        var follows = context.Microphone?.Follows ?? context.Request.Source.Microphone?.Follows;
        if (follows is FollowRule.Named followed && !apps.Any(app => app.NamePart.Equals(followed.NamePart, StringComparison.OrdinalIgnoreCase)))
            apps.Add(followed);

        return apps;
    }

    /// <summary>
    /// The warning for an app taking sound from a Bluetooth microphone: what it does to the class's sound, and the fix,
    /// naming the laptop's own microphone array when one is connected. Worded for a headset or earbuds alike, since the
    /// watch counts every Bluetooth microphone.
    /// </summary>
    private static string BluetoothText(BluetoothMicrophoneUse use, string? builtInMicrophone)
    {
        var builtIn = builtInMicrophone is null ? "the laptop's built-in microphone" : $"the laptop's built-in microphone (\"{builtInMicrophone}\")";
        return $"{use.App} is taking sound from the Bluetooth microphone \"{use.Microphone}\": that puts the headset or earbuds into call mode, and the class's sound may be going to them " +
            $"in a way Windows cannot record, so this recording may be silent. In {use.App}, set the microphone to {builtIn}, or to another that is not Bluetooth";
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

    /// <summary>A number with its unit, singular for one and plural otherwise, such as "1 minute" or "30 minutes"; <paramref name="plural"/> when the plural is not the unit with an s.</summary>
    internal static string Count(long number, string unit, string? plural = null) =>
        string.Create(CultureInfo.InvariantCulture, $"{number} {(number == 1 ? unit : plural ?? unit + "s")}");

    /// <summary>The shorter of two lengths.</summary>
    private static TimeSpan Shorter(TimeSpan first, TimeSpan second) => first < second ? first : second;

    /// <summary>A pause's reminder, for one that has lasted <paramref name="paused"/>.</summary>
    private static string PauseText(TimeSpan paused) => $"Still paused, for {DescribeLength(paused)} now; P records again";

    /// <summary>
    /// The silence warning: with the class, that nothing has been heard from what it plays through; in person, that nothing
    /// has been heard from the microphone, with what to check about it.
    /// </summary>
    private static string SilenceText(TrackSnapshot main, RecordingMode mode, TimeSpan silence) => mode == RecordingMode.Microphone
        ? $"No sound from the microphone \"{main.Source}\" for {DescribeLength(silence)}. Check that it is not muted in Windows (the microphone key) and that Windows lets desktop apps use it (Settings > Privacy & security > Microphone)"
        : $"No sound for {DescribeLength(silence)}. Check that the class is playing through \"{main.Source}\" and that Windows is not muted";

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
    private Attention Attend(List<Condition> conditions)
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
    /// The room a class of <paramref name="length"/> takes on its drive, as the checks below count it: its one track and the
    /// upload copy. A YouTube batch keeps this much free for the next class (see <see cref="SpaceWarningBelow"/>).
    /// </summary>
    public static long RoomFor(TimeSpan length) => (long)Math.Ceiling(length.TotalHours * (TrackBytesPerHour + UploadBytesPerHour));

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

    /// <summary>
    /// A YouTube video's own ends (see <see cref="VideoEndWatch"/>), for a video's run: why it stops now, or null; nothing for a
    /// class's run. A stop is said once; the looks after it, until the loop stops, give the same cause and say nothing more.
    /// </summary>
    private string? WatchTheVideo(RecordingSnapshot snapshot, MonitorContext context, TimeSpan silenceWarning, long now, List<(LogLevel Level, string Text)> lines,
        List<Condition> conditions)
    {
        if (_videoEnds is null) return null;
        if (_videoStopped is not null) return _videoStopped;

        var longest = TimeSpan.FromHours((youTube?.Value ?? new YouTubeOptions()).LongestVideoHours);
        _videoStopped = _videoEnds.Look(snapshot, context, now, _awakeSince, silenceWarning, longest, lines, conditions);
        return _videoStopped;
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

    /// <summary>What <see cref="Start"/> began: the freeze watch's timer and the ear for the gate's evidence, stopped together.</summary>
    private sealed class Started(IDisposable timer, IDisposable ear) : IDisposable
    {
        /// <summary>Stops listening for evidence, then the timer.</summary>
        public void Dispose()
        {
            ear.Dispose();
            timer.Dispose();
        }
    }

    /// <summary>
    /// Watches that audio keeps arriving from one track's open capture although it should, and has the capture reopened
    /// when it does not. Silence is audio too: what counts is whether the capture hands over anything at all, never how
    /// loud it is, so a quiet class is no stall. A capture that hands over audio all the time, silence included, as an
    /// app's or a microphone's does, should never hand over nothing for <see cref="ContinuousArrivalLimit"/>. A device's
    /// capture hands over audio whenever something plays on it, so it is stalled when the device's meter has shown sound at
    /// every look for <see cref="MeterArrivalLimit"/> while nothing arrived; a look with the meter quiet starts that count
    /// again. The time before the last freeze does not count. A stall is warned about, with a reopening, at most once per
    /// <see cref="ArrivalWarningSpacing"/>, and needs attention until audio has flowed again for as long as the limit: a
    /// new capture that has not delivered yet is no sign that audio is back, and nor is a time with no capture open at all.
    /// One watch per track.
    /// </summary>
    private sealed class ArrivalWatch(TimeProvider time)
    {
        /// <summary>How long a capture that hands over audio all the time, silence included, may hand over none at all before it counts as stalled.</summary>
        private static readonly TimeSpan ContinuousArrivalLimit = TimeSpan.FromSeconds(5);

        /// <summary>How long a device's meter may show sound while no audio arrives from its capture before the capture counts as stalled.</summary>
        private static readonly TimeSpan MeterArrivalLimit = TimeSpan.FromSeconds(10);

        /// <summary>The least time between two warnings that audio has stopped arriving, each with a reopening of the capture.</summary>
        private static readonly TimeSpan ArrivalWarningSpacing = TimeSpan.FromSeconds(30);

        /// <summary>Since when the recorded device's meter has shown sound at every look, or null while it does not.</summary>
        private long? _meterSoundSince;

        /// <summary>When the last warning that audio has stopped arriving was given, or null.</summary>
        private long? _lastArrivalWarning;

        /// <summary>Since when audio has flowed again after a stall, or null.</summary>
        private long? _flowingSince;

        /// <summary>True from a look that found audio had stopped arriving until audio has flowed again for as long.</summary>
        public bool Stalled { get; private set; }

        /// <summary>The words of the last warning that audio has stopped arriving, and the title's words for it.</summary>
        public (string Short, string Text) Stall { get; private set; } = ("", "");

        /// <summary>Forgets a stall and its counts, as when the track's capture was released on purpose.</summary>
        public void Reset()
        {
            Stalled = false;
            _meterSoundSince = null;
            _flowingSince = null;
        }

        /// <summary>One look at <paramref name="track"/>, which is <paramref name="which"/> one; returns the track to reopen, or null.</summary>
        public (Track Track, string Reason)? Watch(TrackSnapshot track, Track which, bool paused, long now, long awakeSince, List<(LogLevel Level, string Text)> lines)
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
            var nothingArrived = Shorter(sinceLastPacket, time.GetElapsedTime(awakeSince, now));
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
                if (!Stalled) return null;

                _flowingSince ??= now;
                if (time.GetElapsedTime(_flowingSince.Value, now) >= limit)
                {
                    Stalled = false;
                    _flowingSince = null;
                }

                return null;
            }

            Stalled = true;
            _flowingSince = null;
            if (_lastArrivalWarning is { } last && time.GetElapsedTime(last, now) < ArrivalWarningSpacing) return null;

            _lastArrivalWarning = now;
            var length = DescribeLength(missing);
            var text = !track.DeliversContinuously
                ? $"No audio has arrived from \"{track.Source}\" for {length} although Windows shows sound playing there; the recording has fallen behind the clock. Reopening it"
                : paused
                    ? $"No audio has arrived from \"{track.Source}\" for {length} although it should. Reopening it, so that the recording can go on when you press P again"
                    : $"No audio has arrived from \"{track.Source}\" for {length} although it should; the recording has fallen behind the clock. Reopening it";
            Stall = ($"NO AUDIO from \"{track.Source}\"", text);
            lines.Add((LogLevel.Warning, text));
            return (which, $"no audio has arrived for {length}");
        }
    }
}

/// <summary>
/// The checks about your microphone, made once a second beside the class's in the modes that record it: the advice at
/// the start (the privacy line, the case line for the class app's mute, which app is followed, which microphone and how,
/// or which one V will open when the track starts switched off, and the warnings, which are a Notice for five minutes),
/// the advice again whenever the recording moves to another microphone, Lenovo Vantage's mode read again every 30
/// seconds, the echo checked again whenever the class moves to another device (in app mode, the app's device every 30
/// seconds), the gate that follows the class app (a line when a state has held for a second, the app not opening the
/// microphone for 5 minutes, its sessions unreadable for 10 seconds, the evidence that its mute is visible), a microphone
/// missing beside the class, and only exact zeros from it, which the recording reports after 2 minutes of them (for a
/// webcam's microphone the webcam zeros rules instead, at the device's own depth, and the problems of its hold). The
/// status line's clause about the microphone comes from here too. Nothing here opens a microphone: the advice reads the
/// registry, the echo check reads Windows' lists of devices and apps, the gate is read as the watch left it, and the
/// mute is read from Windows' own switch.
/// </summary>
internal sealed class MicrophoneChecks(MicrophoneAdvice advice, ILoopbackCaptureProvider outputs, TimeProvider time)
{
    /// <summary>How long the followed app may leave the microphone unopened before it is a Notice.</summary>
    public static readonly TimeSpan NotOpenedNoticeAfter = TimeSpan.FromMinutes(5);

    /// <summary>How long the app's sessions may be unreadable before it is an Alert.</summary>
    public static readonly TimeSpan UnreadableAlertAfter = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The length of exact zeros that the recording's snapshot stands for when it says <see cref="TrackSnapshot.OnlyZeros"/>:
    /// the recording counts the zeros itself (RecordingSession's OnlyZerosAfter, 2 minutes), and the checks say so at the
    /// look that first finds the flag set, with this length in the words, rather than counting them a second time.
    /// </summary>
    public static readonly TimeSpan OnlyZerosLength = TimeSpan.FromMinutes(2);

    /// <summary>How often a microphone missing beside the class is said to be waited for.</summary>
    public static readonly TimeSpan MissingReminderEvery = TimeSpan.FromSeconds(30);

    /// <summary>How long a warning about the microphone stays a Notice in the title; the log keeps the words.</summary>
    public static readonly TimeSpan NoticeFor = TimeSpan.FromMinutes(5);

    /// <summary>How long a state of the gate must hold before it is logged: shorter changes are counted by the gate, not logged.</summary>
    private static readonly TimeSpan HeldFor = TimeSpan.FromSeconds(1);

    /// <summary>The words by which the advice's line that Vantage's noise cancelling went off is told from its warnings, which are about it being on.</summary>
    private const string VantageWentOff = " is now off";

    /// <summary>The evidence the gate reported, from the watch's thread, waiting for the next look.</summary>
    private readonly ConcurrentQueue<string> _evidence = new();

    /// <summary>The warnings that are Notices now, newest last.</summary>
    private readonly List<Noticed> _noticed = [];

    /// <summary>True once the first look has given the advice at the start.</summary>
    private bool _advised;

    /// <summary>The microphone last advised about, by its id; null for none.</summary>
    private string? _microphoneId;

    /// <summary>The device the class was last found to play on, by name, and whether the room hears it; null before the first look found one.</summary>
    private string? _classDevice;

    private bool _classHeard;

    /// <summary>When Vantage's mode, and in app mode the app's device, were last looked at.</summary>
    private long _lastPoll;

    /// <summary>The gate's state as last seen, since when, and the position then; and the state last logged.</summary>
    private MicrophoneGateState? _pending;

    private long _pendingSince;
    private TimeSpan _pendingPosition;
    private MicrophoneGateState? _logged;

    /// <summary>Since when the app has not opened the microphone, or null; and whether that has been said for this stretch.</summary>
    private long? _notOpenedSince;

    private bool _notOpenedSaid;

    /// <summary>Since when the sessions have been unreadable, or null; whether that has been said; and its words.</summary>
    private long? _unreadableSince;

    private bool _unreadableSaid;
    private string _unreadableText = "";

    /// <summary>True once only zeros from the microphone, while it should be heard, have been said for this stretch of them; and the words they were said with.</summary>
    private bool _zerosSaid;

    private string _zerosText = "";

    /// <summary>The capture the webcam rule at the start was said for, by its number; 0 for none; and the words it was said with.</summary>
    private int _webcamStartSaidFor;

    private string _webcamStartText = "";

    /// <summary>Where the webcam rule after sound stands, said once a recording; and the words it was said with.</summary>
    private WebcamLater _webcamLater;

    private string _webcamLaterText = "";

    /// <summary>Whether Windows mutes the webcam's microphone, as asked once in the stretch of zeros now; null outside one.</summary>
    private bool? _webcamMuted;

    /// <summary>True while Windows' lists of devices and apps cannot be read, from the look that noted it: the note is given once per such stretch.</summary>
    private bool _listingFailing;

    /// <summary>When a microphone missing beside the class was last said to be waited for, or null.</summary>
    private long? _lastMissingWarning;

    /// <summary>Listens for the evidence <paramref name="gate"/> reports, until the result is disposed; nothing to listen to without a gate.</summary>
    public IDisposable Listen(MicrophoneGate? gate)
    {
        if (gate is null) return new Ear(null);
        gate.Evidence += OnEvidence;
        return new Ear(() => gate.Evidence -= OnEvidence);
    }

    /// <summary>
    /// The status line's clause about your microphone, with the class: "; microphone: following chrome, recording", the
    /// gate's words while it is not, what is waited for while there is no microphone, "off" with what V does while it is
    /// switched off, or the microphone's name while no app is followed. Empty for the other modes.
    /// </summary>
    public string StatusClause(RecordingSnapshot snapshot, MonitorContext context)
    {
        if (context.Request.Source.Mode != RecordingMode.ClassAndMicrophone || snapshot.Microphone is not { } track) return "";

        var details = context.Microphone;
        var gate = details?.Gate;
        var followed = MicrophoneWords.Followed(details?.Follows);
        string clause;
        if (track.Off)
            clause = gate?.Why ?? "off (V switches it on)";
        else if (track.WaitingFor is { } waitingFor)
            clause = $"waiting for {waitingFor}";
        else if (gate is null)
            clause = $"recording \"{track.Source}\"";
        else
        {
            var (state, why) = gate.Describe();
            clause = state switch
            {
                MicrophoneGateState.Recording => $"following {followed}, recording",
                MicrophoneGateState.NotListening => $"following {followed}, muted ({why})",
                MicrophoneGateState.MutedInWindows => $"following {followed}, muted in Windows",
                MicrophoneGateState.NotOpened or MicrophoneGateState.Unreadable => $"following {followed}, {why}",
                MicrophoneGateState.OtherMicrophone => why ?? $"following {followed}",
                MicrophoneGateState.Off => why ?? "off (V switches it on)",
                _ => $"recording \"{track.Source}\""
            };
        }

        return $"; microphone: {clause}";
    }

    /// <summary>
    /// One look, in the modes that record your microphone: the advice at the start at the first look, and at every look
    /// since the microphone followed to another, Vantage's mode and the class's device looked at again, the gate watched,
    /// a missing microphone and only zeros said, the evidence reported, and the warnings that are still Notices added to
    /// <paramref name="conditions"/>. <paramref name="began"/> is when a condition first seen now began.
    /// </summary>
    public void Tick(RecordingSnapshot snapshot, MonitorContext context, RecordingOptions options, long now, long began,
        List<(LogLevel Level, string Text)> lines, List<Condition> conditions)
    {
        var mode = context.Request.Source.Mode;
        if (mode == RecordingMode.Class) return;

        var withClass = mode == RecordingMode.ClassAndMicrophone;
        var track = withClass ? snapshot.Microphone : snapshot.Main;
        if (track is null) return;

        var details = context.Microphone;
        var gate = withClass ? details?.Gate : null;
        var inAppMode = context.Request.Source.Class is ClassSource.App;

        if (!_advised)
        {
            AdviseAtStart(snapshot, track, context, options, withClass, now, lines);
        }
        else
        {
            FollowTheMicrophone(details, withClass, now, lines);
            var poll = time.GetElapsedTime(_lastPoll, now) >= MicrophoneAdvice.VantageInterval;
            if (poll)
            {
                _lastPoll = now;
                PollVantage(now, lines);
            }

            // The class's device is looked at when its source changes; in app mode the source stays the app while it moves
            // between devices, so the app's device is looked at as soon as the app plays, and then with each poll.
            if (withClass && (inAppMode ? _classDevice is null || poll : snapshot.Main.Source != _classDevice))
                CheckTheClassDevice(snapshot.Main, context, now, lines);
        }

        // The state and its words are read together, so that a look arriving on the watch's thread between two reads
        // cannot pair one state with another state's words.
        var described = gate?.Describe();
        var state = described?.State;
        if (described is { } gateNow) WatchTheGate(gateNow.State, gateNow.Why, details!.Follows, snapshot, now, began, lines, conditions);
        WatchAMissingMicrophone(track, withClass, now, lines, conditions);

        // A webcam's microphone has rules of its own for its zeros, which a quiet room does not set off (camera-first 3.4);
        // Windows' own silence (packets flagged silent, a muted microphone) stays with the warning every microphone has.
        if (details?.Webcam is { } webcam && !track.WindowsSilent && !WebcamMuted(track, details))
            WatchWebcamZeros(track, state, snapshot.Paused, withClass, webcam, lines, conditions);
        else
            WatchOnlyZeros(track, state, snapshot.Paused, withClass, MicrophoneWords.App(details?.Follows), details, lines, conditions);
        WatchTheHold(details?.Webcam, withClass, conditions);
        ReportEvidence(MicrophoneWords.App(details?.Follows), now, lines);

        _noticed.RemoveAll(notice => time.GetElapsedTime(notice.At, now) >= NoticeFor);
        foreach (var notice in _noticed) conditions.Add((notice.Name, AttentionLevel.Notice, notice.Short, notice.Text));
    }

    /// <summary>
    /// The advice at the start, at the first look: the lines in the advice's order, with the line that names the followed
    /// app after the case line; each warning as one, and all of them one Notice that says how many. A track that starts
    /// switched off is advised about the microphone it would record, so that the warnings about it come now and not at V.
    /// </summary>
    private void AdviseAtStart(RecordingSnapshot snapshot, TrackSnapshot track, MonitorContext context, RecordingOptions options, bool withClass, long now,
        List<(LogLevel Level, string Text)> lines)
    {
        _advised = true;
        _lastPoll = now;
        var details = context.Microphone;
        var microphone = details is null ? NamedOnly(track) : Named(details);
        _microphoneId = microphone?.Id;

        string? classDevice = null;
        var heard = false;
        if (withClass)
        {
            string? failure;
            (classDevice, heard, failure) = ClassDevice(snapshot.Main, context.Request.Source.Class);
            NoteTheListing(failure, lines);
        }

        _classDevice = classDevice;
        _classHeard = heard;

        var start = new MicrophoneStart(context.Request.Source.Mode, microphone, details?.Processing ?? options.MicrophoneProcessing, RawRefusal: null,
            Why: details?.MicrophoneWhy, WaitingFor: microphone is null ? track.WaitingFor : null, ClassDeviceName: classDevice, ClassHeardInTheRoom: heard,
            Follows: details?.Follows, AppMute: options.ClassAppMute, Off: track.Off);
        List<string> warnings = [];
        var index = 0;
        foreach (var line in advice.AtStart(start))
        {
            lines.Add((line.Warning ? LogLevel.Warning : LogLevel.Information, line.Text));
            if (line.Warning) warnings.Add(line.Text);
            if (++index == 2 && withClass && FollowingLine(context, details?.Follows) is { } following) lines.Add((LogLevel.Information, following));
        }

        if (warnings.Count > 0)
            Notice("StartWarnings", $"{RecordingMonitor.Count(warnings.Count, "warning")} at the start", string.Join(" ", warnings), now);
    }

    /// <summary>
    /// The line that names the followed app and where that came from: the app being recorded, the setting, or a choice
    /// made for the run (--mic-follows, or the menu). Null when no app is followed.
    /// </summary>
    private static string? FollowingLine(MonitorContext context, FollowRule? follows)
    {
        if (MicrophoneWords.Followed(follows) is not { } followed) return null;

        var whom = follows is FollowRule.Any ? "any app that holds the microphone" : followed;
        if (context.Request.Source.Microphone?.Follows is not null) return $"Following: {whom} (chosen for this run)";
        if (follows is FollowRule.Named && context.Request.Source.Class is ClassSource.App) return $"Following: {whom}, the app being recorded";
        return follows is FollowRule.Any ? $"Following: {whom} (Recording:MicrophoneFollows {FollowRule.AnyWord})" : $"Following: {whom} (Recording:MicrophoneFollows)";
    }

    /// <summary>
    /// The microphone a recording captures when it says no more about it than its snapshot (one without
    /// <see cref="IMicrophoneRecording"/>): a stand-in with only the name known, so that the start names it rather than
    /// says one is waited for. It is not the array, Bluetooth or anything else the warnings look for, since the recording
    /// cannot say. Null while the track captures nothing.
    /// </summary>
    private static Microphone? NamedOnly(TrackSnapshot track) => track.Capturing && track.Source.Length > 0
        ? new Microphone(track.Source, track.Source, IsDefault: false, IsBuiltInArray: false, IsBluetooth: false, IsLoopbackInput: false, RawSupported: false,
            Enumerator: "", BluetoothEvidence: null)
        : null;

    /// <summary>The microphone the advice is about: the one recorded now, or, while none is (switched off, or not opened yet), the one the recording would record.</summary>
    private static Microphone? Named(IMicrophoneRecording details) => details.Microphone ?? details.WouldRecord;

    /// <summary>
    /// The advice again when the recording has moved to another microphone, or would record another once switched on:
    /// its warnings, and a Notice that says how many; none clears the Notice. A recording that says no more than its
    /// snapshot cannot be seen to move.
    /// </summary>
    private void FollowTheMicrophone(IMicrophoneRecording? details, bool withClass, long now, List<(LogLevel Level, string Text)> lines)
    {
        if (details is null) return;

        var microphone = Named(details);
        if (microphone?.Id == _microphoneId) return;

        _microphoneId = microphone?.Id;
        if (microphone is null) return;

        var warnings = advice.ForMicrophone(microphone, withClass);
        foreach (var line in warnings) lines.Add((LogLevel.Warning, line.Text));
        if (warnings.Count > 0)
            Notice("MicrophoneWarnings", $"{RecordingMonitor.Count(warnings.Count, "warning")} about the microphone", string.Join(" ", warnings.Select(line => line.Text)), now);
        else
            Forget("MicrophoneWarnings");
    }

    /// <summary>Vantage's mode read again: a change to on, or to another mode, is a warning and a Notice; a change to off is said plainly and clears the Notice.</summary>
    private void PollVantage(long now, List<(LogLevel Level, string Text)> lines)
    {
        if (advice.VantageNow() is not { } line) return;

        if (line.Contains(VantageWentOff, StringComparison.Ordinal))
        {
            lines.Add((LogLevel.Information, line));
            Forget("Vantage");
            return;
        }

        lines.Add((LogLevel.Warning, line));
        Notice("Vantage", "Vantage noise cancelling on", line, now);
    }

    /// <summary>
    /// The echo checked again for the device the class plays on now: a warning and a Notice when the room hears it, and
    /// the Notice cleared when it does not. A device that cannot be looked up now is looked up again at the next look,
    /// with a Debug line once per stretch of failures.
    /// </summary>
    private void CheckTheClassDevice(TrackSnapshot main, MonitorContext context, long now, List<(LogLevel Level, string Text)> lines)
    {
        if (main.Source.Length == 0) return;

        var (device, heard, failure) = ClassDevice(main, context.Request.Source.Class);
        NoteTheListing(failure, lines);
        if (device is null || (device == _classDevice && heard == _classHeard)) return;

        _classDevice = device;
        _classHeard = heard;
        if (advice.ForClassDevice(device, heard) is { } echo)
        {
            lines.Add((LogLevel.Warning, echo));
            Notice("Echo", "echo: use headphones", echo, now);
        }
        else
        {
            Forget("Echo");
        }
    }

    /// <summary>
    /// The device the class plays on, by name, and whether the room hears it: in app mode the device the app plays
    /// through, found among the apps Windows lists; otherwise the recorded device itself. The name is null when the
    /// class's source has no name yet, when the app is not playing, or when Windows' lists cannot be read now, which
    /// <c>Failure</c> says in words (null otherwise).
    /// </summary>
    private (string? Name, bool HeardInTheRoom, string? Failure) ClassDevice(TrackSnapshot main, ClassSource? source)
    {
        if (main.Source.Length == 0) return (null, false, null);

        try
        {
            if (source is ClassSource.App)
            {
                if (outputs.GetPlayingApps().FirstOrDefault(app => app.Name.Equals(main.Source, StringComparison.OrdinalIgnoreCase)) is not { } playing)
                    return (null, false, null);

                var devices = outputs.GetOutputDevices();
                var device = devices.FirstOrDefault(candidate => playing.DeviceId.Length > 0 && candidate.Id == playing.DeviceId)
                    ?? devices.FirstOrDefault(candidate => candidate.Name == playing.DeviceName);
                return (playing.DeviceName.Length > 0 ? playing.DeviceName : device?.Name, device?.HeardInTheRoom ?? false, null);
            }

            return (main.Source, outputs.GetOutputDevices().FirstOrDefault(candidate => candidate.Name == main.Source)?.HeardInTheRoom ?? false, null);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            // Deliberately not rethrown: the echo check only words a warning, and a look at Windows' lists that fails, as
            // while its audio service restarts, must not keep the other checks, the heartbeat and the state from the
            // window. The failure goes back to the caller, which gives it a Debug line once per stretch (NoteTheListing),
            // and this check looks again at the next look.
            return (null, false, ex.Message);
        }
    }

    /// <summary>
    /// Notes a listing that failed, as <see cref="ClassDevice"/> reports it, at Debug once per stretch of failures: the
    /// echo check only words a warning, so the failure is a trace, not a warning. A listing that was read ends the stretch.
    /// </summary>
    private void NoteTheListing(string? failure, List<(LogLevel Level, string Text)> lines)
    {
        if (failure is null)
        {
            _listingFailing = false;
            return;
        }

        if (_listingFailing) return;

        _listingFailing = true;
        lines.Add((LogLevel.Debug, $"The echo check could not read Windows' device list, so it looks again at the next second: {failure}"));
    }

    /// <summary>
    /// The gate watched: a state that has held for a second is logged with the position it began at (the first state is
    /// the start's, said by the advice); the app not opening the microphone for 5 minutes is said once and a Notice while
    /// it lasts; its sessions unreadable for 10 seconds are said once and an Alert while they are.
    /// </summary>
    private void WatchTheGate(MicrophoneGateState state, string? why, FollowRule? follows, RecordingSnapshot snapshot, long now, long began,
        List<(LogLevel Level, string Text)> lines, List<Condition> conditions)
    {
        var app = MicrophoneWords.App(follows);
        if (_pending is null)
        {
            _pending = _logged = state;
            _pendingSince = now;
        }
        else if (state != _pending)
        {
            _pending = state;
            _pendingSince = now;
            _pendingPosition = snapshot.Position;
        }
        else if (state != _logged && time.GetElapsedTime(_pendingSince, now) >= HeldFor)
        {
            _logged = state;
            if (GateLine(state, why, app, _pendingPosition) is { } line) lines.Add((LogLevel.Information, line));
        }

        if (state == MicrophoneGateState.NotOpened)
        {
            _notOpenedSince ??= began;
            if (time.GetElapsedTime(_notOpenedSince.Value, now) >= NotOpenedNoticeAfter)
            {
                var whom = follows is FollowRule.Named ? $"{app} has not opened the microphone" : "no app has opened the microphone";
                var text = $"{whom} for {RecordingMonitor.DescribeLength(NotOpenedNoticeAfter)}; your voice is not recorded until it does. Join the class, or check that the page may use the microphone";
                if (!_notOpenedSaid)
                {
                    _notOpenedSaid = true;
                    lines.Add((LogLevel.Warning, text));
                }

                conditions.Add(("MicrophoneNotOpened", AttentionLevel.Notice, whom, text));
            }
        }
        else
        {
            _notOpenedSince = null;
            _notOpenedSaid = false;
        }

        if (state == MicrophoneGateState.Unreadable)
        {
            _unreadableSince ??= began;
            if (time.GetElapsedTime(_unreadableSince.Value, now) >= UnreadableAlertAfter)
            {
                if (!_unreadableSaid)
                {
                    _unreadableSaid = true;
                    _unreadableText = $"{Capitalised(why ?? "cannot read the microphone's sessions")}; your voice is not recorded until the sessions can be read again";
                    lines.Add((LogLevel.Warning, _unreadableText));
                }

                conditions.Add(("MicrophoneUnreadable", AttentionLevel.Alert, "CANNOT READ THE MICROPHONE SESSIONS", _unreadableText));
            }
        }
        else
        {
            _unreadableSince = null;
            _unreadableSaid = false;
        }
    }

    /// <summary>The line for a state of the gate that has held, from the position it began at; none for the microphone switched off, which V says, and for no app followed.</summary>
    private static string? GateLine(MicrophoneGateState state, string? why, string app, TimeSpan position)
    {
        var from = Durations.Format(position);
        return state switch
        {
            MicrophoneGateState.Recording => $"Microphone: {app} is listening; your voice is being recorded from {from}",
            MicrophoneGateState.Off or MicrophoneGateState.Unfollowed => null,
            _ => $"Microphone: {why ?? state.ToString()}; your voice is not recorded from {from}"
        };
    }

    /// <summary>A microphone missing beside the class: said every 30 seconds, and a Notice while it lasts. In person the class's own checks say that nothing is recorded.</summary>
    private void WatchAMissingMicrophone(TrackSnapshot track, bool withClass, long now, List<(LogLevel Level, string Text)> lines, List<Condition> conditions)
    {
        if (!withClass || track.Off || track.WaitingFor is not { } waitingFor)
        {
            _lastMissingWarning = null;
            return;
        }

        var text = $"Waiting for {waitingFor}; the class is being recorded";
        if (_lastMissingWarning is not { } last || time.GetElapsedTime(last, now) >= MissingReminderEvery)
        {
            _lastMissingWarning = now;
            lines.Add((LogLevel.Warning, text));
        }

        conditions.Add(("MicrophoneMissing", AttentionLevel.Notice, "no microphone", text));
    }

    /// <summary>
    /// Only exact zeros from the microphone while it should be heard (on, capturing, not paused, and the gate open when
    /// there is one), as the recording reports them once they have lasted <see cref="OnlyZerosLength"/>: said once, at
    /// the look that finds them, with what to check, and a Notice with the class or an Alert in person, where the whole
    /// recording is then silence. Where no gate has read the mute, the recording is asked once, at that look, whether
    /// Windows mutes the microphone, so that the line can say so outright. Anything but zeros ends it.
    /// </summary>
    private void WatchOnlyZeros(TrackSnapshot track, MicrophoneGateState? state, bool paused, bool withClass, string app, IMicrophoneRecording? details,
        List<(LogLevel Level, string Text)> lines, List<Condition> conditions)
    {
        var heard = state is null or MicrophoneGateState.Recording or MicrophoneGateState.Unfollowed;
        if (paused || track.Off || !track.Capturing || !track.OnlyZeros || !heard)
        {
            _zerosSaid = false;
            return;
        }

        if (!_zerosSaid)
        {
            _zerosSaid = true;
            _zerosText = OnlyZerosText(track, state, app, details);
            lines.Add((LogLevel.Warning, _zerosText));
        }

        conditions.Add(withClass
            ? ("MicrophoneSilent", AttentionLevel.Notice, "only silence from the microphone", _zerosText)
            : ("MicrophoneSilent", AttentionLevel.Alert, "ONLY SILENCE FROM THE MICROPHONE", _zerosText));
    }

    /// <summary>
    /// The words for only zeros from the microphone: with the followed app listening, the gate's looks have read the mute,
    /// so the privacy settings and the microphone itself are what to check; otherwise that it is muted in Windows, when
    /// Windows says so, or what to check when it does not say.
    /// </summary>
    private static string OnlyZerosText(TrackSnapshot track, MicrophoneGateState? state, string app, IMicrophoneRecording? details)
    {
        // The words give the length the recording's flag stands for, rather than a count that grows.
        var length = RecordingMonitor.Count((long)OnlyZerosLength.TotalMinutes, "minute");
        var silence = $"Only silence from the microphone \"{track.Source}\" for {length}";
        if (state is MicrophoneGateState.Recording)
            return $"{silence} although {app} is listening and Windows is not muting it. Check Settings > Privacy & security > Microphone (Let desktop apps access your microphone), and the microphone itself";
        if (details?.MutedInWindows == true)
            return $"{silence}: it is muted in Windows (the microphone key, or Sound settings > Input)";
        return $"{silence}. Check that it is not muted in Windows (the microphone key, or Sound settings > Input) and that Windows lets desktop apps use it (Settings > Privacy & security > Microphone)";
    }

    /// <summary>
    /// Whether Windows mutes the webcam's microphone, asked once per stretch of zeros long enough for a webcam rule, so that
    /// a muted microphone is left to the warning every microphone has; false while no such stretch lasts.
    /// </summary>
    private bool WebcamMuted(TrackSnapshot track, IMicrophoneRecording details)
    {
        if (track.RawZerosFor is not { } zeros || zeros < RecordingSession.WebcamZerosAtStart)
        {
            _webcamMuted = null;
            return false;
        }

        return _webcamMuted ??= details.MutedInWindows == true;
    }

    /// <summary>
    /// The webcam zeros rules (camera-first 3.4), in place of the warning about only silence for a microphone that belongs
    /// to a webcam, such as the OBSBOT, whose noise reduction sends exact zeros whenever the room is quiet (finding 4). The
    /// zeros are counted at the device's own depth, so Windows' faint residue counts as sound. At the start: only exact
    /// zeros since the capture opened for <see cref="RecordingSession.WebcamZerosAtStart"/>, said once a capture, in words
    /// that depend on whether the hold has the camera awake. Later, after sound: only exact zeros for
    /// <see cref="RecordingSession.WebcamZerosAfterSound"/>, said once a recording, never while the hold's frames arrive (the
    /// camera cannot be asleep), its Notice ending when sound returns. Each a Notice with the class, an Alert in person.
    /// </summary>
    private void WatchWebcamZeros(TrackSnapshot track, MicrophoneGateState? state, bool paused, bool withClass, Camera.WebcamState webcam,
        List<(LogLevel Level, string Text)> lines, List<Condition> conditions)
    {
        _zerosSaid = false;
        var heard = state is null or MicrophoneGateState.Recording or MicrophoneGateState.Unfollowed;
        var zeros = !paused && !track.Off && track.Capturing && heard ? track.RawZerosFor : null;

        if (zeros is { } sinceOpened && !track.RawSoundSinceOpened && sinceOpened >= RecordingSession.WebcamZerosAtStart)
        {
            if (_webcamStartSaidFor != track.CaptureNumber)
            {
                _webcamStartSaidFor = track.CaptureNumber;
                _webcamStartText = WebcamStartText(webcam);
                lines.Add((LogLevel.Warning, _webcamStartText));
            }

            conditions.Add(WebcamCondition(withClass, _webcamStartText));
            return;
        }

        var later = zeros is { } quiet && track.RawSoundSinceOpened && quiet >= RecordingSession.WebcamZerosAfterSound && !(webcam.Holding && webcam.HasFrames);
        if (later && _webcamLater == WebcamLater.NotSaid)
        {
            _webcamLater = WebcamLater.Lasting;
            _webcamLaterText = WebcamLaterText(webcam);
            lines.Add((LogLevel.Warning, _webcamLaterText));
        }
        else if (_webcamLater == WebcamLater.Lasting && zeros is { } now && now < RecordingSession.WebcamZerosAfterSound)
        {
            _webcamLater = WebcamLater.Over;
        }

        if (later && _webcamLater == WebcamLater.Lasting) conditions.Add(WebcamCondition(withClass, _webcamLaterText));
    }

    /// <summary>A webcam zeros rule as what needs attention: a Notice with the class, an Alert in person.</summary>
    private static Condition WebcamCondition(bool withClass, string text) => withClass
        ? ("WebcamSilent", AttentionLevel.Notice, "only zeros from the webcam's microphone", text)
        : ("WebcamSilent", AttentionLevel.Alert, "ONLY ZEROS FROM THE WEBCAM'S MICROPHONE", text);

    /// <summary>The webcam's hold has a problem (the picture stopped, or the camera cannot be opened): a Notice with the class, an Alert in person, while it lasts.</summary>
    private static void WatchTheHold(Camera.WebcamState? webcam, bool withClass, List<Condition> conditions)
    {
        if (webcam is not { Problem: { } problem }) return;

        var words = webcam.ProblemShort ?? "camera";
        conditions.Add(withClass
            ? ("CameraHold", AttentionLevel.Notice, words, problem)
            : ("CameraHold", AttentionLevel.Alert, words.ToUpperInvariant(), problem));
    }

    /// <summary>The start rule's words: what the light says, or, with the hold's frames arriving, what else to check.</summary>
    public static string WebcamStartText(Camera.WebcamState webcam)
    {
        var since = $"The microphone of the camera \"{webcam.CameraName}\" has sent only exact zeros since it opened, {RecordingMonitor.Count((long)RecordingSession.WebcamZerosAtStart.TotalMinutes, "minute")} ago";
        return webcam.Holding && webcam.HasFrames
            ? $"{since}, although its camera is open and awake. Check that its microphone is on in OBSBOT Center, and that Windows lets desktop apps use the microphone"
            : $"{since}. If the camera's light is off, the camera is asleep and its microphone off: unfold its lens or wake it, and turn off Device Auto Sleep in OBSBOT Center or turn on Recording:WakeCameraMicrophone. If the light is on, the room is just quiet: this camera sends exact zeros in silence";
    }

    /// <summary>The later rule's words: the light tells a camera fallen asleep from a quiet room.</summary>
    public static string WebcamLaterText(Camera.WebcamState webcam) =>
        $"The microphone of the camera \"{webcam.CameraName}\" has sent only exact zeros for {RecordingMonitor.Count((long)RecordingSession.WebcamZerosAfterSound.TotalMinutes, "minute")}. If its light is off, the camera has fallen asleep (an open microphone does not keep it awake) and your voice is not being recorded: turn off Device Auto Sleep in OBSBOT Center, or turn on Recording:WakeCameraMicrophone. If the light is on, the room is quiet and nothing is lost";

    /// <summary>The evidence the gate reported since the last look: each a warning and a Notice.</summary>
    private void ReportEvidence(string app, long now, List<(LogLevel Level, string Text)> lines)
    {
        while (_evidence.TryDequeue(out var text))
        {
            lines.Add((LogLevel.Warning, text));
            Notice("Evidence", $"{app} let go of the microphone (see the log)", text, now);
        }
    }

    /// <summary>Keeps the gate's evidence, from the watch's thread, for the next look.</summary>
    private void OnEvidence(object? sender, string text) => _evidence.Enqueue(text);

    /// <summary>Makes a warning a Notice from now, in place of any earlier one of the same name.</summary>
    private void Notice(string name, string shortWords, string text, long now)
    {
        _noticed.RemoveAll(notice => notice.Name == name);
        _noticed.Add(new Noticed(name, shortWords, text, now));
    }

    /// <summary>Ends the Notice of that name, when there is one.</summary>
    private void Forget(string name) => _noticed.RemoveAll(notice => notice.Name == name);

    /// <summary>The words with their first letter in upper case, for the start of a sentence.</summary>
    private static string Capitalised(string words) => words.Length == 0 ? words : char.ToUpperInvariant(words[0]) + words[1..];

    /// <summary>A warning that is a Notice: its name, its words for the title and the log, and when it was given.</summary>
    private sealed record Noticed(string Name, string Short, string Text, long At);

    /// <summary>Where the webcam rule after sound stands: said at most once a recording.</summary>
    private enum WebcamLater
    {
        /// <summary>Not said yet.</summary>
        NotSaid,

        /// <summary>Said, and the zeros last: a Notice, or an Alert in person.</summary>
        Lasting,

        /// <summary>Said, and sound came back: never said again in this recording.</summary>
        Over
    }

    /// <summary>The ear for the gate's evidence; disposing it stops listening.</summary>
    private sealed class Ear(Action? stop) : IDisposable
    {
        /// <summary>Stops listening, once.</summary>
        public void Dispose() => Interlocked.Exchange(ref stop, null)?.Invoke();
    }
}
