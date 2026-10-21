using ClassScribe.Drive;
using ClassScribe.Storage;

namespace ClassScribe.Tests;

/// <summary>
/// What ClassScribe remembers of each recording and Google Drive, one file per recording in the temporary ClassScribe folder's
/// drive\recordings: named after the base name and 8 hexadecimal digits of the folder's path, in any letter case; written
/// whole; the states by name; the queue is those that wait, the one queued first first; one that cannot be read counts as
/// none, with a warning.
/// </summary>
[TestFixture]
public class UploadManifestTests
{
    private GoogleWorld _world = null!;
    private UploadManifests _manifests = null!;

    /// <summary>A world with its temporary folders.</summary>
    [SetUp]
    public void SetUp()
    {
        _world = new GoogleWorld();
        _manifests = _world.Manifests();
    }

    /// <summary>Deletes the world's folders.</summary>
    [TearDown]
    public void TearDown() => _world.Dispose();

    /// <summary>The tests' recording, in a course and date folder of the temporary recordings folder.</summary>
    private RecordingFiles Mantiq => new(Path.Combine(_world.Recordings.Path, "Mantiq", "2026-10-17"), "Mantiq_2026-10-17_14-04-42Z");

    /// <summary>
    /// A manifest's file is named after the base name and 8 hexadecimal digits of the folder's path: the same for the folder
    /// written in another letter case or with a separator at its end, another for another folder with the same base name.
    /// </summary>
    [Test]
    public void A_manifest_is_named_after_the_base_name_and_its_folder()
    {
        var path = _manifests.PathOf(Mantiq);

        Assert.That(Path.GetDirectoryName(path), Is.EqualTo(Path.Combine(_world.Runtime.Folder.Root, "drive", "recordings")));
        Assert.That(Path.GetFileName(path), Does.Match(@"^Mantiq_2026-10-17_14-04-42Z\.[0-9a-f]{8}\.json$"));
        Assert.That(_manifests.PathOf(Mantiq with { Folder = Mantiq.Folder.ToUpperInvariant() + Path.DirectorySeparatorChar }), Is.EqualTo(path));
        Assert.That(_manifests.PathOf(Mantiq with { Folder = Path.Combine(_world.Recordings.Path, "Mantiq", "2026-10-17 (2)") }), Is.Not.EqualTo(path));
    }

    /// <summary>
    /// Queuing writes a manifest that waits, queued now; queuing again keeps when it was first queued and what is known of
    /// its files; a recording that was done waits again.
    /// </summary>
    [Test]
    public void Queuing_writes_a_manifest_that_waits()
    {
        var queued = _manifests.Queue(Mantiq);
        Assert.That((queued.State, queued.QueuedUtc, queued.Folder, queued.BaseName), Is.EqualTo((UploadState.Waiting, TestRuns.Start, Mantiq.Folder, Mantiq.BaseName)));
        Assert.That(File.Exists(_manifests.PathOf(Mantiq)), Is.True);

        _manifests.Write(queued with { State = UploadState.Done, DoneUtc = TestRuns.Start, Files = [new ManifestFile("Mantiq_2026-10-17_14-04-42Z.md") { Size = 3 }] });
        _world.Time.Advance(TimeSpan.FromHours(1));
        var again = _manifests.Queue(Mantiq);

        Assert.That((again.State, again.QueuedUtc, again.DoneUtc, again.Files.Single().Size), Is.EqualTo((UploadState.Waiting, TestRuns.Start, (DateTimeOffset?)null, 3L)));
    }

    /// <summary>A manifest reads back as it was written, its files and an upload begun included, with the states written by name.</summary>
    [Test]
    public void A_manifest_reads_back_as_written()
    {
        var session = new UploadSession("https://www.googleapis.com/upload/drive/v3/files?uploadType=resumable&upload_id=abc", TestRuns.Start, 700_000, "0123456789abcdef0123456789abcdef", 262_144, null);
        var file = new ManifestFile("Mantiq_2026-10-17_14-04-42Z.wav")
        {
            Size = 700_000, LastWriteUtc = TestRuns.Start, Md5 = "0123456789abcdef0123456789abcdef", Seconds = 43.75, Session = session
        };
        _manifests.Write(new UploadManifest(Mantiq.Folder, Mantiq.BaseName)
        {
            QueuedUtc = TestRuns.Start, Attempts = 2, LastError = "Google Drive could not be reached.", DriveFolderId = "folder-1", DrivePath = "ClassScribe/Mantiq/2026-10-17", Files = [file]
        });

        var read = _manifests.Read(Mantiq)!;

        Assert.That((read.Attempts, read.LastError, read.DriveFolderId, read.DriveLocation), Is.EqualTo((2, "Google Drive could not be reached.", "folder-1", "My Drive > ClassScribe > Mantiq > 2026-10-17")));
        Assert.That(read.Files.Single(), Is.EqualTo(file));
        Assert.That(read.DriveLink, Is.EqualTo("https://drive.google.com/drive/folders/folder-1"));
        Assert.That(File.ReadAllText(_manifests.PathOf(Mantiq)), Does.Contain("\"state\": \"Waiting\""));
    }

