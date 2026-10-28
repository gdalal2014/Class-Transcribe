using System.Net;
using ClassScribe.Recorder;
using ClassScribe.Settings;
using ClassScribe.Storage;
using ClassScribe.Transcription;
using ClassScribe.YouTube;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClassScribe.Tests;

/// <summary>
/// One transcript of a batch (design 2.8): made by the real workflow over a mocked service, written into the run journal
/// while it is made and marked seen at its end, its lines in the recording's log, what it cost read from the account, and
/// its new files handed to Google Drive's part. A failure ends its run as failed and is thrown on.
/// </summary>
[TestFixture]
public class VideoTranscriptRunTests
{
    private YouTubeWorld _world = null!;
    private Mock<ITranscriptionClient> _client = null!;
    private RecordingFiles _files = null!;

    /// <summary>A world with one downloaded video's recording, and the mocked service.</summary>
    [SetUp]
    public void SetUp()
    {
        _world = new YouTubeWorld();
        var (id, started) = TranscriptionRuns.ThisProcess();
        _world.Runtime.Processes.Start(id, started);
        _files = _world.ClassFolders.CreateRecording("Risala");
        TestAudio.WriteWav(_files.Segment(1), 16000, new short[1600]);
        VideoSourceFile.Write(_files.Source, VideoSourceFileTests.Downloaded());
        _client = Mocks.TranscriptionClient();
    }

    /// <summary>Deletes the world's folders.</summary>
    [TearDown]
    public void TearDown() => _world.Dispose();

    /// <summary>The transcript's run over the real workflow, the world's run files, log, account and Drive.</summary>
    private VideoTranscriptRun Run()
    {
        var usage = new UsageReport(_world.Account, _world.Time, _world.Log<UsageReport>());
        var progress = new TranscriptionProgress();
        var workflow = new TranscriptionWorkflow(Mocks.AudioPreparer().Object, _client.Object, TestGlossaries.Choice(_world.ElevenLabs),
            new TranscriptFormatter(TestOptions.Create(new TranscriptOptions())), _world.ClassFolders, new ClassMarks(NullLogger<ClassMarks>.Instance),
            _world.Recording, _world.ElevenLabs, progress, usage, _world.Log<TranscriptionWorkflow>());
        return new VideoTranscriptRun(workflow, new TranscriptionRuns(_world.Runtime.Journal, _world.Runtime.Time, _world.Log<TranscriptionRuns>()), progress, usage,
            _world.Drive, _world.RecorderLog, _world.Log<VideoTranscriptRun>());
    }

    /// <summary>The video to transcribe.</summary>
    private VideoToTranscribe Video() => new(_files, YouTubeTestRuns.Video(folder: _files.Folder), "Risala", _world.Recordings.Path, TimeSpan.FromSeconds(0.1));

    /// <summary>
    /// The transcript is saved, headed by the video's title; its run is over, transcribed, and seen; its lines are in the
    /// recording's log; the account is read before and after, and what it cost is handed back; its new files go to Drive.
    /// </summary>
    [Test]
    public async Task A_transcript_is_made_recorded_and_handed_on()
    {
        _world.Account.Answer(FakeElevenLabsAccount.Read(1_000), FakeElevenLabsAccount.Read(1_120));

        var transcribed = await Run().RunAsync(Video(), CancellationToken.None);

        Assert.That(File.ReadAllLines(transcribed.TranscriptPath)[0], Is.EqualTo("# The creed, part 1"));
        Assert.That(transcribed.Charged, Is.EqualTo(new UsageCharge(120, 98_880)));
        var run = _world.Runtime.Journal.List().Single();
        Assert.That((run.Record.State, run.Record.Outcome!.Kind, run.Record.Seen, run.IsVideo), Is.EqualTo((RunState.Finished, OutcomeKind.Transcribed, true, true)));
        Assert.That(_world.Drive.Calls, Is.EqualTo([$"Upload {_files.BaseName}"]));
        _world.RecorderLog.Flush(TimeSpan.FromSeconds(10));
        Assert.That(File.ReadAllText(_files.Log), Does.Contain("Transcript saved to").And.Contain("ElevenLabs charged 120 credits"));
    }

    /// <summary>A failure ends the run as failed, with the command to try again, and is thrown on; nothing goes to Drive.</summary>
    [Test]
    public void A_failure_ends_the_run_as_failed_and_is_thrown_on()
    {
        _client.Setup(Calls.AnyTranscription).ThrowsAsync(new TranscriptionException("ElevenLabs could not transcribe the file. (HTTP 500)", HttpStatusCode.InternalServerError));

        Assert.That(async () => await Run().RunAsync(Video(), CancellationToken.None), Throws.InstanceOf<TranscriptionException>());

        var run = _world.Runtime.Journal.List().Single();
        Assert.That((run.Record.Outcome!.Kind, run.Record.Seen), Is.EqualTo((OutcomeKind.TranscriptFailed, true)));
        Assert.That(run.Record.Outcome.RetryCommand, Does.Contain("transcribe"));
        Assert.That(_world.Drive.Calls, Is.Empty);
    }
}
