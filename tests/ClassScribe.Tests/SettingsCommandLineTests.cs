using System.Text;
using ClassScribe.Commands;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using Moq;
using ClassScribe.Settings;

namespace ClassScribe.Tests;

/// <summary>
/// The settings command on the command line (layout-config 1.8): its commands and their requests, the mistakes named before
/// anything runs, its own kind, and how a script reads its output (UTF-8 through a pipe, JSON alone on standard output).
/// </summary>
[TestFixture]
public class SettingsCommandLineTests
{
    private TempFolder _folder = null!;
    private TempFolder _settingsFolder = null!;
    private Mock<ICommandRunner> _commands = null!;
    private List<SettingsRequest> _asked = null!;
    private TestCommandLine _line = null!;

    /// <summary>The command line over mocked commands, which note each settings request and succeed.</summary>
    [SetUp]
    public void SetUp()
    {
        _folder = new TempFolder();
        _settingsFolder = new TempFolder();
        _asked = [];
        _commands = new Mock<ICommandRunner>();
        _commands.Setup(c => c.SettingsAsync(It.IsAny<SettingsRequest>())).Callback((SettingsRequest request) => _asked.Add(request)).ReturnsAsync(0);
        _line = new TestCommandLine(_commands.Object, new FakeConsoleUi(), new TestSettings(_settingsFolder.Path), _folder.Path);
    }

    /// <summary>Deletes the folders.</summary>
    [TearDown]
    public void TearDown()
    {
        _folder.Dispose();
        _settingsFolder.Dispose();
    }

    /// <summary>Each command of settings asks for what it names, a list's items one by one and a negative number taken as a value.</summary>
    [Test]
    public async Task Each_command_asks_for_what_it_names()
    {
        Assert.That(await _line.RunAsync("settings", "path"), Is.EqualTo(0));
        Assert.That(await _line.RunAsync("settings", "list", "--json"), Is.EqualTo(0));
        Assert.That(await _line.RunAsync("Settings", "Get", "Recording:OutputFolder"), Is.EqualTo(0));
        Assert.That(await _line.RunAsync("settings", "set", "YouTube:CourseChannels", "@one", "@two"), Is.EqualTo(0));
        Assert.That(await _line.RunAsync("settings", "set", "Logging:LogLevel:ClassScribe", "Debug"), Is.EqualTo(0));
        Assert.That(await _line.RunAsync("settings", "reset", "Recording:Device"), Is.EqualTo(0));
        Assert.That(await _line.RunAsync("settings", "reset", "--all"), Is.EqualTo(0));
        Assert.That(await _line.RunAsync("settings", "open"), Is.EqualTo(0));
        Assert.That(await _line.RunAsync("settings", "fill"), Is.EqualTo(0));
        Assert.That(await _line.RunAsync("settings", "set", "Recording:FlushSeconds", "-5"), Is.EqualTo(0));

        Assert.That(_asked[0], Is.InstanceOf<SettingsRequest.ShowPath>());
        Assert.That(_asked[1], Is.EqualTo(new SettingsRequest.List(true)));
        Assert.That(_asked[2], Is.EqualTo(new SettingsRequest.Get("Recording:OutputFolder", false)));
        Assert.That(((SettingsRequest.Set)_asked[3]).Values, Is.EqualTo(new[] { "@one", "@two" }));
        Assert.That(((SettingsRequest.Set)_asked[4]).Name, Is.EqualTo("Logging:LogLevel:ClassScribe"));
        Assert.That(_asked[5], Is.EqualTo(new SettingsRequest.Reset("Recording:Device")));
        Assert.That(_asked[6], Is.EqualTo(new SettingsRequest.Reset(null)));
        Assert.That(_asked[7], Is.InstanceOf<SettingsRequest.Open>());
        Assert.That(_asked[8], Is.InstanceOf<SettingsRequest.Fill>());
        Assert.That(((SettingsRequest.Set)_asked[9]).Values, Is.EqualTo(new[] { "-5" }), "a negative number is a value, never an option");
    }

