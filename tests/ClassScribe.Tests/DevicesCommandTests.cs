using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using ClassScribe.Audio;
using ClassScribe.Audio.Microphones;
using ClassScribe.Commands;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Tests;

/// <summary>
/// The devices command: the output devices, the apps with sound and the microphones, as the simulated devices,
/// microphones and microphone sessions report them, each with the option of record and record-only that records it.
/// Nothing is opened to list them, and nothing is asked for before the command runs.
/// </summary>
[TestFixture]
public class DevicesCommandTests
{
    /// <summary>The name of the output device the tests' laptop plays through by default.</summary>
    private const string Speakers = "Speakers (Realtek(R) Audio)";

    /// <summary>The line that says nothing was opened, which comes after every list.</summary>
    private const string NothingOpened = "Nothing was opened or recorded to list these.";

    /// <summary>The line before <see cref="NothingOpened"/> when anything could not be read, so that a look at the end of the list tells.</summary>
    private const string NotEverything = "Not everything could be read from Windows just now: the lines above say what, and why.";

    /// <summary>What the sessions reader says of the Zoom app's session on a microphone whose state Windows would not give.</summary>
    private const string ZoomSessionUnread = "session 2 of 2 (Zoom, pid 9090) not readable: GetState: The parameter is incorrect. (0x80070057)";

    /// <summary>The example lines at the end: the class alone, the class with your microphone, and in person.</summary>
    private static readonly string[] Examples =
    [
        "For example: ClassScribe record \"Kalam\" --device auto --output \"D:\\Classes\"",
        "  ClassScribe record \"Kalam\" --app chrome --mic --output \"D:\\Classes\"",
        "  ClassScribe record \"Kalam\" --in-person --output \"D:\\Classes\""
    ];

    private FakeConsoleUi _ui = null!;
    private FakeCaptureProvider _devices = null!;
    private FakeMicrophoneProvider _microphones = null!;
    private FakeMicrophoneSessions _sessions = null!;
    private RecordingLogger<DevicesCommand> _log = null!;

    /// <summary>Creates the console the list is written to, the simulated devices and microphones, the simulated microphone sessions, and a log to inspect.</summary>
    [SetUp]
    public void SetUp()
    {
        _ui = new FakeConsoleUi();
        _devices = new FakeCaptureProvider();
        _microphones = new FakeMicrophoneProvider();
        _sessions = new FakeMicrophoneSessions();
        _log = new RecordingLogger<DevicesCommand>();
    }

    /// <summary>The command over the simulated devices, microphones and sessions, which it asks for only when it runs.</summary>
    private DevicesCommand CreateCommand() =>
        new(new Lazy<ILoopbackCaptureProvider>(() => _devices), new Lazy<IMicrophoneProvider>(() => _microphones), new Lazy<IMicrophoneSessions>(() => _sessions), _ui, _log);

    /// <summary>What the command wrote, line by line, with each run of spaces that lines up a column made two spaces.</summary>
    private string[] Lines => [.. _ui.Screen.Split(Environment.NewLine).Select(line => Regex.Replace(line, " {3,}", "  "))];

    /// <summary>The --mic option that picks <paramref name="microphone"/>: its whole name, in quotes.</summary>
    private static string MicOption(Microphone microphone) => $"--mic \"{microphone.Name}\"";

    /// <summary>
    /// The output devices come first, marked default or playing, each with the --device option that records it, and then
    /// --device auto for the device the class plays on; then the apps with sound, one line per app at its loudest, each
    /// with its --app option. ClassScribe's own processes are left out: a recording's own capture shows up as an app with
    /// sound. Then the microphones, each with the --mic option that picks it, and the rule that picks one when none is
    /// named. The list says that nothing was opened, and ends with the examples. Nothing is opened, and the exit code is 0.
    /// </summary>
    [Test]
    public void The_devices_the_apps_with_sound_and_the_microphones_are_listed_with_the_option_that_records_each()
    {
        _devices.ConnectDevice(Speakers);
        _devices.ConnectDevice("Headphones (Pixel Buds Pro 2)", level: 0.3f);
        _devices.AddApp(4242, "chrome", Speakers);
        _devices.AddApp(4243, "chrome", Speakers, level: 0.4f);
        _devices.AddApp(5151, "msedge", Speakers);
        _devices.AddApp(6161, "ClassScribe", Speakers, level: 0.2f);
        _devices.AddApp(Environment.ProcessId, "testhost", Speakers);
        _microphones.Connect(LaptopMicrophones.Array, LaptopMicrophones.Obsbot);

        var exitCode = CreateCommand().Run();

        string[] expected =
        [
            "Output devices:",
            $"  {Speakers}  (default)  --device \"{Speakers}\"",
            "  Headphones (Pixel Buds Pro 2)  (playing)  --device \"Headphones (Pixel Buds Pro 2)\"",
            "  The device the class plays on  --device auto",
            "Apps with sound:",
            $"  chrome  (app on {Speakers}, playing)  --app chrome",
            $"  msedge  (app on {Speakers})  --app msedge",
            "Microphones:",
            $"  {LaptopMicrophones.Array.Name}  (built in, default, raw)  {MicOption(LaptopMicrophones.Array)}",
            $"  {LaptopMicrophones.Obsbot.Name}  (USB, raw)  {MicOption(LaptopMicrophones.Obsbot)}",
            "  Windows' default microphone, never a Bluetooth one  --mic",
            NothingOpened,
            .. Examples
        ];
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(Lines, Is.EqualTo(expected));
        Assert.That(_devices.Created, Is.Empty, "no output device was opened");
        Assert.That(_microphones.Opened, Is.Empty, "no microphone was opened");
    }

