using System.Globalization;
using System.Reflection;
using System.Text.Json.Nodes;
using ClassScribe.Drive;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Settings;

/// <summary>What a setting holds, which decides how it is written in the file, read from the command line and shown.</summary>
public enum SettingKind
{
    /// <summary>Text, such as a path or a name.</summary>
    Text,

    /// <summary>A whole number, such as 60.</summary>
    WholeNumber,

    /// <summary>A number written with a dot, such as 1.5.</summary>
    Number,

    /// <summary>true or false.</summary>
    YesNo,

    /// <summary>One of the names of an enum, such as OnBattery.</summary>
    Choice,

    /// <summary>A list of text, written in square brackets.</summary>
    List,

    /// <summary>Entries by name, each set and reset on its own by its key path (build-plan amendment 25), such as Logging:LogLevel:Default.</summary>
    Map
}

/// <summary>One setting of the six sections the user's file holds (see <see cref="SettingsCatalog"/>).</summary>
/// <param name="Name">Its full name, such as Recording:OutputFolder.</param>
/// <param name="Kind">What it holds.</param>
/// <param name="Default">Its default in code, as the file writes it: a JSON value, a list, or for a map its default entries.</param>
/// <param name="ChoiceType">For a choice, or a map whose entries are choices, the enum whose names it takes; null otherwise.</param>
/// <param name="EntryKind">For a map, what each entry holds; null otherwise.</param>
public sealed record SettingKey(string Name, SettingKind Kind, JsonNode? Default, Type? ChoiceType = null, SettingKind? EntryKind = null)
{
    /// <summary>The section it is in, such as Recording.</summary>
    public string Section => Name[..Name.IndexOf(':')];

    /// <summary>Its path below the section, such as OutputFolder or Console:FormatterName.</summary>
    public string Path => Name[(Name.IndexOf(':') + 1)..];
}

/// <summary>
/// Every setting ClassScribe's user file holds, reflected from the options' types in their order, section by section
/// (Recording, ElevenLabs, Transcript, Drive, YouTube) and then the Logging section of <see cref="DefaultSettings"/>; never
/// ElevenLabs:ApiKey, which only ELEVENLABS_API_KEY holds, and never a property without a setter, such as
/// ElevenLabs:Timeout. The settings writer adds, sets and resets only these, and the saved classes under Classes; settings
/// list and get show them. A property that is a dictionary of whole numbers, such as Recording:CameraOffsets, is a map, as
/// the Logging section's log levels are, whose entries are set one by one (amendment 25).
/// </summary>
public static class SettingsCatalog
{
    /// <summary>The section that holds the saved classes, keyed by each class's name.</summary>
    public const string ClassesSection = "Classes";

    /// <summary>The log's section.</summary>
    public const string LoggingSection = "Logging";

    /// <summary>The six sections, in the order the user's file holds them; Classes comes after them.</summary>
    public static IReadOnlyList<string> Sections { get; } =
        [RecordingOptions.SectionName, ElevenLabsOptions.SectionName, TranscriptOptions.SectionName, DriveOptions.SectionName, YouTubeOptions.SectionName, LoggingSection];

    /// <summary>Every setting, in the file's order.</summary>
    public static IReadOnlyList<SettingKey> Keys { get; } = Build();