    /// <summary>settings alone, set without a value, and reset with neither a setting nor --all, or both, are mistakes named with the help, exit 2.</summary>
    [TestCase("settings")]
    [TestCase("settings", "set", "Recording:FlushSeconds")]
    [TestCase("settings", "reset")]
    [TestCase("settings", "reset", "Recording:Device", "--all")]
    [TestCase("settings", "get")]
    public async Task Mistakes_are_named_with_the_help(params string[] args)
    {
        Assert.That(await _line.RunAsync(args), Is.EqualTo(2));

        Assert.That(_asked, Is.Empty);
        Assert.That(_line.Mistakes, Is.Not.Empty);
        Assert.That(_line.Output.ToString(), Does.Contain("ClassScribe settings list"), "the help, with its examples");
    }

    /// <summary>A mistake the command finds as it runs, such as an unknown setting, is followed by the command's help; a failure is not.</summary>
    [Test]
    public async Task A_mistake_found_as_it_runs_is_followed_by_the_help()
    {
        _commands.Setup(c => c.SettingsAsync(It.IsAny<SettingsRequest.Set>())).ReturnsAsync(2);
        _commands.Setup(c => c.SettingsAsync(It.IsAny<SettingsRequest.Fill>())).ReturnsAsync(1);

        Assert.That(await _line.RunAsync("settings", "set", "Recording:Nothing", "1"), Is.EqualTo(2));
        Assert.That(_line.Output.ToString(), Does.Contain("Change one setting in your settings file"));

        _line.Output.GetStringBuilder().Clear();
        Assert.That(await _line.RunAsync("settings", "fill"), Is.EqualTo(1));
        Assert.That(_line.Output.ToString(), Is.Empty);
    }

    /// <summary>settings and its commands are of their own kind, neither the gate nor the report before them; help stays help.</summary>
    [Test]
    public void Settings_is_a_command_of_its_own_kind()
    {
        Assert.That(_line.KindOf("settings", "list"), Is.EqualTo(CommandKind.Settings));
        Assert.That(_line.KindOf("settings", "set", "Recording:FlushSeconds", "5"), Is.EqualTo(CommandKind.Settings));
        Assert.That(_line.KindOf("settings", "--help"), Is.EqualTo(CommandKind.Help));
        Assert.That(_line.KindOf("list"), Is.EqualTo(CommandKind.Command));
    }

    /// <summary>
    /// Program sends settings' and class' redirected output in UTF-8, and with --json every log line to standard error;
    /// only those two commands, in any case.
    /// </summary>
    [Test]
    public void Program_knows_the_commands_of_their_own_kind_and_their_json()
    {
        Assert.That(CommandLineTree.IsOwnKind(["settings", "list"]), Is.True);
        Assert.That(CommandLineTree.IsOwnKind(["CLASS", "list"]), Is.True);
        Assert.That(CommandLineTree.IsOwnKind(["list"]), Is.False);
        Assert.That(CommandLineTree.IsOwnKind([]), Is.False);
        Assert.That(CommandLineTree.WantsJson(["settings", "list", "--JSON"]), Is.True);
        Assert.That(CommandLineTree.WantsJson(["settings", "list"]), Is.False);
        Assert.That(CommandLineTree.WantsJson(["list", "--json"]), Is.False);
    }

    /// <summary>The writer settings and class answer a pipe with writes UTF-8 without a byte-order mark: Ṣughrā arrives as it is.</summary>
    [Test]
    public void Redirected_output_is_UTF8_without_a_byte_order_mark()
    {
        using var stream = new MemoryStream();

        using (var writer = ConsoleUi.Utf8Writer(stream)) writer.Write("Ṣughrā");

        Assert.That(stream.ToArray(), Is.EqualTo(new UTF8Encoding(false).GetBytes("Ṣughrā")));
    }

    /// <summary>With --json, the app's console log goes to standard error from its first line, so standard output holds the JSON alone.</summary>
    [Test]
    public void With_json_every_log_line_goes_to_standard_error()
    {
        using var runtime = new TestRuntime();
        using var host = AppHost.Build(new SettingsPlaces(_settingsFolder.Path, userFolder: null), "test-key", services =>
        {
            services.AddSingleton(runtime.Folder);
            AppHost.LogToStandardError(services);
        });

        Assert.That(host.Services.GetRequiredService<IOptionsMonitor<ConsoleLoggerOptions>>().CurrentValue.LogToStandardErrorThreshold, Is.EqualTo(LogLevel.Trace));
    }
}
