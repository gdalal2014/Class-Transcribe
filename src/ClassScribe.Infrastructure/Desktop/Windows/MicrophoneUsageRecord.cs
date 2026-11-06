using System.Runtime.Versioning;
using ClassScribe.Audio.Microphones;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Desktop.Windows;

/// <summary>
/// Reads Windows' record of which apps used the microphone and when: the record behind Settings, Privacy and security,
/// Microphone, Recent activity. Store apps are subkeys of the microphone key; desktop apps are subkeys of its NonPackaged
/// key, named by their program's path with # in place of \. LastUsedTimeStart and LastUsedTimeStop are FILETIMEs in UTC,
/// and a stop time of 0 while a start time is set means the app has the microphone open now. Every key is opened for
/// reading and closed at once (see <see cref="ConsentStoreRecord"/>, which the camera's record shares).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MicrophoneUsageRecord(ILogger<MicrophoneUsageRecord> logger) : IMicrophoneUsageRecord
{
    /// <summary>The record's key under HKEY_CURRENT_USER.</summary>
    public const string MicrophoneKey = $@"{ConsentStoreRecord.ConsentStore}\microphone";

    /// <inheritdoc />
    public UsageRecordListing Read() => ConsentStoreRecord.Read(MicrophoneKey, "microphone", logger);
}
