using System.Globalization;
using System.Text.RegularExpressions;

namespace ClassScribe.Storage;

/// <summary>
/// The files of one recording, all in its folder and named after it: the name given when it started,
/// an underscore, and the UTC time it started, such as <c>Kalam_2026-10-15_20-55-27Z</c>. The audio
/// is the .wav of that name, with _part2, _part3 and so on for further segments; the transcript is the
/// .md of that name, with (2), (3) and so on for later transcripts of the same recording.
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

    /// <summary>True when the recording has been transcribed at least once.</summary>
    public bool HasTranscript => File.Exists(Transcript());

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
