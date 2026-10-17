using ClassScribe.Audio.Microphones;

namespace ClassScribe.Tests;

/// <summary>
/// Which microphone is recorded: Windows' default unless it is Bluetooth or an input that records what the computer plays,
/// then the laptop's own array, then another microphone that is neither; a microphone named by part of its name, Bluetooth
/// allowed; the microphone the class app uses, when it differs; and the choice kept by endpoint id for the whole
/// recording, with a fallback after the chosen microphone has been gone for ten seconds and a move back when it returns.
/// Nothing here opens a microphone: the chooser only looks at what the provider lists.
/// </summary>
[TestFixture]
public class MicrophoneChooserTests
{
    private static readonly Microphone Array = LaptopMicrophones.Array;
    private static readonly Microphone PixelBuds = LaptopMicrophones.PixelBuds;
    private static readonly Microphone Soundcore = LaptopMicrophones.Soundcore;
    private static readonly Microphone Obsbot = LaptopMicrophones.Obsbot;
    private static readonly Microphone StereoMix = LaptopMicrophones.StereoMix;

    private ManualTimeProvider _time = null!;
    private RecordingLogger<MicrophoneChooser> _log = null!;

    /// <summary>A clock the test moves by hand, and a log to inspect.</summary>
    [SetUp]
    public void SetUp()
    {
        _time = new ManualTimeProvider(new DateTimeOffset(2026, 10, 17, 19, 0, 0, TimeSpan.Zero));
        _log = new RecordingLogger<MicrophoneChooser>();
    }

    /// <summary>A chooser for <paramref name="namePart"/>, or for Windows' default microphone when none is given.</summary>
    private MicrophoneChooser Chooser(string? namePart = null) => new(namePart, _time, _log);

    /// <summary>The microphone as Windows' default, or not.</summary>
    private static Microphone Default(Microphone microphone, bool isDefault = true) => microphone with { IsDefault = isDefault };

    /// <summary>The array, Windows' default, as this laptop lists it, beside the others.</summary>
    private static Microphone[] Laptop() => [Array, Obsbot, PixelBuds];

    /// <summary>With nothing named, Windows' default microphone is taken: on this laptop the built-in array.</summary>
    [Test]
    public void Picks_Windows_default_microphone_when_nothing_is_named()
    {
        var chooser = Chooser();

        var pick = chooser.Choose(Laptop());

        Assert.That(pick.Chosen, Is.EqualTo(Array));
        Assert.That(pick.Why, Is.EqualTo("Windows' default microphone"));
        Assert.That(pick.FellBack, Is.False);
        Assert.That(chooser.PinnedId, Is.EqualTo(Array.Id));
    }

    /// <summary>A USB microphone that is Windows' default is taken before the array.</summary>
    [Test]
    public void Picks_a_USB_default_before_the_array()
    {
        var pick = Chooser().Choose([Default(Array, false), Default(Obsbot), PixelBuds]);

        Assert.That(pick.Chosen, Is.EqualTo(Default(Obsbot)));
        Assert.That(pick.Why, Is.EqualTo("Windows' default microphone"));
    }

    /// <summary>A Bluetooth default is never taken by itself: the array is, and the reason names the Bluetooth one.</summary>
    [Test]
    public void A_Bluetooth_default_falls_to_the_array()
    {
        var pick = Chooser().Choose([Default(Array, false), Obsbot, Default(PixelBuds)]);

        Assert.That(pick.Chosen, Is.EqualTo(Default(Array, false)));
        Assert.That(pick.Why, Is.EqualTo("the laptop's microphone array, because Windows' default \"Headset (2- Pixel Buds Pro 2)\" is Bluetooth"));
    }

    /// <summary>The soundcore headset is Bluetooth by a property, not by its enumerator, and is passed over the same way.</summary>
    [Test]
    public void A_headset_that_is_Bluetooth_by_a_property_is_passed_over_too()
    {
        var pick = Chooser().Choose([Default(Array, false), Default(Soundcore)]);

        Assert.That(pick.Chosen, Is.EqualTo(Default(Array, false)));
    }

