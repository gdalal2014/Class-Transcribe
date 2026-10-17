using ClassScribe.Audio;
using ClassScribe.Audio.Microphones;
using ClassScribe.Commands;
using ClassScribe.Settings;

namespace ClassScribe.Tests;

/// <summary>
/// Menu choice 7, driven by a scripted keyboard over this laptop's microphones: W records the microphone with the class, I
/// alone in person, N none; then a number picks a microphone and Enter leaves it to the settings, or Windows' default; with
/// the class, one more question says which app the microphone follows. The pick holds for the session, and the menu says
/// which settings would keep it. Nothing here opens a microphone: the menu only lists them.
/// </summary>
[TestFixture]
public class MicrophoneMenuTests
{
    private const string Chrome = "chrome";

    private TempFolder _settingsFolder = null!;
    private TestSettings _settings = null!;
    private TestOptions<RecordingOptions> _recording = null!;
    private FakeMicrophoneProvider _microphones = null!;
    private SourceChoice _choice = null!;
    private FakeConsoleUi _ui = null!;

    /// <summary>Connects this laptop's microphones, with Stereo Mix among them, over settings with their defaults, and a console to type into.</summary>
    [SetUp]
    public void SetUp()
    {
        _settingsFolder = new TempFolder();
        _settings = new TestSettings(_settingsFolder.Path);
        _recording = TestOptions.Create(new RecordingOptions());
        _microphones = new FakeMicrophoneProvider().Connect(LaptopMicrophones.Array, LaptopMicrophones.PixelBuds, LaptopMicrophones.Soundcore, LaptopMicrophones.Obsbot, LaptopMicrophones.StereoMix);
        _choice = new SourceChoice(_recording);
        _ui = new FakeConsoleUi();
    }

    /// <summary>Deletes the settings folder.</summary>
    [TearDown]
    public void TearDown() => _settingsFolder.Dispose();

    /// <summary>Runs the menu until it is left, failing the test rather than hanging if it never is.</summary>
    private Task<int> RunAsync() =>
        new MicrophoneMenu(_microphones, _choice, _settings.File, _recording, _ui).RunAsync().WaitAsync(TimeSpan.FromSeconds(15));

    /// <summary>Everything shown so far, line by line.</summary>
    private string[] Lines() => _ui.Screen.Split(Environment.NewLine);

    /// <summary>The plan that records the class from the device it plays on and the microphone <paramref name="microphone"/>, following <paramref name="follows"/>.</summary>
    private static SourcePlan WithMicrophone(string? microphone, FollowRule? follows) =>
        new(RecordingMode.ClassAndMicrophone, new ClassSource.Automatic(), new MicrophoneChoice(microphone, follows));

    /// <summary>The plan that records the microphone <paramref name="microphone"/> alone, in person.</summary>
    private static SourcePlan InPerson(string? microphone) => new(RecordingMode.Microphone, null, new MicrophoneChoice(microphone));

    /// <summary>
    /// W lists the microphones with their notes, built in, default and raw for the array, USB for the webcam's, and for a
    /// Bluetooth one that it is never chosen unless picked here, but not Stereo Mix, which is never recorded; the number
    /// picked is pinned by its full name, and the menu says which settings keep that.
    /// </summary>
    [Test]
    public async Task W_lists_the_microphones_with_their_notes_and_pins_the_one_picked()
    {
        _ui.Type('w', '4');
        _ui.TypeLine("");

        Assert.That(await RunAsync(), Is.EqualTo(0));

        Assert.That(_choice.Current, Is.EqualTo(WithMicrophone(LaptopMicrophones.Obsbot.Name, new FollowRule.Named(Chrome))));
        Assert.That(Lines(), Has.Member($"  1  {LaptopMicrophones.Array.Name}  (built in, default, raw)")
            .And.Member($"  2  {LaptopMicrophones.PixelBuds.Name}  (Bluetooth: never chosen unless picked here; call-quality sound)")
            .And.Member($"  3  {LaptopMicrophones.Soundcore.Name}  (Bluetooth: never chosen unless picked here; call-quality sound)")
            .And.Member($"  4  {LaptopMicrophones.Obsbot.Name}  (USB, raw)"));
        Assert.That(_ui.Screen, Does.Not.Contain("Stereo Mix"), "an input that records what the computer plays is never recorded");
        Assert.That(Lines(), Has.Member(
            $"Recording from the device the class plays on, and your microphone \"{LaptopMicrophones.Obsbot.Name}\" (following chrome) until ClassScribe is closed, whatever the settings say. " +
            $"To keep that, set Recording:Mode to \"ClassAndMicrophone\", Recording:Microphone to \"{LaptopMicrophones.Obsbot.Name}\" and Recording:MicrophoneFollows to \"chrome\" in {_settings.File.Path}."));
    }

