using ClassScribe.Audio;
using ClassScribe.Commands;
using ClassScribe.Recorder;
using ClassScribe.Runs;
using ClassScribe.Settings;
using ClassScribe.Storage;
using ClassScribe.YouTube;
using Moq;

namespace ClassScribe.Tests;

/// <summary>
/// A batch on the record path (design 1.4, 2 and 4, with Gulzar's answers 2 A and 3 B), attended: the plan with the titles,
/// the worst case of the cost and the time, and the account; Y in the menu opens the first video, and Enter each other, and
/// each on the command line; each video recorded by a run of its own, started as the batch's, into one date folder; S, Esc,
/// Ctrl+C twice; a window handed to one left open; two windows that would not open; a class that comes first; what was done
/// before; the transcripts after the recordings; the summary and its exit code. Chrome, its windows, YouTube's titles and each
/// video's run are played (see <see cref="VideoWorld"/>).
/// </summary>
[TestFixture]
public class VideoBatchTests
{
    private const string First = "https://youtu.be/abcdefghijk";
    private const string Second = "https://youtu.be/bcdefghijkl";

    private VideoWorld _world = null!;

    /// <summary>A world with two videos' titles at YouTube.</summary>
    [SetUp]
    public void SetUp()
    {
        _world = new VideoWorld();
        _world.Title("The creed, part 1").Title("The creed, part 2");
    }

    /// <summary>Deletes the world's folders.</summary>
    [TearDown]
    public void TearDown() => _world.Dispose();

    /// <summary>
    /// --plan prints the plan and stops: the titles and channels, read once each, a second apart, from YouTube's oEmbed address;
    /// how the videos are played and end; the longest each is recorded for and the most their transcripts can cost; and the
    /// account. No window opens, and nothing is recorded.
    /// </summary>
    [Test]
    public async Task The_plan_says_what_is_recorded_how_and_at_most_what_it_costs()
    {
        _world.Account.Answer(FakeElevenLabsAccount.Read(43_362));

        var exitCode = await _world.RunAsync(_world.Request([First, Second], BatchConfirmation.PlanOnly));

        Assert.That(exitCode, Is.Zero);
        Assert.That(_world.Screen, Is.SupersetOf(new[]
        {
            $"Recording 2 videos for \"Risala\" as they play, one at a time, into {Path.Combine(_world.Recordings.Path, "Risala")}:",
            "  01  The creed, part 1  Shaykh X",
            "  02  The creed, part 2  Shaykh X",
            "Each opens in a Chrome window of ClassScribe's own: press play there. ClassScribe records until the video ends,",
            "closes the window, and opens the next when you press Enter. Leave YouTube's Autoplay on: its move to the next",
            "video is how ClassScribe knows this one has ended.",
            "Each is recorded for as long as it plays, at most 4 hours (YouTube:LongestVideoHours), ads included, so at most 8 hours for these 2: they cost at most $1.76 to transcribe, most likely much less ($0.22 for each hour recorded). The transcripts follow the recordings."
        }));
        Assert.That(_world.Screen, Has.Some.StartsWith("ElevenLabs account: 43,362 of 100,000 credits used this period"));
        Assert.That(_world.Http.Requests.Select(request => request.Uri!.Query), Is.EqualTo(new[]
        {
            "?url=https%3A%2F%2Fwww.youtube.com%2Fwatch%3Fv%3Dabcdefghijk&format=json", "?url=https%3A%2F%2Fwww.youtube.com%2Fwatch%3Fv%3Dbcdefghijkl&format=json"
        }));
        Assert.That(_world.Starter.Launches, Is.Empty);
        Assert.That(_world.Started, Is.Empty);
    }

    /// <summary>
    /// The plan's longest recording is held to what one file to ElevenLabs can hold, less an hour (architecture 7.2): with
    /// YouTube:LongestVideoHours at 12, each video is recorded for at most 9 hours, and the plan says why.
    /// </summary>
    [Test]
    public async Task The_plans_longest_recording_is_held_an_hour_within_one_file()
    {
        _world.YouTube.Set(new YouTubeOptions { LongestVideoHours = 12 });

        Assert.That(await _world.RunAsync(_world.Request([First, Second], BatchConfirmation.PlanOnly)), Is.Zero);

        Assert.That(_world.Screen, Has.Some.StartsWith(
            "Each is recorded for as long as it plays, at most 9 hours (YouTube:LongestVideoHours is 12, above what one file to ElevenLabs can hold), ads included, so at most 18 hours for these 2:"));
    }

