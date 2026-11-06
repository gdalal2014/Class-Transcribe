namespace ClassScribe.YouTube;

/// <summary>
/// yt-dlp's arguments, built here and nowhere else (design 3.3). Every call but the version's starts with one fixed set:
/// --ignore-config, which keeps out the configuration files beside yt-dlp, in %APPDATA%, the home folder and the working
/// folder, any of which could add cookies; --no-cookies and --no-cookies-from-browser; nothing marked watched; a second
/// between requests; Deno and FFmpeg by their paths; and a cache in ClassScribe's own folder. Never passed, and refused in
/// any list (<see cref="ProblemWith"/>): an option that signs in, hands over cookies, a browser's data or credentials, runs a
/// command, reads more configuration, or updates yt-dlp. --load-info-json takes only a path inside the video's own staging
/// folder, and nothing names ClassScribe's video window's profile, which may hold Google sign-in cookies. A link always comes
/// last, rebuilt from its ID. A download keeps YouTube's own sample rate: FFmpeg is asked for mono alone (-ac 1).
/// </summary>
/// <param name="tools">Where yt-dlp's helpers are.</param>
/// <param name="folders">The feature's folders: the staging folders and the cache.</param>
public sealed class YtDlpArguments(ToolPaths tools, YouTubeFolders folders)
{
    /// <summary>The audio a download takes, best first: m4a (normally 128 kbps AAC at 44.1 kHz), then any AAC, then any audio, then the small combined format.</summary>
    public const string AudioFormat = "ba[ext=m4a]/ba[acodec^=mp4a]/ba/b";

    /// <summary>What FFmpeg is told when it makes the WAV: mono, at the source's own rate.</summary>
    public const string ToWav = "ExtractAudio+ffmpeg_o:-ac 1";

    /// <summary>The name of the file yt-dlp writes the finished WAV's path into, once it is in place.</summary>
    public const string DoneFileName = "done.txt";

    /// <summary>
    /// The options never passed, in any form (alone or with =value), and refused in any list: they sign in, hand over cookies,
    /// a browser's data or credentials, run a command, read more configuration, or update yt-dlp. Compared with letter case
    /// kept, since -p (a password) is not -P (paths).
    /// </summary>
    public static readonly IReadOnlySet<string> Forbidden = new HashSet<string>(StringComparer.Ordinal)
    {
        "--cookies", "--cookies-from-browser", "--username", "-u", "--password", "-p", "--twofactor", "-2", "--netrc", "-n", "--netrc-location",
        "--netrc-cmd", "--video-password", "--ap-mso", "--ap-username", "--ap-password", "--ap-list-mso", "--remote-components", "--exec",
        "--exec-before-download", "--config-locations", "--config-location", "-U", "--update", "--update-to", "--load-cookies", "--mark-watched"
    };

    /// <summary>The version call: it asks yt-dlp nothing of YouTube, so it needs no fixed set.</summary>
    public static IReadOnlyList<string> Version { get; } = ["--version"];

    /// <summary>The fixed set every other call starts with.</summary>
    public IReadOnlyList<string> Fixed =>
    [
        "--ignore-config", "--no-cookies", "--no-cookies-from-browser", "--no-mark-watched", "--no-colors", "--sleep-requests", "1",
        "--js-runtimes", $"deno:{tools.Deno}", "--ffmpeg-location", tools.FfmpegFolder, "--cache-dir", folders.Cache
    ];

    /// <summary>The information file a video's read writes: &lt;staging&gt;\&lt;ID&gt;.info.json.</summary>
    public string InfoFile(string videoId) => Path.Combine(folders.StagingOf(videoId), $"{videoId}.info.json");

    /// <summary>The file a download writes the finished WAV's path into: &lt;staging&gt;\done.txt.</summary>
    public string DoneFile(string videoId) => Path.Combine(folders.StagingOf(videoId), DoneFileName);

    /// <summary>A playlist's read: one JSON of its entries, without opening each video.</summary>
    public IReadOnlyList<string> ReadPlaylist(string playlistId) => Checked([.. Fixed, "--flat-playlist", "-J", YouTubeLinks.PlaylistUrl(playlistId)]);

