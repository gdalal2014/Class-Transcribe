using ClassScribe.Desktop;
using ClassScribe.Recorder;
using ClassScribe.Settings;
using ClassScribe.Storage;
using ClassScribe.YouTube;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClassScribe.Tests;

/// <summary>
/// What holds for every test in this assembly, set once before the first: no test may reach the real
/// %APPDATA%\ClassScribe, which holds Gulzar's Google client and sign-in. With it, a test that ran a class through the real
/// app would find him signed in, and the upload after the class would send the test's recording to his own Google Drive.
/// Nor may a test reach the real %LOCALAPPDATA%\ClassScribe, which holds the run files and logs of his real classes and the
/// queue for Google Drive. Nor the real %USERPROFILE%\ClassScribe, which holds his classes with his PDFs and notes beside
/// them, and where a test that forgets --output would record or list. So the switches
/// <see cref="RoamingFolder.RefuseTheRealFolderSwitch"/>, <see cref="RuntimeFolder.RefuseTheRealFolderSwitch"/> and
/// <see cref="ClassFolders.RefuseTheRealFolderSwitch"/> are turned on, and a test that asks for any of the three real
/// folders fails at once, naming what to give instead. Nor may a test run the real yt-dlp, Deno or FFmpeg, which would
/// reach YouTube from his connection: <see cref="YouTubeGuards.RefuseTheRealThingsSwitch"/> is turned on too. Nor may a
/// test open a real camera or microphone: <see cref="DeviceGuards.RefuseTheRealDevicesSwitch"/> is turned on as well.
/// </summary>
[SetUpFixture]
public sealed class TestGuards
{
    /// <summary>
    /// Turns on the switches that keep every test out of the real %APPDATA%\ClassScribe, %LOCALAPPDATA%\ClassScribe and
    /// %USERPROFILE%\ClassScribe, away from the real tools of the YouTube feature, and away from the real cameras and
    /// microphones.
    /// </summary>
    [OneTimeSetUp]
    public void KeepOutOfTheRealFolders()
    {
        AppContext.SetSwitch(RoamingFolder.RefuseTheRealFolderSwitch, true);
        AppContext.SetSwitch(RuntimeFolder.RefuseTheRealFolderSwitch, true);
        AppContext.SetSwitch(ClassFolders.RefuseTheRealFolderSwitch, true);
        AppContext.SetSwitch(YouTubeGuards.RefuseTheRealThingsSwitch, true);
        AppContext.SetSwitch(DeviceGuards.RefuseTheRealDevicesSwitch, true);
    }
}

/// <summary>
/// The folder that holds the recordings: a test gives its own, and asking for the real %USERPROFILE%\ClassScribe, or for a
/// folder in it, fails, while the settings' warnings may still name it.
/// </summary>
[TestFixture]
public class RecordingsFolderGuardTests
{
    /// <summary>
    /// In a test, resolving the recordings folder to the real %USERPROFILE%\ClassScribe throws, however the setting writes
    /// it (empty, null, the variable, a relative name, quotes, another letter case, a course folder in it), and says what to
    /// give instead, so that no test can record or list there.
    /// </summary>
    [TestCase("")]
    [TestCase(null)]
    [TestCase("%USERPROFILE%\\ClassScribe")]
    [TestCase("%USERPROFILE%\\ClassScribe\\")]
    [TestCase("ClassScribe")]
    [TestCase("\"%USERPROFILE%\\classscribe\"")]
    [TestCase("%USERPROFILE%\\ClassScribe\\Mantiq")]
    public void A_test_cannot_reach_the_real_recordings_folder(string? outputFolder)
    {
        var refused = Assert.Throws<InvalidOperationException>(() => ClassFolders.RootFor(outputFolder));

        Assert.That(refused!.Message, Does.Contain("%USERPROFILE%\\ClassScribe").And.Contain("TempFolder"));
    }

    /// <summary>The folders' own root, read from the settings, is refused the same way for the shipped default.</summary>
    [Test]
    public void The_folders_root_over_the_shipped_default_is_refused()
    {
        var folders = new ClassFolders(TestOptions.Create(new RecordingOptions()), TimeProvider.System, NullLogger<ClassFolders>.Instance);

        Assert.Throws<InvalidOperationException>(() => _ = folders.Root);
    }

    /// <summary>A folder beside the real one, as the dry runs' is, or a temporary folder, is used as given.</summary>
    [Test]
    public void A_folder_beside_the_real_one_is_used()
    {
        using var temp = new TempFolder();
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        Assert.Multiple(() =>
        {
            Assert.That(ClassFolders.RootFor("%USERPROFILE%\\ClassScribe dry runs"), Is.EqualTo(Path.Combine(profile, "ClassScribe dry runs")));
            Assert.That(ClassFolders.RootFor(temp.Path), Is.EqualTo(temp.Path));
        });
    }

    /// <summary>
    /// The folder a setting names is still worked out without refusal, for the settings' warnings, which only say where it
    /// is: an empty setting names a ClassScribe folder in the user profile.
    /// </summary>
    [Test]
    public void The_real_folder_is_still_named_for_the_settings_warnings()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        Assert.Multiple(() =>
        {
            Assert.That(ClassFolders.RootNamedBy(""), Is.EqualTo(Path.Combine(profile, "ClassScribe")));
            Assert.That(ClassFolders.RootNamedBy(null), Is.EqualTo(Path.Combine(profile, "ClassScribe")));
            Assert.That(new SettingsCheck().FindWarnings(new ConfigurationBuilder().Build()), Is.Empty, "the shipped default names a folder of its own, so the warnings have nothing to say");
        });
    }
}

/// <summary>The folder that holds the Google sign-in: a test gives its own, and asking for the real one fails.</summary>
[TestFixture]
public class RoamingFolderTests
{
    /// <summary>A folder given is used, as a full path, with the store's file in it.</summary>
    [Test]
    public void A_folder_given_is_used()
    {
        using var temp = new TempFolder();

        var folder = new RoamingFolder(temp.Path);

        Assert.That(folder.Root, Is.EqualTo(temp.Path));
        Assert.That(folder.GoogleDriveFile, Is.EqualTo(temp.File(RoamingFolder.GoogleDriveFileName)));
    }

    /// <summary>
    /// In a test, asking for the real %APPDATA%\ClassScribe throws, and says what to register instead, so that no test can
    /// read the real sign-in and reach the real Google Drive with it.
    /// </summary>
    [Test]
    public void A_test_cannot_reach_the_real_folder()
    {
        var refused = Assert.Throws<InvalidOperationException>(() => _ = new RoamingFolder());

        Assert.That(refused!.Message, Does.Contain("new RoamingFolder("));
        Assert.That(refused.Message, Does.Contain("FakeCredentialStore"));
    }
}
