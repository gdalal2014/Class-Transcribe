using System.Net;
using ClassScribe.Commands;
using ClassScribe.Desktop;
using ClassScribe.Drive;
using ClassScribe.Storage;
using ClassScribe.Transcription;
using ClassScribe.YouTube;
using Moq;

namespace ClassScribe.Tests;

/// <summary>
/// A batch on the download path (design 1.4, 3 and 2.6 to 2.10, with Gulzar's answers 4 to 8): the reads, the plan, the
/// confirmation, the downloads into one date folder of the course, the source files, the transcripts, the summary and the
/// exit code, over yt-dlp played, the transcripts played, and a test's own folders. Nothing reaches YouTube, ElevenLabs or
/// Google.
/// </summary>
[TestFixture]
public class DownloadBatchTests
{
    private const string First = "abcdefghijk";
    private const string Second = "bcdefghijkl";
    private const string Third = "cdefghijklm";
    private YouTubeWorld _world = null!;

    /// <summary>A world with two public videos of three seconds each.</summary>
    [SetUp]
    public void SetUp()
    {
        _world = new YouTubeWorld();
        _world.Tools.With(First, YtDlpReadTests.Info(First, "The creed, part 1", duration: 3), 3)
            .With(Second, YtDlpReadTests.Info(Second, "The creed, part 2", duration: 3), 3);
    }

    /// <summary>Deletes the world's folders.</summary>
    [TearDown]
    public void TearDown() => _world.Dispose();

    /// <summary>The link of video <paramref name="id"/>.</summary>
    private static string Link(string id) => YouTubeLinks.WatchUrl(id);

    /// <summary>The recordings the batch made, by their video's ID, oldest first.</summary>
    private IReadOnlyList<(string VideoId, RecordingFiles Files, VideoSource Source)> Made() =>
    [
        .. Directory.GetFiles(_world.DateFolder, "*.source.json")
            .Select(path => (Files: RecordingFiles.TryParse(path)!, Source: VideoSourceFile.Read(path)!))
            .OrderBy(made => made.Files.BaseName, StringComparer.Ordinal)
            .Select(made => (made.Source.VideoId, made.Files, made.Source))
    ];

    /// <summary>
    /// Two public videos with --yes: each read, the plan shown, each downloaded in turn into one date folder of the course as
    /// an ordinary recording at YouTube's own rate, with its source file and its log, then transcribed in order; the laptop
    /// kept awake throughout, the course folder opened once, the staging gone; exit code 0.
    /// </summary>
    [Test]
    public async Task Two_public_videos_are_read_planned_downloaded_into_one_date_folder_and_transcribed()
    {
        var exitCode = await _world.RunAsync(_world.Request([Link(First), Link(Second)]));

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(_world.Tools.Reads, Is.EqualTo([First, Second]));
        Assert.That(_world.Tools.Downloads, Is.EqualTo([First, Second]));
        Assert.That(_world.Screen, Does.Contain("Read 2 videos from 2 links:"));
        Assert.That(_world.Screen.Any(line => line.Contains("The creed, part 1", StringComparison.Ordinal) && line.EndsWith("ready", StringComparison.Ordinal)), Is.True);

        var made = Made();
        Assert.That(made.Select(item => item.VideoId), Is.EqualTo([First, Second]));
        Assert.That(made.Select(item => item.Files.Folder).Distinct(), Is.EqualTo([Path.Combine(_world.Recordings.Path, "Risala", "2026-10-15")]));
        foreach (var (_, files, source) in made)
        {
            Assert.That(ClassFolders.MeasureSegment(files.Segment(1)), Is.EqualTo(TimeSpan.FromSeconds(3)));
            Assert.That((source.Obtained, source.Ended, source.EndConfirmed, source.AcceptedBy), Is.EqualTo((VideoSource.Downloaded, "Downloaded", true, VideoSource.ByYes)));
            Assert.That(source.Confirmed, Does.StartWith("--yes on the command line"));
            Assert.That(source.Tool, Is.EqualTo("yt-dlp 2026.10.10"));
            Assert.That(File.ReadAllText(files.Log), Does.Contain($"Downloading").And.Contain(source.Title));
        }

        Assert.That(_world.Transcriber.Asked.Select(asked => asked.Video.VideoId), Is.EqualTo([First, Second]));
        Assert.That(_world.Power.Holds.Single().Reason, Is.EqualTo(StayAwake.VideosReason));
        Assert.That(_world.Power.Holding, Is.False);
        _world.Desktop.Verify(desktop => desktop.OpenFolder(Path.Combine(_world.Recordings.Path, "Risala")), Times.Once);
        Assert.That(Directory.Exists(_world.Folders.Staging) ? Directory.GetDirectories(_world.Folders.Staging) : [], Is.Empty);
        Assert.That(_world.Screen, Does.Contain($"  01  The creed, part 1: downloaded; transcribed: {made[0].Files.Transcript()}"));
    }

