using System.Net;
using System.Web;
using ClassScribe.Drive;
using ClassScribe.Drive.Windows;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClassScribe.Tests;

/// <summary>
/// The Google sign-in for a desktop app, against the in-memory Google, with the browser and the listener played by the test
/// (and once the real listener on 127.0.0.1): the authorization address with PKCE S256, the loopback redirect and
/// prompt=consent; the wait for the redirect with this sign-in's state, which ignores any other request, gives up after 5
/// minutes or on Esc, and stops at access_denied; the exchange with the verifier and the same redirect address; drive.file
/// required; Drive checked and ClassScribe's folder found or made before the sign-in is kept. And the sign-out: revoke, then
/// forget the token, keep the client, whatever Google answers.
/// </summary>
[TestFixture]
public class GoogleSignInTests
{
    private GoogleWorld _world = null!;

    /// <summary>A world with nothing stored, as before the first sign-in.</summary>
    [SetUp]
    public void SetUp() => _world = new GoogleWorld();

    /// <summary>Deletes the world's folders.</summary>
    [TearDown]
    public void TearDown() => _world.Dispose();

    /// <summary>Signs in with the client file, failing the test rather than hanging if the sign-in waits for nothing.</summary>
    private Task<SignedIn> SignInAsync(string? clientFile = "", GoogleSignIn? signIn = null, CancellationToken cancellationToken = default) =>
        (signIn ?? _world.SignIn()).SignInAsync(clientFile == "" ? _world.WriteClientFile() : clientFile, cancellationToken).WaitAsync(TimeSpan.FromSeconds(15));

    /// <summary>The message of the <see cref="GoogleDriveException"/> <paramref name="action"/> fails with, which must be of <paramref name="problem"/>.</summary>
    private static string FailsWith(DriveProblem problem, Func<Task> action)
    {
        var failure = Assert.ThrowsAsync<GoogleDriveException>(action)!;
        Assert.That(failure.Problem, Is.EqualTo(problem));
        return failure.Message;
    }

    /// <summary>
    /// The browser goes to Google's sign-in for desktop apps with every parameter: the client, the loopback redirect address
    /// of the listener, a code, drive.file alone, the S256 challenge of the verifier the exchange then sends, this sign-in's
    /// state, prompt=consent for a fresh refresh token, and offline access.
    /// </summary>
    [Test]
    public async Task The_browser_goes_to_Googles_sign_in_with_every_parameter()
    {
        await SignInAsync();

        var address = _world.Browser.Opened.Single();
        var sent = HttpUtility.ParseQueryString(address.Query);
        var verifier = _world.Google.TokenRequests.Single().Form["code_verifier"]!;
        Assert.That(address.GetLeftPart(UriPartial.Path), Is.EqualTo("https://accounts.google.com/o/oauth2/v2/auth"));
        Assert.That(sent.AllKeys.Order(), Is.EqualTo(new[] { "access_type", "client_id", "code_challenge", "code_challenge_method", "prompt", "redirect_uri", "response_type", "scope", "state" }));
        Assert.That(sent["client_id"], Is.EqualTo(FakeGoogle.ClientId));
        Assert.That(sent["redirect_uri"], Is.EqualTo(_world.Receiver.Current!.RedirectUri.AbsoluteUri).And.StartsWith("http://127.0.0.1:"));
        Assert.That(sent["response_type"], Is.EqualTo("code"));
        Assert.That(sent["scope"], Is.EqualTo("https://www.googleapis.com/auth/drive.file"));
        Assert.That(sent["code_challenge"], Is.EqualTo(Pkce.ChallengeFor(verifier)));
        Assert.That(sent["code_challenge_method"], Is.EqualTo("S256"));
        Assert.That(sent["state"], Has.Length.EqualTo(43));
        Assert.That(sent["prompt"], Is.EqualTo("consent"));
        Assert.That(sent["access_type"], Is.EqualTo("offline"));
    }

