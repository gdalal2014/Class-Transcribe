using System.Runtime.Versioning;
using System.Security;
using ClassScribe.Audio.Microphones;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace ClassScribe.Desktop.Windows;

/// <summary>
/// Reads Windows' record of which apps used the microphone and when: the record behind Settings, Privacy and security,
/// Microphone, Recent activity. Store apps are subkeys of the microphone key; desktop apps are subkeys of its NonPackaged
/// key, named by their program's path with # in place of \. LastUsedTimeStart and LastUsedTimeStop are FILETIMEs in UTC,
/// and a stop time of 0 while a start time is set means the app has the microphone open now. Every key is opened for
/// reading and closed at once.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MicrophoneUsageRecord(ILogger<MicrophoneUsageRecord> logger) : IMicrophoneUsageRecord
{
    /// <summary>The record's key under HKEY_CURRENT_USER.</summary>
    public const string MicrophoneKey = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";

    /// <summary>The subkey that holds desktop apps, which Windows calls non-packaged.</summary>
    private const string DesktopAppsKey = "NonPackaged";

    /// <inheritdoc />
    public UsageRecordListing Read()
    {
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(MicrophoneKey, writable: false);
            if (root is null) return new UsageRecordListing(null, null, [], $@"HKEY_CURRENT_USER\{MicrophoneKey} does not exist");

            var apps = new List<UsageEntry>();
            string? desktopAccess = null;
            foreach (var name in root.GetSubKeyNames())
            {
                if (name.Equals(DesktopAppsKey, StringComparison.OrdinalIgnoreCase))
                {
                    using var desktop = root.OpenSubKey(name, writable: false);
                    if (desktop is null) continue;

                    desktopAccess = desktop.GetValue("Value") as string;
                    foreach (var app in desktop.GetSubKeyNames()) AddApp(desktop, app, packaged: false, apps);
                }
                else
                {
                    AddApp(root, name, packaged: true, apps);
                }
            }

            return new UsageRecordListing(root.GetValue("Value") as string, desktopAccess, apps, null);
        }
        catch (Exception ex) when (ex is IOException or SecurityException or UnauthorizedAccessException)
        {
            // Deliberately not rethrown: the record only corroborates the sessions and names apps, and never decides
            // anything on its own, so a record that cannot be read is a listing that says why, and the watch goes on
            // without it. Noted at Debug, because the watch reads it four times a second.
            logger.LogDebug(ex, "Could not read Windows' microphone usage record ({Reason})", ex.Message);
            return new UsageRecordListing(null, null, [], $"{ex.GetType().Name}: {ex.Message.Trim()}");
        }
    }

    /// <summary>Adds one app's entry; an entry removed since its parent was listed is left out, as the next read will show.</summary>
    private static void AddApp(RegistryKey parent, string name, bool packaged, List<UsageEntry> apps)
    {
        using var key = parent.OpenSubKey(name, writable: false);
        if (key is null) return;

        var path = packaged ? null : name.Replace('#', '\\');
        var app = path is null ? name : Path.GetFileName(path);
        var entryKey = packaged ? name : $@"{DesktopAppsKey}\{name}";
        apps.Add(new UsageEntry(entryKey, app, path, ReadTime(key, "LastUsedTimeStart"), ReadTime(key, "LastUsedTimeStop")));
    }

    /// <summary>Reads a FILETIME value written as a QWORD; null when it is absent or of another kind.</summary>
    private static long? ReadTime(RegistryKey key, string name) => key.GetValue(name) switch
    {
        long value => value,
        _ => null
    };
}
