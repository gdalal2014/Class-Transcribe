using System.Net;
using ClassScribe.Drive;
using Refit;

namespace ClassScribe.Tests;

/// <summary>
/// The calls to Drive, against the in-memory Google: each carries the access token as a bearer; a refused token is renewed
/// once and the call made again; Google's refusals become messages that say what to do, the Drive API left disabled among
/// them; Google out of reach says so. No request leaves the process.
/// </summary>
[TestFixture]
public class DriveClientTests
{
    private GoogleWorld _world = null!;

    /// <summary>A world whose store holds the tests' client, signed in.</summary>
    [SetUp]
    public void SetUp()
    {
        _world = new GoogleWorld();
        _world.Store.Stored = FakeCredentialStore.With(_world.Google.IssueRefreshToken()).Stored;
    }

    /// <summary>Deletes the world's folders.</summary>
    [TearDown]
    public void TearDown() => _world.Dispose();

    /// <summary>The message of the <see cref="GoogleDriveException"/> <paramref name="action"/> fails with, which must be of <paramref name="problem"/>.</summary>
    private static string FailsWith(DriveProblem problem, Func<Task> action)
    {
        var failure = Assert.ThrowsAsync<GoogleDriveException>(action)!;
        Assert.That(failure.Problem, Is.EqualTo(problem));
        return failure.Message;
    }

    /// <summary>About gives the account and its storage, asked with the access token as a bearer and the fields ClassScribe reads.</summary>
    [Test]
    public async Task About_gives_the_account_and_its_storage()
    {
        var about = await _world.Drive().GetAboutAsync(CancellationToken.None);

        Assert.That(about, Is.EqualTo(new DriveAbout(new DriveUser("A Student", "student@example.com"), new DriveStorageQuota(_world.Google.StorageLimit, _world.Google.StorageUsage))));
        var request = _world.Google.Requests.Single(request => request.Path == "/drive/v3/about");
        Assert.That(request.Host, Is.EqualTo("www.googleapis.com"));
        Assert.That(request.Bearer, Is.EqualTo(_world.Google.AccessTokensIssued.Single()));
        Assert.That(request.Query["fields"], Is.EqualTo("user(displayName,emailAddress),storageQuota(limit,usage)"));
    }

    /// <summary>A token Drive refuses (HTTP 401) is renewed once, and the call made again with the new one.</summary>
    [Test]
    public async Task A_refused_token_is_renewed_once_and_the_call_made_again()
    {
        var drive = _world.Drive();
        await drive.GetAboutAsync(CancellationToken.None);
        _world.Google.ExpireAccessTokens();

        var about = await drive.GetAboutAsync(CancellationToken.None);

        Assert.That(about.User?.EmailAddress, Is.EqualTo("student@example.com"));
        var asked = _world.Google.Requests.Where(request => request.Path == "/drive/v3/about").Select(request => request.Bearer).ToList();
        Assert.That(asked, Is.EqualTo(new[] { _world.Google.AccessTokensIssued[0], _world.Google.AccessTokensIssued[0], _world.Google.AccessTokensIssued[1] }));
    }

    /// <summary>A token refused twice in a row is not renewed again: the call fails, saying the sign-in was not accepted.</summary>
    [Test]
    public void A_token_refused_twice_is_not_renewed_again()
    {
        _world.Google.Fail("/drive/v3/about", HttpStatusCode.Unauthorized, FakeGoogle.DriveError(401, "authError", "Request had invalid authentication credentials.", "UNAUTHENTICATED"), times: 2);

        var message = FailsWith(DriveProblem.Refused, () => _world.Drive().GetAboutAsync(CancellationToken.None));

        Assert.That(message, Does.StartWith("Google Drive did not accept ClassScribe's sign-in (HTTP 401"));
        Assert.That(_world.Google.Refreshes, Is.EqualTo(2));
    }

    /// <summary>The Drive API left disabled in the Google Cloud project is named, with where to enable it.</summary>
    [Test]
    public void A_Drive_API_left_disabled_is_named_with_where_to_enable_it()
    {
        _world.Google.Fail("/drive/v3/about", HttpStatusCode.Forbidden,
            FakeGoogle.DriveError(403, "accessNotConfigured", "Google Drive API has not been used in project 123456789012 before or it is disabled.", "PERMISSION_DENIED"));

        var message = FailsWith(DriveProblem.Refused, () => _world.Drive().GetAboutAsync(CancellationToken.None));

        Assert.That(message, Is.EqualTo(DriveClient.ApiDisabled));
    }