    /// <summary>
    /// The browser goes to the sign-in page Drive:AuthorizationUrl names (architecture 7.2), a local stub's in a dry run, with
    /// the sign-in's own parameters after it and no others: a query or fragment of the page's address, which the settings
    /// check refuses, is left out, so that a scope of its own can never come before drive.file and ask for more (7.4).
    /// </summary>
    [TestCase("http://127.0.0.1:5007/o/oauth2/v2/auth")]
    [TestCase("http://127.0.0.1:5007/o/oauth2/v2/auth?hl=ar")]
    [TestCase("http://127.0.0.1:5007/o/oauth2/v2/auth?scope=https%3A%2F%2Fwww.googleapis.com%2Fauth%2Fdrive&prompt=none")]
    [TestCase("http://127.0.0.1:5007/o/oauth2/v2/auth#consent")]
    public async Task The_browser_goes_to_the_sign_in_page_the_settings_name(string page)
    {
        _world.Options.Set(new DriveOptions { AuthorizationUrl = page });
        _world.Google.AuthorizationPage = "http://127.0.0.1:5007/o/oauth2/v2/auth";

        await SignInAsync();

        var address = _world.Browser.Opened.Single();
        var sent = HttpUtility.ParseQueryString(address.Query);
        Assert.That(address.GetLeftPart(UriPartial.Path), Is.EqualTo("http://127.0.0.1:5007/o/oauth2/v2/auth"));
        Assert.That(sent.AllKeys.Order(), Is.EqualTo(new[] { "access_type", "client_id", "code_challenge", "code_challenge_method", "prompt", "redirect_uri", "response_type", "scope", "state" }));
        Assert.That((sent["client_id"], sent["scope"], sent["prompt"], address.Fragment), Is.EqualTo((FakeGoogle.ClientId, "https://www.googleapis.com/auth/drive.file", "consent", "")));
    }

    /// <summary>The exchange sends the code, the client, the verifier and the redirect address exactly as the browser was sent it.</summary>
    [Test]
    public async Task The_exchange_sends_the_verifier_and_the_same_redirect_address()
    {
        await SignInAsync();

        var form = _world.Google.TokenRequests.Single().Form;
        var sent = HttpUtility.ParseQueryString(_world.Browser.Opened.Single().Query);
        Assert.That(form.AllKeys.Order(), Is.EqualTo(new[] { "client_id", "client_secret", "code", "code_verifier", "grant_type", "redirect_uri" }));
        Assert.That((form["grant_type"], form["client_id"], form["client_secret"]), Is.EqualTo(("authorization_code", FakeGoogle.ClientId, FakeGoogle.ClientSecret)));
        Assert.That(form["redirect_uri"], Is.EqualTo(sent["redirect_uri"]));
        Assert.That(form["code_verifier"], Has.Length.EqualTo(43));
        Assert.That(_world.Google.TokenRequests.Single().ContentType, Is.EqualTo("application/x-www-form-urlencoded"));
    }

    /// <summary>
    /// A sign-in that goes through keeps the client, the refresh token, the account, when and the scope; finds Drive answering
    /// and makes ClassScribe's folder in My Drive; says where uploads go; and answers the browser with a page that says to go
    /// back. The listener is closed once Google's answer is in.
    /// </summary>
    [Test]
    public async Task A_sign_in_keeps_the_sign_in_and_makes_ClassScribes_folder()
    {
        _world.Time.Advance(TimeSpan.FromMinutes(3));

        var signedIn = await SignInAsync();

        var folder = _world.Google.Files.Single();
        Assert.That(signedIn, Is.EqualTo(new SignedIn("student@example.com", new DriveFolder(folder.Id, "ClassScribe"))));
        Assert.That(_world.Store.Stored, Is.EqualTo(new GoogleCredentials(FakeGoogle.ClientId, FakeGoogle.ClientSecret)
        {
            RefreshToken = _world.Google.RefreshTokensIssued.Single(),
            Email = "student@example.com",
            SignedInUtc = TestRuns.Start.AddMinutes(3),
            Scope = FakeGoogle.DriveFileScope
        }));
        Assert.That(folder.AppProperties["classscribe"], Is.EqualTo("root"));
        Assert.That(_world.Logs.Messages, Has.Some.EqualTo($"Signed in to Google Drive as student@example.com. Uploads go to My Drive > ClassScribe (https://drive.google.com/drive/folders/{folder.Id})."));
        var listener = _world.Receiver.Current!;
        Assert.That(listener.Disposed, Is.True);
        Assert.That(listener.Answers.Select(answer => (answer.Path, answer.Status)), Is.EqualTo(new[] { ("/", 200) }));
        Assert.That(listener.Answers[0].Html, Does.Contain("You can close this tab and go back to ClassScribe"));
    }

