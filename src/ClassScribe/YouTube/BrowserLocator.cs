using System.Security;
using ClassScribe.Desktop;
using ClassScribe.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClassScribe.YouTube;

/// <summary>
/// Finds Chrome for the record path's video window (design 4.1): the chrome.exe YouTube:BrowserPath names, and nothing else
/// when it names one; else the (Default) value of Chrome's App Paths key, which Windows' Run box reads too, under
/// HKEY_LOCAL_MACHINE for a machine install and then under HKEY_CURRENT_USER for one of this user's; else Program Files,
/// Program Files (x86) and %LOCALAPPDATA%, each followed by Google\Chrome\Application\chrome.exe. The registry and the disk
/// are read through seams, so that no test looks at the real ones. It lives as long as the app, and reads the setting at each
/// search. The adapter of <see cref="IToolLocator"/>.
/// </summary>
public sealed class BrowserLocator(IRegistryValues registry, IFileProbe files, IOptionsMonitor<YouTubeOptions> options, ILogger<BrowserLocator> logger) : IToolLocator
{
    /// <summary>Chrome's App Paths key, under HKEY_LOCAL_MACHINE or HKEY_CURRENT_USER, whose (Default) value is its chrome.exe.</summary>
    public const string AppPathsKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe";

    /// <inheritdoc />
    string? IToolLocator.FindBrowser() => Find();

    /// <inheritdoc />
    string IToolLocator.BrowserVersionOf(string chrome) => VersionOf(chrome);

    /// <summary>Where Chrome keeps chrome.exe below each of the folders it is installed in.</summary>
    private static readonly string InstallFolder = Path.Combine("Google", "Chrome", "Application", "chrome.exe");

    /// <summary>The Program Files folder; a test gives its own.</summary>
    internal string ProgramFiles { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolderOption.DoNotVerify);

    /// <summary>The Program Files (x86) folder; a test gives its own.</summary>
    internal string ProgramFilesX86 { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolderOption.DoNotVerify);

    /// <summary>This user's local application data folder, where a per-user Chrome lives; a test gives its own.</summary>
    internal string LocalAppData { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify);

    /// <summary>Chrome's chrome.exe, as a full path; null when it is nowhere to be found (see <see cref="IToolLocator.BrowserNotFound"/>).</summary>
    public string? Find()
    {
        var setting = SettingPaths.Expanded(options.CurrentValue.BrowserPath);
        if (setting.Length > 0)
        {
            var given = IsExe(setting) && files.FileExists(setting) ? setting : null;
            logger.LogDebug("Chrome, as YouTube:BrowserPath names it: {Path}", given ?? "not there");
            return given;
        }

        var found = AppPaths().Concat(InstallFolders()).FirstOrDefault(candidate => IsExe(candidate) && files.FileExists(candidate));
        logger.LogDebug("Looked for Chrome: {Path}", found ?? "not found");
        return found;
    }

    /// <summary>Chrome's version, as its file gives it, for the video's source file: "Chrome 141.0.7390.108", or "Chrome" when the file does not say.</summary>
    public string VersionOf(string chrome) => files.VersionOf(chrome) is { } version ? $"Chrome {version}" : "Chrome";

    /// <summary>
    /// The (Default) value of Chrome's App Paths key, the machine's then the user's, quotes and spaces around it gone; a value
    /// that is not there, or a registry that cannot be read, is passed over, the second said at Debug.
    /// </summary>
    private IEnumerable<string> AppPaths()
    {
        foreach (var (read, where) in new (Func<string?>, string)[]
                 {
                     (() => registry.ReadLocalMachineString(AppPathsKey, ""), "the machine's"),
                     (() => registry.ReadString(AppPathsKey, ""), "the user's")
                 })
        {
            string? value;
            try
            {
                value = read();
            }
            catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
            {
                // Deliberately not rethrown: App Paths is one of several places Chrome is looked for; one that cannot be read
                // is passed over, and Chrome found nowhere is said with what to do.
                logger.LogDebug(ex, "Could not read {Where} App Paths for Chrome; it is looked for without them", where);
                continue;
            }

            if (SettingPaths.Expanded(value) is { Length: > 0 } path) yield return path;
        }
    }

    /// <summary>Chrome's chrome.exe in each folder it is installed in: Program Files, Program Files (x86), then %LOCALAPPDATA%.</summary>
    private IEnumerable<string> InstallFolders() =>
        new[] { ProgramFiles, ProgramFilesX86, LocalAppData }.Where(folder => !string.IsNullOrEmpty(folder)).Select(folder => Path.Combine(folder, InstallFolder));

    /// <summary>True for a full path to an .exe, without a character no path can hold.</summary>
    private static bool IsExe(string path) =>
        path.IndexOfAny(Path.GetInvalidPathChars()) < 0 && Path.IsPathFullyQualified(path) && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
}
