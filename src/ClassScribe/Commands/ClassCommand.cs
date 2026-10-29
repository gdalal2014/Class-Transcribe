using System.Text.Json.Nodes;
using ClassScribe.Desktop;
using ClassScribe.Settings;
using ClassScribe.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClassScribe.Commands;

/// <summary>What the class command is asked to do (layout-config 4.4).</summary>
public abstract record ClassRequest
{
    /// <summary>Only the kinds declared here derive from it.</summary>
    private ClassRequest()
    {
    }

    /// <summary>class list: every saved class, whether it can be used and why not; as JSON with <paramref name="Json"/>.</summary>
    /// <param name="Json">True for JSON alone on standard output.</param>
    public sealed record List(bool Json) : ClassRequest;

    /// <summary>class show: one saved class's fields, what it records and its command; as JSON with <paramref name="Json"/>.</summary>
    /// <param name="Name">The class's name.</param>
    /// <param name="Json">True for JSON alone on standard output.</param>
    public sealed record Show(string Name, bool Json) : ClassRequest;

    /// <summary>class add: a new saved class, its course copied from its name unless given.</summary>
    /// <param name="Name">The class's name.</param>
    /// <param name="Choices">Its options.</param>
    public sealed record Add(string Name, ClassChoices Choices) : ClassRequest;

    /// <summary>class set: changes a saved class by its options, in groups (see <see cref="ClassChoices"/>).</summary>
    /// <param name="Name">The class's name.</param>
    /// <param name="Choices">The options given.</param>
    public sealed record Set(string Name, ClassChoices Choices) : ClassRequest;

    /// <summary>class rename: changes the class's name only; its course, folders, files and Drive stay as they are.</summary>
    /// <param name="Name">The class's name now.</param>
    /// <param name="NewName">Its new name.</param>
    public sealed record Rename(string Name, string NewName) : ClassRequest;

    /// <summary>class remove: removes the saved class; no recording is touched.</summary>
    /// <param name="Name">The class's name.</param>
    public sealed record Remove(string Name) : ClassRequest;
}

/// <summary>
/// One saved class as class list and class show give it: the class as the file holds it, checked on its own, its fields as
/// the file holds them, its own glossary as a full path, and its command. The window writes it as text or JSON (the Cli's
/// ClassOutput).
/// </summary>
/// <param name="Entry">The class, checked on its own.</param>
/// <param name="Fields">Its fields as the file holds them, by name, in the settings' order.</param>
/// <param name="Glossary">Its own glossary as a full path; "" for none, or for a value no path can hold; null for ElevenLabs:KeytermsFile, and for a class that cannot be used.</param>
/// <param name="Command">The launcher's line that records it, from PowerShell; null for a class that cannot be used.</param>
public sealed record ClassShown(SavedClassEntry Entry, IReadOnlyDictionary<string, string?> Fields, string? Glossary, string? Command);

/// <summary>
/// What the class command answers: the exit code; what is said on standard error, refusals and the reasons a file cannot be
/// read, in order, which come before anything on standard output; and, for class list and class show, the classes shown,
/// as data, which the window writes as text or JSON (the Cli's ClassOutput).
/// </summary>
/// <param name="ExitCode">0 done, or a class that can be used; 2 a mistake in what was asked, or a class that cannot be used or is not there; 1 a file that cannot be read or changed.</param>
/// <param name="Errors">The lines for standard error, in order; empty when nothing is wrong.</param>
public sealed record ClassAnswer(int ExitCode, IReadOnlyList<string> Errors)
{
    /// <summary>For class list, the settings file's path its header names. Null otherwise.</summary>
    public string? Path { get; init; }

    /// <summary>For class list, every saved class; for class show, the one shown. Null when nothing is shown.</summary>
    public IReadOnlyList<ClassShown>? Classes { get; init; }

    /// <summary>An answer that says nothing: <paramref name="exitCode"/> alone, as add, set, rename and remove answer, whose lines the writer logs.</summary>
    public static ClassAnswer Done(int exitCode) => new(exitCode, []);

