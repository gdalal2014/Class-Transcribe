namespace ClassScribe.Commands;

/// <summary>Command lines the app prints for the person to run later, such as to try a transcript again.</summary>
public static class CommandLine
{
    /// <summary>
    /// The PowerShell command that transcribes <paramref name="path"/> with this very program, named by
    /// its full path, so that it runs as printed from any folder: a bare "ClassScribe" is not a command,
    /// neither for the published exe nor under dotnet run.
    /// </summary>
    public static string Transcribe(string path) => Transcribe(path, Environment.ProcessPath, AppContext.BaseDirectory);

    /// <summary>
    /// The same for the program at <paramref name="processPath"/>, whose files are in
    /// <paramref name="appFolder"/>. A process that is dotnet itself, as with "dotnet ClassScribe.dll",
    /// runs the app's dll from that folder.
    /// </summary>
    public static string Transcribe(string path, string? processPath, string appFolder) =>
        processPath is null || Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? $"dotnet \"{Path.Combine(appFolder, "ClassScribe.dll")}\" transcribe \"{path}\""
            : $"& \"{processPath}\" transcribe \"{path}\"";
}
