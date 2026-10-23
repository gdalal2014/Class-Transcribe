using System.Net;
using ClassScribe.Audio;
using ClassScribe.Commands;
using ClassScribe.Desktop;
using ClassScribe.Recorder;
using ClassScribe.Settings;
using ClassScribe.Storage;
using ClassScribe.Transcription;
using ClassScribe.YouTube;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Refit;

namespace ClassScribe.Tests;

/// <summary>What a played video's run does (see <see cref="VideoWorld"/>), as its recorder would.</summary>
/// <param name="Length">How long the recording it writes is.</param>
/// <param name="EndedBy">What ends its recording, as its run file names it.</param>
public sealed record PlayedVideoRun(TimeSpan Length, string EndedBy = RunEndCause.VideoClosed)
{
    /// <summary>How its run ends: a recording, unless the test says otherwise.</summary>
    public OutcomeKind Outcome { get; init; } = OutcomeKind.Recorded;

    /// <summary>True when its view was left with Esc: it records on in the background, its process alive.</summary>
    public bool StaysRecording { get; init; }

    /// <summary>True when its recorder is lost while it records: its run file stays as it was, recording, and its process is gone.</summary>
    public bool Lost { get; init; }

    /// <summary>True when the starter refused it because a class records, which the played run then starts, for <see cref="ClassFor"/>.</summary>
    public bool Refused { get; init; }

    /// <summary>How long the class that refused it records, on the test's clock.</summary>
    public TimeSpan ClassFor { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>What a run in the window throws, as one that failed there does; null when it ends.</summary>
    public Exception? Throws { get; init; }

    /// <summary>Runs while it records, such as Ctrl+C pressed twice, or a class starting.</summary>
    public Action<VideoWorld>? During { get; init; }
}

/// <summary>
/// Everything a test of the record path's batch needs (design 7.2), put together once: a test's own ClassScribe folder with
/// its run files and a clock at the tests' class, read in US Eastern time; a recordings folder; the console and Ctrl+C;
/// Chrome found on a played disk; its windows and processes played; YouTube's titles from a stub transport at a stub address;
/// the account, the transcripts played, keep-awake and the desktop; the settings; and each video's run played through the
/// command runner, as its recorder would play it: its recording, its source file, its run file and its window closed. Nothing
/// starts, nothing plays, and nothing reaches YouTube.
/// </summary>
public sealed class VideoWorld : IDisposable
{
    /// <summary>Chrome, as the played disk has it.</summary>
    public const string Chrome = @"C:\Program Files\Google\Chrome\Application\chrome.exe";

    /// <summary>The process of the class that a played refusal starts.</summary>
    public const int ClassProcess = 9000;

    private readonly List<RecordRequest> _started = [];

    /// <summary>Puts everything together.</summary>
    public VideoWorld()
    {
        Folders = new YouTubeFolders(Runtime.Folder);
        Files.Add(Chrome);
        Files.Versions[Chrome] = "141.0.7390.108";
        Starter = new FakeProcessStarter(Runtime.Time);
        Browsers = new FakeVideoBrowsers(Starter, Runtime.Processes);
        Recording = TestOptions.Create(new RecordingOptions { OutputFolder = Recordings.Path, OpenFolderWhenDone = true });
        ElevenLabs = TestOptions.Create(new ElevenLabsOptions { ApiKey = "dummy-key-for-a-local-test", KeytermsFile = Recordings.File("no-glossary.txt") });
        ClassFolders = new ClassFolders(Recording, Time, Log<ClassFolders>());
        Commands.Setup(runner => runner.RecordAsync(It.IsAny<RecordRequest>(), It.IsAny<StartOrigin>()))
            .Returns((RecordRequest request, StartOrigin origin) => PlayAsync(request, origin));
    }

    /// <summary>The test's own ClassScribe folder, run files and played processes.</summary>
    public TestRuntime Runtime { get; } = new();

    /// <summary>The clock, at the tests' class until a test moves it, read in US Eastern time.</summary>
    public TimeProvider Time => _time ??= new ZonedClock(Runtime.Time, ZonedTimeProvider.EasternSummer.LocalTimeZone);

    private TimeProvider? _time;

    /// <summary>The recordings folder.</summary>
    public TempFolder Recordings { get; } = new();

    /// <summary>The feature's folders, in the test's ClassScribe folder.</summary>
    public YouTubeFolders Folders { get; }

    /// <summary>The disk Chrome is looked for on.</summary>
    public FakeFileProbe Files { get; } = new();

    /// <summary>The starter of Chrome, played.</summary>
    public FakeProcessStarter Starter { get; }

    /// <summary>The windows and processes of Chrome, played.</summary>
    public FakeVideoBrowsers Browsers { get; }

    /// <summary>YouTube's oEmbed address, a stub.</summary>
    public StubHttpHandler Http { get; } = new();

    /// <summary>The console.</summary>
    public FakeConsoleUi Ui { get; } = new();

    /// <summary>Ctrl+C, in the batch's scope; a test that runs two batches gives the second its own, as each batch's scope has.</summary>
    public FakeUserSignals Signals { get; set; } = new();

