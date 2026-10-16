using ClassScribe.Audio;
using ClassScribe.Settings;
using Microsoft.Extensions.Options;

namespace ClassScribe.Tests;

/// <summary>What gets recorded: the device the class is playing through, the device chosen by name, or the app chosen by name.</summary>
[TestFixture]
public class CaptureChooserTests
{
    private static readonly AudioFormat Pcm16KMono = new(16000, 1, 16, false);
    private static readonly OutputDevice Speakers = new("speakers", "Speakers (Realtek(R) Audio)", IsDefault: true, Level: 0);
    private static readonly OutputDevice Headphones = new("buds", "Headphones (Pixel Buds Pro 2)", IsDefault: false, Level: 0);
    private static readonly PlayingApp Chrome = new(4242, "chrome", "Speakers (Realtek(R) Audio)", Level: 0);
    private static readonly PlayingApp Edge = new(5151, "msedge", "Speakers (Realtek(R) Audio)", Level: 0);

    private ManualTimeProvider _time = null!;
    private SourceChoice _choice = null!;
    private CaptureChooser _chooser = null!;

    /// <summary>Creates a chooser on a clock the test moves by hand, in automatic mode.</summary>
    [SetUp]
    public void SetUp()
    {
        _time = new ManualTimeProvider(new DateTimeOffset(2026, 10, 15, 19, 0, 0, TimeSpan.Zero));
        _choice = new SourceChoice(Options.Create(new RecordingOptions()));
        _chooser = new CaptureChooser(_time, _choice);
    }

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

    /// <summary>A device chosen by name is recorded whatever is playing elsewhere; part of the name is enough, in any case.</summary>
    [Test]
    public void A_chosen_device_is_recorded_and_no_other()
    {
        _choice.Device = "pixel buds";

        var atStart = _chooser.ChooseDevice([Playing(Speakers, 0.9f), Headphones], recordedId: null, sinceRecordedSound: null);
        var later = _chooser.ChooseDevice([Playing(Speakers, 0.9f), Headphones], Headphones.Id, sinceRecordedSound: null);

        Assert.That(atStart, Is.EqualTo(Headphones));
        Assert.That(later, Is.EqualTo(Headphones));
    }

    /// <summary>A chosen device that is not connected leaves nothing to record, even while others play.</summary>
    [Test]
    public void A_chosen_device_that_is_not_connected_gives_nothing()
    {
        _choice.Device = "Pixel Buds";

        Assert.That(_chooser.ChooseDevice([Playing(Speakers)], recordedId: null, sinceRecordedSound: null), Is.Null);
    }

    /// <summary>A chosen app is recorded from the process that has its sound; of several, the one playing loudest.</summary>
    [Test]
    public void A_chosen_app_is_recorded_from_its_process()
    {
        _choice.App = "chrome";
        var secondChrome = Chrome with { ProcessId = 4343 };

        Assert.That(_chooser.ChooseApp([Edge, Chrome], recordedId: null), Is.EqualTo(Chrome));
        Assert.That(_chooser.ChooseApp([Playing(Edge, 0.9f), Chrome, Playing(secondChrome)], recordedId: null), Is.EqualTo(Playing(secondChrome)));
        Assert.That(_chooser.ChooseApp([Playing(Edge)], recordedId: null), Is.Null, "no process of the app has sound open");
    }

    /// <summary>The process being recorded is kept while it still has sound open, even when another process of the app is louder.</summary>
    [Test]
    public void Keeps_the_recorded_process_of_the_app()
    {
        _choice.App = "chrome";
        var louder = Playing(Chrome with { ProcessId = 4343 }, 0.9f);

        var chosen = _chooser.ChooseApp([Chrome, louder], recordedId: CaptureTarget.ForApp(Chrome).Id);

        Assert.That(chosen, Is.EqualTo(Chrome));
    }

    /// <summary>When the recorded process is gone, another process of the app is taken.</summary>
    [Test]
    public void Follows_the_app_to_its_new_process()
    {
        _choice.App = "chrome";
        var restarted = Chrome with { ProcessId = 4343 };

        var chosen = _chooser.ChooseApp([Edge, restarted], recordedId: CaptureTarget.ForApp(Chrome).Id);

        Assert.That(chosen, Is.EqualTo(restarted));
    }

    /// <summary>The app is matched by its process name in any case, or by part of it.</summary>
    [Test]
    public void The_app_is_matched_by_part_of_its_name()
    {
        _choice.App = "EDGE";

        Assert.That(_chooser.ChooseApp([Chrome, Edge], recordedId: null), Is.EqualTo(Edge));
    }

    /// <summary>Choose picks the mode from the choice: an app, a device by name, or the device with sound, each from what the provider lists.</summary>
    [Test]
    public void Choose_picks_the_source_the_choice_asks_for()
    {
        var provider = new FakeCaptureProvider();
        provider.Enqueue(new FakeAudioCapture(Pcm16KMono, "Speakers"));
        provider.Enqueue(new FakeAudioCapture(Pcm16KMono, "Headphones"), level: 0.3f);
        provider.AddApp(4242, "chrome", "Speakers", level: 0.4f);

        var automatic = _chooser.Choose(provider, recordedId: null, sinceRecordedSound: null);
        _choice.Device = "speakers";
        var device = _chooser.Choose(provider, recordedId: null, sinceRecordedSound: null);
        _choice.App = "chrome";
        var app = _chooser.Choose(provider, recordedId: null, sinceRecordedSound: null);

        Assert.That(automatic, Is.EqualTo(new CaptureTarget("Headphones", "Headphones")));
        Assert.That(device, Is.EqualTo(new CaptureTarget("Speakers", "Speakers")));
        Assert.That(app, Is.EqualTo(new CaptureTarget("process:4242", "chrome", 4242)));
    }

    /// <summary>What the recording waits for when nothing can be recorded yet, in each mode.</summary>
    [Test]
    public void Says_what_it_is_waiting_for()
    {
        Assert.That(_chooser.WaitingFor, Is.EqualTo("an output device"));
        _choice.Device = "Pixel Buds";
        Assert.That(_chooser.WaitingFor, Is.EqualTo("the output device \"Pixel Buds\" to connect"));
        _choice.App = "chrome";
        Assert.That(_chooser.WaitingFor, Is.EqualTo("\"chrome\" to play sound"));
    }

    /// <summary>The choice comes from the settings, blank meaning automatic; choosing a device drops a chosen app and the other way round.</summary>
    [Test]
    public void The_choice_comes_from_the_settings_and_is_one_thing_at_a_time()
    {
        var fromSettings = new SourceChoice(Options.Create(new RecordingOptions { Device = "  Pixel Buds " }));
        var app = new SourceChoice(Options.Create(new RecordingOptions { App = "chrome" }));
        var blank = new SourceChoice(Options.Create(new RecordingOptions { Device = "   ", App = "" }));

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
}
