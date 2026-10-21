using ClassScribe.Settings;
using Microsoft.Extensions.Configuration;

namespace ClassScribe.Tests;

/// <summary>
/// The check's warnings about where the recordings go (layout-config 2): a drive's root, the user folder itself, a folder
/// holding AppData or Windows, and a folder inside another program's sync folder (Google Drive for desktop, by its drive's
/// label or a path through My Drive, and Dropbox, by the folders its info.json names), for Recording:OutputFolder and for a
/// saved class's own folder. Warnings only: none of these refuses the settings. The folders' facts are fakes, so nothing on
/// this computer is looked at.
/// </summary>
[TestFixture]
public class OutputFolderWarningsTests
{
    private FakeOutputFolderFacts _facts = null!;

    /// <summary>No folder, no label and no Dropbox known.</summary>
    [SetUp]
    public void SetUp() => _facts = new FakeOutputFolderFacts();

    /// <summary>The check's warnings for <paramref name="settings"/>, over the fake facts.</summary>
    private IReadOnlyList<string> Warnings(Dictionary<string, string?> settings) =>
        new SettingsCheck(_facts).FindWarnings(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());

    /// <summary>The check's problems for <paramref name="settings"/>, over the fake facts.</summary>
    private IReadOnlyList<string> Problems(Dictionary<string, string?> settings) =>
        new SettingsCheck(_facts).FindProblems(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());

    /// <summary>A drive's root brings a warning that names a folder of its own on that drive, and is not refused.</summary>
    [Test]
    public void A_drives_root_brings_a_warning()
    {
        var settings = new Dictionary<string, string?> { ["Recording:OutputFolder"] = @"D:\" };

        Assert.That(Warnings(settings), Is.EqualTo(new[]
        {
            @"Recording:OutputFolder is the root of a drive (D:\). Each course's folder would sit among the drive's own folders; a folder of its own, such as D:\ClassScribe, keeps the classes together"
        }));
        Assert.That(Problems(settings), Is.Empty);
    }

    /// <summary>The user folder itself brings a warning, however it is written.</summary>
    [TestCase("%USERPROFILE%")]
    [TestCase("%USERPROFILE%\\")]
    [TestCase(".")]
    public void The_user_folder_itself_brings_a_warning(string folder)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        Assert.That(Warnings(new() { ["Recording:OutputFolder"] = folder }), Is.EqualTo(new[]
        {
            $"Recording:OutputFolder is your user folder itself ({profile}). Each course's folder would sit beside Documents, Desktop and AppData; a folder of its own, such as %USERPROFILE%\\ClassScribe, keeps the classes together"
        }));
    }

    /// <summary>A folder that holds AppData, as a user folder does, or Windows, as Windows' own drive does, brings a warning.</summary>
    [TestCase("AppData", "as a user folder does")]
    [TestCase("Windows", "as Windows' own drive does")]
    public void A_folder_holding_AppData_or_Windows_brings_a_warning(string name, string why)
    {
        using var folder = new TempFolder();
        _facts.Folders.Add(Path.Combine(folder.Path, name));

        Assert.That(Warnings(new() { ["Recording:OutputFolder"] = folder.Path }), Is.EqualTo(new[]
        {
            $"Recording:OutputFolder ({folder.Path}) holds a folder named {name}, {why}. Each course's folder would sit beside it; a folder of its own, such as {Path.Combine(folder.Path, "ClassScribe")}, keeps the classes together"
        }));
    }

