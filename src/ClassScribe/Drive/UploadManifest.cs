using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClassScribe.Recorder;
using ClassScribe.Storage;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Drive;

/// <summary>Where a recording stands with Google Drive.</summary>
public enum UploadState
{
    /// <summary>It waits to go up, or to go up whole: it is in the queue.</summary>
    Waiting,

    /// <summary>Every file of it that goes up is on Drive, each checked against Drive's MD5.</summary>
    Done,

    /// <summary>
    /// Its folder is not there now, on a drive that is: it was moved or deleted by hand before all of it went up. It is out
    /// of the queue; what of it went up stays on Drive, and asked for again, once its folder is back, it goes into the queue.
    /// </summary>
    Missing
}

/// <summary>
/// An upload to Drive that has begun and not ended: the session's address, which lets the upload go on where it stopped, even
/// after a restart, for up to a week; what it was started for (the size and MD5 of the content, and the file a new revision
/// is of); and how much Drive has confirmed. The address is never shown or logged: as text the record leaves it out.
/// </summary>
/// <param name="Address">The session's address, as Drive gave it in its Location header.</param>
/// <param name="StartedUtc">When the session was started.</param>
/// <param name="Size">The size of the content it was started for.</param>
/// <param name="Md5">The MD5 of that content, as Drive writes it: 32 lowercase hexadecimal digits.</param>
/// <param name="Confirmed">How many bytes from the start Drive has confirmed.</param>
/// <param name="RevisionOf">The id of the file on Drive it makes a new revision of; null for a new file.</param>
public sealed record UploadSession(string Address, DateTimeOffset StartedUtc, long Size, string Md5, long Confirmed, string? RevisionOf)
{
    /// <summary>The session as text, without its address.</summary>
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("StartedUtc = ").Append(StartedUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture))
            .Append(", Size = ").Append(Size)
            .Append(", Confirmed = ").Append(Confirmed)
            .Append(", RevisionOf = ").Append(RevisionOf);
        return true;
    }
}

/// <summary>
/// One file of a recording, as the manifest remembers it: what it was on this laptop when last looked at (its size, when it
/// was last written, its MD5, worked out again only when one of those two changes, and for audio its length), its copy on
/// Drive once checked (the id, size and MD5 Drive gave, and when), when it was deleted from this laptop to free space, and an
/// upload of it that has begun and not ended.
/// </summary>
/// <param name="Name">The file's name, in the recording's folder.</param>
public sealed record ManifestFile(string Name)
{
    /// <summary>Its size on this laptop when last looked at.</summary>
    public long Size { get; init; }

    /// <summary>When it was last written, as Windows said when it was last looked at.</summary>
    public DateTimeOffset LastWriteUtc { get; init; }

    /// <summary>Its MD5 for that size and time, 32 lowercase hexadecimal digits; null until worked out.</summary>
    public string? Md5 { get; init; }

    /// <summary>For audio, its length in seconds, kept so that the length is still known once the file is gone from this laptop.</summary>
    public double? Seconds { get; init; }

    /// <summary>Drive's id for its copy; null until it has one.</summary>
    public string? DriveId { get; init; }

    /// <summary>The size of the copy on Drive, as Drive said when it was checked.</summary>
    public long? DriveSize { get; init; }

    /// <summary>The MD5 of the copy on Drive, as Drive said when it was checked.</summary>
    public string? DriveMd5 { get; init; }

    /// <summary>When the copy on Drive was checked and found to match the file; null until then.</summary>
    public DateTimeOffset? VerifiedUtc { get; init; }

    /// <summary>When ClassScribe deleted the file from this laptop to free space, after checking its copy on Drive; null while it is kept.</summary>
    public DateTimeOffset? FreedUtc { get; init; }

    /// <summary>An upload of it that has begun and not ended; null when there is none.</summary>
    public UploadSession? Session { get; init; }

    /// <summary>
    /// Fields of the file this build does not know, as a later build wrote them, kept so that writing the manifest back loses
    /// none of them (layout-config 6.3); null when there were none, so that files still compare equal.
    /// </summary>
    [JsonExtensionData]
    // A setter, not init: the generated reader fills an init-only property through the constructor, where extension data
    // cannot go.
    public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>True once a copy on Drive has been checked against Drive's size and MD5.</summary>
    [JsonIgnore]
    public bool IsVerified => DriveId is not null && VerifiedUtc is not null && DriveMd5 is not null && DriveSize is not null;

