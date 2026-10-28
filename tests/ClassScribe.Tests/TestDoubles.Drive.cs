using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using System.Web;
using ClassScribe.Commands;
using ClassScribe.Drive;
using ClassScribe.Recorder;
using ClassScribe.Settings;
using ClassScribe.Storage;
using Microsoft.Extensions.Logging;
using Refit;

namespace ClassScribe.Tests;

/// <summary>What <see cref="FakeGoogle"/> saw of one request: the method, the path, the query, the bearer token and the body.</summary>
/// <param name="Method">The HTTP method.</param>
/// <param name="Host">The host the request went to.</param>
/// <param name="Path">The path, such as /drive/v3/about.</param>
/// <param name="Query">The query, decoded.</param>
/// <param name="Bearer">The bearer token in the Authorization header, or null when there was none.</param>
/// <param name="Body">The body as text; empty when there was none.</param>
/// <param name="ContentType">The body's content type, or null.</param>
public sealed record GoogleRequest(HttpMethod Method, string Host, string Path, NameValueCollection Query, string? Bearer, string Body, string? ContentType)
{
    /// <summary>The body read as a URL-encoded form, as the token and revoke endpoints take it.</summary>
    public NameValueCollection Form => HttpUtility.ParseQueryString(Body);

    /// <summary>The body's bytes as they were sent; empty when there was none.</summary>
    public byte[] Bytes { get; init; } = [];

    /// <summary>The body's Content-Range, such as bytes 0-262143/1000000 or bytes */1000000; null when it had none.</summary>
    public string? ContentRange { get; init; }

    /// <summary>The X-Upload-Content-Length header of a request that starts an upload; null otherwise.</summary>
    public string? UploadLength { get; init; }

    /// <summary>True for a request that sends a piece of an upload, or asks how much of one Drive has.</summary>
    public bool IsPiece => Method == HttpMethod.Put && Path.StartsWith("/upload/drive/v3/files", StringComparison.Ordinal);

    /// <summary>True for a request that asks how much of an upload Drive has: an empty piece with bytes */size.</summary>
    public bool IsStatusQuery => IsPiece && ContentRange?.StartsWith("bytes */", StringComparison.Ordinal) == true;

    /// <summary>True for a request that starts the upload of a new file.</summary>
    public bool IsUploadStart => Method == HttpMethod.Post && Path == "/upload/drive/v3/files";

    /// <summary>True for a request that starts the upload of a new revision of a file.</summary>
    public bool IsRevisionStart => Method == HttpMethod.Patch && Path.StartsWith("/upload/drive/v3/files/", StringComparison.Ordinal);
}

/// <summary>One file or folder in <see cref="FakeGoogle"/>'s Drive.</summary>
public sealed class FakeDriveFile
{
    /// <summary>Drive's id for it.</summary>
    public required string Id { get; init; }

    /// <summary>Its name.</summary>
    public required string Name { get; set; }

    /// <summary>Its MIME type; a folder's is application/vnd.google-apps.folder.</summary>
    public required string MimeType { get; init; }

    /// <summary>The folders it is in; none for My Drive itself.</summary>
    public List<string> Parents { get; init; } = [];

    /// <summary>The properties private to the app that made it.</summary>
    public Dictionary<string, string> AppProperties { get; init; } = [];

    /// <summary>True once it is in the trash.</summary>
    public bool Trashed { get; set; }

    /// <summary>A file's content, its head revision; empty for a folder.</summary>
    public byte[] Content { get; set; } = [];

    /// <summary>How many revisions of its content were uploaded: 1 for a file uploaded once, 0 for a folder or one added by the test.</summary>
    public int Revisions { get; set; }

    /// <summary>The MD5 Drive gives for it instead of its content's, as a fault; null for the right one.</summary>
    public string? Md5Override { get; set; }

    /// <summary>True for a folder.</summary>
    public bool IsFolder => MimeType == FakeGoogle.FolderType;

    /// <summary>Its size, as Drive gives it for a file with content; null for a folder.</summary>
    public long? Size => IsFolder ? null : Content.Length;

    /// <summary>Its MD5, as Drive gives it for a file with content: 32 lowercase hexadecimal digits; null for a folder.</summary>
    public string? Md5 => IsFolder ? null : Md5Override ?? Convert.ToHexStringLower(MD5.HashData(Content));
}

/// <summary>A resumable upload session in <see cref="FakeGoogle"/>: what it is for, what it declared, and the bytes received so far.</summary>
public sealed class FakeUploadSession
{
    /// <summary>The upload id, the secret part of its address.</summary>
    public required string Id { get; init; }

    /// <summary>The new file's name; null for a new revision.</summary>
    public string? Name { get; init; }

    /// <summary>The new file's type.</summary>
    public string? MimeType { get; init; }

    /// <summary>The folders the new file goes into.</summary>
    public List<string> Parents { get; init; } = [];

    /// <summary>The file a new revision is of; null for a new file.</summary>
    public string? RevisionOf { get; init; }

    /// <summary>The length the upload declared it would send.</summary>
    public long Declared { get; init; }

    /// <summary>The bytes received so far, in order.</summary>
    public MemoryStream Received { get; } = new();

    /// <summary>The file it made once complete; null before.</summary>
    public FakeDriveFile? Completed { get; set; }

    /// <summary>True once Drive has forgotten it, as after a week: every request to it answers 404.</summary>
    public bool Expired { get; set; }
}

/// <summary>
/// A small Google in memory, holding state rather than a queue of answers: the token endpoint (the codes it issued, the S256
/// check of the verifier, refresh tokens, revoke, invalid_grant, invalid_client), Drive's about, and its folders with their
/// app properties, found by the query ClassScribe sends for its own folder. The browser's side is <see cref="Authorize"/>,
/// which <see cref="FakeBrowser"/> calls as Google's consent page would. Every request is kept, and every secret it handed
/// out or was handed (<see cref="Secrets"/>), so a test can look for them in the logs. Faults on demand: a one-off answer
/// for a path (<see cref="Fail(string, HttpStatusCode, string, int)"/>), Google out of reach (<see cref="Unreachable"/>), a
/// request held until the test lets it go (<see cref="Hold"/>). No request ever leaves the process.
/// </summary>
public sealed partial class FakeGoogle : HttpMessageHandler
{
    /// <summary>The client id of the Desktop app client the tests sign in with.</summary>
    public const string ClientId = "123456789012-abcdefghijklmnopqrstuv0a1b2c3.apps.googleusercontent.com";

    /// <summary>That client's secret.</summary>
    public const string ClientSecret = "GOCSPX-TestClientSecret-0123456789ab";

    /// <summary>The scope that lets ClassScribe see and change only its own files.</summary>
    public const string DriveFileScope = "https://www.googleapis.com/auth/drive.file";

    /// <summary>The MIME type of a Drive folder.</summary>
    public const string FolderType = "application/vnd.google-apps.folder";

    /// <summary>What every piece of a resumable upload but the last must be a multiple of, as Drive asks: 256 KiB.</summary>
    public const int PieceMultiple = 256 * 1024;

    private readonly Lock _gate = new();
    private readonly List<GoogleRequest> _requests = [];
    private readonly HashSet<string> _secrets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IssuedCode> _codes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _refreshTokens = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _accessTokens = new(StringComparer.Ordinal);
    private readonly HashSet<string> _revoked = new(StringComparer.Ordinal);
    private readonly List<(string Path, Func<HttpResponseMessage> Answer)> _faults = [];
    private readonly List<(Func<GoogleRequest, bool> When, Func<HttpResponseMessage> Answer)> _faultsWhen = [];
    private readonly List<(string Path, Task Release)> _holds = [];
    private readonly Dictionary<string, FakeUploadSession> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _wrongMd5 = new(StringComparer.Ordinal);
    private int _counter;

    /// <summary>When set, the next piece commits only this many of its bytes and answers 308, as Drive may.</summary>
    private int? _commitOnly;

