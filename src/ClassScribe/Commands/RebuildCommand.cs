using System.Text.RegularExpressions;
using ClassScribe.Desktop;
using ClassScribe.Drive;
using ClassScribe.Settings;
using ClassScribe.Storage;
using ClassScribe.Transcription;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClassScribe.Commands;

/// <summary>
/// Lays a transcript out again, for free, from an answer ElevenLabs sent earlier and ClassScribe saved: with the
/// recording's marks and the Transcript settings as they are now, written as the next free copy, so that nothing is
/// overwritten. Nothing is sent to ElevenLabs and no API key is needed. It suits labels typed into the marks after the
/// class, and a change to the Transcript settings. Once Drive is set up, the new transcript then goes up to Google Drive
/// with its recording (see <see cref="IDriveAutoUpload"/>). The settings are those of the run's own scope.
/// </summary>
public sealed partial class RebuildCommand(
    TranscriptionWorkflow workflow,
    IClassFolders folders,
    IFileProbe probe,
    IDesktop desktop,
    IDriveAutoUpload drive,
    IOptionsSnapshot<RecordingOptions> recording,
    ILogger<RebuildCommand> logger)
{
    /// <summary>
    /// Rebuilds the transcript <paramref name="path"/> names: a folder means its newest recording with a saved answer,
    /// whether or not its audio is still there, among those in it and in its date folders (a course's folder, a class's
    /// date folder, or a folder of the old layout); any file of a recording means that recording, from its newest saved
    /// answer; and an answer saved as .json means that answer. When <paramref name="open"/> is true, the new transcript is
    /// opened as the settings ask. Returns the process exit code.
    /// </summary>
    public async Task<int> RunAsync(string path, bool open)
    {
        var fullPath = probe.FullPath(path);
        RecordingFiles files;
        string? answer = null;
        if (probe.DirectoryExists(fullPath))
        {
            if (FindInFolder(fullPath) is not { } found)
            {
                logger.LogError("There is no ClassScribe recording in {Folder}", fullPath);
                return 1;
            }

            files = found;
        }
        else if (probe.FileExists(fullPath))
        {
            if (Recognise(fullPath) is not { } recognised)
            {
                logger.LogError("{File} is neither a file of a ClassScribe recording nor an answer from ElevenLabs saved as .json, so there is nothing to rebuild from it", fullPath);
                return 1;
            }

            (files, answer) = recognised;
        }
        else
        {
            logger.LogError("Nothing found at {Path}", fullPath);
            return 1;
        }

        answer ??= TranscriptCopies.NewestAnswer(files);
        if (answer is null)
        {
            SayThereIsNoAnswer(files);
            return 1;
        }

        var rebuilt = await workflow.RebuildAsync(files, answer, CancellationToken.None);
        logger.LogInformation("Transcript saved to {Path}", rebuilt);
        if (open) desktop.ShowTranscript(recording.Value, new TranscriptFiles(rebuilt, answer));
        logger.LogInformation("{Cost}", NothingCharged);

        // Last of all, the new transcript goes up to Google Drive with its recording, once Drive is set up; an answer saved
        // under a name of its own belongs to no recording, and nothing goes up.
        if (RecordingFiles.TryParse(rebuilt) is { } rebuiltRecording) await drive.UploadAsync(rebuiltRecording);
        return 0;
    }

    /// <summary>The cost line of a rebuild, where a transcription's says what it cost: nothing went to ElevenLabs.</summary>
    public const string NothingCharged = "Nothing was sent to ElevenLabs, so nothing was charged";

    /// <summary>
    /// The recording a folder stands for: the one started last among those with a saved answer, or, when none has one, the
    /// one started last. The recordings of a folder are those in it and in its date folders (see
    /// <see cref="IClassFolders.RecordingFoldersIn"/>), so a course's folder stands for its recordings in both layouts and a
    /// date folder for its own. A recording counts by any of its files, so one whose audio was deleted since counts, and
    /// so does one known only by an answer kept as unread, though that is no saved answer. Each recording started later
    /// than the one rebuilt is named with the reason it was passed over, so that the one rebuilt is not taken for it. Null
    /// when the folder holds no recording. The folders are read strictly, so one that cannot be read is an error rather
    /// than a folder without recordings.
    /// </summary>
    private RecordingFiles? FindInFolder(string folder)
    {
        var found = folders.RecordingFoldersIn(folder)
            .SelectMany(folders.FilesIn)
            .Select(file => (Folder: Path.GetDirectoryName(file)!, Match: FileOfARecording().Match(Path.GetFileName(file))))
            .Where(file => file.Match.Success && !file.Match.Groups["record"].Success)
            .ToList();
        var answered = found
            .Where(file => file.Match.Groups["answer"].Success && !file.Match.Groups["unread"].Success)
            .Select(file => Path.Combine(file.Folder, file.Match.Groups["base"].Value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var recordings = found
            .Select(file => new RecordingFiles(file.Folder, file.Match.Groups["base"].Value))
            .DistinctBy(OwnPath, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(files => files.StartedUtc ?? DateTimeOffset.MinValue)
            .ThenByDescending(files => files.BaseName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var newestAnswered = recordings.FindIndex(files => answered.Contains(OwnPath(files)));
        if (newestAnswered < 0) return recordings.FirstOrDefault();

        foreach (var newer in recordings.Take(newestAnswered)) SayPassedOver(newer, recordings[newestAnswered]);
        return recordings[newestAnswered];
    }

    /// <summary>The recording's base name in its folder, which tells it apart from a recording of the same base name in another folder.</summary>
    private static string OwnPath(RecordingFiles files) => Path.Combine(files.Folder, files.BaseName);

    /// <summary>
    /// Says why <paramref name="newer"/>, started later than <paramref name="rebuilt"/>, is passed over in a folder: the
    /// answer to an earlier try, which was paid for, is kept apart, and the command that lays it out follows, since such an
    /// answer is not taken unasked; it has a transcript whose saved answer is no longer there; or it has no saved answer yet.
    /// </summary>
    private void SayPassedOver(RecordingFiles newer, RecordingFiles rebuilt)
    {
        if (folders.KeptAnswerOf(newer) is { } kept)
        {
            logger.LogWarning("{Newer} has no saved answer from ElevenLabs that became a transcript, but the answer to an earlier try, which has been paid for, is kept in {Answer}. {Rebuilt}, the newest recording here with a saved answer, is rebuilt instead", newer.BaseName, kept, rebuilt.BaseName);
            logger.LogInformation("To lay that answer out as a transcript for free, run: {Command}", CommandLine.Rebuild(kept));
        }
        else if (newer.HasTranscript)
        {
            logger.LogInformation("{Newer} has a transcript, but its saved answer from ElevenLabs is no longer there, so {Rebuilt}, the newest recording here that still has one, is rebuilt", newer.BaseName, rebuilt.BaseName);
        }
        else
        {
            logger.LogInformation("{Newer} has no saved answer from ElevenLabs yet, so {Rebuilt}, the newest recording here that has one, is rebuilt", newer.BaseName, rebuilt.BaseName);
        }
    }

    /// <summary>
    /// The recording a file belongs to, and the answer it is when it is one: a segment, the marks, a transcript or an
    /// answer of a recording, by its name; or an answer saved under a name of its own, such as one downloaded from
    /// ElevenLabs, whose transcript then goes beside it under its name. An answer of a recording is laid out where that
    /// recording is (see <see cref="WhereRecorded"/>). Null for any other file.
    /// </summary>
    private (RecordingFiles Files, string? Answer)? Recognise(string path)
    {
        var folder = Path.GetDirectoryName(path)!;
        var name = Path.GetFileName(path);
        if (FileOfARecording().Match(name) is { Success: true } match)
        {
            var files = new RecordingFiles(folder, match.Groups["base"].Value);
            return match.Groups["answer"].Success ? (WhereRecorded(files), path) : (files, null);
        }

        return string.Equals(Path.GetExtension(name), ".json", StringComparison.OrdinalIgnoreCase)
            ? (new RecordingFiles(folder, Path.GetFileNameWithoutExtension(name)), path)
            : null;
    }

    /// <summary>
    /// The recording an answer of <paramref name="files"/> is laid out beside: the files beside the answer when the
    /// recording is there, or else those in the folder of the recording's name next to the answer, or in one of that
    /// folder's date folders, when the recording is there. That is where the recording is when its answer was kept in the
    /// recordings folder, because the recording's own folder would not take it; its transcript then goes beside the
    /// recording, with its marks and the time it was recorded, and the recording counts as transcribed. When it is in
    /// none of those places, the files beside the answer.
    /// </summary>
    private RecordingFiles WhereRecorded(RecordingFiles files)
    {
        if (IsThere(files)) return files;

        var course = Path.Combine(files.Folder, files.Name);
        if (!probe.DirectoryExists(course)) return files;

        return folders.RecordingFoldersIn(course).Select(folder => files with { Folder = folder }).FirstOrDefault(IsThere) ?? files;
    }

    /// <summary>True when the recording's audio, marks or a transcript of it are in its <see cref="RecordingFiles.Folder"/>.</summary>
    private bool IsThere(RecordingFiles files) =>
        files.FindSegments().Count > 0 || probe.FileExists(files.Marks) || TranscriptCopies.Transcripts(files).Count > 0;

    /// <summary>
    /// Says that the recording has no saved answer to rebuild from. When an answer from an earlier try that was paid for
    /// is kept as unread, it is not taken unasked, since it was set aside: the message says where it is and gives the
    /// command that lays it out. A recording with a transcript has had a saved answer, which is no longer there.
    /// </summary>
    private void SayThereIsNoAnswer(RecordingFiles files)
    {
        if (folders.KeptAnswerOf(files) is { } kept)
        {
            logger.LogError("{Recording} has no saved answer from ElevenLabs that became a transcript, but the answer to an earlier try, which has been paid for, is kept in {Answer}", files.BaseName, kept);
            logger.LogInformation("To lay that answer out as a transcript for free, run: {Command}", CommandLine.Rebuild(kept));
        }
        else if (files.HasTranscript)
        {
            logger.LogError("{Recording} has a transcript, but its saved answer from ElevenLabs is no longer there, so it cannot be rebuilt for free", files.BaseName);
        }
        else
        {
            logger.LogError("{Recording} has no saved answer from ElevenLabs yet; transcribe it first", files.BaseName);
        }
    }

    /// <summary>
    /// The name of a file of a recording (see <see cref="RecordingFiles"/>): a segment (.wav, _partN.wav), the marks
    /// (.marks.txt), the glossary record (.glossary.json), a transcript (.md, (N).md) or an answer (.json, (N).json, and
    /// .unread.json for one kept as unread). "base" is the recording's base name; "answer" is there for an answer, "unread"
    /// for one kept as unread, and "record" for the glossary record, which names the recording but, like a log, is no sign
    /// of one in a folder.
    /// </summary>
    [GeneratedRegex(@"^(?<base>.+_[0-9]{4}-[0-9]{2}-[0-9]{2}_[0-9]{2}-[0-9]{2}-[0-9]{2}Z)(?:(?:_part[0-9]{1,9})?\.wav|\.marks\.txt|(?<record>\.glossary\.json)|(?: \([0-9]{1,9}\))?(?:\.md|(?<unread>\.unread)?(?<answer>\.json)))$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FileOfARecording();
}
