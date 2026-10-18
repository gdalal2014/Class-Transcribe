using ClassScribe.Desktop;
using ClassScribe.Recorder;
using ClassScribe.Settings;
using ClassScribe.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClassScribe.Drive;

/// <summary>What one upload pass came to.</summary>
/// <param name="Recordings">Each recording it took, in order, and where it stands.</param>
/// <param name="Stopped">What ended the pass before it was through, in plain words; null when nothing did.</param>
public sealed record PassResult(IReadOnlyList<RecordingUploadResult> Recordings, string? Stopped)
{
    /// <summary>
    /// True when nothing stopped the pass and every recording it took is done, or was taken out of the queue because its
    /// folder was moved or deleted (<see cref="RecordingOutcome.Missing"/>): nothing it took waits.
    /// </summary>
    public bool AllDone => Stopped is null && Recordings.All(recording => recording.Outcome is RecordingOutcome.Done or RecordingOutcome.Missing);
}

/// <summary>
/// One pass of uploads to Google Drive: the recordings asked for first, then every other recording waiting in the queue (only
/// those whose next try is due, unless the whole queue is asked for), until none is left that it has not tried. A recording
/// being recorded or transcribed when the pass comes to it is passed over, and looked at once more before the pass lets go,
/// so that a class whose run ends meanwhile goes up with it. Before anything, the recordings asked for go into the queue,
/// once ClassScribe has a Google client: before the first sign-in nothing is queued, and one done and unchanged since stays
/// done (<see cref="RecordingUpload.IsUpToDate"/>). Without a sign-in they wait, which is said. One pass at a time, across
/// processes, under the upload lock (<see cref="DriveUploadLock"/>): a pass that finds it held leaves its recordings in the
/// queue, which the holder takes before it lets go, even one it was uploading as they were queued, which it takes once more
/// (see <see cref="UploadManifest.TimesQueued"/>). The laptop is kept awake while the
/// pass runs (<see cref="StayAwake.UploadingReason"/>), and while a class records, in this window or in the background, the
/// pass waits between pieces, looking every 30 seconds, so the class's own sound and microphone are not held up; it never
/// looks at recording.lock. Google out of reach after asking again, a full Drive or a sign-in that has gone ends the pass,
/// with what waits and how it goes up (after the next class too, while Drive:Enabled is on); a cancel leaves everything
/// waiting, to go on where it stopped. One per run.
/// </summary>
public sealed class DriveUploadPass(
    RecordingUpload upload,
    UploadManifests manifests,
    DriveUploadLock uploadLock,
    IGoogleCredentialStore store,
    RunJournal journal,
    IPowerRequests power,
    IPowerSource powerSource,
    IOptionsSnapshot<RecordingOptions> recording,
    IOptionsSnapshot<DriveOptions> drive,
    TimeProvider time,
    ILogger<DriveUploadPass> logger)
{
    /// <summary>How often a pass held up by a class that records looks whether it has stopped.</summary>
    public static readonly TimeSpan PausePoll = TimeSpan.FromSeconds(30);

    /// <summary>How the queue is told to go up by hand: menu choice 9, or the drive upload command.</summary>
    public const string HowTheQueueGoesUp = "with menu choice 9 > U, or ClassScribe drive upload";

    /// <summary>How the queue is told to go up while the uploads after each class are on (Drive:Enabled).</summary>
    public const string AfterTheNextClass = $"after the next class, or {HowTheQueueGoesUp}";

    /// <summary>How the queue goes up, as the settings of this run have it.</summary>
    private string How => drive.Value.Enabled ? AfterTheNextClass : HowTheQueueGoesUp;

    /// <summary>
    /// Uploads <paramref name="recordings"/>, then the queue: with <paramref name="everythingWaiting"/>, every recording that
    /// waits; otherwise those whose next try is due. Returns what it came to; a cancel is thrown, with everything left waiting.
    /// </summary>
    public async Task<PassResult> RunAsync(IReadOnlyList<RecordingFiles> recordings, bool everythingWaiting, CancellationToken cancellationToken)
    {
        var stored = store.Read();
        if (stored is { Credentials: null, Problem: null })
        {
            logger.LogError("{Problem}", GoogleTokens.NotSetUp);
            return new PassResult([], GoogleTokens.NotSetUp);
        }

        var queued = recordings.Where(files => !upload.IsUpToDate(files)).ToList();
        foreach (var files in queued) manifests.Queue(files);

        if (stored.Problem is { } unreadable) return Stop(unreadable, recordings);
        if (stored.Credentials is not { IsSignedIn: true }) return Stop(GoogleTokens.NotSignedIn, recordings);

        using var held = uploadLock.TryTake();
        if (held is null)
        {
            if (recordings.Count > 0 && queued.Count == 0)
            {
                // What was asked for is on Drive already, unchanged since, so nothing of it waits for the other upload.
                logger.LogInformation("Another ClassScribe is uploading to Google Drive; {Names} {Is} on Drive already, unchanged since it went up",
                    Names(recordings), recordings.Count == 1 ? "is" : "are");
                return new PassResult([.. recordings.Select(files => new RecordingUploadResult(files, RecordingOutcome.Done, null))], null);
            }

            var names = queued.Count == 0 ? "the recordings waiting" : Names(queued);
            var message = $"Another ClassScribe is uploading to Google Drive; {names} {(queued.Count == 1 ? "waits and goes" : "wait and go")} up after it.";
            logger.LogInformation("{Message}", message);
            return new PassResult([], message);
        }

        var screen = recording.Value.KeepScreenOn;
        using var awake = power.Hold(StayAwake.UploadingReason, () => StayAwake.Needs(screen, powerSource));
        var taken = new Taken(manifests);
        try
        {
            foreach (var files in recordings)
            {
                if (!taken.Has(files)) await TakeAsync(taken, files, cancellationToken);
            }

            while (manifests.Waiting().Where(waiting => taken.MayTake(waiting.Recording, upload) && (everythingWaiting || IsDue(waiting))).ToList() is [_, ..] next)
            {
                foreach (var waiting in next) await TakeAsync(taken, waiting.Recording, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is GoogleDriveException or UploadNotTrackedException)
        {
            // Deliberately not rethrown: Google out of reach after asking again, a full Drive, a sign-in that has gone, or a
            // manifest that cannot be written ends the pass, and that is its answer; every recording not done stays in the
            // queue, which this says, with how it goes up.
            return Stop(ex.Message, taken.Results, recordings);
        }
        catch (OperationCanceledException)
        {
            var waiting = manifests.Waiting().Count;
            logger.LogWarning("The upload to Google Drive was stopped. {Count} {Recordings} {Wait}, and {Go} on from where {It} stopped {How}",
                waiting, waiting == 1 ? "recording" : "recordings", waiting == 1 ? "waits" : "wait", waiting == 1 ? "goes" : "go", waiting == 1 ? "it" : "they", How);
            throw;
        }

        if (taken.Results.Count == 0) logger.LogInformation("Nothing waits to go up to Google Drive.");
        return new PassResult(taken.Results, null);
    }

    /// <summary>Takes <paramref name="files"/>: notes how often it was queued as the try begins, uploads it, and notes what that came to.</summary>
    private async Task TakeAsync(Taken taken, RecordingFiles files, CancellationToken cancellationToken)
    {
        taken.Begin(files);
        taken.Note(await UploadOneAsync(files, cancellationToken));
    }

    /// <summary>Uploads one recording; one Drive refuses on the way waits with Drive's words, and the pass goes on with the next.</summary>
    private async Task<RecordingUploadResult> UploadOneAsync(RecordingFiles files, CancellationToken cancellationToken)
    {
        try
        {
            return await upload.UploadAsync(files, PauseWhileRecordingAsync, cancellationToken);
        }
        catch (GoogleDriveException ex) when (ex.Problem == DriveProblem.Refused)
        {
            // Deliberately not rethrown: Drive refusing one recording, such as its folder, does not keep the others from going
            // up; it waits in the queue with Drive's words, which this says.
            logger.LogWarning("{Recording} waits to go up to Google Drive: {Problem}", files.BaseName, ex.Message);
            return new RecordingUploadResult(files, RecordingOutcome.Waiting, ex.Message);
        }
    }

    /// <summary>
    /// Waits, before a piece goes, while a class records, in this window or in the background, as its run file says: the
    /// pause is said once, and so is the going on. It looks every 30 seconds, and never at recording.lock.
    /// </summary>
    private async Task PauseWhileRecordingAsync(CancellationToken cancellationToken)
    {
        string? pausedFor = null;
        while (journal.List().FirstOrDefault(run => run.IsRecordingNow) is { } run)
        {
            if (pausedFor is null)
            {
                pausedFor = run.Record.Request.Name;
                logger.LogInformation("Google Drive: paused while \"{Name}\" records", pausedFor);
            }

            await Task.Delay(PausePoll, time, cancellationToken);
        }

        if (pausedFor is not null) logger.LogInformation("Google Drive: going on, now that \"{Name}\" has stopped recording", pausedFor);
    }

    /// <summary>True when <paramref name="manifest"/>'s next try is due now.</summary>
    private bool IsDue(UploadManifest manifest) => manifest.NextAttemptUtc is not { } next || next <= time.GetUtcNow();

    /// <summary>Ends a pass before anything went up, with <paramref name="problem"/> and what waits.</summary>
    private PassResult Stop(string problem, IReadOnlyList<RecordingFiles> recordings) => Stop(problem, [], recordings);

    /// <summary>Ends the pass with <paramref name="problem"/>, saying how many recordings wait and how they go up.</summary>
    private PassResult Stop(string problem, IReadOnlyList<RecordingUploadResult> results, IReadOnlyList<RecordingFiles> recordings)
    {
        var waiting = manifests.Waiting().Count;
        var message = waiting == 0
            ? problem
            : $"{problem} {waiting} {(waiting == 1 ? "recording waits" : "recordings wait")} to go up to Google Drive {How}.";
        logger.LogError("{Problem}", message);
        var left = recordings.Where(files => results.All(result => result.Files != files)).Select(files => new RecordingUploadResult(files, RecordingOutcome.Waiting, problem));
        return new PassResult([.. results, .. left], message);
    }

    /// <summary>The recordings' base names, in words: "A", "A and B", or "A, B and C".</summary>
    private static string Names(IReadOnlyList<RecordingFiles> recordings)
    {
        var names = recordings.Select(files => files.BaseName).ToList();
        return names.Count == 1 ? names[0] : $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}";
    }

    /// <summary>
    /// The recordings a pass has taken, by their manifest, and what each came to, the latest try of each in the order they
    /// ended: one done with, which is taken once more only when it was put in the queue again after its try began, as by
    /// another ClassScribe that found the lock held; or one being recorded or transcribed, which may be taken once more once
    /// it has stopped.
    /// </summary>
    private sealed class Taken(UploadManifests manifests)
    {
        private readonly HashSet<string> _doneWith = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _busy = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _queuedAtTry = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<RecordingUploadResult> _results = [];

        /// <summary>What each recording taken came to, its latest try only.</summary>
        public IReadOnlyList<RecordingUploadResult> Results => _results;

        /// <summary>True when <paramref name="files"/> has been taken in this pass already.</summary>
        public bool Has(RecordingFiles files) => _doneWith.Contains(Key(files)) || _busy.Contains(Key(files));

        /// <summary>Notes, as a try of <paramref name="files"/> begins, how many times it has been put in the queue so far.</summary>
        public void Begin(RecordingFiles files) => _queuedAtTry[Key(files)] = TimesQueued(files);

        /// <summary>
        /// True when <paramref name="files"/> may be taken now: it has not been; it was done with, and has been put in the queue
        /// again since its try began; or it was busy then and <paramref name="upload"/> finds it busy no more.
        /// </summary>
        public bool MayTake(RecordingFiles files, RecordingUpload upload)
        {
            var key = Key(files);
            if (_doneWith.Contains(key)) return TimesQueued(files) > _queuedAtTry.GetValueOrDefault(key);

            return !_busy.Contains(key) || upload.Busy(files) is null;
        }

        /// <summary>Notes what a try came to: busy the first time, the recording may be taken once more; otherwise it is done with.</summary>
        public void Note(RecordingUploadResult result)
        {
            var key = Key(result.Files);
            _results.RemoveAll(earlier => string.Equals(Key(earlier.Files), key, StringComparison.OrdinalIgnoreCase));
            _results.Add(result);
            if (result.Outcome == RecordingOutcome.InProgress && _busy.Add(key))
            {
                _doneWith.Remove(key);
                return;
            }

            _busy.Remove(key);
            _doneWith.Add(key);
        }

        /// <summary>How many times <paramref name="files"/> has been put in the queue, as its manifest says; none without one.</summary>
        private int TimesQueued(RecordingFiles files) => manifests.Read(files)?.TimesQueued ?? 0;

        /// <summary>The recording's manifest file, which names it whatever the letter case of its folder.</summary>
        private string Key(RecordingFiles files) => manifests.PathOf(files);
    }
}
