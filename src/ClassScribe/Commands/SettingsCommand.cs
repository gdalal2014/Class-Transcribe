using System.Globalization;
using System.Text.Json.Nodes;
using ClassScribe.Settings;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Commands;

/// <summary>What the settings command is asked to do (layout-config 1.8).</summary>
public abstract record SettingsRequest
{
    /// <summary>Only the kinds declared here derive from it.</summary>
    private SettingsRequest()
    {
    }

    /// <summary>settings path: the file's full path, alone on the line.</summary>
    public sealed record ShowPath : SettingsRequest;

    /// <summary>settings list: every setting, its value, its default and where it comes from; as JSON with <paramref name="Json"/>.</summary>
    /// <param name="Json">True for JSON alone on standard output.</param>
    public sealed record List(bool Json) : SettingsRequest;

    /// <summary>settings get: the value of <paramref name="Name"/> alone; as JSON with <paramref name="Json"/>.</summary>
    /// <param name="Name">The setting, such as Recording:OutputFolder, or a map's entry.</param>
    /// <param name="Json">True for JSON alone on standard output.</param>
    public sealed record Get(string Name, bool Json) : SettingsRequest;

    /// <summary>settings set: <paramref name="Name"/> to <paramref name="Values"/>, a list's items one by one.</summary>
    /// <param name="Name">The setting.</param>
    /// <param name="Values">Its value, or a list's items.</param>
    public sealed record Set(string Name, IReadOnlyList<string> Values) : SettingsRequest;

    /// <summary>settings reset: <paramref name="Name"/> back at its default; null for --all, a fresh copy of every setting.</summary>
    /// <param name="Name">The setting, or null for every one.</param>
    public sealed record Reset(string? Name) : SettingsRequest;

    /// <summary>settings open: the user's file in Notepad, made first if needed.</summary>
    public sealed record Open : SettingsRequest;

    /// <summary>settings fill: every setting the user's file lacks added at its default; never makes a file.</summary>
    public sealed record Fill : SettingsRequest;
}

/// <summary>Where a command of its own kind, settings or class, writes what it answers: standard output and standard error.</summary>
/// <param name="Out">Standard output: the answer itself, such as a path, a value or JSON.</param>
/// <param name="Error">Standard error: warnings and refusals that are no part of the answer.</param>
public sealed record CommandOutput(TextWriter Out, TextWriter Error);

