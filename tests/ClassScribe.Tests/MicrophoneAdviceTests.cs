using System.Runtime.Versioning;
using System.Security;
using ClassScribe.Audio;
using ClassScribe.Audio.Microphones;
using ClassScribe.Desktop;
using ClassScribe.Desktop.Windows;
using Microsoft.Extensions.DependencyInjection;

namespace ClassScribe.Tests;

/// <summary>
/// What ClassScribe says at the start of a recording with the microphone, and while it runs: the privacy line and how the
/// class app's mute is followed, which microphone and how, Lenovo Vantage's noise cancelling, Windows' ducking, a Bluetooth
/// microphone, the class playing where the microphone hears it, and the in-person advice. The registry values come from a
/// fake; nothing here reads the real registry except the test that resolves the advice from the app's registrations.
/// </summary>
[TestFixture]
public class MicrophoneAdviceTests
{
    private const string Privacy = "Your microphone is recorded too, and uploaded to ElevenLabs with the class.";
    private const string RoomPrivacy = "Your microphone records the room, and the recording is uploaded to ElevenLabs. Ask before recording others.";
    private const string ArrayRaw = "Microphone: \"Microphone Array (Intel® Smart Sound Technology for Digital Microphones)\" (Windows' default microphone), without Windows' sound processing (RAW)";
    private const string Ducking80 = "Windows lowers other sounds by 80% while a microphone is open, which can make the class quieter, in the recording too. Set Sound > More sound settings > Communications to Do nothing.";
    private const string VantageOn = "Lenovo Vantage's microphone noise cancelling is on (mode 1, probably \"Multiple Voices\"); it can cut out a lecturer across the room. Set Vantage > Device Settings > Sound > Input to Off.";
    private const string VantageOff = "Set Vantage > Device Settings > Sound > Input to Off.";
    private const string InPerson = "Plug in and keep the lid open; the screen stays on while on battery. For a break press P; V switches the microphone off but keeps the clock running, and that silence is uploaded.";

    private FakeRegistryValues _registry = null!;
    private RecordingLogger<MicrophoneAdvice> _log = null!;
    private MicrophoneAdvice _advice = null!;

    /// <summary>A registry with Vantage off and ducking set to Do nothing, so that only what a test turns on is warned about.</summary>
    [SetUp]
    public void SetUp()
    {
        _registry = new FakeRegistryValues()
            .Set(MicrophoneAdvice.VantageKey, MicrophoneAdvice.VantageValue, "0")
            .Set(MicrophoneAdvice.DuckingKey, MicrophoneAdvice.DuckingValue, 3);
        _log = new RecordingLogger<MicrophoneAdvice>();
        _advice = new MicrophoneAdvice(_registry, _log);
    }

    /// <summary>A start with the class and the array in RAW, the class on headphones, following chrome, whose mute is not known.</summary>
    private static MicrophoneStart WithClass(Microphone? microphone = null, FollowRule? follows = null, ClassAppMute appMute = ClassAppMute.Unknown) =>
        new(RecordingMode.ClassAndMicrophone, microphone ?? LaptopMicrophones.Array, MicrophoneProcessing.Raw, Why: "Windows' default microphone",
            ClassDeviceName: "Headphones (Pixel Buds Pro 2)", ClassHeardInTheRoom: false, Follows: follows ?? new FollowRule.Named("chrome"), AppMute: appMute);

    /// <summary>A start in person with the array in RAW.</summary>
    private static MicrophoneStart InPersonStart(Microphone? microphone = null) =>
        new(RecordingMode.Microphone, microphone ?? LaptopMicrophones.Array, MicrophoneProcessing.Raw, Why: "Windows' default microphone");

    /// <summary>The texts of the lines, in order.</summary>
    private IReadOnlyList<string> Lines(MicrophoneStart start) => [.. _advice.AtStart(start).Select(line => line.Text)];

    /// <summary>The texts of the lines that are warnings, in order.</summary>
    private IReadOnlyList<string> Warnings(MicrophoneStart start) => [.. _advice.AtStart(start).Where(line => line.Warning).Select(line => line.Text)];

    /// <summary>With the class and no app followed, the privacy line says that V is the switch, and the microphone line follows; neither is a warning.</summary>
    [Test]
    public void With_the_class_the_privacy_line_names_V_when_no_app_is_followed()
    {
        var start = WithClass(follows: new FollowRule.None());

        Assert.That(Lines(start), Is.EqualTo([Privacy, "V switches it off and on; muting yourself in the class app does not.", ArrayRaw]));
        Assert.That(Warnings(start), Is.Empty);
    }

