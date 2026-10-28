using ClassScribe.Audio;
using ClassScribe.Drive;
using ClassScribe.Storage;

namespace ClassScribe.Tests;

/// <summary>
/// drive upload and drive free-space, against the in-memory Google: a path names a recording by any of its files; a folder
/// its recordings and those of its date folders, in either layout; the recordings folder each course in it, with a folder
/// holding no recording named and passed over, and the microphone check's folder passed over without a word; files that are
/// not a recording's are never touched. No path uploads what waits. Exit codes: 0 when everything is done, 1 otherwise.
/// </summary>
[TestFixture]
public class DriveUploadCommandTests
{
    private GoogleWorld _world = null!;
    private RecordingFiles _mantiq = null!;
    private RecordingFiles _mantiqLater = null!;
    private RecordingFiles _kalam = null!;

    /// <summary>
    /// A signed-in world and a recordings folder as Gulzar's: Mantiq with two classes on 17 October, its slides in a folder of
    /// their own and a PDF beside the course; Kalam of the old flat layout; three empty folders; and the microphone check's.
    /// </summary>
    [SetUp]
    public void SetUp()
    {
        _world = new GoogleWorld();
        _world.SignedIn();
        _mantiq = _world.WriteRecording(@"Mantiq\2026-10-17", "Mantiq", new DateTimeOffset(2026, 10, 17, 14, 4, 42, TimeSpan.Zero), 2000, ".md", ".json");
        _mantiqLater = _world.WriteRecording(@"Mantiq\2026-10-17 (2)", "Mantiq", new DateTimeOffset(2026, 10, 17, 19, 0, 0, TimeSpan.Zero), 2000);
        _kalam = _world.WriteRecording("Kalam", "Kalam", new DateTimeOffset(2026, 10, 15, 23, 0, 2, TimeSpan.Zero), 2000, ".md");
        Directory.CreateDirectory(Path.Combine(_world.Recordings.Path, "Mantiq", "Slides"));
        File.WriteAllText(Path.Combine(_world.Recordings.Path, "Mantiq", "Slides", "Class_1_slides.pdf"), "%PDF");
        File.WriteAllText(Path.Combine(_world.Recordings.Path, "Mantiq", "Al-Sullam_al-munawraq_translated.pdf"), "%PDF");
        foreach (var empty in new[] { "2026-10-15_22-25-30", "2026-10-15_22-25-47", "2026-10-15_22-26-01" })
            Directory.CreateDirectory(Path.Combine(_world.Recordings.Path, empty));
        Directory.CreateDirectory(Path.Combine(_world.Recordings.Path, FolderNames.MicCheck));
        File.WriteAllText(Path.Combine(_world.Recordings.Path, FolderNames.MicCheck, "mic-check_2026-10-16_09-00-00.log"), "a check");
    }

    /// <summary>Deletes the world's folders.</summary>
    [TearDown]
    public void TearDown() => _world.Dispose();

    /// <summary>Uploads what <paramref name="path"/> names, moving the clock while it waits.</summary>
    private async Task<int> UploadAsync(string? path)
    {
        var upload = _world.UploadCommand().UploadAsync(path);
        await _world.Time.AdvanceUntilAsync(() => upload.IsCompleted, TimeSpan.FromSeconds(1), timeoutSeconds: 30);
        return await upload;
    }

    /// <summary>The base names of the recordings whose files went up, in the order the first of each went.</summary>
    private List<string> Uploaded() =>
        [.. _world.Google.Requests.Where(request => request.IsUploadStart).Select(request => RecordingFiles.TryParse(JsonText.Object(request.Body)["name"]!.GetValue<string>())!.BaseName).Distinct()];

    /// <summary>Any file of a recording names that recording alone, here its transcript.</summary>
    [Test]
    public async Task A_recordings_file_uploads_that_recording()
    {
        Assert.That(await UploadAsync(_mantiq.Transcript()), Is.EqualTo(0));

        Assert.That(Uploaded(), Is.EqualTo(new[] { _mantiq.BaseName }));
    }

