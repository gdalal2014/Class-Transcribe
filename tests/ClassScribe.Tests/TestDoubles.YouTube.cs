using System.Collections.Concurrent;
using System.Net;
using ClassScribe.Commands;
using ClassScribe.Desktop;
using ClassScribe.Drive;
using ClassScribe.Recorder;
using ClassScribe.Settings;
using ClassScribe.Storage;
using ClassScribe.Transcription;
using ClassScribe.YouTube;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NAudio.Wave;

namespace ClassScribe.Tests;

/// <summary>The YouTube parts of runs as the tests write them down: a video's request, to be changed with <c>with</c> where a test needs something else.</summary>
public static class YouTubeTestRuns
{
    /// <summary>The id of the tests' batch.</summary>
    public static readonly string BatchId = TestRuns.Id(90);

    /// <summary>The video <paramref name="videoId"/>, downloaded, the first of a batch of one, its folder left to the run.</summary>
    public static VideoRequest Video(string videoId = "abcdefghijk", string title = "The creed, part 1", string? folder = null) =>
        new(YouTubeLinks.WatchUrl(videoId), videoId, title, "Your Channel", VideoSource.Downloaded, folder, BrowserProcessId: null, BrowserStartedUtc: null,
            Position: 1, Count: 1, BatchId);
}

/// <summary>Files a test says are there, and their versions; any other path is not. Paths compare without regard to case, as Windows compares them.</summary>
public sealed class FakeFileProbe : IFileProbe
{
    private readonly HashSet<string> _files = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The product version of each program's file that carries one, by path.</summary>
    public Dictionary<string, string> Versions { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Says that files are at <paramref name="paths"/>.</summary>
    public void Add(params string[] paths)
    {
        foreach (var path in paths) _files.Add(path);
    }

    /// <inheritdoc />
    public string? VersionOf(string path) => Versions.TryGetValue(path, out var version) ? version : null;

    /// <inheritdoc />
    public bool FileExists(string path) => _files.Contains(path);
}

/// <summary>How a played yt-dlp answers for one video: its information, the audio it downloads, and the failures it plays.</summary>
/// <param name="InfoJson">The information file its read writes.</param>
/// <param name="Seconds">How long the audio its download writes is.</param>
/// <param name="Rate">The audio's sample rate.</param>
public sealed record FakeVideo(string InfoJson, double Seconds, int Rate = 8000)
{
    /// <summary>The error line its read fails with; null when the read succeeds.</summary>
    public string? ReadError { get; init; }

    /// <summary>The error line its download fails with; null when the download succeeds.</summary>
    public string? DownloadError { get; init; }

    /// <summary>The exit code a failed download ends with.</summary>
    public int DownloadExitCode { get; init; } = 1;

    /// <summary>How many of its downloads are refused with HTTP 403 before one succeeds.</summary>
    public int Forbidden { get; init; }
}

/// <summary>
/// yt-dlp, Deno and FFmpeg as a test plays them (design 7.2): each call is kept with when it came on the test's clock;
/// --version and -version answer with the versions set; a playlist's read answers with its listing; a video's read writes
/// the information file yt-dlp would; a download writes the WAV FFmpeg would make, with NAudio's writer at the video's rate,
/// and done.txt naming it. Every argument list is checked as it comes: one holding a forbidden option, or not starting with
/// the fixed set, fails the test. Nothing is run, and nothing reaches YouTube.
/// </summary>
public sealed class FakeToolRunner(YouTubeFolders folders, TimeProvider time) : IToolRunner
{
    private readonly ConcurrentQueue<(ToolCall Call, long At)> _calls = new();
    private readonly ConcurrentDictionary<string, int> _forbiddenGiven = new();

    /// <summary>yt-dlp's version, as --version says it.</summary>
    public string YtDlpVersion { get; set; } = "2026.10.10";

    /// <summary>The videos it knows, by ID.</summary>
    public Dictionary<string, FakeVideo> Videos { get; } = new(StringComparer.Ordinal);

    /// <summary>The playlists it knows, by ID: the flat listing's JSON.</summary>
    public Dictionary<string, string> Playlists { get; } = new(StringComparer.Ordinal);