    /// <summary>When set, the next piece commits this many of its bytes, then the connection drops before any answer.</summary>
    private int? _dropAfter;

    /// <summary>The account's email address, which about gives.</summary>
    public string Email { get; set; } = "student@example.com";

    /// <summary>The account's name, which about gives.</summary>
    public string DisplayName { get; set; } = "A Student";

    /// <summary>The account's storage limit in bytes, 30 TB unless the test says otherwise; null for an account without one.</summary>
    public long? StorageLimit { get; set; } = 30L * 1024 * 1024 * 1024 * 1024;

    /// <summary>The storage the account uses, in bytes: 1.2 TB unless the test says otherwise.</summary>
    public long StorageUsage { get; set; } = 1_319_413_953_331;

    /// <summary>The scopes the consent page grants, as the token answer lists them: drive.file unless a box is left unticked.</summary>
    public string GrantedScope { get; set; } = DriveFileScope;

    /// <summary>Whether the code exchange hands out a refresh token, as it always does for a Desktop app client.</summary>
    public bool IssuesRefreshToken { get; set; } = true;

    /// <summary>When true, every request fails as if Google could not be reached.</summary>
    public bool Unreachable { get; set; }

    /// <summary>The folders and files in this Drive.</summary>
    public List<FakeDriveFile> Files { get; } = [];

    /// <summary>
    /// The scheme and host the address of a new upload session names, such as https://www.googleapis.com; null for the
    /// request's own. A test sets another host to play an answer that would send the file elsewhere.
    /// </summary>
    public string? SessionHost { get; set; }

    /// <summary>The upload sessions started so far, by upload id.</summary>
    public IReadOnlyDictionary<string, FakeUploadSession> Sessions
    {
        get { lock (_gate) return new Dictionary<string, FakeUploadSession>(_sessions); }
    }

    /// <summary>The requests that sent a piece of an upload, status queries left out, in order.</summary>
    public IReadOnlyList<GoogleRequest> Pieces => [.. Requests.Where(request => request.IsPiece && !request.IsStatusQuery)];

    /// <summary>The files, not folders, in this Drive, in the order they were made.</summary>
    public IReadOnlyList<FakeDriveFile> Uploaded
    {
        get { lock (_gate) return [.. Files.Where(file => !file.IsFolder)]; }
    }

    /// <summary>
    /// The requests that could change or remove something already on Drive other than by a new revision of a file's content:
    /// a DELETE, a trash, a metadata update or a share. ClassScribe never sends one.
    /// </summary>
    public IReadOnlyList<GoogleRequest> Destructive =>
        [.. Requests.Where(request => request.Method == HttpMethod.Delete
            || (request.Method == HttpMethod.Patch && !request.Path.StartsWith("/upload/", StringComparison.Ordinal))
            || request.Path.Contains("/permissions", StringComparison.Ordinal)
            || request.Path.EndsWith("/trash", StringComparison.Ordinal))];

    /// <summary>The parameters of the last authorization address the browser was sent to, decoded; null before the first.</summary>
    public NameValueCollection? LastAuthorization { get; private set; }

    /// <summary>Every request so far, in order.</summary>
    public IReadOnlyList<GoogleRequest> Requests
    {
        get { lock (_gate) return [.. _requests]; }
    }

    /// <summary>
    /// Every secret of the sign-ins so far: the client secret, the states and challenges' verifiers, the codes, the refresh
    /// tokens and the access tokens, as handed out or received.
    /// </summary>
    public IReadOnlyList<string> Secrets
    {
        get { lock (_gate) return [ClientSecret, .. _secrets]; }
    }

    /// <summary>The refresh tokens handed out so far, in order.</summary>
    public List<string> RefreshTokensIssued { get; } = [];

    /// <summary>The access tokens handed out so far, in order.</summary>
    public List<string> AccessTokensIssued { get; } = [];

    /// <summary>How many requests reached the token endpoint with grant_type=refresh_token.</summary>
    public int Refreshes => Requests.Count(request => request.Path == "/token" && request.Form["grant_type"] == "refresh_token");

    /// <summary>The requests to the token endpoint, in order.</summary>
    public IReadOnlyList<GoogleRequest> TokenRequests => [.. Requests.Where(request => request.Path == "/token")];

    /// <summary>The refresh tokens and access tokens revoked so far.</summary>
    public IReadOnlyCollection<string> Revoked
    {
        get { lock (_gate) return [.. _revoked]; }
    }

    /// <summary>
    /// Plays Google's consent page for the authorization address <paramref name="address"/>: checks that it is Google's,
    /// keeps its parameters, and hands out a code for that client, redirect address and challenge.
    /// </summary>
    public string Authorize(Uri address)
    {
        Assert.That(address.Host, Is.EqualTo("accounts.google.com"), "the sign-in goes to Google's accounts page");
        Assert.That(address.AbsolutePath, Is.EqualTo("/o/oauth2/v2/auth"));
        var parameters = HttpUtility.ParseQueryString(address.Query);
        lock (_gate)
        {
            LastAuthorization = parameters;
            var code = $"4/0AQSTgQ-code-{++_counter:D4}-{Guid.NewGuid():N}";
            _codes[code] = new IssuedCode(parameters["client_id"] ?? "", parameters["redirect_uri"] ?? "", parameters["code_challenge"] ?? "", parameters["code_challenge_method"] ?? "");
            _secrets.Add(code);
            if (parameters["state"] is { } state) _secrets.Add(state);
            return code;
        }
    }

    /// <summary>Signs in as a person would have, without the browser: a refresh token for the tests' client, as one stored earlier would be.</summary>
    public string IssueRefreshToken()
    {
        lock (_gate) return NewRefreshToken();
    }

    /// <summary>Makes every access token handed out so far one Drive no longer takes, as an hour after it was made.</summary>
    public void ExpireAccessTokens()
    {
        lock (_gate)
        {
            foreach (var token in _accessTokens.Keys) _revoked.Add(token);
        }
    }

    /// <summary>Revokes <paramref name="refreshToken"/>, as the person might at their Google Account, with every access token made from it.</summary>
    public void RevokeByHand(string refreshToken)
    {
        lock (_gate) Revoke(refreshToken);
    }

    /// <summary>
    /// Adds a folder to this Drive, in <paramref name="parentId"/> or in My Drive; with <paramref name="root"/>, ClassScribe's own
    /// mark for its folder in My Drive.
    /// </summary>
    public FakeDriveFile AddFolder(string name, bool root = false, bool trashed = false, string? parentId = null)
    {
        var folder = new FakeDriveFile
        {
            Id = $"folder-{Interlocked.Increment(ref _counter):D4}",
            Name = name,
            MimeType = FolderType,
            Parents = parentId is null ? [] : [parentId],
            AppProperties = root ? new() { ["classscribe"] = "root" } : [],
            Trashed = trashed
        };
        lock (_gate) Files.Add(folder);
        return folder;
    }

    /// <summary>Adds a file with <paramref name="content"/> to the folder <paramref name="parentId"/>, as one uploaded earlier, from another laptop or before a manifest was lost.</summary>
    public FakeDriveFile AddFile(string name, string parentId, byte[] content, string mimeType = "application/octet-stream")
    {
        var file = new FakeDriveFile
        {
            Id = $"file-{Interlocked.Increment(ref _counter):D4}",
            Name = name,
            MimeType = mimeType,
            Parents = [parentId],
            Content = content
        };
        lock (_gate) Files.Add(file);
        return file;
    }

    /// <summary>The folder named <paramref name="name"/> in <paramref name="parentId"/>, or in My Drive when that is null; null when there is none.</summary>
    public FakeDriveFile? Folder(string name, string? parentId = null)
    {
        lock (_gate)
            return Files.FirstOrDefault(file => file.IsFolder && file.Name == name && (parentId is null ? file.Parents.Count == 0 : file.Parents.Contains(parentId)));
    }

