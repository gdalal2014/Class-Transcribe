using System.Globalization;
using ClassScribe.Recorder;
using ClassScribe.Storage;
using Microsoft.Extensions.Options;

namespace ClassScribe.Drive;

/// <summary>What waits to go up to Google Drive, as the report at the start offers it.</summary>
/// <param name="Line">What waits, in words: how many recordings, which one when there is one, how much, and the last try.</param>
/// <param name="Count">How many recordings wait.</param>
/// <param name="SignedIn">True when ClassScribe is signed in, so that an upload can be offered; false when a sign-in comes first.</param>
public sealed record DriveWaiting(string Line, int Count, bool SignedIn);

/// <summary>
/// What the menu's header and the report at the start say of Google Drive (design sections 3.3 and 4.3), from the store and
/// the manifests alone: Google is asked nothing, so the menu is never held up. Before Drive is set up, nothing at all. A
/// recording being recorded or transcribed now, which its own run put in the queue as it started, does not count as
/// waiting, and nor does one being uploaded now, in this process or in another (see
/// <see cref="UploadManifest.IsBeingUploaded"/>), which the header shows with how far it is. The settings are read each time,
/// and the local times are those of the clock's time zone. One for the process.
/// </summary>
public sealed class DriveQueueReport(IGoogleCredentialStore store, IUploadManifests manifests, IRunJournal journal, IClassFolders folders, IOptionsMonitor<DriveOptions> options, TimeProvider time)
{
    /// <summary>How the time of a last try is written, in local time: 17 Oct 21:40.</summary>
    private const string TryFormat = "d MMM HH:mm";

    /// <summary>
    /// The menu header's line: signed in with nothing waiting; how many recordings wait, how much, and that 9 &gt; U uploads
    /// them; the one uploading now, with how far it is; or not signed in, or a store that cannot be read, with what waits and
    /// how to sign in. With the uploads after each class off (Drive:Enabled), it says so. Null before Drive is set up.
    /// </summary>
    public string? HeaderLine()
    {
        var stored = store.Read();
        if (stored is { Credentials: null, Problem: null }) return null;

        var (waiting, uploading) = Queue();
        var count = waiting.Count;
        if (stored.Problem is not null) return $"Google Drive: the sign-in cannot be read{Waits(count, uploading)}. 9 > S signs in again.";
        if (stored.Credentials is not { IsSignedIn: true }) return $"Google Drive: not signed in{Waits(count, uploading)}. 9 > S signs in.";

        var off = options.CurrentValue.Enabled ? "" : " Uploads after each class are off (Drive:Enabled).";
        if (uploading is { } now)
        {
            var more = count == 0 ? "" : count == 1 ? "; 1 more waits" : $"; {count} more wait";
            return $"Google Drive: uploading {now.BaseName}{Percent(now)}{more}.{off}";
        }

        return count == 0
            ? $"Google Drive: signed in; nothing waits to go up.{off}"
            : $"Google Drive: {Recordings(count)} to go up ({Sizes.Describe(Bytes(waiting))}). 9 > U uploads {(count == 1 ? "it" : "them")}.{off}";
    }

    /// <summary>
    /// What waits to go up, for the report at the start: one recording by its name, its size and its last try; several by
    /// their number, their size in all and the latest try. Null before Drive is set up, and when nothing waits.
    /// </summary>
    public DriveWaiting? Waiting()
    {
        var stored = store.Read();
        if (stored is { Credentials: null, Problem: null }) return null;

        var (waiting, _) = Queue();
        if (waiting.Count == 0) return null;

        var latest = waiting.Where(manifest => manifest.LastTriedUtc is not null).MaxBy(manifest => manifest.LastTriedUtc) ?? waiting[0];
        var size = Sizes.Describe(Bytes(waiting));
        var what = waiting.Count == 1 ? $"{waiting[0].BaseName}, {size}" : $"{size} in all";
        return new DriveWaiting($"Google Drive: {Recordings(waiting.Count)} to go up ({what}; {Tried(latest)}).", waiting.Count, stored.Credentials is { IsSignedIn: true });
    }

    /// <summary>
    /// The recordings that wait to go up, the one queued first first, one being uploaded now among them, without those being
    /// recorded or transcribed now, which their own runs queued and which go up once they have stopped: what menu choice 9's
    /// U counts and drive status lists, as the header and the report at the start count them.
    /// </summary>
    public IReadOnlyList<UploadManifest> Queued()
    {
        var runs = journal.List();
        return [.. manifests.Waiting().Where(manifest => RecordingUpload.BusyIn(runs, manifest.Recording, folders) is null)];
    }

    /// <summary>
    /// The recordings that wait (see <see cref="Queued"/>), without the one being uploaded now, which comes apart; the most
    /// recent of those, when several are.
    /// </summary>
    private (List<UploadManifest> Waiting, UploadManifest? Uploading) Queue()
    {
        var now = time.GetUtcNow();
        var queued = Queued();
        var uploading = queued.Where(manifest => manifest.IsBeingUploaded(now)).MaxBy(manifest => manifest.UpdatedUtc);
        return ([.. queued.Where(manifest => manifest != uploading)], uploading);
    }

    /// <summary>"; 2 recordings wait to go up", counting the one uploading now, for a line that cannot upload them; empty when none waits.</summary>
    private static string Waits(int count, UploadManifest? uploading)
    {
        var all = count + (uploading is null ? 0 : 1);
        return all == 0 ? "" : $"; {Recordings(all)} to go up";
    }

    /// <summary>"1 recording waits" or "3 recordings wait".</summary>
    private static string Recordings(int count) => count == 1 ? "1 recording waits" : $"{count} recordings wait";

    /// <summary>The size of the recordings' files on this laptop, the upload copy left out.</summary>
    private long Bytes(IEnumerable<UploadManifest> waiting) => waiting.Sum(manifest => OnDisk(manifest.Recording).Bytes);

    /// <summary>
    /// How many files of <paramref name="files"/> wait on this laptop to go up and their size (see
    /// <see cref="IUploadManifests.OnDisk"/>), a lesson's finished video counted only with Drive:LessonVideos on, as it is now.
    /// </summary>
    public (int Count, long Bytes) OnDisk(Storage.RecordingFiles files) => manifests.OnDisk(files, options.CurrentValue.LessonVideos);

    /// <summary>
    /// " (41%)": how much of the recording uploading now Drive has, against its files on this laptop, never 100% before it is
    /// done; empty when nothing of it is on this laptop to measure.
    /// </summary>
    private string Percent(UploadManifest uploading)
    {
        var total = OnDisk(uploading.Recording).Bytes;
        if (total == 0) return "";

        var percent = (int)Math.Min(99, uploading.BytesOnDrive * 100 / total);
        return string.Create(CultureInfo.InvariantCulture, $" ({percent}%)");
    }

    /// <summary>The last try of <paramref name="manifest"/>, in local time, and what went wrong then: "last try 17 Oct 21:40: ...", or "not tried yet".</summary>
    private string Tried(UploadManifest manifest)
    {
        if (manifest.LastTriedUtc is not { } last) return "not tried yet";

        var when = TimeZoneInfo.ConvertTime(last, time.LocalTimeZone).ToString(TryFormat, CultureInfo.InvariantCulture);
        return manifest.LastError is { } error ? $"last try {when}: {error.TrimEnd().TrimEnd('.')}" : $"last try {when}, not finished";
    }
}
