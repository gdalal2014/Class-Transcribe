namespace ClassScribe.Transcription;

/// <summary>Where a transcription is, as whoever shows it is told (see <see cref="TranscriptionProgress"/>).</summary>
public enum TranscriptionStep
{
    /// <summary>Making the smaller copy that is uploaded.</summary>
    Preparing,

    /// <summary>Uploading to ElevenLabs; the percent, when it is known, says how much of the upload has gone.</summary>
    Uploading,

    /// <summary>Uploaded; waiting for ElevenLabs' transcript.</summary>
    WaitingForTranscript
}

/// <summary>
/// Hands a transcription's progress from where it happens to whoever shows it, within one run's scope: the workflow says
/// when it prepares the upload copy and when the upload begins, the client how much of the upload has gone and when all of
/// it has, and the record command shows each on the window's title and taskbar (UPLOADING 40%) and writes it into the run
/// file's heartbeat, so that the menu and the list can say "(uploading 40%)" of a transcription in the background. A
/// transcription nobody follows, as the transcribe command runs, reports to no one. One per scope.
/// </summary>
public sealed class TranscriptionProgress
{
    /// <summary>
    /// Raised at each step, and as the percent uploaded changes, on the thread that reports it: the step, and the percent
    /// uploaded, from 0 to 100, when it is known. What is raised must never throw: it would fail the upload.
    /// </summary>
    public event Action<TranscriptionStep, int?>? Changed;

    /// <summary>Reports <paramref name="step"/>, with <paramref name="uploadPercent"/> when it is known.</summary>
    public void Report(TranscriptionStep step, int? uploadPercent = null) => Changed?.Invoke(step, uploadPercent);
}
