using System.Text.Json.Nodes;

namespace ClassScribe.Settings;

/// <summary>
/// The user's settings file as a tree the settings writer changes (see <see cref="ISettingsStore.Change"/>): its values found by a
/// setting's full name, such as Recording:OutputFolder, in any case, as .NET reads the file; the saved classes under
/// Classes; and keys the writer does not know, from a newer build, kept as they are.
/// </summary>
/// <param name="root">The file's top object.</param>
public sealed class SettingsDocument(JsonObject root)
{
    /// <summary>The file's top object.</summary>
    public JsonObject Root { get; } = root;

    /// <summary>The value at <paramref name="name"/>, in any case; null when it is not there.</summary>
    public JsonNode? Get(string name)
    {
        JsonNode? node = Root;
        foreach (var part in name.Split(':'))
        {
            if (node is not JsonObject parent || Find(parent, part) is not { } found) return null;
            node = parent[found];
        }

        return node;
    }

    /// <summary>True when there is a value at <paramref name="name"/>, a null included.</summary>
    public bool Has(string name)
    {
        var parts = name.Split(':');
        return Parent(parts, create: false) is { } parent && Find(parent, parts[^1]) is not null;
    }

    /// <summary>
    /// Puts <paramref name="value"/> at <paramref name="name"/>, making the sections on the way; a value there already, in
    /// any case, is replaced under its own name.
    /// </summary>
    public void Set(string name, JsonNode? value)
    {
        var parts = name.Split(':');
        var parent = Parent(parts, create: true)!;
        parent[Find(parent, parts[^1]) ?? parts[^1]] = value;
    }

    /// <summary>Removes the value at <paramref name="name"/>, in any case; true when there was one.</summary>
    public bool Remove(string name)
    {
        var parts = name.Split(':');
        return Parent(parts, create: false) is { } parent && Find(parent, parts[^1]) is { } found && parent.Remove(found);
    }

    /// <summary>The saved classes' section, made when <paramref name="create"/> is true and it is not there; null otherwise when it is not.</summary>
    public JsonObject? Classes(bool create = false)
    {
        if (Find(Root, SettingsCatalog.ClassesSection) is { } name && Root[name] is JsonObject classes) return classes;
        if (!create) return null;

        var made = new JsonObject();
        Root[SettingsCatalog.ClassesSection] = made;
        return made;
    }

    /// <summary>The saved class named <paramref name="name"/>, in any case: its key as the file writes it, and its fields; null when there is none.</summary>
    public (string Key, JsonObject Fields)? FindClass(string name)
    {
        if (Classes() is not { } classes || Find(classes, name.Trim()) is not { } key || classes[key] is not JsonObject fields) return null;
        return (key, fields);
    }

    /// <summary>The name of the property of <paramref name="parent"/> that is <paramref name="name"/> in any case; null when there is none.</summary>
    public static string? Find(JsonObject parent, string name) =>
        parent.Select(property => property.Key).FirstOrDefault(key => key.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The object that holds the last of <paramref name="parts"/>, made on the way when <paramref name="create"/> is true; null when it is not there or is not an object.</summary>
    private JsonObject? Parent(string[] parts, bool create)
    {
        var node = Root;
        foreach (var part in parts[..^1])
        {
            if (Find(node, part) is { } found)
            {
                if (node[found] is JsonObject child)
                {
                    node = child;
                    continue;
                }

                if (!create) return null;

                var replaced = new JsonObject();
                node[found] = replaced;
                node = replaced;
                continue;
            }

            if (!create) return null;
            var made = new JsonObject();
            node[part] = made;
            node = made;
        }

        return node;
    }
}
