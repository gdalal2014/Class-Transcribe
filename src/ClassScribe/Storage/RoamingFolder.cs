namespace ClassScribe.Storage;

/// <summary>
/// ClassScribe's folder in the user's roaming application data, %APPDATA%\ClassScribe: it holds the Google client and the
/// Google Drive sign-in, encrypted for the Windows account (<see cref="GoogleDriveFile"/>). On a laptop outside a domain,
/// Roaming does not roam. Nothing is made until a sign-in is stored, and tests give a temporary folder instead: a test run
/// turns on <see cref="RefuseTheRealFolderSwitch"/>, so that a test that forgets fails rather than reading the real sign-in.
/// </summary>
/// <param name="root">The folder to use instead, for tests; null for %APPDATA%\ClassScribe.</param>
public sealed class RoamingFolder(string? root = null)
{
    /// <summary>The name of the file that holds the Google client and sign-in.</summary>
    public const string GoogleDriveFileName = "google-drive.dat";

    /// <summary>
    /// The AppContext switch a test run turns on: with it on, the real %APPDATA%\ClassScribe is refused, and only a folder
    /// given is used. The real folder holds the Google sign-in, with which a test would reach the real Google Drive, and the
    /// upload after a class would send the test's recording there. ClassScribe itself never turns it on.
    /// </summary>
    public const string RefuseTheRealFolderSwitch = "ClassScribe.RoamingFolder.RefuseTheRealFolder";

    /// <summary>The folder, as a full path.</summary>
    public string Root { get; } = root is null ? DefaultRoot() : Path.GetFullPath(root);

    /// <summary>The file that holds the Google client and sign-in, encrypted for the Windows account.</summary>
    public string GoogleDriveFile => Path.Combine(Root, GoogleDriveFileName);

    /// <summary>%APPDATA%\ClassScribe for the user ClassScribe runs as; refused while <see cref="RefuseTheRealFolderSwitch"/> is on.</summary>
    private static string DefaultRoot()
    {
        if (AppContext.TryGetSwitch(RefuseTheRealFolderSwitch, out var refused) && refused)
        {
            throw new InvalidOperationException(
                "A test asked for the real %APPDATA%\\ClassScribe, which holds the Google sign-in. Register new RoamingFolder(<a temporary folder>), or a FakeCredentialStore as the IGoogleCredentialStore, instead.");
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify);
        if (string.IsNullOrEmpty(appData))
            throw new InvalidOperationException("Windows did not say where this user's application data folder (%APPDATA%) is, so ClassScribe has nowhere to keep its Google Drive sign-in.");

        return Path.Combine(appData, "ClassScribe");
    }
}
