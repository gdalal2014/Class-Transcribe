using System.Runtime.Versioning;
using System.Security;
using ClassScribe.Audio.Microphones;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace ClassScribe.Desktop.Windows;

/// <summary>
/// Reads one of Windows' records of which apps used a device and when, under HKEY_CURRENT_USER's ConsentStore: the
/// microphone's (<see cref="MicrophoneUsageRecord"/>) or the camera's (webcam, teach-video 6.10). Store apps are subkeys of
/// the capability's key; desktop apps are subkeys of its NonPackaged key, named by their program's path with # in place of
/// \. LastUsedTimeStart and LastUsedTimeStop are FILETIMEs in UTC, and a stop time of 0 while a start time is set means the
/// app has the device open now. The key's own Value, and NonPackaged's, are Windows' privacy switches for all apps and for
/// desktop apps (Allow or Deny). Every key is opened for reading and closed at once; nothing is written.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ConsentStoreRecord
{
    /// <summary>The ConsentStore under HKEY_CURRENT_USER (and HKEY_LOCAL_MACHINE, for the switch of the whole device).</summary>
    public const string ConsentStore = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";

    /// <summary>The subkey that holds desktop apps, which Windows calls non-packaged.</summary>
    public const string DesktopAppsKey = "NonPackaged";

    /// <summary>
    /// The record under <paramref name="key"/>, a path below HKEY_CURRENT_USER, of the device <paramref name="what"/> names
    /// (microphone, camera), as it is now. A record that cannot be read gives a listing with its problem; nothing is thrown.
    /// </summary>
    public static UsageRecordListing Read(string key, string what, ILogger logger)
    {
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(key, writable: false);
            if (root is null) return new UsageRecordListing(null, null, [], $@"HKEY_CURRENT_USER\{key} does not exist");

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
            // Deliberately not rethrown: the record only corroborates and names apps, and never decides anything on its own,
            // so a record that cannot be read is a listing that says why, and the caller goes on without it. Noted at Debug,
            // because the microphone's watch reads it four times a second.
            logger.LogDebug(ex, "Could not read Windows' {What} usage record ({Reason})", what, ex.Message);
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
