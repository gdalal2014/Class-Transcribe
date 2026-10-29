using System.Text.Json.Nodes;
using ClassScribe.Settings;

namespace ClassScribe.Commands;

/// <summary>
/// Writes what the settings command answers (layout-config 1.8; architecture question 4): its refusals and warnings on
/// standard error, and on standard output the file's path alone, the rows of settings list (a line each, or JSON alone
/// with --json: key, value, default, source and newerDefault) and the value of settings get (a list's items a line each,
/// a map's entries as name=value a line each, or as JSON), written with every character beyond ASCII escaped so that
/// PowerShell's ConvertFrom-Json reads it whatever the code page. Returns the command's exit code.
/// </summary>
/// <param name="command">The use case.</param>
/// <param name="output">Standard output and standard error.</param>
public sealed class SettingsOutput(SettingsCommand command, CommandOutput output)
{
    /// <summary>Does what <paramref name="request"/> asks, writes what it answers, and returns the exit code.</summary>
    public async Task<int> RunAsync(SettingsRequest request)
    {
        var answer = await command.RunAsync(request);
        foreach (var line in answer.Errors) output.Error.WriteLine(line);
        switch (request)
        {
            case SettingsRequest.ShowPath when answer.Path is { } path:
                output.Out.WriteLine(path);
                break;
            case SettingsRequest.List list when answer.Rows is { } rows:
                WriteList(rows, answer.Path!, list.Json);
                break;
            case SettingsRequest.Get get when answer.Rows is { } matching:
                WriteValue(answer.Key!, matching, get.Json);
                break;
        }

        return answer.ExitCode;
    }

    /// <summary>settings list: a line per setting, or JSON alone.</summary>
    private void WriteList(IReadOnlyList<SettingRow> rows, string path, bool json)
    {
        if (json)
        {
            output.Out.WriteLine(new JsonArray([.. rows.Select(row => (JsonNode?)Json(row))]).ToJsonString());
            return;
        }

        output.Out.WriteLine($"Settings in {path}:");
        foreach (var row in rows) output.Out.WriteLine(Line(row));
    }

    /// <summary>
    /// settings get: the value of one setting alone (a list's items a line each, a map's entries as name=value a line each),
    /// or as JSON.
    /// </summary>
    private void WriteValue(string known, IReadOnlyList<SettingRow> matching, bool json)
    {
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
    }

    /// <summary>
    /// The row as settings list --json writes it: key, value, default, source (default, file or environment), and
    /// newerDefault, the newer default as text, or null.
    /// </summary>
    public static JsonObject Json(SettingRow row) => new()
    {
        ["key"] = row.Key,
        ["value"] = row.Value?.DeepClone(),
        ["default"] = row.Default?.DeepClone(),
        ["source"] = row.Source.ToString().ToLowerInvariant(),
        ["newerDefault"] = row.NewerDefault?.NewDefault
    };

    /// <summary>
    /// The row as settings list writes it: the setting and its value, then in brackets what differs from the default: the
    /// default, the environment variable that sets it, and a newer default the file holds an older one of.
    /// </summary>
    public static string Line(SettingRow row)
    {
        var notes = new List<string>();
        if (row.Variable is not null) notes.Add($"from the environment variable {row.Variable}");
        if (!JsonNode.DeepEquals(row.Value, row.Default)) notes.Add(row.Default is null ? "no default" : $"default {SettingsCatalog.Show(row.Default)}");
        if (row.NewerDefault is { } newer) notes.Add(DefaultChanges.Describe(newer));
        return notes.Count == 0 ? $"{row.Key} = {SettingsCatalog.Show(row.Value)}" : $"{row.Key} = {SettingsCatalog.Show(row.Value)}  ({string.Join("; ", notes)})";
    }
}
