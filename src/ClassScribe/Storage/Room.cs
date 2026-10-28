namespace ClassScribe.Storage;

/// <summary>
/// The room recording takes on a drive, as a run's checks of the free space count it and as a YouTube batch keeps it for the
/// next class: what an hour of one track and an hour of the upload copy take, and the room a class of a given length takes.
/// </summary>
public static class Room
{
    /// <summary>At the start, room for less recording than this is warned about; a YouTube batch keeps this much room for a class.</summary>
    public static readonly TimeSpan SpaceWarningBelow = TimeSpan.FromHours(3);

    /// <summary>What an hour of one track takes on disk: 48 kHz, 16-bit, mono, about 346 MB.</summary>
    public const long TrackBytesPerHour = 48_000L * 2 * 3600;

    /// <summary>What an hour of the upload copy takes on disk: 16 kHz, 16-bit, mono, about 115 MB.</summary>
    public const long UploadBytesPerHour = 16_000L * 2 * 3600;

    /// <summary>
    /// The room a class of <paramref name="length"/> takes on its drive, as a run's checks of the free space count it: its one track and the
    /// upload copy. A YouTube batch keeps this much free for the next class (see <see cref="SpaceWarningBelow"/>).
    /// </summary>
    public static long For(TimeSpan length) => (long)Math.Ceiling(length.TotalHours * (TrackBytesPerHour + UploadBytesPerHour));
}