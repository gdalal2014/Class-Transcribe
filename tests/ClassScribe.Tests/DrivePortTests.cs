using System.Security.Cryptography;
using ClassScribe.Drive;
using ClassScribe.Recorder;
using ClassScribe.Settings;
using ClassScribe.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace ClassScribe.Tests;

/// <summary>
/// The contracts of the Drive ports (architecture 4.3, slice 3c) over their real adapters: Drive as the use cases use it
/// (<see cref="IGoogleDrive"/>) and Google's OAuth endpoints as the sign-in uses them (<see cref="IGoogleAccount"/>), both
/// through <see cref="FakeGoogle"/>; a client file read (<see cref="IGoogleClientFiles"/>); the upload records
/// (<see cref="IUploadManifests"/>) and one upload pass at a time (<see cref="IDriveUploadLock"/>) in a temporary ClassScribe
/// folder; the members <see cref="IRecordingFolder"/> gained for the uploads, over a temporary folder; and that the window's
/// host and the recorder's resolve each port to its adapter. Nothing reaches Google.
/// </summary>
[TestFixture]
public class DrivePortTests
{
    private GoogleWorld _world = null!;
    private TempFolder _temp = null!;

    /// <summary>A fake Google, a temporary ClassScribe folder and a temporary folder of the test's own.</summary>
    [SetUp]
    public void SetUp()
    {
        _world = new GoogleWorld();
        _temp = new TempFolder();
    }

    /// <summary>Deletes the folders.</summary>
    [TearDown]
    public void TearDown()
    {
        _world.Dispose();
        _temp.Dispose();
    }

    /// <summary>An upload watch that waits for nothing and keeps nothing.</summary>
    private sealed class NoWatch : IUploadWatch
    {
        /// <inheritdoc />
        public Task BeforePieceAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        /// <inheritdoc />
        public void SessionChanged(UploadSession? session)
        {
        }
    }

    /// <summary>The bytes of <paramref name="length"/> as an upload source, with their MD5 as Drive writes it.</summary>
    private static (UploadSource Source, string Md5) Source(int length)
    {
        var content = new byte[length];
        for (var i = 0; i < content.Length; i++) content[i] = (byte)(i * 7);
        var md5 = Convert.ToHexStringLower(MD5.HashData(content));
        return (new UploadSource(new MemoryStream(content), content.Length, md5, "audio/wav"), md5);
    }