    /// <summary>The folder at <paramref name="path"/> under ClassScribe's own folder, such as Mantiq/2026-10-17; null when it is not there.</summary>
    public FakeDriveFile? FolderAt(string path)
    {
        var root = RootFolder;
        foreach (var name in path.Split('/'))
        {
            if (root is null) return null;
            root = Folder(name, root.Id);
        }

        return root;
    }

    /// <summary>ClassScribe's own folder in My Drive, by its mark, outside the trash; null when there is none.</summary>
    public FakeDriveFile? RootFolder
    {
        get
        {
            lock (_gate)
                return Files.FirstOrDefault(file => file.IsFolder && !file.Trashed && file.AppProperties.TryGetValue("classscribe", out var mark) && mark == "root");
        }
    }

    /// <summary>The files, not folders, in the folder <paramref name="folderId"/>, by name.</summary>
    public IReadOnlyList<FakeDriveFile> FilesIn(string folderId)
    {
        lock (_gate) return [.. Files.Where(file => !file.IsFolder && file.Parents.Contains(folderId)).OrderBy(file => file.Name, StringComparer.Ordinal)];
    }

    /// <summary>The next piece sent commits only its first <paramref name="bytes"/> bytes, and Drive answers 308 with a Range that says so.</summary>
    public void CommitOnlyNext(int bytes)
    {
        lock (_gate) _commitOnly = bytes;
    }

    /// <summary>The next piece sent commits its first <paramref name="bytes"/> bytes, then the connection drops before Drive answers.</summary>
    public void DropNextAfter(int bytes)
    {
        lock (_gate) _dropAfter = bytes;
    }

    /// <summary>Drive forgets every upload session so far, as it does after a week: each answers 404 from now on.</summary>
    public void ExpireSessions()
    {
        lock (_gate)
        {
            foreach (var session in _sessions.Values) session.Expired = true;
        }
    }

    /// <summary>The next <paramref name="times"/> uploads of a file named <paramref name="name"/> come out with an MD5 on Drive that is not their content's.</summary>
    public void WrongMd5For(string name, int times = 1)
    {
        lock (_gate) _wrongMd5[name] = times;
    }

