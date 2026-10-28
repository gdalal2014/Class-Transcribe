using ClassScribe.Recorder;
using ClassScribe.Settings;
using ClassScribe.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClassScribe.Tests;

/// <summary>
/// The contracts of the ports of runs (architecture 4.3, slice 3b2) over their real adapters, in temporary folders that stand
/// for %LOCALAPPDATA%\ClassScribe: ClassScribe's own folder (<see cref="IRuntimeFolder"/>) and one recording at a time
/// (<see cref="IRecordingLock"/>); and that the window's host and the recorder's resolve each port to the very adapter they
/// hold.
/// </summary>
[TestFixture]
public class RecorderPortTests
{
    private TempFolder _temp = null!;
    private TestRuntime _runtime = null!;

    /// <summary>A temporary folder of the test's own, and ClassScribe's own folder in another.</summary>
    [SetUp]
    public void SetUp()
    {
        _temp = new TempFolder();
        _runtime = new TestRuntime();
    }

    /// <summary>Deletes the folders.</summary>
    [TearDown]
    public void TearDown()
    {
        _runtime.Dispose();
        _temp.Dispose();
    }

    /// <summary>The lock port over the adapter at <paramref name="folder"/>, which notes nothing and waits for nothing before a new try.</summary>
    private static IRecordingLock Lock(RuntimeFolder folder) => new RecordingLock(folder, NullLogger<RecordingLock>.Instance, _ => { });

    /// <summary>The folder port names its root as a full path, its runs folder and its lock file, makes nothing until asked, and makes both folders when asked whether it can be written.</summary>
    [Test]
    public void The_folder_port_names_its_paths_and_makes_them_only_when_asked_whether_it_can_be_written()
    {
        var root = Path.Combine(_temp.Path, "ClassScribe");
        IRuntimeFolder folder = new RuntimeFolder(Path.Combine(_temp.Path, "Elsewhere", "..", "ClassScribe"));

        Assert.Multiple(() =>
        {
            Assert.That(folder.Root, Is.EqualTo(root), "a full path, with .. resolved");
            Assert.That(folder.Runs, Is.EqualTo(Path.Combine(root, "runs")));
            Assert.That(folder.LockFile, Is.EqualTo(Path.Combine(root, "recording.lock")));
            Assert.That(Directory.Exists(root), Is.False, "nothing is made until it is needed");
        });

        var problem = folder.DescribeWriteProblem();

        Assert.Multiple(() =>
        {
            Assert.That(problem, Is.Null);
            Assert.That(folder.CanWrite(), Is.True);
            Assert.That(Directory.Exists(folder.Runs), Is.True, "the check makes the folder and its runs folder");
            Assert.That(Directory.GetFiles(root), Is.Empty, "the probe files are gone");
            Assert.That(Directory.GetFiles(folder.Runs), Is.Empty);
        });
    }

    /// <summary>A folder that cannot be made, because a file stands where it should be, cannot be written, and the problem names the folder and why.</summary>
    [Test]
    public void The_folder_port_says_what_is_wrong_when_it_cannot_be_written()
    {
        var blocker = _temp.File("blocker");
        File.WriteAllText(blocker, "a file where the folder should be");
        IRuntimeFolder folder = new RuntimeFolder(Path.Combine(blocker, "ClassScribe"));

        Assert.Multiple(() =>
        {
            Assert.That(folder.CanWrite(), Is.False);
            Assert.That(folder.DescribeWriteProblem(), Does.StartWith($"{folder.Root} cannot be written: ").And.Not.EndWith(": "));
        });
    }

    /// <summary>
    /// The lock port takes the lock, making the folder and the lock file; while it is held another take gives null and a
    /// look says it is held; letting go, once or twice, frees it for the next take, and before the first take nothing holds it.
    /// </summary>
    [Test]
    public void The_lock_port_takes_holds_and_lets_go()
    {
        var folder = new RuntimeFolder(_temp.File("ClassScribe"));
        var first = Lock(folder);
        var second = Lock(folder);

        var heldBefore = second.IsHeld();
        var held = first.TryTake();
        var whileHeld = (second.IsHeld(), second.TryTake());
        held?.Dispose();
        held?.Dispose();
        var heldAfter = second.IsHeld();
        using var again = second.TryTake();

        Assert.Multiple(() =>
        {
            Assert.That(heldBefore, Is.False, "no lock file yet: nobody has ever held it");
            Assert.That(held, Is.Not.Null);
            Assert.That(File.Exists(folder.LockFile), Is.True, "the folder and the lock file were made");
            Assert.That(whileHeld, Is.EqualTo((true, (IDisposable?)null)), "held by the first");
            Assert.That(heldAfter, Is.False);
            Assert.That(again, Is.Not.Null, "free once let go; letting go twice does nothing more");
        });
    }

    /// <summary>A lock whose folder cannot be made throws from a take, as the port says: it says nothing about another recording.</summary>
    [Test]
    public void The_lock_port_throws_when_its_folder_cannot_be_made()
    {
        var blocker = _temp.File("blocker");
        File.WriteAllText(blocker, "a file where the folder should be");
        var refused = Lock(new RuntimeFolder(Path.Combine(blocker, "ClassScribe")));

        Assert.That(() => refused.TryTake(), Throws.InstanceOf<IOException>());
    }

    /// <summary>
    /// Both hosts resolve each port to the adapter they hold: the window's to the folder registered last (a test's, as
    /// ClassScribe's own folder) and the lock over it; the recorder's to the folder it was given.
    /// </summary>
    [Test]
    public void Both_hosts_resolve_the_ports_to_the_adapters_they_hold()
    {
        File.WriteAllText(_temp.File(SettingsFile.FileName), "{}");
        var places = new SettingsPlaces(_temp.Path, userFolder: null);
        using var window = AppHost.Build(places, "test-key", services =>
            services.AddSingleton(_runtime.Folder).AddSingleton(new RoamingFolder(_temp.File("Roaming"))));
        var runId = TestRuns.Id();
        using var log = new RecorderLog(_runtime.Time);
        using var recorder = AppHost.BuildRecorder(new RecorderStart(runId, TestRuns.Request(runId), new Dictionary<string, string?>()), "test-key", log, _runtime.Folder, places, _runtime.Time);

        Assert.Multiple(() =>
        {
            foreach (var (name, services) in new[] { ("the window", window.Services), ("the recorder", recorder.Services) })
            {
                Assert.That(services.GetRequiredService<IRuntimeFolder>(), Is.SameAs(_runtime.Folder), $"{name}: ClassScribe's own folder");
                Assert.That(services.GetRequiredService<IRecordingLock>(), Is.SameAs(services.GetRequiredService<RecordingLock>()), $"{name}: the lock");
            }
        });
    }
}
