using ClassScribe.Audio;
using ClassScribe.Commands;
using ClassScribe.Desktop;
using ClassScribe.Drive;
using ClassScribe.Settings;
using ClassScribe.Storage;
using ClassScribe.Transcription;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClassScribe.Tests;

/// <summary>
/// A class whose audio went up to Google Drive and was then freed from the laptop, as the rest of the app sees it: listed,
/// with its audio on Drive and its length as measured when it went up; its transcript readable and rebuilt for free from the
/// saved answer; and transcribing it again refused, saying where the audio is, rather than sending its transcript as if it
/// were audio, or an older class in its place. Menu choice 3 offers the rebuild alone. Nothing here asks Google anything
/// once the class is freed.
/// </summary>
[TestFixture]
public class FreedAudioTests
{
    private static readonly DateTimeOffset MantiqStart = new(2026, 10, 17, 14, 4, 42, TimeSpan.Zero);
    private GoogleWorld _world = null!;
    private RecordingFiles _mantiq = null!;
    private Mock<IAudioPreparer> _preparer = null!;
    private Mock<ITranscriptionClient> _client = null!;
    private RecordingLogger<TranscribeCommand> _transcribeLog = null!;
    private FakeConsoleUi _ui = null!;

    /// <summary>Today's class, with both tracks, its transcript, answer and marks, uploaded and then freed; a transcription that must never be sent.</summary>
    [SetUp]
    public async Task SetUp()
    {
        _world = new GoogleWorld();
        _world.SignedIn();
        _mantiq = _world.WriteRecording(@"Mantiq\2026-10-17", "Mantiq", MantiqStart, extras: [".marks.txt"]);
        GoogleWorld.WriteAudio(_mantiq.Segment(1, Track.Microphone), 4000);
        File.WriteAllText(_mantiq.Json(), """{ "text": "Bismillah. Today we begin the Sullam." }""");
        File.WriteAllText(_mantiq.Transcript(), "# Mantiq\n\nRecorded 17 Oct 2026 10:04 (local)\n\nBismillah.\n");
        await _world.RecordingUpload().UploadAsync(_mantiq, _ => Task.CompletedTask, CancellationToken.None);
        await _world.Freer().FreeAsync(_mantiq, CancellationToken.None);
        _preparer = new Mock<IAudioPreparer>(MockBehavior.Strict);
        _client = new Mock<ITranscriptionClient>(MockBehavior.Strict);
        _transcribeLog = new RecordingLogger<TranscribeCommand>();
        _ui = new FakeConsoleUi();
    }

    /// <summary>Deletes the world's folders.</summary>
    [TearDown]
    public void TearDown() => _world.Dispose();

    /// <summary>The workflow over the world's class folders, with an upload that must never be made.</summary>
    private TranscriptionWorkflow Workflow(ClassFolders folders) =>
        new(_preparer.Object, _client.Object, new KeytermLoader(NullLogger<KeytermLoader>.Instance), new TranscriptFormatter(TestOptions.Create(new TranscriptOptions())),
            folders, new ClassMarks(NullLogger<ClassMarks>.Instance), TestOptions.Create(new ElevenLabsOptions { ApiKey = "dummy-key-for-a-local-test" }), _world.Recording,
            new TranscriptionProgress(), new UsageReport(new FakeElevenLabsAccount(), _world.Time, NullLogger<UsageReport>.Instance), NullLogger<TranscriptionWorkflow>.Instance);

    /// <summary>The transcribe command over the world's class folders, which know of the audio on Drive.</summary>
    private TranscribeCommand Transcribe()
    {
        var folders = _world.ClassFolders();
        return new TranscribeCommand(Workflow(folders), new UsageReport(new FakeElevenLabsAccount(), _world.Time, NullLogger<UsageReport>.Instance), folders, new FakeUserSignals(),
            new Mock<IDesktop>().Object, new FakePowerRequests(), new FakePowerSource(), new FakeDriveAutoUpload(), _world.Recording, TestOptions.Create(new ElevenLabsOptions { ApiKey = "dummy-key-for-a-local-test" }),
            ZonedTimeProvider.EasternSummer, _transcribeLog);
    }

