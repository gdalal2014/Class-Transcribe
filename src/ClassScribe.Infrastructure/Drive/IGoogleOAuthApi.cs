using Refit;

namespace ClassScribe.Drive;

/// <summary>
/// Google's OAuth endpoints (base https://oauth2.googleapis.com/, Drive:OAuthBaseUrl), declared for Refit: the token
/// endpoint, for the code exchange and for each refresh, and the revoke endpoint, for a sign-out. Every answer comes back
/// whatever its status, so that Google's error is read rather than thrown. The forms hold secrets: neither they nor the
/// answers are ever logged, and the client's HTTP logging is removed (see <see cref="DriveServices"/>).
/// </summary>
public interface IGoogleOAuthApi
{
    /// <summary>
    /// POST /token with a URL-encoded form: grant_type=authorization_code with the code, the client, the redirect address
    /// and the PKCE verifier; or grant_type=refresh_token with the client and the refresh token.
    /// </summary>
    [Post("/token")]
    Task<HttpResponseMessage> TokenAsync([Body(BodySerializationMethod.UrlEncoded)] Dictionary<string, string> form, CancellationToken cancellationToken);

    /// <summary>POST /revoke with the token in a URL-encoded form: Google then ends ClassScribe's access, every scope of it.</summary>
    [Post("/revoke")]
    Task<HttpResponseMessage> RevokeAsync([Body(BodySerializationMethod.UrlEncoded)] Dictionary<string, string> form, CancellationToken cancellationToken);
}