    /// <summary>Another refusal gives what was asked, Google's status, reason and words.</summary>
    [Test]
    public void Another_refusal_gives_Googles_status_reason_and_words()
    {
        _world.Google.Fail("/drive/v3/about", HttpStatusCode.Forbidden, FakeGoogle.DriveError(403, "insufficientFilePermissions", "The user does not have sufficient permissions for this file."));

        var message = FailsWith(DriveProblem.Refused, () => _world.Drive().GetAboutAsync(CancellationToken.None));

        Assert.That(message, Is.EqualTo("Google Drive refused to show the account (HTTP 403, insufficientFilePermissions: The user does not have sufficient permissions for this file.)."));
    }

    /// <summary>
    /// An answer worth asking again after a wait is told apart from a refusal, as Google's guide to its errors says: 408, 429,
    /// 500, 502, 503 and 504, and 403 with a rate limit as the reason; the words are those of any refusal.
    /// </summary>
    [TestCase(408, "", "")]
    [TestCase(429, "rateLimitExceeded", "Rate Limit Exceeded")]
    [TestCase(500, "backendError", "Backend Error")]
    [TestCase(502, "", "")]
    [TestCase(503, "backendError", "Backend Error")]
    [TestCase(504, "", "")]
    [TestCase(403, "userRateLimitExceeded", "User rate limit exceeded.")]
    [TestCase(403, "rateLimitExceeded", "Rate Limit Exceeded")]
    public void A_busy_Google_is_told_apart_from_a_refusal(int status, string reason, string words)
    {
        _world.Google.Fail("/drive/v3/about", (HttpStatusCode)status, reason.Length == 0 ? "" : FakeGoogle.DriveError(status, reason, words));

        var message = FailsWith(DriveProblem.Busy, () => _world.Drive().GetAboutAsync(CancellationToken.None));

        Assert.That(message, Does.StartWith($"Google Drive refused to show the account (HTTP {status}"));
    }

    /// <summary>A full Drive, or a limit of the day reached, says so, with what to do: nothing more goes up for now.</summary>
    [TestCase("storageQuotaExceeded", "Google Drive is full: the account's storage takes nothing more (HTTP 403, storageQuotaExceeded: The user's Drive storage quota has been exceeded.). Make room in Google Drive, or add storage to the account, then upload again.")]
    [TestCase("uploadLimitExceeded", "Google Drive takes no more uploads today (HTTP 403, uploadLimitExceeded: The user's Drive storage quota has been exceeded.). Upload again tomorrow.")]
    [TestCase("dailyLimitExceeded", "Google Drive takes no more uploads today (HTTP 403, dailyLimitExceeded: The user's Drive storage quota has been exceeded.). Upload again tomorrow.")]
    public void A_full_Drive_says_so(string reason, string expected)
    {
        _world.Google.FailWhen(request => request.IsUploadStart, HttpStatusCode.Forbidden, FakeGoogle.DriveError(403, reason, "The user's Drive storage quota has been exceeded."));

        var message = FailsWith(DriveProblem.Full, () => _world.Drive().StartUploadAsync(new DriveFileMetadata("a.md", "text/markdown", ["folder-1"], null), "text/markdown", 3, CancellationToken.None));

        Assert.That(message, Is.EqualTo(expected));
    }

