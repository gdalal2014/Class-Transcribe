using System.Globalization;
using System.Reflection;
using ClassScribe.Audio.Microphones;
using ClassScribe.Storage;
using ClassScribe.Transcription;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace ClassScribe.Settings;

/// <summary>
/// Checks settings before they are used. Settings are otherwise read only when first used, which for the transcript
/// layout is after the paid upload; here a value that cannot be read, or one that makes no sense, is found at once, with
/// a message that names the setting. The settings to check are an argument, so that a staged copy of them is checked
/// before any of it reaches the app (see <see cref="SettingsReloader"/>). The log's own settings are checked too, since
/// they are read again with the others.
/// </summary>
public sealed class SettingsCheck
{
    /// <summary>How to write a path in appsettings.json. A single backslash there escapes the next letter: \t becomes a tab, \n a line break.</summary>
    private const string PathAdvice = "In appsettings.json, write each backslash twice, as in \"D:\\\\Classes\", or use forward slashes, as in \"D:/Classes\".";

    /// <summary>The section that holds the log's settings.</summary>
    private const string LoggingSection = "Logging";

    /// <summary>The most Recording:ContinueWithinMinutes may be: a day.</summary>
    private const int LongestContinueWithinMinutes = 1440;

    /// <summary>The name of ClassScribe's own process, which the microphone must never follow.</summary>
    private const string OwnProcessName = "ClassScribe";

    /// <summary>What Recording:MicrophoneFollows takes, for its messages.</summary>
    private const string FollowsAdvice = $"it must be part of the class app's name, such as chrome, or {FollowRule.AnyWord} (any app that holds the microphone) or {FollowRule.NoneWord} (V alone).";

    /// <summary>The environment variables in which Windows names the OneDrive folders.</summary>
    private static readonly string[] OneDriveVariables = ["OneDrive", "OneDriveConsumer", "OneDriveCommercial"];

