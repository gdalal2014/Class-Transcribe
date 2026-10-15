using System.Globalization;
using System.Text.RegularExpressions;
using ClassScribe.Audio;
using ClassScribe.Settings;
using Microsoft.Extensions.Options;

namespace ClassScribe.Storage;

/// <summary>A recorded class, as the menu lists it.</summary>
/// <param name="Folder">Full path of the class folder.</param>
/// <param name="Name">Name of the class folder, normally the date and time the recording started.</param>
/// <param name="LastRecordedUtc">When audio was last written to the class.</param>
/// <param name="Length">Length of the recording.</param>
/// <param name="HasTranscript">True when the class folder already holds a transcript.</param>
public sealed record RecordedClass(string Folder, string Name, DateTime LastRecordedUtc, TimeSpan Length, bool HasTranscript);

/// <summary>
/// Where recordings and transcripts go: one folder per class, and one per transcribed file, all under
/// Recording:OutputFolder. A folder is never reused, so nothing in an existing one is overwritten.
/// </summary>
public sealed partial class ClassFolders(IOptions<RecordingOptions> options, TimeProvider time)
{
    /// <summary>File name of the transcript in a class folder.</summary>
    public const string TranscriptFileName = "transcript.txt";

    /// <summary>The folder that holds every class folder, with environment variables such as %USERPROFILE% expanded.</summary>
    public string Root
    {
        get
        {
            var configured = options.Value.OutputFolder;
            return string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ClassScribe")
                : Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured.Trim()));
        }
    }

    /// <summary>Creates the folder for a class recorded now, named after the local date and time.</summary>
    public string CreateForClass() =>
        // Invariant, so the name is a Gregorian date that sorts by time whatever calendar the machine is set to.
        CreateNew(time.GetLocalNow().ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture));

    /// <summary>Creates the folder for the transcript of a file, named after the file.</summary>
    public string CreateForFile(string sourcePath) => CreateNew(Path.GetFileNameWithoutExtension(sourcePath));

    /// <summary>Lists the class folders that hold a recording, the most recently recorded first.</summary>
    public IReadOnlyList<RecordedClass> ListRecent(int count)
    {
        var root = Root;
        if (!Directory.Exists(root)) return [];

        return Directory.EnumerateDirectories(root)
            .Select(Describe)
            .OfType<RecordedClass>()
            .OrderByDescending(recorded => recorded.LastRecordedUtc)
            .Take(count)
            .ToList();
    }

    /// <summary>Lists the recorder's segment files in a folder, in the order they were recorded.</summary>
    public static IReadOnlyList<string> FindSegments(string folder) =>
        Directory.EnumerateFiles(folder, "recording-*.wav")
            .Select(file => (FullPath: file, Name: SegmentName().Match(Path.GetFileName(file))))
            .Where(file => file.Name.Success)
            .OrderBy(file => int.Parse(file.Name.Groups["number"].Value, CultureInfo.InvariantCulture))
            .Select(file => file.FullPath)
            .ToList();

    /// <summary>Creates a folder with the given name under <see cref="Root"/>, adding _2, _3 and so on when the name is taken.</summary>
    private string CreateNew(string name)
    {
        var root = Root;
        var folder = Path.Combine(root, name);
        for (var suffix = 2; Directory.Exists(folder); suffix++)
            folder = Path.Combine(root, $"{name}_{suffix}");

        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>Describes a class folder, or returns null when it holds no recording.</summary>
    private static RecordedClass? Describe(string folder)
    {
        var segments = FindSegments(folder);
        if (segments.Count == 0) return null;

        var length = TimeSpan.Zero;
        var lastRecorded = DateTime.MinValue;
        foreach (var segment in segments)
        {
            length += SegmentLength(segment);
            var written = File.GetLastWriteTimeUtc(segment);
            if (written > lastRecorded) lastRecorded = written;
        }

        return new RecordedClass(folder, Path.GetFileName(folder), lastRecorded, length, File.Exists(Path.Combine(folder, TranscriptFileName)));
    }

    /// <summary>
    /// Length of a segment, worked out from its size the way the preparer reads it: to the end of the
    /// file, whatever the header says. A file the recorder did not write counts as empty.
    /// </summary>
    private static TimeSpan SegmentLength(string path)
    {
        Span<byte> header = stackalloc byte[WavSegmentWriter.HeaderSize];
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var read = file.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        if (read < header.Length || !WavSegmentWriter.TryReadHeader(header, out var sampleRate)) return TimeSpan.Zero;

        return TimeSpan.FromSeconds((file.Length - WavSegmentWriter.HeaderSize) / 2.0 / sampleRate);
    }

    /// <summary>The recorder's segment files, and nothing else that happens to end in .wav.</summary>
    [GeneratedRegex(@"^recording-(?<number>[0-9]{1,9})\.wav$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SegmentName();
}