    /// <summary>--plan prints the plan, reads included, and stops: nothing is downloaded, no recording is made, nothing is transcribed; exit code 0.</summary>
    [Test]
    public async Task Plan_prints_the_plan_and_fetches_nothing()
    {
        var exitCode = await _world.RunAsync(_world.Request([Link(First), Link(Second)], BatchConfirmation.PlanOnly));

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(_world.Tools.Downloads, Is.Empty);
        Assert.That(Directory.Exists(Path.Combine(_world.Recordings.Path, "Risala")), Is.False);
        Assert.That(_world.Transcriber.Asked, Is.Empty);
        Assert.That(_world.Screen, Has.Some.EqualTo("Ready: 2 videos, 1 min: less than a cent to transcribe ($0.22 an hour)."));
    }

    /// <summary>A course's channel is never downloaded, --allow-unlisted or not; the plan says so, and the exit code is 1.</summary>
    [Test]
    public async Task A_courses_channel_is_never_downloaded()
    {
        _world.Tools.With(Third, YtDlpReadTests.Info(Third, "A lesson", duration: 3, availability: "unlisted", channelId: "UCcoursecoursecoursecour"), 3);

        var exitCode = await _world.RunAsync(_world.Request([Link(Third), Link(First)], allowUnlisted: true));

        Assert.That(exitCode, Is.EqualTo(1));
        Assert.That(_world.Tools.Downloads, Is.EqualTo([First]));
        Assert.That(_world.Screen, Has.Some.Contains("A lesson").And.Some.EndsWith("your course's channel: never downloaded"));
    }

    /// <summary>
    /// On the command line an unlisted video needs a word: with --yes alone it is left out, the summary says how to give
    /// the word, and the exit code is 1; with --allow-unlisted it is downloaded, the plan names it as accepted by the flag,
    /// and its source file keeps that.
    /// </summary>
    [Test]
    public async Task An_unlisted_video_needs_a_word_which_allow_unlisted_gives()
    {
        _world.Tools.With(Third, YtDlpReadTests.Info(Third, "Your own upload", duration: 3, availability: "unlisted"), 3);

        var withoutWord = await _world.RunAsync(_world.Request([Link(Third)]));

        Assert.That(withoutWord, Is.EqualTo(1));
        Assert.That(_world.Tools.Downloads, Is.Empty);
        Assert.That(_world.Screen, Has.Some.EndsWith("needs your word"));
        var wordLine = _world.Screen.Single(line => line.Contains("Your own upload: needs your word:", StringComparison.Ordinal));
        Assert.That(wordLine, Does.Contain("needs your word: in the menu (Y > D), or add --allow-unlisted").And.Contain("; or record it as it plays: ")
            .And.Contain(" youtube \"https://www.youtube.com/watch?v=cdefghijklm\" --name \"Risala\""));

        var withWord = await _world.RunAsync(_world.Request([Link(Third)], allowUnlisted: true));

        Assert.That(withWord, Is.EqualTo(0));
        Assert.That(_world.Screen, Has.Some.EndsWith("ready (--allow-unlisted)"));
        var source = Made().Single().Source;
        Assert.That(source.AcceptedBy, Is.EqualTo(VideoSource.ByAllowUnlisted));
        Assert.That(source.Confirmed, Does.StartWith("--allow-unlisted on the command line"));
    }

