using ClassScribe.Audio.Microphones;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Tests;

/// <summary>
/// The watch that reads, four times a second, whether the class app has the recorded microphone open and is taking
/// sound, with the readers played by fakes: which process counts as the app, what each look says, the look over every
/// microphone every ten seconds, the thread, and what happens when a reader fails in a way it never should.
/// </summary>
[TestFixture]
public class ClassMicrophoneWatchTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 17, 19, 0, 0, TimeSpan.Zero);

    private ManualTimeProvider _time = null!;
    private FakeMicrophoneSessions _sessions = null!;
    private FakeMicrophoneUsageRecord _record = null!;
    private FakeProcessNames _names = null!;
    private RecordingLogger<ClassMicrophoneWatch> _log = null!;
    private ClassMicrophoneWatch _watch = null!;
    private List<WatchLook> _looked = null!;

    /// <summary>A clock moved by hand, the readers as fakes with this laptop's microphones, and a watch following chrome on the array.</summary>
    [SetUp]
    public void SetUp()
    {
        _time = new ManualTimeProvider(Start);
        _sessions = new FakeMicrophoneSessions();
        _sessions.Set(Laptop.Listing(Laptop.ArrayId));
        _sessions.Set(Laptop.Listing(Laptop.ObsbotId));
        _record = new FakeMicrophoneUsageRecord();
        _names = new FakeProcessNames();
        _log = new RecordingLogger<ClassMicrophoneWatch>();
        _looked = [];
        Make(new FollowRule.Named("chrome"));
    }

    /// <summary>Stops the watch.</summary>
    [TearDown]
    public void TearDown() => _watch.Dispose();

    /// <summary>Makes the watch under test for <paramref name="rule"/>, reading the array, with its looks kept.</summary>
    private void Make(FollowRule rule)
    {
        _watch = new ClassMicrophoneWatch(_sessions, _record, _names, rule, new FakeOwnProcesses(Laptop.OwnProcess), _time, _log);
        _watch.Looked += (_, look) => _looked.Add(look);
        _watch.Follow(Laptop.ArrayId);
    }

    /// <summary>Chrome's capture session belongs to a chrome.exe utility process, not the browser: the app is matched by its process name, so that session counts.</summary>
    [Test]
    public void Chrome_is_matched_by_the_name_of_its_utility_process()
    {
        _sessions.SetSessions(Laptop.ArrayId, Laptop.Session(Laptop.ArrayId, Laptop.ChromeUtility), Laptop.SystemSounds(Laptop.ArrayId));

        var look = _watch.LookNow();

        Assert.Multiple(() =>
        {
            Assert.That(look.Listening, Is.EqualTo(AppListening.Yes));
            Assert.That(look.Holds, Is.True);
            Assert.That(look.ListeningApps, Is.EqualTo(new[] { "chrome" }));
            Assert.That(look.MutedInWindows, Is.False);
            Assert.That(look.RecordedMicrophone, Is.EqualTo(Laptop.ArrayName));
            Assert.That(look.OtherMicrophone, Is.Null);
            Assert.That(look.Problem, Is.Null);
            Assert.That(_looked, Is.EqualTo(new[] { look }), "handed to Looked too");
        });
    }

    /// <summary>The match is the one --app uses, equal to or containing the name part: chromedriver matches chrome too, as it does there, which this test pins.</summary>
    [Test]
    public void The_match_is_the_one_app_mode_uses_so_chromedriver_matches_chrome()
    {
        _sessions.SetSessions(Laptop.ArrayId, Laptop.Session(Laptop.ArrayId, Laptop.ChromeDriver));

        Assert.That(_watch.LookNow().Listening, Is.EqualTo(AppListening.Yes));
    }

    /// <summary>Following any app, ClassScribe's own processes, by id and by name, and Windows' system sounds never count; any other app does.</summary>
    [Test]
    public void Following_any_app_ignores_ClassScribes_own_processes_and_Windows_sounds()
    {
        Make(new FollowRule.Any());
        _sessions.SetSessions(Laptop.ArrayId, Laptop.Session(Laptop.ArrayId, Laptop.OwnProcess), Laptop.Session(Laptop.ArrayId, Laptop.OtherClassScribe), Laptop.SystemSounds(Laptop.ArrayId));

        var look = _watch.LookNow();
        Assert.Multiple(() =>
        {
            Assert.That(look.Listening, Is.EqualTo(AppListening.No));
            Assert.That(look.Holds, Is.False);
            Assert.That(look.ListeningApps, Is.Empty);
        });

        _sessions.SetSessions(Laptop.ArrayId, Laptop.Session(Laptop.ArrayId, Laptop.OwnProcess), Laptop.Session(Laptop.ArrayId, Laptop.Vibe));

        look = _watch.LookNow();
        Assert.Multiple(() =>
        {
            Assert.That(look.Listening, Is.EqualTo(AppListening.Yes));
            Assert.That(look.ListeningApps, Is.EqualTo(new[] { "vibe" }));
        });
    }

    /// <summary>Following chrome, another app taking sound is listed but does not open the gate.</summary>
    [Test]
    public void Following_chrome_another_app_listening_is_listed_but_does_not_count()
    {
        _sessions.SetSessions(Laptop.ArrayId, Laptop.Session(Laptop.ArrayId, Laptop.Vibe), Laptop.Session(Laptop.ArrayId, Laptop.OwnProcess));

        var look = _watch.LookNow();

        Assert.Multiple(() =>
        {
            Assert.That(look.Listening, Is.EqualTo(AppListening.No));
            Assert.That(look.Holds, Is.False);
            Assert.That(look.ListeningApps, Is.EqualTo(new[] { "vibe" }));
        });
    }

    /// <summary>A session that is inactive holds the microphone without listening; an expired one neither holds nor listens.</summary>
    [TestCase(CaptureSessionState.Inactive, true)]
    [TestCase(CaptureSessionState.Expired, false)]
    public void A_session_that_is_not_active_is_not_listening(CaptureSessionState state, bool holds)
    {
        _sessions.SetSessions(Laptop.ArrayId, Laptop.Session(Laptop.ArrayId, Laptop.ChromeUtility, state));

        var look = _watch.LookNow();

        Assert.Multiple(() =>
        {
            Assert.That(look.Listening, Is.EqualTo(AppListening.No));
            Assert.That(look.Holds, Is.EqualTo(holds));
        });
    }

    /// <summary>When the sessions could not all be read and the app was not found among those read, nothing is known; found active among them, it is listening all the same.</summary>
    [Test]
    public void An_incomplete_listing_is_unknown_unless_the_app_was_found_listening()
    {
        _sessions.Set(Laptop.Listing(Laptop.ArrayId, Laptop.Session(Laptop.ArrayId, Laptop.Vibe)) with { Complete = false, Problem = "session 2 of 2 not readable: (0x88890004)" });

        var look = _watch.LookNow();
        Assert.Multiple(() =>
        {
            Assert.That(look.Listening, Is.EqualTo(AppListening.Unknown));
            Assert.That(look.Problem, Is.EqualTo("session 2 of 2 not readable: (0x88890004)"));
        });

        _sessions.Set(Laptop.Listing(Laptop.ArrayId, Laptop.Session(Laptop.ArrayId, Laptop.ChromeUtility)) with { Complete = false, Problem = "session 2 of 2 not readable: (0x88890004)" });

        Assert.That(_watch.LookNow().Listening, Is.EqualTo(AppListening.Yes));
    }

    /// <summary>
    /// A session that could not be read in full, but whose process was read, concerns only that process: following chrome,
    /// the Zoom app's unreadable session, ClassScribe's own and one of no single process leave chrome's listening known, so
    /// that chrome holding the microphone without listening is no, and no reason to drop everything as unreadable. The
    /// problem is still handed on with the look.
    /// </summary>
    [Test]
    public void A_session_of_another_app_that_cannot_be_read_leaves_the_followed_app_known()
    {
        _sessions.Set(WithUnread([Laptop.Session(Laptop.ArrayId, Laptop.ChromeUtility, CaptureSessionState.Inactive)],
            new UnreadCaptureSession(Laptop.Zoom, "Zoom", null), new UnreadCaptureSession(Laptop.OwnProcess, "ClassScribe", null), new UnreadCaptureSession(0, null, null)));

        var muted = _watch.LookNow();

        _sessions.Set(WithUnread([Laptop.Session(Laptop.ArrayId, Laptop.ChromeUtility)], new UnreadCaptureSession(Laptop.Zoom, "Zoom", null)));
        var listening = _watch.LookNow();

        Assert.Multiple(() =>
        {
            Assert.That(muted.Listening, Is.EqualTo(AppListening.No));
            Assert.That(muted.Holds, Is.True);
            Assert.That(muted.Problem, Is.EqualTo(ZoomUnread));
            Assert.That(listening.Listening, Is.EqualTo(AppListening.Yes));
            Assert.That(_log.Problems, Is.Empty);
        });
    }

    /// <summary>A session that could not be read at all, its process included, may be the followed app's: nothing is known unless the app was found listening.</summary>
    [Test]
    public void A_session_whose_process_cannot_be_read_leaves_nothing_known()
    {
        _sessions.Set(WithUnread([Laptop.Session(Laptop.ArrayId, Laptop.ChromeUtility, CaptureSessionState.Inactive)], new UnreadCaptureSession(null, null, null)));

        Assert.That(_watch.LookNow().Listening, Is.EqualTo(AppListening.Unknown));
    }

    /// <summary>
    /// Following any app, an app's session whose state could not be read leaves nothing known while Windows' usage record
    /// says nothing of that app: it may be listening.
    /// </summary>
    [Test]
    public void Following_any_app_another_apps_unread_session_leaves_nothing_known()
    {
        Make(new FollowRule.Any());
        _sessions.Set(WithUnread([], new UnreadCaptureSession(Laptop.Zoom, "Zoom", null)));

        var look = _watch.LookNow();

        Assert.Multiple(() =>
        {
            Assert.That(look.Listening, Is.EqualTo(AppListening.Unknown));
            Assert.That(look.UsageInUse, Is.Null, "the look's word from the record is the followed app's, and no one app is followed");
        });
    }

    /// <summary>
    /// Following any app (auto, the shipped default), the session of an app whose state Windows would not give, as the Zoom
    /// app's in a meeting on 18 October, is judged by Windows' usage record as a followed app's is: by the entry of its own
    /// program, matched by the whole name, so that another program whose name holds the app's says nothing of it. In use,
    /// the app is listening; closed, it is not. The look's own word from the record stays null, since no one app is
    /// followed. That the record decides is said once, at Information, naming the app.
    /// </summary>
    [Test]
    public void Following_any_app_an_unread_session_is_judged_by_its_own_programs_entry_in_the_usage_record()
    {
        Make(new FollowRule.Any());
        _sessions.Set(WithUnread([], new UnreadCaptureSession(Laptop.Zoom, "Zoom", null)));
        _record.Entries.Add(FakeMicrophoneUsageRecord.Desktop("ZoomOutlookPlugin.exe", inUse: true));

        var anotherProgram = _watch.LookNow();
        _record.Entries.Add(FakeMicrophoneUsageRecord.Desktop("Zoom.exe", inUse: true));
        var inUse = _watch.LookNow();
        _record.Entries[1] = FakeMicrophoneUsageRecord.Desktop("Zoom.exe", inUse: false);
        var closed = _watch.LookNow();

        Assert.Multiple(() =>
        {
            Assert.That(anotherProgram.Listening, Is.EqualTo(AppListening.Unknown), "a program whose name only holds the app's says nothing of it");
            Assert.That(inUse.Listening, Is.EqualTo(AppListening.Yes));
            Assert.That(inUse.Holds, Is.True);
            Assert.That(inUse.ListeningApps, Is.EqualTo(new[] { "Zoom" }));
            Assert.That(inUse.UsageInUse, Is.Null, "no one app is followed");
            Assert.That(closed.Listening, Is.EqualTo(AppListening.No), "the other program in use says nothing of the Zoom app");
            Assert.That(closed.Holds, Is.True);
            Assert.That(_log.Entries.Where(entry => entry.Level >= LogLevel.Information).Select(entry => entry.Message), Is.EqualTo(new[]
            {
                $"Windows would not give the state of Zoom's session on \"{Laptop.ArrayName}\" ({ZoomUnread}); until it does, Windows' microphone usage record says whether Zoom is listening"
            }), "said once, when the record first decides");
        });
    }

    /// <summary>
    /// Following any app, with several sessions whose state could not be read, one app using the microphone by the record
    /// is enough for listening, and only that app is listed; not listening needs every one of them to have closed it by the
    /// record. One the record says nothing of, or whose program could not be named, leaves nothing known otherwise.
    /// </summary>
    [Test]
    public void Following_any_app_every_unread_sessions_program_must_have_closed_the_microphone_for_none_to_be_listening()
    {
        Make(new FollowRule.Any());
        _sessions.Set(WithUnread([], new UnreadCaptureSession(Laptop.Zoom, "Zoom", null), new UnreadCaptureSession(Laptop.Vibe, "vibe", null)));
        _record.Entries.Add(FakeMicrophoneUsageRecord.Desktop("Zoom.exe", inUse: false));

        var oneSaysNothing = _watch.LookNow();
        _record.Entries.Add(FakeMicrophoneUsageRecord.Desktop("vibe.exe", inUse: false));
        var bothClosed = _watch.LookNow();
        _record.Entries[1] = FakeMicrophoneUsageRecord.Desktop("vibe.exe", inUse: true);
        var oneInUse = _watch.LookNow();
        _record.Entries[1] = FakeMicrophoneUsageRecord.Desktop("vibe.exe", inUse: false);
        _sessions.Set(WithUnread([], new UnreadCaptureSession(Laptop.Zoom, "Zoom", null), new UnreadCaptureSession(Laptop.Vibe, null, null)));
        var oneUnnamed = _watch.LookNow();

        Assert.Multiple(() =>
        {
            Assert.That(oneSaysNothing.Listening, Is.EqualTo(AppListening.Unknown));
            Assert.That(bothClosed.Listening, Is.EqualTo(AppListening.No));
            Assert.That(oneInUse.Listening, Is.EqualTo(AppListening.Yes));
            Assert.That(oneInUse.ListeningApps, Is.EqualTo(new[] { "vibe" }));
            Assert.That(oneUnnamed.Listening, Is.EqualTo(AppListening.Unknown), "a session whose program could not be named cannot be looked up");
        });
    }

    /// <summary>
    /// Following any app, the record does not stand in for the unread session of an app that the last look over every
    /// microphone found taking sound from another microphone: its word is explained by that one, and nothing is known. No
    /// other microphone is reported, as there is no one app to move the recording after.
    /// </summary>
    [Test]
    public void Following_any_app_the_usage_record_does_not_decide_for_an_app_listening_on_another_microphone()
    {
        Make(new FollowRule.Any());
        _record.Entries.Add(FakeMicrophoneUsageRecord.Desktop("Zoom.exe", inUse: true));
        _sessions.Set(WithUnread([], new UnreadCaptureSession(Laptop.Zoom, "Zoom", null)));
        _sessions.SetSessions(Laptop.ObsbotId, Laptop.Session(Laptop.ObsbotId, Laptop.Zoom));

        var elsewhere = _watch.LookNow();
        _sessions.SetSessions(Laptop.ObsbotId);
        _time.Advance(ClassMicrophoneWatch.AllMicrophonesInterval);
        var nowhereElse = _watch.LookNow();

        Assert.Multiple(() =>
        {
            Assert.That(elsewhere.Listening, Is.EqualTo(AppListening.Unknown));
            Assert.That(elsewhere.OtherMicrophone, Is.Null);
            Assert.That(nowhereElse.Listening, Is.EqualTo(AppListening.Yes), "once the next look over every microphone finds it nowhere else, the record decides");
        });
    }

    /// <summary>The followed app's session whose instance identifier could not be read, but whose state was, is judged by that state.</summary>
    [TestCase(CaptureSessionState.Active, AppListening.Yes, true)]
    [TestCase(CaptureSessionState.Inactive, AppListening.No, true)]
    [TestCase(CaptureSessionState.Expired, AppListening.No, false)]
    public void The_followed_apps_unread_session_whose_state_was_read_is_judged_by_it(CaptureSessionState state, AppListening listening, bool holds)
    {
        Make(new FollowRule.Named("zoom"));
        _sessions.Set(WithUnread([], new UnreadCaptureSession(Laptop.Zoom, "Zoom", state)));

        var look = _watch.LookNow();

        Assert.Multiple(() =>
        {
            Assert.That(look.Listening, Is.EqualTo(listening));
            Assert.That(look.Holds, Is.EqualTo(holds));
            Assert.That(look.ListeningApps, Is.EqualTo(listening == AppListening.Yes ? new[] { "Zoom" } : []));
        });
    }

    /// <summary>
    /// When the followed app's own session on the recorded microphone cannot be read beyond its process, as with the Zoom
    /// app in a meeting on 18 October, Windows' usage record stands in for its state: the app using the microphone now is
    /// listening, the app having closed it is not, and a record that says nothing of the app leaves nothing known, which
    /// the gate reports as unreadable. That the record decides is said once, at Information, until the session can be read again.
    /// </summary>
    [Test]
    public void The_followed_apps_own_unread_session_is_judged_by_the_usage_record()
    {
        Make(new FollowRule.Named("zoom"));
        _sessions.Set(WithUnread([], new UnreadCaptureSession(Laptop.Zoom, "Zoom", null)));

        var nothingSaid = _watch.LookNow();
        _record.Entries.Add(FakeMicrophoneUsageRecord.Desktop("Zoom.exe", inUse: true));
        var inUse = _watch.LookNow();
        var stillInUse = _watch.LookNow();
        _record.Entries[0] = FakeMicrophoneUsageRecord.Desktop("Zoom.exe", inUse: false);
        var closed = _watch.LookNow();

        Assert.Multiple(() =>
        {
            Assert.That(nothingSaid.Listening, Is.EqualTo(AppListening.Unknown));
            Assert.That(nothingSaid.Problem, Is.EqualTo(ZoomUnread), "the gate's words for the unreadable state name the session");
            Assert.That(inUse.Listening, Is.EqualTo(AppListening.Yes));
            Assert.That(inUse.Holds, Is.True);
            Assert.That(inUse.UsageInUse, Is.True);
            Assert.That(inUse.ListeningApps, Is.EqualTo(new[] { "Zoom" }));
            Assert.That(stillInUse.Listening, Is.EqualTo(AppListening.Yes));
            Assert.That(closed.Listening, Is.EqualTo(AppListening.No));
            Assert.That(closed.Holds, Is.True);
            Assert.That(_log.Entries.Where(entry => entry.Level >= LogLevel.Information).Select(entry => entry.Message), Is.EqualTo(new[]
            {
                $"Windows would not give the state of zoom's session on \"{Laptop.ArrayName}\" ({ZoomUnread}); until it does, Windows' microphone usage record says whether zoom is listening"
            }), "said once, when the record first decides");
        });

        _sessions.Set(Laptop.Listing(Laptop.ArrayId, Laptop.Session(Laptop.ArrayId, Laptop.Zoom)));
        _watch.LookNow();
        _sessions.Set(WithUnread([], new UnreadCaptureSession(Laptop.Zoom, "Zoom", null)));
        _watch.LookNow();

        Assert.That(_log.Entries.Count(entry => entry.Level == LogLevel.Information), Is.EqualTo(2), "said again once the session had been read");
    }

    /// <summary>
    /// The usage record is per app, not per microphone: while the followed app was found taking sound from another
    /// microphone, the record's word that it uses a microphone is explained by that one, so it does not stand in for the
    /// unread session on the recorded microphone, and nothing is known of it.
    /// </summary>
    [Test]
    public void The_usage_record_does_not_decide_while_the_app_listens_on_another_microphone()
    {
        Make(new FollowRule.Named("zoom"));
        _record.Entries.Add(FakeMicrophoneUsageRecord.Desktop("Zoom.exe", inUse: true));
        _sessions.Set(WithUnread([], new UnreadCaptureSession(Laptop.Zoom, "Zoom", null)));
        _sessions.SetSessions(Laptop.ObsbotId, Laptop.Session(Laptop.ObsbotId, Laptop.Zoom));

        var look = _watch.LookNow();

        Assert.Multiple(() =>
        {
            Assert.That(look.Listening, Is.EqualTo(AppListening.Unknown));
            Assert.That(look.OtherMicrophoneId, Is.EqualTo(Laptop.ObsbotId), "the chooser moves the recording to the app's microphone");
        });
    }

    /// <summary>What the sessions reader says of the Zoom app's session on the array whose state Windows would not give.</summary>
    private const string ZoomUnread = "session 2 of 2 (Zoom, pid 9090) not readable: GetState: The parameter is incorrect. (0x80070057)";

    /// <summary>The array's listing with <paramref name="sessions"/> read and <paramref name="unread"/> not read in full, with the Zoom app's session's words as its problem.</summary>
    private static MicrophoneSessionListing WithUnread(CaptureSession[] sessions, params UnreadCaptureSession[] unread) =>
        Laptop.Listing(Laptop.ArrayId, sessions) with { Complete = false, Problem = ZoomUnread, Unread = unread };

    /// <summary>A microphone that is not connected gives an unknown look, with the reason, and no mute.</summary>
    [Test]
    public void A_microphone_that_is_not_connected_gives_an_unknown_look()
    {
        _sessions.Remove(Laptop.ArrayId);

        var look = _watch.LookNow();

        Assert.Multiple(() =>
        {
            Assert.That(look.Listening, Is.EqualTo(AppListening.Unknown));
            Assert.That(look.Problem, Is.EqualTo("the microphone is not connected"));
            Assert.That(look.MutedInWindows, Is.Null);
        });
    }

    /// <summary>The microphone's mute in Windows is handed on with the look.</summary>
    [Test]
    public void The_mute_in_Windows_is_handed_on()
    {
        _sessions.Set(Laptop.Listing(Laptop.ArrayId, Laptop.Session(Laptop.ArrayId, Laptop.ChromeUtility)) with { MutedInWindows = true });

        Assert.That(_watch.LookNow().MutedInWindows, Is.True);
    }

    /// <summary>Each look is stamped on the clock the packets are stamped with, in 100-nanosecond units.</summary>
    [Test]
    public void A_look_is_stamped_on_the_packets_clock()
    {
        _time.Advance(TimeSpan.FromSeconds(1.5));

        Assert.That(_watch.LookNow().Timestamp100ns, Is.EqualTo(15_000_000));
    }

    /// <summary>Windows' usage record says whether the followed app has the microphone open now, has closed it, or is absent from it.</summary>
    [Test]
    public void The_usage_record_says_what_it_knows_of_the_app()
    {
        Assert.That(_watch.LookNow().UsageInUse, Is.Null, "absent from the record");

        _record.Entries.Add(FakeMicrophoneUsageRecord.Desktop("chrome.exe", inUse: true));
        Assert.That(_watch.LookNow().UsageInUse, Is.True);

        _record.Entries[0] = FakeMicrophoneUsageRecord.Desktop("chrome.exe", inUse: false);
        Assert.That(_watch.LookNow().UsageInUse, Is.False);
    }

    /// <summary>
    /// An entry in the record without any recorded use, as Windows keeps for most apps that once asked for the microphone,
    /// says nothing: only entries with a recorded use decide, and one of them open now outweighs the entry without times.
    /// </summary>
    [Test]
    public void A_usage_entry_without_times_is_neutral()
    {
        _record.Entries.Add(FakeMicrophoneUsageRecord.Unused("chrome.exe"));
        Assert.That(_watch.LookNow().UsageInUse, Is.Null, "an entry without times is no word on the app");

        _record.Entries.Add(FakeMicrophoneUsageRecord.Desktop("chrome.exe", inUse: true));
        Assert.That(_watch.LookNow().UsageInUse, Is.True);

        _record.Entries[1] = FakeMicrophoneUsageRecord.Desktop("chrome.exe", inUse: false);
        Assert.That(_watch.LookNow().UsageInUse, Is.False, "the entry with a recorded use decides");
    }

    /// <summary>
    /// With the app listening and its entry in the record without times, the gate stays open for the whole class: the
    /// record has not said that the app closed the microphone, so its two-second rule never applies.
    /// </summary>
    [Test]
    public void A_usage_entry_without_times_does_not_close_the_gate()
    {
        var gate = new MicrophoneGate(ClassAppMute.Visible, new FollowRule.Named("chrome"), _time, new RecordingLogger<MicrophoneGate>());
        _watch.Looked += (_, look) => gate.Record(look);
        _record.Entries.Add(FakeMicrophoneUsageRecord.Unused("chrome.exe"));
        _sessions.SetSessions(Laptop.ArrayId, Laptop.Session(Laptop.ArrayId, Laptop.ChromeUtility));

        for (var look = 0; look < 20; look++)
        {
            _watch.LookNow();
            _time.Advance(ClassMicrophoneWatch.Interval);
        }

        _watch.LookNow();

        Assert.Multiple(() =>
        {
            Assert.That(gate.State, Is.EqualTo(MicrophoneGateState.Recording));
            Assert.That(gate.Judge(TimeSpan.FromSeconds(4.9).Ticks), Is.EqualTo(GateVerdict.Write));
        });
    }

    /// <summary>
    /// When a look read the sessions but not everything else about the microphone, such as its mute in Windows, the look
    /// is known and the sessions alone decide, and the reason is logged once, at Information, until it clears.
    /// </summary>
    [Test]
    public void A_mute_that_cannot_be_read_is_logged_once_while_the_sessions_decide()
    {
        var problem = "endpoint mute not readable (0x80070005)";
        _sessions.Set(Laptop.Listing(Laptop.ArrayId, Laptop.Session(Laptop.ArrayId, Laptop.ChromeUtility)) with { MutedInWindows = null, Problem = problem });

        var first = _watch.LookNow();
        var second = _watch.LookNow();

        Assert.Multiple(() =>
        {
            Assert.That(first.Listening, Is.EqualTo(AppListening.Yes));
            Assert.That(first.MutedInWindows, Is.Null);
            Assert.That(first.Problem, Is.EqualTo(problem));
            Assert.That(second.Listening, Is.EqualTo(AppListening.Yes));
            Assert.That(_log.Entries.Where(entry => entry.Message.Contains(problem)), Has.Exactly(1).Items, "logged once, not at every look");
            Assert.That(_log.Entries.Single(entry => entry.Message.Contains(problem)).Level, Is.EqualTo(LogLevel.Information));
            Assert.That(_log.Entries.Single(entry => entry.Message.Contains(problem)).Message, Does.Contain(Laptop.ArrayName));
        });

        _sessions.Set(Laptop.Listing(Laptop.ArrayId, Laptop.Session(Laptop.ArrayId, Laptop.ChromeUtility)));
        _watch.LookNow();
        _sessions.Set(Laptop.Listing(Laptop.ArrayId, Laptop.Session(Laptop.ArrayId, Laptop.ChromeUtility)) with { MutedInWindows = null, Problem = problem });
        _watch.LookNow();

        Assert.That(_log.Entries.Where(entry => entry.Message.Contains(problem)), Has.Exactly(2).Items, "logged again once it had cleared");
    }

    /// <summary>A Store app is matched in the record by its package family name, such as the packaged Teams.</summary>
    [Test]
    public void A_Store_app_is_matched_in_the_record_by_its_package_family_name()
    {
        Make(new FollowRule.Named("teams"));
        _record.Entries.Add(FakeMicrophoneUsageRecord.Packaged("MSTeams_8wekyb3d8bbwe", inUse: true));

        Assert.That(_watch.LookNow().UsageInUse, Is.True);
    }

    /// <summary>A record that cannot be read decides nothing: it is logged at Debug once, and the look goes on without it.</summary>
    [Test]
    public void A_record_that_cannot_be_read_is_logged_once_and_ignored()
    {
        _record.Problem = "SecurityException: Requested registry access is not allowed.";
        _sessions.SetSessions(Laptop.ArrayId, Laptop.Session(Laptop.ArrayId, Laptop.ChromeUtility));

        var first = _watch.LookNow();
        var second = _watch.LookNow();

        Assert.Multiple(() =>
        {
            Assert.That(first.Listening, Is.EqualTo(AppListening.Yes));
            Assert.That(first.UsageInUse, Is.Null);
            Assert.That(second.UsageInUse, Is.Null);
            Assert.That(_log.Entries.Where(entry => entry.Message.Contains("usage record")), Has.Exactly(1).Items);
            Assert.That(_log.Entries.Single(entry => entry.Message.Contains("usage record")).Level, Is.EqualTo(LogLevel.Debug));
        });
    }

    /// <summary>
    /// The first look, and one every ten seconds, covers every microphone; the looks between read the recorded one alone.
    /// The look over every microphone finds the followed app taking sound from another one, which the looks until the next
    /// such look report, as long as the app has no active session on the recorded one.
    /// </summary>
    [Test]
    public void Every_ten_seconds_a_look_covers_every_microphone_to_find_the_app_on_another_one()
    {
        _sessions.SetSessions(Laptop.ObsbotId, Laptop.Session(Laptop.ObsbotId, Laptop.ChromeUtility));

        var first = _watch.LookNow();
        _time.Advance(TimeSpan.FromSeconds(5));
        var second = _watch.LookNow();
        _time.Advance(TimeSpan.FromSeconds(5));
        var third = _watch.LookNow();

        Assert.Multiple(() =>
        {
            Assert.That(_sessions.ReadAlls, Is.EqualTo(2), "the first look and the one ten seconds later");
            Assert.That(_sessions.Reads.GetValueOrDefault(Laptop.ArrayId), Is.EqualTo(1), "the look between");
            Assert.That(first.OtherMicrophone, Is.EqualTo(Laptop.ObsbotName));
            Assert.That(first.OtherMicrophoneId, Is.EqualTo(Laptop.ObsbotId), "the chooser can move the recording to it");
            Assert.That(second.OtherMicrophone, Is.EqualTo(Laptop.ObsbotName), "kept until the next look over every microphone");
            Assert.That(third.OtherMicrophone, Is.EqualTo(Laptop.ObsbotName));
            Assert.That(first.Listening, Is.EqualTo(AppListening.No));
        });

        _sessions.SetSessions(Laptop.ArrayId, Laptop.Session(Laptop.ArrayId, Laptop.ChromeUtility));
        var fourth = _watch.LookNow();

        Assert.Multiple(() =>
        {
            Assert.That(fourth.Listening, Is.EqualTo(AppListening.Yes));
            Assert.That(fourth.OtherMicrophone, Is.Null, "the app has an active session on the recorded microphone");
            Assert.That(fourth.OtherMicrophoneId, Is.Null);
        });
    }

    /// <summary>
    /// The watch's looks feed the gate: a packet stamped after the last look is held, and a look asked for by hand, as at
    /// Stop and when V arms the microphone, decides it at once.
    /// </summary>
    [Test]
    public void A_look_asked_for_by_hand_decides_a_held_packet()
    {
        var gate = new MicrophoneGate(ClassAppMute.Visible, new FollowRule.Named("chrome"), _time, new RecordingLogger<MicrophoneGate>());
        _watch.Looked += (_, look) => gate.Record(look);
        _sessions.SetSessions(Laptop.ArrayId, Laptop.Session(Laptop.ArrayId, Laptop.ChromeUtility));
        _watch.LookNow();
        _time.Advance(ClassMicrophoneWatch.Interval);
        var stamp = _time.GetTimestamp() - TimeSpan.FromMilliseconds(100).Ticks;

        Assert.That(gate.Judge(stamp), Is.EqualTo(GateVerdict.Undecided));

        _watch.LookNow();

        Assert.That(gate.Judge(stamp), Is.EqualTo(GateVerdict.Write));
    }

    /// <summary>Following any app, no other microphone is reported: there is no one app to find elsewhere.</summary>
    [Test]
    public void Following_any_app_no_other_microphone_is_reported()
    {
        Make(new FollowRule.Any());
        _sessions.SetSessions(Laptop.ObsbotId, Laptop.Session(Laptop.ObsbotId, Laptop.Vibe));

        Assert.That(_watch.LookNow().OtherMicrophone, Is.Null);
    }

    /// <summary>Following another microphone, the recording having moved to it, the next look reads that one, over every microphone again.</summary>
    [Test]
    public void Following_another_microphone_repoints_the_looks()
    {
        _sessions.SetSessions(Laptop.ObsbotId, Laptop.Session(Laptop.ObsbotId, Laptop.ChromeUtility));
        _watch.LookNow();
        _time.Advance(TimeSpan.FromSeconds(1));

        _watch.Follow(Laptop.ObsbotId);
        var look = _watch.LookNow();

        Assert.Multiple(() =>
        {
            Assert.That(_watch.EndpointId, Is.EqualTo(Laptop.ObsbotId));
            Assert.That(_sessions.ReadAlls, Is.EqualTo(2), "a fresh look over every microphone");
            Assert.That(look.Listening, Is.EqualTo(AppListening.Yes));
            Assert.That(look.RecordedMicrophone, Is.EqualTo(Laptop.ObsbotName));
            Assert.That(look.OtherMicrophone, Is.Null);
        });
    }

    /// <summary>
    /// While the app is not taking sound, the look says whether the process that last did still runs, so that its session
    /// going when the app closed is not taken for a mute; while it is taking sound, and before it ever did, nothing is said.
    /// </summary>
    [Test]
    public void While_the_app_is_not_listening_the_look_says_whether_its_process_still_runs()
    {
        Assert.That(_watch.LookNow().ProcessAlive, Is.Null, "before the app ever listened");

        _sessions.SetSessions(Laptop.ArrayId, Laptop.Session(Laptop.ArrayId, Laptop.ChromeUtility));
        Assert.That(_watch.LookNow().ProcessAlive, Is.Null, "while it listens");

        _sessions.SetSessions(Laptop.ArrayId);
        Assert.That(_watch.LookNow().ProcessAlive, Is.True, "its session gone, the process runs");

        _names.Names.Remove(Laptop.ChromeUtility);
        Assert.That(_watch.LookNow().ProcessAlive, Is.False, "the process has ended");
    }

    /// <summary>
    /// Started, the watch looks at once on a thread of its own, named and in the multithreaded apartment, then every
    /// quarter second; stopped, it looks no more.
    /// </summary>
    [Test]
    public async Task The_watch_looks_at_once_and_then_every_quarter_second_on_its_own_thread()
    {
        string? threadName = null;
        var apartment = ApartmentState.Unknown;
        var background = false;
        _watch.Looked += (_, _) =>
        {
            threadName ??= Thread.CurrentThread.Name;
            apartment = Thread.CurrentThread.GetApartmentState();
            background = Thread.CurrentThread.IsBackground;
        };

        _watch.Start(Laptop.ArrayId);
        // Waits on this test's own handler, not on the fixture's count: the fixture's handler runs first, so under load the
        // count can be one while the thread's name has not been taken yet.
        await Wait.UntilAsync(() => Volatile.Read(ref threadName) is not null);

        Assert.Multiple(() =>
        {
            Assert.That(threadName, Is.EqualTo(ClassMicrophoneWatch.ThreadName));
            Assert.That(apartment, Is.EqualTo(ApartmentState.MTA));
            Assert.That(background, Is.True, "a watch thread must never keep the process alive");
            Assert.That(_watch.EndpointId, Is.EqualTo(Laptop.ArrayId));
        });

        _time.Advance(TimeSpan.FromMilliseconds(100));
        await Task.Delay(50);
        Assert.That(_looked, Has.Count.EqualTo(1), "a tenth of a second is too soon");

        _time.Advance(TimeSpan.FromMilliseconds(150));
        await Wait.UntilAsync(() => _looked.Count == 2);
        _time.Advance(ClassMicrophoneWatch.Interval);
        await Wait.UntilAsync(() => _looked.Count == 3);

        _watch.Stop();
        _time.Advance(ClassMicrophoneWatch.Interval);
        await Task.Delay(50);

        Assert.Multiple(() =>
        {
            Assert.That(_looked, Has.Count.EqualTo(3), "no look after Stop");
            Assert.That(_time.ActiveTimers, Is.Zero, "the timer is disposed with the thread");
            Assert.That(_watch.Failure, Is.Null);
        });
    }

    /// <summary>Stopping a watch that was never started, and stopping it twice, is harmless.</summary>
    [Test]
    public void Stopping_before_starting_and_twice_is_harmless()
    {
        Assert.DoesNotThrow(() => _watch.Stop());

        _watch.Start(Laptop.ArrayId);
        _watch.Stop();

        Assert.DoesNotThrow(() => _watch.Stop());
    }

    /// <summary>
    /// A failure the readers never let through, such as a bug, ends the watch rather than the class: it is logged as an
    /// error, handed on through Failed and Failure, and a last look saying that nothing can be read goes to whoever listens,
    /// so that the gate drops everything from then on. A look asked for afterwards reads nothing and says the same.
    /// </summary>
    [Test]
    public async Task A_failure_on_the_thread_ends_the_watch_and_is_reported()
    {
        var failure = new InvalidOperationException("The reader has a bug.");
        _sessions.Failure = failure;
        Exception? failed = null;
        _watch.Failed += (_, exception) => failed = exception;

        _watch.Start(Laptop.ArrayId);
        await Wait.UntilAsync(() => failed is not null);
        await Wait.UntilAsync(() => _looked.Count == 1);

        var reads = _sessions.Reads.Count;
        var later = _watch.LookNow();

        Assert.Multiple(() =>
        {
            Assert.That(failed, Is.SameAs(failure));
            Assert.That(_watch.Failure, Is.SameAs(failure));
            Assert.That(_looked[0].Listening, Is.EqualTo(AppListening.Unknown));
            Assert.That(_looked[0].Problem, Is.EqualTo("the microphone watch stopped: The reader has a bug."));
            Assert.That(later.Listening, Is.EqualTo(AppListening.Unknown));
            Assert.That(later.Problem, Is.EqualTo("the microphone watch stopped: The reader has a bug."));
            Assert.That(_sessions.Reads.Count, Is.EqualTo(reads), "nothing is read after the failure");
            Assert.That(_log.Problems, Has.Exactly(1).Items);
            Assert.That(_log.Problems[0], Does.StartWith("The microphone watch stopped: The reader has a bug.").And.Contain("your voice is not recorded for the rest of this run"));
            Assert.That(_time.ActiveTimers, Is.Zero);
        });
    }

    /// <summary>A failure in a look asked for by hand, before the thread runs, reaches the caller: nothing on another thread is there to be protected.</summary>
    [Test]
    public void A_failure_in_a_look_asked_for_by_hand_reaches_the_caller()
    {
        _sessions.Failure = new InvalidOperationException("The reader has a bug.");

        Assert.That(() => _watch.LookNow(), Throws.InvalidOperationException);
    }

    /// <summary>Looking before any microphone was named is a mistake in the code, and says so.</summary>
    [Test]
    public void Looking_before_a_microphone_is_named_is_refused()
    {
        var watch = new ClassMicrophoneWatch(_sessions, _record, _names, new FollowRule.Named("chrome"), new FakeOwnProcesses(), _time, _log);

        Assert.That(() => watch.LookNow(), Throws.InvalidOperationException);
    }
}
