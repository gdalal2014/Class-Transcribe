using ClassScribe.Commands;
using ClassScribe.Drive;
using ClassScribe.Storage;
using Moq;

namespace ClassScribe.Tests;

/// <summary>
/// Menu choice 9, Google Drive: the sign-in's state from the store alone (nothing is asked of Google to show it), and the
/// keys it offers: S signs in, asking for the client file (Enter alone keeps the client already kept); while signed in, U
/// uploads what waits, R one of the recent recordings, F a folder or file dragged in, L frees room once a recording is on
/// Drive (asked with Y first), I checks with Google and O signs out; Esc goes back. What each does goes to the runner,
/// which runs the drive command in a scope of its own. The recordings and the manifests are in temporary folders.
/// </summary>
[TestFixture]
public class DriveMenuTests
{
    private GoogleWorld _world = null!;
    private FakeCredentialStore _store = null!;
    private FakeConsoleUi _ui = null!;
    private Mock<ICommandRunner> _commands = null!;

    /// <summary>Nothing stored, a console the test types into, and a runner whose commands succeed.</summary>
    [SetUp]
    public void SetUp()
    {
        _world = new GoogleWorld();
        _store = new FakeCredentialStore();
        _ui = new FakeConsoleUi();
        _commands = new Mock<ICommandRunner>();
        _commands.Setup(c => c.DriveSignInAsync(It.IsAny<string?>())).ReturnsAsync(0);
        _commands.Setup(c => c.DriveSignOutAsync()).ReturnsAsync(0);
        _commands.Setup(c => c.DriveStatusAsync()).ReturnsAsync(0);
        _commands.Setup(c => c.DriveUploadAsync(It.IsAny<string?>())).ReturnsAsync(0);
        _commands.Setup(c => c.DriveFreeSpaceAsync(It.IsAny<string>())).ReturnsAsync(0);
    }

    /// <summary>Deletes the temporary folders.</summary>
    [TearDown]
    public void TearDown() => _world.Dispose();

    /// <summary>Runs the menu, failing the test rather than waiting if it waits for a key that never comes.</summary>
    private Task<int> RunAsync() =>
        new DriveMenu(_commands.Object, _store, _world.ClassFolders(), _world.Manifests(), new DriveQueueReport(_store, _world.Manifests(), _world.Runtime.Journal, _world.Options, ZonedClock.Eastern(_world.Time)),
            _ui, ZonedTimeProvider.EasternSummer).RunAsync().WaitAsync(TimeSpan.FromSeconds(15));

    /// <summary>Signs the store in.</summary>
    private void SignIn() => _store.Stored = FakeCredentialStore.With("1//0gToken").Stored;

    /// <summary>A class of Mantiq on 17 October in its course and date folder, with its transcript, as the tests' recording.</summary>
    private RecordingFiles Mantiq() =>
        _world.WriteRecording(@"Mantiq\2026-10-17", "Mantiq", new DateTimeOffset(2026, 10, 17, 14, 4, 42, TimeSpan.Zero), 2000, ".md", ".json");

    /// <summary>Notes <paramref name="files"/> as <paramref name="state"/> in its manifest, with its folder on Drive.</summary>
    private void Note(RecordingFiles files, UploadState state) =>
        _world.Manifests().Write(new UploadManifest(files.Folder, files.BaseName) { State = state, DriveFolderId = "folder-9", DrivePath = "ClassScribe/Mantiq/2026-10-17" });

    /// <summary>
    /// Signed in, the keys of the uploads come first, U with what waits; Esc goes back without a call. One recording waiting
    /// is counted, with the size of its files here.
    /// </summary>
    [Test]
    public async Task Signed_in_offers_the_uploads_and_free_space()
    {
        SignIn();
        Note(Mantiq(), UploadState.Waiting);
        _ui.Type('\u001b');

        Assert.That(await RunAsync(), Is.EqualTo(0));

        Assert.That(Lines(), Is.EqualTo(new[]
        {
            "Google Drive: signed in as student@example.com since 15 Oct 2026.",
            "  U  Upload what waits to go up (1 recording, 2.1 KB)",
            "  R  Upload a recent recording",
            "  F  Upload a folder or a recording's file (drag it into this window, or type its path)",
            "  L  Free room on this laptop: delete a recording's audio once Google Drive has it, checked",
            "  I  Check with Google: the account, the storage used and ClassScribe's folder on Drive",
            "  S  Sign in again",
            "  O  Sign out",
            "Press a key, or Esc to go back."
        }));
        _commands.VerifyNoOtherCalls();
    }

