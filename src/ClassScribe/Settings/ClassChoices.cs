using ClassScribe.Audio;

namespace ClassScribe.Settings;

/// <summary>Which option named a class's source: --app, --device or --in-person.</summary>
public enum SourceOption
{
    /// <summary>--app: one app's sound, by its process name.</summary>
    App,

    /// <summary>--device: one output device by part of its name, or auto.</summary>
    Device,

    /// <summary>--in-person: the microphone alone, with part of its name or nothing.</summary>
    InPerson
}

/// <summary>A source given on the command line: the option, and its value, null for --in-person without a microphone's name.</summary>
/// <param name="Option">Which option.</param>
/// <param name="Value">Its value.</param>
public sealed record SourceGiven(SourceOption Option, string? Value);

/// <summary>
/// The options class add and class set take, and those given with record --class over a saved class (layout-config 4.4),
/// applied to a class's fields in groups, so that a run or a class never ends up half one class and half another: a source
/// (--app, --device or --in-person) replaces the class's whole source, its mode and the app its microphone follows; --mic
/// replaces its microphone, and on a class without one adds it; --mic-follows replaces what it follows, without --mic when
/// the class records the microphone beside the class; --camera and --resolution replace the camera and its size, in person
/// only; --output, --course, --record-only and --transcribe set their fields; --clear empties a field without an empty
/// string, which Windows PowerShell 5.1 would drop.
/// </summary>
public sealed record ClassChoices
{
    /// <summary>The words --clear takes, and the field each empties: camera empties the size too.</summary>
    public static IReadOnlyDictionary<string, string> ClearWords { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["output"] = ClassFields.OutputFolder,
        ["microphone"] = ClassFields.Microphone,
        ["follows"] = ClassFields.MicrophoneFollows,
        ["camera"] = ClassFields.Camera,
        ["resolution"] = ClassFields.CameraResolution
    };

    /// <summary>The recording's name, --course, or the name given with record --class; null to keep the class's.</summary>
    public string? Course { get; init; }

    /// <summary>The source given; null to keep the class's.</summary>
    public SourceGiven? Source { get; init; }

    /// <summary>True when --mic was given.</summary>
    public bool Mic { get; init; }

    /// <summary>
    /// True to record the class alone, without your microphone, as menu C's editor answers it: the microphone and the app it
    /// followed go, and a class in person is refused, since in person the microphone is the source.
    /// </summary>
    public bool ClassAlone { get; init; }

    /// <summary>The microphone --mic named; null for Recording:Microphone and Windows' default.</summary>
    public string? MicName { get; init; }

    /// <summary>The app --mic-follows named, or auto or none; null to keep the class's.</summary>
    public string? Follows { get; init; }

    /// <summary>True when --camera was given.</summary>
    public bool Camera { get; init; }

    /// <summary>The camera --camera named; null for any camera.</summary>
    public string? CameraName { get; init; }

    /// <summary>The picture's size --resolution gave; null to keep the class's.</summary>
    public string? Resolution { get; init; }

    /// <summary>The recordings folder --output gave, as a full path; null to keep the class's.</summary>
    public string? Output { get; init; }

    /// <summary>False for --record-only, true for --transcribe; null to keep the class's.</summary>
    public bool? Transcribe { get; init; }

    /// <summary>The fields --clear empties, by their words (see <see cref="ClearWords"/>).</summary>
    public IReadOnlyList<string> Clear { get; init; } = [];

    /// <summary>
    /// Applies the choices to <paramref name="fields"/>, a class's fields by name in any case, as the class says, the clears
    /// first; returns what keeps them from going together on this class, in words, or null.
    /// </summary>
    public string? ApplyTo(Dictionary<string, string?> fields)
    {
        foreach (var word in Clear)
        {
            if (!ClearWords.TryGetValue(word, out var field)) return $"--clear takes {SettingsCatalog.Listed([.. ClearWords.Keys])}, not \"{word}\"";
            fields.Remove(field);
            if (field == ClassFields.Camera) fields.Remove(ClassFields.CameraResolution);
        }

        if (Course is not null) fields[ClassFields.Course] = Course;
        var mode = ModeOf(fields);
        if (Source is { } source)
        {
            fields.Remove(ClassFields.MicrophoneFollows);
            if (source.Option == SourceOption.InPerson)
            {
                fields.Remove(ClassFields.App);
                fields.Remove(ClassFields.Device);
                mode = Set(fields, RecordingMode.Microphone);
                if (source.Value is { } microphone) fields[ClassFields.Microphone] = microphone;
            }
            else
            {
                fields.Remove(source.Option == SourceOption.App ? ClassFields.Device : ClassFields.App);
                fields[source.Option == SourceOption.App ? ClassFields.App : ClassFields.Device] = source.Value;
                if (mode == RecordingMode.Microphone)
                {
                    fields.Remove(ClassFields.Microphone);
                    fields.Remove(ClassFields.Camera);
                    fields.Remove(ClassFields.CameraResolution);
                    mode = Set(fields, RecordingMode.Class);
                }
            }
        }

        if (ClassAlone)
        {
            if (mode == RecordingMode.Microphone) return "a class in person records your microphone alone, so it cannot be recorded without it";
            fields.Remove(ClassFields.Microphone);
            fields.Remove(ClassFields.MicrophoneFollows);
            mode = Set(fields, RecordingMode.Class);
        }

        if (Mic)
        {
            if (mode == RecordingMode.Microphone)
                return "--mic adds your microphone beside the class; this class is in person, where the microphone is already the source: give --in-person \"<microphone>\" to change it";
            mode = Set(fields, RecordingMode.ClassAndMicrophone);
            if (MicName is null) fields.Remove(ClassFields.Microphone);
            else fields[ClassFields.Microphone] = MicName;
        }

        if (Follows is not null)
        {
            if (mode != RecordingMode.ClassAndMicrophone) return "--mic-follows goes with a class that records your microphone beside the class: add --mic";
            fields[ClassFields.MicrophoneFollows] = Follows;
        }

        if (Camera)
        {
            if (mode != RecordingMode.Microphone) return "--camera records a lesson's picture only in person: give it with --in-person";
            fields[ClassFields.Camera] = CameraName ?? ClassFields.AnyCamera;
        }

        if (Resolution is not null)
        {
            if (!fields.ContainsKey(ClassFields.Camera)) return "--resolution needs --camera, or a class that records a camera";
            fields[ClassFields.CameraResolution] = Resolution;
        }

        if (Output is not null) fields[ClassFields.OutputFolder] = Output;
        if (Transcribe is { } transcribe) fields[ClassFields.Transcribe] = transcribe ? "true" : "false";
        return null;
    }

    /// <summary>The mode <paramref name="fields"/> hold: their Mode in any case, Class when it is empty or not one.</summary>
    private static RecordingMode ModeOf(Dictionary<string, string?> fields) =>
        fields.TryGetValue(ClassFields.Mode, out var text) && Enum.GetNames<RecordingMode>().FirstOrDefault(name => name.Equals(text?.Trim(), StringComparison.OrdinalIgnoreCase)) is { } known
            ? Enum.Parse<RecordingMode>(known)
            : RecordingMode.Class;

    /// <summary>Writes <paramref name="mode"/> into <paramref name="fields"/> and returns it.</summary>
    private static RecordingMode Set(Dictionary<string, string?> fields, RecordingMode mode)
    {
        fields[ClassFields.Mode] = mode.ToString();
        return mode;
    }
}