    /// <summary>
    /// The plan holds the longest recording to the batch's own ElevenLabs:LongestFileHours, less an hour (architecture 7.2),
    /// never to ElevenLabs' 10 hours: with a file of 5 hours, YouTube:LongestVideoHours at 6 is held to 4 hours, which comes
    /// before the safety stop of 9 hours, itself held to 4 hours 30 minutes, and the plan says why.
    /// </summary>
    [Test]
    public async Task The_plans_longest_recording_is_held_by_the_batchs_own_file_limit()
    {
        var elevenLabs = _world.ElevenLabs.Value;
        _world.ElevenLabs.Set(new ElevenLabsOptions { ApiKey = elevenLabs.ApiKey, KeytermsFile = elevenLabs.KeytermsFile, LongestFileHours = 5 });
        _world.YouTube.Set(new YouTubeOptions { LongestVideoHours = 6 });

        Assert.That(await _world.RunAsync(_world.Request([First, Second], BatchConfirmation.PlanOnly)), Is.Zero);

        Assert.That(_world.Screen, Has.Some.StartsWith(
            "Each is recorded for as long as it plays, at most 4 hours (YouTube:LongestVideoHours is 6, above what one file to ElevenLabs can hold), ads included, so at most 8 hours for these 2:"));
    }

    /// <summary>
    /// The plan's price counts the glossary a recording of the course would be sent with, as the one resolver decides it
    /// (architecture 8.3): here a saved class of the course Risala names its own, of three terms, so each hour costs the
    /// glossary's price too.
    /// </summary>
    [Test]
    public async Task The_plans_price_counts_the_glossary_of_the_course()
    {
        var glossary = _world.Recordings.File("risala.txt");
        File.WriteAllLines(glossary, ["tawḥīd", "ʿaqīda", "kalām"]);
        _world.Classes["Classes:Risala:Course"] = "Risala";
        _world.Classes["Classes:Risala:App"] = "chrome";
        _world.Classes["Classes:Risala:KeytermsFile"] = glossary;

        Assert.That(await _world.RunAsync(_world.Request([First, Second], BatchConfirmation.PlanOnly)), Is.Zero);

        Assert.That(_world.Screen, Has.Some.EqualTo(
            "Each is recorded for as long as it plays, at most 4 hours (YouTube:LongestVideoHours), ads included, so at most 8 hours for these 2: they cost at most $2.16 to transcribe, most likely much less ($0.27 for each hour recorded). The transcripts follow the recordings."));
    }

    /// <summary>A video YouTube gives no title for keeps its ID as its title, and is tried all the same; without transcripts nothing is sent to ElevenLabs.</summary>
    [Test]
    public async Task A_video_without_a_title_keeps_its_ID_and_is_tried_all_the_same()
    {
        using var world = new VideoWorld();
        world.Title(null);

        await world.RunAsync(world.Request([First], BatchConfirmation.PlanOnly, transcribe: false));

        Assert.That(world.Screen, Has.Some.EqualTo($"  01  abcdefghijk  -        ({VideoTitle.NoTitle})"));
        Assert.That(world.Screen, Has.Some.EndsWith("so at most 4 hours for this one. Nothing is sent to ElevenLabs."));
        Assert.That(world.Account.Reads, Is.Zero);
    }

    /// <summary>
    /// In the menu, Y at the plan opens the first video at once, and Enter the second: each is recorded by a run of its own,
    /// started as the batch's: record only, Chrome's sound, the video's link, title, channel and @handle, Chrome's version, the
    /// window's browser process and its start, its place in the batch, and the batch's date folder from the second on. The
    /// windows are closed; the transcripts follow the recordings; the course folder opens once; exit code 0.
    /// </summary>
    [Test]
    public async Task In_the_menu_Y_opens_the_first_and_Enter_each_next()
    {
        _world.Ui.Type('y', '\r');

        var exitCode = await _world.RunAsync(_world.Request([First, Second], BatchConfirmation.Ask));

        Assert.That(exitCode, Is.Zero);
        Assert.That(_world.Screen, Has.Some.EqualTo("Y starts; any other key goes back."));
        Assert.That(_world.Screen, Has.Some.EqualTo("Next: 02 The creed, part 2. Enter opens it; S stops here and goes on to the transcripts."));
        Assert.That(_world.Screen, Has.None.StartsWith("Next: 01"));

        var (first, second) = (_world.Started[0], _world.Started[1]);
        var browsers = _world.Browsers.Started;
        Assert.That((first.Transcribe, first.Open, first.Continue, first.Source.Class), Is.EqualTo((false, false, ContinueChoice.New, (ClassSource)new ClassSource.App("chrome"))));
        Assert.That(first.Video, Is.EqualTo(new VideoRequest("https://www.youtube.com/watch?v=abcdefghijk", "abcdefghijk", "The creed, part 1", "Shaykh X", VideoSource.Recorded,
            Folder: null, browsers[0].ProcessId, browsers[0].StartedUtc, 1, 2, first.Video!.BatchId, "@shaykhx", "Chrome 141.0.7390.108")));
        Assert.That((second.Video!.Folder, second.Video.BrowserProcessId, second.Video.Position, second.Video.BatchId),
            Is.EqualTo((_world.DateFolder, browsers[1].ProcessId, 2, first.Video.BatchId)));
        Assert.That(browsers.Select(browser => browser.ExitCode), Has.All.Not.Null, "every window closed");
        Assert.That(_world.Transcriber.Asked.Select(video => video.Video.Title), Is.EqualTo(new[] { "The creed, part 1", "The creed, part 2" }));
        _world.Desktop.Verify(desktop => desktop.OpenFolder(Path.Combine(_world.Recordings.Path, "Risala")), Times.Once);
        Assert.That(_world.Screen, Has.Some.StartsWith("  01  The creed, part 1: recorded (00:00:30), the video window was closed; transcribed: "));
    }

