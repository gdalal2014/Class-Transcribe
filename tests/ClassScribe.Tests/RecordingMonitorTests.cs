using System.Globalization;
using ClassScribe.Audio;
using ClassScribe.Audio.Microphones;
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

    /// <summary>The source the tests' tracks record unless a test names another: the speakers, which the room hears.</summary>
    private const string Speakers = "Fake Speakers";

    /// <summary>Headphones, which the room does not hear.</summary>
    private const string Headphones = "Headphones (Pixel Buds Pro 2)";

    private ManualTimeProvider _time = null!;
    private FakeSystemTimes _times = null!;
    private FakeFreeSpace _space = null!;
    private FakeRegistryValues _registry = null!;
    private FakeCaptureProvider _outputs = null!;
    private FakeMicrophoneSessions _sessions = null!;
    private FakeMicrophoneProvider _microphones = null!;
    private RecordingMonitor _monitor = null!;
    private MonitorContext _context = null!;

    /// <summary>
    /// The clock at the tests' class, the computer's clocks read from it, room for any class on the disk, a registry with
    /// Vantage off and ducking set to Do nothing, the output devices (the speakers, heard in the room, and headphones), and
    /// the microphones (the laptop's array and the Pixel Buds), with no app on any of them.
    /// </summary>
    [SetUp]
    public void SetUp()
    {
        _time = new ManualTimeProvider(TestRuns.Start);
        _times = new FakeSystemTimes(_time);
        _space = new FakeFreeSpace();
        _registry = new FakeRegistryValues()
            .Set(MicrophoneAdvice.VantageKey, MicrophoneAdvice.VantageValue, "0")
            .Set(MicrophoneAdvice.DuckingKey, MicrophoneAdvice.DuckingValue, 3);
        _outputs = new FakeCaptureProvider();
        _outputs.ConnectDevice(Speakers, heardInTheRoom: true);
        _outputs.ConnectDevice(Headphones);
        _sessions = new FakeMicrophoneSessions();
        _microphones = new FakeMicrophoneProvider().Connect(LaptopMicrophones.Array, LaptopMicrophones.PixelBuds);
    }

    /// <summary>
    /// Makes the monitor with <paramref name="options"/> (the defaults unless given) for <paramref name="request"/> (the
    /// tests' request unless given), as the recording loop starts it now; <paramref name="microphone"/> is what the
    /// recording says about your microphone beyond its snapshot, when it has one.
    /// </summary>
    private void Begin(RecordingOptions? options = null, RecordRequest? request = null, IMicrophoneRecording? microphone = null)
    {
        var bluetooth = new BluetoothMicrophoneWatch(_sessions, _microphones, new FakeOwnProcesses(Laptop.OwnProcess), _time, NullLogger<BluetoothMicrophoneWatch>.Instance);
        _monitor = new RecordingMonitor(new FreezeWatch(_times, _time, NullLogger<FreezeWatch>.Instance), _space, TestOptions.Create(options ?? new RecordingOptions()),
            new MicrophoneAdvice(_registry, NullLogger<MicrophoneAdvice>.Instance), _outputs, bluetooth, _time);
        _context = new MonitorContext(request ?? TestRuns.Request(TestRuns.Id()), Folder, _time.GetUtcNow(), _time.GetTimestamp(), TimeSpan.Zero, microphone);
    }

    // ------------------------------------------------------------------ the class app on a Bluetooth microphone

    /// <summary>The warning for chrome on the Pixel Buds, with the laptop's array named as the fix; worded for any Bluetooth headset or earbuds, since the watch counts them all.</summary>
    private static readonly string ChromeOnBuds =
        $"chrome is taking sound from the Bluetooth microphone \"{LaptopMicrophones.PixelBuds.Name}\": that puts the headset or earbuds into call mode, and the class's sound may be going to them " +
        $"in a way Windows cannot record, so this recording may be silent. In chrome, set the microphone to the laptop's built-in microphone (\"{LaptopMicrophones.Array.Name}\"), or to another that is not Bluetooth";

    /// <summary>Puts chrome's capture session on the Pixel Buds, taking sound.</summary>
    private void ChromeListensOnTheBuds() => _sessions.SetSessions(LaptopMicrophones.PixelBuds.Id, Laptop.Session(LaptopMicrophones.PixelBuds.Id, Laptop.ChromeUtility));

    /// <summary>
    /// With the class recorded from a device and no app followed, any app taking sound from a Bluetooth microphone is warned
    /// about at the first look, naming the app, the earbuds and the fix, and is a Notice while it lasts; the warning is not
    /// repeated while nothing changes, and when the app lets go, the next look (every 10 seconds) says so and the Notice ends.
    /// </summary>
    [Test]
    public void The_class_app_on_a_Bluetooth_microphone_is_warned_about_once_and_until_it_lets_go()
    {
        ChromeListensOnTheBuds();
        Begin(new RecordingOptions { StatusIntervalSeconds = 3600 });

        var first = Look(Recording());
        var meanwhile = Run(9, _ => Recording());
        _sessions.SetSessions(LaptopMicrophones.PixelBuds.Id);
        var tenth = Tick(Recording());

        Assert.That(first.Lines, Is.EqualTo([(LogLevel.Warning, ChromeOnBuds)]));
        Assert.That((first.Attention.Level, first.Attention.Short, first.Attention.Text, first.Attention.Count), Is.EqualTo((AttentionLevel.Notice, "chrome on a Bluetooth microphone", ChromeOnBuds, 1)));
        Assert.That(Texts(meanwhile), Is.Empty, "said once");
        Assert.That(meanwhile.Select(tick => tick.Attention.Level), Has.All.EqualTo(AttentionLevel.Notice), "a Notice while it lasts");
        Assert.That(tenth.Lines, Is.EqualTo([(LogLevel.Information, $"chrome has let go of the Bluetooth microphone \"{LaptopMicrophones.PixelBuds.Name}\"")]));
        Assert.That(tenth.Attention.Level, Is.EqualTo(AttentionLevel.None));
    }

    /// <summary>An app that takes a Bluetooth microphone mid-class is warned about at the look that finds it, within 10 seconds.</summary>
    [Test]
    public void An_app_that_takes_a_Bluetooth_microphone_mid_class_is_warned_about_within_10_seconds()
    {
        Begin(new RecordingOptions { StatusIntervalSeconds = 3600 });
        var start = Look(Recording());
        ChromeListensOnTheBuds();

        var ticks = Run(10, _ => Recording());

        Assert.That(start.Lines, Is.Empty);
        Assert.That(Texts(ticks), Is.EqualTo([ChromeOnBuds]));
        Assert.That(ticks[^1].Attention.Short, Is.EqualTo("chrome on a Bluetooth microphone"));
    }

    /// <summary>In app mode the app recorded is the one that counts: another app on the earbuds is nothing, the recorded app is warned about.</summary>
    [Test]
    public void In_app_mode_the_app_recorded_is_the_one_watched()
    {
        _sessions.SetSessions(LaptopMicrophones.PixelBuds.Id, Laptop.Session(LaptopMicrophones.PixelBuds.Id, Laptop.Vibe));
        Begin(new RecordingOptions { StatusIntervalSeconds = 3600 }, RequestFor(new ClassSource.App("chrome")));
        var vibe = Look(App(0));
        ChromeListensOnTheBuds();
        var chrome = Run(10, _ => App(0));

        Assert.That(vibe.Lines, Is.Empty);
        Assert.That(Texts(chrome), Is.EqualTo([ChromeOnBuds]));
    }

    /// <summary>With the class from a device and the microphone following chrome, chrome is the app watched, by the request's plan.</summary>
    [Test]
    public void With_the_microphone_following_an_app_that_app_is_the_one_watched()
    {
        _sessions.SetSessions(LaptopMicrophones.PixelBuds.Id, Laptop.Session(LaptopMicrophones.PixelBuds.Id, Laptop.Vibe));
        var request = TestRuns.Request(TestRuns.Id()) with
        {
            Source = new SourcePlan(RecordingMode.ClassAndMicrophone, new ClassSource.Automatic(), new MicrophoneChoice(null, new FollowRule.Named("chrome")))
        };
        Begin(new RecordingOptions { StatusIntervalSeconds = 3600 }, request);

        var vibe = Look(Recording());
        ChromeListensOnTheBuds();
        var chrome = Run(10, _ => Recording());

        Assert.That(Texts(vibe), Has.None.Contains("Bluetooth"));
        Assert.That(Texts(chrome), Is.EqualTo([ChromeOnBuds]));
    }

    /// <summary>In person there is no class to lose to the earbuds, so nothing is watched or said.</summary>
    [Test]
    public void In_person_nothing_is_said_about_a_Bluetooth_microphone()
    {
        ChromeListensOnTheBuds();
        var request = TestRuns.Request(TestRuns.Id()) with { Source = new SourcePlan(RecordingMode.Microphone, Class: null, new MicrophoneChoice(null)) };
        Begin(new RecordingOptions { StatusIntervalSeconds = 3600 }, request);

        var first = Look(Recording(FakeRecording.Track("Microphone Array", deliversContinuously: true)));

        Assert.That(Texts(first), Has.None.Contains("Bluetooth"));
        Assert.That(_sessions.ReadAlls, Is.EqualTo(0));
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

    // ------------------------------------------------------------------ the microphone: the lines at the start

    private const string ClassPrivacy = "Your microphone is recorded too, and uploaded to ElevenLabs with the class.";
    private const string RoomPrivacy = "Your microphone records the room, and the recording is uploaded to ElevenLabs. Ask before recording others.";
    private const string VisibleCase = "Your voice is recorded while chrome is listening: when you mute in chrome, ClassScribe stops too. V switches it off by hand.";
    private const string InvisibleCase = "chrome keeps the microphone open while you are muted, so ClassScribe cannot see your mute. Your voice is recorded only after you press V, and only while chrome holds the microphone. Muting the microphone in Windows (the microphone key) stops it too.";
    private const string UnknownCase = "ClassScribe has not been told whether chrome lets go of the microphone when you mute (most class apps and pages keep it open), so it cannot follow your mute yet: your voice is recorded only after you press V, and only while chrome holds the microphone. Muting the microphone in Windows (the microphone key) stops it too. Run the microphone check (menu choice 8) during a class, or set Recording:ClassAppMute.";
    private const string VAlone = "V switches it off and on; muting yourself in the class app does not.";
    private const string FollowingChrome = "Following: chrome (Recording:MicrophoneFollows)";
    private const string ArrayRaw = "Microphone: \"Microphone Array (Intel® Smart Sound Technology for Digital Microphones)\" (Windows' default microphone), without Windows' sound processing (RAW)";
    private const string VantageOn = "Lenovo Vantage's microphone noise cancelling is on (mode 1, probably \"Multiple Voices\"); it can cut out a lecturer across the room. Set Vantage > Device Settings > Sound > Input to Off.";
    private const string Ducking80 = "Windows lowers other sounds by 80% while a microphone is open, which can make the class quieter, in the recording too. Set Sound > More sound settings > Communications to Do nothing.";
    private const string EchoOnSpeakers = "The class plays on \"Fake Speakers\", so the microphone records it a second time (an echo). Use headphones.";
    private const string BluetoothBuds = "The microphone \"Headset (2- Pixel Buds Pro 2)\" is Bluetooth: using it switches the earbuds to call quality, for the class too. The laptop's microphone array avoids that.";
    private const string InPersonAdvice = "Plug in and keep the lid open; the screen stays on while on battery. For a break press P; V switches the microphone off but keeps the clock running, and that silence is uploaded.";
    private const string StatusOnHeadphones = "Recording: 00:00:42 captured from \"Headphones (Pixel Buds Pro 2)\"";

    /// <summary>A request to record the class from <paramref name="source"/> (the device it plays on unless given) and your microphone, following what the settings say unless <paramref name="chosen"/> names a rule for the run.</summary>
    private static RecordRequest WithMicrophone(ClassSource? source = null, FollowRule? chosen = null, string? namePart = null) =>
        TestRuns.Request(TestRuns.Id()) with { Source = new SourcePlan(RecordingMode.ClassAndMicrophone, source ?? new ClassSource.Automatic(), new MicrophoneChoice(namePart, chosen)) };

    /// <summary>A request to record your microphone alone, in person.</summary>
    private static RecordRequest InPerson() =>
        TestRuns.Request(TestRuns.Id()) with { Source = new SourcePlan(RecordingMode.Microphone, Class: null, new MicrophoneChoice(null)) };

    /// <summary>The microphone track: the array, capturing and handing over audio all the time, unless a test says otherwise.</summary>
    private static TrackSnapshot MicrophoneTrack(bool capturing = true, bool off = false, bool onlyZeros = false, string? waitingFor = null) =>
        FakeRecording.Track(LaptopMicrophones.Array.Name, waitingFor: waitingFor, deliversContinuously: true, capturing: capturing, off: off, onlyZeros: onlyZeros);

    /// <summary>A recording of the class, from the headphones unless <paramref name="main"/> says otherwise, and your microphone, the array unless <paramref name="microphone"/> says otherwise, 42 seconds in.</summary>
    private static RecordingSnapshot WithTracks(TrackSnapshot? main = null, TrackSnapshot? microphone = null, int seconds = 42, bool paused = false) =>
        new(TimeSpan.FromSeconds(seconds), paused, main ?? FakeRecording.Track(Headphones), microphone ?? MicrophoneTrack());

    /// <summary>A recording of your microphone alone, in person, 42 seconds in.</summary>
    private static RecordingSnapshot InPersonTracks(TrackSnapshot? main = null) =>
        new(TimeSpan.FromSeconds(42), false, main ?? MicrophoneTrack(), Microphone: null);

    /// <summary>
    /// Begins with the class and your microphone, the status line off unless <paramref name="options"/> say otherwise:
    /// the recording follows <paramref name="rule"/> (chrome unless given) through a gate for an app whose mute is
    /// <paramref name="appMute"/>, as the settings say too, and the class comes from <paramref name="source"/>. Returns
    /// the recording, whose gate the test feeds looks and whose microphone it changes.
    /// </summary>
    private FakeMicrophoneRecording BeginWithMicrophone(ClassAppMute appMute = ClassAppMute.Visible, FollowRule? rule = null, ClassSource? source = null,
        RecordingOptions? options = null, FollowRule? chosen = null)
    {
        var recording = new FakeMicrophoneRecording();
        recording.Follow(rule ?? new FollowRule.Named("chrome"), appMute, _time);
        Begin(options ?? new RecordingOptions { StatusIntervalSeconds = 3600, ClassAppMute = appMute }, WithMicrophone(source, chosen), recording);
        return recording;
    }

    /// <summary>Hands the gate a look made now, as the watch does: <paramref name="look"/> makes it for the clock's moment.</summary>
    private static WatchLook Feed(MicrophoneGate gate, ManualTimeProvider time, Func<TimeSpan, WatchLook> look)
    {
        var made = look(TimeSpan.FromTicks(time.GetTimestamp()));
        gate.Record(made);
        return made;
    }

    /// <summary>Hands the test's gate a look made now.</summary>
    private void Feed(MicrophoneGate gate, Func<TimeSpan, WatchLook> look) => Feed(gate, _time, look);

    /// <summary>The texts of the lines that are warnings, in order.</summary>
    private static string[] Warnings(MonitorTick tick) => [.. tick.Lines.Where(line => line.Level == LogLevel.Warning).Select(line => line.Text)];

    /// <summary>The texts of every look's lines that are warnings, in order.</summary>
    private static string[] Warnings(IEnumerable<MonitorTick> ticks) => [.. ticks.SelectMany(Warnings)];

    /// <summary>
    /// With the class and your microphone, the first look gives the privacy line, the case line for an app whose mute
    /// Windows can see, which app is followed and from where, and which microphone with how it is recorded; nothing
    /// needs attention when the class plays on headphones and the settings are right.
    /// </summary>
    [Test]
    public void With_the_class_the_first_look_gives_the_privacy_the_case_the_following_and_the_microphone_lines()
    {
        BeginWithMicrophone(ClassAppMute.Visible);

        var first = Look(WithTracks());
        var second = Tick(WithTracks());

        Assert.That(first.Lines, Is.EqualTo(new[]
        {
            (LogLevel.Information, ClassPrivacy),
            (LogLevel.Information, VisibleCase),
            (LogLevel.Information, FollowingChrome),
            (LogLevel.Information, ArrayRaw)
        }));
        Assert.That(first.Attention, Is.EqualTo(Attention.None));
        Assert.That(Texts(second), Is.Empty, "the start lines come once");
    }

    /// <summary>
    /// Each warning at the start is logged as one, in the advice's order after the lines about what is recorded, and the
    /// warnings are a Notice in the title, which says how many, for five minutes.
    /// </summary>
    [Test]
    public void Each_warning_at_the_start_is_logged_and_the_warnings_are_a_Notice_for_5_minutes()
    {
        _registry.Set(MicrophoneAdvice.VantageKey, MicrophoneAdvice.VantageValue, "1");
        _registry.Remove(MicrophoneAdvice.DuckingKey, MicrophoneAdvice.DuckingValue);
        BeginWithMicrophone(ClassAppMute.Unknown);

        var first = Look(WithTracks(main: FakeRecording.Track(Speakers)));
        var during = Run(299, _ => WithTracks(main: FakeRecording.Track(Speakers)));
        var after = Tick(WithTracks(main: FakeRecording.Track(Speakers)));

        Assert.That(first.Lines, Is.EqualTo(new[]
        {
            (LogLevel.Information, ClassPrivacy),
            (LogLevel.Information, UnknownCase),
            (LogLevel.Information, FollowingChrome),
            (LogLevel.Information, ArrayRaw),
            (LogLevel.Warning, VantageOn),
            (LogLevel.Warning, Ducking80),
            (LogLevel.Warning, EchoOnSpeakers)
        }));
        Assert.That(first.Attention, Is.EqualTo(new Attention(AttentionLevel.Notice, "3 warnings at the start", $"{VantageOn} {Ducking80} {EchoOnSpeakers}", 1)));
        Assert.That(during.Select(tick => tick.Attention.Level), Has.All.EqualTo(AttentionLevel.Notice));
        Assert.That(after.Attention, Is.EqualTo(new Attention(AttentionLevel.None, "", "", 1)), "the Notice lasts 5 minutes; the log keeps the words");
        Assert.That(Texts(during.Append(after)), Is.Empty);
    }

    /// <summary>In app mode the followed app is the app being recorded, and the line says so.</summary>
    [Test]
    public void In_app_mode_the_following_line_names_the_app_being_recorded()
    {
        _outputs.AddApp(4242, "chrome", Headphones);
        BeginWithMicrophone(ClassAppMute.Visible, source: new ClassSource.App("chrome"));

        var first = Look(WithTracks(main: FakeRecording.Track("chrome", deliversContinuously: true)));

        Assert.That(Texts(first), Has.One.EqualTo("Following: chrome, the app being recorded"));
        Assert.That(Warnings(first), Is.Empty, "the app plays on headphones");
    }

    /// <summary>A rule chosen for the run, with --mic-follows or in the menu, is said to be so.</summary>
    [Test]
    public void A_rule_chosen_for_the_run_is_said_to_be()
    {
        BeginWithMicrophone(ClassAppMute.Visible, rule: new FollowRule.Named("zoom"), chosen: new FollowRule.Named("zoom"));

        Assert.That(Texts(Look(WithTracks())), Has.One.EqualTo("Following: zoom (chosen for this run)"));
    }

    /// <summary>Following any app that holds the microphone is said as such; following none gives no line, and the case line names V.</summary>
    [Test]
    public void Following_any_app_and_none_are_said_as_such()
    {
        BeginWithMicrophone(ClassAppMute.Visible, rule: new FollowRule.Any());
        var any = Look(WithTracks());

        BeginWithMicrophone(ClassAppMute.Visible, rule: new FollowRule.None());
        var none = Look(WithTracks());

        Assert.That(Texts(any), Has.One.EqualTo("Following: any app that holds the microphone (Recording:MicrophoneFollows auto)"));
        Assert.That(Texts(none), Has.None.StartsWith("Following:"));
        Assert.That(Texts(none)[1], Is.EqualTo(VAlone));
    }

    /// <summary>In person the lines are the room's privacy line, the microphone, and the advice about the battery; ducking and the echo do not apply.</summary>
    [Test]
    public void In_person_the_lines_are_the_room_the_microphone_and_the_battery_advice()
    {
        _registry.Remove(MicrophoneAdvice.DuckingKey, MicrophoneAdvice.DuckingValue);
        Begin(new RecordingOptions { StatusIntervalSeconds = 3600 }, InPerson(), new FakeMicrophoneRecording(RecordingMode.Microphone));

        var first = Look(InPersonTracks());

        Assert.That(first.Lines, Is.EqualTo(new[]
        {
            (LogLevel.Information, RoomPrivacy),
            (LogLevel.Information, ArrayRaw),
            (LogLevel.Information, InPersonAdvice)
        }));
        Assert.That(first.Attention, Is.EqualTo(Attention.None));
    }

    /// <summary>
    /// A recording that says nothing about its microphone beyond its snapshot (one without <see cref="IMicrophoneRecording"/>)
    /// still has the microphone it captures named at the start, with the processing the settings ask for, rather than
    /// said to be waited for; with no app followed, the case line names V, and nothing is said about a move.
    /// </summary>
    [Test]
    public void A_recording_that_says_no_more_than_its_snapshot_still_names_its_microphone_at_the_start()
    {
        Begin(new RecordingOptions { StatusIntervalSeconds = 3600 }, WithMicrophone(), microphone: null);

        var first = Look(WithTracks());
        var second = Tick(WithTracks());

        Assert.That(Texts(first), Is.EqualTo(new[] { ClassPrivacy, VAlone, "Microphone: \"Microphone Array (Intel® Smart Sound Technology for Digital Microphones)\", without Windows' sound processing (RAW)" }));
        Assert.That(first.Attention, Is.EqualTo(Attention.None));
        Assert.That(Texts(second), Is.Empty);
    }

    /// <summary>
    /// A track that starts switched off, as it does while the class app's mute cannot be seen, has no microphone recorded
    /// yet: the line names the one the recording would record, with why and what will be asked for, as switched off
    /// until V rather than waited for, and the warnings about it (Vantage, for the array) come at the start, not at V,
    /// which then opens the microphone already named and has nothing new to warn about.
    /// </summary>
    [Test]
    public void A_microphone_switched_off_at_the_start_is_named_and_warned_about()
    {
        _registry.Set(MicrophoneAdvice.VantageKey, MicrophoneAdvice.VantageValue, "1");
        var recording = BeginWithMicrophone(ClassAppMute.Unknown);
        recording.Microphone = null;
        recording.WouldRecord = LaptopMicrophones.Array;
        var off = WithTracks(microphone: MicrophoneTrack(capturing: false, off: true));

        var first = Look(off);
        var next = Run(31, _ => off);
        recording.Microphone = LaptopMicrophones.Array;
        var on = Tick(WithTracks());

        Assert.That(Texts(first), Is.EqualTo(new[]
        {
            ClassPrivacy,
            UnknownCase,
            FollowingChrome,
            $"Microphone: \"{LaptopMicrophones.Array.Name}\" (Windows' default microphone), switched off until you press V; RAW will be asked for",
            VantageOn
        }));
        Assert.That(first.Attention, Is.EqualTo(new Attention(AttentionLevel.Notice, "1 warning at the start", VantageOn, 1)));
        Assert.That(Texts(next), Is.Empty, "nothing is waited for while off");
        Assert.That(Texts(on), Is.Empty, "V opened the microphone named at the start: nothing new to warn about");
    }

    /// <summary>When Windows refused RAW and the capture opened with its sound processing, the microphone line says so: the processing the recording reports is the capture's, not the setting's.</summary>
    [Test]
    public void The_microphone_line_says_with_Windows_sound_processing_when_the_capture_opened_so()
    {
        var recording = BeginWithMicrophone(ClassAppMute.Visible);
        recording.Processing = MicrophoneProcessing.Default;

        var first = Look(WithTracks());

        Assert.That(Texts(first), Does.Contain($"Microphone: \"{LaptopMicrophones.Array.Name}\" (Windows' default microphone), with Windows' sound processing"));
    }

    /// <summary>Without a microphone at the start the microphone line says what is waited for, and a warning says so every 30 seconds, with a Notice, until one is found.</summary>
    [Test]
    public void A_missing_microphone_with_the_class_is_said_every_30_seconds_and_is_a_Notice()
    {
        var recording = BeginWithMicrophone(ClassAppMute.Visible);
        recording.Microphone = null;
        var missing = MicrophoneTrack(capturing: false, waitingFor: "the microphone \"OBSBOT\"");
        const string waiting = "Waiting for the microphone \"OBSBOT\"; the class is being recorded";

        var first = Look(WithTracks(microphone: missing));
        var next = Run(29, _ => WithTracks(microphone: missing));
        var thirty = Tick(WithTracks(microphone: missing));
        recording.Microphone = LaptopMicrophones.Obsbot;
        var found = Tick(WithTracks(microphone: FakeRecording.Track(LaptopMicrophones.Obsbot.Name, deliversContinuously: true)));

        Assert.That(Texts(first), Is.EqualTo(new[] { ClassPrivacy, VisibleCase, FollowingChrome, "Microphone: waiting for the microphone \"OBSBOT\"", waiting }));
        Assert.That(first.Attention, Is.EqualTo(new Attention(AttentionLevel.Notice, "no microphone", waiting, 1)));
        Assert.That(Texts(next), Is.Empty);
        Assert.That(Texts(thirty), Is.EqualTo(new[] { waiting }));
        Assert.That(found.Attention.Level, Is.EqualTo(AttentionLevel.None));
        Assert.That(Warnings(found), Is.Empty, "the OBSBOT is neither Bluetooth nor the array");
    }

    // ------------------------------------------------------------------ the microphone: Vantage, the echo, a move

    /// <summary>Vantage's mode is read again every 30 seconds: a change to on is a warning and a Notice; a change to off is said plainly and clears the Notice.</summary>
    [Test]
    public void Vantage_is_read_again_every_30_seconds_and_a_change_is_noticed()
    {
        BeginWithMicrophone(ClassAppMute.Visible);
        Look(WithTracks());
        _registry.Set(MicrophoneAdvice.VantageKey, MicrophoneAdvice.VantageValue, "4");
        var before = Run(29, _ => WithTracks());
        var thirty = Tick(WithTracks());
        _registry.Set(MicrophoneAdvice.VantageKey, MicrophoneAdvice.VantageValue, "0");
        var sixty = Run(30, _ => WithTracks())[^1];

        const string switchedOn = "Lenovo Vantage's microphone noise cancelling has switched on (mode 4, probably \"Only My Voice\"); it can cut out a lecturer across the room. Set Vantage > Device Settings > Sound > Input to Off.";
        Assert.That(Texts(before), Is.Empty, "not before 30 seconds");
        Assert.That(thirty.Lines, Is.EqualTo(new[] { (LogLevel.Warning, switchedOn) }));
        Assert.That(thirty.Attention, Is.EqualTo(new Attention(AttentionLevel.Notice, "Vantage noise cancelling on", switchedOn, 1)));
        Assert.That(sixty.Lines, Is.EqualTo(new[] { (LogLevel.Information, "Lenovo Vantage's microphone noise cancelling is now off.") }));
        Assert.That(sixty.Attention.Level, Is.EqualTo(AttentionLevel.None));
    }

    /// <summary>The echo is checked again whenever the class moves to another device: a warning and a Notice on the speakers, nothing on headphones.</summary>
    [Test]
    public void The_echo_is_checked_again_when_the_class_moves_to_another_device()
    {
        BeginWithMicrophone(ClassAppMute.Visible);
        var start = Look(WithTracks());
        var onSpeakers = Tick(WithTracks(main: FakeRecording.Track(Speakers)));
        var still = Tick(WithTracks(main: FakeRecording.Track(Speakers)));
        var back = Tick(WithTracks());

        Assert.That(Warnings(start), Is.Empty);
        Assert.That(onSpeakers.Lines, Is.EqualTo(new[] { (LogLevel.Warning, EchoOnSpeakers) }));
        Assert.That(onSpeakers.Attention, Is.EqualTo(new Attention(AttentionLevel.Notice, "echo: use headphones", EchoOnSpeakers, 1)));
        Assert.That(Texts(still), Is.Empty, "said once per move");
        Assert.That(Texts(back), Is.Empty);
        Assert.That(back.Attention.Level, Is.EqualTo(AttentionLevel.None), "headphones end the echo");
    }

    /// <summary>In app mode the class's source does not change when the app moves to another device, so the app's device is looked at every 30 seconds.</summary>
    [Test]
    public void In_app_mode_the_apps_device_is_checked_every_30_seconds()
    {
        _outputs.AddApp(4242, "chrome", Headphones);
        BeginWithMicrophone(ClassAppMute.Visible, source: new ClassSource.App("chrome"));
        var chrome = WithTracks(main: FakeRecording.Track("chrome", deliversContinuously: true));
        var start = Look(chrome);
        _outputs.AddApp(4242, "chrome", Speakers);
        var before = Run(29, _ => chrome);
        var thirty = Tick(chrome);

        Assert.That(Warnings(start), Is.Empty);
        Assert.That(Texts(before), Is.Empty);
        Assert.That(thirty.Lines, Is.EqualTo(new[] { (LogLevel.Warning, EchoOnSpeakers) }));
        Assert.That(thirty.Attention.Short, Is.EqualTo("echo: use headphones"));
    }

    /// <summary>
    /// A look at the devices that fails, as while Windows' audio service restarts, costs the echo check alone: the other
    /// checks go on, the failure leaves a Debug line once per stretch of failures, and the check looks again at the next
    /// look, so that the echo is warned about once the list can be read.
    /// </summary>
    [Test]
    public void A_listing_that_fails_does_not_stop_the_checks()
    {
        BeginWithMicrophone(ClassAppMute.Visible, options: new RecordingOptions { StatusIntervalSeconds = 1 });
        Look(WithTracks());
        _outputs.FailListings(new InvalidOperationException("The audio service is not running."));
        var moved = Tick(WithTracks(main: FakeRecording.Track(Speakers)));
        var still = Tick(WithTracks(main: FakeRecording.Track(Speakers)));
        _outputs.FailListings(null);
        var read = Tick(WithTracks(main: FakeRecording.Track(Speakers)));
        _outputs.FailListings(new InvalidOperationException("The audio service is not running."));
        var again = Tick(WithTracks());

        const string gateClause = "; microphone: following chrome, waiting for chrome to open the microphone";
        const string onSpeakers = "Recording: 00:00:42 captured from \"Fake Speakers\"" + gateClause;
        const string failed = "The echo check could not read Windows' device list, so it looks again at the next second: The audio service is not running.";
        Assert.That(moved.Lines, Is.EqualTo(new[] { (LogLevel.Debug, failed), (LogLevel.Information, onSpeakers) }));
        Assert.That(still.Lines, Is.EqualTo(new[] { (LogLevel.Information, onSpeakers) }), "noted once per stretch of failures");
        Assert.That(read.Lines, Is.EqualTo(new[] { (LogLevel.Warning, EchoOnSpeakers), (LogLevel.Information, onSpeakers) }));
        Assert.That(again.Lines, Is.EqualTo(new[] { (LogLevel.Debug, failed), (LogLevel.Information, StatusOnHeadphones + gateClause) }), "a new stretch is noted again");
    }

    /// <summary>
    /// A move to another microphone is advised about as the start was: a Bluetooth one is warned about, with a Notice
    /// that says how many warnings; a move to one that needs no warning clears it; a move back to the array reads Vantage again.
    /// </summary>
    [Test]
    public void A_move_to_another_microphone_is_warned_about()
    {
        var recording = BeginWithMicrophone(ClassAppMute.Visible);
        Look(WithTracks());
        recording.Microphone = LaptopMicrophones.PixelBuds;
        var buds = Tick(WithTracks(microphone: FakeRecording.Track(LaptopMicrophones.PixelBuds.Name, deliversContinuously: true)));
        recording.Microphone = LaptopMicrophones.Obsbot;
        var obsbot = Tick(WithTracks(microphone: FakeRecording.Track(LaptopMicrophones.Obsbot.Name, deliversContinuously: true)));
        _registry.Set(MicrophoneAdvice.VantageKey, MicrophoneAdvice.VantageValue, "1");
        recording.Microphone = LaptopMicrophones.Array;
        var array = Tick(WithTracks());

        Assert.That(buds.Lines, Is.EqualTo(new[] { (LogLevel.Warning, BluetoothBuds) }));
        Assert.That(buds.Attention, Is.EqualTo(new Attention(AttentionLevel.Notice, "1 warning about the microphone", BluetoothBuds, 1)));
        Assert.That(Texts(obsbot), Is.Empty);
        Assert.That(obsbot.Attention.Level, Is.EqualTo(AttentionLevel.None));
        Assert.That(array.Lines, Is.EqualTo(new[] { (LogLevel.Warning, VantageOn) }));
    }

    // ------------------------------------------------------------------ the microphone: the gate in the status line and the log

    /// <summary>The status line names both sources: the class, and your microphone with the gate's words for its state.</summary>
    [Test]
    public void The_status_line_names_both_sources()
    {
        var recording = BeginWithMicrophone(ClassAppMute.Visible, options: new RecordingOptions { StatusIntervalSeconds = 1 });
        var gate = recording.Gate!;
        Look(WithTracks());

        Feed(gate, at => Looks.Open(at));
        var open = Tick(WithTracks());
        Feed(gate, at => Looks.NotListening(at));
        var notListening = Tick(WithTracks());
        Feed(gate, at => Looks.MutedInWindows(at));
        var mutedInWindows = Tick(WithTracks());
        Feed(gate, at => Looks.NotOpened(at));
        var notOpened = Tick(WithTracks());
        Feed(gate, at => Looks.OtherMicrophone(at));
        var other = Tick(WithTracks());
        Feed(gate, at => Looks.Unknown(at));
        Tick(WithTracks());
        var unreadable = Tick(WithTracks());
        gate.Arm(false);
        var off = Tick(WithTracks(microphone: MicrophoneTrack(off: true)));
        gate.Arm(true);
        var missing = Tick(WithTracks(microphone: MicrophoneTrack(capturing: false, waitingFor: "the microphone \"OBSBOT\"")));

        Assert.That(Texts(open), Is.EqualTo(new[] { $"{StatusOnHeadphones}; microphone: following chrome, recording" }));
        Assert.That(Texts(notListening), Does.Contain($"{StatusOnHeadphones}; microphone: following chrome, muted (chrome is not listening)"));
        Assert.That(Texts(mutedInWindows), Does.Contain($"{StatusOnHeadphones}; microphone: following chrome, muted in Windows"));
        Assert.That(Texts(notOpened), Does.Contain($"{StatusOnHeadphones}; microphone: following chrome, waiting for chrome to open the microphone"));
        Assert.That(Texts(other), Does.Contain($"{StatusOnHeadphones}; microphone: chrome uses \"{Laptop.ObsbotName}\"; ClassScribe records \"{Laptop.ArrayName}\""));
        Assert.That(Texts(unreadable), Does.Contain($"{StatusOnHeadphones}; microphone: following chrome, cannot read chrome's microphone session: sessions not readable: The audio service is not running (0x88890010)"));
        Assert.That(Texts(off), Does.Contain($"{StatusOnHeadphones}; microphone: off (V switches it on)"));
        Assert.That(Texts(missing), Does.Contain($"{StatusOnHeadphones}; microphone: waiting for the microphone \"OBSBOT\""));
    }

    /// <summary>With no app followed the status line names the microphone recorded; in person the microphone is the one source named.</summary>
    [Test]
    public void The_status_line_names_the_microphone_when_no_app_is_followed()
    {
        BeginWithMicrophone(ClassAppMute.Unknown, rule: new FollowRule.None(), options: new RecordingOptions { StatusIntervalSeconds = 1 });
        Look(WithTracks());
        var withClass = Tick(WithTracks());

        Begin(new RecordingOptions { StatusIntervalSeconds = 1 }, InPerson(), new FakeMicrophoneRecording(RecordingMode.Microphone));
        Look(InPersonTracks());
        var inPerson = Tick(InPersonTracks());

        Assert.That(Texts(withClass), Is.EqualTo(new[] { $"{StatusOnHeadphones}; microphone: recording \"{Laptop.ArrayName}\"" }));
        Assert.That(Texts(inPerson), Is.EqualTo(new[] { $"Recording: 00:00:42 captured from \"{Laptop.ArrayName}\"" }));
    }

    /// <summary>When the app's mute cannot be seen the microphone starts off, and the status line says what V does.</summary>
    [Test]
    public void When_the_apps_mute_cannot_be_seen_the_status_line_says_what_V_does()
    {
        BeginWithMicrophone(ClassAppMute.Unknown, options: new RecordingOptions { StatusIntervalSeconds = 1, ClassAppMute = ClassAppMute.Unknown });
        Look(WithTracks(microphone: MicrophoneTrack(off: true)));

        var off = Tick(WithTracks(microphone: MicrophoneTrack(off: true)));

        Assert.That(Texts(off), Is.EqualTo(new[] { $"{StatusOnHeadphones}; microphone: off (V records your voice while chrome holds the microphone)" }));
    }

    /// <summary>A state of the gate that has held for a second is logged once, with the position it began at; one that did not hold is not.</summary>
    [Test]
    public void A_gate_state_held_for_a_second_is_logged_once_with_its_position()
    {
        var recording = BeginWithMicrophone(ClassAppMute.Visible);
        var gate = recording.Gate!;
        Look(WithTracks(seconds: 0));

        Feed(gate, at => Looks.Open(at));
        var one = Tick(WithTracks(seconds: 1));
        Feed(gate, at => Looks.Open(at));
        var two = Tick(WithTracks(seconds: 2));
        Feed(gate, at => Looks.NotListening(at));
        var three = Tick(WithTracks(seconds: 3));
        Feed(gate, at => Looks.Open(at));
        var four = Tick(WithTracks(seconds: 4));
        Feed(gate, at => Looks.Open(at));
        var five = Tick(WithTracks(seconds: 5));
        Feed(gate, at => Looks.NotListening(at));
        var six = Tick(WithTracks(seconds: 6));
        Feed(gate, at => Looks.NotListening(at));
        var seven = Tick(WithTracks(seconds: 7));

        Assert.That(Texts(one), Is.Empty, "not before the state has held for a second");
        Assert.That(two.Lines, Is.EqualTo(new[] { (LogLevel.Information, "Microphone: chrome is listening; your voice is being recorded from 00:00:01") }));
        Assert.That(Texts(new[] { three, four, five }), Is.Empty, "a change that reverts within a second is not logged");
        Assert.That(Texts(six), Is.Empty);
        Assert.That(seven.Lines, Is.EqualTo(new[] { (LogLevel.Information, "Microphone: chrome is not listening; your voice is not recorded from 00:00:06") }));
    }

    /// <summary>The app not having opened the microphone for 5 minutes is said once and is a Notice while it lasts.</summary>
    [Test]
    public void The_app_not_opening_the_microphone_for_5_minutes_is_a_Notice()
    {
        var recording = BeginWithMicrophone(ClassAppMute.Visible);
        var gate = recording.Gate!;
        Look(WithTracks());

        var before = Run(299, _ => { Feed(gate, at => Looks.NotOpened(at)); return WithTracks(); });
        Feed(gate, at => Looks.NotOpened(at));
        var fiveMinutes = Tick(WithTracks());
        Feed(gate, at => Looks.NotOpened(at));
        var after = Tick(WithTracks());
        Feed(gate, at => Looks.Open(at));
        var opened = Tick(WithTracks());

        const string line = "chrome has not opened the microphone for 5 minutes; your voice is not recorded until it does. Join the class, or check that the page may use the microphone";
        Assert.That(Warnings(before.Skip(1)), Is.Empty);
        Assert.That(before.Select(tick => tick.Attention.Level), Has.All.EqualTo(AttentionLevel.None));
        Assert.That(fiveMinutes.Lines, Does.Contain((LogLevel.Warning, line)));
        Assert.That(fiveMinutes.Attention, Is.EqualTo(new Attention(AttentionLevel.Notice, "chrome has not opened the microphone", line, 1)));
        Assert.That(Warnings(after), Is.Empty, "said once");
        Assert.That(after.Attention.Level, Is.EqualTo(AttentionLevel.Notice));
        Assert.That(opened.Attention.Level, Is.EqualTo(AttentionLevel.None));
    }

    /// <summary>
    /// Sessions that cannot be read for 10 seconds are an Alert, said once, which outranks the Notice for a silent class;
    /// the first unreadable look is skipped for a second (see <see cref="MicrophoneGate.UnreadableAfter"/>), and the
    /// condition counts from the look before it was first seen.
    /// </summary>
    [Test]
    public void Sessions_unreadable_for_10_seconds_are_an_Alert()
    {
        var recording = BeginWithMicrophone(ClassAppMute.Visible);
        var gate = recording.Gate!;
        Look(WithTracks());
        RecordingSnapshot SilentClass(int seconds) => WithTracks(main: FakeRecording.Track(Headphones, sinceLastSound: TimeSpan.FromSeconds(seconds)));

        var silent = Run(60, seconds => { Feed(gate, at => Looks.Open(at)); return SilentClass(seconds); })[^1];
        var before = Run(10, seconds => { Feed(gate, at => Looks.Unknown(at)); return SilentClass(seconds); });
        Feed(gate, at => Looks.Unknown(at));
        var ten = Tick(SilentClass(71));
        Feed(gate, at => Looks.Open(at));
        var readable = Tick(WithTracks());

        const string line = "Cannot read chrome's microphone session: sessions not readable: The audio service is not running (0x88890010); your voice is not recorded until the sessions can be read again";
        Assert.That(silent.Attention.Level, Is.EqualTo(AttentionLevel.Notice), "the silent class");
        Assert.That(before.Select(tick => tick.Attention.Level), Has.All.EqualTo(AttentionLevel.Notice), "the silent class still, until 10 seconds");
        Assert.That(Warnings(before), Is.Empty);
        Assert.That(ten.Lines, Does.Contain((LogLevel.Warning, line)));
        Assert.That(ten.Attention.Level, Is.EqualTo(AttentionLevel.Alert));
        Assert.That(ten.Attention.Short, Is.EqualTo("CANNOT READ THE MICROPHONE SESSIONS"));
        Assert.That(ten.Attention.Text, Is.EqualTo(line));
        Assert.That(readable.Attention.Level, Is.EqualTo(AttentionLevel.None));
    }

    /// <summary>The evidence that the app's mute is visible, reported once by the gate, is a warning and a Notice for five minutes.</summary>
    [Test]
    public void The_evidence_of_a_visible_mute_is_a_warning_and_a_Notice()
    {
        var recording = BeginWithMicrophone(ClassAppMute.Unknown);
        var gate = recording.Gate!;
        using var started = _monitor.Start(_context);
        Look(WithTracks());
        Feed(gate, at => Looks.Open(at));
        Tick(WithTracks());
        Feed(gate, at => Looks.NotListening(at));
        Tick(WithTracks());
        Feed(gate, at => Looks.NotListening(at));
        Tick(WithTracks());
        Feed(gate, at => Looks.Open(at));

        var evidence = Tick(WithTracks());
        var later = Run(300, _ => WithTracks())[^1];

        var line = Warnings(evidence).Single();
        Assert.That(line, Does.StartWith("chrome let go of the microphone at ").And.EndWith("set Recording:ClassAppMute to Visible and ClassScribe will follow it by itself."));
        Assert.That(evidence.Attention, Is.EqualTo(new Attention(AttentionLevel.Notice, "chrome let go of the microphone (see the log)", line, 1)));
        Assert.That(later.Attention.Level, Is.EqualTo(AttentionLevel.None));
    }

    // ------------------------------------------------------------------ the microphone: only zeros, and the silence warning in person

    /// <summary>
    /// The recording counts the microphone's exact zeros itself and says so in its snapshot once they have lasted 2
    /// minutes (see <see cref="TrackSnapshot.OnlyZeros"/>): the look that first finds that, while the app listens and
    /// Windows is not muting, gives the warning, with that length in its words, and a Notice with the class; it is said
    /// once, and anything but zeros ends the Notice. The monitor does not wait another 2 minutes of its own.
    /// </summary>
    [Test]
    public void Only_zeros_with_the_class_are_said_at_the_look_that_finds_them_and_are_a_Notice()
    {
        var recording = BeginWithMicrophone(ClassAppMute.Visible);
        var gate = recording.Gate!;
        Look(WithTracks());

        var before = Run(5, _ => { Feed(gate, at => Looks.Open(at)); return WithTracks(); });
        Feed(gate, at => Looks.Open(at));
        var zeros = Tick(WithTracks(microphone: MicrophoneTrack(onlyZeros: true)));
        Feed(gate, at => Looks.Open(at));
        var still = Tick(WithTracks(microphone: MicrophoneTrack(onlyZeros: true)));
        Feed(gate, at => Looks.Open(at));
        var sound = Tick(WithTracks());

        const string line = "Only silence from the microphone \"Microphone Array (Intel® Smart Sound Technology for Digital Microphones)\" for 2 minutes although chrome is listening and Windows is not muting it. Check Settings > Privacy & security > Microphone (Let desktop apps access your microphone), and the microphone itself";
        Assert.That(Warnings(before), Is.Empty);
        Assert.That(zeros.Lines, Does.Contain((LogLevel.Warning, line)));
        Assert.That(zeros.Attention, Is.EqualTo(new Attention(AttentionLevel.Notice, "only silence from the microphone", line, 1)));
        Assert.That(Warnings(still), Is.Empty, "said once");
        Assert.That(still.Attention.Level, Is.EqualTo(AttentionLevel.Notice));
        Assert.That(sound.Attention.Level, Is.EqualTo(AttentionLevel.None));
    }

    /// <summary>
    /// In person the look that first finds the recording reporting only zeros gives the warning, with what to check, and
    /// an Alert: the whole recording is silence. Said once; the Alert lasts while the zeros do.
    /// </summary>
    [Test]
    public void Only_zeros_in_person_are_an_Alert_at_the_look_that_finds_them()
    {
        Begin(new RecordingOptions { StatusIntervalSeconds = 3600, SilenceWarningSeconds = 3600 }, InPerson(), new FakeMicrophoneRecording(RecordingMode.Microphone));
        Look(InPersonTracks());

        var before = Run(5, _ => InPersonTracks());
        var zeros = Tick(InPersonTracks(MicrophoneTrack(onlyZeros: true)));
        var still = Tick(InPersonTracks(MicrophoneTrack(onlyZeros: true)));

        const string line = "Only silence from the microphone \"Microphone Array (Intel® Smart Sound Technology for Digital Microphones)\" for 2 minutes. Check that it is not muted in Windows (the microphone key, or Sound settings > Input) and that Windows lets desktop apps use it (Settings > Privacy & security > Microphone)";
        Assert.That(before.Select(tick => tick.Attention.Level), Has.All.EqualTo(AttentionLevel.None));
        Assert.That(zeros.Lines, Is.EqualTo(new[] { (LogLevel.Warning, line) }));
        Assert.That(zeros.Attention, Is.EqualTo(new Attention(AttentionLevel.Alert, "ONLY SILENCE FROM THE MICROPHONE", line, 1)));
        Assert.That(Texts(still), Is.Empty, "said once");
        Assert.That(still.Attention.Level, Is.EqualTo(AttentionLevel.Alert));
    }

    /// <summary>
    /// In person, where no gate has read the mute, the recording is asked whether Windows mutes the microphone at the look
    /// that first finds only zeros: when it does, the line says so outright; when Windows says no, or cannot say, the line
    /// says what to check, as before. Asked once: the words stay while the zeros last, whatever Windows says later.
    /// </summary>
    [TestCase(true, "Only silence from the microphone \"Microphone Array (Intel® Smart Sound Technology for Digital Microphones)\" for 2 minutes: it is muted in Windows (the microphone key, or Sound settings > Input)")]
    [TestCase(false, "Only silence from the microphone \"Microphone Array (Intel® Smart Sound Technology for Digital Microphones)\" for 2 minutes. Check that it is not muted in Windows (the microphone key, or Sound settings > Input) and that Windows lets desktop apps use it (Settings > Privacy & security > Microphone)")]
    public void Only_zeros_in_person_say_muted_in_Windows_when_Windows_says_so(bool muted, string line)
    {
        var recording = new FakeMicrophoneRecording(RecordingMode.Microphone) { MutedInWindows = muted };
        Begin(new RecordingOptions { StatusIntervalSeconds = 3600, SilenceWarningSeconds = 3600 }, InPerson(), recording);
        Look(InPersonTracks());

        var zeros = Tick(InPersonTracks(MicrophoneTrack(onlyZeros: true)));
        recording.MutedInWindows = !muted;
        var still = Tick(InPersonTracks(MicrophoneTrack(onlyZeros: true)));

        Assert.That(zeros.Lines, Is.EqualTo(new[] { (LogLevel.Warning, line) }));
        Assert.That(zeros.Attention, Is.EqualTo(new Attention(AttentionLevel.Alert, "ONLY SILENCE FROM THE MICROPHONE", line, 1)));
        Assert.That(still.Attention.Text, Is.EqualTo(line), "asked once, at the look that found the zeros");
    }

    /// <summary>
    /// A microphone beside the class that hands over nothing for 5 seconds, although a microphone hands over audio all the
    /// time, is warned about and its capture reopened, and a Notice until audio flows again; switched off with V it has no
    /// capture to watch, and a stall from before is forgotten.
    /// </summary>
    [Test]
    public void A_microphone_beside_the_class_that_hands_over_nothing_for_5_seconds_is_reopened_and_a_Notice()
    {
        BeginWithMicrophone(ClassAppMute.Visible);
        Look(WithTracks());

        var before = Run(4, seconds => WithTracks(microphone: MicrophoneTrack() with { SinceLastPacket = TimeSpan.FromSeconds(seconds) }));
        var five = Tick(WithTracks(microphone: MicrophoneTrack() with { SinceLastPacket = TimeSpan.FromSeconds(5) }));
        var off = Tick(WithTracks(microphone: MicrophoneTrack(off: true)));

        const string line = "No audio has arrived from \"Microphone Array (Intel® Smart Sound Technology for Digital Microphones)\" for 5 seconds although it should; the recording has fallen behind the clock. Reopening it";
        Assert.That(Warnings(before), Is.Empty);
        Assert.That(before.Select(tick => tick.Reopen), Has.All.Null);
        Assert.That(five.Lines, Does.Contain((LogLevel.Warning, line)));
        Assert.That(five.Reopen, Is.EqualTo((Track.Microphone, "no audio has arrived for 5 seconds")));
        Assert.That(five.Attention, Is.EqualTo(new Attention(AttentionLevel.Notice, "no audio from the microphone", line, 1)));
        Assert.That(off.Attention.Level, Is.EqualTo(AttentionLevel.None), "switched off with V, the microphone has no capture to watch");
    }

    /// <summary>In person the silence warning names the microphone and says what to check about it.</summary>
    [Test]
    public void In_person_the_silence_warning_names_the_microphone()
    {
        Begin(new RecordingOptions { StatusIntervalSeconds = 3600 }, InPerson(), new FakeMicrophoneRecording(RecordingMode.Microphone));
        Look(InPersonTracks());

        var minute = Run(60, seconds => InPersonTracks(FakeRecording.Track(Laptop.ArrayName, sinceLastSound: TimeSpan.FromSeconds(seconds), deliversContinuously: true)))[^1];

        const string line = "No sound from the microphone \"Microphone Array (Intel® Smart Sound Technology for Digital Microphones)\" for 60 seconds. Check that it is not muted in Windows (the microphone key) and that Windows lets desktop apps use it (Settings > Privacy & security > Microphone)";
        Assert.That(minute.Lines, Is.EqualTo(new[] { (LogLevel.Warning, line) }));
        Assert.That(minute.Attention, Is.EqualTo(new Attention(AttentionLevel.Notice, "no sound 1 min", line, 1)));
    }
}
