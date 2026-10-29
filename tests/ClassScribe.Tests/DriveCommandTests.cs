using System.Net;
using ClassScribe.Commands;
using ClassScribe.Drive;

namespace ClassScribe.Tests;

/// <summary>
/// The drive command's sign-in, sign-out and status, against the in-memory Google: what each says, in the window or the
/// log, and its exit code: 0 done, 1 failed or not signed in, a cancel let through for App to turn into 130. The status
/// shows the account, since when, the end of the client id (never the secret), the storage and ClassScribe's folder.
/// </summary>
[TestFixture]
public class DriveCommandTests
{
    private GoogleWorld _world = null!;

    /// <summary>A world with nothing stored, as before the first sign-in.</summary>
    [SetUp]
    public void SetUp() => _world = new GoogleWorld();

    /// <summary>Deletes the world's folders.</summary>
    [TearDown]
    public void TearDown() => _world.Dispose();

    /// <summary>The lines the window shows, without empty ones.</summary>
    private string[] Lines() => _world.Ui.Screen.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>A sign-in that goes through ends with exit code 0; the command needs no question answered.</summary>
    [Test]
    public async Task A_sign_in_ends_with_0()
    {
        Assert.That(await _world.Command().SignInAsync(_world.WriteClientFile()), Is.EqualTo(0));

        Assert.That(_world.Store.Stored!.IsSignedIn, Is.True);
        Assert.That(_world.Logs.Problems, Is.Empty);
    }

    /// <summary>A sign-in that does not go through says why, once, as an error, and ends with exit code 1.</summary>
    [Test]
    public async Task A_sign_in_that_fails_says_why_once_and_ends_with_1()
    {
        _world.Browser.Play = BrowserPlay.Deny;

        Assert.That(await _world.Command().SignInAsync(_world.WriteClientFile()), Is.EqualTo(1));

        Assert.That(_world.Logs.Problems, Is.EqualTo(new[] { GoogleSignIn.NotAllowed }));
    }

    /// <summary>The first sign-in without a client file says how to give one, and ends with 1.</summary>
    [Test]
    public async Task A_first_sign_in_without_a_client_file_says_how_to_give_one()
    {
        Assert.That(await _world.Command().SignInAsync(null), Is.EqualTo(1));

        Assert.That(_world.Logs.Problems, Is.EqualTo(new[] { GoogleSignIn.NoClient }));
    }

    /// <summary>Giving up with Esc is a cancel, which App turns into exit code 130 and "Cancelled".</summary>
    [Test]
    public async Task Giving_up_is_a_cancel()
    {
        _world.Browser.Play = BrowserPlay.Nothing;

        var signIn = _world.Command().SignInAsync(_world.WriteClientFile());
        await Wait.UntilAsync(() => _world.Ui.Screen.Contains("Waiting for Google"));
        _world.Ui.Type('\u001b');

        Assert.That(async () => await signIn, Throws.InstanceOf<OperationCanceledException>());
    }

    /// <summary>Ctrl+C through the run's signals cancels the wait for Google too.</summary>
    [Test]
    public async Task Ctrl_C_through_the_runs_signals_cancels_the_wait()
    {
        _world.Browser.Play = BrowserPlay.Nothing;
        var signals = new FakeUserSignals();

        var signIn = _world.Command(signals).SignInAsync(_world.WriteClientFile());
        await Wait.UntilAsync(() => _world.Ui.Screen.Contains("Waiting for Google"));
        signals.CtrlCPressed();

        Assert.That(async () => await signIn, Throws.InstanceOf<OperationCanceledException>());
        Assert.That(signals.StopRequested.IsCompleted, Is.True, "nothing records, so a first Ctrl+C cancels");
    }

    /// <summary>A sign-out that Google confirms ends with 0; one Google could not be asked about ends with 1, with what to do.</summary>
    [Test]
    public async Task Sign_out_ends_with_0_or_1_when_Google_could_not_be_asked()
    {
        await _world.SignInAsync();
        Assert.That(await _world.Command().SignOutAsync(), Is.EqualTo(0));

        await _world.SignInAsync();
        _world.Google.Unreachable = true;
        Assert.That(await _world.Command().SignOutAsync(), Is.EqualTo(1));

        _world.Google.Unreachable = false;
        Assert.That(await _world.Command().SignOutAsync(), Is.EqualTo(0), "nothing to sign out of");
        Assert.That(_world.Store.Stored!.IsSignedIn, Is.False);
    }