    /// <summary>
    /// Starting an upload sends the file's name, type and folder as JSON, with the type and length of what will follow, and
    /// gives back the session's address from Drive's Location header; starting a revision names the file, and leaves its
    /// name and folder as they are.
    /// </summary>
    [Test]
    public async Task Starting_an_upload_gives_the_sessions_address()
    {
        var drive = _world.Drive();

        var address = await drive.StartUploadAsync(new DriveFileMetadata("Kalam.wav", "audio/wav", ["folder-1"], null), "audio/wav", 1234, CancellationToken.None);

        var start = _world.Google.Requests.Single(request => request.IsUploadStart);
        Assert.That(start.Query["uploadType"], Is.EqualTo("resumable"));
        Assert.That(start.UploadLength, Is.EqualTo("1234"));
        Assert.That(JsonText.Object(start.Body).ToJsonString(), Is.EqualTo("""{"name":"Kalam.wav","mimeType":"audio/wav","parents":["folder-1"]}"""));
        var session = _world.Google.Sessions.Values.Single();
        Assert.That(address, Is.EqualTo($"https://www.googleapis.com/upload/drive/v3/files?uploadType=resumable&upload_id={session.Id}"));

        var file = _world.Google.AddFile("Kalam.log", "folder-1", [1, 2, 3]);
        var revision = await drive.StartRevisionAsync(file.Id, "text/plain", 10, CancellationToken.None);
        var patch = _world.Google.Requests.Single(request => request.IsRevisionStart);
        Assert.That((patch.Path, patch.Body), Is.EqualTo(($"/upload/drive/v3/files/{file.Id}", "{}")));
        Assert.That(revision, Does.StartWith($"https://www.googleapis.com/upload/drive/v3/files/{file.Id}?uploadType=resumable&upload_id="));
    }

    /// <summary>An answer without a Location header is a refusal that says Drive did not say where to send the file.</summary>
    [Test]
    public void An_answer_without_a_session_address_is_a_refusal()
    {
        _world.Google.FailWhen(request => request.IsUploadStart, HttpStatusCode.OK, "");

        var message = FailsWith(DriveProblem.Refused,
            () => _world.Drive().StartUploadAsync(new DriveFileMetadata("a.md", "text/markdown", ["folder-1"], null), "text/markdown", 3, CancellationToken.None));

        Assert.That(message, Is.EqualTo("Google Drive did not say where to send the file when asked to start an upload."));
    }

    /// <summary>
    /// A piece answered 308 is "more", from the end of the Range Drive confirms; the last piece is the file, complete; a
    /// session Drive has forgotten (404 or 410) is "gone".
    /// </summary>
    [Test]
    public async Task A_piece_is_more_complete_or_gone()
    {
        const int size = FakeGoogle.PieceMultiple + 2;
        var drive = _world.Drive();
        var address = UploadAddress.TryParse(await drive.StartUploadAsync(new DriveFileMetadata("a.md", "text/markdown", ["folder-1"], null), "text/markdown", size, CancellationToken.None),
            "https://www.googleapis.com/", DriveApi.WrittenFor, revisionOf: null)!;
        var bytes = Enumerable.Range(0, size).Select(i => (byte)(i % 251)).ToArray();
        using var content = new MemoryStream(bytes);

        var first = await drive.SendPieceAsync(address, () => new PieceContent(content, 0, FakeGoogle.PieceMultiple, size), DriveClient.CallLimit, CancellationToken.None);
        var last = await drive.SendPieceAsync(address, () => new PieceContent(content, FakeGoogle.PieceMultiple, 2, size), DriveClient.CallLimit, CancellationToken.None);

        Assert.That((first.Outcome, first.Confirmed), Is.EqualTo((PieceOutcome.More, (long)FakeGoogle.PieceMultiple)));
        Assert.That((last.Outcome, last.File?.Id), Is.EqualTo((PieceOutcome.Complete, _world.Google.Uploaded.Single().Id)));
        Assert.That(_world.Google.Uploaded.Single().Content, Is.EqualTo(bytes));
        Assert.That(_world.Google.Pieces.Select(piece => piece.ContentRange), Is.EqualTo(new[] { "bytes 0-262143/262146", "bytes 262144-262145/262146" }));

        _world.Google.ExpireSessions();
        var gone = await drive.SendPieceAsync(address, () => PieceContent.StatusQuery(size), DriveClient.CallLimit, CancellationToken.None);
        _world.Google.FailWhen(request => request.IsPiece, HttpStatusCode.Gone, "");
        var goneToo = await drive.SendPieceAsync(address, () => PieceContent.StatusQuery(size), DriveClient.CallLimit, CancellationToken.None);
        Assert.That((gone.Outcome, goneToo.Outcome), Is.EqualTo((PieceOutcome.Gone, PieceOutcome.Gone)));
    }

