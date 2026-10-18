using System.Net;
using ClassScribe.Drive;

namespace ClassScribe.Tests;

/// <summary>
/// The access tokens of a process: held in memory only, renewed with the stored refresh token when less than two minutes
/// are left, one renewal at a time for the whole process, and once more when Drive refuses one. Google ending the sign-in
/// (invalid_grant) removes the dead token from the store and keeps the client; a refused client, Google out of reach and a
/// store without a sign-in each say what to do. All against the in-memory Google, on the test's clock.
/// </summary>
[TestFixture]
public class GoogleTokensTests
{
    private GoogleWorld _world = null!;
    private string _refreshToken = null!;

    /// <summary>A world whose store holds the tests' client, signed in with a refresh token Google knows.</summary>
    [SetUp]
    public void SetUp()
    {
        _world = new GoogleWorld(FakeCredentialStore.With(refreshToken: null));
        _refreshToken = _world.Google.IssueRefreshToken();
        _world.Store.Stored = FakeCredentialStore.With(_refreshToken).Stored;
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

    /// <summary>The first token is made with the stored client and refresh token, as Google's refresh asks: one request, the right form.</summary>
    [Test]
    public async Task The_first_token_is_made_with_the_stored_refresh_token()
    {
        var token = await _world.Tokens().GetAccessTokenAsync(CancellationToken.None);

        Assert.That(token, Is.EqualTo(_world.Google.AccessTokensIssued.Single()));
        var form = _world.Google.TokenRequests.Single().Form;
        Assert.That((form["grant_type"], form["client_id"], form["client_secret"], form["refresh_token"]),
            Is.EqualTo(("refresh_token", FakeGoogle.ClientId, FakeGoogle.ClientSecret, _refreshToken)));
    }

    /// <summary>A token is used again while it has more than two minutes left, and renewed once it has less, on the steady clock.</summary>
    [Test]
    public async Task A_token_is_used_until_two_minutes_are_left()
    {
        var tokens = _world.Tokens();
        var first = await tokens.GetAccessTokenAsync(CancellationToken.None);

        _world.Time.Advance(TimeSpan.FromSeconds(3599 - 121));
        var again = await tokens.GetAccessTokenAsync(CancellationToken.None);
        _world.Time.Advance(TimeSpan.FromSeconds(2));
        var renewed = await tokens.GetAccessTokenAsync(CancellationToken.None);

        Assert.That(again, Is.EqualTo(first), "121 seconds left");
        Assert.That(renewed, Is.Not.EqualTo(first), "119 seconds left");
        Assert.That(_world.Google.Refreshes, Is.EqualTo(2));
    }

    /// <summary>A change of the time of day does not age a token: only the steady clock counts.</summary>
    [Test]
    public async Task A_change_of_the_time_of_day_does_not_age_a_token()
    {
        var tokens = _world.Tokens();
        var first = await tokens.GetAccessTokenAsync(CancellationToken.None);

        _world.Time.StepWallClock(TimeSpan.FromHours(3));

        Assert.That(await tokens.GetAccessTokenAsync(CancellationToken.None), Is.EqualTo(first));
    }

    /// <summary>A token the sign-in was given is used as it is, without asking Google, and forgetting it asks again.</summary>
    [Test]
    public async Task A_token_given_by_the_sign_in_is_used_and_forgetting_it_asks_again()
    {
        var tokens = _world.Tokens();
        tokens.Seed("ya29.from-the-sign-in", TimeSpan.FromSeconds(3599));

        Assert.That(await tokens.GetAccessTokenAsync(CancellationToken.None), Is.EqualTo("ya29.from-the-sign-in"));
        Assert.That(_world.Google.Requests, Is.Empty);

        tokens.Forget();
        Assert.That(await tokens.GetAccessTokenAsync(CancellationToken.None), Is.Not.EqualTo("ya29.from-the-sign-in"));
        Assert.That(_world.Google.Refreshes, Is.EqualTo(1));
    }

    /// <summary>Callers at the same moment, through separate runs of the same process, share one renewal: one request to Google.</summary>
    [Test]
    public async Task Callers_at_the_same_moment_share_one_renewal()
    {
        var release = new TaskCompletionSource();
        _world.Google.Hold("/token", release.Task);

        var callers = Enumerable.Range(0, 5).Select(_ => _world.Tokens().GetAccessTokenAsync(CancellationToken.None)).ToList();
        await Wait.UntilAsync(() => _world.Google.TokenRequests.Count == 1);
        await Task.Delay(50);
        release.SetResult();
        var tokens = await Task.WhenAll(callers);

        Assert.That(_world.Google.Refreshes, Is.EqualTo(1));
        Assert.That(tokens.Distinct(), Is.EqualTo(new[] { _world.Google.AccessTokensIssued.Single() }));
    }

    /// <summary>
    /// A token Drive refused is renewed at once, even with time left, and a second caller holding the same refused token gets
    /// the renewed one without another request.
    /// </summary>
    [Test]
    public async Task A_token_Drive_refused_is_renewed_once()
    {
        var tokens = _world.Tokens();
        var refused = await tokens.GetAccessTokenAsync(CancellationToken.None);

        var renewed = await tokens.RenewAsync(refused, CancellationToken.None);
        var again = await _world.Tokens().RenewAsync(refused, CancellationToken.None);

        Assert.That(renewed, Is.Not.EqualTo(refused));
        Assert.That(again, Is.EqualTo(renewed));
        Assert.That(_world.Google.Refreshes, Is.EqualTo(2));
    }

    /// <summary>
    /// Google ending the sign-in (invalid_grant: revoked, six months unused, or Testing's seven days) removes the dead refresh
    /// token from the store, keeps the client, and says to sign in again.
    /// </summary>
    [Test]
    public void Google_ending_the_sign_in_removes_the_dead_token_and_keeps_the_client()
    {
        _world.Google.RevokeByHand(_refreshToken);

        var message = FailsWith(DriveProblem.SignInEnded, () => _world.Tokens().GetAccessTokenAsync(CancellationToken.None));

        Assert.That(message, Is.EqualTo(GoogleTokens.SignInEnded));
        Assert.That(_world.Store.Stored, Is.EqualTo(FakeCredentialStore.With(null).Stored), "the client is kept, the sign-in is gone");
        Assert.That(_world.Logs.Messages, Has.Some.Contains("invalid_grant"), "Google's error is logged, at Debug: the caller says what to do");
    }

    /// <summary>
    /// When another process signed in again while this one renewed with the old token, the new sign-in is not removed with
    /// the old one.
    /// </summary>
    [Test]
    public void A_newer_sign_in_is_not_removed_with_the_old_one()
    {
        var newer = FakeCredentialStore.With("1//0gNewerSignIn").Stored!;
        _world.Google.RevokeByHand(_refreshToken);
        var release = new TaskCompletionSource();
        _world.Google.Hold("/token", release.Task);

        var renewal = _world.Tokens().GetAccessTokenAsync(CancellationToken.None);
        Assert.That(() => _world.Google.TokenRequests.Count, Is.EqualTo(1).After(5000, 10));
        _world.Store.Stored = newer;
        release.SetResult();

        Assert.That(FailsWith(DriveProblem.SignInEnded, () => renewal), Is.EqualTo(GoogleTokens.SignInEnded));
        Assert.That(_world.Store.Stored, Is.EqualTo(newer));
    }

    /// <summary>A client Google no longer knows (invalid_client) says to import the client file again, and keeps the sign-in.</summary>
    [Test]
    public void A_client_Google_no_longer_knows_says_to_import_it_again()
    {
        _world.Google.Fail("/token", HttpStatusCode.Unauthorized, FakeGoogle.OAuthError("invalid_client", "The OAuth client was deleted."));

        var message = FailsWith(DriveProblem.ClientRefused, () => _world.Tokens().GetAccessTokenAsync(CancellationToken.None));

        Assert.That(message, Does.StartWith("Google no longer accepts ClassScribe's Google client (invalid_client: The OAuth client was deleted.)"));
        Assert.That(message, Does.Contain("drive signin --client"));
        Assert.That(_world.Store.Stored!.RefreshToken, Is.EqualTo(_refreshToken));
    }

    /// <summary>Google out of reach says so with the reason, and keeps the sign-in.</summary>
    [Test]
    public void Google_out_of_reach_says_so_and_keeps_the_sign_in()
    {
        _world.Google.Unreachable = true;

        var message = FailsWith(DriveProblem.Unreachable, () => _world.Tokens().GetAccessTokenAsync(CancellationToken.None));

        Assert.That(message, Does.StartWith("Google could not be reached to renew ClassScribe's sign-in (No such host is known."));
        Assert.That(_world.Store.Stored!.RefreshToken, Is.EqualTo(_refreshToken));
    }

    /// <summary>An answer that never comes ends at the limit of a minute, as Google out of reach.</summary>
    [Test]
    public async Task An_answer_that_never_comes_ends_at_the_limit()
    {
        _world.Google.Hold("/token", Task.Delay(Timeout.Infinite));

        var renewal = _world.Tokens().GetAccessTokenAsync(CancellationToken.None);
        await Wait.UntilAsync(() => _world.Google.TokenRequests.Count == 1);
        _world.Time.Advance(GoogleTokens.CallLimit);

        Assert.That(FailsWith(DriveProblem.Unreachable, () => renewal), Does.Contain("no answer within 60 seconds"));
    }

    /// <summary>Another refusal from Google gives its status and error, and keeps the sign-in.</summary>
    [Test]
    public void Another_refusal_gives_Googles_status_and_error()
    {
        _world.Google.Fail("/token", HttpStatusCode.InternalServerError, FakeGoogle.OAuthError("internal_failure", "Backend Error"));

        var message = FailsWith(DriveProblem.Refused, () => _world.Tokens().GetAccessTokenAsync(CancellationToken.None));

        Assert.That(message, Is.EqualTo("Google refused to renew ClassScribe's sign-in (HTTP 500, internal_failure: Backend Error). Try again later."));
    }

    /// <summary>Without a client, without a sign-in, or with a store that cannot be read, nothing is asked of Google, and the message says what to do.</summary>
    [Test]
    public void Without_a_sign_in_nothing_is_asked_and_the_message_says_what_to_do()
    {
        _world.Store.Stored = null;
        Assert.That(FailsWith(DriveProblem.NotSetUp, () => _world.Tokens().GetAccessTokenAsync(CancellationToken.None)), Is.EqualTo(GoogleTokens.NotSetUp));

        _world.Store.Stored = FakeCredentialStore.With(null).Stored;
        Assert.That(FailsWith(DriveProblem.NotSignedIn, () => _world.Tokens().GetAccessTokenAsync(CancellationToken.None)), Is.EqualTo(GoogleTokens.NotSignedIn));

        _world.Store.MakeUnreadable("google-drive.dat cannot be opened by this Windows account.");
        Assert.That(FailsWith(DriveProblem.StoreUnreadable, () => _world.Tokens().GetAccessTokenAsync(CancellationToken.None)),
            Is.EqualTo("google-drive.dat cannot be opened by this Windows account."));

        Assert.That(_world.Google.Requests, Is.Empty);
    }
}