    /// <summary>Without an array, the first microphone that is neither Bluetooth nor a loopback input is taken.</summary>
    [Test]
    public void Without_an_array_the_first_allowed_microphone_is_taken()
    {
        var pick = Chooser().Choose([Default(PixelBuds), Soundcore, Obsbot]);

        Assert.That(pick.Chosen, Is.EqualTo(Obsbot));
        Assert.That(pick.Why, Is.EqualTo("another microphone, because Windows' default \"Headset (2- Pixel Buds Pro 2)\" is Bluetooth and the laptop has no microphone array"));
    }

    /// <summary>When Windows names no default, the array is taken, then any allowed microphone.</summary>
    [Test]
    public void Without_a_default_the_array_is_taken()
    {
        var pick = Chooser().Choose([Obsbot, Default(Array, false)]);

        Assert.That(pick.Chosen, Is.EqualTo(Default(Array, false)));
        Assert.That(pick.Why, Is.EqualTo("the laptop's microphone array, because Windows names no default microphone"));
    }

    /// <summary>A name part picks the first microphone whose name contains it, in any case, and Bluetooth is allowed this way.</summary>
    [Test]
    public void A_name_part_picks_by_name_in_any_case_and_allows_Bluetooth()
    {
        var chooser = Chooser("pixel BUDS");

        var pick = chooser.Choose(Laptop());

        Assert.That(pick.Chosen, Is.EqualTo(PixelBuds));
        Assert.That(pick.Why, Is.EqualTo("the microphone named \"pixel BUDS\""));
        Assert.That(chooser.NamePart, Is.EqualTo("pixel BUDS"));
        Assert.That(chooser.PinnedId, Is.EqualTo(PixelBuds.Id));
    }

    /// <summary>A name that matches nothing waits for that microphone, and says so.</summary>
    [Test]
    public void A_name_that_matches_nothing_waits_for_it()
    {
        var chooser = Chooser("OBSBOT");

        var pick = chooser.Choose([Array, PixelBuds]);

        Assert.That(pick.Chosen, Is.Null);
        Assert.That(pick.FellBack, Is.False);
        Assert.That(chooser.WaitingFor, Is.EqualTo("the microphone \"OBSBOT\""));
        Assert.That(pick.Why, Is.EqualTo("waiting for the microphone \"OBSBOT\""));
        Assert.That(chooser.PinnedId, Is.Null, "nothing is pinned until something is chosen");
    }

    /// <summary>A name that is only spaces means no name: Windows' default is taken.</summary>
    [Test]
    public void A_blank_name_means_no_name()
    {
        var chooser = Chooser("   ");

        Assert.That(chooser.NamePart, Is.Null);
        Assert.That(chooser.Choose(Laptop()).Chosen, Is.EqualTo(Array));
    }

    /// <summary>An input that records what the computer plays is never taken: not as the default, not by name, not as the last resort.</summary>
    [Test]
    public void Stereo_Mix_is_never_chosen()
    {
        var byName = Chooser("Stereo Mix");

        Assert.That(Chooser().Choose([Default(StereoMix), Default(Array, false)]).Chosen, Is.EqualTo(Default(Array, false)), "the default rule passes it over");
        Assert.That(byName.Choose([StereoMix, Array]).Chosen, Is.Null, "a name does not allow it");
        Assert.That(byName.WaitingFor, Is.EqualTo("a microphone other than \"Stereo Mix (Realtek(R) Audio)\", which records what the computer plays"));
        Assert.That(Chooser().Choose([StereoMix]).Chosen, Is.Null, "nor is it the last resort");
    }

    /// <summary>With only Bluetooth microphones connected, nothing is chosen, and the wait says that a Bluetooth microphone has to be named.</summary>
    [Test]
    public void With_only_Bluetooth_microphones_it_waits_and_says_one_has_to_be_named()
    {
        var chooser = Chooser();

        var pick = chooser.Choose([Default(PixelBuds), Soundcore]);

        Assert.That(pick.Chosen, Is.Null);
        Assert.That(chooser.WaitingFor, Is.EqualTo("a microphone that is not Bluetooth (\"Headset (2- Pixel Buds Pro 2)\" is used only when named in Recording:Microphone)"));
    }

