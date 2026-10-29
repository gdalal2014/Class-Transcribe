using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClassScribe.Storage;

namespace ClassScribe.Drive;

/// <summary>Where a recording stands with Google Drive.</summary>
public enum UploadState
{
    /// <summary>It waits to go up, or to go up whole: it is in the queue.</summary>
    Waiting,

    /// <summary>Every file of it that goes up is on Drive, each checked against Drive's MD5.</summary>
    Done,

    /// <summary>
    /// Its folder is not there now, on a drive that is: it was moved or deleted by hand before all of it went up. It is out
    /// of the queue; what of it went up stays on Drive, and asked for again, once its folder is back, it goes into the queue.
    /// </summary>
    Missing
}

/// <summary>
/// An upload to Drive that has begun and not ended: the session's address, which lets the upload go on where it stopped, even
/// after a restart, for up to a week; what it was started for (the size and MD5 of the content, and the file a new revision
/// is of); and how much Drive has confirmed. The address is never shown or logged: as text the record leaves it out.
/// </summary>
/// <param name="Address">The session's address, as Drive gave it in its Location header.</param>
/// <param name="StartedUtc">When the session was started.</param>
/// <param name="Size">The size of the content it was started for.</param>
/// <param name="Md5">The MD5 of that content, as Drive writes it: 32 lowercase hexadecimal digits.</param>
/// <param name="Confirmed">How many bytes from the start Drive has confirmed.</param>
/// <param name="RevisionOf">The id of the file on Drive it makes a new revision of; null for a new file.</param>
public sealed record UploadSession(string Address, DateTimeOffset StartedUtc, long Size, string Md5, long Confirmed, string? RevisionOf)
{
    /// <summary>The session as text, without its address.</summary>
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("StartedUtc = ").Append(StartedUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture))
            .Append(", Size = ").Append(Size)
            .Append(", Confirmed = ").Append(Confirmed)
            .Append(", RevisionOf = ").Append(RevisionOf);
        return true;
    }
}

/// <summary>
/// One file of a recording, as the manifest remembers it: what it was on this laptop when last looked at (its size, when it
/// was last written, its MD5, worked out again only when one of those two changes, and for audio its length), its copy on
/// Drive once checked (the id, size and MD5 Drive gave, and when), when it was deleted from this laptop to free space, and an
/// upload of it that has begun and not ended.
/// </summary>
/// <param name="Name">The file's name, in the recording's folder.</param>
public sealed record ManifestFile(string Name)
{
    /// <summary>Its size on this laptop when last looked at.</summary>
    public long Size { get; init; }

    /// <summary>When it was last written, as Windows said when it was last looked at.</summary>
    public DateTimeOffset LastWriteUtc { get; init; }

    /// <summary>Its MD5 for that size and time, 32 lowercase hexadecimal digits; null until worked out.</summary>
    public string? Md5 { get; init; }

    /// <summary>For audio, its length in seconds, kept so that the length is still known once the file is gone from this laptop.</summary>
    public double? Seconds { get; init; }

    /// <summary>Drive's id for its copy; null until it has one.</summary>
    public string? DriveId { get; init; }

    /// <summary>The size of the copy on Drive, as Drive said when it was checked.</summary>
    public long? DriveSize { get; init; }

    /// <summary>The MD5 of the copy on Drive, as Drive said when it was checked.</summary>
    public string? DriveMd5 { get; init; }

    /// <summary>When the copy on Drive was checked and found to match the file; null until then.</summary>
    public DateTimeOffset? VerifiedUtc { get; init; }

    /// <summary>When ClassScribe deleted the file from this laptop to free space, after checking its copy on Drive; null while it is kept.</summary>
    public DateTimeOffset? FreedUtc { get; init; }

    /// <summary>An upload of it that has begun and not ended; null when there is none.</summary>
    public UploadSession? Session { get; init; }

    /// <summary>
    /// Fields of the file this build does not know, as a later build wrote them, kept so that writing the manifest back loses
    /// none of them (layout-config 6.3); null when there were none, so that files still compare equal.
    /// </summary>
    [JsonExtensionData]
    // A setter, not init: the generated reader fills an init-only property through the constructor, where extension data
    // cannot go.
    public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>True once a copy on Drive has been checked against Drive's size and MD5.</summary>
    [JsonIgnore]
    public bool IsVerified => DriveId is not null && VerifiedUtc is not null && DriveMd5 is not null && DriveSize is not null;