    /// <summary>Any key but Y at the plan goes back: no window opens.</summary>
    [Test]
    public async Task Any_other_key_at_the_plan_goes_back()
    {
        _world.Ui.Type('n');

        Assert.That(await _world.RunAsync(_world.Request([First], BatchConfirmation.Ask)), Is.Zero);
        Assert.That(_world.Starter.Launches, Is.Empty);
    }

    /// <summary>On the command line, --yes goes ahead after the plan, and Enter is still asked before every video, the first too: ClassScribe never opens one by itself.</summary>
    [Test]
    public async Task On_the_command_line_Enter_is_asked_before_every_video()
    {
        _world.Ui.Type('\r');

        var running = _world.RunAsync(_world.Request([First]));
        await Wait.UntilAsync(() => _world.Started.Count == 1, timeoutSeconds: 30);

        Assert.That(await running, Is.Zero);
        Assert.That(_world.Screen, Has.Some.EqualTo("Next: 01 The creed, part 1. Enter opens it; S stops here and goes on to the transcripts."));
    }

    /// <summary>
    /// S between videos opens no more: the transcripts of those recorded follow, and the summary says what was not recorded,
    /// with the same command to record it; exit code 1.
    /// </summary>
    [Test]
    public async Task S_between_videos_stops_and_the_transcripts_follow()
    {
        _world.Ui.Type('y', 's');

        var exitCode = await _world.RunAsync(_world.Request([First, Second], BatchConfirmation.Ask));

        Assert.That(exitCode, Is.EqualTo(1));
        Assert.That(_world.Started, Has.Count.EqualTo(1));
        Assert.That(_world.Transcriber.Asked, Has.Count.EqualTo(1));
        Assert.That(_world.Screen, Has.Some.EqualTo("  02  The creed, part 2: not recorded: you stopped the videos"));
        Assert.That(_world.Screen, Has.Some.EqualTo("Run the same command again to record what is left; what is done is skipped."));
    }

    /// <summary>
    /// Chrome that hands the video to a window of ClassScribe's left open is said, with what to do; Enter tries again once it
    /// is closed, and the video then records.
    /// </summary>
    [Test]
    public async Task A_video_handed_to_a_window_left_open_is_tried_again_on_Enter()
    {
        _world.Starter.BrowsersEndAtOnceWith = 0;
        _world.Ui.Type('y');

        var running = _world.RunAsync(_world.Request([First], BatchConfirmation.Ask));
        await Wait.UntilAsync(() => _world.Lines.Contains(VideoWindow.HandedOff), timeoutSeconds: 30);
        _world.Starter.BrowsersEndAtOnceWith = null;
        _world.Ui.Type('\r');

        Assert.That(await running, Is.Zero);
        Assert.That(_world.Screen, Has.Some.EqualTo("Enter tries again; S stops here and goes on to the transcripts."));
        Assert.That(_world.Started, Has.Count.EqualTo(1));
    }

