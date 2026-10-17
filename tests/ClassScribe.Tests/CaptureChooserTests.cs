using System.Runtime.InteropServices;
using ClassScribe.Audio;
using ClassScribe.Commands;
using ClassScribe.Settings;

namespace ClassScribe.Tests;

/// <summary>
/// What gets recorded: the device the class is playing through, the device chosen by name, or the app chosen by name, as a
/// run's class source names it; the choice the menu and the settings make; and the loopback source, which hands the
/// session what the chooser picks, with the words for its messages.
/// </summary>
[TestFixture]
public class CaptureChooserTests
{
    private static readonly AudioFormat Pcm16KMono = new(16000, 1, 16, false);
    private static readonly OutputDevice Speakers = new("speakers", "Speakers (Realtek(R) Audio)", IsDefault: true, Level: 0);
    private static readonly OutputDevice Headphones = new("buds", "Headphones (Pixel Buds Pro 2)", IsDefault: false, Level: 0);
    private static readonly PlayingApp Chrome = new(4242, "chrome", "Speakers (Realtek(R) Audio)", Level: 0);
    private static readonly PlayingApp Edge = new(5151, "msedge", "Speakers (Realtek(R) Audio)", Level: 0);

    /// <summary>What a chooser says once the app it records has closed.</summary>
    private const string ChromeClosed = "\"chrome\" has closed; recording silence until it plays again";

    private ManualTimeProvider _time = null!;
    private RecordingLogger<CaptureChooser> _log = null!;
    private CaptureChooser _chooser = null!;

    /// <summary>Creates a chooser on a clock the test moves by hand, for the device the class plays on, with a log to inspect.</summary>
    [SetUp]
    public void SetUp()
    {
        _time = new ManualTimeProvider(new DateTimeOffset(2026, 10, 15, 19, 0, 0, TimeSpan.Zero));
        _log = new RecordingLogger<CaptureChooser>();
        _chooser = Chooser(new ClassSource.Automatic());
    }

    /// <summary>A chooser for <paramref name="source"/>, on the test's clock and logging to the test's log.</summary>
    private CaptureChooser Chooser(ClassSource source) => new(_time, source, _log);

    /// <summary>The device with a level, as Windows' meter would report it while it plays.</summary>
    private static OutputDevice Playing(OutputDevice device, float level = 0.2f) => device with { Level = level };

    /// <summary>The app with a level, as Windows' meter for its session would report it while it plays.</summary>
    private static PlayingApp Playing(PlayingApp app, float level = 0.2f) => app with { Level = level };

    /// <summary>At the start, the device with sound is taken; with none playing, the default one.</summary>
    [Test]
    public void Starts_on_the_device_with_sound_or_else_the_default_one()
    {
        Assert.That(_chooser.ChooseDevice([Speakers, Playing(Headphones)], recordedId: null, sinceRecordedSound: null), Is.EqualTo(Playing(Headphones)));
        Assert.That(_chooser.ChooseDevice([Speakers, Headphones], recordedId: null, sinceRecordedSound: null), Is.EqualTo(Speakers));
        Assert.That(_chooser.ChooseDevice([Headphones], recordedId: null, sinceRecordedSound: null), Is.EqualTo(Headphones), "with no default, any device beats none");
        Assert.That(_chooser.ChooseDevice([], recordedId: null, sinceRecordedSound: null), Is.Null);
    }

    /// <summary>A device that is playing is kept, however loud another one is.</summary>
    [Test]
    public void Keeps_the_recorded_device_while_it_is_playing()
    {
        var chosen = _chooser.ChooseDevice([Speakers, Playing(Headphones, 0.9f)], Speakers.Id, sinceRecordedSound: TimeSpan.FromSeconds(1));

        Assert.That(chosen, Is.EqualTo(Speakers));
    }

    /// <summary>Once the recorded device has been silent for a while and another has carried sound for a while, the recording moves.</summary>
    [Test]
    public void Leaves_a_silent_device_for_one_whose_sound_lasts()
    {
        var first = _chooser.ChooseDevice([Speakers, Playing(Headphones)], Speakers.Id, sinceRecordedSound: CaptureChooser.LeaveAfter);
        _time.Advance(CaptureChooser.SustainFor);
        var second = _chooser.ChooseDevice([Speakers, Playing(Headphones)], Speakers.Id, sinceRecordedSound: CaptureChooser.LeaveAfter + CaptureChooser.SustainFor);

        Assert.That(first, Is.EqualTo(Speakers), "the first sighting of sound elsewhere is not enough");
        Assert.That(second, Is.EqualTo(Playing(Headphones)));
    }

