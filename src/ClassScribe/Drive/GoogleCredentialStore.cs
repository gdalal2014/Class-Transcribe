using System.Text;
using System.Text.Json.Serialization;

namespace ClassScribe.Drive;

/// <summary>
/// What ClassScribe keeps of Google: the client it signs in with, and, while it is signed in, the refresh token, the account's
/// email, when it signed in and the scope Google granted. Kept encrypted for the Windows account (see
/// <see cref="DpapiGoogleCredentialStore"/>), never in the settings, a log, a run file or a test. As text it shows the end of
/// the client id, whether it is signed in and as whom, never the secret or the token.
/// </summary>
/// <param name="ClientId">The client id.</param>
/// <param name="ClientSecret">The client secret.</param>
public sealed record GoogleCredentials(string ClientId, string ClientSecret)
{
    /// <summary>The refresh token of the sign-in; null when not signed in.</summary>
    public string? RefreshToken { get; init; }

    /// <summary>The email address of the Google account signed in; null when not signed in.</summary>
    public string? Email { get; init; }

    /// <summary>When the sign-in was made, in UTC; null when not signed in.</summary>
    public DateTimeOffset? SignedInUtc { get; init; }

    /// <summary>The scopes Google granted, separated by spaces; null when not signed in.</summary>
    public string? Scope { get; init; }

    /// <summary>True while there is a sign-in: a refresh token.</summary>
    [JsonIgnore]
    public bool IsSignedIn => !string.IsNullOrEmpty(RefreshToken);

    /// <summary>The client these credentials sign in with.</summary>
    [JsonIgnore]
    public GoogleClient Client => new(ClientId, ClientSecret);

    /// <summary>The same client without the sign-in, as after a sign-out or once Google has ended the sign-in.</summary>
    public GoogleCredentials SignedOut() => this with { RefreshToken = null, Email = null, SignedInUtc = null, Scope = null };

    /// <summary>The credentials as text, which a log line might hold by mistake: the end of the client id, whether signed in, and as whom.</summary>
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("ClientId = ").Append(GoogleClient.Describe(ClientId))
            .Append(", IsSignedIn = ").Append(IsSignedIn)
            .Append(", Email = ").Append(Email);
        return true;
    }
}

/// <summary>What reading the store found: the credentials, nothing (not set up), or a store that cannot be read, with why.</summary>
/// <param name="Credentials">What is stored; null when nothing is, or when it cannot be read.</param>
/// <param name="Problem">Why the store cannot be read, with what to do, in plain words; null when it can.</param>
public sealed record StoredCredentials(GoogleCredentials? Credentials, string? Problem)
{
    /// <summary>Nothing stored: ClassScribe has never been given a Google client.</summary>
    public static StoredCredentials None { get; } = new(null, null);

    /// <summary>A client is kept, so Google Drive is set up, signed in or not.</summary>
    public bool IsSetUp => Credentials is not null;

    /// <summary>What was read: <paramref name="credentials"/>.</summary>
    public static StoredCredentials Of(GoogleCredentials credentials) => new(credentials, null);

    /// <summary>A store that cannot be read, for <paramref name="problem"/>; it counts as no sign-in.</summary>
    public static StoredCredentials Unreadable(string problem) => new(null, problem);
}

/// <summary>
/// Where the Google client and sign-in are kept. One file, written whole or not at all. Tests use an in-memory store, and
/// never the real %APPDATA%\ClassScribe.
/// </summary>
public interface IGoogleCredentialStore
{
    /// <summary>The file that holds them, for messages.</summary>
    string Location { get; }

    /// <summary>
    /// Reads what is kept: the credentials, nothing, or, for a store that cannot be read, the reason, which counts as no
    /// sign-in. Never throws for the store's own state.
    /// </summary>
    StoredCredentials Read();

    /// <summary>Keeps <paramref name="credentials"/> in place of whatever was kept, whole or not at all.</summary>
    void Write(GoogleCredentials credentials);
}
