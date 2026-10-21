using System.Text;
using System.Text.Json.Nodes;
using ClassScribe.Settings;
using Microsoft.Extensions.Options;

namespace ClassScribe.Tests;

/// <summary>
/// The one writer of the user's settings file (layout-config 1.6, build-plan amendments 25, 38 and 51): the file a full copy
/// of every setting, made at the first change from the file read until then; changes to the parsed tree, checked before
/// they are written, with the hash check, the lock and the file before kept; and what it refuses. Every folder is a
/// temporary one.
/// </summary>
[TestFixture]
public class SettingsWriterTests
{
    private SettingsFileFixtures _files = null!;

    /// <summary>The program's settings, the user's folder empty, ClassScribe's own folder for the marker and the lock.</summary>
    [SetUp]
    public void SetUp() => _files = new SettingsFileFixtures("""{ "Recording": { "OutputFolder": "D:\\Classes", "FlushSeconds": 5 } }""");

    /// <summary>Deletes the folders.</summary>
    [TearDown]
    public void TearDown() => _files.Dispose();

    /// <summary>
    /// The first change makes the user's file as a full copy: every setting this build knows, at the value of the file
    /// read until then (a hand edit beside the program survives) or else its default, with the change made, under
    /// ClassScribe's header; the marker is written, and the line says where the settings are kept now. Nothing is made at
    /// start, and the settings now in force are the user's file's.
    /// </summary>
    [Test]
    public void The_first_change_makes_a_full_copy_with_the_values_read_until_then()
    {
        Assert.That(File.Exists(_files.UserFile), Is.False, "nothing is made before the first change");

        var change = _files.Writer.Set("Recording:FlushSeconds", "7");

        Assert.That(change.ExitCode, Is.EqualTo(0));
        Assert.That(change.Lines[0], Is.EqualTo($"Your settings are now kept in {_files.UserFile}; an update of ClassScribe never changes that file."));
        Assert.That(change.Lines, Has.Member($"Recording:FlushSeconds is now 7 (it was 5) in {_files.UserFile}."));
        Assert.That(_files.UserValue("Recording:OutputFolder")!.GetValue<string>(), Is.EqualTo(@"D:\Classes"), "the hand edit beside the program is kept");
        Assert.That(_files.UserValue("Recording:FlushSeconds")!.GetValue<int>(), Is.EqualTo(7));
        foreach (var key in SettingsCatalog.Keys)
            Assert.That(_files.UserValue(key.Name), Is.Not.Null, $"{key.Name} is in the full copy");
        Assert.That(_files.UserValue("Logging:LogLevel:Default")!.GetValue<string>(), Is.EqualTo("Information"));
        Assert.That(_files.UserValue("ElevenLabs:ApiKey"), Is.Null, "never the key");
        Assert.That(_files.UserText, Does.StartWith(string.Join(Environment.NewLine, SettingsWriter.Header)));
        Assert.That(File.ReadAllText(_files.State.File("settings-home.txt")).Trim(), Is.EqualTo(_files.UserFile));
        Assert.That(File.Exists(_files.PreviousFile), Is.False, "there was no file of the user's before");
        Assert.That(_files.Places.Origin, Is.EqualTo(SettingsOrigin.UserFolder));
        Assert.That(_files.Configuration["Recording:FlushSeconds"], Is.EqualTo("7"), "reloaded");
    }

    /// <summary>
    /// The file is written in the options' order, section by section, as UTF-8 without a byte-order mark, with letters such
    /// as ṣ and ā as they are rather than escaped, so that a person can read it in Notepad.
    /// </summary>
    [Test]
    public void The_file_is_readable_UTF8_in_the_options_order()
    {
        _files.Writer.Set("YouTube:MayDownloadChannels", "@ṢughrāChannel");

        var bytes = File.ReadAllBytes(_files.UserFile);
        Assert.That(bytes[..3], Is.Not.EqualTo(new byte[] { 0xEF, 0xBB, 0xBF }), "no byte-order mark");
        Assert.That(Encoding.UTF8.GetString(bytes), Does.Contain("\"@ṢughrāChannel\""));
        Assert.That(_files.UserTree.Select(section => section.Key), Is.EqualTo(new[] { "Recording", "ElevenLabs", "Transcript", "Drive", "YouTube", "Logging" }));
        Assert.That(_files.UserTree["Recording"]!.AsObject().Select(setting => setting.Key).Take(3), Is.EqualTo(new[] { "OutputFolder", "Device", "App" }));
    }

