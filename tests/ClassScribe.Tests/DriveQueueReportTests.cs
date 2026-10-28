using ClassScribe.Drive;
using ClassScribe.Recorder;
using ClassScribe.Storage;

namespace ClassScribe.Tests;

/// <summary>
/// What the menu's header and the report at the start say of Google Drive, from the store and the manifests alone, asking
/// Google nothing: nothing before Drive is set up; signed in with nothing waiting; how many recordings wait and how much;
/// an upload going on now, in this process or another, with how far it is; not signed in, or a store that cannot be read,
/// with how to sign in; and a recording being recorded or transcribed now, which its own run queued, never counts as
/// waiting. Local times are read in US Eastern time.
/// </summary>
[TestFixture]
public class DriveQueueReportTests
{
    private static readonly DateTimeOffset MantiqStart = new(2026, 10, 17, 14, 4, 42, TimeSpan.Zero);
    private GoogleWorld _world = null!;
    private RecordingFiles _mantiq = null!;
    private RecordingFiles _kalam = null!;

    /// <summary>A signed-in world with two recordings on the laptop, neither in the queue yet.</summary>
    [SetUp]
    public void SetUp()
    {
        _world = new GoogleWorld();
        _world.SignedIn();
        _mantiq = _world.WriteRecording(@"Mantiq\2026-10-17", "Mantiq", MantiqStart, extras: [".md"]);
        _kalam = _world.WriteRecording("Kalam", "Kalam", new DateTimeOffset(2026, 10, 15, 23, 0, 2, TimeSpan.Zero), 2000);
    }

    /// <summary>Deletes the world's folders.</summary>
    [TearDown]
    public void TearDown() => _world.Dispose();

    /// <summary>The size of <paramref name="files"/> on this laptop, as the lines say it.</summary>
    private static string SizeOf(params RecordingFiles[] files) => Sizes.Describe(files.Sum(recording => UploadManifests.OnDisk(recording).Bytes));

    /// <summary>Before Drive is set up, neither the header nor the report says anything of it.</summary>
    [Test]
    public void Before_Drive_is_set_up_nothing_is_said()
    {
        _world.Store.Stored = null;
        _world.Manifests().Queue(_mantiq);

        Assert.That(_world.QueueReport().HeaderLine(), Is.Null);
        Assert.That(_world.QueueReport().Waiting(), Is.Null);
    }

    /// <summary>Signed in with nothing waiting, the header says so, and the report has nothing to offer.</summary>
    [Test]
    public void Signed_in_with_nothing_waiting()
    {
        Assert.That(_world.QueueReport().HeaderLine(), Is.EqualTo("Google Drive: signed in; nothing waits to go up."));
        Assert.That(_world.QueueReport().Waiting(), Is.Null);
    }

    /// <summary>
    /// With recordings waiting, the header says how many and how much, and that 9 > U uploads them; the report names one
    /// recording, with its size and its last try, and several by their number and size, with the latest try.
    /// </summary>
    [Test]
    public void Recordings_waiting_are_counted_with_their_size_and_last_try()
    {
        _world.Manifests().Queue(_kalam);
        Assert.That(_world.QueueReport().HeaderLine(), Is.EqualTo($"Google Drive: 1 recording waits to go up ({SizeOf(_kalam)}). 9 > U uploads it."));
        Assert.That(_world.QueueReport().Waiting(), Is.EqualTo(new DriveWaiting($"Google Drive: 1 recording waits to go up ({_kalam.BaseName}, {SizeOf(_kalam)}; not tried yet).", 1, SignedIn: true)));

        _world.Manifests().Write(_world.Manifests().Queue(_mantiq) with
        {
            LastTriedUtc = new DateTimeOffset(2026, 10, 18, 1, 40, 0, TimeSpan.Zero),
            LastError = "Google Drive could not be reached (No such host is known.)."
        });

        Assert.That(_world.QueueReport().HeaderLine(), Is.EqualTo($"Google Drive: 2 recordings wait to go up ({SizeOf(_kalam, _mantiq)}). 9 > U uploads them."));
        Assert.That(_world.QueueReport().Waiting()!.Line,
            Is.EqualTo($"Google Drive: 2 recordings wait to go up ({SizeOf(_kalam, _mantiq)} in all; last try 17 Oct 21:40: Google Drive could not be reached (No such host is known.))."));
    }

