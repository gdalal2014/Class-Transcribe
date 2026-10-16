namespace ClassScribe.Commands;

/// <summary>
/// Command lines the app prints for the person to run later, such as to try a transcript again or to rebuild one for
/// free. Each names this very program by its full path, so that it runs as printed from any folder: a bare
/// "ClassScribe" is not a command, neither for the published exe nor under dotnet run.
/// </summary>
public static class CommandLine
{
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
        Command(processPath, appFolder, again ? $"transcribe \"{path}\" --again" : $"transcribe \"{path}\"");

    /// <summary>
    /// The PowerShell command that rebuilds the transcript of <paramref name="path"/> with this very program, for free,
    /// from ElevenLabs' saved answer: a recording's folder, one of its files, or the answer itself.
    /// </summary>
    public static string Rebuild(string path) => Rebuild(path, Environment.ProcessPath, AppContext.BaseDirectory);

    /// <summary>The same for the program at <paramref name="processPath"/>, whose files are in <paramref name="appFolder"/>.</summary>
    public static string Rebuild(string path, string? processPath, string appFolder) => Command(processPath, appFolder, $"rebuild \"{path}\"");

    /// <summary>
    /// The program at <paramref name="processPath"/> followed by <paramref name="arguments"/>, as PowerShell runs it. A
    /// process that is dotnet itself, or one whose path is not known, runs the app's dll in <paramref name="appFolder"/>
    /// with dotnet.
    /// </summary>
    private static string Command(string? processPath, string appFolder, string arguments) =>
        processPath is null || Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? $"dotnet \"{Path.Combine(appFolder, "ClassScribe.dll")}\" {arguments}"
            : $"& \"{processPath}\" {arguments}";
}