    /// <summary>
    /// A class being recorded now, which its own run put in the queue as it began, is not counted on U's line, as the menu's
    /// header and the report at the start do not count it: it goes up once it has stopped.
    /// </summary>
    [Test]
    public async Task U_does_not_count_a_class_being_recorded()
    {
        SignIn();
        var mantiq = Mantiq();
        _world.Manifests().Queue(mantiq);
        _world.Runtime.Processes.Start(TestRuns.ProcessId, TestRuns.ProcessStart);
        _world.Runtime.Journal.Begin(TestRuns.Record(TestRuns.Id(), ClassScribe.Recorder.RunState.Recording, name: "Mantiq") with { BaseName = mantiq.BaseName, Folder = mantiq.Folder });
        _ui.Type('\u001b');

        await RunAsync();

        Assert.That(Lines(), Has.Member("  U  Upload what waits to go up (nothing waits now)"));
    }

    /// <summary>U uploads what waits in the queue, all of it; nothing waiting is said on its line.</summary>
    [Test]
    public async Task U_uploads_what_waits()
    {
        SignIn();
        _ui.Type('u');

        await RunAsync();

        Assert.That(Lines(), Has.Member("  U  Upload what waits to go up (nothing waits now)"));
        _commands.Verify(c => c.DriveUploadAsync(null), Times.Once);
        _commands.VerifyNoOtherCalls();
    }

