using System.Security.Cryptography;
using ClassScribe.Audio;
using ClassScribe.Commands;
using ClassScribe.Storage;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Drive;

/// <summary>What freeing space did for one recording.</summary>
/// <param name="Files">The recording.</param>
/// <param name="Deleted">The audio files deleted from this laptop, by name.</param>
/// <param name="Bytes">How much room that freed.</param>
/// <param name="Problem">Why nothing, or not all of it, was deleted, in plain words; null when all its audio was freed, or there was none.</param>
public sealed record FreedAudio(RecordingFiles Files, IReadOnlyList<string> Deleted, long Bytes, string? Problem);

/// <summary>
/// Frees room on the laptop: deletes a recording's audio, its .wav, _partN.wav, .mic.wav and _partN.mic.wav files, once each
/// is on Google Drive. Before anything is deleted, every one of them is checked again: its upload was checked when it went up
/// (the manifest says so), its MD5 is worked out afresh from the file itself, and Drive is asked now for its copy, which must
/// be outside the trash with the same size and MD5. Only when all of them pass is any deleted, so a recording never keeps
/// part of its audio by mistake; one that fails is named with why, and nothing of that recording is deleted. Never its
/// transcripts, ElevenLabs' answers, its marks or its log, nor any other file in its folder, and never a recording being
/// recorded or transcribed. Nothing on Drive is touched: the copies there stay as they are. The manifest notes what was freed,
/// so the recording counts as done and safe on Drive, never as waiting. One per run.
/// </summary>
public sealed class LocalAudioFreer(DriveClient drive, DriveRetry retry, UploadManifests manifests, RecordingUpload uploads, TimeProvider time, ILogger<LocalAudioFreer> logger)
{
    /// <summary>ERROR_SHARING_VIOLATION as an HRESULT: another program holds the file in a way that does not allow this open.</summary>
    private const int SharingViolation = unchecked((int)0x80070020);

    /// <summary>
    /// Deletes the audio of <paramref name="files"/> from this laptop once every file of it is checked against its copy on
    /// Drive, and says what it freed, or why it freed nothing. A failure to reach Drive is thrown, with nothing deleted.
    /// </summary>
    public async Task<FreedAudio> FreeAsync(RecordingFiles files, CancellationToken cancellationToken)
    {
        if (uploads.Busy(files) is { } busy) return Refuse(files, $"{files.BaseName} {busy}, so none of its audio was deleted.");
        if (UploadCopyInUse(files)) return Refuse(files, $"{files.BaseName} is being transcribed now, so none of its audio was deleted.");

        var audio = files.FindSegments().Concat(files.FindSegments(Track.Microphone)).ToList();
        var manifest = manifests.Read(files);
        if (audio.Count == 0)
        {
            logger.LogInformation("{Recording} has no audio on this laptop to delete{Where}", files.BaseName,
                manifest is null ? "" : $"; its audio is on Google Drive, in {manifest.DriveLocation}");
            return new FreedAudio(files, [], 0, null);
        }

        if (manifest is null)
            return Refuse(files, $"{files.BaseName} has not gone up to Google Drive yet, so none of its audio was deleted. Upload it first: {CommandLine.DriveUpload(files.Segment(1))}");

        List<(string Path, ManifestFile Entry)> checkedFiles = [];
        List<string> problems = [];
        foreach (var path in audio)
        {
            var name = Path.GetFileName(path);
            if (await CheckAsync(path, name, manifest.File(name), cancellationToken) is { } problem) problems.Add(problem);
            else checkedFiles.Add((path, manifest.File(name)!));
        }

        if (problems.Count > 0)
        {
            foreach (var problem in problems) logger.LogWarning("{Problem}", problem);
            return Refuse(files, $"None of the audio of {files.BaseName} was deleted: {problems[0]}");
        }

        List<string> deleted = [];
        long bytes = 0;
        string? failure = null;
        Dictionary<string, ManifestFile> freed = new(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, entry) in checkedFiles)
        {
            var size = new FileInfo(path).Length;
            var seconds = entry.Seconds ?? ClassFolders.MeasureSegment(path).TotalSeconds;
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Deliberately not rethrown: a file that cannot be deleted now, as one another program holds, stays where it
                // is with its copy on Drive; the others are freed, and this says which was kept and why.
                failure ??= $"{Path.GetFileName(path)} could not be deleted ({ex.Message.TrimEnd('.')}); it stays on this laptop";
                logger.LogWarning("{Problem}", failure);
                continue;
            }