    /// <summary>A device that never carried sound is left as soon as sound elsewhere has lasted.</summary>
    [Test]
    public void Leaves_a_device_that_never_had_sound()
    {
        _chooser.ChooseDevice([Speakers, Playing(Headphones)], Speakers.Id, sinceRecordedSound: null);
        _time.Advance(CaptureChooser.SustainFor);

        Assert.That(_chooser.ChooseDevice([Speakers, Playing(Headphones)], Speakers.Id, sinceRecordedSound: null), Is.EqualTo(Playing(Headphones)));
    }

    /// <summary>A short sound elsewhere, such as a notification, is not followed: the sound has to last between two looks.</summary>
    [Test]
    public void Ignores_a_short_sound_on_another_device()
    {
        _chooser.ChooseDevice([Speakers, Playing(Headphones)], Speakers.Id, sinceRecordedSound: null);
        _time.Advance(CaptureChooser.SustainFor);
        var afterQuiet = _chooser.ChooseDevice([Speakers, Headphones], Speakers.Id, sinceRecordedSound: null);
        _time.Advance(CaptureChooser.SustainFor);
        var seenAgain = _chooser.ChooseDevice([Speakers, Playing(Headphones)], Speakers.Id, sinceRecordedSound: null);

        Assert.That(afterQuiet, Is.EqualTo(Speakers));
        Assert.That(seenAgain, Is.EqualTo(Speakers), "the sound has to last from this look to the next");
    }

    /// <summary>Setting the computer's clock forward does not make a short sound elsewhere look as if it had lasted: the chooser times sound on the steady clock.</summary>
    [Test]
    public void A_change_of_the_computer_clock_does_not_make_a_short_sound_look_lasting()
    {
        _chooser.ChooseDevice([Speakers, Playing(Headphones)], Speakers.Id, sinceRecordedSound: null);
        _time.StepWallClock(TimeSpan.FromMinutes(1));        // Windows sets its clock forward between two looks

        Assert.That(_chooser.ChooseDevice([Speakers, Playing(Headphones)], Speakers.Id, sinceRecordedSound: null), Is.EqualTo(Speakers));
    }

    /// <summary>A silent device is not left for the silent default one: a pause in the class must not move the recording.</summary>
    [Test]
    public void Stays_put_while_nothing_is_playing_anywhere()
    {
        var chosen = _chooser.ChooseDevice([Speakers, Headphones], Headphones.Id, sinceRecordedSound: null);

        Assert.That(chosen, Is.EqualTo(Headphones));
    }

    /// <summary>Of several devices with sound, the loudest is taken.</summary>
    [Test]
    public void Prefers_the_loudest_device()
    {
        var third = new OutputDevice("hdmi", "Monitor (HDMI)", IsDefault: false, Level: 0.5f);

        var chosen = _chooser.ChooseDevice([Playing(Speakers, 0.1f), Playing(Headphones, 0.3f), third], recordedId: "gone", sinceRecordedSound: null);

        Assert.That(chosen, Is.EqualTo(third));
    }

    /// <summary>When the recorded device is gone, the device with sound is taken at once, or else the default one.</summary>
    [Test]
    public void Replaces_a_device_that_is_gone_at_once()
    {
        Assert.That(_chooser.ChooseDevice([Speakers, Playing(Headphones)], recordedId: "gone", sinceRecordedSound: null), Is.EqualTo(Playing(Headphones)));
        Assert.That(_chooser.ChooseDevice([Speakers, Headphones], recordedId: "gone", sinceRecordedSound: null), Is.EqualTo(Speakers));
    }

