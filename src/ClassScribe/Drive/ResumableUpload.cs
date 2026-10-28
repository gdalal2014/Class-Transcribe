using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClassScribe.Drive;

/// <summary>
/// Where the pieces of a resumable upload go: the session's upload id, and for a new revision the id of the file. It comes
/// from the Location header Drive answered with, or from a manifest, and is used only when that address is Drive's own
/// (<see cref="TryParse"/>), so a tampered manifest or a strange answer can never send a file anywhere else. As text it
/// never shows the upload id, which is what lets anyone send to the session.
/// </summary>
/// <param name="UploadId">The session's upload id.</param>
/// <param name="FileId">The id of the file a new revision is of; null for a new file.</param>
internal sealed partial record UploadAddress(string UploadId, string? FileId)
{
    /// <summary>
    /// The session at <paramref name="address"/>, when it is Drive's own: the scheme, host and port of
    /// <paramref name="apiBaseUrl"/> (Drive:ApiBaseUrl), the path of the upload endpoint at <paramref name="apiVersion"/>
    /// (Drive:ApiVersion), upload/drive/v3/files, for a new file, or of the file <paramref name="revisionOf"/>, for a new
    /// revision of it, and an upload id of letters, digits, dashes and underscores. Null for anything else.
    /// </summary>
    public static UploadAddress? TryParse(string? address, string apiBaseUrl, string apiVersion, string? revisionOf)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || !Uri.TryCreate(apiBaseUrl, UriKind.Absolute, out var api)) return null;
        if (!string.Equals(uri.Scheme, api.Scheme, StringComparison.OrdinalIgnoreCase) || !string.Equals(uri.Host, api.Host, StringComparison.OrdinalIgnoreCase)
            || uri.Port != api.Port)
            return null;

        var expected = $"{api.AbsolutePath.TrimEnd('/')}/upload/drive/{apiVersion}/files{(revisionOf is null ? "" : "/" + revisionOf)}";
        if (!string.Equals(uri.AbsolutePath.TrimEnd('/'), expected, StringComparison.Ordinal)) return null;

        var uploadId = HttpUtility.ParseQueryString(uri.Query)["upload_id"];
        return uploadId is not null && UploadIdShape().IsMatch(uploadId) ? new UploadAddress(uploadId, revisionOf) : null;
    }

    /// <summary>The address as text, without its upload id.</summary>
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("FileId = ").Append(FileId);
        return true;
    }

    /// <summary>The shape of an upload id: letters, digits, dashes and underscores, as Drive makes them.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9_\-]{1,512}\z", RegexOptions.CultureInvariant)]
    private static partial Regex UploadIdShape();
}

/// <summary>
/// One file sent to Drive with its resumable upload protocol: a session is started (or a new revision's), and the file goes
/// in pieces of Drive:ChunkMegabytes, each with its Content-Range; a 308 asks for more from the end of its Range, and 200 or
/// 201 is the file, complete. A session kept from before, for the same content and not older than six days, is asked how
/// much it has and goes on from there, even in another process. A piece lost to a dropped connection or a busy Drive is
/// followed by a wait (<see cref="DriveRetry"/>) and the question how much Drive has, and the upload goes on from what Drive
/// confirms; a session that is gone starts again from the beginning, once. A piece may take the longer of 2 minutes and its
/// bytes at 0.25 Mbit/s. The session's address is never logged. One per run.
/// </summary>
public sealed class ResumableUpload(DriveClient drive, DriveRetry retry, IOptionsSnapshot<DriveOptions> options, TimeProvider time, ILogger<ResumableUpload> logger)
{
    /// <summary>How old a kept session may be and still be used: Drive keeps one a week, and a day is left to spare.</summary>
    public static readonly TimeSpan SessionsKeptFor = TimeSpan.FromDays(6);

    /// <summary>The shortest time a piece may take, the answer included.</summary>
    public static readonly TimeSpan ShortestPieceLimit = TimeSpan.FromMinutes(2);

