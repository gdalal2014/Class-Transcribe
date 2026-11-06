namespace ClassScribe.Drive;

/// <summary>What kept something about Google Drive from working, which decides what the person is told to do.</summary>
public enum DriveProblem
{
    /// <summary>No Google client has been imported yet, so ClassScribe has never been signed in.</summary>
    NotSetUp,

    /// <summary>The client is kept, but there is no sign-in: never made, or signed out.</summary>
    NotSignedIn,

    /// <summary>The kept client and sign-in cannot be read, such as one made by another Windows account.</summary>
    StoreUnreadable,

    /// <summary>Google ended the sign-in (invalid_grant): revoked, unused for six months, or made while the project was in Testing.</summary>
    SignInEnded,

    /// <summary>Google no longer accepts the client (invalid_client): deleted, or its secret replaced.</summary>
    ClientRefused,

    /// <summary>The client file given is not one ClassScribe can sign in with.</summary>
    ClientFile,

    /// <summary>The person did not allow ClassScribe on Google's consent page (access_denied).</summary>
    NotAllowed,

    /// <summary>Google's consent page granted no access to Google Drive: the box for it was left unticked.</summary>
    ScopeNotGranted,

    /// <summary>Google's answer did not come back to ClassScribe in time.</summary>
    TimedOut,

    /// <summary>Google could not be reached: no network, or no answer in time.</summary>
    Unreachable,

    /// <summary>
    /// Google answered that it is busy, for a moment: HTTP 408, 429, 500, 502, 503 or 504, or 403 with a rate limit as the
    /// reason. Asked again after a wait, it usually goes through.
    /// </summary>
    Busy,

    /// <summary>Google Drive takes nothing more for now: the account's storage is full, or the day's upload limit is reached.</summary>
    Full,

    /// <summary>Google answered, and refused: an error in its answer says why.</summary>
    Refused
}

/// <summary>
/// A failure about Google Drive, with a message in plain words for the person at the computer that says what happened and
/// what to do. The message never holds a secret: no client secret, token, code, verifier or state.
/// </summary>
/// <param name="problem">What kind of failure it is.</param>
/// <param name="message">What happened and what to do, in plain words.</param>
/// <param name="inner">The failure underneath, if any.</param>
public sealed class GoogleDriveException(DriveProblem problem, string message, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>What kind of failure it is.</summary>
    public DriveProblem Problem { get; } = problem;
}
