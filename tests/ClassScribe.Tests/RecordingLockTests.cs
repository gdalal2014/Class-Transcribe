using System.Diagnostics;
using ClassScribe.Recorder;

namespace ClassScribe.Tests;

/// <summary>
/// One recording at a time: the lock file in ClassScribe's own folder, held open without sharing for as long as a run
/// captures. Another holder makes a take fail after a few short tries; letting go, or the holder's process ending, frees
/// it. The files are real, in a temporary folder that stands for %LOCALAPPDATA%\ClassScribe.
/// </summary>
[TestFixture]
public class RecordingLockTests
{
    private TempFolder _temp = null!;
    private RuntimeFolder _folder = null!;
    private RecordingLogger<RecordingLock> _log = null!;
    private List<TimeSpan> _pauses = null!;

    /// <summary>Makes the folder, a log to inspect, and a list of the pauses taken before each new try.</summary>
    [SetUp]
    public void SetUp()
    {
        _temp = new TempFolder();
        _folder = new RuntimeFolder(_temp.Path);
        _log = new RecordingLogger<RecordingLock>();
        _pauses = [];
    }

    /// <summary>Deletes the folder, which fails if a test left the lock file open.</summary>
    [TearDown]
    public void TearDown() => _temp.Dispose();

    /// <summary>A lock over the test's folder that notes each pause before a new try instead of sleeping, unless <paramref name="pause"/> says what to do.</summary>
    private RecordingLock Lock(Action<TimeSpan>? pause = null) => new(_folder, _log, pause ?? _pauses.Add);

    /// <summary>A second take fails while the first is held, and succeeds once the first is let go.</summary>
    [Test]
    public void A_second_take_fails_while_the_first_is_held_and_succeeds_once_it_is_let_go()
    {
        var first = Lock().TryTake();
        var whileHeld = Lock().TryTake();
        first?.Dispose();
        _pauses.Clear();
        using var afterwards = Lock().TryTake();

        Assert.That(first, Is.Not.Null);
        Assert.That(whileHeld, Is.Null);
        Assert.That(afterwards, Is.Not.Null);
        Assert.That(_pauses, Is.Empty, "taken at once");
    }

    /// <summary>
    /// A held lock is tried three more times, 100 ms apart, before it counts as held: a window looking whether it is held,
    /// or a virus scanner, holds the file for a moment only, and must not make a recorder refuse to record.
    /// </summary>
    [Test]
    public void A_held_lock_is_tried_three_more_times_before_it_counts_as_held()
    {
        using var held = Lock().TryTake();

        var second = Lock().TryTake();

        Assert.That(second, Is.Null);
        Assert.That(_pauses, Is.EqualTo(Enumerable.Repeat(TimeSpan.FromMilliseconds(100), 3)));
    }

    /// <summary>A lock let go while a take tries it again is taken.</summary>
    [Test]
    public void A_lock_let_go_while_a_take_tries_again_is_taken()
    {
        var held = Lock().TryTake();

        using var taken = Lock(_ =>
        {
            held?.Dispose();
            held = null;
        }).TryTake();

        Assert.That(taken, Is.Not.Null);
    }

    /// <summary>Taking the lock makes ClassScribe's own folder when it is not there yet, and keeps the lock file in it.</summary>
    [Test]
    public void Taking_the_lock_makes_the_folder_it_is_kept_in()
    {
        var folder = new RuntimeFolder(_temp.File("not there yet"));

        using var taken = new RecordingLock(folder, _log, _pauses.Add).TryTake();

        Assert.That(taken, Is.Not.Null);
        Assert.That(File.Exists(folder.LockFile), Is.True);
        Assert.That(folder.LockFile, Is.EqualTo(Path.Combine(folder.Root, "recording.lock")));
    }

    /// <summary>
    /// Whether the lock is held is told without taking it: not before anything took it, yes while it is held, by this
    /// process or another, and not once it is let go; and a take after the look succeeds.
    /// </summary>
    [Test]
    public void Whether_the_lock_is_held_is_told_without_taking_it()
    {
        var beforeAnyTake = Lock().IsHeld();
        var taken = Lock().TryTake();
        var whileHeld = Lock().IsHeld();
        taken?.Dispose();
        var afterwards = Lock().IsHeld();
        using var again = Lock().TryTake();

        Assert.That(beforeAnyTake, Is.False);
        Assert.That(whileHeld, Is.True);
        Assert.That(afterwards, Is.False);
        Assert.That(again, Is.Not.Null);
        Assert.That(_pauses, Is.Empty, "looking never makes a take try again");
    }

