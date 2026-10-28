using ClassScribe.Audio;
using ClassScribe.Audio.Microphones;
using ClassScribe.Camera;
using ClassScribe.Runs;
using ClassScribe.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Settings;

/// <summary>
/// The saved classes under Classes in the settings (layout-config 4.1 and 4.2), read from the settings in force at each use,
/// as the gate last accepted them. Each class is checked on its own: a problem in one class makes only that class unusable,
/// and is named in the menu's header and by class list, while the other classes and the rest of the file go on. Names are
/// looked up ignoring case, exactly, never by a prefix, since .NET binds them case-sensitively while Windows' users do not
/// type them so.
/// </summary>
/// <param name="configuration">The settings in force.</param>
/// <param name="file">The settings file, read by <see cref="FindInFile"/>; null for none, as in most tests.</param>
/// <param name="reloader">Stages the file's bytes for <see cref="FindInFile"/>; null for none.</param>
/// <param name="logger">Where a file that cannot be read for <see cref="FindInFile"/> is noted; null for nowhere.</param>
public sealed class SavedClasses(IConfiguration configuration, SettingsFile? file = null, SettingsReloader? reloader = null, ILogger<SavedClasses>? logger = null)
{
    /// <summary>The most characters a class's name or course may have, as a folder's name.</summary>
    public const int LongestName = 80;

    /// <summary>The longest part of a camera's name a class may hold.</summary>
    private const int LongestCameraName = 200;

    /// <summary>The name of ClassScribe's own process, which the microphone must never follow.</summary>
    private const string OwnProcessName = "ClassScribe";

    /// <summary>Every saved class, in the order of their names, as the settings give them.</summary>
    public IReadOnlyList<SavedClassEntry> All() => Read(configuration);

