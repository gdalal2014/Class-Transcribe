using ClassScribe.Audio;
using ClassScribe.Audio.Microphones;
using ClassScribe.Camera;
using ClassScribe.Drive;
using ClassScribe.Settings;
using ClassScribe.Transcription;
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

    /// <summary>
    /// Each problem names the settings it is about (amendment 4): the one its sentence starts with, a list's entry by its
    /// place, a log level by its path, and for a channel in both YouTube lists both lists, so that a recording can drop
    /// exactly those and keep the rest. The sentences are those of <see cref="SettingsCheck.FindProblems"/>.
    /// </summary>
    [Test]
    public void Each_problem_names_its_settings()
    {
        var settings = Settings(new()
        {
            ["Recording:FlushSeconds"] = "0",
            ["Recording:KeepScreenOn"] = "Sometimes",
            ["YouTube:MayDownloadChannels:0"] = "@shared",
            ["YouTube:CourseChannels:0"] = "@shared",
            ["YouTube:CourseChannels:1"] = "not a channel",
            ["Logging:LogLevel:Default"] = "Loud"
        });

        var problems = new SettingsCheck().FindProblemsByKey(settings);

        Assert.That(problems.Select(problem => string.Join(" + ", problem.Keys)), Is.EquivalentTo(new[]
        {
            "Recording:KeepScreenOn",
            "Recording:FlushSeconds",
            "YouTube:CourseChannels:1",
            "YouTube:MayDownloadChannels + YouTube:CourseChannels",
            "Logging:LogLevel:Default"
        }));
        Assert.That(problems.Select(problem => problem.Text), Is.EqualTo(new SettingsCheck().FindProblems(settings)));
    }

    /// <summary>A value that cannot be read as what the setting needs is reported with the setting's name, the value and what is expected.</summary>
    [TestCase("Transcript:ParagraphPauseSeconds", "1,5", "a number written with a dot")]
    [TestCase("Recording:KeepUploadFile", "no", "true or false")]
    [TestCase("Recording:FlushSeconds", "5s", "a whole number")]
    [TestCase("ElevenLabs:TimeoutMinutes", "", "a whole number")]
    [TestCase("ElevenLabs:SlowestUploadMbps", "1,5", "a number written with a dot")]
    [TestCase("ElevenLabs:PricePerHourUsd", "0,22", "a number written with a dot")]
    [TestCase("ElevenLabs:ReadAccountUsage", "maybe", "true or false")]
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
    [TestCase("Drive:Enabled", "after the class", "true or false")]
    [TestCase("Drive:LessonVideos", "on", "true or false")]
    [TestCase("ElevenLabs:MaxKeyterms", "1,000", "a whole number")]
    [TestCase("ElevenLabs:KeytermCharacterLimit", "fifty", "a whole number")]
    [TestCase("ElevenLabs:MaxKeytermWords", "5.5", "a whole number")]
    [TestCase("ElevenLabs:LongestFileHours", "10h", "a number written with a dot")]
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
    /// The safety stop is from 0 to 24 hours (architecture 7.2, question 10): 0 turns the stop off, and a stop later than one
    /// file to ElevenLabs can hold is no longer refused, since a refused file would stop a class, but warned about and held
    /// there (see <see cref="A_safety_stop_above_what_one_file_holds_is_warned_about_and_held_there"/>). A value outside the
    /// range is refused with the range, an example and what 0 does.
    /// </summary>
    [TestCase("0", true)]
    [TestCase("0.5", true)]
    [TestCase("9", true)]
    [TestCase("9.5", true)]
    [TestCase("9.6", true)]
    [TestCase("12", true)]
    [TestCase("24", true)]
    [TestCase("24.1", false)]
    [TestCase("25", false)]
    [TestCase("-1", false)]
    [TestCase("NaN", false)]
    [TestCase("Infinity", false)]
    public void The_safety_stop_is_from_0_to_24_hours(string value, bool usable)
    {
        var problems = Problems(new() { ["Recording:StopAfterHours"] = value });

        Assert.That(problems, usable ? Is.Empty : Has.One.EqualTo(
            $"Recording:StopAfterHours is {value}; it must be from 0 to 24 (hours), such as 9. 0 turns the safety stop off."));
    }

    /// <summary>
    /// A safety stop later than one file to ElevenLabs can hold, less the half hour ClassScribe keeps in hand, is warned
    /// about, never refused: the recording stops there instead, which the warning says. At 9.5 hours, or with the stop off,
    /// nothing is said.
    /// </summary>
    [TestCase("12", "9.5")]
    [TestCase("9.6", "9.5")]
    [TestCase("24", "9.5")]
    public void A_safety_stop_above_what_one_file_holds_is_warned_about_and_held_there(string value, string stopsAt)
    {
        Dictionary<string, string?> settings = new() { ["Recording:StopAfterHours"] = value };

        Assert.That(Problems(settings), Is.Empty);
        Assert.That(Warnings(settings), Is.EqualTo(new[]
        {
            $"Recording:StopAfterHours is {value}, above what one file to ElevenLabs can hold: recordings stop at {stopsAt} hours, half an hour within ElevenLabs:LongestFileHours (10)"
        }));
        Assert.That(Warnings(new() { ["Recording:StopAfterHours"] = "9.5" }), Is.Empty);
        Assert.That(Warnings(new() { ["Recording:StopAfterHours"] = "0" }), Is.Empty);
    }

    /// <summary>
    /// The longest file lowered to 5 hours refuses nothing anywhere: the default safety stop of 9 hours is held to 4.5 hours,
    /// with a warning, and a video's 4 hours fit within the hour kept below the file, so they are not mentioned.
    /// </summary>
    [Test]
    public void Lowering_the_longest_file_to_5_hours_stops_recordings_at_4_and_a_half_and_refuses_nothing()
    {
        Dictionary<string, string?> settings = new() { ["ElevenLabs:LongestFileHours"] = "5" };

        Assert.That(Problems(settings), Is.Empty);
        Assert.That(Warnings(settings), Is.EqualTo(new[]
        {
            "Recording:StopAfterHours is 9, above what one file to ElevenLabs can hold: recordings stop at 4.5 hours, half an hour within ElevenLabs:LongestFileHours (5)"
        }));
        Assert.That(TranscriptionLimits.From(new ElevenLabsOptions { LongestFileHours = 5 }).SafetyStop(new RecordingOptions().StopAfterHours), Is.EqualTo(TimeSpan.FromHours(4.5)));
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
            Assert.That(recording.MicrophoneFollows, Is.EqualTo("auto"), "N9: any app that holds the microphone, not one laptop's browser");
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
    /// The camera's settings (camera-first step 11): the picture is 720p unless Recording:CameraResolution says otherwise,
    /// and the wake of a sleeping webcam is off by default; the shipped file holds both at those values, and passes.
    /// </summary>
    [Test]
    public void The_cameras_settings_have_their_defaults_and_the_shipped_file_holds_them()
    {
        var shipped = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: false, reloadOnChange: false)
            .AddApiKeyFromEnvironment("test-key")
            .Build();
        var fromFile = shipped.GetSection(RecordingOptions.SectionName).Get<RecordingOptions>()!;
        var defaults = new RecordingOptions();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(defaults.CameraResolution, Is.EqualTo("720p"));
            Assert.That(defaults.WakeCameraMicrophone, Is.False);
            Assert.That(shipped["Recording:CameraResolution"], Is.EqualTo("720p"));
            Assert.That(shipped["Recording:WakeCameraMicrophone"], Is.EqualTo("False").IgnoreCase);
            Assert.That((fromFile.CameraResolution, fromFile.WakeCameraMicrophone), Is.EqualTo((defaults.CameraResolution, defaults.WakeCameraMicrophone)));
            Assert.That(new SettingsCheck().FindProblems(shipped), Is.Empty);
        }
    }

    /// <summary>
    /// The picture's size is read by the same parser as --resolution and the menu: the three sizes in any case and a typed
    /// size within the limits pass; anything else is refused with the setting's name, the value and the rule.
    /// </summary>
    [TestCase("480p", true)]
    [TestCase("1080P", true)]
    [TestCase("1024x576", true)]
    [TestCase("", false)]
    [TestCase("720", false)]
    [TestCase("1025x576", false)]
    [TestCase("4096x2160", false)]
    public void The_picture_size_is_checked_by_the_one_parser(string value, bool usable)
    {
        var problems = Problems(new() { ["Recording:CameraResolution"] = value });

        if (usable)
        {
            Assert.That(problems, Is.Empty);
            return;
        }

        Assert.That(problems, Has.Count.EqualTo(1));
        Assert.That(problems[0], Does.StartWith($"Recording:CameraResolution is \"{value}\"").And.Contain(CameraSize.Rule));
    }

    /// <summary>The wake is a yes or no: true and false in any case pass, and anything else is refused by its name.</summary>
    [TestCase("true", true)]
    [TestCase("False", true)]
    [TestCase("yes", false)]
    [TestCase("1x", false)]
    public void The_wake_is_a_yes_or_no(string value, bool usable)
    {
        var problems = Problems(new() { ["Recording:WakeCameraMicrophone"] = value });

        if (usable) Assert.That(problems, Is.Empty);
        else Assert.That(problems, Has.Some.Contains("Recording:WakeCameraMicrophone"));
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
    [TestCase("ElevenLabs:BaseUrl", "http://api.elevenlabs.io/")]
    [TestCase("ElevenLabs:ModelId", " ")]
    [TestCase("ElevenLabs:SlowestUploadMbps", "0")]
    [TestCase("ElevenLabs:SlowestUploadMbps", "-1")]
    [TestCase("ElevenLabs:SlowestUploadMbps", "NaN")]
    [TestCase("ElevenLabs:SlowestUploadMbps", "100000")]
    [TestCase("ElevenLabs:PricePerHourUsd", "-0.01")]
    [TestCase("ElevenLabs:PricePerHourUsd", "101")]
    [TestCase("ElevenLabs:KeytermsPricePerHourUsd", "NaN")]
    [TestCase("ElevenLabs:KeytermsPricePerHourUsd", "-1")]
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

    /// <summary>The prices behind the cost estimate are from 0 to 100 dollars an hour, 0 turning the price off; the message says so, with the price list's example.</summary>
    [TestCase("ElevenLabs:PricePerHourUsd", "0", true, "0.22")]
    [TestCase("ElevenLabs:PricePerHourUsd", "100", true, "0.22")]
    [TestCase("ElevenLabs:PricePerHourUsd", "-1", false, "0.22")]
    [TestCase("ElevenLabs:KeytermsPricePerHourUsd", "0", true, "0.05")]
    [TestCase("ElevenLabs:KeytermsPricePerHourUsd", "250", false, "0.05")]
    public void The_prices_behind_the_estimate_are_from_0_to_100_dollars_an_hour(string setting, string value, bool usable, string example)
    {
        var problems = Problems(new() { [setting] = value });

        Assert.That(problems, usable ? Is.Empty : Has.One.EqualTo($"{setting} is {value}; it must be from 0 to 100 (dollars an hour), such as {example}."));
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

    /// <summary>The Drive section's defaults: the folder ClassScribe in My Drive, and Google's own addresses.</summary>
    [Test]
    public void The_Drive_defaults_are_Googles_addresses_and_the_ClassScribe_folder()
    {
        var drive = new DriveOptions();

        Assert.That((drive.FolderName, drive.ApiBaseUrl, drive.OAuthBaseUrl), Is.EqualTo(("ClassScribe", "https://www.googleapis.com/", "https://oauth2.googleapis.com/")));
        Assert.That(Problems(new() { ["Drive:FolderName"] = "Classes of 2026 (Arabic)" }), Is.Empty);
    }

    /// <summary>The folder's name must be a plain name of at most 100 characters: not empty, and without a tab or a line break.</summary>
    [TestCase("", "Drive:FolderName is empty; it names the folder in My Drive that holds the classes, such as ClassScribe.")]
    [TestCase("   ", "Drive:FolderName is empty; it names the folder in My Drive that holds the classes, such as ClassScribe.")]
    [TestCase("Class\tScribe", "Drive:FolderName holds a tab, a line break or another control character; write a plain name, such as ClassScribe.")]
    public void The_Drive_folder_name_must_be_a_plain_name(string value, string problem)
    {
        Assert.That(Problems(new() { ["Drive:FolderName"] = value }), Is.EqualTo(new[] { problem }));
    }

    /// <summary>A name of 101 characters is refused, with the length; 100 is taken.</summary>
    [Test]
    public void The_Drive_folder_name_may_have_at_most_100_characters()
    {
        Assert.That(Problems(new() { ["Drive:FolderName"] = new string('a', 100) }), Is.Empty);
        Assert.That(Problems(new() { ["Drive:FolderName"] = new string('a', 101) }),
            Is.EqualTo(new[] { "Drive:FolderName is 101 characters long; it may be at most 100, such as ClassScribe." }));
    }

    /// <summary>What goes up and in pieces of what size: everything, in pieces of 8 MiB, unless the settings say otherwise.</summary>
    [Test]
    public void The_Drive_defaults_upload_everything_in_pieces_of_8_MiB()
    {
        var drive = new DriveOptions();

        Assert.That((drive.Upload, drive.ChunkMegabytes), Is.EqualTo((DriveUploadScope.Everything, 8)));
        Assert.That(Problems(new() { ["Drive:Upload"] = "withoutaudio", ["Drive:ChunkMegabytes"] = "256" }), Is.Empty);
    }

    /// <summary>Uploads after each class are on by default, once Drive is set up; false leaves them to menu choice 9 and drive upload.</summary>
    [Test]
    public void Drive_uploads_after_each_class_by_default()
    {
        Assert.That(new DriveOptions().Enabled, Is.True);
        Assert.That(Problems(new() { ["Drive:Enabled"] = "false" }), Is.Empty);
    }

    /// <summary>Drive:Upload takes Everything or WithoutAudio by name, in any case, and nothing else, not even a number.</summary>
    [TestCase("Everything", true)]
    [TestCase("WithoutAudio", true)]
    [TestCase("everything", true)]
    [TestCase("1", false)]
    [TestCase("Audio", false)]
    public void Drive_Upload_takes_the_names_of_its_values(string value, bool usable)
    {
        var problems = Problems(new() { ["Drive:Upload"] = value });

        Assert.That(problems, usable ? Is.Empty : Has.One.EqualTo($"Drive:Upload is \"{value}\", which is not Everything or WithoutAudio."));
    }

    /// <summary>A piece is from 1 to 256 MiB: a smaller one would cost a request per MiB, a larger one too much memory and too much to send again.</summary>
    [TestCase(0, false)]
    [TestCase(1, true)]
    [TestCase(256, true)]
    [TestCase(257, false)]
    [TestCase(-8, false)]
    public void Drive_ChunkMegabytes_is_from_1_to_256(int value, bool usable)
    {
        var problems = Problems(new() { ["Drive:ChunkMegabytes"] = value.ToString(System.Globalization.CultureInfo.InvariantCulture) });

        Assert.That(problems, usable ? Is.Empty : Is.EqualTo(new[] { $"Drive:ChunkMegabytes is {value}; it must be from 1 to 256." }));
    }

    /// <summary>
    /// Google's addresses must be https, or http only to this computer, for a local stub in a dry run: the client secret and
    /// the tokens are sent there, so plain http elsewhere is refused, and so is anything that is not a web address.
    /// </summary>
    [TestCase("https://www.googleapis.com/", true)]
    [TestCase("https://drive-stub.example/", true)]
    [TestCase("http://127.0.0.1:5005/", true)]
    [TestCase("http://localhost:5005", true)]
    [TestCase("http://www.googleapis.com/", false)]
    [TestCase("ftp://127.0.0.1/", false)]
    [TestCase("www.googleapis.com", false)]
    [TestCase("", false)]
    public void Google_addresses_must_be_https_or_a_local_stub(string value, bool usable)
    {
        foreach (var (setting, example) in new[]
                 {
                     ("Drive:ApiBaseUrl", "https://www.googleapis.com/"), ("Drive:OAuthBaseUrl", "https://oauth2.googleapis.com/"),
                     ("Drive:AuthorizationUrl", "https://accounts.google.com/o/oauth2/v2/auth")
                 })
        {
            var problems = Problems(new() { [setting] = value });

            Assert.That(problems, usable
                ? Is.Empty
                : Is.EqualTo(new[] { $"{setting} is \"{value}\"; it must be an https address, such as {example}, or http://127.0.0.1:<port>/ for a local stub." }));
        }
    }

    /// <summary>
    /// Google's sign-in page is its address alone, without ? or #: the sign-in adds its own parameters, the drive.file scope
    /// among them, which stays in code so that the settings can never ask for more of the Drive (architecture 7.4), and a
    /// parameter of the page's own could come before them. A page that is no https address gets that problem alone.
    /// </summary>
    [TestCase("https://accounts.google.com/o/oauth2/v2/auth?hl=ar")]
    [TestCase("https://accounts.google.com/o/oauth2/v2/auth?scope=https%3A%2F%2Fwww.googleapis.com%2Fauth%2Fdrive")]
    [TestCase("https://accounts.google.com/o/oauth2/v2/auth?")]
    [TestCase("https://accounts.google.com/o/oauth2/v2/auth#consent")]
    [TestCase("http://127.0.0.1:5007/o/oauth2/v2/auth?prompt=none")]
    public void The_sign_in_page_is_its_address_alone_without_a_query(string value)
    {
        Assert.That(Problems(new() { ["Drive:AuthorizationUrl"] = value }), Is.EqualTo(new[]
        {
            $"Drive:AuthorizationUrl is \"{value}\"; it must be the address of Google's sign-in page alone, without ? or #, such as https://accounts.google.com/o/oauth2/v2/auth, as ClassScribe adds the sign-in's own parameters to it."
        }));
        Assert.That(Problems(new() { ["Drive:AuthorizationUrl"] = "http://accounts.google.com/o/oauth2/v2/auth?hl=ar" }), Is.EqualTo(new[]
        {
            "Drive:AuthorizationUrl is \"http://accounts.google.com/o/oauth2/v2/auth?hl=ar\"; it must be an https address, such as https://accounts.google.com/o/oauth2/v2/auth, or http://127.0.0.1:<port>/ for a local stub."
        }));
    }

    // ------------------------------------------------------------------ the vendors' values (architecture 7.2)

    /// <summary>
    /// The vendors' values that were in code are settings now, at the values the code held: the API versions this build's
    /// requests and answers were written for, ElevenLabs' documented limits of a glossary and of one file, and Google's sign-in
    /// page. The defaults pass, and nothing is said about them.
    /// </summary>
    [Test]
    public void The_vendors_values_have_the_defaults_the_code_held()
    {
        var elevenLabs = new ElevenLabsOptions();
        var drive = new DriveOptions();

        using (Assert.EnterMultipleScope())
        {
            Assert.That((elevenLabs.ApiVersion, drive.ApiVersion), Is.EqualTo(("v1", "v3")));
            Assert.That((elevenLabs.ApiVersion, drive.ApiVersion), Is.EqualTo((ElevenLabsApi.WrittenFor, DriveApi.WrittenFor)), "the versions this build was written for");
            Assert.That(new KeytermLimits(elevenLabs.MaxKeyterms, elevenLabs.KeytermCharacterLimit, elevenLabs.MaxKeytermWords), Is.EqualTo(KeytermLimits.ElevenLabs));
            Assert.That((elevenLabs.MaxKeyterms, elevenLabs.KeytermCharacterLimit, elevenLabs.MaxKeytermWords), Is.EqualTo((1000, 50, 5)));
            Assert.That(elevenLabs.LongestFileHours, Is.EqualTo(10));
            Assert.That(TimeSpan.FromHours(elevenLabs.LongestFileHours), Is.EqualTo(TranscriptionLimits.ElevenLabs.LongestFile));
            Assert.That(drive.AuthorizationUrl, Is.EqualTo("https://accounts.google.com/o/oauth2/v2/auth"));
            Assert.That(Problems([]), Is.Empty);
            Assert.That(Warnings([]), Is.Empty);
        }
    }

    /// <summary>
    /// An API version goes into the address of every request, so it is v and one to three digits, nothing else: a slash, a
    /// space, a capital V or the digits alone are refused, with an example; a version this build was not written for is only
    /// warned about (see <see cref="An_API_version_other_than_the_one_this_build_was_written_for_is_warned_about"/>).
    /// </summary>
    [TestCase("v1", true)]
    [TestCase("v2", true)]
    [TestCase("v123", true)]
    [TestCase("v1/../x", false)]
    [TestCase("1", false)]
    [TestCase("V1", false)]
    [TestCase(" v1", false)]
    [TestCase("v", false)]
    [TestCase("v1234", false)]
    [TestCase("", false)]
    public void An_API_version_is_v_and_one_to_three_digits(string value, bool usable)
    {
        foreach (var (setting, example) in new[] { ("ElevenLabs:ApiVersion", "v1"), ("Drive:ApiVersion", "v3") })
        {
            var problems = Problems(new() { [setting] = value });

            Assert.That(problems, usable
                ? Is.Empty
                : Is.EqualTo(new[] { $"{setting} is \"{value}\"; it must be v and one to three digits, such as {example}, as it goes into the address of every request." }));
        }
    }

    /// <summary>
    /// A version other than the one this build's requests and answers were written for is accepted, with a warning that names
    /// both and how to put it back, since the service may refuse the requests or answer in another shape.
    /// </summary>
    [Test]
    public void An_API_version_other_than_the_one_this_build_was_written_for_is_warned_about()
    {
        Dictionary<string, string?> settings = new() { ["ElevenLabs:ApiVersion"] = "v2", ["Drive:ApiVersion"] = "v4" };

        Assert.That(Problems(settings), Is.Empty);
        Assert.That(Warnings(settings), Is.EqualTo(new[]
        {
            "ElevenLabs:ApiVersion is v2, but this build's requests and answers were written for v1, so ElevenLabs may refuse them or answer in a shape ClassScribe cannot read. ClassScribe settings reset ElevenLabs:ApiVersion puts it back",
            "Drive:ApiVersion is v4, but this build's requests and answers were written for v3, so Google Drive may refuse them or answer in a shape ClassScribe cannot read. ClassScribe settings reset Drive:ApiVersion puts it back"
        }));
        Assert.That(Warnings(new() { ["ElevenLabs:ApiVersion"] = "v1", ["Drive:ApiVersion"] = "v3" }), Is.Empty);
    }

    /// <summary>
    /// The glossary's limits and the longest file are refused only outside a sane range (question 10): from 1 to 10,000
    /// terms, a term shorter than 2 to 1,000 characters, 1 to 50 words, and 1 to 24 hours in one file.
    /// </summary>
    [TestCase("ElevenLabs:MaxKeyterms", "1", true)]
    [TestCase("ElevenLabs:MaxKeyterms", "10000", true)]
    [TestCase("ElevenLabs:MaxKeyterms", "0", false)]
    [TestCase("ElevenLabs:MaxKeyterms", "10001", false)]
    [TestCase("ElevenLabs:KeytermCharacterLimit", "2", true)]
    [TestCase("ElevenLabs:KeytermCharacterLimit", "1000", true)]
    [TestCase("ElevenLabs:KeytermCharacterLimit", "1", false)]
    [TestCase("ElevenLabs:KeytermCharacterLimit", "1001", false)]
    [TestCase("ElevenLabs:MaxKeytermWords", "1", true)]
    [TestCase("ElevenLabs:MaxKeytermWords", "50", true)]
    [TestCase("ElevenLabs:MaxKeytermWords", "0", false)]
    [TestCase("ElevenLabs:MaxKeytermWords", "51", false)]
    [TestCase("ElevenLabs:LongestFileHours", "1", true)]
    [TestCase("ElevenLabs:LongestFileHours", "24", true)]
    [TestCase("ElevenLabs:LongestFileHours", "0.5", false)]
    [TestCase("ElevenLabs:LongestFileHours", "25", false)]
    [TestCase("ElevenLabs:LongestFileHours", "NaN", false)]
    public void The_vendors_limits_are_refused_only_outside_a_sane_range(string setting, string value, bool usable)
    {
        var problems = Problems(new() { [setting] = value });

        if (usable)
        {
            Assert.That(problems, Is.Empty);
            return;
        }

        Assert.That(problems, Has.Count.EqualTo(1));
        Assert.That(problems[0], Does.StartWith($"{setting} is {value}; it must be from "));
    }

    /// <summary>
    /// A limit set above ElevenLabs' own is warned about, never refused: ElevenLabs would refuse the whole request, perhaps
    /// after an hour of upload, which the warning says, with how to put the limit back. At ElevenLabs' own value nothing is said.
    /// </summary>
    [TestCase("ElevenLabs:MaxKeyterms", "2000", "1000 terms in one glossary")]
    [TestCase("ElevenLabs:KeytermCharacterLimit", "60", "terms shorter than 50 characters")]
    [TestCase("ElevenLabs:MaxKeytermWords", "6", "terms of at most 5 words")]
    [TestCase("ElevenLabs:LongestFileHours", "12", "10 hours of audio in one file")]
    public void A_limit_above_ElevenLabs_own_is_warned_about_not_refused(string setting, string value, string documented)
    {
        Dictionary<string, string?> settings = new() { [setting] = value };

        Assert.That(Problems(settings), Is.Empty);
        Assert.That(Warnings(settings), Has.One.EqualTo(
            $"{setting} is {value}, above what ElevenLabs takes ({documented}): it refuses the whole request over it, perhaps after an hour of upload. ClassScribe settings reset {setting} puts it back"));
    }

    /// <summary>ElevenLabs' own limits, and any below them, bring no warning.</summary>
    [Test]
    public void ElevenLabs_own_limits_and_lower_ones_bring_no_warning()
    {
        Assert.That(Warnings(new()
        {
            ["ElevenLabs:MaxKeyterms"] = "1000",
            ["ElevenLabs:KeytermCharacterLimit"] = "20",
            ["ElevenLabs:MaxKeytermWords"] = "5",
            ["ElevenLabs:LongestFileHours"] = "10"
        }), Is.Empty);
    }

    /// <summary>
    /// ElevenLabs' address must be https, or http only to this computer, for a local stub: the API key travels in a header of
    /// every request, so a mistyped http:// to anywhere else would send it unencrypted (architecture 7.2).
    /// </summary>
    [TestCase("https://api.elevenlabs.io/", true)]
    [TestCase("http://127.0.0.1:5001/", true)]
    [TestCase("http://localhost:5001/", true)]
    [TestCase("http://example.com/", false)]
    [TestCase("http://api.elevenlabs.io/", false)]
    [TestCase("api.elevenlabs.io", false)]
    public void ElevenLabs_address_must_be_https_or_a_local_stub(string value, bool usable)
    {
        var problems = Problems(new() { ["ElevenLabs:BaseUrl"] = value });

        Assert.That(problems, usable
            ? Is.Empty
            : Is.EqualTo(new[] { $"ElevenLabs:BaseUrl is \"{value}\"; it must be an https address, such as https://api.elevenlabs.io/, or http://127.0.0.1:<port>/ for a local stub." }));
    }
}
