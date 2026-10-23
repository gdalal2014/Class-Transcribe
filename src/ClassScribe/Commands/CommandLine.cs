using System.Text;
using ClassScribe.Audio;

namespace ClassScribe.Commands;

/// <summary>
/// Command lines the app prints for the person to run later, such as to try a transcript again, to rebuild one for free,
/// or to continue a recording that ended unexpectedly. Each names this very program by its full path, so that it runs as
/// printed from any folder: a bare "ClassScribe" is not a command, neither for the published exe nor under dotnet run.
/// Each value is put in PowerShell's double quotes, with the characters that mean something there escaped, so that it
/// reaches ClassScribe as it is.
/// </summary>
public static class CommandLine
{
    /// <summary>The left double quotation mark, U+201C, which PowerShell takes for a straight double quote.</summary>
    private const char LeftDoubleQuote = (char)0x201C;

    /// <summary>The right double quotation mark, U+201D, which PowerShell takes for a straight double quote.</summary>
    private const char RightDoubleQuote = (char)0x201D;

    /// <summary>The double low-9 quotation mark, U+201E, as German opens a quotation, which PowerShell takes for a straight double quote.</summary>
    private const char LowDoubleQuote = (char)0x201E;

    /// <summary>
    /// The PowerShell command that transcribes <paramref name="path"/> with this very program. With
    /// <paramref name="again"/>, it ends with --again, which a recording that already has a transcript needs, since
    /// transcribing it again is billed again.
    /// </summary>
    public static string Transcribe(string path, bool again = false) => Transcribe(path, Environment.ProcessPath, AppContext.BaseDirectory, again);

    /// <summary>
    /// The same for the program at <paramref name="processPath"/>, whose files are in
    /// <paramref name="appFolder"/>. A process that is dotnet itself, as with "dotnet ClassScribe.dll",
    /// runs the app's dll from that folder.
    /// </summary>
    public static string Transcribe(string path, string? processPath, string appFolder, bool again = false) =>
        Command(processPath, appFolder, again ? $"transcribe {Quote(path)} --again" : $"transcribe {Quote(path)}");

    /// <summary>
    /// The PowerShell command that rebuilds the transcript of <paramref name="path"/> with this very program, for free,
    /// from ElevenLabs' saved answer: a recording's folder, one of its files, or the answer itself.
    /// </summary>
    public static string Rebuild(string path) => Rebuild(path, Environment.ProcessPath, AppContext.BaseDirectory);

    /// <summary>The same for the program at <paramref name="processPath"/>, whose files are in <paramref name="appFolder"/>.</summary>
    public static string Rebuild(string path, string? processPath, string appFolder) => Command(processPath, appFolder, $"rebuild {Quote(path)}");

    /// <summary>
    /// The PowerShell command that makes a lesson's video again, for free, from its camera pieces and its sound, with this
    /// very program (finish-video): a recording's folder or one of its files.
    /// </summary>
    public static string FinishVideo(string path) => FinishVideo(path, Environment.ProcessPath, AppContext.BaseDirectory);

    /// <summary>The same for the program at <paramref name="processPath"/>, whose files are in <paramref name="appFolder"/>.</summary>
    public static string FinishVideo(string path, string? processPath, string appFolder) => Command(processPath, appFolder, $"finish-video {Quote(path)}");

    /// <summary>The PowerShell command that sets the setting <paramref name="key"/> to <paramref name="value"/> in the user's own settings file, with this very program.</summary>
    public static string SettingsSet(string key, string value) => SettingsSet(key, value, Environment.ProcessPath, AppContext.BaseDirectory);

    /// <summary>The same for the program at <paramref name="processPath"/>, whose files are in <paramref name="appFolder"/>.</summary>
    public static string SettingsSet(string key, string value, string? processPath, string appFolder) => Command(processPath, appFolder, $"settings set {key} {Quote(value)}");

    /// <summary>
    /// The PowerShell command that uploads <paramref name="path"/> to Google Drive with this very program: a recording's file,
    /// a folder of recordings, or, when it is null, what waits in the queue.
    /// </summary>
    public static string DriveUpload(string? path) => DriveUpload(path, Environment.ProcessPath, AppContext.BaseDirectory);

    /// <summary>The same for the program at <paramref name="processPath"/>, whose files are in <paramref name="appFolder"/>.</summary>
    public static string DriveUpload(string? path, string? processPath, string appFolder) =>
        Command(processPath, appFolder, path is null ? "drive upload" : $"drive upload {Quote(path)}");

    /// <summary>
    /// The PowerShell command that records <paramref name="links"/> as they play with this very program (the youtube command
    /// without --download), into the course <paramref name="name"/> under <paramref name="outputRoot"/>, going ahead after the
    /// plan (--yes): for a video the download path may not take, and for the rest of a video stopped at its longest.
    /// </summary>
    public static string YouTube(IEnumerable<string> links, string name, string outputRoot) =>
        YouTube(links, name, outputRoot, Environment.ProcessPath, AppContext.BaseDirectory);

