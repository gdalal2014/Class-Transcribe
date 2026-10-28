using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClassScribe.Drive;

/// <summary>What Drive said to a piece of a resumable upload, or to the question how much of it Drive has.</summary>
internal enum PieceOutcome
{
    /// <summary>Drive wants more (HTTP 308), from the end of what it confirms it has.</summary>
    More,

    /// <summary>The file is complete (HTTP 200 or 201).</summary>
    Complete,

    /// <summary>The session is gone (HTTP 404 or 410): it expired, after a week at most, and the upload starts again from the beginning.</summary>
    Gone
}

/// <summary>Drive's answer to a piece of a resumable upload.</summary>
/// <param name="Outcome">What it said.</param>
/// <param name="Confirmed">How many bytes from the start Drive confirms it has, for <see cref="PieceOutcome.More"/>.</param>
/// <param name="File">The file, for <see cref="PieceOutcome.Complete"/>: at least its id.</param>
internal sealed record PieceAnswer(PieceOutcome Outcome, long Confirmed, DriveFile? File);

/// <summary>
/// The calls to Drive, through <see cref="IDriveApi"/>: each with the access token from <see cref="GoogleTokens"/> as its
/// bearer, within a limit of its own (a minute, or longer for a piece of an upload); when Drive refuses the token (HTTP 401),
/// it is renewed once and the call made again. Google's refusals become a <see cref="GoogleDriveException"/> whose message
/// says what to do: <see cref="DriveProblem.Busy"/> for an answer worth asking again after a wait (408, 429, 5xx, a rate
/// limit), <see cref="DriveProblem.Full"/> for a Drive that takes nothing more for now, the Drive API left disabled in the
/// Google Cloud project, and any other refusal with Google's own words; Google out of reach is one of
/// <see cref="DriveProblem.Unreachable"/>; the caller's own cancel is rethrown. Neither the token nor the address of an upload
/// session is ever logged. Each call goes to the version of the API the run's Drive:ApiVersion gives (architecture 7.3). One
/// per run, as the Refit client is.
/// </summary>
public sealed class DriveClient(IDriveApi api, GoogleTokens tokens, IOptionsSnapshot<DriveOptions> options, TimeProvider time, ILogger<DriveClient> logger)
{
    /// <summary>How long one call may take, the answer included.</summary>
    public static readonly TimeSpan CallLimit = TimeSpan.FromSeconds(60);

    /// <summary>The version of the Drive API the calls go to, from the run's settings.</summary>
    private string Version => options.Value.ApiVersion;

    /// <summary>What is said when the Drive API is not enabled in the Google Cloud project.</summary>
    public const string ApiDisabled =
        "Google Drive answered that the Drive API is not enabled in your Google Cloud project. Open https://console.cloud.google.com/apis/library/drive.googleapis.com in that project, click Enable, wait a minute, then try again.";

    /// <summary>The status of Drive's answer that asks for more of a resumable upload, Resume Incomplete.</summary>
    private const HttpStatusCode ResumeIncomplete = (HttpStatusCode)308;

    /// <summary>The reasons of a 403 that mean "wait a moment and ask again".</summary>
    private static readonly HashSet<string> RateLimits = new(StringComparer.Ordinal) { "userRateLimitExceeded", "rateLimitExceeded" };

    /// <summary>The reasons of a 403 that mean a limit of the day is reached, so Drive takes nothing more until tomorrow.</summary>
    private static readonly HashSet<string> DailyLimits = new(StringComparer.Ordinal) { "uploadLimitExceeded", "dailyLimitExceeded" };

    /// <summary>The statuses that mean "wait a moment and ask again".</summary>
    private static readonly HashSet<HttpStatusCode> BusyStatuses =
    [
        HttpStatusCode.RequestTimeout, HttpStatusCode.TooManyRequests, HttpStatusCode.InternalServerError, HttpStatusCode.BadGateway,
        HttpStatusCode.ServiceUnavailable, HttpStatusCode.GatewayTimeout
    ];