    /// <summary>A video's read: its information file alone, into its staging folder, named by its ID.</summary>
    public IReadOnlyList<string> ReadVideo(string videoId) =>
        Checked([.. Fixed, "--no-playlist", "--skip-download", "--write-info-json", "--no-write-playlist-metafiles", "-o", Template(videoId), YouTubeLinks.WatchUrl(videoId)]);

    /// <summary>
    /// A video's download into its staging folder: from its read's information file when <paramref name="fromInfoFile"/>,
    /// so that YouTube's pages are asked once per video, otherwise from its link; the best audio, made a mono WAV at YouTube's
    /// own rate, the path of the finished WAV written to done.txt once it is in place.
    /// </summary>
    public IReadOnlyList<string> Download(string videoId, bool fromInfoFile)
    {
        var staging = folders.StagingOf(videoId);
        List<string> arguments = [.. Fixed, "--no-playlist", "--newline"];
        if (fromInfoFile) arguments.AddRange(["--load-info-json", InfoFile(videoId)]);
        arguments.AddRange(
        [
            "-f", AudioFormat, "-x", "--audio-format", "wav", "--postprocessor-args", ToWav,
            "-P", $"temp:{Path.Combine(staging, "tmp")}", "-o", Template(videoId), "--print-to-file", "after_move:filepath", DoneFile(videoId)
        ]);
        if (!fromInfoFile) arguments.Add(YouTubeLinks.WatchUrl(videoId));
        return Checked(arguments);
    }

    /// <summary>
    /// What is wrong with <paramref name="arguments"/> as a call of yt-dlp, or null when nothing is: a call but the version's
    /// that does not start with the fixed set's first words; a forbidden option (see <see cref="Forbidden"/>), alone or with
    /// =value, or one that starts --client-certificate; --load-info-json with a path outside a video's staging folder of
    /// <paramref name="folders"/>; or any argument that names the video window's profile.
    /// </summary>
    public static string? ProblemWith(IReadOnlyList<string> arguments, YouTubeFolders folders)
    {
        if (arguments.SequenceEqual(Version)) return null;
        if (arguments is not ["--ignore-config", "--no-cookies", "--no-cookies-from-browser", ..])
            return "a call of yt-dlp must start with --ignore-config --no-cookies --no-cookies-from-browser";

        for (var i = 0; i < arguments.Count; i++)
        {
            var argument = arguments[i];
            if (argument.Contains(folders.VideoBrowser, StringComparison.OrdinalIgnoreCase))
                return $"\"{argument}\" names ClassScribe's video window's profile, which yt-dlp is never given";
            if (!argument.StartsWith('-')) continue;

            var option = argument.Split('=', 2)[0];
            if (Forbidden.Contains(option) || option.StartsWith("--client-certificate", StringComparison.Ordinal))
                return $"{option} is never given to yt-dlp: it signs in, hands over cookies or a browser's data, runs a command, reads more configuration or updates yt-dlp";

            if (option == "--load-info-json" && !IsInStaging(i + 1 < arguments.Count ? arguments[i + 1] : null, folders))
                return "--load-info-json takes only a path ClassScribe built inside a video's staging folder";
        }

        return null;
    }

    /// <summary>The output template of video <paramref name="videoId"/>: its staging folder, its ID, and yt-dlp's extension.</summary>
    private string Template(string videoId) => Path.Combine(folders.StagingOf(videoId), $"{videoId}.%(ext)s");

    /// <summary>True when <paramref name="path"/> is a file in a video's staging folder, &lt;staging&gt;\&lt;ID&gt;\..., with nothing that leads out of it.</summary>
    private static bool IsInStaging(string? path, YouTubeFolders folders)
    {
        if (path is null || path.Contains("..", StringComparison.Ordinal) || !Path.IsPathFullyQualified(path)) return false;

        var folder = Path.GetDirectoryName(Path.GetFullPath(path));
        return folder is not null
               && YouTubeLinks.IsVideoId(Path.GetFileName(folder))
               && string.Equals(Path.GetDirectoryName(folder), folders.Staging, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary><paramref name="arguments"/>, once <see cref="ProblemWith"/> finds nothing wrong; otherwise an <see cref="InvalidOperationException"/>, before yt-dlp is ever run with them.</summary>
    private IReadOnlyList<string> Checked(IReadOnlyList<string> arguments) =>
        ProblemWith(arguments, folders) is { } problem ? throw new InvalidOperationException($"ClassScribe built arguments for yt-dlp it must never give: {problem}.") : arguments;
}