    /// <summary>
    /// In the menu, each video that needs a word is asked about: Y takes it on his word, kept in its source file; N leaves it
    /// out. Then YES, typed in full, goes ahead.
    /// </summary>
    [Test]
    public async Task The_menu_asks_each_video_that_needs_a_word_then_YES()
    {
        _world.Tools.With(Third, YtDlpReadTests.Info(Third, "Unlisted one", duration: 3, availability: "unlisted"), 3)
            .With("defghijklmn", YtDlpReadTests.Info("defghijklmn", "Unlisted two", duration: 3, availability: null), 3);
        _world.Ui.Type('y', 'n');
        _world.Ui.TypeLine("YES");

        var exitCode = await _world.RunAsync(_world.Request([Link(Third), Link("defghijklmn"), Link(First)], BatchConfirmation.Ask));

        Assert.That(_world.Tools.Downloads, Is.EqualTo([Third, First]));
        Assert.That(Made().Single(item => item.VideoId == Third).Source.AcceptedBy, Is.EqualTo(VideoSource.ByWord));
        Assert.That(Made().Single(item => item.VideoId == Third).Source.Confirmed, Does.StartWith("You said you uploaded this video or its owner allows downloading"));
        Assert.That(Made().Single(item => item.VideoId == First).Source.Confirmed, Does.StartWith("You typed YES for the batch"));
        Assert.That(_world.Screen, Has.Some.Contains("Did you upload it, or does its owner allow you"));
        Assert.That(_world.Screen, Has.Some.EqualTo("Type YES and press Enter to go ahead; Enter alone goes back."));
        Assert.That(exitCode, Is.EqualTo(1), "the video left out by N makes the exit code 1");
    }

    /// <summary>In the menu, Enter alone at YES goes back: nothing is fetched or sent, and the exit code is 0.</summary>
    [Test]
    public async Task Enter_alone_at_YES_goes_back()
    {
        _world.Ui.TypeLine("");

        var exitCode = await _world.RunAsync(_world.Request([Link(First)], BatchConfirmation.Ask));

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(_world.Tools.Downloads, Is.Empty);
        Assert.That(_world.Transcriber.Asked, Is.Empty);
    }

    /// <summary>
    /// What was fetched before is not fetched again: a video transcribed before is left out; one fetched whole without a
    /// transcript is only transcribed; --again fetches both again as new recordings.
    /// </summary>
    [Test]
    public async Task What_was_fetched_before_is_skipped_or_only_transcribed_unless_again()
    {
        await _world.RunAsync(_world.Request([Link(First), Link(Second)], transcribe: false));
        var before = Made();
        File.WriteAllText(before[0].Files.Transcript(), "# done");
        _world.Runtime.Time.Advance(TimeSpan.FromMinutes(5));

        var exitCode = await _world.RunAsync(_world.Request([Link(First), Link(Second)]));

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(_world.Tools.Downloads, Is.EqualTo([First, Second]), "nothing more was downloaded");
        Assert.That(_world.Screen, Has.Some.Contains("already transcribed on"));
        Assert.That(_world.Screen, Has.Some.EndsWith($"fetched before, not transcribed yet: {before[1].Files.BaseName}"));
        Assert.That(_world.Transcriber.Asked.Select(asked => asked.Files), Is.EqualTo([before[1].Files]));

        _world.Runtime.Time.Advance(TimeSpan.FromMinutes(5));
        await _world.RunAsync(_world.Request([Link(First), Link(Second)], again: true, transcribe: false));

        Assert.That(_world.Tools.Downloads, Is.EqualTo([First, Second, First, Second]));
    }

    /// <summary>A video whose earlier fetch left no audio, or one that may be cut short, is fetched again as a new recording; the old one stays.</summary>
    [Test]
    public async Task An_unfinished_fetch_is_fetched_again()
    {
        var old = RecordingFiles.ForNew(Path.Combine(_world.Recordings.Path, "Risala", "2026-10-14"), "Risala", new DateTimeOffset(2026, 10, 14, 23, 0, 0, TimeSpan.Zero));
        Directory.CreateDirectory(old.Folder);
        VideoSourceFile.Write(old.Source, VideoSourceFileTests.Downloaded(First) with { Ended = "Downloading", EndConfirmed = false });

        await _world.RunAsync(_world.Request([Link(First)]));

        Assert.That(_world.Tools.Downloads, Is.EqualTo([First]));
        Assert.That(_world.Screen, Has.Some.Contains("but it holds no audio: fetched again"));
        Assert.That(File.Exists(old.Source), Is.True);
    }