    /// <summary>Two windows in a row that would not open stop the videos: something is wrong with Chrome; exit code 1.</summary>
    [Test]
    public async Task Two_windows_that_would_not_open_stop_the_videos()
    {
        _world.Title("The creed, part 3");
        _world.Starter.RefuseBrowsersWith = 2;
        _world.Ui.Type('y', '\r');

        var exitCode = await _world.RunAsync(_world.Request([First, Second, "https://youtu.be/cdefghijklm"], BatchConfirmation.Ask));

        Assert.That(exitCode, Is.EqualTo(1));
        Assert.That(_world.Started, Is.Empty);
        Assert.That(_world.Starter.Launches, Has.Count.EqualTo(2));
        Assert.That(_world.Lines, Has.Some.StartsWith("Two videos in a row could not be recorded, so no more windows open"));
        Assert.That(_world.Screen, Has.Some.StartsWith("  01  The creed, part 1: The video window did not open: Windows did not start Chrome:"));
        Assert.That(_world.Screen, Has.Some.EqualTo("  03  The creed, part 3: not recorded: the videos stopped after two that could not be recorded"));
    }

    /// <summary>
    /// Esc in a video's view leaves it recording in the background: no more videos open; the others are transcribed, this one
    /// is left for the same command later; exit code 1.
    /// </summary>
    [Test]
    public async Task Esc_in_a_videos_view_leaves_it_recording_and_opens_no_more()
    {
        _world.Plays["bcdefghijkl"] = new PlayedVideoRun(TimeSpan.FromSeconds(30)) { StaysRecording = true };
        _world.Title("The creed, part 3");
        _world.Ui.Type('y', '\r');

        var exitCode = await _world.RunAsync(_world.Request([First, Second, "https://youtu.be/cdefghijklm"], BatchConfirmation.Ask));

        Assert.That(exitCode, Is.EqualTo(1));
        Assert.That(_world.Started, Has.Count.EqualTo(2));
        Assert.That(_world.Transcriber.Asked.Select(video => video.Video.VideoId), Is.EqualTo(new[] { "abcdefghijk" }));
        Assert.That(_world.Screen, Has.Some.EqualTo("  02  The creed, part 2: records on in the background; run the same command again once it has ended to transcribe it"));
        Assert.That(_world.Screen, Has.Some.EqualTo("  03  The creed, part 3: not recorded: you left the view of the video before it"));
        Assert.That(_world.Browsers.Started[1].ExitCode, Is.Null, "the video recording in the background keeps its window");
    }

    /// <summary>Ctrl+C twice between videos stops the batch: no more videos, no transcripts, exit code 130.</summary>
    [Test]
    public async Task Ctrl_C_twice_between_videos_stops_the_batch()
    {
        _world.Ui.Type('y');

        var running = _world.RunAsync(_world.Request([First, Second], BatchConfirmation.Ask));
        await Wait.UntilAsync(() => _world.Screen.Contains("Next: 02 The creed, part 2. Enter opens it; S stops here and goes on to the transcripts."), timeoutSeconds: 30);
        _world.Signals.CtrlCPressed();
        _world.Signals.CtrlCPressed();

        Assert.That(await running, Is.EqualTo(130));
        Assert.That(_world.Started, Has.Count.EqualTo(1));
        Assert.That(_world.Transcriber.Asked, Is.Empty);
    }

    /// <summary>Ctrl+C twice while a video records stops that video, as it stops a class, and the batch: no transcripts, exit code 130.</summary>
    [Test]
    public async Task Ctrl_C_twice_during_a_video_stops_it_and_the_batch()
    {
        _world.Plays["abcdefghijk"] = new PlayedVideoRun(TimeSpan.FromSeconds(20), RunEndCause.Q)
        {
            During = world =>
            {
                world.Signals.CtrlCPressed();
                world.Signals.CtrlCPressed();
            }
        };
        _world.Ui.Type('y');

        Assert.That(await _world.RunAsync(_world.Request([First, Second], BatchConfirmation.Ask)), Is.EqualTo(130));
        Assert.That(_world.Started, Has.Count.EqualTo(1));
        Assert.That(_world.Transcriber.Asked, Is.Empty);
    }

    /// <summary>
    /// A class that started stopped the video: its recording may be cut short, which the summary says, and it is transcribed
    /// all the same; the next video waits while the class records, then opens on Enter; exit code 1, since the same command
    /// records the first again.
    /// </summary>
    [Test]
    public async Task A_class_comes_first_and_the_next_video_waits_for_it()
    {
        _world.Plays["abcdefghijk"] = new PlayedVideoRun(TimeSpan.FromMinutes(10), RunEndCause.ClassStarted) { During = world => world.StartAClass(TimeSpan.FromMinutes(5)) };
        _world.Ui.Type('y', '\r');

        var exitCode = await _world.RunAsync(_world.Request([First, Second], BatchConfirmation.Ask));

        Assert.That(exitCode, Is.EqualTo(1));
        Assert.That(_world.Lines, Has.Some.EqualTo("YouTube: waiting while \"Kalam\" records; the videos go on after it"));
        Assert.That(_world.Started, Has.Count.EqualTo(2));
        Assert.That(_world.Transcriber.Asked, Has.Count.EqualTo(2));
        Assert.That(_world.Screen, Has.Some.StartsWith(
            "  01  The creed, part 1: recorded (00:10:00), but stopped so that a class could record, so it may be cut short; the same command records it again; transcribed: "));
    }