    /// <summary>The address is written to the window, to open by hand, with the wait's limit and Esc, and never to the log.</summary>
    [Test]
    public async Task The_address_is_written_to_the_window_and_not_to_the_log()
    {
        await SignInAsync();

        var address = _world.Browser.Opened.Single().AbsoluteUri;
        Assert.That(_world.Ui.Screen, Does.Contain("Opening Google's sign-in in your browser...")
            .And.Contain(address)
            .And.Contain("Waiting for Google (up to 5 minutes; Esc gives up)."));
        Assert.That(_world.Logs.Messages, Has.None.Contains(address).And.None.Contains("state="));
    }

    /// <summary>A browser that does not open leaves the address to copy, and the wait goes on: opened by hand, the sign-in goes through.</summary>
    [Test]
    public async Task A_browser_that_does_not_open_leaves_the_address_to_copy()
    {
        _world.Browser.Play = BrowserPlay.CannotOpen;

        var signIn = SignInAsync();
        await Wait.UntilAsync(() => _world.Ui.Screen.Contains("Waiting for Google"));
        Assert.That(_world.Ui.Screen, Does.Contain("The browser did not open by itself. Copy this address into your browser:"));
        _world.Browser.Play = BrowserPlay.Allow;
        _world.Browser.Open(_world.Browser.Opened.Single());

        Assert.That((await signIn).Email, Is.EqualTo("student@example.com"));
    }

    /// <summary>
    /// A request to the listener without this sign-in's state, as from another page, is refused with 400 and the wait goes on;
    /// so is a favicon, with 404; the right one is then taken.
    /// </summary>
    [TestCase(BrowserPlay.WrongStateFirst, "/", 400)]
    [TestCase(BrowserPlay.FaviconFirst, "/favicon.ico", 404)]
    public async Task Another_request_is_refused_and_the_right_one_taken(BrowserPlay play, string path, int status)
    {
        _world.Browser.Play = play;

        await SignInAsync();

        Assert.That(_world.Receiver.Current!.Answers.Select(answer => (answer.Path, answer.Status)), Is.EqualTo(new[] { (path, status), ("/", 200) }));
        Assert.That(_world.Google.TokenRequests.Single().Form["code"], Does.StartWith("4/0AQSTgQ-code-"), "the stray code was never exchanged");
        Assert.That(_world.Store.Stored!.IsSignedIn, Is.True);
    }

    /// <summary>
    /// The tab closed before the page arrived, which the listener says as an IOException, is passed over at Debug: the code
    /// Google sent is exchanged all the same, the sign-in is kept and the listener closed.
    /// </summary>
    [Test]
    public async Task A_tab_closed_before_the_page_arrived_does_not_end_the_sign_in()
    {
        _world.Browser.Play = BrowserPlay.ClosesTheTab;

        var signedIn = await SignInAsync();

        Assert.That(signedIn.Email, Is.EqualTo("student@example.com"));
        Assert.That(_world.Store.Stored!.IsSignedIn, Is.True);
        Assert.That(_world.Google.TokenRequests, Has.Count.EqualTo(1));
        Assert.That(_world.Logs.Messages, Has.Some.EqualTo("The browser did not take the page"));
        Assert.That(_world.Receiver.Current!.Answers, Is.Empty, "the page never reached the tab");
        Assert.That(_world.Receiver.Current.Disposed, Is.True);
    }

    /// <summary>
    /// Cancel on Google's page (access_denied) ends the sign-in with that said, answers the browser, keeps the client just
    /// imported (so trying again needs no file) and stores no sign-in. Nothing is exchanged.
    /// </summary>
    [Test]
    public void Access_denied_stores_no_sign_in()
    {
        _world.Browser.Play = BrowserPlay.Deny;

        Assert.That(FailsWith(DriveProblem.NotAllowed, () => SignInAsync()), Is.EqualTo(GoogleSignIn.NotAllowed));

        Assert.That(_world.Store.Stored, Is.EqualTo(FakeCredentialStore.With(null).Stored));
        Assert.That(_world.Google.TokenRequests, Is.Empty);
        Assert.That(_world.Receiver.Current!.Answers.Single().Html, Does.Contain("not allowed"));
        Assert.That(_world.Receiver.Current.Disposed, Is.True);
    }

