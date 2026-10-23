using System.Text;
using System.Text.Json;
using ClassScribe.Drive;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Settings;

/// <summary>A setting a recording left out of a settings file that failed the check (build-plan amendment 4).</summary>
/// <param name="Key">The setting, such as Recording:FlushSeconds.</param>
/// <param name="Refused">The value the file gave it, which the check refused.</param>
/// <param name="InPlace">The value the recording uses in its place: an environment variable's, or the default.</param>
public sealed record DroppedSetting(string Key, string? Refused, string InPlace)
{
    /// <summary>What a recording's first lines say of it, after the file's path.</summary>
    public string Describe(string path) => $"{Key} is \"{Refused}\" in {path}, which the check refuses; this recording uses {InPlace} in its place.";
}

/// <summary>What reading the settings again found: whether they can be used, what is wrong if not, and which file was read.</summary>
public sealed record SettingsVerdict(bool Usable, IReadOnlyList<string> Problems, string Path)
{
    /// <summary>What is worth saying about the file without refusing it, such as an API key written into it.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>
    /// While the user's own file is missing (see <see cref="SettingsOrigin.Missing"/>), what is said about it: the defaults
    /// beside the program were read for now, and no change is written. Null otherwise.
    /// </summary>
    public string? Missing { get; init; }

    /// <summary>
    /// For a recording, the settings left out of a file that failed the check, each with the value it gave and the one used
    /// instead (build-plan amendment 4); the file's problems stay in <see cref="Problems"/>, for the menu's header. Empty
    /// when nothing was left out.
    /// </summary>
    public IReadOnlyList<DroppedSetting> Dropped { get; init; } = [];

    /// <summary>
    /// For a recording, why nothing of the file could be taken (not valid JSON, unreadable, gone), and that the settings last
    /// read, or the defaults, and the command line are used instead; empty otherwise.
    /// </summary>
    public IReadOnlyList<string> Kept { get; init; } = [];
}

/// <summary>
/// Reads appsettings.json again and lets its values into the app's settings only once they have passed the check. The
/// file's text is read as the host's own JSON reader reads it, a staged copy of the settings is built from the live
/// sources with those values in the file's place, and the check runs on the copy. Only then are the values accepted into
/// <see cref="CheckedSettingsSource"/> and the live settings reloaded. A plain reload of the file would make a value live
/// before anything could check it, and one that cannot be read breaks every read of the settings after it.
/// </summary>
public sealed class SettingsReloader(ConfigurationManager configuration, CheckedSettingsSource accepted, SettingsFile file, SettingsCheck check, ILogger<SettingsReloader> logger)
{
    /// <summary>How to write a path in JSON. A single backslash there escapes the next letter, which is the usual reason a file is not valid JSON.</summary>
    public const string JsonPathAdvice = "In JSON, write each backslash in a path twice, as in \"D:\\\\Classes\", or use forward slashes, as in \"D:/Classes\".";

    /// <summary>The mark an editor may put at the start of a UTF-8 file, U+FEFF. It is no part of the settings.</summary>
    private const char ByteOrderMark = (char)0xFEFF;

    /// <summary>The sections a separate recorder is given: ClassScribe's own settings and the log's.</summary>
    private static readonly string[] SnapshotSections =
        [RecordingOptions.SectionName, ElevenLabsOptions.SectionName, TranscriptOptions.SectionName, DriveOptions.SectionName, YouTubeOptions.SectionName, "Logging"];

    /// <summary>One reload at a time.</summary>
    private readonly Lock _gate = new();

    /// <summary>The verdict of the last reading of the file; null before the first.</summary>
    private SettingsVerdict? _last;

    /// <summary>
    /// Whether the values live now came from the file, as it was when it was last accepted. A file that is gone since then
    /// was deleted or renamed by mistake, or is being saved, far more likely than meant to bring back the defaults, which
    /// would send the next class to another folder or device; without the file from the start, the defaults apply.
    /// </summary>
    private bool _fileAccepted;

    /// <summary>
    /// What the last reading of the file found: at start, or before the last menu choice or command. Null before the
    /// first. The menu shows it, so that a file that cannot be used is plain before any choice is made.
    /// </summary>
    public SettingsVerdict? Last => Volatile.Read(ref _last);

    /// <summary>
    /// The settings the user's own file holds at a default a later build changed (see <see cref="DefaultChanges"/>), as its
    /// values were last accepted; none while another file is read, whose values are the build's own.
    /// </summary>
    public IReadOnlyList<DefaultChange> OlderDefaultsHeld() =>
        file.Places.Origin == SettingsOrigin.UserFolder ? DefaultChanges.HeldIn(accepted.Values) : [];

