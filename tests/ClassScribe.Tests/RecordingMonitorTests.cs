using System.Globalization;
using ClassScribe.Audio;
using ClassScribe.Commands;
using ClassScribe.Recorder;
using ClassScribe.Settings;
using ClassScribe.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClassScribe.Tests;

/// <summary>
/// The checks made once a second while a class records, one look at a time: the status line, the silence warning, a
/// recording that cannot record or write, audio that stops arriving, a time ClassScribe did not run, --minutes and the
/// safety stop, the free space, a pause left on, and what needs attention in the title. The recording is described by
/// hand at each look, the clock is moved by hand, and the free space is set by the test.
/// </summary>
[TestFixture]
public class RecordingMonitorTests
{
    /// <summary>The folder the tests' recording goes to; only its drive's free space is read, from the test's stand-in.</summary>
    private const string Folder = @"D:\Classes\Kalam";

    /// <summary>The source the tests' tracks record unless a test names another.</summary>
    private const string Speakers = "Fake Speakers";

    private ManualTimeProvider _time = null!;
    private FakeSystemTimes _times = null!;
    private FakeFreeSpace _space = null!;
    private RecordingMonitor _monitor = null!;
    private MonitorContext _context = null!;

    /// <summary>The clock at the tests' class, the computer's clocks read from it, and room for any class on the disk.</summary>
    [SetUp]
    public void SetUp()
    {
        _time = new ManualTimeProvider(TestRuns.Start);
        _times = new FakeSystemTimes(_time);
        _space = new FakeFreeSpace();
    }

    /// <summary>
    /// Makes the monitor with <paramref name="options"/> (the defaults unless given) for <paramref name="request"/> (the
    /// tests' request unless given), as the recording loop starts it now.
    /// </summary>
    private void Begin(RecordingOptions? options = null, RecordRequest? request = null)
    {
        _monitor = new RecordingMonitor(new FreezeWatch(_times, _time, NullLogger<FreezeWatch>.Instance), _space, TestOptions.Create(options ?? new RecordingOptions()), _time);
        _context = new MonitorContext(request ?? TestRuns.Request(TestRuns.Id()), Folder, _time.GetUtcNow(), _time.GetTimestamp(), TimeSpan.Zero);
    }

    /// <summary>A look at <paramref name="snapshot"/> now, without moving the clock.</summary>
    private MonitorTick Look(RecordingSnapshot snapshot) => _monitor.Tick(snapshot, _context);

    /// <summary>Moves the clock on a second, then looks at <paramref name="snapshot"/>, as the loop does once a second.</summary>
    private MonitorTick Tick(RecordingSnapshot snapshot)
    {
        _time.Advance(TimeSpan.FromSeconds(1));
        return Look(snapshot);
    }

    /// <summary>
    /// Looks once a second for <paramref name="seconds"/> seconds, each time at the snapshot <paramref name="snapshot"/>
    /// gives for the seconds since the start, and returns every look's findings, in order.
    /// </summary>
    private List<MonitorTick> Run(int seconds, Func<int, RecordingSnapshot> snapshot)
    {
        var ticks = new List<MonitorTick>();
        for (var i = 0; i < seconds; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            ticks.Add(Look(snapshot(Seconds())));
        }

        return ticks;
    }

    /// <summary>Whole seconds since the monitor began, on the steady clock.</summary>
    private int Seconds() => (int)_time.GetElapsedTime(_context.StartedTimestamp).TotalSeconds;

    /// <summary>A recording as a look sees it: by default one that records from the speakers, has just heard sound and just received audio.</summary>
    private static RecordingSnapshot Recording(TrackSnapshot? main = null, TimeSpan position = default, bool paused = false) =>
        new(position, paused, main ?? FakeRecording.Track(), Microphone: null);

    /// <summary>The tests' request, recording the class from <paramref name="source"/>.</summary>
    private static RecordRequest RequestFor(ClassSource source) =>
        TestRuns.Request(TestRuns.Id()) with { Source = new SourcePlan(RecordingMode.Class, source, Microphone: null) };

    /// <summary>A recording that has heard no sound for <paramref name="seconds"/> seconds.</summary>
    private static RecordingSnapshot Silent(int seconds) => Recording(FakeRecording.Track(sinceLastSound: TimeSpan.FromSeconds(seconds)));

    /// <summary>A recording of an app's sound, which arrives all the time, last received <paramref name="sinceLastPacket"/> seconds ago.</summary>
    private static RecordingSnapshot App(int sinceLastPacket, bool paused = false) =>
        Recording(FakeRecording.Track("chrome", deliversContinuously: true, recordedLevel: 0.3f, sinceLastPacket: TimeSpan.FromSeconds(sinceLastPacket)), paused: paused);

    /// <summary>The texts of the lines a look found, in order.</summary>
    private static string[] Texts(MonitorTick tick) => [.. tick.Lines.Select(line => line.Text)];

    /// <summary>Every line of every look, in order.</summary>
    private static string[] Texts(IEnumerable<MonitorTick> ticks) => [.. ticks.SelectMany(Texts)];