    /// <summary>A device named by the class source is recorded whatever is playing elsewhere; part of the name is enough, in any case.</summary>
    [Test]
    public void A_chosen_device_is_recorded_and_no_other()
    {
        var chooser = Chooser(new ClassSource.Device("pixel buds"));

        var atStart = chooser.ChooseDevice([Playing(Speakers, 0.9f), Headphones], recordedId: null, sinceRecordedSound: null);
        var later = chooser.ChooseDevice([Playing(Speakers, 0.9f), Headphones], Headphones.Id, sinceRecordedSound: null);

        Assert.That(atStart, Is.EqualTo(Headphones));
        Assert.That(later, Is.EqualTo(Headphones));
    }

    /// <summary>A chosen device that is not connected leaves nothing to record, even while others play.</summary>
    [Test]
    public void A_chosen_device_that_is_not_connected_gives_nothing()
    {
        var chooser = Chooser(new ClassSource.Device("Pixel Buds"));

        Assert.That(chooser.ChooseDevice([Playing(Speakers)], recordedId: null, sinceRecordedSound: null), Is.Null);
    }

    /// <summary>A chosen app is recorded from the process that has its sound; of several, the one playing loudest.</summary>
    [Test]
    public void A_chosen_app_is_recorded_from_its_process()
    {
        var chooser = Chooser(new ClassSource.App("chrome"));
        var secondChrome = Chrome with { ProcessId = 4343 };

        Assert.That(chooser.ChooseApp([Edge, Chrome], recordedId: null), Is.EqualTo(Chrome));
        Assert.That(chooser.ChooseApp([Playing(Edge, 0.9f), Chrome, Playing(secondChrome)], recordedId: null), Is.EqualTo(Playing(secondChrome)));
        Assert.That(chooser.ChooseApp([Playing(Edge)], recordedId: null), Is.Null, "no process of the app has sound open");
    }

    /// <summary>The process being recorded is kept while it still has sound open, even when another process of the app is louder.</summary>
    [Test]
    public void Keeps_the_recorded_process_of_the_app()
    {
        var chooser = Chooser(new ClassSource.App("chrome"));
        var louder = Playing(Chrome with { ProcessId = 4343 }, 0.9f);

        var chosen = chooser.ChooseApp([Chrome, louder], recordedId: CaptureTarget.ForApp(Chrome).Id);

        Assert.That(chosen, Is.EqualTo(Chrome));
    }

    /// <summary>When the recorded process is gone, another process of the app is taken.</summary>
    [Test]
    public void Follows_the_app_to_its_new_process()
    {
        var chooser = Chooser(new ClassSource.App("chrome"));
        var restarted = Chrome with { ProcessId = 4343 };

        var chosen = chooser.ChooseApp([Edge, restarted], recordedId: CaptureTarget.ForApp(Chrome).Id);

        Assert.That(chosen, Is.EqualTo(restarted));
        Assert.That(_log.Messages, Has.None.Contains("has closed"), "the app has not closed: it plays from another process");
    }

    /// <summary>The app is matched by its process name in any case, or by part of it.</summary>
    [Test]
    public void The_app_is_matched_by_part_of_its_name()
    {
        var chooser = Chooser(new ClassSource.App("EDGE"));

        Assert.That(chooser.ChooseApp([Chrome, Edge], recordedId: null), Is.EqualTo(Edge));
    }

    /// <summary>
    /// The chooser follows the class source it was made for, and nothing else: each kind of source picks from what the
    /// provider lists in its own way, even when that leaves nothing to record while something else plays.
    /// </summary>
    [Test]
    public void The_chooser_follows_its_class_source()
    {
        var provider = new FakeCaptureProvider();
        provider.Enqueue(new FakeAudioCapture(Pcm16KMono, "Speakers"));
        provider.Enqueue(new FakeAudioCapture(Pcm16KMono, "Headphones"), level: 0.3f);
        provider.AddApp(4242, "chrome", "Speakers", level: 0.4f);
        var automatic = Chooser(new ClassSource.Automatic());
        var device = Chooser(new ClassSource.Device("speakers"));
        var app = Chooser(new ClassSource.App("chrome"));
        var absentDevice = Chooser(new ClassSource.Device("Pixel Buds"));
        var absentApp = Chooser(new ClassSource.App("msedge"));

        Assert.That(automatic.Choose(provider, recordedId: null, sinceRecordedSound: null), Is.EqualTo(new CaptureTarget("Headphones", "Headphones")), "the device with sound");
        Assert.That(device.Choose(provider, recordedId: null, sinceRecordedSound: null), Is.EqualTo(new CaptureTarget("Speakers", "Speakers")), "the device named, though another plays");
        Assert.That(app.Choose(provider, recordedId: null, sinceRecordedSound: null), Is.EqualTo(new CaptureTarget("process:4242", "chrome", 4242)), "the app's process, not a device");
        Assert.That(absentDevice.Choose(provider, recordedId: null, sinceRecordedSound: null), Is.Null);
        Assert.That(absentApp.Choose(provider, recordedId: null, sinceRecordedSound: null), Is.Null);
        Assert.That((automatic.IsApp, automatic.ChosenDevice, automatic.Source), Is.EqualTo((false, (string?)null, (ClassSource)new ClassSource.Automatic())));
        Assert.That((device.IsApp, device.ChosenDevice), Is.EqualTo((false, "speakers")));
        Assert.That((app.IsApp, app.ChosenDevice, app.Source), Is.EqualTo((true, (string?)null, (ClassSource)new ClassSource.App("chrome"))));
    }

