using System.Text.RegularExpressions;
using ClassScribe.Audio.Microphones;
using ClassScribe.Settings;
using ClassScribe.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClassScribe.Tests;

/// <summary>
/// The microphone check's log file: named after the local start time without the Z of a recording's stamp, so that it
/// never passes for a recording; each line in the file as soon as it is written; and the header and the cost line that
/// MicWatch's log had.
/// </summary>
[TestFixture]
public class MicCheckLogTests
{
    /// <summary>When the check started, in local time, four hours behind UTC.</summary>
    private static readonly DateTimeOffset Started = new(2026, 10, 17, 19, 0, 12, TimeSpan.FromHours(-4));

    private TempFolder _root = null!;

    /// <summary>A recordings folder of the test's own, under which the mic-check folder goes.</summary>
    [SetUp]
    public void SetUp() => _root = new TempFolder();

    /// <summary>Deletes the folder, which fails when a log was left open.</summary>
    [TearDown]
    public void TearDown() => _root.Dispose();

    /// <summary>The mic-check folder under the recordings folder.</summary>
    private string MicCheckFolder => _root.File(MicCheckLog.FolderName);

    /// <summary>
    /// The log is mic-check_&lt;local start time&gt;.log in the folder given, which is created; its name is no recording's, and a
    /// mic-check folder under the recordings folder lists no recording, so the menu and the list command never show one.
    /// </summary>
    [Test]
    public void The_log_is_named_after_the_local_start_time_and_is_no_recording()
    {
        using var log = MicCheckLog.Create(MicCheckFolder, Started);

        Assert.That(log.Path, Is.EqualTo(Path.Combine(MicCheckFolder, "mic-check_2026-10-17_19-00-12.log")));
        Assert.That(File.Exists(log.Path), Is.True);
        Assert.That(RecordingFiles.TryParse(log.Path), Is.Null, "the check's log is no recording's file");
        Assert.That(ClassFolders.FindNewest(MicCheckFolder), Is.Null, "the mic-check folder holds no recording");

        var folders = new ClassFolders(TestOptions.Create(new RecordingOptions { OutputFolder = _root.Path }), TimeProvider.System, NullLogger<ClassFolders>.Instance);
        Assert.That(folders.ListAll(), Is.Empty, "the recordings folder lists no recording for it");
        Assert.That(folders.ListRecent(10), Is.Empty);
    }

    /// <summary>A second check started within the same second gets the next name, so that no log is ever overwritten.</summary>
    [Test]
    public void A_second_log_in_the_same_second_takes_the_next_name()
    {
        using var first = MicCheckLog.Create(MicCheckFolder, Started);
        using var second = MicCheckLog.Create(MicCheckFolder, Started);
        using var third = MicCheckLog.Create(MicCheckFolder, Started);

        Assert.That(second.Path, Is.EqualTo(Path.Combine(MicCheckFolder, "mic-check_2026-10-17_19-00-12_2.log")));
        Assert.That(third.Path, Is.EqualTo(Path.Combine(MicCheckFolder, "mic-check_2026-10-17_19-00-12_3.log")));
    }

    /// <summary>
    /// A timestamped line is the local time to the millisecond, the kind in a column, and the text; it is in the file as soon
    /// as it is written, while the log is open and readable by another program, and counted; a plain line is neither stamped
    /// nor counted.
    /// </summary>
    [Test]
    public void Each_line_reaches_the_file_as_it_is_written()
    {
        using var log = MicCheckLog.Create(MicCheckFolder, Started);
        var at = Started.AddMilliseconds(177);

        var written = log.Write(new MicCheckLine(at, "MIC", "Microphone Array: at start"));
        log.Plain("Log file: somewhere");
        log.Write(new MicCheckLine(at.AddSeconds(1), "SESSION", "#1 chrome"));

        Assert.That(written, Is.EqualTo("19:00:12.177  MIC      Microphone Array: at start"));
        Assert.That(log.TimedLines, Is.EqualTo(2));
        Assert.That(ReadWhileOpen(log.Path), Is.EqualTo(new[]
        {
            "19:00:12.177  MIC      Microphone Array: at start",
            "Log file: somewhere",
            "19:00:13.177  SESSION  #1 chrome"
        }));
    }