    /// <summary>A rule left out means no app is followed, as in person.</summary>
    [Test]
    public void No_rule_means_no_app_is_followed()
    {
        var start = WithClass() with { Follows = null };

        Assert.That(Lines(start)[1], Is.EqualTo("V switches it off and on; muting yourself in the class app does not."));
    }

    /// <summary>When the class app lets go of the microphone on mute, the line says that ClassScribe follows the mute by itself.</summary>
    [Test]
    public void The_case_line_for_an_app_whose_mute_Windows_can_see()
    {
        var lines = Lines(WithClass(appMute: ClassAppMute.Visible));

        Assert.That(lines[1], Is.EqualTo("Your voice is recorded while chrome is listening: when you mute in chrome, ClassScribe stops too. V switches it off by hand."));
    }

    /// <summary>When the class app keeps the microphone open while muted, the line says that V records and the Windows mute stops it.</summary>
    [Test]
    public void The_case_line_for_an_app_whose_mute_Windows_cannot_see()
    {
        var lines = Lines(WithClass(appMute: ClassAppMute.Invisible));

        Assert.That(lines[1], Is.EqualTo("chrome keeps the microphone open while you are muted, so ClassScribe cannot see your mute. " +
            "Your voice is recorded only after you press V, and only while chrome holds the microphone. Muting the microphone in Windows (the microphone key) stops it too."));
    }

    /// <summary>
    /// Until the class app has been measured, the line says that ClassScribe does not know whether the app lets go of
    /// the microphone, and so cannot follow the mute yet: what V does meanwhile, and how to find out. It never states
    /// as a fact that the app keeps the microphone open, which is what has not been measured.
    /// </summary>
    [Test]
    public void The_case_line_for_an_app_not_measured_yet()
    {
        var lines = Lines(WithClass(appMute: ClassAppMute.Unknown));

        Assert.That(lines[1], Is.EqualTo("ClassScribe has not been told whether chrome lets go of the microphone when you mute (most class apps and pages keep it open), so it cannot follow your mute yet: " +
            "your voice is recorded only after you press V, and only while chrome holds the microphone. Muting the microphone in Windows (the microphone key) stops it too. " +
            "Run the microphone check (menu choice 8, then W) during a class, or set Recording:ClassAppMute."));
        Assert.That(lines[1], Does.Not.StartWith("chrome keeps the microphone open"), "what has not been measured is not stated as a fact");
    }

    /// <summary>
    /// A track that starts switched off, as it does while the class app's mute cannot be seen, still names the microphone
    /// V will open and what will be asked for, since nothing is waited for; the warnings about it (Vantage, for the array)
    /// come at once rather than at V; and with no microphone picked yet the line says so.
    /// </summary>
    [TestCase(MicrophoneProcessing.Raw, "RAW will be asked for")]
    [TestCase(MicrophoneProcessing.Default, "Windows' sound processing will be kept")]
    public void A_track_switched_off_at_the_start_names_the_microphone_V_will_open(MicrophoneProcessing asked, string willBe)
    {
        _registry.Set(MicrophoneAdvice.VantageKey, MicrophoneAdvice.VantageValue, "1");
        var off = WithClass() with { Used = asked, Off = true };

        var lines = Lines(off);
        var none = Lines(off with { Microphone = null, Why = null });

        Assert.That(lines[2], Is.EqualTo($"Microphone: \"{LaptopMicrophones.Array.Name}\" (Windows' default microphone), switched off until you press V; {willBe}"));
        Assert.That(lines, Does.Contain(VantageOn), "warned about at the start, not at V");
        Assert.That(none[2], Is.EqualTo("Microphone: switched off until you press V; none to record yet (waiting for a microphone)"));
    }

    /// <summary>Following any app that holds the microphone, the lines say "the class app" instead of a name.</summary>
    [Test]
    public void Following_any_app_names_the_class_app()
    {
        var visible = Lines(WithClass(follows: new FollowRule.Any(), appMute: ClassAppMute.Visible));
        var invisible = Lines(WithClass(follows: new FollowRule.Any(), appMute: ClassAppMute.Invisible));

        Assert.That(visible[1], Is.EqualTo("Your voice is recorded while the class app is listening: when you mute in the class app, ClassScribe stops too. V switches it off by hand."));
        Assert.That(invisible[1], Does.StartWith("The class app keeps the microphone open while you are muted"));
    }

