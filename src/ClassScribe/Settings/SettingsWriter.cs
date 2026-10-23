using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Settings;

/// <summary>What an edit of the settings file decided: the lines to say, and whether it refuses, with which exit code.</summary>
/// <param name="Lines">What is said: what changed, or why nothing was.</param>
/// <param name="ExitCode">0 to write the change; 2 for a mistake in what was asked, such as an unknown class; 1 for anything else that refuses it.</param>
public sealed record SettingsEdit(IReadOnlyList<string> Lines, int ExitCode = 0)
{
    /// <summary>The settings the edit changed, by their full names, so that one an environment variable overrides is pointed out.</summary>
    public IReadOnlyList<string> Keys { get; init; } = [];

    /// <summary>The saved class the edit makes or changes, by its name after the edit; null when it changes none.</summary>
    public string? ClassName { get; init; }

    /// <summary>The edit made its change, said in <paramref name="lines"/>.</summary>
    public static SettingsEdit Changed(params string[] lines) => new(lines);

    /// <summary>The edit refuses, for <paramref name="why"/>, with exit code <paramref name="exitCode"/>: nothing is written.</summary>
    public static SettingsEdit Refused(string why, int exitCode = 2) => new([why], exitCode);
}

/// <summary>What a change of the settings file came to: its exit code, and what was said.</summary>
/// <param name="ExitCode">0 when the file holds the change (or held it already); 2 for a mistake in what was asked or a value the check refuses; 1 for a file that cannot be changed.</param>
/// <param name="Lines">What was said, in the order it was said.</param>
public sealed record SettingsChange(int ExitCode, IReadOnlyList<string> Lines);

/// <summary>
/// The one writer of the user's settings file (layout-config 1.6, with build-plan amendments 25 and 38): the menu's S and C,
/// settings and class all go through it, since .NET's configuration is read-only by design. One change at a time under
/// settings.lock (waiting up to 2 seconds); the file read, with a SHA-256 of its bytes; a file that is not valid JSON, or that
/// holds comments of its own beside ClassScribe's header, refused rather than lost; the change made to the parsed tree, so
/// that keys a newer build wrote stay as they are; the result checked with the reloader's own check before anything is
/// written, a change that adds a problem refused in the check's words while problems already there do not block it; the
/// file read again and compared, starting again up to three times when another program saved it meanwhile; then written
/// whole (ClassScribe's header, the values indented, UTF-8 without a byte-order mark, letters such as ṣ kept readable) to a
/// temporary file moved into its place, the bytes before kept as appsettings.previous.json, one undo (without an ElevenLabs
/// key a hand edit put there, which is never written to any file); the settings reloaded,
/// and the change logged. The file is a full copy of every setting (amendment 38): made at the first change from the file
/// read until then, with every setting this build knows; and at every later change any setting the file lacks is added at
/// its default, which is said with the running build's version. ElevenLabs:ApiKey is never written.
/// </summary>
public sealed class SettingsWriter(SettingsPlaces places, SettingsFile file, SettingsReloader reloader, ILogger<SettingsWriter> logger)
{
    /// <summary>
    /// ClassScribe's header comment, written before the opening brace: what the file is, that a later build adds its new
    /// settings and says when a default held has changed, and the three ways to change it.
    /// </summary>
    public static readonly IReadOnlyList<string> Header =
    [
        "// ClassScribe's settings for this Windows user: every setting, at the value it had when ClassScribe last wrote this",
        "// file. A later ClassScribe adds its new settings here at their defaults and tells you when a default you hold has",
        "// changed (\"settings list\" shows it). Change them in the menu (S and C), with \"ClassScribe settings set\", or here."
    ];

    /// <summary>What a file with comments of its own is told: a change from ClassScribe would lose them.</summary>
    public const string CommentsOfItsOwn = "Your settings file has comments of your own, which a change from ClassScribe would lose. Change it in Notepad (ClassScribe settings open), or remove the comments.";

    /// <summary>What a change of the ElevenLabs key is told: the ground rule, in words, and how to set the key where it lives.</summary>
    public const string ApiKeyRefused =
        "The ElevenLabs key is never kept in the settings: ClassScribe reads it from the ELEVENLABS_API_KEY environment variable alone. Set it there, for your Windows user, in PowerShell: [Environment]::SetEnvironmentVariable('ELEVENLABS_API_KEY', '<your key>', 'User'), then open a new window.";

    /// <summary>What a change says when it took out an ElevenLabs key a hand edit put in the user's file: from the file and from the copy before.</summary>
    public const string ApiKeyTakenOut =
        $"Your settings file held an ElevenLabs key, which ClassScribe took out of it and out of {SettingsPlaces.PreviousFileName}: it reads the key from the {ElevenLabsOptions.ApiKeyVariable} environment variable alone.";

