using System.Text.Json.Nodes;

namespace ClassScribe.Commands;

/// <summary>
/// Writes what the class command answers (layout-config 4.4; architecture question 4): its refusals on standard error,
/// and for class list and class show the classes on standard output, a line each with what it records or why it cannot be
/// used, or JSON alone with --json (name, usable, problems, fields, what it records, its command, and its own glossary as
/// a full path, "" for none, null for ElevenLabs:KeytermsFile), written with every character beyond ASCII escaped so that
/// PowerShell's ConvertFrom-Json reads it whatever the code page. Returns the command's exit code.
/// </summary>
/// <param name="command">The use case.</param>
/// <param name="output">Standard output and standard error.</param>
public sealed class ClassOutput(ClassCommand command, CommandOutput output)
{
    /// <summary>Does what <paramref name="request"/> asks, writes what it answers, and returns the exit code.</summary>
    public async Task<int> RunAsync(ClassRequest request)
    {
        var answer = await command.RunAsync(request);
        foreach (var line in answer.Errors) output.Error.WriteLine(line);
        switch (request)
        {
            case ClassRequest.List list when answer.Classes is { } classes:
                WriteList(classes, answer.Path!, list.Json);
                break;
            case ClassRequest.Show show when answer.Classes is [var shown]:
                WriteShown(shown, show.Json);
                break;
        }

        return answer.ExitCode;
    }

    /// <summary>class list: a line per saved class, what it records or why it cannot be used; or JSON alone.</summary>
    private void WriteList(IReadOnlyList<ClassShown> classes, string path, bool json)
    {
        if (json)
        {
            output.Out.WriteLine(new JsonArray([.. classes.Select(shown => (JsonNode?)Json(shown))]).ToJsonString());
            return;
        }

        if (classes.Count == 0)
        {
            output.Out.WriteLine("No saved classes yet. ClassScribe class add \"<name>\" --app <process> saves one; menu choice C does too.");
            return;
        }

        output.Out.WriteLine($"Saved classes in {path}:");
        foreach (var shown in classes)
            output.Out.WriteLine(shown.Entry.Class is { } usable ? $"  {shown.Entry.Name}  {usable.Describe()}" : $"  {shown.Entry.Name}  cannot be used: {string.Join(" ", shown.Entry.Problems)}");
    }

    /// <summary>class show: the class's fields, what it records and its command, or as JSON.</summary>
    private void WriteShown(ClassShown shown, bool json)
    {
        if (json)
        {
            output.Out.WriteLine(Json(shown).ToJsonString());
            return;
        }

        var found = shown.Entry;
        output.Out.WriteLine(found.Class is { } usable ? $"{found.Name}: {usable.Describe()}" : found.CannotBeUsed);
        foreach (var (field, value) in shown.Fields) output.Out.WriteLine($"  {field} = {value}");
        if (shown.Command is { } command) output.Out.WriteLine($"From PowerShell: {command}");
    }

    /// <summary>
    /// A class as class list --json and class show --json write it: name, usable, problems, fields, what it records, its
    /// command, and its own glossary as a full path ("" for none, null for ElevenLabs:KeytermsFile).
    /// </summary>
    public static JsonObject Json(ClassShown shown) => new()
    {
        ["name"] = shown.Entry.Name,
        ["usable"] = shown.Entry.Usable,
        ["problems"] = new JsonArray([.. shown.Entry.Problems.Select(problem => (JsonNode?)problem)]),
        ["fields"] = new JsonObject(shown.Fields.Select(field => KeyValuePair.Create(field.Key, (JsonNode?)field.Value))),
        ["records"] = shown.Entry.Class?.Describe(),
        ["command"] = shown.Command,
        ["glossary"] = shown.Glossary.Value
    };
}