            deleted.Add(Path.GetFileName(path));
            bytes += size;
            freed[entry.Name] = entry with { FreedUtc = time.GetUtcNow(), Seconds = seconds, Session = null };
        }

        // Noted in the manifest as it is now, so that a queue written meanwhile, as by a transcript made in another window, stays.
        var now = manifests.Read(files) ?? manifest;
        manifests.Write(now with { Files = [.. now.Files.Select(entry => freed.GetValueOrDefault(entry.Name, entry))] });
        if (deleted.Count > 0)
        {
            logger.LogInformation("Freed {Size} on this laptop: deleted {Count} of {Recording} ({Names}). Their copies stay on Google Drive, in {Location}; the transcripts, ElevenLabs' answers, the marks and the log stay here",
                DriveCommand.Size(bytes), deleted.Count == 1 ? "1 audio file" : $"{deleted.Count} audio files", files.BaseName, string.Join(", ", deleted), manifest.DriveLocation);
        }

        return new FreedAudio(files, deleted, bytes, failure);
    }

    /// <summary>
    /// Why the audio file <paramref name="name"/> at <paramref name="path"/> may not be deleted; null when its copy on Drive,
    /// <paramref name="entry"/>, was checked when it went up and Drive still has it, outside the trash, with the same size and
    /// the MD5 worked out now from the file itself.
    /// </summary>
    private async Task<string?> CheckAsync(string path, string name, ManifestFile? entry, CancellationToken cancellationToken)
    {
        if (entry is not { IsVerified: true, DriveId: { } driveId }) return $"{name} has not gone up to Google Drive yet.";

        long size;
        string md5;
        try
        {
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, bufferSize: 1, FileOptions.Asynchronous);
            size = file.Length;
            md5 = Convert.ToHexStringLower(await MD5.HashDataAsync(file, cancellationToken));
        }
        catch (IOException ex) when (ex.HResult == SharingViolation)
        {
            // Deliberately not rethrown: a file another program holds open, as a recorder holds the segment it writes, is not
            // deleted; this is the reason given.
            return $"{name} is open in another program, as while it is being recorded.";
        }

        var copy = await retry.RunAsync(token => drive.GetFileAsync(driveId, token), "show a file", cancellationToken);
        if (copy is null || copy.Trashed == true) return $"{name}'s copy on Google Drive is no longer there, or is in the trash.";
        if (copy.Size != size || !string.Equals(copy.Md5Checksum, md5, StringComparison.OrdinalIgnoreCase))
            return $"{name} differs from its copy on Google Drive (here {size} bytes, MD5 {md5}; there {copy.Size?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "no size"}, MD5 {copy.Md5Checksum ?? "none"}).";

        return null;
    }

    /// <summary>True when the recording's upload copy is open in a program now, as while ElevenLabs is sent it.</summary>
    private static bool UploadCopyInUse(RecordingFiles files)
    {
        try
        {
            using var copy = new FileStream(files.Upload, FileMode.Open, FileAccess.Read, FileShare.Delete, bufferSize: 1);
            return false;
        }
        catch (IOException ex) when (ex.HResult == SharingViolation)
        {
            // Deliberately not rethrown: a sharing violation is the answer this look is for, that a program has the copy open.
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // Deliberately not rethrown: without an upload copy, nothing transcribes the recording in a window now.
            return false;
        }
    }

    /// <summary>Says <paramref name="problem"/>, and returns that nothing of <paramref name="files"/> was deleted.</summary>
    private FreedAudio Refuse(RecordingFiles files, string problem)
    {
        logger.LogError("{Problem}", problem);
        return new FreedAudio(files, [], 0, problem);
    }
}
