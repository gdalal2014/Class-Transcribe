using System.Collections.Frozen;
using System.Text.RegularExpressions;
using ClassScribe.Audio;
using ClassScribe.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClassScribe.Storage;

/// <summary>A recording, as the menu lists it.</summary>
/// <param name="Files">The recording's files.</param>
/// <param name="LastRecordedUtc">When audio was last written to it.</param>
/// <param name="Length">Length of the recording.</param>
/// <param name="HasTranscript">True when it has been transcribed at least once.</param>
/// <param name="KeptAnswer">
/// The newest answer ElevenLabs was paid for that did not become a transcript (see
/// <see cref="RecordingFiles.FindKeptAnswers"/>), kept beside the recording or in the recordings folder; null when there is none.
/// </param>
/// <param name="BeingWritten">
/// True while it is being recorded, in this window or another: a recorder still writes its newest segment (see
/// <see cref="ClassFolders.IsBeingWritten"/>). It is not transcribed until it has stopped.
/// </param>
public sealed record RecordedClass(RecordingFiles Files, DateTime LastRecordedUtc, TimeSpan Length, bool HasTranscript, string? KeptAnswer, bool BeingWritten);

/// <summary>
/// Where recordings and transcripts go: a folder per name under Recording:OutputFolder, holding the
/// files of every recording of that name, each stamped with its UTC start time (see
/// <see cref="RecordingFiles"/>). Nothing is ever overwritten. It lives as long as the app, and reads the
/// setting at each use, so a change counts as soon as the settings have been read again.
/// </summary>
public sealed partial class ClassFolders(IOptionsMonitor<RecordingOptions> options, TimeProvider time, ILogger<ClassFolders> logger)
{
    /// <summary>The folder name a transcribed file gets when nothing usable is left of its own name, as with ".mp3".</summary>
    private const string Untitled = "Untitled";

    /// <summary>ERROR_SHARING_VIOLATION as an HRESULT: what opening a file reports when the way another program holds it open does not allow that.</summary>
    private const int SharingViolation = unchecked((int)0x80070020);

    /// <summary>
    /// The names Windows keeps for devices, alone or followed by an extension, so that no file or folder
    /// can have them. The list is Microsoft's: https://learn.microsoft.com/windows/win32/fileio/naming-a-file
    /// </summary>
    private static readonly FrozenSet<string> DeviceNames = FrozenSet.Create(StringComparer.OrdinalIgnoreCase,
        "CON", "PRN", "AUX", "NUL",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³");

    /// <summary>
    /// How <see cref="ListRecent"/> looks into the folders under the root: a folder ClassScribe may not open,
    /// such as System Volume Information when the root is a drive, counts as empty. Hidden and system files
    /// are still seen, as everywhere else.
    /// </summary>
    private static readonly EnumerationOptions Listing = new() { IgnoreInaccessible = true, AttributesToSkip = 0 };

    /// <summary>
    /// The folder that holds every recording folder: Recording:OutputFolder with environment variables
    /// such as %USERPROFILE% expanded and any surrounding quotes removed. A relative setting is taken from
    /// the user profile, so it means the same folder however ClassScribe was started; an empty one means
    /// a ClassScribe folder there.
    /// </summary>
    public string Root => RootFor(options.CurrentValue.OutputFolder);

    /// <summary>
    /// The folder that holds every recording folder when Recording:OutputFolder is <paramref name="outputFolder"/>:
    /// environment variables such as %USERPROFILE% expanded and any surrounding quotes removed, a relative path taken
    /// from the user profile, and an empty one meaning a ClassScribe folder there.
    /// </summary>
    public static string RootFor(string? outputFolder)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var configured = Environment.ExpandEnvironmentVariables(outputFolder ?? "").Trim().Trim('"').Trim();
        return configured.Length == 0
            ? Path.Combine(profile, "ClassScribe")
            : Path.GetFullPath(configured, profile);
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

        // The path of the folder Windows actually made, in case it tidied the name in a way SafeName did not.
        var folder = Directory.CreateDirectory(Path.Combine(Root, safe)).FullName;
        var started = time.GetUtcNow();
        var files = RecordingFiles.ForNew(folder, safe, started);
        while (File.Exists(files.Segment(1)))
            files = RecordingFiles.ForNew(folder, safe, started = started.AddSeconds(1));

        return files;
    }

    /// <summary>
    /// The files for the transcript of <paramref name="sourcePath"/>: a folder named after the file, and
    /// files stamped with the time now. A file with nothing usable in its name, such as ".mp3", gets an
    /// "Untitled" folder.
    /// </summary>
    public RecordingFiles CreateForFile(string sourcePath)
    {
        var name = SafeName(Path.GetFileNameWithoutExtension(sourcePath));
        return CreateRecording(name.Length > 0 ? name : Untitled);
    }