    /// <summary>
    /// A lock file that cannot be opened for another reason than another holder, here because a folder stands where it
    /// should be, is an error, not another recording: it is not tried again, and the caller learns what is wrong.
    /// </summary>
    [Test]
    public void A_lock_file_that_cannot_be_opened_for_another_reason_is_an_error_not_another_recording()
    {
        Directory.CreateDirectory(_folder.LockFile);

        Assert.Throws<UnauthorizedAccessException>(() => Lock().TryTake());
        Assert.That(_pauses, Is.Empty);
        Assert.That(_log.Messages.Single(), Does.StartWith($"Could not open the lock file {_folder.LockFile}"));
    }

    /// <summary>
    /// A lock file that cannot be looked at for another reason than a holder or its absence is an error for the caller, not
    /// an answer either way, and it is logged: here a folder stands where the lock file should be, or the lock's folder has
    /// a name Windows refuses.
    /// </summary>
    [Test]
    public void A_lock_file_that_cannot_be_looked_at_for_another_reason_is_an_error_not_an_answer()
    {
        Directory.CreateDirectory(_folder.LockFile);
        var refusedFolder = new RuntimeFolder(_temp.File("not|allowed"));
        var refusedName = new RecordingLock(refusedFolder, _log, _pauses.Add);

        Assert.Throws<UnauthorizedAccessException>(() => Lock().IsHeld());
        Assert.Throws<IOException>(() => refusedName.IsHeld());
        Assert.That(_log.Messages, Is.EqualTo(new[] { _folder.LockFile, refusedFolder.LockFile }.Select(path => $"Could not look at the lock file {path}")));
    }

    /// <summary>Letting the lock go twice does no harm, and the second time does not let go a lock someone else took meanwhile.</summary>
    [Test]
    public void Letting_the_lock_go_twice_does_no_harm()
    {
        var first = Lock().TryTake()!;
        first.Dispose();
        using var second = Lock().TryTake();

        Assert.DoesNotThrow(first.Dispose);
        Assert.That(Lock().IsHeld(), Is.True, "the second take still holds it");
    }

    /// <summary>
    /// A lock held by another process stops a take, and is free again once that process ends, however it ends: Windows
    /// closes the file when the process goes. The other process is cmd.exe, without a window, holding the lock file open
    /// while its built-in pause waits for a key that never comes; the test ends it. Right after it ends, the file cmd.exe
    /// wrote into may still be held for a moment, by Windows or a virus scanner looking at it, which the take's tries,
    /// 100 ms apart as in ClassScribe, ride over.
    /// </summary>
    [Test, Category("WindowsProcess"), Platform("Win")]
    public async Task A_lock_held_by_a_process_that_ends_is_free_again()
    {
        Directory.CreateDirectory(_folder.Root);
        var start = new ProcessStartInfo("cmd.exe", $"/d /c pause > \"{_folder.LockFile}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true
        };

        using var holder = Process.Start(start)!;
        bool takenWhileHeld;
        try
        {
            // Pause's prompt in the file shows that cmd.exe has opened it and waits.
            await Wait.UntilAsync(() => LengthOf(_folder.LockFile) > 0);
            using var whileHeld = Lock().TryTake();
            takenWhileHeld = whileHeld is not null;
        }
        finally
        {
            holder.Kill();
            await holder.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }

        _pauses.Clear();
        using var afterwards = Lock(pause =>
        {
            _pauses.Add(pause);
            Thread.Sleep(pause);
        }).TryTake();

        Assert.That(takenWhileHeld, Is.False, "cmd.exe held it");
        Assert.That(afterwards, Is.Not.Null, $"free once cmd.exe ended, after {_pauses.Count} pauses");
        TestContext.Out.WriteLine($"Taken after {_pauses.Count} pauses of 100 ms once cmd.exe had ended.");
    }

    /// <summary>The length of the file at <paramref name="path"/>, or 0 while it is not there.</summary>
    private static long LengthOf(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (FileNotFoundException)
        {
            return 0;
        }
    }
}
