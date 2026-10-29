using System.Globalization;
using System.Text.Json.Nodes;
using ClassScribe.Desktop;
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

/// <summary>
/// What the settings command answers: the exit code; what is said on standard error, refusals and warnings in order, which
/// come before anything on standard output; and, for settings path, list and get, what is shown on standard output, as
/// data, which the window writes as text or JSON (the Cli's SettingsOutput).
/// </summary>
/// <param name="ExitCode">0 done; 2 a mistake in what was asked; 1 a file that cannot be read or changed, or is missing.</param>
/// <param name="Errors">The lines for standard error, in order; empty when nothing is wrong.</param>
public sealed record SettingsAnswer(int ExitCode, IReadOnlyList<string> Errors)
{
    /// <summary>For settings path, the settings file's full path; for settings list, the path its header names. Null otherwise.</summary>
    public string? Path { get; init; }

    /// <summary>For settings list, every row; for settings get, the rows the setting matches (one, or a map's entries). Null when nothing is shown.</summary>
    public IReadOnlyList<SettingRow>? Rows { get; init; }

    /// <summary>For settings get, the setting's full name as the catalog knows it, which a map's entries are keyed under. Null otherwise.</summary>
    public string? Key { get; init; }

    /// <summary>An answer that says nothing: <paramref name="exitCode"/> alone, as set, reset, open and fill answer, whose lines the writer logs.</summary>
    public static SettingsAnswer Done(int exitCode) => new(exitCode, []);

    /// <summary>An answer that refuses with <paramref name="why"/> on standard error and <paramref name="exitCode"/>.</summary>
    public static SettingsAnswer Refused(int exitCode, string why) => new(exitCode, [why]);
}

/// <summary>
/// The settings command (layout-config 1.8, build-plan amendments 25, 38 and 51): path, list, get, set, reset, open and fill,
/// run without the settings gate or the report at the start, since it must be able to fix a file the gate refuses. Changes
/// go through the one writer (<see cref="ISettingsStore.Change"/>), which logs them; what it answers is data
/// (<see cref="SettingsAnswer"/>), which the window writes on standard output, JSON alone with --json, written with every
/// character beyond ASCII escaped so that PowerShell's ConvertFrom-Json reads it whatever the code page, and on standard
/// error. The ElevenLabs key is never listed, read or written. Exit codes: 0 done; 2 a mistake in what was asked, such as an
/// unknown setting, the key, or a value the check refuses; 1 a file that cannot be read or changed, or is missing.
/// </summary>
/// <param name="store">The settings: where they are, the file, the staged values as they would be used, and the one writer of the user's file.</param>
/// <param name="files">Whether the user's file is there, for open.</param>
/// <param name="desktop">Opens the file in Notepad.</param>
/// <param name="logger">Where a file that cannot be read is noted.</param>
public sealed class SettingsCommand(ISettingsStore store, IFileProbe files, IDesktop desktop, ILogger<SettingsCommand> logger)
{
    /// <summary>The exit code of a mistake in what was asked.</summary>
    private const int Mistake = 2;

    /// <summary>The exit code of a file that cannot be read or changed.</summary>
    private const int Failed = 1;

    /// <summary>Does what <paramref name="request"/> asks and returns what it answers, with the exit code.</summary>
    public Task<SettingsAnswer> RunAsync(SettingsRequest request) => Task.FromResult(request switch
    {
        SettingsRequest.ShowPath => new SettingsAnswer(0, []) { Path = store.SettingsPath },
        SettingsRequest.List => List(),
        SettingsRequest.Get get => Get(get.Name),
        SettingsRequest.Set set => SettingsAnswer.Done(store.Set(set.Name, [.. set.Values]).ExitCode),
        SettingsRequest.Reset { Name: { } name } => SettingsAnswer.Done(store.Reset(name).ExitCode),
        SettingsRequest.Reset => SettingsAnswer.Done(store.ResetAll().ExitCode),
        SettingsRequest.Open => Open(),
        SettingsRequest.Fill => SettingsAnswer.Done(store.Fill().ExitCode),
        _ => throw new ArgumentOutOfRangeException(nameof(request), request, "A settings request this build does not know.")
    });

