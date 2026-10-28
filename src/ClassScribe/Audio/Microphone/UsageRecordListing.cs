namespace ClassScribe.Audio.Microphones;

/// <summary>One app's entry in Windows' record of which apps used the microphone, and when.</summary>
/// <param name="Key">The entry's key below the record's key, such as NonPackaged\C:#Program Files#Google#Chrome#Application#chrome.exe.</param>
/// <param name="App">A short name: the program's file name (chrome.exe) for a desktop app, the package family name (MSTeams_8wekyb3d8bbwe) for a Store app.</param>
/// <param name="Path">The program's full path for a desktop app; null for a Store app.</param>
/// <param name="StartFileTime">When the app last opened the microphone, as a Windows FILETIME in UTC; null when the record does not say.</param>
/// <param name="StopFileTime">When it last closed it, the same way; 0 while it has it open; null when the record does not say.</param>
public sealed record UsageEntry(string Key, string App, string? Path, long? StartFileTime, long? StopFileTime)
{
    /// <summary>Whether Windows has recorded any use of the microphone by this app.</summary>
    public bool HasUsage => StartFileTime is > 0 || StopFileTime is > 0;

    /// <summary>Whether Windows counts the app as using the microphone now: a start time, and a stop time of 0 or none.</summary>
    public bool InUse => StartFileTime is > 0 && (StopFileTime is null or 0);

    /// <summary>The app's name as the follow rule matches names: the file name without .exe for a desktop app, the package family name for a Store app.</summary>
    public string ProcessName => App.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? App[..^4] : App;
}

/// <summary>One reading of Windows' microphone usage record.</summary>
/// <param name="Access">The record's own value: whether apps may use the microphone at all (Allow or Deny); null when it is not set.</param>
/// <param name="DesktopAccess">The same for desktop apps; null when it is not set.</param>
/// <param name="Apps">Every app's entry, used or not.</param>
/// <param name="Problem">Why the record could not be read, or null; when set, the other values mean nothing.</param>
public sealed record UsageRecordListing(string? Access, string? DesktopAccess, IReadOnlyList<UsageEntry> Apps, string? Problem);