    /// <summary>Lists what is wrong with <paramref name="configuration"/>, one sentence per problem, each starting with the setting's name. Empty when it can be used.</summary>
    public IReadOnlyList<string> FindProblems(IConfiguration configuration)
    {
        List<string> problems = [];

        const string recordingSection = RecordingOptions.SectionName;
        var recording = Bind<RecordingOptions>(configuration, recordingSection, problems);
        CheckPath(problems, Name(recordingSection, nameof(RecordingOptions.OutputFolder)), recording.OutputFolder);
        CheckRange(problems, Name(recordingSection, nameof(RecordingOptions.FlushSeconds)), recording.FlushSeconds, 1);
        CheckRange(problems, Name(recordingSection, nameof(RecordingOptions.SilenceWarningSeconds)), recording.SilenceWarningSeconds, 1);
        CheckRange(problems, Name(recordingSection, nameof(RecordingOptions.StatusIntervalSeconds)), recording.StatusIntervalSeconds, 1);

        // A WAV file cannot describe more than 4 GB of audio, and tiny segments would scatter a class over hundreds of files.
        CheckRange(problems, Name(recordingSection, nameof(RecordingOptions.MaxSegmentMegabytes)), recording.MaxSegmentMegabytes, 10, 4000);

        // A class cut short by a crash or a restart is continued within minutes or hours. A recording that stopped more
        // than a day ago belongs to another class, and continuing it would join the two into one recording. 0 never offers.
        var continueWithin = recording.ContinueWithinMinutes;
        if (continueWithin is < 0 or > LongestContinueWithinMinutes)
            problems.Add(string.Create(CultureInfo.InvariantCulture,
                $"{Name(recordingSection, nameof(RecordingOptions.ContinueWithinMinutes))} is {continueWithin}; it must be from 0 to {LongestContinueWithinMinutes} (a day), such as 60. 0 never offers to continue a recording."));

        // ElevenLabs takes at most 10 hours of audio in one file, and ClassScribe sends at most the longest recording the
        // workflow allows: a later safety stop would let a recording grow too long to transcribe. 0 turns the stop off.
        var stopAfter = recording.StopAfterHours;
        var latestStop = TranscriptionWorkflow.LongestRecording.TotalHours;
        if (!double.IsFinite(stopAfter) || stopAfter < 0 || stopAfter > latestStop)
            problems.Add(string.Create(CultureInfo.InvariantCulture,
                $"{Name(recordingSection, nameof(RecordingOptions.StopAfterHours))} is {stopAfter}; it must be from 0 to {latestStop} (hours), such as 9. ElevenLabs takes at most 10 hours of audio in one file; 0 turns the safety stop off."));

        // The microphone track follows the app named here. Empty, it would follow nothing and record nothing; naming
        // ClassScribe, whose own capture holds the microphone for the whole class, it would follow ClassScribe and record
        // everything. A name part matches any app whose process name contains it, so any part of "ClassScribe" names it.
        var follows = Name(recordingSection, nameof(RecordingOptions.MicrophoneFollows));
        if (!FollowRule.TryParse(recording.MicrophoneFollows, out var rule))
            problems.Add($"{follows} is \"{recording.MicrophoneFollows}\"; {FollowsAdvice}");
        else if (rule is FollowRule.Named named && named.Matches(OwnProcessName))
            problems.Add($"{follows} is \"{recording.MicrophoneFollows}\", which names ClassScribe itself; {FollowsAdvice}");

        const string elevenLabsSection = ElevenLabsOptions.SectionName;
        var elevenLabs = Bind<ElevenLabsOptions>(configuration, elevenLabsSection, problems);
        var baseUrl = Name(elevenLabsSection, nameof(ElevenLabsOptions.BaseUrl));
        if (!Uri.TryCreate(elevenLabs.BaseUrl, UriKind.Absolute, out var address) || address.Scheme is not ("https" or "http"))
            problems.Add($"{baseUrl} is \"{elevenLabs.BaseUrl}\"; it must be a web address, such as https://api.elevenlabs.io/.");

        if (string.IsNullOrWhiteSpace(elevenLabs.ModelId))
            problems.Add($"{Name(elevenLabsSection, nameof(ElevenLabsOptions.ModelId))} is empty; it names the ElevenLabs model, such as scribe_v2.");

        var keytermsFile = Name(elevenLabsSection, nameof(ElevenLabsOptions.KeytermsFile));
        if (elevenLabs.KeytermsFile is null)
            problems.Add($"{keytermsFile} is null; write a file name, such as keyterms.txt, or \"\" to send no glossary.");
        else
            CheckPath(problems, keytermsFile, elevenLabs.KeytermsFile);

        // A day is far more than an upload and its transcript take; HttpClient itself stops at about 24 days.
        CheckRange(problems, Name(elevenLabsSection, nameof(ElevenLabsOptions.TimeoutMinutes)), elevenLabs.TimeoutMinutes, 1, 1440);
        CheckRange(problems, Name(elevenLabsSection, nameof(ElevenLabsOptions.MaxAttempts)), elevenLabs.MaxAttempts, 1, 10);
        CheckRange(problems, Name(elevenLabsSection, nameof(ElevenLabsOptions.RetryDelaySeconds)), elevenLabs.RetryDelaySeconds, 0, 3600);

        // A tenth of a megabit per second still gives the longest recording ElevenLabs takes a time limit of about a day;
        // a gigabit line is faster than any upload needs to be allowed for.
        CheckRange(problems, Name(elevenLabsSection, nameof(ElevenLabsOptions.SlowestUploadMbps)), elevenLabs.SlowestUploadMbps, 0.1, 1000, "megabits per second", "1");

        // The prices behind the cost estimate: 0 turns the estimate off, and a hundred dollars an hour is far above any
        // price ElevenLabs lists; a negative price or NaN is a slip.
        CheckRange(problems, Name(elevenLabsSection, nameof(ElevenLabsOptions.PricePerHourUsd)), elevenLabs.PricePerHourUsd, 0, 100, "dollars an hour", "0.22");
        CheckRange(problems, Name(elevenLabsSection, nameof(ElevenLabsOptions.KeytermsPricePerHourUsd)), elevenLabs.KeytermsPricePerHourUsd, 0, 100, "dollars an hour", "0.05");

        const string transcriptSection = TranscriptOptions.SectionName;
        var transcript = Bind<TranscriptOptions>(configuration, transcriptSection, problems);

        // 0 turns a kind of paragraph break off, as TranscriptFormatter reads it. A value below 0 would do the
        // same there, but it is far more likely a slip than a way of saying "off", so it is refused.
        var pause = transcript.ParagraphPauseSeconds;
        if (!double.IsFinite(pause) || pause < 0)
            problems.Add($"{Name(transcriptSection, nameof(TranscriptOptions.ParagraphPauseSeconds))} is {pause.ToString(CultureInfo.InvariantCulture)}; it must be 0 or more, such as 2.0. 0 turns paragraph breaks at pauses off.");

        var length = transcript.MaxParagraphCharacters;
        if (length < 0)
            problems.Add(string.Create(CultureInfo.InvariantCulture, $"{Name(transcriptSection, nameof(TranscriptOptions.MaxParagraphCharacters))} is {length}; it must be 0 or more, such as 900. 0 turns the splitting of long paragraphs off."));

        // 0 writes no heading for silence left out, as the transcript reads it; below 0 is a slip, as above.
        var gap = transcript.ShowGapsLongerThanSeconds;
        if (gap < 0)
            problems.Add(string.Create(CultureInfo.InvariantCulture, $"{Name(transcriptSection, nameof(TranscriptOptions.ShowGapsLongerThanSeconds))} is {gap}; it must be 0 or more, such as 60. 0 turns the headings for silence left out off."));

        CheckLogging(configuration, problems);
        return problems;
    }