    /// <summary>An answer that refuses with <paramref name="why"/> on standard error and <paramref name="exitCode"/>.</summary>
    public static ClassAnswer Refused(int exitCode, string why) => new(exitCode, [why]);
}

/// <summary>
/// The class command (layout-config 4.4, build-plan amendments 4 and 11): list, show, add, set, rename and remove the saved
/// classes under Classes in the user's settings file, without the gate or the report at the start, as settings runs. Changes
/// go through the one writer (<see cref="ISettingsStore.Change"/>), which refuses a change that adds a problem to the class it
/// changes; what it answers is data (<see cref="ClassAnswer"/>), which the window writes on standard output and standard
/// error; class show answers the launcher's question, exit 0 when the class can be used, 2 when it cannot or is not
/// there, 1 when the file is not valid JSON, whatever the problems outside Classes, which a recording drops. Names are matched
/// exactly, ignoring case, never by a prefix.
/// </summary>
/// <param name="store">The settings: where they are, the file, the staged values, and the one writer of the user's file.</param>
/// <param name="files">Whether a glossary or an old course's folder is there.</param>
/// <param name="recording">The run's recording settings: the recordings folder a class without its own uses.</param>
/// <param name="folders">The recordings' library: where a course's folder is under a recordings folder.</param>
/// <param name="commandLine">The commands printed for later: a class's launcher line.</param>
/// <param name="logger">Where a file that cannot be read is noted.</param>
public sealed class ClassCommand(ISettingsStore store, IFileProbe files, IOptionsSnapshot<RecordingOptions> recording, IClassFolders folders, CommandLine commandLine, ILogger<ClassCommand> logger)
{
    /// <summary>The exit code of a mistake in what was asked, or a class that cannot be used.</summary>
    private const int Mistake = 2;

    /// <summary>The exit code of a file that cannot be read or changed.</summary>
    private const int Failed = 1;

    /// <summary>Does what <paramref name="request"/> asks and returns what it answers, with the exit code.</summary>
    public Task<ClassAnswer> RunAsync(ClassRequest request) => Task.FromResult(request switch
    {
        ClassRequest.List => List(),
        ClassRequest.Show show => Show(show.Name),
        ClassRequest.Add add => Add(add.Name.Trim(), add.Choices),
        ClassRequest.Set set => Change(set.Name.Trim(), set.Choices),
        ClassRequest.Rename rename => Rename(rename.Name.Trim(), rename.NewName.Trim()),
        ClassRequest.Remove remove => Remove(remove.Name.Trim()),
        _ => throw new ArgumentOutOfRangeException(nameof(request), request, "A class request this build does not know.")
    });

