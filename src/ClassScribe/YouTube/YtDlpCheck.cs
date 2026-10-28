using System.ComponentModel;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace ClassScribe.YouTube;

/// <summary>
/// youtube check, and step 4 of every download batch (design 3.1): where yt-dlp, Deno, FFmpeg and FFprobe are (see
/// <see cref="ToolLocator"/>), and their versions, from yt-dlp --version, deno --version, ffmpeg -version and ffprobe -version,
/// which ask nothing of the network. A tool missing is a problem, with the winget command that installs it; one that does
/// not answer is a problem too, since Bitdefender may have blocked it. yt-dlp more than 90 days old, by its version, which is
/// its release date, brings a warning with the update command: 90 days is when yt-dlp itself warns. ClassScribe never
/// updates yt-dlp.
/// </summary>
public sealed class YtDlpCheck(ToolLocator locator, IToolRunner tools, YouTubeFolders folders, TimeProvider time, ILogger<YtDlpCheck> logger)
{
    /// <summary>yt-dlp older than this, by its release date, brings a warning.</summary>
    public static readonly TimeSpan OldAfter = TimeSpan.FromDays(90);

    /// <summary>Finds the tools, asks each its version, and says what it found.</summary>
    public async Task<ToolCheck> RunAsync(CancellationToken cancellationToken)
    {
        var found = locator.Find();
        List<string> lines = [];
        List<string> problems = [.. found.Missing];
        Directory.CreateDirectory(folders.Root);

        string? ytDlpVersion = null;
        foreach (var (name, path, arguments) in new (string, string?, string[])[]
                 {
                     ("yt-dlp", found.YtDlp, ["--version"]), ("Deno", found.Deno, ["--version"]), ("FFmpeg", found.Ffmpeg, ["-version"]), ("FFprobe", found.Ffprobe, ["-version"])
                 })
        {
            if (path is null) continue;

            var version = await VersionOfAsync(name, path, arguments, cancellationToken);
            if (version is null)
            {
                problems.Add($"{name} is at {path}, but it did not say its version; Bitdefender may have blocked it (the README says how to allow it), or it is damaged: reinstall it with {ToolSearch.InstallYtDlp}");
                continue;
            }

            if (name == "yt-dlp") ytDlpVersion = version;
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"{name,-8} {version,-12} {path}"));
        }

        var warning = ytDlpVersion is { } current && YtDlpRead.ReleaseDate(current) is { } released
            && DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime).DayNumber - released.DayNumber is var age && age > OldAfter.TotalDays
            ? string.Create(CultureInfo.InvariantCulture,
                $"yt-dlp {current} is {age} days old, and YouTube changes often, so a download may fail: update it with {YtDlpErrors.Update}")
            : null;
        return new ToolCheck(problems.Count == 0 ? found.Paths : null, ytDlpVersion, lines, problems, warning);
    }

    /// <summary>
    /// The version <paramref name="name"/> at <paramref name="path"/> says when asked with <paramref name="arguments"/>: yt-dlp's
    /// first line as it is, Deno's number after "deno", FFmpeg's and FFprobe's word after "version". Null when it cannot be run,
    /// or says none, which is noted at Debug.
    /// </summary>
    private async Task<string?> VersionOfAsync(string name, string path, string[] arguments, CancellationToken cancellationToken)
    {
        ToolResult result;
        try
        {
            result = await tools.RunAsync(new ToolCall(path, arguments, folders.Root), null, cancellationToken);
        }
        catch (Win32Exception ex)
        {
            // Deliberately not rethrown: a tool that cannot be started is the answer this check gives, a problem said in words
            // with what to do; the reason is noted for a look.
            logger.LogDebug(ex, "Could not run {Program} to ask its version", path);
            return null;
        }

        var first = result.Output.FirstOrDefault(line => line.Trim().Length > 0)?.Trim();
        if (result.ExitCode != 0 || first is null)
        {
            logger.LogDebug("{Program} said no version (exit code {ExitCode}): {Lines}", path, result.ExitCode, string.Join(" | ", result.Errors));
            return null;
        }

        var words = first.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return name switch
        {
            "yt-dlp" => first,
            "Deno" => words.Length > 1 ? words[1] : first,
            _ => Array.IndexOf(words, "version") is var at and >= 0 && at + 1 < words.Length ? words[at + 1] : first
        };
    }
}