    /// <summary>A recording its own run is recording or transcribing now, as every class is from the start of its run, does not count as waiting.</summary>
    [TestCase(RunState.Recording)]
    [TestCase(RunState.Transcribing)]
    public void A_recording_being_recorded_or_transcribed_does_not_count(RunState state)
    {
        _world.Manifests().Queue(_mantiq);
        _world.Runtime.Processes.Start(TestRuns.ProcessId, TestRuns.ProcessStart);
        _world.Runtime.Journal.Begin(TestRuns.Record(TestRuns.Id(), state, name: "Mantiq") with { BaseName = _mantiq.BaseName, Folder = _mantiq.Folder });

        Assert.That(_world.QueueReport().HeaderLine(), Is.EqualTo("Google Drive: signed in; nothing waits to go up."));
        Assert.That(_world.QueueReport().Waiting(), Is.Null);
    }

    /// <summary>
    /// An upload going on now, in this process or a recorder, shows as uploading, with how much of the recording Drive has,
    /// and what else waits; the report offers nothing for it.
    /// </summary>
    [Test]
    public void An_upload_going_on_now_shows_how_far_it_is()
    {
        _world.Manifests().Queue(_kalam);
        var transcript = new FileInfo(_mantiq.Transcript()).Length;
        var audio = new FileInfo(_mantiq.Segment(1)).Length;
        var confirmed = ((transcript + audio) * 41 + 99) / 100 - transcript;
        _world.Manifests().Write(_world.Manifests().Queue(_mantiq) with
        {
            LastTriedUtc = _world.Time.GetUtcNow().AddMinutes(-3),
            UpdatedUtc = _world.Time.GetUtcNow().AddMinutes(-1),
            Files =
            [
                new ManifestFile($"{_mantiq.BaseName}.md") { Size = transcript, Md5 = "a", DriveId = "x", DriveSize = transcript, DriveMd5 = "a", VerifiedUtc = _world.Time.GetUtcNow() },
                new ManifestFile($"{_mantiq.BaseName}.wav") { Size = audio, Session = new UploadSession("https://www.googleapis.com/upload", _world.Time.GetUtcNow(), audio, "b", confirmed, null) }
            ]
        });

        Assert.That(_world.QueueReport().HeaderLine(), Is.EqualTo($"Google Drive: uploading {_mantiq.BaseName} (41%); 1 more waits."));
        Assert.That(_world.QueueReport().Waiting()!.Count, Is.EqualTo(1), "the one uploading is not offered");
    }

    /// <summary>A try that failed a minute ago is no upload going on: the recording counts as waiting, with what went wrong.</summary>
    [Test]
    public void A_try_that_failed_lately_counts_as_waiting()
    {
        _world.Manifests().Write(_world.Manifests().Queue(_mantiq) with
        {
            LastTriedUtc = _world.Time.GetUtcNow().AddMinutes(-2),
            UpdatedUtc = _world.Time.GetUtcNow().AddMinutes(-1),
            LastError = "Google Drive is full."
        });

        Assert.That(_world.QueueReport().HeaderLine(), Is.EqualTo($"Google Drive: 1 recording waits to go up ({SizeOf(_mantiq)}). 9 > U uploads it."));
        Assert.That(_world.QueueReport().Waiting()!.Line, Does.EndWith("; last try 15 Oct 18:58: Google Drive is full)."));
    }