/// <summary>
/// The settings command (layout-config 1.8, build-plan amendments 25, 38 and 51): path, list, get, set, reset, open and fill,
/// run without the settings gate or the report at the start, since it must be able to fix a file the gate refuses. Changes
/// go through the one writer (<see cref="SettingsWriter"/>), which logs them; answers go to standard output, JSON alone with
/// --json, written with every character beyond ASCII escaped so that PowerShell's ConvertFrom-Json reads it whatever the code
/// page. The ElevenLabs key is never listed, read or written. Exit codes: 0 done; 2 a mistake in what was asked, such as an
/// unknown setting, the key, or a value the check refuses; 1 a file that cannot be read or changed, or is missing.
/// </summary>
/// <param name="writer">The one writer of the user's file.</param>
/// <param name="places">Where the settings are.</param>
/// <param name="file">The settings file, read for list and get.</param>
/// <param name="reloader">Stages the file's values as they would be used, for list and get.</param>
/// <param name="desktop">Opens the file in Notepad.</param>
/// <param name="output">Standard output and standard error.</param>
/// <param name="logger">Where a file that cannot be read is noted.</param>
public sealed class SettingsCommand(SettingsWriter writer, SettingsPlaces places, SettingsFile file, SettingsReloader reloader, IDesktop desktop, CommandOutput output,
    ILogger<SettingsCommand> logger)
{
    /// <summary>The exit code of a mistake in what was asked.</summary>
    private const int Mistake = 2;

    /// <summary>The exit code of a file that cannot be read or changed.</summary>
    private const int Failed = 1;

    /// <summary>Does what <paramref name="request"/> asks and returns the exit code.</summary>
    public Task<int> RunAsync(SettingsRequest request) => Task.FromResult(request switch
    {
        SettingsRequest.ShowPath => ShowPath(),
        SettingsRequest.List list => List(list.Json),
        SettingsRequest.Get get => Get(get.Name, get.Json),
        SettingsRequest.Set set => writer.Set(set.Name, [.. set.Values]).ExitCode,
        SettingsRequest.Reset { Name: { } name } => writer.Reset(name).ExitCode,
        SettingsRequest.Reset => writer.ResetAll().ExitCode,
        SettingsRequest.Open => Open(),
        SettingsRequest.Fill => writer.Fill().ExitCode,
        _ => throw new ArgumentOutOfRangeException(nameof(request), request, "A settings request this build does not know.")
    });

    /// <summary>
    /// The rows of settings list: each setting of the catalog, a map's entries one by one, with its value in force, its
    /// default, where it comes from, and the newer default when the user's file holds an older one; read from the file the
    /// places name now, with environment variables over it. Null, with the reason written to standard error, when the file
    /// cannot be read.
    /// </summary>
    public IReadOnlyList<SettingRow>? Rows()
    {
        var source = places.Current();
        byte[]? bytes;
        try
        {
            bytes = file.Read(source).Bytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Deliberately not rethrown: a file that cannot be read is the answer, said on standard error with exit 1.
            logger.LogDebug(ex, "Could not read {Path} to list it", source.ReadPath);
            output.Error.WriteLine($"{source.ReadPath} could not be read: {ex.Message}");
            return null;
        }

        using var staged = reloader.Stage(bytes);
        if (staged.Configuration is not { } settings)
        {
            foreach (var line in staged.Unreadable) output.Error.WriteLine(line);
            output.Error.WriteLine($"Nothing can be read from {source.ReadPath} until it is fixed.");
            return null;
        }

        var older = source.Origin == SettingsOrigin.UserFolder ? DefaultChanges.HeldIn(staged.Values) : [];
        var rows = new List<SettingRow>();
        foreach (var key in SettingsCatalog.Keys)
        {
            if (key.Kind == SettingKind.Map)
            {
                var defaults = (JsonObject)key.Default!;
                var entries = defaults.Select(entry => entry.Key)
                    .Concat(settings.GetSection(key.Name).GetChildren().Select(child => child.Key))
                    .Distinct(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in entries)
                {
                    var name = $"{key.Name}:{entry}";
                    var own = defaults.FirstOrDefault(pair => pair.Key.Equals(entry, StringComparison.OrdinalIgnoreCase)).Value;
                    rows.Add(Row(name, Typed(key.EntryKind!.Value, settings[name]), own?.DeepClone(), staged, older));
                }

                continue;
            }

            JsonNode? value = key.Kind == SettingKind.List
                ? new JsonArray([.. settings.GetSection(key.Name).GetChildren().Select(child => (JsonNode?)JsonValue.Create(child.Value))])
                : Typed(key.Kind, settings[key.Name]);
            rows.Add(Row(key.Name, value, key.Default?.DeepClone(), staged, older));
        }

        return rows;
    }

    /// <summary>settings path: the settings file's full path, alone on the line.</summary>
    private int ShowPath()
    {
        output.Out.WriteLine(places.SettingsPath);
        return 0;
    }

    /// <summary>settings list: a line per setting, or JSON alone (see <see cref="Rows"/>).</summary>
    private int List(bool json)
    {
        WarnWhileMissing();
        if (Rows() is not { } rows) return Failed;

        if (json)
        {
            output.Out.WriteLine(new JsonArray([.. rows.Select(row => (JsonNode?)row.ToJson())]).ToJsonString());
            return 0;
        }

        output.Out.WriteLine($"Settings in {places.SettingsPath}:");
        foreach (var row in rows) output.Out.WriteLine(row.Line());
        return 0;
    }

    /// <summary>
    /// settings get: the value of one setting alone (a list's items a line each, a map's entries as name=value a line each),
    /// or as JSON. An unknown name and the key are mistakes, exit 2.
    /// </summary>
    private int Get(string name, bool json)
    {
        if (SettingsCatalog.IsTheKey(name)) return Refuse(Mistake, SettingsCatalog.ApiKeyRefused);

        var known = SettingsCatalog.Find(name)?.Name ?? (SettingsCatalog.FindEntry(name) is { } entry ? $"{entry.Map.Name}:{entry.Entry}" : null);
        if (known is null)
            return Refuse(Mistake, SettingsCatalog.DidYouMean(name) is { } near
                ? $"{name.Trim()} is not a setting; did you mean {near}? ClassScribe settings list shows them all."
                : $"{name.Trim()} is not a setting. ClassScribe settings list shows them all.");

        WarnWhileMissing();
        if (Rows() is not { } rows) return Failed;

        var matching = rows.Where(row => row.Key.Equals(known, StringComparison.OrdinalIgnoreCase) || row.Key.StartsWith(known + ":", StringComparison.OrdinalIgnoreCase)).ToList();
        IEnumerable<string> lines;
        if (SettingsCatalog.Find(known) is { Kind: SettingKind.Map })
        {
            var entries = matching.Select(row => (Entry: row.Key[(known.Length + 1)..], row.Value)).ToList();
            lines = json
                ? [new JsonObject(entries.Select(entry => KeyValuePair.Create(entry.Entry, entry.Value?.DeepClone()))).ToJsonString()]
                : entries.Select(entry => $"{entry.Entry}={SettingsCatalog.Show(entry.Value)}");
        }
        else
        {
            var value = matching.FirstOrDefault()?.Value;
            lines = json ? [value?.ToJsonString() ?? "null"]
                : value is JsonArray list ? list.Select(SettingsCatalog.Show)
                : [SettingsCatalog.Show(value)];
        }

        foreach (var line in lines) output.Out.WriteLine(line);
        return 0;
    }

    /// <summary>
    /// settings open: the user's file in Notepad, made first as a full copy when there is none yet. Refused, exit 1, while
    /// the file is missing (a new one would hide the loss) or when there is no folder for it.
    /// </summary>
    private int Open()
    {
        if (places.UserFile is not { } user) return Refuse(Failed, $"ClassScribe has no folder for your own settings on this computer; {places.ProgramFile} is the file it reads.");
        if (places.DescribeMissing() is { } missing) return Refuse(Failed, missing);

        if (places.Origin != SettingsOrigin.UserFolder || !File.Exists(user))
        {
            var made = writer.Change("make the file to open it", _ => SettingsEdit.Changed());
            if (made.ExitCode != 0) return made.ExitCode;
        }

        desktop.OpenInNotepad(user);
        return 0;
    }

    /// <summary>While the user's file is missing, says so on standard error first: what is shown are the defaults.</summary>
    private void WarnWhileMissing()
    {
        if (places.DescribeMissing() is { } missing) output.Error.WriteLine($"WARNING  {missing}");
    }

    /// <summary>Writes <paramref name="why"/> to standard error and returns <paramref name="exitCode"/>.</summary>
    private int Refuse(int exitCode, string why)
    {
        output.Error.WriteLine(why);
        return exitCode;
    }

    /// <summary>
    /// One row: the value in force, its default, where it comes from (an environment variable, the file read, or the default)
    /// and, for a value the user's file holds at an older default, the newer one.
    /// </summary>
    private static SettingRow Row(string name, JsonNode? value, JsonNode? defaultValue, StagedSettings staged, IReadOnlyList<DefaultChange> older)
    {
        var variable = SettingsWriter.EnvironmentOverride(name);
        var inFile = staged.Values.Keys.Any(key => key.Equals(name, StringComparison.OrdinalIgnoreCase) || key.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase));
        var source = variable is not null ? SettingSource.Environment : inFile ? SettingSource.File : SettingSource.Default;
        var newer = older.FirstOrDefault(change => change.Key.Equals(name, StringComparison.OrdinalIgnoreCase));
        return new SettingRow(name, value, defaultValue, source, variable, newer);
    }

    /// <summary>A setting's text, as the settings hold it, as the JSON value its kind writes: a number, true or false, or text; null for none.</summary>
    private static JsonNode? Typed(SettingKind kind, string? text) => text is null ? null : kind switch
    {
        SettingKind.WholeNumber when int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole) => JsonValue.Create(whole),
        SettingKind.Number when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) => JsonValue.Create(number),
        SettingKind.YesNo when bool.TryParse(text, out var yes) => JsonValue.Create(yes),
        _ => JsonValue.Create(text)
    };
}

