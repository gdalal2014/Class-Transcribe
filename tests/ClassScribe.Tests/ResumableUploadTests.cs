using System.Net;
using System.Security.Cryptography;
using ClassScribe.Drive;

namespace ClassScribe.Tests;

/// <summary>
/// One file sent with Drive's resumable upload protocol, against the in-memory Google: pieces of 256 KiB with their
/// Content-Range; a short Range resends the rest; a dropped or busy piece is followed by a wait and the question how much
/// Drive has; a refused token is renewed; a session Drive has forgotten starts again from the beginning, once; a kept
/// session goes on in another uploader, unless it is older than six days, for other content, or not Drive's own address.
/// Nothing leaves the process, and the session's address is never logged.
/// </summary>
[TestFixture]
public class ResumableUploadTests
{
    private GoogleWorld _world = null!;
    private Watch _watch = null!;

    /// <summary>A signed-in world and a watch that keeps what the upload tells.</summary>
    [SetUp]
    public void SetUp()
    {
        _world = new GoogleWorld();
        _world.SignedIn();
        _folder = _world.Google.AddFolder("2026-10-17");
        _watch = new Watch();
    }

    /// <summary>Deletes the world's folders.</summary>
    [TearDown]
    public void TearDown() => _world.Dispose();

    private FakeDriveFile _folder = null!;

    /// <summary><paramref name="length"/> bytes that differ from piece to piece.</summary>
    private static byte[] Bytes(int length) => [.. Enumerable.Range(0, length).Select(i => (byte)(i * 7 % 251))];

    /// <summary>What goes up: <paramref name="content"/>, its MD5 and a type.</summary>
    private static UploadSource Source(byte[] content) => new(new MemoryStream(content), content.Length, Convert.ToHexStringLower(MD5.HashData(content)), "audio/wav");

    /// <summary>A new file named Mantiq.wav in the test's folder.</summary>
    private UploadTarget NewFile => new("Mantiq.wav", _folder.Id, null);

    /// <summary>Sends <paramref name="content"/> with a fresh uploader, going on with <paramref name="kept"/>, moving the clock while it waits.</summary>
    private async Task<DriveFile> SendAsync(byte[] content, UploadSession? kept = null, UploadTarget? target = null, CancellationToken cancellationToken = default)
    {
        var sending = _world.Resumable().SendAsync(Source(content), target ?? NewFile, kept, _watch, cancellationToken);
        await _world.Time.AdvanceUntilAsync(() => sending.IsCompleted, TimeSpan.FromSeconds(1), timeoutSeconds: 30);
        return await sending;
    }

    /// <summary>The upload starts that went to Drive.</summary>
    private int Starts => _world.Google.Requests.Count(request => request.IsUploadStart || request.IsRevisionStart);

    /// <summary>
    /// Without a piece size for tests, the pieces are Drive:ChunkMegabytes MiB each, a multiple of 256 KiB as Drive asks, so
    /// 1 is 1,048,576 bytes; the last is shorter, and the file on Drive is the very bytes.
    /// </summary>
    [Test]
    public async Task The_pieces_are_Drive_ChunkMegabytes_MiB()
    {
        _world.Options.Set(new DriveOptions { ChunkMegabytes = 1 });
        var content = Bytes(2_500_000);
        var uploader = new ResumableUpload(_world.Drive(), _world.Retry(), _world.Options, _world.Time, _world.Log<ResumableUpload>());

        var sending = uploader.SendAsync(Source(content), NewFile, null, _watch, CancellationToken.None);
        await _world.Time.AdvanceUntilAsync(() => sending.IsCompleted, TimeSpan.FromSeconds(1), timeoutSeconds: 30);
        await sending;

        Assert.That(_world.Google.Pieces.Select(piece => piece.Bytes.Length), Is.EqualTo(new[] { 1_048_576, 1_048_576, 2_500_000 - 2 * 1_048_576 }));
        Assert.That(_world.Google.Uploaded.Single().Content, Is.EqualTo(content));
    }