    /// <summary>
    /// Reads the file, checks it as it would be used, and makes it the live settings when nothing is wrong with it. When
    /// something is, the verdict says what, and the live settings stay as they were: nothing of the file is taken. The
    /// verdict is kept as <see cref="Last"/>. For a recording (<paramref name="forRecording"/>, build-plan amendment 4), a
    /// refused file never stops the class: a file that is valid JSON keeps every value that passes and drops only the
    /// settings the check refuses, each named with its value and the one used in its place (<see cref="SettingsVerdict.Dropped"/>);
    /// a file that cannot be read, is not valid JSON or is gone gives the settings last read, or the defaults, and the
    /// command line, and says so (<see cref="SettingsVerdict.Kept"/>). Either way the verdict is usable.
    /// </summary>
    public SettingsVerdict TryReload(bool forRecording = false)
    {
        lock (_gate)
        {
            var verdict = ReadAndAccept(forRecording);
            Volatile.Write(ref _last, verdict);
            return verdict;
        }
    }

    /// <summary>
    /// The reading itself: reads the file, checks a staged copy of the settings with its values, and accepts them when
    /// the check finds nothing wrong. A file that is gone after its values were accepted is refused, and those values
    /// stay (see <see cref="_fileAccepted"/>). For a recording, see <see cref="TryReload"/>. Call with the lock held.
    /// </summary>
    private SettingsVerdict ReadAndAccept(bool forRecording)
    {
        IReadOnlyDictionary<string, string?> candidate;
        bool fromFile;
        var source = file.Places.Current();
        var path = source.ReadPath;
        try
        {
            var text = file.Read(source, expectFile: _fileAccepted);
            if (text.Bytes is null && _fileAccepted)
                return Refused(path, [text.Origin == SettingsOrigin.UserFolder ? UserFileGone(path) : $"{path} is no longer there, so the settings stay as they were when it was last read. Put the file back, or start ClassScribe again to go back to the defaults."], forRecording);

            // An empty file is far more likely an accident, such as a save cut short, than a wish for the defaults.
            if (text.Bytes is { } bytes && string.IsNullOrWhiteSpace(Encoding.UTF8.GetString(bytes).TrimStart(ByteOrderMark)))
                return Refused(path, ["appsettings.json is empty. Write { } in it at least; the settings it leaves out then keep their defaults."], forRecording);

            candidate = Parse(text);
            fromFile = text.Bytes is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Deliberately not rethrown: a file that cannot be read now refuses this run, the verdict says why, and the
            // settings stay as they were. The next choice or command reads it again.
            logger.LogDebug(ex, "Could not read {Path}", path);
            return Refused(path, [$"{path} could not be read: {ex.Message}"], forRecording);
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            // Deliberately not rethrown: a file that is not valid JSON refuses this run, the verdict says where the
            // mistake is, and the settings stay as they were.
            logger.LogDebug(ex, "{Path} is not valid JSON", path);
            return Refused(path, DescribeUnreadable(ex), forRecording);
        }

        var (problems, warnings) = Check(candidate);
        var problemTexts = problems.Select(problem => problem.Text).ToList();
        var dropped = new List<DroppedSetting>();
        if (problems.Count > 0)
        {
            if (!forRecording) return Refused(path, problemTexts) with { Warnings = warnings };
            (candidate, dropped, problems, warnings) = DropRefused(candidate, problems, warnings);
            if (problems.Count > 0) return Refused(path, problemTexts, forRecording) with { Warnings = warnings };
        }

        if (Accept(candidate, path) is { } failure) return Refused(path, [failure], forRecording) with { Warnings = warnings };

        _fileAccepted = fromFile;
        logger.LogDebug("Read the settings in {Path}", path);
        return new SettingsVerdict(true, problemTexts, path)
        {
            Warnings = warnings,
            Missing = source.Origin == SettingsOrigin.Missing ? file.Places.DescribeMissing() : null,
            Dropped = dropped
        };
    }