    /// <summary>Without the ElevenLabs key, a batch that transcribes is refused before anything is asked of YouTube; with --no-transcribe it goes on.</summary>
    [Test]
    public async Task Without_a_key_a_batch_that_transcribes_is_refused_before_anything()
    {
        _world.ElevenLabs.Set(new Settings.ElevenLabsOptions { ApiKey = "" });

        var exitCode = await _world.RunAsync(_world.Request([Link(First)]));

        Assert.That(exitCode, Is.EqualTo(1));
        Assert.That(_world.Tools.Calls, Is.Empty);
        Assert.That(_world.Logs.Problems.Single(), Does.StartWith("No ElevenLabs API key found in ELEVENLABS_API_KEY").And.Contain("--no-transcribe"));
        Assert.That(await _world.RunAsync(_world.Request([Link(First)], transcribe: false)), Is.EqualTo(0));
    }

    /// <summary>
    /// The plan gives the account's credits, read once; after the first transcript, one line projects the rest in the
    /// account's own credits.
    /// </summary>
    [Test]
    public async Task The_plan_gives_the_accounts_credits_and_the_first_transcript_projects_the_rest()
    {
        _world.Account.Answer(FakeElevenLabsAccount.Read(43_362));
        _world.Transcriber.FirstCharge = new UsageCharge(50, 56_588);

        await _world.RunAsync(_world.Request([Link(First), Link(Second)]));

        Assert.That(_world.Screen, Has.Some.StartsWith("ElevenLabs account: 43,362 of 100,000 credits used this period; 56,638 left"));
        Assert.That(_world.Logs.Messages, Has.Some.EqualTo("The first transcript used 50 credits for 00:00:03; the other 1 (1 min) would use about 50 at that rate; 56,588 are left this period"));
    }

    /// <summary>
    /// ElevenLabs refusing the credits (402) stops the transcripts: the rest are not tried, the summary says so, and the
    /// audio is kept for the same command later; exit code 1.
    /// </summary>
    [TestCase(HttpStatusCode.PaymentRequired)]
    [TestCase(HttpStatusCode.Unauthorized)]
    [TestCase(HttpStatusCode.Forbidden)]
    [TestCase(HttpStatusCode.TooManyRequests)]
    public async Task ElevenLabs_refusing_stops_the_transcripts(HttpStatusCode status)
    {
        _world.Transcriber.Failures[First] = FakeVideoTranscriber.Refusal(status);

        var exitCode = await _world.RunAsync(_world.Request([Link(First), Link(Second)]));

        Assert.That(exitCode, Is.EqualTo(1));
        Assert.That(_world.Transcriber.Asked.Select(asked => asked.Video.VideoId), Is.EqualTo([First]));
        Assert.That(_world.Logs.Problems, Has.Some.StartsWith("The transcripts stopped:"));
        Assert.That(_world.Screen, Has.Some.Contains("The creed, part 2: downloaded; not transcribed"));
        Assert.That(Made().Select(item => File.Exists(item.Files.Segment(1))), Is.All.True, "the audio is kept");
    }

    /// <summary>Any other transcript's failure is that video's alone: the next is transcribed.</summary>
    [Test]
    public async Task Another_transcripts_failure_is_that_videos_alone()
    {
        _world.Transcriber.Failures[First] = new TranscriptionException("ElevenLabs could not transcribe the file. (HTTP 500)", HttpStatusCode.InternalServerError);

        var exitCode = await _world.RunAsync(_world.Request([Link(First), Link(Second)]));

        Assert.That(exitCode, Is.EqualTo(1));
        Assert.That(_world.Transcriber.Asked.Select(asked => asked.Video.VideoId), Is.EqualTo([First, Second]));
    }

