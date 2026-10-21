using ClassScribe.Audio;
using ClassScribe.Audio.Microphones;
using ClassScribe.Camera;
using ClassScribe.Commands;
using ClassScribe.Storage;
using Microsoft.Extensions.Configuration;

namespace ClassScribe.Settings;

/// <summary>
/// The fields of a saved class, as the settings file writes them under Classes:&lt;name&gt; (layout-config 4.1, build-plan
/// amendment 11 with camera-first's Camera and CameraResolution). FolderLayout, MicrophoneSpeaker and ClassSpeaker come with
/// later steps; a field this build does not know passes the check and stays in the file (amendment 3).
/// </summary>
public static class ClassFields
{
    /// <summary>The recording's name, which names its folder and files; always written, copied from the class's name by class add.</summary>
    public const string Course = "Course";

    /// <summary>Class, ClassAndMicrophone or Microphone (in person); Class when empty.</summary>
    public const string Mode = "Mode";

    /// <summary>The app whose sound is recorded, by its process name.</summary>
    public const string App = "App";

    /// <summary>The output device recorded, by part of its name, or auto; automatic when App is empty too.</summary>
    public const string Device = "Device";

    /// <summary>The microphone, by part of its name; Recording:Microphone, else Windows' default (never Bluetooth), when empty.</summary>
    public const string Microphone = "Microphone";

    /// <summary>The app the microphone follows: a process name, auto or none; the App in app mode, else Recording:MicrophoneFollows, when empty.</summary>
    public const string MicrophoneFollows = "MicrophoneFollows";

    /// <summary>The recordings folder; Recording:OutputFolder when empty.</summary>
    public const string OutputFolder = "OutputFolder";

    /// <summary>false to record only when the class is started from menu C; true when empty.</summary>
    public const string Transcribe = "Transcribe";

    /// <summary>The camera that records a lesson's picture too, in person: part of its name, or auto for any.</summary>
    public const string Camera = "Camera";

    /// <summary>The camera's picture size, only with Camera: 480p, 720p, 1080p or a width and a height.</summary>
    public const string CameraResolution = "CameraResolution";

    /// <summary>The word a camera field holds for any camera: a USB camera when one is plugged in, else the built-in front camera.</summary>
    public const string AnyCamera = "auto";

    /// <summary>Every field this build knows, in the order the file writes them.</summary>
    public static IReadOnlyList<string> All { get; } = [Course, Mode, App, Device, Microphone, MicrophoneFollows, OutputFolder, Transcribe, Camera, CameraResolution];
}