    /// <summary>With no microphone at all, the wait names a microphone, plainly.</summary>
    [Test]
    public void With_no_microphone_it_waits_for_one()
    {
        var chooser = Chooser();

        Assert.That(chooser.WaitingFor, Is.EqualTo("a microphone"), "before the first look");
        Assert.That(chooser.Choose([]).Chosen, Is.Null);
        Assert.That(chooser.WaitingFor, Is.EqualTo("a microphone"));
    }

    /// <summary>The choice is kept by endpoint id: earbuds that connect mid-class and become Windows' default are not followed.</summary>
    [Test]
    public void Keeps_the_chosen_microphone_when_the_default_changes()
    {
        var chooser = Chooser();
        chooser.Choose([Array]);

        var pick = chooser.Choose([Default(Array, false), Default(PixelBuds), Default(Obsbot)]);

        Assert.That(pick.Chosen, Is.EqualTo(Default(Array, false)));
        Assert.That(pick.Why, Is.EqualTo("the microphone chosen at the start"));
        Assert.That(pick.FellBack, Is.False);
    }

    /// <summary>
    /// A chosen microphone that has gone is waited for, and only once it has been gone for ten seconds is another taken by
    /// the default rule, which never takes Bluetooth, with a warning in the log.
    /// </summary>
    [Test]
    public void Falls_back_after_the_chosen_microphone_has_been_gone_for_ten_seconds()
    {
        var chooser = Chooser("OBSBOT");
        chooser.Choose(Laptop());

        var atOnce = chooser.Choose([Array, PixelBuds]);
        _time.Advance(MicrophoneChooser.FallBackAfter - TimeSpan.FromSeconds(1));
        var tooSoon = chooser.Choose([Array, PixelBuds]);
        _time.Advance(TimeSpan.FromSeconds(1));
        var fallen = chooser.Choose([Default(PixelBuds), Default(Array, false)]);

        Assert.That(atOnce.Chosen, Is.Null);
        Assert.That(atOnce.Why, Is.EqualTo("waiting for \"OBSBOT Meet Flip Microphone (OBSBOT Meet Flip Audio)\" to come back"));
        Assert.That(chooser.WaitingFor, Is.EqualTo("the microphone \"OBSBOT\""));
        Assert.That(tooSoon.Chosen, Is.Null);
        Assert.That(fallen.Chosen, Is.EqualTo(Default(Array, false)));
        Assert.That(fallen.FellBack, Is.True);
        Assert.That(fallen.Why, Is.EqualTo("the laptop's microphone array, instead of \"OBSBOT Meet Flip Microphone (OBSBOT Meet Flip Audio)\", which has been gone for 10 s"));
        Assert.That(chooser.PinnedId, Is.EqualTo(Obsbot.Id), "the choice stays with the microphone that has gone");
        Assert.That(_log.Problems, Is.EqualTo([
            "The microphone \"OBSBOT Meet Flip Microphone (OBSBOT Meet Flip Audio)\" has been gone for 10 s; recording \"Microphone Array (Intel® Smart Sound Technology for Digital Microphones)\" instead until it returns"]));
    }