    /// <summary>A bot check while reading stops the reads and the downloads: what is left is not read, nothing is downloaded, and the plan says why.</summary>
    [Test]
    public async Task A_bot_check_while_reading_stops_the_reads_and_downloads()
    {
        _world.Tools.With(Second, YtDlpReadTests.Info(Second), 3, video => video with { ReadError = "ERROR: [youtube] bcdefghijkl: Sign in to confirm you’re not a bot" })
            .With(Third, YtDlpReadTests.Info(Third, duration: 3), 3);

        var exitCode = await _world.RunAsync(_world.Request([Link(First), Link(Second), Link(Third)]));

        Assert.That(exitCode, Is.EqualTo(1));
        Assert.That(_world.Tools.Reads, Is.EqualTo([First, Second]));
        Assert.That(_world.Tools.Downloads, Is.Empty);
        Assert.That(_world.Screen, Has.Some.EndsWith("not read: the reads stopped before it"));
    }

    /// <summary>A bot check while downloading stops the downloads; what was fetched is still transcribed.</summary>
    [Test]
    public async Task A_bot_check_while_downloading_stops_the_downloads_and_what_was_fetched_is_transcribed()
    {
        _world.Tools.With(Second, YtDlpReadTests.Info(Second, duration: 3), 3, video => video with { DownloadError = "ERROR: [youtube] bcdefghijkl: Sign in to confirm you're not a bot" })
            .With(Third, YtDlpReadTests.Info(Third, duration: 3), 3);

        var exitCode = await _world.RunAsync(_world.Request([Link(First), Link(Second), Link(Third)]));

        Assert.That(exitCode, Is.EqualTo(1));
        Assert.That(_world.Tools.Downloads, Is.EqualTo([First, Second]));
        Assert.That(_world.Transcriber.Asked.Select(asked => asked.Video.VideoId), Is.EqualTo([First]));
        Assert.That(_world.Logs.Messages, Has.Some.StartsWith("The downloads stopped: YouTube asked to confirm you are not a bot"));
    }

    /// <summary>
    /// A video the download path may not take, or could not, but which can be watched, is given the command that records it
    /// as it plays instead (Gulzar's answer 3 B): a course's video, one that needs a sign-in, and one a bot check left.
    /// </summary>
    [Test]
    public async Task What_may_not_be_downloaded_is_given_the_command_that_records_it_as_it_plays()
    {
        _world.Tools.With(Third, YtDlpReadTests.Info(Third, "A lesson", duration: 3, availability: "unlisted", channelId: "UCcoursecoursecoursecour"), 3)
            .With(Second, YtDlpReadTests.Info(Second, "Members only", duration: 3, availability: "subscriber_only"), 3)
            .With("defghijklmn", YtDlpReadTests.Info("defghijklmn", "A public talk", duration: 3), 3,
                video => video with { DownloadError = "ERROR: [youtube] defghijklmn: Sign in to confirm you're not a bot" });

        var exitCode = await _world.RunAsync(_world.Request([Link(Third), Link(Second), Link("defghijklmn")]));

        Assert.That(exitCode, Is.EqualTo(1));
        // The command that records the video id names as it plays, as the summary gives it.
        string RecordIt(string id) => CommandLine.YouTube([YouTubeLinks.WatchUrl(id)], "Risala", _world.Recordings.Path);
        Assert.That(_world.Screen, Has.Some.EqualTo($"  01  A lesson: your course's channel: never downloaded; record it as it plays instead: {RecordIt(Third)}"));
        Assert.That(_world.Screen, Has.Some.EqualTo($"  02  Members only: needs a sign-in: left out; record it as it plays instead: {RecordIt(Second)}"));
        var botLine = _world.Screen.Single(line => line.StartsWith("  03  A public talk: ", StringComparison.Ordinal));
        Assert.That(botLine, Does.StartWith("  03  A public talk: YouTube asked to confirm you are not a bot.")
            .And.EndWith($"These videos can be recorded as they play instead: {RecordIt("defghijklmn")}"));
    }

    /// <summary>Three failures to reach YouTube in a row stop the downloads.</summary>
    [Test]
    public async Task Three_network_failures_in_a_row_stop_the_downloads()
    {
        foreach (var id in new[] { First, Second, Third, "defghijklmn" })
            _world.Tools.With(id, YtDlpReadTests.Info(id, duration: 3), 3, video => video with { DownloadError = "ERROR: Unable to download webpage: getaddrinfo failed" });

        await _world.RunAsync(_world.Request([Link(First), Link(Second), Link(Third), Link("defghijklmn")]));

        Assert.That(_world.Tools.Downloads, Is.EqualTo([First, Second, Third]));
    }