    /// <summary>Drive refuses a piece that is not the last and not a multiple of 256 KiB, as the real Drive does, and says so.</summary>
    [Test]
    public async Task Drive_refuses_a_piece_that_is_not_a_multiple_of_256_KiB()
    {
        var drive = _world.Drive();
        var address = UploadAddress.TryParse(await drive.StartUploadAsync(new DriveFileMetadata("a.wav", "audio/wav", [_folder.Id], null), "audio/wav", 1_000_000, CancellationToken.None),
            "https://www.googleapis.com/", revisionOf: null)!;
        using var content = new MemoryStream(Bytes(1_000_000));

        Assert.That(async () => await drive.SendPieceAsync(address, () => new PieceContent(content, 0, 300_000, content.Length), DriveClient.CallLimit, CancellationToken.None),
            Throws.InstanceOf<GoogleDriveException>().With.Message.Contains("not a multiple of 256 KiB"));
    }

    /// <summary>
    /// A file of three pieces goes up in pieces of 256 KiB, the last shorter, each with its Content-Range, after one start that
    /// names it, its type and folder; the file on Drive is the very bytes; the watch hears of the session and of each piece
    /// Drive confirmed, and is asked before each piece.
    /// </summary>
    [Test]
    public async Task A_file_goes_up_in_pieces_of_256_KiB()
    {
        var content = Bytes(700_000);

        var made = await SendAsync(content);

        var file = _world.Google.Uploaded.Single();
        Assert.That((made.Id, file.Name, file.MimeType, file.Parents.Single()), Is.EqualTo((file.Id, "Mantiq.wav", "audio/wav", _folder.Id)));
        Assert.That(file.Content, Is.EqualTo(content));
        Assert.That(_world.Google.Pieces.Select(piece => piece.ContentRange), Is.EqualTo(new[] { "bytes 0-262143/700000", "bytes 262144-524287/700000", "bytes 524288-699999/700000" }));
        Assert.That(_world.Google.Pieces.Select(piece => piece.Bytes.Length), Is.EqualTo(new[] { 262144, 262144, 175712 }));
        Assert.That(_watch.Sessions.Select(session => session?.Confirmed), Is.EqualTo(new long?[] { 0, 262144, 524288 }));
        Assert.That(_watch.Pieces, Is.EqualTo(3));
        Assert.That(Starts, Is.EqualTo(1));
    }

    /// <summary>A file of no bytes goes up with one empty piece, bytes */0, after its start.</summary>
    [Test]
    public async Task A_file_of_no_bytes_goes_up_with_an_empty_piece()
    {
        await SendAsync([]);

        Assert.That(_world.Google.Uploaded.Single().Content, Is.Empty);
        Assert.That(_world.Google.Requests.Where(request => request.IsPiece).Select(piece => piece.ContentRange), Is.EqualTo(new[] { "bytes */0" }));
    }

    /// <summary>Drive confirming less than a piece is believed: the rest of that piece is sent again from where Drive says, and the file is whole.</summary>
    [Test]
    public async Task A_short_Range_sends_the_rest_again()
    {
        var content = Bytes(600_000);
        _world.Google.CommitOnlyNext(1000);

        await SendAsync(content);

        Assert.That(_world.Google.Uploaded.Single().Content, Is.EqualTo(content));
        Assert.That(_world.Google.Pieces.Select(piece => piece.ContentRange).Take(2), Is.EqualTo(new[] { "bytes 0-262143/600000", "bytes 1000-263143/600000" }));
    }

    /// <summary>
    /// A connection that drops in a piece is followed by a wait of 2 seconds and the question how much Drive has, and the
    /// upload goes on from there, never assuming what Drive received.
    /// </summary>
    [Test]
    public async Task A_dropped_piece_asks_how_much_Drive_has_and_goes_on_from_there()
    {
        var content = Bytes(600_000);
        _world.Google.DropNextAfter(100_000);

        await SendAsync(content);

        Assert.That(_world.Google.Uploaded.Single().Content, Is.EqualTo(content));
        var pieces = _world.Google.Requests.Where(request => request.IsPiece).Select(request => request.ContentRange).ToList();
        Assert.That(pieces.Take(3), Is.EqualTo(new[] { "bytes 0-262143/600000", "bytes */600000", "bytes 100000-362143/600000" }));
        Assert.That(_world.Lines, Has.Some.StartsWith("Google Drive could not be reached (The connection was reset). ClassScribe asks again in 2 seconds"));
    }