    /// <summary>The transcripts, played.</summary>
    public FakeVideoTranscriber Transcriber { get; } = new();

    /// <summary>The account's credits.</summary>
    public FakeElevenLabsAccount Account { get; } = new();

    /// <summary>Keep-awake.</summary>
    public FakePowerRequests Power { get; } = new();

    /// <summary>The desktop, which opens folders.</summary>
    public Mock<IDesktop> Desktop { get; } = new();

    /// <summary>The command runner, whose starts of videos are played (see <see cref="Plays"/>).</summary>
    public Mock<ICommandRunner> Commands { get; } = new();

    /// <summary>What each video's run does, by video ID; a run not named records 30 seconds and ends as Gulzar closing its window ends it.</summary>
    public Dictionary<string, PlayedVideoRun> Plays { get; } = new(StringComparer.Ordinal);

    /// <summary>The Recording settings.</summary>
    public TestOptions<RecordingOptions> Recording { get; }

    /// <summary>The ElevenLabs settings, with a dummy key.</summary>
    public TestOptions<ElevenLabsOptions> ElevenLabs { get; }

    /// <summary>The YouTube settings.</summary>
    public TestOptions<YouTubeOptions> YouTube { get; } = TestOptions.Create(new YouTubeOptions());

    /// <summary>The class folders, on the test's clock.</summary>
    public ClassFolders ClassFolders { get; }

    /// <summary>Every log line of every part, in order.</summary>
    public TestLoggerFactory Logs { get; } = new();

    /// <summary>Every video's run started, in order, as the batch asked for it.</summary>
    public IReadOnlyList<RecordRequest> Started
    {
        get { lock (_started) return [.. _started]; }
    }

    /// <summary>A logger of the batch's parts into <see cref="Logs"/>.</summary>
    public ILogger<T> Log<T>() => new Logger<T>(Logs);

    /// <summary>YouTube gives <paramref name="title"/> by <paramref name="channel"/> for the next video read; null for no title.</summary>
    public VideoWorld Title(string? title, string channel = "Shaykh X", string handle = "@shaykhx")
    {
        if (title is null)
            Http.EnqueueFor("/oembed", HttpStatusCode.NotFound, "Not Found");
        else
            Http.EnqueueFor("/oembed", HttpStatusCode.OK, $$"""{"title":"{{title}}","author_name":"{{channel}}","author_url":"https://www.youtube.com/{{handle}}"}""");
        return this;
    }

    /// <summary>The batch, put together.</summary>
    public VideoBatch Batch()
    {
        var wait = new ClassWait(Runtime.Journal, Runtime.Time, Log<ClassWait>());
        var usage = new UsageReport(Account, Time, Log<UsageReport>());
        var batch = new YouTubeBatch(new YouTubeLock(Folders, Log<YouTubeLock>()), wait, new BatchTranscripts(Transcriber, wait, ClassFolders, Runtime.Journal, Log<BatchTranscripts>()), Account, usage, Power,
            new FakePowerSource(), Ui, Desktop.Object, Recording, ElevenLabs, Runtime.Time, Log<YouTubeBatch>());
        var http = new HttpClient(new YouTubeHostGuard { InnerHandler = Http }) { BaseAddress = new Uri("http://127.0.0.1:5005/") };
        var titles = new OEmbedTitles(RestService.ForGenerated<IYouTubeOEmbedApi>(http, YouTubeJson.Default, OEmbedTitles.RefitSettings()), Runtime.Time, Log<OEmbedTitles>());
        var browsers = new BrowserLocator(new FakeRegistryValues(), Files, YouTube, Log<BrowserLocator>()) { ProgramFiles = @"C:\Program Files", ProgramFilesX86 = @"C:\Program Files (x86)", LocalAppData = @"C:\Users\gdala\AppData\Local" };
        var windows = new VideoWindow(Starter, Browsers.Windows, Folders, Runtime.Time, Log<VideoWindow>());
        return new VideoBatch(batch, new DownloadedAudio(Folders, Recording, Runtime.Time, Log<DownloadedAudio>()), browsers, titles, windows, Commands.Object, Runtime.Journal, ClassFolders, new KeytermLoader(new SettingsPlaces(AppContext.BaseDirectory, userFolder: null), NullLogger<KeytermLoader>.Instance), Signals, Ui,
            YouTube, Recording, ElevenLabs, Time, Log<VideoBatch>());
    }

    /// <summary>A request for <paramref name="links"/>, recorded as they play into the course Risala, going ahead with --yes unless told otherwise.</summary>
    public YouTubeRequest Request(string[] links, BatchConfirmation confirmation = BatchConfirmation.Yes, bool transcribe = true, bool again = false) =>
        new(LinkList.Read(links.Select(link => new LinkWord(link, "the command line"))).Links, "Risala", Recordings.Path, Download: false, confirmation, transcribe,
            Open: true, again);