    /// <summary>
    /// set writes the value as the setting's kind even when it is the default, since the file is a full copy: a number as a
    /// number, true or false, an enum's own name whatever case was typed, text, and every item of a list.
    /// </summary>
    [Test]
    public void Set_writes_each_kind_of_value_as_its_kind()
    {
        Assert.That(_files.Writer.Set("recording:keepscreenon", "always").ExitCode, Is.EqualTo(0));
        Assert.That(_files.Writer.Set("Recording:StopAfterHours", "8.5").ExitCode, Is.EqualTo(0));
        Assert.That(_files.Writer.Set("Recording:KeepUploadFile", "True").ExitCode, Is.EqualTo(0));
        Assert.That(_files.Writer.Set("YouTube:CourseChannels", "@one", "@two").ExitCode, Is.EqualTo(0));
        Assert.That(_files.Writer.Set("Recording:FlushSeconds", "5").ExitCode, Is.EqualTo(0));

        Assert.That(_files.UserValue("Recording:KeepScreenOn")!.GetValue<string>(), Is.EqualTo("Always"));
        Assert.That(_files.UserValue("Recording:StopAfterHours")!.GetValue<double>(), Is.EqualTo(8.5));
        Assert.That(_files.UserValue("Recording:KeepUploadFile")!.GetValue<bool>(), Is.True);
        Assert.That(_files.UserValue("YouTube:CourseChannels")!.AsArray().Select(item => item!.GetValue<string>()), Is.EqualTo(new[] { "@one", "@two" }));
        Assert.That(_files.UserValue("Recording:FlushSeconds")!.GetValue<int>(), Is.EqualTo(5), "a value equal to the default is written");
    }

    /// <summary>A value set to what the file holds already leaves the file as it is and says so.</summary>
    [Test]
    public void Setting_the_value_held_already_writes_nothing()
    {
        _files.Writer.Set("Recording:FlushSeconds", "7");
        var written = File.GetLastWriteTimeUtc(_files.UserFile);
        File.Delete(_files.PreviousFile);

        var change = _files.Writer.Set("Recording:FlushSeconds", "7");

        Assert.That(change, Is.EqualTo(new SettingsChange(0, ["Recording:FlushSeconds is 7, as it was."])).Using<SettingsChange>((a, b) => a.ExitCode == b.ExitCode && a.Lines.SequenceEqual(b.Lines)));
        Assert.That(File.Exists(_files.PreviousFile), Is.False, "nothing was written");
        Assert.That(File.GetLastWriteTimeUtc(_files.UserFile), Is.EqualTo(written));
    }

    /// <summary>
    /// reset writes this build's default into the file rather than removing the key; reset --all writes a fresh full copy,
    /// keeping the saved classes and the file before in appsettings.previous.json.
    /// </summary>
    [Test]
    public void Reset_writes_the_default_and_reset_all_a_fresh_copy()
    {
        _files.Writer.Set("Recording:FlushSeconds", "7");
        _files.Writer.Set("Recording:Device", "Pixel Buds");
        var text = _files.UserText.Replace("\"Logging\"", "\"Classes\": { \"Mantiq\": { \"Course\": \"Mantiq\", \"App\": \"chrome\" } },\n  \"Logging\"", StringComparison.Ordinal);
        _files.WriteUser(text);

        var reset = _files.Writer.Reset("Recording:FlushSeconds");
        Assert.That(reset.ExitCode, Is.EqualTo(0));
        Assert.That(reset.Lines, Has.Member($"Recording:FlushSeconds is back at its default, 5 (it was 7) in {_files.UserFile}."));
        Assert.That(_files.UserValue("Recording:FlushSeconds")!.GetValue<int>(), Is.EqualTo(5), "written, not removed");

        var before = File.ReadAllBytes(_files.UserFile);
        var all = _files.Writer.ResetAll();

        Assert.That(all.ExitCode, Is.EqualTo(0));
        Assert.That(_files.UserValue("Recording:Device")!.GetValue<string>(), Is.Empty);
        Assert.That(_files.UserValue("Recording:OutputFolder")!.GetValue<string>(), Is.EqualTo(RecordingOptions.DefaultOutputFolder), "the build's default, not the file beside the program's");
        Assert.That(_files.UserValue("Classes:Mantiq:App")!.GetValue<string>(), Is.EqualTo("chrome"), "the saved classes are kept");
        Assert.That(File.ReadAllBytes(_files.PreviousFile), Is.EqualTo(before));
    }