    /// <summary>The saved class named <paramref name="name"/>, ignoring case and spaces around it; null when there is none.</summary>
    public SavedClassEntry? Find(string name) => All().FirstOrDefault(entry => entry.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The saved class named <paramref name="name"/>, ignoring case and spaces around it, as the settings file holds it now,
    /// staged and checked as the reloader does, each class on its own. For N5's hint on the command line, which is given
    /// before the gate reads the file for a recording: until then the settings in force are what the first read accepted,
    /// none of the file's values when a setting outside Classes is refused, while record --class would record the class,
    /// since a recording drops only that setting. Null when there is none, when there is no file to read (as in most
    /// tests), and when the file cannot be read now or is not valid JSON.
    /// </summary>
    public SavedClassEntry? FindInFile(string name)
    {
        if (file is null || reloader is null) return null;

        try
        {
            using var staged = reloader.Stage(file.Read().Bytes);
            return staged.Classes.FirstOrDefault(entry => entry.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Deliberately not rethrown: the class is looked for only to add a hint to a mistake, which is said without it
            // while the file cannot be read.
            logger?.LogDebug(ex, "Could not read {Path} for the saved classes", file.Path);
            return null;
        }
    }

    /// <summary>
    /// The fields of the saved class named <paramref name="name"/>, ignoring case, as the settings hold them, by name in any
    /// case and fields this build does not know included, for options given over it to be laid on; null when there is none.
    /// </summary>
    public Dictionary<string, string?>? FieldsOf(string name) =>
        configuration.GetSection(SettingsCatalog.ClassesSection).GetChildren().FirstOrDefault(child => child.Key.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)) is { } section
            ? new Dictionary<string, string?>(Fields(section), StringComparer.OrdinalIgnoreCase)
            : null;

    /// <summary>
    /// The saved classes a typed name may mean, as menu choices 1 and 2 offer them: the class of that name, else those whose
    /// course it is, ignoring case.
    /// </summary>
    public IReadOnlyList<SavedClassEntry> Matching(string typed)
    {
        if (Find(typed) is { } named) return [named];

        return [.. All().Where(entry => entry.Class?.Course.Equals(typed.Trim(), StringComparison.OrdinalIgnoreCase) == true)];
    }

    /// <summary>The saved classes in <paramref name="settings"/>, each checked on its own (see the class).</summary>
    public static IReadOnlyList<SavedClassEntry> Read(IConfiguration settings) =>
        [.. settings.GetSection(SettingsCatalog.ClassesSection).GetChildren().Select(child => Check(child.Key, Fields(child)))];

    /// <summary>
    /// The class <paramref name="name"/> with <paramref name="fields"/>, checked (layout-config 4.2, amendment 11): the name
    /// and the course must survive the cleaning a folder's name gets unchanged and be at most 80 characters, the course is
    /// required; the mode one of three; App and Device not both, and neither in person; the microphone and the app it follows
    /// only with a microphone, the app never ClassScribe and never in person; the folder a path; Transcribe true or false;
    /// the camera only in person, 1 to 200 characters; its size by <see cref="CameraSize.TryParse"/>, only with a camera.
    /// The glossary is read as it is written and never makes the class unusable, since a glossary must never stop a
    /// recording. Fields this build does not know pass.
    /// </summary>
    public static SavedClassEntry Check(string name, IReadOnlyDictionary<string, string?> fields)
    {
        var problems = new List<string>();

        // A field's text, trimmed; null when it is not there or holds only spaces.
        string? Field(string field) => fields.TryGetValue(field, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

        CheckName(problems, "Its name", name);
        var course = Field(ClassFields.Course);
        if (course is null) problems.Add("Course is empty; it names the recording's folder and files, such as Mantiq.");
        else CheckName(problems, "Course", course);

        var mode = RecordingMode.Class;
        if (Field(ClassFields.Mode) is { } modeText)
        {
            if (Enum.GetNames<RecordingMode>().FirstOrDefault(known => known.Equals(modeText, StringComparison.OrdinalIgnoreCase)) is { } known)
                mode = Enum.Parse<RecordingMode>(known);
            else
                problems.Add($"Mode is \"{modeText}\"; it must be Class, ClassAndMicrophone or Microphone (in person).");
        }

        var app = Field(ClassFields.App);
        var device = Field(ClassFields.Device);
        if (app is not null && device is not null) problems.Add("App and Device are both set; a class records one app or one output device.");
        if (mode == RecordingMode.Microphone && (app is not null || device is not null)) problems.Add("App or Device is set, but the class is in person (Mode Microphone), which records no class.");

        var microphone = Field(ClassFields.Microphone);
        if (microphone is not null && mode == RecordingMode.Class) problems.Add("Microphone is set, but the class records the class alone (Mode Class); set Mode to ClassAndMicrophone, or clear it.");

        FollowRule? follows = null;
        if (Field(ClassFields.MicrophoneFollows) is { } followText)
        {
            if (mode != RecordingMode.ClassAndMicrophone)
                problems.Add("MicrophoneFollows is set, but only a class recorded with your microphone beside it follows an app.");
            else if (!FollowRule.TryParse(followText, out follows) || follows is FollowRule.Named named && named.Matches(OwnProcessName))
                problems.Add($"MicrophoneFollows is \"{followText}\"; it must be part of the class app's name, such as chrome, or {FollowRule.AnyWord} or {FollowRule.NoneWord}, and never ClassScribe.");
        }

        var output = Field(ClassFields.OutputFolder);
        if (output is not null && output.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            problems.Add("OutputFolder holds a tab, a line break or another character that cannot be part of a path.");

        var transcribe = true;
        if (Field(ClassFields.Transcribe) is { } transcribeText && !bool.TryParse(transcribeText, out transcribe))
            problems.Add($"Transcribe is \"{transcribeText}\"; it must be true or false.");

        var camera = Field(ClassFields.Camera);
        if (camera is not null && mode != RecordingMode.Microphone) problems.Add("Camera is set, but a lesson's picture is recorded only in person (Mode Microphone).");
        if (camera is { Length: > LongestCameraName }) problems.Add($"Camera is {camera.Length} characters long; part of a camera's name is at most {LongestCameraName}.");

        string? size = null;
        if (Field(ClassFields.CameraResolution) is { } sizeText)
        {
            if (camera is null) problems.Add("CameraResolution is set, but the class records no camera.");
            if (CameraSize.TryParse(sizeText, out var parsed)) size = parsed.Text;
            else problems.Add($"CameraResolution is \"{sizeText}\"; it must be {CameraSize.Rule}.");
        }

        if (problems.Count > 0) return new SavedClassEntry(name, null, problems);

        ClassSource? source = mode == RecordingMode.Microphone ? null
            : app is not null ? new ClassSource.App(app.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? app[..^4] : app)
            : device is not null && !device.Equals("auto", StringComparison.OrdinalIgnoreCase) ? new ClassSource.Device(device)
            : new ClassSource.Automatic();
        return new SavedClassEntry(name, new SavedClass(name, course!, mode, source, mode == RecordingMode.Class ? null : microphone, follows, output, transcribe, camera, size,
            Field(ClassFields.KeytermsFile)), []);
    }

    /// <summary>The fields of one class's section, by name, ignoring case; a field holding a list or an object is passed over.</summary>
    private static IReadOnlyDictionary<string, string?> Fields(IConfigurationSection section) =>
        section.GetChildren().ToDictionary(child => child.Key, child => child.Value, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Adds a problem when <paramref name="value"/>, a class's name or course called <paramref name="what"/>, would change as
    /// a folder's name, or is longer than <see cref="LongestName"/>.
    /// </summary>
    private static void CheckName(List<string> problems, string what, string value)
    {
        if (ClassFolders.SafeName(value) != value.Trim())
            problems.Add($"{what}, \"{value}\", holds characters Windows does not allow in a folder's name, or spaces or dots at its end.");
        else if (value.Trim().Length > LongestName)
            problems.Add($"{what} is {value.Trim().Length} characters long; it may be at most {LongestName}.");
    }
}