    /// <summary>Runs the batch for <paramref name="request"/>, the clock moving on half a second at a time until it is done, within a minute.</summary>
    public async Task<int> RunAsync(YouTubeRequest request)
    {
        var running = Batch().RunAsync(request);
        await Runtime.Time.AdvanceUntilAsync(() => running.IsCompleted, TimeSpan.FromMilliseconds(500), timeoutSeconds: 60);
        return await running;
    }

    /// <summary>The lines the console showed.</summary>
    public string[] Screen => Ui.Screen.Split(Environment.NewLine);

    /// <summary>Every log line, as the window shows it.</summary>
    public IReadOnlyList<string> Lines => Logs.Messages;

    /// <summary>The batch's date folder, the one date folder under Risala.</summary>
    public string DateFolder => Directory.GetDirectories(Path.Combine(Recordings.Path, "Risala")).Single();

    /// <summary>Deletes the folders.</summary>
    public void Dispose()
    {
        Recordings.Dispose();
        Runtime.Dispose();
    }

    /// <summary>
    /// Plays the run of a video the batch asked for, as its recorder would: refused while a class records; otherwise its
    /// recording in the batch's folder or a date folder of its own, its source file, its run file with how it ended, and the
    /// window asked to close; or left recording in the background; or failing in the window.
    /// </summary>
    private Task<int> PlayAsync(RecordRequest request, StartOrigin origin)
    {
        Assert.That(origin, Is.EqualTo(StartOrigin.VideoBatch), "a batch's video is started as one");
        lock (_started) _started.Add(request);
        var video = request.Video!;
        var play = Plays.TryGetValue(video.VideoId, out var given) ? given : new PlayedVideoRun(TimeSpan.FromSeconds(30));
        if (play.Refused)
        {
            Plays[video.VideoId] = play with { Refused = false };
            StartAClass(play.ClassFor);
            return Task.FromResult(1);
        }

        var files = video.Folder is { } folder ? ClassFolders.CreateRecordingIn(folder, request.Name) : ClassFolders.CreateRecording(request.Name, request.OutputRoot);
        Directory.CreateDirectory(files.Folder);
        using (var writer = new WavSegmentWriter(files.Segment(1), 10)) writer.Write(new byte[(int)(play.Length.TotalSeconds * 10) * 2]);
        play.During?.Invoke(this);

        var stillRecording = play.StaysRecording || play.Lost;
        var (ended, confirmed) = stillRecording ? (VideoEnds.NotFinished, false) : VideoEnds.Of(play.EndedBy);
        VideoSourceFile.Write(files.Source, new VideoSource(VideoSource.YouTubeKind, video.Url, video.VideoId, video.Title, video.Channel, video.ChannelHandle, null, null, null,
            null, null, VideoSource.Recorded, video.Tool, Time.GetUtcNow(), null, null, ended, confirmed));

        var processId = 8000 + _started.Count;
        var outcome = play.Outcome == OutcomeKind.Recorded
            ? new RunOutcome(OutcomeKind.Recorded, 0, $"The recording of \"{video.Title}\" is in {files.Folder}.", null, files.Folder, null)
            : new RunOutcome(play.Outcome, 1, play.Outcome == OutcomeKind.NothingRecorded ? $"Nothing played in ClassScribe's video window, so nothing of \"{video.Title}\" was kept to transcribe" : "The recording failed.", null, files.Folder, null);
        var record = TestRuns.Record(request.RunId, stillRecording ? RunState.Recording : RunState.Finished, processId, Runtime.Time.GetUtcNow()) with
        {
            Request = request,
            Folder = files.Folder,
            BaseName = files.BaseName,
            StartedUtc = Runtime.Time.GetUtcNow(),
            EndedBy = stillRecording ? null : play.EndedBy,
            Outcome = stillRecording ? null : outcome
        };
        Runtime.Journal.Write(Runtime.Journal.RunFile(request.RunId), record);
        if (play.StaysRecording)
        {
            Runtime.Processes.Start(processId, record.ProcessStartedUtc);
            return Task.FromResult(0);
        }

        // A recorder lost leaves its window open: the batch closes it.
        if (play.Lost) return Task.FromResult(1);

        // The run asks its window to close as its capture stops.
        Browsers.Windows.Close(video.BrowserProcessId!.Value);
        if (play.Throws is { } failure) return Task.FromException<int>(failure);
        return Task.FromResult(outcome.ExitCode);
    }

    /// <summary>A class starts recording now, in its own recorder, and stops after <paramref name="length"/> on the test's clock.</summary>
    public void StartAClass(TimeSpan length)
    {
        var id = TestRuns.Id(40);
        var record = TestRuns.Record(id, RunState.Recording, ClassProcess, TestRuns.ProcessStart, "Kalam") with { StartedUtc = Runtime.Time.GetUtcNow() };
        Runtime.Journal.Write(Runtime.Journal.RunFile(id), record);
        Runtime.Processes.Start(ClassProcess, TestRuns.ProcessStart);
        Runtime.Time.CreateTimer(_ => Runtime.Processes.End(ClassProcess), null, length, Timeout.InfiniteTimeSpan);
    }
}