    /// <summary>The microphone line says how Windows processes the audio: RAW, Default because RAW was refused, or Default as asked.</summary>
    [Test]
    public void The_microphone_line_says_RAW_or_why_not()
    {
        var raw = Lines(WithClass())[2];
        var refused = Lines(WithClass() with { Used = MicrophoneProcessing.Default, RawRefusal = "0x88890027" })[2];
        var asked = Lines(WithClass() with { Used = MicrophoneProcessing.Default })[2];
        var noWhy = Lines(WithClass() with { Why = null })[2];

        Assert.That(raw, Is.EqualTo(ArrayRaw));
        Assert.That(refused, Is.EqualTo("Microphone: \"Microphone Array (Intel® Smart Sound Technology for Digital Microphones)\" (Windows' default microphone), with Windows' sound processing (RAW was not available: 0x88890027)"));
        Assert.That(asked, Is.EqualTo("Microphone: \"Microphone Array (Intel® Smart Sound Technology for Digital Microphones)\" (Windows' default microphone), with Windows' sound processing"));
        Assert.That(noWhy, Is.EqualTo("Microphone: \"Microphone Array (Intel® Smart Sound Technology for Digital Microphones)\", without Windows' sound processing (RAW)"));
    }

    /// <summary>Vantage's noise cancelling is warned about when it is on and the microphone is the built-in array, with the mode's number and its likely label.</summary>
    [Test]
    public void Vantage_noise_cancelling_is_warned_about_for_the_array()
    {
        _registry.Set(MicrophoneAdvice.VantageKey, MicrophoneAdvice.VantageValue, "1");
        var multipleVoices = Warnings(WithClass());
        _registry.Set(MicrophoneAdvice.VantageKey, MicrophoneAdvice.VantageValue, "4");
        var onlyMyVoice = Warnings(WithClass());
        _registry.Set(MicrophoneAdvice.VantageKey, MicrophoneAdvice.VantageValue, "10");
        var unlabelled = Warnings(WithClass());

        Assert.That(multipleVoices, Is.EqualTo([VantageOn]));
        Assert.That(onlyMyVoice, Is.EqualTo([$"Lenovo Vantage's microphone noise cancelling is on (mode 4, probably \"Only My Voice\"); it can cut out a lecturer across the room. {VantageOff}"]));
        Assert.That(unlabelled, Is.EqualTo([$"Lenovo Vantage's microphone noise cancelling is on (mode 10); it can cut out a lecturer across the room. {VantageOff}"]));
    }

    /// <summary>Vantage off, or not installed (the value missing), and a microphone other than the array, each give no Vantage warning.</summary>
    [Test]
    public void Vantage_is_not_warned_about_when_off_or_absent_or_for_another_microphone()
    {
        var off = Warnings(WithClass());
        _registry.Remove(MicrophoneAdvice.VantageKey, MicrophoneAdvice.VantageValue);
        var absent = Warnings(WithClass());
        _registry.Set(MicrophoneAdvice.VantageKey, MicrophoneAdvice.VantageValue, "1");
        var obsbot = Warnings(WithClass(LaptopMicrophones.Obsbot));

        Assert.That(off, Is.Empty);
        Assert.That(absent, Is.Empty);
        Assert.That(obsbot, Is.Empty);
    }

    /// <summary>Windows' ducking is warned about with the class unless it is set to Do nothing: missing (Windows' default of 80%), 80%, 50% and mute each in their words.</summary>
    [Test]
    public void Ducking_is_warned_about_unless_set_to_do_nothing()
    {
        var doNothing = Warnings(WithClass());
        _registry.Remove(MicrophoneAdvice.DuckingKey, MicrophoneAdvice.DuckingValue);
        var missing = Warnings(WithClass());
        _registry.Set(MicrophoneAdvice.DuckingKey, MicrophoneAdvice.DuckingValue, 1);
        var eighty = Warnings(WithClass());
        _registry.Set(MicrophoneAdvice.DuckingKey, MicrophoneAdvice.DuckingValue, 2);
        var fifty = Warnings(WithClass());
        _registry.Set(MicrophoneAdvice.DuckingKey, MicrophoneAdvice.DuckingValue, 0);
        var mute = Warnings(WithClass());

        Assert.That(doNothing, Is.Empty);
        Assert.That(missing, Is.EqualTo([Ducking80]));
        Assert.That(eighty, Is.EqualTo([Ducking80]));
        Assert.That(fifty, Is.EqualTo(["Windows lowers other sounds by 50% while a microphone is open, which can make the class quieter, in the recording too. Set Sound > More sound settings > Communications to Do nothing."]));
        Assert.That(mute, Is.EqualTo(["Windows mutes other sounds while a microphone is open, which silences the class, in the recording too. Set Sound > More sound settings > Communications to Do nothing."]));
    }

