using System.Net;
using ClassScribe.Audio;
using ClassScribe.Commands;
using ClassScribe.Desktop;
using ClassScribe.Drive;
using ClassScribe.Recorder;
using ClassScribe.Storage;

namespace ClassScribe.Tests;

/// <summary>
/// One recording uploaded to the in-memory Google: every file of it but the upload copy, in the order transcripts, answers,
/// marks, the class's audio, the microphone's, the log; never the PDFs beside it; into the folder that mirrors its own
/// (course then date, or an old flat folder's name); each checked against Drive's size and MD5, and remembered in the
/// manifest. A second upload sends nothing; a grown text file goes up as a new revision, nothing else is ever replaced; a
/// lost manifest adopts Drive's files by name and MD5; a recording being recorded or transcribed is passed over; a file
/// deleted from the laptop after its copy was checked counts as safe on Drive. Nothing leaves the process.
/// </summary>
[TestFixture]
public class RecordingUploadTests
{
    private static readonly DateTimeOffset MantiqStart = new(2026, 10, 17, 14, 4, 42, TimeSpan.Zero);
    private GoogleWorld _world = null!;
    private RecordingFiles _mantiq = null!;

    /// <summary>
    /// A signed-in world and today's class: Mantiq in Mantiq\2026-10-17, with its transcript, answer, marks and log, the
    /// class's audio in three pieces and the microphone's in one, an upload copy left behind, and Gulzar's PDFs beside it.
    /// </summary>
    [SetUp]
    public void SetUp()
    {
        _world = new GoogleWorld();
        _world.SignedIn();
        _mantiq = _world.WriteRecording(@"Mantiq\2026-10-17", "Mantiq", MantiqStart, extras: [".md", ".json", ".marks.txt", ".log"]);
        GoogleWorld.WriteAudio(_mantiq.Segment(1, Track.Microphone), 4000);
        GoogleWorld.WriteAudio(_mantiq.Upload, 1000);
        File.WriteAllText(Path.Combine(_mantiq.Folder, "Class_1_slides.pdf"), "%PDF slides");
        File.WriteAllText(Path.Combine(_mantiq.Folder, "_.pdf"), "%PDF book");
    }

    /// <summary>Deletes the world's folders.</summary>
    [TearDown]
    public void TearDown() => _world.Dispose();