    /// <summary>An upload going on whose every byte Drive has, its last check still to come, shows 99%, never 100%, until it is done.</summary>
    [Test]
    public void An_upload_with_every_byte_on_Drive_shows_99_percent_until_it_is_done()
    {
        var transcript = new FileInfo(_mantiq.Transcript()).Length;
        var audio = new FileInfo(_mantiq.Segment(1)).Length;
        _world.Manifests().Write(_world.Manifests().Queue(_mantiq) with
        {
            LastTriedUtc = _world.Time.GetUtcNow().AddMinutes(-3),
            UpdatedUtc = _world.Time.GetUtcNow().AddMinutes(-1),
            Files =
            [
                new ManifestFile($"{_mantiq.BaseName}.md") { Size = transcript, Md5 = "a", DriveId = "x", DriveSize = transcript, DriveMd5 = "a", VerifiedUtc = _world.Time.GetUtcNow() },
                new ManifestFile($"{_mantiq.BaseName}.wav") { Size = audio, Session = new UploadSession("https://www.googleapis.com/upload", _world.Time.GetUtcNow(), audio, "b", audio, null) }
            ]
        });

        Assert.That(_world.QueueReport().HeaderLine(), Is.EqualTo($"Google Drive: uploading {_mantiq.BaseName} (99%)."));
    }

    /// <summary>
    /// A recording done a moment ago and put in the queue again, as a transcript made later does, waits to go up: the upload
    /// that ended is not taken for one going on now.
    /// </summary>
    [Test]
    public async Task A_recording_queued_again_right_after_its_upload_waits_rather_than_uploading()
    {
        await _world.RecordingUpload().UploadAsync(_mantiq, _ => Task.CompletedTask, CancellationToken.None);
        _world.Manifests().Queue(_mantiq);

        Assert.That(_world.QueueReport().HeaderLine(), Is.EqualTo($"Google Drive: 1 recording waits to go up ({SizeOf(_mantiq)}). 9 > U uploads it."));
    }

    /// <summary>An upload that has written nothing for more than five minutes, as one whose process ended, counts as waiting.</summary>
    [Test]
    public void An_upload_silent_for_more_than_five_minutes_counts_as_waiting()
    {
        _world.Manifests().Write(_world.Manifests().Queue(_mantiq) with
        {
            LastTriedUtc = _world.Time.GetUtcNow().AddMinutes(-20),
            UpdatedUtc = _world.Time.GetUtcNow().AddMinutes(-6)
        });

        Assert.That(_world.QueueReport().HeaderLine(), Does.StartWith("Google Drive: 1 recording waits to go up ("));
        Assert.That(_world.QueueReport().Waiting()!.Line, Does.EndWith("; last try 15 Oct 18:40, not finished)."));
    }

    /// <summary>Not signed in, the header says so, with what waits and how to sign in; the report says what waits, not to upload it.</summary>
    [Test]
    public void Not_signed_in_says_how_to_sign_in()
    {
        _world.Store.Stored = FakeCredentialStore.With(null).Stored;
        Assert.That(_world.QueueReport().HeaderLine(), Is.EqualTo("Google Drive: not signed in. 9 > S signs in."));

        _world.Manifests().Queue(_kalam);
        Assert.That(_world.QueueReport().HeaderLine(), Is.EqualTo("Google Drive: not signed in; 1 recording waits to go up. 9 > S signs in."));
        Assert.That(_world.QueueReport().Waiting()!.SignedIn, Is.False);
    }

    /// <summary>A store that cannot be read counts as not signed in, and the header says so.</summary>
    [Test]
    public void A_store_that_cannot_be_read_says_to_sign_in_again()
    {
        _world.Store.MakeUnreadable("Windows could not decrypt it");

        Assert.That(_world.QueueReport().HeaderLine(), Is.EqualTo("Google Drive: the sign-in cannot be read. 9 > S signs in again."));
    }

    /// <summary>With the uploads after each class off, the header says so, as what waits then goes up only by hand.</summary>
    [Test]
    public void With_the_uploads_after_each_class_off_the_header_says_so()
    {
        _world.Options.Set(new DriveOptions { Enabled = false });
        _world.Manifests().Queue(_kalam);

        Assert.That(_world.QueueReport().HeaderLine(),
            Is.EqualTo($"Google Drive: 1 recording waits to go up ({SizeOf(_kalam)}). 9 > U uploads it. Uploads after each class are off (Drive:Enabled)."));
    }
}