    /// <summary>
    /// fill adds every setting the file lacks at its default, with a line naming this build's version, and touches nothing
    /// else; with every setting there it says nothing; and with no user file it writes nothing and says so (amendment 51).
    /// </summary>
    [Test]
    public void Fill_adds_what_the_file_lacks_and_never_makes_a_file()
    {
        Assert.That(_files.Writer.Fill(), Is.EqualTo(new SettingsChange(0, ["No settings file yet; nothing to fill."])).Using<SettingsChange>(SameChange));
        Assert.That(File.Exists(_files.UserFile), Is.False);

        _files.WriteUser("""{ "Recording": { "FlushSeconds": 9, "Future": 1 }, "Transcript": { "IncludeTimestamps": false } }""");
        var filled = _files.Writer.Fill();

        Assert.That(filled.ExitCode, Is.EqualTo(0));
        var missing = SettingsCatalog.Keys.Sum(key => key.Kind == SettingKind.Map ? ((JsonObject)key.Default!).Count : 1) - 2;
        Assert.That(filled.Lines[0], Is.EqualTo($"ClassScribe {BuildInfo.Version} added {missing} settings to your file:"));
        Assert.That(filled.Lines, Has.Member("  Recording:CameraResolution = 720p"));
        Assert.That(filled.Lines, Has.Count.EqualTo(missing + 1), "a line per setting added");
        Assert.That(_files.UserValue("Recording:FlushSeconds")!.GetValue<int>(), Is.EqualTo(9));
        Assert.That(_files.UserValue("Transcript:IncludeTimestamps")!.GetValue<bool>(), Is.False);
        Assert.That(_files.UserValue("Recording:Future")!.GetValue<int>(), Is.EqualTo(1), "a key of a newer build stays");

        Assert.That(_files.Writer.Fill().Lines, Is.Empty, "complete: nothing to say");
    }

    /// <summary>A setting added by a later change, not fill, is said in one line with the build's version and the default it took.</summary>
    [Test]
    public void A_change_says_what_it_added()
    {
        _files.Writer.Set("Recording:FlushSeconds", "7");
        var tree = _files.UserTree;
        tree["Recording"]!.AsObject().Remove("CameraResolution");
        _files.WriteUser(tree.ToJsonString());

        var change = _files.Writer.Set("Recording:FlushSeconds", "8");

        Assert.That(change.Lines[0], Is.EqualTo($"ClassScribe {BuildInfo.Version} added 1 setting to your file: Recording:CameraResolution = 720p."));
    }

    /// <summary>
    /// Keys a newer build wrote, in a known section or a section of its own, stay as they are through a change; the key a
    /// hand edit put in the file goes, since it is never written.
    /// </summary>
    [Test]
    public void Keys_of_a_newer_build_stay_and_the_api_key_goes()
    {
        _files.WriteUser("""{ "Recording": { "FlushSeconds": 5, "CameraOffsets": { "OBSBOT": -60 } }, "Teach": { "Kind": "x" }, "ElevenLabs": { "ApiKey": "sk-hand" } }""");

        Assert.That(_files.Writer.Set("Recording:FlushSeconds", "6").ExitCode, Is.EqualTo(0));

        Assert.That(_files.UserValue("Recording:CameraOffsets:OBSBOT")!.GetValue<int>(), Is.EqualTo(-60));
        Assert.That(_files.UserValue("Teach:Kind")!.GetValue<string>(), Is.EqualTo("x"));
        Assert.That(_files.UserText, Does.Not.Contain("sk-hand"));
    }