    /// <summary>A busy Drive (503, then 429) is waited out on the clock, each time followed by the question how much it has.</summary>
    [Test]
    public async Task A_busy_Drive_is_waited_out()
    {
        var content = Bytes(300_000);
        _world.Google.FailWhen(request => request.IsPiece && !request.IsStatusQuery, HttpStatusCode.ServiceUnavailable, FakeGoogle.DriveError(503, "backendError", "Backend Error"));
        _world.Google.FailWhen(request => request.IsPiece && !request.IsStatusQuery, HttpStatusCode.TooManyRequests, FakeGoogle.DriveError(429, "rateLimitExceeded", "Rate Limit Exceeded"));

        await SendAsync(content);

        Assert.That(_world.Google.Uploaded.Single().Content, Is.EqualTo(content));
        Assert.That(_world.Google.Requests.Count(request => request.IsStatusQuery), Is.EqualTo(2));
    }

    /// <summary>A piece that fails eight times in a row fails the upload with Drive's last words; the session is kept for next time.</summary>
    [Test]
    public void Eight_failed_pieces_in_a_row_fail_the_upload()
    {
        _world.Google.FailWhen(request => request.IsPiece && !request.IsStatusQuery, HttpStatusCode.ServiceUnavailable, FakeGoogle.DriveError(503, "backendError", "Backend Error"), times: 8);

        var failure = Assert.ThrowsAsync<GoogleDriveException>(() => SendAsync(Bytes(300_000)))!;

        Assert.That(failure.Problem, Is.EqualTo(DriveProblem.Busy));
        Assert.That(_world.Google.Pieces, Has.Count.EqualTo(8));
        Assert.That(_watch.Sessions.Last(), Is.Not.Null);
        Assert.That(_world.Google.Uploaded, Is.Empty, "nothing is made until the last piece is in");
    }

    /// <summary>A token that expires in the middle of a file is renewed once, and the piece sent again with the new one.</summary>
    [Test]
    public async Task An_expired_token_is_renewed_and_the_piece_sent_again()
    {
        var content = Bytes(600_000);
        _watch.Before = () =>
        {
            if (_watch.Pieces == 2) _world.Google.ExpireAccessTokens();
        };

        await SendAsync(content);

        Assert.That(_world.Google.Uploaded.Single().Content, Is.EqualTo(content));
        Assert.That(_world.Google.AccessTokensIssued, Has.Count.EqualTo(2));
    }

    /// <summary>A session Drive has forgotten is started again from the beginning, once, and the file comes out whole.</summary>
    [Test]
    public async Task A_forgotten_session_starts_again_from_the_beginning_once()
    {
        var content = Bytes(600_000);
        _watch.Before = () =>
        {
            if (_watch.Pieces == 2) _world.Google.ExpireSessions();
        };

        await SendAsync(content);

        Assert.That(_world.Google.Uploaded.Single().Content, Is.EqualTo(content));
        Assert.That(Starts, Is.EqualTo(2));
        Assert.That(_watch.Sessions, Has.Member(null), "the forgotten session is dropped from the manifest");
    }

    /// <summary>A session forgotten a second time fails the upload, which starts again with the next one.</summary>
    [Test]
    public void A_session_forgotten_twice_fails_the_upload()
    {
        _watch.Before = () => _world.Google.ExpireSessions();

        var failure = Assert.ThrowsAsync<GoogleDriveException>(() => SendAsync(Bytes(300_000)))!;

        Assert.That((failure.Problem, failure.Message), Is.EqualTo((DriveProblem.Refused, "Google Drive lost the upload of Mantiq.wav twice; it starts again with the next upload.")));
    }

    /// <summary>
    /// A session kept from an upload that stopped, as after a restart, goes on in a new uploader: Drive is asked how much it
    /// has, and only the rest is sent, in the same session.
    /// </summary>
    [Test]
    public async Task A_kept_session_goes_on_where_it_stopped()
    {
        var content = Bytes(700_000);
        using var stop = new CancellationTokenSource();
        _watch.Before = () =>
        {
            if (_watch.Pieces == 2) stop.Cancel();
        };
        Assert.That(async () => await SendAsync(content, cancellationToken: stop.Token), Throws.InstanceOf<OperationCanceledException>());
        var kept = _watch.Sessions.Last()!;
        Assert.That(kept.Confirmed, Is.EqualTo(262144));

        _watch = new Watch();
        await SendAsync(content, kept);

        Assert.That(_world.Google.Uploaded.Single().Content, Is.EqualTo(content));
        Assert.That(Starts, Is.EqualTo(1));
        var after = _world.Google.Requests.Where(request => request.IsPiece).Select(request => request.ContentRange).Skip(1).ToList();
        Assert.That(after, Is.EqualTo(new[] { "bytes */700000", "bytes 262144-524287/700000", "bytes 524288-699999/700000" }));
    }

