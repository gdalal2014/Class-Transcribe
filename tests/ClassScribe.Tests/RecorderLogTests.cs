using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using ClassScribe.Recorder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClassScribe.Tests;

/// <summary>
/// The recorder's log: the numbered lines it holds in memory for the windows, the lines it hands on to whoever follows it,
/// the files it writes on disk (the recording's own log, and recorder.log before the recording's folder is known), and the
/// logger provider that fills it with the text the console would show.
/// </summary>
[TestFixture]
public class RecorderLogTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 15, 23, 0, 2, TimeSpan.Zero);

    /// <summary>How long a test waits for the log's own threads before it fails.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The shortest pause the tests accept for the log's pause of 100 ms between two tries. Windows ends a sleep on a tick
    /// of its clock, about every 16 ms, so a sleep of 100 ms may end up to a tick early.
    /// </summary>
    private static readonly TimeSpan ShortestPause = TimeSpan.FromMilliseconds(80);

    private ManualTimeProvider _time = null!;
    private TempFolder _folder = null!;
    private List<RecorderLog> _logs = null!;
    private List<StallingStream> _stalls = null!;

    /// <summary>Creates a clock moved by hand and a folder of the test's own.</summary>
    [SetUp]
    public void SetUp()
    {
        _time = new ManualTimeProvider(Start);
        _folder = new TempFolder();
        _logs = [];
        _stalls = [];
    }

    /// <summary>Lets every stalled file go, disposes every log the test made, so its files are closed, then deletes the folder.</summary>
    [TearDown]
    public void TearDown()
    {
        foreach (var stall in _stalls) stall.Release();
        foreach (var log in _logs) log.Dispose();
        _folder.Dispose();
    }

    /// <summary>A log on the test's clock, disposed when the test ends.</summary>
    private RecorderLog NewLog(string? startupFile = null, Func<string, Stream>? open = null, TimeSpan? drainPatience = null,
        Action<string, string>? move = null)
    {
        var log = new RecorderLog(_time, startupFile)
        {
            OpenFile = open ?? RecorderLog.OpenForAppending,
            DrainPatience = drainPatience ?? RecorderLog.DefaultDrainPatience,
            MoveFile = move ?? RecorderLog.MoveReplacing
        };
        _logs.Add(log);
        return log;
    }

    /// <summary>A file that takes nothing until the test lets it go, released when the test ends.</summary>
    private StallingStream NewStall()
    {
        var stall = new StallingStream();
        _stalls.Add(stall);
        return stall;
    }

    /// <summary>Reads a log file while the log may still hold it open for writing, as Notepad or a window would.</summary>
    private static string ReadFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>The text a log file holds for these lines: each on a line of its own.</summary>
    private static string Lines(params string[] lines) => string.Concat(lines.Select(line => line + Environment.NewLine));

    /// <summary>The numbers of the lines.</summary>
    private static long[] Numbers(IEnumerable<LogEntry> lines) => [.. lines.Select(line => line.Seq)];

    /// <summary>
    /// Fails the test unless nothing holds the file open, which is when it can be opened with no sharing at all. The log
    /// shares its files for reading, writing, renaming and deleting, so a rename or a delete succeeds even while it holds
    /// one; only this shows whether it let go. A virus scanner may hold a file just written for a moment, so this tries
    /// for a few seconds first.
    /// </summary>
    private static void AssertNothingHolds(string path, string because)
    {
        var deadline = Environment.TickCount64 + 5000;
        while (true)
        {
            try
            {
                using var alone = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                return;
            }
            catch (IOException ex)
            {
                // Deliberately not rethrown: a sharing violation means something still holds the file. It is tried again
                // until the deadline, and then the test fails with Windows' message.
                if (Environment.TickCount64 >= deadline) Assert.Fail($"{because}: {ex.Message}");
                Thread.Sleep(50);
            }
        }
    }

    /// <summary>
    /// Writes the lines on another thread, and fails the test unless that is done within the patience: a log that waited
    /// for its file or a follower fails the test rather than hanging it.
    /// </summary>
    private static void WriteAtOnce(RecorderLog log, params string[] lines)
    {
        var writing = Task.Run(() =>
        {
            foreach (var line in lines) log.Write(LogLevel.Information, line);
        });

        Assert.That(writing.Wait(Patience), Is.True, "writing a line waited");
    }

    /// <summary>
    /// The recording's log named now is known, so that an upload of the recording can tell that the log is still being
    /// written by this process: none at first, the one named, and none again once it ends.
    /// </summary>
    [Test]
    public void The_recordings_log_named_now_is_known()
    {
        var log = NewLog();
        var path = _folder.File("Kalam_2026-10-15_23-00-02Z.log");
        Assert.That(log.NamedFile, Is.Null);

        log.WriteFileTo(path);
        Assert.That(log.NamedFile, Is.EqualTo(path));

        log.EndFile();
        Assert.That(log.NamedFile, Is.Null);
    }

    // ---- The lines held in memory ----

    /// <summary>
    /// Lines are numbered from 1 in the order they are written, and each keeps its level, its text and the time it was
    /// written; with nothing seen yet, a window gets them all, the oldest line held is the first, and the last number is
    /// the last line's.
    /// </summary>
    [Test]
    public void Lines_are_numbered_from_1_in_the_order_they_are_written()
    {
        var log = NewLog();

        log.Write(LogLevel.Information, "19:00:02  Recording \"Kalam\"");
        _time.Advance(TimeSpan.FromSeconds(3));
        log.Write(LogLevel.Warning, "19:00:05  WARNING  No sound for 60 seconds");
        _time.Advance(TimeSpan.FromSeconds(1));
        log.Write(LogLevel.Error, "19:00:06  ERROR  Could not save the mark");

        var (lines, oldest, last) = log.Since(0);
        Assert.That(lines, Is.EqualTo(new[]
        {
            new LogEntry(1, Start, LogLevel.Information, "19:00:02  Recording \"Kalam\""),
            new LogEntry(2, Start.AddSeconds(3), LogLevel.Warning, "19:00:05  WARNING  No sound for 60 seconds"),
            new LogEntry(3, Start.AddSeconds(4), LogLevel.Error, "19:00:06  ERROR  Could not save the mark")
        }));
        Assert.That(oldest, Is.EqualTo(1));
        Assert.That(last, Is.EqualTo(3));
    }

    /// <summary>A window that has seen lines up to a number gets only the lines after it, up to the last number.</summary>
    [Test]
    public void Since_gives_the_lines_after_the_number_a_window_saw_last()
    {
        var log = NewLog();
        for (var line = 1; line <= 5; line++) log.Write(LogLevel.Information, $"line {line}");

        var (lines, oldest, last) = log.Since(2);

        Assert.That(lines.Select(entry => entry.Text), Is.EqualTo(new[] { "line 3", "line 4", "line 5" }));
        Assert.That(Numbers(lines), Is.EqualTo(new long[] { 3, 4, 5 }));
        Assert.That(oldest, Is.EqualTo(1));
        Assert.That(last, Is.EqualTo(5));
    }

    /// <summary>
    /// Before the first line there is nothing to give, the oldest number is 1, the number the first line will get, and the
    /// last number is 0.
    /// </summary>
    [Test]
    public void Before_the_first_line_there_is_nothing_and_the_oldest_number_is_1()
    {
        var (lines, oldest, last) = NewLog().Since(0);

        Assert.That(lines, Is.Empty);
        Assert.That(oldest, Is.EqualTo(1));
        Assert.That(last, Is.EqualTo(0));
    }

    /// <summary>
    /// A number beyond the last line, such as one a window kept from another run, gives no lines, and the last number shows
    /// that the window's number is not one this log gave: the link then remembers the last number, not the window's, so
    /// the lines still to come are not taken for lines the window has seen.
    /// </summary>
    [Test]
    public void A_number_beyond_the_last_line_gives_no_lines_and_the_last_number_shows_it()
    {
        var log = NewLog();
        for (var line = 1; line <= 3; line++) log.Write(LogLevel.Information, $"line {line}");

        var atTheLast = log.Since(3);
        Assert.That(atTheLast.Lines, Is.Empty);
        Assert.That(atTheLast.LastSeq, Is.EqualTo(3));

        var beyond = log.Since(40);
        Assert.That(beyond.Lines, Is.Empty);
        Assert.That(beyond.OldestSeq, Is.EqualTo(1));
        Assert.That(beyond.LastSeq, Is.EqualTo(3), "the last number is below the window's, which this log never gave");
    }

    /// <summary>
    /// Once more lines have been written than the log holds, the oldest are cut: a window gets what is still held, from the
    /// oldest number it holds, and that number tells it how many it missed.
    /// </summary>
    [Test]
    public void Since_after_the_ring_has_cut_old_lines_starts_at_the_oldest_line_held()
    {
        var log = NewLog();
        for (var line = 1; line <= RecorderLog.Capacity + 5; line++) log.Write(LogLevel.Information, $"line {line}");

        var (all, oldest, last) = log.Since(0);
        Assert.That(oldest, Is.EqualTo(6));
        Assert.That(last, Is.EqualTo(RecorderLog.Capacity + 5));
        Assert.That(all, Has.Count.EqualTo(RecorderLog.Capacity));
        Assert.That(all[0], Has.Property(nameof(LogEntry.Seq)).EqualTo(6).And.Property(nameof(LogEntry.Text)).EqualTo("line 6"));
        Assert.That(all[^1], Has.Property(nameof(LogEntry.Seq)).EqualTo(RecorderLog.Capacity + 5));

        var (afterThree, oldestAgain, _) = log.Since(3);
        Assert.That(Numbers(afterThree), Is.EqualTo(Numbers(all)), "a window that saw line 3 gets everything still held");
        Assert.That(oldestAgain, Is.EqualTo(6));

        var (lastThree, _, _) = log.Since(RecorderLog.Capacity + 2);
        Assert.That(lastThree.Select(entry => entry.Text), Is.EqualTo(new[] { $"line {RecorderLog.Capacity + 3}", $"line {RecorderLog.Capacity + 4}", $"line {RecorderLog.Capacity + 5}" }));
    }

    /// <summary>Lines written from several threads at once each get a number of their own, with none skipped.</summary>
    [Test]
    public void Lines_written_from_several_threads_at_once_each_get_a_number_of_their_own()
    {
        var log = NewLog();

        Parallel.For(0, 8, thread =>
        {
            for (var line = 0; line < 500; line++) log.Write(LogLevel.Information, $"{thread}:{line}");
        });

        var lines = log.Since(0).Lines;
        Assert.That(Numbers(lines), Is.EqualTo(Enumerable.Range(1, 4000).Select(number => (long)number)));
        Assert.That(lines.Select(line => line.Text), Is.Unique);
    }

    // ---- The lines handed on to followers ----

    /// <summary>Followers get every line once, in the order of the numbers, even when several threads write at once.</summary>
    [Test]
    public async Task Followers_get_each_line_once_in_the_order_of_its_number()
    {
        var log = NewLog();
        var heard = new ConcurrentQueue<LogEntry>();
        log.Written += heard.Enqueue;

        Parallel.For(0, 8, thread =>
        {
            for (var line = 0; line < 250; line++) log.Write(LogLevel.Information, $"{thread}:{line}");
        });

        await Wait.UntilAsync(() => heard.Count >= 2000);
        Assert.That(Numbers(heard), Is.EqualTo(Enumerable.Range(1, 2000).Select(number => (long)number)));
    }

    /// <summary>A follower gets the lines written after it began to follow; the earlier ones are there to ask for (Since).</summary>
    [Test]
    public async Task A_follower_gets_only_the_lines_written_after_it_began_to_follow()
    {
        var log = NewLog();
        log.Write(LogLevel.Information, "before 1");
        log.Write(LogLevel.Information, "before 2");
        var heard = new ConcurrentQueue<LogEntry>();

        log.Written += heard.Enqueue;
        log.Write(LogLevel.Information, "after 1");
        log.Write(LogLevel.Information, "after 2");

        await Wait.UntilAsync(() => heard.Count >= 2);
        await Task.Delay(50);
        Assert.That(heard.Select(line => line.Text), Is.EqualTo(new[] { "after 1", "after 2" }));
        Assert.That(Numbers(heard), Is.EqualTo(new long[] { 3, 4 }));
    }

    /// <summary>
    /// Writing a line never waits for a follower: while one is held up, as a window's link may be, lines are still written
    /// at once, and the follower gets them all, in order, once it goes on.
    /// </summary>
    [Test]
    public async Task Writing_never_waits_for_a_follower()
    {
        var log = NewLog();
        using var held = new ManualResetEventSlim();
        using var holding = new ManualResetEventSlim();
        var heard = new ConcurrentQueue<LogEntry>();
        log.Written += line =>
        {
            holding.Set();
            held.Wait();
            heard.Enqueue(line);
        };

        try
        {
            WriteAtOnce(log, "line 1");
            Assert.That(holding.Wait(Patience), Is.True, "the follower should have been handed the first line");

            WriteAtOnce(log, [.. Enumerable.Range(2, 99).Select(line => $"line {line}")]);
            Assert.That(log.Since(0).Lines, Has.Count.EqualTo(100), "the lines can be asked for while a follower is held up");
        }
        finally
        {
            held.Set();
        }

        await Wait.UntilAsync(() => heard.Count >= 100);
        Assert.That(Numbers(heard), Is.EqualTo(Enumerable.Range(1, 100).Select(number => (long)number)));
    }

    /// <summary>
    /// The log holds no lock while it hands a line on. The link's handler takes the link's lock, which a thread registering
    /// a window holds while it asks the log for the lines to replay; if the log held its lock while the handler waited, the
    /// two would wait for each other for ever. Here the handler waits for another thread that asks the log for lines, and
    /// that thread is answered. Only another thread shows this, since the log's lock lets the thread holding it in again.
    /// A follower may also ask on its own thread, and stop following, from inside its handler, and then gets no more lines.
    /// </summary>
    [Test]
    public async Task A_follower_may_ask_for_lines_and_stop_following_from_inside_its_handler()
    {
        var log = NewLog();
        var asked = new ConcurrentQueue<int>();
        var askedFromAnotherThread = new ConcurrentQueue<bool>();
        Action<LogEntry>? follower = null;
        follower = _ =>
        {
            asked.Enqueue(log.Since(0).Lines.Count);
            var asking = Task.Run(() => log.Since(0).Lines.Count);
            askedFromAnotherThread.Enqueue(asking.Wait(Patience) && asking.Result == 1);
            log.Written -= follower;
        };
        log.Written += follower;

        WriteAtOnce(log, "line 1");
        await Wait.UntilAsync(() => !askedFromAnotherThread.IsEmpty, timeoutSeconds: 15);
        WriteAtOnce(log, "line 2");
        await Task.Delay(50);

        Assert.That(askedFromAnotherThread, Is.EqualTo(new[] { true }), "another thread waited for the log while it handed a line on");
        Assert.That(asked, Is.EqualTo(new[] { 1 }));
    }

    /// <summary>
    /// The followers are handed the lines after Write has returned, so the link waits for them before it sends a run's
    /// end: once WaitForFollowers returns, a follower has been handed every line written before, so the run's last lines,
    /// "n log lines were lost" among them, reach a window ahead of the end, after which the window stops reading.
    /// </summary>
    [Test]
    public void Once_the_followers_are_waited_for_they_have_every_line_written_before()
    {
        var log = NewLog();
        var server = new Lock();
        var window = new List<string>();
        using var holding = new ManualResetEventSlim();
        using var held = new ManualResetEventSlim();
        log.Written += line =>
        {
            // The link is busy with the first line until the test lets it go, so the run's last lines, written meanwhile,
            // are handed on after it; and it takes a moment for each line, so the end would come first if nothing waited.
            if (line.Seq == 1)
            {
                holding.Set();
                held.Wait();
            }

            Thread.Sleep(50);
            lock (server) window.Add(line.Text);
        };
        log.Write(LogLevel.Information, "Recording stopped");
        Assert.That(holding.Wait(Patience), Is.True, "the follower should have been handed the first line");
        log.Write(LogLevel.Information, "Transcript saved to C:\\Users\\gdala\\ClassScribe\\Kalam\\Kalam_2026-10-15_23-00-02Z.md");
        log.Write(LogLevel.Warning, "2 log lines were lost");
        var letGo = Task.Run(async () =>
        {
            await Task.Delay(100);
            held.Set();
        });

        Assert.That(log.WaitForFollowers(Patience), Is.True);
        lock (server) window.Add("end");

        Assert.That(letGo.Wait(Patience), Is.True);
        lock (server)
        {
            Assert.That(window, Is.EqualTo(new[]
            {
                "Recording stopped", "Transcript saved to C:\\Users\\gdala\\ClassScribe\\Kalam\\Kalam_2026-10-15_23-00-02Z.md",
                "2 log lines were lost", "end"
            }));
        }
    }

    /// <summary>
    /// Waiting for the followers gives up after its patience while one is held up, and says so; once it goes on, the wait
    /// succeeds.
    /// </summary>
    [Test]
    public void Waiting_for_the_followers_gives_up_after_its_patience_while_one_is_held_up()
    {
        var log = NewLog();
        using var held = new ManualResetEventSlim();
        log.Written += _ => held.Wait();

        try
        {
            log.Write(LogLevel.Information, "line 1");
            Assert.That(log.WaitForFollowers(TimeSpan.FromMilliseconds(200)), Is.False);
        }
        finally
        {
            held.Set();
        }

        Assert.That(log.WaitForFollowers(Patience), Is.True);
    }

    /// <summary>
    /// When the last follower stops following while a line is still to be handed on, nobody is left to hand it to: the
    /// wait for the followers ends then, and succeeds.
    /// </summary>
    [Test]
    public void Waiting_for_the_followers_ends_when_the_last_one_stops_following()
    {
        var log = NewLog();
        using var holding = new ManualResetEventSlim();
        using var held = new ManualResetEventSlim();
        Action<LogEntry>? follower = null;
        follower = _ =>
        {
            holding.Set();
            held.Wait();
            log.Written -= follower;
        };
        log.Written += follower;
        log.Write(LogLevel.Information, "line 1");
        Assert.That(holding.Wait(Patience), Is.True, "the follower should have been handed the first line");
        log.Write(LogLevel.Information, "line 2");

        var letGo = Task.Run(async () =>
        {
            await Task.Delay(100);
            held.Set();
        });

        Assert.That(log.WaitForFollowers(Patience), Is.True);
        Assert.That(letGo.Wait(Patience), Is.True);
    }

    /// <summary>With nobody following, there is no follower to wait for: the wait succeeds at once.</summary>
    [Test]
    public void Waiting_for_the_followers_while_nobody_follows_succeeds_at_once()
    {
        var log = NewLog();
        log.Write(LogLevel.Information, "nobody follows");

        Assert.That(log.WaitForFollowers(TimeSpan.Zero), Is.True);
    }

    /// <summary>
    /// A follower that fails keeps no line from the others. The first failure is noted in the log itself, as an error with
    /// the exception, and later failures are not, so that a follower failing on every line cannot fill the log.
    /// </summary>
    [Test]
    public async Task A_follower_that_fails_keeps_no_line_from_the_others_and_its_first_failure_is_noted()
    {
        var log = NewLog();
        log.Written += _ => throw new InvalidOperationException("The window has gone.");
        var heard = new ConcurrentQueue<LogEntry>();
        log.Written += heard.Enqueue;

        log.Write(LogLevel.Information, "line 1");
        log.Write(LogLevel.Information, "line 2");
        log.Write(LogLevel.Information, "line 3");
        await Wait.UntilAsync(() => heard.Count >= 4);
        log.Write(LogLevel.Information, "line 4");
        await Wait.UntilAsync(() => heard.Count >= 5);
        await Task.Delay(50);

        Assert.That(heard.Where(line => line.Text.StartsWith("line ", StringComparison.Ordinal)).Select(line => line.Text),
            Is.EqualTo(new[] { "line 1", "line 2", "line 3", "line 4" }));
        var notes = heard.Where(line => line.Level == LogLevel.Error).ToList();
        Assert.That(notes, Has.Count.EqualTo(1), "only the first failure is noted");
        Assert.That(notes[0].Text, Does.Contain("InvalidOperationException: The window has gone."));
        Assert.That(Numbers(heard), Is.Ordered);
        Assert.That(log.Since(0).Lines, Has.Count.EqualTo(5), "the note is in the log, with the lines");
    }

    /// <summary>A follower that has stopped following gets no more lines; the others still do.</summary>
    [Test]
    public async Task A_follower_that_stops_following_gets_no_more_lines()
    {
        var log = NewLog();
        var gone = new ConcurrentQueue<LogEntry>();
        var staying = new ConcurrentQueue<LogEntry>();
        Action<LogEntry> leaving = gone.Enqueue;
        log.Written += leaving;
        log.Written += staying.Enqueue;
        log.Write(LogLevel.Information, "both");
        await Wait.UntilAsync(() => gone.Count >= 1 && staying.Count >= 1);

        log.Written -= leaving;
        log.Write(LogLevel.Information, "one");
        await Wait.UntilAsync(() => staying.Count >= 2);

        Assert.That(gone.Select(line => line.Text), Is.EqualTo(new[] { "both" }));
        Assert.That(staying.Select(line => line.Text), Is.EqualTo(new[] { "both", "one" }));
    }

    // ---- The files ----

    /// <summary>
    /// The lines written before the recording's log is named, while its folder was not known yet, are kept for it: they
    /// reach the file first, followed by the lines written after.
    /// </summary>
    [Test]
    public void Lines_before_WriteFileTo_reach_the_file()
    {
        var log = NewLog();
        var path = _folder.File("Kalam_2026-10-15_23-00-02Z.log");
        log.Write(LogLevel.Information, "starting");
        log.Write(LogLevel.Information, "taking the lock");

        log.WriteFileTo(path);
        log.Write(LogLevel.Information, "recording");

        Assert.That(log.Flush(Patience), Is.True);
        Assert.That(ReadFile(path), Is.EqualTo(Lines("starting", "taking the lock", "recording")));
        Assert.That(log.LostFileLines, Is.Zero);
    }

    /// <summary>
    /// Until the recording's log is named, every line goes to the start-up file at once, where a failure to start is found;
    /// those lines also go to the recording's log once it is named, and the later lines go there alone.
    /// </summary>
    [Test]
    public void Lines_before_the_recordings_log_go_to_the_startup_file_and_also_to_the_recordings_log()
    {
        var startup = _folder.File("recorder.log");
        var path = _folder.File("Kalam_2026-10-15_23-00-02Z.log");
        var log = NewLog(startup);

        log.Write(LogLevel.Information, "starting");
        Assert.That(log.Flush(Patience), Is.True);
        Assert.That(ReadFile(startup), Is.EqualTo(Lines("starting")));

        log.WriteFileTo(path);
        log.Write(LogLevel.Information, "recording");

        Assert.That(log.Flush(Patience), Is.True);
        Assert.That(ReadFile(path), Is.EqualTo(Lines("starting", "recording")));
        Assert.That(ReadFile(startup), Is.EqualTo(Lines("starting")));
        Assert.That(log.StartupFile, Is.EqualTo(startup));
    }

    /// <summary>A run that continues a recording adds its lines to the end of the recording's log, after the earlier run's.</summary>
    [Test]
    public void A_run_that_continues_a_recording_adds_to_the_end_of_its_log()
    {
        var path = _folder.File("Kalam_2026-10-15_23-00-02Z.log");
        File.WriteAllText(path, Lines("19:00:02  Recording \"Kalam\"", "19:47:10  WARNING  The recorder ended unexpectedly"));
        var log = NewLog();
        log.Write(LogLevel.Information, "19:47:12  Continuing the same recording");

        log.WriteFileTo(path);
        log.Write(LogLevel.Information, "19:47:13  Recording");

        Assert.That(log.Flush(Patience), Is.True);
        Assert.That(ReadFile(path), Is.EqualTo(Lines("19:00:02  Recording \"Kalam\"", "19:47:10  WARNING  The recorder ended unexpectedly",
            "19:47:12  Continuing the same recording", "19:47:13  Recording")));
    }

    /// <summary>
    /// EndFile ends the recording's log: the lines after it go to the start-up file, and are kept for the next recording's
    /// log, which gets none of the lines already in the last.
    /// </summary>
    [Test]
    public void EndFile_ends_the_recordings_log_and_keeps_later_lines_for_the_next_one()
    {
        var startup = _folder.File("recorder.log");
        var first = _folder.File("Kalam_2026-10-15_23-00-02Z.log");
        var second = _folder.File("Fiqh_2026-10-16_00-30-00Z.log");
        var log = NewLog(startup);

        log.WriteFileTo(first);
        log.Write(LogLevel.Information, "Kalam");
        log.EndFile();
        log.Write(LogLevel.Information, "between");
        log.WriteFileTo(second);
        log.Write(LogLevel.Information, "Fiqh");

        Assert.That(log.Flush(Patience), Is.True);
        Assert.That(ReadFile(first), Is.EqualTo(Lines("Kalam")));
        Assert.That(ReadFile(second), Is.EqualTo(Lines("between", "Fiqh")));
        Assert.That(ReadFile(startup), Is.EqualTo(Lines("between")));
    }

    /// <summary>
    /// A window logs other work before and between its recordings, such as the transcription of another class, and marks
    /// where each run in it begins (KeepFromNow). Each recording's log then gets the lines from its run's start, those
    /// written before its folder was known included, and none of the lines from before; all of them stay in memory.
    /// </summary>
    [Test]
    public void A_window_keeps_the_lines_from_before_a_run_began_out_of_its_recordings_log()
    {
        var first = _folder.File("Kalam_2026-10-15_23-00-02Z.log");
        var second = _folder.File("Fiqh_2026-10-16_00-30-00Z.log");
        var log = NewLog();
        log.Write(LogLevel.Information, "Transcribing the recording Fiqh_2026-10-14_18-00-00Z");
        log.Write(LogLevel.Information, "Transcript saved to C:\\Users\\gdala\\ClassScribe\\Fiqh\\Fiqh_2026-10-14_18-00-00Z.md");

        log.KeepFromNow();
        log.Write(LogLevel.Information, "Kalam: taking the lock");
        log.WriteFileTo(first);
        log.Write(LogLevel.Information, "Kalam: recording");
        log.EndFile();
        log.Write(LogLevel.Information, "Back in the menu");
        log.KeepFromNow();
        log.Write(LogLevel.Information, "Fiqh: taking the lock");
        log.WriteFileTo(second);
        log.Write(LogLevel.Information, "Fiqh: recording");

        Assert.That(log.Flush(Patience), Is.True);
        Assert.That(ReadFile(first), Is.EqualTo(Lines("Kalam: taking the lock", "Kalam: recording")));
        Assert.That(ReadFile(second), Is.EqualTo(Lines("Fiqh: taking the lock", "Fiqh: recording")));
        Assert.That(log.Since(0).Lines, Has.Count.EqualTo(7));
    }

    /// <summary>
    /// Without a start-up file, as in a window, EndFile lets go of the recording's log once its lines are written, so the
    /// recording's folder can be moved or deleted after the run while ClassScribe stays open.
    /// </summary>
    [Test]
    public void Without_a_startup_file_EndFile_lets_go_of_the_recordings_log()
    {
        var path = _folder.File("Kalam_2026-10-15_23-00-02Z.log");
        var log = NewLog();
        log.WriteFileTo(path);
        log.Write(LogLevel.Information, "Kalam");

        log.EndFile();

        Assert.That(log.Flush(Patience), Is.True);
        Assert.That(ReadFile(path), Is.EqualTo(Lines("Kalam")));
        AssertNothingHolds(path, "the recording's log should have been let go");
    }

    /// <summary>Naming another file while one is open moves the later lines to it; the lines already written stay where they are.</summary>
    [Test]
    public void Naming_another_file_moves_the_later_lines_to_it()
    {
        var first = _folder.File("Kalam_2026-10-15_23-00-02Z.log");
        var second = _folder.File("Fiqh_2026-10-16_00-30-00Z.log");
        var log = NewLog();

        log.WriteFileTo(first);
        log.Write(LogLevel.Information, "Kalam");
        log.WriteFileTo(second);
        log.Write(LogLevel.Information, "Fiqh");

        Assert.That(log.Flush(Patience), Is.True);
        Assert.That(ReadFile(first), Is.EqualTo(Lines("Kalam")));
        Assert.That(ReadFile(second), Is.EqualTo(Lines("Fiqh")));
    }

    /// <summary>The log files can be read while the log writes them, as a window or Notepad reads them during a class.</summary>
    [Test]
    public void The_log_files_can_be_read_while_they_are_written()
    {
        var startup = _folder.File("recorder.log");
        var path = _folder.File("Kalam_2026-10-15_23-00-02Z.log");
        var log = NewLog(startup);
        log.Write(LogLevel.Information, "starting");
        log.WriteFileTo(path);
        log.Write(LogLevel.Information, "line 1");
        Assert.That(log.Flush(Patience), Is.True);

        using (var reading = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            log.Write(LogLevel.Information, "line 2");
            Assert.That(log.Flush(Patience), Is.True);
            using var reader = new StreamReader(reading, Encoding.UTF8);
            Assert.That(reader.ReadToEnd(), Is.EqualTo(Lines("starting", "line 1", "line 2")));
        }

        Assert.That(ReadFile(startup), Is.EqualTo(Lines("starting")));
        Assert.That(log.LostFileLines, Is.Zero);
    }

    /// <summary>
    /// Two logs adding to the same file at once, as two recorders that start together do with recorder.log, never write
    /// over each other's lines: each line goes at the end of the file, wherever the other log has taken it.
    /// </summary>
    [Test, Platform("Win")]
    public void Two_logs_adding_to_one_file_never_write_over_each_others_lines()
    {
        var startup = _folder.File("recorder.log");
        var first = NewLog(startup);
        var second = NewLog(startup);

        foreach (var (log, text) in new[] { (first, "first 1"), (second, "second 1"), (first, "first 2"), (second, "second 2") })
        {
            log.Write(LogLevel.Information, text);
            Assert.That(log.Flush(Patience), Is.True);
        }

        Assert.That(ReadFile(startup), Is.EqualTo(Lines("first 1", "second 1", "first 2", "second 2")));
    }

    /// <summary>
    /// recorder.log is kept to two generations of about a megabyte: once it has reached the limit, it becomes
    /// recorder.old.log, in place of the one before, and a new recorder.log is begun.
    /// </summary>
    [Test]
    public void The_startup_file_keeps_two_generations_of_about_a_megabyte()
    {
        var startup = _folder.File("recorder.log");
        var older = _folder.File("recorder.old.log");
        File.WriteAllText(startup, new string('x', (int)RecorderLog.StartupFileLimit));
        File.WriteAllText(older, Lines("the oldest"));
        var log = NewLog(startup);

        log.Write(LogLevel.Information, "first");
        Assert.That(log.Flush(Patience), Is.True);
        Assert.That(new FileInfo(older).Length, Is.EqualTo(RecorderLog.StartupFileLimit), "the full file became the older generation");
        Assert.That(ReadFile(startup), Is.EqualTo(Lines("first")));

        var longLine = new string('y', (int)RecorderLog.StartupFileLimit);
        log.Write(LogLevel.Information, longLine);
        Assert.That(log.Flush(Patience), Is.True);
        Assert.That(ReadFile(startup), Is.EqualTo(Lines("first", longLine)), "a file is turned over before a line is added, never in the middle of one");

        log.Write(LogLevel.Information, "next");
        Assert.That(log.Flush(Patience), Is.True);
        Assert.That(ReadFile(older), Is.EqualTo(Lines("first", longLine)));
        Assert.That(ReadFile(startup), Is.EqualTo(Lines("next")));
        Assert.That(log.LostFileLines, Is.Zero);
    }

    /// <summary>
    /// While another program holds the older generation, as a virus scanner may, the start-up file cannot be turned over:
    /// the lines go on into the full file, and none is lost. Once it is let go, the next lines turn the file over.
    /// </summary>
    [Test]
    public void While_another_program_holds_the_older_file_the_lines_go_on_into_the_full_one()
    {
        var startup = _folder.File("recorder.log");
        var older = _folder.File("recorder.old.log");
        File.WriteAllText(startup, new string('x', (int)RecorderLog.StartupFileLimit));
        File.WriteAllText(older, Lines("the oldest"));
        var log = NewLog(startup);

        // Opened without sharing delete, the older file cannot be replaced while this is open.
        using (new FileStream(older, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            log.Write(LogLevel.Information, "held");
            Assert.That(log.Flush(Patience), Is.True);
        }

        Assert.That(ReadFile(older), Is.EqualTo(Lines("the oldest")));
        Assert.That(ReadFile(startup), Does.EndWith("x" + Lines("held")).And.Length.EqualTo(RecorderLog.StartupFileLimit + Lines("held").Length));
        Assert.That(log.LostFileLines, Is.Zero);

        log.Write(LogLevel.Information, "let go");
        Assert.That(log.Flush(Patience), Is.True);
        Assert.That(ReadFile(startup), Is.EqualTo(Lines("let go")));
        Assert.That(ReadFile(older), Does.EndWith("x" + Lines("held")));
    }

    /// <summary>
    /// While another program holds recorder.old.log for a moment, as a virus scanner may, turning recorder.log over is
    /// tried again, about 100 ms apart, up to three times: here the older file is let go after the second try fails, so the
    /// third turns the file over, and no line is lost.
    /// </summary>
    [Test]
    public void Turning_the_startup_file_over_is_tried_again_while_another_program_holds_the_older_file()
    {
        var startup = _folder.File("recorder.log");
        var older = _folder.File("recorder.old.log");
        File.WriteAllText(startup, new string('x', (int)RecorderLog.StartupFileLimit));
        File.WriteAllText(older, Lines("the oldest"));
        var clock = Stopwatch.StartNew();
        var tries = new ConcurrentQueue<TimeSpan>();
        // Opened without sharing delete, the older file cannot be replaced while this is open.
        var holding = new FileStream(older, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            var log = NewLog(startup, move: (from, to) =>
            {
                tries.Enqueue(clock.Elapsed);
                try
                {
                    RecorderLog.MoveReplacing(from, to);
                }
                finally
                {
                    if (tries.Count == 2) holding.Dispose();
                }
            });

            log.Write(LogLevel.Information, "first");

            Assert.That(log.Flush(Patience), Is.True);
            Assert.That(ReadFile(startup), Is.EqualTo(Lines("first")), "the full file should have been turned over");
            Assert.That(new FileInfo(older).Length, Is.EqualTo(RecorderLog.StartupFileLimit));
            Assert.That(log.LostFileLines, Is.Zero);
            var times = tries.ToArray();
            Assert.That(times, Has.Length.EqualTo(3));
            Assert.That(times[1] - times[0], Is.GreaterThanOrEqualTo(ShortestPause));
            Assert.That(times[2] - times[1], Is.GreaterThanOrEqualTo(ShortestPause));
        }
        finally
        {
            holding.Dispose();
        }
    }

    /// <summary>
    /// A file another program holds for a moment, as a virus scanner may when it was just written, is opened once it is
    /// let go, on a try about 100 ms after the first: no line is lost.
    /// </summary>
    [Test]
    public void A_file_held_for_a_moment_by_another_program_is_opened_once_it_is_let_go()
    {
        var clock = Stopwatch.StartNew();
        var tries = new ConcurrentQueue<TimeSpan>();
        var file = new MemoryStream();
        var log = NewLog(open: _ =>
        {
            tries.Enqueue(clock.Elapsed);
            return tries.Count == 1
                ? throw new IOException("The process cannot access the file because it is being used by another process.")
                : file;
        });
        log.WriteFileTo(_folder.File("Kalam_2026-10-15_23-00-02Z.log"));

        log.Write(LogLevel.Information, "line 1");

        Assert.That(log.Flush(Patience), Is.True);
        Assert.That(Encoding.UTF8.GetString(file.ToArray()), Is.EqualTo(Lines("line 1")));
        Assert.That(log.LostFileLines, Is.Zero);
        var times = tries.ToArray();
        Assert.That(times, Has.Length.EqualTo(2));
        Assert.That(times[1] - times[0], Is.GreaterThanOrEqualTo(ShortestPause), "the second try should come after a pause");
    }

    /// <summary>
    /// Writing a line never waits for the disk: while the file is stalled, lines are still written at once, and once it
    /// takes them again they all reach it.
    /// </summary>
    [Test]
    public void Write_never_blocks_when_the_file_stalls()
    {
        var stall = NewStall();
        var log = NewLog(open: _ => stall);
        log.WriteFileTo(_folder.File("Kalam_2026-10-15_23-00-02Z.log"));
        WriteAtOnce(log, "line 0");
        Assert.That(stall.Entered.Wait(Patience), Is.True, "the file should have been asked to take the first line");

        WriteAtOnce(log, [.. Enumerable.Range(1, 100).Select(line => $"line {line}")]);

        Assert.That(log.Since(0).Lines, Has.Count.EqualTo(101));
        stall.Release();
        Assert.That(log.Flush(Patience), Is.True);
        Assert.That(stall.Text, Is.EqualTo(Lines([.. Enumerable.Range(0, 101).Select(line => $"line {line}")])));
        Assert.That(log.LostFileLines, Is.Zero);
    }

    /// <summary>
    /// While the file is stalled, the lines waiting for it are held up to the queue's limit; the lines beyond it are not
    /// held but counted, and the run reports the count. They are still in memory for the windows.
    /// </summary>
    [Test]
    public void Lines_beyond_the_files_queue_are_counted_as_lost()
    {
        var stall = NewStall();
        var log = NewLog(open: _ => stall);
        log.WriteFileTo(_folder.File("Kalam_2026-10-15_23-00-02Z.log"));
        WriteAtOnce(log, "line 0");
        Assert.That(stall.Entered.Wait(Patience), Is.True, "the file should have been asked to take the first line");

        WriteAtOnce(log, [.. Enumerable.Range(1, RecorderLog.FileQueueCapacity + 5).Select(line => $"line {line}")]);

        Assert.That(log.LostFileLines, Is.EqualTo(5));
        stall.Release();
        Assert.That(log.Flush(Patience), Is.True);
        Assert.That(stall.Text, Is.EqualTo(Lines([.. Enumerable.Range(0, RecorderLog.FileQueueCapacity + 1).Select(line => $"line {line}")])));
        Assert.That(log.LostFileLines, Is.EqualTo(5));
        Assert.That(log.Since(0).Lines, Has.Count.EqualTo(RecorderLog.FileQueueCapacity + 6));
    }

    /// <summary>
    /// Waits for the lines written so far and then for the note about the first failure, which the log writes while those
    /// lines fail, before Flush returns: a second Flush covers the note's own try at the file.
    /// </summary>
    private static void FlushWithTheNote(RecorderLog log)
    {
        Assert.That(log.Flush(Patience), Is.True);
        Assert.That(log.Flush(Patience), Is.True);
    }

    /// <summary>
    /// Lines the file cannot take, because it cannot be opened, are counted as lost and stay in memory, with the note about
    /// the failure, which did not reach the file either; once the file can be opened again, the next lines go in.
    /// </summary>
    [Test]
    public void Lines_a_file_that_cannot_be_opened_does_not_take_are_counted_and_later_lines_go_in()
    {
        var failing = true;
        var file = new MemoryStream();
        var log = NewLog(open: _ => Volatile.Read(ref failing) ? throw new UnauthorizedAccessException("Access to the path is denied.") : file);
        log.WriteFileTo(_folder.File("Kalam_2026-10-15_23-00-02Z.log"));

        for (var line = 1; line <= 3; line++) log.Write(LogLevel.Information, $"line {line}");
        FlushWithTheNote(log);
        Assert.That(log.LostFileLines, Is.EqualTo(4), "the three lines and the note");
        Assert.That(log.Since(0).Lines.Select(line => line.Level), Is.EqualTo(new[] { LogLevel.Information, LogLevel.Information, LogLevel.Information, LogLevel.Error }));

        Volatile.Write(ref failing, false);
        log.Write(LogLevel.Information, "line 4");
        Assert.That(log.Flush(Patience), Is.True);
        Assert.That(Encoding.UTF8.GetString(file.ToArray()), Is.EqualTo(Lines("line 4")));
        Assert.That(log.LostFileLines, Is.EqualTo(4));
    }

    /// <summary>
    /// Lines a full disk refuses are counted as lost, with the note about the failure; the file is opened again for the next
    /// lines, which go in once the disk takes them.
    /// </summary>
    [Test]
    public void Lines_a_full_disk_refuses_are_counted_and_the_file_is_opened_again_for_the_next()
    {
        var full = true;
        var refused = new ConcurrentQueue<RefusingStream>();
        var fine = new MemoryStream();
        var log = NewLog(open: _ =>
        {
            if (!Volatile.Read(ref full)) return fine;
            var refusing = new RefusingStream();
            refused.Enqueue(refusing);
            return refusing;
        });
        log.WriteFileTo(_folder.File("Kalam_2026-10-15_23-00-02Z.log"));

        log.Write(LogLevel.Information, "line 1");
        log.Write(LogLevel.Information, "line 2");
        FlushWithTheNote(log);
        Assert.That(log.LostFileLines, Is.EqualTo(3), "the two lines and the note");
        Assert.That(refused, Is.Not.Empty);
        Assert.That(refused.Select(file => file.Disposed), Is.All.True, "a file that failed is let go, so the next lines open it again");

        Volatile.Write(ref full, false);
        log.Write(LogLevel.Information, "line 3");
        Assert.That(log.Flush(Patience), Is.True);
        Assert.That(Encoding.UTF8.GetString(fine.ToArray()), Is.EqualTo(Lines("line 3")));
        Assert.That(log.LostFileLines, Is.EqualTo(3));
    }

    /// <summary>
    /// The first time a file refuses lines, the log says why, once: an error line naming the file, with Windows' message,
    /// which the windows are handed at once, and FirstFileFailure, which the run names with the count of lost lines at its
    /// end, since a window that records shows what the run logs but not the log's own lines. Later failures are not noted,
    /// so that a full disk cannot fill the log with notes; the note itself did not reach the full disk either.
    /// </summary>
    [Test]
    public async Task The_first_file_failure_is_noted_once_with_the_file_and_the_reason()
    {
        var path = _folder.File("Kalam_2026-10-15_23-00-02Z.log");
        var log = NewLog(open: _ => new RefusingStream());
        var heard = new ConcurrentQueue<LogEntry>();
        log.Written += heard.Enqueue;
        log.WriteFileTo(path);
        Assert.That(log.FirstFileFailure, Is.Null);

        log.Write(LogLevel.Information, "line 1");
        Assert.That(log.Flush(Patience), Is.True);
        log.Write(LogLevel.Information, "line 2");
        Assert.That(log.Flush(Patience), Is.True);

        var notes = log.Since(0).Lines.Where(line => line.Level == LogLevel.Error).ToList();
        Assert.That(notes, Has.Count.EqualTo(1), "only the first failure is noted");
        Assert.That(notes[0].Text, Does.Contain($"ERROR  Could not write the log file {path}").And.Contain("IOException: There is not enough space on the disk."));
        Assert.That(log.FirstFileFailure, Is.EqualTo($"Could not write the log file {path}: There is not enough space on the disk."));
        Assert.That(log.LostFileLines, Is.EqualTo(3));
        await Wait.UntilAsync(() => heard.Any(line => line.Level == LogLevel.Error));
    }

    /// <summary>Flush gives up after its patience when the file is stalled, and says so; once the file takes the lines, it succeeds.</summary>
    [Test]
    public void Flush_gives_up_after_its_patience_while_the_file_stalls()
    {
        var stall = NewStall();
        var log = NewLog(open: _ => stall);
        log.WriteFileTo(_folder.File("Kalam_2026-10-15_23-00-02Z.log"));
        WriteAtOnce(log, "line 1");

        Assert.That(log.Flush(TimeSpan.FromMilliseconds(200)), Is.False);

        stall.Release();
        Assert.That(log.Flush(Patience), Is.True);
        Assert.That(stall.Text, Is.EqualTo(Lines("line 1")));
    }

    /// <summary>With no file to write, there is nothing to wait for: Flush succeeds at once.</summary>
    [Test]
    public void Flush_without_a_file_succeeds_at_once()
    {
        var log = NewLog();
        log.Write(LogLevel.Information, "kept in memory");

        Assert.That(log.Flush(TimeSpan.Zero), Is.True);
        Assert.That(log.LostFileLines, Is.Zero);
    }

    /// <summary>
    /// Dispose writes the lines still waiting and closes the file, so nothing holds it afterwards. A line written later, as
    /// a part of ClassScribe that is shutting down may write, is still held in memory, and counted as lost to the file,
    /// which it does not make again once deleted.
    /// </summary>
    [Test]
    public void Dispose_writes_what_is_left_and_closes_the_file()
    {
        var path = _folder.File("Kalam_2026-10-15_23-00-02Z.log");
        var log = NewLog();
        log.WriteFileTo(path);
        for (var line = 1; line <= 3; line++) log.Write(LogLevel.Information, $"line {line}");

        log.Dispose();

        Assert.That(ReadFile(path), Is.EqualTo(Lines("line 1", "line 2", "line 3")));
        AssertNothingHolds(path, "the file should be closed");
        File.Delete(path);

        log.Write(LogLevel.Information, "late");
        Assert.That(log.Since(3).Lines.Select(line => line.Text), Is.EqualTo(new[] { "late" }));
        Assert.That(log.LostFileLines, Is.EqualTo(1));
        Assert.That(File.Exists(path), Is.False);
        Assert.Throws<ObjectDisposedException>(() => log.WriteFileTo(path));
        Assert.DoesNotThrow(log.EndFile);
        Assert.DoesNotThrow(log.Dispose, "disposing twice");
    }

    /// <summary>Dispose does not wait for ever for a stalled file: it gives up after its patience, and the process can end.</summary>
    [Test]
    public void Dispose_does_not_wait_for_ever_for_a_stalled_file()
    {
        var stall = NewStall();
        var log = NewLog(open: _ => stall, drainPatience: TimeSpan.FromMilliseconds(200));
        log.WriteFileTo(_folder.File("Kalam_2026-10-15_23-00-02Z.log"));
        WriteAtOnce(log, "line 1");
        Assert.That(stall.Entered.Wait(Patience), Is.True);

        var disposing = Task.Run(log.Dispose);

        Assert.That(disposing.Wait(Patience), Is.True, "Dispose waited for a stalled file");
    }

    // ---- The logger provider ----

    /// <summary>Formats one entry as the console's plain format writes it.</summary>
    private static string Plain(LogLevel level, string message, Exception? exception)
    {
        using var writer = new StringWriter();
        var entry = new LogEntry<string>(level, "ClassScribe.Commands.RecordCommand", new EventId(0), message, exception, (state, _) => state);
        new PlainConsoleFormatter().Write(in entry, null, writer);
        return writer.ToString();
    }

    /// <summary>
    /// What the provider writes into the log is the text the console's plain format writes, without the line break at its
    /// end, at every level, the lines of a failure's exceptions included.
    /// </summary>
    [Test]
    public void The_providers_text_is_what_the_plain_console_format_writes()
    {
        var log = NewLog();
        using var provider = new RecorderLogProvider(log);
        var logger = provider.CreateLogger("ClassScribe.Commands.RecordCommand");
        (LogLevel Level, string Message, Exception? Exception)[] cases =
        [
            (LogLevel.Information, "Recording \"Kalam\" to C:\\Users\\gdala\\ClassScribe\\Kalam", null),
            (LogLevel.Debug, "Reopening the capture: no audio for 5 seconds", null),
            (LogLevel.Warning, "No sound for 60 seconds. Is the class playing?", null),
            (LogLevel.Error, "Could not upload the recording", new InvalidOperationException("Could not upload.", new IOException("Connection reset."))),
            (LogLevel.Critical, "ClassScribe failed", null)
        ];

        foreach (var (level, message, exception) in cases)
        {
            var before = Plain(level, message, exception);
            logger.Log(level, new EventId(0), message, exception, (state, _) => state);
            var after = Plain(level, message, exception);

            var written = log.Since(0).Lines[^1];
            // The plain format reads the clock itself: a second may begin between the two formats, so the line matches one of them.
            Assert.That(written.Text + Environment.NewLine, Is.EqualTo(before).Or.EqualTo(after), $"at {level}");
            Assert.That(written.Level, Is.EqualTo(level));
        }

        Assert.That(log.Since(0).Lines, Has.Count.EqualTo(cases.Length));
    }

    /// <summary>A message with placeholders is written with its values filled in, as the console shows it.</summary>
    [Test]
    public void A_message_with_placeholders_is_written_with_its_values()
    {
        var log = NewLog();
        using var provider = new RecorderLogProvider(log);

        provider.CreateLogger("ClassScribe.Commands.RecordCommand").LogWarning("Mark {Number} at {Position}", 3, "00:42:13");

        Assert.That(log.Since(0).Lines.Single().Text, Does.Match(@"^\d\d:\d\d:\d\d  WARNING  Mark 3 at 00:42:13$"));
    }

    /// <summary>A message the plain format writes nothing for, an empty one without an exception, adds no line.</summary>
    [Test]
    public void An_empty_message_adds_no_line()
    {
        var log = NewLog();
        using var provider = new RecorderLogProvider(log);

        provider.CreateLogger("ClassScribe").Log(LogLevel.Information, new EventId(0), "", null, (state, _) => state);

        Assert.That(log.Since(0).Lines, Is.Empty);
    }

    /// <summary>The provider writes into the log and nowhere else: nothing reaches the console, which the recorder does not have.</summary>
    [Test]
    public void The_provider_writes_into_the_log_and_nowhere_else()
    {
        var log = NewLog();
        using var provider = new RecorderLogProvider(log);
        var logger = provider.CreateLogger("ClassScribe.Commands.RecordCommand");
        var output = Console.Out;
        var errors = Console.Error;
        using var shownOutput = new StringWriter();
        using var shownErrors = new StringWriter();

        try
        {
            Console.SetOut(shownOutput);
            Console.SetError(shownErrors);
            logger.LogInformation("Recording \"Kalam\"");
            logger.LogError(new IOException("The disk is full."), "Could not write the recording");
        }
        finally
        {
            Console.SetOut(output);
            Console.SetError(errors);
        }

        Assert.That(shownOutput.ToString(), Is.Empty);
        Assert.That(shownErrors.ToString(), Is.Empty);
        Assert.That(log.Since(0).Lines, Has.Count.EqualTo(2));
    }

    /// <summary>
    /// A container with the provider among the loggers, and a service that takes a logger and follows the log, as the
    /// window link does, can be built and resolved: the log takes no logger, so there is no cycle. What that service logs
    /// comes back to it through the log.
    /// </summary>
    [Test]
    public async Task A_container_with_the_provider_and_a_follower_that_logs_resolves_without_a_cycle()
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(_time);
        services.AddSingleton<RecorderLog>();
        services.AddLogging(logging => logging.Services.AddSingleton<ILoggerProvider, RecorderLogProvider>());
        services.AddSingleton<LogFollower>();

        await using var container = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        var follower = container.GetRequiredService<LogFollower>();
        follower.Say("Recording \"Kalam\"");

        await Wait.UntilAsync(() => !follower.Heard.IsEmpty);
        Assert.That(follower.Heard.Single().Text, Does.EndWith("  Recording \"Kalam\""));
        Assert.That(container.GetRequiredService<RecorderLog>().Since(0).Lines, Has.Count.EqualTo(1));
        Assert.That(container.GetRequiredService<RecorderLog>().StartupFile, Is.Null, "the container made the log without a start-up file");
    }

    /// <summary>A service that takes a logger and follows the log, as the window link does.</summary>
    public sealed class LogFollower
    {
        private readonly ILogger<LogFollower> _logger;

        /// <summary>Follows <paramref name="log"/>, and logs through <paramref name="logger"/>.</summary>
        public LogFollower(RecorderLog log, ILogger<LogFollower> logger)
        {
            _logger = logger;
            log.Written += Heard.Enqueue;
        }

        /// <summary>The lines the log has handed on.</summary>
        public ConcurrentQueue<LogEntry> Heard { get; } = new();

        /// <summary>Logs <paramref name="text"/> as information.</summary>
        public void Say(string text) => _logger.LogInformation("{Text}", text);
    }

    /// <summary>
    /// A file whose writes wait until the test lets them go, as a stalled disk does. It keeps what it is given, so the test
    /// can read it back.
    /// </summary>
    private sealed class StallingStream : Stream
    {
        private readonly ManualResetEventSlim _released = new();
        private readonly MemoryStream _kept = new();

        /// <summary>Set when the first write has begun to wait.</summary>
        public ManualResetEventSlim Entered { get; } = new();

        /// <summary>What has been written, as text.</summary>
        public string Text
        {
            get { lock (_kept) return Encoding.UTF8.GetString(_kept.ToArray()); }
        }

        /// <summary>Lets every write go on, those waiting now and those to come.</summary>
        public void Release() => _released.Set();

        /// <inheritdoc />
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        /// <inheritdoc />
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Entered.Set();
            _released.Wait();
            lock (_kept) _kept.Write(buffer);
        }

        /// <inheritdoc />
        public override void Flush()
        {
        }

        /// <inheritdoc />
        public override bool CanRead => false;

        /// <inheritdoc />
        public override bool CanSeek => false;

        /// <inheritdoc />
        public override bool CanWrite => true;

        /// <inheritdoc />
        public override long Length => throw new NotSupportedException();

        /// <inheritdoc />
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        /// <inheritdoc />
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        /// <inheritdoc />
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        /// <inheritdoc />
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    /// <summary>A file on a full disk: every write fails, as Windows fails it.</summary>
    private sealed class RefusingStream : Stream
    {
        /// <summary>True once the file has been let go.</summary>
        public bool Disposed { get; private set; }

        /// <inheritdoc />
        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("There is not enough space on the disk.");

        /// <inheritdoc />
        public override void Write(ReadOnlySpan<byte> buffer) => throw new IOException("There is not enough space on the disk.");

        /// <inheritdoc />
        public override void Flush()
        {
        }

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }

        /// <inheritdoc />
        public override bool CanRead => false;

        /// <inheritdoc />
        public override bool CanSeek => false;

        /// <inheritdoc />
        public override bool CanWrite => true;

        /// <inheritdoc />
        public override long Length => throw new NotSupportedException();

        /// <inheritdoc />
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        /// <inheritdoc />
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        /// <inheritdoc />
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        /// <inheritdoc />
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