    /// <summary>
    /// Enter instead of a number leaves the microphone to the settings: Windows' default microphone, never a Bluetooth one,
    /// while Recording:Microphone is empty, which the question and the header say.
    /// </summary>
    [Test]
    public async Task W_with_Enter_leaves_the_microphone_to_Windows_default()
    {
        _ui.Type('w', '\r');
        _ui.TypeLine("");

        Assert.That(await RunAsync(), Is.EqualTo(0));

        Assert.That(_choice.Current, Is.EqualTo(WithMicrophone(null, new FollowRule.Named(Chrome))));
        Assert.That(Lines(), Has.Member("Press a number, Enter for Windows' default microphone (never a Bluetooth one), or Esc to go back."));
        Assert.That(Lines(), Has.Member(
            "Recording from the device the class plays on, and Windows' default microphone (following chrome) until ClassScribe is closed, whatever the settings say. " +
            $"To keep that, set Recording:Mode to \"ClassAndMicrophone\" and Recording:MicrophoneFollows to \"chrome\", and leave Recording:Microphone empty in {_settings.File.Path}."));
    }

    /// <summary>When Recording:Microphone names a microphone, Enter keeps to it, and the question says so.</summary>
    [Test]
    public async Task W_with_Enter_keeps_the_microphone_the_settings_name()
    {
        _recording.Set(new RecordingOptions { Microphone = "OBSBOT" });
        _ui.Type('w', '\r');
        _ui.TypeLine("");

        Assert.That(await RunAsync(), Is.EqualTo(0));

        Assert.That(_choice.Current, Is.EqualTo(WithMicrophone(null, new FollowRule.Named(Chrome))));
        Assert.That(Lines(), Has.Member("Press a number, Enter for the microphone Recording:Microphone names, \"OBSBOT\", or Esc to go back."));
        Assert.That(Lines(), Has.Member(
            "Recording from the device the class plays on, and your microphone \"OBSBOT\" (following chrome) until ClassScribe is closed, whatever the settings say. " +
            $"To keep that, set Recording:Mode to \"ClassAndMicrophone\" and Recording:MicrophoneFollows to \"chrome\", and keep Recording:Microphone as \"OBSBOT\" in {_settings.File.Path}."));
    }

    /// <summary>I records the microphone alone, in person: no class, so no app to follow and no question about one.</summary>
    [Test]
    public async Task I_records_the_microphone_alone_in_person()
    {
        _ui.Type('i', '1');

        Assert.That(await RunAsync(), Is.EqualTo(0));

        Assert.That(_choice.Current, Is.EqualTo(InPerson(LaptopMicrophones.Array.Name)));
        Assert.That(_ui.Screen, Does.Not.Contain("Follow the class app"));
        Assert.That(Lines(), Has.Member(
            $"Recording from your microphone \"{LaptopMicrophones.Array.Name}\", in person until ClassScribe is closed, whatever the settings say. " +
            $"To keep that, set Recording:Mode to \"Microphone\" and Recording:Microphone to \"{LaptopMicrophones.Array.Name}\" in {_settings.File.Path}."));
    }

    /// <summary>N records the class alone, whatever Recording:Mode says, and the menu says which setting keeps that.</summary>
    [Test]
    public async Task N_records_the_class_alone()
    {
        _recording.Set(new RecordingOptions { Mode = RecordingMode.ClassAndMicrophone });
        _ui.Type('n');

        Assert.That(await RunAsync(), Is.EqualTo(0));

        Assert.That(_choice.Current, Is.EqualTo(new SourcePlan(RecordingMode.Class, new ClassSource.Automatic(), null)));
        Assert.That(_ui.Screen, Does.Not.Contain("Microphones:"), "no microphone to pick when none is recorded");
        Assert.That(Lines(), Has.Member($"Recording from the device the class plays on until ClassScribe is closed, whatever the settings say. To keep that, set Recording:Mode to \"Class\" in {_settings.File.Path}."));
    }

