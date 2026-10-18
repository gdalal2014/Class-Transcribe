using ClassScribe.Commands;
using ClassScribe.Recorder;
using ClassScribe.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClassScribe.Drive;

/// <summary>
/// Google Drive's part in a run, in a recorder or in a window, and in a transcript made later: the uploads that happen by
/// themselves (design section 3), once Drive is set up and Drive:Enabled is on. Nothing of it ever reaches the run: what
/// fails, and a cancel, is said and passed over, since a copy on Drive must never cost the class or its transcript.
/// </summary>
public interface IDriveAutoUpload
{
    /// <summary>
    /// Puts <paramref name="files"/>, the recording a run records, into the queue as the run starts, so that a class whose
    /// recorder crashed or whose laptop restarted still goes up later; and keeps it, for the upload after the run. Never
    /// throws: a queue that cannot be written is said, and the end of the run queues it again.
    /// </summary>
    void Queue(RecordingFiles files);

    /// <summary>Notes how the run ended, as its outcome says, for the upload after it.</summary>
    void Ended(RunOutcome outcome);

    /// <summary>
    /// The upload after the run, once its end has gone to the windows and its results are open: after a transcript, after
    /// record-only and after a transcript that failed, the run's recording goes up, then the rest of the queue that is due.
    /// Not after a cancel, a run that recorded nothing or failed, nor one that ended without an outcome, as when Windows ended
    /// the session: the recording waits in the queue. Never throws.
    /// </summary>
    Task AfterRunAsync();

    /// <summary>
    /// Uploads <paramref name="files"/>, which a transcript made later has added to, then the rest of the queue that is due:
    /// only what is new goes up. Never throws.
    /// </summary>
    Task UploadAsync(RecordingFiles files);
}

/// <summary>
/// The uploads that happen by themselves (see <see cref="IDriveAutoUpload"/>), over one pass (<see cref="DriveUploadPass"/>),
/// which is made only when one runs. Before Drive is set up (no Google client kept), and with Drive:Enabled off, it puts
/// nothing in the queue, sends nothing and says nothing but at Debug. A first Ctrl+C cancels its upload, since nothing
/// records in the run any more by then, and the recording waits, to go on where it stopped. One per run, and so per scope.
/// </summary>
public sealed class DriveAutoUpload(
    IGoogleCredentialStore store,
    UploadManifests manifests,
    Lazy<DriveUploadPass> pass,
    IUserSignals signals,
    IOptionsSnapshot<DriveOptions> options,
    ILogger<DriveAutoUpload> logger) : IDriveAutoUpload
{
    /// <summary>The recording of the run, once it is known.</summary>
    private RecordingFiles? _recording;

    /// <summary>How the run ended, once it has.</summary>
    private RunOutcome? _outcome;

    /// <inheritdoc />
    public void Queue(RecordingFiles files)
    {
        _recording = files;
        if (!IsOn()) return;

        try
        {
            manifests.Queue(files);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Deliberately not rethrown: the queue is kept only so that a class whose recorder crashed still goes up later,
            // and must never cost the class; the end of the run queues the recording again, which this says.
            logger.LogWarning("Could not put {Recording} in the queue for Google Drive ({Reason}); it goes in when the class ends", files.BaseName, ex.Message);
        }
    }

    /// <inheritdoc />
    public void Ended(RunOutcome outcome) => _outcome = outcome;

    /// <inheritdoc />
    public Task AfterRunAsync()
    {
        if (_recording is not { } files) return Task.CompletedTask;

        if (_outcome is not { Kind: OutcomeKind.Transcribed or OutcomeKind.Recorded or OutcomeKind.TranscriptFailed })
        {
            logger.LogDebug("{Recording} does not go up to Google Drive after this run ({Outcome}); if it is in the queue, it waits there", files.BaseName,
                _outcome?.Kind.ToString() ?? "no outcome");
            return Task.CompletedTask;
        }

        return UploadAsync(files);
    }

    /// <inheritdoc />
    public async Task UploadAsync(RecordingFiles files)
    {
        if (!IsOn()) return;

        // Nothing records in this run any more, so the first Ctrl+C cancels the upload.
        signals.RequestStop();
        try
        {
            await pass.Value.RunAsync([files], everythingWaiting: false, signals.Cancellation);
        }
        catch (OperationCanceledException)
        {
            // Deliberately not rethrown: a Ctrl+C during the upload stops the copy to Drive, not the class or its transcript,
            // which are done; the pass has said what waits and how it goes on.
        }
        catch (Exception ex)
        {
            // Deliberately not rethrown: a copy on Drive must never cost the class or its transcript, so whatever fails on the
            // way is said here, with what waits, and the run ends as it would have.
            logger.LogError(ex, "The upload to Google Drive stopped because of an error ({Reason}); {Recording} waits to go up {How}", ex.Message, files.BaseName,
                DriveUploadPass.AfterTheNextClass);
        }
    }

    /// <summary>
    /// True when the uploads after each class are on (Drive:Enabled) and Drive is set up: a Google client is kept, signed in
    /// or not, or the store cannot be read, which the upload then says. Otherwise why not is said at Debug alone.
    /// </summary>
    private bool IsOn()
    {
        if (!options.Value.Enabled)
        {
            logger.LogDebug("Uploads to Google Drive after each class are off (Drive:Enabled)");
            return false;
        }

        if (store.Read() is { Credentials: null, Problem: null })
        {
            logger.LogDebug("Google Drive is not set up, so nothing goes into the queue or up");
            return false;
        }

        return true;
    }
}
