using System.Text;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Drive;

/// <summary>
/// Google's answer to a code exchange or a revoke, as the sign-in reads it: the HTTP status, the tokens on success, and
/// Google's error with its description in words. As text it shows only which tokens are there and the error, never a token.
/// </summary>
/// <param name="Status">The HTTP status of the answer.</param>
/// <param name="AccessToken">The access token, which Drive takes for about an hour; null when the answer holds none.</param>
/// <param name="ExpiresIn">How many seconds the access token lasts.</param>
/// <param name="RefreshToken">The long-lived refresh token; only a code exchange gives one.</param>
/// <param name="Scope">The scopes granted, separated by spaces.</param>
/// <param name="Error">Google's error code on failure, such as invalid_grant; null when it gave none.</param>
/// <param name="Described">Google's error and its description, as "invalid_grant: Token has been expired or revoked.", or "no error given".</param>
public sealed record GoogleAnswer(int Status, string? AccessToken, int? ExpiresIn, string? RefreshToken, string? Scope, string? Error, string Described)
{
    /// <summary>The answer as text: its status, which tokens it holds and Google's error, never the tokens themselves.</summary>
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Status = ").Append(Status)
            .Append(", HasAccessToken = ").Append(AccessToken is not null)
            .Append(", HasRefreshToken = ").Append(RefreshToken is not null)
            .Append(", ExpiresIn = ").Append(ExpiresIn)
            .Append(", Scope = ").Append(Scope)
            .Append(", Error = ").Append(Error);
        return true;
    }
}

/// <summary>
/// Google's OAuth endpoints as the sign-in uses them (architecture 4.3): the code exchange at the end of a sign-in, the
/// revoke of a sign-out, and the access token of this process, seeded by a sign-in and let go at a sign-out. The renewal
/// of the access token is the adapter's own, for the calls to Drive. The forms hold secrets: neither they nor the answers
/// are ever logged. Google out of reach, or silent until the limit, is a <see cref="GoogleDriveException"/> of
/// <see cref="DriveProblem.Unreachable"/> that says what it could not be reached for; the caller's own cancel is rethrown.
/// </summary>
public interface IGoogleAccount
{
    /// <summary>What is said when ClassScribe has never been given a Google client.</summary>
    const string NotSetUp =
        "Google Drive is not set up yet: ClassScribe has no Google client. Follow the README's \"Google Drive (set up once)\", then sign in with menu choice 9 > S, or ClassScribe drive signin --client \"<the client file you downloaded>\".";

    /// <summary>What is said when the client is kept but there is no sign-in.</summary>
    const string NotSignedIn = "ClassScribe is not signed in to Google Drive. Sign in with menu choice 9 > S, or ClassScribe drive signin.";

    /// <summary>
    /// Exchanges <paramref name="code"/> for tokens, with <paramref name="client"/>, the PKCE <paramref name="verifier"/> and
    /// <paramref name="redirect"/> exactly as sent, and returns Google's answer whatever its status. A connection that fails
    /// is said at Debug through <paramref name="logger"/>, the caller's, before it is thrown.
    /// </summary>
    Task<GoogleAnswer> ExchangeCodeAsync(GoogleClient client, string code, string verifier, string redirect, ILogger logger, CancellationToken cancellationToken);

    /// <summary>
    /// Asks Google to end ClassScribe's access, every scope of it, by revoking <paramref name="refreshToken"/>, and returns
    /// Google's answer whatever its status. A connection that fails is said at Debug through <paramref name="logger"/>, the
    /// caller's, before it is thrown.
    /// </summary>
    Task<GoogleAnswer> RevokeAsync(string refreshToken, ILogger logger, CancellationToken cancellationToken);

    /// <summary>Holds the token a sign-in was just given, which lasts <paramref name="lifetime"/>, so its first requests to Drive use it.</summary>
    void Seed(string accessToken, TimeSpan lifetime);

    /// <summary>Lets the token go, as at a sign-out.</summary>
    void Forget();
}
