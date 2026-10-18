using System.Globalization;
using System.Net;
using ClassScribe.Commands;
using ClassScribe.Drive;
using ClassScribe.Recorder;
using ClassScribe.Storage;
using ClassScribe.Transcription;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ClassScribe.YouTube;

/// <summary>One recording of a batch to transcribe.</summary>
/// <param name="Files">The recording's files.</param>
/// <param name="Video">The video it is the recording of.</param>
/// <param name="Name">The course.</param>
/// <param name="OutputRoot">The recordings folder.</param>
/// <param name="Length">The recording's length, for the projection of the credits.</param>
public sealed record VideoToTranscribe(RecordingFiles Files, VideoRequest Video, string Name, string OutputRoot, TimeSpan Length);

/// <summary>What a transcript of a batch came to.</summary>
/// <param name="TranscriptPath">The transcript.</param>
/// <param name="Charged">What it cost the account, when both reads of the credits succeeded.</param>
public sealed record VideoTranscribed(string TranscriptPath, UsageCharge? Charged);

/// <summary>
/// Makes one transcript of a batch (design 2.8), behind an interface so that the batch's tests need not run the workflow.
/// The real one (<see cref="ScopedVideoTranscriber"/>) makes each in a DI scope of its own, as transcribe does.
/// </summary>
public interface IVideoTranscriber
{
    /// <summary>
    /// Transcribes <paramref name="video"/>'s recording and returns where its transcript is. A failure is thrown as the
    /// workflow throws it, a refusal of ElevenLabs as a <see cref="TranscriptionException"/> with its status; a cancel as an
    /// <see cref="OperationCanceledException"/>.
    /// </summary>
    Task<VideoTranscribed> TranscribeAsync(VideoToTranscribe video, CancellationToken cancellationToken);
}

/// <summary>Makes each transcript of a batch in a DI scope of its own (<see cref="VideoTranscriptRun"/>): the workflow and the client are per run.</summary>
public sealed class ScopedVideoTranscriber(IServiceScopeFactory scopes) : IVideoTranscriber
{
    /// <inheritdoc />
    public async Task<VideoTranscribed> TranscribeAsync(VideoToTranscribe video, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<VideoTranscriptRun>().RunAsync(video, cancellationToken);
    }
}

/// <summary>
/// One transcript of a batch, in a scope of its own: written into the run journal while it is made (see
/// <see cref="TranscriptionRuns"/>), its lines into the recording's log, then what it cost the account, and last its new files
/// up to Google Drive, once Drive is set up, as transcribe does.
/// </summary>
public sealed class VideoTranscriptRun(
    TranscriptionWorkflow workflow,
    TranscriptionRuns runs,
    TranscriptionProgress progress,
    UsageReport usage,
    IDriveAutoUpload drive,
    RecorderLog log,
    ILogger<VideoTranscriptRun> logger)
{
    /// <summary>Transcribes <paramref name="video"/>'s recording (see <see cref="IVideoTranscriber.TranscribeAsync"/>).</summary>
    public async Task<VideoTranscribed> RunAsync(VideoToTranscribe video, CancellationToken cancellationToken)
    {
        var files = video.Files;
        log.KeepFromNow();
        log.WriteFileTo(files.Log);
        using var run = runs.Begin(video.Name, video.OutputRoot, video.Video, files, progress);
        try
        {
            var saved = await workflow.TranscribeRecordingAsync(files, cancellationToken);
            logger.LogInformation("Transcript saved to {Path}", saved.TranscriptPath);
            run.End(new RunOutcome(OutcomeKind.Transcribed, 0, $"Transcript saved to {saved.TranscriptPath}", saved.TranscriptPath, files.Folder, RetryCommand: null));
            await usage.ReportAsync(cancellationToken);
            await drive.UploadAsync(files);
            return new VideoTranscribed(saved.TranscriptPath, usage.Charged);
        }
        catch (OperationCanceledException)
        {
            run.End(new RunOutcome(OutcomeKind.Cancelled, 130, $"The transcript of \"{video.Video.Title}\" was cancelled; its audio is kept.", TranscriptPath: null, files.Folder,
                CommandLine.Transcribe(files.Segment(1))));
            throw;
        }
        catch (Exception ex)
        {
            run.End(new RunOutcome(OutcomeKind.TranscriptFailed, 1, ex.Message, TranscriptPath: null, files.Folder, CommandLine.Transcribe(files.Segment(1))));
            throw;
        }
        finally
        {
            log.EndFile();
        }
    }
}

/// <summary>What happened to one recording in a batch's transcripts.</summary>
public enum TranscriptStep
{
    /// <summary>Transcribed.</summary>
    Transcribed,

    /// <summary>Its transcript failed, for a reason of its own; the audio is kept.</summary>
    Failed,

    /// <summary>Not tried: the transcripts stopped before it, at ElevenLabs' refusal or Q.</summary>
    Left
}

/// <summary>What happened to one recording in a batch's transcripts.</summary>
/// <param name="Video">The recording.</param>
/// <param name="Step">What happened.</param>
/// <param name="TranscriptPath">Its transcript, once made.</param>
/// <param name="Problem">Why it failed or was left, in words.</param>
public sealed record TranscriptOutcome(VideoToTranscribe Video, TranscriptStep Step, string? TranscriptPath, string? Problem);

