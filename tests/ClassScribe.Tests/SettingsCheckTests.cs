using ClassScribe.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Tests;

/// <summary>The settings check that runs before anything is recorded or uploaded.</summary>
[TestFixture]
public class SettingsCheckTests
{
    private RecordingLogger<SettingsCheck> _log = null!;
    private ServiceProvider? _services;

    /// <summary>Creates the logger the check reports to.</summary>
    [SetUp]
    public void SetUp() => _log = new RecordingLogger<SettingsCheck>();

    /// <summary>Disposes the services of the test.</summary>
    [TearDown]
    public void TearDown() => _services?.Dispose();

    /// <summary>
    /// Builds the check over the given settings, bound as the app binds them, with the key from the
    /// environment added last, as the app adds it.
    /// </summary>
    private SettingsCheck CreateCheck(Dictionary<string, string?> settings, string? apiKey = "test-key")
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .AddApiKeyFromEnvironment(apiKey)
            .Build();
        _services = new ServiceCollection()
            .AddLogging()
            .AddClassScribe(configuration)
            .AddSingleton<ILogger<SettingsCheck>>(_log)
            .BuildServiceProvider();
        return _services.GetRequiredService<SettingsCheck>();
    }

    /// <summary>The defaults are usable, and nothing is reported about them.</summary>
    [Test]
    public void The_defaults_pass()
    {
        var check = CreateCheck([]);

        Assert.That(check.FindProblems(), Is.Empty);
        Assert.That(check.AreUsable(), Is.True);
        Assert.That(_log.Problems, Is.Empty);
    }

    /// <summary>A value that cannot be read as what the setting needs is reported with the setting's name, the value and what is expected.</summary>
    [TestCase("Transcript:ParagraphPauseSeconds", "1,5", "a number written with a dot")]
    [TestCase("Recording:KeepUploadFile", "no", "true or false")]
    [TestCase("Recording:FlushSeconds", "5s", "a whole number")]
    [TestCase("ElevenLabs:TimeoutMinutes", "", "a whole number")]
    public void A_value_that_cannot_be_read_is_reported_by_its_setting(string setting, string value, string expected)
    {
        var problems = CreateCheck(new() { [setting] = value }).FindProblems();

        Assert.That(problems, Has.Count.EqualTo(1));
        Assert.That(problems[0], Does.StartWith(setting).And.Contain($"\"{value}\"").And.Contain(expected));
    }

    /// <summary>A value that can be read but makes no sense, or would break something, is reported with the setting's name.</summary>
    [TestCase("Transcript:ParagraphPauseSeconds", "-1")]
    [TestCase("Transcript:ParagraphPauseSeconds", "NaN")]
    [TestCase("Transcript:MaxParagraphCharacters", "-1")]
    [TestCase("Recording:FlushSeconds", "0")]
    [TestCase("Recording:SilenceWarningSeconds", "0")]
    [TestCase("Recording:StatusIntervalSeconds", "-5")]
    [TestCase("Recording:MaxSegmentMegabytes", "0")]
    [TestCase("Recording:MaxSegmentMegabytes", "5000")]
    [TestCase("ElevenLabs:TimeoutMinutes", "0")]
    [TestCase("ElevenLabs:TimeoutMinutes", "100000")]
    [TestCase("ElevenLabs:MaxAttempts", "0")]
    [TestCase("ElevenLabs:RetryDelaySeconds", "-1")]
    [TestCase("ElevenLabs:BaseUrl", "api.elevenlabs.io")]
    [TestCase("ElevenLabs:BaseUrl", "ftp://api.elevenlabs.io/")]
    [TestCase("ElevenLabs:ModelId", " ")]
    public void A_value_out_of_range_is_reported_by_its_setting(string setting, string value)
    {
        var problems = CreateCheck(new() { [setting] = value }).FindProblems();

        Assert.That(problems, Has.Count.EqualTo(1));
        Assert.That(problems[0], Does.StartWith(setting));
    }

    /// <summary>0 in a paragraph setting turns that kind of paragraph break off, as the transcript's layout reads it, so it is no problem.</summary>
    [TestCase("Transcript:ParagraphPauseSeconds")]
    [TestCase("Transcript:MaxParagraphCharacters")]
    public void Zero_turns_a_kind_of_paragraph_break_off(string setting)
    {
        var problems = CreateCheck(new() { [setting] = "0" }).FindProblems();

        Assert.That(problems, Is.Empty);
    }

    /// <summary>A negative paragraph setting is refused, with the setting's name and that 0 is the way to turn that kind of break off.</summary>
    [TestCase("Transcript:ParagraphPauseSeconds", "-2.5")]
    [TestCase("Transcript:MaxParagraphCharacters", "-900")]
    public void A_negative_paragraph_setting_is_reported_with_how_to_turn_the_break_off(string setting, string value)
    {
        var problems = CreateCheck(new() { [setting] = value }).FindProblems();

        Assert.That(problems, Has.Count.EqualTo(1));
        Assert.That(problems[0], Does.StartWith($"{setting} is {value};").And.Contain("0 or more").And.Contain("0 turns"));
    }

    /// <summary>
    /// A path holding a tab or a line break, which is what a single backslash before t or n becomes in
    /// JSON, is reported with how to write a path in appsettings.json.
    /// </summary>
    [TestCase("Recording:OutputFolder", "D:\tClasses")]
    [TestCase("ElevenLabs:KeytermsFile", "C:\new\\keyterms.txt")]
    public void A_path_with_a_tab_or_a_line_break_is_reported_with_how_to_write_paths(string setting, string value)
    {
        var problems = CreateCheck(new() { [setting] = value }).FindProblems();

        Assert.That(problems, Has.Count.EqualTo(1));
        Assert.That(problems[0], Does.StartWith(setting).And.Contain(@"D:\\Classes").And.Contain("D:/Classes"));
    }

    /// <summary>Every problem is reported at once, so one start shows all there is to fix.</summary>
    [Test]
    public void Every_problem_is_reported_at_once()
    {
        var problems = CreateCheck(new()
        {
            ["Recording:FlushSeconds"] = "0",
            ["Recording:KeepUploadFile"] = "no",
            ["ElevenLabs:MaxAttempts"] = "0",
            ["Transcript:ParagraphPauseSeconds"] = "1,5",
            ["Transcript:MaxParagraphCharacters"] = "-1"
        }).FindProblems();

        Assert.That(problems.Select(problem => problem.Split(' ')[0]), Is.EquivalentTo(new[]
        {
            "Recording:FlushSeconds", "Recording:KeepUploadFile", "ElevenLabs:MaxAttempts", "Transcript:ParagraphPauseSeconds", "Transcript:MaxParagraphCharacters"
        }));
    }

    /// <summary>Each problem is logged as an error, followed by what to do, and the settings count as not usable.</summary>
    [Test]
    public void Each_problem_is_logged_as_an_error()
    {
        var check = CreateCheck(new() { ["Recording:FlushSeconds"] = "0", ["ElevenLabs:MaxAttempts"] = "0" });

        Assert.That(check.AreUsable(), Is.False);
        Assert.That(_log.Problems, Has.Count.EqualTo(3));
        Assert.That(_log.Problems[0], Does.StartWith("Recording:FlushSeconds"));
        Assert.That(_log.Problems[1], Does.StartWith("ElevenLabs:MaxAttempts"));
        Assert.That(_log.Problems[2], Does.Contain("appsettings.json"));
    }

    /// <summary>
    /// A key written into the settings is not used; a warning names the setting and asks for it to be
    /// taken out of the file, without ever showing a key.
    /// </summary>
    [Test]
    public void A_key_in_the_settings_brings_a_warning_that_never_shows_a_key()
    {
        var check = CreateCheck(new() { ["ElevenLabs:ApiKey"] = "key-in-file" }, apiKey: "key-from-variable");

        Assert.That(check.AreUsable(), Is.True, "only a warning: the key from the variable is used");
        Assert.That(_log.Problems, Has.Count.EqualTo(1));
        Assert.That(_log.Problems[0], Does.Contain(ElevenLabsOptions.ApiKeySetting).And.Contain("Remove it").And.Contain(ElevenLabsOptions.ApiKeyVariable));
        Assert.That(_log.Problems[0], Does.Not.Contain("key-in-file").And.Not.Contain("key-from-variable"));
    }
}
