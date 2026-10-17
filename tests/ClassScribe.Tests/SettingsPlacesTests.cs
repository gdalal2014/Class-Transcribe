using ClassScribe.Settings;

namespace ClassScribe.Tests;

/// <summary>
/// Where the settings and the glossary are read from: the user's own folder when it holds a settings file when ClassScribe
/// starts, else the program's folder, as before. Every folder here is a temporary one; the real %APPDATA% is never read.
/// </summary>
[TestFixture]
public class SettingsPlacesTests
{
    private TempFolder _program = null!;
    private TempFolder _user = null!;

    /// <summary>Creates a folder for the program and one for the user's files.</summary>
    [SetUp]
    public void SetUp()
    {
        _program = new TempFolder();
        _user = new TempFolder();
    }

    /// <summary>Deletes the folders.</summary>
    [TearDown]
    public void TearDown()
    {
        _user.Dispose();
        _program.Dispose();
    }

    /// <summary>Writes the user's own settings file.</summary>
    private void WriteUserSettings(string json = "{}") => File.WriteAllText(_user.File("appsettings.json"), json);

    /// <summary>A settings file in the user's folder at start makes that folder the one read: the settings file and the folder are the user's.</summary>
    [Test]
    public void A_user_file_at_start_makes_the_users_folder_the_one_read()
    {
        WriteUserSettings();

        var places = new SettingsPlaces(_program.Path, _user.Path);

        Assert.That(places.Origin, Is.EqualTo(SettingsOrigin.UserFolder));
        Assert.That(places.Folder, Is.EqualTo(_user.Path));
        Assert.That(places.SettingsPath, Is.EqualTo(_user.File("appsettings.json")));
        Assert.That((places.ProgramFolder, places.UserFolder), Is.EqualTo((_program.Path, _user.Path)));
    }

    /// <summary>
    /// Without a user file, or without a user folder at all (the tests, and a profile without one), the file beside the
    /// program is read, as before: under dotnet run, for a portable copy, and when the installed user file is missing.
    /// </summary>
    [TestCase(true, Description = "the user's folder exists but holds no settings file")]
    [TestCase(false, Description = "no user folder")]
    public void Without_a_user_file_the_file_beside_the_program_is_read(bool userFolder)
    {
        var places = new SettingsPlaces(_program.Path, userFolder ? _user.Path : null);

        Assert.That(places.Origin, Is.EqualTo(SettingsOrigin.BesideTheProgram));
        Assert.That(places.Folder, Is.EqualTo(_program.Path));
        Assert.That(places.SettingsPath, Is.EqualTo(_program.File("appsettings.json")));
        Assert.That(places.UserFolder, Is.EqualTo(userFolder ? _user.Path : null));
    }

    /// <summary>A user folder that is not there yet counts as an empty one, and nothing is created: ClassScribe never writes a settings file.</summary>
    [Test]
    public void A_user_folder_that_is_not_there_is_not_created()
    {
        var missing = _user.File("Nested");

        var places = new SettingsPlaces(_program.Path, missing);

        Assert.That(places.Origin, Is.EqualTo(SettingsOrigin.BesideTheProgram));
        Assert.That(Directory.Exists(missing), Is.False);
    }

    /// <summary>The choice is made once, when ClassScribe starts: a user file that appears later does not change it until the next start.</summary>
    [Test]
    public void The_choice_is_made_once_at_start()
    {
        var places = new SettingsPlaces(_program.Path, _user.Path);

        WriteUserSettings();

        Assert.That(places.Origin, Is.EqualTo(SettingsOrigin.BesideTheProgram));
        Assert.That(places.SettingsPath, Is.EqualTo(_program.File("appsettings.json")));
        Assert.That(new SettingsPlaces(_program.Path, _user.Path).Origin, Is.EqualTo(SettingsOrigin.UserFolder), "the next start reads it");
    }

    /// <summary>A trailing separator on either folder, as AppContext.BaseDirectory has one, changes nothing in the paths.</summary>
    [Test]
    public void A_trailing_separator_is_dropped()
    {
        WriteUserSettings();

        var places = new SettingsPlaces(_program.Path + Path.DirectorySeparatorChar, _user.Path + Path.DirectorySeparatorChar);

        Assert.That(places.ProgramFolder, Is.EqualTo(_program.Path));
        Assert.That(places.UserFolder, Is.EqualTo(_user.Path));
        Assert.That(places.SettingsPath, Is.EqualTo(_user.File("appsettings.json")));
    }

