using ClassScribe.Settings;

namespace ClassScribe.Tests;

/// <summary>
/// Where the settings and the glossary are read from, decided at each read: the user's own file when it is there, the
/// missing-file state when ClassScribe made one that is gone, else the program's folder, as before. Every folder here is a
/// temporary one; the real %APPDATA% and %LOCALAPPDATA% are never read.
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

    /// <summary>A user folder that is not there yet counts as an empty one, and nothing is created by reading: the user's file is made only at the first change.</summary>
    [Test]
    public void A_user_folder_that_is_not_there_is_not_created()
    {
        var missing = _user.File("Nested");

        var places = new SettingsPlaces(_program.Path, missing);

        Assert.That(places.Origin, Is.EqualTo(SettingsOrigin.BesideTheProgram));
        Assert.That(Directory.Exists(missing), Is.False);
    }

    /// <summary>
    /// The choice is made at every read of the settings: a user file that appears while ClassScribe runs, as one another
    /// window made at its first change, is read from the next read on.
    /// </summary>
    [Test]
    public void The_choice_is_made_at_each_read()
    {
        var places = new SettingsPlaces(_program.Path, _user.Path);
        Assert.That(places.Origin, Is.EqualTo(SettingsOrigin.BesideTheProgram));

        WriteUserSettings();

        Assert.That(places.Origin, Is.EqualTo(SettingsOrigin.UserFolder));
        Assert.That(places.SettingsPath, Is.EqualTo(_user.File("appsettings.json")));
    }

    /// <summary>
    /// A process that has read the user's file never goes back to the file beside the program: with the file gone, the
    /// user's file is still the one named, and its read finds nothing, which the reloader refuses (the settings stay).
    /// </summary>
    [Test]
    public void Once_the_users_file_is_read_the_file_beside_the_program_never_comes_back()
    {
        WriteUserSettings();
        var places = new SettingsPlaces(_program.Path, _user.Path);
        Assert.That(places.Origin, Is.EqualTo(SettingsOrigin.UserFolder));

        File.Delete(_user.File("appsettings.json"));

        Assert.That(places.Current(), Is.EqualTo(new SettingsSource(SettingsOrigin.UserFolder, _user.File("appsettings.json"), _user.File("appsettings.json"))));
        Assert.That(new SettingsPlaces(_program.Path, _user.Path).Origin, Is.EqualTo(SettingsOrigin.BesideTheProgram), "a new process without the marker: the file beside the program");
    }

    /// <summary>
    /// Once ClassScribe has made the user's file, its marker says so: a later start that finds the file gone is in the
    /// missing state, which names the user's file and reads the defaults beside the program, and says what to do.
    /// </summary>
    [Test]
    public void The_marker_without_the_users_file_is_the_missing_state()
    {
        using var state = new TempFolder();
        var places = new SettingsPlaces(_program.Path, _user.Path, state.Path);
        Assert.That(places.HasMarker, Is.False);

        places.MarkUserFileMade();

        Assert.That(File.ReadAllText(state.File(SettingsPlaces.MarkerFileName)).Trim(), Is.EqualTo(_user.File("appsettings.json")), "the marker holds the file's full path");
        var fresh = new SettingsPlaces(_program.Path, _user.Path, state.Path);
        Assert.That(fresh.Current(), Is.EqualTo(new SettingsSource(SettingsOrigin.Missing, _user.File("appsettings.json"), _program.File("appsettings.json"))));
        Assert.That(fresh.Folder, Is.EqualTo(_program.Path), "the defaults beside the program are read for now");
        Assert.That(fresh.DescribeMissing(), Is.EqualTo(
            $"Your settings file {_user.File("appsettings.json")} is missing. If it was moved or deleted by mistake, put it back (appsettings.previous.json beside it is the version before your last change). To start again from the defaults: ClassScribe settings reset --all."));
        Assert.That(fresh.DescribeTemplateInUse(), Is.Null, "the missing file has its own line");

        WriteUserSettings();
        Assert.That(fresh.Origin, Is.EqualTo(SettingsOrigin.UserFolder), "put back, it is read again");
        Assert.That(fresh.DescribeMissing(), Is.Null);
    }

    /// <summary>
    /// The marker and the writer's lock go into the state folder given, %LOCALAPPDATA%\ClassScribe for this user; without
    /// one they are beside the user's file, as with CLASSSCRIBE_SETTINGS_FOLDER; and without a user folder there are none.
    /// </summary>
    [Test]
    public void The_marker_and_the_lock_are_in_the_state_folder()
    {
        using var state = new TempFolder();

        var places = new SettingsPlaces(_program.Path, _user.Path, state.Path);
        Assert.That((places.MarkerFile, places.LockFile, places.PreviousFile),
            Is.EqualTo((state.File("settings-home.txt"), state.File("settings.lock"), _user.File("appsettings.previous.json"))));

        var beside = new SettingsPlaces(_program.Path, _user.Path);
        Assert.That((beside.MarkerFile, beside.LockFile), Is.EqualTo((_user.File("settings-home.txt"), _user.File("settings.lock"))));

        var none = new SettingsPlaces(_program.Path, null, state.Path);
        Assert.That((none.UserFile, none.MarkerFile, none.LockFile, none.PreviousFile), Is.EqualTo(((string?)null, (string?)null, (string?)null, (string?)null)));
        Assert.That(none.HasMarker, Is.False);
    }

    /// <summary>
    /// CLASSSCRIBE_SETTINGS_FOLDER, which a dry run sets in its own process, moves the user's file, the marker, the lock and
    /// the file before the last change into the folder it names, environment variables and quotes allowed, and nothing of
    /// %APPDATA% or %LOCALAPPDATA% is asked for, which a test run refuses.
    /// </summary>
    [Test]
    public void The_settings_folder_variable_moves_every_file_into_its_folder()
    {
        using var variable = new TemporaryVariable(SettingsPlaces.FolderVariable, $"\"{_user.Path}\"");

        var places = SettingsPlaces.ForThisUser(_program.Path);

        Assert.That(places.IsOverridden, Is.True);
        Assert.That((places.UserFolder, places.StateFolder), Is.EqualTo((_user.Path, _user.Path)));
        Assert.That(places.MarkerFile, Is.EqualTo(_user.File("settings-home.txt")));
        Assert.That(new SettingsPlaces(_program.Path, _user.Path).IsOverridden, Is.False);
    }

    /// <summary>Without the variable, the places for this user ask for the real roaming and runtime folders, which a test run refuses.</summary>
    [Test]
    public void For_this_user_the_real_folders_are_refused_in_a_test_run()
    {
        using var variable = new TemporaryVariable(SettingsPlaces.FolderVariable, null);

        Assert.That(() => SettingsPlaces.ForThisUser(_program.Path), Throws.InvalidOperationException.With.Message.Contains(@"%APPDATA%\ClassScribe"));
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
    /// Installed without a user file of their own yet, the file read is the one beside the program, and the menu's note says
    /// these are the defaults and where ClassScribe makes the user's own file at the first change; it names no script, since
    /// none makes it. With the user's file read, or without a user folder, there is no note.
    /// </summary>
    [Test]
    public void The_defaults_note_comes_only_while_the_user_has_no_file_of_their_own()
    {
        Assert.That(new SettingsPlaces(_program.Path, _user.Path).DescribeTemplateInUse(),
            Is.EqualTo($"the defaults; your own file, {_user.File("appsettings.json")}, is made at your first change in S or C, or with settings set"));
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