    /// <summary>The slowest upload a piece's time limit allows for, in megabits per second.</summary>
    public const double SlowestMbps = 0.25;

    /// <summary>A MiB.</summary>
    private const int Mebibyte = 1024 * 1024;

    /// <summary>The size of each piece in bytes, for tests; null for Drive:ChunkMegabytes. Drive asks for a multiple of 256 KiB.</summary>
    internal int? PieceSize { get; init; }

    /// <summary>The size of each piece in bytes.</summary>
    private int PieceBytes => PieceSize ?? Math.Clamp(options.Value.ChunkMegabytes, DriveOptions.SmallestChunkMegabytes, DriveOptions.LargestChunkMegabytes) * Mebibyte;

    /// <summary>The time a piece of <paramref name="bytes"/> may take: the longer of 2 minutes and those bytes at 0.25 Mbit/s.</summary>
    public static TimeSpan PieceLimit(long bytes)
    {
        var atSlowest = TimeSpan.FromSeconds(bytes * 8.0 / (SlowestMbps * 1_000_000));
        return atSlowest > ShortestPieceLimit ? atSlowest : ShortestPieceLimit;
    }

    /// <summary>
    /// Sends <paramref name="source"/> to <paramref name="target"/>, going on with <paramref name="kept"/> when it is a usable
    /// session for this very content, and returns the file as Drive made it (at least its id). <paramref name="watch"/> is told
    /// of every change of the session, for the manifest, and may hold the upload up before each piece. A failure that
    /// asking again did not cure is thrown as Drive gave it; the session stays usable unless Drive said it is gone.
    /// </summary>
    public async Task<DriveFile> SendAsync(UploadSource source, UploadTarget target, UploadSession? kept, IUploadWatch watch, CancellationToken cancellationToken)
    {
        var apiBase = options.Value.ApiBaseUrl;
        var (session, address) = Usable(kept, source, target, apiBase);
        if (kept is not null && session is null) watch.SessionChanged(null);

        var restartsLeft = 1;
        var failures = 0;
        var asking = session is not null;
        while (true)
        {
            if (session is null || address is null)
            {
                (session, address) = await StartAsync(source, target, apiBase, cancellationToken);
                watch.SessionChanged(session);
            }
            else if (asking)
            {
                var status = await retry.RunAsync(token => drive.SendPieceAsync(address, () => PieceContent.StatusQuery(source.Length), DriveClient.CallLimit, token),
                    "ask how much of an upload it has", cancellationToken);
                if (status.Outcome == PieceOutcome.Complete) return status.File!;
                if (status.Outcome == PieceOutcome.Gone)
                {
                    (session, address) = Restart(target, ref restartsLeft, watch);
                    continue;
                }

                session = session with { Confirmed = Math.Min(status.Confirmed, source.Length) };
                watch.SessionChanged(session);
            }

            asking = true;
            var offset = session.Confirmed;
            while (true)
            {
                await watch.BeforePieceAsync(cancellationToken);
                var count = (int)Math.Min(PieceBytes, source.Length - offset);
                var from = offset;
                PieceAnswer answer;
                try
                {
                    answer = await drive.SendPieceAsync(address, () => new PieceContent(source.Content, from, count, source.Length), PieceLimit(count), cancellationToken);
                }
                catch (GoogleDriveException ex) when (DriveRetry.IsPassing(ex) && ++failures < DriveRetry.MaxAttempts)
                {
                    // Deliberately not rethrown: a piece lost to a dropped connection or a busy Drive is not sent again blindly;
                    // after a wait, Drive is asked how much it has, and the upload goes on from there. The last failure stands.
                    await retry.WaitAsync(failures, ex, "take a piece of an upload", cancellationToken);
                    break;
                }

                if (answer.Outcome == PieceOutcome.Complete) return answer.File!;
                if (answer.Outcome == PieceOutcome.Gone)
                {
                    (session, address) = Restart(target, ref restartsLeft, watch);
                    asking = false;
                    break;
                }

                var confirmed = Math.Min(answer.Confirmed, source.Length);
                if (confirmed <= offset && count > 0 && ++failures >= DriveRetry.MaxAttempts)
                    throw new GoogleDriveException(DriveProblem.Refused, $"Google Drive took none of the last pieces of {target.Name}; it goes up again with the next upload.");
                if (confirmed > offset) failures = 0;

                offset = confirmed;
                session = session with { Confirmed = offset };
                watch.SessionChanged(session);
            }
        }
    }

