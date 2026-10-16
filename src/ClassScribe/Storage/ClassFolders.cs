using ClassScribe.Audio;
using ClassScribe.Settings;
using Microsoft.Extensions.Options;

namespace ClassScribe.Storage;

/// <summary>A recording, as the menu lists it.</summary>
/// <param name="Files">The recording's files.</param>
/// <param name="LastRecordedUtc">When audio was last written to it.</param>
/// <param name="Length">Length of the recording.</param>
/// <param name="HasTranscript">True when it has been transcribed at least once.</param>
public sealed record RecordedClass(RecordingFiles Files, DateTime LastRecordedUtc, TimeSpan Length, bool HasTranscript);

/// <summary>
/// Where recordings and transcripts go: a folder per name under Recording:OutputFolder, holding the
/// files of every recording of that name, each stamped with its UTC start time (see
/// <see cref="RecordingFiles"/>). Nothing is ever overwritten.
/// </summary>
public sealed class ClassFolders(IOptions<RecordingOptions> options, TimeProvider time)
{
    /// <summary>The folder that holds every recording folder, with environment variables such as %USERPROFILE% expanded.</summary>
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

    /// <summary>
    /// The files for a recording named <paramref name="name"/> starting now: a folder of that name under
    /// the root, created if needed and shared with earlier recordings of the name, and files stamped
    /// with the UTC time. A recording started within the same second as another takes the next second.
    /// </summary>
    public RecordingFiles CreateRecording(string name)
    {
        var safe = SafeName(name);
        if (safe.Length == 0) throw new ArgumentException("A name is needed.", nameof(name));

        var folder = Path.Combine(Root, safe);
        Directory.CreateDirectory(folder);
        var started = time.GetUtcNow();
        var files = RecordingFiles.ForNew(folder, safe, started);
        while (File.Exists(files.Segment(1)))
            files = RecordingFiles.ForNew(folder, safe, started = started.AddSeconds(1));

        return files;
    }

    /// <summary>The files for the transcript of <paramref name="sourcePath"/>: a folder named after the file, and files stamped with the time now.</summary>
    public RecordingFiles CreateForFile(string sourcePath) => CreateRecording(Path.GetFileNameWithoutExtension(sourcePath));

    /// <summary>Lists the recordings under the root, the most recently recorded first.</summary>
    public IReadOnlyList<RecordedClass> ListRecent(int count)
    {
        var root = Root;
        if (!Directory.Exists(root)) return [];

        return Directory.EnumerateDirectories(root)
            .SelectMany(FindRecordings)
            .Select(Describe)
            .OrderByDescending(recorded => recorded.LastRecordedUtc)
            .Take(count)
            .ToList();
    }

    /// <summary>The recording started last in a folder, or null when the folder holds none.</summary>
    public static RecordingFiles? FindNewest(string folder) =>
        FindRecordings(folder)
            .OrderByDescending(files => files.StartedUtc ?? DateTimeOffset.MinValue)
            .ThenByDescending(files => files.BaseName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

    /// <summary>
    /// Cleans a name for use as a folder and file name: the characters Windows forbids go, runs of
    /// spaces become one, and trailing dots go. The result is empty when nothing usable is left.
    /// </summary>
    public static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var kept = new string([.. name.Where(c => !invalid.Contains(c))]);
        var tidy = string.Join(' ', kept.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return tidy.TrimEnd('.', ' ');
    }

    /// <summary>The recordings in a folder: one per distinct base name among its segment files.</summary>
    private static IEnumerable<RecordingFiles> FindRecordings(string folder) =>
        Directory.EnumerateFiles(folder, "*.wav")
            .Select(RecordingFiles.TryParse)
            .OfType<RecordingFiles>()
            .Distinct();

    /// <summary>Describes a recording from its segment files.</summary>
    private static RecordedClass Describe(RecordingFiles files)
    {
        var length = TimeSpan.Zero;
        var lastRecorded = DateTime.MinValue;
        foreach (var segment in files.FindSegments())
        {
            length += SegmentLength(segment);
            var written = File.GetLastWriteTimeUtc(segment);
            if (written > lastRecorded) lastRecorded = written;
        }

        return new RecordedClass(files, lastRecorded, length, files.HasTranscript);
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
}