    /// <summary>
    /// While fallen back, the recording moves back the moment the chosen microphone returns, and a second absence is timed
    /// afresh. The fallback is warned about once per absence, not at every look.
    /// </summary>
    [Test]
    public void Moves_back_when_the_chosen_microphone_returns()
    {
        const string gone = "The microphone \"OBSBOT Meet Flip Microphone (OBSBOT Meet Flip Audio)\" has been gone for 10 s; recording \"Microphone Array (Intel® Smart Sound Technology for Digital Microphones)\" instead until it returns";
        var chooser = Chooser("OBSBOT");
        chooser.Choose(Laptop());
        chooser.Choose([Array]);
        _time.Advance(MicrophoneChooser.FallBackAfter);
        var fallen = chooser.Choose([Array]);
        var stillFallen = chooser.Choose([Array]);
        var warnedOnce = _log.Problems;

        var back = chooser.Choose([Array, Obsbot]);
        var goneAgain = chooser.Choose([Array]);
        _time.Advance(MicrophoneChooser.FallBackAfter - TimeSpan.FromSeconds(1));
        var stillWaiting = chooser.Choose([Array]);
        var warnedStillOnce = _log.Problems;
        _time.Advance(TimeSpan.FromSeconds(1));
        var fallenAgain = chooser.Choose([Array]);

        Assert.That(fallen.FellBack, Is.True);
        Assert.That(stillFallen.FellBack, Is.True);
        Assert.That(stillFallen.Chosen, Is.EqualTo(Array));
        Assert.That(warnedOnce, Is.EqualTo([gone]), "warned once, not at every look while fallen back");
        Assert.That(back.Chosen, Is.EqualTo(Obsbot));
        Assert.That(back.FellBack, Is.False);
        Assert.That(back.Why, Is.EqualTo("the microphone chosen at the start, back again"));
        Assert.That(goneAgain.Chosen, Is.Null, "a new absence is waited out again");
        Assert.That(stillWaiting.Chosen, Is.Null);
        Assert.That(warnedStillOnce, Is.EqualTo([gone]), "nothing more while waiting");
        Assert.That(fallenAgain.FellBack, Is.True);
        Assert.That(_log.Problems, Is.EqualTo([gone, gone]), "a second absence is warned about again");
        Assert.That(_log.Messages, Does.Contain("The microphone \"OBSBOT Meet Flip Microphone (OBSBOT Meet Flip Audio)\" is back; recording it again"));
    }

    /// <summary>
    /// While fallen back, the class app using the very microphone that stands in makes it the choice, without saying that
    /// it is recorded instead of itself; the chosen microphone's return then no longer moves the recording.
    /// </summary>
    [Test]
    public void The_apps_microphone_that_is_standing_in_becomes_the_choice()
    {
        var chooser = Chooser("OBSBOT");
        chooser.Choose(Laptop());
        chooser.Choose([Array]);
        _time.Advance(MicrophoneChooser.FallBackAfter);
        var fallen = chooser.Choose([Array]);

        var kept = chooser.Choose([Array], appMicrophoneId: Array.Id);
        var later = chooser.Choose([Array, Obsbot]);

        Assert.That(fallen.FellBack, Is.True);
        Assert.That(kept.Chosen, Is.EqualTo(Array));
        Assert.That(kept.FellBack, Is.False);
        Assert.That(kept.Why, Is.EqualTo("the microphone the class app uses"));
        Assert.That(chooser.PinnedId, Is.EqualTo(Array.Id));
        Assert.That(later.Chosen, Is.EqualTo(Array), "the OBSBOT's return no longer moves the recording");
        Assert.That(_log.Messages, Does.Contain("The class app uses the microphone \"Microphone Array (Intel® Smart Sound Technology for Digital Microphones)\", the one standing in; keeping it"));
        Assert.That(_log.Messages, Has.None.Contains("instead of \"Microphone Array"));
    }

    /// <summary>The fallback never takes Bluetooth, even when the microphone chosen by name was Bluetooth.</summary>
    [Test]
    public void The_fallback_never_takes_Bluetooth_even_for_a_named_Bluetooth_microphone()
    {
        var chooser = Chooser("pixel");
        chooser.Choose(Laptop());
        chooser.Choose([Array, Soundcore]);
        _time.Advance(MicrophoneChooser.FallBackAfter);

        var fallen = chooser.Choose([Default(Soundcore), Default(Array, false)]);
        var nothing = chooser.Choose([Default(Soundcore)]);

        Assert.That(fallen.Chosen, Is.EqualTo(Default(Array, false)));
        Assert.That(fallen.FellBack, Is.True);
        Assert.That(nothing.Chosen, Is.Null);
        Assert.That(nothing.FellBack, Is.False);
        Assert.That(chooser.WaitingFor, Is.EqualTo("the microphone \"pixel\""));
    }

