using ClassScribe.Settings;
using ClassScribe.YouTube;

namespace ClassScribe.Tests;

/// <summary>
/// Finding Chrome for the record path's video window (design 4.1): YouTube:BrowserPath; else the (Default) value of Chrome's
/// App Paths key, under HKEY_LOCAL_MACHINE and then HKEY_CURRENT_USER; else Program Files, Program Files (x86) and
/// %LOCALAPPDATA%, each followed by Google\Chrome\Application\chrome.exe; none, and it says where it looked and what to do.
/// The registry and the disk are played, so no test reads the real ones; and Chrome's version is read from its file.
/// </summary>
[TestFixture]
public class BrowserLocatorTests
{
    private const string ProgramFiles = @"C:\Program Files";
    private const string ProgramFilesX86 = @"C:\Program Files (x86)";
    private const string LocalAppData = @"C:\Users\gdala\AppData\Local";
    private const string Machine = @"C:\Program Files\Google\Chrome\Application\chrome.exe";

    private FakeRegistryValues _registry = null!;
    private FakeFileProbe _files = null!;
    private TestOptions<YouTubeOptions> _options = null!;
    private RecordingLogger<BrowserLocator> _log = null!;

    /// <summary>An empty registry and disk, and the settings' defaults.</summary>
    [SetUp]
    public void SetUp()
    {
        _registry = new FakeRegistryValues();
        _files = new FakeFileProbe();
        _options = TestOptions.Create(new YouTubeOptions());
        _log = new RecordingLogger<BrowserLocator>();
    }

    /// <summary>The locator under test, with the played folders.</summary>
    private BrowserLocator Locator() =>
        new(_registry, _files, _options, _log) { ProgramFiles = ProgramFiles, ProgramFilesX86 = ProgramFilesX86, LocalAppData = LocalAppData };

    /// <summary>The path in YouTube:BrowserPath comes first, quotes around it gone, whatever else is there.</summary>
    [Test]
    public void The_setting_comes_first()
    {
        _options.Set(new YouTubeOptions { BrowserPath = "\"D:\\Chrome\\chrome.exe\"" });
        _files.Add(@"D:\Chrome\chrome.exe", Machine);
        _registry.SetLocalMachine(BrowserLocator.AppPathsKey, "", Machine);

        Assert.That(Locator().Find(), Is.EqualTo(@"D:\Chrome\chrome.exe"));
    }

    /// <summary>A setting that names a file no longer there finds nothing: it was chosen, so nothing else is taken in its place.</summary>
    [Test]
    public void A_setting_whose_file_is_gone_finds_nothing()
    {
        _options.Set(new YouTubeOptions { BrowserPath = @"D:\Chrome\chrome.exe" });
        _files.Add(Machine);

        Assert.That(Locator().Find(), Is.Null);
    }

    /// <summary>Without a setting, the (Default) value of Chrome's App Paths key under HKEY_LOCAL_MACHINE, as Chrome's installer writes it.</summary>
    [Test]
    public void The_machines_App_Paths_come_next()
    {
        _registry.SetLocalMachine(BrowserLocator.AppPathsKey, "", @"E:\Apps\Chrome\chrome.exe");
        _registry.Set(BrowserLocator.AppPathsKey, "", @"F:\Mine\chrome.exe");
        _files.Add(@"E:\Apps\Chrome\chrome.exe", @"F:\Mine\chrome.exe", Machine);

        Assert.That(Locator().Find(), Is.EqualTo(@"E:\Apps\Chrome\chrome.exe"));
    }

    /// <summary>Then the user's App Paths, as a per-user install writes them, in quotes or not.</summary>
    [Test]
    public void The_users_App_Paths_come_after_the_machines()
    {
        _registry.Set(BrowserLocator.AppPathsKey, "", "\"F:\\Mine\\chrome.exe\"");
        _files.Add(@"F:\Mine\chrome.exe", Machine);

        Assert.That(Locator().Find(), Is.EqualTo(@"F:\Mine\chrome.exe"));
    }

    /// <summary>An App Paths value whose file is not there, or that is no full path to an .exe, is passed over.</summary>
    [TestCase(@"E:\Gone\chrome.exe")]
    [TestCase("chrome.exe")]
    [TestCase(@"E:\Apps\chrome.lnk")]
    public void An_App_Paths_value_that_names_no_exe_there_is_passed_over(string value)
    {
        _registry.SetLocalMachine(BrowserLocator.AppPathsKey, "", value);
        _files.Add(@"E:\Apps\chrome.lnk", Machine);

        Assert.That(Locator().Find(), Is.EqualTo(Machine));
    }

    /// <summary>Then the three folders, in order: Program Files, Program Files (x86), and %LOCALAPPDATA%.</summary>
    [TestCase(ProgramFiles)]
    [TestCase(ProgramFilesX86)]
    [TestCase(LocalAppData)]
    public void The_folders_come_last_in_order(string folder)
    {
        var chrome = Path.Combine(folder, "Google", "Chrome", "Application", "chrome.exe");
        _files.Add(chrome, Path.Combine(LocalAppData, "Google", "Chrome", "Application", "chrome.exe"));

        Assert.That(Locator().Find(), Is.EqualTo(chrome));
    }

    /// <summary>A registry that cannot be read is passed over, said at Debug; the folders are still looked in.</summary>
    [Test]
    public void A_registry_that_cannot_be_read_is_passed_over()
    {
        _registry.Failure = new UnauthorizedAccessException("Access is denied.");
        _files.Add(Machine);

        Assert.That(Locator().Find(), Is.EqualTo(Machine));
        Assert.That(_log.Problems, Is.Empty);
    }

    /// <summary>Found nowhere: nothing, and the words say where it was looked for and what to do.</summary>
    [Test]
    public void Found_nowhere_finds_nothing_and_says_what_to_do()
    {
        Assert.That(Locator().Find(), Is.Null);
        Assert.That(IToolLocator.BrowserNotFound,
            Is.EqualTo("Chrome was not found (YouTube:BrowserPath, App Paths, Program Files, LOCALAPPDATA). Install it, or set YouTube:BrowserPath to its chrome.exe"));
    }

    /// <summary>Chrome's version is its file's product version, as Windows shows it; null when the file does not say.</summary>
    [Test]
    public void Chromes_version_is_read_from_its_file()
    {
        _files.Add(Machine);
        _files.Versions[Machine] = "141.0.7390.108";

        Assert.That(Locator().VersionOf(Machine), Is.EqualTo("Chrome 141.0.7390.108"));
        Assert.That(Locator().VersionOf(@"D:\Other\chrome.exe"), Is.EqualTo("Chrome"));
    }
}