    /// <summary>
    /// A chooser made from the choice the menu and the settings make records what the choice names at that moment, as a
    /// run's request does: a pick made afterwards is for the next run, not for this one.
    /// </summary>
    [Test]
    public void A_chooser_made_from_the_choice_records_what_it_names_at_that_moment()
    {
        var choice = new SourceChoice(TestOptions.Create(new RecordingOptions { App = "chrome" }));

        var chooser = new CaptureChooser(_time, choice);
        choice.Device = "Pixel Buds";

        Assert.That(chooser.Source, Is.EqualTo(new ClassSource.App("chrome")));
        Assert.That(chooser.WaitingFor, Is.EqualTo("\"chrome\" to play sound"));
    }

    /// <summary>
    /// Each look reads how loud Windows' meter shows the device it keeps or moves to, so the level follows the device
    /// from look to look. There is none before the first look, nor after a look that found nothing to record.
    /// </summary>
    [Test]
    public void Each_look_reads_the_level_of_the_recorded_device()
    {
        var provider = new FakeCaptureProvider();
        provider.ConnectDevice("Speakers", level: 0.25f);
        provider.ConnectDevice("Headphones");

        var beforeAnyLook = _chooser.RecordedLevel;
        _chooser.Choose(provider, recordedId: null, sinceRecordedSound: null);
        var atStart = _chooser.RecordedLevel;
        provider.SetLevel("Speakers", 0.5f);
        _chooser.Choose(provider, recordedId: "Speakers", sinceRecordedSound: TimeSpan.Zero);
        var nextLook = _chooser.RecordedLevel;
        provider.SetLevel("Speakers", 0);
        provider.SetLevel("Headphones", 0.75f);
        _chooser.Choose(provider, recordedId: "Speakers", sinceRecordedSound: null);       // first sight of sound elsewhere: kept
        var kept = _chooser.RecordedLevel;
        _time.Advance(CaptureChooser.SustainFor);
        var moved = _chooser.Choose(provider, recordedId: "Speakers", sinceRecordedSound: null);
        var afterTheMove = _chooser.RecordedLevel;
        provider.RemoveDevices();                                                           // nothing left to record
        _chooser.Choose(provider, recordedId: "Headphones", sinceRecordedSound: null);
        var nothingToRecord = _chooser.RecordedLevel;

        Assert.That(beforeAnyLook, Is.Null);
        Assert.That(atStart, Is.EqualTo(0.25f));
        Assert.That(nextLook, Is.EqualTo(0.5f));
        Assert.That(kept, Is.EqualTo(0f), "the device kept is the one whose level counts");
        Assert.That(moved, Is.EqualTo(new CaptureTarget("Headphones", "Headphones")));
        Assert.That(afterTheMove, Is.EqualTo(0.75f), "and once the recording moves, the new device's");
        Assert.That(nothingToRecord, Is.Null);
    }