    /// <summary>
    /// Drive through the port: the account with its email, name and storage as Application records; ClassScribe's folder
    /// found or made, and found again; a path under it, level by level; a file sent with the resumable upload, which comes
    /// back as an item with Drive's size and MD5, outside the trash and no folder, found again by its id and among its
    /// folder's children; nothing for an id Drive does not have; and nothing on Drive changed or removed.
    /// </summary>
    [Test]
    public async Task Drive_gives_the_account_the_folders_and_the_files_as_application_records()
    {
        _world.SignedIn();
        var drive = _world.GoogleDrive();

        var account = await drive.GetAccountAsync(CancellationToken.None);
        var root = await drive.FindOrCreateRootAsync(CancellationToken.None);
        var found = await drive.FindRootAsync(CancellationToken.None);
        var path = await drive.FindOrCreatePathAsync(["Mantiq", "2026-10-17"], CancellationToken.None);
        var (source, md5) = Source(1000);
        var sending = drive.SendAsync(source, new UploadTarget("Mantiq.wav", path.Folder.Id, null), null, new NoWatch(), CancellationToken.None);
        await _world.Time.AdvanceUntilAsync(() => sending.IsCompleted, TimeSpan.FromSeconds(1), timeoutSeconds: 30);
        var made = await sending;
        var item = await drive.GetItemAsync(made.Id!, CancellationToken.None);
        var children = await drive.ListChildrenAsync(path.Folder.Id, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(account, Is.EqualTo(new DriveAccount("student@example.com", "A Student", new DriveSpace(_world.Google.StorageUsage, _world.Google.StorageLimit))));
            Assert.That(root.Name, Is.EqualTo("ClassScribe"));
            Assert.That(found, Is.EqualTo(root));
            Assert.That(path.Path, Is.EqualTo("ClassScribe/Mantiq/2026-10-17"));
            Assert.That(made.Id, Is.Not.Null.And.Not.Empty);
            Assert.That(item, Is.EqualTo(new DriveItem(made.Id, "Mantiq.wav", IsFolder: false, 1000, md5, Trashed: false)));
            Assert.That(children, Is.EqualTo(new[] { item }));
            Assert.That(_world.Google.Destructive, Is.Empty);
        });
        Assert.That(await drive.GetItemAsync("no-such-id", CancellationToken.None), Is.Null);
    }

    /// <summary>
    /// The account through the port: a refresh token revoked (Google says OK, no error), a token Google does not know refused
    /// with its error in words, a code Google never issued refused with invalid_grant, and the answer's text, which a log line
    /// might hold by mistake, never a token.
    /// </summary>
    [Test]
    public async Task The_account_exchanges_and_revokes_through_google_and_its_answers_hide_the_tokens()
    {
        var account = (IGoogleAccount)_world.Tokens();
        var logger = _world.Log<DrivePortTests>();
        var token = _world.Google.IssueRefreshToken();

        var revoked = await account.RevokeAsync(token, logger, CancellationToken.None);
        var unknown = await account.RevokeAsync("not-a-token", logger, CancellationToken.None);
        var refused = await account.ExchangeCodeAsync(new GoogleClient(FakeGoogle.ClientId, FakeGoogle.ClientSecret), "no-such-code", "verifier", "http://127.0.0.1:5/", logger,
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That((revoked.Status, revoked.Error, revoked.Described), Is.EqualTo((200, (string?)null, "no error given")));
            Assert.That((unknown.Status, unknown.Error, unknown.Described), Is.EqualTo((400, "invalid_token", "invalid_token: Token expired or revoked")));
            Assert.That((refused.Status, refused.Error, refused.AccessToken, refused.RefreshToken), Is.EqualTo((400, "invalid_grant", (string?)null, (string?)null)));
            Assert.That(refused.Described, Does.StartWith("invalid_grant: "));
            var answer = new GoogleAnswer(200, "ya29.secret-access", 3600, "1//secret-refresh", "scope", null, "no error given");
            Assert.That(answer.ToString(), Does.Not.Contain("secret").And.Contain("HasAccessToken = True").And.Contain("HasRefreshToken = True"));
        });
    }

    /// <summary>A token seeded by a sign-in is the one Drive gets, with no request to Google; once forgotten, a renewal asks Google.</summary>
    [Test]
    public async Task A_seeded_token_is_used_until_it_is_forgotten()
    {
        _world.SignedIn();
        var tokens = _world.Tokens();
        var account = (IGoogleAccount)tokens;
        var requests = _world.Google.Requests.Count;

        account.Seed("seeded-token", TimeSpan.FromHours(1));
        var seeded = await tokens.GetAccessTokenAsync(CancellationToken.None);
        account.Forget();
        var renewed = await tokens.GetAccessTokenAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(seeded, Is.EqualTo("seeded-token"));
            Assert.That(renewed, Is.Not.EqualTo("seeded-token"));
            Assert.That(_world.Google.Requests.Count, Is.EqualTo(requests + 1), "one request, the renewal after the forget");
        });
    }

    /// <summary>A client file as Google writes it is read to the client; a Web application's file is refused with what to do.</summary>
    [Test]
    public void A_client_file_is_read_and_another_kind_refused()
    {
        IGoogleClientFiles files = new GoogleClientFile();

        var client = files.Read(_world.WriteClientFile());
        var refusal = Assert.Throws<GoogleDriveException>(() => files.Read(_world.WriteClientFile("web", name: "web.json")))!;

        Assert.Multiple(() =>
        {
            Assert.That(client, Is.EqualTo(new GoogleClient(FakeGoogle.ClientId, FakeGoogle.ClientSecret)));
            Assert.That(refusal.Problem, Is.EqualTo(DriveProblem.ClientFile));
            Assert.That(refusal.Message, Does.Contain("Web application client"));
        });
    }

    /// <summary>
    /// The manifests through the port: a recording queued is read back waiting, queued once, and listed among those that wait;
    /// kept waiting with a problem it keeps the count; written done it leaves the queue but not the folder; its file is under
    /// the manifests' folder; and what of it is on this laptop is counted without its upload copy.
    /// </summary>
    [Test]
    public void The_manifests_queue_read_keep_waiting_list_and_count_what_is_on_disk()
    {
        var files = _world.WriteRecording(@"Mantiq\2026-10-17", "Mantiq", TestRuns.Start, extras: ".md");
        File.WriteAllBytes(files.Upload, new byte[10]);
        IUploadManifests manifests = _world.Manifests();

        var queued = manifests.Queue(files);
        var read = manifests.Read(files);
        var waiting = manifests.Waiting();
        var kept = manifests.KeepWaiting(files, "busy");
        manifests.Write(kept with { State = UploadState.Done, LastError = null });

        Assert.Multiple(() =>
        {
            Assert.That((queued.State, queued.TimesQueued, queued.BaseName), Is.EqualTo((UploadState.Waiting, 1, files.BaseName)));
            Assert.That((read?.State, read?.TimesQueued, read?.QueuedUtc, read?.Folder), Is.EqualTo((queued.State, queued.TimesQueued, queued.QueuedUtc, queued.Folder)));
            Assert.That(waiting.Select(manifest => manifest.BaseName), Is.EqualTo(new[] { files.BaseName }));
            Assert.That((kept.LastError, kept.TimesQueued), Is.EqualTo(("busy", 1)));
            Assert.That(manifests.Waiting(), Is.Empty);
            Assert.That(manifests.All().Single().State, Is.EqualTo(UploadState.Done));
            Assert.That(manifests.PathOf(files), Does.StartWith(manifests.Folder).And.EndWith(".json"));
            Assert.That(manifests.OnDisk(files).Count, Is.EqualTo(2), "the .wav and the .md, not the upload copy");
            Assert.That(manifests.OnDisk(files).Bytes, Is.EqualTo(new FileInfo(files.Segment(1)).Length + new FileInfo(files.Transcript()).Length));
        });
    }

    /// <summary>The upload lock through the port: taken once, refused to a second taker while held, and free again once let go.</summary>
    [Test]
    public void The_upload_lock_is_held_by_one_at_a_time()
    {
        IDriveUploadLock first = _world.Lock();
        IDriveUploadLock second = _world.Lock();

        using (var held = first.TryTake())
        {
            Assert.That(held, Is.Not.Null);
            Assert.That(second.TryTake(), Is.Null, "held by the first");
        }

        using var again = second.TryTake();
        Assert.That(again, Is.Not.Null, "free once the first let go");
    }

    /// <summary>
    /// The recording's folder through its new members: every file of the recording's own and none beside it; a file opened to
    /// read, refused with a sharing violation while another holds it for writing unless others may write, and read then; a
    /// file deleted; and the upload copy seen in use only while a program holds it open.
    /// </summary>
    [Test]
    public async Task The_recording_folder_lists_opens_deletes_and_sees_the_upload_copy_in_use()
    {
        var files = RecordingFiles.ForNew(_temp.Path, "Mantiq", TestRuns.Start);
        File.WriteAllBytes(files.Segment(1), [1, 2, 3]);
        File.WriteAllText(files.Transcript(), "transcript");
        File.WriteAllText(Path.Combine(_temp.Path, "Mantiq_notes.pdf"), "pdf");
        IRecordingFolder folder = new RecordingFolder();

        var own = folder.FilesOf(files).Select(Path.GetFileName).OrderBy(name => name, StringComparer.Ordinal).ToList();
        byte[] read;
        await using (var stream = folder.OpenToRead(files.Segment(1), othersMayWrite: false))
        {
            read = new byte[stream.Length];
            await stream.ReadExactlyAsync(read);
        }

        IOException? refused;
        string shared;
        using (var writer = new FileStream(files.Transcript(), FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            refused = Assert.Throws<IOException>(() => folder.OpenToRead(files.Transcript(), othersMayWrite: false));
            await using var text = folder.OpenToRead(files.Transcript(), othersMayWrite: true);
            shared = await new StreamReader(text).ReadToEndAsync();
        }

        var noCopy = folder.IsUploadCopyInUse(files);
        File.WriteAllBytes(files.Upload, [0]);
        bool inUse;
        using (new FileStream(files.Upload, FileMode.Open, FileAccess.Read, FileShare.None)) inUse = folder.IsUploadCopyInUse(files);
        var closed = folder.IsUploadCopyInUse(files);
        folder.Delete(files.Transcript());

        Assert.Multiple(() =>
        {
            Assert.That(own, Is.EqualTo(new[] { Path.GetFileName(files.Transcript()), Path.GetFileName(files.Segment(1)) }));
            Assert.That(read, Is.EqualTo(new byte[] { 1, 2, 3 }));
            Assert.That(refused!.HResult, Is.EqualTo(unchecked((int)0x80070020)), "a sharing violation, as the uploads tell a file in use by");
            Assert.That(shared, Is.EqualTo("transcript"));
            Assert.That((noCopy, inUse, closed), Is.EqualTo((false, true, false)));
            Assert.That(File.Exists(files.Transcript()), Is.False);
        });
    }

    /// <summary>Both hosts resolve each Drive port to the adapter they hold: the same singletons for the manifests and the lock, the tokens, Drive and the client files for the rest.</summary>
    [Test]
    public void Both_hosts_resolve_the_ports_to_the_adapters_they_hold()
    {
        File.WriteAllText(_temp.File(SettingsFile.FileName), "{}");
        var places = new SettingsPlaces(_temp.Path, userFolder: null);
        var runtime = _world.Runtime;
        using var window = AppHost.Build(places, "test-key", services =>
            services.AddSingleton(runtime.Folder).AddSingleton(new RoamingFolder(_temp.File("Roaming"))));
        var runId = TestRuns.Id();
        using var log = new RecorderLog(runtime.Time);
        using var recorder = AppHost.BuildRecorder(new RecorderStart(runId, TestRuns.Request(runId), new Dictionary<string, string?>()), "test-key", log, runtime.Folder, places, runtime.Time,
            services => services.AddSingleton(new RoamingFolder(_temp.File("Roaming"))));

        Assert.Multiple(() =>
        {
            foreach (var (name, host) in new[] { ("the window", window.Services), ("the recorder", recorder.Services) })
            {
                using var scope = host.CreateScope();
                var services = scope.ServiceProvider;
                Assert.That(services.GetRequiredService<IUploadManifests>(), Is.SameAs(services.GetRequiredService<UploadManifests>()), $"{name}: the manifests");
                Assert.That(services.GetRequiredService<IDriveUploadLock>(), Is.SameAs(services.GetRequiredService<DriveUploadLock>()), $"{name}: the upload lock");
                Assert.That(services.GetRequiredService<IGoogleAccount>(), Is.TypeOf<GoogleTokens>(), $"{name}: the account");
                Assert.That(services.GetRequiredService<IGoogleDrive>(), Is.TypeOf<GoogleDrive>(), $"{name}: Drive");
                Assert.That(services.GetRequiredService<Lazy<IGoogleDrive>>().Value, Is.TypeOf<GoogleDrive>(), $"{name}: Drive, made when asked");
                Assert.That(services.GetRequiredService<IGoogleClientFiles>(), Is.TypeOf<GoogleClientFile>(), $"{name}: the client files");
            }
        });
    }
}