    /// <summary>
    /// The status when signed in: the account, since when in local time, the end of the client id and never the secret; the
    /// storage used of the limit; and ClassScribe's folder with its address. Exit code 0.
    /// </summary>
    [Test]
    public async Task The_status_when_signed_in_shows_the_account_the_storage_and_the_folder()
    {
        await _world.SignInAsync();
        var folder = _world.Google.Files.Single();

        Assert.That(await _world.Command().StatusAsync(), Is.EqualTo(0));

        Assert.That(Lines().TakeLast(4), Is.EqualTo(new[]
        {
            "Google Drive: signed in as student@example.com since 15 Oct 2026 (client ...a1b2c3.apps.googleusercontent.com)",
            "Storage: 1.2 TB of 30 TB used",
            $"Uploads go to My Drive > ClassScribe: https://drive.google.com/drive/folders/{folder.Id}",
            "Nothing waits to go up."
        }));
        Assert.That(_world.Ui.Screen, Does.Not.Contain(FakeGoogle.ClientSecret));
    }

    /// <summary>
    /// The status names each recording that waits to go up, with how many of its files are on the laptop and their size, and
    /// either that it was not tried yet or when it was last tried, in local time, and what went wrong then.
    /// </summary>
    [Test]
    public async Task The_status_names_what_waits()
    {
        await _world.SignInAsync();
        var mantiq = _world.WriteRecording(@"Mantiq\2026-10-17", "Mantiq", new DateTimeOffset(2026, 10, 17, 14, 4, 42, TimeSpan.Zero), 2000, ".md");
        var kalam = _world.WriteRecording("Kalam", "Kalam", new DateTimeOffset(2026, 10, 15, 23, 0, 2, TimeSpan.Zero), 2000);
        _world.Manifests().Queue(mantiq);
        _world.Time.Advance(TimeSpan.FromMinutes(1));
        _world.Manifests().Write(_world.Manifests().Queue(kalam) with
        {
            LastTriedUtc = new DateTimeOffset(2026, 10, 18, 1, 40, 0, TimeSpan.Zero),
            LastError = "Google Drive could not be reached (No such host is known)."
        });
        var command = new DriveCommand(_world.SignIn(), _world.Store, _world.GoogleDrive(), _world.QueueReport(), _world.Manifests(), _world.Options, new FakeUserSignals(), _world.Ui,
            ZonedTimeProvider.EasternSummer, _world.Log<DriveCommand>());

        Assert.That(await command.StatusAsync(), Is.EqualTo(0));

        var md = new FileInfo(mantiq.Transcript()).Length + new FileInfo(mantiq.Segment(1)).Length;
        Assert.That(Lines().TakeLast(2), Is.EqualTo(new[]
        {
            $"Waiting: {mantiq.BaseName} (2 files, {Sizes.Describe(md)}), not tried yet",
            $"Waiting: {kalam.BaseName} (1 file, {Sizes.Describe(new FileInfo(kalam.Segment(1)).Length)}), last try 17 Oct 21:40: Google Drive could not be reached (No such host is known)."
        }));
    }

    /// <summary>A class being recorded now, which its own run put in the queue as it began, is not listed as waiting: it goes up once it has stopped.</summary>
    [Test]
    public async Task The_status_does_not_list_a_class_being_recorded()
    {
        await _world.SignInAsync();
        var mantiq = _world.WriteRecording(@"Mantiq\2026-10-17", "Mantiq", new DateTimeOffset(2026, 10, 17, 14, 4, 42, TimeSpan.Zero), 2000, ".md");
        _world.Manifests().Queue(mantiq);
        _world.Runtime.Processes.Start(TestRuns.ProcessId, TestRuns.ProcessStart);
        _world.Runtime.Journal.Begin(TestRuns.Record(TestRuns.Id(), ClassScribe.Recorder.RunState.Recording, name: "Mantiq") with { BaseName = mantiq.BaseName, Folder = mantiq.Folder });

        Assert.That(await _world.Command().StatusAsync(), Is.EqualTo(0));

        Assert.That(Lines().Last(), Is.EqualTo("Nothing waits to go up."));
    }

    /// <summary>The date is the local one: a class at 23:00 UTC is the same evening in US Eastern time.</summary>
    [Test]
    public async Task The_date_is_local()
    {
        await _world.SignInAsync();
        var command = new DriveCommand(_world.SignIn(), _world.Store, _world.GoogleDrive(), _world.QueueReport(), _world.Manifests(), _world.Options, new FakeUserSignals(), _world.Ui,
            ZonedTimeProvider.EasternSummer, _world.Log<DriveCommand>());
        _world.Store.Stored = _world.Store.Stored! with { SignedInUtc = new DateTimeOffset(2026, 10, 18, 1, 30, 0, TimeSpan.Zero) };

        await command.StatusAsync();

        Assert.That(_world.Ui.Screen, Does.Contain("since 17 Oct 2026"));
    }