    /// <summary>
    /// An unknown setting is refused with exit 2 and the nearest known one; the ElevenLabs key is refused with the ground
    /// rule's words and is never written or said; a value of the wrong kind, several values for one, and a whole map are
    /// refused with exit 2; nothing is written.
    /// </summary>
    [Test]
    public void Mistakes_are_refused_with_exit_2_and_nothing_written()
    {
        Assert.That(_files.Writer.Set("Recording:FlushSecond", "7").Lines.Single(),
            Is.EqualTo("Recording:FlushSecond is not a setting; did you mean Recording:FlushSeconds? ClassScribe settings list shows them all."));
        var key = _files.Writer.Set("ElevenLabs:ApiKey", "sk-secret");
        Assert.That((key.ExitCode, key.Lines.Single()), Is.EqualTo((2, SettingsWriter.ApiKeyRefused)));
        Assert.That(_files.Writer.Reset("elevenlabs:apikey").Lines.Single(), Is.EqualTo(SettingsWriter.ApiKeyRefused));
        Assert.That(_files.Writer.Set("Recording:FlushSeconds", "abc").Lines.Single(), Is.EqualTo("Recording:FlushSeconds takes a whole number, such as 60, not \"abc\"."));
        Assert.That(_files.Writer.Set("Recording:KeepScreenOn", "Sometimes").Lines.Single(), Is.EqualTo("Recording:KeepScreenOn takes OnBattery, Always or Never, not \"Sometimes\"."));
        Assert.That(_files.Writer.Set("Recording:Device", "Pixel", "Buds").ExitCode, Is.EqualTo(2));
        Assert.That(_files.Writer.Set("Logging:LogLevel", "Debug").ExitCode, Is.EqualTo(2));
        Assert.That(_files.Writer.Set("Recording:FlushSeconds").ExitCode, Is.EqualTo(2));

        Assert.That(File.Exists(_files.UserFile), Is.False);
        Assert.That(_files.Log.Messages, Has.None.Contains("sk-secret"));
    }

    /// <summary>A value the check refuses is refused in the check's words, with exit 2, and nothing is written.</summary>
    [Test]
    public void A_value_the_check_refuses_is_named_in_its_words()
    {
        var change = _files.Writer.Set("Recording:FlushSeconds", "0");

        Assert.That(change.ExitCode, Is.EqualTo(2));
        Assert.That(change.Lines, Is.EqualTo(new[] { "Recording:FlushSeconds is 0; it must be 1 or more.", "Nothing was changed." }));
        Assert.That(File.Exists(_files.UserFile), Is.False);
    }

    /// <summary>A file with a problem already takes a change that adds none: the change is written, and the problem said.</summary>
    [Test]
    public void A_file_with_problems_takes_a_change_that_adds_none()
    {
        _files.WriteUser("""{ "Recording": { "ContinueWithinMinutes": 5000 } }""");

        var change = _files.Writer.Set("Recording:FlushSeconds", "7");

        Assert.That(change.ExitCode, Is.EqualTo(0));
        Assert.That(_files.UserValue("Recording:FlushSeconds")!.GetValue<int>(), Is.EqualTo(7));
        Assert.That(change.Lines[^1], Does.StartWith("The change is saved, but the file still has problems that keep it from being used: Recording:ContinueWithinMinutes is 5000"));
    }

    /// <summary>
    /// A file with comments of the person's own is refused, exit 1, before anything is lost; ClassScribe's own header is
    /// no such comment, and a trailing comma is read as .NET reads it.
    /// </summary>
    [Test]
    public void Comments_of_ones_own_are_refused_and_the_header_and_trailing_commas_are_read()
    {
        _files.WriteUser("{\n  // my note\n  \"Recording\": { \"FlushSeconds\": 5, },\n}");
        var refused = _files.Writer.Set("Recording:FlushSeconds", "7");
        Assert.That((refused.ExitCode, refused.Lines.Single()), Is.EqualTo((1, SettingsWriter.CommentsOfItsOwn)));

        _files.WriteUser(string.Join("\n", SettingsWriter.Header) + "\n{ \"Recording\": { \"FlushSeconds\": 5, }, }");
        Assert.That(_files.Writer.Set("Recording:FlushSeconds", "7").ExitCode, Is.EqualTo(0));
        Assert.That(SettingsWriter.HasCommentsOfItsOwn(File.ReadAllBytes(_files.UserFile)), Is.False);
    }