    /// <summary>Runs as each call of yt-dlp begins, before it answers, such as to cancel the batch at that moment.</summary>
    public Func<ToolCall, Task>? OnCall { get; set; }

    /// <summary>Every call so far, in order.</summary>
    public IReadOnlyList<ToolCall> Calls => [.. _calls.Select(call => call.Call)];

    /// <summary>Every call so far with when it came, on the steady clock.</summary>
    public IReadOnlyList<(ToolCall Call, long At)> TimedCalls => [.. _calls];

    /// <summary>The downloads so far, by video ID, in order.</summary>
    public IReadOnlyList<string> Downloads => [.. Calls.Where(call => call.Arguments.Contains("--newline")).Select(VideoIdOf)];

    /// <summary>The video reads so far, by video ID, in order.</summary>
    public IReadOnlyList<string> Reads => [.. Calls.Where(call => call.Arguments.Contains("--skip-download")).Select(VideoIdOf)];

    /// <summary>Adds a video with the information <paramref name="infoJson"/> gives and audio <paramref name="seconds"/> long.</summary>
    public FakeToolRunner With(string videoId, string infoJson, double seconds, Func<FakeVideo, FakeVideo>? change = null)
    {
        var video = new FakeVideo(infoJson, seconds);
        Videos[videoId] = change?.Invoke(video) ?? video;
        return this;
    }

    /// <inheritdoc />
    public async Task<ToolResult> RunAsync(ToolCall call, Action<ToolLine>? onLine, CancellationToken cancellationToken)
    {
        _calls.Enqueue((call, time.GetTimestamp()));
        if (OnCall is { } onCall) await onCall(call);
        cancellationToken.ThrowIfCancellationRequested();

        var program = Path.GetFileNameWithoutExtension(call.Program).ToLowerInvariant();
        if (call.Arguments is ["--version"] or ["-version"])
        {
            return program switch
            {
                "yt-dlp" => Answer(onLine, YtDlpVersion),
                "deno" => Answer(onLine, "deno 2.9.7 (stable, release, x86_64-pc-windows-msvc)"),
                _ => Answer(onLine, $"{program} version 7.1-full_build-www.gyan.dev Copyright (c) 2000-2026 the FFmpeg developers")
            };
        }

        if (YtDlpArguments.ProblemWith(call.Arguments, folders) is { } problem) Assert.Fail($"yt-dlp was given an argument it must never get: {problem}");
        Assert.That(program, Is.EqualTo("yt-dlp"), "only yt-dlp is ever run with a link");

        if (call.Arguments.Contains("--flat-playlist"))
        {
            var id = call.Arguments[^1].Split("list=")[1];
            return Playlists.TryGetValue(id, out var listing) ? Answer(onLine, listing) : Fail(onLine, $"ERROR: [youtube:tab] {id}: The playlist does not exist.");
        }

        var videoId = VideoIdOf(call);
        if (!Videos.TryGetValue(videoId, out var video)) return Fail(onLine, $"ERROR: [youtube] {videoId}: Video unavailable");
        var staging = folders.StagingOf(videoId);
        Directory.CreateDirectory(staging);

        if (call.Arguments.Contains("--skip-download"))
        {
            if (video.ReadError is { } readError) return Fail(onLine, readError);
            await File.WriteAllTextAsync(Path.Combine(staging, $"{videoId}.info.json"), video.InfoJson, cancellationToken);
            return Answer(onLine, $"[info] Writing video metadata as JSON to: {videoId}.info.json");
        }

        if (video.DownloadError is { } downloadError) return Fail(onLine, downloadError, video.DownloadExitCode);
        if (_forbiddenGiven.AddOrUpdate(videoId, 1, (_, count) => count + 1) <= video.Forbidden)
            return Fail(onLine, $"ERROR: unable to download video data: HTTP Error 403: Forbidden");

        var wav = Path.Combine(staging, $"{videoId}.wav");
        using (var writer = new WaveFileWriter(wav, new WaveFormat(video.Rate, 16, 2)))
        {
            var samples = new float[(int)(video.Rate * video.Seconds) * 2];
            for (var i = 0; i < samples.Length; i++) samples[i] = 0.2f * MathF.Sin(i / 7f);
            writer.WriteSamples(samples, 0, samples.Length);
        }

        await File.WriteAllTextAsync(Path.Combine(staging, YtDlpArguments.DoneFileName), wav + Environment.NewLine, cancellationToken);
        onLine?.Invoke(new ToolLine("[download]  50.0% of 1.00MiB", false));
        return Answer(onLine, "[download] 100% of 1.00MiB");
    }