    /// <summary>What settings reset --all says when the file it replaces is not valid JSON and holds an ElevenLabs key: no copy of it is kept.</summary>
    public const string ApiKeyNotKept =
        $"The file as it was is not kept in {SettingsPlaces.PreviousFileName}, since it holds an ElevenLabs key, which ClassScribe never writes to a file: it reads the key from the {ElevenLabsOptions.ApiKeyVariable} environment variable alone.";

    /// <summary>What settings fill says when there is no user file yet: nothing is made, since a file is made only at the first change.</summary>
    public const string NothingToFill = "No settings file yet; nothing to fill.";

    /// <summary>
    /// The headers ClassScribe has written, the one it writes now first. The comments before the opening brace are
    /// ClassScribe's own only when they are the lines of one of these, in order, each a line comment as written (a line may
    /// be taken out). A later build that rewords the header adds the old wording here, so that the files earlier builds
    /// wrote are still told from the person's own comments.
    /// </summary>
    private static readonly IReadOnlyList<IReadOnlyList<string>> KnownHeaders = [Header];

    /// <summary>How many times a change starts again when another program saved the file meanwhile.</summary>
    private const int Tries = 3;

    /// <summary>How the file is written: indented, with letters such as ṣ and ā as they are rather than escaped.</summary>
    private static readonly JsonSerializerOptions Written = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>How the file is read: comments passed over and trailing commas allowed, as .NET's settings reader does.</summary>
    private static readonly JsonDocumentOptions Reading = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>How long a change waits for another change to let go of the lock; a test shortens it.</summary>
    internal TimeSpan LockPatience { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Called between the check and the second read of the file, so that a test can save the file there, as another program would.</summary>
    internal Action? BeforeSecondRead { get; set; }

    /// <summary>The file the writer changes: the user's own, made at the first change; the file beside the program when there is no user folder.</summary>
    public string FilePath => places.UserFile ?? places.ProgramFile;

    /// <summary>
    /// Changes the user's file by <paramref name="edit"/>, described as <paramref name="what"/> in the log, as the class
    /// says. <paramref name="freshCopy"/> starts from the defaults instead of the file, for settings reset --all, which alone
    /// may make the file while it is missing or not valid JSON; the saved classes are kept even then when they can be read.
    /// Returns what came of it.
    /// </summary>
    public SettingsChange Change(string what, Func<SettingsDocument, SettingsEdit> edit, bool freshCopy = false)
    {
        if (places.UserFile is not { } target)
            return Refuse(1, $"ClassScribe has no folder for your own settings on this computer (Windows did not say where %APPDATA% is), so it cannot change them; edit {places.ProgramFile} instead.");
        if (!freshCopy && places.DescribeMissing() is { } missing) return Refuse(1, missing);

        using var held = TakeLock();
        if (held is null) return Refuse(1, "Another ClassScribe is changing your settings just now; try again in a moment.");

        for (var attempt = 1; attempt <= Tries; attempt++)
        {
            var source = places.Current();
            var before = ReadBytes(source);
            if (before.Failure is { } unreadable) return Refuse(1, unreadable);

            var firstChange = source.Origin != SettingsOrigin.UserFolder;
            using var stagedBefore = reloader.Stage(before.Bytes);
            if (!freshCopy && !stagedBefore.Readable)
                return Refuse(1, [.. stagedBefore.Unreadable, $"Nothing was changed. Fix {source.ReadPath} first, or start again from the defaults with: ClassScribe settings reset --all."]);
            if (!freshCopy && !firstChange && before.Bytes is { } own && HasCommentsOfItsOwn(own)) return Refuse(1, CommentsOfItsOwn);

            var root = freshCopy ? FreshCopy(before.Bytes) : Parse(before.Bytes);
            var added = AddMissing(root);
            RemoveTheKey(root);
            var outcome = edit(new SettingsDocument(root));
            if (outcome.ExitCode != 0) return Refuse(outcome.ExitCode, [.. outcome.Lines]);

            var ordered = Order(root);
            var after = Serialize(ordered);
            using var stagedAfter = reloader.Stage(after);
            if (NewProblems(stagedBefore, stagedAfter, freshCopy) is [_, ..] problems)
                return Refuse(2, [.. problems.Select(problem => problem.Text), "Nothing was changed."]);

            // A saved class the change makes or changes is held to the same rule as the file: a problem the change adds to it
            // refuses the change, in the class check's words, while one it had already does not block it (layout-config 4.2).
            if (outcome.ClassName is { } className
                && stagedAfter.ProblemsOf(className).Except(stagedBefore.ProblemsOf(className), StringComparer.Ordinal).ToList() is [_, ..] classProblems)
                return Refuse(2, [$"The saved class \"{className}\" would not be usable: {string.Join(" ", classProblems)}", "Nothing was changed."]);

            BeforeSecondRead?.Invoke();
            if (!Unchanged(target, firstChange, before.Hash))
            {
                logger.LogDebug("{Path} was saved by another program while ClassScribe changed it ({What}); starting again ({Attempt} of {Tries})", target, what, attempt, Tries);
                continue;
            }

            // A file that already holds the change, every setting included, is left as it is.
            if (!firstChange && before.Bytes is { } current && current.AsSpan().SequenceEqual(after)) return Done(outcome.Lines);

            var lines = new List<string>();
            var kept = firstChange ? null : KeptBefore(before.Bytes);
            Write(target, kept?.Bytes, after, firstChange);
            if (firstChange)
            {
                places.MarkUserFileMade();
                lines.Add($"Your settings are now kept in {target}; an update of ClassScribe never changes that file.");
            }
            else if (added.Count > 0)
            {
                lines.AddRange(AddedLines(added));
            }

            lines.AddRange(outcome.Lines);
            if (kept is { KeyTakenOut: true }) lines.Add(ApiKeyTakenOut);
            if (freshCopy && kept is not null) lines.Add(kept.Bytes is null ? ApiKeyNotKept : $"The file as it was is in {SettingsPlaces.PreviousFileName} beside it.");
            lines.AddRange(Overridden(outcome));
            var reloaded = reloader.TryReload();
            if (!reloaded.Usable)
                lines.Add($"The change is saved, but the file still has problems that keep it from being used: {string.Join(" ", reloaded.Problems)}");
            logger.LogDebug("Changed the settings in {Path}: {What}", target, what);
            return Done(lines);
        }

        return Refuse(1, $"Your settings file {target} keeps changing while ClassScribe changes it, as if another program saved it again and again. Nothing was changed; close the other program, then try again.");
    }

    /// <summary>
    /// settings set: sets <paramref name="name"/> to <paramref name="values"/>, written as the setting's kind (a number,
    /// true or false, an enum's own name, text, or a list's items), even when that is the default, since the file is a full
    /// copy. An entry of a map is set by its key path, such as Logging:LogLevel:ClassScribe.Drive (amendment 25). Refused
    /// with exit 2: ElevenLabs:ApiKey, an unknown name (with the nearest known one), a value of the wrong kind, and a value the
    /// check refuses, in its words.
    /// </summary>
    public SettingsChange Set(string name, params string[] values) => SetAll([(name, values)]);

    /// <summary>
    /// Sets several settings in one change, as the menu's S does after a pick in choice 6 or 7: each as <see cref="Set"/>
    /// sets it, all written together or none.
    /// </summary>
    public SettingsChange SetAll(IReadOnlyList<(string Name, string[] Values)> settings)
    {
        var changes = new List<(string Name, JsonNode? Value)>();
        foreach (var (name, values) in settings)
        {
            if (Target(name, values) is not { } target) continue;
            if (target.Problem is { } problem) return Refuse(2, problem);
            changes.Add((target.Name, target.Value));
        }

        return Change(string.Join(", ", changes.Select(change => change.Name)), document =>
        {
            var lines = new List<string>();
            string? className = null;
            foreach (var (name, value) in changes)
            {
                if (ClassField(name) is { } field)
                {
                    if (document.FindClass(field.Class) is null) return SettingsEdit.Refused(NoSuchClass(field.Class, document));
                    className = document.FindClass(field.Class)!.Value.Key;
                }

                var before = document.Get(name)?.DeepClone();
                document.Set(name, value?.DeepClone());
                lines.Add(JsonNode.DeepEquals(before, value)
                    ? $"{name} is {Shown(value)}, as it was."
                    : $"{name} is now {Shown(value)} (it was {Shown(before)}) in {places.UserFile}.");
            }

            return SettingsEdit.Changed([.. lines]) with { Keys = [.. changes.Select(change => change.Name)], ClassName = className };
        });
    }

    /// <summary>
    /// settings reset: puts <paramref name="name"/> back at this build's default, written into the file (amendment 38); an
    /// entry of a map without a default of its own is removed (amendment 25). Refused with exit 2 for ElevenLabs:ApiKey and
    /// an unknown name.
    /// </summary>
    public SettingsChange Reset(string name)
    {
        if (IsTheKey(name)) return Refuse(2, ApiKeyRefused);

        var (key, value, problem) = DefaultOf(name);
        if (problem is not null) return Refuse(2, problem);

        return Change($"reset {key}", document =>
        {
            string? className = null;
            if (ClassField(key) is { } field)
            {
                if (document.FindClass(field.Class) is not { } found) return SettingsEdit.Refused(NoSuchClass(field.Class, document));
                className = found.Key;
            }

            var before = document.Get(key)?.DeepClone();
            if (value is null) document.Remove(key);
            else document.Set(key, value.DeepClone());
            var now = value is null ? "removed" : $"back at its default, {Shown(value)}";
            return SettingsEdit.Changed($"{key} is {now} (it was {Shown(before)}) in {places.UserFile}.") with { Keys = [key], ClassName = className };
        });
    }

    /// <summary>
    /// The saved class's field <paramref name="name"/> names, as Classes:&lt;class&gt;:&lt;field&gt; with a field this build
    /// knows, in any case: the class's name as given and the field's own name; null for any other name.
    /// </summary>
    public static (string Class, string Field)? ClassField(string name)
    {
        var parts = name.Trim().Split(':');
        if (parts.Length != 3 || !parts[0].Equals(SettingsCatalog.ClassesSection, StringComparison.OrdinalIgnoreCase) || parts[1].Trim().Length == 0) return null;

        return ClassFields.All.FirstOrDefault(field => field.Equals(parts[2].Trim(), StringComparison.OrdinalIgnoreCase)) is { } known ? (parts[1].Trim(), known) : null;
    }

    /// <summary>What a name that is no saved class of <paramref name="document"/> is told: that it is not one, and the saved ones.</summary>
    private static string NoSuchClass(string name, SettingsDocument document)
    {
        var names = document.Classes()?.Select(pair => $"\"{pair.Key}\"").ToList() ?? [];
        return names.Count == 0
            ? $"There is no saved class \"{name}\", and no saved class yet: ClassScribe class add \"{name}\" ... saves one."
            : $"There is no saved class \"{name}\". The saved classes are: {string.Join(", ", names)}.";
    }

    /// <summary>
    /// settings reset --all: a fresh full copy of every setting at this build's default, the saved classes kept, the file
    /// before kept as appsettings.previous.json. It alone may replace a file that is missing or not valid JSON.
    /// </summary>
    public SettingsChange ResetAll() =>
        Change("reset --all", _ => SettingsEdit.Changed($"Every setting is back at its default in {places.UserFile}; your saved classes are kept."), freshCopy: true);

    /// <summary>
    /// Adds to the user's file every setting this build knows and the file lacks, at its default, saying so a line each,
    /// and nothing more (settings fill, build-plan amendments 38 and 51). With no user file yet it writes nothing, exit 0: a
    /// file is made only at the first change. While the user's file is missing it is refused with exit 1, as every change is
    /// (layout-config 1.4), so that the installer's run does not hide the loss.
    /// </summary>
    public SettingsChange Fill()
    {
        if (places.DescribeMissing() is { } missing) return Refuse(1, missing);
        if (places.Origin != SettingsOrigin.UserFolder || places.UserFile is null) return new SettingsChange(0, [NothingToFill]);

        return Change("fill", _ => SettingsEdit.Changed());
    }

    /// <summary>
    /// What is said when a change added settings the file lacked, with this build's own version (from <see cref="BuildInfo"/>,
    /// so it is never wrong): one line for one setting, such as "ClassScribe 0.11.0 added 1 setting to your file:
    /// Recording:CameraResolution = 720p.", and for several a first line with how many, then a line each.
    /// </summary>
    public static IReadOnlyList<string> AddedLines(IReadOnlyList<(string Name, JsonNode? Value)> added) =>
        added.Count == 1
            ? [$"ClassScribe {BuildInfo.Version} added 1 setting to your file: {added[0].Name} = {SettingsCatalog.Show(added[0].Value)}."]
            : [string.Create(CultureInfo.InvariantCulture, $"ClassScribe {BuildInfo.Version} added {added.Count} settings to your file:"),
                .. added.Select(setting => $"  {setting.Name} = {SettingsCatalog.Show(setting.Value)}")];

    /// <summary>
    /// Adds every setting of <see cref="SettingsCatalog"/> that <paramref name="root"/> lacks, at its default, and for a map
    /// each default entry it lacks, or, for a map without default entries such as Recording:CameraOffsets, the map itself,
    /// empty, so that the full copy shows it; returns what was added, in the file's order.
    /// </summary>
    public static IReadOnlyList<(string Name, JsonNode? Value)> AddMissing(JsonObject root)
    {
        var document = new SettingsDocument(root);
        var added = new List<(string, JsonNode?)>();
        foreach (var key in SettingsCatalog.Keys)
        {
            if (key.Kind == SettingKind.Map)
            {
                var defaults = (JsonObject)key.Default!;
                if (defaults.Count == 0 && !document.Has(key.Name))
                {
                    document.Set(key.Name, new JsonObject());
                    added.Add((key.Name, new JsonObject()));
                }

                foreach (var (entry, value) in defaults)
                {
                    var name = $"{key.Name}:{entry}";
                    if (document.Has(name)) continue;

                    document.Set(name, value?.DeepClone());
                    added.Add((name, value));
                }

                continue;
            }

            if (document.Has(key.Name)) continue;
            document.Set(key.Name, key.Default?.DeepClone());
            added.Add((key.Name, key.Default));
        }

        return added;
    }

    /// <summary>
    /// The file's bytes, written as ClassScribe writes it: the header, then <paramref name="root"/> indented, with a line
    /// break at the end, in UTF-8 without a byte-order mark.
    /// </summary>
    public static byte[] Serialize(JsonObject root)
    {
        var text = new StringBuilder();
        foreach (var line in Header) text.Append(line).Append(Environment.NewLine);
        text.Append(root.ToJsonString(Written)).Append(Environment.NewLine);
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text.ToString());
    }