    /// <summary>Another error from Google on the redirect, such as one about the client, is given with Google's code.</summary>
    [Test]
    public async Task Another_error_on_the_redirect_is_named()
    {
        _world.Browser.Play = BrowserPlay.Nothing;
        var signIn = SignInAsync();
        await Wait.UntilAsync(() => _world.Receiver.Current is not null && _world.Browser.Opened.Count == 1);
        var state = HttpUtility.ParseQueryString(_world.Browser.Opened.Single().Query)["state"]!;

        _world.Receiver.Current!.Arrive("/", new() { ["error"] = "invalid_request", ["state"] = state });

        Assert.That(FailsWith(DriveProblem.Refused, () => signIn), Does.StartWith("Google ended the sign-in with an error (invalid_request); no sign-in was stored."));
    }

    /// <summary>No answer within 5 minutes, on the clock, gives up: nothing exchanged, no sign-in stored, the listener closed.</summary>
    [Test]
    public async Task No_answer_within_five_minutes_gives_up()
    {
        _world.Browser.Play = BrowserPlay.Nothing;

        var signIn = SignInAsync();
        await Wait.UntilAsync(() => _world.Ui.Screen.Contains("Waiting for Google") && _world.Time.UntilNextTimer == GoogleSignIn.WaitLimit);
        _world.Time.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));
        Assert.That(_world.Time.UntilNextTimer, Is.EqualTo(TimeSpan.FromSeconds(1)), "a second before the limit, the limit is still to come");
        Assert.That(signIn.IsCompleted, Is.False, "a second before the limit");
        _world.Time.Advance(TimeSpan.FromSeconds(1));

        Assert.That(FailsWith(DriveProblem.TimedOut, () => signIn), Is.EqualTo(GoogleSignIn.TimedOut));
        Assert.That(_world.Store.Stored!.IsSignedIn, Is.False);
        Assert.That(_world.Receiver.Current!.Disposed, Is.True);
    }

    /// <summary>
    /// Esc gives up at once, as a cancel: nothing exchanged, no sign-in stored, the listener closed; so does Ctrl+C. The keys
    /// are named, not given as their control characters, so that a filter can name each case.
    /// </summary>
    [TestCase("Esc")]
    [TestCase("Ctrl+C")]
    public async Task Esc_or_Ctrl_C_gives_up(string key)
    {
        _world.Browser.Play = BrowserPlay.Nothing;

        var signIn = SignInAsync();
        await Wait.UntilAsync(() => _world.Ui.Screen.Contains("Waiting for Google"));
        _world.Ui.Type('x', key == "Esc" ? '\u001b' : '\u0003');

        Assert.That(async () => await signIn, Throws.InstanceOf<OperationCanceledException>());
        Assert.That(_world.Store.Stored!.IsSignedIn, Is.False);
        Assert.That(_world.Google.TokenRequests, Is.Empty);
        Assert.That(_world.Receiver.Current!.Disposed, Is.True);
        Assert.That(_world.Logs.Messages, Has.Some.EqualTo(GoogleSignIn.GivenUp));
    }

    /// <summary>The run's own cancel, such as Ctrl+C through the run's signals, ends the wait as a cancel too.</summary>
    [Test]
    public async Task The_runs_cancel_ends_the_wait()
    {
        _world.Browser.Play = BrowserPlay.Nothing;
        using var cancel = new CancellationTokenSource();

        var signIn = SignInAsync(cancellationToken: cancel.Token);
        await Wait.UntilAsync(() => _world.Ui.Screen.Contains("Waiting for Google"));
        await cancel.CancelAsync();

        Assert.That(async () => await signIn, Throws.InstanceOf<OperationCanceledException>());
        Assert.That(_world.Receiver.Current!.Disposed, Is.True);
    }

    /// <summary>
    /// A consent page where the Drive box was left unticked grants no drive.file: nothing is stored, Drive is never asked, and
    /// the message says to tick it.
    /// </summary>
    [Test]
    public void A_scope_not_granted_stores_no_sign_in()
    {
        _world.Google.GrantedScope = "openid";

        Assert.That(FailsWith(DriveProblem.ScopeNotGranted, () => SignInAsync()), Is.EqualTo(GoogleSignIn.ScopeNotGranted));

        Assert.That(_world.Store.Stored!.IsSignedIn, Is.False);
        Assert.That(_world.Google.Requests.Where(request => request.Path.StartsWith("/drive/", StringComparison.Ordinal)), Is.Empty);
    }

    /// <summary>drive.file among other scopes is enough.</summary>
    [Test]
    public async Task Drive_file_among_other_scopes_is_enough()
    {
        _world.Google.GrantedScope = $"openid {FakeGoogle.DriveFileScope} email";

        Assert.That((await SignInAsync()).Email, Is.EqualTo("student@example.com"));
    }

    /// <summary>An answer without a refresh token stores no sign-in, and says so.</summary>
    [Test]
    public void An_answer_without_a_refresh_token_stores_no_sign_in()
    {
        _world.Google.IssuesRefreshToken = false;

        Assert.That(FailsWith(DriveProblem.Refused, () => SignInAsync()), Is.EqualTo(GoogleSignIn.NoRefreshToken));
        Assert.That(_world.Store.Stored!.IsSignedIn, Is.False);
    }

    /// <summary>An exchange Google refuses gives Google's error; one that refuses the client says to download its file again.</summary>
    [Test]
    public void An_exchange_Google_refuses_says_why()
    {
        _world.Google.Fail("/token", HttpStatusCode.BadRequest, FakeGoogle.OAuthError("invalid_grant", "Bad Request"));
        Assert.That(FailsWith(DriveProblem.Refused, () => SignInAsync()), Is.EqualTo("Google refused the sign-in (HTTP 400, invalid_grant: Bad Request); no sign-in was stored. Sign in again."));

        _world.Google.Fail("/token", HttpStatusCode.Unauthorized, FakeGoogle.OAuthError("invalid_client", "The OAuth client was not found."));
        Assert.That(FailsWith(DriveProblem.ClientRefused, () => SignInAsync()), Does.StartWith("Google does not accept the client in that file (invalid_client: The OAuth client was not found.)"));
        Assert.That(_world.Store.Stored!.IsSignedIn, Is.False);
    }

    /// <summary>
    /// The Drive API left disabled in the project is named, and the sign-in is not kept: the store is as it was before, and
    /// the token of the sign-in is let go.
    /// </summary>
    [Test]
    public void A_Drive_API_left_disabled_is_named_and_the_sign_in_not_kept()
    {
        _world.Google.Fail("/drive/v3/about", HttpStatusCode.Forbidden,
            FakeGoogle.DriveError(403, "accessNotConfigured", "Google Drive API has not been used in project 123456789012 before or it is disabled.", "PERMISSION_DENIED"));

        Assert.That(FailsWith(DriveProblem.Refused, () => SignInAsync()), Is.EqualTo(DriveClient.ApiDisabled));

        Assert.That(_world.Store.Stored, Is.EqualTo(FakeCredentialStore.With(null).Stored));
        Assert.That(_world.Cache.Valid(_world.Time, TimeSpan.Zero), Is.Null, "the token is let go");
    }

    /// <summary>A failure after the exchange puts back the sign-in that was kept before, rather than leaving none.</summary>
    [Test]
    public async Task A_failure_after_the_exchange_puts_back_the_sign_in_kept_before()
    {
        await _world.SignInAsync();
        var before = _world.Store.Stored;
        _world.Google.Fail("/drive/v3/about", HttpStatusCode.ServiceUnavailable, FakeGoogle.DriveError(503, "backendError", "Backend Error"));

        Assert.That(FailsWith(DriveProblem.Busy, () => SignInAsync(clientFile: null)), Does.StartWith("Google Drive refused to show the account (HTTP 503"));

        Assert.That(_world.Store.Stored, Is.EqualTo(before));
    }

    /// <summary>The first sign-in needs the client file: without one, nothing is opened, and the message says how to give it.</summary>
    [Test]
    public void The_first_sign_in_needs_the_client_file()
    {
        Assert.That(FailsWith(DriveProblem.NotSetUp, () => SignInAsync(clientFile: null)), Is.EqualTo(GoogleSignIn.NoClient));

        Assert.That(_world.Receiver.Opened, Is.EqualTo(0));
        Assert.That(_world.Browser.Opened, Is.Empty);
    }

    /// <summary>A store that cannot be read, without a client file, says why, and that the client file is needed again.</summary>
    [Test]
    public void An_unreadable_store_without_a_client_file_says_why()
    {
        _world.Store.MakeUnreadable("google-drive.dat cannot be opened by this Windows account.");

        Assert.That(FailsWith(DriveProblem.StoreUnreadable, () => SignInAsync(clientFile: null)), Is.EqualTo("google-drive.dat cannot be opened by this Windows account."));
    }

    /// <summary>A client file that cannot be used stops the sign-in before anything is opened or stored.</summary>
    [Test]
    public void A_client_file_that_cannot_be_used_stops_the_sign_in_first()
    {
        var web = _world.WriteClientFile(kind: "web");

        Assert.That(FailsWith(DriveProblem.ClientFile, () => SignInAsync(web)), Does.Contain("Web application client"));

        Assert.That((_world.Receiver.Opened, _world.Store.Writes), Is.EqualTo((0, 0)));
    }

    /// <summary>A second sign-in uses the kept client, with no file, and keeps the new sign-in in place of the old.</summary>
    [Test]
    public async Task A_second_sign_in_uses_the_kept_client()
    {
        await _world.SignInAsync();

        await SignInAsync(clientFile: null);

        Assert.That(_world.Store.Stored!.RefreshToken, Is.EqualTo(_world.Google.RefreshTokensIssued[1]));
        Assert.That(_world.Google.Files, Has.Count.EqualTo(1), "ClassScribe's folder was found again, not made twice");
    }

    /// <summary>
    /// A client file of another client replaces the kept client, and the old sign-in, which was the old client's, goes; a
    /// file with a new secret for the same client keeps the sign-in and takes the new secret.
    /// </summary>
    [Test]
    public async Task A_new_client_file_replaces_the_kept_client()
    {
        var signIn = _world.SignIn();
        _world.Store.Stored = new GoogleCredentials("999-other.apps.googleusercontent.com", "GOCSPX-old") { RefreshToken = "1//0gOld", Email = "old@example.com" };
        _world.Browser.Play = BrowserPlay.Deny;
        Assert.That(FailsWith(DriveProblem.NotAllowed, () => SignInAsync(signIn: signIn)), Is.EqualTo(GoogleSignIn.NotAllowed));
        Assert.That(_world.Store.Stored, Is.EqualTo(FakeCredentialStore.With(null).Stored), "the other client and its sign-in were replaced");

        _world.Store.Stored = FakeCredentialStore.With("1//0gKept").Stored! with { ClientSecret = "GOCSPX-old-secret" };
        Assert.That(FailsWith(DriveProblem.NotAllowed, () => SignInAsync()), Is.EqualTo(GoogleSignIn.NotAllowed));
        Assert.That(_world.Store.Stored, Is.EqualTo(FakeCredentialStore.With("1//0gKept").Stored), "the same client: the sign-in is kept, the secret is new");
    }

    /// <summary>An existing folder of ClassScribe's in My Drive is found, not made again.</summary>
    [Test]
    public async Task An_existing_folder_is_found_not_made_again()
    {
        var existing = _world.Google.AddFolder("ClassScribe", root: true);

        var signedIn = await SignInAsync();

        Assert.That(signedIn.Folder.Id, Is.EqualTo(existing.Id));
        Assert.That(_world.Google.Files, Has.Count.EqualTo(1));
    }

    /// <summary>
    /// A whole sign-in through the real listener on 127.0.0.1, with a browser that really asks it: the browser gets the page,
    /// and the sign-in is kept. Nothing leaves the computer.
    /// </summary>
    [Test]
    public async Task A_sign_in_through_the_real_listener()
    {
        var browser = new LocalHttpBrowser(_world.Google);
        var signIn = _world.SignIn(browser, new HttpListenerLoopbackReceiver(NullLogger<HttpListenerLoopbackReceiver>.Instance));

        var signedIn = await SignInAsync(signIn: signIn);

        var (status, page) = await browser.Done.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(page, Does.Contain("You can close this tab and go back to ClassScribe"));
        Assert.That(signedIn.Email, Is.EqualTo("student@example.com"));
        Assert.That(_world.Store.Stored!.IsSignedIn, Is.True);
    }

    // ---- Signing out ----

    /// <summary>
    /// Signing out asks Google to revoke the refresh token, forgets it here, and keeps the client, so signing in again needs
    /// no file; nothing on Drive is touched; Drive no longer takes the tokens.
    /// </summary>
    [Test]
    public async Task Signing_out_revokes_and_forgets_the_token_and_keeps_the_client()
    {
        var refreshToken = await _world.SignInAsync();
        var requestsBefore = _world.Google.Requests.Count;

        var outcome = await _world.SignIn().SignOutAsync(CancellationToken.None);

        Assert.That(outcome, Is.EqualTo(SignOutOutcome.Revoked));
        var revoke = _world.Google.Requests.Skip(requestsBefore).Single();
        Assert.That((revoke.Method, revoke.Host, revoke.Path, revoke.Form["token"]), Is.EqualTo((HttpMethod.Post, "oauth2.googleapis.com", "/revoke", refreshToken)));
        Assert.That(_world.Google.Revoked, Does.Contain(refreshToken));
        Assert.That(_world.Store.Stored, Is.EqualTo(FakeCredentialStore.With(null).Stored));
        Assert.That(_world.Cache.Valid(_world.Time, TimeSpan.Zero), Is.Null);
        Assert.That(_world.Logs.Messages, Has.Some.EqualTo(GoogleSignIn.SignedOut));
        Assert.That(_world.Google.Files.Single().Trashed, Is.False);
    }

    /// <summary>A token Google had already ended is forgotten all the same, and that is said.</summary>
    [Test]
    public async Task Signing_out_of_a_sign_in_Google_had_ended_forgets_it()
    {
        _world.Google.RevokeByHand(await _world.SignInAsync());

        Assert.That(await _world.SignIn().SignOutAsync(CancellationToken.None), Is.EqualTo(SignOutOutcome.AlreadyEnded));
        Assert.That(_world.Store.Stored!.IsSignedIn, Is.False);
        Assert.That(_world.Logs.Messages, Has.Some.EqualTo(GoogleSignIn.SignedOutAlreadyEnded));
    }

    /// <summary>
    /// When Google cannot be reached, the token is forgotten here all the same, and the warning says how to end the access by
    /// hand at the Google Account.
    /// </summary>
    [Test]
    public async Task Signing_out_while_Google_cannot_be_reached_forgets_the_token_and_says_what_to_do()
    {
        await _world.SignInAsync();
        _world.Google.Unreachable = true;

        var outcome = await _world.SignIn().SignOutAsync(CancellationToken.None);

        Assert.That(outcome, Is.EqualTo(SignOutOutcome.NotRevoked));
        Assert.That(_world.Store.Stored, Is.EqualTo(FakeCredentialStore.With(null).Stored));
        Assert.That(_world.Logs.Problems.Single(), Does.StartWith("Signed out here, but Google could not be reached to end ClassScribe's access (No such host is known.")
            .And.Contain("https://myaccount.google.com/connections"));
    }

    /// <summary>Signing out without a sign-in asks Google nothing and says so.</summary>
    [Test]
    public async Task Signing_out_without_a_sign_in_says_so()
    {
        _world.Store.Stored = FakeCredentialStore.With(null).Stored;

        Assert.That(await _world.SignIn().SignOutAsync(CancellationToken.None), Is.EqualTo(SignOutOutcome.WasNotSignedIn));
        Assert.That(_world.Google.Requests, Is.Empty);
        Assert.That(_world.Store.Writes, Is.EqualTo(0));
    }
}
