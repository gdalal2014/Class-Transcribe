using ClassScribe.Commands;
using ClassScribe.Drive;
using ClassScribe.Recorder;
using ClassScribe.Settings;
using ClassScribe.Storage;
using ClassScribe.Transcription;
using ClassScribe.YouTube;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClassScribe.Tests;

/// <summary>
/// The run file of each transcript a batch makes in its window (design 2.8): while it lasts, transcribe in another window
/// and Google Drive see it and refuse or wait; its heartbeat carries the upload's percent; its end gives the outcome and
/// marks it seen; and a window closed in the middle leaves it for the next start to report as an interrupted transcription.
/// The run files are real files in a test's own ClassScribe folder, and the processes are played.
/// </summary>
[TestFixture]
public class TranscriptionRunsTests
{
    private TestRuntime _runtime = null!;
    private TempFolder _recordings = null!;
    private RecordingFiles _files = null!;
    private TranscriptionProgress _progress = null!;

    /// <summary>A test's own ClassScribe folder with this process playing as running, and one video's recording.</summary>
    [SetUp]
    public void SetUp()
    {
        _runtime = new TestRuntime();
        var (id, started) = TranscriptionRuns.ThisProcess();
        _runtime.Processes.Start(id, started);
        _recordings = new TempFolder();
        _files = RecordingFiles.ForNew(Path.Combine(_recordings.Path, "Risala", "2026-10-18"), "Risala", new DateTimeOffset(2026, 10, 18, 14, 2, 11, TimeSpan.Zero));
        Directory.CreateDirectory(_files.Folder);
        TestAudio.WriteWav(_files.Segment(1), 10, new short[10]);
        _progress = new TranscriptionProgress();
    }

    /// <summary>Deletes the folders.</summary>
    [TearDown]
    public void TearDown()
    {
        _recordings.Dispose();
        _runtime.Dispose();
    }

    /// <summary>Begins the transcript's run.</summary>
    private TranscriptionRun Begin() =>
        new TranscriptionRuns(_runtime.Journal, _runtime.Time, NullLogger<TranscriptionRuns>.Instance).Begin("Risala", _recordings.Path, YouTubeTestRuns.Video(), _files, _progress);

    /// <summary>The run file says: this process, in this window, transcribing the video's recording, its request the video's.</summary>
    [Test]
    public void The_run_file_says_this_window_transcribes_the_videos_recording()
    {
        using var run = Begin();

        var view = _runtime.Journal.Find(run.RunId!)!;
        Assert.That(view.IsTranscribingNow, Is.True);
        Assert.That(view.IsVideo, Is.True);
        Assert.That((view.Record.Home, view.Record.ProcessId, view.Record.BaseName, view.Record.Folder),
            Is.EqualTo((RunHome.InWindow, Environment.ProcessId, _files.BaseName, _files.Folder)));
        Assert.That(view.Record.Request with { RunId = "" }, Is.EqualTo(view.Record.Request with { RunId = "", Video = YouTubeTestRuns.Video() }));
    }

    /// <summary>While it lasts, Google Drive's uploads pass the recording over, and transcribe in another window refuses it rather than bill it twice.</summary>
    [Test]
    public async Task While_it_lasts_Drive_waits_and_transcribe_refuses()
    {
        using var run = Begin();
        var services = new ServiceCollection().AddSingleton(_runtime.Journal).AddSingleton<IRunJournal>(_runtime.Journal).BuildServiceProvider();
        var log = new RecordingLogger<CommandRunner>();
        var runner = new CommandRunner(services.GetRequiredService<IServiceScopeFactory>(), services.GetRequiredService<IServiceProviderIsService>(), log);

        var exitCode = await runner.TranscribeAsync(new TranscribeRequest(_files.Segment(1), FromCommandLine: true));

        Assert.That(RecordingUpload.BusyIn(_runtime.Journal.List(), _files, new ClassFolders(TestOptions.Create(new RecordingOptions()), _runtime.Time, NullLogger<ClassFolders>.Instance)), Is.EqualTo("is being transcribed now"));
        Assert.That(exitCode, Is.EqualTo(1));
        Assert.That(log.Problems.Single(), Does.Contain("is being transcribed in another ClassScribe window"));
    }

    /// <summary>The heartbeat carries the upload's percent, as the list and the menu's header read it.</summary>
    [Test]
    public void The_heartbeat_carries_the_uploads_percent()
    {
        using var run = Begin();

        _progress.Report(TranscriptionStep.Uploading, 40);

        Assert.That(_runtime.Journal.Find(run.RunId!)!.Record.UploadPercent, Is.EqualTo(40));
    }

    /// <summary>The end gives the outcome and marks the run seen: the batch's window has shown it, so the next start reports nothing.</summary>
    [Test]
    public void The_end_gives_the_outcome_and_marks_it_seen()
    {
        var run = Begin();
        var id = run.RunId!;

        run.End(new RunOutcome(OutcomeKind.Transcribed, 0, "Transcript saved", _files.Transcript(), _files.Folder, RetryCommand: null));
        run.Dispose();

        var view = _runtime.Journal.Find(id)!;
        Assert.That((view.Record.State, view.Record.Outcome!.Kind, view.Record.Seen), Is.EqualTo((RunState.Finished, OutcomeKind.Transcribed, true)));
        Assert.That(view.NeedsReport, Is.False);
    }

    /// <summary>A window closed in the middle leaves the run transcribing, which the next start reports as an interrupted transcription.</summary>
    [Test]
    public void A_window_closed_in_the_middle_is_reported_as_an_interrupted_transcription()
    {
        var run = Begin();
        var id = run.RunId!;
        run.Dispose();

        _runtime.Processes.End(Environment.ProcessId);

        Assert.That(_runtime.Journal.Find(id)!.TranscriptionInterrupted, Is.True);
    }
}
