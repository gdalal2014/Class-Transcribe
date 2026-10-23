using System.Net;
using System.Text;
using ClassScribe.Commands;
using ClassScribe.Drive;
using ClassScribe.Link;
using ClassScribe.Recorder;
using ClassScribe.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClassScribe.Tests;

// Google Drive's part in the recorder (design section 3.1, step 8), on the same real host as the rest of the recorder's
// tests, with the in-memory Google as every HTTP client's handler and its sign-in in a store in memory: after a transcript,
// after record-only and after a transcript that failed, the recorder uploads the class, last of all, once the end has gone
// to the windows; not after a cancel; before Drive is set up it queues and sends nothing; and nothing of Drive changes the
// run's outcome or exit code. Nothing leaves the process.
public partial class RecorderHostTests
{
    /// <summary>The in-memory Google, when a test has signed the recorder in to it; null for none, as before Drive is set up.</summary>
    private FakeGoogle? _google;

    /// <summary>The store of the Google sign-in, in memory, with <see cref="_google"/>.</summary>
    private FakeCredentialStore? _store;

    /// <summary>Puts the in-memory Google, and the sign-in to it, in place of the real ones, when a test has signed in.</summary>
    private void OnGoogleDrive(IServiceCollection services)
    {
        if (_google is not { } google) return;

        services.Replace(ServiceDescriptor.Singleton<IGoogleCredentialStore>(_store!));
        services.ConfigureAll<HttpClientFactoryOptions>(options => options.HttpMessageHandlerBuilderActions.Add(builder => builder.PrimaryHandler = google));
    }

    /// <summary>Signs the recorder in to the in-memory Google, as a sign-in stored earlier would, and returns it.</summary>
    private FakeGoogle SignedInToDrive()
    {
        _google = new FakeGoogle();
        _store = FakeCredentialStore.With(_google.IssueRefreshToken());
        return _google;
    }

    /// <summary>The manifests of the uploads, in the test's ClassScribe folder.</summary>
    private UploadManifests Manifests() => new(_runtime.Folder, _time, NullLogger<UploadManifests>.Instance);

    /// <summary>The names of the files in the class's folder on Drive, ClassScribe > Kalam > its date folder.</summary>
    private IEnumerable<string> OnDrive(RecordingFiles files) =>
        _google!.FolderAt($"Kalam/{Path.GetFileName(files.Folder)}") is { } folder ? _google.FilesIn(folder.Id).Select(file => file.Name) : [];

    /// <summary>
    /// Records half a second of tone for <paramref name="request"/>, stops it from another window, and returns the recording
    /// and the recorder's exit code; no window is attached.
    /// </summary>
    private async Task<(FakeRecording Recording, int ExitCode)> RecordAndStopAsync(RecordRequest request, string? apiKey = "dummy-key-for-a-local-test")
    {
        var run = Host().RunAsync(request.RunId, apiKey);
        var recording = await StartedAsync(run);
        TestAudio.WriteWav(recording.Files!.Segment(1), 16000, TestAudio.Tone(16000, seconds: 0.5, frequency: 440, amplitude: 0.5));
        StopFromAnotherWindow(request.RunId);
        return (recording, await EndedAsync(run));
    }

    /// <summary>
    /// After the transcript, once the end has gone to the windows and the owner has left, the recorder uploads the class to
    /// ClassScribe > Kalam > its date folder: the transcript, the answer, the audio and the log, the upload copy never. The
    /// last line, where the class is on Drive, goes into the recording's log before the log goes up, so Drive's copy holds
    /// it. The run's outcome and exit code are those of the transcript.
    /// </summary>
    [Test]
    public async Task After_the_transcript_the_recorder_uploads_the_class_last_of_all()
    {
        var google = SignedInToDrive();
        var endSeen = new TaskCompletionSource();
        bool? endSeenAtTheFirstRequest = null;
        google.FailWhen(_ =>
        {
            endSeenAtTheFirstRequest ??= endSeen.Task.IsCompleted;
            return false;
        }, HttpStatusCode.OK, "");
        var request = WriteRequest(transcribe: true);
        var run = Host().RunAsync(request.RunId, "dummy-key-for-a-local-test");
        var recording = await StartedAsync(run);
        TestAudio.WriteWav(recording.Files!.Segment(1), 16000, TestAudio.Tone(16000, seconds: 0.5, frequency: 440, amplitude: 0.5));
        var window = await AttachAsync(owner: true);

        await window.SendKeyAsync(LinkKeys.Q);
        await window.SendKeyAsync(LinkKeys.Q);
        var (_, end) = await window.UntilEndAsync();
        endSeen.SetResult();
        await CloseAsync(window);

        Assert.That(await EndedAsync(run), Is.EqualTo(0));
        Assert.That(end.Outcome.Kind, Is.EqualTo(OutcomeKind.Transcribed));
        Assert.That(RunFile(request.RunId).Outcome?.Kind, Is.EqualTo(OutcomeKind.Transcribed));
        Assert.That(endSeenAtTheFirstRequest, Is.True, "the window had the end, and had left, before Google was asked anything");
        var files = recording.Files;
        var b = files.BaseName;
        Assert.That(OnDrive(files), Is.EquivalentTo(new[] { $"{b}.md", $"{b}.json", $"{b}.glossary.json", $"{b}.wav", $"{b}.log" }));
        var lastLine = $"{b} is on Google Drive: My Drive > ClassScribe > Kalam > {Path.GetFileName(files.Folder)} (5 files,";
        var driveLog = Encoding.UTF8.GetString(google.Files.Single(file => file.Name == $"{b}.log").Content);
        Assert.That(driveLog, Does.Contain(lastLine), "the last line went up in the log");
        Assert.That(driveLog, Does.Contain("Transcript saved to"), "the log of the class went up whole");
        Assert.That(Manifests().Read(files)!.State, Is.EqualTo(UploadState.Done));
        Assert.That(google.Destructive, Is.Empty);
    }

