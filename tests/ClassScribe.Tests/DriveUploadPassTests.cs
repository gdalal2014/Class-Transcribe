using System.Net;
using ClassScribe.Desktop;
using ClassScribe.Drive;
using ClassScribe.Recorder;
using ClassScribe.Storage;

namespace ClassScribe.Tests;

/// <summary>
/// One pass of uploads against the in-memory Google: the recordings asked for go into the queue first (once a Google client
/// is kept; before the first sign-in nothing is queued), then up, then the rest of the queue that is due, or all of it when
/// asked; one pass at a time under the upload lock; the laptop kept awake meanwhile; paused between pieces while a class
/// records; ended by Google out of reach or a sign-in that has gone, with what waits said; a cancel leaves everything to go
/// on where it stopped.
/// </summary>
[TestFixture]
public class DriveUploadPassTests
{
    private static readonly DateTimeOffset MantiqStart = new(2026, 10, 17, 14, 4, 42, TimeSpan.Zero);
    private GoogleWorld _world = null!;
    private RecordingFiles _mantiq = null!;
    private RecordingFiles _kalam = null!;

    /// <summary>A signed-in world, today's class in its course and date folder, and an older one of the old flat layout.</summary>
    [SetUp]
    public void SetUp()
    {
        _world = new GoogleWorld();
        _world.SignedIn();
        _mantiq = _world.WriteRecording(@"Mantiq\2026-10-17", "Mantiq", MantiqStart, extras: [".md", ".log"]);
        _kalam = _world.WriteRecording("Kalam", "Kalam", new DateTimeOffset(2026, 10, 15, 23, 0, 2, TimeSpan.Zero), 2000, ".md");
    }

    /// <summary>Deletes the world's folders.</summary>
    [TearDown]
    public void TearDown() => _world.Dispose();

    /// <summary>Runs a pass over <paramref name="recordings"/>, moving the clock while it waits.</summary>
    private async Task<PassResult> RunAsync(IReadOnlyList<RecordingFiles> recordings, bool everythingWaiting = false, CancellationToken cancellationToken = default)
    {
        var pass = _world.Pass().RunAsync(recordings, everythingWaiting, cancellationToken);
        await _world.Time.AdvanceUntilAsync(() => pass.IsCompleted, TimeSpan.FromSeconds(1), timeoutSeconds: 30);
        return await pass;
    }

    /// <summary>The names of the files whose upload was started, in order.</summary>
    private List<string> Started() =>
        [.. _world.Google.Requests.Where(request => request.IsUploadStart).Select(request => JsonText.Object(request.Body)["name"]!.GetValue<string>())];

    /// <summary>Before the first sign-in, nothing is queued and nothing is asked of Google; the pass says how to set Drive up.</summary>
    [Test]
    public async Task Before_Drive_is_set_up_nothing_is_queued()
    {
        _world.Store.Stored = null;

        var result = await RunAsync([_mantiq]);

        Assert.That(result.Stopped, Is.EqualTo(IGoogleAccount.NotSetUp));
        Assert.That(_world.Manifests().All(), Is.Empty);
        Assert.That(_world.Google.Requests, Is.Empty);
    }

    /// <summary>Signed out, the recordings wait in the queue, and the pass says how to sign in and how they go up.</summary>
    [Test]
    public async Task Signed_out_the_recordings_wait()
    {
        _world.Store.Stored = FakeCredentialStore.With(null).Stored;

        var result = await RunAsync([_mantiq]);

        Assert.That(result.Stopped, Is.EqualTo($"{IGoogleAccount.NotSignedIn} 1 recording waits to go up to Google Drive after the next class, or with menu choice 9 > U, or ClassScribe drive upload."));
        Assert.That(result.AllDone, Is.False);
        Assert.That(_world.Manifests().Waiting().Single().BaseName, Is.EqualTo(_mantiq.BaseName));
        Assert.That(_world.Google.Requests, Is.Empty);
        Assert.That(_world.Logs.Problems, Has.Member(result.Stopped));
    }

