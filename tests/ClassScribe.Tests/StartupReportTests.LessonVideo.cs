using ClassScribe.Audio;
using ClassScribe.Commands;
using ClassScribe.Recorder;
using ClassScribe.Storage;
using Moq;

namespace ClassScribe.Tests;

/// <summary>
/// The report at the start and a lesson's video (teach-video 6.9, slice 8): V, to make it now, free, for a lesson whose
/// video is still to be made by its files and whose process is gone; T beside it only when the lesson was to be
/// transcribed and has no transcript, C only when it ended while recording; a record-only lesson found in Transcribing said
/// to have had its video made, not its transcript; an unseen outcome followed by V once; a lesson with its video made
/// dealt with.
/// </summary>
public partial class StartupReportTests
{
    /// <summary>Writes a camera piece of <paramref name="files"/>, as a lesson's camera process leaves it, last written before the audio.</summary>
    private static void WritePiece(RecordingFiles files)
    {
        File.WriteAllBytes(files.CameraPiece(TimeSpan.Zero), [1, 2, 3]);
        File.SetLastWriteTimeUtc(files.CameraPiece(TimeSpan.Zero), LastAudio.AddSeconds(-1).UtcDateTime);
    }

    /// <summary>The run of a lesson with the camera "OBSBOT", in person, transcribed when <paramref name="transcribe"/>.</summary>
    private static Func<RunRecord, RunRecord> Lesson(bool transcribe, Func<RunRecord, RunRecord>? more = null) => record =>
    {
        var lesson = record with
        {
            Request = record.Request with
            {
                Transcribe = transcribe,
                Source = new SourcePlan(RecordingMode.Microphone, Class: null, new MicrophoneChoice(null), new CameraChoice("OBSBOT", "720p"))
            }
        };
        return more?.Invoke(lesson) ?? lesson;
    };

    /// <summary>
    /// A record-only lesson whose video's making was cut off is said as such, with V and F and no T; V makes the video
    /// through the runner, by one of the lesson's files, and ends the report.
    /// </summary>
    [Test]
    public async Task A_lesson_whose_video_was_cut_off_offers_V()
    {
        var files = WriteRecording();
        WritePiece(files);
        WriteRun(files, RunState.Transcribing, change: Lesson(transcribe: false));
        _commands.Setup(commands => commands.FinishVideoAsync(It.IsAny<FinishVideoRequest>())).ReturnsAsync(0);
        _ui.Type('t', 'v');

        var choice = await Report().ShowAsync(interactive: true);

        Assert.That(choice, Is.EqualTo(new StartupChoice(RunId, StartupAction.MakeVideo)));
        Assert.That(_ui.Screen, Does.StartWith($"The lesson video of \"Kalam\" ({files.BaseName}) was not finished; its sound and its camera pieces are kept."));
        Assert.That(_ui.Screen, Does.Contain("V  make the lesson's video now, free").And.Not.Contain("T  transcribe"));
        _commands.Verify(commands => commands.FinishVideoAsync(new FinishVideoRequest(files.Segment(1), true)), Times.Once);
        _commands.Verify(commands => commands.TranscribeAsync(It.IsAny<TranscribeRequest>()), Times.Never, "T is not offered for a record-only lesson");
        Assert.That(Run().Seen, Is.True);
    }

    /// <summary>
    /// A lesson to be transcribed, with its transcript but not its video, offers V alone beside F, and is said to have had its
    /// video cut off, not its transcript, which is there.
    /// </summary>
    [Test]
    public async Task A_transcribed_lesson_without_its_video_offers_V_alone()
    {
        var files = WriteRecording();
        WritePiece(files);
        File.WriteAllText(files.Transcript(), "the transcript");
        WriteRun(files, RunState.Transcribing, change: Lesson(transcribe: true));
        _ui.Type('\r');

        await Report().ShowAsync(interactive: true);

        Assert.That(_ui.Screen, Does.StartWith($"The lesson video of \"Kalam\" ({files.BaseName}) was not finished; its sound and its camera pieces are kept."));
        Assert.That(_ui.Screen, Does.Not.Contain("The transcript of").And.Not.Contain("ElevenLabs may bill it again"));
        Assert.That(_ui.Screen, Does.Contain("V  make the lesson's video now, free").And.Not.Contain("T  transcribe"));
        Assert.That(Run().Seen, Is.False, "Enter leaves it for the next start");
    }