    /// <summary>The video a read or a download names: the link's ID, or the output template's name.</summary>
    private static string VideoIdOf(ToolCall call)
    {
        var output = call.Arguments.ToList().IndexOf("-o");
        return output >= 0 ? Path.GetFileName(call.Arguments[output + 1]).Split('.')[0] : call.Arguments[^1].Split("v=")[1];
    }

    /// <summary>A successful run that wrote <paramref name="output"/>.</summary>
    private static ToolResult Answer(Action<ToolLine>? onLine, string output)
    {
        var lines = output.Split('\n');
        foreach (var line in lines) onLine?.Invoke(new ToolLine(line, false));
        return new ToolResult(0, lines, []);
    }

    /// <summary>A failed run that wrote <paramref name="error"/> to standard error.</summary>
    private static ToolResult Fail(Action<ToolLine>? onLine, string error, int exitCode = 1)
    {
        onLine?.Invoke(new ToolLine(error, true));
        return new ToolResult(exitCode, [], [error]);
    }
}

/// <summary>The batch's pace as a test plays it: no wait at all, each wait asked for kept in order.</summary>
public sealed class InstantPace : IBatchPace
{
    private readonly ConcurrentQueue<string> _asked = new();

    /// <summary>Each wait asked for, in order: "read", "download" and "ended".</summary>
    public IReadOnlyList<string> Asked => [.. _asked];

