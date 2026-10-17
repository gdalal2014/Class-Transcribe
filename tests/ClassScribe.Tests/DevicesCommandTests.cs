using System.Text.RegularExpressions;
using ClassScribe.Audio;
using ClassScribe.Commands;

namespace ClassScribe.Tests;

/// <summary>
/// The devices command: the output devices and the apps with sound, as the simulated devices report them, each with the
/// option of record and record-only that records it. Nothing is opened to list them.
/// </summary>
[TestFixture]
public class DevicesCommandTests
{
    /// <summary>The name of the output device the tests' laptop plays through by default.</summary>
    private const string Speakers = "Speakers (Realtek(R) Audio)";

    private FakeConsoleUi _ui = null!;
    private FakeCaptureProvider _devices = null!;

    /// <summary>Creates the console the list is written to and the simulated devices.</summary>
    [SetUp]
    public void SetUp()
    {
        _ui = new FakeConsoleUi();
        _devices = new FakeCaptureProvider();
    }

    /// <summary>The command over the simulated devices, which it asks for only when it runs.</summary>
    private DevicesCommand CreateCommand() => new(new Lazy<ILoopbackCaptureProvider>(() => _devices), _ui);

    /// <summary>What the command wrote, line by line, with each run of spaces that lines up a column made two spaces.</summary>
    private string[] Lines => [.. _ui.Screen.Split(Environment.NewLine).Select(line => Regex.Replace(line, " {3,}", "  "))];

    /// <summary>
    /// The output devices come first, marked default or playing, each with the --device option that records it, and then
    /// --device auto for the device the class plays on; then the apps with sound, one line per app at its loudest, each
    /// with its --app option. ClassScribe's own processes are left out: a recording's own capture shows up as an app with
    /// sound. The list says that nothing was opened, and ends with an example. Nothing is opened, and the exit code is 0.
    /// </summary>
    [Test]
    public void The_devices_and_the_apps_with_sound_are_listed_with_the_option_that_records_each()
    {
        _devices.ConnectDevice(Speakers);
        _devices.ConnectDevice("Headphones (Pixel Buds Pro 2)", level: 0.3f);
        _devices.AddApp(4242, "chrome", Speakers);
        _devices.AddApp(4243, "chrome", Speakers, level: 0.4f);
        _devices.AddApp(5151, "msedge", Speakers);
        _devices.AddApp(6161, "ClassScribe", Speakers, level: 0.2f);
        _devices.AddApp(Environment.ProcessId, "testhost", Speakers);

        var exitCode = CreateCommand().Run();

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(Lines, Is.EqualTo(new[]
        {
            "Output devices:",
            $"  {Speakers}  (default)  --device \"{Speakers}\"",
            "  Headphones (Pixel Buds Pro 2)  (playing)  --device \"Headphones (Pixel Buds Pro 2)\"",
            "  The device the class plays on  --device auto",
            "Apps with sound:",
            $"  chrome  (app on {Speakers}, playing)  --app chrome",
            $"  msedge  (app on {Speakers})  --app msedge",
            "Nothing was opened or recorded to list these.",
            "For example: ClassScribe record \"Kalam\" --device auto --output \"D:\\Classes\""
        }));
        Assert.That(_devices.Created, Is.Empty, "nothing was opened");
    }

    /// <summary>The options of the devices and the apps line up in one column, so that the one wanted is easy to find.</summary>
    [Test]
    public void The_options_line_up_in_one_column()
    {
        _devices.ConnectDevice(Speakers);
        _devices.AddApp(4242, "chrome", Speakers);

        CreateCommand().Run();

        var columns = _ui.Screen.Split(Environment.NewLine)
            .Where(line => line.StartsWith("  ", StringComparison.Ordinal))
            .Select(line => Regex.Match(line, " --(?:device|app) ").Index)
            .Where(column => column > 0)
            .Distinct();
        Assert.That(columns.Count(), Is.EqualTo(1));
    }

    /// <summary>Without devices or apps the list says so, and still offers --device auto, which waits for a device to play the class.</summary>
    [Test]
    public void Without_devices_or_apps_the_list_says_so()
    {
        var exitCode = CreateCommand().Run();

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(Lines, Is.EqualTo(new[]
        {
            "Output devices:",
            "  None is connected now.",
            "  The device the class plays on  --device auto",
            "Apps with sound:",
            "  None has sound open now.",
            "Nothing was opened or recorded to list these.",
            "For example: ClassScribe record \"Kalam\" --device auto --output \"D:\\Classes\""
        }));
    }

    /// <summary>The devices are asked for only when the command runs, so making the command touches no audio device.</summary>
    [Test]
    public void The_devices_are_asked_for_only_when_the_command_runs()
    {
        var asked = false;

        _ = new DevicesCommand(new Lazy<ILoopbackCaptureProvider>(() =>
        {
            asked = true;
            return _devices;
        }), _ui);

        Assert.That(asked, Is.False);
    }
}