    /// <summary>What the check finds wrong with <paramref name="candidate"/> as the file's values, and what it warns about.</summary>
    private (IReadOnlyList<SettingsProblem> Problems, IReadOnlyList<string> Warnings) Check(IReadOnlyDictionary<string, string?> candidate)
    {
        var staged = Stage(candidate);
        try
        {
            return (check.FindProblemsByKey(staged), check.FindWarnings(staged));
        }
        finally
        {
            (staged as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// For a recording: <paramref name="candidate"/> without the settings <paramref name="problems"/> name (each with every
    /// value under it, so a list's entry or a section goes whole), checked again until nothing more is refused, a few rounds
    /// at most; with what was left out, each with the value used in its place, and what is still refused, if anything.
    /// </summary>
    private (IReadOnlyDictionary<string, string?> Candidate, List<DroppedSetting> Dropped, IReadOnlyList<SettingsProblem> Problems, IReadOnlyList<string> Warnings)
        DropRefused(IReadOnlyDictionary<string, string?> candidate, IReadOnlyList<SettingsProblem> problems, IReadOnlyList<string> warnings)
    {
        var kept = new Dictionary<string, string?>(candidate, StringComparer.OrdinalIgnoreCase);
        var removed = new List<(string Key, string? Value)>();
        for (var round = 0; round < 5 && problems.Count > 0; round++)
        {
            var named = problems.SelectMany(problem => problem.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var going = kept.Keys.Where(key => named.Any(name => key.Equals(name, StringComparison.OrdinalIgnoreCase) || key.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase))).ToList();
            if (going.Count == 0) break;

            foreach (var key in going)
            {
                removed.Add((key, kept[key]));
                kept.Remove(key);
            }

            (problems, warnings) = Check(kept);
        }

        var staged = Stage(kept);
        try
        {
            var dropped = removed.Select(pair => new DroppedSetting(pair.Key, pair.Value, InPlace(staged, pair.Key))).ToList();
            return (kept, dropped, problems, warnings);
        }
        finally
        {
            (staged as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// The value a recording uses in place of a setting it left out: an environment variable's or the Logging defaults' as
    /// the staged settings hold it, else the setting's default in code, else none.
    /// </summary>
    private static string InPlace(IConfiguration staged, string key)
    {
        if (staged[key] is { } held) return $"\"{held}\"";
        if (SettingsCatalog.Find(key) is { Default: { } defaultValue }) return $"the default, \"{SettingsCatalog.Show(defaultValue)}\",";
        return "nothing (the default)";
    }

    /// <summary>
    /// What is said when the user's own file, read before, is gone while ClassScribe runs: the settings stay as they were,
    /// and how to put the file back or start again from the defaults (layout-config 1.4).
    /// </summary>
    private string UserFileGone(string path) =>
        $"{path} is no longer there, so the settings stay as they were when it was last read. Put it back ({SettingsPlaces.PreviousFileName} beside it is the version before your last change), or start again from the defaults with: ClassScribe settings reset --all.";

    /// <summary>
    /// What a settings file holding <paramref name="bytes"/> would give, through the reloader's own parse, staging and
    /// check, without accepting any of it (layout-config 1.6 step 5): the settings writer checks a change with it before
    /// writing it, and settings list and get read the values in force with it. Null bytes stand for no file. The caller
    /// disposes the result.
    /// </summary>
    public StagedSettings Stage(byte[]? bytes)
    {
        if (bytes is not null && string.IsNullOrWhiteSpace(Encoding.UTF8.GetString(bytes).TrimStart(ByteOrderMark)))
            return StagedSettings.Unusable(["appsettings.json is empty. Write { } in it at least; the settings it leaves out then keep their defaults."]);

        IReadOnlyDictionary<string, string?> values;
        try
        {
            values = Parse(new SettingsText("", bytes));
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            // Deliberately not rethrown: bytes that are not valid JSON are the answer asked for, in the reloader's own words.
            logger.LogDebug(ex, "The settings staged are not valid JSON");
            return StagedSettings.Unusable(DescribeUnreadable(ex));
        }

        var staged = Stage(values);
        return new StagedSettings([], values, staged, check.FindProblemsByKey(staged), check.FindWarnings(staged));
    }

    /// <summary>
    /// The settings a separate recorder is given: the Recording, ElevenLabs, Transcript, Drive and Logging sections as they
    /// are now, overrides from environment variables included, by setting name, such as Recording:FlushSeconds. None of the
    /// Drive settings holds a secret: the Google client and sign-in are never in the settings. Never the whole of the
    /// settings, which hold every environment variable, ELEVENLABS_API_KEY among them, and never the key: ElevenLabs:ApiKey
    /// is left out, and so is any value that holds the key.
    /// </summary>
    public IReadOnlyDictionary<string, string?> Snapshot()
    {
        var key = configuration[ElevenLabsOptions.ApiKeySetting]?.Trim();
        var snapshot = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var section in SnapshotSections)
        {
            foreach (var (name, value) in configuration.GetSection(section).AsEnumerable())
            {
                if (value is null || name.Equals(ElevenLabsOptions.ApiKeySetting, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.IsNullOrEmpty(key) && value.Contains(key, StringComparison.Ordinal)) continue;

                snapshot[name] = value;
            }
        }

        return snapshot;
    }

    /// <summary>A JSON mistake in words, with its line counted from 1, as an editor counts, rather than the parser's own count from 0.</summary>
    public static string DescribeJsonMistake(JsonException json)
    {
        if (json.LineNumber is not { } line) return json.Message;

        var message = json.Message;
        var position = message.IndexOf(" LineNumber:", StringComparison.Ordinal);
        return $"The mistake is on line {line + 1}: {(position >= 0 ? message[..position] : message)}";
    }

    /// <summary>A verdict that refuses the file at <paramref name="path"/> for <paramref name="problems"/>.</summary>
    private static SettingsVerdict Refused(string path, IReadOnlyList<string> problems) => new(false, problems, path);

    /// <summary>
    /// The same, unless it is for a recording (<paramref name="forRecording"/>), which a refused file never stops: then the
    /// settings stay as they were last read, or the defaults where nothing was, with the command line over them, the verdict
    /// is usable, and <see cref="SettingsVerdict.Kept"/> says so after the problems (build-plan amendment 4).
    /// </summary>
    private static SettingsVerdict Refused(string path, IReadOnlyList<string> problems, bool forRecording) =>
        forRecording
            ? new SettingsVerdict(true, problems, path)
            {
                Kept = [.. problems, $"Nothing of {path} could be taken, so this recording uses the settings ClassScribe read last, or the defaults where it read none, and what the command line says."]
            }
            : Refused(path, problems);

    /// <summary>What to say about a file that is not valid JSON: where the mistake is and how to write a path, or what else is wrong with its shape.</summary>
    private static IReadOnlyList<string> DescribeUnreadable(Exception failure) =>
        failure is JsonException json
            ? [DescribeJsonMistake(json), JsonPathAdvice]
            : [$"appsettings.json cannot be read: {failure.Message}"];

    /// <summary>The values the file holds, by setting name, as a JSON settings source would hold them; none when there is no file.</summary>
    private static IReadOnlyDictionary<string, string?> Parse(SettingsText text) =>
        text.Bytes is { } bytes ? JsonValues.Read(bytes) : new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The settings as they would be with <paramref name="candidate"/> as the file's values: the live sources in their
    /// order, environment variables and the key included, with the candidate in the place of the file's checked copy.
    /// </summary>
    private IConfigurationRoot Stage(IReadOnlyDictionary<string, string?> candidate)
    {
        var staged = new ConfigurationBuilder();
        foreach (var source in configuration.Sources)
            staged.Add(ReferenceEquals(source, accepted) ? new MemoryConfigurationSource { InitialData = candidate } : source);

        return staged.Build();
    }

    /// <summary>
    /// Makes <paramref name="candidate"/> the file's values in the live settings and reloads them. The check has passed
    /// them, so this does not fail for any setting it knows. Should a part of the app that it does not know still be
    /// unable to read a value, the values accepted before are put back, so that every part can read the settings again,
    /// and the reason is returned, with the file at <paramref name="path"/> named in the log. Null once the candidate is live.
    /// </summary>
    private string? Accept(IReadOnlyDictionary<string, string?> candidate, string path)
    {
        var before = accepted.Values;
        accepted.Accept(candidate);
        try
        {
            ((IConfigurationRoot)configuration).Reload();
            return null;
        }
        catch (Exception ex)
        {
            // Deliberately not rethrown: the reload already ran every part's update, so the failure is known in full here.
            // The settings that worked before are put back, which every part can read again, and the run is refused with
            // the reason, as for any other problem with the file.
            logger.LogWarning(ex, "The settings in {Path} passed the check, but a part of ClassScribe could not read them, so the settings before them stay", path);
            accepted.Accept(before);
            ((IConfigurationRoot)configuration).Reload();
            return $"A part of ClassScribe could not use these settings: {Reasons(ex)}";
        }
    }

    /// <summary>What went wrong in a reload, in the words of each part that failed.</summary>
    private static string Reasons(Exception failure) =>
        failure is AggregateException aggregate
            ? string.Join(" ", aggregate.Flatten().InnerExceptions.Select(inner => inner.Message))
            : failure.Message;

    /// <summary>The host's own JSON reader for settings, used on bytes already read, so the values are exactly those a JSON settings source holds.</summary>
    private sealed class JsonValues() : JsonStreamConfigurationProvider(new JsonStreamConfigurationSource())
    {
        /// <summary>Reads <paramref name="bytes"/> as a settings file. Throws <see cref="JsonException"/> or <see cref="FormatException"/> for text that is not one.</summary>
        public static IReadOnlyDictionary<string, string?> Read(byte[] bytes)
        {
            var reader = new JsonValues();
            using var stream = new MemoryStream(bytes, writable: false);
            reader.Load(stream);
            return new Dictionary<string, string?>(reader.Data, StringComparer.OrdinalIgnoreCase);
        }
    }
}
