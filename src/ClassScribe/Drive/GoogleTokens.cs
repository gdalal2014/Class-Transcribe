using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Drive;

/// <summary>
/// The access token of this process, held in memory only, never written anywhere, and the lock that lets one renewal run at
/// a time for the whole process. One for the process; each run reads and renews it through <see cref="GoogleTokens"/>. Each
/// process (the window, a recorder) has its own, made from the same stored refresh token.
/// </summary>
public sealed class GoogleTokenCache
{
    private readonly Lock _gate = new();

    /// <summary>The token, when it was obtained on the steady clock, and how long Google said it lasts; null when there is none.</summary>
    private (string Token, long ObtainedAt, TimeSpan Lifetime)? _current;

    /// <summary>Held while a renewal runs, so that callers at the same moment share one.</summary>
    internal SemaphoreSlim Renewal { get; } = new(1, 1);

    /// <summary>The token held, if it still has more than <paramref name="margin"/> to live on <paramref name="time"/>'s steady clock; null otherwise.</summary>
    internal string? Valid(TimeProvider time, TimeSpan margin)
    {
        lock (_gate)
            return _current is { } current && time.GetElapsedTime(current.ObtainedAt) < current.Lifetime - margin ? current.Token : null;
    }

    /// <summary>Holds <paramref name="token"/>, obtained now, for <paramref name="lifetime"/>.</summary>
    internal void Hold(string token, TimeSpan lifetime, TimeProvider time)
    {
        lock (_gate) _current = (token, time.GetTimestamp(), lifetime);
    }

    /// <summary>Lets the token go, as at a sign-out, or when a sign-in that gave it was not kept.</summary>
    internal void Forget()
    {
        lock (_gate) _current = null;
    }
}

/// <summary>
/// The access token a run sends to Drive, from this process's <see cref="GoogleTokenCache"/>: used while it has more than two
/// minutes left, renewed with the stored refresh token when it has less (grant_type=refresh_token), and renewed at once when
/// Drive refuses it ("refresh the access token using the long-lived refresh token", Google's guide to Drive's errors). One
/// renewal at a time for the process. When Google ends the sign-in (invalid_grant), the dead refresh token is removed from
/// the store and the client kept. The tokens, the client secret and Google's answer are never logged: only Google's error
/// and its description. One per run, so each run's Refit client follows Drive:OAuthBaseUrl as the settings are then.
/// </summary>
public sealed class GoogleTokens(GoogleTokenCache cache, IGoogleOAuthApi oauth, IGoogleCredentialStore store, TimeProvider time, ILogger<GoogleTokens> logger)
{
    /// <summary>A token with less than this left is renewed before it is used.</summary>
    public static readonly TimeSpan RenewBefore = TimeSpan.FromMinutes(2);

    /// <summary>How long one request to Google's token endpoint may take, the answer included.</summary>
    public static readonly TimeSpan CallLimit = TimeSpan.FromSeconds(60);

    /// <summary>What is said when ClassScribe has never been given a Google client.</summary>
    public const string NotSetUp =
        "Google Drive is not set up yet: ClassScribe has no Google client. Follow the README's \"Google Drive (set up once)\", then sign in with menu choice 9 > S, or ClassScribe drive signin --client \"<the client file you downloaded>\".";

    /// <summary>What is said when the client is kept but there is no sign-in.</summary>
    public const string NotSignedIn = "ClassScribe is not signed in to Google Drive. Sign in with menu choice 9 > S, or ClassScribe drive signin.";

    /// <summary>What is said when Google has ended the sign-in.</summary>
    public const string SignInEnded =
        "Google ended ClassScribe's sign-in to Google Drive: it was revoked, went unused for six months, or was made while the Google project was in Testing, which lasts 7 days. Sign in again with menu choice 9 > S, or ClassScribe drive signin.";

