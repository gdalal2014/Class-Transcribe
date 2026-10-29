using System.Net;
using ClassScribe.Drive;
using ClassScribe.Recorder;
using ClassScribe.Storage;

namespace ClassScribe.Tests;

/// <summary>
/// Google Drive's part in a run and in a transcript made later, against the in-memory Google: the recording goes into the
/// queue as its run starts, once Drive is set up and the uploads after each class are on (Drive:Enabled); after a
/// transcript, after record-only and after a transcript that failed, it goes up, then the rest of the queue that is due;
/// not after a cancel, nor when nothing was recorded, nor when the run ended without an outcome, as when Windows ended the
/// session, and it waits in the queue. Before Drive is set up nothing is queued or said. Nothing of Drive ever reaches the
/// run: a failure, a cancel or a queue that cannot be written is said and passed over. A transcript made later sends its
/// new files only.
/// </summary>
[TestFixture]
public class DriveAutoUploadTests
{
    private static readonly DateTimeOffset MantiqStart = new(2026, 10, 17, 14, 4, 42, TimeSpan.Zero);
    private GoogleWorld _world = null!;
    private RecordingFiles _mantiq = null!;
    private RecordingFiles _kalam = null!;
    private FakeUserSignals _signals = null!;

    /// <summary>A signed-in world, today's class in its course and date folder, and an older one of the old flat layout.</summary>
    [SetUp]
    public void SetUp()
    {
        _world = new GoogleWorld();
        _world.SignedIn();
        _mantiq = _world.WriteRecording(@"Mantiq\2026-10-17", "Mantiq", MantiqStart, extras: [".md", ".json", ".log"]);
        _kalam = _world.WriteRecording("Kalam", "Kalam", new DateTimeOffset(2026, 10, 15, 23, 0, 2, TimeSpan.Zero), 2000, ".md");
        _signals = new FakeUserSignals();
        _signals.RequestStop();
    }

    /// <summary>Deletes the world's folders.</summary>
    [TearDown]
    public void TearDown() => _world.Dispose();

    /// <summary>How a run of <paramref name="kind"/> ended, as the record command writes it.</summary>
    private RunOutcome Outcome(OutcomeKind kind) => new(kind, kind is OutcomeKind.Transcribed or OutcomeKind.Recorded ? 0 : 1, $"{kind}.", null, _mantiq.Folder, null);

    /// <summary>Runs the upload after a run that recorded Mantiq and ended as <paramref name="kind"/>, moving the clock while it waits.</summary>
    private async Task AfterRunAsync(OutcomeKind? kind, DriveAutoUpload? auto = null)
    {
        auto ??= _world.AutoUpload(_signals);
        auto.Queue(_mantiq);
        if (kind is { } ended) auto.Ended(Outcome(ended));
        var after = auto.AfterRunAsync();
        await _world.Time.AdvanceUntilAsync(() => after.IsCompleted, TimeSpan.FromSeconds(1), timeoutSeconds: 30);
        await after;
    }

    /// <summary>The names of the files whose upload was started, in order.</summary>
    private List<string> Started() =>
        [.. _world.Google.Requests.Where(request => request.IsUploadStart).Select(request => JsonText.Object(request.Body)["name"]!.GetValue<string>())];

    /// <summary>The recording goes into the queue as its run starts, and nothing is asked of Google then.</summary>
    [Test]
    public void The_recording_goes_into_the_queue_as_its_run_starts()
    {
        _world.AutoUpload(_signals).Queue(_mantiq);

        Assert.That(_world.Manifests().Waiting().Select(manifest => manifest.BaseName), Is.EqualTo(new[] { _mantiq.BaseName }));
        Assert.That(_world.Google.Requests, Is.Empty);
    }

    /// <summary>Signed out, the recording still goes into the queue as its run starts, for the upload after the sign-in.</summary>
    [Test]
    public void Signed_out_the_recording_still_goes_into_the_queue()
    {
        _world.Store.Stored = FakeCredentialStore.With(null).Stored;

        _world.AutoUpload(_signals).Queue(_mantiq);

        Assert.That(_world.Manifests().Waiting().Select(manifest => manifest.BaseName), Is.EqualTo(new[] { _mantiq.BaseName }));
    }

