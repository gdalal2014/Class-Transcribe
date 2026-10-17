namespace ClassScribe.Settings;

/// <summary>Which settings file ClassScribe read when it started: the user's own, or the one beside the program.</summary>
public enum SettingsOrigin
{
    /// <summary>The user's own file, in the user's folder: %APPDATA%\ClassScribe once installed.</summary>
    UserFolder,

    /// <summary>The file beside the program: the shipped template once installed, the build's copy under dotnet run, or a portable copy's own.</summary>
    BesideTheProgram
}

/// <summary>
/// Where the settings and the glossary are read from: the user's folder when it holds a settings file at start, else the
/// program's. The choice is made once, here, when ClassScribe starts, so a user file that appears or goes while it runs
/// changes nothing until the next start; a file gone after it was read is refused as before (see <see cref="SettingsReloader"/>).
/// The user's folder keeps the settings and the glossary apart from the program, which an update replaces whole, and apart
/// from ClassScribe's own runtime folder, which an uninstall removes. ClassScribe never writes there: the installer seeds the files.
/// </summary>
public sealed class SettingsPlaces
{
    /// <summary>The name of the user's folder under the roaming application data folder.</summary>
    public const string FolderName = "ClassScribe";

    /// <summary>
    /// Decides, now, which folder is read: <paramref name="userFolder"/> when it holds an appsettings.json, else
    /// <paramref name="programFolder"/>, the program's own. Null for <paramref name="userFolder"/> means there is no user
    /// folder at all, as in the tests, and the program's folder is read as it always was.
    /// </summary>
    public SettingsPlaces(string programFolder, string? userFolder)
    {
        ProgramFolder = Normalize(programFolder);
        UserFolder = userFolder is null ? null : Normalize(userFolder);
        Origin = UserFolder is not null && File.Exists(Path.Combine(UserFolder, SettingsFile.FileName)) ? SettingsOrigin.UserFolder : SettingsOrigin.BesideTheProgram;
    }

    /// <summary>
    /// The user's folder for this user: ClassScribe under the roaming application data folder (%APPDATA%), where Windows
    /// keeps a user's settings. Only the path: nothing is read. Only <see cref="ForThisUser"/>, which Program and the
    /// recorder call, looks into it.
    /// </summary>
    public static string UserFolderForThisUser => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), FolderName);

    /// <summary>The folder the program runs from: the exe's own, whatever folder it was started from.</summary>
    public string ProgramFolder { get; }

    /// <summary>The user's folder, whether or not it holds a settings file; null when there is none, as in the tests.</summary>
    public string? UserFolder { get; }

    /// <summary>Which folder's appsettings.json is read, decided when ClassScribe started.</summary>
    public SettingsOrigin Origin { get; }

    /// <summary>The folder whose appsettings.json is read.</summary>
    public string Folder => Origin == SettingsOrigin.UserFolder ? UserFolder! : ProgramFolder;

    /// <summary>The appsettings.json that is read, in <see cref="Folder"/>.</summary>
    public string SettingsPath => Path.Combine(Folder, SettingsFile.FileName);

    /// <summary>
    /// The places for this user: the program's folder given and the user's folder of <see cref="UserFolderForThisUser"/>.
    /// Without a roaming application data folder there is no user folder, and the program's folder is read.
    /// </summary>
    public static SettingsPlaces ForThisUser(string programFolder)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return new SettingsPlaces(programFolder, appData.Length == 0 ? null : Path.Combine(appData, FolderName));
    }

    /// <summary>
    /// Where a file a setting names is, such as the glossary of ElevenLabs:KeytermsFile: environment variables such as
    /// %USERPROFILE% expanded, quotes around the path ignored, and a relative path taken from the folder whose settings
    /// were read when the file is there, else from the program's folder: the user's own glossary first, the shipped one
    /// second. An empty setting gives an empty path, which means no file was wanted.
    /// </summary>
    public string ResolveFile(string configured)
    {
        var path = Environment.ExpandEnvironmentVariables(configured).Trim().Trim('"').Trim();
        if (path.Length == 0) return "";

        var inSettingsFolder = Path.GetFullPath(path, Folder);
        return Origin == SettingsOrigin.UserFolder && !File.Exists(inSettingsFolder) ? Path.GetFullPath(path, ProgramFolder) : inSettingsFolder;
    }

    /// <summary>
    /// What the menu's header adds to the file's name when the file read is the shipped template beside the installed
    /// program because the user's own copy is missing: that these are the shipped defaults, where the user's copy belongs,
    /// and how to get it back. Null when the user's file is read, when there is no user folder, and under a build's output
    /// (dotnet run), where the copy beside the program is the one meant and the stale-copy warning does the pointing out.
    /// </summary>
    public string? DescribeTemplateInUse()
    {
        if (Origin == SettingsOrigin.UserFolder || UserFolder is null || IsBuildOutput(ProgramFolder)) return null;

        return $"the shipped defaults; your own copy belongs in {Path.Combine(UserFolder, SettingsFile.FileName)}: run Update-ClassScribe.cmd, or copy the file there";
    }

    /// <summary>
    /// True for a build's output folder, &lt;project&gt;\bin\&lt;configuration&gt;\&lt;framework&gt;, as dotnet run leaves it:
    /// the folder's name alone decides, since nothing is installed under a bin folder.
    /// </summary>
    private static bool IsBuildOutput(string folder) =>
        new DirectoryInfo(folder).Parent?.Parent is { } bin && bin.Name.Equals("bin", StringComparison.OrdinalIgnoreCase);

    /// <summary>The folder as a full path without a trailing separator, so that the paths made from it read the same however it was given.</summary>
    private static string Normalize(string folder) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
}
