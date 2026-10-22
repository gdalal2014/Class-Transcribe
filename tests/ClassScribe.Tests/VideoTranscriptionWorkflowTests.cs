using ClassScribe.Settings;
using ClassScribe.Storage;
using ClassScribe.Transcription;
using ClassScribe.YouTube;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClassScribe.Tests;

/// <summary>
/// A YouTube video's recording through the workflow (design 2.9): its transcript is headed by the video's title and link,
/// in a transcript made with ElevenLabs and in a free rebuild alike, the rebuild keeping when it was downloaded once its
/// audio is gone; a source file that cannot be read never costs the paid transcript.
/// </summary>
[TestFixture]
public class VideoTranscriptionWorkflowTests
{
    /// <summary>When the video was downloaded: 14:02:11 UTC on 18 October 2026.</summary>
    private static readonly DateTimeOffset Downloaded = new(2026, 10, 18, 14, 2, 11, TimeSpan.Zero);

    private TempFolder _folder = null!;
    private RecordingFiles _files = null!;
    private Mock<ITranscriptionClient> _client = null!;
    private RecordingLogger<TranscriptionWorkflow> _log = null!;

    /// <summary>A recording of one downloaded video in a batch's date folder, with its source file, and the mocked service.</summary>
    [SetUp]
    public void SetUp()
    {
        _folder = new TempFolder();
        _files = RecordingFiles.ForNew(_folder.File(@"Recordings\Risala\2026-10-18"), "Risala", Downloaded);
        Directory.CreateDirectory(_files.Folder);
        TestAudio.WriteWav(_files.Segment(1), 16000, new short[1600]);
        VideoSourceFile.Write(_files.Source, VideoSourceFileTests.Downloaded());
        _client = Mocks.TranscriptionClient();
        _log = new RecordingLogger<TranscriptionWorkflow>();
    }

    /// <summary>Deletes the temporary folder.</summary>
    [TearDown]
    public void TearDown() => _folder.Dispose();

    /// <summary>The workflow around the mocked preparer and service, with positions off so that the transcript's text is short.</summary>
    private TranscriptionWorkflow Workflow()
    {
        var recording = TestOptions.Create(new RecordingOptions { OutputFolder = _folder.File("Recordings") });
        return new TranscriptionWorkflow(
            Mocks.AudioPreparer().Object,
            _client.Object,
            new KeytermLoader(NullLogger<KeytermLoader>.Instance),
            new TranscriptFormatter(TestOptions.Create(new TranscriptOptions { IncludeTimestamps = false })),
            new ClassFolders(recording, ZonedClock.Eastern(new ManualTimeProvider(Downloaded)), NullLogger<ClassFolders>.Instance),
            new ClassMarks(NullLogger<ClassMarks>.Instance),
            TestOptions.Create(new ElevenLabsOptions { ApiKey = "dummy-key-for-a-local-test", KeytermsFile = _folder.File("no-glossary.txt") }),
            recording,
            new TranscriptionProgress(),
            new UsageReport(new FakeElevenLabsAccount(), TimeProvider.System, NullLogger<UsageReport>.Instance),
            _log);
    }

    /// <summary>The transcript of a downloaded video is headed by its title, when it was downloaded, and the video's link.</summary>
    [Test]
    public async Task A_videos_transcript_is_headed_by_its_title_and_link()
    {
        var saved = await Workflow().TranscribeRecordingAsync(_files, CancellationToken.None);

        var lines = File.ReadAllLines(saved.TranscriptPath).Where(line => line.Length > 0).ToList();
        Assert.That(lines[0], Is.EqualTo("# The creed, part 1"));
        Assert.That(lines[1], Is.EqualTo("Downloaded 18 October 2026, 14:02 UTC."));
        Assert.That(lines[2], Does.StartWith("From YouTube: [The creed, part 1](https://www.youtube.com/watch?v=abcdefghijk), by Your Channel"));
        Assert.That(lines[3], Is.EqualTo("Hello world."));
    }

    /// <summary>
    /// A free rebuild keeps the heading, and with the audio deleted, as when Drive has it, still says when the video was
    /// downloaded: the source file shows that the files are a recording's.
    /// </summary>
    [Test]
    public async Task A_rebuild_keeps_the_heading_once_the_audio_is_gone()
    {
        await Workflow().TranscribeRecordingAsync(_files, CancellationToken.None);
        File.Delete(_files.Segment(1));
        File.Delete(_files.Transcript());

        var rebuilt = await Workflow().RebuildAsync(_files, answerPath: null, CancellationToken.None);

        var lines = File.ReadAllLines(rebuilt).Where(line => line.Length > 0).ToList();
        Assert.That(lines[0], Is.EqualTo("# The creed, part 1"));
        Assert.That(lines[1], Is.EqualTo("Downloaded 18 October 2026, 14:02 UTC."));
        Assert.That(lines[2], Does.StartWith("From YouTube: [The creed, part 1]"));
    }

    /// <summary>A transcript made before, whose first line under the title says when the video was downloaded, keeps that time in a rebuild without the source file.</summary>
    [Test]
    public async Task A_transcript_that_says_when_it_was_downloaded_shows_a_recording()
    {
        await Workflow().TranscribeRecordingAsync(_files, CancellationToken.None);
        File.Delete(_files.Segment(1));
        File.Delete(_files.Source);

        var rebuilt = await Workflow().RebuildAsync(_files, answerPath: null, CancellationToken.None);

        Assert.That(File.ReadAllLines(rebuilt).Where(line => line.Length > 0).ElementAt(1), Is.EqualTo("Recorded 18 October 2026, 14:02 UTC."));
    }

    /// <summary>A source file that cannot be read is said, and the paid transcript is saved all the same, headed by the recording's name.</summary>
    [Test]
    public async Task A_source_file_that_cannot_be_read_never_costs_the_transcript()
    {
        File.WriteAllText(_files.Source, "not json");

        var saved = await Workflow().TranscribeRecordingAsync(_files, CancellationToken.None);

        Assert.That(File.ReadAllLines(saved.TranscriptPath)[0], Is.EqualTo("# Risala"));
        Assert.That(_log.Problems.Single(), Does.StartWith("The video's source file of Risala_2026-10-18_14-02-11Z could not be read"));
    }
}