    /// <summary>
    /// A file that is not valid JSON is refused with exit 1 in the reloader's own words and how to go on; reset --all makes
    /// a fresh file over it, its bytes kept as appsettings.previous.json.
    /// </summary>
    [Test]
    public void A_file_that_is_not_JSON_is_refused_and_reset_all_starts_again()
    {
        _files.WriteUser("{ \"Recording\": { \"OutputFolder\": \"D:\\Classes\" } }");

        var refused = _files.Writer.Set("Recording:FlushSeconds", "7");

        Assert.That(refused.ExitCode, Is.EqualTo(1));
        Assert.That(refused.Lines[0], Does.StartWith("The mistake is on line 1:"));
        Assert.That(refused.Lines[^1], Is.EqualTo($"Nothing was changed. Fix {_files.UserFile} first, or start again from the defaults with: ClassScribe settings reset --all."));
        Assert.That(_files.Writer.ResetAll().ExitCode, Is.EqualTo(0));
        Assert.That(File.ReadAllText(_files.PreviousFile), Does.Contain("D:\\Classes"));
        Assert.That(_files.UserValue("Recording:FlushSeconds")!.GetValue<int>(), Is.EqualTo(5));
    }

    /// <summary>
    /// The file saved by another program between the check and the write makes the change start again from the new file,
    /// so the other program's edit is kept; a file that keeps changing is given up after three tries, exit 1.
    /// </summary>
    [Test]
    public void A_file_saved_meanwhile_starts_the_change_again_and_one_that_keeps_changing_is_given_up()
    {
        _files.Writer.Set("Recording:FlushSeconds", "7");
        var saves = 0;
        _files.Writer.BeforeSecondRead = () =>
        {
            if (saves++ > 0) return;
            var tree = _files.UserTree;
            tree["Recording"]!["Device"] = "Edited meanwhile";
            _files.WriteUser(tree.ToJsonString());
        };

        Assert.That(_files.Writer.Set("Recording:FlushSeconds", "8").ExitCode, Is.EqualTo(0));
        Assert.That(_files.UserValue("Recording:Device")!.GetValue<string>(), Is.EqualTo("Edited meanwhile"));
        Assert.That(_files.UserValue("Recording:FlushSeconds")!.GetValue<int>(), Is.EqualTo(8));

        _files.Writer.BeforeSecondRead = () => _files.WriteUser(_files.UserText + " ");
        var given = _files.Writer.Set("Recording:FlushSeconds", "9");

        Assert.That(given.ExitCode, Is.EqualTo(1));
        Assert.That(given.Lines.Single(), Does.Contain("keeps changing while ClassScribe changes it"));
        Assert.That(_files.UserValue("Recording:FlushSeconds")!.GetValue<int>(), Is.EqualTo(8));
    }

    /// <summary>Each change keeps the bytes before it as appsettings.previous.json, one undo.</summary>
    [Test]
    public void The_file_before_each_change_is_kept_as_previous()
    {
        _files.Writer.Set("Recording:FlushSeconds", "7");
        var before = File.ReadAllBytes(_files.UserFile);

        _files.Writer.Set("Recording:FlushSeconds", "8");

        Assert.That(File.ReadAllBytes(_files.PreviousFile), Is.EqualTo(before));
    }