    /// <summary>
    /// A kept session is not used when it is older than six days, when the file has changed since, or when it was for another
    /// file on Drive: a new session starts from the beginning.
    /// </summary>
    [TestCase("old")]
    [TestCase("changed")]
    [TestCase("other target")]
    public async Task A_kept_session_that_does_not_fit_is_started_again(string why)
    {
        var content = Bytes(300_000);
        var md5 = Convert.ToHexStringLower(MD5.HashData(content));
        var address = await _world.Drive().StartUploadAsync(new DriveFileMetadata("Mantiq.wav", "audio/wav", [_folder.Id], null), "audio/wav", content.Length, CancellationToken.None);
        var kept = new UploadSession(address, _world.Time.GetUtcNow(), content.Length, md5, 0, null);
        kept = why switch
        {
            "old" => kept with { StartedUtc = _world.Time.GetUtcNow().AddDays(-6).AddMinutes(-1) },
            "changed" => kept with { Md5 = "00000000000000000000000000000000" },
            _ => kept with { RevisionOf = "file-other" }
        };

        await SendAsync(content, kept);

        Assert.That(Starts, Is.EqualTo(2));
        Assert.That(_world.Google.Requests.Count(request => request.IsStatusQuery), Is.Zero);
        Assert.That(_watch.Sessions[0], Is.Null, "the session that does not fit is dropped first");
    }

    /// <summary>An upload address Drive gives on another host is refused, and nothing is sent there.</summary>
    [Test]
    public void An_address_on_another_host_is_refused()
    {
        _world.Google.SessionHost = "https://uploads.example.com";

        var failure = Assert.ThrowsAsync<GoogleDriveException>(() => SendAsync(Bytes(1000)))!;

        Assert.That(failure.Message, Is.EqualTo("Google Drive answered with an upload address for Mantiq.wav that is not its own, so nothing was sent there."));
        Assert.That(_world.Google.Requests.Where(request => request.IsPiece), Is.Empty);
    }

    /// <summary>
    /// A kept session whose address is not Drive's own, as in a manifest someone changed, is not used, which is said, and a
    /// new session on Drive's own address takes its place: the file is never sent anywhere else.
    /// </summary>
    [Test]
    public async Task A_kept_address_that_is_not_Drives_own_is_not_used()
    {
        var content = Bytes(1000);
        var kept = new UploadSession("https://uploads.example.com/upload/drive/v3/files?uploadType=resumable&upload_id=abc", _world.Time.GetUtcNow(), content.Length,
            Convert.ToHexStringLower(MD5.HashData(content)), 0, null);

        await SendAsync(content, kept);

        Assert.That(_world.Google.Requests.Where(request => request.Host == "uploads.example.com"), Is.Empty);
        Assert.That(_world.Google.Requests.Where(request => request.IsPiece).Select(request => request.Host).Distinct(), Is.EqualTo(new[] { "www.googleapis.com" }));
        Assert.That(_world.Lines, Has.Member("The upload of Mantiq.wav begun earlier names an address that is not Google Drive's own, so it is not used; the upload starts again"));
        Assert.That(_world.Google.Uploaded.Single().Content, Is.EqualTo(content));
    }

    /// <summary>A new revision of a file goes to that file: it keeps its id, name and folder, and gets the new content.</summary>
    [Test]
    public async Task A_new_revision_goes_to_the_same_file()
    {
        var file = _world.Google.AddFile("Mantiq.log", _folder.Id, "old"u8.ToArray());
        var content = "old and more"u8.ToArray();

        var made = await SendAsync(content, target: new UploadTarget("Mantiq.log", null, file.Id));

        Assert.That(made.Id, Is.EqualTo(file.Id));
        Assert.That((file.Content, file.Revisions, _world.Google.Uploaded.Count), Is.EqualTo((content, 1, 1)));
        Assert.That(_world.Google.Requests.Single(request => request.IsPiece).Path, Is.EqualTo($"/upload/drive/v3/files/{file.Id}"));
    }