    /// <summary>Storage is given in the units Google One uses; an account without a limit says so.</summary>
    [TestCase(0L, 15L * 1024 * 1024 * 1024, "Storage: 0 bytes of 15 GB used")]
    [TestCase(512L * 1024 * 1024, 2L * 1024 * 1024 * 1024 * 1024, "Storage: 512 MB of 2 TB used")]
    [TestCase(1_319_413_953_331L, null, "Storage: 1.2 TB used; the account has no limit")]
    public async Task Storage_is_given_in_Google_Ones_units(long usage, long? limit, string line)
    {
        await _world.SignInAsync();
        (_world.Google.StorageUsage, _world.Google.StorageLimit) = (usage, limit);

        await _world.Command().StatusAsync();

        Assert.That(Lines(), Has.Member(line));
    }

    /// <summary>ClassScribe's folder gone from My Drive is said, with what makes it again; the status still ends with 0.</summary>
    [Test]
    public async Task A_folder_gone_from_My_Drive_is_said()
    {
        await _world.SignInAsync();
        _world.Google.Files.Single().Trashed = true;

        Assert.That(await _world.Command().StatusAsync(), Is.EqualTo(0));

        Assert.That(Lines()[^2], Is.EqualTo("The ClassScribe folder is not in My Drive now: it is in the trash, or was deleted. Signing in again makes a new one; the trash is left as it is."));
        Assert.That(_world.Google.Files, Has.Count.EqualTo(1), "the status makes nothing");
    }

    /// <summary>Not set up: the status says how to set up, asks Google nothing, and ends with 1.</summary>
    [Test]
    public async Task The_status_when_not_set_up_says_how_to_set_up()
    {
        Assert.That(await _world.Command().StatusAsync(), Is.EqualTo(1));

        Assert.That(Lines(), Is.EqualTo(new[] { DriveCommand.NotSetUpLine }));
        Assert.That(_world.Google.Requests, Is.Empty);
    }

    /// <summary>Set up and signed out: the status names the client kept, says how to sign in, asks Google nothing, and ends with 1.</summary>
    [Test]
    public async Task The_status_when_signed_out_names_the_client_and_how_to_sign_in()
    {
        _world.Store.Stored = FakeCredentialStore.With(null).Stored;

        Assert.That(await _world.Command().StatusAsync(), Is.EqualTo(1));

        Assert.That(Lines(), Is.EqualTo(new[] { "Google Drive: not signed in (the client ...a1b2c3.apps.googleusercontent.com is kept). Sign in with menu choice 9 > S, or: ClassScribe drive signin" }));
        Assert.That(_world.Google.Requests, Is.Empty);
    }

    /// <summary>A store that cannot be read: the status says not signed in, with the reason as an error, and ends with 1.</summary>
    [Test]
    public async Task The_status_with_an_unreadable_store_gives_the_reason()
    {
        _world.Store.MakeUnreadable("google-drive.dat cannot be opened by this Windows account.");

        Assert.That(await _world.Command().StatusAsync(), Is.EqualTo(1));

        Assert.That(Lines(), Is.EqualTo(new[] { "Google Drive: not signed in." }));
        Assert.That(_world.Logs.Problems, Is.EqualTo(new[] { "google-drive.dat cannot be opened by this Windows account." }));
    }

    /// <summary>Google having ended the sign-in: the status says to sign in again, the dead token is gone, and it ends with 1.</summary>
    [Test]
    public async Task The_status_when_Google_ended_the_sign_in_says_to_sign_in_again()
    {
        _world.Google.RevokeByHand(await _world.SignInAsync());
        _world.Cache.Forget();

        Assert.That(await _world.Command().StatusAsync(), Is.EqualTo(1));

        Assert.That(_world.Logs.Problems, Is.EqualTo(new[] { GoogleTokens.SignInEnded }));
        Assert.That(_world.Store.Stored!.IsSignedIn, Is.False);
    }

    /// <summary>Google out of reach: the status says so, keeps the sign-in, and ends with 1.</summary>
    [Test]
    public async Task The_status_when_Google_cannot_be_reached_keeps_the_sign_in()
    {
        await _world.SignInAsync();
        _world.Google.Fail("/drive/v3/about", new HttpRequestException("The network is unreachable."));

        Assert.That(await _world.Command().StatusAsync(), Is.EqualTo(1));

        Assert.That(_world.Logs.Problems, Is.EqualTo(new[] { "Google Drive could not be reached (The network is unreachable)." }));
        Assert.That(_world.Store.Stored!.IsSignedIn, Is.True);
    }

    /// <summary>A Drive API left disabled: the status names it, and ends with 1.</summary>
    [Test]
    public async Task The_status_names_a_Drive_API_left_disabled()
    {
        await _world.SignInAsync();
        _world.Google.Fail("/drive/v3/about", HttpStatusCode.Forbidden, FakeGoogle.DriveError(403, "accessNotConfigured", "Google Drive API has not been used in project 1 before or it is disabled."));

        Assert.That(await _world.Command().StatusAsync(), Is.EqualTo(1));

        Assert.That(_world.Logs.Problems, Is.EqualTo(new[] { DriveClient.ApiDisabled }));
    }
}
