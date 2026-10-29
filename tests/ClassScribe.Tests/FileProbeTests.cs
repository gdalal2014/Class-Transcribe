using ClassScribe.Desktop;

namespace ClassScribe.Tests;

/// <summary>
/// The contract of <see cref="IFileProbe"/> over its real adapter, in a temporary folder: whether a file or a folder is
/// there, when a file was last written, how long it is, both from one look, and a full path, each as the file system
/// answers it.
/// </summary>
[TestFixture]
public class FileProbeTests
{
    private TempFolder _folder = null!;
    private FileProbe _probe = null!;

    /// <summary>Creates a temporary folder and the probe.</summary>
    [SetUp]
    public void SetUp()
    {
        _folder = new TempFolder();
        _probe = new FileProbe();
    }

    /// <summary>Deletes the temporary folder.</summary>
    [TearDown]
    public void TearDown() => _folder.Dispose();

    /// <summary>A file is there and a folder is not a file; a folder is there and a file is not a folder; nothing is at a path that leads nowhere.</summary>
    [Test]
    public void Tells_a_file_from_a_folder_and_from_nothing()
    {
        var file = _folder.File("Kalam_2026-10-15_20-55-27Z.marks.txt");
        File.WriteAllText(file, "00:00:01\n");
        var inner = _folder.File("Slides");
        Directory.CreateDirectory(inner);

        Assert.Multiple(() =>
        {
            Assert.That(_probe.FileExists(file), Is.True);
            Assert.That(_probe.FileExists(inner), Is.False, "a folder is no file");
            Assert.That(_probe.FileExists(_folder.File("missing.txt")), Is.False);
            Assert.That(_probe.DirectoryExists(inner), Is.True);
            Assert.That(_probe.DirectoryExists(file), Is.False, "a file is no folder");
            Assert.That(_probe.DirectoryExists(_folder.File("missing")), Is.False);
        });
    }

    /// <summary>The last write time is the file system's, in UTC, and a file that is not there gets the file system's earliest time rather than an error.</summary>
    [Test]
    public void Reads_when_a_file_was_last_written()
    {
        var file = _folder.File("Kalam_2026-10-15_20-55-27Z.log");
        File.WriteAllText(file, "a line\n");
        var written = new DateTime(2026, 10, 15, 21, 30, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(file, written);

        Assert.Multiple(() =>
        {
            Assert.That(_probe.LastWriteUtc(file), Is.EqualTo(written));
            Assert.That(_probe.LastWriteUtc(file).Kind, Is.EqualTo(DateTimeKind.Utc));
            Assert.That(_probe.LastWriteUtc(_folder.File("missing.log")), Is.EqualTo(File.GetLastWriteTimeUtc(_folder.File("missing.log"))));
            Assert.That(_probe.LastWriteUtc(_folder.File("missing.log")).Year, Is.EqualTo(1601), "the file system's earliest time stands for a file that is not there");
        });
    }

    /// <summary>The length is the file's size in bytes, and a file that is not there is an error, as a caller that counts on the file expects.</summary>
    [Test]
    public void Reads_how_long_a_file_is()
    {
        var file = _folder.File("Kalam_2026-10-15_20-55-27Z.wav");
        File.WriteAllBytes(file, new byte[123]);

        Assert.Multiple(() =>
        {
            Assert.That(_probe.Length(file), Is.EqualTo(123));
            Assert.Throws<FileNotFoundException>(() => _probe.Length(_folder.File("missing.wav")));
            Assert.Throws<FileNotFoundException>(() => _probe.Length(_folder.File(Path.Combine("missing", "missing.wav"))), "a file whose folder is not there is a missing file too");
        });
    }

    /// <summary>One look gives a file's length and last write together, in UTC; and null for a file that is not there, for a folder, and for a file whose folder is not there.</summary>
    [Test]
    public void Describes_a_file_in_one_look()
    {
        var file = _folder.File("Kalam_2026-10-15_20-55-27Z.wav");
        File.WriteAllBytes(file, new byte[123]);
        var written = new DateTime(2026, 10, 15, 21, 30, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(file, written);
        var inner = _folder.File("Slides");
        Directory.CreateDirectory(inner);

        Assert.Multiple(() =>
        {
            Assert.That(_probe.Describe(file), Is.EqualTo((123L, written)));
            Assert.That(_probe.Describe(file)!.Value.LastWriteUtc.Kind, Is.EqualTo(DateTimeKind.Utc));
            Assert.That(_probe.Describe(inner), Is.Null, "a folder is no file");
            Assert.That(_probe.Describe(_folder.File("missing.wav")), Is.Null);
            Assert.That(_probe.Describe(_folder.File(Path.Combine("missing", "missing.wav"))), Is.Null, "a file whose folder is not there is not there either");
        });
    }

    /// <summary>A full path is resolved as the file system resolves it: a relative path against the current folder, and dots and doubled separators folded away.</summary>
    [Test]
    public void Gives_the_full_path()
    {
        var nested = Path.Combine(_folder.Path, "Mantiq", "..", "Mantiq", "2026-10-17");

        Assert.Multiple(() =>
        {
            Assert.That(_probe.FullPath(nested), Is.EqualTo(Path.Combine(_folder.Path, "Mantiq", "2026-10-17")));
            Assert.That(_probe.FullPath("Kalam"), Is.EqualTo(Path.Combine(Directory.GetCurrentDirectory(), "Kalam")));
            Assert.That(_probe.FullPath(_folder.Path + Path.DirectorySeparatorChar), Is.EqualTo(_folder.Path + Path.DirectorySeparatorChar));
        });
    }
}
