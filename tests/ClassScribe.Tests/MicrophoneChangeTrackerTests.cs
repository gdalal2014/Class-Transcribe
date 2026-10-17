using ClassScribe.Audio.Microphones;

namespace ClassScribe.Tests;

/// <summary>
/// The microphone check's change tracker: given what each check read about the microphones, their sessions and Windows'
/// microphone usage record, it answers with a line for each change, as MicWatch's tracker did, with the HOW-TO's
/// examples as the expected strings. It is pure: the listings are made by hand, and the times are fixed.
/// </summary>
[TestFixture]
public class MicrophoneChangeTrackerTests
{
    /// <summary>The laptop's time zone in the tests: four hours behind UTC the whole year, so that times read the same on every machine.</summary>
    private static readonly TimeSpan Zone = TimeSpan.FromHours(-4);

    /// <summary>The laptop's zone as the tracker takes it.</summary>
    private static readonly TimeZoneInfo LaptopZone = TimeZoneInfo.CreateCustomTimeZone("Test fixed", Zone, "Test fixed", "Test fixed");

    /// <summary>When the first check is made, in local time.</summary>
    private static readonly DateTimeOffset At = new(2026, 10, 17, 19, 0, 12, 177, Zone);

    /// <summary>The path Windows' usage record gives for ClassScribe's own program.</summary>
    private static readonly string OwnPath = Laptop.Names[Laptop.OwnProcess].Path!;

    /// <summary>Chrome's capture session's instance identifier, which stays the same while the session changes state.</summary>
    private const string ChromeKey = "{0.0.1.00000000}.{06cd3d4c-1b2e-4f6a-9d8c-chrome}";

    /// <summary>The path Windows' usage record gives for Chrome.</summary>
    private const string ChromePath = @"C:\Program Files\Google\Chrome\Application\chrome.exe";

    private FakeProcessNames _names = null!;
    private MicrophoneChangeTracker _tracker = null!;

    /// <summary>A tracker for the check's own process, with this laptop's processes named.</summary>
    [SetUp]
    public void SetUp()
    {
        _names = new FakeProcessNames();
        _tracker = new MicrophoneChangeTracker(_names, new FakeOwnProcesses(Laptop.OwnProcess), Laptop.OwnProcess, LaptopZone);
    }

    /// <summary>The built-in array as a check reads it: Windows' default for every role, not muted, at 93 percent, with <paramref name="sessions"/>.</summary>
    private static MicrophoneSessionListing Array(bool? muted = false, int? volume = 93,
        MicrophoneRoles roles = MicrophoneRoles.Console | MicrophoneRoles.Multimedia | MicrophoneRoles.Communications, params CaptureSession[] sessions) =>
        new(Laptop.ArrayId, Laptop.ArrayName, roles, muted, volume, sessions, Complete: true, Problem: null);

    /// <summary>The OBSBOT as a check reads it: Windows' default for calls, at 80 percent.</summary>
    private static MicrophoneSessionListing Obsbot() =>
        new(Laptop.ObsbotId, Laptop.ObsbotName, MicrophoneRoles.Communications, MutedInWindows: false, VolumePercent: 80, [], Complete: true, Problem: null);

    /// <summary>Chrome's capture session on the array, held by its utility process, in <paramref name="state"/>.</summary>
    private static CaptureSession Chrome(CaptureSessionState state = CaptureSessionState.Active) =>
        new(ChromeKey, Laptop.ArrayId, Laptop.ChromeUtility, "chrome", SystemSounds: false, state, SessionMuted: false);

    /// <summary>A usage record that allows the microphone to every app, with <paramref name="apps"/>.</summary>
    private static UsageRecordListing Record(params UsageEntry[] apps) => new("Allow", "Allow", apps, null);

    /// <summary>Chrome's entry in the usage record, last opened at <paramref name="start"/> and closed at <paramref name="stop"/> (0 while open).</summary>
    private static UsageEntry ChromeEntry(long? start, long? stop) =>
        new(@"NonPackaged\C:#Program Files#Google#Chrome#Application#chrome.exe", "chrome.exe", ChromePath, start, stop);