    /// <summary>The account and its storage (about.get).</summary>
    public async Task<DriveAbout> GetAboutAsync(CancellationToken cancellationToken)
    {
        using var response = await CallAsync((authorization, token) => api.GetAboutAsync(authorization, Version, token), "show the account", CallLimit, cancellationToken);
        return await ReadAsync(response, DriveJson.Default.DriveAbout, "show the account", cancellationToken);
    }

    /// <summary>The file or folder <paramref name="fileId"/>, with its size and MD5 and whether it is in the trash; null when Drive does not have it (HTTP 404).</summary>
    public async Task<DriveFile?> GetFileAsync(string fileId, CancellationToken cancellationToken)
    {
        using var response = await CallAsync((authorization, token) => api.GetAsync(authorization, Version, fileId, token), "show a file", CallLimit, cancellationToken,
            HttpStatusCode.NotFound);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;

        return await ReadAsync(response, DriveJson.Default.DriveFile, "show a file", cancellationToken);
    }

    /// <summary>Every file and folder <paramref name="query"/> finds, page after page, among those ClassScribe made.</summary>
    public async Task<IReadOnlyList<DriveFile>> ListAsync(string query, CancellationToken cancellationToken)
    {
        List<DriveFile> found = [];
        string? page = null;
        do
        {
            var next = page;
            using var response = await CallAsync((authorization, token) => api.ListAsync(authorization, Version, query, next, token), "search ClassScribe's files", CallLimit,
                cancellationToken);
            var list = await ReadAsync(response, DriveJson.Default.DriveFileList, "search ClassScribe's files", cancellationToken);
            found.AddRange(list.Files ?? []);
            page = list.NextPageToken;
        }
        while (!string.IsNullOrEmpty(page));

        return found;
    }

    /// <summary>Makes the folder <paramref name="folder"/> describes, and returns it as Drive made it.</summary>
    public async Task<DriveFile> CreateFolderAsync(DriveFileMetadata folder, CancellationToken cancellationToken)
    {
        using var response = await CallAsync((authorization, token) => api.CreateFolderAsync(authorization, Version, folder, token), "make a folder", CallLimit, cancellationToken);
        return await ReadAsync(response, DriveJson.Default.DriveFile, "make a folder", cancellationToken);
    }

    /// <summary>
    /// Starts the resumable upload of a new file described by <paramref name="metadata"/>, of <paramref name="length"/> bytes
    /// of <paramref name="contentType"/>, and returns the session's address as Drive gave it, for the caller to check. It is
    /// never logged: its upload id is what lets anyone send to the session.
    /// </summary>
    public async Task<string> StartUploadAsync(DriveFileMetadata metadata, string contentType, long length, CancellationToken cancellationToken)
    {
        using var response = await CallAsync((authorization, token) => api.StartUploadAsync(authorization, Version, contentType, length, metadata, token), "start an upload",
            CallLimit, cancellationToken);
        return SessionAddress(response, "start an upload");
    }

    /// <summary>
    /// Starts the resumable upload of a new revision of ClassScribe's own file <paramref name="fileId"/>, of
    /// <paramref name="length"/> bytes of <paramref name="contentType"/>, and returns the session's address as
    /// <see cref="StartUploadAsync"/> does. The file keeps its id, its name and its earlier revisions.
    /// </summary>
    public async Task<string> StartRevisionAsync(string fileId, string contentType, long length, CancellationToken cancellationToken)
    {
        var unchanged = new DriveFileMetadata(Name: null, MimeType: null, Parents: null, AppProperties: null);
        using var response = await CallAsync((authorization, token) => api.StartRevisionAsync(authorization, Version, fileId, contentType, length, unchanged, token),
            "start a new revision of a file", CallLimit, cancellationToken);
        return SessionAddress(response, "start a new revision of a file");
    }