    /// <summary>One change at a time: while another holds settings.lock, a change waits, then gives up with exit 1.</summary>
    [Test]
    public void A_change_waits_for_the_lock_then_gives_up()
    {
        _files.Writer.LockPatience = TimeSpan.FromMilliseconds(200);
        using (new FileStream(_files.State.File("settings.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            var change = _files.Writer.Set("Recording:FlushSeconds", "7");
            Assert.That((change.ExitCode, change.Lines.Single()), Is.EqualTo((1, "Another ClassScribe is changing your settings just now; try again in a moment.")));
        }

        Assert.That(_files.Writer.Set("Recording:FlushSeconds", "7").ExitCode, Is.EqualTo(0), "let go, the next change goes ahead");
    }

    /// <summary>A setting an environment variable overrides is saved, and the line says it has no effect while that variable is set.</summary>
    [Test]
    public void An_environment_override_is_said()
    {
        using var variable = new TemporaryVariable("Recording__FlushSeconds", "9");

        var change = _files.Writer.Set("Recording:FlushSeconds", "7");

        Assert.That(change.Lines[^1], Is.EqualTo("The environment variable Recording__FlushSeconds sets Recording:FlushSeconds for ClassScribe, so this change has no effect while it is set."));
        Assert.That(_files.UserValue("Recording:FlushSeconds")!.GetValue<int>(), Is.EqualTo(7));
    }

    /// <summary>
    /// While the user's file is missing (layout-config 1.4), every change is refused with exit 1 and the missing-file words,
    /// since a new file would hide the loss; reset --all makes a fresh file and keeps the marker.
    /// </summary>
    [Test]
    public void A_missing_file_refuses_every_change_but_reset_all()
    {
        _files.Writer.Set("Recording:FlushSeconds", "7");
        File.Delete(_files.UserFile);
        var places = new SettingsPlaces(_files.Program.Path, _files.User.Path, _files.State.Path);
        var writer = new SettingsWriter(places, new SettingsFile(places, Microsoft.Extensions.Logging.Abstractions.NullLogger<SettingsFile>.Instance), _files.Reloader, _files.Log);

        var refused = writer.Set("Recording:FlushSeconds", "8");

        Assert.That((refused.ExitCode, refused.Lines.Single()), Is.EqualTo((1, places.DescribeMissing())));
        Assert.That(File.Exists(_files.UserFile), Is.False);
        Assert.That(writer.ResetAll().ExitCode, Is.EqualTo(0));
        Assert.That(File.Exists(_files.UserFile), Is.True);
        Assert.That(places.HasMarker, Is.True);
    }

    /// <summary>
    /// A map's entries are set and reset one by one by their key path (amendment 25): an entry set is written under the
    /// map, one with a default of its own is reset to it, one without is removed, and a value its kind refuses is refused.
    /// </summary>
    [Test]
    public void A_maps_entries_are_set_and_reset_by_their_key_path()
    {
        Assert.That(_files.Writer.Set("Logging:LogLevel:ClassScribe.Drive", "debug").ExitCode, Is.EqualTo(0));
        Assert.That(_files.UserValue("Logging:LogLevel:ClassScribe.Drive")!.GetValue<string>(), Is.EqualTo("Debug"));
        Assert.That(_files.Writer.Set("Logging:LogLevel:Default", "Warning").ExitCode, Is.EqualTo(0));

        Assert.That(_files.Writer.Reset("Logging:LogLevel:ClassScribe.Drive").Lines, Has.Member($"Logging:LogLevel:ClassScribe.Drive is removed (it was \"Debug\") in {_files.UserFile}."));
        Assert.That(_files.UserValue("Logging:LogLevel:ClassScribe.Drive"), Is.Null);
        Assert.That(_files.Writer.Reset("Logging:LogLevel:Default").ExitCode, Is.EqualTo(0));
        Assert.That(_files.UserValue("Logging:LogLevel:Default")!.GetValue<string>(), Is.EqualTo("Information"));
        var refused = _files.Writer.Set("Logging:LogLevel:ClassScribe", "Loud");
        Assert.That((refused.ExitCode, refused.Lines.Single()), Is.EqualTo((2, "Logging:LogLevel:ClassScribe takes Trace, Debug, Information, Warning, Error, Critical or None, not \"Loud\".")));
    }

    /// <summary>
    /// With CLASSSCRIBE_SETTINGS_FOLDER, as a dry run sets it, the marker and the lock are beside the file in that folder,
    /// and nothing goes into ClassScribe's own folder.
    /// </summary>
    [Test]
    public void With_the_folder_override_every_file_is_in_that_folder()
    {
        using var overridden = new SettingsFileFixtures(overridden: true);

        Assert.That(overridden.Writer.Set("Recording:FlushSeconds", "7").ExitCode, Is.EqualTo(0));

        Assert.That(File.Exists(overridden.User.File("settings-home.txt")), Is.True);
        Assert.That(File.Exists(overridden.User.File("settings.lock")), Is.True);
        Assert.That(Directory.EnumerateFileSystemEntries(overridden.State.Path), Is.Empty);
    }

    /// <summary>Two changes compared by their exit codes and lines.</summary>
    private static bool SameChange(SettingsChange first, SettingsChange second) => first.ExitCode == second.ExitCode && first.Lines.SequenceEqual(second.Lines);
}
