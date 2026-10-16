using ClassScribe.Settings;
using Microsoft.Extensions.Configuration;

namespace ClassScribe.Tests;

/// <summary>The check that settings pass before they are used, whatever configuration it is given.</summary>
[TestFixture]
public class SettingsCheckTests
{
    /// <summary>The settings given, with the key from the environment added last, as the app adds it.</summary>
    private static IConfigurationRoot Settings(Dictionary<string, string?> settings, string? apiKey = "test-key") =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .AddApiKeyFromEnvironment(apiKey)
            .Build();

    /// <summary>What the check finds wrong with the given settings.</summary>
    private static IReadOnlyList<string> Problems(Dictionary<string, string?> settings) => new SettingsCheck().FindProblems(Settings(settings));

    /// <summary>What the check warns about in the given settings.</summary>
    private static IReadOnlyList<string> Warnings(Dictionary<string, string?> settings, string? apiKey = "test-key") => new SettingsCheck().FindWarnings(Settings(settings, apiKey));

    /// <summary>The defaults are usable, and nothing is said about them.</summary>
    [Test]
    public void The_defaults_pass()
    {
        Assert.That(Problems([]), Is.Empty);
        Assert.That(Warnings([]), Is.Empty);
    }

    /// <summary>A value that cannot be read as what the setting needs is reported with the setting's name, the value and what is expected.</summary>
    [TestCase("Transcript:ParagraphPauseSeconds", "1,5", "a number written with a dot")]
    [TestCase("Recording:KeepUploadFile", "no", "true or false")]
    [TestCase("Recording:FlushSeconds", "5s", "a whole number")]
    [TestCase("ElevenLabs:TimeoutMinutes", "", "a whole number")]
    [TestCase("ElevenLabs:SlowestUploadMbps", "1,5", "a number written with a dot")]
    [TestCase("Recording:KeepScreenOn", "Sometimes", "OnBattery, Always or Never")]
    [TestCase("Recording:KeepScreenOn", "", "OnBattery, Always or Never")]
    public void A_value_that_cannot_be_read_is_reported_by_its_setting(string setting, string value, string expected)
    {
        var problems = Problems(new() { [setting] = value });

        Assert.That(problems, Has.Count.EqualTo(1));
        Assert.That(problems[0], Does.StartWith(setting).And.Contain($"\"{value}\"").And.Contain(expected));
    }