    /// <summary>
    /// Each microphone says which apps hold it now, from Windows' own lists: an app taking sound is listening, one that
    /// holds the microphone without taking sound is said so, and a microphone muted in Windows says that too. Windows'
    /// own sounds and ClassScribe's own processes, whose capture holds a microphone during a recording, are left out.
    /// </summary>
    [Test]
    public void Each_microphone_says_which_apps_hold_it_now_and_whether_Windows_mutes_it()
    {
        var array = LaptopMicrophones.Array;
        var obsbot = LaptopMicrophones.Obsbot;
        _microphones.Connect(array, obsbot);
        _sessions.Set(Laptop.Listing(array.Id, Laptop.Session(array.Id, Laptop.ChromeUtility), Laptop.Session(array.Id, Laptop.Vibe, CaptureSessionState.Inactive),
            Laptop.Session(array.Id, Laptop.OwnProcess), Laptop.SystemSounds(array.Id)));
        _sessions.Set(Laptop.Listing(obsbot.Id) with { MutedInWindows = true });

        CreateCommand().Run();

        Assert.That(Lines, Contains.Item($"  {array.Name}  (built in, default, raw; chrome is listening; vibe holds it without listening)  {MicOption(array)}"));
        Assert.That(Lines, Contains.Item($"  {obsbot.Name}  (USB, raw; muted in Windows)  {MicOption(obsbot)}"));
    }

    /// <summary>
    /// An app whose session has expired has let go of the microphone, as a class app that stops its track on mute does,
    /// so it is not listed as holding it; a process that Windows could not name is named by its number.
    /// </summary>
    [Test]
    public void An_expired_session_is_left_out_and_a_process_without_a_name_is_named_by_its_number()
    {
        var array = LaptopMicrophones.Array;
        _microphones.Connect(array);
        _sessions.Set(Laptop.Listing(array.Id, Laptop.Session(array.Id, Laptop.ChromeUtility), Laptop.Session(array.Id, Laptop.Vibe, CaptureSessionState.Expired),
            new CaptureSession("unnamed", array.Id, 9999, ImageName: null, SystemSounds: false, CaptureSessionState.Active, SessionMuted: null)));

        CreateCommand().Run();

        Assert.That(Lines, Contains.Item($"  {array.Name}  (built in, default, raw; chrome is listening; process 9999 is listening)  {MicOption(array)}"));
        Assert.That(_ui.Screen, Does.Not.Contain("vibe"), "an app whose session has expired no longer holds the microphone");
    }

    /// <summary>
    /// A Bluetooth microphone is listed with the warning that it is never chosen unless named, since using it switches
    /// the earbuds to call quality; an input that records what the computer plays is listed as never recorded, and has
    /// no option, since no name picks it.
    /// </summary>
    [Test]
    public void A_Bluetooth_microphone_and_a_loopback_input_say_why_they_are_not_chosen()
    {
        _microphones.Connect(LaptopMicrophones.PixelBuds, LaptopMicrophones.Soundcore, LaptopMicrophones.StereoMix);

        CreateCommand().Run();

        Assert.That(Lines, Contains.Item($"  {LaptopMicrophones.PixelBuds.Name}  (Bluetooth: never chosen unless named; call-quality sound)  {MicOption(LaptopMicrophones.PixelBuds)}"));
        Assert.That(Lines, Contains.Item($"  {LaptopMicrophones.Soundcore.Name}  (Bluetooth: never chosen unless named; call-quality sound)  {MicOption(LaptopMicrophones.Soundcore)}"));
        Assert.That(Lines, Contains.Item($"  {LaptopMicrophones.StereoMix.Name}  (records what the computer plays; never recorded)"));
    }