    /// <summary>The line that says where the audio is, for the tests' class.</summary>
    private string AudioIsOnDrive(string which = "The audio") =>
        $"{which} of {_mantiq.BaseName} is on Google Drive, not on this laptop: My Drive > ClassScribe > Mantiq > 2026-10-17 ({_world.Manifests().Read(_mantiq)!.DriveLink}). To transcribe it again, download its .wav files from there into {_mantiq.Folder} first";

    /// <summary>The list shows the freed class, with its length as measured when it went up, its transcript, and that its audio is on Drive.</summary>
    [Test]
    public void The_list_shows_a_freed_class_with_its_audio_on_Drive()
    {
        var ui = new FakeConsoleUi();

        new ListCommand(_world.ClassFolders(), _world.Runtime.Journal, ui, ZonedTimeProvider.EasternSummer).Run(null);

        Assert.That(ui.Screen, Does.Contain("  Mantiq  17 Oct 10:04  00:00:49  class + microphone  audio on Drive  transcript"));
    }

    /// <summary>
    /// Transcribing a file of the freed class, its transcript, is refused with exit code 1, saying where the audio is and how to
    /// rebuild for free: the transcript is never sent as if it were audio.
    /// </summary>
    [Test]
    public async Task Transcribing_a_file_of_a_freed_class_says_the_audio_is_on_Drive()
    {
        Assert.That(await Transcribe().RunAsync(new TranscribeRequest(_mantiq.Transcript(), FromCommandLine: true, Again: true)), Is.EqualTo(1));

        Assert.That(_transcribeLog.Problems, Is.EqualTo(new[] { AudioIsOnDrive() }));
        Assert.That(_transcribeLog.Messages, Has.Member($"To lay its transcript out again for free, from ElevenLabs' saved answer, run: {CommandLine.Rebuild(_mantiq.Json())}"));
    }

    /// <summary>
    /// Transcribing the freed class's date folder, or its course's folder although an older class there still has its audio,
    /// means the freed class, the newest, and is refused: the older class is not sent in its place.
    /// </summary>
    [Test]
    public async Task Transcribing_the_folder_of_a_freed_class_says_the_audio_is_on_Drive()
    {
        _world.WriteRecording(@"Mantiq\2026-10-10", "Mantiq", MantiqStart.AddDays(-7), 2000);

        Assert.That(await Transcribe().RunAsync(new TranscribeRequest(_mantiq.Folder)), Is.EqualTo(1));
        Assert.That(await Transcribe().RunAsync(new TranscribeRequest(Path.GetDirectoryName(_mantiq.Folder)!)), Is.EqualTo(1));

        Assert.That(_transcribeLog.Problems, Is.EqualTo(new[] { AudioIsOnDrive(), AudioIsOnDrive() }));
    }

    /// <summary>A class part of whose audio is gone, its microphone's track deleted by hand after it went up, is not sent short: that is said.</summary>
    [Test]
    public async Task A_class_with_part_of_its_audio_on_Drive_is_not_sent_short()
    {
        var fiqh = _world.WriteRecording(@"Fiqh\2026-10-16", "Fiqh", MantiqStart.AddDays(-1), 2000);
        GoogleWorld.WriteAudio(fiqh.Segment(1, Track.Microphone), 2000);
        await _world.RecordingUpload().UploadAsync(fiqh, _ => Task.CompletedTask, CancellationToken.None);
        File.Delete(fiqh.Segment(1, Track.Microphone));

        Assert.That(await Transcribe().RunAsync(new TranscribeRequest(fiqh.Segment(1))), Is.EqualTo(1));

        Assert.That(_transcribeLog.Problems.Single(), Does.StartWith($"Part of the audio of {fiqh.BaseName} is on Google Drive, not on this laptop: My Drive > ClassScribe > Fiqh > 2026-10-16"));
    }

    /// <summary>Rebuilding the freed class works from its saved answer, as for any class: a new transcript copy, nothing sent.</summary>
    [Test]
    public async Task Rebuilding_a_freed_class_works_from_its_saved_answer()
    {
        var folders = _world.ClassFolders();
        var rebuild = new RebuildCommand(Workflow(folders), folders, new Mock<IDesktop>().Object, new FakeDriveAutoUpload(), _world.Recording, NullLogger<RebuildCommand>.Instance);

        Assert.That(await rebuild.RunAsync(_mantiq.Folder, open: false), Is.EqualTo(0));

        Assert.That(File.ReadAllText(_mantiq.Transcript(2)), Does.Contain("Bismillah. Today we begin the Sullam."));
    }

