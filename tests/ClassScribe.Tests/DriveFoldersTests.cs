using ClassScribe.Drive;

namespace ClassScribe.Tests;

/// <summary>
/// ClassScribe's own folder in My Drive: found again by its remembered id, else by ClassScribe's mark on it, whatever it is
/// called now and wherever it is, else made once with that mark; and the folders under it that mirror the laptop's, course
/// then date folder, or an old flat folder's name, each found by its remembered id, else by its name in the level above
/// (escaped in the search), else made there. A folder in the trash does not count, and is left there. The ids are
/// remembered in the temporary ClassScribe folder, never the real one.
/// </summary>
[TestFixture]
public class DriveFoldersTests
{
    private GoogleWorld _world = null!;

    /// <summary>A world whose store holds the tests' client, signed in.</summary>
    [SetUp]
    public void SetUp()
    {
        _world = new GoogleWorld();
        _world.Store.Stored = FakeCredentialStore.With(_world.Google.IssueRefreshToken()).Stored;
    }

    /// <summary>Deletes the world's folders.</summary>
    [TearDown]
    public void TearDown() => _world.Dispose();

    /// <summary>The remembered folders file in the temporary ClassScribe folder.</summary>
    private string Remembered => Path.Combine(_world.Runtime.Folder.Root, "drive", "folders.json");

    /// <summary>The folders made so far, through files.create.</summary>
    private int FoldersMade => _world.Google.Requests.Count(request => request.Method == HttpMethod.Post && request.Path == "/drive/v3/files");

    /// <summary>
    /// With no folder on Drive, one is made in My Drive with the name of Drive:FolderName, as a folder, with ClassScribe's mark,
    /// and its id is remembered; a second run finds it by that id, with one request and nothing made.
    /// </summary>
    [Test]
    public async Task The_folder_is_made_once_with_the_mark_and_found_again_by_its_id()
    {
        var made = await _world.Folders().FindOrCreateRootAsync(CancellationToken.None);

        var folder = _world.Google.Files.Single();
        Assert.That(made, Is.EqualTo(new DriveFolder(folder.Id, "ClassScribe")));
        Assert.That(made.Link, Is.EqualTo($"https://drive.google.com/drive/folders/{folder.Id}"));
        Assert.That((folder.MimeType, folder.Parents.Count, folder.AppProperties["classscribe"]), Is.EqualTo((FakeGoogle.FolderType, 0, "root")));
        Assert.That(JsonText.File(Remembered)["rootId"]!.GetValue<string>(), Is.EqualTo(folder.Id));

        var before = _world.Google.Requests.Count;
        var again = await _world.Folders().FindOrCreateRootAsync(CancellationToken.None);

        Assert.That(again, Is.EqualTo(made));
        Assert.That(_world.Google.Requests.Skip(before).Select(request => request.Path), Is.EqualTo(new[] { $"/drive/v3/files/{folder.Id}" }));
        Assert.That(FoldersMade, Is.EqualTo(1));
    }

    /// <summary>The name comes from Drive:FolderName, trimmed.</summary>
    [Test]
    public async Task The_name_comes_from_the_setting()
    {
        _world.Options.Set(new DriveOptions { FolderName = "  Classes  " });

        var made = await _world.Folders().FindOrCreateRootAsync(CancellationToken.None);

        Assert.That(made.Name, Is.EqualTo("Classes"));
        Assert.That(_world.Google.Files.Single().Name, Is.EqualTo("Classes"));
    }

    /// <summary>
    /// A folder with ClassScribe's mark is found by it when no id is remembered, as on another laptop or after the file was
    /// lost, even when Gulzar renamed it in Drive; nothing is made, and its id is remembered.
    /// </summary>
    [Test]
    public async Task A_marked_folder_is_found_by_its_mark_even_renamed()
    {
        var renamed = _world.Google.AddFolder("My classes", root: true);

        var found = await _world.Folders().FindOrCreateRootAsync(CancellationToken.None);

        Assert.That(found, Is.EqualTo(new DriveFolder(renamed.Id, "My classes")));
        Assert.That(FoldersMade, Is.EqualTo(0));
        Assert.That(JsonText.File(Remembered)["rootId"]!.GetValue<string>(), Is.EqualTo(renamed.Id));
    }

    /// <summary>A folder of the same name without ClassScribe's mark, such as one made by hand, is not ClassScribe's: a folder is made.</summary>
    [Test]
    public async Task A_folder_made_by_hand_is_not_taken()
    {
        var byHand = _world.Google.AddFolder("ClassScribe");

        var made = await _world.Folders().FindOrCreateRootAsync(CancellationToken.None);

        Assert.That(made.Id, Is.Not.EqualTo(byHand.Id));
        Assert.That(FoldersMade, Is.EqualTo(1));
    }