    /// <inheritdoc />
    public Task BeforeReadAsync(CancellationToken cancellationToken)
    {
        _asked.Enqueue("read");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task BeforeDownloadAsync(CancellationToken cancellationToken)
    {
        _asked.Enqueue("download");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void DownloadEnded() => _asked.Enqueue("ended");
}

/// <summary>
/// A batch's transcripts as a test plays them: each recording asked for is kept; its transcript is written beside it, or the
/// failure set for its video is thrown. Nothing is sent to ElevenLabs.
/// </summary>
public sealed class FakeVideoTranscriber : IVideoTranscriber
{
    private readonly ConcurrentQueue<VideoToTranscribe> _asked = new();

    /// <summary>The failure each video's transcript throws, by video ID.</summary>
    public Dictionary<string, Exception> Failures { get; } = new(StringComparer.Ordinal);

    /// <summary>What the first transcript cost the account, as the usage report would read it; null for no reading.</summary>
    public UsageCharge? FirstCharge { get; set; }

    /// <summary>Runs as each transcript begins, such as to press a key at that moment.</summary>
    public Action<VideoToTranscribe>? OnTranscribe { get; set; }

    /// <summary>The recordings asked for, in order.</summary>
    public IReadOnlyList<VideoToTranscribe> Asked => [.. _asked];

    /// <inheritdoc />
    public Task<VideoTranscribed> TranscribeAsync(VideoToTranscribe video, CancellationToken cancellationToken)
    {
        _asked.Enqueue(video);
        OnTranscribe?.Invoke(video);
        cancellationToken.ThrowIfCancellationRequested();
        if (Failures.TryGetValue(video.Video.VideoId, out var failure)) throw failure;

        File.WriteAllText(video.Files.Transcript(), $"# {video.Video.Title}\n");
        return Task.FromResult(new VideoTranscribed(video.Files.Transcript(), _asked.Count == 1 ? FirstCharge : null));
    }

    /// <summary>A refusal of ElevenLabs with <paramref name="status"/>, as the client throws it once its own tries are spent.</summary>
    public static TranscriptionException Refusal(HttpStatusCode status) => new($"ElevenLabs refused (HTTP {(int)status}).", status);
}

/// <summary>Google Drive's room as a test sets it; none, as when Drive is not signed in, unless the test says.</summary>
public sealed class FakeDriveRoom : IDriveRoom
{
    /// <summary>The room to answer with.</summary>
    public DriveSpace? Space { get; set; }

    /// <summary>How many times Drive was asked.</summary>
    public int Reads { get; private set; }

    /// <inheritdoc />
    public Task<DriveSpace?> ReadAsync(CancellationToken cancellationToken)
    {
        Reads++;
        return Task.FromResult(Space);
    }
}

/// <summary>
/// Everything a download batch's test needs (design 7.2), put together once: a test's own ClassScribe folder with its run
/// files and a clock at the tests' class, a recordings folder, the console, yt-dlp played, its helpers found in winget's
/// links folder on a played disk, the pace without waits, the transcripts played, the account, Drive's part and room, the
/// free space, keep-awake and the desktop, the settings, and the logs. Disposing it deletes the folders.
/// </summary>
public sealed class YouTubeWorld : IDisposable
{
    /// <summary>winget's links folder, as the played disk has it.</summary>
    public const string Links = @"C:\Users\gdala\AppData\Local\Microsoft\WinGet\Links";

    /// <summary>Puts everything together; without <paramref name="withTools"/>, the played disk holds none of yt-dlp's tools.</summary>
    public YouTubeWorld(bool withTools = true)
    {
        Folders = new YouTubeFolders(Runtime.Folder);
        Tools = new FakeToolRunner(Folders, Runtime.Time);
        if (withTools) Files.Add($@"{Links}\yt-dlp.exe", $@"{Links}\deno.exe", $@"{Links}\ffmpeg.exe", $@"{Links}\ffprobe.exe");
        Recording = TestOptions.Create(new RecordingOptions { OutputFolder = Recordings.Path, OpenFolderWhenDone = true });
        ElevenLabs = TestOptions.Create(new ElevenLabsOptions { ApiKey = "dummy-key-for-a-local-test", KeytermsFile = Recordings.File("no-glossary.txt") });
        ClassFolders = new ClassFolders(Recording, Time, Log<ClassFolders>());
        RecorderLog = new RecorderLog(Runtime.Time);
        Logs.AddProvider(new RecorderLogProvider(RecorderLog));
    }

    /// <summary>The window's log, which every line goes to, as the app's provider hands them on, and which writes each recording's .log.</summary>
    public RecorderLog RecorderLog { get; }

    /// <summary>The test's own ClassScribe folder, run files and played processes.</summary>
    public TestRuntime Runtime { get; } = new();

    /// <summary>The clock, at the tests' class until a test moves it, read in US Eastern time.</summary>
    public TimeProvider Time => _time ??= new ZonedClock(Runtime.Time, ZonedTimeProvider.EasternSummer.LocalTimeZone);

    private TimeProvider? _time;

    /// <summary>The recordings folder.</summary>
    public TempFolder Recordings { get; } = new();

    /// <summary>The feature's folders, in the test's ClassScribe folder.</summary>
    public YouTubeFolders Folders { get; }

    /// <summary>yt-dlp and its helpers, played.</summary>
    public FakeToolRunner Tools { get; }

    /// <summary>The disk the tools are looked for on.</summary>
    public FakeFileProbe Files { get; } = new();

    /// <summary>The console.</summary>
    public FakeConsoleUi Ui { get; } = new();

    /// <summary>The pace, without waits.</summary>
    public InstantPace Pace { get; } = new();

    /// <summary>The transcripts, played.</summary>
    public FakeVideoTranscriber Transcriber { get; } = new();

    /// <summary>The account's credits.</summary>
    public FakeElevenLabsAccount Account { get; } = new();

    /// <summary>Google Drive's part.</summary>
    public FakeDriveAutoUpload Drive { get; } = new();

    /// <summary>Google Drive's room.</summary>
    public FakeDriveRoom DriveRoom { get; } = new();

    /// <summary>The free space of every drive.</summary>
    public FakeFreeSpace FreeSpace { get; } = new();

    /// <summary>Keep-awake.</summary>
    public FakePowerRequests Power { get; } = new();

    /// <summary>The desktop, which opens folders.</summary>
    public Mock<IDesktop> Desktop { get; } = new();

    /// <summary>The Recording settings.</summary>
    public TestOptions<RecordingOptions> Recording { get; }

    /// <summary>The ElevenLabs settings, with a dummy key.</summary>
    public TestOptions<ElevenLabsOptions> ElevenLabs { get; }

    /// <summary>The YouTube settings, the course's channel listed.</summary>
    public TestOptions<YouTubeOptions> YouTube { get; } = TestOptions.Create(new YouTubeOptions { CourseChannels = ["UCcoursecoursecoursecour"] });

    /// <summary>The class folders, on the test's clock.</summary>
    public ClassFolders ClassFolders { get; }

    /// <summary>Every log line of every part, in order.</summary>
    public TestLoggerFactory Logs { get; } = new();

    /// <summary>A logger of the batch's parts into <see cref="Logs"/>.</summary>
    public ILogger<T> Log<T>() => new Logger<T>(Logs);

    /// <summary>The batch's transcripts over the played transcriber.</summary>
    public BatchTranscripts Transcripts() => new(Transcriber, Wait(), ClassFolders, Runtime.Journal, Log<BatchTranscripts>());

    /// <summary>The wait while a class records, over the test's run files.</summary>
    public ClassWait Wait() => new(Runtime.Journal, Runtime.Time, Log<ClassWait>());

    /// <summary>The batch, put together.</summary>
    public DownloadBatch Batch()
    {
        var usage = new UsageReport(Account, Time, Log<UsageReport>());
        var batch = new YouTubeBatch(new YouTubeLock(Folders, Log<YouTubeLock>()), Wait(), Transcripts(), Account, usage, Power, new FakePowerSource(), Ui, Desktop.Object,
            Recording, ElevenLabs, Runtime.Time, Log<YouTubeBatch>());
        var locator = new ToolLocator(new FakeRegistryValues(), Files, YouTube, Log<ToolLocator>()) { LocalAppData = @"C:\Users\gdala\AppData\Local" };
        return new DownloadBatch(batch, new YtDlpCheck(locator, Tools, Folders, Time, Log<YtDlpCheck>()), Folders, Tools, Pace,
            new DownloadedAudio(Folders, Recording, Runtime.Time, Log<DownloadedAudio>()), ClassFolders, RecorderLog, DriveRoom, Drive, FreeSpace,
            new KeytermLoader(new SettingsPlaces(AppContext.BaseDirectory, userFolder: null), NullLogger<KeytermLoader>.Instance), Ui, YouTube, ElevenLabs, Time, Log<DownloadBatch>());
    }

    /// <summary>A request for <paramref name="links"/>, downloaded into the course Risala, going ahead with --yes unless told otherwise.</summary>
    public YouTubeRequest Request(string[] links, BatchConfirmation confirmation = BatchConfirmation.Yes, bool transcribe = true, bool again = false, bool allowUnlisted = false) =>
        new(LinkList.Read(links.Select(link => new LinkWord(link, "the command line"))).Links, "Risala", Recordings.Path, Download: true, confirmation, transcribe,
            Open: true, again, allowUnlisted);

    /// <summary>Runs the batch for <paramref name="request"/>, within a minute, and waits for its lines to reach the recordings' logs.</summary>
    public async Task<int> RunAsync(YouTubeRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            return await Batch().RunAsync(request, cancellationToken).WaitAsync(TimeSpan.FromMinutes(1));
        }
        finally
        {
            RecorderLog.Flush(TimeSpan.FromSeconds(10));
        }
    }

    /// <summary>The lines the console showed.</summary>
    public string[] Screen => Ui.Screen.Split(Environment.NewLine);

    /// <summary>The batch's date folder, the one date folder under Risala.</summary>
    public string DateFolder => Directory.GetDirectories(Path.Combine(Recordings.Path, "Risala")).Single();

    /// <summary>Closes the log, so that it lets go of any file it writes, and deletes the folders.</summary>
    public void Dispose()
    {
        RecorderLog.Dispose();
        Recordings.Dispose();
        Runtime.Dispose();
    }
}