    /// <summary>The options of the devices, the apps and the microphones line up in one column, so that the one wanted is easy to find.</summary>
    [Test]
    public void The_options_line_up_in_one_column()
    {
        _devices.ConnectDevice(Speakers);
        _devices.AddApp(4242, "chrome", Speakers);
        _microphones.Connect(LaptopMicrophones.Array, LaptopMicrophones.Obsbot);

        CreateCommand().Run();

        var columns = _ui.Screen.Split(Environment.NewLine)
            .Where(line => line.StartsWith("  ", StringComparison.Ordinal) && !line.Contains("ClassScribe record", StringComparison.Ordinal))
            .Select(line => Regex.Match(line, " --(?:device|app|mic)(?: |$)").Index)
            .Where(column => column > 0)
            .Distinct();
        Assert.That(columns.Count(), Is.EqualTo(1));
    }

    /// <summary>
    /// Without devices, apps or microphones each list says so, and the list still offers --device auto, which waits for a
    /// device to play the class, and --mic, which waits for a microphone.
    /// </summary>
    [Test]
    public void Without_devices_apps_or_microphones_the_lists_say_so()
    {
        var exitCode = CreateCommand().Run();

        string[] expected =
        [
            "Output devices:",
            "  None is connected now.",
            "  The device the class plays on  --device auto",
            "Apps with sound:",
            "  None has sound open now.",
            "Microphones:",
            "  None is connected now.",
            "  Windows' default microphone, never a Bluetooth one  --mic",
            NothingOpened,
            .. Examples
        ];
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(Lines, Is.EqualTo(expected));
    }

    /// <summary>
    /// A microphone whose sessions are not in Windows' list, as when the list could not be read and came back empty,
    /// is listed with its own notes and says nothing about apps.
    /// </summary>
    [Test]
    public void A_microphone_missing_from_the_sessions_list_says_nothing_about_apps()
    {
        _microphones.Connect(LaptopMicrophones.Array);

        var exitCode = CreateCommand().Run();

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(Lines, Contains.Item($"  {LaptopMicrophones.Array.Name}  (built in, default, raw)  {MicOption(LaptopMicrophones.Array)}"));
        Assert.That(_sessions.ReadAlls, Is.EqualTo(1), "every microphone's sessions are read in one look");
    }

    /// <summary>The devices, the microphones and the sessions are asked for only when the command runs, so making the command touches no audio device.</summary>
    [Test]
    public void The_devices_and_the_microphones_are_asked_for_only_when_the_command_runs()
    {
        var asked = false;

        _ = new DevicesCommand(
            new Lazy<ILoopbackCaptureProvider>(() =>
            {
                asked = true;
                return _devices;
            }),
            new Lazy<IMicrophoneProvider>(() =>
            {
                asked = true;
                return _microphones;
            }),
            new Lazy<IMicrophoneSessions>(() =>
            {
                asked = true;
                return _sessions;
            }),
            _ui, _log);

        Assert.That(asked, Is.False);
    }

    /// <summary>
    /// The Zoom crash of 18 October: a list Windows will not give, here the output devices and the apps with sound failing
    /// with E_INVALIDARG as .NET's own mapping gives it, an ArgumentException, says so in its place, with the reason and the
    /// result, and the other lists are given in full: the command runs to its end, with exit code 0, rather than stopping
    /// at the first list. The failure, with where it came from, is in the log at Debug.
    /// </summary>
    [Test]
    public void A_list_that_cannot_be_read_says_so_and_the_others_are_listed()
    {
        _devices.FailListings(new ArgumentException("Value does not fall within the expected range."));
        _microphones.Connect(LaptopMicrophones.Array);

        var exitCode = CreateCommand().Run();

        string[] expected =
        [
            "Output devices:",
            "  Could not be read now: Value does not fall within the expected range. (0x80070057)",
            "  The device the class plays on  --device auto",
            "Apps with sound:",
            "  Could not be read now: Value does not fall within the expected range. (0x80070057)",
            "Microphones:",
            $"  {LaptopMicrophones.Array.Name}  (built in, default, raw)  {MicOption(LaptopMicrophones.Array)}",
            "  Windows' default microphone, never a Bluetooth one  --mic",
            NotEverything,
            NothingOpened,
            .. Examples
        ];
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(Lines, Is.EqualTo(expected));
        Assert.That(_log.Entries.Where(entry => entry.Level == LogLevel.Debug).Select(entry => entry.Message), Is.EqualTo(new[]
        {
            "Could not read the output devices for the list (0x80070057)",
            "Could not read the apps with sound for the list (0x80070057)"
        }));
        Assert.That(_log.Problems, Is.Empty);
    }