    /// <summary>
    /// The recordings asked for go up first, then the rest of the queue: a class that could not go up yesterday goes up after
    /// today's, each into its own folder.
    /// </summary>
    [Test]
    public async Task The_recordings_asked_for_go_first_then_the_queue()
    {
        _world.Manifests().Queue(_kalam);

        var result = await RunAsync([_mantiq]);

        Assert.That(result.AllDone, Is.True);
        Assert.That(result.Recordings.Select(recording => recording.Files.BaseName), Is.EqualTo(new[] { _mantiq.BaseName, _kalam.BaseName }));
        Assert.That(Started(), Is.EqualTo(new[] { $"{_mantiq.BaseName}.md", $"{_mantiq.BaseName}.wav", $"{_mantiq.BaseName}.log", $"{_kalam.BaseName}.md", $"{_kalam.BaseName}.wav" }));
        Assert.That(_world.Google.FolderAt("Kalam"), Is.Not.Null);
        Assert.That(_world.Manifests().Waiting(), Is.Empty);
    }

    /// <summary>A recording whose next try is not due yet is left in the queue, unless the whole queue is asked for.</summary>
    [Test]
    public async Task A_recording_not_due_waits_unless_everything_is_asked_for()
    {
        _world.Manifests().Write(_world.Manifests().Queue(_kalam) with { NextAttemptUtc = TestRuns.Start.AddDays(1) });

        var asked = await RunAsync([_mantiq]);
        Assert.That(asked.Recordings.Select(recording => recording.Files.BaseName), Is.EqualTo(new[] { _mantiq.BaseName }));

        var everything = await RunAsync([], everythingWaiting: true);
        Assert.That(everything.Recordings.Select(recording => recording.Files.BaseName), Is.EqualTo(new[] { _kalam.BaseName }));
        Assert.That(everything.AllDone, Is.True);
    }

    /// <summary>With nothing asked for and nothing waiting, the pass says so, and asks Google nothing.</summary>
    [Test]
    public async Task Nothing_waiting_is_said()
    {
        var result = await RunAsync([], everythingWaiting: true);

        Assert.That((result.AllDone, result.Recordings.Count), Is.EqualTo((true, 0)));
        Assert.That(_world.Lines, Has.Member("Nothing waits to go up to Google Drive."));
        Assert.That(_world.Google.Requests, Is.Empty);
    }

    /// <summary>
    /// While another ClassScribe holds the upload lock, nothing goes up: the recording waits in the queue, which the holder
    /// takes before it lets go, and that is said. Once the lock is free, a pass takes it, and lets it go at the end.
    /// </summary>
    [Test]
    public async Task One_pass_at_a_time()
    {
        PassResult result;
        using (_world.Lock().TryTake())
        {
            result = await RunAsync([_mantiq]);
        }

        Assert.That(result.Stopped, Is.EqualTo($"Another ClassScribe is uploading to Google Drive; {_mantiq.BaseName} waits and goes up after it."));
        Assert.That(_world.Google.Requests, Is.Empty);
        Assert.That(_world.Manifests().Waiting().Single().BaseName, Is.EqualTo(_mantiq.BaseName));

        Assert.That((await RunAsync([])).AllDone, Is.True);
        Assert.That(_world.Manifests().Waiting(), Is.Empty);
        using var free = _world.Lock().TryTake();
        Assert.That(free, Is.Not.Null, "the pass let the lock go");
    }

    /// <summary>
    /// A recording put in the queue again while another ClassScribe uploads it, as when a transcript made meanwhile adds a
    /// (2).md and that ClassScribe finds the lock held, is taken once more by the holder before it lets go: the new file goes
    /// up, and only then is the recording done. It is never marked done with the new file left behind.
    /// </summary>
    [Test]
    public async Task A_recording_queued_again_while_it_goes_up_is_taken_once_more()
    {
        var release = new TaskCompletionSource();
        _world.Google.Hold("/upload/drive/v3/files", release.Task);
        var holder = _world.Pass().RunAsync([_mantiq], everythingWaiting: false, CancellationToken.None);
        await Wait.UntilAsync(() => _world.Google.Requests.Any(request => request.IsUploadStart));
        File.WriteAllText(_mantiq.Transcript(2), "# A transcript laid out again\n");

        var second = await _world.Pass().RunAsync([_mantiq], everythingWaiting: false, CancellationToken.None);
        Assert.That(second.Stopped, Is.EqualTo($"Another ClassScribe is uploading to Google Drive; {_mantiq.BaseName} waits and goes up after it."));

        release.SetResult();
        var result = await holder.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.That(result.AllDone, Is.True);
        Assert.That(result.Recordings.Select(recording => (recording.Files.BaseName, recording.Outcome)), Is.EqualTo(new[] { (_mantiq.BaseName, RecordingOutcome.Done) }));
        Assert.That(_world.Google.FilesIn(_world.Google.FolderAt("Mantiq/2026-10-17")!.Id).Select(file => file.Name), Has.Member(Path.GetFileName(_mantiq.Transcript(2))));
        Assert.That(_world.Manifests().Read(_mantiq)!.State, Is.EqualTo(UploadState.Done));
        Assert.That(_world.Manifests().Waiting(), Is.Empty);
    }

