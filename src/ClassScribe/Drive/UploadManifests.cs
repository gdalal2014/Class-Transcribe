using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClassScribe.Recorder;
using ClassScribe.Storage;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Drive;

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
/// files are. The queue is the manifests that wait. The adapter of <see cref="IUploadManifests"/>. One for the process.
/// </summary>
public sealed class UploadManifests(RuntimeFolder runtime, TimeProvider time, ILogger<UploadManifests> logger) : IUploadManifests
{
    /// <inheritdoc />
    (int Count, long Bytes) IUploadManifests.OnDisk(RecordingFiles files, bool withLessonVideo) => OnDisk(files, withLessonVideo);

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
