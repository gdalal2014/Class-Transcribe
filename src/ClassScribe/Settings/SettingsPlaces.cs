using ClassScribe.Recorder;
using ClassScribe.Storage;

namespace ClassScribe.Settings;

/// <summary>Which settings file ClassScribe reads now: the user's own, the one beside the program, or none while the user's own is missing.</summary>
public enum SettingsOrigin
{
    /// <summary>The user's own file, in the user's folder: %APPDATA%\ClassScribe, unless CLASSSCRIBE_SETTINGS_FOLDER names another.</summary>
    UserFolder,

    /// <summary>The file beside the program: the shipped defaults once installed, the build's copy under dotnet run, or a portable copy's own.</summary>
    BesideTheProgram,

    /// <summary>
    /// ClassScribe made the user's own file once (the marker says so), and it is not there now: deleted or renamed by
    /// mistake, or on a redirected %APPDATA% that is offline. The defaults beside the program are read for what is done now,
    /// with a warning, and no change is written, since a new file would hide the loss (layout-config 1.4).
    /// </summary>
    Missing
}

/// <summary>The settings file read now, as <see cref="SettingsPlaces.Current"/> decides it.</summary>
/// <param name="Origin">Which file it is.</param>
/// <param name="Path">The file the person knows as theirs: the user's own file, even while it is missing, or the one beside the program.</param>
/// <param name="ReadPath">The file whose values are read: the user's own, or the one beside the program, which stands in for a missing user file.</param>
public sealed record SettingsSource(SettingsOrigin Origin, string Path, string ReadPath);

/// <summary>
/// Where the settings and the glossary are read from, decided at every read of the settings (layout-config 1.1): the user's
/// own appsettings.json when it exists; otherwise, when ClassScribe has made one before (its marker, settings-home.txt, is
/// there), the missing-file state; otherwise the file beside the program. A process that has read the user's file never
/// goes back to the file beside the program: should that file go while ClassScribe runs, the settings stay as they were
/// (see <see cref="SettingsReloader"/>). So a file made by one window is read by every other window at its next choice.
/// The user's folder is %APPDATA%\ClassScribe (<see cref="RoamingFolder"/>), kept apart from the program, which an update
/// replaces whole; the marker and the writer's lock are in ClassScribe's own %LOCALAPPDATA%\ClassScribe
/// (<see cref="RuntimeFolder"/>). CLASSSCRIBE_SETTINGS_FOLDER, which a dry run sets in its own process, moves all of them,
/// the user's file, the marker, the lock and the file before the last change, into the folder it names. ClassScribe writes
/// the user's file only through its settings writer, at the first change.
/// </summary>
public sealed class SettingsPlaces
{
    /// <summary>The name of the user's folder under the roaming application data folder.</summary>
    public const string FolderName = "ClassScribe";

    /// <summary>The environment variable that moves the user's settings into a folder of its own, for a dry run (build-plan amendment 16).</summary>
    public const string FolderVariable = "CLASSSCRIBE_SETTINGS_FOLDER";

    /// <summary>The marker ClassScribe writes once it has made the user's file: it holds the file's full path.</summary>
    public const string MarkerFileName = "settings-home.txt";

    /// <summary>The lock the settings writer holds while it changes the file, one change at a time.</summary>
    public const string LockFileName = "settings.lock";

    /// <summary>The user's file as it was before the last change, one undo, beside it.</summary>
    public const string PreviousFileName = "appsettings.previous.json";

    /// <summary>Whether this process has read the user's own file: from then on it never goes back to the file beside the program.</summary>
    private volatile bool _userFileRead;

    /// <summary>
    /// The places with <paramref name="programFolder"/> as the program's own, <paramref name="userFolder"/> as the user's,
    /// or null for none at all, as in most tests, where the file beside the program is always read; and
    /// <paramref name="stateFolder"/> for the marker and the writer's lock, or null to keep them beside the user's file.
    /// <paramref name="overridden"/> says the user's folder came from <see cref="FolderVariable"/>, which the menu names.
    /// Nothing is looked at or made here.
    /// </summary>
    public SettingsPlaces(string programFolder, string? userFolder, string? stateFolder = null, bool overridden = false)
    {
        ProgramFolder = Normalize(programFolder);
        UserFolder = userFolder is null ? null : Normalize(userFolder);
        StateFolder = UserFolder is null ? null : stateFolder is null ? UserFolder : Normalize(stateFolder);
        IsOverridden = overridden && UserFolder is not null;
    }

