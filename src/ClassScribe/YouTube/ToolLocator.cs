using System.Security;
using ClassScribe.Desktop;
using ClassScribe.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClassScribe.YouTube;

/// <summary>What a search for the tools found: each one's full path, or null when it was not found.</summary>
/// <param name="YtDlp">yt-dlp.exe.</param>
/// <param name="Deno">deno.exe.</param>
/// <param name="Ffmpeg">ffmpeg.exe, from a folder that holds ffprobe.exe too.</param>
/// <param name="Ffprobe">ffprobe.exe, beside ffmpeg.exe.</param>
internal sealed record ToolSearch(string? YtDlp, string? Deno, string? Ffmpeg, string? Ffprobe)
{
    /// <summary>The winget command that installs yt-dlp, which brings Deno and FFmpeg with it.</summary>
    public const string InstallYtDlp = "winget install --source winget --id yt-dlp.yt-dlp";

    /// <summary>The paths a download is given; null while any tool is missing.</summary>
    public ToolPaths? Paths => YtDlp is { } ytDlp && Deno is { } deno && Ffmpeg is { } ffmpeg && Ffprobe is not null
        ? new ToolPaths(ytDlp, deno, Path.GetDirectoryName(ffmpeg)!)
        : null;

    /// <summary>Each tool not found, where it was looked for, and the winget command that installs it.</summary>
    public IReadOnlyList<string> Missing =>
    [
        .. YtDlp is null ? [$"yt-dlp was not found (YouTube:YtDlpPath, winget's links folder, PATH). Install it: {InstallYtDlp}"] : Array.Empty<string>(),
        .. Deno is null ? ["Deno was not found (beside yt-dlp, winget's links folder, PATH). Install it: winget install --source winget --id DenoLand.Deno"] : Array.Empty<string>(),
        .. Ffmpeg is null ? ["FFmpeg and FFprobe were not found in one folder (beside yt-dlp, winget's links folder, PATH). Install them: winget install --source winget --id yt-dlp.FFmpeg"] : Array.Empty<string>()
    ];
}

/// <summary>
/// Finds yt-dlp, Deno, FFmpeg and FFprobe (design 3.1), which ClassScribe neither ships nor installs. yt-dlp: the file
/// YouTube:YtDlpPath names; else winget's links folder, %LOCALAPPDATA%\Microsoft\WinGet\Links, where winget puts the
/// commands of portable packages; else each folder of PATH as the registry holds it now, the user's before the machine's, so
/// that a window opened before winget changed PATH still finds it. Deno, and FFmpeg with FFprobe in one folder: beside
/// yt-dlp first, then the same places. The registry and the disk are read through seams, so that no test looks at the real
/// ones. It lives as long as the app, and reads the setting at each search.
/// </summary>
public sealed class ToolLocator(IRegistryValues registry, IFileProbe files, IOptionsMonitor<YouTubeOptions> options, ILogger<ToolLocator> logger)
{
    /// <summary>The key, under HKEY_CURRENT_USER, of the user's environment variables, PATH among them.</summary>
    public const string UserPathKey = "Environment";

    /// <summary>The key, under HKEY_LOCAL_MACHINE, of the machine's environment variables, PATH among them.</summary>
    public const string MachinePathKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";

    /// <summary>This user's local application data folder, under which winget keeps its links; a test gives its own.</summary>
    internal string LocalAppData { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify);

    /// <summary>winget's links folder.</summary>
    public string LinksFolder => Path.Combine(LocalAppData, "Microsoft", "WinGet", "Links");

    /// <summary>Looks for the four tools, as the class summary says, and says what it found.</summary>
    internal ToolSearch Find()
    {
        List<string> elsewhere = [LinksFolder, .. PathFolders()];
        var setting = YouTubeOptions.Expanded(options.CurrentValue.YtDlpPath);
        var ytDlp = setting.Length > 0
            ? files.FileExists(setting) ? setting : null
            : First(elsewhere, "yt-dlp.exe");

        List<string> folders = [.. ytDlp is null ? [] : new[] { Path.GetDirectoryName(ytDlp)! }, .. elsewhere];
        var deno = First(folders, "deno.exe");
        var ffmpegFolder = folders.FirstOrDefault(folder => files.FileExists(Path.Combine(folder, "ffmpeg.exe")) && files.FileExists(Path.Combine(folder, "ffprobe.exe")));
        var found = new ToolSearch(ytDlp, deno, ffmpegFolder is null ? null : Path.Combine(ffmpegFolder, "ffmpeg.exe"), ffmpegFolder is null ? null : Path.Combine(ffmpegFolder, "ffprobe.exe"));
        logger.LogDebug("Looked for yt-dlp and its helpers: yt-dlp {YtDlp}, Deno {Deno}, FFmpeg {Ffmpeg}", found.YtDlp ?? "not found", found.Deno ?? "not found", found.Ffmpeg ?? "not found");
        return found;
    }

    /// <summary>The first of <paramref name="folders"/> that holds <paramref name="fileName"/>, as a full path; null when none does.</summary>
    private string? First(IEnumerable<string> folders, string fileName) =>
        folders.Select(folder => Path.Combine(folder, fileName)).FirstOrDefault(files.FileExists);

    /// <summary>
    /// The folders of PATH as the registry holds it now, the user's then the machine's, each with its variables expanded and
    /// the quotes and spaces around it gone; blanks and what cannot be a folder are passed over. A registry that cannot be
    /// read leaves its PATH out, which is said at Debug.
    /// </summary>
    private IEnumerable<string> PathFolders()
    {
        List<string> folders = [];
        foreach (var (read, where) in new (Func<string?>, string)[]
                 {
                     (() => registry.ReadString(UserPathKey, "Path"), "the user's"),
                     (() => registry.ReadLocalMachineString(MachinePathKey, "Path"), "the machine's")
                 })
        {
            string? path;
            try
            {
                path = read();
            }
            catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
            {
                // Deliberately not rethrown: PATH is one of several places the tools are looked for; one that cannot be read
                // is passed over, and a tool not found anywhere is said with the command that installs it.
                logger.LogDebug(ex, "Could not read {Where} PATH; the tools are looked for without it", where);
                continue;
            }

            foreach (var entry in (path ?? "").Split(';'))
            {
                var folder = Environment.ExpandEnvironmentVariables(entry).Trim().Trim('"').Trim();
                if (folder.Length > 0 && folder.IndexOfAny(Path.GetInvalidPathChars()) < 0 && Path.IsPathFullyQualified(folder)) folders.Add(folder);
            }
        }

        return folders;
    }
}
