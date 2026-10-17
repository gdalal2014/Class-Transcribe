using System.Globalization;
using ClassScribe.Desktop;
using ClassScribe.Settings;
using ClassScribe.Storage;
using ClassScribe.Transcription;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClassScribe.Commands;

/// <summary>What to transcribe, and how.</summary>
/// <param name="Path">
/// A recording's folder (its newest recording is meant), any file of a recording that still has its audio (a segment, its
/// transcript, its answer, its marks, its log or its upload copy), or any other audio or video file.
/// </param>
/// <param name="OutputRoot">
/// The folder given with --output, as a full path: the transcript of a file that is not a recording's goes into a folder
/// named after the file inside it. Null for Recording:OutputFolder. A recording's transcript goes beside the recording
/// either way.
/// </param>
/// <param name="Open">False to open nothing when the transcript is ready (--no-open); otherwise the settings decide.</param>
/// <param name="Again">
/// True to transcribe a recording again on the command line (--again), although it already has a transcript, or an
/// answer from an earlier try that was paid for. Either way that is billed again.
/// </param>
/// <param name="FromCommandLine">
/// True for a command given on the command line, or a file dropped on the app, which nobody is asked about: a recording
/// that would be billed again then needs <paramref name="Again"/>. Menu choices 3 and 4 ask before they send such a
/// recording, so their requests pass.
/// </param>
public sealed record TranscribeRequest(string Path, string? OutputRoot = null, bool Open = true, bool Again = false, bool FromCommandLine = false);

