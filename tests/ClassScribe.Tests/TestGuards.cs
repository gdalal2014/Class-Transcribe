using ClassScribe.Recorder;
using ClassScribe.Storage;

namespace ClassScribe.Tests;

/// <summary>
/// What holds for every test in this assembly, set once before the first: no test may reach the real
/// %APPDATA%\ClassScribe, which holds Gulzar's Google client and sign-in. With it, a test that ran a class through the real
/// app would find him signed in, and the upload after the class would send the test's recording to his own Google Drive.
/// Nor may a test reach the real %LOCALAPPDATA%\ClassScribe, which holds the run files and logs of his real classes and the
/// queue for Google Drive. So the switches <see cref="RoamingFolder.RefuseTheRealFolderSwitch"/> and
/// <see cref="RuntimeFolder.RefuseTheRealFolderSwitch"/> are turned on, and a test that asks for either real folder fails at
/// once, naming what to register instead. Nor may a test run the real yt-dlp, Deno or FFmpeg, which would reach YouTube
/// from his connection: <see cref="YouTube.YouTubeGuards.RefuseTheRealThingsSwitch"/> is turned on too. Nor may a test open a
/// real camera or microphone: <see cref="Desktop.DeviceGuards.RefuseTheRealDevicesSwitch"/> is turned on as well.
/// </summary>
[SetUpFixture]
public sealed class TestGuards
{
    /// <summary>
    /// Turns on the switches that keep every test out of the real %APPDATA%\ClassScribe and %LOCALAPPDATA%\ClassScribe, away
    /// from the real tools of the YouTube feature, and away from the real cameras and microphones.
    /// </summary>
    [OneTimeSetUp]
    public void KeepOutOfTheRealFolders()
    {
        AppContext.SetSwitch(RoamingFolder.RefuseTheRealFolderSwitch, true);
        AppContext.SetSwitch(RuntimeFolder.RefuseTheRealFolderSwitch, true);
        AppContext.SetSwitch(YouTube.YouTubeGuards.RefuseTheRealThingsSwitch, true);
        AppContext.SetSwitch(Desktop.DeviceGuards.RefuseTheRealDevicesSwitch, true);
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