    /// <summary>A date folder names its class; the course's folder each of its classes, newest first, its slides and PDFs left alone.</summary>
    [Test]
    public async Task A_date_folder_names_its_class_and_a_course_folder_each_of_them()
    {
        Assert.That(await UploadAsync(_mantiqLater.Folder), Is.EqualTo(0));
        Assert.That(Uploaded(), Is.EqualTo(new[] { _mantiqLater.BaseName }));

        Assert.That(await UploadAsync(Path.Combine(_world.Recordings.Path, "Mantiq")), Is.EqualTo(0));
        Assert.That(Uploaded(), Is.EqualTo(new[] { _mantiqLater.BaseName, _mantiq.BaseName }));
        Assert.That(_world.Google.Uploaded.Select(file => file.Name), Has.None.EndsWith(".pdf"));
        Assert.That(_world.Google.FolderAt("Mantiq/Slides"), Is.Null);
    }

    /// <summary>
    /// The recordings folder names each recording of each course; the folders holding no recording are named and passed
    /// over, the microphone check's without a word; each class goes to the folder that mirrors its own.
    /// </summary>
    [Test]
    public async Task The_recordings_folder_names_every_recording_and_passes_over_empty_folders()
    {
        Assert.That(await UploadAsync(_world.Recordings.Path), Is.EqualTo(0));

        Assert.That(Uploaded(), Is.EquivalentTo(new[] { _mantiq.BaseName, _mantiqLater.BaseName, _kalam.BaseName }));
        Assert.That(_world.Google.FolderAt("Mantiq/2026-10-17"), Is.Not.Null);
        Assert.That(_world.Google.FolderAt("Mantiq/2026-10-17 (2)"), Is.Not.Null);
        Assert.That(_world.Google.FolderAt("Kalam"), Is.Not.Null);
        Assert.That(_world.Lines, Has.Member($"{Path.Combine(_world.Recordings.Path, "2026-10-15_22-25-30")} holds no recording; it is passed over"));
        Assert.That(_world.Lines.Where(line => line.Contains(FolderNames.MicCheck, StringComparison.Ordinal)), Is.Empty);
    }

    /// <summary>
    /// The recordings folder with a recording's file lying at its top, as an answer ElevenLabs was paid for that its own
    /// folder would not take, still names every course in it; the file at the top is left out, which is said with how to lay
    /// it out beside its recording, and no folder on Drive is named after the recordings folder.
    /// </summary>
    [Test]
    public async Task The_recordings_folder_with_a_kept_answer_at_its_top_still_names_every_course()
    {
        var kept = new RecordingFiles(_world.Recordings.Path, _kalam.BaseName).UnreadAnswer();
        File.WriteAllText(kept, "{}");

        Assert.That(await UploadAsync(_world.Recordings.Path), Is.EqualTo(0));

        Assert.That(Uploaded(), Is.EquivalentTo(new[] { _mantiq.BaseName, _mantiqLater.BaseName, _kalam.BaseName }));
        Assert.That(_world.Google.Uploaded.Select(file => file.Name), Has.None.EqualTo(Path.GetFileName(kept)));
        Assert.That(_world.Google.Files.Where(file => file.IsFolder).Select(file => file.Name), Has.None.EqualTo(Path.GetFileName(_world.Recordings.Path)));
        Assert.That(_world.Lines, Has.Member($"{Path.GetFileName(kept)} lies in {_world.Recordings.Path} itself, not in a recording's folder, so it does not go up; to lay it out beside its recording, rebuild it: {ClassScribe.Commands.CommandLine.Rebuild(kept)}"));
    }