    /// <summary>After record-only, once the files are closed, the recorder uploads the audio and the log; the outcome stays Recorded.</summary>
    [Test]
    public async Task After_record_only_the_recorder_uploads_the_class()
    {
        SignedInToDrive();
        var request = WriteRequest(transcribe: false);

        var (recording, exitCode) = await RecordAndStopAsync(request);

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(RunFile(request.RunId).Outcome?.Kind, Is.EqualTo(OutcomeKind.Recorded));
        var b = recording.Files!.BaseName;
        Assert.That(OnDrive(recording.Files), Is.EquivalentTo(new[] { $"{b}.glossary.json", $"{b}.wav", $"{b}.log" }));
        Assert.That(Manifests().Read(recording.Files)!.State, Is.EqualTo(UploadState.Done));
    }

    /// <summary>After a transcript that failed, here for want of a key, the audio goes up all the same; the outcome and exit code are the failure's.</summary>
    [Test]
    public async Task After_a_failed_transcript_the_audio_goes_up_all_the_same()
    {
        SignedInToDrive();
        var request = WriteRequest(transcribe: true);

        var (recording, exitCode) = await RecordAndStopAsync(request, apiKey: null);

        Assert.That(exitCode, Is.EqualTo(1));
        Assert.That(RunFile(request.RunId).Outcome?.Kind, Is.EqualTo(OutcomeKind.TranscriptFailed));
        Assert.That(OnDrive(recording.Files!), Has.Member($"{recording.Files!.BaseName}.wav"));
    }

    /// <summary>
    /// After a cancel nothing is sent to Google: the class waits in the queue, where its run put it as it started, and goes
    /// up with the next upload.
    /// </summary>
    [Test]
    public async Task After_a_cancel_nothing_goes_up_and_the_class_waits()
    {
        var google = SignedInToDrive();
        _client.Setup(Calls.AnyTranscription).ThrowsAsync(new OperationCanceledException());
        var request = WriteRequest(transcribe: true);

        var (recording, exitCode) = await RecordAndStopAsync(request);

        Assert.That(exitCode, Is.EqualTo(RecorderHost.CancelledExitCode));
        Assert.That(RunFile(request.RunId).Outcome?.Kind, Is.EqualTo(OutcomeKind.Cancelled));
        Assert.That(google.Requests, Is.Empty);
        Assert.That(Manifests().Waiting().Select(manifest => manifest.BaseName), Is.EqualTo(new[] { recording.Files!.BaseName }));
    }

    /// <summary>
    /// A failure on the way to Drive changes neither the run's outcome nor its exit code: it is said in the recorder's log,
    /// and the class waits in the queue.
    /// </summary>
    [Test]
    public async Task A_failure_of_Drive_changes_neither_the_outcome_nor_the_exit_code()
    {
        var google = SignedInToDrive();
        google.FailWhen(_ => true, new InvalidOperationException("The fake network broke."), times: 1000);
        var request = WriteRequest(transcribe: true);

        var (recording, exitCode) = await RecordAndStopAsync(request);

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(RunFile(request.RunId).Outcome?.Kind, Is.EqualTo(OutcomeKind.Transcribed));
        Assert.That(LoggedLines(), Has.Some.Contains("The upload to Google Drive stopped because of an error (The fake network broke.)"));
        Assert.That(Manifests().Waiting().Select(manifest => manifest.BaseName), Is.EqualTo(new[] { recording.Files!.BaseName }));
    }

    /// <summary>Before Drive is set up, the recorder puts nothing in the queue, sends nothing and says nothing of Google Drive.</summary>
    [Test]
    public async Task Before_Drive_is_set_up_the_recorder_queues_and_sends_nothing()
    {
        var request = WriteRequest(transcribe: true);

        var (_, exitCode) = await RecordAndStopAsync(request);

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(Directory.Exists(Path.Combine(_runtime.Folder.Root, DriveFolders.DriveFolderName)), Is.False, "no manifest, no lock");
        Assert.That(LoggedLines(), Has.None.Contains("Google Drive"));
    }
}
