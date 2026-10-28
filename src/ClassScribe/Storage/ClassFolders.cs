using System.Collections.Frozen;
using System.Globalization;
using System.Text.RegularExpressions;
using ClassScribe.Audio;
using ClassScribe.Settings;
using ClassScribe.Transcription;
using ClassScribe.YouTube;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClassScribe.Storage;

/// <summary>A recording, as the menu and the list command list it.</summary>
/// <param name="Files">The recording's files.</param>
/// <param name="LastRecordedUtc">When audio was last written to it, on either track.</param>
/// <param name="Length">Length of the recording: that of its longest track, every segment read to its end (see <see cref="ClassFolders.LengthOnDisk"/>).</param>
/// <param name="HasTranscript">True when it has been transcribed at least once.</param>
/// <param name="KeptAnswer">
/// The newest answer ElevenLabs was paid for that did not become a transcript (see
/// <see cref="RecordingFiles.FindKeptAnswers"/>), kept beside the recording or in the recordings folder; null when there is none.
/// </param>
/// <param name="BeingWritten">
/// True while it is being recorded, in this window or another: a recorder still writes the newest segment of one of its
/// tracks (see <see cref="ClassFolders.IsBeingWritten"/>). It is not transcribed until it has stopped.
/// </param>
/// <param name="Parts">
/// How many parts it was made in: one, and one more for each time it was taken up again after it stopped, as its resume
/// notes say (see <see cref="ClassMarks.PartsOf"/>).
/// </param>
/// <param name="HasMicrophone">True when your microphone was recorded with the class, on a track of its own: the .mic.wav files.</param>
/// <param name="OnDrive">
/// The recording's audio when it is on Google Drive alone, checked, and none of it is left on this laptop (see
/// <see cref="IAudioOnDrive"/>): its transcripts can be read and rebuilt, and transcribing it again needs the audio back.
/// Null while its audio is on this laptop.
/// </param>
/// <param name="Video">
/// The YouTube video it is the recording of, from its source file (see <see cref="RecordingFiles.Source"/>); null for a
/// class's recording, and for a video's whose source file cannot be read.
/// </param>
/// <param name="LessonVideo">What its files say of a lesson's finished video (see <see cref="ClassFolders.LessonVideoStateOf"/>): none for a recording without the camera.</param>
public sealed record RecordedClass(RecordingFiles Files, DateTime LastRecordedUtc, TimeSpan Length, bool HasTranscript, string? KeptAnswer, bool BeingWritten,
    int Parts = 1, bool HasMicrophone = false, AudioOnDrive? OnDrive = null, VideoSource? Video = null, LessonVideoState LessonVideo = LessonVideoState.None);

/// <summary>
/// What a recording's files say of a lesson's finished video (teach-video 6.9 and 6.15): decided from the files alone, so that
/// one rule covers a crash, a session Windows ended, a cancel, a time limit, a failure and a record-only lesson. A camera's
/// piece or a video's <c>.partial</c> held open for writing is never counted.
/// </summary>
public enum LessonVideoState
{
    /// <summary>No camera's piece and no finished video: a recording without the camera.</summary>
    None,

    /// <summary>The finished video is there, and no piece is newer than it.</summary>
    Made,

    /// <summary>Pieces and no finished video, or a piece newer than the video, as a continued lesson's: its video is still to be made.</summary>
    NotMade
}

/// <summary>
/// A recording that a new run of the same name may continue as its next part, because it stopped recently with neither a
/// transcript nor an answer from ElevenLabs paid for, as after a crash, a closed window or a restart (see
/// <see cref="ClassFolders.FindContinuable"/>).
/// </summary>
/// <param name="Files">The recording's files, which the run that continues it writes into.</param>
/// <param name="Length">Its length on disk (see <see cref="ClassFolders.LengthOnDisk"/>): where the run that continues it starts.</param>
/// <param name="StoppedUtc">When its audio was last written, on either track.</param>
/// <param name="Parts">How many parts it was made in so far (see <see cref="ClassMarks.PartsOf"/>).</param>
public sealed record ContinueCandidate(RecordingFiles Files, TimeSpan Length, DateTimeOffset StoppedUtc, int Parts);

/// <summary>
/// Where recordings and transcripts go: under Recording:OutputFolder, or under the recordings folder given for one run
/// (--output), a folder per name, the course, and in it a folder per class, named after the local date the class started
/// on, such as Mantiq\2026-10-17, with " (2)", " (3)" and so on for a second and third class of the name that day. Each
/// recording's files are stamped with its UTC start time (see <see cref="RecordingFiles"/>). Recordings made before this
/// layout, flat in the folder of their name, are read as before wherever recordings are looked for: below a course
/// folder, its own files and those of its date folders count, and no other folder, such as one of slides. Nothing is
/// ever moved or overwritten. It also finds a recording that a new run may continue as its next part, and lists the
/// recordings, those whose audio is on Google Drive alone included (see <paramref name="audioOnDrive"/>). It lives as long
/// as the app, and reads the setting at each use, so a change counts as soon as the settings have been read again.
/// </summary>
/// <param name="options">The Recording settings, read at each use.</param>
/// <param name="time">The clock, in the run's time zone, for the date folders.</param>
/// <param name="logger">Where problems with folders are said.</param>
/// <param name="audioOnDrive">What is known of recordings' audio kept on Google Drive alone; none when it is not given.</param>
public sealed partial class ClassFolders(IOptionsMonitor<RecordingOptions> options, TimeProvider time, ILogger<ClassFolders> logger, IAudioOnDrive? audioOnDrive = null)
{
    /// <summary>What is known of recordings' audio kept on Google Drive alone.</summary>
    private readonly IAudioOnDrive _onDrive = audioOnDrive ?? NoAudioOnDrive.Instance;