    /// <summary>Before Drive is set up, nothing is queued, nothing goes up and nothing is said, however the run ends.</summary>
    [Test]
    public async Task Before_Drive_is_set_up_nothing_is_queued_or_said()
    {
        _world.Store.Stored = null;

        await AfterRunAsync(OutcomeKind.Transcribed);

        Assert.That(_world.Manifests().All(), Is.Empty);
        Assert.That(_world.Google.Requests, Is.Empty);
        Assert.That(_world.Logs.Problems, Is.Empty);
    }

    /// <summary>With the uploads after each class off (Drive:Enabled false), nothing is queued and nothing goes up after the run.</summary>
    [Test]
    public async Task With_the_uploads_after_each_class_off_nothing_is_queued_or_sent()
    {
        _world.Options.Set(new DriveOptions { Enabled = false });

        await AfterRunAsync(OutcomeKind.Transcribed);

        Assert.That(_world.Manifests().All(), Is.Empty);
        Assert.That(_world.Google.Requests, Is.Empty);
    }

    /// <summary>
    /// A queue that cannot be written as the run starts costs the class nothing: it is said, nothing is thrown, and the end
    /// of the run queues the recording again.
    /// </summary>
    [Test]
    public void A_queue_that_cannot_be_written_costs_the_class_nothing()
    {
        Directory.CreateDirectory(_world.Runtime.Folder.Root);
        File.WriteAllText(Path.Combine(_world.Runtime.Folder.Root, DriveFolders.DriveFolderName), "a file where the folder goes");

        Assert.That(() => _world.AutoUpload(_signals).Queue(_mantiq), Throws.Nothing);

        Assert.That(_world.Logs.Problems, Has.One.StartsWith($"Could not put {_mantiq.BaseName} in the queue for Google Drive (").And.EndsWith("); it goes in when the class ends"));
    }

    /// <summary>
    /// After a transcript the recording goes up, then the rest of the queue that is due: a class that could not go up
    /// yesterday goes up after today's.
    /// </summary>
    [Test]
    public async Task After_a_transcript_the_recording_goes_up_then_the_queue()
    {
        _world.Manifests().Queue(_kalam);

        await AfterRunAsync(OutcomeKind.Transcribed);

        var b = _mantiq.BaseName;
        Assert.That(Started(), Is.EqualTo(new[] { $"{b}.md", $"{b}.json", $"{b}.wav", $"{b}.log", $"{_kalam.BaseName}.md", $"{_kalam.BaseName}.wav" }));
        Assert.That(_world.Manifests().Waiting(), Is.Empty);
        Assert.That(_world.Logs.Problems, Is.Empty);
    }

    /// <summary>After record-only, and after a transcript that failed, the audio goes up all the same.</summary>
    [TestCase(OutcomeKind.Recorded)]
    [TestCase(OutcomeKind.TranscriptFailed)]
    public async Task After_record_only_or_a_failed_transcript_the_recording_goes_up(OutcomeKind kind)
    {
        await AfterRunAsync(kind);

        Assert.That(Started(), Has.Member($"{_mantiq.BaseName}.wav"));
        Assert.That(_world.Manifests().Read(_mantiq)!.State, Is.EqualTo(UploadState.Done));
    }

    /// <summary>
    /// After a cancel, a run that recorded nothing or a recorder that failed, nothing goes up: the recording waits in the
    /// queue, for the next upload.
    /// </summary>
    [TestCase(OutcomeKind.Cancelled)]
    [TestCase(OutcomeKind.NothingRecorded)]
    [TestCase(OutcomeKind.RecorderFailed)]
    public async Task Not_after_a_cancel_nothing_recorded_or_a_failure(OutcomeKind kind)
    {
        await AfterRunAsync(kind);

        Assert.That(_world.Google.Requests, Is.Empty);
        Assert.That(_world.Manifests().Waiting().Select(manifest => manifest.BaseName), Is.EqualTo(new[] { _mantiq.BaseName }));
    }

    /// <summary>A run that ended without an outcome, as when Windows ended the session, sends nothing: the recording waits.</summary>
    [Test]
    public async Task Not_after_a_run_without_an_outcome()
    {
        await AfterRunAsync(kind: null);

        Assert.That(_world.Google.Requests, Is.Empty);
        Assert.That(_world.Manifests().Waiting().Select(manifest => manifest.BaseName), Is.EqualTo(new[] { _mantiq.BaseName }));
    }