    /// <summary>
    /// Fields a later build adds, to the manifest or to one of its files, are kept when this build reads the manifest and
    /// writes it back, as an upload does at each step (layout-config 6.3): an older build no longer drops them. A manifest
    /// without them reads with none, so that manifests still compare equal.
    /// </summary>
    [Test]
    public void Fields_a_later_build_adds_are_kept_when_a_manifest_is_written_back()
    {
        _manifests.Write(new UploadManifest(Mantiq.Folder, Mantiq.BaseName) { QueuedUtc = TestRuns.Start, Files = [new ManifestFile("Mantiq_2026-10-17_14-04-42Z.md") { Size = 3 }] });
        Assert.That(_manifests.Read(Mantiq)!.Extra, Is.Null);
        var later = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(_manifests.PathOf(Mantiq)))!.AsObject();
        later["laterField"] = "kept";
        later["files"]![0]!.AsObject()["laterFileField"] = 7;
        File.WriteAllText(_manifests.PathOf(Mantiq), later.ToJsonString());

        var read = _manifests.Read(Mantiq)!;
        _manifests.Write(read with { Attempts = 1, Files = [read.Files[0] with { Md5 = "0123456789abcdef0123456789abcdef" }] });

        var written = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(_manifests.PathOf(Mantiq)))!.AsObject();
        Assert.That(written["laterField"]!.GetValue<string>(), Is.EqualTo("kept"));
        Assert.That(written["files"]![0]!["laterFileField"]!.GetValue<int>(), Is.EqualTo(7));
        Assert.That(written["attempts"]!.GetValue<int>(), Is.EqualTo(1));
    }

    /// <summary>The queue is the manifests that wait, the one queued first first; one that is done is not in it.</summary>
    [Test]
    public void The_queue_is_the_manifests_that_wait_oldest_first()
    {
        var kalam = new RecordingFiles(Path.Combine(_world.Recordings.Path, "Kalam"), "Kalam_2026-10-15_23-00-02Z");
        var fiqh = new RecordingFiles(Path.Combine(_world.Recordings.Path, "Fiqh", "2026-10-16"), "Fiqh_2026-10-16_23-00-02Z");
        _manifests.Queue(Mantiq);
        _world.Time.Advance(TimeSpan.FromMinutes(1));
        _manifests.Queue(kalam);
        _world.Time.Advance(TimeSpan.FromMinutes(1));
        _manifests.Write(_manifests.Queue(fiqh) with { State = UploadState.Done });

        Assert.That(_manifests.Waiting().Select(manifest => manifest.BaseName), Is.EqualTo(new[] { Mantiq.BaseName, kalam.BaseName }));
        Assert.That(_manifests.All(), Has.Count.EqualTo(3));
    }

    /// <summary>A manifest that cannot be read counts as none, with a warning that names it, and is left out of the queue.</summary>
    [Test]
    public void A_manifest_that_cannot_be_read_counts_as_none()
    {
        _manifests.Queue(Mantiq);
        File.WriteAllText(_manifests.PathOf(Mantiq), "{ not json");

        Assert.That(_manifests.Read(Mantiq), Is.Null);
        Assert.That(_manifests.Waiting(), Is.Empty);
        Assert.That(_world.Logs.Problems, Has.Some.StartsWith($"Could not read {_manifests.PathOf(Mantiq)}"));
    }

    /// <summary>A file's copy counts as checked once Drive's id, size and MD5 are known, and as the same when they match the file's.</summary>
    [Test]
    public void A_file_is_checked_and_in_step_by_Drives_size_and_MD5()
    {
        var file = new ManifestFile("a.md") { Size = 3, Md5 = "abc" };
        Assert.That((file.IsVerified, file.InSync), Is.EqualTo((false, false)));

        var verified = file with { DriveId = "file-1", DriveSize = 3, DriveMd5 = "ABC", VerifiedUtc = TestRuns.Start };
        Assert.That((verified.IsVerified, verified.InSync), Is.EqualTo((true, true)));
        Assert.That((verified with { Size = 4 }).InSync, Is.False);
    }
}