/// <summary>Where a setting's value in force comes from.</summary>
public enum SettingSource
{
    /// <summary>The default in code: the file read does not hold it.</summary>
    Default,

    /// <summary>The settings file read.</summary>
    File,

    /// <summary>An environment variable, such as Recording__OutputFolder, which wins over the file.</summary>
    Environment
}

/// <summary>One line of settings list.</summary>
/// <param name="Key">The setting's full name, a map's entry by its key path.</param>
/// <param name="Value">Its value in force.</param>
/// <param name="Default">Its default in code; null for a map's entry without one.</param>
/// <param name="Source">Where the value comes from.</param>
/// <param name="Variable">The environment variable that sets it, when one does.</param>
/// <param name="NewerDefault">The change of default, when the user's file holds the older one.</param>
public sealed record SettingRow(string Key, JsonNode? Value, JsonNode? Default, SettingSource Source, string? Variable, DefaultChange? NewerDefault)
{
    /// <summary>
    /// The row as settings list --json writes it: key, value, default, source (default, file or environment), and
    /// newerDefault, the newer default as text, or null.
    /// </summary>
    public JsonObject ToJson() => new()
    {
        ["key"] = Key,
        ["value"] = Value?.DeepClone(),
        ["default"] = Default?.DeepClone(),
        ["source"] = Source.ToString().ToLowerInvariant(),
        ["newerDefault"] = NewerDefault?.NewDefault
    };

    /// <summary>
    /// The row as settings list writes it: the setting and its value, then in brackets what differs from the default: the
    /// default, the environment variable that sets it, and a newer default the file holds an older one of.
    /// </summary>
    public string Line()
    {
        var notes = new List<string>();
        if (Variable is not null) notes.Add($"from the environment variable {Variable}");
        if (!JsonNode.DeepEquals(Value, Default)) notes.Add(Default is null ? "no default" : $"default {SettingsCatalog.Show(Default)}");
        if (NewerDefault is { } newer) notes.Add(DefaultChanges.Describe(newer));
        return notes.Count == 0 ? $"{Key} = {SettingsCatalog.Show(Value)}" : $"{Key} = {SettingsCatalog.Show(Value)}  ({string.Join("; ", notes)})";
    }
}