    /// <summary>
    /// Lists what is worth saying about <paramref name="configuration"/> without refusing it: an API key written into the
    /// settings, which is never used, and a recordings folder inside OneDrive.
    /// </summary>
    public IReadOnlyList<string> FindWarnings(IConfiguration configuration)
    {
        List<string> warnings = [];
        if (ApiKeyFromEnvironment.IsSetElsewhere(configuration))
            warnings.Add($"{ElevenLabsOptions.ApiKeySetting} is set in the settings, in appsettings.json or as an environment variable. It is not used: the key is read only from {ElevenLabsOptions.ApiKeyVariable}. Remove it from the file, so it cannot end up in git or be shared by mistake");

        // A path that cannot be a path is a problem of its own (see FindProblems), not a place to look up.
        var outputFolder = configuration[Name(RecordingOptions.SectionName, nameof(RecordingOptions.OutputFolder))];
        if (outputFolder is null || outputFolder.IndexOfAny(Path.GetInvalidPathChars()) < 0)
        {
            var root = ClassFolders.RootFor(outputFolder);
            if (IsInsideOneDrive(root))
                warnings.Add($"{Name(RecordingOptions.SectionName, nameof(RecordingOptions.OutputFolder))} is inside OneDrive ({root}). OneDrive uploads a recording again and again while it grows, and can hold files that ClassScribe needs to delete; a folder outside OneDrive, such as %USERPROFILE%\\ClassScribe, is safer");
        }

        return warnings;
    }

    /// <summary>True when <paramref name="folder"/> is a OneDrive folder that Windows names, or lies inside one.</summary>
    private static bool IsInsideOneDrive(string folder) =>
        OneDriveVariables
            .Select(Environment.GetEnvironmentVariable)
            .OfType<string>()
            .Select(oneDrive => oneDrive.Trim())
            .Where(oneDrive => oneDrive.Length > 0 && oneDrive.IndexOfAny(Path.GetInvalidPathChars()) < 0 && Path.IsPathFullyQualified(oneDrive))
            .Select(oneDrive => Path.TrimEndingDirectorySeparator(Path.GetFullPath(oneDrive)))
            .Any(oneDrive => folder.Equals(oneDrive, StringComparison.OrdinalIgnoreCase)
                || folder.StartsWith(oneDrive + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The log's settings. Each log level, under Logging:LogLevel and under a log's own section such as
    /// Logging:Console:LogLevel, must be a level's name; an empty one is passed over, as the log passes it over. The
    /// console log's settings and those of its formats must be values the console log takes. Read again while
    /// ClassScribe runs, a value the log cannot take would keep it from taking any new settings at all.
    /// </summary>
    private static void CheckLogging(IConfiguration configuration, List<string> problems)
    {
        foreach (var section in configuration.GetSection(LoggingSection).GetChildren())
        {
            var levels = section.Key.Equals("LogLevel", StringComparison.OrdinalIgnoreCase) ? section : section.GetSection("LogLevel");
            foreach (var (key, text) in levels.AsEnumerable())
            {
                if (string.IsNullOrEmpty(text) || IsName(typeof(LogLevel), text)) continue;
                problems.Add($"{key} is \"{text}\", which is not {Names(typeof(LogLevel))}.");
            }
        }

        var captureScopes = $"{LoggingSection}:CaptureScopes";
        if (configuration[captureScopes] is { } scopes && !bool.TryParse(scopes, out _))
            problems.Add($"{captureScopes} is \"{scopes}\", which is not true or false.");

        CheckBinding<ConsoleLoggerOptions>(configuration, $"{LoggingSection}:Console", problems);

        // Every format of the console log reads the same section; the two richest cover the settings of the others.
        var formatterOptions = $"{LoggingSection}:Console:FormatterOptions";
        if (CheckBinding<SimpleConsoleFormatterOptions>(configuration, formatterOptions, problems))
            CheckBinding<JsonConsoleFormatterOptions>(configuration, formatterOptions, problems);
    }

    /// <summary>
    /// Binds the section at <paramref name="path"/> to <typeparamref name="TOptions"/> as the log does, and adds a problem
    /// when that fails: a value of the wrong kind, or one the log refuses. Returns true when the section can be used.
    /// </summary>
    private static bool CheckBinding<TOptions>(IConfiguration configuration, string path, List<string> problems) where TOptions : new()
    {
        try
        {
            configuration.GetSection(path).Bind(new TOptions());
            return true;
        }
        catch (Exception ex)
        {
            // Deliberately not rethrown: a value the log cannot take is a problem to report with the others, in words,
            // and the reason is in the problem; it is not a failure of the check itself.
            var reason = ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
            problems.Add($"{path} holds a value the console log cannot use: {reason.Message}");
            return false;
        }
    }

    /// <summary>
    /// Binds a section the way the app does, after reporting each value that cannot be read as the type
    /// its setting needs. Those values are left out of the binding, so that the section's other settings
    /// can still be checked; the settings left out keep their defaults here.
    /// </summary>
    private static TOptions Bind<TOptions>(IConfiguration configuration, string section, List<string> problems) where TOptions : class, new()
    {
        var unreadable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in typeof(TOptions).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(property => property.CanWrite))
        {
            var key = Name(section, property.Name);
            if (configuration[key] is not { } text || Expected(property.PropertyType, text) is not { } expected) continue;

            problems.Add($"{key} is \"{text}\", which is not {expected}.");
            unreadable.Add(key);
        }

        var readable = configuration.GetSection(section).AsEnumerable().Where(setting => !unreadable.Contains(setting.Key));
        return new ConfigurationBuilder().AddInMemoryCollection(readable).Build().GetSection(section).Get<TOptions>() ?? new TOptions();
    }

    /// <summary>
    /// What a setting of type <paramref name="type"/> must look like, when <paramref name="text"/> cannot
    /// be read as one; null when it can. The rules are those the settings are bound with, except that an
    /// enum setting takes only the names of its values, in any case: the binding would also take a number,
    /// or several names joined by a comma, and read them as some value without saying which.
    /// </summary>
    private static string? Expected(Type type, string text)
    {
        if (type == typeof(int))
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out _) ? null : "a whole number, such as 60";

        if (type == typeof(double))
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _) ? null : "a number written with a dot, such as 1.5";