    /// <summary>
    /// The rows of settings list: each setting of the catalog, a map's entries one by one, with its value in force, its
    /// default, where it comes from, and the newer default when the user's file holds an older one; read from the file the
    /// places name now, with environment variables over it. Null rows, with the reason in the lines for standard error,
    /// when the file cannot be read.
    /// </summary>
    private (IReadOnlyList<SettingRow>? Rows, IReadOnlyList<string> Errors) Rows()
    {
        var source = store.Current();
        byte[]? bytes;
        try
        {
            bytes = store.Read(source).Bytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Deliberately not rethrown: a file that cannot be read is the answer, said on standard error with exit 1.
            logger.LogDebug(ex, "Could not read {Path} to list it", source.ReadPath);
            return (null, [$"{source.ReadPath} could not be read: {ex.Message}"]);
        }

        using var staged = store.Stage(bytes);
        if (!staged.Readable) return (null, [.. staged.Unreadable, $"Nothing can be read from {source.ReadPath} until it is fixed."]);

        var older = source.Origin == SettingsOrigin.UserFolder ? DefaultChanges.HeldIn(staged.Values) : [];
        var rows = new List<SettingRow>();
        foreach (var key in SettingsCatalog.Keys)
        {
            if (key.Kind == SettingKind.Map)
            {
                var defaults = (JsonObject)key.Default!;
                var entries = defaults.Select(entry => entry.Key)
                    .Concat(staged.Children(key.Name).Select(child => child.Key))
                    .Distinct(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in entries)
                {
                    var name = $"{key.Name}:{entry}";
                    var own = defaults.FirstOrDefault(pair => pair.Key.Equals(entry, StringComparison.OrdinalIgnoreCase)).Value;
                    rows.Add(Row(name, Typed(key.EntryKind!.Value, staged[name]), own?.DeepClone(), staged, older));
                }

                continue;
            }

            JsonNode? value = key.Kind == SettingKind.List
                ? new JsonArray([.. staged.Children(key.Name).Select(child => (JsonNode?)JsonValue.Create(child.Value))])
                : Typed(key.Kind, staged[key.Name]);
            rows.Add(Row(key.Name, value, key.Default?.DeepClone(), staged, older));
        }

        return (rows, []);
    }

    /// <summary>settings list: every row (see <see cref="Rows"/>) with the file's path, after the warning while the user's file is missing; exit 1 when the file cannot be read.</summary>
    private SettingsAnswer List()
    {
        var warnings = WhileMissing();
        var (rows, errors) = Rows();
        if (rows is null) return new SettingsAnswer(Failed, [.. warnings, .. errors]);

        return new SettingsAnswer(0, warnings) { Path = store.SettingsPath, Rows = rows };
    }

    /// <summary>
    /// settings get: the rows of one setting alone (one, or a map's entries), under its name as the catalog knows it, after
    /// the warning while the user's file is missing. An unknown name and the key are mistakes, exit 2.
    /// </summary>
    private SettingsAnswer Get(string name)
    {
        if (SettingsCatalog.IsTheKey(name)) return SettingsAnswer.Refused(Mistake, SettingsCatalog.ApiKeyRefused);

        var known = SettingsCatalog.Find(name)?.Name ?? (SettingsCatalog.FindEntry(name) is { } entry ? $"{entry.Map.Name}:{entry.Entry}" : null);
        if (known is null)
            return SettingsAnswer.Refused(Mistake, SettingsCatalog.DidYouMean(name) is { } near
                ? $"{name.Trim()} is not a setting; did you mean {near}? ClassScribe settings list shows them all."
                : $"{name.Trim()} is not a setting. ClassScribe settings list shows them all.");

        var warnings = WhileMissing();
        var (rows, errors) = Rows();
        if (rows is null) return new SettingsAnswer(Failed, [.. warnings, .. errors]);

        var matching = rows.Where(row => row.Key.Equals(known, StringComparison.OrdinalIgnoreCase) || row.Key.StartsWith(known + ":", StringComparison.OrdinalIgnoreCase)).ToList();
        return new SettingsAnswer(0, warnings) { Key = known, Rows = matching };
    }

    /// <summary>
    /// settings open: the user's file in Notepad, made first as a full copy when there is none yet. Refused, exit 1, while
    /// the file is missing (a new one would hide the loss) or when there is no folder for it.
    /// </summary>
    private SettingsAnswer Open()
    {
        if (store.UserFile is not { } user) return SettingsAnswer.Refused(Failed, $"ClassScribe has no folder for your own settings on this computer; {store.ProgramFile} is the file it reads.");
        if (store.DescribeMissing() is { } missing) return SettingsAnswer.Refused(Failed, missing);

        if (store.Origin != SettingsOrigin.UserFolder || !files.FileExists(user))
        {
            var made = store.Change("make the file to open it", _ => SettingsEdit.Changed());
            if (made.ExitCode != 0) return SettingsAnswer.Done(made.ExitCode);
        }

        desktop.OpenInNotepad(user);
        return SettingsAnswer.Done(0);
    }

    /// <summary>While the user's file is missing, the line that says so, first on standard error: what is shown are the defaults.</summary>
    private IReadOnlyList<string> WhileMissing() => store.DescribeMissing() is { } missing ? [$"WARNING  {missing}"] : [];

    /// <summary>
    /// One row: the value in force, its default, where it comes from (an environment variable, the file read, or the default)
    /// and, for a value the user's file holds at an older default, the newer one.
    /// </summary>
    private SettingRow Row(string name, JsonNode? value, JsonNode? defaultValue, IStagedSettings staged, IReadOnlyList<DefaultChange> older)
    {
        var variable = store.EnvironmentOverride(name);
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

/// <summary>One row of settings list: a setting with its value in force, which the window writes as a line or as JSON (the Cli's SettingsOutput).</summary>
/// <param name="Key">The setting's full name, a map's entry by its key path.</param>
/// <param name="Value">Its value in force.</param>
/// <param name="Default">Its default in code; null for a map's entry without one.</param>
/// <param name="Source">Where the value comes from.</param>
/// <param name="Variable">The environment variable that sets it, when one does.</param>
/// <param name="NewerDefault">The change of default, when the user's file holds the older one.</param>
public sealed record SettingRow(string Key, JsonNode? Value, JsonNode? Default, SettingSource Source, string? Variable, DefaultChange? NewerDefault);