    /// <summary>
    /// R lists the recent recordings, each with where it stands with Drive, and uploads the one picked, by its audio file.
    /// </summary>
    [Test]
    public async Task R_lists_the_recent_recordings_with_their_Drive_state_and_uploads_the_one_picked()
    {
        SignIn();
        var kalam = _world.WriteRecording("Kalam", "Kalam", new DateTimeOffset(2026, 10, 15, 23, 0, 2, TimeSpan.Zero), 2000);
        var fiqh = _world.WriteRecording(@"Fiqh\2026-10-16", "Fiqh", new DateTimeOffset(2026, 10, 16, 23, 0, 2, TimeSpan.Zero), 2000);
        var mantiq = Mantiq();
        File.SetLastWriteTimeUtc(kalam.Segment(1), new DateTime(2026, 10, 16, 1, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(fiqh.Segment(1), new DateTime(2026, 10, 17, 1, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(mantiq.Segment(1), new DateTime(2026, 10, 17, 16, 0, 0, DateTimeKind.Utc));
        Note(mantiq, UploadState.Done);
        Note(fiqh, UploadState.Waiting);
        _ui.Type('r', '3');

        await RunAsync();

        Assert.That(Lines(), Is.SupersetOf(new[]
        {
            "Recent recordings, newest first:",
            "  1  Mantiq_2026-10-17_14-04-42Z  00:00:00  on Drive",
            "  2  Fiqh_2026-10-16_23-00-02Z  00:00:00  waiting",
            "  3  Kalam_2026-10-15_23-00-02Z  00:00:00  not uploaded",
            "Press a recording's number to upload it, or Esc to go back."
        }));
        _commands.Verify(c => c.DriveUploadAsync(kalam.Segment(1)), Times.Once);
    }

    /// <summary>F uploads the folder or file dragged in, without the quotes Windows puts around it; one not there is said, and nothing runs.</summary>
    [Test]
    public async Task F_uploads_the_path_given()
    {
        SignIn();
        var mantiq = Mantiq();
        _ui.Type('f');
        _ui.TypeLine($" \"{mantiq.Folder}\" ");

        await RunAsync();

        _commands.Verify(c => c.DriveUploadAsync(mantiq.Folder), Times.Once);

        _ui.Type('F');
        _ui.TypeLine(Path.Combine(mantiq.Folder, "nothing here"));
        await RunAsync();

        Assert.That(Lines(), Has.Member($"Nothing found at {Path.Combine(mantiq.Folder, "nothing here")}"));
        _commands.Verify(c => c.DriveUploadAsync(It.IsAny<string?>()), Times.Once);
    }

    /// <summary>
    /// L, for a recording on Drive, names the audio files it would delete and what stays, and frees them on Y, by the
    /// recording's first audio file; any other key goes back without a call.
    /// </summary>
    [Test]
    public async Task L_frees_a_recording_on_Drive_after_Y()
    {
        SignIn();
        var mantiq = Mantiq();
        Note(mantiq, UploadState.Done);
        _ui.Type('l', '1', 'n');

        await RunAsync();

        _commands.Verify(c => c.DriveFreeSpaceAsync(It.IsAny<string>()), Times.Never);
        Assert.That(Lines(), Is.SupersetOf(new[]
        {
            "This deletes from this laptop the audio of Mantiq_2026-10-17_14-04-42Z: Mantiq_2026-10-17_14-04-42Z.wav (2 KB).",
            "Each file is first checked again against its copy on Google Drive, in My Drive > ClassScribe > Mantiq > 2026-10-17, which stays as it is. The transcripts, ElevenLabs' answers, the marks and the log stay here.",
            "Press Y to delete the audio from this laptop, or any other key to go back."
        }));

        _ui.Type('L', '1', 'y');
        await RunAsync();

        _commands.Verify(c => c.DriveFreeSpaceAsync(mantiq.Segment(1)), Times.Once);
    }

    /// <summary>L, for a recording that is not all on Drive yet, says so and deletes nothing.</summary>
    [TestCase(UploadState.Waiting)]
    [TestCase(null)]
    public async Task L_keeps_the_audio_of_a_recording_not_on_Drive(UploadState? state)
    {
        SignIn();
        var mantiq = Mantiq();
        if (state is { } known) Note(mantiq, known);
        _ui.Type('l', '1');

        await RunAsync();

        Assert.That(Lines(), Has.Member("Mantiq_2026-10-17_14-04-42Z is not all on Google Drive yet, so its audio stays here. Upload it first: R, then its number."));
        _commands.Verify(c => c.DriveFreeSpaceAsync(It.IsAny<string>()), Times.Never);
    }

    /// <summary>Without a sign-in, U, R, F and L do nothing, as O and I do not.</summary>
    [Test]
    public async Task Without_a_sign_in_the_uploads_wait()
    {
        _store.Stored = FakeCredentialStore.With(null).Stored;
        Mantiq();
        _ui.Type('u', 'r', 'f', 'l', '\u001b');

        Assert.That(await RunAsync(), Is.EqualTo(0));

        Assert.That(Lines(), Has.No.Member("  U  Upload what waits to go up (nothing waits now)"));
        _commands.VerifyNoOtherCalls();
    }

    /// <summary>The lines shown, without empty ones.</summary>
    private string[] Lines() => _ui.Screen.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Before the first sign-in, it says Drive is not set up and where the steps are, and offers S alone.</summary>
    [Test]
    public async Task Not_set_up_offers_a_sign_in_with_the_client_file()
    {
        _ui.Type('\u001b');

        Assert.That(await RunAsync(), Is.EqualTo(0));

        Assert.That(Lines(), Is.EqualTo(new[]
        {
            "Google Drive: not set up. Sign in once with the client file you made in your Google Cloud project; the README's \"Google Drive (set up once)\" has the steps.",
            "  S  Sign in with the client file you downloaded from Google",
            "Press a key, or Esc to go back."
        }));
        _commands.VerifyNoOtherCalls();
    }

    /// <summary>S asks for the client file, dragged in or typed; the path goes to the sign-in without the quotes Windows puts around it.</summary>
    [Test]
    public async Task S_signs_in_with_the_client_file_given()
    {
        _ui.Type('s');
        _ui.TypeLine("  \"C:\\Users\\Student\\Downloads\\client_secret_1.json\" ");

        Assert.That(await RunAsync(), Is.EqualTo(0));

        Assert.That(_ui.Screen, Does.Contain("Drag the client file you downloaded from Google (client_secret_....json, in your Downloads folder) into this window, or type its path, then press Enter. Esc goes back."));
        _commands.Verify(c => c.DriveSignInAsync(@"C:\Users\Student\Downloads\client_secret_1.json"), Times.Once);
        _commands.VerifyNoOtherCalls();
    }

    /// <summary>Before the first sign-in, Enter alone or Esc at the question goes back: there is no client to sign in with.</summary>
    [TestCase("")]
    [TestCase(null)]
    public async Task Without_a_client_Enter_alone_or_Esc_goes_back(string? line)
    {
        _ui.Type('S');
        _ui.TypeLine(line);

        Assert.That(await RunAsync(), Is.EqualTo(0));

        _commands.VerifyNoOtherCalls();
    }

    /// <summary>Set up and signed out: the client kept is named, S signs in again, and Enter alone keeps that client.</summary>
    [Test]
    public async Task Signed_out_S_with_Enter_alone_keeps_the_client()
    {
        _store.Stored = FakeCredentialStore.With(null).Stored;
        _ui.Type('s');
        _ui.TypeLine("");

        await RunAsync();

        Assert.That(Lines()[0], Is.EqualTo("Google Drive: not signed in (the client ...a1b2c3.apps.googleusercontent.com is kept)."));
        Assert.That(Lines(), Has.Member("  S  Sign in").And.No.Member("  O  Sign out"));
        Assert.That(_ui.Screen, Does.Contain("press Enter alone to sign in with the client ClassScribe keeps (...a1b2c3.apps.googleusercontent.com)"));
        _commands.Verify(c => c.DriveSignInAsync(null), Times.Once);
    }

    /// <summary>Signed in: the account and the local date, and I, S and O; O signs out and I checks with Google.</summary>
    [TestCase('o')]
    [TestCase('I')]
    public async Task Signed_in_offers_a_check_another_sign_in_and_a_sign_out(char key)
    {
        _store.Stored = FakeCredentialStore.With("1//0gToken").Stored! with { SignedInUtc = new DateTimeOffset(2026, 10, 18, 1, 30, 0, TimeSpan.Zero) };
        _ui.Type(key);

        await RunAsync();

        Assert.That(Lines()[0], Is.EqualTo("Google Drive: signed in as student@example.com since 17 Oct 2026."));
        Assert.That(Lines(), Is.SupersetOf(new[]
        {
            "  I  Check with Google: the account, the storage used and ClassScribe's folder on Drive",
            "  S  Sign in again",
            "  O  Sign out",
            "Press a key, or Esc to go back."
        }));
        if (char.ToUpperInvariant(key) == 'O') _commands.Verify(c => c.DriveSignOutAsync(), Times.Once);
        else _commands.Verify(c => c.DriveStatusAsync(), Times.Once);
        _commands.VerifyNoOtherCalls();
    }

    /// <summary>O and I do nothing until there is a sign-in; other keys are ignored; Q goes back as Esc does.</summary>
    [Test]
    public async Task O_and_I_wait_for_a_sign_in_and_other_keys_are_ignored()
    {
        _ui.Type('o', 'i', 'x', '5', 'q');

        Assert.That(await RunAsync(), Is.EqualTo(0));

        _commands.VerifyNoOtherCalls();
    }

    /// <summary>A store that cannot be read says why, and S signs in with a client file.</summary>
    [Test]
    public async Task An_unreadable_store_says_why()
    {
        _store.MakeUnreadable("google-drive.dat cannot be opened by this Windows account.");
        _ui.Type('\u001b');

        await RunAsync();

        Assert.That(Lines()[0], Is.EqualTo("Google Drive: not signed in. google-drive.dat cannot be opened by this Windows account."));
    }

    /// <summary>The exit code of what was run comes back.</summary>
    [Test]
    public async Task The_exit_code_of_what_was_run_comes_back()
    {
        _store.Stored = FakeCredentialStore.With("1//0gToken").Stored;
        _commands.Setup(c => c.DriveSignOutAsync()).ReturnsAsync(1);
        _ui.Type('O');

        Assert.That(await RunAsync(), Is.EqualTo(1));
    }
}