    /// <summary>How a date folder's name writes the local date a class started on: 2026-10-17.</summary>
    public const string DateFolderFormat = "yyyy-MM-dd";

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
    /// How a folder is looked into when it is read strictly, as for transcribing it: a folder ClassScribe may not open is an
    /// error, and hidden and system folders are seen, as Windows' own listing does it.
    /// </summary>
    private static readonly EnumerationOptions Strict = new() { IgnoreInaccessible = false, AttributesToSkip = 0 };

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
    /// The files for a recording named <paramref name="name"/> starting now: the folder of that name, the course, under
    /// <paramref name="root"/>, the recordings folder given for the run (--output), or under the root when that is null,
    /// shared with the course's other recordings; in it the date folder of the local date now, on the run's clock and in
    /// its time zone, or the first of its " (2)", " (3)" and so on that holds no recording yet (see
    /// <see cref="FreeDateFolder"/>); and files stamped with the UTC time. Both folders are created if needed. A recording
    /// started within the same second as another of the course takes the next second, so that no two share a base name.
    /// </summary>
    public RecordingFiles CreateRecording(string name, string? root = null)
    {
        var safe = SafeName(name);
        if (safe.Length == 0) throw new ArgumentException("A name is needed.", nameof(name));

        // The path of the folder Windows actually made, in case it tidied the name in a way SafeName did not.
        var course = Directory.CreateDirectory(Path.Combine(RootOf(root), safe)).FullName;
        var started = time.GetUtcNow();
        var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(started, time.LocalTimeZone).DateTime);
        var folder = Directory.CreateDirectory(Path.Combine(course, FreeDateFolder(course, date))).FullName;
        var files = RecordingFiles.ForNew(folder, safe, started);
        while (CourseHasBaseName(course, files.BaseName))
            files = RecordingFiles.ForNew(folder, safe, started = started.AddSeconds(1));

