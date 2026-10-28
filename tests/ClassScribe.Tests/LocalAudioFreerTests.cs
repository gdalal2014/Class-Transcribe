using System.Net;
using ClassScribe.Audio;
using ClassScribe.Commands;
using ClassScribe.Drive;
using ClassScribe.Recorder;
using ClassScribe.Storage;

namespace ClassScribe.Tests;

/// <summary>
/// Freeing room on the laptop: a recording's .wav, _partN.wav and .mic.wav files are deleted only once each was checked when
/// it went up and Drive, asked again now, still has a copy outside the trash with the same size and the MD5 of the file as
/// it is now; all of them or none. Never its transcripts, answers, marks or log, nor the PDFs beside it; never a recording
/// being recorded or transcribed. Nothing on Drive is touched: only files.get is asked. The manifest notes what was freed.
/// </summary>
[TestFixture]
public class LocalAudioFreerTests
{
    private static readonly DateTimeOffset MantiqStart = new(2026, 10, 17, 14, 4, 42, TimeSpan.Zero);
    private GoogleWorld _world = null!;
    private RecordingFiles _mantiq = null!;

    /// <summary>A signed-in world and today's class, both tracks, with its transcript, answer, marks, log and the PDFs beside it.</summary>
    [SetUp]
    public void SetUp()
    {
        _world = new GoogleWorld();
        _world.SignedIn();
        _mantiq = _world.WriteRecording(@"Mantiq\2026-10-17", "Mantiq", MantiqStart, 3000, ".md", ".json", ".marks.txt", ".log");
        GoogleWorld.WriteAudio(_mantiq.Segment(1, Track.Microphone), 3000);
        File.WriteAllText(Path.Combine(_mantiq.Folder, "Class_1_slides.pdf"), "%PDF slides");
    }

    /// <summary>Deletes the world's folders.</summary>
    [TearDown]
    public void TearDown() => _world.Dispose();

    /// <summary>Uploads the tests' recording, as it would have gone up after its class.</summary>
    private Task UploadAsync() => _world.RecordingUpload().UploadAsync(_mantiq, _ => Task.CompletedTask, CancellationToken.None);

