using ClassScribe.Audio;
using ClassScribe.Audio.Microphones;
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
    [TestCase("Recording:SeparateProcess", "yes", "true or false")]
    [TestCase("Recording:ReopenWindow", "Sometimes", "OnCloseAndProblems, OnProblems or Never")]
    [TestCase("Recording:BlockShutdown", "no", "true or false")]
    [TestCase("Recording:ContinueWithinMinutes", "an hour", "a whole number")]
    [TestCase("Recording:ContinueWithinMinutes", "60.5", "a whole number")]
    [TestCase("Recording:StopAfterHours", "9,5", "a number written with a dot")]
    [TestCase("Recording:Mode", "Both", "Class, ClassAndMicrophone or Microphone")]
    [TestCase("Recording:Mode", "1", "Class, ClassAndMicrophone or Microphone")]
    [TestCase("Recording:MicrophoneProcessing", "raw mode", "Raw or Default")]
    [TestCase("Recording:ClassAppMute", "Maybe", "Visible, Invisible or Unknown")]
    [TestCase("Transcript:ShowGapsLongerThanSeconds", "1 minute", "a whole number")]
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

    /// <summary>
    /// The settings added for the background recorder have these defaults when the file leaves them out: record in a
    /// separate process; reopen a minimized window when the last one is closed and whenever something needs attention;
    /// ask before a restart or shutdown started from Windows; offer to continue a recording that stopped within the hour;
    /// and stop by itself after 9 hours. The defaults pass the check.
    /// </summary>
    [Test]
    public void The_background_recorders_settings_have_their_defaults()
    {
        var recording = new RecordingOptions();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(recording.SeparateProcess, Is.True);
            Assert.That(recording.ReopenWindow, Is.EqualTo(ReopenWindow.OnCloseAndProblems));
            Assert.That(recording.BlockShutdown, Is.True);
            Assert.That(recording.ContinueWithinMinutes, Is.EqualTo(60));
            Assert.That(recording.StopAfterHours, Is.EqualTo(9));
            Assert.That(Problems([]), Is.Empty);
        }
    }

    /// <summary>
    /// Each setting added for the background recorder is bound from the file as written: the switches, the name of when
    /// to reopen a window (in any case), the minutes and the hours.
    /// </summary>
    [Test]
    public void The_background_recorders_settings_are_taken_from_the_file()
    {
        var settings = Settings(new()
        {
            ["Recording:SeparateProcess"] = "false",
            ["Recording:ReopenWindow"] = "onproblems",
            ["Recording:BlockShutdown"] = "False",
            ["Recording:ContinueWithinMinutes"] = "0",
            ["Recording:StopAfterHours"] = "4.5"
        });

        var recording = settings.GetSection(RecordingOptions.SectionName).Get<RecordingOptions>()!;

        Assert.That(new SettingsCheck().FindProblems(settings), Is.Empty);
        Assert.That(
            (recording.SeparateProcess, recording.ReopenWindow, recording.BlockShutdown, recording.ContinueWithinMinutes, recording.StopAfterHours),
            Is.EqualTo((false, ReopenWindow.OnProblems, false, 0, 4.5)));
    }

    /// <summary>
    /// When to reopen a window takes the names of its choices, in any case. A number, or several names joined with a
    /// comma, would be read as some choice without saying which, so they are refused, with the names that work.
    /// </summary>
    [TestCase("OnCloseAndProblems", true)]
    [TestCase("onproblems", true)]
    [TestCase(" NEVER ", true)]
    [TestCase("OnClose", false)]
    [TestCase("0", false)]
    [TestCase("Never, OnProblems", false)]
    public void When_to_reopen_a_window_takes_its_names_in_any_case_and_nothing_else(string value, bool usable)
    {
        var problems = Problems(new() { ["Recording:ReopenWindow"] = value });

        Assert.That(problems, usable ? Is.Empty : Has.One.EqualTo($"Recording:ReopenWindow is \"{value}\", which is not OnCloseAndProblems, OnProblems or Never."));
    }

    /// <summary>
    /// The safety stop is from 0 to 9.5 hours. ElevenLabs takes at most 10 hours of audio in one file, and ClassScribe
    /// sends at most 9.5, so a later stop would make a recording too long to transcribe; 0 turns the stop off. A value
    /// outside that range is refused with the range, an example, the reason and what 0 does.
    /// </summary>
    [TestCase("0", true)]
    [TestCase("0.5", true)]
    [TestCase("9", true)]
    [TestCase("9.5", true)]
    [TestCase("9.6", false)]
    [TestCase("24", false)]
    [TestCase("-1", false)]
    [TestCase("NaN", false)]
    [TestCase("Infinity", false)]
    public void The_safety_stop_is_from_0_to_9_and_a_half_hours(string value, bool usable)
    {
        var problems = Problems(new() { ["Recording:StopAfterHours"] = value });

        Assert.That(problems, usable ? Is.Empty : Has.One.EqualTo(
            $"Recording:StopAfterHours is {value}; it must be from 0 to 9.5 (hours), such as 9. ElevenLabs takes at most 10 hours of audio in one file; 0 turns the safety stop off."));
    }

    /// <summary>
    /// Continuing a recording is offered for one that stopped from 0 to 1440 minutes (a day) ago; 0 never offers it. A
    /// negative value is refused, and so is more than a day, which would join two classes into one recording.
    /// </summary>
    [TestCase("0", true)]
    [TestCase("60", true)]
    [TestCase("1440", true)]
    [TestCase("1441", false)]
    [TestCase("-1", false)]
    public void Continuing_is_offered_for_a_recording_that_stopped_up_to_a_day_ago(string value, bool usable)
    {
        var problems = Problems(new() { ["Recording:ContinueWithinMinutes"] = value });

        Assert.That(problems, usable ? Is.Empty : Has.One.EqualTo(
            $"Recording:ContinueWithinMinutes is {value}; it must be from 0 to 1440 (a day), such as 60. 0 never offers to continue a recording."));
    }

    /// <summary>
    /// The settings added for the microphone have these defaults when the file leaves them out: the class alone is
    /// recorded; the microphone is Windows' default one; unprocessed audio (RAW) is asked for; with the class, the
    /// microphone follows chrome; and whether the class app lets go of the microphone on mute is not known. A heading
    /// marks 60 seconds or more of silence left out of a class-only recording. The defaults pass the check.
    /// </summary>
    [Test]
    public void The_microphones_settings_have_their_defaults()
    {
        var recording = new RecordingOptions();
        var transcript = new TranscriptOptions();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(recording.Mode, Is.EqualTo(RecordingMode.Class));
            Assert.That(recording.Microphone, Is.Empty);
            Assert.That(recording.MicrophoneProcessing, Is.EqualTo(MicrophoneProcessing.Raw));
            Assert.That(recording.MicrophoneFollows, Is.EqualTo("chrome"));
            Assert.That(recording.ClassAppMute, Is.EqualTo(ClassAppMute.Unknown));
            Assert.That(transcript.ShowGapsLongerThanSeconds, Is.EqualTo(60));
            Assert.That(Problems([]), Is.Empty);
        }
    }

    /// <summary>Each setting added for the microphone is bound from the file as written: the names in any case, the name parts as they are, and the seconds.</summary>
    [Test]
    public void The_microphones_settings_are_taken_from_the_file()
    {
        var settings = Settings(new()
        {
            ["Recording:Mode"] = "classandmicrophone",
            ["Recording:Microphone"] = "OBSBOT",
            ["Recording:MicrophoneProcessing"] = "DEFAULT",
            ["Recording:MicrophoneFollows"] = "msedge",
            ["Recording:ClassAppMute"] = "visible",
            ["Transcript:ShowGapsLongerThanSeconds"] = "0"
        });

        var recording = settings.GetSection(RecordingOptions.SectionName).Get<RecordingOptions>()!;
        var transcript = settings.GetSection(TranscriptOptions.SectionName).Get<TranscriptOptions>()!;

        Assert.That(new SettingsCheck().FindProblems(settings), Is.Empty);
        Assert.That(
            (recording.Mode, recording.Microphone, recording.MicrophoneProcessing, recording.MicrophoneFollows, recording.ClassAppMute, transcript.ShowGapsLongerThanSeconds),
            Is.EqualTo((RecordingMode.ClassAndMicrophone, "OBSBOT", MicrophoneProcessing.Default, "msedge", ClassAppMute.Visible, 0)));
    }

    /// <summary>
    /// The app the microphone follows is part of an app's name, or auto or none. Empty, it would follow nothing and
    /// record nothing; naming ClassScribe itself, whose own capture holds the microphone for the whole class, it would
    /// follow ClassScribe and record everything. Both are refused, with what is taken instead. A name part matches an app
    /// whose process name contains it, so any part of "classscribe" names ClassScribe, with or without .exe.
    /// </summary>
    [TestCase("chrome", true)]
    [TestCase(" Zoom ", true)]
    [TestCase("auto", true)]
    [TestCase("none", true)]
    [TestCase("", false)]
    [TestCase("   ", false)]
    [TestCase("ClassScribe", false)]
    [TestCase("classscribe.exe", false)]
    [TestCase("scribe", false)]
    public void The_app_the_microphone_follows_must_name_an_app_other_than_ClassScribe(string value, bool usable)
    {
        var problems = Problems(new() { ["Recording:MicrophoneFollows"] = value });

        if (usable)
        {
            Assert.That(problems, Is.Empty);
            return;
        }

        Assert.That(problems, Has.Count.EqualTo(1));
        Assert.That(problems[0], Does.StartWith($"Recording:MicrophoneFollows is \"{value}\"").And.Contain("such as chrome").And.Contain("auto").And.Contain("none"));
        if (value.Trim().Length > 0) Assert.That(problems[0], Does.Contain("ClassScribe itself"));
    }

    /// <summary>
    /// The appsettings.json that ships passes the check, and holds each setting added for the microphone at the value the
    /// code gives it when the file leaves it out, so that the file shows every setting there is.
    /// </summary>
    [Test]
    public void The_shipped_settings_hold_the_microphones_settings_at_their_defaults()
    {
        var shipped = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: false, reloadOnChange: false)
            .AddApiKeyFromEnvironment("test-key")
            .Build();
        var recording = shipped.GetSection(RecordingOptions.SectionName).Get<RecordingOptions>()!;
        var transcript = shipped.GetSection(TranscriptOptions.SectionName).Get<TranscriptOptions>()!;
        var defaults = new RecordingOptions();
        string[] added =
        [
            $"{RecordingOptions.SectionName}:{nameof(RecordingOptions.Mode)}", $"{RecordingOptions.SectionName}:{nameof(RecordingOptions.Microphone)}",
            $"{RecordingOptions.SectionName}:{nameof(RecordingOptions.MicrophoneProcessing)}", $"{RecordingOptions.SectionName}:{nameof(RecordingOptions.MicrophoneFollows)}",
            $"{RecordingOptions.SectionName}:{nameof(RecordingOptions.ClassAppMute)}", $"{TranscriptOptions.SectionName}:{nameof(TranscriptOptions.ShowGapsLongerThanSeconds)}"
        ];

        Assert.That(new SettingsCheck().FindProblems(shipped), Is.Empty);
        Assert.That(added.Where(setting => shipped[setting] is null), Is.Empty, "every added setting is in the file");
        Assert.That(
            (recording.Mode, recording.Microphone, recording.MicrophoneProcessing, recording.MicrophoneFollows, recording.ClassAppMute, transcript.ShowGapsLongerThanSeconds),
            Is.EqualTo((defaults.Mode, defaults.Microphone, defaults.MicrophoneProcessing, defaults.MicrophoneFollows, defaults.ClassAppMute, new TranscriptOptions().ShowGapsLongerThanSeconds)));
    }

    /// <summary>
    /// The appsettings.json that ships passes the check, and holds each setting added for the background recorder at the
    /// value the code gives it when the file leaves it out, so that the file shows every setting there is.
    /// </summary>
    [Test]
    public void The_shipped_settings_pass_and_hold_the_background_recorders_settings_at_their_defaults()
    {
        var shipped = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: false, reloadOnChange: false)
            .AddApiKeyFromEnvironment("test-key")
            .Build();
        var recording = shipped.GetSection(RecordingOptions.SectionName).Get<RecordingOptions>()!;
        var defaults = new RecordingOptions();
        string[] added =
        [
            nameof(RecordingOptions.SeparateProcess), nameof(RecordingOptions.ReopenWindow), nameof(RecordingOptions.BlockShutdown),
            nameof(RecordingOptions.ContinueWithinMinutes), nameof(RecordingOptions.StopAfterHours)
        ];

        Assert.That(new SettingsCheck().FindProblems(shipped), Is.Empty);
        Assert.That(added.Where(setting => shipped[$"{RecordingOptions.SectionName}:{setting}"] is null), Is.Empty, "every added setting is in the file");
        Assert.That(
            (recording.SeparateProcess, recording.ReopenWindow, recording.BlockShutdown, recording.ContinueWithinMinutes, recording.StopAfterHours),
            Is.EqualTo((defaults.SeparateProcess, defaults.ReopenWindow, defaults.BlockShutdown, defaults.ContinueWithinMinutes, defaults.StopAfterHours)));
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
    [TestCase("Transcript:ShowGapsLongerThanSeconds", "-1")]
    [TestCase("Recording:MicrophoneFollows", "")]
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
    /// The silence a heading marks in a class-only recording is a number of seconds, 0 or more; 0 writes no such heading.
    /// A negative value is refused, with the setting's name, an example and what 0 does.
    /// </summary>
    [TestCase("0", true)]
    [TestCase("60", true)]
    [TestCase("-1", false)]
    public void The_silence_a_heading_marks_is_0_seconds_or_more(string value, bool usable)
    {
        var problems = Problems(new() { ["Transcript:ShowGapsLongerThanSeconds"] = value });

        Assert.That(problems, usable ? Is.Empty : Has.One.EqualTo(
            $"Transcript:ShowGapsLongerThanSeconds is {value}; it must be 0 or more, such as 60. 0 turns the headings for silence left out off."));
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
            ["Recording:MicrophoneFollows"] = "",
            ["Recording:ClassAppMute"] = "Maybe",
            ["ElevenLabs:MaxAttempts"] = "0",
            ["Transcript:ParagraphPauseSeconds"] = "1,5",
            ["Transcript:MaxParagraphCharacters"] = "-1",
            ["Transcript:ShowGapsLongerThanSeconds"] = "-1",
            ["Logging:LogLevel:Default"] = "Loud"
        });

        Assert.That(problems.Select(problem => problem.Split(' ')[0]), Is.EquivalentTo(new[]
        {
            "Recording:FlushSeconds", "Recording:KeepUploadFile", "Recording:KeepScreenOn", "Recording:MicrophoneFollows", "Recording:ClassAppMute",
            "ElevenLabs:MaxAttempts", "Transcript:ParagraphPauseSeconds", "Transcript:MaxParagraphCharacters", "Transcript:ShowGapsLongerThanSeconds",
            "Logging:LogLevel:Default"
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