    /// <summary>What a name that is no saved class is told: that it is not one, and the saved ones, or how to save one.</summary>
    public static string NoSuchClass(string name, IEnumerable<string> saved)
    {
        var names = saved.ToList();
        return names.Count == 0
            ? $"There is no saved class \"{name}\", and no saved class yet: ClassScribe class add \"{name}\" ... saves one."
            : $"There is no saved class \"{name}\". The saved classes are: {string.Join(", ", names.Select(known => $"\"{known}\""))}.";
    }

    /// <summary>
    /// What a name for a class must be, when it is not: a folder's name as Windows takes it, unchanged by the cleaning a
    /// recording's folder gets, at most 80 characters, and not empty. Null when it is one.
    /// </summary>
    public static string? NameProblem(string name) =>
        name.Length == 0 ? "A saved class needs a name, such as \"Mantiq\"."
        : FolderNames.SafeName(name) != name ? $"\"{name}\" cannot be a class's name: it holds characters Windows does not allow in a folder's name (\\ / : * ? \" < > |), or spaces or dots at its end."
        : name.Length > SavedClass.LongestName ? $"\"{name}\" is {name.Length} characters long; a class's name may be at most {SavedClass.LongestName}."
        : null;

    /// <summary>class list: every saved class, what it records or why it cannot be used, with the file's path; exit 1 when the file cannot be read.</summary>
    private ClassAnswer List()
    {
        var (classes, errors) = Read();
        if (classes is null) return new ClassAnswer(Failed, errors);

        return new ClassAnswer(0, []) { Path = store.SettingsPath, Classes = [.. classes.Entries.Select(entry => Shown(entry, classes.Fields[entry.Name]))] };
    }

    /// <summary>
    /// class show: the class's fields, what it records and its command; exit 0 when it can be used, 2 when it
    /// cannot or is not there, or while the settings file is missing, and 1 when the file is not valid JSON (build-plan
    /// amendment 4): the launcher then records with its own values.
    /// </summary>
    private ClassAnswer Show(string name)
    {
        if (store.DescribeMissing() is { } missing) return ClassAnswer.Refused(Mistake, missing);
        var (classes, errors) = Read();
        if (classes is null) return new ClassAnswer(Failed, errors);

        if (classes.Entries.FirstOrDefault(entry => entry.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)) is not { } found)
            return ClassAnswer.Refused(Mistake, NoSuchClass(name.Trim(), classes.Entries.Select(entry => entry.Name)));

        return new ClassAnswer(found.Usable ? 0 : Mistake, []) { Classes = [Shown(found, classes.Fields[found.Name])] };
    }

    /// <summary>class add: a new saved class, its course copied from its name unless --course gives one (layout-config 4.4).</summary>
    private ClassAnswer Add(string name, ClassChoices choices)
    {
        if (NameProblem(name) is { } problem) return ClassAnswer.Refused(Mistake, problem);

        return ClassAnswer.Done(store.Change($"class add \"{name}\"", document =>
        {
            if (document.FindClass(name) is { } existing)
                return SettingsEdit.Refused($"There is a saved class \"{existing.Key}\" already; ClassScribe class set changes it, and class rename renames it.");

            var fields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { [ClassFields.Course] = name };
            if (choices.ApplyTo(fields) is { } mix) return SettingsEdit.Refused(mix);

            var written = new JsonObject();
            Write(written, fields);
            document.Classes(create: true)![name] = written;
            return SettingsEdit.Changed(
                [$"Saved the class \"{name}\" in {store.UserFile}: {Describe(name, fields)}.", .. GlossaryNotThere(fields),
                    $"From PowerShell: {commandLine.RecordClass(name)}"]) with { ClassName = name };
        }).ExitCode);
    }

    /// <summary>
    /// class set: changes the saved class by the options given, in groups (see <see cref="ClassChoices"/>); a changed course
    /// says that the next class goes to a new folder, with new file names, and that continue will not offer the old course's
    /// recordings, and names the old course's folder when it holds recordings.
    /// </summary>
    private ClassAnswer Change(string name, ClassChoices choices) =>
        ClassAnswer.Done(store.Change($"class set \"{name}\"", document =>
        {
            if (document.FindClass(name) is not { } found) return SettingsEdit.Refused(NoSuchClass(name, document.Classes()?.Select(pair => pair.Key) ?? []));

            var fields = Known(found.Fields);
            var oldCourse = fields.GetValueOrDefault(ClassFields.Course);
            if (choices.ApplyTo(fields) is { } mix) return SettingsEdit.Refused(mix);

            Write(found.Fields, fields);
            var lines = new List<string> { $"Changed the class \"{found.Key}\" in {store.UserFile}: {Describe(found.Key, fields)}." };
            if (choices.Keyterms is not null) lines.AddRange(GlossaryNotThere(fields));
            if (fields.GetValueOrDefault(ClassFields.Course) is { } newCourse && oldCourse is not null && !newCourse.Equals(oldCourse, StringComparison.Ordinal))
                lines.AddRange(CourseChanged(found.Key, oldCourse, newCourse, fields.GetValueOrDefault(ClassFields.OutputFolder)));
            return SettingsEdit.Changed([.. lines]) with { ClassName = found.Key };
        }).ExitCode);

    /// <summary>
    /// class rename: the name only; the course, the folders, the file names and Drive stay as they are, and the launcher's
    /// line with the new name is said. Two names that differ only in letter case would make .NET refuse the whole file, so
    /// a name another class has, in any case, is refused.
    /// </summary>
    private ClassAnswer Rename(string name, string newName)
    {
        if (NameProblem(newName) is { } problem) return ClassAnswer.Refused(Mistake, problem);

        return ClassAnswer.Done(store.Change($"class rename \"{name}\" \"{newName}\"", document =>
        {
            var classes = document.Classes();
            if (document.FindClass(name) is not { } found) return SettingsEdit.Refused(NoSuchClass(name, classes?.Select(pair => pair.Key) ?? []));
            if (document.FindClass(newName) is { } other && !other.Key.Equals(found.Key, StringComparison.Ordinal))
                return SettingsEdit.Refused($"There is a saved class \"{other.Key}\" already; names that differ only in letter case are one name to the settings.");

            var renamed = new JsonObject();
            var all = classes!;
            foreach (var key in all.Select(pair => pair.Key).ToList())
            {
                var node = all[key];
                all.Remove(key);
                renamed[key == found.Key ? newName : key] = node;
            }

            document.Root[SettingsDocument.Find(document.Root, SettingsCatalog.ClassesSection)!] = renamed;
            return SettingsEdit.Changed(
                $"Renamed the class \"{found.Key}\" to \"{newName}\"; its course, its folders, its files and Google Drive stay as they are.",
                $"Its launcher's line now: {commandLine.RecordClass(newName)}") with { ClassName = newName };
        }).ExitCode);
    }

    /// <summary>class remove: the saved class goes from the settings; no recording is touched. The section goes when it is left empty.</summary>
    private ClassAnswer Remove(string name) =>
        ClassAnswer.Done(store.Change($"class remove \"{name}\"", document =>
        {
            var classes = document.Classes();
            if (document.FindClass(name) is not { } found) return SettingsEdit.Refused(NoSuchClass(name, classes?.Select(pair => pair.Key) ?? []));

            classes!.Remove(found.Key);
            if (classes.Count == 0) document.Remove(SettingsCatalog.ClassesSection);
            return SettingsEdit.Changed($"Removed the saved class \"{found.Key}\" from {store.UserFile}; no recording was touched.");
        }).ExitCode);

    /// <summary>
    /// The saved classes of the file read now, each checked on its own, with each class's fields as the file holds them; null,
    /// with the reason in the lines for standard error, when the file cannot be read or is not valid JSON.
    /// </summary>
    private (FileClasses? Classes, IReadOnlyList<string> Errors) Read()
    {
        var source = store.Current();
        try
        {
            using var staged = store.Stage(store.Read(source).Bytes);
            if (!staged.Readable) return (null, [.. staged.Unreadable, $"Nothing can be read from {source.ReadPath} until it is fixed."]);

            var section = staged.Children(SettingsCatalog.ClassesSection);
            var fields = section.ToDictionary(child => child.Key, child => (IReadOnlyDictionary<string, string?>)staged.Children($"{SettingsCatalog.ClassesSection}:{child.Key}")
                .Where(field => field.Value is not null).ToDictionary(field => field.Key, field => field.Value));
            return (new FileClasses(staged.Classes, fields), []);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Deliberately not rethrown: a file that cannot be read is the answer, said on standard error with exit 1.
            logger.LogDebug(ex, "Could not read {Path} for the saved classes", source.ReadPath);
            return (null, [$"{source.ReadPath} could not be read: {ex.Message}"]);
        }
    }

    /// <summary>The saved classes of the file read now, each checked on its own, and each class's fields as the file holds them, by the class's name.</summary>
    /// <param name="Entries">The classes, checked.</param>
    /// <param name="Fields">Each class's fields, by the class's name as the file writes it.</param>
    private sealed record FileClasses(IReadOnlyList<SavedClassEntry> Entries, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>> Fields);

    /// <summary>
    /// A class as class list and class show give it: its fields as the file holds them, its own glossary as a full path ("" for
    /// none, null for ElevenLabs:KeytermsFile), and its command.
    /// </summary>
    private ClassShown Shown(SavedClassEntry entry, IReadOnlyDictionary<string, string?> fields) =>
        new(entry, fields, entry.Class?.KeytermsFile is { } glossary ? GlossaryPath(glossary, store.Folder) : null, entry.Class is { } usable ? commandLine.RecordClass(usable.Name) : null);

    /// <summary>
    /// The file a class's glossary value names, as a full path, a relative one taken from <paramref name="folder"/>; "" for
    /// none, or for a value no path can hold.
    /// </summary>
    private string GlossaryPath(string glossary, string folder)
    {
        if (SavedClass.MeansNoGlossary(glossary)) return "";

        try
        {
            return store.ResolveFile(glossary, folder);
        }
        catch (ArgumentException)
        {
            // Deliberately not rethrown: a value no path can hold names no file; it is said as none here, and the run's start
            // and the menu's header say what is wrong with it.
            return "";
        }
    }

    /// <summary>
    /// The line that says the class's own glossary, in <paramref name="fields"/>, is not there yet: saved all the same, since
    /// the file may come later, and a glossary never stops a recording. A relative one is taken from the user's folder, where
    /// the writer saves the class. Nothing for none, for the setting's, and for a file that is there.
    /// </summary>
    private IEnumerable<string> GlossaryNotThere(Dictionary<string, string?> fields)
    {
        if (fields.GetValueOrDefault(ClassFields.KeytermsFile) is not { } glossary || GlossaryPath(glossary, store.UserFolder ?? store.Folder) is not { Length: > 0 } path
            || files.FileExists(path)) yield break;

        yield return $"The glossary {path} is not there yet: the class's transcripts are made without its terms until it is.";
    }

    /// <summary>A class's known fields, as text by name in any case: what the options are laid on.</summary>
    private static Dictionary<string, string?> Known(JsonObject fields)
    {
        var known = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in ClassFields.All)
        {
            if (SettingsDocument.Find(fields, field) is not { } found) continue;
            known[field] = fields[found] switch
            {
                JsonValue value when value.TryGetValue<bool>(out var yes) => yes ? "true" : "false",
                JsonValue value when value.TryGetValue<string>(out var text) => text,
                var other => other?.ToJsonString()
            };
        }

        return known;
    }

    /// <summary>
    /// Writes the known fields of <paramref name="fields"/> into <paramref name="target"/>: each set one as text, Transcribe as
    /// true or false; each empty or gone one removed. Fields a newer build wrote stay as they are.
    /// </summary>
    private static void Write(JsonObject target, Dictionary<string, string?> fields)
    {
        foreach (var field in ClassFields.All)
        {
            var existing = SettingsDocument.Find(target, field);
            if (fields.GetValueOrDefault(field) is not { Length: > 0 } value)
            {
                if (existing is not null) target.Remove(existing);
                continue;
            }

            target[existing ?? field] = field == ClassFields.Transcribe && bool.TryParse(value, out var yes) ? JsonValue.Create(yes) : JsonValue.Create(value.Trim());
        }
    }

    /// <summary>What the class <paramref name="name"/> with <paramref name="fields"/> records, in words; or why it cannot be used.</summary>
    private static string Describe(string name, Dictionary<string, string?> fields)
    {
        var entry = SavedClass.Check(name, fields);
        return entry.Class?.Describe() ?? string.Join(" ", entry.Problems);
    }

    /// <summary>
    /// What is said when a class's course changes (layout-config 4.4): its next class goes to a new folder, with new file
    /// names and a new folder on Google Drive, and continue will not offer the old course's recordings; and where those are,
    /// when the old course's folder is there.
    /// </summary>
    private IEnumerable<string> CourseChanged(string name, string oldCourse, string newCourse, string? classFolder)
    {
        yield return $"The next class of {name} goes to a new folder, {FolderNames.SafeName(newCourse)}\\..., with new file names and a new folder on Google Drive; continue will not offer recordings of {oldCourse}.";
        var old = System.IO.Path.Combine(folders.RootFor(classFolder ?? recording.Value.OutputFolder), FolderNames.SafeName(oldCourse));
        if (files.DirectoryExists(old)) yield return $"The recordings of {oldCourse} stay where they are, in {old}.";
    }
}