        if (type == typeof(bool))
            return bool.TryParse(text, out _) ? null : "true or false";

        if (type.IsEnum)
            return IsName(type, text) ? null : Names(type);

        return null;
    }

    /// <summary>True when <paramref name="text"/> is the name of one of the values of the enum <paramref name="type"/>, in any case.</summary>
    private static bool IsName(Type type, string text) =>
        Enum.GetNames(type).Any(name => name.Equals(text.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The names of the values of the enum <paramref name="type"/>, as in "OnBattery, Always or Never".</summary>
    private static string Names(Type type)
    {
        var names = Enum.GetNames(type);
        return names.Length == 1 ? names[0] : $"{string.Join(", ", names[..^1])} or {names[^1]}";
    }

    /// <summary>Adds a problem when a whole-number setting is below <paramref name="min"/> or above <paramref name="max"/>.</summary>
    private static void CheckRange(List<string> problems, string name, int value, int min, int max = int.MaxValue)
    {
        if (value >= min && value <= max) return;

        if (max == int.MaxValue)
            problems.Add(string.Create(CultureInfo.InvariantCulture, $"{name} is {value}; it must be {min} or more."));
        else
            problems.Add(string.Create(CultureInfo.InvariantCulture, $"{name} is {value}; it must be from {min} to {max}."));
    }

    /// <summary>Adds a problem when a number setting is not a number from <paramref name="min"/> to <paramref name="max"/>, saying in what <paramref name="unit"/>, with an <paramref name="example"/>.</summary>
    private static void CheckRange(List<string> problems, string name, double value, double min, double max, string unit, string example)
    {
        if (double.IsFinite(value) && value >= min && value <= max) return;

        problems.Add(string.Create(CultureInfo.InvariantCulture, $"{name} is {value}; it must be from {min} to {max} ({unit}), such as {example}."));
    }

    /// <summary>Adds a problem when a path holds a character no path can hold, which is what a single backslash in appsettings.json often turns into.</summary>
    private static void CheckPath(List<string> problems, string name, string? path)
    {
        if (path is not null && path.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            problems.Add($"{name} holds a tab, a line break or another character that cannot be part of a path. {PathAdvice}");
    }

    /// <summary>The full name of a setting, such as Recording:FlushSeconds.</summary>
    private static string Name(string section, string setting) => $"{section}:{setting}";
}