    /// <summary>Uploads <paramref name="files"/>, failing the test rather than waiting for ever; nothing here waits on the clock.</summary>
    private async Task<RecordingUploadResult> UploadAsync(RecordingFiles? files = null)
    {
        return await _world.RecordingUpload().UploadAsync(files ?? _mantiq, _ => Task.CompletedTask, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
    }

    /// <summary>The names of the files whose upload was started, new files and new revisions, in order.</summary>
    private List<string> Started() =>
        [.. _world.Google.Requests.Where(request => request.IsUploadStart).Select(request => JsonText.Object(request.Body)["name"]!.GetValue<string>())];

    /// <summary>The folder Mantiq\2026-10-17 on Drive.</summary>
    private FakeDriveFile DriveFolder => _world.Google.FolderAt("Mantiq/2026-10-17")!;

    /// <summary>The manifest of the tests' recording.</summary>
    private UploadManifest Manifest => _world.Manifests().Read(_mantiq)!;

    /// <summary>
    /// Every file of the recording goes up, in order, into ClassScribe > Mantiq > 2026-10-17, as the very bytes, with types
    /// Drive keeps as they are; the upload copy and the PDFs stay behind; the last line says where it is, how much, and that
    /// each file was checked against Drive's MD5.
    /// </summary>
    [Test]
    public async Task Every_file_of_the_recording_goes_up_in_order_and_nothing_else()
    {
        var result = await UploadAsync();

        Assert.That(result, Is.EqualTo(new RecordingUploadResult(_mantiq, RecordingOutcome.Done, null)));
        var b = _mantiq.BaseName;
        Assert.That(Started(), Is.EqualTo(new[] { $"{b}.md", $"{b}.json", $"{b}.marks.txt", $"{b}.wav", $"{b}.mic.wav", $"{b}.log" }));
        var onDrive = _world.Google.FilesIn(DriveFolder.Id);
        Assert.That(onDrive.Select(file => file.Name), Is.EquivalentTo(Started()));
        foreach (var file in onDrive) Assert.That(file.Content, Is.EqualTo(File.ReadAllBytes(Path.Combine(_mantiq.Folder, file.Name))), file.Name);
        Assert.That(onDrive.ToDictionary(file => file.Name, file => file.MimeType), Is.EquivalentTo(new Dictionary<string, string>
        {
            [$"{b}.md"] = "text/markdown", [$"{b}.json"] = "application/json", [$"{b}.marks.txt"] = "text/plain",
            [$"{b}.wav"] = "audio/wav", [$"{b}.mic.wav"] = "audio/wav", [$"{b}.log"] = "text/plain"
        }));
        var size = Sizes.Describe(onDrive.Sum(file => file.Content.Length));
        Assert.That(_world.Lines.Last(), Is.EqualTo($"{b} is on Google Drive: My Drive > ClassScribe > Mantiq > 2026-10-17 (6 files, {size}, each checked against Drive's MD5): https://drive.google.com/drive/folders/{DriveFolder.Id}"));
        Assert.That(_world.Lines, Has.Member($"Google Drive: uploading {b} (6 files, {size}) to ClassScribe/Mantiq/2026-10-17"));
    }

    /// <summary>
    /// The manifest says the recording is done, and for each file its size, MD5, its copy's id, size and MD5 and when it was
    /// checked; for audio its length; no upload is left begun.
    /// </summary>
    [Test]
    public async Task The_manifest_remembers_each_checked_copy()
    {
        await UploadAsync();

        var manifest = Manifest;
        Assert.That((manifest.State, manifest.Attempts, manifest.DoneUtc, manifest.LastError, manifest.DriveFolderId, manifest.DrivePath),
            Is.EqualTo((UploadState.Done, 1, (DateTimeOffset?)TestRuns.Start, (string?)null, DriveFolder.Id, "ClassScribe/Mantiq/2026-10-17")));
        Assert.That(manifest.Files, Has.Count.EqualTo(6).And.All.Matches<ManifestFile>(file => file.InSync && file.Session is null));
        var audio = manifest.File($"{_mantiq.BaseName}.wav")!;
        Assert.That(audio.Seconds, Is.EqualTo(ClassFolders.MeasureSegment(_mantiq.Segment(1)).TotalSeconds));
        Assert.That(audio.DriveId, Is.EqualTo(_world.Google.FilesIn(DriveFolder.Id).Single(file => file.Name == audio.Name).Id));
    }

    /// <summary>A file larger than one piece is said every tenth or so, as it goes; a small one only goes up.</summary>
    [Test]
    public async Task A_large_file_is_said_as_it_goes()
    {
        await UploadAsync();

        var name = $"{_mantiq.BaseName}.wav";
        var total = Sizes.Describe(new FileInfo(_mantiq.Segment(1)).Length);
        Assert.That(_world.Lines.Where(line => line.StartsWith(name + ":", StringComparison.Ordinal)), Is.EqualTo(new[]
        {
            $"{name}: 30% (256 KB of {total})", $"{name}: 60% (512 KB of {total})"
        }));
    }

    /// <summary>A second upload of a recording that has not changed sends nothing and asks Drive nothing, and says it is on Drive already.</summary>
    [Test]
    public async Task A_second_upload_sends_nothing()
    {
        await UploadAsync();
        var before = _world.Google.Requests.Count;

        var again = await UploadAsync();

        Assert.That(again.Outcome, Is.EqualTo(RecordingOutcome.Done));
        Assert.That(_world.Google.Requests.Skip(before), Is.Empty);
        Assert.That(_world.Lines.Last(), Does.StartWith($"{_mantiq.BaseName} is already on Google Drive: My Drive > ClassScribe > Mantiq > 2026-10-17 (6 files,"));
    }

    /// <summary>
    /// A log that has grown since it went up, as after a continued class, goes up as a new revision of the same file on Drive,
    /// never as a second file; a new transcript copy is a new file; nothing else goes up again.
    /// </summary>
    [Test]
    public async Task A_grown_log_is_a_new_revision_and_a_new_transcript_a_new_file()
    {
        await UploadAsync();
        var log = _world.Google.FilesIn(DriveFolder.Id).Single(file => file.Name.EndsWith(".log", StringComparison.Ordinal));
        File.AppendAllText(_mantiq.Log, "a line more\n");
        File.WriteAllText(_mantiq.Transcript(2), "# A second transcript\n");
        var startsBefore = _world.Google.Requests.Count(request => request.IsUploadStart || request.IsRevisionStart);

        await UploadAsync();

        Assert.That(log.Content, Is.EqualTo(File.ReadAllBytes(_mantiq.Log)));
        Assert.That(log.Revisions, Is.EqualTo(2));
        var names = _world.Google.FilesIn(DriveFolder.Id).Select(file => file.Name).ToList();
        Assert.That(names, Has.Exactly(1).EqualTo($"{_mantiq.BaseName}.log").And.Exactly(1).EqualTo($"{_mantiq.BaseName} (2).md"));
        Assert.That(_world.Google.Requests.Count(request => request.IsUploadStart || request.IsRevisionStart) - startsBefore, Is.EqualTo(2));
        Assert.That(_world.Google.Destructive, Is.Empty);
    }

    /// <summary>
    /// A file that changed in any other way, audio whose bytes differ or a text file that is now smaller, is never sent over
    /// its copy on Drive: that copy stays as it is, which is said, and the recording is done.
    /// </summary>
    [Test]
    public async Task Any_other_change_leaves_the_copy_on_Drive_as_it_is()
    {
        await UploadAsync();
        var copies = _world.Google.FilesIn(DriveFolder.Id).ToDictionary(file => file.Name, file => file.Content);
        GoogleWorld.WriteAudio(_mantiq.Segment(1, Track.Microphone), 6000);
        File.WriteAllText(_mantiq.Transcript(), "#");

        var result = await UploadAsync();

        Assert.That(result.Outcome, Is.EqualTo(RecordingOutcome.Done));
        Assert.That(_world.Google.FilesIn(DriveFolder.Id).ToDictionary(file => file.Name, file => file.Content), Is.EquivalentTo(copies));
        Assert.That(_world.Logs.Problems, Has.Member($"{_mantiq.BaseName}.mic.wav on this laptop differs from its copy on Google Drive, which ClassScribe never replaces: only a text file that has grown, or a lesson's video made again, goes up again, as a new revision. The copy on Drive stays as it is"));
        Assert.That(_world.Logs.Problems, Has.Member($"{_mantiq.BaseName}.md on this laptop differs from its copy on Google Drive, which ClassScribe never replaces: only a text file that has grown, or a lesson's video made again, goes up again, as a new revision. The copy on Drive stays as it is"));
    }

    /// <summary>
    /// With the manifest lost, as on another laptop or after %LOCALAPPDATA% was cleared, the files already in the folder on
    /// Drive are adopted by name and MD5, and nothing goes up twice.
    /// </summary>
    [Test]
    public async Task A_lost_manifest_adopts_Drives_files_by_name_and_MD5()
    {
        await UploadAsync();
        File.Delete(_world.Manifests().PathOf(_mantiq));
        File.Delete(Path.Combine(_world.Runtime.Folder.Root, "drive", "folders.json"));
        var starts = _world.Google.Requests.Count(request => request.IsUploadStart || request.IsRevisionStart);

        var result = await UploadAsync();

        Assert.That(result.Outcome, Is.EqualTo(RecordingOutcome.Done));
        Assert.That(_world.Google.Requests.Count(request => request.IsUploadStart || request.IsRevisionStart), Is.EqualTo(starts));
        Assert.That(_world.Google.Uploaded, Has.Count.EqualTo(6));
        Assert.That(Manifest.Files, Has.All.Matches<ManifestFile>(file => file.InSync));
        Assert.That(_world.Lines.Count(line => line.EndsWith(": already on Drive", StringComparison.Ordinal)), Is.EqualTo(6));
    }

    /// <summary>
    /// With no manifest, a text file of the same name that is larger here than on Drive goes up as a new revision of Drive's;
    /// audio of the same name that differs is left out, which is said, and Drive's copy stays.
    /// </summary>
    [Test]
    public async Task Without_a_manifest_a_larger_text_file_is_a_revision_and_different_audio_left_out()
    {
        var root = _world.Google.AddFolder("ClassScribe", root: true);
        var course = _world.Google.AddFolder("Mantiq", parentId: root.Id);
        var date = _world.Google.AddFolder("2026-10-17", parentId: course.Id);
        var log = _world.Google.AddFile($"{_mantiq.BaseName}.log", date.Id, "short"u8.ToArray());
        var audio = _world.Google.AddFile($"{_mantiq.BaseName}.wav", date.Id, [1, 2, 3]);

        await UploadAsync();

        Assert.That((log.Content, log.Revisions), Is.EqualTo((File.ReadAllBytes(_mantiq.Log), 1)));
        Assert.That(audio.Content, Is.EqualTo(new byte[] { 1, 2, 3 }));
        Assert.That(_world.Google.FilesIn(date.Id).Count(file => file.Name == audio.Name), Is.EqualTo(1));
        Assert.That(_world.Logs.Problems, Has.Member($"A different {_mantiq.BaseName}.wav is already in My Drive > ClassScribe > Mantiq > 2026-10-17 on Google Drive; ClassScribe never replaces it, so this one did not go up"));
    }

    /// <summary>
    /// A copy whose MD5 on Drive is not the file's is said, with both, and sent again once as a new revision of itself, which
    /// is then checked; one that still differs leaves the recording waiting, and the next upload sends it again.
    /// </summary>
    [Test]
    public async Task A_copy_that_does_not_match_is_sent_again_once_then_waits()
    {
        var name = $"{_mantiq.BaseName}.marks.txt";
        _world.Google.WrongMd5For(name);

        Assert.That((await UploadAsync()).Outcome, Is.EqualTo(RecordingOutcome.Done));
        Assert.That(_world.Logs.Problems, Has.Some.StartsWith($"{name} does not match Drive's MD5 after it went up (here "));
        Assert.That(_world.Google.FilesIn(DriveFolder.Id).Single(file => file.Name == name).Revisions, Is.EqualTo(2));

        var other = _world.WriteRecording(@"Mantiq\2026-10-17 (2)", "Mantiq", MantiqStart.AddHours(3), 1000, ".md");
        var md = $"{other.BaseName}.md";
        _world.Google.WrongMd5For(md, times: 2);

        var waiting = await UploadAsync(other);

        Assert.That(waiting, Is.EqualTo(new RecordingUploadResult(other, RecordingOutcome.Waiting, $"{md} did not go up: {md} still does not match Drive's MD5 after it was sent again; it goes up again with the next upload.")));
        Assert.That(_world.Manifests().Read(other)!.State, Is.EqualTo(UploadState.Waiting));

        Assert.That((await UploadAsync(other)).Outcome, Is.EqualTo(RecordingOutcome.Done));
        Assert.That(_world.Google.FilesIn(_world.Google.FolderAt("Mantiq/2026-10-17 (2)")!.Id).Count(file => file.Name == md), Is.EqualTo(1), "the copy that did not match got a new revision, not a twin");
    }

    /// <summary>With Drive:Upload WithoutAudio, the .wav files stay on the laptop alone; everything else goes up.</summary>
    [Test]
    public async Task Without_audio_the_wav_files_stay_behind()
    {
        _world.Options.Set(new DriveOptions { Upload = DriveUploadScope.WithoutAudio });

        await UploadAsync();

        Assert.That(Started(), Has.None.EndsWith(".wav"));
        Assert.That(Started(), Has.Count.EqualTo(4));
    }

    /// <summary>A recording of the old flat layout goes to ClassScribe > its name, and the folder's name is spelled as on disk, whatever was typed.</summary>
    [Test]
    public async Task An_old_flat_recording_goes_to_a_folder_of_its_name_spelled_as_on_disk()
    {
        var kalam = _world.WriteRecording("Kalam", "Kalam", new DateTimeOffset(2026, 10, 15, 23, 0, 2, TimeSpan.Zero), 1000, ".md");

        await UploadAsync(kalam with { Folder = kalam.Folder.ToLowerInvariant() });

        var folder = _world.Google.FolderAt("Kalam");
        Assert.That(folder, Is.Not.Null);
        Assert.That(_world.Google.FilesIn(folder!.Id).Select(file => file.Name), Is.EquivalentTo(new[] { $"{kalam.BaseName}.md", $"{kalam.BaseName}.wav" }));
    }

    /// <summary>
    /// Audio deleted from the laptop after its copy was checked, freed or by hand, is safe on Drive: it is not sent again,
    /// nothing is missing, and the recording is done, not waiting.
    /// </summary>
    [Test]
    public async Task Audio_deleted_after_it_went_up_counts_as_safe_on_Drive()
    {
        await UploadAsync();
        File.Delete(_mantiq.Segment(1));
        File.Delete(_mantiq.Segment(1, Track.Microphone));
        File.AppendAllText(_mantiq.Log, "a line more\n");

        var result = await UploadAsync();

        Assert.That(result.Outcome, Is.EqualTo(RecordingOutcome.Done));
        Assert.That(Started(), Has.Count.EqualTo(6));
        Assert.That(Manifest.Files.Select(file => file.Name), Has.Member($"{_mantiq.BaseName}.wav").And.Member($"{_mantiq.BaseName}.mic.wav"));
        Assert.That(_world.Manifests().Waiting(), Is.Empty);
        Assert.That(_world.Lines.Last(), Does.StartWith($"{_mantiq.BaseName} is on Google Drive: My Drive > ClassScribe > Mantiq > 2026-10-17 (6 files,"));
    }

    /// <summary>
    /// Whether a done recording is up to date is read from one look at each of its files: a verified file that vanishes
    /// between the listing and the look, as when free-space in another window deletes it, makes the recording not up to date
    /// rather than an error, so the pass queues it and the run notes the file as gone. With the file there, it is up to date.
    /// </summary>
    [Test]
    public async Task A_verified_file_that_vanishes_as_it_is_looked_at_makes_the_recording_not_up_to_date()
    {
        await UploadAsync();

        Assert.That(_world.RecordingUpload().IsUpToDate(_mantiq), Is.True, "with every file there");
        Assert.That(_world.RecordingUpload(new VanishingProbe(_mantiq.Segment(1))).IsUpToDate(_mantiq), Is.False, "with the class's audio gone at the look");
    }

    /// <summary>The disk as the real probe reads it, but for one file, listed and then gone: there when asked, gone at the look, its length an error, as a file deleted between two looks is.</summary>
    private sealed class VanishingProbe(string vanished) : IFileProbe
    {
        private readonly FileProbe _disk = new();

        /// <summary>True when <paramref name="path"/> names the vanished file.</summary>
        private bool IsVanished(string path) => string.Equals(Path.GetFullPath(path), Path.GetFullPath(vanished), StringComparison.OrdinalIgnoreCase);

        /// <inheritdoc />
        public bool FileExists(string path) => IsVanished(path) || _disk.FileExists(path);

        /// <inheritdoc />
        public bool DirectoryExists(string path) => _disk.DirectoryExists(path);

        /// <inheritdoc />
        public DateTime LastWriteUtc(string path) => IsVanished(path) ? DateTime.FromFileTimeUtc(0) : _disk.LastWriteUtc(path);

        /// <inheritdoc />
        public long Length(string path) => IsVanished(path) ? throw new FileNotFoundException("The file was deleted meanwhile.", path) : _disk.Length(path);

        /// <inheritdoc />
        public (long Length, DateTime LastWriteUtc)? Describe(string path) => IsVanished(path) ? null : _disk.Describe(path);

        /// <inheritdoc />
        public string FullPath(string path) => _disk.FullPath(path);

        /// <inheritdoc />
        public string? VersionOf(string path) => _disk.VersionOf(path);
    }

    /// <summary>A file that never went up and is gone from the laptop is forgotten, which is said; nothing is missing.</summary>
    [Test]
    public async Task A_file_gone_before_it_went_up_is_forgotten()
    {
        var name = $"{_mantiq.BaseName}.marks.txt";
        _world.Manifests().Write(new UploadManifest(_mantiq.Folder, _mantiq.BaseName) { Files = [new ManifestFile(name) { Size = 3 }, new ManifestFile($"{_mantiq.BaseName} (2).md")] });

        await UploadAsync();

        Assert.That(_world.Lines, Has.Member($"{_mantiq.BaseName} (2).md is no longer in {_mantiq.Folder} and never went up to Google Drive, so it is left out"));
        Assert.That(Manifest.Files.Select(file => file.Name), Has.No.Member($"{_mantiq.BaseName} (2).md"));
    }

    /// <summary>A recording a recorder still writes, its newest segment held open for writing, is passed over and left waiting; nothing of it goes up.</summary>
    [Test]
    public async Task A_recording_being_written_waits()
    {
        using (new FileStream(_mantiq.Segment(1), FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            var result = await UploadAsync();

            Assert.That(result, Is.EqualTo(new RecordingUploadResult(_mantiq, RecordingOutcome.InProgress, $"{_mantiq.BaseName} is being recorded now")));
        }

        Assert.That(_world.Google.Requests.Where(request => request.Path.StartsWith("/upload", StringComparison.Ordinal)), Is.Empty);
        Assert.That(_world.Manifests().Waiting().Single().BaseName, Is.EqualTo(_mantiq.BaseName));
        Assert.That(_world.Lines, Has.Member($"{_mantiq.BaseName} is being recorded now; it goes up once it has stopped"));
    }

    /// <summary>A recording whose run records it, or transcribes it, as its run file says, is passed over and left waiting.</summary>
    [TestCase(RunState.Recording, "is being recorded now")]
    [TestCase(RunState.Stopping, "is being recorded now")]
    [TestCase(RunState.Transcribing, "is being transcribed now")]
    public async Task A_recording_with_a_live_run_waits(RunState state, string busy)
    {
        var runId = TestRuns.Id();
        _world.Runtime.Processes.Start(TestRuns.ProcessId, TestRuns.ProcessStart);
        _world.Runtime.Journal.Begin(TestRuns.Record(runId, state, name: "Mantiq") with { Folder = _mantiq.Folder, BaseName = _mantiq.BaseName });

        var result = await UploadAsync();

        Assert.That((result.Outcome, result.Problem), Is.EqualTo((RecordingOutcome.InProgress, $"{_mantiq.BaseName} {busy}")));
        Assert.That(_world.Google.Uploaded, Is.Empty);
    }

    /// <summary>A file Drive refuses keeps the recording waiting, with Drive's words; the others go up.</summary>
    [Test]
    public async Task A_file_Drive_refuses_keeps_the_recording_waiting_and_the_others_go_up()
    {
        _world.Google.FailWhen(request => request.IsUploadStart && request.Body.Contains(".marks.txt", StringComparison.Ordinal), HttpStatusCode.BadRequest,
            FakeGoogle.DriveError(400, "invalid", "Invalid Value"));

        var result = await UploadAsync();

        var name = $"{_mantiq.BaseName}.marks.txt";
        Assert.That(result, Is.EqualTo(new RecordingUploadResult(_mantiq, RecordingOutcome.Waiting, $"{name} did not go up: Google Drive refused to start an upload (HTTP 400, invalid: Invalid Value).")));
        Assert.That(_world.Google.Uploaded.Select(file => file.Name).ToList(), Has.Count.EqualTo(5).And.No.Member(name));
        Assert.That(Manifest.LastError, Is.EqualTo(result.Problem));
    }

    /// <summary>A full Drive ends the upload, which is thrown on; the recording waits, with the next try after a class no sooner than a day later.</summary>
    [Test]
    public void A_full_Drive_ends_the_upload_and_the_recording_waits_a_day()
    {
        _world.Google.FailWhen(request => request.IsUploadStart, HttpStatusCode.Forbidden, FakeGoogle.DriveError(403, "storageQuotaExceeded", "The user's Drive storage quota has been exceeded."));

        var failure = Assert.ThrowsAsync<GoogleDriveException>(() => UploadAsync())!;

        Assert.That(failure.Problem, Is.EqualTo(DriveProblem.Full));
        Assert.That((Manifest.State, Manifest.LastError, Manifest.NextAttemptUtc), Is.EqualTo((UploadState.Waiting, failure.Message, (DateTimeOffset?)TestRuns.Start.AddDays(1))));
    }

    /// <summary>
    /// A recording whose folder is on a drive that is not there now, as a USB drive that is out, waits for an upload that
    /// finds it, with why, which the report at the start shows; Google is asked nothing.
    /// </summary>
    [Test]
    public async Task A_recording_on_a_drive_that_is_out_waits()
    {
        var drive = Enumerable.Range('D', 23).Select(letter => $"{(char)letter}:\\").Last(root => !Directory.Exists(root));
        var away = new RecordingFiles(Path.Combine(drive, "Classes", "Gone", "2026-10-17"), "Gone_2026-10-17_14-04-42Z");
        _world.Manifests().Queue(away);

        var result = await UploadAsync(away);

        var problem = $"The folder of {away.BaseName}, {away.Folder}, is not there now: its drive, {drive}, is not there either";
        Assert.That(result, Is.EqualTo(new RecordingUploadResult(away, RecordingOutcome.Waiting, problem)));
        Assert.That(_world.Manifests().Waiting().Single(), Has.Property(nameof(UploadManifest.LastError)).EqualTo(problem));
        Assert.That(_world.QueueReport().Waiting()!.Line, Does.EndWith($"last try 15 Oct 19:00: {problem})."));
        Assert.That(_world.Lines, Has.Member($"{problem}; it waits for an upload that finds it"));
        Assert.That(_world.Google.Requests, Is.Empty);
    }

    /// <summary>
    /// A recording whose every file went up and was checked, queued again and then its folder deleted or moved by hand, on a
    /// drive that is there, is done: what it had is safe on Drive, nothing waits, and Google is asked nothing.
    /// </summary>
    [Test]
    public async Task A_recording_all_on_Drive_whose_folder_was_deleted_is_done()
    {
        await UploadAsync();
        _world.Manifests().Queue(_mantiq);
        Directory.Delete(_mantiq.Folder, recursive: true);
        var before = _world.Google.Requests.Count;

        var result = await UploadAsync();

        Assert.That(result, Is.EqualTo(new RecordingUploadResult(_mantiq, RecordingOutcome.Done, null)));
        Assert.That((Manifest.State, Manifest.LastError), Is.EqualTo((UploadState.Done, (string?)null)));
        Assert.That(_world.Manifests().Waiting(), Is.Empty);
        Assert.That(_world.QueueReport().HeaderLine(), Is.EqualTo("Google Drive: signed in; nothing waits to go up."));
        Assert.That(_world.Lines, Has.Member($"The folder of {_mantiq.BaseName}, {_mantiq.Folder}, is not there now, but every file of it went up to Google Drive before and was checked: it is in My Drive > ClassScribe > Mantiq > 2026-10-17"));
        Assert.That(_world.Google.Requests.Skip(before), Is.Empty);
    }

    /// <summary>
    /// A recording queued that never went up, whose folder was then moved by hand, as into a course and date folder, or
    /// deleted, on a drive that is there, is taken out of the queue, which is said with what to do; its manifest says why.
    /// It no longer counts as waiting, and a pass of the whole queue does not take it again.
    /// </summary>
    [Test]
    public async Task A_recording_never_uploaded_whose_folder_was_moved_leaves_the_queue()
    {
        var kalam = _world.WriteRecording("Kalam", "Kalam", new DateTimeOffset(2026, 10, 15, 23, 0, 2, TimeSpan.Zero), 1000, ".md");
        _world.Manifests().Queue(kalam);
        var moved = Path.Combine(_world.Recordings.Path, "Kalam2", "2026-10-15");
        Directory.CreateDirectory(Path.GetDirectoryName(moved)!);
        Directory.Move(kalam.Folder, moved);

        var result = await UploadAsync(kalam);

        var problem = $"The folder of {kalam.BaseName}, {kalam.Folder}, is not there now: it was moved or deleted";
        Assert.That(result, Is.EqualTo(new RecordingUploadResult(kalam, RecordingOutcome.Missing, problem)));
        Assert.That(_world.Manifests().Read(kalam), Has.Property(nameof(UploadManifest.State)).EqualTo(UploadState.Missing).And.Property(nameof(UploadManifest.LastError)).EqualTo(problem));
        Assert.That(_world.Manifests().Waiting(), Is.Empty);
        Assert.That(_world.QueueReport().Waiting(), Is.Null);
        Assert.That(_world.Logs.Problems, Has.Member($"{problem}, so it is taken out of the queue for Google Drive. If it was moved, upload it from where it is now: menu choice 9 > F, or ClassScribe drive upload \"<its folder>\""));

        var again = await _world.Pass().RunAsync([], everythingWaiting: true, CancellationToken.None);
        Assert.That((again.AllDone, again.Recordings.Count), Is.EqualTo((true, 0)));
        Assert.That(_world.Google.Requests.Where(request => request.Path.StartsWith("/upload", StringComparison.Ordinal)), Is.Empty);
    }

    /// <summary>A recording taken out of the queue whose folder is back, asked for again, goes into the queue and up as any other.</summary>
    [Test]
    public async Task A_recording_whose_folder_is_back_goes_up_when_asked_for()
    {
        var kalam = _world.WriteRecording("Kalam", "Kalam", new DateTimeOffset(2026, 10, 15, 23, 0, 2, TimeSpan.Zero), 1000, ".md");
        _world.Manifests().Queue(kalam);
        var away = kalam.Folder + " away";
        Directory.Move(kalam.Folder, away);
        await UploadAsync(kalam);
        Directory.Move(away, kalam.Folder);

        var result = await _world.Pass().RunAsync([kalam], everythingWaiting: false, CancellationToken.None);

        Assert.That(result.AllDone, Is.True);
        Assert.That(_world.Manifests().Read(kalam)!.State, Is.EqualTo(UploadState.Done));
        Assert.That(_world.Google.FilesIn(_world.Google.FolderAt("Kalam")!.Id).Select(file => file.Name), Is.EquivalentTo(new[] { $"{kalam.BaseName}.md", $"{kalam.BaseName}.wav" }));
    }

    /// <summary>
    /// A text file that has grown since it went up, whose copy on Drive was deleted there since, goes up as a new file in the
    /// recording's folder on Drive; nothing is sent to the copy that is gone, and the recording is done.
    /// </summary>
    [Test]
    public async Task A_grown_log_whose_copy_was_deleted_on_Drive_goes_up_as_a_new_file()
    {
        await UploadAsync();
        var name = $"{_mantiq.BaseName}.log";
        _world.Google.Files.Remove(_world.Google.Files.Single(file => file.Name == name));
        File.AppendAllText(_mantiq.Log, "more lines of a continued class\n");

        var result = await UploadAsync();

        Assert.That(result.Outcome, Is.EqualTo(RecordingOutcome.Done));
        Assert.That(_world.Google.FilesIn(DriveFolder.Id).Single(file => file.Name == name).Content, Is.EqualTo(File.ReadAllBytes(_mantiq.Log)));
        Assert.That(_world.Google.Requests.Where(request => request.IsRevisionStart), Is.Empty);
        Assert.That(Manifest.File(name)!.InSync, Is.True);
    }

    /// <summary>
    /// A text file that has grown since it went up, whose copy on Drive is in the trash, goes up as a new file in the folder,
    /// outside the trash; the copy in the trash is left exactly as it was, and nothing is taken out of the trash.
    /// </summary>
    [Test]
    public async Task A_grown_log_whose_copy_is_in_the_trash_goes_up_as_a_new_file()
    {
        await UploadAsync();
        var name = $"{_mantiq.BaseName}.log";
        var trashed = _world.Google.Files.Single(file => file.Name == name);
        trashed.Trashed = true;
        var content = trashed.Content;
        File.AppendAllText(_mantiq.Log, "more lines of a continued class\n");

        var result = await UploadAsync();

        Assert.That(result.Outcome, Is.EqualTo(RecordingOutcome.Done));
        Assert.That((trashed.Trashed, trashed.Content, trashed.Revisions), Is.EqualTo((true, content, 1)));
        var live = _world.Google.FilesIn(DriveFolder.Id).Where(file => file.Name == name && !file.Trashed).ToList();
        Assert.That(live.Single().Content, Is.EqualTo(File.ReadAllBytes(_mantiq.Log)));
        Assert.That(_world.Logs.Problems.Where(line => line.Contains("MD5", StringComparison.Ordinal)), Is.Empty);
        Assert.That(_world.Google.Destructive, Is.Empty);
    }

    /// <summary>
    /// With no manifest, a text file of the same name on Drive that holds other bytes and is as large as this one, or larger,
    /// is never replaced: its content and revisions stay as they are, which is said, and nothing could change or remove it.
    /// </summary>
    [TestCase("larger")]
    [TestCase("same size")]
    public async Task Without_a_manifest_a_text_file_on_Drive_that_has_not_grown_is_left_as_it_is(string size)
    {
        var root = _world.Google.AddFolder("ClassScribe", root: true);
        var course = _world.Google.AddFolder("Mantiq", parentId: root.Id);
        var date = _world.Google.AddFolder("2026-10-17", parentId: course.Id);
        var here = File.ReadAllBytes(_mantiq.Transcript());
        byte[] other = size == "larger" ? [.. here, .. "# and more written on another laptop\n"u8.ToArray()] : [.. here.Select(b => (byte)(b ^ 1))];
        var md = _world.Google.AddFile($"{_mantiq.BaseName}.md", date.Id, other);

        await UploadAsync();

        Assert.That((md.Content, md.Revisions), Is.EqualTo((other, 0)));
        Assert.That(_world.Google.FilesIn(date.Id).Count(file => file.Name == md.Name), Is.EqualTo(1));
        Assert.That(_world.Logs.Problems, Has.Member($"A different {_mantiq.BaseName}.md is already in My Drive > ClassScribe > Mantiq > 2026-10-17 on Google Drive; ClassScribe never replaces it, so this one did not go up"));
        Assert.That(_world.Google.Destructive, Is.Empty);
    }

    /// <summary>
    /// A file whose name begins with the recording's base name but is not one of its own, as Explorer's copy of its audio or
    /// notes beside it, stays on the laptop: it does not go up and is not in the manifest.
    /// </summary>
    [Test]
    public async Task Files_named_like_the_recording_that_are_not_its_own_stay_behind()
    {
        var copy = Path.Combine(_mantiq.Folder, $"{_mantiq.BaseName} - Copy.wav");
        File.Copy(_mantiq.Segment(1), copy);
        var notes = Path.Combine(_mantiq.Folder, $"{_mantiq.BaseName} notes.pdf");
        File.WriteAllText(notes, "%PDF notes");

        await UploadAsync();

        Assert.That(_world.Google.Uploaded.Select(file => file.Name), Has.None.EqualTo(Path.GetFileName(copy)).And.None.EqualTo(Path.GetFileName(notes)));
        Assert.That(Manifest.Files.Select(file => file.Name), Has.None.EqualTo(Path.GetFileName(copy)).And.None.EqualTo(Path.GetFileName(notes)));
    }

    /// <summary>
    /// A manifest that cannot be written while the recording goes up ends its upload with words of its own, since an upload
    /// that cannot keep track of itself must not go on: no file after it goes up, and it is never taken for a file that could
    /// not be read.
    /// </summary>
    [Test]
    public async Task A_manifest_that_cannot_be_written_ends_the_upload()
    {
        var path = _world.Manifests().PathOf(_mantiq);
        _world.Google.FailWhen(request => request.IsUploadStart && request.Body.Contains(".json", StringComparison.Ordinal) && ReadOnly(path), HttpStatusCode.OK, "");
        try
        {
            var failure = Assert.ThrowsAsync<UploadNotTrackedException>(() => UploadAsync())!;

            Assert.That(failure.Message, Does.StartWith($"ClassScribe could not keep track of the upload of {_mantiq.BaseName} in {path} ("));
            Assert.That(failure.Message, Does.EndWith("), so the upload stopped; it waits in the queue for the next one."));
            Assert.That(_world.Logs.Problems, Has.None.Contains("could not be read to go up"));
            Assert.That(Started(), Is.EqualTo(new[] { $"{_mantiq.BaseName}.md", $"{_mantiq.BaseName}.json" }));
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    /// <summary>Makes <paramref name="path"/> read-only, as a file Windows will not let ClassScribe replace, and says false, so that the fault it rides on never fires.</summary>
    private static bool ReadOnly(string path)
    {
        File.SetAttributes(path, FileAttributes.ReadOnly);
        return false;
    }

    /// <summary>
    /// A file transcribed on its own, laid out as a recording, has its transcript and answer go up; the file it came from
    /// lives elsewhere and does not.
    /// </summary>
    [Test]
    public async Task A_file_transcribed_on_its_own_has_its_transcript_and_answer_go_up()
    {
        var lecture = new RecordingFiles(Directory.CreateDirectory(Path.Combine(_world.Recordings.Path, "Lecture 5", "2026-10-17")).FullName, "Lecture 5_2026-10-17_18-00-00Z");
        File.WriteAllText(lecture.Transcript(), "# Lecture 5\n");
        File.WriteAllText(lecture.Json(), "{}");

        var result = await UploadAsync(lecture);

        Assert.That(result.Outcome, Is.EqualTo(RecordingOutcome.Done));
        Assert.That(_world.Google.FilesIn(_world.Google.FolderAt("Lecture 5/2026-10-17")!.Id).Select(file => file.Name),
            Is.EquivalentTo(new[] { "Lecture 5_2026-10-17_18-00-00Z.md", "Lecture 5_2026-10-17_18-00-00Z.json" }));
    }

    /// <summary>
    /// While this process still writes the recording's log, as a recorder does after its class, the last line, where the
    /// recording is on Drive, is written just before the log goes up, so that Drive's copy of the log holds it; it is said
    /// once, not again at the end.
    /// </summary>
    [Test]
    public async Task While_this_process_writes_the_log_the_last_line_goes_up_in_it()
    {
        var log = _world.RecorderLog;
        _world.Logs.AddProvider(new RecorderLogProvider(log));
        log.WriteFileTo(_mantiq.Log);

        var result = await UploadAsync();

        Assert.That(result.Outcome, Is.EqualTo(RecordingOutcome.Done));
        var lastLine = $"{_mantiq.BaseName} is on Google Drive: My Drive > ClassScribe > Mantiq > 2026-10-17 (6 files,";
        var onDrive = System.Text.Encoding.UTF8.GetString(_world.Google.FilesIn(DriveFolder.Id).Single(file => file.Name == $"{_mantiq.BaseName}.log").Content);
        Assert.That(onDrive, Does.Contain(lastLine));
        Assert.That(onDrive, Does.StartWith($".log of {_mantiq.BaseName}"), "the lines go on from the log as it was");
        Assert.That(_world.Lines.Count(line => line.Contains(" is on Google Drive: ", StringComparison.Ordinal)), Is.EqualTo(1));
        Assert.That(Manifest.State, Is.EqualTo(UploadState.Done));
    }

    /// <summary>A log this process does not write, as in an upload from the menu, goes up as it is, and the last line comes after it.</summary>
    [Test]
    public async Task A_log_this_process_does_not_write_goes_up_before_the_last_line()
    {
        var log = _world.RecorderLog;
        _world.Logs.AddProvider(new RecorderLogProvider(log));
        log.WriteFileTo(Path.Combine(_world.Recordings.Path, "Kalam_2026-10-15_23-00-02Z.log"));

        await UploadAsync();

        var onDrive = System.Text.Encoding.UTF8.GetString(_world.Google.FilesIn(DriveFolder.Id).Single(file => file.Name == $"{_mantiq.BaseName}.log").Content);
        Assert.That(onDrive, Is.EqualTo($".log of {_mantiq.BaseName}\n"));
        Assert.That(_world.Lines.Last(), Does.StartWith($"{_mantiq.BaseName} is on Google Drive:"));
    }
}
