using System.Globalization;
using System.Text.RegularExpressions;

namespace ClassScribe.Storage;

/// <summary>
/// The files of one recording, all in its folder and named after it: the name given when it started,
/// an underscore, and the UTC time it started, such as <c>Kalam_2026-10-15_20-55-27Z</c>. The audio
/// is the .wav of that name, with _part2, _part3 and so on for further segments; the transcript is the
/// .md of that name, with (2), (3) and so on for later transcripts of the same recording, and ElevenLabs'
/// answer the .json beside it. An answer that was paid for but did not become a transcript is the
/// .unread.json of that name, or a .json left without its .md (see <see cref="FindKeptAnswers"/>).
/// </summary>
/// <param name="Folder">The folder holding the recording's files.</param>
/// <param name="BaseName">The name and time stamp every file name starts with.</param>
public sealed partial record RecordingFiles(string Folder, string BaseName)
{
    /// <summary>How the UTC start time is written into file names: sortable, and without the colons Windows forbids.</summary>
    public const string StampFormat = "yyyy-MM-dd_HH-mm-ss'Z'";

    /// <summary>The name given when the recording started: the base name without the time stamp.</summary>
    public string Name => StampSuffix().Replace(BaseName, "");

    /// <summary>When the recording started, read from its name, or null when the name carries no time stamp.</summary>
    public DateTimeOffset? StartedUtc
    {
        get
        {
            var match = StampSuffix().Match(BaseName);
            return match.Success && DateTimeOffset.TryParseExact(match.Groups["stamp"].Value, StampFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var started)
                ? started
                : null;
        }
    }

    /// <summary>The marks file: one mark per line, set with M while recording.</summary>
    public string Marks => Path.Combine(Folder, $"{BaseName}.marks.txt");

    /// <summary>The 16 kHz copy that is uploaded.</summary>
    public string Upload => Path.Combine(Folder, $"{BaseName}.upload.wav");

    /// <summary>
    /// True when the recording has been transcribed at least once: any copy of its transcript is there, not only
    /// the first, which may have been deleted while a later one was kept.
    /// </summary>
    public bool HasTranscript
    {
        get
        {
            if (!Directory.Exists(Folder)) return false;

            var transcript = new Regex($"^{Regex.Escape(BaseName)}(?: \\([0-9]{{1,9}}\\))?\\.md$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return Directory.EnumerateFiles(Folder, $"{BaseName}*.md").Any(file => transcript.IsMatch(Path.GetFileName(file)));
        }
    }

    /// <summary>The files of a recording of <paramref name="name"/> started at <paramref name="startedUtc"/>, in <paramref name="folder"/>.</summary>
    public static RecordingFiles ForNew(string folder, string name, DateTimeOffset startedUtc) =>
        new(folder, $"{name}_{startedUtc.UtcDateTime.ToString(StampFormat, CultureInfo.InvariantCulture)}");

    /// <summary>The recording a segment file belongs to, or null when the file is not a segment of one.</summary>
    public static RecordingFiles? TryParse(string wavPath)
    {
        var match = SegmentFileName().Match(Path.GetFileName(wavPath));
        return match.Success ? new RecordingFiles(Path.GetDirectoryName(Path.GetFullPath(wavPath))!, match.Groups["base"].Value) : null;
    }

    /// <summary>The audio file of segment <paramref name="number"/>: the first has the bare name, the rest a part number.</summary>
    public string Segment(int number) => Path.Combine(Folder, number == 1 ? $"{BaseName}.wav" : $"{BaseName}_part{number}.wav");

    /// <summary>The transcript, a Markdown document. <paramref name="copy"/> 2 and up name later transcripts of the same recording.</summary>
    public string Transcript(int copy = 1) => Path.Combine(Folder, copy == 1 ? $"{BaseName}.md" : $"{BaseName} ({copy}).md");

    /// <summary>ElevenLabs' answer as it arrived, for the transcript of the same <paramref name="copy"/>.</summary>
    public string Json(int copy = 1) => Path.Combine(Folder, copy == 1 ? $"{BaseName}.json" : $"{BaseName} ({copy}).json");

    /// <summary>
    /// An answer ElevenLabs was paid for that did not become a transcript, because it could not be read or could
    /// not be saved: kept under a name of its own, so that it neither counts as a transcript nor takes the next
    /// transcript's place. <paramref name="copy"/> 2 and up name later such answers of the same recording.
    /// </summary>
    public string UnreadAnswer(int copy = 1) => Path.Combine(Folder, copy == 1 ? $"{BaseName}.unread.json" : $"{BaseName} ({copy}).unread.json");

    /// <summary>The first <see cref="UnreadAnswer"/> name not taken yet, so that an answer kept earlier is never overwritten.</summary>
    public string NextFreeUnreadAnswer()
    {
        for (var copy = 1; ; copy++)
        {
            var path = UnreadAnswer(copy);
            if (!File.Exists(path)) return path;
        }
    }

    /// <summary>
    /// The answers ElevenLabs was paid for that did not become a transcript, kept among the recording's files: every
    /// <see cref="UnreadAnswer"/>, and every copy's <see cref="Json"/> whose <see cref="Transcript"/> is missing, as
    /// when the transcript could not be made. Empty when the folder holds none, or is not there.
    /// </summary>
    public IReadOnlyList<string> FindKeptAnswers()
    {
        if (!Directory.Exists(Folder)) return [];

        var answer = new Regex($"^{Regex.Escape(BaseName)}(?: \\([0-9]{{1,9}}\\))?(?<unread>\\.unread)?\\.json$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return Directory.EnumerateFiles(Folder, $"{BaseName}*.json")
            .Where(file => answer.Match(Path.GetFileName(file)) is { Success: true } match
                && (match.Groups["unread"].Success || !File.Exists(Path.ChangeExtension(file, ".md"))))
            .ToList();
    }

    /// <summary>The segment files that exist, in recording order.</summary>
    public IReadOnlyList<string> FindSegments()
    {
        if (!Directory.Exists(Folder)) return [];

        var segment = new Regex($"^{Regex.Escape(BaseName)}(?:_part(?<number>[0-9]{{1,9}}))?\\.wav$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return Directory.EnumerateFiles(Folder, $"{BaseName}*.wav")
            .Select(file => (Path: file, Match: segment.Match(Path.GetFileName(file))))
            .Where(file => file.Match.Success)
            .OrderBy(file => file.Match.Groups["number"].Success ? int.Parse(file.Match.Groups["number"].Value, CultureInfo.InvariantCulture) : 1)
            .Select(file => file.Path)
            .ToList();
    }

    /// <summary>A segment file's name: the base name with its time stamp, an optional part number, and .wav.</summary>
    [GeneratedRegex(@"^(?<base>.+_[0-9]{4}-[0-9]{2}-[0-9]{2}_[0-9]{2}-[0-9]{2}-[0-9]{2}Z)(?:_part[0-9]{1,9})?\.wav$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SegmentFileName();

    /// <summary>The time stamp at the end of a base name.</summary>
    [GeneratedRegex(@"_(?<stamp>[0-9]{4}-[0-9]{2}-[0-9]{2}_[0-9]{2}-[0-9]{2}-[0-9]{2}Z)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StampSuffix();
}