    /// <summary>
    /// With the class, the question which app to follow takes Enter for the app in force, chrome from the settings here,
    /// another app's name, with .exe dropped, A for any app that holds the microphone, N for none, and the words auto and
    /// none as the settings take them.
    /// </summary>
    [TestCase("", "named", "chrome")]
    [TestCase("zoom", "named", "zoom")]
    [TestCase("Zoom.exe", "named", "Zoom")]
    [TestCase("a", "any", null)]
    [TestCase("A", "any", null)]
    [TestCase("auto", "any", null)]
    [TestCase("n", "none", null)]
    [TestCase("N", "none", null)]
    [TestCase("none", "none", null)]
    public async Task The_follow_question_takes_Enter_a_name_A_or_N(string answer, string kind, string? namePart)
    {
        _ui.Type('w', '1');
        _ui.TypeLine(answer);

        Assert.That(await RunAsync(), Is.EqualTo(0));

        FollowRule expected = kind switch
        {
            "named" => new FollowRule.Named(namePart!),
            "any" => new FollowRule.Any(),
            _ => new FollowRule.None()
        };
        Assert.That(_choice.Current, Is.EqualTo(WithMicrophone(LaptopMicrophones.Array.Name, expected)));
        Assert.That(Lines(), Has.Member("Follow the class app: Enter keeps \"chrome\", type another app's name, A follows any app that holds the microphone, N follows none (V only)."));
        var keeps = kind switch { "named" => namePart, "any" => "auto", _ => "none" };
        Assert.That(_ui.Screen, Does.Contain($"Recording:MicrophoneFollows to \"{keeps}\""));
    }

    /// <summary>What the header says of each rule: the app followed, any app, or V alone.</summary>
    [TestCase("zoom", "(following zoom)")]
    [TestCase("a", "(following any app that holds the microphone)")]
    [TestCase("n", "(whenever V is on)")]
    public async Task The_header_says_which_app_is_followed(string answer, string words)
    {
        _ui.Type('w', '1');
        _ui.TypeLine(answer);

        await RunAsync();

        Assert.That(_choice.Description, Is.EqualTo($"the device the class plays on, and your microphone \"{LaptopMicrophones.Array.Name}\" {words}"));
    }

    /// <summary>When the class is recorded from an app, Enter keeps that app, the one being recorded, and the question says so.</summary>
    [Test]
    public async Task The_follow_question_keeps_the_app_being_recorded()
    {
        _choice.App = "msedge";
        _ui.Type('w', '1');
        _ui.TypeLine("");

        await RunAsync();

        Assert.That(_choice.Current, Is.EqualTo(new SourcePlan(RecordingMode.ClassAndMicrophone, new ClassSource.App("msedge"),
            new MicrophoneChoice(LaptopMicrophones.Array.Name, new FollowRule.Named("msedge")))));
        Assert.That(Lines(), Has.Member("Follow the class app: Enter keeps \"msedge\", the app being recorded, type another app's name, A follows any app that holds the microphone, N follows none (V only)."));
    }

    /// <summary>
    /// A name that is part of ClassScribe's own is refused, with why: ClassScribe's own recording holds the microphone for
    /// the whole class, so following it would record everything. The app in force is kept.
    /// </summary>
    [TestCase("ClassScribe")]
    [TestCase("scribe.exe")]
    public async Task An_app_that_is_ClassScribe_itself_is_refused_and_the_app_in_force_kept(string answer)
    {
        _ui.Type('w', '1');
        _ui.TypeLine(answer);

        Assert.That(await RunAsync(), Is.EqualTo(0));

        Assert.That(_choice.Current, Is.EqualTo(WithMicrophone(LaptopMicrophones.Array.Name, new FollowRule.Named(Chrome))));
        Assert.That(Lines(), Has.Member($"\"{answer}\" names ClassScribe itself, whose own recording holds the microphone for the whole class; following it would record everything. Keeping \"chrome\"."));
    }

    /// <summary>Esc at any question goes back with the choice as it was, and nothing about settings is said.</summary>
    [TestCase("first")]
    [TestCase("microphone")]
    [TestCase("follow")]
    public async Task Esc_at_any_question_goes_back_without_a_change(string at)
    {
        switch (at)
        {
            case "first":
                _ui.Type('\u001b');
                break;
            case "microphone":
                _ui.Type('w', '\u001b');
                break;
            default:
                _ui.Type('w', '1');
                _ui.TypeLine(null);
                break;
        }

        Assert.That(await RunAsync(), Is.EqualTo(0));

        Assert.That(_choice.Current, Is.EqualTo(new SourcePlan(RecordingMode.Class, new ClassSource.Automatic(), null)));
        Assert.That(_ui.Screen, Does.Not.Contain("To keep that"));
    }