    /// <summary>
    /// True when <paramref name="bytes"/> hold a comment of the person's own: any comment after the opening brace, and before
    /// it any comment that is not a line of ClassScribe's header (see <see cref="KnownHeaders"/>), such as a note of one's
    /// own, a block comment, or a header line reworded or written twice. Such a comment would be lost by a change from
    /// ClassScribe, so the change is refused. Bytes that are not JSON hold none here.
    /// </summary>
    public static bool HasCommentsOfItsOwn(byte[] bytes)
    {
        var json = WithoutByteOrderMark(bytes);
        var reader = new Utf8JsonReader(json, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Allow, AllowTrailingCommas = true });
        List<string>? beforeTheBrace = [];
        try
        {
            while (reader.Read())
            {
                if (reader.TokenType != JsonTokenType.Comment)
                {
                    if (!IsHeader(beforeTheBrace)) return true;
                    beforeTheBrace = null;
                    continue;
                }

                // A comment inside or after the braces is always the person's own; before them, a line comment is kept as
                // written, "//" and all, to be compared with the header's lines, and a block comment never is one.
                if (beforeTheBrace is null) return true;
                var lineComment = json[(int)reader.TokenStartIndex + 1] == (byte)'/';
                beforeTheBrace.Add(lineComment ? $"//{reader.GetComment()}" : $"/*{reader.GetComment()}*/");
            }
        }
        catch (JsonException)
        {
            // Deliberately not rethrown: bytes that are not JSON are refused by the check in the reloader's own words, which
            // say where the mistake is; whether they hold comments does not matter then.
            return false;
        }

