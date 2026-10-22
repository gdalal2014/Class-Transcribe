using System.Globalization;
using System.Text.RegularExpressions;
using ClassScribe.Audio;

namespace ClassScribe.Storage;

/// <summary>
/// The files of one recording, all in its folder and named after it: the name given when it started,
/// an underscore, and the UTC time it started, such as <c>Kalam_2026-10-15_20-55-27Z</c>. The audio
/// is the .wav of that name, with _part2, _part3 and so on for further segments, and the microphone's
/// track, when it was recorded with the class, the .mic.wav files beside them; the transcript is the
/// .md of that name, with (2), (3) and so on for later transcripts of the same recording, and ElevenLabs'
/// answer the .json beside it. An answer that was paid for but did not become a transcript is the
/// .unread.json of that name, or a .json left without its .md (see <see cref="FindKeptAnswers"/>). The
/// marks, the log and the upload copy complete the set, with a YouTube video's source file for a video's recording.
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

    /// <summary>
    /// The marks file: one mark per line, set with M while recording, and the notes ClassScribe adds there itself, such as
    /// where the recording was taken up again (see <see cref="ClassMarks"/>).
    /// </summary>
    public string Marks => Path.Combine(Folder, $"{BaseName}.marks.txt");

    /// <summary>
    /// The log: everything the runs that recorded the recording logged, in the order they logged it, a run that
    /// continues the recording appending to it. It is the record of a class after a crash, a closed window or a restart.
    /// </summary>
    public string Log => Path.Combine(Folder, $"{BaseName}.log");

    /// <summary>The 16 kHz copy that is uploaded.</summary>
    public string Upload => Path.Combine(Folder, $"{BaseName}.upload.wav");

    /// <summary>
    /// A YouTube video's source file: the video's title and link and how its audio was obtained (see
    /// <see cref="YouTube.VideoSource"/>). Only a video's recording has one; it is never an answer or a transcript.
    /// </summary>
    public string Source => Path.Combine(Folder, $"{BaseName}.source.json");

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

    /// <summary>
    /// The recording any of its files belongs to, by the file's name: a segment of either track, the marks, the log, the
    /// upload copy, a video's source file, or any copy of its transcript or of ElevenLabs' answer, an answer kept as unread included. Null for
    /// any other file, such as one without a time stamp in its name. Whether the recording's audio is still there is for
    /// the caller to look at (see <see cref="FindSegments"/>).
    /// </summary>
    public static RecordingFiles? TryParse(string path) => Parse(FileOfARecording(), path);

    /// <summary>
    /// The recording a segment of its main track belongs to, by the file's name, or null for any other file, a file of
    /// the recording that is not such a segment included. A recording is listed by these, so that files left without
    /// their audio, such as an upload copy, do not pass for one.
    /// </summary>
    internal static RecordingFiles? TryParseSegment(string path) => Parse(MainSegmentName(), path);

    /// <summary>
    /// The audio file of segment <paramref name="number"/> of <paramref name="track"/>: the first has the bare name, the rest
    /// a part number; the main track's files end in .wav, the microphone's in .mic.wav. The main track is meant when none is named.
    /// </summary>
    public string Segment(int number, Track track = Track.Main) =>
        Path.Combine(Folder, number == 1 ? $"{BaseName}{Extension(track)}" : $"{BaseName}_part{number}{Extension(track)}");

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

    /// <summary>
    /// The segment files of <paramref name="track"/> that exist, in recording order: for the main track the .wav files of
    /// the recording's name, and for the microphone's track the .mic.wav files beside them. Neither track takes the
    /// other's files. Empty when there are none, or the folder is not there.
    /// </summary>
    public IReadOnlyList<string> FindSegments(Track track = Track.Main)
    {
        if (!Directory.Exists(Folder)) return [];

        var segment = new Regex($"^{Regex.Escape(BaseName)}(?:_part(?<number>[0-9]{{1,9}}))?{Regex.Escape(Extension(track))}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return Directory.EnumerateFiles(Folder, $"{BaseName}*.wav")
            .Select(file => (Path: file, Match: segment.Match(Path.GetFileName(file))))
            .Where(file => file.Match.Success)
            .OrderBy(file => file.Match.Groups["number"].Success ? int.Parse(file.Match.Groups["number"].Value, CultureInfo.InvariantCulture) : 1)
            .Select(file => file.Path)
            .ToList();
    }

    /// <summary>What a segment file's name ends in on <paramref name="track"/>: .wav on the main track, .mic.wav on the microphone's.</summary>
    private static string Extension(Track track) => track == Track.Microphone ? ".mic.wav" : ".wav";

    /// <summary>The recording whose base name <paramref name="fileName"/> gives the file at <paramref name="path"/>, or null when it gives none.</summary>
    private static RecordingFiles? Parse(Regex fileName, string path)
    {
        var match = fileName.Match(Path.GetFileName(path));
        return match.Success ? new RecordingFiles(Path.GetDirectoryName(Path.GetFullPath(path))!, match.Groups["base"].Value) : null;
    }

    /// <summary>A segment file's name on the main track: the base name with its time stamp, an optional part number, and .wav.</summary>
    [GeneratedRegex(@"^(?<base>.+_[0-9]{4}-[0-9]{2}-[0-9]{2}_[0-9]{2}-[0-9]{2}-[0-9]{2}Z)(?:_part[0-9]{1,9})?\.wav$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MainSegmentName();

    /// <summary>
    /// The name of any file of a recording: the base name with its time stamp, then a segment of either track (.wav,
    /// _partN.wav, .mic.wav, _partN.mic.wav), the upload copy (.upload.wav), the marks (.marks.txt), the log (.log), a
    /// video's source file (.source.json), or a copy of the transcript or of the answer (.md, .json, .unread.json, each with
    /// " (N)" before it from the second copy).
    /// </summary>
    [GeneratedRegex(@"^(?<base>.+_[0-9]{4}-[0-9]{2}-[0-9]{2}_[0-9]{2}-[0-9]{2}-[0-9]{2}Z)(?:(?:_part[0-9]{1,9})?(?:\.mic)?\.wav|\.upload\.wav|\.marks\.txt|\.log|\.source\.json|(?: \([0-9]{1,9}\))?(?:\.md|(?:\.unread)?\.json))$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FileOfARecording();

    /// <summary>The time stamp at the end of a base name.</summary>
    [GeneratedRegex(@"_(?<stamp>[0-9]{4}-[0-9]{2}-[0-9]{2}_[0-9]{2}-[0-9]{2}-[0-9]{2}Z)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StampSuffix();
}