    /// <summary>
    /// A recording done and unchanged since, its audio freed, asked for again while signed out or while another ClassScribe
    /// holds the lock, is not put back in the queue: it stays done, and nothing is said to wait. With the lock held, it is
    /// said to be on Drive already, and counts as done.
    /// </summary>
    [TestCase("signed out")]
    [TestCase("lock held")]
    public async Task A_recording_done_and_unchanged_stays_done_when_asked_for_again(string why)
    {
        Assert.That((await RunAsync([_mantiq])).AllDone, Is.True);
        Assert.That((await _world.Freer().FreeAsync(_mantiq, CancellationToken.None)).Deleted, Has.Count.EqualTo(1));

        PassResult result;
        if (why == "signed out")
        {
            _world.Store.Stored = FakeCredentialStore.With(null).Stored;
            result = await RunAsync([_mantiq]);
            Assert.That(result.Stopped, Is.EqualTo(IGoogleAccount.NotSignedIn));
        }
        else
        {
            using (_world.Lock().TryTake()) result = await RunAsync([_mantiq]);
            Assert.That(result.AllDone, Is.True);
            Assert.That(_world.Lines, Has.Member($"Another ClassScribe is uploading to Google Drive; {_mantiq.BaseName} is on Drive already, unchanged since it went up"));
        }

        Assert.That(_world.Manifests().Read(_mantiq)!.State, Is.EqualTo(UploadState.Done));
        Assert.That(_world.Manifests().Waiting(), Is.Empty);
        Assert.That(_world.QueueReport().Waiting(), Is.Null);
    }

    /// <summary>The laptop is kept awake, for an upload, from the first request to the last line, and let go once at the end.</summary>
    [Test]
    public async Task The_laptop_is_kept_awake_while_the_pass_runs()
    {
        var uploadedAtRelease = -1;
        _world.Power.OnRelease = () => uploadedAtRelease = _world.Google.Uploaded.Count;

        await RunAsync([_mantiq]);

        var hold = _world.Power.Holds.Single();
        Assert.That((hold.Reason, hold.Releases), Is.EqualTo((StayAwake.UploadingReason, 1)));
        Assert.That(hold.Needs(), Is.EqualTo(PowerNeeds.System | PowerNeeds.Execution));
        Assert.That(uploadedAtRelease, Is.EqualTo(3));
    }

    /// <summary>
    /// Google out of reach, after asking again for about four minutes on the clock, ends the pass: nothing more is tried,
    /// every recording waits, and the last line says how many and how they go up.
    /// </summary>
    [Test]
    public async Task Google_out_of_reach_ends_the_pass_and_everything_waits()
    {
        _world.Manifests().Queue(_kalam);
        _world.Google.Unreachable = true;

        var result = await RunAsync([_mantiq]);

        Assert.That(result.Stopped, Does.StartWith("Google could not be reached to renew ClassScribe's sign-in (No such host is known."));
        Assert.That(result.Stopped, Does.EndWith(" 2 recordings wait to go up to Google Drive after the next class, or with menu choice 9 > U, or ClassScribe drive upload."));
        Assert.That(result.Recordings.Select(recording => (recording.Files.BaseName, recording.Outcome)), Is.EqualTo(new[] { (_mantiq.BaseName, RecordingOutcome.Waiting) }));
        Assert.That(_world.Manifests().Read(_mantiq)!.LastError, Does.StartWith("Google could not be reached"));
        Assert.That(_world.Power.Holds.Single().Released, Is.True);
    }

    /// <summary>A sign-in Google has ended ends the pass, and says to sign in again; everything waits.</summary>
    [Test]
    public async Task A_sign_in_that_has_ended_ends_the_pass()
    {
        _world.Google.RevokeByHand(_world.Store.Stored!.RefreshToken!);

        var result = await RunAsync([_mantiq]);

        Assert.That(result.Stopped, Is.EqualTo($"{GoogleTokens.SignInEnded} 1 recording waits to go up to Google Drive after the next class, or with menu choice 9 > U, or ClassScribe drive upload."));
        Assert.That(_world.Store.Stored!.IsSignedIn, Is.False);
    }