    /// <summary>The file is UTF-8 without a byte order mark, so that a device name such as Intel® reads as it is in any editor.</summary>
    [Test]
    public void The_file_is_utf8_without_a_byte_order_mark()
    {
        using var log = MicCheckLog.Create(MicCheckFolder, Started);
        log.Plain("Intel® Smart Sound");

        var bytes = ReadBytesWhileOpen(log.Path);
        Assert.That(bytes.Take(3), Is.Not.EqualTo(new byte[] { 0xEF, 0xBB, 0xBF }), "no byte order mark");
        Assert.That(System.Text.Encoding.UTF8.GetString(bytes), Does.StartWith("Intel® Smart Sound"));
    }

    /// <summary>
    /// The header says what the check is, when it started with the zone, the Windows version and the check's own process id,
    /// what it reads and that it opens nothing, how often it checks, what the session states mean, where the log is, and how
    /// to stop it: Q or Ctrl+C, or by itself after --seconds.
    /// </summary>
    [Test]
    public void The_header_names_the_check_the_start_the_process_and_the_log()
    {
        var path = Path.Combine(MicCheckFolder, "mic-check_2026-10-17_19-00-12.log");

        var lines = MicCheckLog.HeaderLines(Started, 1234, path, TimeSpan.FromMilliseconds(250), stopAfter: null);
        var timed = MicCheckLog.HeaderLines(Started, 1234, path, TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(60));

        Assert.That(lines[0], Is.EqualTo($"ClassScribe microphone check (build {BuildInfo.Build}): watches what the apps on this computer do with the microphone."));
        Assert.That(lines[1], Is.EqualTo($"Started 2026-10-17 19:00:12 local time (UTC-04:00), Windows {Environment.OSVersion.Version}, ClassScribe's own process id 1234."));
        Assert.That(lines[2], Is.EqualTo("It only reads Windows' lists of microphones and their sessions, and Windows' microphone usage record. It never opens a microphone, records no sound and sends nothing anywhere."));
        Assert.That(lines[3], Is.EqualTo("It checks every 250 ms and writes a line only when something changes; the first lines show how things are at the start."));
        Assert.That(lines[4], Is.EqualTo("Session states: Active = the app is taking sound from the microphone now; Inactive = open but not taking sound; Expired = closed."));
        Assert.That(lines[5], Is.EqualTo($"Log file: {path}"));
        Assert.That(lines[6], Is.EqualTo("Press Q or Ctrl+C to stop. M writes a numbered mark."));
        Assert.That(lines[7], Is.Empty, "a blank line ends the header");
        Assert.That(timed[6], Is.EqualTo("Stops by itself after 60 seconds; Q or Ctrl+C stops it sooner. M writes a numbered mark."));
    }

    /// <summary>The cost line reports the process's own processor time, peak memory, handles and garbage collections, so that a long log shows the check stayed light.</summary>
    [Test]
    public void The_cost_line_reports_the_process()
    {
        var line = MicCheckLog.CostLine();

        Assert.That(line, Does.Match(@"^The check's own cost: \d+ ms of processor time; peak working set \d+\.\d MB; \d+ handles open now; managed heap \d+\.\d MB after \d+ gen 0, \d+ gen 1 and \d+ gen 2 garbage collections\.$"));
    }

    /// <summary>A folder that cannot be made, because a file stands where it should be, fails the creation with the file system's exception for the command to report.</summary>
    [Test]
    public void A_folder_that_cannot_be_made_fails_the_creation()
    {
        File.WriteAllText(MicCheckFolder, "");

        Assert.That(() => MicCheckLog.Create(MicCheckFolder, Started), Throws.InstanceOf<IOException>());
    }

    /// <summary>The lines of the file while the log holds it open for writing, as another program would read them.</summary>
    private static string[] ReadWhileOpen(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return Regex.Split(reader.ReadToEnd().TrimEnd('\r', '\n'), "\r?\n");
    }

    /// <summary>The bytes of the file while the log holds it open for writing.</summary>
    private static byte[] ReadBytesWhileOpen(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}