    /// <summary>A download YouTube refuses with HTTP 403 reads the video again and tries once more.</summary>
    [Test]
    public async Task A_403_reads_the_video_again_and_tries_once_more()
    {
        _world.Tools.With(First, YtDlpReadTests.Info(First, duration: 3), 3, video => video with { Forbidden = 1 });

        var exitCode = await _world.RunAsync(_world.Request([Link(First)]));

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(_world.Tools.Reads, Is.EqualTo([First, First]));
        Assert.That(_world.Tools.Downloads, Is.EqualTo([First, First]));
    }

    /// <summary>A read more than 5 hours old is read again before its download: the media addresses in it have expired.</summary>
    [Test]
    public async Task A_read_more_than_five_hours_old_is_read_again()
    {
        _world.Tools.OnCall = call =>
        {
            if (call.Arguments.Contains("--skip-download") && call.Arguments[^1].EndsWith(Second, StringComparison.Ordinal)) _world.Runtime.Time.Advance(TimeSpan.FromHours(6));
            return Task.CompletedTask;
        };

        await _world.RunAsync(_world.Request([Link(First), Link(Second)]));

        Assert.That(_world.Tools.Reads, Is.EqualTo([First, Second, First]));
    }

    /// <summary>A download whose audio is shorter than the video is left out as incomplete; the next video goes on.</summary>
    [Test]
    public async Task An_incomplete_download_is_left_out_and_the_next_goes_on()
    {
        _world.Tools.With(First, YtDlpReadTests.Info(First, duration: 30), 3);

        var exitCode = await _world.RunAsync(_world.Request([Link(First), Link(Second)]));

        Assert.That(exitCode, Is.EqualTo(1));
        Assert.That(_world.Screen, Has.Some.Contains("the download is incomplete"));
        Assert.That(_world.Transcriber.Asked.Select(asked => asked.Video.VideoId), Is.EqualTo([Second]));
    }

    /// <summary>The pace is asked before each read and each download, and told when each download ends.</summary>
    [Test]
    public async Task The_pace_is_asked_before_each_read_and_download()
    {
        await _world.RunAsync(_world.Request([Link(First), Link(Second)]));

        Assert.That(_world.Pace.Asked, Is.EqualTo(["read", "read", "download", "ended", "download", "ended"]));
    }

    /// <summary>Too little room for the audio refuses the batch before anything is downloaded; exit code 1.</summary>
    [Test]
    public async Task Too_little_room_refuses_the_batch_before_any_download()
    {
        _world.FreeSpace.Bytes = 1000;

        var exitCode = await _world.RunAsync(_world.Request([Link(First)]));

        Assert.That(exitCode, Is.EqualTo(1));
        Assert.That(_world.Tools.Downloads, Is.Empty);
        Assert.That(_world.Screen, Has.Some.StartsWith("There is not room enough"));
    }

    /// <summary>Another batch holding the lock refuses this one, naming the window; nothing is asked of YouTube.</summary>
    [Test]
    public async Task Another_batch_refuses_this_one()
    {
        using var other = new YouTubeLock(_world.Folders, _world.Log<YouTubeLock>()).TryTake(out _);

        var exitCode = await _world.RunAsync(_world.Request([Link(First)]));

        Assert.That(exitCode, Is.EqualTo(1));
        Assert.That(_world.Tools.Calls, Is.Empty);
        Assert.That(_world.Logs.Problems.Single(), Is.EqualTo($"Another ClassScribe window (process {Environment.ProcessId}) is working through YouTube videos; wait for it to finish, or close it"));
    }

    /// <summary>yt-dlp not found refuses the batch, with the winget command; nothing is asked of YouTube.</summary>
    [Test]
    public async Task Missing_tools_refuse_the_batch_with_the_winget_command()
    {
        using var empty = new YouTubeWorld(withTools: false);

        var exitCode = await empty.RunAsync(empty.Request([Link(First)]));

        Assert.That(exitCode, Is.EqualTo(1));
        Assert.That(empty.Tools.Calls, Is.Empty);
        Assert.That(empty.Logs.Problems, Has.Some.Contains("winget install --source winget --id yt-dlp.yt-dlp"));
    }