    /// <summary>
    /// Google Drive for desktop: a drive labelled Google Drive, or a path through My Drive (its folder when it mirrors), brings
    /// a warning that the recordings would go up twice; a folder whose name only starts like My Drive does not.
    /// </summary>
    [Test]
    public void A_folder_inside_Google_Drive_brings_a_warning()
    {
        _facts.Labels[@"G:\"] = "Google Drive";
        var mirrored = @"C:\Users\someone\My Drive\Classes";

        var onTheDrive = Warnings(new() { ["Recording:OutputFolder"] = @"G:\Shared drives\Classes" });
        var throughMyDrive = Warnings(new() { ["Recording:OutputFolder"] = mirrored });

        Assert.That(onTheDrive, Is.EqualTo(new[]
        {
            @"Recording:OutputFolder is inside Google Drive (G:\Shared drives\Classes). Google Drive for desktop uploads a recording again and again while it grows, and ClassScribe's own copy on Google Drive would put each class there a second time; a folder outside Google Drive, such as %USERPROFILE%\ClassScribe, is safer"
        }));
        Assert.That(throughMyDrive, Has.Count.EqualTo(1));
        Assert.That(throughMyDrive[0], Does.StartWith($"Recording:OutputFolder is inside Google Drive ({mirrored})."));
        Assert.That(Warnings(new() { ["Recording:OutputFolder"] = @"C:\Users\someone\My Drives\Classes" }), Is.Empty);
        Assert.That(Warnings(new() { ["Recording:OutputFolder"] = @"H:\Classes" }), Is.Empty, "another drive, with another label");
    }

    /// <summary>A folder under one that Dropbox's info.json names brings a warning; one beside it, whose name only starts the same, does not.</summary>
    [Test]
    public void A_folder_inside_Dropbox_brings_a_warning()
    {
        _facts.Dropbox.Add(@"C:\Users\someone\Dropbox");

        Assert.That(Warnings(new() { ["Recording:OutputFolder"] = @"C:\Users\someone\Dropbox\Classes" }), Is.EqualTo(new[]
        {
            @"Recording:OutputFolder is inside Dropbox (C:\Users\someone\Dropbox\Classes). Dropbox uploads a recording again and again while it grows, and can hold files that ClassScribe needs to delete, and with ClassScribe's own copy on Google Drive each class would go up to the cloud twice; a folder outside Dropbox, such as %USERPROFILE%\ClassScribe, is safer"
        }));
        Assert.That(Warnings(new() { ["Recording:OutputFolder"] = @"C:\Users\someone\Dropbox (Old)\Classes" }), Is.Empty);
    }

    /// <summary>A sync folder's warning comes beside a placement's: a Google Drive root is both.</summary>
    [Test]
    public void A_google_drive_root_brings_both_warnings()
    {
        _facts.Labels[@"G:\"] = "Google Drive";

        var warnings = Warnings(new() { ["Recording:OutputFolder"] = @"G:\" });

        Assert.That(warnings, Has.Count.EqualTo(2));
        Assert.That(warnings[0], Does.StartWith(@"Recording:OutputFolder is the root of a drive (G:\)."));
        Assert.That(warnings[1], Does.StartWith(@"Recording:OutputFolder is inside Google Drive (G:\)."));
    }

    /// <summary>A saved class's own folder is looked at the same way, named by the class's setting.</summary>
    [Test]
    public void A_saved_classs_own_folder_is_warned_about_too()
    {
        _facts.Dropbox.Add(@"E:\Dropbox");

        var warnings = Warnings(new()
        {
            ["Classes:Mantiq:Course"] = "Mantiq",
            ["Classes:Mantiq:App"] = "chrome",
            ["Classes:Mantiq:OutputFolder"] = @"E:\Dropbox\Mantiq",
            ["Classes:Kalam:Course"] = "Kalam",
            ["Classes:Kalam:App"] = "chrome"
        });

        Assert.That(warnings, Has.Count.EqualTo(1));
        Assert.That(warnings[0], Does.StartWith(@"Classes:Mantiq:OutputFolder is inside Dropbox (E:\Dropbox\Mantiq)."));
    }

    /// <summary>The usual folders bring nothing: the default under the user folder, and a folder of its own on another drive.</summary>
    [TestCase("")]
    [TestCase(@"D:\Classes")]
    [TestCase(@"%USERPROFILE%\ClassScribe")]
    public void The_usual_folders_bring_nothing(string folder)
    {
        Assert.That(Warnings(new() { ["Recording:OutputFolder"] = folder }), Is.Empty);
    }

    /// <summary>
    /// The facts on this computer: Dropbox's folders read from its info.json files, the personal and the business one, each
    /// file looked for where Dropbox keeps it; a file that is missing or is not JSON gives none, and nothing is thrown.
    /// </summary>
    [Test]
    public void Dropboxs_folders_come_from_its_info_files()
    {
        using var folder = new TempFolder();
        File.WriteAllText(folder.File("roaming.json"), """{ "personal": { "path": "C:\\Users\\someone\\Dropbox", "host": 1 }, "business": { "path": "C:\\Users\\someone\\Dropbox (Work)" } }""");
        File.WriteAllText(folder.File("broken.json"), "{ not json");
        File.WriteAllText(folder.File("odd.json"), """{ "personal": "C:\\Dropbox", "business": { "path": 4 } }""");

        var facts = new OutputFolderFacts([folder.File("roaming.json"), folder.File("broken.json"), folder.File("odd.json"), folder.File("missing.json")]);

        Assert.That(facts.DropboxFolders(), Is.EqualTo(new[] { @"C:\Users\someone\Dropbox", @"C:\Users\someone\Dropbox (Work)" }));
    }

    /// <summary>The facts on this computer: a folder held is found by its name; a drive that is not there has no label.</summary>
    [Test]
    public void A_folder_held_is_found_and_a_missing_drive_has_no_label()
    {
        using var folder = new TempFolder();
        Directory.CreateDirectory(folder.File("AppData"));
        var facts = new OutputFolderFacts([]);

        Assert.That((facts.HasFolder(folder.Path, "AppData"), facts.HasFolder(folder.Path, "Windows")), Is.EqualTo((true, false)));
        Assert.That(facts.VolumeLabel(folder.File("not a root")), Is.Null, "only a drive's root has a label");
    }

    /// <summary>The places Dropbox keeps its info.json: under the roaming and the local application data folders.</summary>
    [Test]
    public void Dropbox_keeps_its_info_file_under_the_application_data_folders()
    {
        Assert.That(OutputFolderFacts.DropboxInfoFiles(), Is.EqualTo(new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dropbox", "info.json"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Dropbox", "info.json")
        }));
    }
}