    /// <summary>A video's start refused because a class started that very moment waits for the class, then is asked again.</summary>
    [Test]
    public async Task A_start_refused_by_a_class_waits_for_it_and_asks_again()
    {
        _world.Plays["abcdefghijk"] = new PlayedVideoRun(TimeSpan.FromSeconds(30)) { Refused = true, ClassFor = TimeSpan.FromMinutes(2) };
        _world.Ui.Type('y', '\r');

        Assert.That(await _world.RunAsync(_world.Request([First], BatchConfirmation.Ask)), Is.Zero);
        Assert.That(_world.Started, Has.Count.EqualTo(2), "refused, then recorded");
        Assert.That(_world.Started[1].Video!.Position, Is.EqualTo(1));
        Assert.That(_world.Browsers.Started, Has.Count.EqualTo(2), "a window each time");
        Assert.That(_world.Screen.Count(line => line == "Next: 01 The creed, part 1. Enter opens it; S stops here and goes on to the transcripts."), Is.EqualTo(1),
            "once the class has ended, Enter opens the video again");
    }

    /// <summary>
    /// A video stopped at YouTube:LongestVideoHours is transcribed and flagged, with the command that records the rest from
    /// where it stopped.
    /// </summary>
    [Test]
    public async Task A_video_stopped_at_its_longest_gives_the_command_for_the_rest()
    {
        using var world = new VideoWorld();
        world.Title("A long lecture");
        world.Plays["abcdefghijk"] = new PlayedVideoRun(TimeSpan.FromHours(4), RunEndCause.VideoLimit);
        world.Ui.Type('y');

        Assert.That(await world.RunAsync(world.Request(["https://www.youtube.com/watch?v=abcdefghijk&t=600s"], BatchConfirmation.Ask)), Is.EqualTo(1));

        var line = world.Screen.Single(text => text.StartsWith("  01  A long lecture: ", StringComparison.Ordinal));
        Assert.That(line, Does.Contain("but stopped at the longest a video is recorded for (YouTube:LongestVideoHours), so it may be cut short"));
        Assert.That(line, Does.Contain(Printed.YouTube(["https://www.youtube.com/watch?v=abcdefghijk&t=15000s"], "Risala", world.Recordings.Path)));
        Assert.That(world.Started.Single().Video!.StartSeconds, Is.EqualTo(600), "the window opened where the link starts");
    }

    /// <summary>
    /// A video's recorder lost while it records is never continued: its audio so far is transcribed, flagged as a recording
    /// that may be cut short, its window is closed by the batch, and the next video goes on; exit code 1.
    /// </summary>
    [Test]
    public async Task A_lost_recorders_audio_is_transcribed_as_one_that_may_be_cut_short()
    {
        _world.Plays["abcdefghijk"] = new PlayedVideoRun(TimeSpan.FromSeconds(30)) { Lost = true };
        _world.Ui.Type('y', '\r');

        Assert.That(await _world.RunAsync(_world.Request([First, Second], BatchConfirmation.Ask)), Is.EqualTo(1));

        Assert.That(_world.Started, Has.Count.EqualTo(2));
        Assert.That(_world.Transcriber.Asked, Has.Count.EqualTo(2));
        Assert.That(_world.Browsers.Started[0].ExitCode, Is.Not.Null, "the batch closed the lost run's window");
        Assert.That(_world.Screen, Has.Some.StartsWith(
            "  01  The creed, part 1: recorded (00:00:30), but its recording did not finish, so it may be cut short; the same command records it again; transcribed: "));
    }

    /// <summary>Two runs in a row that failed before they recorded stop the videos, as two windows that would not open do.</summary>
    [Test]
    public async Task Two_runs_that_failed_before_they_recorded_stop_the_videos()
    {
        _world.Title("The creed, part 3");
        _world.Plays["abcdefghijk"] = new PlayedVideoRun(TimeSpan.Zero) { Outcome = OutcomeKind.RecorderFailed };
        _world.Plays["bcdefghijkl"] = new PlayedVideoRun(TimeSpan.Zero) { Outcome = OutcomeKind.RecorderFailed };
        _world.Ui.Type('y', '\r');

        Assert.That(await _world.RunAsync(_world.Request([First, Second, "https://youtu.be/cdefghijklm"], BatchConfirmation.Ask)), Is.EqualTo(1));

        Assert.That(_world.Started, Has.Count.EqualTo(2));
        Assert.That(_world.Screen, Has.Some.EqualTo("  01  The creed, part 1: not recorded: The recording failed"));
        Assert.That(_world.Screen, Has.Some.EqualTo("  03  The creed, part 3: not recorded: the videos stopped after two that could not be recorded"));
    }

