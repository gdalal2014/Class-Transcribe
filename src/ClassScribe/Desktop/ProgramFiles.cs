namespace ClassScribe.Desktop;

/// <summary>
/// The names of ClassScribe's program and its files, the same however it was started: the window, the recorder
/// (--recorder) and the camera process (--camera-process) all run ClassScribe.exe, or under dotnet ClassScribe.dll.
/// </summary>
public static class ProgramFiles
{
    /// <summary>The name of ClassScribe's program, which is ClassScribe's own however it was started.</summary>
    public const string Name = "ClassScribe";

    /// <summary>ClassScribe's program file: ClassScribe.exe.</summary>
    public const string Exe = Name + ".exe";

    /// <summary>The file name of ClassScribe's dll, which dotnet runs: ClassScribe.dll.</summary>
    public const string Dll = Name + ".dll";
}