    /// <summary>
    /// Menu choice 3 lists the freed class, notes its audio on Drive, and when picked says where the audio is and offers the
    /// free rebuild alone, from the saved answer; nothing is sent to be transcribed.
    /// </summary>
    [Test]
    public async Task Menu_choice_3_offers_the_rebuild_alone()
    {
        var commands = new Mock<ICommandRunner>();
        commands.Setup(c => c.RebuildAsync(It.IsAny<string>(), It.IsAny<bool>())).ReturnsAsync(0);
        using var settingsFolder = new TempFolder();
        var settings = new TestSettings(settingsFolder.Path);
        var menu = new MenuCommand(commands.Object, new NoStartupReport(), _ui, settings.Gate(), settings.File, _world.ClassFolders(), new FakeCaptureProvider(),
            new SourceChoice(TestOptions.Create(new RecordingOptions())), new RecordingNamePrompt(_ui), new Mock<IDesktop>().Object, ZonedTimeProvider.EasternSummer,
            NullLogger<MenuCommand>.Instance);
        _ui.Type('3', '1', 'r', 'q');

        Assert.That(await menu.RunAsync().WaitAsync(TimeSpan.FromSeconds(15)), Is.EqualTo(0));

        Assert.That(_ui.Screen, Does.Contain($"  1  {_mantiq.BaseName}  00:00:49  class + microphone  audio on Drive  transcript ready"));
        Assert.That(_ui.Screen, Does.Contain($"The audio of {_mantiq.BaseName} is on Google Drive, not on this laptop (My Drive > ClassScribe > Mantiq > 2026-10-17). To transcribe it again, download its .wav files from there into {_mantiq.Folder} first. R rebuilds its transcript"));
        commands.Verify(c => c.RebuildAsync(_mantiq.Json(), true), Times.Once);
        commands.Verify(c => c.TranscribeAsync(It.IsAny<TranscribeRequest>()), Times.Never);
    }

    /// <summary>
    /// Menu choice 4 with the course's folder, whose newest class is the freed one and whose older class still has its audio,
    /// asks about the freed one, as transcribe would take it: it says where its audio is and offers the free rebuild of its own
    /// answer; the older class is neither described nor sent.
    /// </summary>
    [Test]
    public async Task Menu_choice_4_with_the_course_folder_asks_about_its_newest_class_even_when_freed()
    {
        var older = _world.WriteRecording(@"Mantiq\2026-10-15", "Mantiq", MantiqStart.AddDays(-2), 2000, ".md", ".json");
        var commands = new Mock<ICommandRunner>();
        commands.Setup(c => c.RebuildAsync(It.IsAny<string>(), It.IsAny<bool>())).ReturnsAsync(0);
        using var settingsFolder = new TempFolder();
        var settings = new TestSettings(settingsFolder.Path);
        var menu = new MenuCommand(commands.Object, new NoStartupReport(), _ui, settings.Gate(), settings.File, _world.ClassFolders(), new FakeCaptureProvider(),
            new SourceChoice(TestOptions.Create(new RecordingOptions())), new RecordingNamePrompt(_ui), new Mock<IDesktop>().Object, ZonedTimeProvider.EasternSummer,
            NullLogger<MenuCommand>.Instance);
        _ui.TypeLine(Path.Combine(_world.Recordings.Path, "Mantiq"));
        _ui.Type('4', 'r', 'q');

        Assert.That(await menu.RunAsync().WaitAsync(TimeSpan.FromSeconds(15)), Is.EqualTo(0));

        Assert.That(_ui.Screen, Does.Contain($"The audio of {_mantiq.BaseName} is on Google Drive, not on this laptop (My Drive > ClassScribe > Mantiq > 2026-10-17)."));
        Assert.That(_ui.Screen, Does.Not.Contain(older.BaseName));
        commands.Verify(c => c.RebuildAsync(_mantiq.Json(), true), Times.Once);
        commands.Verify(c => c.TranscribeAsync(It.IsAny<TranscribeRequest>()), Times.Never);
    }
}