    /// <summary>Keys that are none of the menu's are ignored until one of them comes.</summary>
    [Test]
    public async Task Other_keys_are_ignored()
    {
        _ui.Type('x', '7', 'i', '9', 'z', '1');

        Assert.That(await RunAsync(), Is.EqualTo(0));

        Assert.That(_choice.Current, Is.EqualTo(InPerson(LaptopMicrophones.Array.Name)));
    }

    /// <summary>Without a microphone connected the list says so, and Enter still leaves the microphone to the settings: the recording waits for one.</summary>
    [Test]
    public async Task Without_microphones_the_list_says_so_and_Enter_still_picks_the_default()
    {
        _microphones = new FakeMicrophoneProvider();
        _ui.Type('i', '\r');

        Assert.That(await RunAsync(), Is.EqualTo(0));

        Assert.That(_choice.Current, Is.EqualTo(InPerson(null)));
        Assert.That(Lines(), Has.Member("  None is connected now; the recording waits for one."));
    }

    /// <summary>Listing the microphones opens nothing: no capture is ever asked for.</summary>
    [Test]
    public async Task Listing_the_microphones_opens_nothing()
    {
        _ui.Type('w', '2');
        _ui.TypeLine("");

        await RunAsync();

        Assert.That(_microphones.Listings, Is.EqualTo(1));
        Assert.That(_microphones.Opened, Is.Empty);
    }

    /// <summary>The pick holds for the session, whatever the settings say afterwards.</summary>
    [Test]
    public async Task The_pick_holds_for_the_session_whatever_the_settings_say()
    {
        _ui.Type('i', '4');
        await RunAsync();

        _recording.Set(new RecordingOptions { Mode = RecordingMode.Class, Microphone = "Array" });

        Assert.That(_choice.Current, Is.EqualTo(InPerson(LaptopMicrophones.Obsbot.Name)));
    }
}

/// <summary>
/// The microphone side of the choice of what to record: the mode and the microphone follow Recording:Mode,
/// Recording:Microphone and Recording:MicrophoneFollows until menu 7 picks something, and then the pick, for the rest of
/// the session; the app followed is the app recorded in app mode, or else the setting; and the words for the header.
/// </summary>
[TestFixture]
public class SourceChoiceMicrophoneTests
{
    /// <summary>Settings with the given mode and microphone, and the app the microphone follows, chrome unless given.</summary>
    private static TestOptions<RecordingOptions> Settings(RecordingMode mode = RecordingMode.Class, string microphone = "", string follows = "chrome") =>
        TestOptions.Create(new RecordingOptions { Mode = mode, Microphone = microphone, MicrophoneFollows = follows });

    /// <summary>
    /// Until menu 7 picks something, the plan follows the settings: the class alone, the class and a microphone left to
    /// the run (null, meaning Recording:Microphone, and the app recorded or Recording:MicrophoneFollows), or the microphone
    /// alone in person, where no app is followed.
    /// </summary>
    [Test]
    public void The_mode_comes_from_the_settings_until_menu_7_picks()
    {
        var settings = Settings();
        var choice = new SourceChoice(settings);

        var classAlone = choice.Current;
        settings.Set(new RecordingOptions { Mode = RecordingMode.ClassAndMicrophone, Microphone = "OBSBOT" });
        var withMicrophone = choice.Current;
        settings.Set(new RecordingOptions { Mode = RecordingMode.Microphone });
        var inPerson = choice.Current;

        Assert.That(classAlone, Is.EqualTo(new SourcePlan(RecordingMode.Class, new ClassSource.Automatic(), null)));
        Assert.That(withMicrophone, Is.EqualTo(new SourcePlan(RecordingMode.ClassAndMicrophone, new ClassSource.Automatic(), new MicrophoneChoice(null, null))),
            "the microphone and the app followed are left to the run, which reads the same settings");
        Assert.That(inPerson, Is.EqualTo(new SourcePlan(RecordingMode.Microphone, null, new MicrophoneChoice(null, null))));
        Assert.That(choice.Mode, Is.EqualTo(RecordingMode.Microphone));
    }