    /// <summary>
    /// Says what keeps recordings from going to the root, in words fit to show before a recording
    /// starts, or returns null when nothing does: an environment variable in Recording:OutputFolder that
    /// is not set, a drive that is not there (an unplugged USB drive, say), or a file where the folder
    /// should be. A root that does not exist yet is no problem; the first recording makes it.
    /// </summary>
    public string? DescribeRootProblem()
    {
        var outputFolder = options.CurrentValue.OutputFolder;
        var root = RootFor(outputFolder);
        var unset = VariableReference().Matches(outputFolder ?? "")
            .FirstOrDefault(variable => Environment.GetEnvironmentVariable(variable.Groups["name"].Value) is null);
        if (unset is not null)
            return $"Recording:OutputFolder uses {unset.Value}, which is not set, so recordings go to {root}";

        if (File.Exists(root))
            return $"{root} is a file, so recordings cannot go there. Check Recording:OutputFolder";

        var drive = Path.GetPathRoot(root);
        if (!string.IsNullOrEmpty(drive) && !Directory.Exists(drive))
            return $"The drive {drive} is not available, so recordings cannot go to {root}";

        return null;
    }

    /// <summary>
    /// Lists the recordings under the root, the most recently recorded first. One bad item does not hide
    /// the rest: a folder ClassScribe may not open is passed over, and a folder or recording that cannot be
    /// read for another reason (a link to a drive that is gone, a file another program holds open) is
    /// left out with a warning. A root that cannot be read is still an error, because the setting needs fixing.
    /// </summary>
    public IReadOnlyList<RecordedClass> ListRecent(int count)
    {
        var root = Root;
        if (!Directory.Exists(root)) return [];

        return Directory.EnumerateDirectories(root)
            .SelectMany(ListRecordingsIn)
            .Select(files => TryDescribe(files, root))
            .OfType<RecordedClass>()
            .OrderByDescending(recorded => recorded.LastRecordedUtc)
            .Take(count)
            .ToList();
    }

    /// <summary>
    /// The recording started last in a folder, or null when the folder holds none. The folder is read
    /// strictly, so one that cannot be read is an error rather than a folder without recordings.
    /// </summary>
    public static RecordingFiles? FindNewest(string folder) =>
        NewestFirst(RecordingsAmong(Directory.EnumerateFiles(folder, "*.wav"))).FirstOrDefault();

    /// <summary>
    /// The recordings in <paramref name="folder"/>, such as one class's folder, newest first by the time each started,
    /// so that the first is the one <see cref="FindNewest"/> finds, each described as the menu's list describes it. The
    /// folder is read strictly, as by <see cref="FindNewest"/>, so one that cannot be read is an error; a recording in it
    /// that cannot be read now is left out with a warning, as in the list.
    /// </summary>
    public IReadOnlyList<RecordedClass> RecordingsIn(string folder)
    {
        var root = Root;
        return NewestFirst(RecordingsAmong(Directory.EnumerateFiles(folder, "*.wav")))
            .Select(files => TryDescribe(files, root))
            .OfType<RecordedClass>()
            .ToList();
    }

    /// <summary>
    /// Describes one recording as the list describes it (see <see cref="ListRecent"/>), such as one given by a path: its
    /// length, whether it has a transcript, an answer paid for and kept without one, and whether it is being recorded now.
    /// Its files are read strictly, so one that cannot be read now is an error, rather than left out as in the list.
    /// </summary>
    public RecordedClass Describe(RecordingFiles files) => Describe(files, Root);

    /// <summary>
    /// The newest answer ElevenLabs was paid for that did not become a transcript of the recording (see
    /// <see cref="RecordingFiles.FindKeptAnswers"/>), kept beside it or, when its own folder would not take it, in the
    /// recordings folder; null when there is none.
    /// </summary>
    public string? KeptAnswerOf(RecordingFiles files) => KeptAnswerOf(files, Root);

    /// <summary>
    /// True while the recording is being recorded, in this window or another: a recorder still writes its newest
    /// segment. The recorder holds that file open for writing and lets others read it, so the file then opens for
    /// reading only when others may go on writing to it. A file that does not open for reading at all is held by
    /// another program, such as a scanner, not by a recorder: that does not count, and transcribing the recording
    /// reports the real reason.
    /// </summary>
    public static bool IsBeingWritten(RecordingFiles files) => IsNewestHeldForWriting(files.FindSegments());

    /// <summary>
    /// The length of a segment, worked out from its size the way the preparer reads it: to the end of the
    /// file, whatever the header says. A file the recorder did not write counts as empty.
    /// </summary>
    public static TimeSpan MeasureSegment(string path)
    {
        Span<byte> header = stackalloc byte[WavSegmentWriter.HeaderSize];
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var read = file.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        if (read < header.Length || !WavSegmentWriter.TryReadHeader(header, out var sampleRate)) return TimeSpan.Zero;

        return TimeSpan.FromSeconds((file.Length - WavSegmentWriter.HeaderSize) / 2.0 / sampleRate);
    }

    /// <summary>
    /// Cleans a name for use as a folder and file name: the characters Windows forbids go, runs of
    /// spaces become one, and trailing dots go, as Windows would drop them from a folder's name. A name
    /// Windows keeps for a device, such as NUL or COM1, gets an underscore after it. The result is empty
    /// when nothing usable is left.
    /// </summary>
    public static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var kept = new string([.. name.Where(c => !invalid.Contains(c))]);
        var tidy = string.Join(' ', kept.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).TrimEnd('.', ' ');