    /// <summary>A video whose window played nothing is not transcribed: nothing was kept of it; exit code 1.</summary>
    [Test]
    public async Task A_video_that_played_nothing_is_not_transcribed()
    {
        _world.Plays["abcdefghijk"] = new PlayedVideoRun(TimeSpan.FromMinutes(5), RunEndCause.NothingPlayed) { Outcome = OutcomeKind.NothingRecorded };
        _world.Ui.Type('y');

        Assert.That(await _world.RunAsync(_world.Request([First], BatchConfirmation.Ask)), Is.EqualTo(1));
        Assert.That(_world.Transcriber.Asked, Is.Empty);
        Assert.That(_world.Screen, Has.Some.EqualTo("  01  The creed, part 1: not recorded: Nothing played in ClassScribe's video window, so nothing of \"The creed, part 1\" was kept to transcribe"));
    }

    /// <summary>
    /// What was done before is not recorded again: a video transcribed before is left out; one recorded whole before without a
    /// transcript is transcribed, not recorded; one that may be cut short is recorded again. With --again, all are recorded.
    /// </summary>
    [Test]
    public async Task What_was_done_before_is_not_recorded_again()
    {
        _world.Title("The creed, part 3");
        RecordedBefore("abcdefghijk", confirmed: true, transcribed: true);
        RecordedBefore("bcdefghijkl", confirmed: true, transcribed: false);
        RecordedBefore("cdefghijklm", confirmed: false, transcribed: false);
        _world.Ui.Type('y');

        var exitCode = await _world.RunAsync(_world.Request([First, Second, "https://youtu.be/cdefghijklm"], BatchConfirmation.Ask));

        Assert.That(exitCode, Is.Zero);
        Assert.That(_world.Started.Select(request => request.Video!.VideoId), Is.EqualTo(new[] { "cdefghijklm" }));
        Assert.That(_world.Transcriber.Asked.Select(video => video.Video.VideoId), Is.EqualTo(new[] { "cdefghijklm", "bcdefghijkl" }));
        Assert.That(_world.Screen, Has.Some.StartsWith("  01  The creed, part 1  Shaykh X  already transcribed on"));
        Assert.That(_world.Screen, Has.Some.StartsWith("  03  The creed, part 3  Shaykh X  fetched before (").And.EndsWith("so it may be cut short: fetched again"));
        Assert.That(_world.Screen, Has.Some.StartsWith("Also transcribed: 1 recording made before, 1 min"));
    }

    /// <summary>Without an ElevenLabs key, a batch that would transcribe is refused at once: no title is read, and no window opens.</summary>
    [Test]
    public async Task Without_a_key_nothing_is_read_or_opened()
    {
        _world.ElevenLabs.Set(new ElevenLabsOptions { ApiKey = "" });

        Assert.That(await _world.RunAsync(_world.Request([First])), Is.EqualTo(1));
        Assert.That(_world.Lines, Has.Some.StartsWith("No ElevenLabs API key found in ELEVENLABS_API_KEY"));
        Assert.That(_world.Http.Requests, Is.Empty);
        Assert.That(_world.Starter.Launches, Is.Empty);
    }

    /// <summary>Without Chrome, the batch is refused before anything starts, with where it was looked for and what to do.</summary>
    [Test]
    public async Task Without_Chrome_nothing_starts()
    {
        using var world = new VideoWorld();
        world.YouTube.Set(new YouTubeOptions { BrowserPath = @"D:\Gone\chrome.exe" });

        Assert.That(await world.RunAsync(world.Request([First])), Is.EqualTo(1));
        Assert.That(world.Lines, Has.Some.EqualTo(IToolLocator.BrowserNotFound));
        Assert.That(world.Http.Requests, Is.Empty);
    }

    /// <summary>Another batch holding youtube.lock refuses this one, which reads and opens nothing.</summary>
    [Test]
    public async Task Another_batch_holding_the_lock_refuses_this_one()
    {
        using var other = new YouTubeLock(_world.Folders, _world.Log<YouTubeLock>()).TryTake(out _);

        Assert.That(await _world.RunAsync(_world.Request([First])), Is.EqualTo(1));
        Assert.That(_world.Http.Requests, Is.Empty);
    }