    /// <summary>
    /// Every call goes to the version of the Drive API the settings give, in the middle of its path (architecture 7.3): about,
    /// a search, a file, a new folder, the start of an upload and of a revision, and a piece of each, under the base address as
    /// it is; v3 by default, v4 when Drive:ApiVersion says so. Here over a stub transport, which answers whatever the path.
    /// </summary>
    [TestCase("v3")]
    [TestCase("v4")]
    public async Task Every_call_goes_to_the_version_the_settings_give(string version)
    {
        var stub = new StubHttpHandler();
        stub.Enqueue(HttpStatusCode.OK, "{}");
        stub.Enqueue(HttpStatusCode.OK, """{"files":[]}""");
        stub.Enqueue(HttpStatusCode.OK, """{"id":"file-1"}""");
        stub.Enqueue(HttpStatusCode.OK, """{"id":"folder-1","name":"Kalam"}""");
        foreach (var session in new[] { $"upload/drive/{version}/files", $"upload/drive/{version}/files/file-1" })
            stub.Enqueue(() => new HttpResponseMessage(HttpStatusCode.OK) { Headers = { Location = new Uri($"https://www.googleapis.com/{session}?uploadType=resumable&upload_id=AB") } });
        stub.Enqueue(HttpStatusCode.OK, """{"id":"file-2"}""");
        stub.Enqueue(HttpStatusCode.OK, """{"id":"file-1"}""");
        var api = RestService.ForGenerated<IDriveApi>(new HttpClient(stub) { BaseAddress = new Uri("https://www.googleapis.com/") }, DriveJson.Default, DriveApiSettings.Create());
        var tokens = _world.Tokens();
        tokens.Seed("access-token", TimeSpan.FromHours(1));
        var drive = new DriveClient(api, tokens, TestOptions.Create(new DriveOptions { ApiVersion = version }), _world.Time, _world.Log<DriveClient>());
        using var content = new MemoryStream([1, 2]);

        await drive.GetAboutAsync(CancellationToken.None);
        await drive.ListAsync("trashed = false", CancellationToken.None);
        await drive.GetFileAsync("file-1", CancellationToken.None);
        await drive.CreateFolderAsync(new DriveFileMetadata("Kalam", "application/vnd.google-apps.folder", null, null), CancellationToken.None);
        var upload = UploadAddress.TryParse(await drive.StartUploadAsync(new DriveFileMetadata("a.md", "text/markdown", ["folder-1"], null), "text/markdown", 2, CancellationToken.None),
            "https://www.googleapis.com/", version, revisionOf: null)!;
        var revision = UploadAddress.TryParse(await drive.StartRevisionAsync("file-1", "text/markdown", 2, CancellationToken.None), "https://www.googleapis.com/", version, "file-1")!;
        await drive.SendPieceAsync(upload, () => new PieceContent(content, 0, 2, 2), DriveClient.CallLimit, CancellationToken.None);
        await drive.SendPieceAsync(revision, () => new PieceContent(content, 0, 2, 2), DriveClient.CallLimit, CancellationToken.None);

        Assert.That(stub.Requests.Select(request => $"{request.Method} {request.Uri!.AbsolutePath}"), Is.EqualTo(new[]
        {
            $"GET /drive/{version}/about", $"GET /drive/{version}/files", $"GET /drive/{version}/files/file-1", $"POST /drive/{version}/files",
            $"POST /upload/drive/{version}/files", $"PATCH /upload/drive/{version}/files/file-1", $"PUT /upload/drive/{version}/files", $"PUT /upload/drive/{version}/files/file-1"
        }));
    }

    /// <summary>A Range Drive confirms is read as bytes=0-N, N + 1 bytes; none, or one in another shape, confirms nothing.</summary>
    [TestCase("bytes=0-262143", 262144L)]
    [TestCase("bytes=0-0", 1L)]
    [TestCase(null, 0L)]
    [TestCase("bytes=5-10", 0L)]
    [TestCase("bytes=0-", 0L)]
    public void A_Range_confirms_its_end_plus_one(string? range, long confirmed)
    {
        using var answer = new HttpResponseMessage((HttpStatusCode)308);
        if (range is not null) answer.Headers.TryAddWithoutValidation("Range", range);

        Assert.That(DriveClient.ConfirmedBy(answer), Is.EqualTo(confirmed));
    }