    /// <summary>
    /// A remembered folder now in the trash does not count: another marked folder is looked for, else one is made. The one in
    /// the trash is left there: ClassScribe never deletes or restores anything on Drive.
    /// </summary>
    [Test]
    public async Task A_remembered_folder_in_the_trash_does_not_count_and_is_left_there()
    {
        var first = await _world.Folders().FindOrCreateRootAsync(CancellationToken.None);
        _world.Google.Files.Single().Trashed = true;

        var second = await _world.Folders().FindOrCreateRootAsync(CancellationToken.None);

        Assert.That(second.Id, Is.Not.EqualTo(first.Id));
        Assert.That(_world.Google.Files.Select(file => (file.Id, file.Trashed)), Is.EqualTo(new[] { (first.Id, true), (second.Id, false) }));
        Assert.That(_world.Google.Requests.Where(request => request.Method != HttpMethod.Get && request.Path != "/token").Select(request => request.Method),
            Is.All.EqualTo(HttpMethod.Post), "nothing on Drive is changed or deleted, only made");
    }

    /// <summary>A remembered id Drive no longer has, or a remembered file that cannot be read, leads to a search, not a failure.</summary>
    [Test]
    public async Task A_remembered_id_that_is_gone_or_a_damaged_file_leads_to_a_search()
    {
        var marked = _world.Google.AddFolder("ClassScribe", root: true);
        Directory.CreateDirectory(Path.GetDirectoryName(Remembered)!);
        File.WriteAllText(Remembered, """{ "rootId": "gone-for-good" }""");

        Assert.That((await _world.Folders().FindRootAsync(CancellationToken.None))?.Id, Is.EqualTo(marked.Id));

        File.WriteAllText(Remembered, "not json");
        Assert.That((await _world.Folders().FindRootAsync(CancellationToken.None))?.Id, Is.EqualTo(marked.Id));
    }

    /// <summary>Finding alone makes nothing: with no folder on Drive, it is null, and nothing is made or remembered.</summary>
    [Test]
    public async Task Finding_alone_makes_nothing()
    {
        Assert.That(await _world.Folders().FindRootAsync(CancellationToken.None), Is.Null);
        Assert.That(FoldersMade, Is.EqualTo(0));
        Assert.That(File.Exists(Remembered), Is.False);
    }

    /// <summary>
    /// A class's folder on Drive mirrors the laptop's, course then date: each level is made once, in the one above, and
    /// remembered by its path under the root; a second upload finds each by its id, with one request each and nothing made.
    /// </summary>
    [Test]
    public async Task A_course_and_date_folder_are_made_once_in_the_root_and_remembered()
    {
        var made = await _world.Folders().FindOrCreatePathAsync(["Mantiq", "2026-10-17"], CancellationToken.None);

        var root = _world.Google.RootFolder!;
        var course = _world.Google.Folder("Mantiq", root.Id)!;
        var date = _world.Google.Folder("2026-10-17", course.Id)!;
        Assert.That(made, Is.EqualTo(new DriveFolderPath(new DriveFolder(date.Id, "2026-10-17"), "ClassScribe/Mantiq/2026-10-17")));
        Assert.That(made.Location, Is.EqualTo("My Drive > ClassScribe > Mantiq > 2026-10-17"));
        Assert.That((course.AppProperties.Count, date.AppProperties.Count), Is.EqualTo((0, 0)), "only ClassScribe's own folder carries the mark");
        var remembered = JsonText.File(Remembered)["folders"]!.AsObject();
        Assert.That((remembered["Mantiq"]!.GetValue<string>(), remembered["Mantiq/2026-10-17"]!.GetValue<string>()), Is.EqualTo((course.Id, date.Id)));

        var before = _world.Google.Requests.Count;
        var again = await _world.Folders().FindOrCreatePathAsync(["Mantiq", "2026-10-17"], CancellationToken.None);

        Assert.That(again, Is.EqualTo(made));
        Assert.That(_world.Google.Requests.Skip(before).Select(request => request.Path), Is.EqualTo(new[]
        {
            $"/drive/v3/files/{root.Id}", $"/drive/v3/files/{course.Id}", $"/drive/v3/files/{date.Id}"
        }));
        Assert.That(FoldersMade, Is.EqualTo(3));
    }

