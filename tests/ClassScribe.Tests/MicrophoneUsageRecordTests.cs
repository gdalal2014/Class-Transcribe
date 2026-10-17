using System.Runtime.Versioning;
using ClassScribe.Audio.Microphones;
using ClassScribe.Desktop.Windows;
using Microsoft.Win32;

namespace ClassScribe.Tests;

/// <summary>What one app's entry in Windows' microphone usage record says, from its two times.</summary>
[TestFixture]
public class UsageEntryTests
{
    /// <summary>A start time with a stop time of 0, or none, means the app has the microphone open now; a later stop time means it has closed it; no times mean no use.</summary>
    [TestCase(133_000_000_000_000_000L, 0L, true, true)]
    [TestCase(133_000_000_000_000_000L, null, true, true)]
    [TestCase(133_000_000_000_000_000L, 133_000_001_000_000_000L, false, true)]
    [TestCase(null, null, false, false)]
    [TestCase(0L, 0L, false, false)]
    [TestCase(null, 133_000_001_000_000_000L, false, true)]
    public void The_times_say_whether_the_app_has_the_microphone_open(long? start, long? stop, bool inUse, bool hasUsage)
    {
        var entry = new UsageEntry(@"NonPackaged\C:#chrome.exe", "chrome.exe", @"C:\chrome.exe", start, stop);

        Assert.Multiple(() =>
        {
            Assert.That(entry.InUse, Is.EqualTo(inUse));
            Assert.That(entry.HasUsage, Is.EqualTo(hasUsage));
        });
    }

    /// <summary>The name the follow rule matches is the file name without .exe for a desktop app, and the package family name for a Store app.</summary>
    [TestCase("chrome.exe", "chrome")]
    [TestCase("Zoom.EXE", "Zoom")]
    [TestCase("MSTeams_8wekyb3d8bbwe", "MSTeams_8wekyb3d8bbwe")]
    public void The_process_name_drops_exe(string app, string processName)
    {
        var entry = new UsageEntry(app, app, null, null, null);

        Assert.That(entry.ProcessName, Is.EqualTo(processName));
    }
}

/// <summary>
/// Windows' microphone usage record read from this user's registry. Nothing is written; the keys are opened for reading
/// and closed at once.
/// </summary>
[TestFixture, Platform("Win"), Category("WindowsAudio")]
[SupportedOSPlatform("windows")]
public sealed class MicrophoneUsageRecordTests
{
    /// <summary>
    /// The record is read from this user's key: each app's entry has its key, its short name, and for a desktop app its
    /// path with the backslashes put back; and the test's own program, which never opens a microphone, has no entry
    /// that says it has one open.
    /// </summary>
    [Test]
    public void The_record_is_read_from_this_users_key()
    {
        using var key = Registry.CurrentUser.OpenSubKey(MicrophoneUsageRecord.MicrophoneKey, writable: false);
        Assume.That(key, Is.Not.Null, "This user has no microphone usage record.");

        var log = new RecordingLogger<MicrophoneUsageRecord>();
        var listing = new MicrophoneUsageRecord(log).Read();

        Assert.Multiple(() =>
        {
            Assert.That(listing.Problem, Is.Null);
            Assert.That(log.Problems, Is.Empty);
            foreach (var entry in listing.Apps)
            {
                Assert.That(entry.Key, Is.Not.Empty);
                Assert.That(entry.App, Is.Not.Empty);
                if (entry.Path is { } path)
                {
                    Assert.That(path, Does.Not.Contain("#"));
                    Assert.That(entry.Key, Does.StartWith(@"NonPackaged\"));
                    Assert.That(entry.App, Is.EqualTo(Path.GetFileName(path)));
                }
                else
                {
                    Assert.That(entry.Key, Is.EqualTo(entry.App));
                }
            }

            var own = Path.GetFileName(Environment.ProcessPath);
            Assert.That(listing.Apps.Where(entry => entry.InUse).Select(entry => entry.App), Has.None.EqualTo(own).IgnoreCase, "this program has no microphone open");
        });
    }

    /// <summary>Reading twice gives the same entries: the read changes nothing.</summary>
    [Test]
    public void Reading_changes_nothing()
    {
        using var key = Registry.CurrentUser.OpenSubKey(MicrophoneUsageRecord.MicrophoneKey, writable: false);
        Assume.That(key, Is.Not.Null, "This user has no microphone usage record.");
        var record = new MicrophoneUsageRecord(new RecordingLogger<MicrophoneUsageRecord>());

        var first = record.Read();
        var second = record.Read();

        Assert.That(second.Apps.Select(entry => entry.Key), Is.EquivalentTo(first.Apps.Select(entry => entry.Key)));
    }
}
