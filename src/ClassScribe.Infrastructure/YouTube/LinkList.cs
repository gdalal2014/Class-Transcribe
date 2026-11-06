using System.Text;

namespace ClassScribe.YouTube;

/// <summary>What a list of links came to.</summary>
/// <param name="Links">The links taken, in the order given, each once.</param>
/// <param name="Problems">Each word that is no link the batch takes, named by where it stands, with its reason.</param>
/// <param name="Notes">Each duplicate passed over, named by where it stands.</param>
public sealed record LinkListReading(IReadOnlyList<YouTubeLink> Links, IReadOnlyList<string> Problems, IReadOnlyList<string> Notes);

/// <summary>
/// Reads the links of a batch (design 1.2): the words of the command line and of a list file, joined in order. A list file
/// is UTF-8, with or without a byte-order mark; several links may stand on a line, separated by spaces; a # at the start of
/// a line, or after a space, starts a comment that runs to the line's end; blank lines are passed over. A link given twice,
/// by its video's or its playlist's ID, is taken once, with a line that names the second, and marked as copied from an
/// embedded player when any of its links was. A word that is no link the batch takes is named by where it stands, with its
/// reason.
/// </summary>
public static class LinkList
{
    /// <summary>The words of the list file at <paramref name="path"/>, each with its line (see <see cref="WordsOf"/>).</summary>
    /// <exception cref="IOException">The file cannot be read.</exception>
    public static IReadOnlyList<LinkWord> ReadFile(string path) =>
        WordsOf(File.ReadAllText(path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)), Path.GetFileName(path));

    /// <summary>The words of a list file's <paramref name="text"/>, named <paramref name="fileName"/>, each with its line, comments and blank lines left out.</summary>
    public static IReadOnlyList<LinkWord> WordsOf(string text, string fileName)
    {
        List<LinkWord> words = [];
        var lines = text.TrimStart('﻿').Split('\n');
        for (var number = 1; number <= lines.Length; number++)
        {
            var line = lines[number - 1].TrimEnd('\r');
            foreach (var word in line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
            {
                // A # at the start of the line, or after a space, begins a comment: the rest of the line is passed over.
                if (word.StartsWith('#')) break;
                words.Add(new LinkWord(word, $"line {number} of {fileName}"));
            }
        }

        return words;
    }

    /// <summary>Reads <paramref name="words"/> as links, in order, each video and playlist taken once.</summary>
    public static LinkListReading Read(IEnumerable<LinkWord> words)
    {
        List<YouTubeLink> links = [];
        List<string> problems = [];
        List<string> notes = [];
        var seen = new HashSet<(bool Playlist, string Id)>();
        foreach (var word in words)
        {
            var reading = YouTubeLinks.Read(word.Text);
            if (reading.Link is not { } link)
            {
                problems.Add($"{Capitalised(word.Where)}: {reading.Problem}");
                continue;
            }

            var isPlaylist = link.Kind == YouTubeLinkKind.Playlist;
            if (!seen.Add((isPlaylist, link.Id)))
            {
                notes.Add($"{Capitalised(word.Where)} lists the {(isPlaylist ? "playlist" : "video")} {link.Id} again; it is taken once");

                // A video given again from an embedded player's button is marked so, wherever it was given first: the
                // download path never takes a video any of its links says came from a web page's player.
                if (link.FromEmbeddedPlayer && links.FindIndex(taken => taken.Kind != YouTubeLinkKind.Playlist && taken.Id == link.Id) is var first and >= 0)
                    links[first] = links[first] with { FromEmbeddedPlayer = true };
                continue;
            }

            links.Add(link);
        }

        return new LinkListReading(links, problems, notes);
    }

    /// <summary><paramref name="text"/> with its first letter capitalised, to start a sentence.</summary>
    private static string Capitalised(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