        return beforeTheBrace is not null && !IsHeader(beforeTheBrace);
    }

    /// <summary>
    /// True when <paramref name="comments"/>, the comments before the opening brace as written, are lines of one of
    /// ClassScribe's headers, in its order, none twice; a line taken out still counts. True for none at all.
    /// </summary>
    private static bool IsHeader(IReadOnlyList<string>? comments) =>
        comments is null || KnownHeaders.Any(header =>
        {
            var next = 0;
            foreach (var comment in comments)
            {
                while (next < header.Count && !header[next].Trim().Equals(comment.Trim(), StringComparison.Ordinal)) next++;
                if (next == header.Count) return false;
                next++;
            }

            return true;
        });

    /// <summary>The settings environment variable that overrides <paramref name="name"/>, such as Recording__OutputFolder, when one is set; null otherwise.</summary>
    public static string? EnvironmentOverride(string name)
    {
        var variable = name.Replace(":", "__", StringComparison.Ordinal);
        foreach (var key in Environment.GetEnvironmentVariables().Keys.OfType<string>())
        {
            if (key.Equals(variable, StringComparison.OrdinalIgnoreCase) || key.StartsWith(variable + "__", StringComparison.OrdinalIgnoreCase)) return key;
        }

        return null;
    }

    /// <summary>
    /// Arranges <paramref name="root"/> as ClassScribe writes the file: the six sections in their order, each with its known
    /// settings in the options' order and then any keys a newer build wrote, as they were; then any other top-level keys;
    /// the saved classes last.
    /// </summary>
    private static JsonObject Order(JsonObject root)
    {
        var ordered = new JsonObject();
        foreach (var section in SettingsCatalog.Sections)
        {
            if (SettingsDocument.Find(root, section) is not { } found) continue;

            var node = Take(root, found);
            if (node is not JsonObject old)
            {
                ordered[section] = node;
                continue;
            }

            var arranged = new JsonObject();
            foreach (var first in SettingsCatalog.Keys.Where(key => key.Section == section).Select(key => key.Path.Split(':')[0]).Distinct())
            {
                if (SettingsDocument.Find(old, first) is { } child) arranged[first] = Take(old, child);
            }

            foreach (var rest in old.Select(property => property.Key).ToList()) arranged[rest] = Take(old, rest);
            ordered[section] = arranged;
        }

        var classes = SettingsDocument.Find(root, SettingsCatalog.ClassesSection);
        foreach (var other in root.Select(property => property.Key).Where(key => key != classes).ToList()) ordered[other] = Take(root, other);
        if (classes is not null) ordered[SettingsCatalog.ClassesSection] = OrderClasses(Take(root, classes));
        return ordered;
    }

    /// <summary>
    /// The saved classes as the file writes them: each class's known fields in their order (see <see cref="ClassFields.All"/>),
    /// then any field a newer build wrote, as it was. Anything that is not an object stays as it is.
    /// </summary>
    private static JsonNode? OrderClasses(JsonNode? classes)
    {
        if (classes is not JsonObject all) return classes;

        foreach (var name in all.Select(property => property.Key).ToList())
        {
            if (all[name] is not JsonObject fields) continue;

            var arranged = new JsonObject();
            foreach (var field in ClassFields.All)
            {
                if (SettingsDocument.Find(fields, field) is { } found) arranged[field] = Take(fields, found);
            }

            foreach (var rest in fields.Select(property => property.Key).ToList()) arranged[rest] = Take(fields, rest);
            all[name] = arranged;
        }

        return all;
    }

    /// <summary>True when <paramref name="name"/> is ElevenLabs:ApiKey, in any case: never set, reset or listed.</summary>
    public static bool IsTheKey(string name) => name.Trim().Equals(ElevenLabsOptions.ApiKeySetting, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The setting <paramref name="name"/> names and <paramref name="values"/> as its JSON value, or why it cannot be set, in
    /// words: the API key, an unknown name, a whole map, no value, several values for one, or a value of the wrong kind.
    /// </summary>
    private static (string Name, JsonNode? Value, string? Problem)? Target(string name, string[] values)
    {
        if (IsTheKey(name)) return (name, null, ApiKeyRefused);
        if (values.Length == 0) return (name, null, $"settings set needs the value after the setting's name, such as ClassScribe settings set {name} \"...\"; settings reset {name} puts it back at its default.");

        if (SettingsCatalog.Find(name) is { } key)
        {
            if (key.Kind == SettingKind.Map) return (key.Name, null, $"{key.Name} holds entries by name: set one at a time, such as ClassScribe settings set {SettingsCatalog.EntryExample(key)}.");
            if (key.Kind == SettingKind.List) return (key.Name, new JsonArray([.. values.Select(item => (JsonNode?)JsonValue.Create(item.Trim()))]), null);
            return One(key.Name, key.Kind, key.ChoiceType, values);
        }

        if (SettingsCatalog.FindEntry(name) is { } entry)
            return One($"{entry.Map.Name}:{entry.Entry}", entry.Map.EntryKind!.Value, entry.Map.ChoiceType, values);

        // A saved class's field, Classes:<class>:<field>: text, Transcribe true or false; the class check judges the value.
        if (ClassField(name) is { } field)
        {
            var path = $"{SettingsCatalog.ClassesSection}:{field.Class}:{field.Field}";
            if (values.Length > 1) return (path, null, $"{path} takes one value, and {values.Length} were given; put a value with spaces in quotes.");
            return field.Field == ClassFields.Transcribe && bool.TryParse(values[0].Trim(), out var yes)
                ? (path, JsonValue.Create(yes), null)
                : (path, JsonValue.Create(values[0].Trim()), null);
        }

        return (name, null, Unknown(name));
    }

    /// <summary>A setting that takes one value: <paramref name="values"/>' only one read as <paramref name="kind"/>, or why not.</summary>
    private static (string Name, JsonNode? Value, string? Problem) One(string name, SettingKind kind, Type? choiceType, string[] values)
    {
        if (values.Length > 1) return (name, null, $"{name} takes one value, and {values.Length} were given; put a value with spaces in quotes, as in \"D:\\My Classes\".");

        var (value, problem) = SettingsCatalog.Read(kind, choiceType, values[0]);
        return (name, value, problem is null ? null : $"{name} {problem}, not \"{values[0]}\".");
    }

    /// <summary>
    /// The setting <paramref name="name"/> names, and its default as the file writes it: null for a map's entry without a
    /// default of its own, which reset removes; or why it cannot be reset.
    /// </summary>
    private static (string Name, JsonNode? Value, string? Problem) DefaultOf(string name)
    {
        if (SettingsCatalog.Find(name) is { } key) return (key.Name, key.Default, null);
        if (SettingsCatalog.FindEntry(name) is { } entry)
        {
            var own = ((JsonObject)entry.Map.Default!).FirstOrDefault(pair => pair.Key.Equals(entry.Entry, StringComparison.OrdinalIgnoreCase));
            return ($"{entry.Map.Name}:{(own.Key ?? entry.Entry)}", own.Value, null);
        }

        // A saved class's field has no default of its own: reset empties it, so the class takes the settings' value.
        if (ClassField(name) is { } field) return ($"{SettingsCatalog.ClassesSection}:{field.Class}:{field.Field}", null, null);

        return (name, null, Unknown(name));
    }

    /// <summary>What an unknown setting is told: that it is not one, the nearest that is, and where the list is.</summary>
    private static string Unknown(string name) =>
        SettingsCatalog.DidYouMean(name) is { } near
            ? $"{name.Trim()} is not a setting; did you mean {near}? ClassScribe settings list shows them all."
            : $"{name.Trim()} is not a setting. ClassScribe settings list shows them all.";

    /// <summary>A value as the change's line shows it: in quotes for text, as written for the rest, "nothing" for none.</summary>
    private static string Shown(JsonNode? value) => value switch
    {
        null => "nothing",
        JsonValue text when text.TryGetValue<string>(out var words) => $"\"{words}\"",
        JsonArray list => $"[{string.Join(", ", list.Select(Shown))}]",
        _ => value.ToJsonString(Written)
    };

    /// <summary>Takes the property <paramref name="name"/> out of <paramref name="parent"/>, so that it can go into another object.</summary>
    private static JsonNode? Take(JsonObject parent, string name)
    {
        var node = parent[name];
        parent.Remove(name);
        return node;
    }

    /// <summary>Removes ElevenLabs:ApiKey, in any case, wherever a hand edit put it: the key is never written.</summary>
    private static void RemoveTheKey(JsonObject root) => new SettingsDocument(root).Remove(ElevenLabsOptions.ApiKeySetting);

    /// <summary>The file read as a tree: comments passed over, trailing commas allowed; an empty object for no file.</summary>
    private static JsonObject Parse(byte[]? bytes) =>
        bytes is null ? [] : JsonNode.Parse(WithoutByteOrderMark(bytes), documentOptions: Reading) as JsonObject ?? [];

    /// <summary>
    /// The tree settings reset --all starts from: no setting (each is added at its default), with the saved classes of
    /// <paramref name="bytes"/> kept when they can be read.
    /// </summary>
    private static JsonObject FreshCopy(byte[]? bytes)
    {
        JsonObject fresh = [];
        try
        {
            if (SettingsDocument.Find(Parse(bytes), SettingsCatalog.ClassesSection) is { } name && Parse(bytes)[name] is JsonObject classes)
                fresh[SettingsCatalog.ClassesSection] = classes.DeepClone();
        }
        catch (JsonException)
        {
            // Deliberately not rethrown: a file that is not valid JSON is what reset --all starts again from, so its saved
            // classes cannot be kept; its bytes stay in appsettings.previous.json.
        }

        return fresh;
    }

    /// <summary>
    /// The problems <paramref name="after"/> has that <paramref name="before"/> did not, by their words: the change added
    /// them. A fresh copy is held to having none at all.
    /// </summary>
    private static IReadOnlyList<SettingsProblem> NewProblems(StagedSettings before, StagedSettings after, bool freshCopy)
    {
        if (!after.Readable) return [.. after.Unreadable.Select(line => new SettingsProblem(line, []))];

        var known = freshCopy ? [] : before.Problems.Select(problem => problem.Text).ToHashSet(StringComparer.Ordinal);
        return [.. after.Problems.Where(problem => !known.Contains(problem.Text))];
    }

    /// <summary>
    /// What is said about each setting <paramref name="outcome"/> changed that an environment variable overrides: the change
    /// is saved, and has no effect while that variable is set.
    /// </summary>
    private static IEnumerable<string> Overridden(SettingsEdit outcome) =>
        outcome.Keys
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(name => (name, Variable: EnvironmentOverride(name)))
            .Where(pair => pair.Variable is not null)
            .Select(pair => $"The environment variable {pair.Variable} sets {pair.name} for ClassScribe, so this change has no effect while it is set.");

    /// <summary>
    /// The bytes the change starts from, and a SHA-256 of them: the user's own file, or until it is made the file read
    /// until then; none when there is none. A file that cannot be read gives a failure in words.
    /// </summary>
    private (byte[]? Bytes, byte[]? Hash, string? Failure) ReadBytes(SettingsSource source)
    {
        try
        {
            var text = file.Read(source, expectFile: source.Origin == SettingsOrigin.UserFolder);
            if (source.Origin == SettingsOrigin.UserFolder && text.Bytes is null)
                return (null, null, places.DescribeMissing() ?? $"{source.Path} is no longer there, so nothing was changed. Put it back, or start again from the defaults with: ClassScribe settings reset --all.");
            return (text.Bytes, text.Bytes is { } bytes ? SHA256.HashData(bytes) : null, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Deliberately not rethrown: a file that cannot be read now refuses the change, which says why.
            logger.LogDebug(ex, "Could not read {Path} to change it", source.ReadPath);
            return (null, null, $"{source.ReadPath} could not be read, so nothing was changed: {ex.Message}");
        }
    }

    /// <summary>
    /// True when the user's file is as it was when the change began: the same bytes, or still not there for the first
    /// change. Another program that saved it meanwhile makes the change start again.
    /// </summary>
    private bool Unchanged(string target, bool firstChange, byte[]? hash)
    {
        if (firstChange) return !File.Exists(target);

        try
        {
            return File.Exists(target) && hash is not null && SHA256.HashData(File.ReadAllBytes(target)).AsSpan().SequenceEqual(hash);
        }
        catch (IOException ex)
        {
            // Deliberately not rethrown: a file another program holds just now is being saved, so the change starts again.
            logger.LogDebug(ex, "Could not read {Path} again before writing it", target);
            return false;
        }
    }

    /// <summary>
    /// What is kept as appsettings.previous.json of the file before a change, <paramref name="bytes"/>: the bytes as they
    /// were; or, when they hold an ElevenLabs key, which ClassScribe never writes to a file, the file without it, written as
    /// ClassScribe writes it (<see cref="BeforeTheChange.KeyTakenOut"/>); or nothing at all when the bytes are not valid JSON
    /// and mention a key, since it cannot be taken out of them (<see cref="BeforeTheChange.Bytes"/> null). Null for no file.
    /// </summary>
    private static BeforeTheChange? KeptBefore(byte[]? bytes)
    {
        if (bytes is null) return null;

        JsonObject tree;
        try
        {
            tree = Parse(bytes);
        }
        catch (JsonException)
        {
            // Deliberately not rethrown: a file that is not valid JSON, which only reset --all replaces, is kept as it was,
            // unless it may hold the key, which cannot be taken out of it; then no copy is kept, and the change says so.
            return Encoding.UTF8.GetString(bytes).Contains("ApiKey", StringComparison.OrdinalIgnoreCase) ? new BeforeTheChange(null, false) : new BeforeTheChange(bytes, false);
        }

        var document = new SettingsDocument(tree);
        if (!document.Has(ElevenLabsOptions.ApiKeySetting)) return new BeforeTheChange(bytes, false);

        RemoveTheKey(tree);
        return new BeforeTheChange(Serialize(tree), true);
    }

    /// <summary>What is kept of the file before a change (see <see cref="KeptBefore"/>).</summary>
    /// <param name="Bytes">The bytes written as appsettings.previous.json; null to keep none.</param>
    /// <param name="KeyTakenOut">True when an ElevenLabs key was taken out of them.</param>
    private sealed record BeforeTheChange(byte[]? Bytes, bool KeyTakenOut);

    /// <summary>
    /// Writes <paramref name="after"/> to <paramref name="target"/> whole: to a temporary file in the same folder, flushed to
    /// the disk, then moved into its place; the bytes before, when there was a file, kept as appsettings.previous.json first.
    /// The first change never replaces a file made meanwhile. Throws as a file write does.
    /// </summary>
    private void Write(string target, byte[]? before, byte[] after, bool firstChange)
    {
        var folder = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(folder);
        if (before is not null) WriteWhole(places.PreviousFile!, before);

        var temporary = Path.Combine(folder, $"{SettingsFile.FileName}.{Guid.NewGuid():N}.tmp");
        WriteFlushed(temporary, after);
        try
        {
            File.Move(temporary, target, overwrite: !firstChange);
        }
        catch
        {
            File.Delete(temporary);
            throw;
        }
    }

    /// <summary>Writes <paramref name="bytes"/> to <paramref name="path"/> through a temporary file moved into its place.</summary>
    private static void WriteWhole(string path, byte[] bytes)
    {
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        WriteFlushed(temporary, bytes);
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>Writes <paramref name="bytes"/> to a new file at <paramref name="path"/>, flushed to the disk.</summary>
    private static void WriteFlushed(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    /// <summary>
    /// Holds settings.lock without sharing, waiting up to <see cref="LockPatience"/> for another change to let go; null when
    /// it is still held then. The caller disposes it to let go.
    /// </summary>
    private FileStream? TakeLock()
    {
        var lockFile = places.LockFile!;
        Directory.CreateDirectory(Path.GetDirectoryName(lockFile)!);
        var waited = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return new FileStream(lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException ex) when (waited.Elapsed < LockPatience)
            {
                // Deliberately not rethrown: another change holds the lock for a moment, so this one waits its turn.
                logger.LogDebug(ex, "{Lock} is held by another change; waiting", lockFile);
                Thread.Sleep(50);
            }
            catch (IOException ex)
            {
                // Deliberately not rethrown: held for longer than a change takes, the lock is the answer, said in words.
                logger.LogDebug(ex, "{Lock} is still held after {Seconds} seconds", lockFile, LockPatience.TotalSeconds);
                return null;
            }
        }
    }

    /// <summary>A change that came to nothing, with <paramref name="exitCode"/> and <paramref name="lines"/>, each logged as a warning.</summary>
    private SettingsChange Refuse(int exitCode, params string[] lines)
    {
        foreach (var line in lines) logger.LogWarning("{Line}", line);
        return new SettingsChange(exitCode, lines);
    }

    /// <summary>A change made, its <paramref name="lines"/> logged.</summary>
    private SettingsChange Done(IReadOnlyList<string> lines)
    {
        foreach (var line in lines) logger.LogInformation("{Line}", line);
        return new SettingsChange(0, lines);
    }

    /// <summary>The bytes without the byte-order mark an editor may put at the start of a UTF-8 file.</summary>
    private static ReadOnlySpan<byte> WithoutByteOrderMark(byte[] bytes) =>
        bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]) ? bytes.AsSpan(3) : bytes;
}