/// <summary>
/// Transcribes audio that already exists: a recording, named by its folder or by one of its files, or any other audio or
/// video file. A recording still being recorded is refused, and so, on the command line without --again, is one
/// that would be billed a second time. Given a folder, it names the folder's other recordings that have no transcript.
/// The laptop is kept awake while the audio is prepared and uploaded and the answer awaited. The settings are those of
/// the run's own scope.
/// </summary>
public sealed class TranscribeCommand(
    TranscriptionWorkflow workflow,
    ClassFolders folders,
    IUserSignals signals,
    IDesktop desktop,
    IPowerRequests power,
    IPowerSource powerSource,
    IOptionsSnapshot<RecordingOptions> recording,
    IOptionsSnapshot<ElevenLabsOptions> elevenLabs,
    TimeProvider time,
    ILogger<TranscribeCommand> logger)
{
    /// <summary>
    /// Transcribes what the request's path names: a folder means the recording started last in it, any file of a
    /// recording that still has its audio means that whole recording, and any other file is sent on its own, its
    /// transcript going under the request's output folder when it gives one. Returns the process exit code.
    /// </summary>
    public async Task<int> RunAsync(TranscribeRequest request)
    {
        // Nothing is recording here, so the first Ctrl+C should already cancel the upload.
        signals.RequestStop();

        var fullPath = Path.GetFullPath(request.Path);
        if (Directory.Exists(fullPath)) return await TranscribeFolderAsync(fullPath, request);

        if (File.Exists(fullPath))
        {
            return RecordingFiles.TryParse(fullPath) is { } recorded && recorded.FindSegments().Count > 0
                ? await TranscribeRecordingAsync(recorded, request)
                : await TranscribeFileAsync(fullPath, request);
        }

        logger.LogError("Nothing found at {Path}", fullPath);
        return 1;
    }

    /// <summary>
    /// Transcribes the recording started last in <paramref name="folder"/>. The folder's other recordings without a
    /// transcript are named before anything is sent and again once the transcript is saved, so that none is missed:
    /// a class split by a crash, for one, is two recordings.
    /// </summary>
    private async Task<int> TranscribeFolderAsync(string folder, TranscribeRequest request)
    {
        if (ClassFolders.FindNewest(folder) is not { } newest)
        {
            logger.LogError("There is no ClassScribe recording in {Folder}", folder);
            return 1;
        }

        var others = folders.RecordingsIn(folder).Where(other => other.Files != newest && !other.HasTranscript).ToList();
        NameOthers(others);
        var exitCode = await TranscribeRecordingAsync(newest, request);
        if (exitCode == 0) NameOthers(others);
        return exitCode;
    }

    /// <summary>
    /// Transcribes a recording from all its segments on disk, with its marks, unless it is refused (see
    /// <see cref="Refuses"/>) or there is no key. The laptop is kept awake meanwhile (see <see cref="KeepAwake"/>). The
    /// transcript goes beside the recording, with its audio, marks and earlier transcripts, so an output folder the
    /// request gives is not used, and that is said.
    /// </summary>
    private async Task<int> TranscribeRecordingAsync(RecordingFiles files, TranscribeRequest request)
    {
        if (Refuses(files, request) || !HasKey()) return 1;

        if (request.OutputRoot is not null)
            logger.LogInformation("--output is not used for a recording: its transcript goes beside it, in {Folder}", files.Folder);
        logger.LogInformation("Transcribing the recording {Recording}", files.BaseName);
        TranscriptFiles saved;
        using (KeepAwake()) saved = await workflow.TranscribeRecordingAsync(files, signals.Cancellation);
        Done(saved, request);
        return 0;
    }

    /// <summary>
    /// Transcribes a file that is not a recording's, on its own, when there is a key, keeping the laptop awake meanwhile.
    /// Its transcript goes into a folder named after it under the request's output folder, or under Recording:OutputFolder.
    /// </summary>
    private async Task<int> TranscribeFileAsync(string path, TranscribeRequest request)
    {
        if (!HasKey()) return 1;

        TranscriptFiles saved;
        using (KeepAwake()) saved = await workflow.TranscribeFileAsync(path, request.OutputRoot, signals.Cancellation);
        Done(saved, request);
        return 0;
    }

    /// <summary>
    /// Asks Windows to keep the laptop awake until the result is disposed, as a recording does, with the screen as
    /// Recording:KeepScreenOn says. A laptop that sleeps during a long upload drops it, and may drop the answer after
    /// ElevenLabs has charged for it. A request Windows refuses is logged, and the upload goes on without it.
    /// </summary>
    private IDisposable KeepAwake()
    {
        var screen = recording.Value.KeepScreenOn;
        return power.Hold(StayAwake.TranscribingReason, () => StayAwake.Needs(screen, powerSource));
    }

    /// <summary>
    /// True, after saying why, when the recording is not to be sent: it is still being recorded, here or in another
    /// window, so what was sent would stop short; or, on the command line without --again, it would be billed a second
    /// time, because it already has a transcript or an answer from an earlier try that was paid for. Menu choices 3 and 4
    /// ask before they send such a recording, so their requests pass. This comes before the check for a key, because the
    /// free rebuild offered here needs none. For a recording with a transcript, the rebuild offered is the one the menu
    /// offers: from its newest saved answer, or, when that is no longer there, from an answer paid for and kept apart.
    /// </summary>
    private bool Refuses(RecordingFiles files, TranscribeRequest request)
    {
        if (ClassFolders.IsBeingWritten(files))
        {
            logger.LogError("{Recording} is being recorded now; transcribe it once it has stopped", files.BaseName);
            return true;
        }

        if (!request.FromCommandLine || request.Again) return false;

        var firstSegment = FirstSegment(files);
        if (TranscriptCopies.Transcripts(files) is [.., var transcript])
        {
            logger.LogError("{Recording} already has a transcript ({Transcript}). Transcribing it again is billed again, so nothing was sent", files.BaseName, Path.GetFileName(transcript));
            if (TranscriptCopies.NewestAnswer(files) is not null)
                logger.LogInformation("To lay it out again for free, from ElevenLabs' saved answer, run: {Command}", CommandLine.Rebuild(firstSegment));
            else if (folders.KeptAnswerOf(files) is { } keptAnswer)
                logger.LogInformation("To make a new transcript for free from ElevenLabs' answer kept in {Answer}, run: {Command}", keptAnswer, CommandLine.Rebuild(keptAnswer));
        }
        else if (folders.KeptAnswerOf(files) is { } kept)
        {
            logger.LogError("ElevenLabs' answer to an earlier try at {Recording}, which has been paid for, is kept in {Answer}. Transcribing it again is billed again, so nothing was sent", files.BaseName, kept);
            logger.LogInformation("To lay that answer out as a transcript for free, run: {Command}", CommandLine.Rebuild(kept));
        }
        else
        {
            return false;
        }

        logger.LogInformation("To transcribe it again all the same, run: {Command}", CommandLine.Transcribe(firstSegment, again: true));
        return true;
    }

    /// <summary>True when there is an API key; otherwise it says where the key belongs.</summary>
    private bool HasKey()
    {
        if (!string.IsNullOrWhiteSpace(elevenLabs.Value.ApiKey)) return true;

        logger.LogError("No ElevenLabs API key found. Set the {Variable} environment variable and open a new terminal (in VS Code, quit every VS Code window and start it again; a new terminal inside it keeps the old settings)", ElevenLabsOptions.ApiKeyVariable);
        return false;
    }

    /// <summary>Says where the transcript was saved, and opens it as the settings ask, unless the request says to open nothing.</summary>
    private void Done(TranscriptFiles saved, TranscribeRequest request)
    {
        logger.LogInformation("Transcript saved to {Path}", saved.TranscriptPath);
        if (request.Open) desktop.ShowTranscript(recording.Value, saved);
    }

    /// <summary>
    /// Names each of a folder's other recordings that has no transcript: its length, when it started in local time, and
    /// the command that transcribes it. For one whose answer from an earlier try was paid for and kept, it gives that
    /// answer and the command that lays it out for free instead, since transcribing it again is billed again.
    /// </summary>
    private void NameOthers(IReadOnlyList<RecordedClass> others)
    {
        foreach (var other in others)
        {
            var details = other.Files.StartedUtc is { } started
                ? $"{Durations.Format(other.Length)}, {TimeZoneInfo.ConvertTime(started, time.LocalTimeZone).ToString("d MMM HH:mm", CultureInfo.InvariantCulture)} local"
                : Durations.Format(other.Length);
            if (other.KeptAnswer is { } kept)
            {
                logger.LogInformation(
                    "Also in this folder, without a transcript: {Recording} ({Details}). ElevenLabs' answer to an earlier try, which has been paid for, is kept in {Answer}. To lay it out as a transcript for free: {Command}",
                    other.Files.BaseName, details, kept, CommandLine.Rebuild(kept));
            }
            else
            {
                logger.LogInformation(
                    "Also in this folder, without a transcript: {Recording} ({Details}). To transcribe it: {Command}",
                    other.Files.BaseName, details, CommandLine.Transcribe(FirstSegment(other.Files)));
            }
        }
    }

    /// <summary>
    /// The recording's first segment on disk, which the commands printed for it name; the bare first segment's name when
    /// none is left, as when its audio was deleted since it was listed.
    /// </summary>
    private static string FirstSegment(RecordingFiles files) => files.FindSegments() is [var first, ..] ? first : files.Segment(1);
}