    /// <summary>A moment as a line gives it: the local time of day, to the second.</summary>
    private static string Local(DateTimeOffset moment, string format = "HH:mm:ss") =>
        TimeZoneInfo.ConvertTime(moment, TimeZoneInfo.Local).ToString(format, CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------ the status line and the silence warning

    /// <summary>
    /// The status line comes at its interval and gives the position in the recording and what it records, "(paused)" while
    /// paused, and what it waits for while nothing can be recorded.
    /// </summary>
    [Test]
    public void The_status_line_gives_the_position_and_what_is_recorded_at_its_interval()
    {
        Begin(new RecordingOptions { StatusIntervalSeconds = 2 });

        var first = Tick(Recording(position: TimeSpan.FromSeconds(1)));
        var second = Tick(Recording(position: TimeSpan.FromSeconds(2)));
        Tick(Recording(position: TimeSpan.FromSeconds(3), paused: true));
        var paused = Tick(Recording(position: TimeSpan.FromSeconds(3), paused: true));
        Tick(Recording(FakeRecording.Track(capturing: false, waitingFor: "the output device \"Pixel Buds\" to connect"), TimeSpan.FromSeconds(3)));
        var waiting = Tick(Recording(FakeRecording.Track(capturing: false, waitingFor: "the output device \"Pixel Buds\" to connect"), TimeSpan.FromSeconds(3)));

        Assert.That(Texts(first), Is.Empty, "not before the interval");
        Assert.That(second.Lines, Is.EqualTo(new[] { (LogLevel.Information, "Recording: 00:00:02 captured from \"Fake Speakers\"") }));
        Assert.That(Texts(paused), Is.EqualTo(new[] { "Recording: 00:00:03 captured from \"Fake Speakers\" (paused)" }));
        Assert.That(Texts(waiting), Is.EqualTo(new[] { "Recording: 00:00:03 captured; waiting for the output device \"Pixel Buds\" to connect" }));
    }

    /// <summary>
    /// A minute without sound gives a warning, again once a minute while the silence lasts, and a Notice in the title that
    /// says for how long; once sound returns the Notice goes, and a new silence is a new event.
    /// </summary>
    [Test]
    public void Silence_is_warned_about_once_a_minute_and_noted_in_the_title()
    {
        Begin(new RecordingOptions { StatusIntervalSeconds = 3600 });

        var quiet = Run(59, Silent);
        var minute = Tick(Silent(60));
        var later = Run(59, Silent);
        var twoMinutes = Tick(Silent(120));
        var sound = Tick(Silent(0));
        Run(59, seconds => Silent(seconds - 121));
        var again = Tick(Silent(60));

        Assert.That(Texts(quiet), Is.Empty);
        Assert.That(quiet.Select(tick => tick.Attention.Level), Has.All.EqualTo(AttentionLevel.None));
        Assert.That(minute.Lines, Is.EqualTo(new[] { (LogLevel.Warning, "No sound for 60 seconds. Check that the class is playing through \"Fake Speakers\" and that Windows is not muted") }));
        Assert.That(minute.Attention, Is.EqualTo(new Attention(AttentionLevel.Notice, "no sound 1 min",
            "No sound for 60 seconds. Check that the class is playing through \"Fake Speakers\" and that Windows is not muted", 1)));
        Assert.That(Texts(later), Is.Empty, "not again within the minute");
        Assert.That(Texts(twoMinutes), Is.EqualTo(new[] { "No sound for 120 seconds. Check that the class is playing through \"Fake Speakers\" and that Windows is not muted" }));
        Assert.That(twoMinutes.Attention.Short, Is.EqualTo("no sound 2 min"));
        Assert.That(twoMinutes.Attention.Count, Is.EqualTo(1), "the same silence");
        Assert.That(sound.Attention, Is.EqualTo(new Attention(AttentionLevel.None, "", "", 1)), "the count stays");
        Assert.That(again.Attention.Level, Is.EqualTo(AttentionLevel.Notice));
        Assert.That(again.Attention.Count, Is.EqualTo(2), "a new silence is a new event");
    }

    /// <summary>
    /// No silence is warned about, and no Notice given, while paused or while nothing can be recorded: the class cannot be
    /// heard then, and the pause or the wait says so for itself.
    /// </summary>
    [Test]
    public void Silence_is_not_warned_about_while_paused_or_waiting()
    {
        Begin(new RecordingOptions { StatusIntervalSeconds = 3600 });

        var paused = Run(90, seconds => Recording(FakeRecording.Track(sinceLastSound: TimeSpan.FromSeconds(seconds)), paused: true));
        var waiting = Run(10, seconds => Recording(FakeRecording.Track(capturing: false, waitingFor: "an output device", sinceLastSound: TimeSpan.FromSeconds(seconds))));

        Assert.That(Texts(paused).Concat(Texts(waiting)), Has.None.StartsWith("No sound"));
        Assert.That(paused.Concat(waiting).Select(tick => tick.Attention.Level), Has.None.EqualTo(AttentionLevel.Notice));
    }

    // ------------------------------------------------------------------ nothing recorded, nothing written

    /// <summary>
    /// A recording that has had nothing to record for 15 seconds is an Alert that names what it waits for; the moment it
    /// records again, the Alert goes. A wait shorter than that, as while a device is replaced, needs no attention.
    /// </summary>
    [Test]
    public void Nothing_recorded_for_15_seconds_is_an_Alert()
    {
        Begin(request: RequestFor(new ClassSource.App("chrome")));
        var waiting = Recording(FakeRecording.Track("chrome", capturing: false, waitingFor: "\"chrome\" to play sound"));

        var first = Run(14, _ => waiting);
        var alert = Tick(waiting);
        var recording = Tick(Recording());

        Assert.That(first.Select(tick => tick.Attention.Level), Has.All.EqualTo(AttentionLevel.None));
        Assert.That(alert.Attention, Is.EqualTo(new Attention(AttentionLevel.Alert, "NOT RECORDING (waiting for \"chrome\")",
            "Nothing has been recorded for 15 seconds; waiting for \"chrome\" to play sound", 1)));
        Assert.That(recording.Attention.Level, Is.EqualTo(AttentionLevel.None));
    }

    /// <summary>
    /// The title names what the recording waits for in a few words, so that the class's name still fits in a terminal's
    /// tab: the app or the device by the name the run was given, or any output device. The log says it in full, in the
    /// words of what picks the device or app to record.
    /// </summary>
    [TestCase("app", "NOT RECORDING (waiting for \"chrome\")", "waiting for \"chrome\" to play sound")]
    [TestCase("device", "NOT RECORDING (waiting for \"Pixel Buds\")", "waiting for the output device \"Pixel Buds\" to connect")]
    [TestCase("automatic", "NOT RECORDING (waiting for an output device)", "waiting for an output device")]
    public void The_title_names_in_a_few_words_what_the_recording_waits_for(string kind, string title, string waitingFor)
    {
        ClassSource source = kind switch
        {
            "app" => new ClassSource.App("chrome"),
            "device" => new ClassSource.Device("Pixel Buds"),
            _ => new ClassSource.Automatic()
        };
        Begin(request: RequestFor(source));
        var chooser = new CaptureChooser(_time, source, NullLogger<CaptureChooser>.Instance);
        var waiting = Recording(FakeRecording.Track(capturing: false, waitingFor: chooser.WaitingFor));

        var alert = Run(15, _ => waiting)[^1];

        Assert.That(alert.Attention, Is.EqualTo(new Attention(AttentionLevel.Alert, title, $"Nothing has been recorded for 15 seconds; {waitingFor}", 1)));
    }

    /// <summary>In person the recording has no class source to name, so the title says NOT RECORDING alone.</summary>
    [Test]
    public void In_person_the_title_says_NOT_RECORDING_alone()
    {
        Begin(request: TestRuns.Request(TestRuns.Id()) with { Source = new SourcePlan(RecordingMode.Microphone, Class: null, new MicrophoneChoice(null)) });
        var waiting = Recording(FakeRecording.Track("Microphone Array", capturing: false, deliversContinuously: true));

        var alert = Run(15, _ => waiting)[^1];

        Assert.That(alert.Attention.Level, Is.EqualTo(AttentionLevel.Alert));
        Assert.That(alert.Attention.Short, Is.EqualTo("NOT RECORDING"));
    }

    /// <summary>While paused, nothing is recorded by choice: a wait for the source then needs no attention.</summary>
    [Test]
    public void Nothing_recorded_while_paused_needs_no_attention()
    {
        Begin();

        var ticks = Run(30, _ => Recording(FakeRecording.Track(capturing: false, waitingFor: "an output device"), paused: true));

        Assert.That(ticks.Select(tick => tick.Attention.Level), Has.All.EqualTo(AttentionLevel.None));
    }

    /// <summary>A track whose files cannot be written is an Alert at once, until a write works again.</summary>
    [Test]
    public void Files_that_cannot_be_written_are_an_Alert_at_once()
    {
        Begin();

        var failing = Tick(Recording(FakeRecording.Track(writeFailing: true)));
        var working = Tick(Recording());

        Assert.That(failing.Attention.Level, Is.EqualTo(AttentionLevel.Alert));
        Assert.That(failing.Attention.Short, Is.EqualTo("CANNOT WRITE"));
        Assert.That(failing.Attention.Text, Does.StartWith("The recording cannot be written to disk now"));
        Assert.That(working.Attention.Level, Is.EqualTo(AttentionLevel.None));
    }

    // ------------------------------------------------------------------ audio that stops arriving

    /// <summary>
    /// An app's sound, which arrives all the time, that stops arriving for 5 seconds is warned about, its capture is to be
    /// reopened, and it is an Alert. The warning and the reopening come again only 30 seconds after the last, while the
    /// Alert stays until audio has flowed again for 5 seconds: a new capture that has not delivered yet is no sign that
    /// audio is back.
    /// </summary>
    [Test]
    public void An_app_that_hands_over_nothing_for_5_seconds_is_warned_about_reopened_and_an_Alert_until_audio_flows_again()
    {
        Begin();

        var before = Run(4, seconds => App(seconds));
        var stalled = Tick(App(5));
        var replaced = Run(29, seconds => App(seconds - 6));            // the new capture from 6 s on delivers nothing either
        var again = Tick(App(29));
        var flowing = Run(5, _ => App(0));
        var back = Tick(App(0));

        Assert.That(Texts(before), Is.Empty);
        Assert.That(before.Select(tick => tick.Reopen), Has.All.Null);
        Assert.That(stalled.Lines, Is.EqualTo(new[]
        {
            (LogLevel.Warning, "No audio has arrived from \"chrome\" for 5 seconds although it should; the recording has fallen behind the clock. Reopening it")
        }));
        Assert.That(stalled.Reopen, Is.EqualTo((Track.Main, "no audio has arrived for 5 seconds")));
        Assert.That(stalled.Attention.Level, Is.EqualTo(AttentionLevel.Alert));
        Assert.That(stalled.Attention.Short, Is.EqualTo("NO AUDIO from \"chrome\""));
        Assert.That(Texts(replaced), Is.Empty, "not again within 30 seconds");
        Assert.That(replaced.Select(tick => tick.Reopen), Has.All.Null);
        Assert.That(replaced.Select(tick => tick.Attention.Level), Has.All.EqualTo(AttentionLevel.Alert), "no audio has come back");
        Assert.That(Texts(again), Has.Length.EqualTo(1), "again 30 seconds after the first");
        Assert.That(again.Reopen, Is.Not.Null);
        Assert.That(flowing.Select(tick => tick.Attention.Level), Has.All.EqualTo(AttentionLevel.Alert), "not before audio has flowed for 5 seconds");
        Assert.That(back.Attention.Level, Is.EqualTo(AttentionLevel.None));
        Assert.That(new[] { stalled, again, back }.Select(tick => tick.Attention.Count), Has.All.EqualTo(1), "one stall, however long");
    }

    /// <summary>
    /// A stalled capture that cannot be opened again has not brought audio back: the stall stays an Alert while no capture
    /// is open, until nothing recorded for 15 seconds takes the title over, as a new event.
    /// </summary>
    [Test]
    public void A_stall_stays_an_Alert_while_no_capture_is_open()
    {
        Begin(new RecordingOptions { StatusIntervalSeconds = 3600 }, RequestFor(new ClassSource.App("chrome")));
        var noCapture = Recording(FakeRecording.Track("chrome", capturing: false, waitingFor: "\"chrome\" to play sound"));

        Run(4, seconds => App(seconds));
        var stalled = Tick(App(5));
        var closed = Run(14, _ => noCapture);
        var waiting = Tick(noCapture);

        Assert.That(stalled.Attention.Short, Is.EqualTo("NO AUDIO from \"chrome\""));
        Assert.That(closed.Select(tick => tick.Attention.Short), Has.All.EqualTo("NO AUDIO from \"chrome\""), "no audio has come back");
        Assert.That(waiting.Attention.Short, Is.EqualTo("NOT RECORDING (waiting for \"chrome\")"));
        Assert.That(waiting.Attention.Count, Is.EqualTo(2));
    }

    /// <summary>While paused, an app's sound that stops arriving is reopened too, and the warning says it is for after the pause.</summary>
    [Test]
    public void While_paused_an_app_that_stops_arriving_is_reopened_for_after_the_pause()
    {
        Begin();

        var ticks = Run(5, seconds => App(seconds, paused: true));

        Assert.That(Texts(ticks), Has.Some.EqualTo(
            "No audio has arrived from \"chrome\" for 5 seconds although it should. Reopening it, so that the recording can go on when you press P again"));
        Assert.That(ticks[^1].Reopen?.Track, Is.EqualTo(Track.Main));
    }

    /// <summary>
    /// A device whose meter has shown sound at every look for 10 seconds while no audio arrived is warned about and
    /// reopened; one quiet look on the meter starts that count again, and silence on an app is audio too.
    /// </summary>
    [Test]
    public void A_device_that_plays_for_10_seconds_without_audio_is_warned_about()
    {
        Begin();
        RecordingSnapshot Device(float level, int sinceLastPacket) =>
            Recording(FakeRecording.Track(recordedLevel: level, sinceLastPacket: TimeSpan.FromSeconds(sinceLastPacket)));

        var playing = Run(6, seconds => Device(0.5f, seconds));
        var quiet = Tick(Device(0f, 7));
        var before = Run(10, seconds => Device(0.5f, seconds));
        var stalled = Tick(Device(0.5f, 18));

        Assert.That(Texts(playing.Append(quiet).Concat(before)), Has.None.StartsWith("No audio"));
        Assert.That(Texts(stalled), Is.EqualTo(new[]
        {
            "No audio has arrived from \"Fake Speakers\" for 10 seconds although Windows shows sound playing there; the recording has fallen behind the clock. Reopening it"
        }));
        Assert.That(stalled.Reopen?.Track, Is.EqualTo(Track.Main));
    }

    // ------------------------------------------------------------------ a time ClassScribe did not run

    /// <summary>
    /// A sleep of the laptop gives one line in local time, a note for the transcript at the recording's position in UTC,
    /// and an Alert for 5 minutes; the silence before it is not blamed on the class.
    /// </summary>
    [Test]
    public void A_sleep_gives_a_line_a_note_and_an_Alert_for_5_minutes()
    {
        Begin(new RecordingOptions { StatusIntervalSeconds = 3600 });
        Run(2, _ => Recording(position: TimeSpan.FromSeconds(42)));
        var from = _time.GetUtcNow();

        _times.Sleep(TimeSpan.FromSeconds(18));
        var woke = Look(Recording(FakeRecording.Track(sinceLastSound: TimeSpan.FromSeconds(80)), TimeSpan.FromSeconds(42)));
        var to = _time.GetUtcNow();
        var during = Run(299, _ => Recording(position: TimeSpan.FromSeconds(42)));
        var after = Tick(Recording(position: TimeSpan.FromSeconds(42)));

        Assert.That(woke.Lines, Is.EqualTo(new[] { (LogLevel.Warning, $"The laptop slept from {Local(from)} to {Local(to)}; nothing was recorded then") }));
        Assert.That(woke.Notes, Is.EqualTo(new[] { new TimelineNote(TimeSpan.FromSeconds(42), "Not recorded: the laptop slept from 23:00:04 to 23:00:22 UTC") }));
        Assert.That(woke.Attention, Is.EqualTo(new Attention(AttentionLevel.Alert, $"SLEPT {Local(from, "HH:mm")} to {Local(to, "HH:mm")}",
            $"The laptop slept from {Local(from)} to {Local(to)}; nothing was recorded then", 1)));
        Assert.That(during.Select(tick => tick.Attention.Level), Has.All.EqualTo(AttentionLevel.Alert));
        Assert.That(after.Attention.Level, Is.EqualTo(AttentionLevel.None), "the Alert lasts 5 minutes");
        Assert.That(Texts(during.Append(after)), Is.Empty);
    }

    /// <summary>ClassScribe held up while the laptop stayed awake is worded as a hold-up, with seconds in the title.</summary>
    [Test]
    public void A_hold_up_is_worded_as_one()
    {
        Begin();
        Run(2, _ => Recording());
        var from = _time.GetUtcNow();

        _time.Advance(TimeSpan.FromSeconds(18));
        var woke = Look(Recording());
        var to = _time.GetUtcNow();

        Assert.That(Texts(woke), Is.EqualTo(new[] { $"ClassScribe was held up from {Local(from)} to {Local(to)} (Windows paused it); nothing was recorded then" }));
        Assert.That(woke.Notes.Single().Text, Is.EqualTo("Not recorded: ClassScribe was held up from 23:00:04 to 23:00:22 UTC (Windows paused it)"));
        Assert.That(woke.Attention.Short, Is.EqualTo($"HELD UP {Local(from)} to {Local(to)}"));
    }

    // ------------------------------------------------------------------ --minutes and the safety stop

    /// <summary>
    /// --minutes counts on the steady clock from the start, through a pause and a sleep alike: with 2 minutes, the
    /// recording stops 120 seconds after it started, however much of that was paused or asleep, and the time it stops is
    /// known from the start. Windows setting its time of day moves when that is by the clock, never the stop itself.
    /// </summary>
    [Test]
    public void The_minutes_count_on_the_steady_clock_through_a_pause_and_a_sleep()
    {
        Begin(request: TestRuns.Request(TestRuns.Id()) with { Minutes = 2 });

        var recording = Run(30, _ => Recording());
        var paused = Run(30, _ => Recording(paused: true));
        _times.Sleep(TimeSpan.FromSeconds(18));
        var woke = Look(Recording());
        _time.StepWallClock(TimeSpan.FromHours(1));
        var before = Run(41, _ => Recording());
        var stop = Tick(Recording());

        Assert.That(recording[0].StopsAtUtc, Is.EqualTo(TestRuns.Start.AddMinutes(2)));
        Assert.That(recording.Concat(paused).Append(woke).Concat(before).Select(tick => tick.StopBecause), Has.All.Null);
        Assert.That(Texts(woke), Has.Length.EqualTo(1).And.All.StartWith("The laptop slept"), "the sleep is said, and nothing stops yet");
        Assert.That(before[^1].StopsAtUtc, Is.EqualTo(TestRuns.Start.AddMinutes(2).AddHours(1)), "by the clock set forward");
        Assert.That(Seconds(), Is.EqualTo(120));
        Assert.That(stop.StopBecause, Is.EqualTo(RunEndCause.Minutes));
        Assert.That(stop.Lines, Has.Some.EqualTo((LogLevel.Information, "The time given with --minutes 2 is up; stopping the recording")));
    }

    /// <summary>A sleep that passes the end of --minutes stops the recording right after waking, and the sleep and the stop are both said.</summary>
    [Test]
    public void A_sleep_past_the_end_of_the_minutes_stops_on_waking_and_says_both()
    {
        Begin(new RecordingOptions { StatusIntervalSeconds = 3600 }, TestRuns.Request(TestRuns.Id()) with { Minutes = 1 });
        Run(2, _ => Recording());

        _times.Sleep(TimeSpan.FromMinutes(2));
        var woke = Look(Recording());

        Assert.That(woke.StopBecause, Is.EqualTo(RunEndCause.Minutes));
        Assert.That(Texts(woke), Has.Length.EqualTo(2));
        Assert.That(Texts(woke)[0], Does.StartWith("The laptop slept from "));
        Assert.That(Texts(woke)[1], Is.EqualTo("The time given with --minutes 1 is up; stopping the recording"));
    }

    /// <summary>With --minutes, the status line says when the recording stops by itself, in local time.</summary>
    [Test]
    public void The_status_line_says_when_the_recording_stops_by_itself()
    {
        Begin(new RecordingOptions { StatusIntervalSeconds = 1 }, TestRuns.Request(TestRuns.Id()) with { Minutes = 90 });

        var tick = Tick(Recording(position: TimeSpan.FromSeconds(1)));

        Assert.That(Texts(tick), Is.EqualTo(new[]
        {
            $"Recording: 00:00:01 captured from \"Fake Speakers\"; stops by itself at {Local(TestRuns.Start.AddMinutes(90), "HH:mm")}"
        }));
    }

    /// <summary>--minutes takes fractions: 1.5 stops after 90 seconds.</summary>
    [Test]
    public void A_fraction_of_a_minute_counts()
    {
        Begin(request: TestRuns.Request(TestRuns.Id()) with { Minutes = 1.5 });

        var ticks = Run(90, _ => Recording());

        Assert.That(ticks.Take(89).Select(tick => tick.StopBecause), Has.All.Null);
        Assert.That(ticks[^1].StopBecause, Is.EqualTo(RunEndCause.Minutes));
        Assert.That(Texts(ticks[^1]), Has.Some.EqualTo("The time given with --minutes 1.5 is up; stopping the recording"));
    }

    /// <summary>
    /// The safety stop warns once, 15 minutes before the recording reaches Recording:StopAfterHours, says from then when it
    /// stops, and stops it when it gets there. The length counts the recording's earlier parts too, since ElevenLabs takes
    /// at most 10 hours in one file.
    /// </summary>
    [Test]
    public void The_safety_stop_warns_15_minutes_before_and_then_stops()
    {
        Begin(new RecordingOptions { StopAfterHours = 9 });

        var early = Tick(Recording(position: new TimeSpan(8, 44, 59)));
        var warned = Tick(Recording(position: new TimeSpan(8, 45, 0)));
        var later = Tick(Recording(position: new TimeSpan(8, 50, 0)));
        var stop = Tick(Recording(position: new TimeSpan(9, 0, 0)));

        Assert.That(Texts(early), Is.Empty);
        Assert.That(early.StopsAtUtc, Is.Null, "not said hours ahead");
        Assert.That(warned.Lines, Is.EqualTo(new[]
        {
            (LogLevel.Warning, "This recording stops by itself in 15 minutes, after 9 hours (Recording:StopAfterHours): ElevenLabs takes at most 10 hours of audio in one file")
        }));
        Assert.That(warned.StopsAtUtc, Is.EqualTo(TestRuns.Start.AddSeconds(2).AddMinutes(15)), "two seconds in, 15 minutes before the stop");
        Assert.That(Texts(later), Is.Empty, "warned once");
        Assert.That(later.StopsAtUtc, Is.Not.Null);
        Assert.That(stop.StopBecause, Is.EqualTo(RunEndCause.SafetyStop));
        Assert.That(stop.Lines, Has.Some.EqualTo((LogLevel.Warning,
            "This recording stops now, after 9 hours (Recording:StopAfterHours): ElevenLabs takes at most 10 hours of audio in one file. Start a new recording to record more")));
    }

    /// <summary>
    /// The safety stop counts the time the recording has run as well, so that a recording left running by mistake stops
    /// even while it records nothing, as when it is paused or the class has ended; here after 72 seconds.
    /// </summary>
    [Test]
    public void The_safety_stop_counts_the_time_the_recording_has_run()
    {
        Begin(new RecordingOptions { StopAfterHours = 0.02 });

        var ticks = Run(72, _ => Recording(paused: true));

        Assert.That(Texts(ticks[0]), Has.Some.StartsWith("This recording stops by itself in 71 seconds, after 72 seconds"));
        Assert.That(ticks.Take(71).Select(tick => tick.StopBecause), Has.All.Null);
        Assert.That(ticks[^1].StopBecause, Is.EqualTo(RunEndCause.SafetyStop));
    }

    /// <summary>With Recording:StopAfterHours 0 the recording never stops by itself.</summary>
    [Test]
    public void No_safety_stop_with_0_hours()
    {
        Begin(new RecordingOptions { StopAfterHours = 0 });

        var tick = Tick(Recording(position: TimeSpan.FromHours(20)));

        Assert.That(tick.StopBecause, Is.Null);
        Assert.That(tick.StopsAtUtc, Is.Null);
        Assert.That(Texts(tick), Is.Empty);
    }

    // ------------------------------------------------------------------ the free space

    /// <summary>
    /// At the start, a disk with room for less than 3 hours of recording is warned about: a track takes 346 MB an hour and
    /// the upload copy 115 MB, so 1 GB holds about 2 hours 10 minutes. It needs no attention yet.
    /// </summary>
    [Test]
    public void Room_for_less_than_3_hours_is_warned_about_at_the_start()
    {
        _space.Bytes = 1_000_000_000;
        Begin();

        var tick = Tick(Recording());

        Assert.That(tick.Lines, Is.EqualTo(new[]
        {
            (LogLevel.Warning, @"Only about 2 hours 10 minutes of recording fit on D:\ (1.0 GB free). Free some space there before a long class")
        }));
        Assert.That(tick.Attention.Level, Is.EqualTo(AttentionLevel.None));
        Assert.That(_space.Asked, Is.EqualTo(new[] { Folder }));
    }

    /// <summary>
    /// Room for less than 3 hours but more than 30 minutes is warned about once, at the start: the readings every 5 minutes
    /// after it, which find the same room, say nothing and need no attention, so the warning does not come back all class.
    /// </summary>
    [Test]
    public void Room_for_less_than_3_hours_is_warned_about_at_the_start_only()
    {
        _space.Bytes = 1_000_000_000;
        Begin(new RecordingOptions { StatusIntervalSeconds = 3600 });

        var ticks = Run(660, _ => Recording());

        Assert.That(_space.Asked, Has.Count.EqualTo(3), "at the start, and at 5 and 10 minutes");
        Assert.That(Texts(ticks[0]), Is.EqualTo(new[]
        {
            @"Only about 2 hours 10 minutes of recording fit on D:\ (1.0 GB free). Free some space there before a long class"
        }));
        Assert.That(Texts(ticks.Skip(1)), Is.Empty, "said once, at the start");
        Assert.That(ticks.Select(tick => tick.Attention.Level), Has.All.EqualTo(AttentionLevel.None));
    }

    /// <summary>A recording with the microphone writes two tracks, so the same disk holds less of it.</summary>
    [Test]
    public void Each_track_counts_for_the_room()
    {
        _space.Bytes = 1_000_000_000;
        Begin(request: TestRuns.Request(TestRuns.Id()) with { Source = new SourcePlan(RecordingMode.ClassAndMicrophone, new ClassSource.Automatic(), new MicrophoneChoice(null)) });

        var tick = Tick(Recording());

        Assert.That(Texts(tick), Has.Some.StartsWith(@"Only about 1 hour 14 minutes of recording fit on D:\"));
    }

    /// <summary>
    /// The free space is read again every 5 minutes, and room for less than 30 minutes is an Alert with a line, until
    /// there is room again. Between those looks, it is not read.
    /// </summary>
    [Test]
    public void Room_for_less_than_30_minutes_is_an_Alert_checked_every_5_minutes()
    {
        Begin(new RecordingOptions { StatusIntervalSeconds = 3600 });

        var plenty = Run(300, _ => Recording());
        _space.Bytes = 200_000_000;
        var low = Tick(Recording());
        var meanwhile = Run(299, _ => Recording());
        _space.Bytes = 0;
        var full = Tick(Recording());
        _space.Bytes = 500_000_000_000;
        Run(299, _ => Recording());
        var room = Tick(Recording());

        Assert.That(Texts(plenty), Is.Empty);
        Assert.That(_space.Asked, Has.Count.EqualTo(4), "at the start, and at 5, 10 and 15 minutes");
        Assert.That(low.Lines, Is.EqualTo(new[]
        {
            (LogLevel.Warning, @"Only about 26 minutes of recording still fit on D:\ (200 MB free). Free some space there now, or the recording will stop being saved")
        }));
        Assert.That(low.Attention.Level, Is.EqualTo(AttentionLevel.Alert));
        Assert.That(low.Attention.Short, Is.EqualTo("DISK NEARLY FULL"));
        Assert.That(meanwhile.Select(tick => tick.Attention.Short), Has.All.EqualTo("DISK NEARLY FULL"));
        Assert.That(Texts(meanwhile), Is.Empty);
        Assert.That(Texts(full), Is.EqualTo(new[] { @"Less than a minute of recording still fits on D:\ (0 MB free). Free some space there now, or the recording will stop being saved" }));
        Assert.That(full.Attention.Count, Is.EqualTo(1), "the same shortage");
        Assert.That(room.Attention.Level, Is.EqualTo(AttentionLevel.None));
    }

    /// <summary>A drive whose free space cannot be read, such as a network share, is not warned about.</summary>
    [Test]
    public void A_drive_that_cannot_be_read_is_not_warned_about()
    {
        _space.Bytes = null;
        Begin();

        var tick = Tick(Recording());

        Assert.That(Texts(tick), Is.Empty);
        Assert.That(tick.Attention.Level, Is.EqualTo(AttentionLevel.None));
    }

    /// <summary>The free space of a real folder is read from its drive.</summary>
    [Test]
    public void The_free_space_of_a_folder_is_read_from_its_drive()
    {
        using var folder = new TempFolder();

        var bytes = new FreeSpace(NullLogger<FreeSpace>.Instance).AvailableBytes(folder.Path);

        Assert.That(bytes, Is.GreaterThan(0));
    }

    /// <summary>
    /// A folder on a network share, or one given without its drive, has no drive whose free space can be read: null, with
    /// a line for diagnosis, rather than a failure that would end the class.
    /// </summary>
    [TestCase(@"\\server\share\Classes")]
    [TestCase("Classes")]
    public void A_folder_without_a_drive_to_read_has_no_free_space(string folder)
    {
        var log = new RecordingLogger<FreeSpace>();

        var bytes = new FreeSpace(log).AvailableBytes(folder);

        Assert.That(bytes, Is.Null);
        Assert.That(log.Messages, Has.Some.StartsWith("Could not read the free space"));
    }

    // ------------------------------------------------------------------ a pause left on

    /// <summary>
    /// A pause is recalled every 5 minutes, and is an Alert from 10 minutes on, whose title says for how long; P resumes,
    /// and a later pause starts its count again.
    /// </summary>
    [Test]
    public void A_pause_is_recalled_every_5_minutes_and_is_an_Alert_from_10_minutes()
    {
        Begin(new RecordingOptions { StatusIntervalSeconds = 3600 });

        var first = Run(299, _ => Recording(paused: true));
        var fiveMinutes = Tick(Recording(paused: true));
        var next = Run(299, _ => Recording(paused: true));
        var tenMinutes = Tick(Recording(paused: true));
        var eleven = Run(60, _ => Recording(paused: true));
        var resumed = Tick(Recording());
        var again = Run(300, _ => Recording(paused: true));

        Assert.That(Texts(first.Concat(next)), Has.None.StartsWith("Still paused"));
        Assert.That(fiveMinutes.Lines, Is.EqualTo(new[] { (LogLevel.Warning, "Still paused, for 5 minutes now; P records again") }));
        Assert.That(fiveMinutes.Attention.Level, Is.EqualTo(AttentionLevel.None));
        Assert.That(Texts(tenMinutes), Is.EqualTo(new[] { "Still paused, for 10 minutes now; P records again" }));
        Assert.That(tenMinutes.Attention, Is.EqualTo(new Attention(AttentionLevel.Alert, "PAUSED 10 MIN", "Still paused, for 10 minutes now; P records again", 1)));
        Assert.That(eleven[^1].Attention.Short, Is.EqualTo("PAUSED 11 MIN"));
        Assert.That(eleven[^1].Attention.Count, Is.EqualTo(1));
        Assert.That(resumed.Attention.Level, Is.EqualTo(AttentionLevel.None));
        Assert.That(Texts(again), Is.EqualTo(new[] { "Still paused, for 5 minutes now; P records again" }));
    }

    /// <summary>
    /// Sound that keeps arriving for 30 seconds while paused means the class seems to be playing unrecorded: a line and an
    /// Alert, said once while it lasts; it ends with the sound, and a new stretch of 30 seconds is a new event.
    /// </summary>
    [Test]
    public void Sound_while_paused_for_30_seconds_is_an_Alert()
    {
        Begin(new RecordingOptions { StatusIntervalSeconds = 3600 });
        var playing = Recording(FakeRecording.Track(soundWhilePaused: true), paused: true);
        var quiet = Recording(paused: true);

        var before = Run(29, _ => playing);
        var thirty = Tick(playing);
        var lasting = Run(20, _ => playing);
        var stopped = Tick(quiet);
        var again = Run(30, _ => playing);

        Assert.That(before.Select(tick => tick.Attention.Level), Has.All.EqualTo(AttentionLevel.None));
        Assert.That(thirty.Lines, Is.EqualTo(new[] { (LogLevel.Warning, "The class seems to be playing while paused; P records again") }));
        Assert.That(thirty.Attention, Is.EqualTo(new Attention(AttentionLevel.Alert, "PLAYING WHILE PAUSED", "The class seems to be playing while paused; P records again", 1)));
        Assert.That(Texts(lasting), Is.Empty, "said once while it lasts");
        Assert.That(lasting.Select(tick => tick.Attention.Level), Has.All.EqualTo(AttentionLevel.Alert));
        Assert.That(stopped.Attention.Level, Is.EqualTo(AttentionLevel.None));
        Assert.That(again[^1].Attention.Count, Is.EqualTo(2));
        Assert.That(Texts(again), Has.Length.EqualTo(1));
    }

    // ------------------------------------------------------------------ attention

    /// <summary>
    /// An Alert takes the title over a Notice, and once it ends the Notice shows again. The count grows with each new
    /// event that shows, so a window flashes for each new Alert, but not for a Notice that starts under an Alert.
    /// </summary>
    [Test]
    public void An_Alert_takes_the_title_over_a_Notice_and_each_new_event_counts_once()
    {
        Begin(new RecordingOptions { StatusIntervalSeconds = 3600 });

        var notice = Run(60, Silent)[^1];
        var alert = Tick(Recording(FakeRecording.Track(sinceLastSound: TimeSpan.FromSeconds(61), writeFailing: true)));
        var noticeBack = Tick(Silent(62));

        Assert.That(notice.Attention.Level, Is.EqualTo(AttentionLevel.Notice));
        Assert.That(notice.Attention.Count, Is.EqualTo(1));
        Assert.That(alert.Attention.Short, Is.EqualTo("CANNOT WRITE"));
        Assert.That(alert.Attention.Count, Is.EqualTo(2));
        Assert.That(noticeBack.Attention.Level, Is.EqualTo(AttentionLevel.Notice));
        Assert.That(noticeBack.Attention.Count, Is.EqualTo(2), "the silence was counted when it began");
    }

    /// <summary>A Notice that starts while an Alert shows is not counted: the window has nothing new to flash for.</summary>
    [Test]
    public void A_Notice_that_starts_under_an_Alert_is_not_counted()
    {
        Begin(new RecordingOptions { StatusIntervalSeconds = 3600 });

        var alert = Tick(Recording(FakeRecording.Track(writeFailing: true)));
        var both = Run(60, seconds => Recording(FakeRecording.Track(sinceLastSound: TimeSpan.FromSeconds(seconds), writeFailing: true)))[^1];
        var notice = Tick(Silent(62));

        Assert.That(alert.Attention.Count, Is.EqualTo(1));
        Assert.That(both.Attention.Short, Is.EqualTo("CANNOT WRITE"));
        Assert.That(both.Attention.Count, Is.EqualTo(1));
        Assert.That(notice.Attention.Level, Is.EqualTo(AttentionLevel.Notice));
        Assert.That(notice.Attention.Count, Is.EqualTo(1));
    }

    /// <summary>The Alerts rank by what matters most: nothing being recorded comes before a sleep that is over.</summary>
    [Test]
    public void Nothing_recorded_ranks_above_a_sleep()
    {
        Begin();
        var waiting = Recording(FakeRecording.Track(capturing: false, waitingFor: "an output device"));

        Run(2, _ => Recording());
        _times.Sleep(TimeSpan.FromSeconds(18));
        var woke = Look(waiting);
        var both = Run(15, _ => waiting)[^1];

        Assert.That(woke.Attention.Short, Does.StartWith("SLEPT"));
        Assert.That(both.Attention.Short, Is.EqualTo("NOT RECORDING (waiting for an output device)"));
        Assert.That(both.Attention.Count, Is.EqualTo(2));
    }

    // ------------------------------------------------------------------ lengths in words

    /// <summary>
    /// A length of up to two minutes is given in seconds; a longer one in whole minutes, and from an hour on in hours and
    /// minutes, so that it reads at a glance. The silence warning, the warning that audio has stopped arriving, the pause
    /// reminders and the free space all give their lengths so.
    /// </summary>
    [TestCase(1, "1 second")]
    [TestCase(120, "120 seconds")]
    [TestCase(121, "2 minutes")]
    [TestCase(179, "2 minutes")]
    [TestCase(3599, "59 minutes")]
    [TestCase(3600, "1 hour")]
    [TestCase(3660, "1 hour 1 minute")]
    [TestCase(9006, "2 hours 30 minutes")]
    [TestCase(90000, "25 hours")]
    public void A_length_is_given_in_units_that_read_at_a_glance(int seconds, string length) =>
        Assert.That(RecordingMonitor.DescribeLength(TimeSpan.FromSeconds(seconds)), Is.EqualTo(length));
}
