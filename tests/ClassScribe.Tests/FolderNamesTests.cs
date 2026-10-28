using ClassScribe.Storage;

namespace ClassScribe.Tests;

/// <summary>The rules of the recordings' folder names, which read no disk (architecture 3.15 a and b: FolderNames in Core).</summary>
[TestFixture]
public class FolderNamesTests
{
    /// <summary>
    /// A name made safe for a folder: the characters Windows forbids go, runs of spaces become one, trailing dots and spaces
    /// go, a name Windows keeps for a device gets an underscore after the part before its first dot, and nothing usable left
    /// gives an empty name.
    /// </summary>
    [TestCase("  Kalam:  part 1?. ", "Kalam part 1")]
    [TestCase("NUL", "NUL_")]
    [TestCase("nul.backup", "nul_.backup")]
    [TestCase("COM\u00B9", "COM\u00B9_")]
    [TestCase("Console", "Console")]
    [TestCase("???", "")]
    public void A_name_is_made_safe_for_a_folder(string name, string expected) =>
        Assert.That(FolderNames.SafeName(name), Is.EqualTo(expected));

    /// <summary>A date folder's name is the date of a real day, alone or with a later class's number in brackets.</summary>
    [TestCase("2026-10-17", true)]
    [TestCase("2026-10-17 (2)", true)]
    [TestCase("2026-02-30", false)]
    [TestCase("2026-10-17 (x)", false)]
    [TestCase("2026-10-17(2)", false)]
    [TestCase("Slides", false)]
    public void A_date_folder_is_told_by_its_name(string name, bool expected) =>
        Assert.That(FolderNames.IsDateFolder(name), Is.EqualTo(expected));

    /// <summary>A day's first class gets the date alone, a later one the date and its number; a later one is told by its name.</summary>
    [Test]
    public void A_later_class_of_a_day_gets_its_number_and_is_told_by_it()
    {
        var day = new DateOnly(2026, 10, 17);

        Assert.That(FolderNames.DateFolderName(day), Is.EqualTo("2026-10-17"));
        Assert.That(FolderNames.DateFolderName(day, 3), Is.EqualTo("2026-10-17 (3)"));
        Assert.That(FolderNames.LaterClassOfItsDay(@"C:\Users\x\ClassScribe\Mantiq\2026-10-17 (2)\"), Is.EqualTo("2026-10-17 (2)"));
        Assert.That(FolderNames.LaterClassOfItsDay(@"C:\Users\x\ClassScribe\Mantiq\2026-10-17"), Is.Null);
        Assert.That(FolderNames.LaterClassOfItsDay(@"C:\Users\x\ClassScribe\Mantiq"), Is.Null);
    }

    /// <summary>The microphone check's logs have a folder of their own name, which no course's date folder can be.</summary>
    [Test]
    public void The_microphone_checks_folder_is_never_a_date_folder()
    {
        Assert.That(FolderNames.MicCheck, Is.EqualTo("mic-check"));
        Assert.That(FolderNames.IsDateFolder(FolderNames.MicCheck), Is.False);
    }
}