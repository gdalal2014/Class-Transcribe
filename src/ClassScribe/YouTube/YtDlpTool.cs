namespace ClassScribe.YouTube;

/// <summary>
/// The adapter of <see cref="IVideoTool"/>: the check (<see cref="YtDlpCheck"/>), whose paths it keeps for the calls, the
/// arguments (<see cref="YtDlpArguments"/>, built and checked before yt-dlp ever sees them), what yt-dlp wrote
/// (<see cref="YtDlpRead"/>, mapped to the batch's records) and what a failure means (<see cref="YtDlpErrors"/>). One per
/// batch, since the paths the check found belong to it.
/// </summary>
public sealed class YtDlpTool(YtDlpCheck check, YouTubeFolders folders) : IVideoTool
{
    /// <summary>What is said when a call is asked for before the check found yt-dlp.</summary>
    private const string NotFoundYet = "yt-dlp's path is known only once the check has found it.";

    /// <summary>yt-dlp's program, as the check found it; null before.</summary>
    private string? _program;

    /// <summary>The arguments, over the paths the check found; null before.</summary>
    private YtDlpArguments? _arguments;

    /// <summary>yt-dlp's program, as the check found it.</summary>
    private string Program => _program ?? throw new InvalidOperationException(NotFoundYet);

    /// <summary>The arguments, over the paths the check found.</summary>
    private YtDlpArguments Arguments => _arguments ?? throw new InvalidOperationException(NotFoundYet);

    /// <inheritdoc />
    public async Task<ToolCheck> CheckAsync(CancellationToken cancellationToken)
    {
        var found = await check.RunAsync(cancellationToken);
        if (found.Paths is { } paths)
        {
            _program = paths.YtDlp;
            _arguments = new YtDlpArguments(paths, folders);
        }

        return found;
    }

    /// <inheritdoc />
    public ToolCall ReadPlaylist(string playlistId) => new(Program, Arguments.ReadPlaylist(playlistId), folders.Root);

    /// <inheritdoc />
    public ToolCall ReadVideo(string videoId) => new(Program, Arguments.ReadVideo(videoId), folders.StagingOf(videoId));

    /// <inheritdoc />
    public ToolCall Download(string videoId, bool fromInfoFile) => new(Program, Arguments.Download(videoId, fromInfoFile), folders.StagingOf(videoId));

    /// <inheritdoc />
    public ListedPlaylist Playlist(IReadOnlyList<string> output, string playlistId)
    {
        var playlist = YtDlpRead.Playlist(string.Join('\n', output), playlistId);
        return new ListedPlaylist(playlist.Id, playlist.Title, [.. playlist.Entries.Select(entry => new ListedVideo(entry.Id!, entry.Title))]);
    }

    /// <inheritdoc />
    public bool HasInfo(string videoId) => File.Exists(Arguments.InfoFile(videoId));

    /// <inheritdoc />
    public async Task<VideoInfo?> ReadInfoAsync(string videoId, CancellationToken cancellationToken)
    {
        var infoFile = Arguments.InfoFile(videoId);
        if (!File.Exists(infoFile)) return null;

        return InfoOf(YtDlpRead.Info(await File.ReadAllTextAsync(infoFile, cancellationToken), infoFile));
    }

    /// <inheritdoc />
    public string? StagedWav(string videoId)
    {
        var done = Arguments.DoneFile(videoId);
        var named = File.Exists(done) ? File.ReadAllLines(done).LastOrDefault(line => line.Trim().Length > 0)?.Trim() : null;
        var staging = folders.StagingOf(videoId);
        return named is not null && Path.IsPathFullyQualified(named) && string.Equals(Path.GetDirectoryName(Path.GetFullPath(named)), staging, StringComparison.OrdinalIgnoreCase)
            && named.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) && File.Exists(named)
            ? named
            : null;
    }

    /// <inheritdoc />
    public DownloadFailure Classify(int exitCode, IReadOnlyList<string> errorLines) => YtDlpErrors.Classify(exitCode, errorLines);

    /// <summary><paramref name="info"/> as the batch reads it: the fields it uses, with the handle, the upload date, the length and the rate worked out.</summary>
    internal static VideoInfo InfoOf(YtDlpInfo info) =>
        new(info.Id!, info.Title, info.Length, info.Channel, info.ChannelId, info.Handle, info.ChannelUrl, info.Uploaded, info.Availability, info.LiveStatus, info.AgeLimit,
            info.SourceRate);
}
