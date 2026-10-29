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
/// so the recording counts as done and safe on Drive, never as waiting. A lesson's camera pieces go too once its finished
/// video's copy on Drive is checked the same way (amendment 47: they are kept for Drive only while the video goes there),
/// only those the video holds (none newer than it), and never the finished video itself. A lesson whose video is still to
/// be made (see <see cref="IClassFolders.LessonVideoStateOf"/>), as a continued lesson's whose newest part has a piece newer
/// than its video, keeps everything while its sound is here: making the video needs both (see <see cref="VideoStillToMake"/>).
/// One per run.
/// </summary>
public sealed class LocalAudioFreer(DriveClient drive, DriveRetry retry, UploadManifests manifests, RecordingUpload uploads, IClassFolders folders, IRecordingFolder recordings,
    CommandLine commandLine, TimeProvider time, ILogger<LocalAudioFreer> logger)
{
    /// <summary>ERROR_SHARING_VIOLATION as an HRESULT: another program holds the file in a way that does not allow this open.</summary>
    private const int SharingViolation = unchecked((int)0x80070020);

    /// <summary>
    /// Why nothing of the lesson <paramref name="files"/> is freed while its video is still to be made (a camera's piece
    /// without a video, or newer than it, see <see cref="IClassFolders.LessonVideoStateOf"/>): making it needs both its sound and
    /// its pieces. Names the command that makes it, free, by a file read through <paramref name="recordings"/>, printed by
    /// <paramref name="commandLine"/>.
    /// </summary>
    public static string VideoStillToMake(RecordingFiles files, IRecordingFolder recordings, CommandLine commandLine) =>
        $"The lesson video of {files.BaseName} is still to be made, so none of its audio or camera pieces were deleted: making it needs them. Make it first, free: " +
        commandLine.FinishVideo(recordings.FindSegments(files).FirstOrDefault() ?? recordings.FindSegments(files, Track.Microphone).FirstOrDefault() ?? files.Folder);

    /// <summary>
    /// Deletes the audio of <paramref name="files"/> from this laptop once every file of it is checked against its copy on
    /// Drive, then a lesson's camera pieces once its video's copy is (see <see cref="FreePiecesAsync"/>), and says what it
    /// freed, or why it freed nothing. A lesson whose video is still to be made, with its sound here, is refused whole. A
    /// failure to reach Drive is thrown, with nothing more deleted.
    /// </summary>
    public async Task<FreedAudio> FreeAsync(RecordingFiles files, CancellationToken cancellationToken)
    {
        if (uploads.Busy(files) is { } busy) return Refuse(files, $"{files.BaseName} {busy}, so none of its audio was deleted.");
        if (UploadCopyInUse(files)) return Refuse(files, $"{files.BaseName} is being transcribed now, so none of its audio was deleted.");
        if (folders.LessonVideoStateOf(files) == LessonVideoState.NotMade && (recordings.FindSegments(files).Count > 0 || recordings.FindSegments(files, Track.Microphone).Count > 0))
            return Refuse(files, VideoStillToMake(files, recordings, commandLine));

        var manifest = manifests.Read(files);
        var freed = await FreeAudioAsync(files, manifest, cancellationToken);
        return manifest is null ? freed : await FreePiecesAsync(files, freed, cancellationToken);
    }

    /// <summary>The audio's part of <see cref="FreeAsync"/>: its .wav files deleted once each is checked against its copy on Drive.</summary>
    private async Task<FreedAudio> FreeAudioAsync(RecordingFiles files, UploadManifest? manifest, CancellationToken cancellationToken)
    {
        var audio = recordings.FindSegments(files).Concat(recordings.FindSegments(files, Track.Microphone)).ToList();
        if (audio.Count == 0)
        {
            logger.LogInformation("{Recording} has no audio on this laptop to delete{Where}", files.BaseName,
                manifest is null ? "" : $"; its audio is on Google Drive, in {manifest.DriveLocation}");
            return new FreedAudio(files, [], 0, null);
        }

        // A YouTube video's audio that YouTube:DriveUpload keeps off Drive was never sent: it is not on Drive to free, and
        // that is no refusal, since the video can be fetched again.
        if (folders.IsVideo(files) && !uploads.SendsAudio(files) && !audio.Any(path => manifest?.File(Path.GetFileName(path)) is { IsVerified: true }))
        {
            logger.LogInformation(
                "{Recording} is a YouTube video's recording; its audio is not on Drive (YouTube:DriveUpload), so it stays. Delete it by hand if you need the room; the video can be fetched again.",
                files.BaseName);
            return new FreedAudio(files, [], 0, null);
        }

        if (manifest is null)
            return Refuse(files, $"{files.BaseName} has not gone up to Google Drive yet, so none of its audio was deleted. Upload it first: {commandLine.DriveUpload(files.Segment(1))}");

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
            var seconds = entry.Seconds ?? folders.MeasureSegment(path).TotalSeconds;
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
                Sizes.Describe(bytes), deleted.Count == 1 ? "1 audio file" : $"{deleted.Count} audio files", files.BaseName, string.Join(", ", deleted), manifest.DriveLocation);
        }

        return new FreedAudio(files, deleted, bytes, failure);
    }

    /// <summary>
    /// A lesson's camera pieces after its audio: deleted once its finished video, <see cref="RecordingFiles.LessonVideo"/>,
    /// is on Drive, checked when it went up and checked again now as each .wav is (its MD5 worked out afresh, Drive's copy
    /// outside the trash with the same size and MD5); otherwise they stay, the only other copy of the picture, which is said
    /// when the video's copy fails the check. Only the pieces the video holds go, those not newer than it: a newer one, whose
    /// picture is in no video yet, stays, named. Never the finished video. What it deletes is added to <paramref name="freed"/>.
    /// </summary>
    private async Task<FreedAudio> FreePiecesAsync(RecordingFiles files, FreedAudio freed, CancellationToken cancellationToken)
    {
        var all = recordings.FindCameraPieces(files).Select(piece => piece.Path).ToList();
        if (all.Count == 0) return freed;

        var video = Path.GetFileName(files.LessonVideo);
        var manifest = manifests.Read(files);
        if (manifest?.File(video) is not { IsVerified: true } entry || !File.Exists(files.LessonVideo))
        {
            logger.LogDebug("The camera's pieces of {Recording} stay on this laptop: its lesson video is not on Google Drive", files.BaseName);
            return freed;
        }

        if (await CheckAsync(files.LessonVideo, video, entry, cancellationToken) is { } problem)
        {
            var kept = $"The camera's pieces of {files.BaseName} stay: {problem}";
            logger.LogWarning("{Problem}", kept);
            return freed with { Problem = freed.Problem ?? kept };
        }

        var madeUtc = File.GetLastWriteTimeUtc(files.LessonVideo);
        var pieces = all.Where(piece => File.GetLastWriteTimeUtc(piece) <= madeUtc).ToList();
        foreach (var piece in all.Except(pieces))
        {
            var newer = $"{Path.GetFileName(piece)} is newer than the lesson video of {files.BaseName}, which does not hold it, so it stays on this laptop";
            logger.LogWarning("{Problem}", newer);
            freed = freed with { Problem = freed.Problem ?? newer };
        }

        List<string> deleted = [];
        long bytes = 0;
        string? failure = null;
        foreach (var piece in pieces)
        {
            var size = new FileInfo(piece).Length;
            try
            {
                File.Delete(piece);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Deliberately not rethrown: a piece that cannot be deleted now stays, beside the video Drive has; the others go,
                // and this says which stayed and why.
                failure ??= $"{Path.GetFileName(piece)} could not be deleted ({ex.Message.TrimEnd('.')}); it stays on this laptop";
                logger.LogWarning("{Problem}", failure);
                continue;
            }

            deleted.Add(Path.GetFileName(piece));
            bytes += size;
        }

        if (deleted.Count > 0)
        {
            logger.LogInformation("Freed the camera's {Count} of {Recording} ({Size}): its lesson video is on Google Drive, checked, in {Location}; the video stays here too",
                deleted.Count == 1 ? "1 piece" : $"{deleted.Count} pieces", files.BaseName, Sizes.Describe(bytes), manifest.DriveLocation);
        }

        return freed with { Deleted = [.. freed.Deleted, .. deleted], Bytes = freed.Bytes + bytes, Problem = freed.Problem ?? failure };
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