    /// <summary>A piece Drive does not answer ends at its own limit, longer than a minute for a large piece, as Google out of reach.</summary>
    [Test]
    public async Task A_piece_ends_at_its_own_limit()
    {
        var drive = _world.Drive();
        var address = UploadAddress.TryParse(await drive.StartUploadAsync(new DriveFileMetadata("a.wav", "audio/wav", ["folder-1"], null), "audio/wav", 8 * 1024 * 1024, CancellationToken.None),
            "https://www.googleapis.com/", DriveApi.WrittenFor, revisionOf: null)!;
        _world.Google.Hold("/upload/drive/v3/files", Task.Delay(Timeout.Infinite));
        using var content = new MemoryStream(new byte[8 * 1024 * 1024]);
        var limit = ResumableUpload.PieceLimit(content.Length);

        var piece = drive.SendPieceAsync(address, () => new PieceContent(content, 0, (int)content.Length, content.Length), limit, CancellationToken.None);
        await Wait.UntilAsync(() => _world.Google.Pieces.Count == 1 && _world.Time.UntilNextTimer == limit);
        _world.Time.Advance(DriveClient.CallLimit);
        Assert.That(_world.Time.UntilNextTimer, Is.EqualTo(limit - DriveClient.CallLimit), "a minute is not the piece's limit, which is still to come");
        Assert.That(piece.IsCompleted, Is.False, "a minute is not the piece's limit");
        _world.Time.Advance(limit - DriveClient.CallLimit);

        Assert.That(FailsWith(DriveProblem.Unreachable, () => piece), Is.EqualTo("Google Drive could not be reached (no answer within 269 seconds)."));
    }

    /// <summary>Google out of reach says so, with the reason; an answer that never comes ends at the limit of a minute.</summary>
    [Test]
    public async Task Google_out_of_reach_says_so()
    {
        var drive = _world.Drive();
        await drive.GetAboutAsync(CancellationToken.None);
        _world.Google.Fail("/drive/v3/about", new HttpRequestException("The connection was reset."));
        Assert.That(FailsWith(DriveProblem.Unreachable, () => drive.GetAboutAsync(CancellationToken.None)),
            Is.EqualTo("Google Drive could not be reached (The connection was reset)."));

        _world.Google.Hold("/drive/v3/about", Task.Delay(Timeout.Infinite));
        var silent = drive.GetAboutAsync(CancellationToken.None);
        await Wait.UntilAsync(() => _world.Google.Requests.Count(request => request.Path == "/drive/v3/about") == 3);
        _world.Time.Advance(DriveClient.CallLimit);
        Assert.That(FailsWith(DriveProblem.Unreachable, () => silent), Is.EqualTo("Google Drive could not be reached (no answer within 60 seconds)."));
    }

    /// <summary>The caller's own cancel, such as Ctrl+C, ends a call as a cancel, not as a failure.</summary>
    [Test]
    public async Task The_callers_cancel_ends_a_call_as_a_cancel()
    {
        _world.Google.Hold("/drive/v3/about", Task.Delay(Timeout.Infinite));
        using var cancel = new CancellationTokenSource();

        var call = _world.Drive().GetAboutAsync(cancel.Token);
        await Wait.UntilAsync(() => _world.Google.Requests.Any(request => request.Path == "/drive/v3/about"));
        await cancel.CancelAsync();

        Assert.That(async () => await call, Throws.InstanceOf<OperationCanceledException>());
    }

    /// <summary>A file Drive does not have is null; one it has comes back with what Drive says of it.</summary>
    [Test]
    public async Task A_file_is_found_by_its_id_or_is_null()
    {
        var folder = _world.Google.AddFolder("ClassScribe", root: true);
        var drive = _world.Drive();

        Assert.That(await drive.GetFileAsync("no-such-id", CancellationToken.None), Is.Null);
        var found = await drive.GetFileAsync(folder.Id, CancellationToken.None);
        Assert.That((found?.Id, found?.Name, found?.MimeType, found?.Trashed), Is.EqualTo((folder.Id, "ClassScribe", FakeGoogle.FolderType, false)));
    }
}