/// <summary>A saved class that can be used, as its fields say (see <see cref="SavedClasses"/>).</summary>
/// <param name="Name">The name it is started by, such as Aqidah - Sughra As-Sughra (Zoom app).</param>
/// <param name="Course">The recording's name, which names its folder and files.</param>
/// <param name="Mode">The class alone, the class and your microphone, or your microphone alone in person.</param>
/// <param name="Source">Where the class's sound comes from; null in person.</param>
/// <param name="Microphone">Part of the microphone's name; null for the settings and Windows' default.</param>
/// <param name="Follows">The app the microphone follows; null for the app recorded in app mode, else Recording:MicrophoneFollows.</param>
/// <param name="OutputFolder">The recordings folder; null for Recording:OutputFolder.</param>
/// <param name="Transcribe">False to record only when started from menu C.</param>
/// <param name="Camera">The camera, in person: part of its name, or <see cref="ClassFields.AnyCamera"/>; null for none.</param>
/// <param name="CameraResolution">The picture's size as <see cref="CameraSize.Text"/> writes it; null for the session's pick or the setting.</param>
public sealed record SavedClass(string Name, string Course, RecordingMode Mode, ClassSource? Source, string? Microphone, FollowRule? Follows, string? OutputFolder,
    bool Transcribe, string? Camera, string? CameraResolution)
{
    /// <summary>What it records, as a run's source plan: the camera's name part null for any camera, its size kept for the start to resolve.</summary>
    public SourcePlan Plan => Mode switch
    {
        RecordingMode.Microphone => new SourcePlan(RecordingMode.Microphone, null, new MicrophoneChoice(Microphone),
            Camera is null ? null : new CameraChoice(Camera.Equals(ClassFields.AnyCamera, StringComparison.OrdinalIgnoreCase) ? null : Camera, CameraResolution)),
        RecordingMode.ClassAndMicrophone => new SourcePlan(RecordingMode.ClassAndMicrophone, Source, new MicrophoneChoice(Microphone, Follows)),
        _ => new SourcePlan(RecordingMode.Class, Source, Microphone: null)
    };

    /// <summary>
    /// What it records, in words for the menu and the run's first line: "chrome, with your microphone (Microphone Array,
    /// following chrome)", "the device the class plays on", "in person, your microphone (OBSBOT), with the camera "OBSBOT"
    /// (720p)", and ", recorded only" for a class that is not transcribed when started from menu C.
    /// </summary>
    public string Describe()
    {
        var microphone = Microphone ?? "Windows' default";
        var words = Mode switch
        {
            RecordingMode.Microphone => $"in person, your microphone ({microphone})" + (Camera is null ? ""
                : Camera.Equals(ClassFields.AnyCamera, StringComparison.OrdinalIgnoreCase) ? $", with a camera ({CameraResolution ?? "the size set"})"
                : $", with the camera \"{Camera}\" ({CameraResolution ?? "the size set"})"),
            RecordingMode.ClassAndMicrophone => $"{SourceWords(Source)}, with your microphone ({microphone}{FollowWords()})",
            _ => SourceWords(Source)
        };
        return Transcribe ? words : $"{words}, recorded only";
    }

    /// <summary>The class's source, in words: the app's name, the device by its name, or the device the class plays on.</summary>
    private static string SourceWords(ClassSource? source) => source switch
    {
        ClassSource.App app => app.ProcessName,
        ClassSource.Device device => $"the output device \"{device.NamePart}\"",
        _ => "the device the class plays on"
    };

    /// <summary>The app the microphone follows, in words after its name: the app named, the app recorded, any app, or V alone.</summary>
    private string FollowWords() => Follows switch
    {
        FollowRule.Named named => $", following {named.NamePart}",
        FollowRule.Any => ", following any app that holds it",
        FollowRule.None => ", whenever V is on",
        _ => Source is ClassSource.App app ? $", following {app.ProcessName}" : ""
    };
}

/// <summary>One saved class as the file holds it: its name, the class when it can be used, and else why not.</summary>
/// <param name="Name">The name it is started by, as the file writes it.</param>
/// <param name="Class">The class, when it can be used; null otherwise.</param>
/// <param name="Problems">Why it cannot be used, one sentence each; empty when it can.</param>
public sealed record SavedClassEntry(string Name, SavedClass? Class, IReadOnlyList<string> Problems)
{
    /// <summary>True when the class can be used.</summary>
    public bool Usable => Class is not null;

    /// <summary>The line the menu's header and class list give a class that cannot be used.</summary>
    public string CannotBeUsed => $"The saved class \"{Name}\" cannot be used: {string.Join(" ", Problems)}";
}

/// <summary>
/// The saved classes under Classes in the settings (layout-config 4.1 and 4.2), read from the settings in force at each use,
/// as the gate last accepted them. Each class is checked on its own: a problem in one class makes only that class unusable,
/// and is named in the menu's header and by class list, while the other classes and the rest of the file go on. Names are
/// looked up ignoring case, exactly, never by a prefix, since .NET binds them case-sensitively while Windows' users do not
/// type them so.
/// </summary>
/// <param name="configuration">The settings in force.</param>
public sealed class SavedClasses(IConfiguration configuration)
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
    /// Fields this build does not know pass.
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
        return new SavedClassEntry(name, new SavedClass(name, course!, mode, source, mode == RecordingMode.Class ? null : microphone, follows, output, transcribe, camera, size), []);
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
