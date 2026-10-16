using System.Globalization;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Settings;

/// <summary>
/// Checks the settings before anything is recorded or uploaded. Settings are otherwise read only when
/// first used, which for the transcript layout is after the paid upload; here a value that cannot be
/// read, or one that makes no sense, stops ClassScribe at once, with a message that names the setting.
/// AppHost reads appsettings.json once, at start, so the values checked are those used for the whole
/// run. It also warns about an API key written into the settings, which is never used.
/// </summary>
public sealed class SettingsCheck(IConfiguration configuration, ILogger<SettingsCheck> logger)
{
    /// <summary>How to write a path in appsettings.json. A single backslash there escapes the next letter: \t becomes a tab, \n a line break.</summary>
    private const string PathAdvice = "In appsettings.json, write each backslash twice, as in \"D:\\\\Classes\", or use forward slashes, as in \"D:/Classes\".";

    /// <summary>
    /// Reports each problem with the settings as an error, and a key written into the settings as a
    /// warning. Returns true when the settings can be used.
    /// </summary>
    public bool AreUsable()
    {
        if (ApiKeyFromEnvironment.IsSetElsewhere(configuration))
        {
            logger.LogWarning(
                "{Setting} is set in the settings, in appsettings.json or as an environment variable. It is not used: the key is read only from {Variable}. Remove it from the file, so it cannot end up in git or be shared by mistake",
                ElevenLabsOptions.ApiKeySetting, ElevenLabsOptions.ApiKeyVariable);
        }

        var problems = FindProblems();
        foreach (var problem in problems) logger.LogError("{Problem}", problem);
        if (problems.Count > 0) logger.LogError("Nothing was started. Fix the settings above in appsettings.json, then start ClassScribe again");

        return problems.Count == 0;
    }

    /// <summary>Lists what is wrong with the settings, one sentence per problem, each starting with the setting's name. Empty when they can be used.</summary>
    public IReadOnlyList<string> FindProblems()
    {
        List<string> problems = [];

        const string recordingSection = RecordingOptions.SectionName;
        var recording = Bind<RecordingOptions>(recordingSection, problems);
        CheckPath(problems, Name(recordingSection, nameof(RecordingOptions.OutputFolder)), recording.OutputFolder);
        CheckRange(problems, Name(recordingSection, nameof(RecordingOptions.FlushSeconds)), recording.FlushSeconds, 1);
        CheckRange(problems, Name(recordingSection, nameof(RecordingOptions.SilenceWarningSeconds)), recording.SilenceWarningSeconds, 1);
        CheckRange(problems, Name(recordingSection, nameof(RecordingOptions.StatusIntervalSeconds)), recording.StatusIntervalSeconds, 1);

        // A WAV file cannot describe more than 4 GB of audio, and tiny segments would scatter a class over hundreds of files.
        CheckRange(problems, Name(recordingSection, nameof(RecordingOptions.MaxSegmentMegabytes)), recording.MaxSegmentMegabytes, 10, 4000);

        const string elevenLabsSection = ElevenLabsOptions.SectionName;
        var elevenLabs = Bind<ElevenLabsOptions>(elevenLabsSection, problems);
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

        const string transcriptSection = TranscriptOptions.SectionName;
        var transcript = Bind<TranscriptOptions>(transcriptSection, problems);

        // 0 turns a kind of paragraph break off, as TranscriptFormatter reads it. A value below 0 would do the
        // same there, but it is far more likely a slip than a way of saying "off", so it is refused.
        var pause = transcript.ParagraphPauseSeconds;
        if (!double.IsFinite(pause) || pause < 0)
            problems.Add($"{Name(transcriptSection, nameof(TranscriptOptions.ParagraphPauseSeconds))} is {pause.ToString(CultureInfo.InvariantCulture)}; it must be 0 or more, such as 2.0. 0 turns paragraph breaks at pauses off.");

        var length = transcript.MaxParagraphCharacters;
        if (length < 0)
            problems.Add(string.Create(CultureInfo.InvariantCulture, $"{Name(transcriptSection, nameof(TranscriptOptions.MaxParagraphCharacters))} is {length}; it must be 0 or more, such as 900. 0 turns the splitting of long paragraphs off."));

        return problems;
    }

    /// <summary>
    /// Binds a section the way the app does, after reporting each value that cannot be read as the type
    /// its setting needs. Those values are left out of the binding, so that the section's other settings
    /// can still be checked; the settings left out keep their defaults here.
    /// </summary>
    private TOptions Bind<TOptions>(string section, List<string> problems) where TOptions : class, new()
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
    /// be read as one; null when it can. The rules are those the settings are bound with.
    /// </summary>
    private static string? Expected(Type type, string text)
    {
        if (type == typeof(int))
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out _) ? null : "a whole number, such as 60";

        if (type == typeof(double))
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _) ? null : "a number written with a dot, such as 1.5";

        if (type == typeof(bool))
            return bool.TryParse(text, out _) ? null : "true or false";

        return null;
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

    /// <summary>Adds a problem when a path holds a character no path can hold, which is what a single backslash in appsettings.json often turns into.</summary>
    private static void CheckPath(List<string> problems, string name, string? path)
    {
        if (path is not null && path.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            problems.Add($"{name} holds a tab, a line break or another character that cannot be part of a path. {PathAdvice}");
    }

    /// <summary>The full name of a setting, such as Recording:FlushSeconds.</summary>
    private static string Name(string section, string setting) => $"{section}:{setting}";
}