    /// <summary>When the class app uses another microphone than the one chosen, the recording switches to the app's, which then stays the choice.</summary>
    [Test]
    public void Follows_the_class_apps_microphone_when_it_differs()
    {
        var chooser = Chooser();
        chooser.Choose(Laptop());

        var moved = chooser.Choose(Laptop(), appMicrophoneId: Obsbot.Id);
        var kept = chooser.Choose(Laptop());
        var same = chooser.Choose(Laptop(), appMicrophoneId: Obsbot.Id);

        Assert.That(moved.Chosen, Is.EqualTo(Obsbot));
        Assert.That(moved.Why, Is.EqualTo("the microphone the class app uses"));
        Assert.That(moved.FellBack, Is.False);
        Assert.That(chooser.PinnedId, Is.EqualTo(Obsbot.Id));
        Assert.That(kept.Chosen, Is.EqualTo(Obsbot), "the app's microphone stays the choice when the app lets go of it");
        Assert.That(kept.Why, Is.EqualTo("the microphone chosen at the start"));
        Assert.That(same.Why, Is.EqualTo("the microphone chosen at the start"), "no move when the app uses the one recorded");
        Assert.That(_log.Messages, Does.Contain(
            "The class app uses the microphone \"OBSBOT Meet Flip Microphone (OBSBOT Meet Flip Audio)\"; recording it instead of \"Microphone Array (Intel® Smart Sound Technology for Digital Microphones)\""));
    }

    /// <summary>
    /// The app's microphone is followed even when it is Bluetooth; the warning about that is the advice's, from the lines
    /// for a move. A loopback input is never followed, and is warned about once, not at every look.
    /// </summary>
    [Test]
    public void The_apps_microphone_is_followed_when_Bluetooth_but_never_when_it_records_what_the_computer_plays()
    {
        var chooser = Chooser();
        chooser.Choose(Laptop());

        var buds = chooser.Choose(Laptop(), appMicrophoneId: PixelBuds.Id);
        var mix = chooser.Choose([.. Laptop(), StereoMix], appMicrophoneId: StereoMix.Id);
        var mixAgain = chooser.Choose([.. Laptop(), StereoMix], appMicrophoneId: StereoMix.Id);

        Assert.That(buds.Chosen, Is.EqualTo(PixelBuds));
        Assert.That(mix.Chosen, Is.EqualTo(PixelBuds), "the choice stays where it was");
        Assert.That(mixAgain.Chosen, Is.EqualTo(PixelBuds));
        Assert.That(_log.Problems, Is.EqualTo([
            "The class app uses \"Stereo Mix (Realtek(R) Audio)\", which records what the computer plays; recording \"Headset (2- Pixel Buds Pro 2)\" still"]),
            "warned once, not at every look");
    }

    /// <summary>An app's microphone that is not among those listed changes nothing.</summary>
    [Test]
    public void An_apps_microphone_that_is_not_listed_is_ignored()
    {
        var chooser = Chooser();
        chooser.Choose(Laptop());

        var pick = chooser.Choose(Laptop(), appMicrophoneId: "{0.0.1.00000000}.{nowhere}");

        Assert.That(pick.Chosen, Is.EqualTo(Array));
        Assert.That(pick.Why, Is.EqualTo("the microphone chosen at the start"));
    }

    /// <summary>The app's microphone is taken at the first look too, before anything else is chosen.</summary>
    [Test]
    public void The_apps_microphone_is_taken_at_the_first_look()
    {
        var pick = Chooser("OBSBOT").Choose(Laptop(), appMicrophoneId: PixelBuds.Id);

        Assert.That(pick.Chosen, Is.EqualTo(PixelBuds));
        Assert.That(pick.Why, Is.EqualTo("the microphone the class app uses"));
    }
}
