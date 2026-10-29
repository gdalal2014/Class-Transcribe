using ClassScribe.Settings;
using ClassScribe.Storage;
using ClassScribe.Transcription;
using ClassScribe.YouTube;
using Microsoft.Extensions.Options;

namespace ClassScribe.Commands;

/// <summary>What menu choice Y remembers for as long as the window is open: the last course named, which Enter takes again.</summary>
public sealed class YouTubeMenuMemory
{
    /// <summary>The course the last batch went to; null before the first.</summary>
    public string? LastCourse { get; set; }
}

/// <summary>
/// Menu choice Y (design 1.3): the links, pasted one or more on a line or as a dragged text file of links, an empty line
/// ending the list; the course (Enter takes the last one used); how to get the audio, R recording each video as it plays in a
/// Chrome window of ClassScribe's own, D downloading it with yt-dlp; whether to transcribe; then the batch, whose plan asks
/// Y to start the recordings, or for each video that needs a word and for YES to download. Esc goes back at every step. A line
/// that holds no link the batch takes is named with its reason and left out, and so is, for R, a playlist's or a live
/// stream's link, which only D takes; the rest go on. The recordings folder is Recording:OutputFolder, as for the menu's
/// classes.
/// </summary>
public sealed class YouTubeMenu(YouTubeCommand command, IConsoleUi ui, IClassFolders folders, YouTubeMenuMemory memory, GlossaryChoice glossaries,
    IOptionsSnapshot<ElevenLabsOptions> elevenLabs)
{
    /// <summary>Asks for the batch and runs it. Returns the exit code; 0 when Esc went back.</summary>
    public async Task<int> RunAsync()
    {
        ui.WriteLine("YouTube videos, each transcribed like a class.");
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
        if (await AskHowAsync() is not { } download) return 0;

        var links = reading.Links;
        if (!download)
        {
            foreach (var other in links.Where(link => link.Kind != YouTubeLinkKind.Video))
            {
                ui.WriteLine(other.Kind == YouTubeLinkKind.Playlist
                    ? $"Left out: {other.Url} is a playlist's link, which only D takes; paste each video's own link to record it as it plays."
                    : $"Left out: {YouTubeLinks.WatchUrl(other.Id)} was given as a live stream's link, which only D takes; paste the video's watch link to record it as it plays.");
            }

            links = [.. links.Where(link => link.Kind == YouTubeLinkKind.Video)];
            if (links.Count == 0)
            {
                ui.WriteLine("No video to record; nothing to do.");
                return 0;
            }

            if (links.Count > VideoBatch.MostVideos)
            {
                ui.WriteLine($"ClassScribe records at most {VideoBatch.MostVideos} videos at a time as they play, each for as long as it plays; these are {links.Count}. Paste fewer, or download them with D.");
                return 0;
            }
        }

        if (await AskTranscribeAsync(name) is not { } transcribe) return 0;

        memory.LastCourse = name;
        return await command.RunAsync(new YouTubeRequest(links, name, folders.Root, download, BatchConfirmation.Ask, transcribe));
    }

    /// <summary>
    /// Asks how to get the videos' audio: R records each as it plays, in a Chrome window of ClassScribe's own, D downloads it
    /// with yt-dlp. True for D, false for R; null for Esc.
    /// </summary>
    private async Task<bool?> AskHowAsync()
    {
        ui.WriteLine("How should ClassScribe get their audio?");
        ui.WriteLine("  R  Record each as it plays, in a Chrome window of ClassScribe's own. You press play there, and it takes");
        ui.WriteLine("     as long as the videos do. For any video you can watch.");
        ui.WriteLine("  D  Download the audio with yt-dlp: fast, but only for videos you uploaded or whose owner allows");
        ui.WriteLine("     downloading, and never your course's.");
        while (true)
        {
            var key = await ui.ReadKeyAsync(CancellationToken.None);
            if (key is not { } pressed || pressed.Key == ConsoleKey.Escape) return null;
            if (pressed.Key == ConsoleKey.R) return false;
            if (pressed.Key == ConsoleKey.D) return true;
        }
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

            var name = typed.Trim().Length == 0 && memory.LastCourse is { } again ? again : FolderNames.SafeName(typed);
            if (name.Length > 0) return name;

            ui.WriteLine("A course's name is needed: its folder is named after it.");
        }
    }

    /// <summary>
    /// Asks whether to transcribe each video, with about what an hour costs, the glossary counted as a recording of
    /// <paramref name="course"/> would be sent with it: T transcribes, O only fetches the audio. Null for Esc.
    /// </summary>
    private async Task<bool?> AskTranscribeAsync(string course)
    {
        var settings = elevenLabs.Value;
        var glossary = glossaries.TermsFor(course) > 0;
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
