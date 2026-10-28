using System.Diagnostics;
using ClassScribe.Audio;
using ClassScribe.Recorder;
using ClassScribe.Runs;
using ClassScribe.Storage;
using ClassScribe.Transcription;
using Microsoft.Extensions.Logging;

namespace ClassScribe.YouTube;

/// <summary>
/// The run records of a batch's transcripts (design 2.8): each transcript a batch makes in its window is written into the run
/// journal as a run of this process, in this window, transcribing the video's recording, so that every guard that looks at
/// the runs sees it: transcribe and menu choice 3 in another window refuse it rather than bill it twice, Google Drive waits
/// for it, the list and the menu's header show it with its upload's percent, and a window closed in the middle leaves it to
/// the next start's report as an interrupted transcription. The run's request is the video's: a request with
/// <see cref="RecordRequest.Video"/> set, which the class machinery never takes for a class.
/// </summary>
public sealed class TranscriptionRuns(IRunJournal journal, TimeProvider time, ILogger<TranscriptionRuns> logger)
{
    /// <summary>
    /// Begins the run file of the transcript of <paramref name="files"/>, the recording of <paramref name="video"/> in the
    /// course <paramref name="name"/>, under <paramref name="outputRoot"/>, following <paramref name="progress"/> into its
    /// heartbeat. A run file that cannot be written is said, and the transcript goes on without it: it is bookkeeping.
    /// </summary>
    public TranscriptionRun Begin(string name, string outputRoot, VideoRequest video, RecordingFiles files, TranscriptionProgress progress)
    {
        var now = time.GetUtcNow();
        var request = new RecordRequest(RunIds.New(now), name, Transcribe: false, new SourcePlan(RecordingMode.Class, new ClassSource.Automatic(), Microphone: null),
            outputRoot, ContinueChoice.New, ContinueBaseName: null, Minutes: null, Open: false, video);
        var (processId, processStartedUtc) = ThisProcess();
        var record = new RunRecord(RunRecord.CurrentVersion, request.RunId, processId, processStartedUtc, Environment.ProcessPath ?? AppContext.BaseDirectory,
            BuildInfo.Build, LinkVersion.Current, Pipe: null, RunHome.InWindow, request, files.BaseName, files.Folder, RunState.Transcribing,
            now, now, TimeSpan.Zero, UploadPercent: null, EndedBy: null, Outcome: null, Seen: false);

        IRunHandle? handle = null;
        try
        {
            handle = journal.Begin(record);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Deliberately not rethrown: the run file is bookkeeping, and the transcript must not be lost for it; without it,
            // another window could transcribe this recording at the same time, which the warning says.
            logger.LogWarning("Could not write the run file of the transcript of {Recording} ({Reason}); until it is done, do not transcribe it in another window",
                files.BaseName, ex.Message);
        }

        return new TranscriptionRun(handle, journal, progress, time);
    }

    /// <summary>This process's id and the time it started, which tell it from a later process given the same id.</summary>
    internal static (int Id, DateTimeOffset StartedUtc) ThisProcess()
    {
        using var process = Process.GetCurrentProcess();
        return (process.Id, new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero));
    }
}

/// <summary>
/// One transcript's run file, while the transcript is made: its heartbeat follows the progress (each step, the first percent,
/// then at most every 10 seconds), and its end gives the outcome and marks it seen, since the batch's window shows it. Let go
/// without an end, as when the window closes, the file keeps saying transcribing, for the next start to report.
/// </summary>
public sealed class TranscriptionRun : IDisposable
{
    private readonly IRunHandle? _handle;
    private readonly IRunJournal _journal;
    private readonly TranscriptionProgress _progress;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private TranscriptionStep? _lastStep;
    private int? _lastPercent;
    private long _lastHeartbeat;

    /// <summary>The run over <paramref name="handle"/>, null when its file could not be written, following <paramref name="progress"/>.</summary>
    internal TranscriptionRun(IRunHandle? handle, IRunJournal journal, TranscriptionProgress progress, TimeProvider time)
    {
        _handle = handle;
        _journal = journal;
        _progress = progress;
        _time = time;
        progress.Changed += Follow;
    }

    /// <summary>The run's id; null when its file could not be written.</summary>
    public string? RunId => _handle?.Current.RunId;

    /// <summary>Ends the run with <paramref name="outcome"/> and marks it seen: the batch's window has shown it.</summary>
    public void End(RunOutcome outcome)
    {
        if (_handle is null) return;

        _handle.End(outcome);
        _journal.MarkSeen(_handle.Current.RunId);
    }

    /// <summary>Stops following the progress and lets the run file go.</summary>
    public void Dispose()
    {
        _progress.Changed -= Follow;
        _handle?.Dispose();
    }

    /// <summary>Writes the upload's percent into the heartbeat: at each step, at the first percent, then at most every 10 seconds on the steady clock. Never throws.</summary>
    private void Follow(TranscriptionStep step, int? percent)
    {
        var shown = step switch
        {
            TranscriptionStep.Preparing => null,
            TranscriptionStep.Uploading => percent,
            _ => 100
        };

        bool due;
        lock (_gate)
        {
            var now = _time.GetTimestamp();
            due = step != _lastStep || (shown is not null) != (_lastPercent is not null) || _time.GetElapsedTime(_lastHeartbeat, now) >= RunTimings.HeartbeatInterval;
            if (due) (_lastStep, _lastPercent, _lastHeartbeat) = (step, shown, now);
        }

        if (due) _handle?.Heartbeat(TimeSpan.Zero, shown);
    }
}