    /// <summary>A recording Drive refuses, as its folder, waits with Drive's words; the pass goes on with the next.</summary>
    [Test]
    public async Task A_refused_recording_waits_and_the_pass_goes_on()
    {
        _world.Google.FailWhen(request => request.Method == HttpMethod.Post && request.Path == "/drive/v3/files" && request.Body.Contains("\"Mantiq\"", StringComparison.Ordinal),
            HttpStatusCode.BadRequest, FakeGoogle.DriveError(400, "invalid", "Invalid Value"));

        var result = await RunAsync([_mantiq, _kalam]);

        Assert.That(result.Recordings.Select(recording => recording.Outcome), Is.EqualTo(new[] { RecordingOutcome.Waiting, RecordingOutcome.Done }));
        Assert.That(result.Recordings[0].Problem, Is.EqualTo("Google Drive refused to make a folder (HTTP 400, invalid: Invalid Value)."));
        Assert.That(result.AllDone, Is.False);
    }

    /// <summary>
    /// While a class records, in a recorder or a window, the pass waits before each piece, says so once, sends nothing, and
    /// looks again every 30 seconds; once the class has stopped it goes on, and says so.
    /// </summary>
    [Test]
    public async Task The_pass_waits_between_pieces_while_a_class_records()
    {
        _world.Runtime.Processes.Start(TestRuns.ProcessId, TestRuns.ProcessStart);
        _world.Runtime.Journal.Begin(TestRuns.Record(TestRuns.Id(), RunState.Recording, name: "Kalam"));

        var pass = _world.Pass().RunAsync([_mantiq], everythingWaiting: false, CancellationToken.None);
        await Wait.UntilAsync(() => _world.Lines.Contains("Google Drive: paused while \"Kalam\" records"));
        for (var poll = 0; poll < 2; poll++)
        {
            // Each look made, the pass waits for the next: its new wait is the sign that it has looked and found the class recording.
            var made = _world.Time.TimersMade;
            _world.Time.Advance(DriveUploadPass.PausePoll);
            await Wait.UntilAsync(() => _world.Time.TimersMade > made && _world.Time.UntilNextTimer == DriveUploadPass.PausePoll);
        }

        Assert.That(_world.Google.Requests.Where(request => request.IsPiece), Is.Empty);
        Assert.That(_world.Lines.Count(line => line.StartsWith("Google Drive: paused", StringComparison.Ordinal)), Is.EqualTo(1));
        Assert.That(File.Exists(_world.Runtime.Folder.LockFile), Is.False, "recording.lock is never touched");

        _world.Runtime.Processes.End(TestRuns.ProcessId);
        await _world.Time.AdvanceUntilAsync(() => pass.IsCompleted, TimeSpan.FromSeconds(1));

        Assert.That((await pass).AllDone, Is.True);
        Assert.That(_world.Lines, Has.Member("Google Drive: going on, now that \"Kalam\" has stopped recording"));
    }

    /// <summary>
    /// A cancel, as Esc or Ctrl+C, stops the pass and leaves the recording waiting, which is said, with the upload begun kept;
    /// the next pass goes on from where it stopped, in the same session, and the file comes out whole.
    /// </summary>
    [Test]
    public async Task A_cancel_leaves_everything_to_go_on_where_it_stopped()
    {
        using var cancel = new CancellationTokenSource();
        var pieces = 0;
        _world.Google.FailWhen(request => request.IsPiece && ++pieces == 3 && Cancel(cancel), HttpStatusCode.ServiceUnavailable, "");

        Assert.That(async () => await RunAsync([_mantiq], cancellationToken: cancel.Token), Throws.InstanceOf<OperationCanceledException>());

        var manifest = _world.Manifests().Read(_mantiq)!;
        Assert.That(manifest.State, Is.EqualTo(UploadState.Waiting));
        Assert.That(manifest.File($"{_mantiq.BaseName}.wav")!.Session!.Confirmed, Is.EqualTo(GoogleWorld.PieceSize));
        Assert.That(_world.Logs.Problems, Has.Member("The upload to Google Drive was stopped. 1 recording waits, and goes on from where it stopped after the next class, or with menu choice 9 > U, or ClassScribe drive upload"));

        var again = await RunAsync([]);

        Assert.That(again.AllDone, Is.True);
        Assert.That(_world.Google.Sessions.Values.Count(session => session.Name == $"{_mantiq.BaseName}.wav"), Is.EqualTo(1));
        Assert.That(_world.Google.Uploaded.Single(file => file.Name == $"{_mantiq.BaseName}.wav").Content, Is.EqualTo(File.ReadAllBytes(_mantiq.Segment(1))));
    }