    /// <summary>A playlist's link, a live link and more than 10 videos are refused as mistakes, as the command line refuses them.</summary>
    [Test]
    public async Task Links_the_record_path_does_not_take_are_mistakes()
    {
        Assert.That(await _world.RunAsync(_world.Request(["https://www.youtube.com/playlist?list=PLxyz"])), Is.EqualTo(2));
        Assert.That(await _world.RunAsync(_world.Request(["https://www.youtube.com/live/abcdefghijk"])), Is.EqualTo(2));
        Assert.That(await _world.RunAsync(_world.Request([.. Enumerable.Range(0, 11).Select(i => $"https://youtu.be/v{i:0000000000}")])), Is.EqualTo(2));
        Assert.That(_world.Http.Requests, Is.Empty);
    }

    /// <summary>
    /// A video whose recording may be cut short is recorded again by the same command, as its summary says, even once the
    /// batch transcribed what it holds: a transcript counts only for a recording of the whole video; the earlier recording
    /// and its transcript stay as they are.
    /// </summary>
    [Test]
    public async Task A_video_cut_short_is_recorded_again_though_it_was_transcribed()
    {
        var before = RecordedBefore("abcdefghijk", confirmed: false, transcribed: true);
        _world.Ui.Type('y');

        var exitCode = await _world.RunAsync(_world.Request([First], BatchConfirmation.Ask));

        Assert.That(exitCode, Is.Zero);
        Assert.That(_world.Started.Select(request => request.Video!.VideoId), Is.EqualTo(new[] { "abcdefghijk" }), "the same command records it again");
        Assert.That(_world.Screen, Has.Some.StartsWith("  01  The creed, part 1  Shaykh X  fetched before (").And.Some.EndsWith("so it may be cut short: fetched again"));
        Assert.That(File.Exists(before.Transcript()), Is.True, "the earlier transcript stays");
    }

    /// <summary>
    /// The command the summary gives for the rest of a video stopped at its longest records the rest, from where it stopped,
    /// once the batch has transcribed what it recorded.
    /// </summary>
    [Test]
    public async Task The_command_for_the_rest_of_a_video_records_it()
    {
        using var world = new VideoWorld();
        world.Title("A long lecture").Title("A long lecture");
        world.Plays["abcdefghijk"] = new PlayedVideoRun(TimeSpan.FromHours(4), RunEndCause.VideoLimit);
        world.Ui.Type('y');
        await world.RunAsync(world.Request(["https://youtu.be/abcdefghijk"], BatchConfirmation.Ask));
        Assert.That(world.Transcriber.Asked, Has.Count.EqualTo(1), "what it recorded was transcribed");
        world.Runtime.Time.Advance(TimeSpan.FromMinutes(1));
        world.Plays["abcdefghijk"] = new PlayedVideoRun(TimeSpan.FromMinutes(30));
        world.Signals = new FakeUserSignals();
        world.Ui.Type('y');

        await world.RunAsync(world.Request(["https://www.youtube.com/watch?v=abcdefghijk&t=14400s"], BatchConfirmation.Ask));

        Assert.That(world.Started, Has.Count.EqualTo(2), "the command for the rest records it");
        Assert.That(world.Started[1].Video!.StartSeconds, Is.EqualTo(14400));
    }

    /// <summary>
    /// A class that starts while the batch waits for Enter comes first: the next video's window opens only once the class has
    /// ended, and on Enter again, never while the class records.
    /// </summary>
    [Test]
    public async Task A_class_that_starts_while_Enter_is_awaited_comes_first()
    {
        var classRecordingWhenTheSecondOpened = false;
        _world.Plays["bcdefghijkl"] = new PlayedVideoRun(TimeSpan.FromMinutes(1))
        {
            During = world => classRecordingWhenTheSecondOpened = world.Runtime.Journal.List().Any(run => run.IsRecordingNow && !run.IsVideo)
        };
        _world.Ui.BeforeKeyRead = reads =>
        {
            // Y at the plan is the first key read; at the prompt for the second video, a class starts in another window.
            if (reads == 2) _world.StartAClass(TimeSpan.FromMinutes(2));
        };
        _world.Ui.Type('y', '\r', '\r');

        Assert.That(await _world.RunAsync(_world.Request([First, Second], BatchConfirmation.Ask)), Is.Zero);

        Assert.That(classRecordingWhenTheSecondOpened, Is.False, "no video window opens while a class records");
        Assert.That(_world.Started, Has.Count.EqualTo(2));
        Assert.That(_world.Lines, Has.Some.EqualTo("YouTube: waiting while \"Kalam\" records; the videos go on after it"));
        Assert.That(_world.Screen.Count(line => line.StartsWith("Next: 02 The creed, part 2. Enter opens it", StringComparison.Ordinal)), Is.EqualTo(2),
            "Enter is asked for again once the class has ended");
    }