    /// <summary>
    /// A folder of the old flat layout is one level under the root, its own name; a second class of a day, " (2)", is a date
    /// folder of its own beside the first.
    /// </summary>
    [Test]
    public async Task An_old_flat_folder_is_one_level_and_a_second_class_of_a_day_its_own_folder()
    {
        var flat = await _world.Folders().FindOrCreatePathAsync(["Kalam"], CancellationToken.None);
        var second = await _world.Folders().FindOrCreatePathAsync(["Mantiq", "2026-10-17 (2)"], CancellationToken.None);

        var root = _world.Google.RootFolder!;
        Assert.That(flat.Path, Is.EqualTo("ClassScribe/Kalam"));
        Assert.That(_world.Google.Folder("Kalam", root.Id)!.Id, Is.EqualTo(flat.Folder.Id));
        Assert.That(second.Path, Is.EqualTo("ClassScribe/Mantiq/2026-10-17 (2)"));
    }

    /// <summary>
    /// Without a remembered id, as on another laptop, each level is found by its name in the one above, with the name's quote
    /// and backslash escaped in the search; nothing is made, and the ids are remembered.
    /// </summary>
    [Test]
    public async Task Levels_are_found_by_name_with_the_name_escaped()
    {
        var root = _world.Google.AddFolder("ClassScribe", root: true);
        var course = _world.Google.AddFolder(@"Al-Ghazali's \ Ihya", parentId: root.Id);
        var date = _world.Google.AddFolder("2026-10-17", parentId: course.Id);
        _world.Google.AddFolder("2026-10-17", parentId: root.Id);

        var found = await _world.Folders().FindOrCreatePathAsync([@"Al-Ghazali's \ Ihya", "2026-10-17"], CancellationToken.None);

        Assert.That(found.Folder.Id, Is.EqualTo(date.Id));
        Assert.That(FoldersMade, Is.Zero);
        Assert.That(_world.Google.Requests.Select(request => request.Query["q"]), Has.Member($@"'{root.Id}' in parents and name = 'Al-Ghazali\'s \\ Ihya' and mimeType = 'application/vnd.google-apps.folder' and trashed = false"));
        Assert.That(JsonText.File(Remembered)["folders"]!.AsObject().Select(pair => pair.Key), Is.EqualTo(new[] { @"Al-Ghazali's \ Ihya", @"Al-Ghazali's \ Ihya/2026-10-17" }));
    }

    /// <summary>A level in the trash is not used: a new one is made beside it, and the one in the trash is left there.</summary>
    [Test]
    public async Task A_level_in_the_trash_is_left_there_and_a_new_one_made()
    {
        var first = await _world.Folders().FindOrCreatePathAsync(["Mantiq"], CancellationToken.None);
        var trashed = _world.Google.Files.Single(file => file.Id == first.Folder.Id);
        trashed.Trashed = true;

        var second = await _world.Folders().FindOrCreatePathAsync(["Mantiq"], CancellationToken.None);

        Assert.That(second.Folder.Id, Is.Not.EqualTo(first.Folder.Id));
        Assert.That(trashed.Trashed, Is.True);
        Assert.That(_world.Google.Destructive, Is.Empty);
    }

    /// <summary>A level Gulzar moved or renamed in Drive is still found by its remembered id, and the class goes where he put it.</summary>
    [Test]
    public async Task A_level_moved_in_Drive_is_followed()
    {
        var made = await _world.Folders().FindOrCreatePathAsync(["Mantiq"], CancellationToken.None);
        var elsewhere = _world.Google.AddFolder("Logic courses");
        var course = _world.Google.Files.Single(file => file.Id == made.Folder.Id);
        course.Parents.Clear();
        course.Parents.Add(elsewhere.Id);
        course.Name = "Logic";

        var again = await _world.Folders().FindOrCreatePathAsync(["Mantiq"], CancellationToken.None);

        Assert.That(again.Folder, Is.EqualTo(new DriveFolder(course.Id, "Logic")));
        Assert.That(FoldersMade, Is.EqualTo(2));
    }

    /// <summary>
    /// When ClassScribe's own folder is made anew, as after the old one went to the trash, the folders remembered under the
    /// old one are forgotten, and the course is made again under the new one.
    /// </summary>
    [Test]
    public async Task A_new_root_forgets_the_folders_under_the_old_one()
    {
        var first = await _world.Folders().FindOrCreatePathAsync(["Mantiq"], CancellationToken.None);
        _world.Google.RootFolder!.Trashed = true;

        var second = await _world.Folders().FindOrCreatePathAsync(["Mantiq"], CancellationToken.None);

        Assert.That(second.Folder.Id, Is.Not.EqualTo(first.Folder.Id));
        var root = _world.Google.RootFolder!;
        Assert.That(_world.Google.Folder("Mantiq", root.Id)!.Id, Is.EqualTo(second.Folder.Id));
        Assert.That(JsonText.File(Remembered)["rootId"]!.GetValue<string>(), Is.EqualTo(root.Id));
    }
}