    /// <summary>
    /// Sends the piece <paramref name="piece"/> makes, which carries its own Content-Range, to the session
    /// <paramref name="session"/>, within <paramref name="limit"/>, and says what Drive answered: more wanted from where it
    /// confirms, the file complete, or the session gone. An empty piece with Content-Range bytes */size asks how much Drive
    /// has. <paramref name="piece"/> is called again for a call repeated with a renewed token.
    /// </summary>
    internal async Task<PieceAnswer> SendPieceAsync(UploadAddress session, Func<HttpContent> piece, TimeSpan limit, CancellationToken cancellationToken)
    {
        const string what = "take a piece of an upload";
        using var response = await CallAsync((authorization, token) => session.FileId is { } fileId
                ? api.SendRevisionAsync(authorization, Version, fileId, session.UploadId, piece(), token)
                : api.SendAsync(authorization, Version, session.UploadId, piece(), token),
            what, limit, cancellationToken, ResumeIncomplete, HttpStatusCode.NotFound, HttpStatusCode.Gone);

        return response.StatusCode switch
        {
            ResumeIncomplete => new PieceAnswer(PieceOutcome.More, ConfirmedBy(response), null),
            HttpStatusCode.NotFound or HttpStatusCode.Gone => new PieceAnswer(PieceOutcome.Gone, 0, null),
            _ => new PieceAnswer(PieceOutcome.Complete, 0, await ReadAsync(response, DriveJson.Default.DriveFile, what, cancellationToken))
        };
    }

    /// <summary>
    /// How many bytes from the start a 308 confirms: its Range header, bytes=0-N, gives N + 1; none means Drive has nothing
    /// yet. A Range that does not start at 0, or cannot be read, counts as nothing, so the upload sends from the start.
    /// </summary>
    internal static long ConfirmedBy(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Range", out var values)) return 0;

        var range = values.FirstOrDefault()?.Trim() ?? "";
        const string prefix = "bytes=0-";
        return range.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && long.TryParse(range[prefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var last)
            ? last + 1
            : 0;
    }

    /// <summary>The Location header of the answer that started a session; a refusal when there is none.</summary>
    private string SessionAddress(HttpResponseMessage response, string what)
    {
        if (response.Headers.Location is { } location) return location.OriginalString;

        logger.LogDebug("Google Drive did not give a session address when asked to {What} (HTTP {Status})", what, (int)response.StatusCode);
        throw new GoogleDriveException(DriveProblem.Refused, $"Google Drive did not say where to send the file when asked to {what}.");
    }

    /// <summary>
    /// Makes <paramref name="call"/> with the access token, within <paramref name="limit"/>, and once more with a renewed
    /// token when Drive refuses it. Returns the answer when it succeeded, or has a status in <paramref name="accepted"/>;
    /// otherwise throws Google's refusal, in words, with <paramref name="what"/> was asked.
    /// </summary>
    private async Task<HttpResponseMessage> CallAsync(Func<string, CancellationToken, Task<HttpResponseMessage>> call, string what, TimeSpan limit,
        CancellationToken cancellationToken, params HttpStatusCode[] accepted)
    {
        var token = await tokens.GetAccessTokenAsync(cancellationToken);
        var response = await SendAsync(call, token, limit, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            response.Dispose();
            logger.LogDebug("Google Drive refused the access token; renewing it");
            token = await tokens.RenewAsync(token, cancellationToken);
            response = await SendAsync(call, token, limit, cancellationToken);
        }

        if (response.IsSuccessStatusCode || accepted.Contains(response.StatusCode)) return response;

        using (response)
        {
            var refusal = await RefusalAsync(response, what, cancellationToken);
            logger.LogDebug("Google Drive refused to {What} (HTTP {Status}): {Message}", what, (int)response.StatusCode, refusal.Message);
            throw refusal;
        }
    }

    /// <summary>Makes <paramref name="call"/> once with <paramref name="token"/>, within <paramref name="limit"/>.</summary>
    private async Task<HttpResponseMessage> SendAsync(Func<string, CancellationToken, Task<HttpResponseMessage>> call, string token, TimeSpan limit,
        CancellationToken cancellationToken)
    {
        using var timer = new CancellationTokenSource(limit, time);
        using var limited = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timer.Token);
        try
        {
            var response = await call($"Bearer {token}", limited.Token);
            await response.Content.LoadIntoBufferAsync(limited.Token);
            return response;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            var seconds = (int)Math.Ceiling(limit.TotalSeconds);
            logger.LogDebug("Google Drive did not answer within {Seconds} seconds", seconds);
            throw new GoogleDriveException(DriveProblem.Unreachable, $"Google Drive could not be reached (no answer within {seconds} seconds).", ex);
        }
        catch (HttpRequestException ex)
        {
            logger.LogDebug("Google Drive could not be reached: {Reason}", ex.Message);
            throw new GoogleDriveException(DriveProblem.Unreachable, $"Google Drive could not be reached ({ex.Message.TrimEnd('.')}).", ex);
        }
    }