    /// <summary>A lesson that ended while it recorded offers C, V and F, and T only when it was to be transcribed.</summary>
    [TestCase(false)]
    [TestCase(true)]
    public async Task A_lesson_that_ended_while_recording_offers_C_and_V(bool transcribe)
    {
        var files = WriteRecording();
        WritePiece(files);
        WriteRun(files, RunState.Recording, change: Lesson(transcribe));
        _ui.Type('\r');

        await Report().ShowAsync(interactive: true);

        Assert.That(_ui.Screen, Does.Contain("V  make the lesson's video now, free"));
        Assert.That(_ui.Screen.Contains("T  transcribe what was recorded", StringComparison.Ordinal), Is.EqualTo(transcribe));
    }

    /// <summary>A lesson with its transcript and its video made has been dealt with: marked seen without a word.</summary>
    [Test]
    public async Task A_lesson_with_its_transcript_and_video_is_dealt_with()
    {
        var files = WriteRecording();
        WritePiece(files);
        File.WriteAllText(files.Transcript(), "the transcript");
        File.WriteAllText(files.LessonVideo, "the video");
        WriteRun(files, RunState.Transcribing, change: Lesson(transcribe: true));

        await Report().ShowAsync(interactive: true);

        Assert.That(_ui.Screen, Is.Empty);
        Assert.That(Run().Seen, Is.True);
    }

    /// <summary>
    /// The outcome nobody saw of a record-only lesson whose video was not made (it failed its check, say) is shown, then V once:
    /// pressing it makes the video; the run is seen either way.
    /// </summary>
    [Test]
    public async Task An_unseen_outcome_of_a_lesson_without_its_video_offers_V_once()
    {
        var files = WriteRecording();
        WritePiece(files);
        WriteRun(files, RunState.Finished, change: Lesson(transcribe: false, record => record with
        {
            Outcome = new RunOutcome(OutcomeKind.Recorded, 0, $"The recording is in {files.Folder}.", null, files.Folder, null)
        }));
        _commands.Setup(commands => commands.FinishVideoAsync(It.IsAny<FinishVideoRequest>())).ReturnsAsync(0);
        _ui.Type('v');

        var choice = await Report().ShowAsync(interactive: true);

        Assert.That(choice, Is.EqualTo(new StartupChoice(RunId, StartupAction.MakeVideo)));
        Assert.That(_ui.Screen, Does.Contain($"\"Kalam\" was recorded in the background: {files.Folder}."));
        Assert.That(_ui.Screen, Does.Contain("The lesson video of \"Kalam\" was not made; its sound and its camera pieces are kept."));
        _commands.Verify(commands => commands.FinishVideoAsync(new FinishVideoRequest(files.Segment(1), true)), Times.Once);
        Assert.That(Run().Seen, Is.True);
    }

    /// <summary>On the command line the report prints the command that makes the video, and no transcribe command for a record-only lesson.</summary>
    [Test]
    public async Task On_the_command_line_the_report_prints_finish_video()
    {
        var files = WriteRecording();
        WritePiece(files);
        WriteRun(files, RunState.Transcribing, change: Lesson(transcribe: false));

        await Report().ShowAsync(interactive: false);

        Assert.That(_ui.Screen, Does.Contain($"To make its lesson video, free: {CommandLine.FinishVideo(files.Segment(1))}"));
        Assert.That(_ui.Screen, Does.Not.Contain("To transcribe"));
    }
}
