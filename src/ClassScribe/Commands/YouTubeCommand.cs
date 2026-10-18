using ClassScribe.Settings;
using ClassScribe.YouTube;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClassScribe.Commands;

/// <summary>
/// The youtube command and menu choice Y's batch (design 1), and youtube check, in the window, each in a scope of its own.
/// Nothing records here, so the first Ctrl+C cancels at once. This build downloads (--download); recording videos as they
/// play is the next part of the feature.
/// </summary>
public sealed class YouTubeCommand(DownloadBatch download, YtDlpCheck check, IUserSignals signals, IConsoleUi ui, IOptionsSnapshot<YouTubeOptions> youTube,
    ILogger<YouTubeCommand> logger)
{
    /// <summary>
    /// Why --allow-unlisted is refused while YouTube:CourseChannels is empty: course pages embed unlisted videos, and the flag
    /// must never take one for a video he may download.
    /// </summary>
    public const string AllowUnlistedNeedsCourseChannels =
        "--allow-unlisted needs your course's channels: list them in YouTube:CourseChannels first, so that a course video is never taken for one you may download";

    /// <summary>The exit code of a mistake on the command line.</summary>
    private const int Mistake = 2;

    /// <summary>Runs the batch <paramref name="request"/> describes and returns the exit code; a cancel is thrown, as Ctrl+C gives it.</summary>
    public async Task<int> RunAsync(YouTubeRequest request)
    {
        signals.RequestStop();
        if (request.AllowUnlisted && youTube.Value.CourseChannels.Length == 0)
        {
            logger.LogError("{Problem}", AllowUnlistedNeedsCourseChannels);
            return Mistake;
        }

        if (!request.Download)
        {
            logger.LogError("Recording YouTube videos as they play is not part of this build yet; add --download to download them with yt-dlp");
            return Mistake;
        }

        return await download.RunAsync(request, signals.Cancellation);
    }

    /// <summary>
    /// youtube check: where yt-dlp, Deno, FFmpeg and FFprobe are, and their versions, what is missing with the winget command
    /// that installs it, and whether yt-dlp is more than 90 days old. Asks nothing of the network. Exit code 0 when everything
    /// is in place and yt-dlp is less than 90 days old, 1 otherwise.
    /// </summary>
    public async Task<int> CheckAsync()
    {
        signals.RequestStop();
        var found = await check.RunAsync(signals.Cancellation);
        foreach (var line in found.Lines) ui.WriteLine(line);
        foreach (var problem in found.Problems) logger.LogError("{Problem}", problem);
        if (found.Warning is { } old) logger.LogWarning("{Warning}", old);
        if (found.Problems.Count > 0 || found.Warning is not null) return 1;

        ui.WriteLine("yt-dlp and its helpers are in place.");
        return 0;
    }
}

/// <summary>What menu choice Y remembers for as long as the window is open: the last course named, which Enter takes again.</summary>
public sealed class YouTubeMenuMemory
{
    /// <summary>The course the last batch went to; null before the first.</summary>
    public string? LastCourse { get; set; }
}

/// <summary>
/// Menu choice Y (design 1.3): the links, pasted one or more on a line or as a dragged text file of links, an empty line
/// ending the list; the course (Enter takes the last one used); whether to transcribe; then the batch, whose plan asks for
/// each video that needs a word and for YES. Esc goes back at every step. A line that holds no link the batch takes is named
/// with its reason and left out; the rest go on. The recordings folder is Recording:OutputFolder, as for the menu's classes.
/// </summary>
public sealed class YouTubeMenu(YouTubeCommand command, IConsoleUi ui, Storage.ClassFolders folders, YouTubeMenuMemory memory, Transcription.KeytermLoader keyterms,
    IOptionsSnapshot<ElevenLabsOptions> elevenLabs)
{
    /// <summary>Asks for the batch and runs it. Returns the exit code; 0 when Esc went back.</summary>
    public async Task<int> RunAsync()
    {
        ui.WriteLine("YouTube videos, downloaded with yt-dlp, each transcribed like a class: only videos you uploaded, or whose owner allows downloading, never your course's.");
        ui.WriteLine("Paste the links, one or more on a line, or drag a text file of links onto the window. An empty line ends the list; Esc goes back.");
        List<LinkWord> words = [];
        for (var line = 1; ; line++)
        {
            ui.Write("> ");
            var typed = await ui.ReadLineAsync(CancellationToken.None);
            if (typed is null) return 0;
            if (typed.Trim().Length == 0) break;

            var path = typed.Trim().Trim('"');
            if (path.Length > 0 && !path.Contains("://", StringComparison.Ordinal) && File.Exists(path))
            {
                words.AddRange(LinkList.ReadFile(path));
                continue;
            }

            words.AddRange(LinkList.WordsOf(typed, "what you pasted").Select(word => word with { Where = $"line {line} of what you pasted" }));
        }

        var reading = LinkList.Read(words);
        foreach (var problem in reading.Problems) ui.WriteLine($"Left out: {problem}");
        foreach (var note in reading.Notes) ui.WriteLine(note);
        if (reading.Links.Count == 0)
        {
            ui.WriteLine("No link was given; nothing to do.");
            return 0;
        }

        if (await AskCourseAsync(reading.Links) is not { } name) return 0;
        if (await AskTranscribeAsync() is not { } transcribe) return 0;

        memory.LastCourse = name;
        return await command.RunAsync(new YouTubeRequest(reading.Links, name, folders.Root, Download: true, BatchConfirmation.Ask, transcribe));
    }

    /// <summary>Asks for the course; Enter takes the last one named. Null for Esc.</summary>
    private async Task<string?> AskCourseAsync(IReadOnlyList<YouTubeLink> links)
    {
        var count = links.Count(link => link.Kind != YouTubeLinkKind.Playlist);
        var playlists = links.Count - count;
        var what = string.Join(" and ", new[] { count > 0 ? DownloadPlan.Count(count, "video") : null, playlists > 0 ? DownloadPlan.Count(playlists, "playlist") : null }.OfType<string>());
        while (true)
        {
            ui.WriteLine(memory.LastCourse is { } last
                ? $"{what}. The course they belong to (Enter takes \"{last}\", the last one used):"
                : $"{what}. The course they belong to:");
            ui.Write("> ");
            var typed = await ui.ReadLineAsync(CancellationToken.None);
            if (typed is null) return null;

            var name = typed.Trim().Length == 0 && memory.LastCourse is { } again ? again : Storage.ClassFolders.SafeName(typed);
            if (name.Length > 0) return name;

            ui.WriteLine("A course's name is needed: its folder is named after it.");
        }
    }

    /// <summary>Asks whether to transcribe each video, with about what an hour costs: T transcribes, O only fetches the audio. Null for Esc.</summary>
    private async Task<bool?> AskTranscribeAsync()
    {
        var settings = elevenLabs.Value;
        var glossary = keyterms.Load(Transcription.KeytermLoader.ResolvePath(settings.KeytermsFile)).Count > 0;
        var hour = settings.PricePerHourUsd + (glossary ? settings.KeytermsPricePerHourUsd : 0);
        ui.WriteLine(System.FormattableString.Invariant(
            $"Then transcribe each one? T transcribes (about ${hour:0.00} an hour of audio{(glossary ? " with the glossary" : "")}), O only fetches the audio, Esc goes back."));
        while (true)
        {
            var key = await ui.ReadKeyAsync(CancellationToken.None);
            if (key is not { } pressed || pressed.Key == ConsoleKey.Escape) return null;
            if (pressed.Key == ConsoleKey.T) return true;
            if (pressed.Key == ConsoleKey.O) return false;
        }
    }
}
