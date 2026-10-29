using System.Net;
using ClassScribe.Recorder;
using ClassScribe.Settings;
using ClassScribe.Storage;
using ClassScribe.YouTube;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Refit;

namespace ClassScribe.Tests;

/// <summary>
/// The contracts of the YouTube ports (architecture 4.3, slice 3c) over their real adapters and the existing fakes, in
/// temporary folders: yt-dlp as a batch uses it (<see cref="IVideoTool"/>, over the played runner and disk), the titles
/// (<see cref="IVideoTitles"/>, over a stub at a stub address), the videos fetched before (<see cref="IVideoIndex"/>), a
/// video's source file (<see cref="IVideoSources"/>), a downloaded video's sound (<see cref="IDownloadedAudio"/>), the video
/// window (<see cref="IVideoWindowOpener"/>, over the fake browsers), Chrome's whereabouts (<see cref="IToolLocator"/>), one
/// batch at a time (<see cref="IYouTubeLock"/>) and the batch's folders (<see cref="IYouTubeFolders"/>); and that both hosts
/// resolve each port to its adapter. Nothing starts a tool or a browser, and nothing reaches YouTube.
/// </summary>
[TestFixture]
public class YouTubePortTests
{
    private const string VideoId = "abcdefghijk";
    private const string PlaylistId = "PLabcdefghijklmnopqrstuvwxyz012345";

    private YouTubeWorld _world = null!;
    private TempFolder _temp = null!;

    /// <summary>A played download world (its runner, disk and folders in temporary places) and a temporary folder of the test's own.</summary>
    [SetUp]
    public void SetUp()
    {
        _world = new YouTubeWorld();
        _temp = new TempFolder();
    }

    /// <summary>Deletes the folders.</summary>
    [TearDown]
    public void TearDown()
    {
        _world.Dispose();
        _temp.Dispose();
    }