    /// <summary>True when the checked copy on Drive is the file as it was when last looked at: the same size and MD5.</summary>
    [JsonIgnore]
    public bool InSync => IsVerified && Md5 is not null && Size == DriveSize && string.Equals(Md5, DriveMd5, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// What ClassScribe remembers of one recording and Google Drive, in %LOCALAPPDATA%\ClassScribe\drive\recordings: its folder
/// and base name; whether it waits in the queue or is done, since when, how many tries, when the next is due and what went
/// wrong last; where it is on Drive; and each of its files (see <see cref="ManifestFile"/>). So nothing is uploaded twice,
/// an upload goes on where it stopped, even after a restart, and a file deleted from this laptop after it went up is known
/// to be safe on Drive rather than missing.
/// </summary>
/// <param name="Folder">The recording's folder on this laptop.</param>
/// <param name="BaseName">The recording's base name, such as Mantiq_2026-10-17_14-04-42Z.</param>
public sealed record UploadManifest(string Folder, string BaseName)
{
    /// <summary>The version of this file's layout.</summary>
    public const int CurrentVersion = 1;

    /// <summary>The version of the layout it was written in.</summary>
    public int Version { get; init; } = CurrentVersion;

    /// <summary>Whether it waits in the queue or is done.</summary>
    public UploadState State { get; init; } = UploadState.Waiting;

    /// <summary>When it was first put in the queue.</summary>
    public DateTimeOffset QueuedUtc { get; init; }

    /// <summary>
    /// How many times it has been put in the queue (see <see cref="UploadManifests.Queue"/>). An upload that began before the
    /// last of them, as when a transcript made in another window while this one goes up adds a (2).md, does not leave it done:
    /// it stays waiting, to be taken once more, so that nothing new is left behind.
    /// </summary>
    public int TimesQueued { get; init; }

    /// <summary>How many times an upload of it has begun.</summary>
    public int Attempts { get; init; }

    /// <summary>When an upload of it last began; null before the first.</summary>
    public DateTimeOffset? LastTriedUtc { get; init; }

    /// <summary>When an upload that comes after a class should try it again, no sooner; null for at once.</summary>
    public DateTimeOffset? NextAttemptUtc { get; init; }

    /// <summary>What went wrong the last time, in plain words; null when nothing did.</summary>
    public string? LastError { get; init; }

    /// <summary>When it was last found done; null while it waits.</summary>
    public DateTimeOffset? DoneUtc { get; init; }

    /// <summary>
    /// When an upload of it last wrote here, as it does at each step and at each piece Drive confirms; null before the first.
    /// A recent one, during a try that has not failed, tells an upload going on now, in this process or in another (see
    /// <see cref="IsBeingUploaded"/>).
    /// </summary>
    public DateTimeOffset? UpdatedUtc { get; init; }

    /// <summary>Drive's id for the folder its files went into.</summary>
    public string? DriveFolderId { get; init; }

    /// <summary>Where that folder is, from ClassScribe's own folder down, as ClassScribe/Mantiq/2026-10-17.</summary>
    public string? DrivePath { get; init; }

    /// <summary>Its files, in the order they go up.</summary>
    public IReadOnlyList<ManifestFile> Files { get; init; } = [];

    /// <summary>
    /// Fields of the manifest this build does not know, as a later build wrote them, kept so that an upload writing it back
    /// loses none of them (layout-config 6.3): an older build sharing the queue no longer drops what a newer one keeps here.
    /// Null when there were none, so that manifests still compare equal.
    /// </summary>
    [JsonExtensionData]
    // A setter, not init: the generated reader fills an init-only property through the constructor, where extension data
    // cannot go.
    public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>The recording the manifest is of.</summary>
    [JsonIgnore]
    public RecordingFiles Recording => new(Folder, BaseName);

    /// <summary>The folder on Drive in a browser; null before it is known.</summary>
    [JsonIgnore]
    public string? DriveLink => DriveFolderId is { } id ? new DriveFolder(id, "").Link : null;

    /// <summary>Where it is on Drive, as Gulzar finds it there: My Drive &gt; ClassScribe &gt; Mantiq &gt; 2026-10-17.</summary>
    [JsonIgnore]
    public string DriveLocation => DrivePath is { Length: > 0 } path ? $"My Drive > {string.Join(" > ", path.Split('/'))}" : "Google Drive";

    /// <summary>
    /// How much of it Drive has: the files whose checked copy is the file as it was last looked at, and the bytes Drive has
    /// confirmed of the uploads begun and not ended.
    /// </summary>
    [JsonIgnore]
    public long BytesOnDrive => Files.Sum(file => (file.InSync ? file.DriveSize ?? 0 : 0) + (file.Session?.Confirmed ?? 0));

    /// <summary>
    /// How long after an upload last wrote here it still counts as going on: longer than a piece of 8 MiB takes at the
    /// slowest speed an upload allows, about 4.5 minutes.
    /// </summary>
    public static readonly TimeSpan GoingOnWithin = TimeSpan.FromMinutes(5);

    /// <summary>
    /// True when an upload of it is going on at <paramref name="now"/>: it waits, a try has begun and not failed, and the
    /// upload wrote here within <see cref="GoingOnWithin"/>. A process that ended in the middle stops counting after that.
    /// </summary>
    public bool IsBeingUploaded(DateTimeOffset now) =>
        State == UploadState.Waiting && LastTriedUtc is not null && LastError is null && UpdatedUtc is { } updated && now - updated <= GoingOnWithin;

    /// <summary>The file named <paramref name="name"/>, in any letter case, as Windows takes names; null when there is none.</summary>
    public ManifestFile? File(string name) => Files.FirstOrDefault(file => string.Equals(file.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// JSON for the manifests, generated at compile time: camelCase names, the web defaults, the states written by name, nothing
/// written for a null, and indented, as the run files are, so a person can read one.
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, WriteIndented = true,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(UploadManifest))]
public sealed partial class UploadJson : JsonSerializerContext;

/// <summary>
/// The manifests, one file per recording in %LOCALAPPDATA%\ClassScribe\drive\recordings, named after the recording's base
/// name and 8 hexadecimal digits of a SHA-256 of its folder's full path (in capitals, as Windows takes paths in any case), so
/// that recordings of the same base name in two folders never share one. Each is written whole or not at all, through a
/// temporary file, and tried again three times, 100 ms apart, when another program holds the file for a moment, as the run
/// files are. The queue is the manifests that wait. One for the process.
/// </summary>
public sealed class UploadManifests(RuntimeFolder runtime, TimeProvider time, ILogger<UploadManifests> logger)
{
    /// <summary>The name of the folder of the manifests, under ClassScribe's folder for Drive.</summary>
    public const string RecordingsFolderName = "recordings";

    /// <summary>How many times a write held up by another program is tried again.</summary>
    private const int Retries = 3;

    /// <summary>The pause before a held-up write is tried again.</summary>
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(100);

    /// <summary>The folder of the manifests: %LOCALAPPDATA%\ClassScribe\drive\recordings.</summary>
    public string Folder => Path.Combine(runtime.Root, DriveFolders.DriveFolderName, RecordingsFolderName);

    /// <summary>The manifest file of <paramref name="files"/>.</summary>
    public string PathOf(RecordingFiles files) => Path.Combine(Folder, $"{files.BaseName}.{FolderHash(files.Folder)}.json");

    /// <summary>
    /// The manifest of <paramref name="files"/>; null when there is none. One that cannot be read counts as none, with a
    /// warning: Drive's own files are then found again by name and MD5, so nothing goes up twice.
    /// </summary>
    public UploadManifest? Read(RecordingFiles files) => ReadFile(PathOf(files));

    /// <summary>
    /// Puts <paramref name="files"/> in the queue, as waiting, and returns its manifest: a new one, queued now, or the one
    /// there, which keeps what it knows of each file. Each queue is counted (<see cref="UploadManifest.TimesQueued"/>), so
    /// that an upload going on meanwhile in another process takes the recording once more rather than leave it done; and no
    /// upload is taken to be going on from the queue alone (see <see cref="UploadManifest.IsBeingUploaded"/>). Written at
    /// once, so a crash or a restart keeps it in the queue.
    /// </summary>
    public UploadManifest Queue(RecordingFiles files)
    {
        var queued = Read(files) is { } known
            ? known with { State = UploadState.Waiting, DoneUtc = null, UpdatedUtc = null, TimesQueued = known.TimesQueued + 1 }
            : new UploadManifest(files.Folder, files.BaseName) { QueuedUtc = time.GetUtcNow(), TimesQueued = 1 };
        Write(queued);
        return queued;
    }

    /// <summary>
    /// Keeps <paramref name="files"/> in the queue, as waiting, or puts it there, without counting a queue of its own: what an
    /// upload notes of a recording it cannot take now, as one being recorded or one on a drive that is out, with
    /// <paramref name="problem"/>, the reason, and the time of the try, when one is given. Returns the manifest written.
    /// </summary>
    public UploadManifest KeepWaiting(RecordingFiles files, string? problem = null)
    {
        var known = Read(files) ?? new UploadManifest(files.Folder, files.BaseName) { QueuedUtc = time.GetUtcNow() };
        var waiting = known with { State = UploadState.Waiting, DoneUtc = null };
        if (problem is not null) waiting = waiting with { LastError = problem, LastTriedUtc = time.GetUtcNow() };
        Write(waiting);
        return waiting;
    }

    /// <summary>Every manifest that waits, the one queued first first.</summary>
    public IReadOnlyList<UploadManifest> Waiting() =>
        [.. All().Where(manifest => manifest.State == UploadState.Waiting).OrderBy(manifest => manifest.QueuedUtc)];

    /// <summary>Every manifest there is, in no particular order; those that cannot be read are left out, with a warning.</summary>
    public IReadOnlyList<UploadManifest> All()
    {
        if (!Directory.Exists(Folder)) return [];

        return [.. Directory.EnumerateFiles(Folder, "*.json").Select(ReadFile).OfType<UploadManifest>()];
    }

    /// <summary>
    /// Writes <paramref name="manifest"/> whole, through a temporary file in the same folder, made if needed; a file another
    /// program holds for a moment is tried again. A write that still fails is logged and thrown: an upload that cannot keep
    /// track of itself must not go on.
    /// </summary>
    public void Write(UploadManifest manifest)
    {
        var path = PathOf(manifest.Recording);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, UploadJson.Default.UploadManifest);
        for (var retry = 0; ; retry++)
        {
            var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
            try
            {
                Directory.CreateDirectory(Folder);
                File.WriteAllBytes(temporary, bytes);
                File.Move(temporary, path, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && retry < Retries)
            {
                // Deliberately not rethrown: another program, such as a virus scanner, may hold the file for a moment, so the
                // write is tried again shortly; the last try's failure goes to the caller.
                logger.LogDebug(ex, "Could not write {Path}; trying again in {Milliseconds} ms", path, RetryDelay.TotalMilliseconds);
                TryDelete(temporary);
                Thread.Sleep(RetryDelay);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The caller says it in words, once: the upload that stops, the queue at a run's start, or the command.
                logger.LogDebug(ex, "Could not write {Path}, where ClassScribe keeps track of the upload of {Recording}", path, manifest.BaseName);
                TryDelete(temporary);
                throw;
            }
        }
    }

    /// <summary>
    /// How many files of <paramref name="files"/> are on this laptop and how large they are together, the upload copy, the
    /// camera's pieces and a lesson video's .partial left out, as a waiting recording is described: none of them ever goes
    /// to Drive; and the lesson's finished video too unless <paramref name="withLessonVideo"/>, as Drive:LessonVideos says.
    /// None when its folder is not there.
    /// </summary>
    public static (int Count, long Bytes) OnDisk(RecordingFiles files, bool withLessonVideo = false)
    {
        if (!Directory.Exists(files.Folder)) return (0, 0);

        var sizes = Directory.EnumerateFiles(files.Folder, $"{files.BaseName}*")
            .Where(path => string.Equals(RecordingFiles.TryParse(path)?.BaseName, files.BaseName, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(Path.GetFileName(path), Path.GetFileName(files.Upload), StringComparison.OrdinalIgnoreCase)
                && !RecordingFiles.IsCameraPiece(path) && !RecordingFiles.IsUnfinishedLessonVideo(path)
                && (withLessonVideo || !RecordingFiles.IsLessonVideo(path)))
            .Select(path => new FileInfo(path).Length)
            .ToList();
        return (sizes.Count, sizes.Sum());
    }

    /// <summary>
    /// 8 hexadecimal digits of a SHA-256 of <paramref name="folder"/>'s full path, without a separator at its end and in
    /// capitals, so that the same folder written in another letter case gives the same digits.
    /// </summary>
    internal static string FolderHash(string folder)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)).ToUpperInvariant();
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(full)))[..8];
    }

    /// <summary>The manifest in <paramref name="path"/>; null when it is not there or cannot be read, which is said.</summary>
    private UploadManifest? ReadFile(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllBytes(path), UploadJson.Default.UploadManifest) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Deliberately not rethrown: a manifest that cannot be read only costs a look on Drive, where the recording's
            // files are found again by name and MD5, so nothing goes up twice; this warning names the file.
            logger.LogWarning("Could not read {Path} ({Reason}); the recording's files on Google Drive are looked for by name instead", path, ex.Message);
            return null;
        }
    }

    /// <summary>Deletes a temporary file a failed write left, if it is there.</summary>
    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Deliberately not rethrown: a temporary file left behind is never read, and the write's own failure is what counts.
            logger.LogDebug(ex, "Could not delete {Path}", path);
        }
    }
}