    /// <summary>
    /// Starts a new session for <paramref name="source"/>, asked again while Google is out of reach or busy, and checks the
    /// address Drive gave: one that is not Drive's own is refused, and nothing is sent there.
    /// </summary>
    private async Task<(UploadSession Session, UploadAddress Address)> StartAsync(UploadSource source, UploadTarget target, string apiBase, CancellationToken cancellationToken)
    {
        var given = await retry.RunAsync(token => target.RevisionOf is { } fileId
                ? drive.StartRevisionAsync(fileId, source.ContentType, source.Length, token)
                : drive.StartUploadAsync(new DriveFileMetadata(target.Name, source.ContentType, target.FolderId is { } folder ? [folder] : null, AppProperties: null),
                    source.ContentType, source.Length, token),
            "start an upload", cancellationToken);
        if (UploadAddress.TryParse(given, apiBase, options.Value.ApiVersion, target.RevisionOf) is not { } address)
        {
            logger.LogDebug("Google Drive gave an upload address for {Name} that is not Drive's own; nothing was sent there", target.Name);
            throw new GoogleDriveException(DriveProblem.Refused, $"Google Drive answered with an upload address for {target.Name} that is not its own, so nothing was sent there.");
        }

        logger.LogDebug("Started an upload of {Name} ({Bytes} bytes{Revision})", target.Name, source.Length, target.RevisionOf is null ? "" : ", a new revision");
        return (new UploadSession(given, time.GetUtcNow(), source.Length, source.Md5, 0, target.RevisionOf), address);
    }

    /// <summary>Drops a session Drive says is gone, so the upload starts again from the beginning, once; a second time it is a failure.</summary>
    private (UploadSession? Session, UploadAddress? Address) Restart(UploadTarget target, ref int restartsLeft, IUploadWatch watch)
    {
        if (restartsLeft-- <= 0)
            throw new GoogleDriveException(DriveProblem.Refused, $"Google Drive lost the upload of {target.Name} twice; it starts again with the next upload.");

        logger.LogDebug("The upload session of {Name} is gone; starting again from the beginning", target.Name);
        watch.SessionChanged(null);
        return (null, null);
    }

    /// <summary>
    /// <paramref name="kept"/> and its address when the session can go on: started for this very content (its size and MD5)
    /// and target, no more than six days ago, with an address that is Drive's own as Drive:ApiBaseUrl and Drive:ApiVersion now
    /// give it, so that a session begun under another version of the API starts again too. Otherwise nothing, and why is noted.
    /// </summary>
    private (UploadSession? Session, UploadAddress? Address) Usable(UploadSession? kept, UploadSource source, UploadTarget target, string apiBase)
    {
        if (kept is null) return (null, null);

        var why = kept.Size != source.Length || !string.Equals(kept.Md5, source.Md5, StringComparison.OrdinalIgnoreCase) ? "the file has changed since"
            : kept.RevisionOf != target.RevisionOf ? "it was for another file on Drive"
            : time.GetUtcNow() - kept.StartedUtc > SessionsKeptFor ? "it is more than six days old"
            : null;
        if (why is not null)
        {
            logger.LogDebug("The upload of {Name} begun earlier is started again: {Why}", target.Name, why);
            return (null, null);
        }

        if (UploadAddress.TryParse(kept.Address, apiBase, options.Value.ApiVersion, target.RevisionOf) is not { } address)
        {
            logger.LogWarning("The upload of {Name} begun earlier names an address that is not Google Drive's own at Drive:ApiBaseUrl and Drive:ApiVersion as they are now, so it is not used; the upload starts again", target.Name);
            return (null, null);
        }

        return (kept, address);
    }
}