/// <summary>
/// A batch's transcripts (design 2.8, step 10): each recording in turn, in a scope of its own, after any class that records
/// has ended. ElevenLabs refusing the key (401, 403), the credits running out (402) or the account rate limited once the
/// client's own tries are spent (429) would fail every transcript left the same way, so the transcripts stop there, with
/// ElevenLabs' words, the recordings left, and that the same command transcribes them once it is fixed; the audio is all
/// kept. Any other failure is that recording's alone. After the first transcript, when the account was read before and
/// after it, one line projects the rest in the account's own credits; dollars are never turned into credits.
/// </summary>
public sealed class BatchTranscripts(IVideoTranscriber transcriber, ClassWait wait, ILogger<BatchTranscripts> logger)
{
    /// <summary>The statuses at which ElevenLabs would refuse every transcript left.</summary>
    private static readonly HashSet<HttpStatusCode> StopsAll = [HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.PaymentRequired, HttpStatusCode.TooManyRequests];

    /// <summary>
    /// Transcribes <paramref name="videos"/> in order, unless <paramref name="stopAfterThis"/> says to stop after the one in
    /// hand (Q), and returns what happened to each. A cancel is thrown, with the transcript in hand cancelled.
    /// </summary>
    public async Task<IReadOnlyList<TranscriptOutcome>> RunAsync(IReadOnlyList<VideoToTranscribe> videos, Func<bool> stopAfterThis, CancellationToken cancellationToken)
    {
        List<TranscriptOutcome> outcomes = [];
        for (var i = 0; i < videos.Count; i++)
        {
            var video = videos[i];
            if (stopAfterThis())
            {
                outcomes.AddRange(videos.Skip(i).Select(left => new TranscriptOutcome(left, TranscriptStep.Left, null, "the transcripts were stopped with Q")));
                break;
            }

            await wait.WhileAClassRecordsAsync(cancellationToken);
            logger.LogInformation("Transcript {Number} of {Count}: \"{Title}\" ({Recording})", i + 1, videos.Count, video.Video.Title, video.Files.BaseName);
            try
            {
                var transcribed = await transcriber.TranscribeAsync(video, cancellationToken);
                outcomes.Add(new TranscriptOutcome(video, TranscriptStep.Transcribed, transcribed.TranscriptPath, null));
                if (i == 0 && transcribed.Charged is { } charged) Project(video, charged, videos.Skip(1).ToList());
            }
            catch (TranscriptionException ex) when (ex.StatusCode is { } status && StopsAll.Contains(status))
            {
                // Deliberately not rethrown: every transcript left would be refused the same way, so they stop here, said
                // with ElevenLabs' words and what is left; the audio is kept for the same command later.
                var left = videos.Skip(i).ToList();
                logger.LogError("The transcripts stopped: {Reason}", ex.Message);
                logger.LogWarning("Not transcribed: {Recordings}. Once that is fixed, run the same command again: what is done is skipped, and these are transcribed",
                    string.Join(", ", left.Select(item => $"\"{item.Video.Title}\" ({item.Files.BaseName})")));
                outcomes.AddRange(left.Select(item => new TranscriptOutcome(item, item == video ? TranscriptStep.Failed : TranscriptStep.Left, null, ex.Message)));
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Deliberately not rethrown: this recording's transcript failed for a reason of its own, which the workflow
                // has said; its audio is kept, and the next recording goes on.
                logger.LogError("The transcript of \"{Title}\" failed: {Reason}", video.Video.Title, ex.Message);
                outcomes.Add(new TranscriptOutcome(video, TranscriptStep.Failed, null, ex.Message));
            }
        }

        return outcomes;
    }

    /// <summary>
    /// Says what the rest of the transcripts would use at the rate of the first, in the account's own credits: "The first
    /// transcript used 5,200 credits for 01:02:33; the other 8 (8 h 39 min) would use about 43,268 at that rate; 56,638 are
    /// left this period." Nothing when the first used none, or nothing is left to transcribe.
    /// </summary>
    private void Project(VideoToTranscribe first, UsageCharge charged, IReadOnlyList<VideoToTranscribe> others)
    {
        if (others.Count == 0 || charged.Credits <= 0 || first.Length <= TimeSpan.Zero) return;

        var rest = others.Aggregate(TimeSpan.Zero, (sum, other) => sum + other.Length);
        var projected = (long)Math.Round(charged.Credits * rest.TotalSeconds / first.Length.TotalSeconds, MidpointRounding.AwayFromZero);
        var left = charged.Left is { } credits ? $"; {Number(credits)} are left this period" : "";
        logger.LogInformation("The first transcript used {Credits} credits for {Length}; the other {Count} ({Rest}) would use about {Projected} at that rate{Left}",
            Number(charged.Credits), Durations.Format(first.Length), others.Count, Hours(rest), Number(projected), left);
    }

    /// <summary>A length as hours and minutes, such as 8 h 39 min, or minutes alone under an hour.</summary>
    internal static string Hours(TimeSpan length) =>
        length >= TimeSpan.FromHours(1)
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)length.TotalHours} h {length.Minutes} min")
            : string.Create(CultureInfo.InvariantCulture, $"{Math.Max(1, (int)Math.Round(length.TotalMinutes))} min");

    /// <summary>A count of credits with thousands separated, such as 43,362.</summary>
    private static string Number(long count) => count.ToString("N0", CultureInfo.InvariantCulture);
}