    /// <summary>
    /// Answers the next <paramref name="times"/> requests that <paramref name="when"/> picks with <paramref name="status"/> and
    /// <paramref name="body"/>, instead of what Google would answer.
    /// </summary>
    public void FailWhen(Func<GoogleRequest, bool> when, HttpStatusCode status, string body, int times = 1)
    {
        lock (_gate)
        {
            for (var i = 0; i < times; i++)
                _faultsWhen.Add((when, () => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") }));
        }
    }

    /// <summary>Fails the next <paramref name="times"/> requests that <paramref name="when"/> picks with <paramref name="failure"/>, such as a broken connection.</summary>
    public void FailWhen(Func<GoogleRequest, bool> when, Exception failure, int times = 1)
    {
        lock (_gate)
        {
            for (var i = 0; i < times; i++) _faultsWhen.Add((when, () => throw failure));
        }
    }

    /// <summary>
    /// Answers the next <paramref name="times"/> requests whose path ends with <paramref name="path"/> with
    /// <paramref name="status"/> and <paramref name="body"/>, instead of what Google would answer.
    /// </summary>
    public void Fail(string path, HttpStatusCode status, string body, int times = 1)
    {
        lock (_gate)
        {
            for (var i = 0; i < times; i++)
                _faults.Add((path, () => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") }));
        }
    }

    /// <summary>Fails the next request whose path ends with <paramref name="path"/> with <paramref name="failure"/>, such as a broken connection.</summary>
    public void Fail(string path, Exception failure)
    {
        lock (_gate) _faults.Add((path, () => throw failure));
    }

    /// <summary>Holds every request whose path ends with <paramref name="path"/>, once it has been seen, until <paramref name="release"/> completes.</summary>
    public void Hold(string path, Task release)
    {
        lock (_gate) _holds.Add((path, release));
    }

    /// <summary>A Google error body as Drive writes it.</summary>
    public static string DriveError(int code, string reason, string message, string? status = null) =>
        new JsonObject
        {
            ["error"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = message,
                ["errors"] = new JsonArray(new JsonObject { ["reason"] = reason, ["message"] = message, ["domain"] = "global" }),
                ["status"] = status
            }
        }.ToJsonString();

    /// <summary>An OAuth error body as the token and revoke endpoints write it.</summary>
    public static string OAuthError(string error, string description) =>
        new JsonObject { ["error"] = error, ["error_description"] = description }.ToJsonString();

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var bytes = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        var body = Encoding.UTF8.GetString(bytes);
        var uri = request.RequestUri!;
        var bearer = request.Headers.Authorization is { Scheme: "Bearer" } authorization ? authorization.Parameter : null;
        var seen = new GoogleRequest(request.Method, uri.Host, uri.AbsolutePath, HttpUtility.ParseQueryString(uri.Query), bearer, body, request.Content?.Headers.ContentType?.MediaType)
        {
            Bytes = bytes,
            ContentRange = request.Content?.Headers.ContentRange?.ToString(),
            UploadLength = request.Headers.TryGetValues("X-Upload-Content-Length", out var lengths) ? lengths.FirstOrDefault() : null
        };

        Func<HttpResponseMessage>? fault;
        Task? held;
        lock (_gate)
        {
            _requests.Add(seen);
            var index = _faults.FindIndex(entry => seen.Path.EndsWith(entry.Path, StringComparison.Ordinal));
            fault = index >= 0 ? _faults[index].Answer : null;
            if (index >= 0) _faults.RemoveAt(index);
            if (fault is null && _faultsWhen.FindIndex(entry => entry.When(seen)) is var picked and >= 0)
            {
                fault = _faultsWhen[picked].Answer;
                _faultsWhen.RemoveAt(picked);
            }

            held = _holds.FirstOrDefault(entry => seen.Path.EndsWith(entry.Path, StringComparison.Ordinal)).Release;
        }

        if (held is not null) await held.WaitAsync(cancellationToken);
        if (Unreachable) throw new HttpRequestException("No such host is known. (fake.googleapis.com:443)");
        if (fault is not null) return fault();

        lock (_gate)
        {
            return (request.Method.Method, seen.Path) switch
            {
                ("POST", "/token") => Token(seen.Form),
                ("POST", "/revoke") => RevokeRequest(seen.Form),
                ("GET", "/drive/v3/about") => WithBearer(seen, About),
                ("GET", "/drive/v3/files") => WithBearer(seen, () => List(seen.Query["q"])),
                ("POST", "/drive/v3/files") => WithBearer(seen, () => Create(body)),
                ("GET", var path) when path.StartsWith("/drive/v3/files/", StringComparison.Ordinal) => WithBearer(seen, () => Get(path["/drive/v3/files/".Length..])),
                ("POST", "/upload/drive/v3/files") => WithBearer(seen, () => StartUpload(seen, uri, revisionOf: null)),
                ("PATCH", var path) when path.StartsWith("/upload/drive/v3/files/", StringComparison.Ordinal) =>
                    WithBearer(seen, () => StartUpload(seen, uri, path["/upload/drive/v3/files/".Length..])),
                ("PUT", var path) when path.StartsWith("/upload/drive/v3/files", StringComparison.Ordinal) => WithBearer(seen, () => Piece(seen)),
                _ => Json(HttpStatusCode.NotFound, DriveError(404, "notFound", $"No such endpoint: {request.Method} {seen.Path}"))
            };
        }
    }

    /// <summary>Starts a resumable upload of a new file, or of a new revision of <paramref name="revisionOf"/>, and answers with the session's address. Call with the lock held.</summary>
    private HttpResponseMessage StartUpload(GoogleRequest request, Uri uri, string? revisionOf)
    {
        Assert.That(request.Query["uploadType"], Is.EqualTo("resumable"), "every upload is a resumable one");
        if (revisionOf is not null && Files.FirstOrDefault(file => file.Id == revisionOf && !file.IsFolder) is null)
            return Json(HttpStatusCode.NotFound, DriveError(404, "notFound", $"File not found: {revisionOf}."));

        var metadata = request.Body.Length > 0 ? JsonNode.Parse(request.Body)!.AsObject() : [];
        var id = $"ABPtcPp-test-session-{++_counter:D4}-{Guid.NewGuid():N}";
        _sessions[id] = new FakeUploadSession
        {
            Id = id,
            Name = (string?)metadata["name"],
            MimeType = (string?)metadata["mimeType"],
            Parents = metadata["parents"] is JsonArray parents ? [.. parents.Select(parent => (string)parent!)] : [],
            RevisionOf = revisionOf,
            Declared = long.Parse(request.UploadLength ?? "0", System.Globalization.CultureInfo.InvariantCulture)
        };
        _secrets.Add(id);

        var host = SessionHost ?? $"{uri.Scheme}://{uri.Authority}";
        var path = revisionOf is null ? "/upload/drive/v3/files" : $"/upload/drive/v3/files/{revisionOf}";
        var answer = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("", Encoding.UTF8, "text/plain") };
        answer.Headers.Location = new Uri($"{host}{path}?uploadType=resumable&upload_id={id}");
        return answer;
    }

    /// <summary>
    /// A piece of a resumable upload, or the question how much Drive has: 308 with the Range received, or 200 with the file
    /// once all the declared bytes are in; 404 for a session Drive does not know or has forgotten; 400 for a piece that does
    /// not follow what was received, or that is not the last and not a multiple of 256 KiB, as Drive refuses it. Call with
    /// the lock held.
    /// </summary>
    private HttpResponseMessage Piece(GoogleRequest request)
    {
        if (request.Query["upload_id"] is not { } id || !_sessions.TryGetValue(id, out var session) || session.Expired)
            return Json(HttpStatusCode.NotFound, DriveError(404, "notFound", "The upload session was not found."));

        var pathFile = request.Path.Length > "/upload/drive/v3/files/".Length ? request.Path["/upload/drive/v3/files/".Length..] : null;
        Assert.That(pathFile, Is.EqualTo(session.RevisionOf), "a piece goes to the session it belongs to");
        if (session.Completed is { } done) return Json(HttpStatusCode.OK, Answer(done));

        var range = request.ContentRange ?? "";
        if (!range.StartsWith("bytes */", StringComparison.Ordinal))
        {
            var dash = range.IndexOf('-', StringComparison.Ordinal);
            var start = long.Parse(range["bytes ".Length..dash], System.Globalization.CultureInfo.InvariantCulture);
            if (start > session.Received.Length) return Json(HttpStatusCode.BadRequest, DriveError(400, "badContent", "The piece does not follow what was received."));
            if (start + request.Bytes.Length < session.Declared && request.Bytes.Length % PieceMultiple != 0)
                return Json(HttpStatusCode.BadRequest, DriveError(400, "badContent", "A piece that is not the last is not a multiple of 256 KiB."));

            session.Received.SetLength(start);
            session.Received.Position = start;
            var take = request.Bytes.Length;
            if (_commitOnly is { } only)
            {
                take = Math.Min(only, take);
                _commitOnly = null;
            }

            if (_dropAfter is { } drop)
            {
                session.Received.Write(request.Bytes, 0, Math.Min(drop, request.Bytes.Length));
                _dropAfter = null;
                throw new HttpRequestException("The connection was reset.");
            }

            session.Received.Write(request.Bytes, 0, take);
        }

        if (session.Received.Length < session.Declared)
        {
            var more = new HttpResponseMessage((HttpStatusCode)308) { Content = new StringContent("") };
            if (session.Received.Length > 0) more.Headers.TryAddWithoutValidation("Range", $"bytes=0-{session.Received.Length - 1}");
            return more;
        }

        var file = session.RevisionOf is { } fileId ? Files.First(known => known.Id == fileId) : NewFile(session);
        file.Content = session.Received.ToArray();
        file.Revisions++;
        file.Md5Override = _wrongMd5.TryGetValue(file.Name, out var wrong) && wrong > 0 ? "0123456789abcdef0123456789abcdef" : null;
        if (wrong > 0) _wrongMd5[file.Name] = wrong - 1;
        session.Completed = file;
        return Json(HttpStatusCode.OK, Answer(file));
    }

    /// <summary>The file a completed session of a new file makes, in its folder. Call with the lock held.</summary>
    private FakeDriveFile NewFile(FakeUploadSession session)
    {
        var file = new FakeDriveFile { Id = $"file-{++_counter:D4}", Name = session.Name ?? "Untitled", MimeType = session.MimeType ?? "application/octet-stream", Parents = session.Parents };
        Files.Add(file);
        return file;
    }

    /// <summary>What Drive answers a completed upload with: the file's kind, id, name and type.</summary>
    private static string Answer(FakeDriveFile file) =>
        new JsonObject { ["kind"] = "drive#file", ["id"] = file.Id, ["name"] = file.Name, ["mimeType"] = file.MimeType }.ToJsonString();

    /// <summary>The token endpoint: the code exchange, or a refresh. Call with the lock held.</summary>
    private HttpResponseMessage Token(NameValueCollection form)
    {
        if (form["client_id"] != ClientId || form["client_secret"] != ClientSecret)
            return Json(HttpStatusCode.Unauthorized, OAuthError("invalid_client", "The OAuth client was not found."));

        switch (form["grant_type"])
        {
            case "authorization_code":
            {
                if (form["code_verifier"] is { } verifier) _secrets.Add(verifier);
                if (form["code"] is not { } code || !_codes.Remove(code, out var issued))
                    return Json(HttpStatusCode.BadRequest, OAuthError("invalid_grant", "Bad Request"));
                if (form["redirect_uri"] != issued.RedirectUri)
                    return Json(HttpStatusCode.BadRequest, OAuthError("redirect_uri_mismatch", "Bad Request"));
                if (issued.Method != "S256" || form["code_verifier"] is not { } given || Challenge(given) != issued.Challenge)
                    return Json(HttpStatusCode.BadRequest, OAuthError("invalid_grant", "Invalid code verifier."));

                var answer = new JsonObject
                {
                    ["access_token"] = NewAccessToken(IssuesRefreshToken ? NewRefreshToken() : null, out var refresh),
                    ["expires_in"] = 3599,
                    ["scope"] = GrantedScope,
                    ["token_type"] = "Bearer"
                };
                if (refresh is not null) answer["refresh_token"] = refresh;
                return Json(HttpStatusCode.OK, answer.ToJsonString());
            }
            case "refresh_token":
            {
                if (form["refresh_token"] is not { } refreshToken || !_refreshTokens.Contains(refreshToken) || _revoked.Contains(refreshToken))
                    return Json(HttpStatusCode.BadRequest, OAuthError("invalid_grant", "Token has been expired or revoked."));

                return Json(HttpStatusCode.OK, new JsonObject
                {
                    ["access_token"] = NewAccessToken(refreshToken, out _),
                    ["expires_in"] = 3599,
                    ["scope"] = DriveFileScope,
                    ["token_type"] = "Bearer"
                }.ToJsonString());
            }
            default:
                return Json(HttpStatusCode.BadRequest, OAuthError("unsupported_grant_type", "Invalid grant_type"));
        }
    }

    /// <summary>The revoke endpoint. Call with the lock held.</summary>
    private HttpResponseMessage RevokeRequest(NameValueCollection form)
    {
        var token = form["token"];
        if (token is null || _revoked.Contains(token) || (!_refreshTokens.Contains(token) && !_accessTokens.ContainsKey(token)))
            return Json(HttpStatusCode.BadRequest, OAuthError("invalid_token", "Token expired or revoked"));

        Revoke(token);
        return Json(HttpStatusCode.OK, "{}");
    }

    /// <summary>Revokes a token, and for a refresh token every access token made from it. Call with the lock held.</summary>
    private void Revoke(string token)
    {
        _revoked.Add(token);
        foreach (var (access, refresh) in _accessTokens)
            if (refresh == token) _revoked.Add(access);
    }

    /// <summary>Drive's about: the account and its storage.</summary>
    private HttpResponseMessage About()
    {
        var quota = new JsonObject { ["usage"] = StorageUsage.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        if (StorageLimit is { } limit) quota["limit"] = limit.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Json(HttpStatusCode.OK, new JsonObject
        {
            ["user"] = new JsonObject { ["displayName"] = DisplayName, ["emailAddress"] = Email },
            ["storageQuota"] = quota
        }.ToJsonString());
    }

    /// <summary>
    /// files.list, for the queries ClassScribe sends: its own folder in My Drive, by its mark; a folder by its name in its
    /// parent; and everything in a folder; each leaving out what is in the trash. Any other query is refused, as Drive refuses
    /// one it cannot read.
    /// </summary>
    private HttpResponseMessage List(string? query)
    {
        const string root = "mimeType = 'application/vnd.google-apps.folder' and appProperties has { key='classscribe' and value='root' } and trashed = false";
        IEnumerable<FakeDriveFile> found;
        if (query == root)
        {
            found = Files.Where(file => file.MimeType == FolderType && !file.Trashed && file.AppProperties.TryGetValue("classscribe", out var mark) && mark == "root");
        }
        else if (FolderByNameQuery().Match(query ?? "") is { Success: true } named)
        {
            var parent = Unescape(named.Groups["parent"].Value);
            var name = Unescape(named.Groups["name"].Value);
            found = Files.Where(file => file.IsFolder && !file.Trashed && file.Parents.Contains(parent) && file.Name == name);
        }
        else if (ChildrenQuery().Match(query ?? "") is { Success: true } children)
        {
            var parent = Unescape(children.Groups["parent"].Value);
            found = Files.Where(file => !file.Trashed && file.Parents.Contains(parent));
        }
        else
        {
            return Json(HttpStatusCode.BadRequest, DriveError(400, "invalid", $"Invalid Value: {query}"));
        }

        return Json(HttpStatusCode.OK, new JsonObject { ["files"] = new JsonArray([.. found.Select(Describe)]) }.ToJsonString());
    }

    /// <summary>A value of a search with Drive's escapes undone: \' is a quote and \\ a backslash.</summary>
    private static string Unescape(string value) => Regex.Replace(value, @"\\(.)", "$1");

    /// <summary>The search for a folder by its name in its parent, with the values escaped as Drive asks.</summary>
    [GeneratedRegex(@"^'(?<parent>(?:[^'\\]|\\.)*)' in parents and name = '(?<name>(?:[^'\\]|\\.)*)' and mimeType = 'application/vnd\.google-apps\.folder' and trashed = false$")]
    private static partial Regex FolderByNameQuery();

    /// <summary>The search for everything in a folder, outside the trash.</summary>
    [GeneratedRegex(@"^'(?<parent>(?:[^'\\]|\\.)*)' in parents and trashed = false$")]
    private static partial Regex ChildrenQuery();

    /// <summary>files.get.</summary>
    private HttpResponseMessage Get(string id) =>
        Files.FirstOrDefault(file => file.Id == id) is { } file
            ? Json(HttpStatusCode.OK, Describe(file).ToJsonString())
            : Json(HttpStatusCode.NotFound, DriveError(404, "notFound", $"File not found: {id}."));

    /// <summary>files.create of a folder, from the metadata in the body.</summary>
    private HttpResponseMessage Create(string body)
    {
        var metadata = JsonNode.Parse(body)!.AsObject();
        var folder = new FakeDriveFile
        {
            Id = $"folder-{++_counter:D4}",
            Name = (string)metadata["name"]!,
            MimeType = (string?)metadata["mimeType"] ?? "application/octet-stream",
            Parents = metadata["parents"] is JsonArray parents ? [.. parents.Select(parent => (string)parent!)] : [],
            AppProperties = metadata["appProperties"] is JsonObject properties ? properties.ToDictionary(property => property.Key, property => (string)property.Value!) : []
        };
        Files.Add(folder);
        return Json(HttpStatusCode.OK, new JsonObject { ["id"] = folder.Id, ["name"] = folder.Name }.ToJsonString());
    }

    /// <summary>Answers <paramref name="answer"/> when the request carries an access token Drive takes; 401 otherwise.</summary>
    private HttpResponseMessage WithBearer(GoogleRequest request, Func<HttpResponseMessage> answer) =>
        request.Bearer is { } token && _accessTokens.ContainsKey(token) && !_revoked.Contains(token)
            ? answer()
            : Json(HttpStatusCode.Unauthorized, DriveError(401, "authError", "Request had invalid authentication credentials.", "UNAUTHENTICATED"));

    /// <summary>A file as Drive describes it; one with content has its size, written as text as Drive writes it, and its MD5.</summary>
    private static JsonObject Describe(FakeDriveFile file)
    {
        var described = new JsonObject
        {
            ["id"] = file.Id,
            ["name"] = file.Name,
            ["mimeType"] = file.MimeType,
            ["trashed"] = file.Trashed,
            ["parents"] = new JsonArray([.. file.Parents.Select(parent => (JsonNode)parent)])
        };
        if (!file.IsFolder)
        {
            described["size"] = file.Size!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            described["md5Checksum"] = file.Md5;
        }

        return described;
    }

    /// <summary>A new refresh token, kept and noted as a secret. Call with the lock held.</summary>
    private string NewRefreshToken()
    {
        var token = $"1//0gTestRefresh-{++_counter:D4}-{Guid.NewGuid():N}";
        _refreshTokens.Add(token);
        _secrets.Add(token);
        RefreshTokensIssued.Add(token);
        return token;
    }

    /// <summary>A new access token made from <paramref name="refreshToken"/>, kept and noted as a secret. Call with the lock held.</summary>
    private string NewAccessToken(string? refreshToken, out string? refresh)
    {
        refresh = refreshToken;
        var token = $"ya29.a0TestAccess-{++_counter:D4}-{Guid.NewGuid():N}";
        _accessTokens[token] = refreshToken ?? "";
        _secrets.Add(token);
        AccessTokensIssued.Add(token);
        return token;
    }

    /// <summary>The S256 challenge of a verifier, worked out here on its own, as Google does.</summary>
    private static string Challenge(string verifier) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>An answer with a JSON body.</summary>
    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>A code handed out: for which client, redirect address and challenge.</summary>
    private sealed record IssuedCode(string ClientId, string RedirectUri, string Challenge, string Method);
}

/// <summary>What <see cref="FakeBrowser"/> does when it is sent to Google's sign-in.</summary>
public enum BrowserPlay
{
    /// <summary>The person allows ClassScribe, and Google sends the browser back with a code.</summary>
    Allow,

    /// <summary>The person clicks Cancel, and Google sends the browser back with error=access_denied.</summary>
    Deny,

    /// <summary>Another page asks the listener first, with a state that is not the sign-in's; then the person allows.</summary>
    WrongStateFirst,

    /// <summary>The browser asks for /favicon.ico first; then the person allows.</summary>
    FaviconFirst,

    /// <summary>The browser opens, and nothing comes back: the person walked away.</summary>
    Nothing,

    /// <summary>The browser cannot be opened at all.</summary>
    CannotOpen
}

/// <summary>
/// The browser as a test plays it: it keeps the addresses it is sent to, and plays the person on Google's pages as
/// <see cref="Play"/> says, sending the redirect to the listener of <see cref="FakeLoopbackReceiver"/> that is open.
/// </summary>
public sealed class FakeBrowser(FakeGoogle google, FakeLoopbackReceiver receiver) : IBrowser
{
    /// <summary>What the person does; Allow unless the test says otherwise.</summary>
    public BrowserPlay Play { get; set; } = BrowserPlay.Allow;

    /// <summary>The addresses the browser was sent to, in order.</summary>
    public List<Uri> Opened { get; } = [];

    /// <inheritdoc />
    public bool Open(Uri address)
    {
        Opened.Add(address);
        if (Play == BrowserPlay.CannotOpen) return false;
        if (Play == BrowserPlay.Nothing) return true;

        var parameters = HttpUtility.ParseQueryString(address.Query);
        var listener = receiver.Current ?? throw new InvalidOperationException("The browser was opened while no listener was open.");
        Assert.That(parameters["redirect_uri"], Is.EqualTo(listener.RedirectUri.AbsoluteUri), "Google sends the browser back to the listener that is open");
        var state = parameters["state"]!;

        if (Play == BrowserPlay.FaviconFirst) listener.Arrive("/favicon.ico", []);
        if (Play == BrowserPlay.WrongStateFirst) listener.Arrive("/", new() { ["code"] = "4/0-not-for-you", ["state"] = "another-page-s-state" });
        if (Play == BrowserPlay.Deny)
        {
            listener.Arrive("/", new() { ["error"] = "access_denied", ["state"] = state });
            return true;
        }

        var code = google.Authorize(address);
        listener.Arrive("/", new() { ["code"] = code, ["scope"] = google.GrantedScope, ["state"] = state });
        return true;
    }
}

/// <summary>
/// The local listener as a test plays it: each <see cref="Open"/> makes a listener on a made-up port of 127.0.0.1, in
/// memory, which <see cref="FakeBrowser"/> sends its requests to and which keeps each answer.
/// </summary>
public sealed class FakeLoopbackReceiver : ILoopbackReceiver
{
    private int _opened;

    /// <summary>The listener open now, or the last one; null before the first.</summary>
    public FakeLoopbackListener? Current { get; private set; }

    /// <summary>How many listeners were opened.</summary>
    public int Opened => _opened;

    /// <inheritdoc />
    public ILoopbackListener Open()
    {
        var number = Interlocked.Increment(ref _opened);
        Current = new FakeLoopbackListener(new Uri($"http://127.0.0.1:{51280 + number}/"));
        return Current;
    }
}

/// <summary>One listener of <see cref="FakeLoopbackReceiver"/>: requests arrive through <see cref="Arrive"/>, and their answers are kept.</summary>
public sealed class FakeLoopbackListener(Uri redirectUri) : ILoopbackListener
{
    private readonly Channel<FakeLoopbackRequest> _arrived = Channel.CreateUnbounded<FakeLoopbackRequest>();
    private readonly ConcurrentQueue<(string Path, int Status, string Html)> _answers = new();

    /// <inheritdoc />
    public Uri RedirectUri { get; } = redirectUri;

    /// <summary>True once the listener was closed.</summary>
    public bool Disposed { get; private set; }

    /// <summary>The answers given, with the path each answered, in order.</summary>
    public IReadOnlyList<(string Path, int Status, string Html)> Answers => [.. _answers];

    /// <summary>A request arrives for <paramref name="path"/> with <paramref name="query"/>.</summary>
    public void Arrive(string path, Dictionary<string, string> query) =>
        _arrived.Writer.TryWrite(new FakeLoopbackRequest(path, query, _answers));

    /// <inheritdoc />
    public async Task<ILoopbackRequest> NextAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        return await _arrived.Reader.ReadAsync(cancellationToken);
    }

    /// <inheritdoc />
    public void Dispose() => Disposed = true;
}

/// <summary>One request to <see cref="FakeLoopbackListener"/>, which keeps its answer with the listener's.</summary>
public sealed class FakeLoopbackRequest(string path, Dictionary<string, string> query, ConcurrentQueue<(string Path, int Status, string Html)> answers) : ILoopbackRequest
{
    /// <inheritdoc />
    public string Path => path;

    /// <inheritdoc />
    public string? Query(string name) => query.GetValueOrDefault(name);

    /// <inheritdoc />
    public Task AnswerAsync(int status, string html)
    {
        answers.Enqueue((path, status, html));
        return Task.CompletedTask;
    }
}

/// <summary>
/// A browser that really asks the listener: it sends Google's redirect, with a code from <see cref="FakeGoogle"/>, to the
/// address given, which must be on 127.0.0.1, so nothing leaves the computer. <see cref="Done"/> completes with the page
/// the listener answered.
/// </summary>
public sealed class LocalHttpBrowser(FakeGoogle google) : IBrowser
{
    private readonly TaskCompletionSource<(HttpStatusCode Status, string Page)> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes with the status and the page the listener answered the redirect with.</summary>
    public Task<(HttpStatusCode Status, string Page)> Done => _done.Task;

    /// <inheritdoc />
    public bool Open(Uri address)
    {
        var parameters = HttpUtility.ParseQueryString(address.Query);
        var redirect = new Uri(parameters["redirect_uri"]!);
        Assert.That(redirect.Host, Is.EqualTo("127.0.0.1"), "the redirect goes to this computer alone");
        var code = google.Authorize(address);
        var target = new Uri(redirect, $"?state={Uri.EscapeDataString(parameters["state"]!)}&code={Uri.EscapeDataString(code)}&scope={Uri.EscapeDataString(google.GrantedScope)}");
        _ = Task.Run(async () =>
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                using var response = await http.GetAsync(target);
                _done.TrySetResult((response.StatusCode, await response.Content.ReadAsStringAsync()));
            }
            catch (Exception ex)
            {
                // Deliberately not rethrown: the test awaits Done, which carries the failure.
                _done.TrySetException(ex);
            }
        });
        return true;
    }
}