        // Windows 11 reads "nul" as the device, and Windows 10 "nul.backup" and "nul .backup" too, so the part before the first dot decides.
        var device = tidy.Split('.')[0].TrimEnd(' ');
        return DeviceNames.Contains(device) ? tidy.Insert(device.Length, "_") : tidy;
    }

    /// <summary>The recordings the given .wav files belong to: one per distinct base name among the segment files.</summary>
    private static IEnumerable<RecordingFiles> RecordingsAmong(IEnumerable<string> wavFiles) =>
        wavFiles
            .Select(RecordingFiles.TryParse)
            .OfType<RecordingFiles>()
            .Distinct();

    /// <summary>Recordings ordered newest first by the time each started, as their names give it, and then by name.</summary>
    private static IOrderedEnumerable<RecordingFiles> NewestFirst(IEnumerable<RecordingFiles> recordings) =>
        recordings
            .OrderByDescending(files => files.StartedUtc ?? DateTimeOffset.MinValue)
            .ThenByDescending(files => files.BaseName, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The newest answer paid for that did not become a transcript of the recording, beside it or in the recordings
    /// folder <paramref name="root"/>; null when there is none (see <see cref="KeptAnswerOf(RecordingFiles)"/>).
    /// </summary>
    private static string? KeptAnswerOf(RecordingFiles files, string root) =>
        files.FindKeptAnswers()
            .Concat((files with { Folder = root }).FindKeptAnswers())
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

    /// <summary>
    /// True when the last of <paramref name="segments"/>, a recording's segments in recording order, is held open for
    /// writing by a recorder (see <see cref="IsBeingWritten"/>). False when there are none. Each look lets others delete
    /// the file meanwhile, so that it never keeps a recorder from removing a segment that no audio reached.
    /// </summary>
    private static bool IsNewestHeldForWriting(IReadOnlyList<string> segments) =>
        segments is [.., var newest] && !Opens(newest, FileShare.Read | FileShare.Delete) && Opens(newest, FileShare.ReadWrite | FileShare.Delete);

    /// <summary>
    /// True when <paramref name="path"/> opens for reading while others may only do what <paramref name="share"/>
    /// allows; false when the way another program holds the file open does not allow that.
    /// </summary>
    private static bool Opens(string path, FileShare share)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, share);
            return true;
        }
        catch (IOException ex) when (ex.HResult == SharingViolation)
        {
            // Deliberately not rethrown: a sharing violation is the answer this look is for, that another program holds
            // the file in a way that does not allow it. Any other failure is a real one, and goes to the caller.
            return false;
        }
    }

    /// <summary>
    /// The recordings in a folder under the root, for the list. A folder ClassScribe may not open counts as
    /// empty (see <see cref="Listing"/>); one that cannot be read for another reason is left out with a warning.
    /// </summary>
    private IReadOnlyList<RecordingFiles> ListRecordingsIn(string folder)
    {
        try
        {
            return [.. RecordingsAmong(Directory.EnumerateFiles(folder, "*.wav", Listing))];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Deliberately not rethrown: the list is only for picking a recording, and one folder that
            // cannot be read must not hide the recordings in the others.
            logger.LogWarning(ex, "Leaving the folder {Folder} out of the list of recordings: it cannot be read", folder);
            return [];
        }
    }

    /// <summary>
    /// Describes a recording for the list, or leaves it out with a warning when one of its files cannot be
    /// read now, as when another program holds it open without sharing. <paramref name="root"/> is the
    /// recordings folder, where an answer its own folder would not take is kept.
    /// </summary>
    private RecordedClass? TryDescribe(RecordingFiles files, string root)
    {
        try
        {
            return Describe(files, root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Deliberately not rethrown: the list is only for picking a recording, and one that cannot be
            // read must not hide the others. Transcribing it reads its files strictly and reports the problem.
            logger.LogWarning(ex, "Leaving {Recording} out of the list of recordings: one of its files cannot be read", files.BaseName);
            return null;
        }
    }

    /// <summary>
    /// Describes a recording from its files: its length and when it was last written to from its segments, whether
    /// it has a transcript, the newest answer paid for but kept without one, beside it or, when its own folder
    /// would not take it, in the recordings folder <paramref name="root"/>, and whether it is being recorded now.
    /// </summary>
    private static RecordedClass Describe(RecordingFiles files, string root)
    {
        var segments = files.FindSegments();
        var length = TimeSpan.Zero;
        var lastRecorded = DateTime.MinValue;
        foreach (var segment in segments)
        {
            length += MeasureSegment(segment);
            var written = File.GetLastWriteTimeUtc(segment);
            if (written > lastRecorded) lastRecorded = written;
        }

        return new RecordedClass(files, lastRecorded, length, files.HasTranscript, KeptAnswerOf(files, root), IsNewestHeldForWriting(segments));
    }

    /// <summary>A reference to an environment variable, such as %USERPROFILE%.</summary>
    [GeneratedRegex(@"%(?<name>[^%\\/]+)%", RegexOptions.CultureInvariant)]
    private static partial Regex VariableReference();
}
