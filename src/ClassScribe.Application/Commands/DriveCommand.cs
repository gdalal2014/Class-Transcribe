using System.Globalization;
using ClassScribe.Drive;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClassScribe.Commands;

/// <summary>
/// The drive command, from the command line (drive signin, drive signout, drive status) and from menu choice 9: signs in to
/// Google Drive, signs out, and shows the sign-in's state with what waits to go up. Each says what happened in plain words,
/// once, and gives the exit code: 0 done; 1 failed, or not signed in; a cancel (Esc, Ctrl+C) is let through, for App to turn
/// into 130. Nothing records here, so a first Ctrl+C cancels. One per run. The uploads are <see cref="DriveUploadCommand"/>'s.
/// </summary>
public sealed class DriveCommand(GoogleSignIn signIn, IGoogleCredentialStore store, IGoogleDrive drive, DriveQueueReport queue, IUploadManifests manifests,
    IOptionsSnapshot<DriveOptions> options, IUserSignals signals, IConsoleUi ui, TimeProvider time, ILogger<DriveCommand> logger)
{
    /// <summary>How the time of a last try is written, in local time: 17 Oct 21:40.</summary>
    private const string TryFormat = "d MMM HH:mm";

    /// <summary>What the status says before the first sign-in.</summary>
    public const string NotSetUpLine =
        "Google Drive: not set up. ClassScribe has no Google client yet; the README's \"Google Drive (set up once)\" says how to make one, then: ClassScribe drive signin --client \"<the client file>\"";

    /// <summary>How the date of a sign-in is written, in local time: 17 Oct 2026.</summary>
    private const string DateFormat = "d MMM yyyy";

    /// <summary>
    /// Signs in, with the client in <paramref name="clientFile"/> or the one kept. Returns 0 once signed in, 1 when the sign-in
    /// did not go through, which is said.
    /// </summary>
    public async Task<int> SignInAsync(string? clientFile)
    {
        signals.RequestStop();
        try
        {
            await signIn.SignInAsync(clientFile, signals.Cancellation);
            return 0;
        }
        catch (GoogleDriveException ex)
        {
            // Deliberately not rethrown: a sign-in that did not go through is this command's answer, said once in words that
            // say what to do, with exit code 1; the details are in the Debug lines of the parts below.
            logger.LogError("{Problem}", ex.Message);
            return 1;
        }
    }

    /// <summary>Signs out. Returns 0, or 1 when Google could not be asked to end the access, which is said with how to end it by hand.</summary>
    public async Task<int> SignOutAsync()
    {
        signals.RequestStop();
        return await signIn.SignOutAsync(signals.Cancellation) == SignOutOutcome.NotRevoked ? 1 : 0;
    }

    /// <summary>
    /// Shows the sign-in's state: the account, since when, the end of the client id; then, from Drive, the storage used and
    /// ClassScribe's folder, which it never makes. Returns 0 when signed in and Drive answers, 1 otherwise, with why.
    /// </summary>
    public async Task<int> StatusAsync()
    {
        signals.RequestStop();
        var stored = store.Read();
        if (stored.Problem is { } problem)
        {
            ui.WriteLine("Google Drive: not signed in.");
            logger.LogError("{Problem}", problem);
            return 1;
        }

        if (stored.Credentials is not { } credentials)
        {
            ui.WriteLine(NotSetUpLine);
            return 1;
        }

        if (!credentials.IsSignedIn)
        {
            ui.WriteLine($"Google Drive: not signed in (the client {credentials.Client.ShortId} is kept). Sign in with menu choice 9 > S, or: ClassScribe drive signin");
            return 1;
        }

        ui.WriteLine($"Google Drive: signed in as {credentials.Email}{Since(credentials.SignedInUtc)} (client {credentials.Client.ShortId})");
        try
        {
            var about = await drive.GetAccountAsync(signals.Cancellation);
            ui.WriteLine(StorageLine(about.Storage));
            var root = await drive.FindRootAsync(signals.Cancellation);
            ui.WriteLine(root is null
                ? $"The {options.Value.FolderName.Trim()} folder is not in My Drive now: it is in the trash, or was deleted. Signing in again makes a new one; the trash is left as it is."
                : $"Uploads go to My Drive > {root.Name}: {root.Link}");
            foreach (var line in QueueLines()) ui.WriteLine(line);
            return 0;
        }
        catch (GoogleDriveException ex)
        {
            // Deliberately not rethrown: what kept Drive from answering is this command's answer, said once in words that say
            // what to do, with exit code 1.
            logger.LogError("{Problem}", ex.Message);
            return 1;
        }
    }

    /// <summary>
    /// What waits to go up, a line each: "Waiting: Mantiq_2026-10-17_14-04-42Z (4 files, 722 MB), not tried yet", or with
    /// the last try in local time and what went wrong then; or "Nothing waits to go up." A class being recorded or
    /// transcribed now is not listed (see <see cref="DriveQueueReport.Queued"/>). The size is that of its files on this
    /// laptop, read from the manifests alone.
    /// </summary>
    private IEnumerable<string> QueueLines()
    {
        var waiting = queue.Queued();
        if (waiting.Count == 0)
        {
            yield return "Nothing waits to go up.";
            yield break;
        }

        foreach (var manifest in waiting)
        {
            var (count, bytes) = manifests.OnDisk(manifest.Recording, options.Value.LessonVideos);
            var tried = manifest.LastTriedUtc is { } last
                ? $"last try {TimeZoneInfo.ConvertTime(last, time.LocalTimeZone).ToString(TryFormat, CultureInfo.InvariantCulture)}{(manifest.LastError is { } error ? $": {error}" : "")}"
                : "not tried yet";
            yield return $"Waiting: {manifest.BaseName} ({(count == 1 ? "1 file" : $"{count} files")}, {Sizes.Describe(bytes)}), {tried}";
        }
    }

    /// <summary>" since 17 Oct 2026", the local date of <paramref name="signedInUtc"/>; empty when it is not known.</summary>
    private string Since(DateTimeOffset? signedInUtc) =>
        signedInUtc is { } utc ? $" since {TimeZoneInfo.ConvertTime(utc, time.LocalTimeZone).ToString(DateFormat, CultureInfo.InvariantCulture)}" : "";

    /// <summary>The storage line: "Storage: 1.2 TB of 30 TB used", or for an account without a limit, "Storage: 1.2 TB used; the account has no limit".</summary>
    private static string StorageLine(DriveSpace? storage)
    {
        var usage = Sizes.Describe(storage?.Usage ?? 0);
        return storage?.Limit is { } limit ? $"Storage: {usage} of {Sizes.Describe(limit)} used" : $"Storage: {usage} used; the account has no limit";
    }
}