    /// <summary>The body of <paramref name="response"/> as <typeparamref name="T"/>; a body that is not throws a refusal that says so.</summary>
    private async Task<T> ReadAsync<T>(HttpResponseMessage response, JsonTypeInfo<T> type, string what, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        try
        {
            return JsonSerializer.Deserialize(body, type) ?? throw new JsonException("The answer was null.");
        }
        catch (JsonException ex)
        {
            logger.LogDebug(ex, "Google Drive's answer when asked to {What} could not be read", what);
            throw new GoogleDriveException(DriveProblem.Refused, $"Google Drive's answer when asked to {what} could not be read.", ex);
        }
    }

    /// <summary>Google's refusal of <paramref name="what"/>, in words, from the status and Google's error body, and what kind of refusal it is.</summary>
    private static async Task<GoogleDriveException> RefusalAsync(HttpResponseMessage response, string what, CancellationToken cancellationToken)
    {
        var status = (int)response.StatusCode;
        var error = Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var reason = error?.Errors?.FirstOrDefault(item => !string.IsNullOrEmpty(item.Reason))?.Reason;
        var words = error?.Message?.Trim();
        var detail = Detail(reason, words);
        var forbidden = response.StatusCode == HttpStatusCode.Forbidden;

        if (reason == "accessNotConfigured" || words?.Contains("has not been used in project", StringComparison.OrdinalIgnoreCase) == true)
            return new GoogleDriveException(DriveProblem.Refused, ApiDisabled);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
            return new GoogleDriveException(DriveProblem.Refused, $"Google Drive did not accept ClassScribe's sign-in (HTTP 401{detail}). Sign in again with menu choice 9 > S, or ClassScribe drive signin.");

        if (forbidden && reason == "storageQuotaExceeded")
            return new GoogleDriveException(DriveProblem.Full, $"Google Drive is full: the account's storage takes nothing more (HTTP 403{detail}). Make room in Google Drive, or add storage to the account, then upload again.");

        if (forbidden && reason is { } daily && DailyLimits.Contains(daily))
            return new GoogleDriveException(DriveProblem.Full, $"Google Drive takes no more uploads today (HTTP 403{detail}). Upload again tomorrow.");

        if (BusyStatuses.Contains(response.StatusCode) || (forbidden && reason is { } rate && RateLimits.Contains(rate)))
            return new GoogleDriveException(DriveProblem.Busy, $"Google Drive refused to {what} (HTTP {status}{detail}).");

        return new GoogleDriveException(DriveProblem.Refused, $"Google Drive refused to {what} (HTTP {status}{detail}).");
    }

    /// <summary>Google's reason and words, after a comma, as ", userRateLimitExceeded: User rate limit exceeded."; empty when there are none.</summary>
    private static string Detail(string? reason, string? words) => (reason, words) switch
    {
        ({ } r, { Length: > 0 } w) => $", {r}: {w}",
        ({ } r, _) => $", {r}",
        (null, { Length: > 0 } w) => $", {w}",
        _ => ""
    };

    /// <summary>Google's error in <paramref name="body"/>, or null for a body that is not one.</summary>
    private static GoogleError? Parse(string body)
    {
        try
        {
            return JsonSerializer.Deserialize(body, DriveJson.Default.GoogleErrorAnswer)?.Error;
        }
        catch (JsonException)
        {
            // Deliberately not rethrown: a refusal without Google's error body is still a refusal, given by its status alone.
            return null;
        }
    }
}