    /// <summary>Each look reads how loud Windows' meter shows the recorded app's sound; none once the app has closed.</summary>
    [Test]
    public void Each_look_reads_the_level_of_the_recorded_app()
    {
        var chooser = Chooser(new ClassSource.App("chrome"));
        var provider = new FakeCaptureProvider();
        provider.ConnectDevice("Speakers", level: 0.9f);
        provider.AddApp(4242, "chrome", "Speakers", level: 0.3f);
        provider.AddApp(5151, "msedge", "Speakers", level: 0.8f);
        var recorded = CaptureTarget.ForApp(Chrome).Id;

        chooser.Choose(provider, recordedId: recorded, sinceRecordedSound: null);
        var first = chooser.RecordedLevel;
        provider.SetAppLevel(4242, 0.6f);
        chooser.Choose(provider, recordedId: recorded, sinceRecordedSound: null);
        var second = chooser.RecordedLevel;
        provider.RemoveApp(4242);
        chooser.Choose(provider, recordedId: recorded, sinceRecordedSound: null);
        var closed = chooser.RecordedLevel;

        Assert.That(first, Is.EqualTo(0.3f), "the app's own level, not its device's or another app's");
        Assert.That(second, Is.EqualTo(0.6f));
        Assert.That(closed, Is.Null);
    }

    /// <summary>
    /// A look that fails, as Windows' listings do while its audio service restarts, hands the failure on and leaves no
    /// level behind, for a device and for an app alike: not the level the look before read, because what plays now is
    /// not known.
    /// </summary>
    [TestCase(false)]
    [TestCase(true)]
    public void A_look_that_fails_leaves_no_level(bool app)
    {
        var chooser = Chooser(app ? new ClassSource.App("chrome") : new ClassSource.Automatic());
        var provider = new FakeCaptureProvider();
        provider.ConnectDevice("Speakers", level: 0.4f);
        provider.AddApp(4242, "chrome", "Speakers", level: 0.4f);
        var recorded = app ? CaptureTarget.ForApp(Chrome).Id : "Speakers";

        chooser.Choose(provider, recordedId: null, sinceRecordedSound: null);
        var before = chooser.RecordedLevel;
        provider.FailListings(new COMException("The audio service is restarting."));

        Assert.Throws<COMException>(() => chooser.Choose(provider, recorded, sinceRecordedSound: TimeSpan.Zero));
        Assert.That(before, Is.EqualTo(0.4f));
        Assert.That(chooser.RecordedLevel, Is.Null);
    }

    /// <summary>What the recording waits for when nothing can be recorded yet, for each kind of class source.</summary>
    [Test]
    public void Says_what_it_is_waiting_for()
    {
        Assert.That(Chooser(new ClassSource.Automatic()).WaitingFor, Is.EqualTo("an output device"));
        Assert.That(Chooser(new ClassSource.Device("Pixel Buds")).WaitingFor, Is.EqualTo("the output device \"Pixel Buds\" to connect"));
        Assert.That(Chooser(new ClassSource.App("chrome")).WaitingFor, Is.EqualTo("\"chrome\" to play sound"));
    }

    /// <summary>
    /// When no process of the recorded app has sound open any more, the app has closed: that is said once, as a warning,
    /// not at every look, and nothing is picked, so the capture already open goes on recording its silence. When the app
    /// has sound open again, the next closing is said again.
    /// </summary>
    [Test]
    public void An_app_that_has_closed_is_said_once_until_it_plays_again()
    {
        var chooser = Chooser(new ClassSource.App("chrome"));
        var recorded = CaptureTarget.ForApp(Chrome).Id;

        var whileItRuns = chooser.ChooseApp([Chrome, Edge], recorded);
        var closed = chooser.ChooseApp([Edge], recorded);
        var stillClosed = chooser.ChooseApp([Edge], recorded);
        var saidOnce = _log.Problems.Count(problem => problem == ChromeClosed);
        var back = chooser.ChooseApp([Chrome], recorded);
        chooser.ChooseApp([], recorded);
        chooser.ChooseApp([], recorded);

        Assert.That(whileItRuns, Is.EqualTo(Chrome));
        Assert.That(closed, Is.Null, "nothing to move to: the open capture stays");
        Assert.That(stillClosed, Is.Null);
        Assert.That(saidOnce, Is.EqualTo(1), "said once, not at every look");
        Assert.That(back, Is.EqualTo(Chrome));
        Assert.That(_log.Problems.Count(problem => problem == ChromeClosed), Is.EqualTo(2), "closing again is news again");
    }