    /// <summary>True when the checked copy on Drive is the file as it was when last looked at: the same size and MD5.</summary>
    [JsonIgnore]
    public bool InSync => IsVerified && Md5 is not null && Size == DriveSize && string.Equals(Md5, DriveMd5, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// What ClassScribe remembers of one recording and Google Drive, in %LOCALAPPDATA%\ClassScribe\drive\recordings: its folder
/// and base name; whether it waits in the queue or is done, since when, how many tries, when the next is due and what went
/// wrong last; where it is on Drive; and each of its files (see <see cref="ManifestFile"/>). So nothing is uploaded twice,
/// an upload goes on where it stopped, even after a restart, and a file deleted from this laptop after it went up is known
/// to be safe on Drive rather than missing.
/// </summary>
/// <param name="Folder">The recording's folder on this laptop.</param>
/// <param name="BaseName">The recording's base name, such as Mantiq_2026-10-17_14-04-42Z.</param>
public sealed record UploadManifest(string Folder, string BaseName)
{
    /// <summary>The version of this file's layout.</summary>
    public const int CurrentVersion = 1;

    /// <summary>The version of the layout it was written in.</summary>
    public int Version { get; init; } = CurrentVersion;

    /// <summary>Whether it waits in the queue or is done.</summary>
    public UploadState State { get; init; } = UploadState.Waiting;

    /// <summary>When it was first put in the queue.</summary>
    public DateTimeOffset QueuedUtc { get; init; }

    /// <summary>
    /// How many times it has been put in the queue (see <see cref="IUploadManifests.Queue"/>). An upload that began before the
    /// last of them, as when a transcript made in another window while this one goes up adds a (2).md, does not leave it done:
    /// it stays waiting, to be taken once more, so that nothing new is left behind.
    /// </summary>
    public int TimesQueued { get; init; }

    /// <summary>How many times an upload of it has begun.</summary>
    public int Attempts { get; init; }

    /// <summary>When an upload of it last began; null before the first.</summary>
    public DateTimeOffset? LastTriedUtc { get; init; }

    /// <summary>When an upload that comes after a class should try it again, no sooner; null for at once.</summary>
    public DateTimeOffset? NextAttemptUtc { get; init; }

    /// <summary>What went wrong the last time, in plain words; null when nothing did.</summary>
    public string? LastError { get; init; }

    /// <summary>When it was last found done; null while it waits.</summary>
    public DateTimeOffset? DoneUtc { get; init; }

    /// <summary>
    /// When an upload of it last wrote here, as it does at each step and at each piece Drive confirms; null before the first.
    /// A recent one, during a try that has not failed, tells an upload going on now, in this process or in another (see
    /// <see cref="IsBeingUploaded"/>).
    /// </summary>
    public DateTimeOffset? UpdatedUtc { get; init; }

    /// <summary>Drive's id for the folder its files went into.</summary>
    public string? DriveFolderId { get; init; }

    /// <summary>Where that folder is, from ClassScribe's own folder down, as ClassScribe/Mantiq/2026-10-17.</summary>
    public string? DrivePath { get; init; }

    /// <summary>Its files, in the order they go up.</summary>
    public IReadOnlyList<ManifestFile> Files { get; init; } = [];

    /// <summary>
    /// Fields of the manifest this build does not know, as a later build wrote them, kept so that an upload writing it back
    /// loses none of them (layout-config 6.3): an older build sharing the queue no longer drops what a newer one keeps here.
    /// Null when there were none, so that manifests still compare equal.
    /// </summary>
    [JsonExtensionData]
    // A setter, not init: the generated reader fills an init-only property through the constructor, where extension data
    // cannot go.
    public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>The recording the manifest is of.</summary>
    [JsonIgnore]
    public RecordingFiles Recording => new(Folder, BaseName);

    /// <summary>The folder on Drive in a browser; null before it is known.</summary>
    [JsonIgnore]
    public string? DriveLink => DriveFolderId is { } id ? new DriveFolder(id, "").Link : null;

    /// <summary>Where it is on Drive, as Gulzar finds it there: My Drive &gt; ClassScribe &gt; Mantiq &gt; 2026-10-17.</summary>
    [JsonIgnore]
    public string DriveLocation => DrivePath is { Length: > 0 } path ? $"My Drive > {string.Join(" > ", path.Split('/'))}" : "Google Drive";

    /// <summary>
    /// How much of it Drive has: the files whose checked copy is the file as it was last looked at, and the bytes Drive has
    /// confirmed of the uploads begun and not ended.
    /// </summary>
    [JsonIgnore]
    public long BytesOnDrive => Files.Sum(file => (file.InSync ? file.DriveSize ?? 0 : 0) + (file.Session?.Confirmed ?? 0));

    /// <summary>
    /// How long after an upload last wrote here it still counts as going on: longer than a piece of 8 MiB takes at the
    /// slowest speed an upload allows, about 4.5 minutes.
    /// </summary>
    public static readonly TimeSpan GoingOnWithin = TimeSpan.FromMinutes(5);

    /// <summary>
    /// True when an upload of it is going on at <paramref name="now"/>: it waits, a try has begun and not failed, and the
    /// upload wrote here within <see cref="GoingOnWithin"/>. A process that ended in the middle stops counting after that.
    /// </summary>
    public bool IsBeingUploaded(DateTimeOffset now) =>
        State == UploadState.Waiting && LastTriedUtc is not null && LastError is null && UpdatedUtc is { } updated && now - updated <= GoingOnWithin;

    /// <summary>The file named <paramref name="name"/>, in any letter case, as Windows takes names; null when there is none.</summary>
    public ManifestFile? File(string name) => Files.FirstOrDefault(file => string.Equals(file.Name, name, StringComparison.OrdinalIgnoreCase));
}