    /// <summary>A pick from menu 7 holds for the rest of the session, whatever the settings say next; picking the class alone drops the microphone.</summary>
    [Test]
    public void A_pick_from_menu_7_holds_whatever_the_settings_say()
    {
        var settings = Settings(RecordingMode.Class);
        var choice = new SourceChoice(settings);

        choice.PickMicrophone(RecordingMode.ClassAndMicrophone, " OBSBOT ", new FollowRule.Any());
        settings.Set(new RecordingOptions { Mode = RecordingMode.Microphone, Microphone = "Array", MicrophoneFollows = "none" });
        var picked = choice.Current;
        choice.PickMicrophone(RecordingMode.Class, null, null);
        var classAlone = choice.Current;

        Assert.That(picked, Is.EqualTo(new SourcePlan(RecordingMode.ClassAndMicrophone, new ClassSource.Automatic(), new MicrophoneChoice("OBSBOT", new FollowRule.Any()))));
        Assert.That(classAlone, Is.EqualTo(new SourcePlan(RecordingMode.Class, new ClassSource.Automatic(), null)));
    }

    /// <summary>In person the pick's app is not carried: there is no class app to follow.</summary>
    [Test]
    public void In_person_no_app_is_followed()
    {
        var choice = new SourceChoice(Settings());

        choice.PickMicrophone(RecordingMode.Microphone, "Array", new FollowRule.Named("chrome"));

        Assert.That(choice.Current, Is.EqualTo(new SourcePlan(RecordingMode.Microphone, null, new MicrophoneChoice("Array", null))));
    }

    /// <summary>
    /// The app the microphone follows: the pick's, else the app the class is recorded from, else Recording:MicrophoneFollows
    /// read as the settings read it (auto and none as words, .exe dropped); null when the setting names nothing.
    /// </summary>
    [Test]
    public void The_app_followed_is_the_picks_the_app_recorded_or_the_setting()
    {
        var settings = Settings(RecordingMode.ClassAndMicrophone);
        var choice = new SourceChoice(settings);

        Assert.That(choice.Follows, Is.EqualTo(new FollowRule.Named("chrome")), "the setting");
        settings.Set(new RecordingOptions { MicrophoneFollows = "auto" });
        Assert.That(choice.Follows, Is.EqualTo(new FollowRule.Any()));
        settings.Set(new RecordingOptions { MicrophoneFollows = "Zoom.exe" });
        Assert.That(choice.Follows, Is.EqualTo(new FollowRule.Named("Zoom")));
        settings.Set(new RecordingOptions { MicrophoneFollows = " " });
        Assert.That(choice.Follows, Is.Null, "a setting that names nothing");

        choice.App = "msedge";
        Assert.That(choice.Follows, Is.EqualTo(new FollowRule.Named("msedge")), "the app recorded");
        choice.PickMicrophone(RecordingMode.ClassAndMicrophone, null, new FollowRule.None());
        Assert.That(choice.Follows, Is.EqualTo(new FollowRule.None()), "the pick");
    }

    /// <summary>
    /// The words for the header: the class alone as before; with the microphone, the microphone named, by the pick or the
    /// setting, or Windows' default, and the app followed; in person, the microphone alone.
    /// </summary>
    [Test]
    public void The_description_names_the_microphone_and_the_app_followed()
    {
        var settings = Settings();
        var choice = new SourceChoice(settings);

        Assert.That(choice.Description, Is.EqualTo("the device the class plays on"));
        settings.Set(new RecordingOptions { Mode = RecordingMode.ClassAndMicrophone });
        Assert.That(choice.Description, Is.EqualTo("the device the class plays on, and Windows' default microphone (following chrome)"));
        settings.Set(new RecordingOptions { Mode = RecordingMode.ClassAndMicrophone, Microphone = "OBSBOT", MicrophoneFollows = "none", App = "chrome" });
        Assert.That(choice.Description, Is.EqualTo("the app \"chrome\", and your microphone \"OBSBOT\" (following chrome)"), "in app mode the app recorded is followed");
        settings.Set(new RecordingOptions { Mode = RecordingMode.ClassAndMicrophone, MicrophoneFollows = "auto" });
        Assert.That(choice.Description, Is.EqualTo("the device the class plays on, and Windows' default microphone (following any app that holds the microphone)"));
        settings.Set(new RecordingOptions { Mode = RecordingMode.Microphone, Microphone = "OBSBOT" });
        Assert.That(choice.Description, Is.EqualTo("your microphone \"OBSBOT\", in person"));
        settings.Set(new RecordingOptions { Mode = RecordingMode.Microphone });
        Assert.That(choice.Description, Is.EqualTo("Windows' default microphone, in person"));
    }
}