    /// <summary>A playlist is read from its flat listing, its videos taken in order with their place, and its warning of hidden videos said.</summary>
    [Test]
    public async Task A_playlists_videos_are_taken_in_order_with_their_place()
    {
        _world.Tools.Playlists["PLxyz"] = $$"""{"id":"PLxyz","title":"Risala, the creed","entries":[{"id":"{{First}}","title":"The creed, part 1"},{"id":"{{Second}}","title":"The creed, part 2"}]}""";

        var exitCode = await _world.RunAsync(_world.Request(["https://www.youtube.com/playlist?list=PLxyz", Link(First)]));

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(_world.Tools.Downloads, Is.EqualTo([First, Second]), "the video given again is taken once");
        Assert.That(_world.Screen, Does.Contain("Read 2 videos from 2 links (1 playlist, \"Risala, the creed\"):"));
        Assert.That(Made().Single(item => item.VideoId == Second).Source.Playlist, Is.EqualTo(new VideoPlaylist("PLxyz", "Risala, the creed", 2)));
    }

    /// <summary>More videos than a batch takes is refused, with exit code 2, before any video is read.</summary>
    [Test]
    public async Task More_videos_than_a_batch_takes_is_refused()
    {
        var entries = string.Join(",", Enumerable.Range(0, DownloadBatch.MostVideos + 1).Select(i => $$"""{"id":"v{{i:0000000000}}"}"""));
        _world.Tools.Playlists["PLbig"] = $$"""{"id":"PLbig","title":"Big","entries":[{{entries}}]}""";

        var exitCode = await _world.RunAsync(_world.Request(["https://www.youtube.com/playlist?list=PLbig"]));

        Assert.That(exitCode, Is.EqualTo(2));
        Assert.That(_world.Tools.Reads, Is.Empty);
    }

    /// <summary>With --no-transcribe nothing is sent to ElevenLabs: the recordings go into Drive's queue and up in one pass, and the summary gives each one's transcribe command.</summary>
    [Test]
    public async Task Without_transcripts_the_recordings_go_up_and_the_summary_gives_the_commands()
    {
        var exitCode = await _world.RunAsync(_world.Request([Link(First), Link(Second)], transcribe: false));

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(_world.Transcriber.Asked, Is.Empty);
        var made = Made();
        Assert.That(_world.Drive.Calls, Is.EqualTo([$"Queue {made[0].Files.BaseName}", $"Queue {made[1].Files.BaseName}", $"Upload {made[0].Files.BaseName}"]));
        Assert.That(_world.Screen, Has.Some.Contains("To transcribe it:").And.Some.Contains("Nothing is sent to ElevenLabs."));
    }

    /// <summary>
    /// Q during a download stops after the video in hand: the next is not downloaded, and what was fetched is still
    /// transcribed. Q twice within 5 seconds stops at once, and the batch is cancelled.
    /// </summary>
    [Test]
    public async Task Q_stops_after_the_video_in_hand_and_Q_twice_at_once()
    {
        _world.Tools.OnCall = call =>
        {
            if (call.Arguments.Contains("--newline")) _world.Ui.Type('q');
            return Task.CompletedTask;
        };

        var exitCode = await _world.RunAsync(_world.Request([Link(First), Link(Second)]));

        Assert.That(_world.Tools.Downloads, Is.EqualTo([First]));
        Assert.That(_world.Transcriber.Asked.Select(asked => asked.Video.VideoId), Is.EqualTo([First]));
        Assert.That(exitCode, Is.EqualTo(1));

        _world.Tools.OnCall = async call =>
        {
            if (!call.Arguments.Contains("--newline")) return;
            _world.Ui.Type('q', 'q');
            await Wait.UntilAsync(() => _world.Logs.Messages.Contains("Stopping at once"), timeoutSeconds: 10);
        };
        _world.Runtime.Time.Advance(TimeSpan.FromMinutes(5));

        Assert.That(async () => await _world.RunAsync(_world.Request([Link(Second)], again: true)), Throws.InstanceOf<OperationCanceledException>());
    }
}