    /// <summary>
    /// An enum setting takes the names of its values, in any case. A number, or several names joined with a comma, would
    /// be read as some value without saying which, so they are refused.
    /// </summary>
    [TestCase("always", true)]
    [TestCase(" Never ", true)]
    [TestCase("ONBATTERY", true)]
    [TestCase("1", false)]
    [TestCase("Always, Never", false)]
    public void An_enum_setting_takes_its_names_in_any_case_and_nothing_else(string value, bool usable)
    {
        var problems = Problems(new() { ["Recording:KeepScreenOn"] = value });

        Assert.That(problems, usable ? Is.Empty : Has.One.EqualTo($"Recording:KeepScreenOn is \"{value}\", which is not OnBattery, Always or Never."));
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
    [TestCase("ElevenLabs:SlowestUploadMbps", "0")]
    [TestCase("ElevenLabs:SlowestUploadMbps", "-1")]
    [TestCase("ElevenLabs:SlowestUploadMbps", "NaN")]
    [TestCase("ElevenLabs:SlowestUploadMbps", "100000")]
    public void A_value_out_of_range_is_reported_by_its_setting(string setting, string value)
    {
        var problems = Problems(new() { [setting] = value });

        Assert.That(problems, Has.Count.EqualTo(1));
        Assert.That(problems[0], Does.StartWith(setting));
    }

    /// <summary>The slowest upload speed is a number of megabits per second from 0.1 to 1000; the message says so, with an example.</summary>
    [TestCase("0.1", true)]
    [TestCase("1000", true)]
    [TestCase("0.05", false)]
    public void The_slowest_upload_speed_is_from_a_tenth_to_a_thousand(string value, bool usable)
    {
        var problems = Problems(new() { ["ElevenLabs:SlowestUploadMbps"] = value });

        Assert.That(problems, usable ? Is.Empty : Has.One.EqualTo($"ElevenLabs:SlowestUploadMbps is {value}; it must be from 0.1 to 1000 (megabits per second), such as 1."));
    }

    /// <summary>0 in a paragraph setting turns that kind of paragraph break off, as the transcript's layout reads it, so it is no problem.</summary>
    [TestCase("Transcript:ParagraphPauseSeconds")]
    [TestCase("Transcript:MaxParagraphCharacters")]
    public void Zero_turns_a_kind_of_paragraph_break_off(string setting)
    {
        Assert.That(Problems(new() { [setting] = "0" }), Is.Empty);
    }

    /// <summary>A negative paragraph setting is refused, with the setting's name and that 0 is the way to turn that kind of break off.</summary>
    [TestCase("Transcript:ParagraphPauseSeconds", "-2.5")]
    [TestCase("Transcript:MaxParagraphCharacters", "-900")]
    public void A_negative_paragraph_setting_is_reported_with_how_to_turn_the_break_off(string setting, string value)
    {
        var problems = Problems(new() { [setting] = value });

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
        var problems = Problems(new() { [setting] = value });

        Assert.That(problems, Has.Count.EqualTo(1));
        Assert.That(problems[0], Does.StartWith(setting).And.Contain(@"D:\\Classes").And.Contain("D:/Classes"));
    }

    /// <summary>
    /// A log level the log cannot read, at any level of the Logging section, is reported by its setting with the names it
    /// takes. Read again while ClassScribe runs, it would otherwise stop the log from taking any settings at all.
    /// </summary>
    [TestCase("Logging:LogLevel:Default", "Informaton")]
    [TestCase("Logging:LogLevel:Microsoft", "2")]
    [TestCase("Logging:Console:LogLevel:Default", "Loud")]
    public void A_log_level_the_log_cannot_read_is_reported(string setting, string value)
    {
        var problems = Problems(new() { [setting] = value });

        Assert.That(problems, Has.One.EqualTo($"{setting} is \"{value}\", which is not Trace, Debug, Information, Warning, Error, Critical or None."));
    }

    /// <summary>Log levels written as the log takes them pass, in any case; an empty one is passed over by the log, and here.</summary>
    [Test]
    public void Log_levels_by_name_pass()
    {
        Assert.That(Problems(new()
        {
            ["Logging:LogLevel:Default"] = "information",
            ["Logging:LogLevel:Microsoft.Hosting.Lifetime"] = "Warning",
            ["Logging:Console:LogLevel:Default"] = "",
            ["Logging:Console:FormatterName"] = "simple"
        }), Is.Empty);
    }

    /// <summary>A console log setting the console log cannot use is reported under its section, with the reason.</summary>
    [TestCase("Logging:Console:MaxQueueLength", "0")]
    [TestCase("Logging:Console:LogToStandardErrorThreshold", "Loud")]
    [TestCase("Logging:Console:FormatterOptions:SingleLine", "yes")]
    [TestCase("Logging:CaptureScopes", "maybe")]
    public void A_console_log_setting_that_cannot_be_used_is_reported(string setting, string value)
    {
        var problems = Problems(new() { [setting] = value });

        Assert.That(problems, Has.Count.EqualTo(1));
        Assert.That(problems[0], Does.StartWith(setting[..setting.LastIndexOf(':')]));
    }

    /// <summary>Every problem is reported at once, so one look shows all there is to fix.</summary>
    [Test]
    public void Every_problem_is_reported_at_once()
    {
        var problems = Problems(new()
        {
            ["Recording:FlushSeconds"] = "0",
            ["Recording:KeepUploadFile"] = "no",
            ["Recording:KeepScreenOn"] = "Sometimes",
            ["ElevenLabs:MaxAttempts"] = "0",
            ["Transcript:ParagraphPauseSeconds"] = "1,5",
            ["Transcript:MaxParagraphCharacters"] = "-1",
            ["Logging:LogLevel:Default"] = "Loud"
        });

        Assert.That(problems.Select(problem => problem.Split(' ')[0]), Is.EquivalentTo(new[]
        {
            "Recording:FlushSeconds", "Recording:KeepUploadFile", "Recording:KeepScreenOn", "ElevenLabs:MaxAttempts",
            "Transcript:ParagraphPauseSeconds", "Transcript:MaxParagraphCharacters", "Logging:LogLevel:Default"
        }));
    }

    /// <summary>
    /// A key written into the settings is not used: it brings a warning, not a problem, which names the setting and asks
    /// for it to be taken out of the file, without ever showing a key.
    /// </summary>
    [Test]
    public void A_key_in_the_settings_brings_a_warning_that_never_shows_a_key()
    {
        var settings = new Dictionary<string, string?> { ["ElevenLabs:ApiKey"] = "key-in-file" };

        var warnings = Warnings(settings, apiKey: "key-from-variable");

        Assert.That(Problems(settings), Is.Empty, "only a warning: the key from the variable is used");
        Assert.That(warnings, Has.Count.EqualTo(1));
        Assert.That(warnings[0], Does.Contain(ElevenLabsOptions.ApiKeySetting).And.Contain("Remove it").And.Contain(ElevenLabsOptions.ApiKeyVariable));
        Assert.That(warnings[0], Does.Not.Contain("key-in-file").And.Not.Contain("key-from-variable"));
    }

    /// <summary>
    /// A recordings folder inside OneDrive brings a warning but is not refused: OneDrive uploads a growing recording again
    /// and again, and can hold files ClassScribe needs to delete. The folder is found however it is written.
    /// </summary>
    [TestCase(@"%OneDriveCommercial%\Classes")]
    [TestCase("{0}\\Classes")]
    [TestCase("{0}")]
    public void A_recordings_folder_inside_OneDrive_brings_a_warning(string outputFolder)
    {
        using var oneDrive = new TempFolder();
        var before = Environment.GetEnvironmentVariable("OneDriveCommercial");
        try
        {
            Environment.SetEnvironmentVariable("OneDriveCommercial", oneDrive.Path);
            var settings = new Dictionary<string, string?> { ["Recording:OutputFolder"] = string.Format(outputFolder, oneDrive.Path) };

            var warnings = Warnings(settings);

            Assert.That(Problems(settings), Is.Empty);
            Assert.That(warnings, Has.Count.EqualTo(1));
            Assert.That(warnings[0], Does.StartWith("Recording:OutputFolder is inside OneDrive").And.Contain(oneDrive.Path));
        }
        finally
        {
            Environment.SetEnvironmentVariable("OneDriveCommercial", before);
        }
    }

    /// <summary>A folder beside OneDrive, whose name only starts like OneDrive's, brings no warning.</summary>
    [Test]
    public void A_folder_whose_name_only_starts_like_OneDrive_brings_no_warning()
    {
        using var oneDrive = new TempFolder();
        var before = Environment.GetEnvironmentVariable("OneDriveCommercial");
        try
        {
            Environment.SetEnvironmentVariable("OneDriveCommercial", oneDrive.Path);

            Assert.That(Warnings(new() { ["Recording:OutputFolder"] = oneDrive.Path + " Backup" }), Is.Empty);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OneDriveCommercial", before);
        }
    }
}