    /// <summary>
    /// The microphones Windows will not list say so in their place, and the output devices are listed all the same; when
    /// only the apps that hold each microphone cannot be read, whether the reader fails or answers with the reason, the
    /// microphones are listed with their own notes and a line says what is missing.
    /// </summary>
    [TestCase("microphones")]
    [TestCase("sessions failing")]
    [TestCase("sessions with a problem")]
    public void Microphones_or_the_apps_that_hold_them_that_cannot_be_read_say_so(string what)
    {
        _devices.ConnectDevice(Speakers);
        _microphones.Connect(LaptopMicrophones.Array);
        switch (what)
        {
            case "microphones":
                _microphones.ListingFailure = new COMException("The audio service is not running.", unchecked((int)0x88890010));
                break;
            case "sessions failing":
                _sessions.Failure = new COMException("The audio service is not running.", unchecked((int)0x88890010));
                break;
            default:
                _sessions.ListingProblem = "The audio service is not running. (0x88890010)";
                break;
        }

        var exitCode = CreateCommand().Run();

        var microphones = what == "microphones"
            ? new[] { "  Could not be read now: The audio service is not running. (0x88890010)" }
            : [$"  {LaptopMicrophones.Array.Name}  (built in, default, raw)  {MicOption(LaptopMicrophones.Array)}",
               "  Which apps hold each microphone could not be read now: The audio service is not running. (0x88890010)"];
        string[] expected =
        [
            "Output devices:",
            $"  {Speakers}  (default)  --device \"{Speakers}\"",
            "  The device the class plays on  --device auto",
            "Apps with sound:",
            "  None has sound open now.",
            "Microphones:",
            .. microphones,
            "  Windows' default microphone, never a Bluetooth one  --mic",
            NotEverything,
            NothingOpened,
            .. Examples
        ];
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(Lines, Is.EqualTo(expected));
    }

    /// <summary>
    /// A microphone whose sessions could not all be read, as with the Zoom app in a meeting on 18 October, is listed with
    /// the apps that were read, and a line below it says what could not be read, whose it was and which of Windows' calls
    /// failed; the line before the end says that not everything could be read. Exit code 0: the command listed what it could.
    /// </summary>
    [Test]
    public void A_microphone_whose_sessions_could_not_all_be_read_says_what_whose_and_which_call()
    {
        var array = LaptopMicrophones.Array;
        _microphones.Connect(array);
        _sessions.Set(Laptop.Listing(array.Id, Laptop.Session(array.Id, Laptop.ChromeUtility)) with
        {
            Complete = false,
            Problem = ZoomSessionUnread,
            Unread = [new UnreadCaptureSession(Laptop.Zoom, "Zoom", null)]
        });

        var exitCode = CreateCommand().Run();

        string[] expected =
        [
            "Microphones:",
            $"  {array.Name}  (built in, default, raw; chrome is listening)  {MicOption(array)}",
            $"  Could not be read now: {ZoomSessionUnread}",
            "  Windows' default microphone, never a Bluetooth one  --mic",
            NotEverything,
            NothingOpened
        ];
        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(Lines.SkipWhile(line => line != "Microphones:").Take(expected.Length), Is.EqualTo(expected));
        Assert.That(_ui.Screen, Does.Contain($"{Environment.NewLine}    Could not be read now: "), "set in below the microphone it is about");
    }

    /// <summary>
    /// Sessions of the apps with sound that could not be read in full are said below the apps, one line each, with whose
    /// they were when Windows named the app, and which call failed: an app named is listed all the same, a session of no
    /// app named is left out, and a device whose sessions could not be listed says so.
    /// </summary>
    [Test]
    public void Apps_whose_sessions_could_not_all_be_read_say_what_whose_and_which_call()
    {
        _devices.ConnectDevice(Speakers);
        _devices.AddApp(6000, "Zoom", Speakers);
        _devices.SetUnread(
            new UnreadAudioSession(Speakers, "Zoom", "GetState: The parameter is incorrect. (0x80070057)"),
            new UnreadAudioSession(Speakers, null, "GetSession: The parameter is incorrect. (0x80070057)"),
            new UnreadAudioSession("Headphones (Pixel Buds Pro 2)", null, "Activate: The parameter is incorrect. (0x80070057)", WholeDevice: true));

        CreateCommand().Run();

        string[] expected =
        [
            "Apps with sound:",
            $"  Zoom  (app on {Speakers})  --app Zoom",
            $"  Could not be read now: Zoom's audio session on \"{Speakers}\" (GetState: The parameter is incorrect. (0x80070057))",
            $"  Could not be read now: an audio session on \"{Speakers}\" (GetSession: The parameter is incorrect. (0x80070057))",
            "  Could not be read now: the audio sessions on \"Headphones (Pixel Buds Pro 2)\" (Activate: The parameter is incorrect. (0x80070057))",
            "Microphones:"
        ];
        Assert.That(Lines.SkipWhile(line => line != "Apps with sound:").Take(expected.Length), Is.EqualTo(expected));
        Assert.That(Lines, Contains.Item(NotEverything));
    }
}