    /// <summary>A moment on the day of the tests, in local time, as a Windows FILETIME.</summary>
    private static long FileTime(int hour, int minute, int second, int millisecond, int day = 17) =>
        new DateTimeOffset(2026, 10, day, hour, minute, second, millisecond, Zone).UtcDateTime.ToFileTimeUtc();

    /// <summary>ClassScribe's own program's entry in the usage record, last opened at <paramref name="start"/> and closed at <paramref name="stop"/> (0 while open).</summary>
    private static UsageEntry OwnEntry(long? start, long? stop) =>
        new(@"NonPackaged\" + OwnPath.Replace('\\', '#'), "ClassScribe.exe", OwnPath, start, stop);

    /// <summary>The recorder of a class recorded beside the check: another ClassScribe process with a session on the array.</summary>
    private static CaptureSession Recorder() =>
        new("recorder-session", Laptop.ArrayId, Laptop.OtherClassScribe, "ClassScribe", SystemSounds: false, CaptureSessionState.Active, SessionMuted: false);

    /// <summary>The lines as "KIND text", for comparing.</summary>
    private static string[] Texts(IReadOnlyList<MicCheckLine> lines) => [.. lines.Select(line => $"{line.Kind} {line.Text}")];

    /// <summary>The first check writes every active microphone as it is, marked "at start", in the HOW-TO's words, stamped with the check's time.</summary>
    [Test]
    public void The_first_check_writes_every_microphone_at_start()
    {
        var lines = _tracker.Update([Array()], At);

        Assert.That(Texts(lines), Is.EqualTo(new[]
        {
            $"MIC {Laptop.ArrayName}: at start: Windows default: Console, Multimedia, Communications; muted in Windows: no; input volume: 93%"
        }));
        Assert.That(lines[0].At, Is.EqualTo(At));
    }

    /// <summary>Without an active microphone the first check says so, so that an empty log is not taken for a check that saw nothing.</summary>
    [Test]
    public void Without_a_microphone_the_first_check_says_so() =>
        Assert.That(Texts(_tracker.Update([], At)), Is.EqualTo(new[] { "MIC No microphone is active at start." }));

    /// <summary>A microphone whose defaults, mute or volume change gets a line with each changed field followed by its old value; nothing while nothing changes.</summary>
    [Test]
    public void A_microphone_that_changes_shows_each_changed_field_with_its_old_value()
    {
        _tracker.Update([Array()], At);

        var unchanged = _tracker.Update([Array()], At.AddMilliseconds(250));
        var changed = _tracker.Update([Array(muted: true, volume: 50, roles: MicrophoneRoles.Console)], At.AddMilliseconds(500));

        Assert.That(unchanged, Is.Empty);
        Assert.That(Texts(changed), Is.EqualTo(new[]
        {
            $"MIC {Laptop.ArrayName}: changed: Windows default: Console (was Console, Multimedia, Communications); muted in Windows: YES (was no); input volume: 50% (was 93%)"
        }));
    }

    /// <summary>A mute or volume that could not be read shows as unknown, and the microphone's problem is on the line until it clears.</summary>
    [Test]
    public void What_could_not_be_read_shows_as_unknown_with_the_problem()
    {
        var problem = Array(muted: null, volume: null) with { Problem = "endpoint volume not available: 0x80070005" };
        _tracker.Update([problem], At);

        var cleared = _tracker.Update([Array()], At.AddMilliseconds(250));

        Assert.That(Texts(cleared), Is.EqualTo(new[]
        {
            $"MIC {Laptop.ArrayName}: changed: Windows default: Console, Multimedia, Communications; muted in Windows: no (was unknown); input volume: 93% (was unknown); the earlier problem has cleared"
        }));
    }

    /// <summary>A microphone plugged in gets a "became active" line with its state, and one unplugged a "no longer active" line.</summary>
    [Test]
    public void A_microphone_becoming_active_and_no_longer_active_are_lines()
    {
        _tracker.Update([Array()], At);

        var pluggedIn = _tracker.Update([Array(), Obsbot()], At.AddSeconds(1));
        var unplugged = _tracker.Update([Array()], At.AddSeconds(2));

        Assert.That(Texts(pluggedIn), Is.EqualTo(new[]
        {
            $"MIC {Laptop.ObsbotName}: became active: Windows default: Communications; muted in Windows: no; input volume: 80%"
        }));
        Assert.That(Texts(unplugged), Is.EqualTo(new[] { $"MIC {Laptop.ObsbotName}: no longer active (unplugged, disabled or removed)" }));
    }

    /// <summary>
    /// A session that appears is numbered and named by its process, with its state, its mute, its program and its identifier;
    /// a change shows the state with its old value; and a session Windows no longer lists is gone.
    /// </summary>
    [Test]
    public void A_session_appearing_changing_and_going_are_lines()
    {
        _tracker.Update([Array()], At);

        var appeared = _tracker.Update([Array(sessions: Chrome())], At.AddSeconds(1));
        var changed = _tracker.Update([Array(sessions: Chrome(CaptureSessionState.Inactive))], At.AddSeconds(2));
        var gone = _tracker.Update([Array()], At.AddSeconds(3));

        var label = $"#1 chrome (pid {Laptop.ChromeUtility}) on {Laptop.ArrayName}";
        Assert.That(Texts(appeared), Is.EqualTo(new[] { $"SESSION {label}: appeared: state: Active; session muted: no; program {ChromePath}; session id {ChromeKey}" }));
        Assert.That(Texts(changed), Is.EqualTo(new[] { $"SESSION {label}: changed: state: Inactive (was Active); session muted: no" }));
        Assert.That(Texts(gone), Is.EqualTo(new[] { $"SESSION {label}: gone (Windows no longer lists it)" }));
    }

    /// <summary>A session already there at the first check is written "at start", after its microphone's line, and sessions are numbered in the order first seen.</summary>
    [Test]
    public void Sessions_at_the_first_check_are_at_start_and_numbered_in_order()
    {
        var vibe = new CaptureSession("vibe-session", Laptop.ArrayId, Laptop.Vibe, "vibe", SystemSounds: false, CaptureSessionState.Inactive, SessionMuted: true);

        var lines = _tracker.Update([Array(sessions: [Chrome(), vibe])], At);

        Assert.That(Texts(lines), Is.EqualTo(new[]
        {
            $"MIC {Laptop.ArrayName}: at start: Windows default: Console, Multimedia, Communications; muted in Windows: no; input volume: 93%",
            $"SESSION #1 chrome (pid {Laptop.ChromeUtility}) on {Laptop.ArrayName}: at start: state: Active; session muted: no; program {ChromePath}; session id {ChromeKey}",
            $"SESSION #2 vibe (pid {Laptop.Vibe}) on {Laptop.ArrayName}: at start: state: Inactive; session muted: YES; program {Laptop.Names[Laptop.Vibe].Path}; session id vibe-session"
        }));
    }

    /// <summary>Windows' own sounds session is named as such, without a program; a process that cannot be looked at is said to be one.</summary>
    [Test]
    public void Windows_sounds_and_a_process_that_cannot_be_looked_at_are_named_as_such()
    {
        var unknown = new CaptureSession("unknown-session", Laptop.ArrayId, 9999, null, SystemSounds: false, CaptureSessionState.Active, SessionMuted: null);

        var lines = _tracker.Update([Array(sessions: [Laptop.SystemSounds(Laptop.ArrayId), unknown])], At);

        Assert.That(Texts(lines).Skip(1), Is.EqualTo(new[]
        {
            $"SESSION #1 Windows' system sounds (pid 0) on {Laptop.ArrayName}: at start: state: Active; session muted: unknown; session id {Laptop.ArrayId}|system",
            $"SESSION #2 a process that cannot be looked at (pid 9999) on {Laptop.ArrayName}: at start: state: Active; session muted: unknown; session id unknown-session"
        }));
    }

    /// <summary>
    /// A session of the check's own process is written with a SELF line, which should never appear; a session of another
    /// ClassScribe process, the recorder of a class recorded beside the check, is labelled as such and expected.
    /// </summary>
    [Test]
    public void The_own_process_gets_a_SELF_line_and_another_ClassScribe_process_is_labelled()
    {
        var own = new CaptureSession("own-session", Laptop.ArrayId, Laptop.OwnProcess, "ClassScribe", SystemSounds: false, CaptureSessionState.Active, SessionMuted: false);
        var recorder = new CaptureSession("recorder-session", Laptop.ArrayId, Laptop.OtherClassScribe, "ClassScribe", SystemSounds: false, CaptureSessionState.Active, SessionMuted: false);

        var lines = _tracker.Update([Array(sessions: [own, recorder])], At);

        var program = Laptop.Names[Laptop.OwnProcess].Path;
        Assert.That(Texts(lines).Skip(1), Is.EqualTo(new[]
        {
            $"SESSION #1 ClassScribe (pid {Laptop.OwnProcess}) on {Laptop.ArrayName}: at start: state: Active; session muted: no; program {program}; session id own-session",
            "SELF ClassScribe's own process has a session on this microphone. This should never happen; please say so when you send the log.",
            $"SESSION #2 ClassScribe, a recording in progress (pid {Laptop.OtherClassScribe}) on {Laptop.ArrayName}: at start: state: Active; session muted: no; program {program}; session id recorder-session"
        }));
    }

    /// <summary>
    /// A session missing from a listing whose sessions could not all be read may still be there, so it is not reported
    /// gone; the microphone's problem is on its line instead. Once the sessions are read completely, it is gone.
    /// </summary>
    [Test]
    public void A_session_missing_from_an_incomplete_listing_is_not_gone()
    {
        _tracker.Update([Array(sessions: Chrome())], At);

        var incomplete = _tracker.Update([Array() with { Complete = false, Problem = "session 1 of 2 not readable: 0x88890004" }], At.AddSeconds(1));
        var complete = _tracker.Update([Array()], At.AddSeconds(2));

        Assert.That(Texts(incomplete), Is.EqualTo(new[]
        {
            $"MIC {Laptop.ArrayName}: changed: Windows default: Console, Multimedia, Communications; muted in Windows: no; input volume: 93%; problem: session 1 of 2 not readable: 0x88890004"
        }));
        Assert.That(Texts(complete), Is.EqualTo(new[]
        {
            $"MIC {Laptop.ArrayName}: changed: Windows default: Console, Multimedia, Communications; muted in Windows: no; input volume: 93%; the earlier problem has cleared",
            $"SESSION #1 chrome (pid {Laptop.ChromeUtility}) on {Laptop.ArrayName}: gone (Windows no longer lists it)"
        }));
    }

    /// <summary>
    /// The first reading of the usage record writes Windows' two access settings, each app's last use with its length (the date
    /// left out when it is the day of the check), and the apps listed without any use; times and lengths as the HOW-TO shows them.
    /// </summary>
    [Test]
    public void The_first_reading_of_the_usage_record_writes_the_settings_and_each_apps_last_use()
    {
        var chrome = ChromeEntry(FileTime(10, 2, 41, 450, day: 11), FileTime(12, 30, 14, 256, day: 11));
        var teams = new UsageEntry("MSTeams_8wekyb3d8bbwe", "MSTeams_8wekyb3d8bbwe", null, FileTime(18, 0, 0, 0), 0);

        var lines = _tracker.Update(Record(chrome, FakeMicrophoneUsageRecord.Unused("vibe.exe"), teams, FakeMicrophoneUsageRecord.Unused("msedge.exe")), At);

        Assert.That(Texts(lines), Is.EqualTo(new[]
        {
            "PRIVACY Windows settings: microphone access: Allow; desktop apps: Allow",
            $"PRIVACY chrome.exe: at start: not using the microphone; last use 2026-10-11 10:02:41.450 to 12:30:14.256 (2 h 27 min 32 s); program {ChromePath}",
            "PRIVACY MSTeams_8wekyb3d8bbwe: at start: USING THE MICROPHONE NOW, since 18:00:00.000",
            "PRIVACY listed with no use recorded: vibe.exe, msedge.exe"
        }));
    }

    /// <summary>
    /// An app's use starting is STARTED with Windows' own time; its use ending is STOPPED with the length; a start and a stop
    /// both new since the last check were a brief use between two checks; and an entry that disappears is said to be removed.
    /// </summary>
    [Test]
    public void An_apps_use_starting_stopping_briefly_and_removed_are_lines()
    {
        _tracker.Update(Record(ChromeEntry(FileTime(10, 2, 41, 450, day: 11), FileTime(12, 30, 14, 256, day: 11))), At);

        var started = _tracker.Update(Record(ChromeEntry(FileTime(19, 3, 40, 431), 0)), At.AddSeconds(1));
        var stopped = _tracker.Update(Record(ChromeEntry(FileTime(19, 3, 40, 431), FileTime(19, 4, 2, 191))), At.AddSeconds(2));
        var brief = _tracker.Update(Record(ChromeEntry(FileTime(19, 5, 0, 0), FileTime(19, 5, 0, 100))), At.AddSeconds(3));
        var removed = _tracker.Update(Record(), At.AddSeconds(4));

        Assert.That(Texts(started), Is.EqualTo(new[] { "PRIVACY chrome.exe: STARTED using the microphone at 19:03:40.431" }));
        Assert.That(Texts(stopped), Is.EqualTo(new[] { "PRIVACY chrome.exe: STOPPED using the microphone at 19:04:02.191, after 21.8 s (from 19:03:40.431)" }));
        Assert.That(Texts(brief), Is.EqualTo(new[] { "PRIVACY chrome.exe: used the microphone briefly between two checks: 19:05:00.000 to 19:05:00.100 (0.1 s)" }));
        Assert.That(Texts(removed), Is.EqualTo(new[] { "PRIVACY chrome.exe: its entry was removed from the record" }));
    }

    /// <summary>An app whose entry had no use, or was not there, gets a "first use recorded" line when its use is first recorded; an entry that does not change gives nothing.</summary>
    [Test]
    public void An_apps_first_recorded_use_is_a_line_and_an_unchanged_entry_is_not()
    {
        _tracker.Update(Record(FakeMicrophoneUsageRecord.Unused("vibe.exe")), At);

        var unchanged = _tracker.Update(Record(FakeMicrophoneUsageRecord.Unused("vibe.exe")), At.AddSeconds(1));
        var vibe = FakeMicrophoneUsageRecord.Unused("vibe.exe") with { StartFileTime = FileTime(19, 10, 0, 0), StopFileTime = 0 };
        var first = _tracker.Update(Record(vibe), At.AddSeconds(2));

        Assert.That(unchanged, Is.Empty);
        Assert.That(Texts(first), Is.EqualTo(new[] { $"PRIVACY vibe.exe: first use recorded: USING THE MICROPHONE NOW, since 19:10:00.000; program {vibe.Path}" }));
    }

    /// <summary>A change in Windows' access settings is a line with the old values.</summary>
    [Test]
    public void A_change_in_the_access_settings_is_a_line()
    {
        _tracker.Update(Record(), At);

        var lines = _tracker.Update(new UsageRecordListing("Deny", "Allow", [], null), At.AddSeconds(1));

        Assert.That(Texts(lines), Is.EqualTo(new[] { "PRIVACY Windows settings: microphone access: Deny (was Allow); desktop apps: Allow" }));
    }

    /// <summary>A problem reading the microphones is an ERROR line once, not every check, and a NOTE says when they can be read again; the first readable check is then the start.</summary>
    [Test]
    public void A_problem_reading_the_microphones_is_written_once_and_a_note_when_it_clears()
    {
        var first = _tracker.MicrophonesUnreadable("COMException: The audio service is not running (0x88890010)", At);
        var again = _tracker.MicrophonesUnreadable("COMException: The audio service is not running (0x88890010)", At.AddMilliseconds(250));
        var other = _tracker.MicrophonesUnreadable("InvalidCastException: no enumerator", At.AddMilliseconds(500));
        var cleared = _tracker.Update([Array()], At.AddSeconds(1));

        Assert.That(Texts(first), Is.EqualTo(new[] { "ERROR Windows' list of microphones could not be read: COMException: The audio service is not running (0x88890010). The check keeps trying." }));
        Assert.That(again, Is.Empty);
        Assert.That(Texts(other), Is.EqualTo(new[] { "ERROR Windows' list of microphones could not be read: InvalidCastException: no enumerator. The check keeps trying." }));
        Assert.That(Texts(cleared), Is.EqualTo(new[]
        {
            "NOTE Windows' list of microphones can be read again.",
            $"MIC {Laptop.ArrayName}: at start: Windows default: Console, Multimedia, Communications; muted in Windows: no; input volume: 93%"
        }));
    }

    /// <summary>A usage record that cannot be read, whether it says so itself or its reader failed, is an ERROR line once and a NOTE when it reads again.</summary>
    [Test]
    public void A_problem_reading_the_usage_record_is_written_once_and_a_note_when_it_clears()
    {
        var first = _tracker.Update(new UsageRecordListing(null, null, [], "SecurityException: Requested registry access is not allowed."), At);
        var again = _tracker.Update(new UsageRecordListing(null, null, [], "SecurityException: Requested registry access is not allowed."), At.AddMilliseconds(250));
        var thrown = _tracker.UsageRecordUnreadable("InvalidOperationException: boom", At.AddMilliseconds(500));
        var cleared = _tracker.Update(Record(), At.AddSeconds(1));

        Assert.That(Texts(first), Is.EqualTo(new[] { "ERROR Windows' microphone usage record could not be read: SecurityException: Requested registry access is not allowed. The check keeps trying." }));
        Assert.That(again, Is.Empty);
        Assert.That(Texts(thrown), Is.EqualTo(new[] { "ERROR Windows' microphone usage record could not be read: InvalidOperationException: boom. The check keeps trying." }));
        Assert.That(Texts(cleared), Is.EqualTo(new[]
        {
            "NOTE Windows' microphone usage record can be read again.",
            "PRIVACY Windows settings: microphone access: Allow; desktop apps: Allow"
        }));
    }

    /// <summary>
    /// The check's own program using the microphone now, by the usage record, is a SELF line, which should never appear,
    /// unless another ClassScribe process holds a microphone: a recorder beside the check is the same program, and its use
    /// of the microphone is expected.
    /// </summary>
    [Test]
    public void The_own_program_in_the_usage_record_is_SELF_unless_another_ClassScribe_process_holds_a_microphone()
    {
        var self = OwnEntry(FileTime(19, 0, 0, 0), 0);

        var alone = _tracker.Update(Record(self), At);
        _tracker.Update([Array(sessions: Recorder())], At.AddSeconds(1));
        var beside = _tracker.Update(Record(self with { StartFileTime = FileTime(19, 0, 5, 0) }), At.AddSeconds(1));

        Assert.That(Texts(alone), Is.EqualTo(new[]
        {
            "PRIVACY Windows settings: microphone access: Allow; desktop apps: Allow",
            $"PRIVACY ClassScribe.exe: at start: USING THE MICROPHONE NOW, since 19:00:00.000; program {OwnPath}",
            "SELF ClassScribe's own program is using the microphone now, by Windows' microphone usage record, and no other ClassScribe process holds a microphone. This should never happen; please say so when you send the log."
        }));
        Assert.That(Texts(beside), Is.EqualTo(new[] { "PRIVACY ClassScribe.exe: started again at 19:00:05.000, with no stop seen in between (in use since 19:00:00.000 before)" }));
    }

    /// <summary>
    /// The record keeps each program's last use for ever, so ClassScribe's own program is in it once a recorder has ever
    /// recorded the microphone: a past use, with its start and its stop, is the recorder's and is no SELF line, at the first
    /// reading or later.
    /// </summary>
    [Test]
    public void The_own_programs_past_use_in_the_usage_record_is_not_SELF()
    {
        _tracker.Update([Array()], At);

        var atStart = _tracker.Update(Record(OwnEntry(FileTime(19, 0, 0, 0, day: 16), FileTime(21, 0, 0, 0, day: 16))), At);
        var brief = _tracker.Update(Record(OwnEntry(FileTime(19, 0, 20, 0), FileTime(19, 0, 20, 500))), At.AddSeconds(10));

        Assert.That(Texts(atStart), Is.EqualTo(new[]
        {
            "PRIVACY Windows settings: microphone access: Allow; desktop apps: Allow",
            $"PRIVACY ClassScribe.exe: at start: not using the microphone; last use 2026-10-16 19:00:00.000 to 21:00:00.000 (2 h 00 min 00 s); program {OwnPath}"
        }));
        Assert.That(Texts(brief), Is.EqualTo(new[] { "PRIVACY ClassScribe.exe: used the microphone briefly between two checks: 19:00:20.000 to 19:00:20.500 (0.5 s)" }));
    }

    /// <summary>
    /// A recorder beside the check stopping is no SELF line: its session goes and the record's STOPPED comes at the same
    /// look, or the look after, and a program that has stopped using the microphone is not using it. A record that says the
    /// own program started again while the recorder held the microphone at the look before, as when the recorder moves to
    /// another microphone between two looks, is not SELF either; two looks after its session went, it is.
    /// </summary>
    [Test]
    public void A_recorder_that_stops_or_moves_between_two_looks_is_not_SELF()
    {
        _tracker.Update([Array(sessions: Recorder())], At);
        _tracker.Update(Record(OwnEntry(FileTime(19, 0, 0, 0), 0)), At);

        _tracker.Update([Array()], At.AddSeconds(1));
        var stopped = _tracker.Update(Record(OwnEntry(FileTime(19, 0, 0, 0), FileTime(19, 0, 13, 0))), At.AddSeconds(1));
        var moved = _tracker.Update(Record(OwnEntry(FileTime(19, 0, 13, 100), 0)), At.AddSeconds(1));
        _tracker.Update([Array()], At.AddSeconds(2));
        var stillAlone = _tracker.Update(Record(OwnEntry(FileTime(19, 0, 14, 0), 0)), At.AddSeconds(2));

        Assert.That(Texts(stopped), Is.EqualTo(new[] { "PRIVACY ClassScribe.exe: STOPPED using the microphone at 19:00:13.000, after 13.0 s (from 19:00:00.000)" }));
        Assert.That(Texts(moved), Is.EqualTo(new[] { "PRIVACY ClassScribe.exe: STARTED using the microphone at 19:00:13.100" }));
        Assert.That(Texts(stillAlone), Is.EqualTo(new[]
        {
            "PRIVACY ClassScribe.exe: started again at 19:00:14.000, with no stop seen in between (in use since 19:00:13.100 before)",
            "SELF ClassScribe's own program is using the microphone now, by Windows' microphone usage record, and no other ClassScribe process holds a microphone. This should never happen; please say so when you send the log."
        }));
    }

    /// <summary>
    /// A time from the usage record is shown in the laptop's zone as it was at that moment, not as it is at the check: a last
    /// use on the other side of a change to or from daylight saving time reads as the clock showed it then.
    /// </summary>
    [Test]
    public void A_time_on_the_other_side_of_a_daylight_saving_change_is_shown_as_the_clock_showed_it_then()
    {
        // A zone five hours behind UTC, and four behind while daylight saving time runs, from 8 March to 1 November.
        var daylight = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(DateTime.MinValue.Date, DateTime.MaxValue.Date, TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 8),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 2, 0, 0), 11, 1));
        var zone = TimeZoneInfo.CreateCustomTimeZone("Test shifting", TimeSpan.FromHours(-5), "Test shifting", "Test standard", "Test daylight", [daylight]);
        var tracker = new MicrophoneChangeTracker(_names, new FakeOwnProcesses(Laptop.OwnProcess), Laptop.OwnProcess, zone);
        var winter = new DateTimeOffset(2026, 11, 2, 19, 0, 12, 177, TimeSpan.FromHours(-5));

        var lines = tracker.Update(Record(ChromeEntry(FileTime(19, 0, 0, 0), FileTime(21, 0, 0, 0))), winter);

        Assert.That(Texts(lines)[1], Is.EqualTo($"PRIVACY chrome.exe: at start: not using the microphone; last use 2026-10-17 19:00:00.000 to 21:00:00.000 (2 h 00 min 00 s); program {ChromePath}"));
    }
}