/// <summary>
/// The store of the Google client and sign-in, in memory: what was written last, or a reason it cannot be read, as a
/// store made by another Windows account would give. It never touches a file.
/// </summary>
public sealed class FakeCredentialStore : IGoogleCredentialStore
{
    private readonly Lock _gate = new();
    private GoogleCredentials? _stored;
    private string? _unreadable;

    /// <summary>How many times it was written.</summary>
    public int Writes { get; private set; }

    /// <summary>What is stored now; null when nothing is.</summary>
    public GoogleCredentials? Stored
    {
        get { lock (_gate) return _stored; }
        set
        {
            lock (_gate)
            {
                _stored = value;
                _unreadable = null;
            }
        }
    }

    /// <inheritdoc />
    public string Location => @"C:\Users\Student\AppData\Roaming\ClassScribe\google-drive.dat";

    /// <summary>Makes the store one that cannot be read, for <paramref name="reason"/>.</summary>
    public void MakeUnreadable(string reason)
    {
        lock (_gate) _unreadable = reason;
    }

    /// <summary>A store holding the tests' client, signed in with <paramref name="refreshToken"/> when one is given.</summary>
    public static FakeCredentialStore With(string? refreshToken, string email = "student@example.com") => new()
    {
        Stored = new GoogleCredentials(FakeGoogle.ClientId, FakeGoogle.ClientSecret)
        {
            RefreshToken = refreshToken,
            Email = refreshToken is null ? null : email,
            SignedInUtc = refreshToken is null ? null : TestRuns.Start,
            Scope = refreshToken is null ? null : FakeGoogle.DriveFileScope
        }
    };