        return files;
    }

    /// <summary>
    /// The files for a recording named <paramref name="name"/> starting now in <paramref name="folder"/>, which a batch of
    /// YouTube videos shares (design 2.4): the batch's first video takes its date folder as a class does (see
    /// <see cref="CreateRecording"/>), and every later one goes into that same folder, made if it is gone, so that a batch of
    /// thirty videos never pushes the day's class to a thirty-first date folder. A recording started within the same second
    /// as another of the course takes the next second, as there; the course is the date folder's parent, or the folder itself
    /// when it is no date folder.
    /// </summary>
    public RecordingFiles CreateRecordingIn(string folder, string name)
    {
        var safe = SafeName(name);
        if (safe.Length == 0) throw new ArgumentException("A name is needed.", nameof(name));

        var full = Directory.CreateDirectory(folder).FullName;
        var trimmed = Path.TrimEndingDirectorySeparator(full);
        var course = IsDateFolderName(Path.GetFileName(trimmed)) && Path.GetDirectoryName(trimmed) is { } parent ? parent : trimmed;
        var started = time.GetUtcNow();
        var files = RecordingFiles.ForNew(trimmed, safe, started);
        while (CourseHasBaseName(course, files.BaseName))
            files = RecordingFiles.ForNew(trimmed, safe, started = started.AddSeconds(1));

        return files;
    }

    /// <summary>
    /// True when <paramref name="files"/> is a YouTube video's recording: it has a source file (see
    /// <see cref="RecordingFiles.Source"/>). The class machinery passes such a recording over: it is never offered to be
    /// continued, and never shown as a class.
    /// </summary>
    public static bool IsVideo(RecordingFiles files) => File.Exists(files.Source);

    /// <summary>
    /// The files for the transcript of <paramref name="sourcePath"/>, laid out as a recording's (see
    /// <see cref="CreateRecording"/>): a folder named after the file, under <paramref name="root"/>, the recordings folder
    /// given for the run (transcribe --output), or under the root when that is null, the date folder of the transcription
    /// in it, and files stamped with the time now. A file with nothing usable in its name, such as ".mp3", gets an
    /// "Untitled" folder.
    /// </summary>
    public RecordingFiles CreateForFile(string sourcePath, string? root = null)
    {
        var name = SafeName(Path.GetFileNameWithoutExtension(sourcePath));
        return CreateRecording(name.Length > 0 ? name : Untitled, root);
    }

    /// <summary>
    /// The files of the recording <paramref name="baseName"/> in <paramref name="folder"/>, for a run that continues it as
    /// its next part: the run's segments take the part numbers after the recording's own, and its marks and notes go into
    /// the same file. The request names the recording by its base name, such as Kalam_2026-10-15_23-00-02Z, and
    /// <see cref="OpenToContinue"/> finds its folder.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="baseName"/> is no recording's base name: one with a folder in it, or without a time stamp.</exception>
    /// <exception cref="FileNotFoundException">None of the recording's audio is in <paramref name="folder"/> any more, so there is nothing to continue.</exception>
    public RecordingFiles Open(string folder, string baseName)
    {
        var files = new RecordingFiles(folder, baseName);
        RefuseUnlessBaseName(baseName);

        if (!HasAudio(files))
            throw new FileNotFoundException($"There is no audio of the recording {baseName} left in {folder} to continue.", files.Segment(1));

        return files;
    }

    /// <summary>
    /// The files of the recording <paramref name="baseName"/> of <paramref name="name"/>, under the recordings folder
    /// <paramref name="root"/>, for a run that continues it as its next part (see <see cref="Open"/>), in whichever layout
    /// it was made: flat in the folder of its name, or in one of that folder's date folders, " (2)" included, whatever the
    /// date is now. The run keeps writing beside it. The folder of the name is found in any letter case, as Windows takes
    /// such names for one.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="baseName"/> is no recording's base name: one with a folder in it, or without a time stamp.</exception>
    /// <exception cref="FileNotFoundException">None of the recording's audio is under the folder of its name any more, so there is nothing to continue.</exception>
    public RecordingFiles OpenToContinue(string root, string name, string baseName)
    {
        RefuseUnlessBaseName(baseName);

        var safe = SafeName(name);
        var course = FolderOf(root, safe) ?? Path.Combine(root, safe);
        if (!Directory.Exists(course)) return Open(course, baseName);

        var found = RecordingFoldersIn(course).Select(folder => new RecordingFiles(folder, baseName)).FirstOrDefault(HasAudio);
        if (found is null)
            throw new FileNotFoundException($"There is no audio of the recording {baseName} left in {course} or its date folders to continue.", Path.Combine(course, baseName));

        return Open(found.Folder, baseName);
    }

    /// <summary>
    /// The recording a new run of <paramref name="name"/> may continue as its next part, or null when there is none. Only
    /// the newest recording of the name, in any letter case, under <paramref name="root"/> (the recordings folder given for
    /// the run, or the root when that is null) is ever offered, in either layout: in the date folders of the name's folder,
    /// or flat in it. A YouTube video's recording (see <see cref="IsVideo"/>) is passed over: it is never a class to
    /// continue, however recent. It is offered only when all of this holds: it has no transcript; no
    /// answer ElevenLabs was paid for is kept for it (see <see cref="KeptAnswerOf(RecordingFiles)"/>), beside it, in the
    /// root or in the recordings folder given for the run; it has the same tracks as a run of <paramref name="mode"/>,
    /// which the microphone's .mic.wav files tell; a lesson with the camera's pieces is offered only to a run with the camera
    /// (<paramref name="camera"/>; teach-video 6.8); it is not being written, on either track nor its newest piece; it has no upload copy, which
    /// is there while a recorder or a window transcribes it, after a transcription that was cut off, and after a failed one
    /// whose copy Recording:KeepUploadFile kept; its audio was last written no more than <paramref name="within"/> ago (zero
    /// never offers one); and it is shorter than <see cref="TranscriptionWorkflow.LongestRecording"/>, so that continued it
    /// can still be sent in one file. So a recording ElevenLabs has billed, or may be billing, is not offered: continued, it
    /// would be sent again whole and billed twice. The files cannot tell a recording of the class alone from one made in
    /// person, both a single main track, nor a recording made without the camera from a lesson whose camera never gave a
    /// piece (not connected, or its process never started), both without pieces, nor show a recorder in the moment between
    /// the end of its capture and the making of its upload copy: the run files can, for the caller to check.
    /// </summary>
    public ContinueCandidate? FindContinuable(string name, RecordingMode mode, TimeSpan within, string? root = null, bool camera = false)
    {
        var safe = SafeName(name);
        if (within <= TimeSpan.Zero || safe.Length == 0) return null;
        if (FolderOf(RootOf(root), safe) is not { } course) return null;

        // A YouTube video's recording shares the course's name and folders, but is never a class to continue.
        var newest = NewestFirst(RecordingsUnder(course))
            .FirstOrDefault(files => string.Equals(files.Name, safe, StringComparison.OrdinalIgnoreCase) && !IsVideo(files));
        if (newest is null) return null;

        var main = newest.FindSegments();
        var microphone = newest.FindSegments(Track.Microphone);
        var pieces = newest.FindCameraPieces().Select(piece => piece.Path).ToList();
        var stopped = LastWritten(main.Concat(microphone));
        var length = LongestTrack(main, microphone);

        // The workflow keeps an answer the recording's own folder would not take in the root, whichever recordings folder
        // the run used; one in the run's own recordings folder counts as well.
        var kept = KeptAnswerOf(newest, Root) ?? KeptAnswerOf(newest, RootOf(root));
        var unfit = newest.HasTranscript ? "it has a transcript"
            : kept is not null ? $"ElevenLabs' answer to it, which has been paid for, is kept in {kept}"
            : microphone.Count > 0 != (mode == RecordingMode.ClassAndMicrophone) ? "its tracks are not those of this run"
            : pieces.Count > 0 && !camera ? "it was recorded with the camera, and this run records no camera"
            : IsNewestHeldForWriting(main) || IsNewestHeldForWriting(microphone) || IsNewestHeldForWriting(pieces) ? "it is being recorded now"
            : File.Exists(newest.Upload) ? "its upload copy is there: it is being transcribed, or a transcription of it was cut off"
            : time.GetUtcNow() - stopped > within ? $"it stopped more than {Durations.Format(within)} ago"
            : length >= TranscriptionWorkflow.LongestRecording ? $"it is {Durations.Format(length)} long already"
            : null;
        if (unfit is not null)
        {
            logger.LogDebug("{Recording} is not offered to be continued: {Reason}", newest.BaseName, unfit);
            return null;
        }

        return new ContinueCandidate(newest, length, stopped, ClassMarks.PartsOf(newest));
    }

    /// <summary>
    /// The length of the recording on disk: that of its longest track, each track's segments read to the end of their
    /// files as the preparer reads them, so that it is exact even after a crash left a header behind. A run that
    /// continues the recording starts there.
    /// </summary>
    public TimeSpan LengthOnDisk(RecordingFiles files) => LongestTrack(files.FindSegments(), files.FindSegments(Track.Microphone));

    /// <summary>
    /// Every recording under <paramref name="root"/> (the recordings folder given for the listing, or the root when that
    /// is null), newest first by the time each started, for the list command. As in the menu's list, one bad item does not
    /// hide the rest (see <see cref="ListRecent"/>); a root that is not there holds none.
    /// </summary>
    public IReadOnlyList<RecordedClass> ListAll(string? root = null)
    {
        var folder = RootOf(root);
        if (!Directory.Exists(folder)) return [];

        return DescribeAllUnder(folder)
            .OrderByDescending(recorded => recorded.Files.StartedUtc ?? DateTimeOffset.MinValue)
            .ThenByDescending(recorded => recorded.LastRecordedUtc)
            .ThenByDescending(recorded => recorded.Files.BaseName, StringComparer.OrdinalIgnoreCase)
            .ToList();
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

        return DescribeAllUnder(root)
            .OrderByDescending(recorded => recorded.LastRecordedUtc)
            .Take(count)
            .ToList();
    }

    /// <summary>
    /// The recording started last in a folder, or null when the folder holds none. The recordings of a folder are those in
    /// it and in its date folders (see <see cref="RecordingFoldersIn"/>): a course's folder stands for its recordings in
    /// both layouts, a date folder for its own. The folders are read strictly, so one that cannot be read is an error
    /// rather than a folder without recordings.
    /// </summary>
    public static RecordingFiles? FindNewest(string folder) => NewestFirst(RecordingsUnder(folder)).FirstOrDefault();

    /// <summary>
    /// The recordings in <paramref name="folder"/> and its date folders, such as a course's or one class's, newest first by
    /// the time each started, so that the first is the one <see cref="FindNewest"/> finds, each described as the menu's
    /// list describes it. The folders are read strictly, as by <see cref="FindNewest"/>, so one that cannot be read is an
    /// error; a recording in them that cannot be read now is left out with a warning, as in the list.
    /// </summary>
    public IReadOnlyList<RecordedClass> RecordingsIn(string folder)
    {
        var root = Root;
        return NewestFirst(RecordingsUnder(folder))
            .Select(files => TryDescribe(files, root))
            .OfType<RecordedClass>()
            .ToList();
    }

    /// <summary>
    /// The folders whose recordings <paramref name="folder"/> stands for: itself, which holds the recordings of the old
    /// layout flat in the folder of their name, and then its date folders, by name (see <see cref="IsDateFolderName"/>).
    /// Its other folders, such as one of slides, are passed over. The folder is read strictly, so one that cannot be read
    /// is an error.
    /// </summary>
    public static IReadOnlyList<string> RecordingFoldersIn(string folder) => [folder, .. DateFoldersIn(folder, Strict)];

    /// <summary>The name of the date folder of <paramref name="date"/>: 2026-10-17, and 2026-10-17 (2) from the second class of a name that day.</summary>
    public static string DateFolderName(DateOnly date, int copy = 1)
    {
        var day = date.ToString(DateFolderFormat, CultureInfo.InvariantCulture);
        return copy == 1 ? day : string.Create(CultureInfo.InvariantCulture, $"{day} ({copy})");
    }

    /// <summary>
    /// True when <paramref name="name"/> is a date folder's name: the date of a real day as yyyy-MM-dd, alone or followed by
    /// a space and a number in brackets, as in 2026-10-17 (2). Any other name, such as Slides, is not.
    /// </summary>
    public static bool IsDateFolderName(string name) => DateFolderMatch(name) is not null;

    /// <summary>
    /// The name of <paramref name="folder"/> when it is the date folder of a later class of its day, such as
    /// 2026-10-17 (2), which the list shows to tell the day's classes apart; null for any other folder, the day's first
    /// date folder included.
    /// </summary>
    public static string? LaterClassOfItsDay(string folder) =>
        Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)) is var name && DateFolderMatch(name) is { } match && match.Groups["copy"].Success ? name : null;

    /// <summary>
    /// The folders that hold the copy of a recording kept in <paramref name="recordingFolder"/> under ClassScribe's own
    /// folder in Google Drive, from the top down, so that Drive mirrors the laptop: for a date folder its course's name and
    /// its own, " (2)" included, as Mantiq and 2026-10-17; for any other folder, as one of the old layout, its own name
    /// alone, as Kalam. Only the folder's own name and its parent's count, whatever the recordings folder is.
    /// </summary>
    public static IReadOnlyList<string> DriveLevelsOf(string recordingFolder)
    {
        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(recordingFolder));
        var own = Path.GetFileName(folder);
        if (own.Length == 0) return [];

        return IsDateFolderName(own) && Path.GetFileName(Path.GetDirectoryName(folder)) is { Length: > 0 } course ? [course, own] : [own];
    }

    /// <summary>
    /// <paramref name="folder"/>'s full path with each folder's name written as it is on disk, as Mantiq when it was typed
    /// mantiq, so that Drive's folders are named as the laptop's are, whatever was typed. A folder that is not there, or
    /// that cannot be looked into, keeps the name as given.
    /// </summary>
    public static string AsOnDisk(string folder)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        var root = Path.GetPathRoot(full) ?? "";
        var current = root;
        foreach (var name in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, OnDiskName(current, name));
        }

        return current.Length == 0 ? full : current;
    }

    /// <summary>
    /// Every recording in <paramref name="folder"/> and its date folders (see <see cref="RecordingFoldersIn"/>), known by any
    /// of its files (see <see cref="RecordingFiles.TryParse"/>), so that one whose audio is on Google Drive alone, or a file
    /// transcribed on its own, counts too; newest first by the time each started. Files that are not a recording's, such as a
    /// class's PDFs, are passed over. The folders are read strictly, so one that cannot be read is an error.
    /// </summary>
    public static IReadOnlyList<RecordingFiles> RecordingsByAnyFileIn(string folder) =>
        [
            .. NewestFirst(RecordingFoldersIn(folder)
                .SelectMany(recordings => Directory.EnumerateFiles(recordings, "*", Strict))
                .Select(RecordingFiles.TryParse)
                .OfType<RecordingFiles>()
                .DistinctBy(files => Path.Combine(files.Folder, files.BaseName), StringComparer.OrdinalIgnoreCase))
        ];

    /// <summary>The audio of <paramref name="files"/> that is on Google Drive alone, checked, and gone from this laptop; null when none of it is.</summary>
    public AudioOnDrive? AudioOnDriveOf(RecordingFiles files) => _onDrive.Find(files);

    /// <summary>
    /// The recording started last in <paramref name="folder"/> and its date folders whose audio is all on Google Drive alone,
    /// with that audio; null when there is none. Transcribing the folder would otherwise pass it over for an older one.
    /// </summary>
    public (RecordingFiles Files, AudioOnDrive Audio)? NewestWithAudioOnDriveIn(string folder) =>
        RecordingsByAnyFileIn(folder)
            .Where(files => !HasAudio(files))
            .Select(files => (Files: files, Audio: _onDrive.Find(files)))
            .Where(found => found.Audio is { AllOfIt: true })
            .Select(found => ((RecordingFiles Files, AudioOnDrive Audio)?)(found.Files, found.Audio!))
            .FirstOrDefault();

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
    /// True while the recording is being recorded, in this window or another: a recorder still writes the newest
    /// segment of one of its tracks, or a camera process its newest piece or the lesson's video (see
    /// <see cref="IsLessonVideoBeingMade"/>). Each holds that file open for writing and lets others read it, so the file then
    /// opens for reading only when others may go on writing to it. A file that does not open for reading at all is held by
    /// another program, such as a scanner, not by a recorder: that does not count, and transcribing the recording reports
    /// the real reason.
    /// </summary>
    public static bool IsBeingWritten(RecordingFiles files) =>
        IsNewestHeldForWriting(files.FindSegments()) || IsNewestHeldForWriting(files.FindSegments(Track.Microphone))
        || IsNewestHeldForWriting([.. files.FindCameraPieces().Select(piece => piece.Path)]) || IsLessonVideoBeingMade(files);

    /// <summary>
    /// True while a camera process makes the lesson's video: it holds the video's <see cref="RecordingFiles.LessonVideoPartial"/>
    /// open for writing, others allowed to read it. A <c>.partial</c> a crash left, which nothing holds, does not count.
    /// </summary>
    public static bool IsLessonVideoBeingMade(RecordingFiles files) =>
        File.Exists(files.LessonVideoPartial) && IsNewestHeldForWriting([files.LessonVideoPartial]);

    /// <summary>
    /// What the files of <paramref name="files"/> say of the lesson's finished video (see <see cref="LessonVideoState"/>): its
    /// camera's pieces, the newest left out while a camera process still writes it, against its finished video. Made when
    /// the video is there and no piece is newer; not made when pieces are there without it, or one is newer; none without
    /// either. A video's <c>.partial</c> is no video.
    /// </summary>
    public static LessonVideoState LessonVideoStateOf(RecordingFiles files)
    {
        var pieces = files.FindCameraPieces().Select(piece => piece.Path).ToList();
        if (IsNewestHeldForWriting(pieces)) pieces.RemoveAt(pieces.Count - 1);

        var video = new FileInfo(files.LessonVideo);
        if (!video.Exists) return pieces.Count > 0 ? LessonVideoState.NotMade : LessonVideoState.None;

        return pieces.Any(piece => File.GetLastWriteTimeUtc(piece) > video.LastWriteTimeUtc) ? LessonVideoState.NotMade : LessonVideoState.Made;
    }

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

    /// <summary>
    /// The recordings the given .wav files belong to: one per distinct base name among the segments of the main track. An
    /// upload copy or a microphone's segment alone makes no recording.
    /// </summary>
    private static IEnumerable<RecordingFiles> RecordingsAmong(IEnumerable<string> wavFiles) =>
        wavFiles
            .Select(RecordingFiles.TryParseSegment)
            .OfType<RecordingFiles>()
            .Distinct();

    /// <summary>
    /// The recordings in <paramref name="folder"/> and in its date folders (see <see cref="RecordingFoldersIn"/>), in no
    /// particular order, every folder read strictly.
    /// </summary>
    private static IEnumerable<RecordingFiles> RecordingsUnder(string folder) =>
        RecordingsAmong(RecordingFoldersIn(folder).SelectMany(recordings => Directory.EnumerateFiles(recordings, "*.wav")));

    /// <summary>The date folders in <paramref name="folder"/>, by name, read as <paramref name="how"/> says; its other folders are passed over.</summary>
    private static IEnumerable<string> DateFoldersIn(string folder, EnumerationOptions how) =>
        Directory.EnumerateDirectories(folder, "*", how)
            .Where(dated => IsDateFolderName(Path.GetFileName(dated)))
            .Order(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The name of the date folder a new recording of the course in <paramref name="course"/> goes to, on
    /// <paramref name="date"/>: the date's own, or the first of its " (2)", " (3)" and so on that is free. A folder is free
    /// when it is not there yet, or holds no class, being empty, holding only a false start's log and marks, or holding
    /// only other files and folders, such as the class's PDFs, which stay as they are. A file of that name is not free, and nor is a folder ClassScribe
    /// may not look into, which could not be written either.
    /// </summary>
    private string FreeDateFolder(string course, DateOnly date)
    {
        for (var copy = 1; ; copy++)
        {
            var name = DateFolderName(date, copy);
            if (IsFree(Path.Combine(course, name))) return name;
        }
    }

    /// <summary>
    /// True when a new recording may go into the date folder <paramref name="folder"/>: it is not there yet, or it is a
    /// folder that holds no class (see <see cref="IsClass"/>), such as one with only a false start's log and marks, and no
    /// YouTube video's audio being written into it (see <see cref="IsAudioBeingConverted"/>). A file of that name is not free,
    /// and nor is a folder that cannot be looked into, which is said.
    /// </summary>
    private bool IsFree(string folder)
    {
        if (File.Exists(folder)) return false;
        if (!Directory.Exists(folder)) return true;

        try
        {
            return !Directory.EnumerateFiles(folder).Any(file => IsClass(file) || IsAudioBeingConverted(file));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Deliberately not rethrown: a date folder that cannot be looked into could not be recorded into either, and
            // must not cost the class; the next date folder's name is taken instead, and this says why.
            logger.LogWarning("The folder {Folder} cannot be looked into ({Reason}), so this recording goes to the next date folder", folder, ex.Message);
            return false;
        }
    }

    /// <summary>
    /// True when <paramref name="file"/> shows that a class was recorded: a file of a recording (see
    /// <see cref="RecordingFiles.TryParse"/>) that is its audio on either track, a transcript or an answer from ElevenLabs,
    /// a camera's piece or a lesson's finished video, or a log or marks of a recording whose audio is on Google Drive alone
    /// (see <see cref="IAudioOnDrive"/>). A log, marks and a glossary record alone, as a start that recorded nothing leaves,
    /// an upload copy, a lesson video's .partial, and a YouTube video's source file, which a video whose fetching failed
    /// leaves, show no class.
    /// </summary>
    private bool IsClass(string file)
    {
        if (RecordingFiles.TryParse(file) is not { } files) return false;

        var kind = Path.GetFileName(file)[files.BaseName.Length..];
        var trace = kind.Equals(".log", StringComparison.OrdinalIgnoreCase)
            || kind.Equals(".marks.txt", StringComparison.OrdinalIgnoreCase)
            || kind.Equals(".glossary.json", StringComparison.OrdinalIgnoreCase)
            || kind.Equals(".upload.wav", StringComparison.OrdinalIgnoreCase)
            || kind.Equals(".mp4.partial", StringComparison.OrdinalIgnoreCase)
            || kind.Equals(".source.json", StringComparison.OrdinalIgnoreCase);
        return !trace || _onDrive.Find(files) is not null;
    }

    /// <summary>
    /// True when <paramref name="file"/> is an unfinished part of a downloaded YouTube video's audio, &lt;segment&gt;.wav.partial,
    /// which its batch writes before it renames it to its segment's name: the date folder is taken by that video, so that a
    /// class starting meanwhile takes its own. One a crash left is deleted by the next YouTube batch.
    /// </summary>
    private static bool IsAudioBeingConverted(string file) =>
        file.EndsWith(".wav.partial", StringComparison.OrdinalIgnoreCase) && RecordingFiles.TryParseSegment(file[..^".partial".Length]) is not null;

    /// <summary>
    /// True when a file of the recording <paramref name="baseName"/> is in <paramref name="course"/> or in any of its date
    /// folders, so that a recording started within the same second as another takes the next second. Folders that cannot be
    /// looked into are passed over: nothing of the course can be written there anyway.
    /// </summary>
    private bool CourseHasBaseName(string course, string baseName) =>
        new[] { course }.Concat(DateFoldersIn(course, Listing)).Any(folder => HoldsBaseName(folder, baseName));

    /// <summary>
    /// True when a file of the recording <paramref name="baseName"/> is in <paramref name="folder"/>. A folder that cannot be
    /// looked into, or that leads nowhere, holds none for this look.
    /// </summary>
    private bool HoldsBaseName(string folder, string baseName)
    {
        try
        {
            return Directory.EnumerateFiles(folder, $"{baseName}*", Listing)
                .Any(file => string.Equals(RecordingFiles.TryParse(file)?.BaseName, baseName, StringComparison.OrdinalIgnoreCase));
        }
        catch (IOException ex)
        {
            // Deliberately not rethrown: this look only keeps two recordings of a course from sharing a name, and a date
            // folder that leads nowhere, such as a link to a drive that is gone, must not keep the class from starting;
            // nothing can have been written there, so it holds no file of the name.
            logger.LogDebug(ex, "Could not look for files of {Recording} in {Folder}", baseName, folder);
            return false;
        }
    }

    /// <summary>
    /// The name of the folder <paramref name="name"/> in <paramref name="parent"/> as it is written on disk; as given when it
    /// is not there, or cannot be looked up.
    /// </summary>
    private static string OnDiskName(string parent, string name)
    {
        try
        {
            return parent.Length > 0 && Directory.Exists(parent)
                ? Directory.EnumerateDirectories(parent, name).Select(Path.GetFileName).FirstOrDefault(found => string.Equals(found, name, StringComparison.OrdinalIgnoreCase)) ?? name
                : name;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Deliberately not rethrown: the name on disk only spells Drive's folders as the laptop's; a folder that cannot be
            // looked into keeps the name as it was given.
            return name;
        }
    }

    /// <summary>Throws when <paramref name="baseName"/> is no recording's base name: empty, with a folder in it, with a character Windows forbids, or without a time stamp.</summary>
    /// <exception cref="ArgumentException"><paramref name="baseName"/> is no recording's base name.</exception>
    private static void RefuseUnlessBaseName(string baseName)
    {
        if (baseName.Length == 0 || Path.GetFileName(baseName) != baseName || baseName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || new RecordingFiles("", baseName).StartedUtc is null)
            throw new ArgumentException($"\"{baseName}\" is not the name of a recording, such as Kalam_2026-10-15_23-00-02Z.", nameof(baseName));
    }

    /// <summary>True when any audio of the recording is in its folder, on either track.</summary>
    private static bool HasAudio(RecordingFiles files) => files.FindSegments().Count > 0 || files.FindSegments(Track.Microphone).Count > 0;

    /// <summary>
    /// The recordings folder for one run or listing: <paramref name="root"/>, the folder given for it, read as
    /// Recording:OutputFolder is read (see <see cref="RootFor"/>), or the root when none is given.
    /// </summary>
    private string RootOf(string? root) => string.IsNullOrWhiteSpace(root) ? Root : RootFor(root);

    /// <summary>
    /// The folder of the recordings of <paramref name="name"/> under <paramref name="root"/>, with its name as it is on
    /// disk, which may differ in letter case, as Windows takes such names for one; null when there is none. The name is
    /// compared with each folder's rather than handed to Windows as a search pattern, which reads some characters its own
    /// way. Of two folders that differ only in letter case, as a folder set to tell case apart can hold, the one spelled
    /// exactly as the name is taken.
    /// </summary>
    private static string? FolderOf(string root, string name)
    {
        if (!Directory.Exists(root)) return null;

        var named = Directory.EnumerateDirectories(root)
            .Where(folder => string.Equals(Path.GetFileName(folder), name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return named.FirstOrDefault(folder => string.Equals(Path.GetFileName(folder), name, StringComparison.Ordinal)) ?? named.FirstOrDefault();
    }

    /// <summary>When audio was last written to any of <paramref name="segments"/>; the earliest time there is when there are none.</summary>
    private static DateTimeOffset LastWritten(IEnumerable<string> segments) =>
        segments.Select(segment => new DateTimeOffset(File.GetLastWriteTimeUtc(segment), TimeSpan.Zero)).DefaultIfEmpty(DateTimeOffset.MinValue).Max();

    /// <summary>The length of the longer of two tracks, each the sum of its segments read to their ends (see <see cref="MeasureSegment"/>).</summary>
    private static TimeSpan LongestTrack(IReadOnlyList<string> main, IReadOnlyList<string> microphone)
    {
        var mainLength = main.Aggregate(TimeSpan.Zero, (length, segment) => length + MeasureSegment(segment));
        var microphoneLength = microphone.Aggregate(TimeSpan.Zero, (length, segment) => length + MeasureSegment(segment));
        return mainLength >= microphoneLength ? mainLength : microphoneLength;
    }

    /// <summary>
    /// Every recording in the folders under <paramref name="root"/>, the courses, and in their date folders, described for
    /// a list, in no particular order. One bad item does not hide the rest (see <see cref="ListRecordingsUnder"/>,
    /// <see cref="ListRecordingsIn"/> and <see cref="TryDescribe"/>); a root that cannot be read is an error.
    /// </summary>
    private IEnumerable<RecordedClass> DescribeAllUnder(string root) =>
        Directory.EnumerateDirectories(root)
            .SelectMany(ListRecordingsUnder)
            .Select(files => TryDescribe(files, root))
            .OfType<RecordedClass>();

    /// <summary>
    /// The recordings of a course folder under the root, for the list: those flat in it, as the old layout put them, and
    /// those in each of its date folders. A course or date folder ClassScribe may not open counts as empty (see
    /// <see cref="Listing"/>); one that cannot be read for another reason is left out with a warning.
    /// </summary>
    private IEnumerable<RecordingFiles> ListRecordingsUnder(string course)
    {
        List<string> dated;
        try
        {
            dated = [.. DateFoldersIn(course, Listing)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Deliberately not rethrown: the list is only for picking a recording, and one folder that
            // cannot be read must not hide the recordings in the others.
            logger.LogWarning(ex, "Leaving the folder {Folder} out of the list of recordings: it cannot be read", course);
            return [];
        }

        return new[] { course }.Concat(dated).SelectMany(ListRecordingsIn);
    }

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
    /// The recordings in a folder under the root, for the list: those with audio of the class on this laptop, and those
    /// whose audio is all on Google Drive alone (see <see cref="IAudioOnDrive"/>). A folder ClassScribe may not open counts
    /// as empty (see <see cref="Listing"/>); one that cannot be read for another reason is left out with a warning.
    /// </summary>
    private IReadOnlyList<RecordingFiles> ListRecordingsIn(string folder)
    {
        try
        {
            var paths = Directory.EnumerateFiles(folder, "*", Listing).ToList();
            var withAudio = RecordingsAmong(paths.Where(path => path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))).ToList();
            if (_onDrive is NoAudioOnDrive) return withAudio;

            var onDriveAlone = paths
                .Select(RecordingFiles.TryParse)
                .OfType<RecordingFiles>()
                .Distinct()
                .Where(files => !withAudio.Contains(files) && !HasAudio(files) && _onDrive.Find(files) is { AllOfIt: true });
            return [.. withAudio, .. onDriveAlone];
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
    /// Describes a recording from its files: its length and when it was last written to from the segments of both its
    /// tracks, whether it has a transcript, the newest answer paid for but kept without one, beside it or, when its own
    /// folder would not take it, in the recordings folder <paramref name="root"/>, whether it is being recorded now, the
    /// parts it was made in, whether it has the microphone's track, and what its files say of a lesson's video. A recording
    /// whose audio is all on Google Drive alone takes its length from what was measured when the audio went up, and counts
    /// as last written at its end. A YouTube video's recording carries its source (see <see cref="VideoOf"/>).
    /// </summary>
    private RecordedClass Describe(RecordingFiles files, string root)
    {
        var main = files.FindSegments();
        var microphone = files.FindSegments(Track.Microphone);
        var lessonVideo = LessonVideoStateOf(files);
        if (main.Count == 0 && microphone.Count == 0 && _onDrive.Find(files) is { AllOfIt: true } onDrive)
        {
            var ended = (files.StartedUtc ?? DateTimeOffset.UnixEpoch) + onDrive.Length;
            return new RecordedClass(files, ended.UtcDateTime, onDrive.Length, files.HasTranscript, KeptAnswerOf(files, root), BeingWritten: false,
                ClassMarks.PartsOf(files), onDrive.HasMicrophone, onDrive, VideoOf(files), lessonVideo);
        }

        return new RecordedClass(files, LastWritten(main.Concat(microphone)).UtcDateTime, LongestTrack(main, microphone), files.HasTranscript,
            KeptAnswerOf(files, root), IsNewestHeldForWriting(main) || IsNewestHeldForWriting(microphone), ClassMarks.PartsOf(files), microphone.Count > 0,
            Video: VideoOf(files), LessonVideo: lessonVideo);
    }

    /// <summary>
    /// The YouTube video <paramref name="files"/> is the recording of, from its source file; null for a class's recording. A
    /// source file that is not one leaves the recording described without it, with a warning that names the file.
    /// </summary>
    private VideoSource? VideoOf(RecordingFiles files)
    {
        try
        {
            return VideoSourceFile.Read(files.Source);
        }
        catch (InvalidDataException ex)
        {
            // Deliberately not rethrown: the source only adds the video's title to the recording's line; a file that is not
            // one must not hide the recording, and this says which file it is.
            logger.LogWarning("{Problem}; {Recording} is listed without its video's title", ex.Message, files.BaseName);
            return null;
        }
    }

    /// <summary>A reference to an environment variable, such as %USERPROFILE%.</summary>
    [GeneratedRegex(@"%(?<name>[^%\\/]+)%", RegexOptions.CultureInvariant)]
    private static partial Regex VariableReference();

    /// <summary>The match of <paramref name="name"/> as a date folder's name, its date that of a real day; null when it is no date folder's name.</summary>
    private static Match? DateFolderMatch(string name) =>
        DateFolderShape().Match(name) is { Success: true } match
        && DateOnly.TryParseExact(match.Groups["date"].Value, DateFolderFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            ? match
            : null;

    /// <summary>
    /// The shape of a date folder's name: four, two and two digits for the date, then for a later class of the day a space
    /// and a number in brackets. Digits are 0 to 9 only, and \z ends the match at the very end.
    /// </summary>
    [GeneratedRegex(@"^(?<date>[0-9]{4}-[0-9]{2}-[0-9]{2})(?<copy> \([0-9]{1,9}\))?\z", RegexOptions.CultureInvariant)]
    private static partial Regex DateFolderShape();
}