    /// <summary>In person there is no class to duck, so ducking is not mentioned whatever it is set to.</summary>
    [Test]
    public void Ducking_is_not_mentioned_in_person()
    {
        _registry.Remove(MicrophoneAdvice.DuckingKey, MicrophoneAdvice.DuckingValue);

        Assert.That(Warnings(InPersonStart()), Is.Empty);
    }

    /// <summary>A Bluetooth microphone is warned about, with and without the class, in the right words for each.</summary>
    [Test]
    public void A_Bluetooth_microphone_is_warned_about()
    {
        var withClass = Warnings(WithClass(LaptopMicrophones.PixelBuds));
        var inPerson = Warnings(InPersonStart(LaptopMicrophones.PixelBuds));
        var array = Warnings(WithClass());

        Assert.That(withClass, Is.EqualTo(["The microphone \"Headset (2- Pixel Buds Pro 2)\" is Bluetooth: using it switches the earbuds to call quality, for the class too. The laptop's microphone array avoids that."]));
        Assert.That(inPerson, Is.EqualTo(["The microphone \"Headset (2- Pixel Buds Pro 2)\" is Bluetooth: using it switches the earbuds to call quality. The laptop's microphone array avoids that."]));
        Assert.That(array, Is.Empty);
    }

    /// <summary>
    /// When the recording moves to another microphone mid-class, the lines for it say what the start would have said about
    /// it: the Bluetooth warning for earbuds the class app picks, in the words for the class or for in person, and nothing
    /// for a USB microphone.
    /// </summary>
    [Test]
    public void A_move_to_a_Bluetooth_microphone_is_warned_about_and_a_move_to_another_is_not()
    {
        _advice.AtStart(WithClass());

        var buds = _advice.ForMicrophone(LaptopMicrophones.PixelBuds, withClass: true);
        var obsbot = _advice.ForMicrophone(LaptopMicrophones.Obsbot, withClass: true);
        var budsInPerson = _advice.ForMicrophone(LaptopMicrophones.PixelBuds, withClass: false);

        Assert.That(buds.Select(line => line.Text), Is.EqualTo(["The microphone \"Headset (2- Pixel Buds Pro 2)\" is Bluetooth: using it switches the earbuds to call quality, for the class too. The laptop's microphone array avoids that."]));
        Assert.That(buds.Select(line => line.Warning), Is.EqualTo([true]));
        Assert.That(obsbot, Is.Empty);
        Assert.That(budsInPerson.Select(line => line.Text), Is.EqualTo(["The microphone \"Headset (2- Pixel Buds Pro 2)\" is Bluetooth: using it switches the earbuds to call quality. The laptop's microphone array avoids that."]));
    }

    /// <summary>
    /// The watch on Vantage's mode follows the microphone recorded: a move to the array warns when noise cancelling is on
    /// and begins the watch, and a move away from it ends the watch, since Vantage's setting matters to the array alone.
    /// </summary>
    [Test]
    public void The_Vantage_watch_follows_the_microphone_recorded()
    {
        _registry.Set(MicrophoneAdvice.VantageKey, MicrophoneAdvice.VantageValue, "1");
        _advice.AtStart(WithClass(LaptopMicrophones.Obsbot));

        var toArray = _advice.ForMicrophone(LaptopMicrophones.Array, withClass: true);
        _registry.Set(MicrophoneAdvice.VantageKey, MicrophoneAdvice.VantageValue, "4");
        var changed = _advice.VantageNow();
        var toObsbot = _advice.ForMicrophone(LaptopMicrophones.Obsbot, withClass: true);
        _registry.Set(MicrophoneAdvice.VantageKey, MicrophoneAdvice.VantageValue, "1");
        var notWatched = _advice.VantageNow();

        Assert.That(toArray.Select(line => (line.Text, line.Warning)), Is.EqualTo([(VantageOn, true)]));
        Assert.That(changed, Does.StartWith("Lenovo Vantage's microphone noise cancelling has changed to mode 4"));
        Assert.That(toObsbot, Is.Empty);
        Assert.That(notWatched, Is.Null, "Vantage does not matter to the OBSBOT");
    }