    /// <inheritdoc />
    public StoredCredentials Read()
    {
        lock (_gate)
        {
            if (_unreadable is { } reason) return StoredCredentials.Unreadable(reason);
            return _stored is null ? StoredCredentials.None : StoredCredentials.Of(_stored);
        }
    }

    /// <inheritdoc />
    public void Write(GoogleCredentials credentials)
    {
        lock (_gate)
        {
            _stored = credentials;
            _unreadable = null;
            Writes++;
        }
    }
}

/// <summary>
/// A logger provider that keeps every line any part of the app logs, with its category, level, message and exception, so a
/// test can look through all of them, those of Microsoft.Extensions.Http included.
/// </summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<(string Category, LogLevel Level, string Text)> _lines = new();

    /// <summary>Every line logged, with its category and level; the text holds the message and the exception, if any.</summary>
    public IReadOnlyList<(string Category, LogLevel Level, string Text)> Lines => [.. _lines];

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    /// <summary>Nothing to let go of.</summary>
    public void Dispose()
    {
    }

    /// <summary>A logger of one category that keeps every line with the provider.</summary>
    private sealed class Logger(CapturingLoggerProvider provider, string category) : ILogger
    {
        /// <inheritdoc />
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        /// <inheritdoc />
        public bool IsEnabled(LogLevel logLevel) => true;

        /// <inheritdoc />
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            provider._lines.Enqueue((category, logLevel, $"{formatter(state, exception)} {exception}"));
    }
}