    /// <summary>
    /// The user's folder for this user: ClassScribe under the roaming application data folder (%APPDATA%), where Windows
    /// keeps a user's settings. Only the path: nothing is read.
    /// </summary>
    public static string UserFolderForThisUser => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), FolderName);

    /// <summary>The folder the program runs from: the exe's own, whatever folder it was started from.</summary>
    public string ProgramFolder { get; }

    /// <summary>The user's folder, whether or not it holds a settings file; null when there is none, as in most tests.</summary>
    public string? UserFolder { get; }

    /// <summary>Where the marker and the writer's lock are: %LOCALAPPDATA%\ClassScribe, or the override's folder; null without a user folder.</summary>
    public string? StateFolder { get; }

    /// <summary>True when <see cref="FolderVariable"/> named the user's folder, as in a dry run.</summary>
    public bool IsOverridden { get; }

    /// <summary>The appsettings.json beside the program.</summary>
    public string ProgramFile => Path.Combine(ProgramFolder, SettingsFile.FileName);

    /// <summary>The user's own appsettings.json, whether or not it is there; null without a user folder.</summary>
    public string? UserFile => UserFolder is null ? null : Path.Combine(UserFolder, SettingsFile.FileName);

    /// <summary>The user's file as it was before the last change; null without a user folder.</summary>
    public string? PreviousFile => UserFolder is null ? null : Path.Combine(UserFolder, PreviousFileName);

    /// <summary>The marker ClassScribe writes once it has made the user's file; null without a user folder.</summary>
    public string? MarkerFile => StateFolder is null ? null : Path.Combine(StateFolder, MarkerFileName);

    /// <summary>The lock the settings writer holds; null without a user folder.</summary>
    public string? LockFile => StateFolder is null ? null : Path.Combine(StateFolder, LockFileName);

    /// <summary>Which file is read now (see <see cref="Current"/>).</summary>
    public SettingsOrigin Origin => Current().Origin;

    /// <summary>The settings file the person knows as theirs now (see <see cref="SettingsSource.Path"/>).</summary>
    public string SettingsPath => Current().Path;

    /// <summary>The folder whose appsettings.json is read now: the user's, or the program's, which also stands in for a missing user file.</summary>
    public string Folder => Path.GetDirectoryName(Current().ReadPath)!;

    /// <summary>True when ClassScribe has made the user's file before: its marker is there.</summary>
    public bool HasMarker => MarkerFile is { } marker && File.Exists(marker);

    /// <summary>
    /// The places for this user: the program's folder given; the user's folder from <see cref="FolderVariable"/> when it is
    /// set, with the marker and the lock beside the file, else %APPDATA%\ClassScribe with them in %LOCALAPPDATA%\ClassScribe.
    /// Without a roaming application data folder there is no user folder, and the file beside the program is read. In a test
    /// run the real folders are refused (see <see cref="RoamingFolder.RefuseTheRealFolderSwitch"/>), so no test can read them.
    /// </summary>
    public static SettingsPlaces ForThisUser(string programFolder)
    {
        var overrideFolder = Environment.GetEnvironmentVariable(FolderVariable);
        if (!string.IsNullOrWhiteSpace(overrideFolder))
            return new SettingsPlaces(programFolder, Path.GetFullPath(Environment.ExpandEnvironmentVariables(overrideFolder.Trim().Trim('"'))), stateFolder: null, overridden: true);

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify);
        return appData.Length == 0
            ? new SettingsPlaces(programFolder, userFolder: null)
            : new SettingsPlaces(programFolder, new RoamingFolder().Root, new RuntimeFolder().Root);
    }

    /// <summary>
    /// Which file is read now: the user's own when it exists, or when this process has read it before (it is then gone,
    /// which the reloader refuses rather than going back to the file beside the program); the missing-file state when the
    /// marker says ClassScribe made one; else the file beside the program.
    /// </summary>
    public SettingsSource Current()
    {
        if (UserFile is { } user)
        {
            if (File.Exists(user))
            {
                _userFileRead = true;
                return new SettingsSource(SettingsOrigin.UserFolder, user, user);
            }

            if (_userFileRead) return new SettingsSource(SettingsOrigin.UserFolder, user, user);
            if (HasMarker) return new SettingsSource(SettingsOrigin.Missing, user, ProgramFile);
        }

        return new SettingsSource(SettingsOrigin.BesideTheProgram, ProgramFile, ProgramFile);
    }

    /// <summary>
    /// Writes the marker, holding the user's file's full path, once ClassScribe has made that file, so that a file that goes
    /// missing later is noticed rather than quietly replaced by the defaults. Throws as a file write does.
    /// </summary>
    public void MarkUserFileMade()
    {
        if (MarkerFile is not { } marker || UserFile is not { } user) return;

        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        File.WriteAllText(marker, user + Environment.NewLine);
    }

    /// <summary>
    /// Where a file a setting names is, such as the glossary of ElevenLabs:KeytermsFile or of a saved class: environment
    /// variables such as %APPDATA% expanded, quotes around the path ignored, and a relative path taken from the folder of the
    /// settings in force (<see cref="Folder"/>) and from nowhere else (architecture 8.2, item 3): the user's folder once the
    /// user's file is read, the program's while none has been made or while it is missing. Never beside the program otherwise,
    /// where an older build's keyterms.txt may be left over. An empty setting gives an empty path, which means no file was wanted.
    /// </summary>
    public string ResolveFile(string configured) => ResolveFile(configured, Folder);

    /// <summary>
    /// Where a file a setting names is, as <see cref="ResolveFile(string)"/> says, with a relative path taken from
    /// <paramref name="folder"/>: the user's folder for a value the settings writer is about to write there, at its first
    /// change too, before the user's file is read.
    /// </summary>
    public static string ResolveFile(string configured, string folder)
    {
        var path = Environment.ExpandEnvironmentVariables(configured).Trim().Trim('"').Trim();
        return path.Length == 0 ? "" : Path.GetFullPath(path, folder);
    }

    /// <summary>
    /// What the menu's header adds to the file's name while the file beside the installed program is read because the user
    /// has no file of their own yet: that these are the defaults, and where ClassScribe makes the user's own file at the
    /// first change. Null when the user's file is read, while it is missing (which has its own line, see
    /// <see cref="DescribeMissing"/>), when there is no user folder, and under a build's output (dotnet run), where the copy
    /// beside the program is the one meant and the stale-copy warning does the pointing out.
    /// </summary>
    public string? DescribeTemplateInUse()
    {
        if (Origin != SettingsOrigin.BesideTheProgram || UserFile is not { } user || IsBuildOutput(ProgramFolder)) return null;

        return $"the defaults; your own file, {user}, is made at your first change in S or C, or with settings set";
    }

    /// <summary>
    /// What is said while the user's file is missing (layout-config 1.4): the menu's header shows it as an error, every
    /// command's first line as a warning, and every change is refused with it. Null while it is not missing.
    /// </summary>
    public string? DescribeMissing() =>
        Origin == SettingsOrigin.Missing && UserFile is { } user
            ? $"Your settings file {user} is missing. If it was moved or deleted by mistake, put it back ({PreviousFileName} beside it is the version before your last change). To start again from the defaults: ClassScribe settings reset --all."
            : null;

    /// <summary>
    /// True for a build's output folder, &lt;project&gt;\bin\&lt;configuration&gt;\&lt;framework&gt;, as dotnet run leaves it:
    /// the folder's name alone decides, since nothing is installed under a bin folder.
    /// </summary>
    private static bool IsBuildOutput(string folder) =>
        new DirectoryInfo(folder).Parent?.Parent is { } bin && bin.Name.Equals("bin", StringComparison.OrdinalIgnoreCase);

    /// <summary>The folder as a full path without a trailing separator, so that the paths made from it read the same however it was given.</summary>
    private static string Normalize(string folder) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
}