    /// <summary>
    /// Nothing is said about an app that was not being recorded: while the recording still waits for it to play, its
    /// absence is no news, and the session says what it waits for.
    /// </summary>
    [Test]
    public void Nothing_is_said_about_an_app_that_was_not_being_recorded()
    {
        var chooser = Chooser(new ClassSource.App("chrome"));

        chooser.ChooseApp([Edge], recordedId: null);
        chooser.ChooseApp([], recordedId: null);

        Assert.That(_log.Messages, Is.Empty);
    }

    /// <summary>The choice comes from the settings, blank meaning automatic; choosing a device drops a chosen app and the other way round.</summary>
    [Test]
    public void The_choice_comes_from_the_settings_and_is_one_thing_at_a_time()
    {
        var fromSettings = new SourceChoice(TestOptions.Create(new RecordingOptions { Device = "  Pixel Buds " }));
        var app = new SourceChoice(TestOptions.Create(new RecordingOptions { App = "chrome" }));
        var blank = new SourceChoice(TestOptions.Create(new RecordingOptions { Device = "   ", App = "" }));

        Assert.That(fromSettings.Device, Is.EqualTo("Pixel Buds"));
        Assert.That((app.App, app.Device), Is.EqualTo(("chrome", (string?)null)));
        Assert.That((blank.App, blank.Device), Is.EqualTo(((string?)null, (string?)null)));

        fromSettings.App = "msedge";
        Assert.That((fromSettings.App, fromSettings.Device), Is.EqualTo(("msedge", (string?)null)), "an app replaces the device");
        fromSettings.Device = "Speakers";
        Assert.That((fromSettings.App, fromSettings.Device), Is.EqualTo(((string?)null, "Speakers")), "a device replaces the app");
        fromSettings.Device = null;
        Assert.That(fromSettings.Description, Is.EqualTo("the device the class plays on"));
        fromSettings.App = "chrome";
        Assert.That(fromSettings.Description, Is.EqualTo("the app \"chrome\""));
    }

    /// <summary>
    /// Until menu 6 picks something, the choice follows the settings as they are read again; once it has, the pick holds
    /// for the rest of the session, automatic included, whatever the settings say next.
    /// </summary>
    [Test]
    public void The_choice_follows_the_settings_until_menu_6_picks_something()
    {
        var settings = TestOptions.Create(new RecordingOptions { Device = "Speakers" });
        var choice = new SourceChoice(settings);

        settings.Set(new RecordingOptions { App = "msedge" });
        Assert.That((choice.App, choice.Device), Is.EqualTo(("msedge", (string?)null)), "followed the settings");

        choice.App = null;
        choice.Device = null;
        settings.Set(new RecordingOptions { Device = "Headphones" });
        Assert.That((choice.App, choice.Device), Is.EqualTo(((string?)null, (string?)null)), "automatic, as picked, whatever the settings say");
        Assert.That(choice.Description, Is.EqualTo("the device the class plays on"));

        choice.Device = "Pixel Buds";
        settings.Set(new RecordingOptions { App = "chrome" });
        Assert.That((choice.App, choice.Device), Is.EqualTo(((string?)null, "Pixel Buds")));
    }

    /// <summary>
    /// The choice, as a run's source plan: the class alone, without a microphone, from the app or the device it names, or
    /// from the device the class plays on when it names neither. It follows the settings, the app winning when they name
    /// both, until menu 6 picks something, and then the pick.
    /// </summary>
    [Test]
    public void The_choice_is_a_plan_for_the_class_alone_from_what_it_names()
    {
        var settings = TestOptions.Create(new RecordingOptions { Device = " Pixel Buds " });
        var choice = new SourceChoice(settings);

        var device = choice.Current;
        settings.Set(new RecordingOptions { App = "chrome", Device = "Speakers" });
        var both = choice.Current;
        settings.Set(new RecordingOptions());
        var neither = choice.Current;
        choice.App = "msedge";
        var picked = choice.Current;
        settings.Set(new RecordingOptions { Device = "Speakers" });
        var pickedStill = choice.Current;

        Assert.That(device, Is.EqualTo(new SourcePlan(RecordingMode.Class, new ClassSource.Device("Pixel Buds"), Microphone: null)));
        Assert.That(both, Is.EqualTo(new SourcePlan(RecordingMode.Class, new ClassSource.App("chrome"), Microphone: null)), "the app wins");
        Assert.That(neither, Is.EqualTo(new SourcePlan(RecordingMode.Class, new ClassSource.Automatic(), Microphone: null)));
        Assert.That(picked, Is.EqualTo(new SourcePlan(RecordingMode.Class, new ClassSource.App("msedge"), Microphone: null)));
        Assert.That(pickedStill, Is.EqualTo(picked), "the pick holds whatever the settings say");
    }

