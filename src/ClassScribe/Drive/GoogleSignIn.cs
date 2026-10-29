using System.Net;
using ClassScribe.Commands;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClassScribe.Drive;

/// <summary>A sign-in that went through: the Google account, and ClassScribe's folder in its My Drive.</summary>
/// <param name="Email">The account's email address.</param>
/// <param name="Folder">ClassScribe's folder in My Drive, found or made.</param>
public sealed record SignedIn(string Email, DriveFolder Folder);

/// <summary>How a sign-out ended. In every case but the first, the sign-in is forgotten here and the client kept.</summary>
public enum SignOutOutcome
{
    /// <summary>There was no sign-in to end; nothing was asked or changed.</summary>
    WasNotSignedIn,

    /// <summary>Google revoked ClassScribe's access.</summary>
    Revoked,

    /// <summary>Google had already ended the access (the token was no longer valid).</summary>
    AlreadyEnded,

    /// <summary>Google could not be reached, or refused: the access may still stand, and the person is told how to end it.</summary>
    NotRevoked
}

/// <summary>
/// Signs ClassScribe in to Google Drive, and out, in the window only, never in a recorder. The sign-in is OAuth 2.0 for a
/// desktop app as Google's guide gives it: a listener on http://127.0.0.1:&lt;free port&gt;/ (<see cref="ILoopbackReceiver"/>),
/// Google's sign-in opened in the browser with PKCE S256, the drive.file scope alone and prompt=consent; a wait of up to 5
/// minutes for Google's redirect with this sign-in's state (compared in constant time; any other request is refused and the
/// wait goes on; Esc or Ctrl+C gives up); the code exchanged with the verifier and the same redirect address; drive.file
/// required in what was granted; Drive asked who the account is, and ClassScribe's folder found or made, before the sign-in
/// is kept. The client is imported from the file Google offers and kept as soon as it is read, so a second try needs no file.
/// The address goes to the window only, and no secret (the client secret, the code, the verifier, the state, a token) is
/// ever logged. Google's sign-in page is Drive:AuthorizationUrl, of the run's settings (architecture 7.2). One per run.
/// </summary>
public sealed class GoogleSignIn(IGoogleAccount account, IGoogleCredentialStore store, IGoogleDrive drive, IGoogleClientFiles clientFiles,
    ILoopbackReceiver receiver, IBrowser browser, IConsoleUi ui, IOptionsSnapshot<DriveOptions> options, TimeProvider time, ILogger<GoogleSignIn> logger)
{
    /// <summary>The only scope ClassScribe asks for: the files and folders it makes itself, nothing else in the Drive.</summary>
    public const string DriveFileScope = "https://www.googleapis.com/auth/drive.file";

    /// <summary>How long the sign-in waits for Google's answer.</summary>
    public static readonly TimeSpan WaitLimit = TimeSpan.FromMinutes(5);

    /// <summary>What is said when the person did not allow ClassScribe on Google's page.</summary>
    public const string NotAllowed = "You did not allow ClassScribe to use Google Drive; no sign-in was stored.";

    /// <summary>What is said when Google granted no access to Drive.</summary>
    public const string ScopeNotGranted =
        "Google did not give ClassScribe access to Google Drive: the box for it was left unticked on Google's page. No sign-in was stored; sign in again and tick the box that lets ClassScribe \"See, edit, create, and delete only the specific Google Drive files you use with this app\".";

    /// <summary>What is said when Google's answer did not come in time.</summary>
    public const string TimedOut = "Google's answer did not reach ClassScribe within 5 minutes, so the sign-in was given up; no sign-in was stored. Sign in again and finish in the browser within 5 minutes.";

    /// <summary>What is logged when the person gives up with Esc or Ctrl+C.</summary>
    public const string GivenUp = "The sign-in was given up; no sign-in was stored.";

    /// <summary>What is said when there is no client to sign in with.</summary>
    public const string NoClient =
        "ClassScribe has no Google client yet, so it cannot sign in. Give it the client file you downloaded from Google: ClassScribe drive signin --client \"<the file>\", or menu choice 9 > S. The README's \"Google Drive (set up once)\" says how to make one.";

    /// <summary>What is said when Google's answer holds no refresh token.</summary>
    public const string NoRefreshToken =
        "Google did not give ClassScribe a lasting sign-in (its answer had no refresh token), so no sign-in was stored. Sign in again; if it happens again, remove ClassScribe's access at https://myaccount.google.com/connections and sign in once more.";

    /// <summary>What is said after a sign-out that Google confirmed.</summary>
    public const string SignedOut =
        "Signed out of Google Drive: Google has ended ClassScribe's access, and the sign-in is forgotten here. The files on Drive stay as they are, and the Google client is kept, so signing in again needs no file.";

    /// <summary>What is said after a sign-out of a sign-in Google had already ended.</summary>
    public const string SignedOutAlreadyEnded =
        "Signed out of Google Drive: the sign-in is forgotten here, and Google had already ended ClassScribe's access. The files on Drive stay as they are, and the Google client is kept, so signing in again needs no file.";

    /// <summary>How to end ClassScribe's access by hand, when Google could not be asked to.</summary>
    private const string EndItYourself =
        "To end it yourself, open https://myaccount.google.com/connections (Google Account > Security > Third-party apps and services), choose ClassScribe and remove its access. The files on Drive stay as they are, and the Google client is kept.";

    /// <summary>
    /// Signs in: with the client in <paramref name="clientFile"/>, imported and kept first, or with the client kept from
    /// before when it is null. Returns the account and ClassScribe's folder. Throws a <see cref="GoogleDriveException"/> that
    /// says what happened and what to do when the sign-in does not go through, in which case no new sign-in is kept, and an
    /// <see cref="OperationCanceledException"/> when the person gives up (Esc, Ctrl+C) or <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    public async Task<SignedIn> SignInAsync(string? clientFile, CancellationToken cancellationToken)
    {
        var client = ClientFor(clientFile);
        var verifier = Pkce.NewVerifier();
        var state = Pkce.NewState();

        string code;
        string redirect;
        using (var listener = receiver.Open())
        {
            redirect = listener.RedirectUri.AbsoluteUri;
            var address = AuthorizationAddress(options.Value.AuthorizationUrl, client.Id, redirect, Pkce.ChallengeFor(verifier), state);

            // The address holds the sign-in's state: it goes to the window, never to the log.
            ui.WriteLine("Opening Google's sign-in in your browser...");
            ui.WriteLine(browser.Open(address)
                ? "If the browser did not open, copy this address into it:"
                : "The browser did not open by itself. Copy this address into your browser:");
            ui.WriteLine(address.AbsoluteUri);
            ui.WriteLine($"Waiting for Google (up to {(int)WaitLimit.TotalMinutes} minutes; Esc gives up).");
            logger.LogDebug("Waiting for Google's answer on {Address}", redirect);

            code = await WaitForCodeAsync(listener, state, cancellationToken);
        }

        var answer = await ExchangeAsync(client, code, verifier, redirect, cancellationToken);
        if (answer.RefreshToken is not { Length: > 0 } refreshToken)
        {
            logger.LogDebug("Google's answer to the sign-in held no refresh token; no sign-in was stored");
            throw new GoogleDriveException(DriveProblem.Refused, NoRefreshToken);
        }

        if (!Granted(answer.Scope))
        {
            logger.LogDebug("Google granted {Scope} without Google Drive; no sign-in was stored", answer.Scope ?? "nothing");
            throw new GoogleDriveException(DriveProblem.ScopeNotGranted, ScopeNotGranted);
        }

        return await KeepAsync(client, answer, refreshToken, cancellationToken);
    }

    /// <summary>
    /// Signs out: asks Google to revoke the refresh token, then forgets it here and keeps the client, whatever Google answers.
    /// Nothing on Drive is touched. Says how it ended, and when Google could not be asked, how to end the access by hand.
    /// </summary>
    public async Task<SignOutOutcome> SignOutAsync(CancellationToken cancellationToken)
    {
        if (store.Read().Credentials is not { IsSignedIn: true } credentials)
        {
            logger.LogInformation("ClassScribe is not signed in to Google Drive; there is nothing to sign out of.");
            return SignOutOutcome.WasNotSignedIn;
        }

        SignOutOutcome outcome;
        string? failure = null;
        try
        {
            var answer = await account.RevokeAsync(credentials.RefreshToken!, logger, cancellationToken);
            outcome = answer.Status == (int)HttpStatusCode.OK ? SignOutOutcome.Revoked
                : answer.Error is "invalid_token" or "invalid_grant" ? SignOutOutcome.AlreadyEnded
                : SignOutOutcome.NotRevoked;
            if (outcome == SignOutOutcome.NotRevoked) failure = $"Google refused to end ClassScribe's access (HTTP {answer.Status}, {answer.Described}).";
        }
        catch (GoogleDriveException ex) when (ex.Problem == DriveProblem.Unreachable)
        {
            // Deliberately not rethrown: the sign-in is forgotten here whatever Google answers, and the warning below says how
            // to end the access by hand.
            outcome = SignOutOutcome.NotRevoked;
            failure = ex.Message;
        }

        store.Write(credentials.SignedOut());
        account.Forget();

        switch (outcome)
        {
            case SignOutOutcome.Revoked:
                logger.LogInformation(SignedOut);
                break;
            case SignOutOutcome.AlreadyEnded:
                logger.LogInformation(SignedOutAlreadyEnded);
                break;
            default:
                logger.LogWarning("Signed out here, but {Failure} {Advice}", failure, EndItYourself);
                break;
        }

        return outcome;
    }

    /// <summary>
    /// The client to sign in with: the one in <paramref name="clientFile"/>, imported and kept at once (a client file of the
    /// same client only brings its secret, and keeps the sign-in; another client's replaces the kept client and its sign-in),
    /// or else the one kept from before.
    /// </summary>
    private GoogleClient ClientFor(string? clientFile)
    {
        if (clientFile is null)
        {
            var read = store.Read();
            if (read.Problem is { } problem) throw new GoogleDriveException(DriveProblem.StoreUnreadable, problem);
            return read.Credentials?.Client ?? throw new GoogleDriveException(DriveProblem.NotSetUp, NoClient);
        }

        GoogleClient client;
        try
        {
            client = clientFiles.Read(clientFile);
        }
        catch (GoogleDriveException ex)
        {
            logger.LogDebug("The client file cannot be used: {Reason}", ex.Message);
            throw;
        }

        var kept = store.Read().Credentials;
        if (kept is not null && kept.ClientId == client.Id)
        {
            if (kept.ClientSecret != client.Secret) store.Write(kept with { ClientSecret = client.Secret });
        }
        else
        {
            store.Write(new GoogleCredentials(client.Id, client.Secret));
        }

        logger.LogInformation("Took the Google client {Client} from {Path}. ClassScribe keeps it, encrypted for your Windows account, in {Store}; the downloaded file can be deleted.",
            client.ShortId, clientFile, store.Location);
        return client;
    }

    /// <summary>
    /// Waits for Google's redirect with <paramref name="state"/>, up to <see cref="WaitLimit"/>, and returns its code. Any
    /// other request is refused and the wait goes on. Esc or Ctrl+C in the window gives up, as a cancel.
    /// </summary>
    private async Task<string> WaitForCodeAsync(ILoopbackListener listener, string state, CancellationToken cancellationToken)
    {
        using var limit = new CancellationTokenSource(WaitLimit, time);
        using var giveUp = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, limit.Token);
        var pressed = false;

        // Watches the keyboard while the wait lasts: Esc or Ctrl+C gives up; other keys are ignored. When nobody can type, it ends at once.
        async Task WatchKeysAsync()
        {
            while (await ui.ReadKeyAsync(giveUp.Token) is { } key)
            {
                if (key.Key != ConsoleKey.Escape && !(key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control))) continue;

                Volatile.Write(ref pressed, true);
                await giveUp.CancelAsync();
                return;
            }
        }

        var keys = WatchKeysAsync();
        try
        {
            while (true)
            {
                ILoopbackRequest request;
                try
                {
                    request = await listener.NextAsync(giveUp.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    if (Volatile.Read(ref pressed))
                    {
                        logger.LogInformation(GivenUp);
                        throw;
                    }

                    logger.LogDebug("Google's answer did not come within {Minutes} minutes; the sign-in was given up", (int)WaitLimit.TotalMinutes);
                    throw new GoogleDriveException(DriveProblem.TimedOut, TimedOut);
                }

                if (await JudgeAsync(request, state) is { } code) return code;
            }
        }
        finally
        {
            await giveUp.CancelAsync();
            await keys;
        }
    }

    /// <summary>
    /// What a request to the listener means: Google's redirect with this sign-in's state and a code, which is returned; or
    /// with an error, which ends the sign-in; anything else is refused, and null lets the wait go on. Each is answered.
    /// </summary>
    private async Task<string?> JudgeAsync(ILoopbackRequest request, string state)
    {
        if (request.Path != "/")
        {
            await AnswerAsync(request, 404, "Nothing is here.");
            return null;
        }

        if (!Pkce.SameState(request.Query("state"), state))
        {
            logger.LogDebug("A request without this sign-in's state reached the listener; it was refused");
            await AnswerAsync(request, 400, "This is not the answer ClassScribe is waiting for. Go back to ClassScribe's window.");
            return null;
        }

        if (request.Query("error") is { } error)
        {
            if (error == "access_denied")
            {
                await AnswerAsync(request, 200, "ClassScribe was not allowed to use Google Drive, and nothing was stored. You can close this tab and go back to ClassScribe.");
                logger.LogDebug("The sign-in was not allowed on Google's page (access_denied)");
                throw new GoogleDriveException(DriveProblem.NotAllowed, NotAllowed);
            }

            var shown = error.Length > 60 ? error[..60] : error;
            await AnswerAsync(request, 200, "Google ended the sign-in with an error. You can close this tab and go back to ClassScribe, which says what to do.");
            logger.LogDebug("Google ended the sign-in with an error ({Error})", shown);
            throw new GoogleDriveException(DriveProblem.Refused, $"Google ended the sign-in with an error ({shown}); no sign-in was stored. Sign in again; if it happens again, check the client in Google Auth platform > Clients.");
        }

        if (request.Query("code") is { Length: > 0 } code)
        {
            await AnswerAsync(request, 200, "ClassScribe has Google's answer and is finishing the sign-in to Google Drive. You can close this tab and go back to ClassScribe.");
            return code;
        }

        await AnswerAsync(request, 400, "This is not the answer ClassScribe is waiting for. Go back to ClassScribe's window.");
        return null;
    }

    /// <summary>Answers the browser with a small page of its own that says <paramref name="text"/>; a browser that has gone does not matter.</summary>
    private async Task AnswerAsync(ILoopbackRequest request, int status, string text)
    {
        try
        {
            await request.AnswerAsync(status, Page(text));
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // Deliberately not rethrown: the tab was closed before the page arrived (the listener's adapter says so as an
            // IOException); the page only says to go back to ClassScribe, whose window says how the sign-in ended.
            logger.LogDebug(ex, "The browser did not take the page");
        }
    }

    /// <summary>
    /// Exchanges <paramref name="code"/> for tokens, with the verifier and <paramref name="redirect"/> exactly as sent, and
    /// returns Google's answer once it holds an access token; Google's refusal is thrown in words.
    /// </summary>
    private async Task<GoogleAnswer> ExchangeAsync(GoogleClient client, string code, string verifier, string redirect, CancellationToken cancellationToken)
    {
        var answer = await account.ExchangeCodeAsync(client, code, verifier, redirect, logger, cancellationToken);
        if (answer.Status == (int)HttpStatusCode.OK && answer.AccessToken is { Length: > 0 }) return answer;

        var error = answer.Described;
        logger.LogDebug("Google refused the sign-in (HTTP {Status}, {Error})", answer.Status, error);
        if (answer.Error == "invalid_client")
        {
            throw new GoogleDriveException(DriveProblem.ClientRefused,
                $"Google does not accept the client in that file ({error}): it was deleted, or its secret was replaced. Download the client's JSON again from Google Auth platform > Clients (Add secret, if the secret is gone), and sign in with it.");
        }

        throw new GoogleDriveException(DriveProblem.Refused, $"Google refused the sign-in (HTTP {answer.Status}, {error}); no sign-in was stored. Sign in again.");
    }

    /// <summary>
    /// Keeps the sign-in once Drive has said who the account is and ClassScribe's folder is found or made. The sign-in is kept
    /// first, so that a renewal on the way uses its own refresh token; should Drive fail, what was kept before is put back and
    /// the sign-in's token let go.
    /// </summary>
    private async Task<SignedIn> KeepAsync(GoogleClient client, GoogleAnswer answer, string refreshToken, CancellationToken cancellationToken)
    {
        var before = store.Read().Credentials ?? new GoogleCredentials(client.Id, client.Secret);
        var signedIn = new GoogleCredentials(client.Id, client.Secret) { RefreshToken = refreshToken, SignedInUtc = time.GetUtcNow(), Scope = answer.Scope };
        account.Seed(answer.AccessToken!, TimeSpan.FromSeconds(Math.Max(0, answer.ExpiresIn ?? 3600)));
        store.Write(signedIn);
        try
        {
            var about = await drive.GetAccountAsync(cancellationToken);
            var folder = await drive.FindOrCreateRootAsync(cancellationToken);
            var email = about.EmailAddress ?? about.DisplayName ?? "your Google account";
            store.Write(signedIn with { Email = email });
            logger.LogInformation("Signed in to Google Drive as {Email}. Uploads go to My Drive > {Folder} ({Link}).", email, folder.Name, folder.Link);
            return new SignedIn(email, folder);
        }
        catch (Exception ex)
        {
            logger.LogDebug("Google Drive could not be checked after the sign-in, so it was not kept: {Reason}", ex.Message);
            account.Forget();
            store.Write(before);
            throw;
        }
    }

    /// <summary>True when the scopes Google granted, separated by spaces, include drive.file.</summary>
    private static bool Granted(string? scope) =>
        scope?.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(DriveFileScope, StringComparer.Ordinal) == true;

    /// <summary>
    /// Google's sign-in address for this sign-in: the sign-in page <paramref name="page"/> (Drive:AuthorizationUrl), with the
    /// client, the loopback redirect address, a code, drive.file alone, the S256 challenge, the state, prompt=consent (so a
    /// second sign-in gets a fresh refresh token) and offline access, and nothing else: a query or fragment of the page's
    /// address, which the settings check refuses, is left out, so that no parameter of the settings' can come before these
    /// and ask for more of the Drive (architecture 7.4).
    /// </summary>
    internal static Uri AuthorizationAddress(string page, string clientId, string redirect, string challenge, string state)
    {
        (string Name, string Value)[] parameters =
        [
            ("client_id", clientId),
            ("redirect_uri", redirect),
            ("response_type", "code"),
            ("scope", DriveFileScope),
            ("code_challenge", challenge),
            ("code_challenge_method", "S256"),
            ("state", state),
            ("prompt", "consent"),
            ("access_type", "offline")
        ];
        var alone = new Uri(page.Trim()).GetLeftPart(UriPartial.Path);
        return new Uri($"{alone}?{string.Join("&", parameters.Select(parameter => $"{parameter.Name}={Uri.EscapeDataString(parameter.Value)}"))}");
    }

    /// <summary>A small page of its own, with nothing loaded from elsewhere, that says <paramref name="text"/>.</summary>
    private static string Page(string text) =>
        $"<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>ClassScribe</title></head><body style=\"font-family: 'Segoe UI', sans-serif; margin: 3em; max-width: 40em\"><h1>ClassScribe</h1><p>{WebUtility.HtmlEncode(text)}</p></body></html>";
}
