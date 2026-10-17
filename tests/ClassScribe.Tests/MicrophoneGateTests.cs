using ClassScribe.Audio.Microphones;

namespace ClassScribe.Tests;

/// <summary>
/// The gate that makes the microphone track follow the class app, fed scripted looks on a clock moved by hand: every
/// packet is judged by the look before its stamp and the look after it, so that nothing from after a mute and nothing
/// from before an unmute is written; what it says about its state; and the numbers it keeps for the summary.
/// </summary>
[TestFixture]
public class MicrophoneGateTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 17, 23, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Look = ClassMicrophoneWatch.Interval;

    private ManualTimeProvider _time = null!;
    private RecordingLogger<MicrophoneGate> _log = null!;
    private MicrophoneGate _gate = null!;
    private ScriptedLooks _looks = null!;
    private int _changed;
    private List<string> _evidence = null!;

    /// <summary>A clock moved by hand, a log, and a gate for a class app whose mute Windows can see, following chrome.</summary>
    [SetUp]
    public void SetUp()
    {
        _time = new ManualTimeProvider(Start);
        _log = new RecordingLogger<MicrophoneGate>();
        _evidence = [];
        _changed = 0;
        Make(ClassAppMute.Visible, new FollowRule.Named("chrome"));
    }

    /// <summary>Makes the gate under test for <paramref name="appMute"/> and <paramref name="rule"/>, with its looks and events wired.</summary>
    private void Make(ClassAppMute appMute, FollowRule rule)
    {
        _gate = new MicrophoneGate(appMute, rule, _time, _log);
        _gate.Changed += (_, _) => _changed++;
        _gate.Evidence += (_, text) => _evidence.Add(text);
        _looks = new ScriptedLooks(_gate, _time);
    }

    /// <summary>Seconds as a time span.</summary>
    private static TimeSpan At(double seconds) => TimeSpan.FromSeconds(seconds);

    /// <summary>The verdict for a packet stamped at <paramref name="seconds"/>.</summary>
    private GateVerdict Judge(double seconds) => _gate.Judge(At(seconds).Ticks);

    /// <summary>
    /// An unmute between two looks: packets stamped before the look that found the app not listening are dropped, packets
    /// between that look and the one that found it listening are dropped too (the look before them found the gate closed),
    /// and packets from the look that found it listening on are written.
    /// </summary>
    [Test]
    public void At_an_unmute_nothing_before_the_look_that_saw_it_is_written()
    {
        _looks.Feed(Looks.NotOpened(At(0)));
        _looks.Feed(Looks.NotOpened(At(0.25)));
        _looks.Feed(Looks.Open(At(0.5)));
        _looks.Feed(Looks.Open(At(0.75)));

        Assert.Multiple(() =>
        {
            Assert.That(Judge(0.1), Is.EqualTo(GateVerdict.Drop), "stamped before the look that found the app not listening");
            Assert.That(Judge(0.3), Is.EqualTo(GateVerdict.Drop), "stamped between the closed look and the open one");
            Assert.That(Judge(0.5), Is.EqualTo(GateVerdict.Write), "stamped at the look that found the app listening");
            Assert.That(Judge(0.6), Is.EqualTo(GateVerdict.Write), "stamped between two open looks");
        });
    }

    /// <summary>
    /// A mute between two looks: packets before the last look that found the app listening are written, packets between
    /// that look and the one that found it not listening are dropped (the look after them found the gate closed), and
    /// nothing from the closed look on is written.
    /// </summary>
    [Test]
    public void At_a_mute_nothing_after_the_last_open_look_is_written()
    {
        _looks.Feed(Looks.Open(At(0)));
        _looks.Feed(Looks.Open(At(0.25)));
        _looks.Feed(Looks.NotListening(At(0.5)));
        _looks.Feed(Looks.NotListening(At(0.75)));

        Assert.Multiple(() =>
        {
            Assert.That(Judge(0.1), Is.EqualTo(GateVerdict.Write), "stamped between two open looks");
            Assert.That(Judge(0.3), Is.EqualTo(GateVerdict.Drop), "stamped between the last open look and the closed one");
            Assert.That(Judge(0.5), Is.EqualTo(GateVerdict.Drop), "stamped at the look that found the app not listening");
            Assert.That(Judge(0.6), Is.EqualTo(GateVerdict.Drop), "stamped after the mute");
        });
    }

    /// <summary>A packet stamped after the last look waits for the next look, which decides it, and the gate says that a look arrived.</summary>
    [Test]
    public void A_packet_after_the_last_look_waits_for_the_next_look()
    {
        _looks.Feed(Looks.Open(At(0)));
        _looks.Feed(Looks.Open(At(0.25)));

        Assert.That(Judge(0.3), Is.EqualTo(GateVerdict.Undecided));

        var changes = _changed;
        _looks.Feed(Looks.Open(At(0.5)));

        Assert.Multiple(() =>
        {
            Assert.That(Judge(0.3), Is.EqualTo(GateVerdict.Write));
            Assert.That(_changed, Is.EqualTo(changes + 1), "Changed raised by the look");
        });
    }

    /// <summary>A packet still undecided a second after its stamp, because no look has come, is dropped: the looks are failing.</summary>
    [Test]
    public void A_packet_undecided_for_a_second_is_dropped()
    {
        _looks.Feed(Looks.Open(At(0)));
        _looks.Feed(Looks.Open(At(0.25)));

        _time.Advance(At(1.2) - TimeSpan.FromTicks(_time.GetTimestamp()));
        Assert.That(Judge(0.3), Is.EqualTo(GateVerdict.Undecided), "0.9 s after its stamp");

        _time.Advance(At(0.11));
        Assert.That(Judge(0.3), Is.EqualTo(GateVerdict.Drop), "1.01 s after its stamp");
    }

    /// <summary>A look that could not read the sessions is skipped when the known looks on either side are within a second and agree.</summary>
    [Test]
    public void An_unknown_look_between_two_agreeing_looks_is_skipped()
    {
        _looks.Feed(Looks.Open(At(0)));
        _looks.Feed(Looks.Unknown(At(0.25)));
        _looks.Feed(Looks.Open(At(0.5)));

        Assert.Multiple(() =>
        {
            Assert.That(Judge(0.1), Is.EqualTo(GateVerdict.Write));
            Assert.That(Judge(0.3), Is.EqualTo(GateVerdict.Write));
            Assert.That(_gate.State, Is.EqualTo(MicrophoneGateState.Recording));
        });
    }

    /// <summary>When the known looks on either side of an unknown one disagree, the packets around it are dropped.</summary>
    [Test]
    public void An_unknown_look_between_two_disagreeing_looks_drops_the_packets_around_it()
    {
        _looks.Feed(Looks.Open(At(0)));
        _looks.Feed(Looks.Unknown(At(0.25)));
        _looks.Feed(Looks.NotListening(At(0.5)));

        Assert.Multiple(() =>
        {
            Assert.That(Judge(0.1), Is.EqualTo(GateVerdict.Drop));
            Assert.That(Judge(0.3), Is.EqualTo(GateVerdict.Drop));
        });
    }

    /// <summary>When the known looks on either side of the unknown ones are more than a second apart, the packets between them are dropped, although the known looks agree.</summary>
    [Test]
    public void Unknown_looks_spanning_more_than_a_second_drop_the_packets_between_the_known_looks()
    {
        _looks.Feed(Looks.Open(At(0)));
        for (var at = 0.25; at < 1.25; at += 0.25) _looks.Feed(Looks.Unknown(At(at)));
        _looks.Feed(Looks.Open(At(1.25)));

        Assert.Multiple(() =>
        {
            Assert.That(Judge(0.3), Is.EqualTo(GateVerdict.Drop));
            Assert.That(Judge(1.1), Is.EqualTo(GateVerdict.Drop));
            Assert.That(Judge(1.25), Is.EqualTo(GateVerdict.Write), "at the known look again");
        });
    }

    /// <summary>
    /// After a second without a look that could read the sessions, the state says they cannot be read, with the reason,
    /// and every packet stamped after the last known look is dropped at once, until a look succeeds.
    /// </summary>
    [Test]
    public void After_a_second_without_a_known_look_the_sessions_are_unreadable_and_everything_is_dropped()
    {
        _looks.Feed(Looks.Open(At(0)));
        for (var at = 0.25; at <= 1.0; at += 0.25) _looks.Feed(Looks.Unknown(At(at)));
        Assert.That(_gate.State, Is.EqualTo(MicrophoneGateState.Recording), "a second without a known look has not passed yet");

        _looks.Feed(Looks.Unknown(At(1.25)));

        Assert.Multiple(() =>
        {
            Assert.That(_gate.State, Is.EqualTo(MicrophoneGateState.Unreadable));
            Assert.That(_gate.Why, Is.EqualTo("cannot read chrome's microphone session: sessions not readable: The audio service is not running (0x88890010)"));
            Assert.That(Judge(1.1), Is.EqualTo(GateVerdict.Drop), "dropped at once, not held");
            Assert.That(Judge(1.3), Is.EqualTo(GateVerdict.Drop));
        });

        _looks.Feed(Looks.Open(At(1.5)));
        _looks.Feed(Looks.Open(At(1.75)));

        Assert.Multiple(() =>
        {
            Assert.That(_gate.State, Is.EqualTo(MicrophoneGateState.Recording));
            Assert.That(Judge(1.6), Is.EqualTo(GateVerdict.Write));
        });
    }

    /// <summary>
    /// When the watch fails it sends one last look, which says nothing can be read, and no look after it. A second after
    /// the last look that could read the sessions, on the clock rather than at a look, the state says the sessions cannot
    /// be read and names the failure, every packet after the last known look is dropped, and the time stops counting as
    /// heard; a look that succeeds brings it back.
    /// </summary>
    [Test]
    public void After_the_watch_fails_the_gate_is_unreadable_a_second_after_the_last_known_look()
    {
        _looks.Feed(Looks.Open(At(0)));
        _looks.Feed(Looks.Open(At(0.25)));
        _looks.Feed(Looks.Unknown(At(0.5), "the microphone watch stopped: The reader has a bug."));

        _time.Advance(At(1.2) - TimeSpan.FromTicks(_time.GetTimestamp()));
        Assert.That(_gate.State, Is.EqualTo(MicrophoneGateState.Recording), "under a second after the last known look");

        _time.Advance(At(0.1));
        Assert.Multiple(() =>
        {
            Assert.That(_gate.State, Is.EqualTo(MicrophoneGateState.Unreadable));
            Assert.That(_gate.Why, Is.EqualTo("cannot read chrome's microphone session: the microphone watch stopped: The reader has a bug."));
            Assert.That(Judge(0.6), Is.EqualTo(GateVerdict.Drop));
            Assert.That(Judge(1.3), Is.EqualTo(GateVerdict.Drop));
        });

        _time.Advance(At(30) - TimeSpan.FromTicks(_time.GetTimestamp()));
        var summary = _gate.Summary();
        Assert.Multiple(() =>
        {
            Assert.That(_gate.State, Is.EqualTo(MicrophoneGateState.Unreadable));
            Assert.That(summary.Heard, Is.EqualTo(At(1.25)), "heard until a second after the last known look, and no longer");
            Assert.That(summary.Muted, Is.EqualTo(At(28.75)));
        });

        _looks.Feed(Looks.Open(At(30)));
        Assert.Multiple(() =>
        {
            Assert.That(_gate.State, Is.EqualTo(MicrophoneGateState.Recording));
            Assert.That(_gate.Summary().Stretches, Is.EqualTo(2));
        });
    }

    /// <summary>
    /// When the known looks on both sides of a packet are more than a second apart, with nothing between them because the
    /// watch stalled, the packet is dropped although both found the gate open: a mute and an unmute could hide between
    /// them, and a packet decided by a look that came late waited longer than the bound allows.
    /// </summary>
    [Test]
    public void A_packet_between_known_looks_more_than_a_second_apart_is_dropped()
    {
        _looks.Feed(Looks.Open(At(0)));
        _looks.Feed(Looks.Open(At(0.25)));
        _looks.Feed(Looks.Open(At(1.75)));

        Assert.Multiple(() =>
        {
            Assert.That(Judge(0.1), Is.EqualTo(GateVerdict.Write), "between looks a quarter second apart");
            Assert.That(Judge(0.5), Is.EqualTo(GateVerdict.Drop), "decided by a look 1.25 s after its stamp");
            Assert.That(Judge(1.6), Is.EqualTo(GateVerdict.Drop), "the look before it was 1.35 s earlier");
            Assert.That(Judge(1.75), Is.EqualTo(GateVerdict.Write), "at the late look itself");
        });
    }

    /// <summary>The microphone muted in Windows closes the gate at the look that sees it: the packets before that look are dropped too, and the state says so.</summary>
    [Test]
    public void Muted_in_Windows_closes_the_gate_at_once()
    {
        _looks.Feed(Looks.Open(At(0)));
        _looks.Feed(Looks.MutedInWindows(At(0.25)));

        Assert.Multiple(() =>
        {
            Assert.That(Judge(0.1), Is.EqualTo(GateVerdict.Drop));
            Assert.That(_gate.State, Is.EqualTo(MicrophoneGateState.MutedInWindows));
            Assert.That(_gate.Why, Is.EqualTo("muted in Windows"));
        });
    }

    /// <summary>
    /// When Windows' usage record says the app has closed the microphone while its session is still active, the gate
    /// follows the stricter one two seconds later, and logs the disagreement at Debug, once; the record saying the app has
    /// it open again opens the gate again.
    /// </summary>
    [Test]
    public void The_usage_record_closes_the_gate_two_seconds_after_it_says_the_app_stopped()
    {
        for (var at = 0.0; at <= 2.25; at += 0.25) _looks.Feed(Looks.Open(At(at), usageInUse: false));

        Assert.Multiple(() =>
        {
            Assert.That(Judge(1.9), Is.EqualTo(GateVerdict.Write), "the record has said stopped for under two seconds on both sides");
            Assert.That(Judge(2.1), Is.EqualTo(GateVerdict.Drop), "the look after it is more than two seconds into the disagreement");
            Assert.That(_gate.State, Is.EqualTo(MicrophoneGateState.NotListening));
            Assert.That(_gate.Why, Is.EqualTo("chrome is not listening (Windows' usage record says it closed the microphone)"));
            Assert.That(_log.Entries.Where(entry => entry.Level == Microsoft.Extensions.Logging.LogLevel.Debug && entry.Message.Contains("usage record")), Has.Exactly(1).Items);
        });

        _looks.Feed(Looks.Open(At(2.5), usageInUse: true));
        _looks.Feed(Looks.Open(At(2.75), usageInUse: true));

        Assert.Multiple(() =>
        {
            Assert.That(Judge(2.6), Is.EqualTo(GateVerdict.Write));
            Assert.That(_gate.State, Is.EqualTo(MicrophoneGateState.Recording));
        });
    }

    /// <summary>An app absent from the usage record is neutral: the sessions alone decide.</summary>
    [Test]
    public void An_app_absent_from_the_usage_record_is_neutral()
    {
        for (var at = 0.0; at <= 3.0; at += 0.25) _looks.Feed(Looks.Open(At(at), usageInUse: null));

        Assert.That(Judge(2.9), Is.EqualTo(GateVerdict.Write));
    }

    /// <summary>
    /// The app letting go of the microphone for at least a second, while its process lives on, and taking it again is
    /// evidence that its mute is visible to Windows: while the setting is Unknown it is raised once per run, in words that
    /// name the times and the setting to make, and every cycle is counted.
    /// </summary>
    [Test]
    public void A_cycle_raises_the_evidence_once_while_the_class_apps_mute_is_unknown()
    {
        Make(ClassAppMute.Unknown, new FollowRule.Named("chrome"));

        Cycle(from: 0, gapSeconds: 1.5);
        Cycle(from: 5, gapSeconds: 1.5);

        // The times are the laptop's time of day: the gap began a quarter second after the start, and ended 1.75 s after it.
        var local = TimeZoneInfo.ConvertTime(Start, _time.LocalTimeZone);
        var letGo = local.ToString("HH:mm:ss");
        var tookAgain = local.AddSeconds(1).ToString("HH:mm:ss");

        Assert.Multiple(() =>
        {
            Assert.That(_evidence, Has.Count.EqualTo(1));
            Assert.That(_evidence[0], Is.EqualTo($"chrome let go of the microphone at {letGo} and took it again at {tookAgain}. If that was you muting and unmuting in the class page, your mute is visible to Windows: set Recording:ClassAppMute to Visible and ClassScribe will follow it by itself."));
            Assert.That(_gate.Summary().Cycles, Is.EqualTo(2));
        });
    }

    /// <summary>With the setting Invisible the cycles are counted for the summary and no evidence is raised; with Visible they are the normal case and are counted too, without evidence.</summary>
    [TestCase(ClassAppMute.Invisible)]
    [TestCase(ClassAppMute.Visible)]
    public void A_cycle_is_counted_without_evidence_when_the_setting_is_made(ClassAppMute appMute)
    {
        Make(appMute, new FollowRule.Named("chrome"));

        Cycle(from: 0, gapSeconds: 2);

        Assert.Multiple(() =>
        {
            Assert.That(_evidence, Is.Empty);
            Assert.That(_gate.Summary().Cycles, Is.EqualTo(1));
        });
    }

    /// <summary>A gap in which the app's process ended is a restart of the app, not a mute: no cycle.</summary>
    [Test]
    public void A_gap_in_which_the_apps_process_ended_is_no_cycle()
    {
        Make(ClassAppMute.Unknown, new FollowRule.Named("chrome"));

        _looks.Feed(Looks.Open(At(0)));
        _looks.Feed(Looks.NotOpened(At(0.25), processAlive: true));
        _looks.Feed(Looks.NotOpened(At(0.5), processAlive: false));
        for (var at = 0.75; at <= 1.5; at += 0.25) _looks.Feed(Looks.NotOpened(At(at), processAlive: true));
        _looks.Feed(Looks.Open(At(1.75)));

        Assert.Multiple(() =>
        {
            Assert.That(_evidence, Is.Empty);
            Assert.That(_gate.Summary().Cycles, Is.Zero);
        });
    }

    /// <summary>A gap shorter than a second is no cycle, and a change that reverts within a second is a flicker, counted and not logged.</summary>
    [Test]
    public void A_change_that_reverts_within_a_second_is_a_flicker_not_a_cycle()
    {
        Make(ClassAppMute.Unknown, new FollowRule.Named("chrome"));

        _looks.Feed(Looks.Open(At(0)));
        _looks.Feed(Looks.NotListening(At(0.25)));
        _looks.Feed(Looks.Open(At(0.5)));
        _looks.Feed(Looks.NotListening(At(0.75)));
        _looks.Feed(Looks.Open(At(1.0)));

        var summary = _gate.Summary();
        Assert.Multiple(() =>
        {
            Assert.That(summary.Flickers, Is.EqualTo(3), "the stretches that ended within a second of starting, the first look's left out");
            Assert.That(summary.Cycles, Is.Zero);
            Assert.That(_evidence, Is.Empty);
            Assert.That(_log.Messages.Where(message => message.Contains("listening")), Is.Empty, "flickers are counted, not logged");
        });
    }

    /// <summary>The microphone starts on when the gate can follow the app's mute, and when no app is followed; off when the app's mute cannot be seen.</summary>
    [TestCase(ClassAppMute.Visible, "named", true)]
    [TestCase(ClassAppMute.Invisible, "named", false)]
    [TestCase(ClassAppMute.Unknown, "named", false)]
    [TestCase(ClassAppMute.Visible, "any", true)]
    [TestCase(ClassAppMute.Unknown, "any", false)]
    [TestCase(ClassAppMute.Unknown, "none", true)]
    [TestCase(ClassAppMute.Invisible, "none", true)]
    public void The_microphone_starts_on_only_where_the_gate_can_follow_the_app(ClassAppMute appMute, string kind, bool armed)
    {
        Make(appMute, Rule(kind));

        Assert.That(_gate.Armed, Is.EqualTo(armed));
    }

    /// <summary>With the microphone off nothing is written, whatever the looks say, and the state says it is off; V switches it on and the looks count again.</summary>
    [Test]
    public void Off_drops_everything_until_V_switches_the_microphone_on()
    {
        Make(ClassAppMute.Invisible, new FollowRule.Named("chrome"));
        _looks.Feed(Looks.Open(At(0)));
        _looks.Feed(Looks.Open(At(0.25)));

        Assert.Multiple(() =>
        {
            Assert.That(Judge(0.1), Is.EqualTo(GateVerdict.Drop));
            Assert.That(_gate.State, Is.EqualTo(MicrophoneGateState.Off));
            Assert.That(_gate.Why, Is.EqualTo("off (V records your voice while chrome holds the microphone)"));
        });

        var changes = _changed;
        _gate.Arm(true);

        Assert.Multiple(() =>
        {
            Assert.That(_gate.Armed, Is.True);
            Assert.That(Judge(0.1), Is.EqualTo(GateVerdict.Write));
            Assert.That(_gate.State, Is.EqualTo(MicrophoneGateState.Recording));
            Assert.That(_gate.Why, Is.Null);
            Assert.That(_changed, Is.EqualTo(changes + 1), "Changed raised by V, so the held packets are judged again");
        });

        _gate.Arm(false);

        Assert.Multiple(() =>
        {
            Assert.That(Judge(0.1), Is.EqualTo(GateVerdict.Drop));
            Assert.That(_gate.State, Is.EqualTo(MicrophoneGateState.Off));
        });
    }

    /// <summary>Where the app's mute is visible, the off words only point to V.</summary>
    [Test]
    public void Off_where_the_apps_mute_is_visible_points_to_V()
    {
        _gate.Arm(false);

        Assert.That(_gate.Why, Is.EqualTo("off (V switches it on)"));
    }

    /// <summary>With no app followed, as in person, the gate is open whenever the microphone is on, without any look, and the state says no app is followed.</summary>
    [Test]
    public void With_no_app_followed_the_gate_is_open_whenever_the_microphone_is_on()
    {
        Make(ClassAppMute.Unknown, new FollowRule.None());

        Assert.Multiple(() =>
        {
            Assert.That(Judge(0.1), Is.EqualTo(GateVerdict.Write));
            Assert.That(_gate.State, Is.EqualTo(MicrophoneGateState.Unfollowed));
            Assert.That(_gate.Why, Is.Null);
        });

        _gate.Arm(false);

        Assert.Multiple(() =>
        {
            Assert.That(Judge(0.2), Is.EqualTo(GateVerdict.Drop));
            Assert.That(_gate.State, Is.EqualTo(MicrophoneGateState.Off));
            Assert.That(_gate.Why, Is.EqualTo("off (V switches it on)"));
        });

        _gate.Arm(true);
        _looks.Feed(Looks.NotOpened(At(1)));

        Assert.That(Judge(1.1), Is.EqualTo(GateVerdict.Write), "a look changes nothing when no app is followed");
    }

    /// <summary>Each state has its words for the status line, naming the app and the microphones.</summary>
    [Test]
    public void Each_state_has_its_words()
    {
        Assert.Multiple(() =>
        {
            Assert.That(_gate.State, Is.EqualTo(MicrophoneGateState.NotOpened), "before the first look");
            Assert.That(_gate.Why, Is.EqualTo("waiting for chrome to open the microphone"));
        });

        _looks.Feed(Looks.NotListening(At(0)));
        Assert.Multiple(() =>
        {
            Assert.That(_gate.State, Is.EqualTo(MicrophoneGateState.NotListening));
            Assert.That(_gate.Why, Is.EqualTo("chrome is not listening"));
        });

        _looks.Feed(Looks.OtherMicrophone(At(0.25)));
        Assert.Multiple(() =>
        {
            Assert.That(_gate.State, Is.EqualTo(MicrophoneGateState.OtherMicrophone));
            Assert.That(_gate.Why, Is.EqualTo("chrome uses \"Microphone (OBSBOT Meet Flip Microphone)\"; ClassScribe records \"Microphone Array (Intel® Smart Sound Technology for Digital Microphones)\""));
        });

        _looks.Feed(Looks.NotOpened(At(0.5)));
        Assert.Multiple(() =>
        {
            Assert.That(_gate.State, Is.EqualTo(MicrophoneGateState.NotOpened));
            Assert.That(_gate.Why, Is.EqualTo("waiting for chrome to open the microphone"));
        });

        _looks.Feed(Looks.Open(At(0.75)));
        Assert.Multiple(() =>
        {
            Assert.That(_gate.State, Is.EqualTo(MicrophoneGateState.Recording));
            Assert.That(_gate.Why, Is.Null);
        });
    }

    /// <summary>Following any app, the words name no app.</summary>
    [Test]
    public void Following_any_app_the_words_name_no_app()
    {
        Make(ClassAppMute.Visible, new FollowRule.Any());
        Assert.That(_gate.Why, Is.EqualTo("waiting for an app to open the microphone"));

        _looks.Feed(Looks.NotListening(At(0)));
        Assert.That(_gate.Why, Is.EqualTo("no app is listening"));

        _looks.Feed(Looks.Unknown(At(0.25)));
        _looks.Feed(Looks.Unknown(At(1.5)));
        Assert.That(_gate.Why, Does.StartWith("cannot read the microphone's sessions: "));

        _gate.Arm(false);
        Make(ClassAppMute.Unknown, new FollowRule.Any());
        Assert.That(_gate.Why, Is.EqualTo("off (V records your voice while an app holds the microphone)"));
    }

    /// <summary>
    /// The summary adds up the run: how long your voice was recorded and in how many stretches, how long the app was not
    /// listening, how long the microphone was off, and how long it was muted in Windows.
    /// </summary>
    [Test]
    public void The_summary_adds_up_the_run()
    {
        _looks.Feed(Looks.Open(At(0)));
        _looks.Feed(Looks.NotListening(At(10)));
        _looks.Feed(Looks.Open(At(20)));
        _time.Advance(At(5));
        _gate.Arm(false);
        _time.Advance(At(5));
        _gate.Arm(true);
        _looks.Feed(Looks.Open(At(30)));
        _looks.Feed(Looks.MutedInWindows(At(40)));
        _time.Advance(At(10));

        // The ten seconds the app did not listen, its process alive, and listened again are one cycle.
        Assert.That(_gate.Summary(), Is.EqualTo(new GateSummary(Heard: At(25), Stretches: 3, Muted: At(10), Off: At(5), MutedInWindows: At(10), Flickers: 0, Cycles: 1)));
    }

    /// <summary>With no app followed, the time the microphone is on counts as heard.</summary>
    [Test]
    public void With_no_app_followed_the_time_on_counts_as_heard()
    {
        Make(ClassAppMute.Unknown, new FollowRule.None());
        _time.Advance(At(30));
        _gate.Arm(false);
        _time.Advance(At(10));

        Assert.That(_gate.Summary(), Is.EqualTo(new GateSummary(Heard: At(30), Stretches: 1, Muted: TimeSpan.Zero, Off: At(10), MutedInWindows: TimeSpan.Zero, Flickers: 0, Cycles: 0)));
    }

    /// <summary>Looks are kept for two seconds: a packet stamped before the looks kept has no look before it, and is dropped.</summary>
    [Test]
    public void A_packet_stamped_before_the_looks_kept_is_dropped()
    {
        _looks.OpenFrom(At(0), At(3));

        Assert.Multiple(() =>
        {
            Assert.That(Judge(0.5), Is.EqualTo(GateVerdict.Drop));
            Assert.That(Judge(2.5), Is.EqualTo(GateVerdict.Write));
        });
    }

    /// <summary>A cycle: the app listening at <paramref name="from"/>, not listening for <paramref name="gapSeconds"/>, then listening again.</summary>
    private void Cycle(double from, double gapSeconds)
    {
        _looks.Feed(Looks.Open(At(from)));
        for (var at = from + 0.25; at < from + 0.25 + gapSeconds; at += 0.25) _looks.Feed(Looks.NotListening(At(at)));
        _looks.Feed(Looks.Open(At(from + 0.25 + gapSeconds)));
    }

    /// <summary>The follow rule <paramref name="kind"/> names.</summary>
    private static FollowRule Rule(string kind) => kind switch
    {
        "named" => new FollowRule.Named("chrome"),
        "any" => new FollowRule.Any(),
        _ => new FollowRule.None()
    };
}