    /// <summary>
    /// The loopback source words its messages for what it records: an output device and system audio for a device, found
    /// or named, and the app by name for an app. It looks once a second.
    /// </summary>
    [Test]
    public void The_loopback_source_words_its_messages_for_what_it_records()
    {
        var provider = new FakeCaptureProvider();
        using var automatic = new LoopbackSource(provider, Chooser(new ClassSource.Automatic()));
        using var device = new LoopbackSource(provider, Chooser(new ClassSource.Device("Pixel Buds")));
        using var app = new LoopbackSource(provider, Chooser(new ClassSource.App("chrome")));

        Assert.That(automatic.Wording, Is.EqualTo(new SourceWording("an output device", "system audio")));
        Assert.That(device.Wording, Is.EqualTo(new SourceWording("an output device", "system audio")));
        Assert.That(app.Wording, Is.EqualTo(new SourceWording("the app \"chrome\"", "the sound of \"chrome\"")));
        Assert.That(automatic.CheckInterval, Is.EqualTo(TimeSpan.FromSeconds(1)));
        Assert.That(app.WaitingFor, Is.EqualTo("\"chrome\" to play sound"));
    }

    /// <summary>The loopback source says why the recording moves, in the words for what it records.</summary>
    [Test]
    public void The_loopback_source_says_why_the_recording_moves()
    {
        var provider = new FakeCaptureProvider();
        using var automatic = new LoopbackSource(provider, Chooser(new ClassSource.Automatic()));
        using var device = new LoopbackSource(provider, Chooser(new ClassSource.Device("Pixel Buds")));
        using var app = new LoopbackSource(provider, Chooser(new ClassSource.App("chrome")));

        Assert.That(automatic.DescribeMove(new CaptureTarget("buds", "Headphones"), "Speakers"), Is.EqualTo("Sound is playing on \"Headphones\"; recording it instead of \"Speakers\""));
        Assert.That(device.DescribeMove(new CaptureTarget("buds", "Pixel Buds"), "Speakers"), Is.EqualTo("Recording \"Pixel Buds\", the chosen output device, instead of \"Speakers\""));
        Assert.That(app.DescribeMove(new CaptureTarget("process:4343", "chrome", 4343), "chrome"), Is.EqualTo("\"chrome\" now plays from another process; following it"));
    }

    /// <summary>
    /// The loopback source hands on what its chooser picks and the level that look read, opens captures through the
    /// provider, and passes the provider's notifications on to whoever listens, until they stop listening.
    /// </summary>
    [Test]
    public void The_loopback_source_picks_opens_and_passes_on_notifications_through_the_provider()
    {
        var provider = new FakeCaptureProvider();
        var speakers = provider.Enqueue(new FakeAudioCapture(Pcm16KMono, "Speakers"), level: 0.25f);
        using var source = new LoopbackSource(provider, Chooser(new ClassSource.Automatic()));
        var notified = 0;
        void Count(object? sender, EventArgs e) => notified++;

        var target = source.Choose(recordedId: null, sinceRecordedSound: null);
        var level = source.RecordedLevel;
        var opened = source.Open(target!);
        source.Changed += Count;
        provider.RaiseDevicesChanged();
        var listening = provider.HasListeners;
        source.Changed -= Count;
        provider.RaiseDevicesChanged();

        Assert.That(target, Is.EqualTo(new CaptureTarget("Speakers", "Speakers")));
        Assert.That(level, Is.EqualTo(0.25f));
        Assert.That(opened, Is.SameAs(speakers));
        Assert.That(listening, Is.True);
        Assert.That(notified, Is.EqualTo(1));
        Assert.That(provider.HasListeners, Is.False);
    }
}