    /// <summary>
    /// The record path cleans up what an earlier download batch left half done, under the lock, as the download path does: the
    /// unfinished parts of a conversion a crash cut short, and the note that names them.
    /// </summary>
    [Test]
    public async Task A_conversion_left_half_done_is_cleaned_up()
    {
        var target = RecordingFiles.ForNew(Path.Combine(_world.Recordings.Path, "Risala", "2026-10-14"), "Risala", new DateTimeOffset(2026, 10, 14, 23, 0, 0, TimeSpan.Zero));
        Directory.CreateDirectory(target.Folder);
        File.WriteAllBytes(target.Segment(1) + DownloadedAudio.PartialEnding, new byte[100]);
        Directory.CreateDirectory(_world.Folders.Root);
        File.WriteAllText(_world.Folders.ConvertingNote,
            $$"""{"videoId":"abcdefghijk","title":"The creed, part 1","folder":{{System.Text.Json.JsonSerializer.Serialize(target.Folder)}},"baseName":"{{target.BaseName}}"}""");

        Assert.That(await _world.RunAsync(_world.Request([First], BatchConfirmation.PlanOnly)), Is.Zero);

        Assert.That(File.Exists(target.Segment(1) + DownloadedAudio.PartialEnding), Is.False);
        Assert.That(File.Exists(_world.Folders.ConvertingNote), Is.False);
        Assert.That(_world.Lines, Has.Some.StartsWith("Deleted the unfinished conversion of \"The creed, part 1\" left by an earlier batch"));
    }

    /// <summary>
    /// A recording made before is looked at again just before its transcript: one transcribed meanwhile in another window, as
    /// with menu choice 3 while the batch records, is not sent to ElevenLabs again.
    /// </summary>
    [Test]
    public async Task A_recording_transcribed_meanwhile_is_not_sent_again()
    {
        var before = RecordedBefore("bcdefghijkl", confirmed: true, transcribed: false);
        _world.Plays["abcdefghijk"] = new PlayedVideoRun(TimeSpan.FromMinutes(10)) { During = _ => File.WriteAllText(before.Transcript(), "# made in another window\n") };
        _world.Ui.Type('y');

        Assert.That(await _world.RunAsync(_world.Request([First, Second], BatchConfirmation.Ask)), Is.Zero);

        Assert.That(_world.Transcriber.Asked.Select(video => video.Video.VideoId), Is.EqualTo(new[] { "abcdefghijk" }), "nothing is billed twice");
        Assert.That(_world.Screen, Has.Some.EqualTo($"  02  The creed, part 2: made before; transcribed: {before.Transcript()}"));
    }

    /// <summary>A link copied from an embedded player's YouTube button is recorded as it plays, as any video he can watch (Gulzar's answer 3 B).</summary>
    [Test]
    public async Task A_link_from_an_embedded_players_button_is_recorded_as_it_plays()
    {
        _world.Ui.Type('y');

        Assert.That(await _world.RunAsync(_world.Request(["https://www.youtube.com/watch?v=abcdefghijk&feature=emb_logo"], BatchConfirmation.Ask)), Is.Zero);
        Assert.That(_world.Started.Single().Video!.Url, Is.EqualTo(YouTubeLinks.WatchUrl("abcdefghijk")));
    }

    /// <summary>A recording of <paramref name="videoId"/> made before, whole or not, with a transcript or not.</summary>
    private RecordingFiles RecordedBefore(string videoId, bool confirmed, bool transcribed)
    {
        var files = _world.ClassFolders.CreateRecording("Risala", _world.Recordings.Path);
        using (var writer = new WavSegmentWriter(files.Segment(1), 10)) writer.Write(new byte[600 * 2]);
        VideoSourceFile.Write(files.Source, new VideoSource(VideoSource.YouTubeKind, YouTubeLinks.WatchUrl(videoId), videoId, videoId, null, null, null, null, null, null, null,
            VideoSource.Recorded, "Chrome", TestRuns.Start, null, null, confirmed ? "the video window was closed" : "ended after 2 minutes of silence", confirmed));
        if (transcribed) File.WriteAllText(files.Transcript(), "# done\n");
        _world.Runtime.Time.Advance(TimeSpan.FromSeconds(5));
        return files;
    }
}