    /// <summary>A file that is not a recording's, or a folder without one, is refused with exit code 1, and nothing is asked of Google.</summary>
    [Test]
    public async Task A_path_without_a_recording_is_refused()
    {
        var pdf = Path.Combine(_world.Recordings.Path, "Mantiq", "Al-Sullam_al-munawraq_translated.pdf");
        Assert.That(await UploadAsync(pdf), Is.EqualTo(1));
        Assert.That(_world.Logs.Problems, Has.Member($"{pdf} is not a file of a ClassScribe recording, so there is nothing to upload from it"));

        var empty = Path.Combine(_world.Recordings.Path, "2026-10-15_22-25-30");
        Assert.That(await UploadAsync(empty), Is.EqualTo(1));
        Assert.That(_world.Logs.Problems, Has.Member($"There is no ClassScribe recording in {empty}"));
        Assert.That(_world.Google.Requests, Is.Empty);
    }

    /// <summary>No path uploads every recording that waits, and gives 0 once all are up; something left waiting gives 1.</summary>
    [Test]
    public async Task No_path_uploads_what_waits()
    {
        _world.Manifests().Queue(_kalam);
        _world.Manifests().Write(_world.Manifests().Queue(_mantiq) with { NextAttemptUtc = TestRuns.Start.AddDays(1) });

        Assert.That(await UploadAsync(null), Is.EqualTo(0));
        Assert.That(Uploaded(), Is.EquivalentTo(new[] { _kalam.BaseName, _mantiq.BaseName }));

        _world.Manifests().Queue(_mantiqLater);
        using (new FileStream(_mantiqLater.Segment(1), FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            Assert.That(await UploadAsync(null), Is.EqualTo(1), "a recording being recorded waits");
    }

    /// <summary>
    /// free-space frees each recording the path names whose audio is all on Drive; one not uploaded keeps its audio and
    /// gives exit code 1; the total freed is said when there were several.
    /// </summary>
    [Test]
    public async Task Free_space_frees_what_is_on_Drive_and_says_the_total()
    {
        await UploadAsync(_mantiq.Folder);
        await UploadAsync(_kalam.Folder);

        Assert.That(await _world.UploadCommand().FreeSpaceAsync(_mantiq.Segment(1)), Is.EqualTo(0));
        Assert.That(File.Exists(_mantiq.Segment(1)), Is.False);
        Assert.That(File.Exists(_mantiq.Transcript()), Is.True);

        Assert.That(await _world.UploadCommand().FreeSpaceAsync(_world.Recordings.Path), Is.EqualTo(1), "the later Mantiq class has not gone up");
        Assert.That(File.Exists(_kalam.Segment(1)), Is.False);
        Assert.That(File.Exists(_mantiqLater.Segment(1)), Is.True);
        Assert.That(_world.Lines, Has.Some.StartsWith("Freed ").And.Some.EndsWith(" on this laptop in all"));
        Assert.That(_world.Google.Destructive, Is.Empty);
    }

    /// <summary>Signed out, free-space cannot check the copies on Drive, so it deletes nothing and says why, with exit code 1.</summary>
    [Test]
    public async Task Free_space_signed_out_deletes_nothing()
    {
        await UploadAsync(_mantiq.Folder);
        _world.Store.Stored = FakeCredentialStore.With(null).Stored;
        _world.Cache.Forget();

        Assert.That(await _world.UploadCommand().FreeSpaceAsync(_mantiq.Folder), Is.EqualTo(1));

        Assert.That(File.Exists(_mantiq.Segment(1)), Is.True);
        Assert.That(_world.Logs.Problems, Has.Member($"{GoogleTokens.NotSignedIn} Nothing more was deleted."));
    }

    /// <summary>The microphone's track is audio too: a recording with it frees both tracks, its .mic.wav included.</summary>
    [Test]
    public async Task Free_space_frees_both_tracks()
    {
        GoogleWorld.WriteAudio(_mantiq.Segment(1, Track.Microphone), 2000);
        await UploadAsync(_mantiq.Folder);

        Assert.That(await _world.UploadCommand().FreeSpaceAsync(_mantiq.Folder), Is.EqualTo(0));

        Assert.That(OnDisk.FindSegments(_mantiq).Concat(OnDisk.FindSegments(_mantiq, Track.Microphone)), Is.Empty);
    }
}