    /// <summary>Frees the tests' recording.</summary>
    private Task<FreedAudio> FreeAsync() => _world.Freer().FreeAsync(_mantiq, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));

    /// <summary>The names of the files in the recording's folder, in order.</summary>
    private string[] FilesHere() => [.. Directory.GetFiles(_mantiq.Folder).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal)];

    /// <summary>
    /// Once the recording is on Drive, both tracks' audio is deleted from the laptop, and nothing else: the transcript,
    /// answer, marks, log and PDF stay. Drive is only asked about the two copies, and nothing there changes. The manifest
    /// notes when each was freed; the line says how much room was freed, where the copies are, and what stays.
    /// </summary>
    [Test]
    public async Task The_audio_goes_once_Drive_has_it_and_nothing_else_does()
    {
        await UploadAsync();
        var onDrive = _world.Google.Uploaded.ToDictionary(file => file.Name, file => file.Content);
        var bytes = new FileInfo(_mantiq.Segment(1)).Length + new FileInfo(_mantiq.Segment(1, Track.Microphone)).Length;
        var before = _world.Google.Requests.Count;

        var freed = await FreeAsync();

        var b = _mantiq.BaseName;
        Assert.That(freed, Is.EqualTo(new FreedAudio(_mantiq, freed.Deleted, bytes, null)));
        Assert.That(freed.Deleted, Is.EqualTo(new[] { $"{b}.wav", $"{b}.mic.wav" }));
        Assert.That(FilesHere(), Is.EqualTo(new[] { "Class_1_slides.pdf", $"{b}.json", $"{b}.log", $"{b}.marks.txt", $"{b}.md" }));
        Assert.That(_world.Google.Requests.Skip(before).Select(request => request.Method), Is.All.EqualTo(HttpMethod.Get));
        Assert.That(_world.Google.Uploaded.ToDictionary(file => file.Name, file => file.Content), Is.EquivalentTo(onDrive));
        Assert.That(_world.Google.Destructive, Is.Empty);
        var manifest = _world.Manifests().Read(_mantiq)!;
        Assert.That(manifest.Files.Where(file => file.FreedUtc is not null).Select(file => file.Name), Is.EquivalentTo(freed.Deleted));
        Assert.That(_world.Lines, Has.Member($"Freed {Sizes.Describe(bytes)} on this laptop: deleted 2 audio files of {b} ({b}.wav, {b}.mic.wav). Their copies stay on Google Drive, in My Drive > ClassScribe > Mantiq > 2026-10-17; the transcripts, ElevenLabs' answers, the marks and the log stay here"));
    }

    /// <summary>A recording that has not gone up keeps its audio; the refusal gives the command that uploads it.</summary>
    [Test]
    public async Task A_recording_not_on_Drive_keeps_its_audio()
    {
        var freed = await FreeAsync();

        Assert.That(freed.Deleted, Is.Empty);
        Assert.That(freed.Problem, Is.EqualTo($"{_mantiq.BaseName} has not gone up to Google Drive yet, so none of its audio was deleted. Upload it first: {CommandLine.DriveUpload(_mantiq.Segment(1))}"));
        Assert.That(File.Exists(_mantiq.Segment(1)), Is.True);
        Assert.That(_world.Google.Requests, Is.Empty);
    }

    /// <summary>
    /// When any audio file is not safe on Drive, none is deleted: one recorded since the upload, one whose copy on Drive is in
    /// the trash, one Drive now gives another MD5 for, or one changed on the laptop since it went up.
    /// </summary>
    [TestCase("new part")]
    [TestCase("trashed")]
    [TestCase("other MD5")]
    [TestCase("changed here")]
    public async Task Any_audio_not_safe_on_Drive_keeps_all_of_it(string why)
    {
        await UploadAsync();
        var mic = _world.Google.Uploaded.Single(file => file.Name.EndsWith(".mic.wav", StringComparison.Ordinal));
        switch (why)
        {
            case "new part":
                GoogleWorld.WriteAudio(_mantiq.Segment(2), 1000);
                break;
            case "trashed":
                mic.Trashed = true;
                break;
            case "other MD5":
                mic.Md5Override = "ffffffffffffffffffffffffffffffff";
                break;
            default:
                GoogleWorld.WriteAudio(_mantiq.Segment(1, Track.Microphone), 3002);
                break;
        }

        var freed = await FreeAsync();

        Assert.That(freed.Deleted, Is.Empty);
        Assert.That(freed.Problem, Does.StartWith($"None of the audio of {_mantiq.BaseName} was deleted: "));
        Assert.That(File.Exists(_mantiq.Segment(1)) && File.Exists(_mantiq.Segment(1, Track.Microphone)), Is.True);
        Assert.That(_world.Manifests().Read(_mantiq)!.Files.Where(file => file.FreedUtc is not null), Is.Empty);
    }

    /// <summary>
    /// Audio changed on the laptop since it went up but still of the same length, its time of writing put back as it was, is
    /// caught by the MD5 worked out afresh from the file: none of the audio is deleted, the reason names both MD5s, and the
    /// manifest notes nothing freed.
    /// </summary>
    [Test]
    public async Task Audio_changed_but_of_the_same_length_keeps_all_of_it()
    {
        await UploadAsync();
        var path = _mantiq.Segment(1, Track.Microphone);
        var written = File.GetLastWriteTimeUtc(path);
        var bytes = File.ReadAllBytes(path);
        var drive = _world.Google.Uploaded.Single(file => file.Name == Path.GetFileName(path)).Md5;
        bytes[^1] ^= 0xFF;
        File.WriteAllBytes(path, bytes);
        File.SetLastWriteTimeUtc(path, written);
        var here = Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(bytes));

        var freed = await FreeAsync();

        Assert.That(freed.Deleted, Is.Empty);
        Assert.That(freed.Problem, Is.EqualTo($"None of the audio of {_mantiq.BaseName} was deleted: {Path.GetFileName(path)} differs from its copy on Google Drive (here {bytes.Length} bytes, MD5 {here}; there {bytes.Length}, MD5 {drive})."));
        Assert.That(File.Exists(_mantiq.Segment(1)) && File.Exists(path), Is.True);
        Assert.That(_world.Manifests().Read(_mantiq)!.Files.Where(file => file.FreedUtc is not null), Is.Empty);
    }

    /// <summary>
    /// A recording put in the queue again while its audio is being freed, as by a transcript made meanwhile in another window,
    /// stays in the queue: what freeing notes in the manifest is added to it as it is then, and undoes nothing.
    /// </summary>
    [Test]
    public async Task A_queue_written_while_the_audio_is_freed_is_kept()
    {
        await UploadAsync();
        var release = new TaskCompletionSource();
        var mic = _world.Google.Uploaded.Single(file => file.Name.EndsWith(".mic.wav", StringComparison.Ordinal));
        _world.Google.Hold($"/drive/v3/files/{mic.Id}", release.Task);

        var freeing = FreeAsync();
        await Wait.UntilAsync(() => _world.Google.Requests.Any(request => request.Path == $"/drive/v3/files/{mic.Id}"));
        var queued = _world.Manifests().Queue(_mantiq);
        release.SetResult();
        var freed = await freeing;

        Assert.That(freed.Deleted, Has.Count.EqualTo(2));
        var manifest = _world.Manifests().Read(_mantiq)!;
        Assert.That((manifest.State, manifest.TimesQueued), Is.EqualTo((UploadState.Waiting, queued.TimesQueued)));
        Assert.That(manifest.Files.Where(file => file.FreedUtc is not null).Select(file => file.Name), Is.EquivalentTo(freed.Deleted));
    }

    /// <summary>A recording being recorded, its newest segment held for writing, or transcribed by its run, keeps its audio.</summary>
    [TestCase("written")]
    [TestCase("transcribing")]
    [TestCase("upload copy open")]
    public async Task A_recording_in_use_keeps_its_audio(string how)
    {
        await UploadAsync();
        IDisposable? held = null;
        switch (how)
        {
            case "written":
                held = new FileStream(_mantiq.Segment(1), FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
                break;
            case "transcribing":
                _world.Runtime.Processes.Start(TestRuns.ProcessId, TestRuns.ProcessStart);
                _world.Runtime.Journal.Begin(TestRuns.Record(TestRuns.Id(), RunState.Transcribing, name: "Mantiq") with { Folder = _mantiq.Folder, BaseName = _mantiq.BaseName });
                break;
            default:
                GoogleWorld.WriteAudio(_mantiq.Upload, 100);
                held = new FileStream(_mantiq.Upload, FileMode.Open, FileAccess.Read, FileShare.Read);
                break;
        }

        FreedAudio freed;
        using (held) freed = await FreeAsync();

        Assert.That(freed.Deleted, Is.Empty);
        Assert.That(freed.Problem, Does.StartWith(_mantiq.BaseName).And.EndWith("so none of its audio was deleted."));
        Assert.That(File.Exists(_mantiq.Segment(1)), Is.True);
    }

    /// <summary>
    /// A recording freed is done and safe on Drive: an upload after it sends nothing again, nothing waits, and the class
    /// folders list it with its audio on Drive and its length as measured when it went up.
    /// </summary>
    [Test]
    public async Task A_freed_recording_is_done_not_waiting()
    {
        await UploadAsync();
        var length = _world.ClassFolders().LengthOnDisk(_mantiq);
        await FreeAsync();
        var starts = _world.Google.Requests.Count(request => request.IsUploadStart);

        var again = await _world.RecordingUpload().UploadAsync(_mantiq, _ => Task.CompletedTask, CancellationToken.None);

        Assert.That(again.Outcome, Is.EqualTo(RecordingOutcome.Done));
        Assert.That(_world.Google.Requests.Count(request => request.IsUploadStart), Is.EqualTo(starts));
        Assert.That(_world.Manifests().Waiting(), Is.Empty);
        var listed = _world.ClassFolders().ListAll().Single();
        Assert.That((listed.Files, listed.Length, listed.HasTranscript, listed.HasMicrophone), Is.EqualTo((_mantiq, length, true, true)));
        Assert.That(listed.OnDrive, Is.EqualTo(new AudioOnDrive(length, true, "My Drive > ClassScribe > Mantiq > 2026-10-17", _world.Manifests().Read(_mantiq)!.DriveLink, AllOfIt: true)));
    }

    /// <summary>A recording whose audio is gone already has nothing to delete; that is said, with where the audio is.</summary>
    [Test]
    public async Task A_recording_freed_already_has_nothing_to_delete()
    {
        await UploadAsync();
        await FreeAsync();

        var again = await FreeAsync();

        Assert.That((again.Deleted.Count, again.Bytes, again.Problem), Is.EqualTo((0, 0L, (string?)null)));
        Assert.That(_world.Lines, Has.Member($"{_mantiq.BaseName} has no audio on this laptop to delete; its audio is on Google Drive, in My Drive > ClassScribe > Mantiq > 2026-10-17"));
    }

    /// <summary>Drive refusing to show a copy while they are checked is thrown on, with nothing deleted.</summary>
    [Test]
    public async Task A_refusal_while_the_copies_are_checked_deletes_nothing()
    {
        await UploadAsync();
        _world.Google.FailWhen(request => request.Method == HttpMethod.Get && request.Path.StartsWith("/drive/v3/files/", StringComparison.Ordinal), HttpStatusCode.Forbidden,
            FakeGoogle.DriveError(403, "insufficientFilePermissions", "No."));

        Assert.ThrowsAsync<GoogleDriveException>(FreeAsync);
        Assert.That(File.Exists(_mantiq.Segment(1)), Is.True);
    }
}