    /// <summary>The session's address, which lets anyone send to it, is never in a log line.</summary>
    [Test]
    public async Task The_sessions_address_is_never_logged()
    {
        _world.Google.DropNextAfter(10);
        await SendAsync(Bytes(300_000));

        var sessions = _world.Google.Sessions.Keys.ToList();
        Assert.That(sessions, Is.Not.Empty);
        Assert.That(_world.Lines.Where(line => sessions.Any(id => line.Contains(id, StringComparison.Ordinal))), Is.Empty);
        Assert.That(_watch.Sessions.OfType<UploadSession>().Select(session => session.ToString()).Where(text => sessions.Any(id => text.Contains(id, StringComparison.Ordinal))), Is.Empty);
    }

    /// <summary>The time a piece may take: two minutes, or its bytes at 0.25 Mbit/s when that is longer.</summary>
    [TestCase(256 * 1024, 120.0)]
    [TestCase(8 * 1024 * 1024, 268.435456)]
    public void A_piece_may_take_two_minutes_or_its_bytes_at_a_quarter_megabit(long bytes, double seconds)
    {
        Assert.That(ResumableUpload.PieceLimit(bytes).TotalSeconds, Is.EqualTo(seconds).Within(0.001));
    }

    /// <summary>Only Drive's own upload address passes: its host and port, the path of the upload endpoint, and an upload id.</summary>
    [TestCase("https://www.googleapis.com/upload/drive/v3/files?uploadType=resumable&upload_id=AB-c_d", null, true)]
    [TestCase("https://WWW.GOOGLEAPIS.COM/upload/drive/v3/files?uploadType=resumable&upload_id=AB", null, true)]
    [TestCase("https://www.googleapis.com/upload/drive/v3/files/file-1?uploadType=resumable&upload_id=AB", "file-1", true)]
    [TestCase("https://www.googleapis.com/upload/drive/v3/files/file-2?uploadType=resumable&upload_id=AB", "file-1", false)]
    [TestCase("https://www.googleapis.com/upload/drive/v3/files/file-1?uploadType=resumable&upload_id=AB", null, false)]
    [TestCase("http://www.googleapis.com/upload/drive/v3/files?uploadType=resumable&upload_id=AB", null, false)]
    [TestCase("https://www.googleapis.com:8443/upload/drive/v3/files?uploadType=resumable&upload_id=AB", null, false)]
    [TestCase("https://evil.example/upload/drive/v3/files?uploadType=resumable&upload_id=AB", null, false)]
    [TestCase("https://www.googleapis.com/drive/v3/files?uploadType=resumable&upload_id=AB", null, false)]
    [TestCase("https://www.googleapis.com/upload/drive/v3/files?uploadType=resumable", null, false)]
    [TestCase("https://www.googleapis.com/upload/drive/v3/files?upload_id=A%26B", null, false)]
    [TestCase("not an address", null, false)]
    public void Only_Drives_own_upload_address_passes(string address, string? revisionOf, bool passes)
    {
        Assert.That(UploadAddress.TryParse(address, "https://www.googleapis.com/", revisionOf) is not null, Is.EqualTo(passes));
    }

    /// <summary>An upload address as text never shows its upload id.</summary>
    [Test]
    public void An_upload_address_as_text_hides_its_upload_id()
    {
        Assert.That(new UploadAddress("secret-upload-id", "file-1").ToString(), Is.EqualTo("UploadAddress { FileId = file-1 }"));
        Assert.That(new UploadSession("https://www.googleapis.com/upload/drive/v3/files?upload_id=secret-upload-id", TestRuns.Start, 1, "md5", 0, null).ToString(),
            Does.Not.Contain("secret-upload-id"));
    }

    /// <summary>What an upload tells, kept, and what it runs before each piece.</summary>
    private sealed class Watch : IUploadWatch
    {
        /// <summary>The sessions told, in order; null for one dropped.</summary>
        public List<UploadSession?> Sessions { get; } = [];

        /// <summary>How many pieces were about to go.</summary>
        public int Pieces { get; private set; }

        /// <summary>What runs before each piece, after it is counted.</summary>
        public Action? Before { get; set; }

        /// <inheritdoc />
        public Task BeforePieceAsync(CancellationToken cancellationToken)
        {
            Pieces++;
            Before?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public void SessionChanged(UploadSession? session) => Sessions.Add(session);
    }
}