/// <summary>
/// One handler for every HTTP client of an app under test that reaches both Google and another service, such as the stub
/// standing in for ElevenLabs: a request to a Google host (googleapis.com) goes to <paramref name="google"/>, any other to
/// <paramref name="other"/>. It disposes neither, since they belong to the test, and each client's chain is disposed on
/// its own.
/// </summary>
/// <param name="google">The in-memory Google.</param>
/// <param name="other">The handler for every other request.</param>
public sealed class GoogleOrElse(FakeGoogle google, HttpMessageHandler other) : HttpMessageHandler
{
    private readonly HttpMessageInvoker _google = new(google, disposeHandler: false);
    private readonly HttpMessageInvoker _other = new(other, disposeHandler: false);

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        request.RequestUri?.Host.EndsWith("googleapis.com", StringComparison.OrdinalIgnoreCase) == true
            ? _google.SendAsync(request, cancellationToken)
            : _other.SendAsync(request, cancellationToken);
}

/// <summary>
/// Google Drive's part in a run, played: it notes each call in <see cref="Calls"/>, in order, as "Queue &lt;base&gt;",
/// "Ended &lt;kind&gt;", "AfterRun" and "Upload &lt;base&gt;", and runs <see cref="OnUpload"/>, when given, as an upload
/// would take its time. It sends nothing anywhere.
/// </summary>
public sealed class FakeDriveAutoUpload : IDriveAutoUpload
{
    private readonly ConcurrentQueue<string> _calls = new();

    /// <summary>Each call so far, in order.</summary>
    public IReadOnlyList<string> Calls => [.. _calls];

    /// <summary>The recordings handed to <see cref="Queue"/> and <see cref="UploadAsync"/>, in order.</summary>
    public List<RecordingFiles> Recordings { get; } = [];

    /// <summary>What an upload after the run, or of a recording, does while it runs; nothing unless a test says.</summary>
    public Func<Task>? OnUpload { get; set; }

    /// <summary>What a test looks at as the recording is queued; nothing unless a test says.</summary>
    public Action<RecordingFiles>? OnQueue { get; set; }

    /// <inheritdoc />
    public void Queue(RecordingFiles files)
    {
        lock (Recordings) Recordings.Add(files);
        _calls.Enqueue($"Queue {files.BaseName}");
        OnQueue?.Invoke(files);
    }

    /// <inheritdoc />
    public void Ended(RunOutcome outcome) => _calls.Enqueue($"Ended {outcome.Kind}");

    /// <inheritdoc />
    public async Task AfterRunAsync()
    {
        _calls.Enqueue("AfterRun");
        if (OnUpload is { } upload) await upload();
    }

    /// <inheritdoc />
    public async Task UploadAsync(RecordingFiles files)
    {
        lock (Recordings) Recordings.Add(files);
        _calls.Enqueue($"Upload {files.BaseName}");
        if (OnUpload is { } upload) await upload();
    }
}

/// <summary>
/// Everything a sign-in test needs around the code under test, put together once: the in-memory Google, the browser, the
/// listener, the store, the console, a clock the test moves, ClassScribe's own folder in a temporary folder, the Drive
/// settings and the Refit clients over the fake Google, and a client file in a temporary folder. Disposing it deletes the
/// folders.
/// </summary>
public sealed class GoogleWorld : IDisposable
{
    /// <summary>Puts everything together, with nothing stored unless <paramref name="store"/> is given.</summary>
    public GoogleWorld(FakeCredentialStore? store = null)
    {
        Store = store ?? new FakeCredentialStore();
        Browser = new FakeBrowser(Google, Receiver);
        OAuth = RestService.ForGenerated<IGoogleOAuthApi>(new HttpClient(Google) { BaseAddress = new Uri("https://oauth2.googleapis.com/") }, GoogleOAuthJson.Default, DriveApiSettings.Create());
        Api = RestService.ForGenerated<IDriveApi>(new HttpClient(Google) { BaseAddress = new Uri("https://www.googleapis.com/") }, DriveJson.Default, DriveApiSettings.Create());
    }

    /// <summary>The in-memory Google.</summary>
    public FakeGoogle Google { get; } = new();

    /// <summary>The listener the sign-in opens.</summary>
    public FakeLoopbackReceiver Receiver { get; } = new();

    /// <summary>The browser, which plays the person.</summary>
    public FakeBrowser Browser { get; }

    /// <summary>The store of the client and sign-in.</summary>
    public FakeCredentialStore Store { get; }

    /// <summary>The console, where the person reads and presses keys.</summary>
    public FakeConsoleUi Ui { get; } = new();

    /// <summary>The clock, at the tests' class until the test moves it.</summary>
    public ManualTimeProvider Time { get; } = new(TestRuns.Start);

    /// <summary>ClassScribe's own folder, in a temporary folder.</summary>
    public TestRuntime Runtime { get; } = new();

    /// <summary>A temporary folder for the client files the tests write.</summary>
    public TempFolder Downloads { get; } = new();

    /// <summary>The Drive settings, as the file would give them.</summary>
    public TestOptions<DriveOptions> Options { get; } = TestOptions.Create(new DriveOptions());

    /// <summary>The YouTube settings, as the file would give them: what of a video's recording goes up among them.</summary>
    public TestOptions<YouTubeOptions> YouTube { get; } = TestOptions.Create(new YouTubeOptions());

    /// <summary>The access tokens of this process.</summary>
    public GoogleTokenCache Cache { get; } = new();

    /// <summary>What every part logged.</summary>
    public TestLoggerFactory Logs { get; } = new();

    /// <summary>A logger of <typeparamref name="T"/> that keeps its lines in <see cref="Logs"/>.</summary>
    public ILogger<T> Log<T>() => new Logger<T>(Logs);

    /// <summary>Google's OAuth endpoints, over the fake Google.</summary>
    public IGoogleOAuthApi OAuth { get; }

    /// <summary>Drive's endpoints, over the fake Google.</summary>
    public IDriveApi Api { get; }

    /// <summary>The access tokens, read and renewed through the fake Google.</summary>
    public GoogleTokens Tokens() => new(Cache, OAuth, Store, Time, Log<GoogleTokens>());

    /// <summary>Drive, through the fake Google.</summary>
    public DriveClient Drive() => new(Api, Tokens(), Options, Time, Log<DriveClient>());

    /// <summary>ClassScribe's folder on Drive, remembered in the temporary ClassScribe folder.</summary>
    public DriveFolders Folders() => new(Drive(), Options, Runtime.Folder, Log<DriveFolders>());

    /// <summary>The sign-in, with everything above, its local times in US Eastern time, so that a date it shows is the same on every machine.</summary>
    public GoogleSignIn SignIn(IBrowser? browser = null, ILoopbackReceiver? receiver = null) =>
        new(OAuth, Tokens(), Store, Drive(), Folders(), receiver ?? Receiver, browser ?? Browser, Ui, ZonedClock.Eastern(Time), Log<GoogleSignIn>());

    /// <summary>
    /// The drive command, with everything above and Ctrl+C played by <paramref name="signals"/>, its local times in US Eastern
    /// time, so that the date of a sign-in it shows is the same on every machine.
    /// </summary>
    public DriveCommand Command(FakeUserSignals? signals = null) =>
        new(SignIn(), Store, Drive(), Folders(), QueueReport(), Options, signals ?? new FakeUserSignals(), Ui, ZonedClock.Eastern(Time), Log<DriveCommand>());

