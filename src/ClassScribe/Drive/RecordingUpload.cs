using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using ClassScribe.Recorder;
using ClassScribe.Settings;
using ClassScribe.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClassScribe.Drive;

/// <summary>Where one recording stands after an upload of it.</summary>
public enum RecordingOutcome
{
    /// <summary>Every file of it that goes up is on Drive, checked.</summary>
    Done,

    /// <summary>Something of it did not go up; it waits in the queue, with what went wrong.</summary>
    Waiting,

    /// <summary>It is being recorded or transcribed now, so nothing of it went up; it waits in the queue.</summary>
    InProgress,

    /// <summary>
    /// Its folder is not there now, on a drive that is, and not all of it went up before: it was moved or deleted by hand. It
    /// is taken out of the queue (see <see cref="UploadState.Missing"/>), and nothing waits for it.
    /// </summary>
    Missing
}

/// <summary>
/// The manifest of a recording could not be written while it went up (see <see cref="UploadManifests.Write"/>): an upload
/// that cannot keep track of itself must not go on, so this ends it, in words of its own, rather than as a file of the
/// recording that could not be read.
/// </summary>
/// <param name="baseName">The recording's base name.</param>
/// <param name="path">The manifest that could not be written.</param>
/// <param name="inner">Why Windows would not write it.</param>
public sealed class UploadNotTrackedException(string baseName, string path, Exception inner)
    : Exception($"ClassScribe could not keep track of the upload of {baseName} in {path} ({inner.Message.TrimEnd().TrimEnd('.')}), so the upload stopped; it waits in the queue for the next one.", inner);

/// <summary>What an upload of one recording came to.</summary>
/// <param name="Files">The recording.</param>
/// <param name="Outcome">Where it stands.</param>
/// <param name="Problem">What kept it from being done, in plain words; null when it is done.</param>
public sealed record RecordingUploadResult(RecordingFiles Files, RecordingOutcome Outcome, string? Problem);