    /// <summary>
    /// A registry read that fails mid-class says nothing, and leaves the mode last read as it was, so that nothing is
    /// reported as switched off or on when the read recovers; only a real change is.
    /// </summary>
    [Test]
    public void A_read_that_fails_mid_class_keeps_the_mode_last_read()
    {
        _registry.Set(MicrophoneAdvice.VantageKey, MicrophoneAdvice.VantageValue, "1");
        _advice.AtStart(WithClass());
        _registry.Failure = new IOException("The registry is busy.");

        var failed = _advice.VantageNow();
        var failedAgain = _advice.VantageNow();
        _registry.Failure = null;
        var recovered = _advice.VantageNow();
        _registry.Set(MicrophoneAdvice.VantageKey, MicrophoneAdvice.VantageValue, "4");
        var changed = _advice.VantageNow();

        Assert.That(failed, Is.Null);
        Assert.That(failedAgain, Is.Null);
        Assert.That(recovered, Is.Null, "the mode is still 1: nothing changed");
        Assert.That(changed, Does.StartWith("Lenovo Vantage's microphone noise cancelling has changed to mode 4"));
        Assert.That(_log.Problems, Is.EqualTo(["Lenovo Vantage's microphone mode could not be read from the registry, so it is not checked: The registry is busy."]));
    }

    /// <summary>
    /// When the mode could not be read at the start and the read recovers mid-class, the mode is reported as it is found,
    /// on or not, rather than as switched on, because nothing is known to compare with.
    /// </summary>
    [Test]
    public void A_mode_first_read_mid_class_is_reported_as_found()
    {
        _registry.Set(MicrophoneAdvice.VantageKey, MicrophoneAdvice.VantageValue, "1");
        _registry.Failure = new IOException("The registry is busy.");
        _advice.AtStart(WithClass());
        _registry.Failure = null;

        var found = _advice.VantageNow();
        var unchanged = _advice.VantageNow();

        Assert.That(found, Is.EqualTo(VantageOn));
        Assert.That(unchanged, Is.Null);
    }

    /// <summary>
    /// With no microphone at the start (none connected, or the named one missing), the privacy lines, the case line and the
    /// ducking warning still come, the microphone line says what is waited for, and the lines about a microphone come when
    /// one is chosen, through the lines for a move; the watch on Vantage begins then.
    /// </summary>
    [Test]
    public void Without_a_microphone_at_the_start_the_lines_say_what_is_waited_for()
    {
        _registry.Set(MicrophoneAdvice.VantageKey, MicrophoneAdvice.VantageValue, "1").Remove(MicrophoneAdvice.DuckingKey, MicrophoneAdvice.DuckingValue);
        var withClass = WithClass() with { Microphone = null, Why = null, WaitingFor = "the microphone \"OBSBOT\"" };
        var inPerson = InPersonStart() with { Microphone = null, Why = null };

        var classLines = Lines(withClass);
        var vantageBefore = _advice.VantageNow();
        var chosen = _advice.ForMicrophone(LaptopMicrophones.Array, withClass: true);
        _registry.Set(MicrophoneAdvice.VantageKey, MicrophoneAdvice.VantageValue, "4");
        var vantageAfter = _advice.VantageNow();
        var roomLines = Lines(inPerson);

        Assert.That(classLines[0], Is.EqualTo(Privacy));
        Assert.That(classLines[1], Does.StartWith("ClassScribe has not been told whether chrome lets go of the microphone"));
        Assert.That(classLines[2], Is.EqualTo("Microphone: waiting for the microphone \"OBSBOT\""));
        Assert.That(classLines[3], Is.EqualTo(Ducking80));
        Assert.That(classLines, Has.Count.EqualTo(4), "no Vantage or Bluetooth line without a microphone");
        Assert.That(vantageBefore, Is.Null, "not watched until a microphone is recorded");
        Assert.That(chosen.Select(line => line.Text), Is.EqualTo([VantageOn]));
        Assert.That(vantageAfter, Does.StartWith("Lenovo Vantage's microphone noise cancelling has changed to mode 4"));
        Assert.That(roomLines, Is.EqualTo([RoomPrivacy, "Microphone: waiting for a microphone", InPerson]));
    }