    /// <summary>
    /// Writes a client file as Google's Download JSON gives it, under <paramref name="kind"/> (installed for a Desktop app,
    /// web for a Web application), with the tests' client; <paramref name="change"/> may change the inner object first.
    /// </summary>
    public string WriteClientFile(string kind = "installed", Action<JsonObject>? change = null, string name = "client_secret_123456789012.json")
    {
        var inner = new JsonObject
        {
            ["client_id"] = FakeGoogle.ClientId,
            ["project_id"] = "classscribe-123456",
            ["auth_uri"] = "https://accounts.google.com/o/oauth2/auth",
            ["token_uri"] = "https://oauth2.googleapis.com/token",
            ["auth_provider_x509_cert_url"] = "https://www.googleapis.com/oauth2/v1/certs",
            ["client_secret"] = FakeGoogle.ClientSecret,
            ["redirect_uris"] = new JsonArray("http://localhost")
        };
        change?.Invoke(inner);
        var path = Downloads.File(name);
        File.WriteAllText(path, new JsonObject { [kind] = inner }.ToJsonString());
        return path;
    }

    /// <summary>Signs in through the browser, as the tests' person allowing it, with the client file; returns the refresh token stored.</summary>
    public async Task<string> SignInAsync()
    {
        await SignIn().SignInAsync(WriteClientFile(), CancellationToken.None);
        return Store.Stored!.RefreshToken!;
    }

    /// <summary>The piece size of the uploads in tests: 256 KiB, the smallest Drive takes, so a file of under a megabyte goes in several pieces.</summary>
    public const int PieceSize = 256 * 1024;

    /// <summary>The recordings folder, in a temporary folder.</summary>
    public TempFolder Recordings { get; } = new();

    /// <summary>Windows' power requests, as the uploads hold them.</summary>
    public FakePowerRequests Power { get; } = new();

    /// <summary>Where the laptop takes its power from.</summary>
    public FakePowerSource PowerSource { get; } = new();

    /// <summary>The Recording settings, with the recordings folder in <see cref="Recordings"/>.</summary>
    public TestOptions<RecordingOptions> Recording => _recording ??= TestOptions.Create(new RecordingOptions { OutputFolder = Recordings.Path });

    private TestOptions<RecordingOptions>? _recording;

    /// <summary>Signs in as one stored earlier would be, without the browser.</summary>
    public void SignedIn() => Store.Stored = FakeCredentialStore.With(Google.IssueRefreshToken()).Stored;

    /// <summary>The upload manifests, in the temporary ClassScribe folder.</summary>
    public UploadManifests Manifests() => new(Runtime.Folder, Time, Log<UploadManifests>());

    /// <summary>The retries of a step, without the random part of each wait, on the test's clock.</summary>
    public DriveRetry Retry() => new(Time, Log<DriveRetry>(), () => 0);

    /// <summary>The resumable upload of one file, in pieces of 256 KiB.</summary>
    public ResumableUpload Resumable() => new(Drive(), Retry(), Options, Time, Log<ResumableUpload>()) { PieceSize = PieceSize };

    /// <summary>
    /// The log the process writes, as the app's every logger does: a test that adds it to <see cref="Logs"/> and names a
    /// recording's log in it plays a process that is still writing that recording's log, as a recorder after its class.
    /// </summary>
    public RecorderLog RecorderLog => _recorderLog ??= new RecorderLog(Time);

    private RecorderLog? _recorderLog;

    /// <summary>The upload of one recording.</summary>
    public RecordingUpload RecordingUpload() => new(Drive(), Folders(), Resumable(), Retry(), Manifests(), Runtime.Journal, RecorderLog, Options, YouTube, Time, Log<RecordingUpload>());

    /// <summary>The upload lock, in the temporary ClassScribe folder.</summary>
    public DriveUploadLock Lock() => new(Runtime.Folder, Log<DriveUploadLock>());

    /// <summary>One pass of uploads, with everything above.</summary>
    public DriveUploadPass Pass() => new(RecordingUpload(), Manifests(), Lock(), Store, Runtime.Journal, Power, PowerSource, Recording, Options, Time, Log<DriveUploadPass>());

    /// <summary>The uploads that happen by themselves in a run, over a pass made when one runs, with Ctrl+C played by <paramref name="signals"/>.</summary>
    public DriveAutoUpload AutoUpload(FakeUserSignals? signals = null) =>
        new(Store, Manifests(), new Lazy<DriveUploadPass>(Pass), signals ?? new FakeUserSignals(), Options, Log<DriveAutoUpload>());

    /// <summary>What the menu's header and the report at the start say of the queue, with local times in US Eastern time.</summary>
    public DriveQueueReport QueueReport() => new(Store, Manifests(), Runtime.Journal, Options, ZonedClock.Eastern(Time));

    /// <summary>The freeing of room on the laptop.</summary>
    public LocalAudioFreer Freer() => new(Drive(), Retry(), Manifests(), RecordingUpload(), Time, Log<LocalAudioFreer>());

    /// <summary>The drive upload and free-space command, with Ctrl+C played by <paramref name="signals"/>.</summary>
    public DriveUploadCommand UploadCommand(FakeUserSignals? signals = null) => new(Pass(), Freer(), signals ?? new FakeUserSignals(), Log<DriveUploadCommand>());

    /// <summary>The class folders under <see cref="Recordings"/>, which know of the audio kept on Drive alone.</summary>
    public ClassFolders ClassFolders() => new(Recording, Time, Log<ClassFolders>(), new DriveCopies(Manifests()));

    /// <summary>The lines logged by every part, in order, as text.</summary>
    public IReadOnlyList<string> Lines => Logs.Messages;

    /// <summary>
    /// Writes a recording of <paramref name="name"/> started at <paramref name="startedUtc"/> into <paramref name="folder"/>
    /// under the recordings folder (a course and date folder, or an old flat one): its audio, a .wav of
    /// <paramref name="audioBytes"/> bytes of audio at 8 kHz, and the files <paramref name="extras"/> names, such as ".md",
    /// ".json", ".marks.txt" and ".log", each with a line of its own. Returns its files.
    /// </summary>
    public RecordingFiles WriteRecording(string folder, string name, DateTimeOffset startedUtc, int audioBytes = 3 * PieceSize - 1000, params string[] extras)
    {
        var files = RecordingFiles.ForNew(Directory.CreateDirectory(Path.Combine(Recordings.Path, folder)).FullName, name, startedUtc);
        WriteAudio(files.Segment(1), audioBytes);
        foreach (var extra in extras) File.WriteAllText(Path.Combine(files.Folder, files.BaseName + extra), $"{extra} of {files.BaseName}\n");
        return files;
    }

    /// <summary>Writes a .wav of <paramref name="audioBytes"/> bytes of audio at 8 kHz, a steady rise so that no two pieces are alike, with the recorder's own writer.</summary>
    public static void WriteAudio(string path, int audioBytes)
    {
        var samples = new short[audioBytes / 2];
        for (var i = 0; i < samples.Length; i++) samples[i] = (short)(i % 30000);
        File.Delete(path);
        TestAudio.WriteWav(path, 8000, samples);
    }

    /// <summary>Closes the log, so that it lets go of any file it writes, and deletes the folders.</summary>
    public void Dispose()
    {
        _recorderLog?.Dispose();
        Runtime.Dispose();
        Downloads.Dispose();
        Recordings.Dispose();
    }
}

/// <summary>Reads JSON written by the code under test, for assertions.</summary>
public static class JsonText
{
    /// <summary>The JSON object in <paramref name="text"/>.</summary>
    public static JsonObject Object(string text) => JsonNode.Parse(text)!.AsObject();

    /// <summary>The JSON object in the file at <paramref name="path"/>.</summary>
    public static JsonObject File(string path) => Object(System.IO.File.ReadAllText(path));

    /// <summary>Whether <paramref name="text"/> is valid JSON.</summary>
    public static bool IsJson(string text)
    {
        try
        {
            JsonDocument.Parse(text).Dispose();
            return true;
        }
        catch (JsonException)
        {
            // Deliberately not rethrown: the answer is whether the text is JSON.
            return false;
        }
    }
}