    /// <summary>With the uploads after each class off (Drive:Enabled false), what waits goes up with menu choice 9 alone, which is said.</summary>
    [Test]
    public async Task With_the_uploads_after_each_class_off_what_waits_goes_up_with_menu_choice_9()
    {
        _world.Options.Set(new DriveOptions { Enabled = false });
        _world.Store.Stored = FakeCredentialStore.With(null).Stored;

        var result = await RunAsync([_mantiq]);

        Assert.That(result.Stopped, Is.EqualTo($"{IGoogleAccount.NotSignedIn} 1 recording waits to go up to Google Drive with menu choice 9 > U, or ClassScribe drive upload."));
    }

    /// <summary>
    /// A recording being transcribed when the pass first comes to it is passed over; once its run has ended, while the pass
    /// still sends another, the pass comes back to it before it lets go, and it goes up, said once in the result.
    /// </summary>
    [Test]
    public async Task A_recording_busy_when_first_looked_at_goes_up_once_it_has_stopped()
    {
        TranscribingNow(_mantiq);
        _world.Google.FailWhen(request => request.IsUploadStart && request.Body.Contains(_kalam.BaseName, StringComparison.Ordinal) && Ended(), HttpStatusCode.OK, "");

        var result = await RunAsync([_mantiq, _kalam]);

        Assert.That(result.Recordings.Select(recording => (recording.Files.BaseName, recording.Outcome)),
            Is.EqualTo(new[] { (_kalam.BaseName, RecordingOutcome.Done), (_mantiq.BaseName, RecordingOutcome.Done) }));
        Assert.That(result.AllDone, Is.True);
        Assert.That(_world.Lines, Has.Member($"{_mantiq.BaseName} is being transcribed now; it goes up once it has stopped"));
        Assert.That(_world.Manifests().Waiting(), Is.Empty);
    }

    /// <summary>
    /// A recording still being written when the pass is through, as by a recorder without a run file, waits for the next
    /// pass; the pass ends all the same. (A recording a live run transcribes holds the whole pass up instead, Gulzar's answer
    /// N13: see <see cref="LessonVideoDriveTests.The_pass_waits_while_a_class_is_transcribed"/>.)
    /// </summary>
    [Test]
    public async Task A_recording_still_busy_waits_and_the_pass_ends()
    {
        using var written = new FileStream(_mantiq.Segment(1), FileMode.Open, FileAccess.Write, FileShare.Read);

        var result = await RunAsync([_mantiq, _kalam]);

        Assert.That(result.Recordings.Select(recording => (recording.Files.BaseName, recording.Outcome)),
            Is.EqualTo(new[] { (_mantiq.BaseName, RecordingOutcome.InProgress), (_kalam.BaseName, RecordingOutcome.Done) }));
        Assert.That(_world.Manifests().Waiting().Select(manifest => manifest.BaseName), Is.EqualTo(new[] { _mantiq.BaseName }));
    }

    /// <summary>A run in a recorder that is transcribing <paramref name="files"/> now, whose process is alive until <see cref="Ended"/>.</summary>
    private void TranscribingNow(RecordingFiles files)
    {
        _world.Runtime.Processes.Start(TestRuns.ProcessId, TestRuns.ProcessStart);
        _world.Runtime.Journal.Begin(TestRuns.Record(TestRuns.Id(), RunState.Transcribing, name: files.Name) with { BaseName = files.BaseName, Folder = files.Folder });
    }

    /// <summary>Ends the process of the run <see cref="TranscribingNow"/> began, and says false, so that the fault it rides on never fires.</summary>
    private bool Ended()
    {
        _world.Runtime.Processes.End(TestRuns.ProcessId);
        return false;
    }

    /// <summary>Cancels <paramref name="cancel"/> and says true, for a fault that cancels as the request it picks is made.</summary>
    private static bool Cancel(CancellationTokenSource cancel)
    {
        cancel.Cancel();
        return true;
    }
}