/// <summary>
/// Uploads one recording to Google Drive, into the folder that mirrors its own on the laptop (see
/// <see cref="ClassFolders.DriveLevelsOf"/>): every file <see cref="RecordingFiles.TryParse"/> takes for one of its own, but
/// the temporary upload copy, and with Drive:Upload WithoutAudio not its .wav files; files in the folder that are not the
/// recording's, such as the class's PDFs, are never touched. In this order: the transcripts, ElevenLabs' answers, the
/// marks, the class's audio, the microphone's, the log, and last a lesson's finished video, only with Drive:LessonVideos on
/// (see <see cref="SendsLessonVideo"/>), streamed as the audio is; never the camera's pieces. Each file's MD5 is worked out once and kept in the manifest
/// (<see cref="UploadManifests"/>) with its size and the time it was last written, and after its last piece the copy on
/// Drive is checked against Drive's own size and MD5; one that differs is sent again once, as a new revision. A file already
/// checked goes up no more; a text file that has grown since (a log, the marks, an answer laid out anew), and a lesson's
/// video made again since (teach-video 8), go up as a new revision of the same file on Drive; nothing else on Drive is ever
/// replaced, deleted or trashed. A file deleted from the
/// laptop after its copy was checked is safe on Drive, not missing. With no manifest, Drive's files in the folder are
/// adopted by name and MD5, so nothing goes up twice. A recording being recorded or transcribed now is passed over and left
/// in the queue, and so is one whose audio another program holds open. One whose folder is not there waits only while its
/// drive is not there either; otherwise it was moved or deleted by hand, and it is done when all of it went up before, or
/// taken out of the queue (see <see cref="FolderNotThere"/>). One put in the queue again while it goes up, as by another
/// ClassScribe that found the lock held, is left waiting at the end, to be taken once more (see
/// <see cref="UploadManifest.TimesQueued"/>). While this process still writes the recording's log,
/// as a recorder does after its class, the last line, where the recording is on Drive, is written just before the log goes
/// up, so that Drive's copy of the log holds it (see <see cref="RecorderLog.NamedFile"/>). One per run.
/// </summary>
public sealed partial class RecordingUpload(
    DriveClient drive,
    DriveFolders folders,
    ResumableUpload upload,
    DriveRetry retry,
    UploadManifests manifests,
    RunJournal journal,
    RecorderLog log,
    IOptionsSnapshot<DriveOptions> options,
    IOptionsSnapshot<YouTubeOptions> youTube,
    TimeProvider time,
    ILogger<RecordingUpload> logger)
{
    /// <summary>How long a recording waits after Drive said it takes nothing more, before an upload after a class tries it again.</summary>
    public static readonly TimeSpan WaitWhenFull = TimeSpan.FromDays(1);

    /// <summary>ERROR_SHARING_VIOLATION as an HRESULT: another program holds the file in a way that does not allow this open.</summary>
    private const int SharingViolation = unchecked((int)0x80070020);

    /// <summary>How much of a file is read at a time to work out its MD5.</summary>
    private const int Md5Buffer = 1024 * 1024;

    /// <summary>The calls to Drive, for the steps of a run.</summary>
    private DriveClient Drive => drive;

    /// <summary>ClassScribe's folders on Drive, for the steps of a run.</summary>
    private DriveFolders Folders => folders;

    /// <summary>The resumable upload of one file, for the steps of a run.</summary>
    private ResumableUpload Upload => upload;

    /// <summary>How a step is asked again, for the steps of a run.</summary>
    private DriveRetry Retry => retry;

    /// <summary>The manifests, for the steps of a run.</summary>
    private UploadManifests Manifests => manifests;

    /// <summary>The clock, for the steps of a run.</summary>
    private TimeProvider Time => time;

    /// <summary>Where the steps of a run say what happens.</summary>
    private ILogger Logger => logger;

    /// <summary>How long the last line may take to reach the recording's log before the log goes up without waiting longer.</summary>
    private static readonly TimeSpan LastLinePatience = TimeSpan.FromSeconds(5);

    /// <summary>True when <paramref name="path"/> is the recording's log, and this process writes it now.</summary>
    private bool IsLogWrittenHere(string path) =>
        log.NamedFile is { } named && string.Equals(Path.GetFullPath(named), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);

    /// <summary>Waits a little for every line written so far to reach its file, so that the log read next holds them.</summary>
    private void FlushTheLog()
    {
        if (!log.Flush(LastLinePatience)) logger.LogDebug("The log was still being written when it went up to Google Drive; the lines after this go up with the next upload");
    }

    /// <summary>
    /// Uploads <paramref name="files"/> and says where it stands. <paramref name="beforePiece"/> runs before each piece and
    /// may hold the upload up, as while a class records. A failure that ends the whole upload (Google out of reach after
    /// asking again, Drive full, the sign-in gone) is thrown, with the recording left in the queue and the failure noted;
    /// one file Drive refuses only keeps the recording waiting, and the others go up.
    /// </summary>
    public async Task<RecordingUploadResult> UploadAsync(RecordingFiles files, Func<CancellationToken, Task> beforePiece, CancellationToken cancellationToken)
    {
        if (Busy(files) is { } busy)
        {
            manifests.KeepWaiting(files);
            logger.LogInformation("{Recording} {Busy}; it goes up once it has stopped", files.BaseName, busy);
            return new RecordingUploadResult(files, RecordingOutcome.InProgress, $"{files.BaseName} {busy}");
        }

        if (!Directory.Exists(files.Folder)) return FolderNotThere(files);

        var run = new Run(this, files, manifests.Read(files) ?? new UploadManifest(files.Folder, files.BaseName) { QueuedUtc = time.GetUtcNow() }, beforePiece);
        try
        {
            return await run.UploadAsync(cancellationToken);
        }
        catch (GoogleDriveException ex)
        {
            run.Failed(ex.Message, ex.Problem == DriveProblem.Full ? time.GetUtcNow() + WaitWhenFull : null);
            logger.LogDebug("The upload of {Recording} stopped: {Problem}", files.BaseName, ex.Message);
            throw;
        }
        catch (OperationCanceledException)
        {
            run.Failed("The upload was stopped before it was done.", null);
            logger.LogDebug("The upload of {Recording} was stopped; it goes on from where it stopped next time", files.BaseName);
            throw;
        }
    }

    /// <summary>
    /// What becomes of <paramref name="files"/> when its folder is not there. On a drive that is not there now, as a USB drive
    /// that is out, it waits in the queue for an upload that finds it, with why. On a drive that is there, the folder was
    /// moved or deleted by hand: when every file of it went up before and was checked, it is done, safe on Drive; otherwise it
    /// is taken out of the queue, which is said, with how to upload it from where it is now. Google is asked nothing, and
    /// nothing on Drive is touched.
    /// </summary>
    private RecordingUploadResult FolderNotThere(RecordingFiles files)
    {
        var drive = Path.GetPathRoot(Path.GetFullPath(files.Folder));
        if (string.IsNullOrEmpty(drive) || !Directory.Exists(drive))
        {
            var away = $"The folder of {files.BaseName}, {files.Folder}, is not there now: its drive, {drive}, is not there either";
            manifests.KeepWaiting(files, away);
            logger.LogInformation("{Away}; it waits for an upload that finds it", away);
            return new RecordingUploadResult(files, RecordingOutcome.Waiting, away);
        }

        var manifest = manifests.Read(files);
        if (manifest is { Files: [_, ..] } && manifest.Files.All(file => file.IsVerified))
        {
            manifests.Write(manifest with { State = UploadState.Done, LastError = null, NextAttemptUtc = null, DoneUtc = time.GetUtcNow() });
            logger.LogInformation("The folder of {Recording}, {Folder}, is not there now, but every file of it went up to Google Drive before and was checked: it is in {Location}",
                files.BaseName, files.Folder, manifest.DriveLocation);
            return new RecordingUploadResult(files, RecordingOutcome.Done, null);
        }

        var gone = $"The folder of {files.BaseName}, {files.Folder}, is not there now: it was moved or deleted";
        if (manifest is not null)
            manifests.Write(manifest with { State = UploadState.Missing, LastError = gone, LastTriedUtc = time.GetUtcNow(), NextAttemptUtc = null, DoneUtc = null });
        logger.LogWarning("{Gone}, so it is taken out of the queue for Google Drive. If it was moved, upload it from where it is now: menu choice 9 > F, or ClassScribe drive upload \"<its folder>\"", gone);
        return new RecordingUploadResult(files, RecordingOutcome.Missing, gone);
    }

    /// <summary>
    /// True when <paramref name="files"/> is done with Drive and nothing of it has changed since: its manifest says it is done,
    /// and every file of it that goes up is on this laptop as it was when its copy on Drive was checked. A file gone from the
    /// laptop since, as audio freed, is safe on Drive and changes nothing. Asked for again, such a recording is not put back in
    /// the queue, so it is never said to wait.
    /// </summary>
    public bool IsUpToDate(RecordingFiles files) =>
        manifests.Read(files) is { State: UploadState.Done } manifest
        && FilesToUpload(files).All(path => IsKnownInSync(manifest.File(Path.GetFileName(path)), path));

    /// <summary>True when the file at <paramref name="path"/> is unchanged since its copy on Drive, <paramref name="entry"/>, was checked and found the same.</summary>
    private static bool IsKnownInSync(ManifestFile? entry, string path)
    {
        var info = new FileInfo(path);
        return entry is { InSync: true } && info.Exists && entry.Size == info.Length && entry.LastWriteUtc == new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
    }

    /// <summary>
    /// Why <paramref name="files"/> cannot go up now, as "is being recorded now"; null when it can. A run that records it,
    /// is stopping or transcribes it, in the background or in a window, says so in its run file; a recorder without one
    /// still holds its newest segment open for writing.
    /// </summary>
    public string? Busy(RecordingFiles files) => BusyIn(journal.List(), files);

    /// <summary>Why <paramref name="files"/> cannot go up now, among <paramref name="runs"/>, the runs the journal lists (see <see cref="Busy"/>); null when it can.</summary>
    public static string? BusyIn(IReadOnlyList<RunView> runs, RecordingFiles files)
    {
        var live = runs.FirstOrDefault(run => (run.IsRecordingNow || run.IsTranscribingNow) && RunView.IsRunOf(run.Record, files));
        if (live is { IsTranscribingNow: true }) return "is being transcribed now";
        if (live is null && ClassFolders.IsLessonVideoBeingMade(files)) return LessonVideoBeingMade;
        if (live is not null || ClassFolders.IsBeingWritten(files)) return "is being recorded now";

        return null;
    }

    /// <summary>Why a lesson waits while a camera process makes its video, as finish-video does from a window with no run file.</summary>
    public const string LessonVideoBeingMade = "is having its lesson video made now";

    /// <summary>
    /// True when the audio of <paramref name="files"/> goes up: for a class's recording as Drive:Upload says, for a YouTube
    /// video's (see <see cref="ClassFolders.IsVideo"/>) as YouTube:DriveUpload says, everything by default.
    /// </summary>
    public bool SendsAudio(RecordingFiles files) =>
        (ClassFolders.IsVideo(files) ? youTube.Value.DriveUpload : options.Value.Upload) == DriveUploadScope.Everything;

    /// <summary>
    /// The files of <paramref name="files"/> that go up, in the order they go: every file of the recording but the upload
    /// copy and the camera's pieces, a video's source file included, and none of its .wav files when its audio does not go
    /// up (see <see cref="SendsAudio"/>). The camera's pieces never go to Drive themselves (camera-first step 12), nor a
    /// lesson video's .partial; the lesson's finished video goes, last, only with Drive:LessonVideos on and its audio going
    /// up, since it holds the same sound (see <see cref="SendsLessonVideo"/>). Files in its folder that are not its own are
    /// left out.
    /// </summary>
    public IReadOnlyList<string> FilesToUpload(RecordingFiles files)
    {
        if (!Directory.Exists(files.Folder)) return [];

        var withoutAudio = !SendsAudio(files);
        var withVideo = SendsLessonVideo(files);
        return
        [
            .. Directory.EnumerateFiles(files.Folder, $"{files.BaseName}*")
                .Where(path => string.Equals(RecordingFiles.TryParse(path)?.BaseName, files.BaseName, StringComparison.OrdinalIgnoreCase))
                .Where(path => !string.Equals(Path.GetFileName(path), Path.GetFileName(files.Upload), StringComparison.OrdinalIgnoreCase))
                .Where(path => !RecordingFiles.IsCameraPiece(path) && !RecordingFiles.IsUnfinishedLessonVideo(path))
                .Where(path => withVideo || !RecordingFiles.IsLessonVideo(path))
                .Where(path => !withoutAudio || !IsAudio(path))
                .OrderBy(path => OrderOf(Path.GetFileName(path), files.BaseName))
                .ThenBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
        ];
    }

    /// <summary>
    /// True when a lesson's finished video goes up with <paramref name="files"/>: Drive:LessonVideos is on (off by default,
    /// Gulzar's answer N19 C) and the recording's audio goes up, as the video holds the same sound.
    /// </summary>
    public bool SendsLessonVideo(RecordingFiles files) => options.Value.LessonVideos && SendsAudio(files);

    /// <summary>True for a file of audio: a segment of either track.</summary>
    public static bool IsAudio(string path) => path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True for a file of media, which goes up streamed from the file as it is, never read into memory, and never as a
    /// revision but for a lesson's finished video made again: a segment of either track, or a lesson's finished video, of up
    /// to gigabytes.
    /// </summary>
    public static bool IsMedia(string path) => IsAudio(path) || path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase);

    /// <summary>The type Drive is told a file has, by its name; never a Google Docs type, so Drive keeps the bytes as they are.</summary>
    public static string ContentTypeOf(string name) =>
        Path.GetExtension(name).ToLowerInvariant() switch
        {
            ".wav" => "audio/wav",
            ".mp4" => "video/mp4",
            ".md" => "text/markdown",
            ".json" => "application/json",
            _ => "text/plain"
        };

    /// <summary>
    /// The line said as a lesson's video of <paramref name="bytes"/> begins to go up: its size, and, once this upload has a
    /// speed from the recording's earlier files (<paramref name="bytesPerSecond"/>), about how long it takes at that speed,
    /// in whole minutes and at least one.
    /// </summary>
    public static string SendingTheVideo(long bytes, double? bytesPerSecond)
    {
        var line = $"Sending the lesson's video ({Sizes.Describe(bytes)}) to Google Drive";
        if (bytesPerSecond is not > 0) return $"{line}.";

        var minutes = Math.Max(1, (int)Math.Round(bytes / bytesPerSecond.Value / 60, MidpointRounding.AwayFromZero));
        return string.Create(CultureInfo.InvariantCulture, $"{line}; at this connection's speed so far, about {minutes} minute{(minutes == 1 ? "" : "s")}.");
    }

    /// <summary>
    /// Where a file of the recording <paramref name="baseName"/> named <paramref name="name"/> comes in the order of the
    /// upload: first the transcripts, then the answers, the marks, the class's audio, the microphone's, the log, and a
    /// lesson's video last, so that the small, useful files are there first; within each, the copies and parts in their order.
    /// </summary>
    private static (int Group, int Number) OrderOf(string name, string baseName)
    {
        var rest = name[baseName.Length..];
        var number = NumberIn().Match(rest) is { Success: true } match ? int.Parse(match.Groups["number"].Value, CultureInfo.InvariantCulture) : 1;
        var group = rest.ToLowerInvariant() switch
        {
            var r when r.EndsWith(".md", StringComparison.Ordinal) => 0,
            var r when r.EndsWith(".json", StringComparison.Ordinal) => 1,
            ".marks.txt" => 2,
            var r when r.EndsWith(".mic.wav", StringComparison.Ordinal) => 4,
            var r when r.EndsWith(".wav", StringComparison.Ordinal) => 3,
            ".mp4" => 6,
            _ => 5
        };
        return (group, number);
    }

    /// <summary>A copy or part number in what follows the base name: " (2)" or "_part2".</summary>
    [GeneratedRegex(@"(?:\((?<number>[0-9]{1,9})\)|_part(?<number>[0-9]{1,9}))", RegexOptions.CultureInvariant)]
    private static partial Regex NumberIn();

    /// <summary>The MD5 of the rest of <paramref name="stream"/> from its start, as Drive writes it: 32 lowercase hexadecimal digits.</summary>
    private static async Task<string> Md5Async(Stream stream, CancellationToken cancellationToken)
    {
        stream.Seek(0, SeekOrigin.Begin);
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        var buffer = new byte[Md5Buffer];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0) md5.AppendData(buffer, 0, read);
        return Convert.ToHexStringLower(md5.GetHashAndReset());
    }

    /// <summary>What became of one file.</summary>
    private enum FileOutcome
    {
        /// <summary>It went up and was checked.</summary>
        Sent,

        /// <summary>Its copy on Drive was there already, and is the same.</summary>
        AlreadyThere,

        /// <summary>It differs from a copy on Drive that ClassScribe may not replace, so it was left out.</summary>
        LeftOut
    }

    /// <summary>Another program holds a file of the recording open in a way that keeps it from being read whole now, as a recorder does.</summary>
    private sealed class FileInUseException(string name, Exception inner) : IOException($"{name} is open in another program", inner);

    /// <summary>One upload of one recording: its manifest as it goes, and the steps.</summary>
    private sealed class Run(RecordingUpload owner, RecordingFiles files, UploadManifest started, Func<CancellationToken, Task> beforePiece)
    {
        private UploadManifest _manifest = started;
        private readonly int _queuedAtStart = started.TimesQueued;
        private readonly List<ManifestFile> _entries = [];
        private IReadOnlyList<DriveFile>? _children;

        /// <summary>The bytes this upload has sent so far, for the speed a lesson's video is said to go up at.</summary>
        private long _sentBytes;

        /// <summary>How long this upload has spent sending them, on the steady clock.</summary>
        private TimeSpan _sendingTime;

        /// <summary>The bytes a second this upload has reached on the recording's files so far; null before any time was spent sending.</summary>
        private double? SpeedSoFar() => _sendingTime > TimeSpan.Zero && _sentBytes > 0 ? _sentBytes / _sendingTime.TotalSeconds : null;

        /// <summary>True when the recording was put in the queue again since this upload began, as by another ClassScribe.</summary>
        private bool QueuedAgain => _manifest.TimesQueued > _queuedAtStart;

        /// <summary>Notes <paramref name="problem"/> in the manifest, which waits, with the next try no sooner than <paramref name="nextAttempt"/>.</summary>
        public void Failed(string problem, DateTimeOffset? nextAttempt)
        {
            _manifest = _manifest with { State = UploadState.Waiting, LastError = problem, NextAttemptUtc = nextAttempt, DoneUtc = null };
            Save();
        }

        /// <summary>The upload, step by step (see <see cref="RecordingUpload"/>).</summary>
        public async Task<RecordingUploadResult> UploadAsync(CancellationToken cancellationToken)
        {
            var now = owner.Time.GetUtcNow();
            _manifest = _manifest with { State = UploadState.Waiting, Attempts = _manifest.Attempts + 1, LastTriedUtc = now, LastError = null, DoneUtc = null, NextAttemptUtc = null };
            var locals = owner.FilesToUpload(files);
            Merge(locals);
            Save();

            var pending = locals.Where(path => !IsKnownInSync(path)).ToList();
            foreach (var known in locals.Except(pending)) owner.Logger.LogDebug("{Name} is already on Google Drive", Path.GetFileName(known));
            if (pending.Count == 0) return Finish(problem: null, alreadyThere: true);

            var levels = ClassFolders.DriveLevelsOf(ClassFolders.AsOnDisk(files.Folder));
            var location = await owner.Retry.RunAsync(token => owner.Folders.FindOrCreatePathAsync(levels, token), "find the recording's folder", cancellationToken);
            _manifest = _manifest with { DriveFolderId = location.Folder.Id, DrivePath = location.Path };
            Save();

            owner.Logger.LogInformation("Google Drive: uploading {Recording} ({Files} {FileWord}, {Size}) to {Path}", files.BaseName, pending.Count,
                pending.Count == 1 ? "file" : "files", Sizes.Describe(pending.Sum(path => new FileInfo(path).Length)), location.Path);

            string? problem = null;
            var said = false;
            foreach (var path in pending)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // The log this process still writes goes up last: the last line goes into it first, so that Drive's copy
                // holds it, unless something has kept the recording from being done already.
                if (problem is null && owner.IsLogWrittenHere(path))
                {
                    SayWhereItIs(alreadyThere: false, comingUp: path);
                    owner.FlushTheLog();
                    said = true;
                }

                if (RecordingFiles.IsLessonVideo(path)) owner.Logger.LogInformation("{Line}", SendingTheVideo(new FileInfo(path).Length, SpeedSoFar()));

                try
                {
                    var outcome = await SendFileAsync(path, location, cancellationToken);
                    if (outcome == FileOutcome.AlreadyThere) owner.Logger.LogInformation("{Name}: already on Drive", Path.GetFileName(path));
                }
                catch (FileInUseException ex)
                {
                    // Deliberately not rethrown: a file another program holds open, as a recorder holds the segment it writes,
                    // cannot be read whole now; the recording waits and goes up once it is closed, which this says.
                    var busy = $"{ex.Message}, as while it is being recorded; {files.BaseName} goes up once it is closed";
                    owner.Logger.LogInformation("{Busy}", busy);
                    Failed(busy, null);
                    return new RecordingUploadResult(files, RecordingOutcome.InProgress, busy);
                }
                catch (GoogleDriveException ex) when (ex.Problem == DriveProblem.Refused)
                {
                    // Deliberately not rethrown: one file Drive refuses does not keep the others from going up; the recording
                    // waits with Drive's words, and the file goes up again with the next upload.
                    owner.Logger.LogWarning("{Name} did not go up to Google Drive: {Problem}", Path.GetFileName(path), ex.Message);
                    problem ??= $"{Path.GetFileName(path)} did not go up: {ex.Message}";
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Deliberately not rethrown: a file that cannot be read now, as one deleted meanwhile, does not keep the
                    // others from going up; the recording waits, and the file is looked at again with the next upload.
                    owner.Logger.LogWarning("{Name} could not be read to go up to Google Drive: {Reason}", Path.GetFileName(path), ex.Message);
                    problem ??= $"{Path.GetFileName(path)} could not be read: {ex.Message}";
                }
            }

            return Finish(problem, alreadyThere: false, said);
        }

        /// <summary>
        /// Ends the upload: done, with the line that says where the recording is on Drive, unless <paramref name="said"/>
        /// says it went into the log before the log went up; or waiting, with <paramref name="problem"/>.
        /// <paramref name="alreadyThere"/> says that nothing had to go up.
        /// </summary>
        private RecordingUploadResult Finish(string? problem, bool alreadyThere, bool said = false)
        {
            if (problem is not null)
            {
                _manifest = _manifest with { State = UploadState.Waiting, LastError = problem };
                Save();
                owner.Logger.LogWarning("{Recording} waits to go up to Google Drive: {Problem}", files.BaseName, problem);
                return new RecordingUploadResult(files, RecordingOutcome.Waiting, problem);
            }

            NoteQueuedMeanwhile();
            if (QueuedAgain)
            {
                // Another ClassScribe put it in the queue while this upload went on, as with a transcript made meanwhile, and
                // found the lock held: what it added may not be in this upload, so the recording stays waiting, to be taken once more.
                var again = $"{files.BaseName} was put in the queue again while it went up, so it is looked at once more";
                _manifest = _manifest with { State = UploadState.Waiting, LastError = null, NextAttemptUtc = null, DoneUtc = null };
                Save();
                owner.Logger.LogInformation("{Again}", again);
                return new RecordingUploadResult(files, RecordingOutcome.Waiting, again);
            }

            _manifest = _manifest with { State = UploadState.Done, LastError = null, NextAttemptUtc = null, DoneUtc = owner.Time.GetUtcNow() };
            Save();
            if (!said) SayWhereItIs(alreadyThere, comingUp: null);
            return new RecordingUploadResult(files, RecordingOutcome.Done, null);
        }

        /// <summary>
        /// The last line: where the recording is on Drive, how many files and how much, each checked against Drive's MD5, and
        /// the folder's link. <paramref name="comingUp"/>, when given, is the log about to go up, counted with the rest.
        /// </summary>
        private void SayWhereItIs(bool alreadyThere, string? comingUp)
        {
            var verified = _entries.Where(entry => entry.IsVerified && (comingUp is null || !string.Equals(entry.Name, Path.GetFileName(comingUp), StringComparison.OrdinalIgnoreCase))).ToList();
            var number = verified.Count + (comingUp is null ? 0 : 1);
            var count = number == 1 ? "1 file" : $"{number} files";
            var size = Sizes.Describe(verified.Sum(entry => entry.DriveSize ?? 0) + (comingUp is null ? 0 : new FileInfo(comingUp).Length));
            var link = _manifest.DriveLink is { } address ? $": {address}" : "";
            owner.Logger.LogInformation("{Recording} is {Already}on Google Drive: {Location} ({Count}, {Size}, each checked against Drive's MD5){Link}", files.BaseName,
                alreadyThere ? "already " : "", _manifest.DriveLocation, count, size, link);
        }

        /// <summary>
        /// Sends one file, or finds it there already, or leaves it out when it differs from a copy on Drive that may not be
        /// replaced; a text file that has grown since its copy went up, or a lesson's video made again, gets a new revision of
        /// that copy.
        /// </summary>
        private async Task<FileOutcome> SendFileAsync(string path, DriveFolderPath location, CancellationToken cancellationToken)
        {
            var name = Path.GetFileName(path);
            var audio = IsAudio(path);
            var media = IsMedia(path);
            var entry = Entry(name);
            var lastWrite = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
            await using var content = await OpenAsync(path, name, media, cancellationToken);
            var size = content.Length;
            var md5 = entry.Md5 is { } known && entry.Size == size && entry.LastWriteUtc == lastWrite ? known : await Md5Async(content, cancellationToken);
            var seconds = audio ? entry.Seconds is { } measured && entry.Size == size ? measured : ClassFolders.MeasureSegment(path).TotalSeconds : (double?)null;
            entry = Put(entry with { Size = size, LastWriteUtc = lastWrite, Md5 = md5, Seconds = seconds, FreedUtc = null });

            if (await TargetOfAsync(entry, name, media, RecordingFiles.IsLessonVideo(path), location, cancellationToken) is not { } target)
                return Entry(name).InSync ? FileOutcome.AlreadyThere : FileOutcome.LeftOut;

            var source = new UploadSource(content, size, md5, ContentTypeOf(name));
            var watch = new FileWatch(this, name, size, beforePiece, owner.Time, owner.Logger);
            var sending = owner.Time.GetTimestamp();
            var made = await owner.Upload.SendAsync(source, target, entry.Session, watch, cancellationToken);
            _sentBytes += size;
            _sendingTime += owner.Time.GetElapsedTime(sending);
            var copy = await CheckAsync(made, name, cancellationToken);
            if (!Matches(copy, size, md5))
            {
                owner.Logger.LogWarning("{Name} does not match Drive's MD5 after it went up (here {Md5}, {Size} bytes; on Drive {DriveMd5}, {DriveSize} bytes); sending it again as a new revision",
                    name, md5, size, copy?.Md5Checksum ?? "none", copy?.Size?.ToString(CultureInfo.InvariantCulture) ?? "no size");
                made = await owner.Upload.SendAsync(source, new UploadTarget(name, null, made.Id), null, watch, cancellationToken);
                copy = await CheckAsync(made, name, cancellationToken);
                if (!Matches(copy, size, md5))
                {
                    Put(Entry(name) with { DriveId = made.Id, DriveSize = copy?.Size, DriveMd5 = copy?.Md5Checksum, VerifiedUtc = null, Session = null });
                    throw new GoogleDriveException(DriveProblem.Refused, $"{name} still does not match Drive's MD5 after it was sent again; it goes up again with the next upload.");
                }
            }

            Put(Entry(name) with { DriveId = made.Id, DriveSize = copy!.Size, DriveMd5 = copy.Md5Checksum, VerifiedUtc = owner.Time.GetUtcNow(), Session = null });
            owner.Logger.LogDebug("{Name} is on Google Drive, checked against its MD5", name);
            return FileOutcome.Sent;
        }

        /// <summary>
        /// Where <paramref name="entry"/> goes, or null when it does not go: already there and the same, or different from a
        /// copy that may not be replaced, which is said. A copy checked before is compared; a text file that has grown since
        /// gets a new revision of that copy while Drive still has it outside the trash, and otherwise goes up as a new file,
        /// the copy in the trash left as it is. ClassScribe's own copy whose check failed last time is asked about again:
        /// adopted when it matches now, otherwise sent again as a new revision of itself, as it was never a good copy. Without
        /// either, the folder on Drive is looked through for a copy of the same name, which is adopted when it is the same.
        /// Media (<paramref name="media"/>: audio, or a lesson's video) is never sent as a revision, but for a lesson's finished
        /// video (<paramref name="lessonVideo"/>) made again since its copy was checked, as a continued lesson's by
        /// finish-video: it goes up whole as a new revision of that copy, larger or smaller (teach-video 8), which is said.
        /// </summary>
        private async Task<UploadTarget?> TargetOfAsync(ManifestFile entry, string name, bool media, bool lessonVideo, DriveFolderPath location, CancellationToken cancellationToken)
        {
            if (entry.IsVerified)
            {
                if (entry.InSync) return null;
                if (!lessonVideo && (media || entry.Size <= entry.DriveSize))
                {
                    owner.Logger.LogWarning("{Name} on this laptop differs from its copy on Google Drive, which ClassScribe never replaces: only a text file that has grown, or a lesson's video made again, goes up again, as a new revision. The copy on Drive stays as it is", name);
                    return null;
                }

                if (await CheckAsync(new DriveFile(entry.DriveId!, name, null, null, null, null, null), name, cancellationToken) is { Trashed: not true })
                {
                    if (lessonVideo) owner.Logger.LogInformation("{Name} was made again since its copy went up to Google Drive, so it goes up whole, as a new revision of that copy", name);
                    return new UploadTarget(name, null, entry.DriveId);
                }

                owner.Logger.LogInformation("{Name}'s copy on Google Drive is no longer there, or is in the trash, so it goes up again as a new file; ClassScribe takes nothing out of the trash", name);
                entry = Put(entry with { DriveId = null, DriveSize = null, DriveMd5 = null, VerifiedUtc = null, Session = null });
            }

            if (entry.DriveId is { } ownId && await CheckAsync(new DriveFile(ownId, name, null, null, null, null, null), name, cancellationToken) is { Trashed: not true } own)
            {
                if (!Matches(own, entry.Size, entry.Md5!)) return new UploadTarget(name, null, ownId);

                Put(entry with { DriveSize = own.Size, DriveMd5 = own.Md5Checksum, VerifiedUtc = owner.Time.GetUtcNow(), Session = null });
                return null;
            }

            _children ??= await owner.Retry.RunAsync(token => owner.Drive.ListAsync(DriveFolders.ChildrenQuery(location.Folder.Id), token), "look through a folder", cancellationToken);
            var named = _children.Where(child => child.MimeType != DriveFolders.FolderType && string.Equals(child.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (named.FirstOrDefault(child => Matches(child, entry.Size, entry.Md5!)) is { } same)
            {
                Put(entry with { DriveId = same.Id, DriveSize = same.Size, DriveMd5 = same.Md5Checksum, VerifiedUtc = owner.Time.GetUtcNow(), Session = null });
                return null;
            }

            if (named.FirstOrDefault() is not { } other) return new UploadTarget(name, location.Folder.Id, null);
            if (!media && entry.Size > (other.Size ?? long.MaxValue)) return new UploadTarget(name, null, other.Id);

            owner.Logger.LogWarning("A different {Name} is already in {Location} on Google Drive; ClassScribe never replaces it, so this one did not go up", name, location.Location);
            return null;
        }

        /// <summary>The copy of <paramref name="made"/> as Drive describes it now, asked again while Google is out of reach or busy.</summary>
        private Task<DriveFile?> CheckAsync(DriveFile made, string name, CancellationToken cancellationToken) =>
            string.IsNullOrEmpty(made.Id)
                ? throw new GoogleDriveException(DriveProblem.Refused, $"Google Drive took {name} but did not say its id.")
                : owner.Retry.RunAsync(token => owner.Drive.GetFileAsync(made.Id, token), "show a file", cancellationToken);

        /// <summary>True when <paramref name="copy"/> is a file outside the trash of <paramref name="size"/> bytes whose MD5 is <paramref name="md5"/>.</summary>
        private static bool Matches(DriveFile? copy, long size, string md5) =>
            copy is { Trashed: not true } && copy.Size == size && string.Equals(copy.Md5Checksum, md5, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// The bytes of the file to send: media (audio, or a lesson's video of up to gigabytes) opened as it is and streamed
        /// from the file, so that no recorder can write to it meanwhile and nothing of it is held in memory, and a text file
        /// read whole into memory at once, which never holds up the writer of a log or of the marks. Another program holding
        /// the file open for writing, as a recorder does, keeps it from being read now.
        /// </summary>
        private static async Task<Stream> OpenAsync(string path, string name, bool media, CancellationToken cancellationToken)
        {
            try
            {
                if (media) return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, bufferSize: 1, FileOptions.Asynchronous);

                await using var text = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1, FileOptions.Asynchronous);
                var snapshot = new MemoryStream();
                await text.CopyToAsync(snapshot, cancellationToken);
                snapshot.Position = 0;
                return snapshot;
            }
            catch (IOException ex) when (ex.HResult == SharingViolation)
            {
                throw new FileInUseException(name, ex);
            }
        }

        /// <summary>True when the file at <paramref name="path"/> is unchanged since its copy on Drive was checked and found the same.</summary>
        private bool IsKnownInSync(string path) => RecordingUpload.IsKnownInSync(Entry(Path.GetFileName(path)), path);

        /// <summary>
        /// The entries for <paramref name="locals"/>, in their order, each the one known or a new one; then those of files
        /// gone from the laptop whose copy on Drive was checked, which stay known as safe there. A file gone that never went
        /// up is forgotten, which is said.
        /// </summary>
        private void Merge(IReadOnlyList<string> locals)
        {
            var names = locals.Select(Path.GetFileName).OfType<string>().ToList();
            _entries.AddRange(names.Select(name => _manifest.File(name) ?? new ManifestFile(name)));
            foreach (var gone in _manifest.Files.Where(entry => !names.Contains(entry.Name, StringComparer.OrdinalIgnoreCase)))
            {
                if (gone.IsVerified)
                    _entries.Add(gone);
                else
                    owner.Logger.LogInformation("{Name} is no longer in {Folder} and never went up to Google Drive, so it is left out", gone.Name, files.Folder);
            }
        }

        /// <summary>The entry of the file <paramref name="name"/>; a new one when there is none.</summary>
        private ManifestFile Entry(string name) => _entries.FirstOrDefault(entry => string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase)) ?? new ManifestFile(name);

        /// <summary>Keeps <paramref name="entry"/> in place of the one of its name, writes the manifest, and returns it.</summary>
        private ManifestFile Put(ManifestFile entry)
        {
            var index = _entries.FindIndex(known => string.Equals(known.Name, entry.Name, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) _entries[index] = entry;
            else _entries.Add(entry);
            Save();
            return entry;
        }

        /// <summary>Notes the session of the file <paramref name="name"/>, for an upload that goes on later where this one stopped.</summary>
        public void Note(string name, UploadSession? session) => Put(Entry(name) with { Session = session });

        /// <summary>
        /// Takes in a queue another ClassScribe wrote meanwhile (see <see cref="UploadManifest.TimesQueued"/>), so that no write
        /// of this upload undoes it and the end of the upload knows of it.
        /// </summary>
        private void NoteQueuedMeanwhile()
        {
            if (owner.Manifests.Read(files) is { } now && now.TimesQueued > _manifest.TimesQueued) _manifest = _manifest with { TimesQueued = now.TimesQueued };
        }

        /// <summary>
        /// Writes the manifest with the entries as they are now, keeping a queue written meanwhile. One that cannot be written
        /// ends the upload (see <see cref="UploadNotTrackedException"/>).
        /// </summary>
        private void Save()
        {
            NoteQueuedMeanwhile();
            _manifest = _manifest with { Files = [.. _entries], UpdatedUtc = owner.Time.GetUtcNow() };
            try
            {
                owner.Manifests.Write(_manifest);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Thrown on as an exception of its own, the failure inside it: an upload that cannot keep track of itself must
                // not go on, and must not be taken for a file of the recording that could not be read, which the others pass.
                throw new UploadNotTrackedException(files.BaseName, owner.Manifests.PathOf(files), ex);
            }
        }
    }

    /// <summary>
    /// What one file's upload tells as it goes: each change of its session goes into the manifest, and every tenth of a file
    /// larger than one piece is said, with how fast it goes; before each piece, whatever the upload waits for.
    /// </summary>
    private sealed class FileWatch(Run run, string name, long size, Func<CancellationToken, Task> beforePiece, TimeProvider time, ILogger logger) : IUploadWatch
    {
        private readonly long _started = time.GetTimestamp();
        private long? _firstConfirmed;
        private int _tenthsSaid;

        /// <inheritdoc />
        public Task BeforePieceAsync(CancellationToken cancellationToken) => beforePiece(cancellationToken);

        /// <inheritdoc />
        public void SessionChanged(UploadSession? session)
        {
            run.Note(name, session);
            if (session is null || size == 0) return;

            _firstConfirmed ??= session.Confirmed;
            var tenths = (int)(session.Confirmed * 10 / size);
            if (tenths <= _tenthsSaid || tenths >= 10) return;

            _tenthsSaid = tenths;
            var seconds = time.GetElapsedTime(_started).TotalSeconds;
            var speed = seconds > 0 ? string.Create(CultureInfo.InvariantCulture, $", {(session.Confirmed - _firstConfirmed.Value) / seconds / (1024 * 1024):0.0} MB/s") : "";
            logger.LogInformation("{Name}: {Percent}% ({Sent} of {Total}{Speed})", name, tenths * 10, Sizes.Describe(session.Confirmed), Sizes.Describe(size), speed);
        }
    }
}
