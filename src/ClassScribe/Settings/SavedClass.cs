using ClassScribe.Audio;
using ClassScribe.Audio.Microphones;
using ClassScribe.Camera;
using ClassScribe.Runs;

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

    /// <summary>
    /// The class's own glossary (architecture 8.2, item 4): a file, environment variables expanded and a relative one taken
    /// from the settings folder, or none for no glossary; ElevenLabs:KeytermsFile when empty. A glossary never makes a class
    /// unusable: one that is not there is said in the menu's header and at the run's start.
    /// </summary>
    public const string KeytermsFile = "KeytermsFile";

    /// <summary>The word a camera field holds for any camera: a USB camera when one is plugged in, else the built-in front camera.</summary>
    public const string AnyCamera = "auto";

    /// <summary>Every field this build knows, in the order the file writes them.</summary>
    public static IReadOnlyList<string> All { get; } = [Course, Mode, App, Device, Microphone, MicrophoneFollows, OutputFolder, Transcribe, Camera, CameraResolution, KeytermsFile];
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
/// <param name="KeytermsFile">
/// The class's own glossary as the file writes it, trimmed: a file, or none for no glossary; null for ElevenLabs:KeytermsFile.
/// </param>
public sealed record SavedClass(string Name, string Course, RecordingMode Mode, ClassSource? Source, string? Microphone, FollowRule? Follows, string? OutputFolder,
    bool Transcribe, string? Camera, string? CameraResolution, string? KeytermsFile = null)
{
    /// <summary>The word a glossary's value holds for no glossary, in any case: a saved class's KeytermsFile, ElevenLabs:KeytermsFile and transcribe --keyterms alike.</summary>
    public const string NoGlossary = "none";

    /// <summary>True when <paramref name="value"/>, a glossary's value as written, means no glossary: empty, or <see cref="NoGlossary"/> in any case.</summary>
    public static bool MeansNoGlossary(string? value) => value is null || value.Trim().Length == 0 || value.Trim().Equals(NoGlossary, StringComparison.OrdinalIgnoreCase);

    /// <summary>A glossary's file as words name it: the file's name, or the value as written when it ends in no file's name.</summary>
    public static string GlossaryName(string file)
    {
        var name = file.Trim().Trim('"').Trim();
        var last = name.LastIndexOfAny(['\\', '/']);
        return last >= 0 && last < name.Length - 1 ? name[(last + 1)..] : name;
    }

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
    /// (720p)"; its own glossary, ", glossary mantiq.txt" by the file's name or ", no glossary"; and ", recorded only" for a
    /// class that is not transcribed when started from menu C.
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
        words += KeytermsFile switch
        {
            null => "",
            var none when MeansNoGlossary(none) => ", no glossary",
            var file => $", glossary {GlossaryName(file)}"
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