    /// <summary>
    /// For this user, the user folder is ClassScribe under the roaming application data folder, where Windows keeps a
    /// user's settings: apart from the program, which an update replaces, and apart from ClassScribe's own runtime folder,
    /// which an uninstall removes. The path alone is checked here; no test reads the real folder.
    /// </summary>
    [Test]
    public void The_users_folder_is_ClassScribe_under_the_roaming_application_data()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        Assert.That(SettingsPlaces.UserFolderForThisUser, Is.EqualTo(Path.Combine(appData, SettingsPlaces.FolderName)));
        Assert.That(SettingsPlaces.FolderName, Is.EqualTo("ClassScribe"));
    }

    /// <summary>
    /// A relative glossary name is taken from the settings folder read when the file is there, and from the program's folder
    /// otherwise: the user's own glossary first, the shipped one second.
    /// </summary>
    [Test]
    public void A_relative_file_is_taken_from_the_settings_folder_first_then_beside_the_program()
    {
        WriteUserSettings();
        var places = new SettingsPlaces(_program.Path, _user.Path);

        Assert.That(places.ResolveFile("keyterms.txt"), Is.EqualTo(_program.File("keyterms.txt")), "not in the user's folder, so beside the program, whether it is there or not");

        File.WriteAllText(_user.File("keyterms.txt"), "kalām");
        Assert.That(places.ResolveFile("keyterms.txt"), Is.EqualTo(_user.File("keyterms.txt")));
        Assert.That(places.ResolveFile(Path.Combine("terms", "more.txt")), Is.EqualTo(_program.File(Path.Combine("terms", "more.txt"))));
    }

    /// <summary>When the file beside the program is read, a relative name is taken from there, as before: a glossary in a user folder without a settings file is not looked at.</summary>
    [Test]
    public void Beside_the_program_a_relative_file_is_taken_from_there()
    {
        File.WriteAllText(_user.File("keyterms.txt"), "kalām");
        var places = new SettingsPlaces(_program.Path, _user.Path);

        Assert.That(places.ResolveFile("keyterms.txt"), Is.EqualTo(_program.File("keyterms.txt")));
    }

    /// <summary>An absolute path, and one with environment variables, is taken as it is, with quotes around it ignored; an empty setting stays empty, which means no glossary.</summary>
    [Test]
    public void Absolute_paths_and_variables_are_taken_as_they_are_and_empty_stays_empty()
    {
        WriteUserSettings();
        var places = new SettingsPlaces(_program.Path, _user.Path);
        var elsewhere = _user.File(Path.Combine("Glossaries", "terms.txt"));

        Assert.That(places.ResolveFile(elsewhere), Is.EqualTo(elsewhere), "whether or not it is there");
        Assert.That(places.ResolveFile($" \"{elsewhere}\" "), Is.EqualTo(elsewhere));
        using var variable = new TemporaryVariable("CLASSSCRIBE_TEST_GLOSSARY", _user.Path);
        Assert.That(places.ResolveFile(@"%CLASSSCRIBE_TEST_GLOSSARY%\terms.txt"), Is.EqualTo(_user.File("terms.txt")));
        Assert.That(places.ResolveFile("  "), Is.Empty);
    }

    /// <summary>
    /// Installed without a user file, the file read is the shipped template, and the menu's note says so and where the
    /// user's copy belongs. With the user's file read, or without a user folder, there is no note.
    /// </summary>
    [Test]
    public void The_template_note_comes_only_when_the_shipped_file_stands_in_for_a_missing_user_file()
    {
        Assert.That(new SettingsPlaces(_program.Path, _user.Path).DescribeTemplateInUse(),
            Is.EqualTo($"the shipped defaults; your own copy belongs in {_user.File("appsettings.json")}: run Update-ClassScribe.cmd, or copy the file there"));
        Assert.That(new SettingsPlaces(_program.Path, null).DescribeTemplateInUse(), Is.Null, "no user folder: as before");

        WriteUserSettings();

        Assert.That(new SettingsPlaces(_program.Path, _user.Path).DescribeTemplateInUse(), Is.Null, "the user's file is read");
    }

    /// <summary>
    /// Under dotnet run, where the program's folder is a build's output under the project's bin, the copy there is the one
    /// meant, so there is no note: the stale-copy warning covers that layout.
    /// </summary>
    [Test]
    public void Under_a_build_output_there_is_no_template_note()
    {
        var project = Directory.CreateDirectory(_program.File(Path.Combine("src", "ClassScribe"))).FullName;
        File.WriteAllText(Path.Combine(project, "ClassScribe.csproj"), "<Project />");
        var output = Directory.CreateDirectory(Path.Combine(project, "bin", "Debug", "net10.0")).FullName;

        Assert.That(new SettingsPlaces(output, _user.Path).DescribeTemplateInUse(), Is.Null);
    }
}