    /// <summary>Signed out, the class waits after it ends, and that is said with how it goes up: after a sign-in, after the next class or with 9 > U.</summary>
    [Test]
    public async Task Signed_out_the_class_waits_and_that_is_said()
    {
        _world.Store.Stored = FakeCredentialStore.With(null).Stored;

        await AfterRunAsync(OutcomeKind.Transcribed);

        Assert.That(_world.Google.Requests, Is.Empty);
        Assert.That(_world.Logs.Problems, Is.EqualTo(new[]
        {
            $"{IGoogleAccount.NotSignedIn} 1 recording waits to go up to Google Drive after the next class, or with menu choice 9 > U, or ClassScribe drive upload."
        }));
    }

    /// <summary>A failure nobody expected on the way to Drive is said and passed over: the run that called goes on as it was, and the recording waits.</summary>
    [Test]
    public async Task A_failure_of_Drive_costs_the_run_nothing()
    {
        _world.Google.FailWhen(_ => true, new InvalidOperationException("The fake network broke."), times: 100);

        Assert.That(async () => await AfterRunAsync(OutcomeKind.Transcribed), Throws.Nothing);

        Assert.That(_world.Logs.Problems, Has.One.StartsWith("The upload to Google Drive stopped because of an error (").And.Contain("The fake network broke."));
        Assert.That(_world.Manifests().Waiting().Select(manifest => manifest.BaseName), Is.EqualTo(new[] { _mantiq.BaseName }));
    }

    /// <summary>A Ctrl+C during the upload after the class stops it without reaching the run: the recording waits, to go on where it stopped.</summary>
    [Test]
    public async Task A_cancel_of_the_upload_after_the_class_is_not_thrown()
    {
        var pieces = 0;
        _world.Google.FailWhen(request => request.IsPiece && ++pieces == 2 && Cancel(), HttpStatusCode.ServiceUnavailable, "");

        Assert.That(async () => await AfterRunAsync(OutcomeKind.Transcribed), Throws.Nothing);

        Assert.That(_world.Manifests().Read(_mantiq)!.State, Is.EqualTo(UploadState.Waiting));
        Assert.That(_world.Logs.Problems, Has.One.StartsWith("The upload to Google Drive was stopped. 1 recording waits"));
    }

    /// <summary>
    /// A transcript made later sends its new files only: the class is on Drive already, and a second transcript and answer
    /// go up beside it; the audio is not sent again.
    /// </summary>
    [Test]
    public async Task A_transcript_made_later_sends_its_new_files_only()
    {
        await AfterRunAsync(OutcomeKind.Recorded);
        var before = Started().Count;
        File.WriteAllText(_mantiq.Transcript(2), "# Mantiq, again\n");
        File.WriteAllText(_mantiq.Json(2), "{}");

        var upload = _world.AutoUpload(_signals).UploadAsync(_mantiq);
        await _world.Time.AdvanceUntilAsync(() => upload.IsCompleted, TimeSpan.FromSeconds(1), timeoutSeconds: 30);
        await upload;

        Assert.That(Started().Skip(before), Is.EqualTo(new[] { $"{_mantiq.BaseName} (2).md", $"{_mantiq.BaseName} (2).json" }));
        Assert.That(_world.Manifests().Read(_mantiq)!.State, Is.EqualTo(UploadState.Done));
    }

    /// <summary>
    /// A transcript laid out again for a class whose audio was deleted from the laptop after it went up sends the new
    /// transcript alone: the audio is safe on Drive, not missing, and the class is done again, not waiting.
    /// </summary>
    [Test]
    public async Task A_rebuild_of_a_class_whose_audio_was_freed_sends_the_new_transcript_alone()
    {
        await AfterRunAsync(OutcomeKind.Transcribed);
        var before = Started().Count;
        File.Delete(_mantiq.Segment(1));
        File.WriteAllText(_mantiq.Transcript(2), "# Mantiq, laid out again\n");

        var upload = _world.AutoUpload(_signals).UploadAsync(_mantiq);
        await _world.Time.AdvanceUntilAsync(() => upload.IsCompleted, TimeSpan.FromSeconds(1), timeoutSeconds: 30);
        await upload;

        Assert.That(Started().Skip(before), Is.EqualTo(new[] { $"{_mantiq.BaseName} (2).md" }));
        Assert.That(_world.Manifests().Read(_mantiq)!.State, Is.EqualTo(UploadState.Done));
        Assert.That(_world.Manifests().Waiting(), Is.Empty);
        Assert.That(_world.Logs.Problems, Is.Empty);
    }

    /// <summary>Cancels the upload, as a Ctrl+C after the stop does, and says false, so that the fault it rides on never fires.</summary>
    private bool Cancel()
    {
        _signals.Cancel();
        return false;
    }
}