    /// <summary>The setting named <paramref name="name"/>, in any case; null when there is none.</summary>
    public static SettingKey? Find(string name) => Keys.FirstOrDefault(key => key.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The map <paramref name="name"/> is an entry of, and the entry's name, such as Logging:LogLevel and ClassScribe.Drive
    /// for Logging:LogLevel:ClassScribe.Drive; null when it names no map's entry.
    /// </summary>
    public static (SettingKey Map, string Entry)? FindEntry(string name)
    {
        var trimmed = name.Trim();
        foreach (var map in Keys.Where(key => key.Kind == SettingKind.Map))
        {
            var prefix = map.Name + ":";
            if (trimmed.Length > prefix.Length && trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !trimmed[prefix.Length..].Contains(':'))
                return (map, trimmed[prefix.Length..]);
        }

        return null;
    }

    /// <summary>
    /// The setting <paramref name="name"/> most likely meant, for "did you mean": one whose name is at most three letters
    /// away, in any case, or whose last part is the same; null when none is that near.
    /// </summary>
    public static string? DidYouMean(string name)
    {
        var given = name.Trim();
        var lastPart = given[(given.LastIndexOf(':') + 1)..];
        return Keys
            .Select(key => (key.Name, Distance: Distance(key.Name.ToLowerInvariant(), given.ToLowerInvariant())))
            .Where(candidate => candidate.Distance <= 3 || candidate.Name[(candidate.Name.LastIndexOf(':') + 1)..].Equals(lastPart, StringComparison.OrdinalIgnoreCase))
            .OrderBy(candidate => candidate.Distance)
            .Select(candidate => candidate.Name)
            .FirstOrDefault();
    }

    /// <summary>
    /// <paramref name="text"/> as the JSON value a setting of <paramref name="kind"/> holds, or the rule it breaks, in words
    /// after the setting's name: an enum's names in any case become its own name, numbers are read with a dot.
    /// </summary>
    public static (JsonNode? Value, string? Problem) Read(SettingKind kind, Type? choiceType, string text)
    {
        var trimmed = text.Trim();
        switch (kind)
        {
            case SettingKind.WholeNumber:
                return int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole)
                    ? (JsonValue.Create(whole), null) : (null, "takes a whole number, such as 60");
            case SettingKind.Number:
                return double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)
                    ? (JsonValue.Create(number), null) : (null, "takes a number written with a dot, such as 1.5");
            case SettingKind.YesNo:
                return bool.TryParse(trimmed, out var yes) ? (JsonValue.Create(yes), null) : (null, "takes true or false");
            case SettingKind.Choice:
                var names = Enum.GetNames(choiceType!);
                return names.FirstOrDefault(choice => choice.Equals(trimmed, StringComparison.OrdinalIgnoreCase)) is { } named
                    ? (JsonValue.Create(named), null) : (null, $"takes {Listed(names)}");
            default:
                return (JsonValue.Create(text), null);
        }
    }

    /// <summary>A setting's value as settings get and list show it: text as it is, a list's items joined with commas, a map's entries as name=value.</summary>
    public static string Show(JsonNode? value) => value switch
    {
        null => "",
        JsonArray list => string.Join(", ", list.Select(Show)),
        JsonObject map => string.Join(", ", map.Select(entry => $"{entry.Key}={Show(entry.Value)}")),
        JsonValue scalar when scalar.TryGetValue<string>(out var text) => text,
        JsonValue scalar when scalar.TryGetValue<bool>(out var yes) => yes ? "true" : "false",
        _ => value.ToJsonString()
    };

    /// <summary>
    /// An entry of the map <paramref name="map"/> as settings set takes it, for the words that refuse a whole map: a log
    /// level for the Logging section's levels, an offset by its camera's name part for a map of whole numbers.
    /// </summary>
    public static string EntryExample(SettingKey map) =>
        map.EntryKind == SettingKind.WholeNumber ? $"{map.Name}:OBSBOT -60" : $"{map.Name}:Default Information";

    /// <summary>The names of an enum, in words: "OnBattery, Always or Never".</summary>
    public static string Listed(IReadOnlyList<string> names) => names.Count == 1 ? names[0] : $"{string.Join(", ", names.Take(names.Count - 1))} or {names[^1]}";

    /// <summary>Reflects the options' types into the settings, in their order, and adds the Logging section's.</summary>
    private static IReadOnlyList<SettingKey> Build()
    {
        var keys = new List<SettingKey>();
        keys.AddRange(From<RecordingOptions>(RecordingOptions.SectionName));
        keys.AddRange(From<ElevenLabsOptions>(ElevenLabsOptions.SectionName).Where(key => !key.Name.Equals(ElevenLabsOptions.ApiKeySetting, StringComparison.OrdinalIgnoreCase)));
        keys.AddRange(From<TranscriptOptions>(TranscriptOptions.SectionName));
        keys.AddRange(From<DriveOptions>(DriveOptions.SectionName));
        keys.AddRange(From<YouTubeOptions>(YouTubeOptions.SectionName));

        var levels = new JsonObject();
        foreach (var (name, value) in DefaultSettings.Values.Where(pair => pair.Key.StartsWith($"{LoggingSection}:LogLevel:", StringComparison.OrdinalIgnoreCase)))
            levels[name[$"{LoggingSection}:LogLevel:".Length..]] = value;
        keys.Add(new SettingKey($"{LoggingSection}:LogLevel", SettingKind.Map, levels, typeof(LogLevel), SettingKind.Choice));
        foreach (var (name, value) in DefaultSettings.Values.Where(pair => !pair.Key.StartsWith($"{LoggingSection}:LogLevel:", StringComparison.OrdinalIgnoreCase)))
            keys.Add(new SettingKey(name, SettingKind.Text, JsonValue.Create(value)));

        return keys;
    }

    /// <summary>The settings of <typeparamref name="TOptions"/>, in the order its properties are declared, with the defaults of a fresh one.</summary>
    private static IEnumerable<SettingKey> From<TOptions>(string section) where TOptions : new()
    {
        var fresh = new TOptions();
        foreach (var property in typeof(TOptions).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(property => property.SetMethod is not null).OrderBy(property => property.MetadataToken))
        {
            var type = property.PropertyType;
            var value = property.GetValue(fresh);
            var name = $"{section}:{property.Name}";
            if (type == typeof(int)) yield return new SettingKey(name, SettingKind.WholeNumber, JsonValue.Create((int)value!));
            else if (type == typeof(double)) yield return new SettingKey(name, SettingKind.Number, JsonValue.Create((double)value!));
            else if (type == typeof(bool)) yield return new SettingKey(name, SettingKind.YesNo, JsonValue.Create((bool)value!));
            else if (type.IsEnum) yield return new SettingKey(name, SettingKind.Choice, JsonValue.Create(value!.ToString()), type);
            else if (type == typeof(string[])) yield return new SettingKey(name, SettingKind.List, new JsonArray([.. ((string[])value!).Select(item => (JsonNode?)JsonValue.Create(item))]));
            else if (type == typeof(string)) yield return new SettingKey(name, SettingKind.Text, JsonValue.Create((string?)value ?? ""));
            else if (type == typeof(Dictionary<string, int>))
                yield return new SettingKey(name, SettingKind.Map, new JsonObject(((Dictionary<string, int>)value!).Select(entry => KeyValuePair.Create(entry.Key, (JsonNode?)JsonValue.Create(entry.Value)))),
                    EntryKind: SettingKind.WholeNumber);
            else throw new InvalidOperationException($"{name} is a {type.Name}, which the settings writer does not know how to write.");
        }
    }

    /// <summary>How many letters must change to turn <paramref name="first"/> into <paramref name="second"/> (the Levenshtein distance).</summary>
    private static int Distance(string first, string second)
    {
        var previous = Enumerable.Range(0, second.Length + 1).ToArray();
        for (var i = 1; i <= first.Length; i++)
        {
            var current = new int[second.Length + 1];
            current[0] = i;
            for (var j = 1; j <= second.Length; j++)
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (first[i - 1] == second[j - 1] ? 0 : 1));
            previous = current;
        }

        return previous[second.Length];
    }
}