    /// <summary>When the class plays on a device the room hears, the microphone records it again: warned at the start, on speakers and on a display's audio alike, not on headphones.</summary>
    [Test]
    public void An_echo_is_warned_about_when_the_class_plays_in_the_room()
    {
        var speakers = Warnings(WithClass() with { ClassDeviceName = "Speakers (Realtek(R) Audio)", ClassHeardInTheRoom = true });
        var hdmi = Warnings(WithClass() with { ClassDeviceName = "LG TV (NVIDIA High Definition Audio)", ClassHeardInTheRoom = true });
        var headphones = Warnings(WithClass());

        Assert.That(speakers, Is.EqualTo(["The class plays on \"Speakers (Realtek(R) Audio)\", so the microphone records it a second time (an echo). Use headphones."]));
        Assert.That(hdmi, Is.EqualTo(["The class plays on \"LG TV (NVIDIA High Definition Audio)\", so the microphone records it a second time (an echo). Use headphones."]));
        Assert.That(headphones, Is.Empty);
    }

    /// <summary>The echo check is made again whenever the class moves: a device heard in the room gives the line, another gives nothing.</summary>
    [Test]
    public void The_echo_check_follows_the_class_to_another_device()
    {
        Assert.That(_advice.ForClassDevice("Speakers (Realtek(R) Audio)", heardInTheRoom: true),
            Is.EqualTo("The class plays on \"Speakers (Realtek(R) Audio)\", so the microphone records it a second time (an echo). Use headphones."));
        Assert.That(_advice.ForClassDevice("Headphones (Pixel Buds Pro 2)", heardInTheRoom: false), Is.Null);
    }

    /// <summary>In person the class is not there to echo, whatever plays in the room.</summary>
    [Test]
    public void No_echo_line_in_person()
    {
        var lines = Lines(InPersonStart() with { ClassDeviceName = "Speakers (Realtek(R) Audio)", ClassHeardInTheRoom = true });

        Assert.That(lines, Has.None.Contains("echo"));
    }

    /// <summary>In person: the room's privacy line, the microphone, then the advice about power, breaks and V; the last is not a warning.</summary>
    [Test]
    public void In_person_the_lines_are_the_room_the_microphone_and_the_battery_advice()
    {
        var start = InPersonStart();

        Assert.That(Lines(start), Is.EqualTo([RoomPrivacy, ArrayRaw, InPerson]));
        Assert.That(Warnings(start), Is.Empty);
    }

    /// <summary>With the class, every warning comes after the lines that say what is recorded, in the order of the design: Vantage, ducking, Bluetooth, echo.</summary>
    [Test]
    public void The_warnings_come_in_order_after_the_lines_about_what_is_recorded()
    {
        _registry.Set(MicrophoneAdvice.VantageKey, MicrophoneAdvice.VantageValue, "1").Remove(MicrophoneAdvice.DuckingKey, MicrophoneAdvice.DuckingValue);
        var start = WithClass(LaptopMicrophones.Array with { IsBluetooth = true }) with { ClassDeviceName = "Speakers (Realtek(R) Audio)", ClassHeardInTheRoom = true };

        var lines = _advice.AtStart(start);

        Assert.That(lines.Select(line => line.Warning), Is.EqualTo([false, false, false, true, true, true, true]));
        Assert.That(lines[3].Text, Does.StartWith("Lenovo Vantage"));
        Assert.That(lines[4].Text, Does.StartWith("Windows lowers"));
        Assert.That(lines[5].Text, Does.StartWith("The microphone \"Microphone Array"));
        Assert.That(lines[6].Text, Does.StartWith("The class plays on"));
    }

    /// <summary>A recording of the class alone has no microphone to advise about.</summary>
    [Test]
    public void A_class_only_recording_gets_no_lines()
    {
        Assert.That(_advice.AtStart(WithClass() with { Mode = RecordingMode.Class }), Is.Empty);
    }