    /// <summary>
    /// The tool through its port: no call before the check has found yt-dlp; after it, the three calls name yt-dlp's path, the
    /// folder each runs in and arguments that start with the fixed set and end with the link, the download loading the
    /// information file; a playlist's listing read to its videos, null entries left out; the information file seen and read
    /// as the batch's record once the read wrote it; the finished WAV only once done.txt names a .wav in the staging folder;
    /// and a failure explained.
    /// </summary>
    [Test]
    public async Task The_tool_builds_the_calls_after_its_check_and_reads_what_yt_dlp_wrote()
    {
        IVideoTool tool = _world.Tool();
        var staging = _world.Folders.StagingOf(VideoId);
        Assert.That(() => tool.ReadVideo(VideoId), Throws.InvalidOperationException.With.Message.Contains("known only once the check"));

        var found = await tool.CheckAsync(CancellationToken.None);
        var playlist = tool.ReadPlaylist(PlaylistId);
        var read = tool.ReadVideo(VideoId);
        var download = tool.Download(VideoId, fromInfoFile: true);
        var listed = tool.Playlist([$$"""{"id":"{{PlaylistId}}","title":"Series","entries":[{"id":"abcdefghijk","title":"One"},null,{"id":"bcdefghijkl","title":"Two"}]}"""], PlaylistId);
        var noInfo = tool.HasInfo(VideoId);
        var nothingRead = await tool.ReadInfoAsync(VideoId, CancellationToken.None);
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, $"{VideoId}.info.json"), YtDlpReadTests.Info(VideoId, "A title", duration: 60));
        var info = await tool.ReadInfoAsync(VideoId, CancellationToken.None);
        var noWav = tool.StagedWav(VideoId);
        var wav = Path.Combine(staging, $"{VideoId}.wav");
        File.WriteAllBytes(wav, [0]);
        File.WriteAllText(Path.Combine(staging, YtDlpArguments.DoneFileName), wav + Environment.NewLine);
        var failure = tool.Classify(1, ["ERROR: Sign in to confirm you're not a bot."]);

        Assert.Multiple(() =>
        {
            Assert.That((found.Paths?.YtDlp, found.YtDlpVersion), Is.EqualTo(($@"{YouTubeWorld.Links}\yt-dlp.exe", _world.Tools.YtDlpVersion)));
            Assert.That((playlist.Program, playlist.WorkingFolder), Is.EqualTo((found.Paths!.YtDlp, _world.Folders.Root)));
            Assert.That(playlist.Arguments.Take(3), Is.EqualTo(new[] { "--ignore-config", "--no-cookies", "--no-cookies-from-browser" }));
            Assert.That(playlist.Arguments[^1], Is.EqualTo(YouTubeLinks.PlaylistUrl(PlaylistId)));
            Assert.That((read.Program, read.WorkingFolder, read.Arguments[^1]), Is.EqualTo((found.Paths.YtDlp, staging, YouTubeLinks.WatchUrl(VideoId))));
            Assert.That((download.WorkingFolder, download.Arguments.Contains("--load-info-json")), Is.EqualTo((staging, true)));
            Assert.That((listed.Id, listed.Title), Is.EqualTo((PlaylistId, "Series")));
            Assert.That(listed.Entries, Is.EqualTo(new[] { new ListedVideo("abcdefghijk", "One"), new ListedVideo("bcdefghijkl", "Two") }));
            Assert.That((noInfo, nothingRead), Is.EqualTo((false, (VideoInfo?)null)));
            Assert.That(tool.HasInfo(VideoId), Is.True);
            Assert.That((info?.Id, info?.Title, info?.Length, info?.Channel, info?.Handle, info?.Uploaded, info?.SourceRate),
                Is.EqualTo((VideoId, "A title", TimeSpan.FromSeconds(60), "Your Channel", "@yourchannel", "2025-03-03", 44100)));
            Assert.That((noWav, tool.StagedWav(VideoId)), Is.EqualTo(((string?)null, wav)));
            Assert.That((failure.Kind, failure.StopsFetching), Is.EqualTo((DownloadFailureKind.BotCheck, true)));
        });
    }

    /// <summary>The titles through the port: a video's title, channel and handle from YouTube's answer at a stub address; none for a video YouTube does not answer for.</summary>
    [Test]
    public async Task The_titles_read_youtubes_answer_and_give_none_for_a_video_it_does_not_answer_for()
    {
        var stub = new StubHttpHandler();
        stub.EnqueueFor("/oembed", HttpStatusCode.OK, """{"title":"A title","author_name":"Your Channel","author_url":"https://www.youtube.com/@yourchannel"}""");
        stub.EnqueueFor("/oembed", HttpStatusCode.NotFound, "");
        var http = new HttpClient(new YouTubeHostGuard { InnerHandler = stub }) { BaseAddress = new Uri("http://127.0.0.1:5005/") };
        IVideoTitles titles = new OEmbedTitles(RestService.ForGenerated<IYouTubeOEmbedApi>(http, YouTubeJson.Default, OEmbedTitles.RefitSettings()), TimeProvider.System, NullLogger<OEmbedTitles>.Instance);

        var title = await titles.ReadAsync(VideoId, CancellationToken.None);
        var none = await titles.ReadAsync("bcdefghijkl", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(title, Is.EqualTo(new VideoTitle("A title", "Your Channel", "@yourchannel", "https://www.youtube.com/@yourchannel")));
            Assert.That(none, Is.Null);
        });
    }

    /// <summary>
    /// The index through the port: read once for the recordings folder, it finds a video's recording by its ID, judges one
    /// transcribed whole as done, fetches it again with --again, and knows nothing of a video never fetched.
    /// </summary>
    [Test]
    public void The_index_finds_the_videos_fetched_before_by_their_source_files()
    {
        var files = RecordingFiles.ForNew(Directory.CreateDirectory(Path.Combine(_world.Recordings.Path, "Risala", "2026-10-17")).FullName, "Risala", TestRuns.Start);
        File.WriteAllBytes(files.Segment(1), [0]);
        File.WriteAllText(files.Transcript(), "# A title");
        ((IVideoSources)new VideoSourceFile()).Write(files.Source, Source(VideoId, "Downloaded", endConfirmed: true));
        IVideoIndex index = new VideoIndex(_world.ClassFolders);

        index.Read(_world.Recordings.Path, NullLogger.Instance);

        Assert.Multiple(() =>
        {
            Assert.That(index.Of(VideoId).Select(past => past.Files), Is.EqualTo(new[] { files }));
            Assert.That(index.Judge(VideoId, again: false, TimeZoneInfo.Utc).Kind, Is.EqualTo(PastKind.Transcribed));
            Assert.That(index.Judge(VideoId, again: true, TimeZoneInfo.Utc).Kind, Is.EqualTo(PastKind.None));
            Assert.That(index.Judge("bcdefghijkl", again: false, TimeZoneInfo.Utc), Is.EqualTo(new PastVerdict(PastKind.None, null, "")));
        });
    }

    /// <summary>The source files through the port: written whole and read back the same; none where there is no file; a file that is not a source refused by name.</summary>
    [Test]
    public void A_source_file_is_written_and_read_back()
    {
        IVideoSources sources = new VideoSourceFile();
        var path = _temp.File("Risala.source.json");
        var source = Source(VideoId, "Downloaded", endConfirmed: true);

        sources.Write(path, source);
        File.WriteAllText(_temp.File("other.source.json"), """{"kind":"other"}""");

        Assert.Multiple(() =>
        {
            Assert.That(sources.Read(path), Is.EqualTo(source));
            Assert.That(sources.Read(_temp.File("missing.source.json")), Is.Null);
            Assert.That(() => sources.Read(_temp.File("other.source.json")), Throws.InstanceOf<InvalidDataException>().With.Message.Contains("other.source.json"));
        });
    }

    /// <summary>
    /// A downloaded video's sound through the port: a staged WAV of a second becomes the recording's first part, its length
    /// given; one that is not as long as the video is refused, with nothing kept; and the clean-up with nothing to clean
    /// up changes nothing.
    /// </summary>
    [Test]
    public void A_downloaded_videos_sound_becomes_the_recordings_part_only_when_it_is_as_long_as_the_video()
    {
        IDownloadedAudio audio = new DownloadedAudio(_world.Folders, _world.Recording, _world.Runtime.Time, NullLogger<DownloadedAudio>.Instance);
        var staged = Path.Combine(Directory.CreateDirectory(_world.Folders.StagingOf(VideoId)).FullName, $"{VideoId}.wav");
        TestAudio.WriteWav(staged, 8000, new short[8000]);
        var files = RecordingFiles.ForNew(_temp.Path, "Risala", TestRuns.Start);

        var length = audio.Convert(staged, files, TimeSpan.FromSeconds(1), "A title", CancellationToken.None);
        var tooShort = Assert.Throws<IncompleteAudioException>(() => audio.Convert(staged, RecordingFiles.ForNew(_temp.Path, "Other", TestRuns.Start), TimeSpan.FromMinutes(1), "A title", CancellationToken.None));
        audio.CleanUp();

        Assert.Multiple(() =>
        {
            Assert.That(length, Is.EqualTo(TimeSpan.FromSeconds(1)));
            Assert.That(File.Exists(files.Segment(1)), Is.True);
            Assert.That(tooShort!.Message, Does.Contain("the download is incomplete"));
            Assert.That(Directory.GetFiles(_temp.Path, "Other*"), Is.Empty, "nothing of the refused audio is kept");
            Assert.That(File.Exists(staged), Is.True, "the staged file is left where it is");
        });
    }

    /// <summary>
    /// The video window through the port, over the fake browsers: a window once Chrome stood 3 seconds, closed on request; a
    /// Chrome that ends at once handed the video to a window left open, which Enter tries again; a Chrome Windows will not
    /// start is said.
    /// </summary>
    [Test]
    public async Task The_video_window_opens_closes_and_says_why_it_did_not_open()
    {
        var starter = new FakeProcessStarter(_world.Runtime.Time);
        var browsers = new FakeVideoBrowsers(starter, _world.Runtime.Processes);
        IVideoWindowOpener windows = new VideoWindow(starter, browsers.Windows, _world.Folders, _world.Runtime.Time, NullLogger<VideoWindow>.Instance);
        const string chrome = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
        var link = YouTubeLinks.WatchUrl(VideoId);

        var opening = windows.OpenAsync(chrome, link, CancellationToken.None);
        await _world.Runtime.Time.AdvanceUntilAsync(() => opening.IsCompleted, TimeSpan.FromMilliseconds(500), atMost: TimeSpan.FromSeconds(5));
        var opened = await opening;
        var closed = await windows.CloseAsync(opened.Window!, CancellationToken.None);
        starter.BrowsersEndAtOnceWith = 0;
        var handedOff = windows.OpenAsync(chrome, link, CancellationToken.None);
        await _world.Runtime.Time.AdvanceUntilAsync(() => handedOff.IsCompleted, TimeSpan.FromMilliseconds(500), atMost: TimeSpan.FromSeconds(5));
        starter.RefuseBrowsersWith = 2;
        var refused = await windows.OpenAsync(chrome, link, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(opened.Window, Is.Not.Null);
            Assert.That(closed, Is.True);
            Assert.That((handedOff.Result.Window, handedOff.Result.HandedOff, handedOff.Result.Problem), Is.EqualTo(((OpenedWindow?)null, true, VideoWindow.HandedOff)));
            Assert.That((refused.Window, refused.HandedOff), Is.EqualTo(((OpenedWindow?)null, false)));
            Assert.That(refused.Problem, Does.StartWith("The video window did not open: "));
        });
    }

    /// <summary>Chrome's whereabouts through the port, over the played disk: found in Program Files with its version, and nowhere when the disk has no Chrome.</summary>
    [Test]
    public void The_locator_finds_chrome_and_its_version_or_says_it_is_nowhere()
    {
        const string chrome = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
        var disk = new FakeFileProbe();
        IToolLocator locator = new BrowserLocator(new FakeRegistryValues(), disk, _world.YouTube, NullLogger<BrowserLocator>.Instance)
        {
            ProgramFiles = @"C:\Program Files", ProgramFilesX86 = @"C:\Program Files (x86)", LocalAppData = @"C:\Users\gdala\AppData\Local"
        };

        var nowhere = locator.FindBrowser();
        disk.Add(chrome);
        disk.Versions[chrome] = "141.0.7390.108";

        Assert.Multiple(() =>
        {
            Assert.That(nowhere, Is.Null);
            Assert.That(locator.FindBrowser(), Is.EqualTo(chrome));
            Assert.That(locator.BrowserVersionOf(chrome), Is.EqualTo("Chrome 141.0.7390.108"));
            Assert.That(locator.BrowserVersionOf(@"C:\nowhere\chrome.exe"), Is.EqualTo("Chrome"));
            Assert.That(IToolLocator.BrowserNotFound, Does.StartWith("Chrome was not found"));
        });
    }

    /// <summary>The batch lock through the port: taken once with this process named as its holder to a second taker, and free again once let go.</summary>
    [Test]
    public void The_batch_lock_is_held_by_one_at_a_time_and_names_its_holder()
    {
        IYouTubeLock first = new YouTubeLock(_world.Folders, NullLogger<YouTubeLock>.Instance);
        IYouTubeLock second = new YouTubeLock(_world.Folders, NullLogger<YouTubeLock>.Instance);

        using (var held = first.TryTake(out _))
        {
            Assert.That(held, Is.Not.Null);
            Assert.That(second.TryTake(out var holder), Is.Null, "held by the first");
            Assert.That(holder, Is.EqualTo(Environment.ProcessId));
        }

        using var again = second.TryTake(out _);
        Assert.That(again, Is.Not.Null, "free once the first let go");
    }

    /// <summary>The batch's folders through the port: each under ClassScribe's own folder, made when asked, a video's staging folder deleted with its files, and anything but a video's ID refused.</summary>
    [Test]
    public void The_folders_are_named_made_and_deleted_under_classscribes_own_folder()
    {
        IYouTubeFolders folders = new YouTubeFolders(_world.Runtime.Folder);
        var root = _world.Runtime.Folder.Root;

        folders.MakeRoot();
        var staging = folders.MakeStaging(VideoId);
        File.WriteAllText(Path.Combine(staging, "a.txt"), "a");
        var made = Directory.Exists(staging);
        folders.DeleteStaging(VideoId);

        Assert.Multiple(() =>
        {
            Assert.That((folders.Root, folders.Staging, folders.StagingOf(VideoId)), Is.EqualTo((Path.Combine(root, "youtube"), Path.Combine(root, "youtube", "staging"), staging)));
            Assert.That(Directory.Exists(folders.Root), Is.True);
            Assert.That(made, Is.True);
            Assert.That(Directory.Exists(staging), Is.False);
            Assert.That(() => folders.StagingOf("not an id"), Throws.ArgumentException);
        });
    }

    /// <summary>Both hosts resolve each YouTube port to the adapter they hold, the batch's parts in a scope of their own.</summary>
    [Test]
    public void Both_hosts_resolve_the_ports_to_the_adapters_they_hold()
    {
        File.WriteAllText(_temp.File(SettingsFile.FileName), "{}");
        var places = new SettingsPlaces(_temp.Path, userFolder: null);
        var runtime = _world.Runtime;
        using var window = AppHost.Build(places, "test-key", services =>
            services.AddSingleton(runtime.Folder).AddSingleton(new RoamingFolder(_temp.File("Roaming"))));
        var runId = TestRuns.Id();
        using var log = new RecorderLog(runtime.Time);
        using var recorder = AppHost.BuildRecorder(new RecorderStart(runId, TestRuns.Request(runId), new Dictionary<string, string?>()), "test-key", log, runtime.Folder, places, runtime.Time,
            services => services.AddSingleton(new RoamingFolder(_temp.File("Roaming"))));

        Assert.Multiple(() =>
        {
            foreach (var (name, host) in new[] { ("the window", window.Services), ("the recorder", recorder.Services) })
            {
                using var scope = host.CreateScope();
                var services = scope.ServiceProvider;
                Assert.That(services.GetRequiredService<IYouTubeFolders>(), Is.SameAs(services.GetRequiredService<YouTubeFolders>()), $"{name}: the folders");
                Assert.That(services.GetRequiredService<IYouTubeLock>(), Is.SameAs(services.GetRequiredService<YouTubeLock>()), $"{name}: the lock");
                Assert.That(services.GetRequiredService<IToolLocator>(), Is.SameAs(services.GetRequiredService<BrowserLocator>()), $"{name}: Chrome's locator");
                Assert.That(services.GetRequiredService<IVideoWindowOpener>(), Is.TypeOf<VideoWindow>(), $"{name}: the window");
                Assert.That(services.GetRequiredService<IVideoTool>(), Is.SameAs(services.GetRequiredService<IVideoTool>()).And.TypeOf<YtDlpTool>(), $"{name}: the tool, one per batch");
                Assert.That(services.GetRequiredService<IDownloadedAudio>(), Is.SameAs(services.GetRequiredService<DownloadedAudio>()), $"{name}: the downloaded audio");
                Assert.That(services.GetRequiredService<IVideoTitles>(), Is.SameAs(services.GetRequiredService<OEmbedTitles>()), $"{name}: the titles");
                Assert.That(services.GetRequiredService<IVideoIndex>(), Is.SameAs(services.GetRequiredService<IVideoIndex>()).And.TypeOf<VideoIndex>(), $"{name}: the index, one per batch");
                Assert.That(services.GetRequiredService<IVideoSources>(), Is.TypeOf<VideoSourceFile>(), $"{name}: the source files");
            }
        });
    }

    /// <summary>A video's source, as a download of <paramref name="videoId"/> leaves it.</summary>
    private static VideoSource Source(string videoId, string ended, bool endConfirmed) =>
        new(VideoSource.YouTubeKind, YouTubeLinks.WatchUrl(videoId), videoId, "A title", "Your Channel", "@yourchannel", null, null, "00:01:00", "public", null, VideoSource.Downloaded,
            "yt-dlp 2026.10.10", TestRuns.Start, "You typed YES for the batch", VideoSource.ByYes, ended, endConfirmed);
}