    /// <summary>The same for the program at <paramref name="processPath"/>, whose files are in <paramref name="appFolder"/>.</summary>
    public static string YouTube(IEnumerable<string> links, string name, string outputRoot, string? processPath, string appFolder) =>
        Command(processPath, appFolder, $"youtube {string.Join(' ', links.Select(Quote))} --name {Quote(name)} --output {Quote(outputRoot)} --yes");

    /// <summary>
    /// The PowerShell command that records the saved class <paramref name="name"/> as saved with this very program, record
    /// --class (layout-config 4.4), or record-only --class when <paramref name="transcribe"/> is false: what class add, rename
    /// and the menu's C print for a launcher of one line.
    /// </summary>
    public static string RecordClass(string name, bool transcribe = true) => RecordClass(name, transcribe, Environment.ProcessPath, AppContext.BaseDirectory);

    /// <summary>The same for the program at <paramref name="processPath"/>, whose files are in <paramref name="appFolder"/>.</summary>
    public static string RecordClass(string name, bool transcribe, string? processPath, string appFolder) =>
        Command(processPath, appFolder, $"{(transcribe ? "record" : "record-only")} --class {Quote(name)}");

    /// <summary>
    /// The PowerShell command that continues, as its next part, the recording <paramref name="request"/>'s run made, with
    /// this very program: record, or record-only for a run that was not to be transcribed, with the run's name, its source
    /// (and, from phase 3, its microphone, with the app it was chosen to follow for the run; in person, its camera and the
    /// picture's size), its recordings folder,
    /// --no-open when the run was to open nothing, and --continue. Nothing else: --minutes counted from the run's own
    /// start, so the continued part records until stopped.
    /// </summary>
    public static string Record(RecordRequest request) => Record(request, Environment.ProcessPath, AppContext.BaseDirectory);

    /// <summary>The same for the program at <paramref name="processPath"/>, whose files are in <paramref name="appFolder"/>.</summary>
    public static string Record(RecordRequest request, string? processPath, string appFolder)
    {
        var arguments = new StringBuilder(request.Transcribe ? "record " : "record-only ").Append(Quote(request.Name));
        if (request.Source.Mode == RecordingMode.Microphone)
        {
            arguments.Append(" --in-person").Append(Named(request.Source.Microphone));

            // The camera and the size the run resolved are kept, so that the continued part takes the same picture.
            if (request.Source.Camera is { } camera)
            {
                arguments.Append(" --camera");
                if (camera.NamePart is { Length: > 0 } cameraName) arguments.Append(' ').Append(Quote(cameraName));
                if (camera.Resolution is { Length: > 0 } size) arguments.Append(" --resolution ").Append(Quote(size));
            }
        }
        else
        {
            arguments.Append(request.Source.Class switch
            {
                ClassSource.Device device => $" --device {Quote(device.NamePart)}",
                ClassSource.App app => $" --app {Quote(app.ProcessName)}",
                _ => " --device auto"
            });
            if (request.Source.Mode == RecordingMode.ClassAndMicrophone)
            {
                arguments.Append(" --mic").Append(Named(request.Source.Microphone));

                // A rule chosen for the run (--mic-follows, or menu choice 7) is kept; without one the continued part follows the recorded app or the setting, as the run did.
                if (request.Source.Microphone?.Follows is { } follows) arguments.Append(" --mic-follows ").Append(Quote(follows.Word));
            }
        }

        arguments.Append(" --output ").Append(Quote(request.OutputRoot));
        if (!request.Open) arguments.Append(" --no-open");
        return Command(processPath, appFolder, arguments.Append(" --continue").ToString());
    }

    /// <summary>
    /// <paramref name="value"/> in PowerShell's double quotes, with the characters that mean something there escaped by a
    /// backtick: $, which starts a variable; the backtick itself; and the double quotes, which would end the text: the
    /// straight one, and the typographic ones PowerShell takes for it as well (“, ” and „), which a name or a folder copied
    /// from a web page or a document may hold.
    /// </summary>
    private static string Quote(string value)
    {
        var quoted = new StringBuilder(value.Length + 2).Append('"');
        foreach (var character in value)
        {
            if (character is '$' or '`' or '"' or LeftDoubleQuote or RightDoubleQuote or LowDoubleQuote) quoted.Append('`');
            quoted.Append(character);
        }

        return quoted.Append('"').ToString();
    }

    /// <summary>" " and the microphone's name in quotes, when one was given; empty for the default rule.</summary>
    private static string Named(MicrophoneChoice? microphone) => microphone?.NamePart is { Length: > 0 } name ? $" {Quote(name)}" : "";

    /// <summary>
    /// The program at <paramref name="processPath"/> followed by <paramref name="arguments"/>, as PowerShell runs it. A
    /// process that is dotnet itself, or one whose path is not known, runs the app's dll in <paramref name="appFolder"/>
    /// with dotnet.
    /// </summary>
    private static string Command(string? processPath, string appFolder, string arguments) =>
        processPath is null || Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? $"dotnet {Quote(Path.Combine(appFolder, "ClassScribe.dll"))} {arguments}"
            : $"& {Quote(processPath)} {arguments}";
}