    /// <summary>
    /// A token Drive takes now: the one held while it has more than two minutes left, or a renewed one. Throws a
    /// <see cref="GoogleDriveException"/> that says what to do when there is no sign-in, when Google has ended it, refuses the
    /// client or cannot be reached.
    /// </summary>
    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken) =>
        cache.Valid(time, RenewBefore) ?? await RenewAsync(refused: null, cancellationToken);

    /// <summary>
    /// A new token, after Drive refused <paramref name="refused"/> (HTTP 401), or because none is held. When another caller
    /// renewed it meanwhile, that token is handed back without another request.
    /// </summary>
    public async Task<string> RenewAsync(string? refused, CancellationToken cancellationToken)
    {
        await cache.Renewal.WaitAsync(cancellationToken);
        try
        {
            if (cache.Valid(time, RenewBefore) is { } current && current != refused) return current;

            return await RequestTokenAsync(cancellationToken);
        }
        finally
        {
            cache.Renewal.Release();
        }
    }

    /// <summary>Holds the token a sign-in was just given, which lasts <paramref name="lifetime"/>, so its first requests to Drive use it.</summary>
    public void Seed(string accessToken, TimeSpan lifetime) => cache.Hold(accessToken, lifetime, time);

    /// <summary>Lets the token go, as at a sign-out.</summary>
    public void Forget() => cache.Forget();

    /// <summary>Asks Google for a new token with the stored client and refresh token, and holds it. Call with the renewal lock held.</summary>
    private async Task<string> RequestTokenAsync(CancellationToken cancellationToken)
    {
        var stored = store.Read();
        if (stored.Problem is { } problem) throw new GoogleDriveException(DriveProblem.StoreUnreadable, problem);
        if (stored.Credentials is not { } credentials) throw new GoogleDriveException(DriveProblem.NotSetUp, NotSetUp);
        if (!credentials.IsSignedIn) throw new GoogleDriveException(DriveProblem.NotSignedIn, NotSignedIn);

        Dictionary<string, string> form = new()
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = credentials.ClientId,
            ["client_secret"] = credentials.ClientSecret,
            ["refresh_token"] = credentials.RefreshToken!
        };

        var (status, answer) = await SendFormAsync(limit => oauth.TokenAsync(form, limit), "renew ClassScribe's sign-in",
            " The sign-in is kept; try again when the connection is back.", time, logger, cancellationToken);
        if (status == HttpStatusCode.OK && answer is { AccessToken: { Length: > 0 } token })
        {
            cache.Hold(token, TimeSpan.FromSeconds(Math.Max(0, answer.ExpiresIn ?? 3600)), time);
            logger.LogDebug("Renewed the access token for Google Drive");
            return token;
        }

        var error = Describe(answer);
        if (answer?.Error == "invalid_grant")
        {
            logger.LogDebug("Google ended ClassScribe's sign-in to Google Drive ({Error}); the dead sign-in is removed and the client kept", error);
            RemoveSignIn(credentials.RefreshToken!);
            throw new GoogleDriveException(DriveProblem.SignInEnded, SignInEnded);
        }

        if (answer?.Error == "invalid_client")
        {
            logger.LogDebug("Google refused ClassScribe's Google client ({Error})", error);
            throw new GoogleDriveException(DriveProblem.ClientRefused,
                $"Google no longer accepts ClassScribe's Google client ({error}): it was deleted, or its secret was replaced. Download the client's JSON again from Google Auth platform > Clients (Add secret, if the secret is gone), then sign in with it: ClassScribe drive signin --client \"<the file>\", or menu choice 9 > S.");
        }

        logger.LogDebug("Google refused to renew the access token for Google Drive (HTTP {Status}, {Error})", (int)status, error);
        throw new GoogleDriveException(DriveProblem.Refused, $"Google refused to renew ClassScribe's sign-in (HTTP {(int)status}, {error}). Try again later.");
    }

    /// <summary>
    /// Makes <paramref name="call"/> to one of Google's OAuth endpoints within <see cref="CallLimit"/>, and reads Google's
    /// answer, whatever its status; the form and the answer are never logged. Google out of reach, or silent until the limit,
    /// is a <see cref="GoogleDriveException"/> of <see cref="DriveProblem.Unreachable"/> that says Google could not be reached
    /// to <paramref name="doing"/>, then <paramref name="advice"/>; the caller's own cancel is rethrown.
    /// </summary>
    internal static async Task<(HttpStatusCode Status, GoogleTokenAnswer? Answer)> SendFormAsync(Func<CancellationToken, Task<HttpResponseMessage>> call, string doing,
        string advice, TimeProvider time, ILogger logger, CancellationToken cancellationToken)
    {
        using var limit = new CancellationTokenSource(CallLimit, time);
        using var limited = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, limit.Token);
        try
        {
            using var response = await call(limited.Token);
            var body = await response.Content.ReadAsStringAsync(limited.Token);
            return (response.StatusCode, Parse(body));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            logger.LogDebug("Google did not answer within {Seconds} seconds when asked to {Doing}", (int)CallLimit.TotalSeconds, doing);
            throw new GoogleDriveException(DriveProblem.Unreachable, $"Google could not be reached to {doing} (no answer within {(int)CallLimit.TotalSeconds} seconds).{advice}", ex);
        }
        catch (HttpRequestException ex)
        {
            logger.LogDebug("Google could not be reached to {Doing}: {Reason}", doing, ex.Message);
            throw new GoogleDriveException(DriveProblem.Unreachable, $"Google could not be reached to {doing} ({ex.Message.TrimEnd('.')}).{advice}", ex);
        }
    }

    /// <summary>
    /// Removes the sign-in whose refresh token Google refused, and keeps the client. A sign-in made meanwhile, by another
    /// process with another token, is left as it is.
    /// </summary>
    private void RemoveSignIn(string refusedToken)
    {
        if (store.Read().Credentials is { } now && now.RefreshToken == refusedToken) store.Write(now.SignedOut());
    }

    /// <summary>Google's answer from the token endpoint, or null for a body that is not one; the body is never logged, since it may hold tokens.</summary>
    internal static GoogleTokenAnswer? Parse(string body)
    {
        try
        {
            return JsonSerializer.Deserialize(body, GoogleOAuthJson.Default.GoogleTokenAnswer);
        }
        catch (JsonException)
        {
            // Deliberately not rethrown: an answer that is not Google's JSON is no answer; the caller reports the HTTP status.
            return null;
        }
    }

    /// <summary>Google's error and its description, as "invalid_grant: Token has been expired or revoked.", or "no error given".</summary>
    internal static string Describe(GoogleTokenAnswer? answer) => answer switch
    {
        { Error: { } error, ErrorDescription: { Length: > 0 } description } => $"{error}: {description}",
        { Error: { } error } => error,
        _ => "no error given"
    };
}