    /// <summary>
    /// Vantage's mode is read again while recording the array, and a line comes only when it changes: switched on, changed to
    /// another mode, or off. Nothing comes while it stays as it was, and nothing before the start has been advised.
    /// </summary>
    [Test]
    public void A_change_of_Vantage_mode_is_noticed_once()
    {
        Assert.That(_advice.VantageNow(), Is.Null, "nothing to compare with before the start");
        _registry.Set(MicrophoneAdvice.VantageKey, MicrophoneAdvice.VantageValue, "1");
        _advice.AtStart(WithClass());

        var unchanged = _advice.VantageNow();
        _registry.Set(MicrophoneAdvice.VantageKey, MicrophoneAdvice.VantageValue, "4");
        var changed = _advice.VantageNow();
        var changedAgain = _advice.VantageNow();
        _registry.Set(MicrophoneAdvice.VantageKey, MicrophoneAdvice.VantageValue, "0");
        var off = _advice.VantageNow();
        _registry.Remove(MicrophoneAdvice.VantageKey, MicrophoneAdvice.VantageValue);
        var removed = _advice.VantageNow();
        _registry.Set(MicrophoneAdvice.VantageKey, MicrophoneAdvice.VantageValue, "1");
        var on = _advice.VantageNow();

        Assert.That(unchanged, Is.Null);
        Assert.That(changed, Is.EqualTo($"Lenovo Vantage's microphone noise cancelling has changed to mode 4 (probably \"Only My Voice\"); it can cut out a lecturer across the room. {VantageOff}"));
        Assert.That(changedAgain, Is.Null);
        Assert.That(off, Is.EqualTo("Lenovo Vantage's microphone noise cancelling is now off."));
        Assert.That(removed, Is.Null, "off and missing are the same thing");
        Assert.That(on, Is.EqualTo($"Lenovo Vantage's microphone noise cancelling has switched on (mode 1, probably \"Multiple Voices\"); it can cut out a lecturer across the room. {VantageOff}"));
    }

    /// <summary>Vantage's setting matters to the built-in array only, so it is not read again while another microphone is recorded.</summary>
    [Test]
    public void Vantage_is_not_read_again_for_another_microphone()
    {
        _advice.AtStart(WithClass(LaptopMicrophones.Obsbot));
        var reads = _registry.Reads;
        _registry.Set(MicrophoneAdvice.VantageKey, MicrophoneAdvice.VantageValue, "4");

        Assert.That(_advice.VantageNow(), Is.Null);
        Assert.That(_registry.Reads, Is.EqualTo(reads));
    }

    /// <summary>
    /// A registry that cannot be read does not stop a class from being recorded: the advice that needs it is left out, the
    /// failure is logged once for each value, and the other lines still come.
    /// </summary>
    [Test]
    public void A_registry_that_cannot_be_read_skips_that_advice_and_warns_once()
    {
        _registry.Failure = new SecurityException("Requested registry access is not allowed.");

        var lines = Lines(WithClass());
        var later = _advice.VantageNow();
        var again = _advice.VantageNow();

        Assert.That(lines, Has.Count.EqualTo(3), "the privacy line, the case line and the microphone line still come");
        Assert.That(lines[0], Is.EqualTo(Privacy));
        Assert.That(lines[2], Is.EqualTo(ArrayRaw));
        Assert.That(lines, Has.None.Contains("Vantage").And.None.Contains("Windows lowers"));
        Assert.That(later, Is.Null);
        Assert.That(again, Is.Null);
        Assert.That(_log.Problems, Is.EqualTo([
            "Lenovo Vantage's microphone mode could not be read from the registry, so it is not checked: Requested registry access is not allowed.",
            "Windows' ducking setting could not be read from the registry, so it is not checked: Requested registry access is not allowed."]));
    }

    /// <summary>The advice resolves from the app's registrations for a run, with the real registry behind it on Windows.</summary>
    [Test, Platform("Win")]
    [SupportedOSPlatform("windows")]
    public void Resolves_from_the_apps_registrations_on_Windows()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddAudio();
        services.AddAudioUseCases();
        services.AddDesktop();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = provider.CreateScope();

        var advice = scope.ServiceProvider.GetRequiredService<MicrophoneAdvice>();

        Assert.That(advice, Is.SameAs(scope.ServiceProvider.GetRequiredService<MicrophoneAdvice>()), "one for the run, which remembers Vantage's last mode");
        Assert.That(provider.GetRequiredService<IRegistryValues>(), Is.InstanceOf<RegistryValues>());
        Assert.That(() => provider.GetRequiredService<MicrophoneAdvice>(), Throws.InvalidOperationException, "the advice belongs to a run");
    }
}
