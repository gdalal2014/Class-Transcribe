using ClassScribe.Desktop;
using ClassScribe.Drive;
using ClassScribe.Storage;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Commands;

/// <summary>
/// The drive command's uploads, from the command line (drive upload, drive free-space) and from menu choice 9: uploads a
/// recording, every recording in a folder, or what waits in the queue, to Google Drive (see <see cref="DriveUploadPass"/>);
/// and frees room on this laptop by deleting recordings' audio once each file's copy on Drive is checked (see
/// <see cref="LocalAudioFreer"/>). A path names a recording by any of its files; a folder, the recordings in it and in its
/// date folders (a course's folder, a class's date folder, or a folder of the old flat layout), its other folders, as one of
/// slides, passed over; or, when it holds none itself, or when its other folders hold recordings, those in each folder in
/// it, as the recordings folder holds its courses; a folder in it without a recording is named and passed over, the
/// microphone check's folder is passed over without a word, and a recording's file lying at its top, as an answer kept
/// there when its own folder would not take it, is left out, which is said with what to do. Exit codes: 0 when everything
/// asked for is done; 1 when something failed, or waits, or there was nothing to take; a cancel (Esc, Ctrl+C) is let through,
/// for App to turn into 130, with everything left waiting. Nothing records here, so a first Ctrl+C cancels. One per run.
/// </summary>
public sealed class DriveUploadCommand(DriveUploadPass pass, LocalAudioFreer freer, IClassFolders folders, IFileProbe probe, IUserSignals signals, ILogger<DriveUploadCommand> logger)
{
    /// <summary>
    /// Uploads the recordings <paramref name="path"/> names, then the rest of the queue whose next try is due; with no path,
    /// every recording waiting in the queue. Returns 0 when everything it took is on Drive, 1 otherwise.
    /// </summary>
    public async Task<int> UploadAsync(string? path)
    {
        signals.RequestStop();
        IReadOnlyList<RecordingFiles> recordings = [];
        if (path is not null)
        {
            recordings = RecordingsAt(path, "to upload");
            if (recordings.Count == 0) return 1;
        }

        var result = await pass.RunAsync(recordings, everythingWaiting: path is null, signals.Cancellation);
        return result.AllDone ? 0 : 1;
    }

    /// <summary>
    /// Deletes the audio of the recordings <paramref name="path"/> names from this laptop, each recording only once all its
    /// audio files are checked against their copies on Drive. Returns 0 when every recording's audio was freed, or had none
    /// left; 1 when any was refused or could not be checked.
    /// </summary>
    public async Task<int> FreeSpaceAsync(string path)
    {
        signals.RequestStop();
        var recordings = RecordingsAt(path, "to free");
        if (recordings.Count == 0) return 1;

        long freed = 0;
        var refused = false;
        foreach (var files in recordings)
        {
            try
            {
                var result = await freer.FreeAsync(files, signals.Cancellation);
                freed += result.Bytes;
                refused |= result.Problem is not null;
            }
            catch (GoogleDriveException ex)
            {
                // Deliberately not rethrown: Drive that cannot be asked, or a sign-in that has gone, is this command's answer,
                // said once; nothing of a recording whose copies could not be checked was deleted.
                logger.LogError("{Problem} Nothing more was deleted.", ex.Message);
                return 1;
            }
        }

        if (recordings.Count > 1) logger.LogInformation("Freed {Size} on this laptop in all", Sizes.Describe(freed));
        return refused ? 1 : 0;
    }

    /// <summary>
    /// The recordings <paramref name="path"/> names (see <see cref="DriveUploadCommand"/>), newest first in each folder;
    /// none, with why said, when it names none, and <paramref name="doing"/> says what they were wanted for.
    /// </summary>
    private IReadOnlyList<RecordingFiles> RecordingsAt(string path, string doing)
    {
        var fullPath = probe.FullPath(path);
        if (probe.FileExists(fullPath))
        {
            if (RecordingFiles.TryParse(fullPath) is { } recording) return [recording with { Folder = folders.AsOnDisk(recording.Folder) }];

            logger.LogError("{File} is not a file of a ClassScribe recording, so there is nothing {Doing} from it", fullPath, doing);
            return [];
        }

        if (!probe.DirectoryExists(fullPath))
        {
            logger.LogError("Nothing found at {Path}", fullPath);
            return [];
        }

        var folder = folders.AsOnDisk(fullPath);
        var here = folders.RecordingsByAnyFileIn(folder);
        var inFolders = folders.FoldersIn(folder)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Where(inner => !string.Equals(Path.GetFileName(inner), FolderNames.MicCheck, StringComparison.OrdinalIgnoreCase) && !FolderNames.IsDateFolder(Path.GetFileName(inner)))
            .Select(inner => (Folder: inner, Recordings: folders.RecordingsByAnyFileIn(inner)))
            .ToList();

        // A course's, a class's or an old flat folder: its own recordings, its other folders, as of slides, passed over.
        if (here.Count > 0 && inFolders.All(inner => inner.Recordings.Count == 0)) return here;

        // A folder of courses, as the recordings folder: each course in it. A recording's file lying at its top is in no
        // recording's folder, as an answer kept there when its own folder would not take it, so it does not go up.
        foreach (var empty in inFolders.Where(inner => inner.Recordings.Count == 0)) logger.LogInformation("{Folder} holds no recording; it is passed over", empty.Folder);
        var atTop = here.Where(files => string.Equals(Path.TrimEndingDirectorySeparator(files.Folder), Path.TrimEndingDirectorySeparator(folder), StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var files in atTop) SayLeftAtTop(files, folder);

        List<RecordingFiles> under = [.. here.Except(atTop), .. inFolders.SelectMany(inner => inner.Recordings)];
        if (under.Count == 0) logger.LogError("There is no ClassScribe recording in {Folder}", folder);
        return under;
    }

    /// <summary>
    /// Says that the files of <paramref name="files"/> lying at the top of <paramref name="folder"/>, a folder of courses, do
    /// not go up, and what to do: rebuild an answer kept there, which lays it out beside its recording, or move the files into
    /// the folder of their class.
    /// </summary>
    private void SayLeftAtTop(RecordingFiles files, string folder)
    {
        var names = folders.FilesIn(folder)
            .Where(path => string.Equals(RecordingFiles.TryParse(path)?.BaseName, files.BaseName, StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFileName)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var one = names.Count == 1;
        var what = $"{string.Join(", ", names)} {(one ? "lies" : "lie")} in {folder} itself, not in a recording's folder, so {(one ? "it does" : "they do")} not go up";
        if (files.FindKeptAnswers() is [var answer, ..])
            logger.LogInformation("{What}; to lay it out beside its recording, rebuild it: {Command}", what, CommandLine.Rebuild(answer));
        else
            logger.LogInformation("{What}; {Move}", what, one ? "move it into the folder of its class to upload it" : "move them into the folder of their class to upload them");
    }
}
