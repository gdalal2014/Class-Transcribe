using System.Globalization;
using ClassScribe.Audio;
using ClassScribe.Drive;
using ClassScribe.Storage;

namespace ClassScribe.Commands;

/// <summary>
/// Menu choice 9, run through <see cref="ICommandRunner.DriveMenuAsync"/>: Google Drive. It shows the sign-in's state from
/// the store alone, asking Google nothing, and offers S to sign in, which asks for the client file Google gave (Enter alone
/// keeps the client already kept). While signed in it also offers U to upload what waits in the queue, R to upload one of
/// the recent recordings (listed with where each stands with Drive), F to upload a folder or a recording's file dragged in
/// or typed, L to free room on this laptop by deleting a recording's audio once Drive has it, checked (asked with Y first),
/// I to check with Google (the account, the storage and ClassScribe's folder) and O to sign out. Each goes to the runner,
/// which runs the drive command in a scope of its own. Esc, Q or Ctrl+C goes back to the menu.
/// </summary>
public sealed class DriveMenu(ICommandRunner commands, IGoogleCredentialStore store, IClassFolders folders, UploadManifests manifests, DriveQueueReport queue, IConsoleUi ui, TimeProvider time)
{
    /// <summary>How the date of a sign-in is written, in local time: 17 Oct 2026.</summary>
    private const string DateFormat = "d MMM yyyy";

    /// <summary>How many recent recordings R and L offer, so that one digit picks one.</summary>
    private const int RecentRecordings = 9;

    /// <summary>Shows the state and the keys, and runs the one pressed. Returns its exit code, or 0 when nothing ran.</summary>
    public async Task<int> RunAsync()
    {
        var stored = store.Read();
        var signedIn = stored.Credentials is { IsSignedIn: true };
        ui.WriteLine(Header(stored));
        if (signedIn)
        {
            ui.WriteLine($"  U  Upload what waits to go up ({Waiting()})");
            ui.WriteLine("  R  Upload a recent recording");
            ui.WriteLine("  F  Upload a folder or a recording's file (drag it into this window, or type its path)");
            ui.WriteLine("  L  Free room on this laptop: delete a recording's audio once Google Drive has it, checked");
            ui.WriteLine("  I  Check with Google: the account, the storage used and ClassScribe's folder on Drive");
            ui.WriteLine("  S  Sign in again");
            ui.WriteLine("  O  Sign out");
        }
        else
        {
            ui.WriteLine(stored.IsSetUp ? "  S  Sign in" : "  S  Sign in with the client file you downloaded from Google");
        }

        ui.WriteLine("Press a key, or Esc to go back.");
        while (true)
        {
            var key = await ui.ReadKeyAsync(CancellationToken.None);
            if (key is not { } pressed || IsBack(pressed)) return 0;

            switch (pressed.Key)
            {
                case ConsoleKey.S:
                    return await SignInAsync(stored.Credentials);
                case ConsoleKey.O when signedIn:
                    return await commands.DriveSignOutAsync();
                case ConsoleKey.I when signedIn:
                    return await commands.DriveStatusAsync();
                case ConsoleKey.U when signedIn:
                    return await commands.DriveUploadAsync(null);
                case ConsoleKey.R when signedIn:
                    return await UploadRecentAsync();
                case ConsoleKey.F when signedIn:
                    return await UploadPathAsync();
                case ConsoleKey.L when signedIn:
                    return await FreeSpaceAsync();
            }
        }
    }

    /// <summary>
    /// How many recordings wait to go up, as U's line says it: "1 recording, 722 MB", "nothing waits now"; a class being
    /// recorded or transcribed now is not counted (see <see cref="DriveQueueReport.Queued"/>). The size is that of their files
    /// on this laptop.
    /// </summary>
    private string Waiting()
    {
        var waiting = queue.Queued();
        if (waiting.Count == 0) return "nothing waits now";

        var bytes = waiting.Sum(manifest => queue.OnDisk(manifest.Recording).Bytes);
        return $"{(waiting.Count == 1 ? "1 recording" : $"{waiting.Count} recordings")}, {Sizes.Describe(bytes)}";
    }

    /// <summary>R: lists the recent recordings with where each stands with Drive, and uploads the one picked.</summary>
    private async Task<int> UploadRecentAsync()
    {
        if (await PickRecentAsync("Press a recording's number to upload it, or Esc to go back.") is not { } picked) return 0;

        return await commands.DriveUploadAsync(FileOf(picked.Files));
    }

    /// <summary>F: asks for a folder or a recording's file, dragged into the window or typed, and uploads what it names.</summary>
    private async Task<int> UploadPathAsync()
    {
        ui.WriteLine("Drag a recording's file, its folder, a course's folder or the recordings folder into this window, or type its path, then press Enter. Esc, or Enter alone, goes back.");
        ui.Write("> ");

        // Windows puts quotes around a dragged path that contains spaces.
        var path = (await ui.ReadLineAsync(CancellationToken.None))?.Trim().Trim('"').Trim();
        if (string.IsNullOrEmpty(path)) return 0;
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            ui.WriteLine($"Nothing found at {path}");
            return 0;
        }

        return await commands.DriveUploadAsync(Path.GetFullPath(path));
    }

    /// <summary>
    /// L: lists the recent recordings with where each stands with Drive; for the one picked, once it is on Drive, says which
    /// audio files would be deleted from this laptop, how much room that frees and what stays, and frees it on Y. One not on
    /// Drive yet, or whose audio is gone from the laptop already, is said, and nothing is deleted.
    /// </summary>
    private async Task<int> FreeSpaceAsync()
    {
        if (await PickRecentAsync("Press a recording's number to free the room its audio takes on this laptop, or Esc to go back.") is not { } picked) return 0;

        var files = picked.Files;
        var audio = files.FindSegments().Concat(files.FindSegments(Track.Microphone)).ToList();
        var manifest = manifests.Read(files);
        if (audio.Count == 0)
        {
            ui.WriteLine($"{files.BaseName} has no audio on this laptop to delete{(manifest is null ? "" : $"; its audio is on Google Drive, in {manifest.DriveLocation}")}.");
            return 0;
        }

        if (manifest is not { State: UploadState.Done })
        {
            ui.WriteLine($"{files.BaseName} is not all on Google Drive yet, so its audio stays here. Upload it first: R, then its number.");
            return 0;
        }

        var bytes = audio.Sum(path => new FileInfo(path).Length);
        ui.WriteLine($"This deletes from this laptop the audio of {files.BaseName}: {string.Join(", ", audio.Select(Path.GetFileName))} ({Sizes.Describe(bytes)}).");
        ui.WriteLine($"Each file is first checked again against its copy on Google Drive, in {manifest.DriveLocation}, which stays as it is. The transcripts, ElevenLabs' answers, the marks and the log stay here.");
        ui.WriteLine("Press Y to delete the audio from this laptop, or any other key to go back.");
        if ((await ui.ReadKeyAsync(CancellationToken.None))?.Key != ConsoleKey.Y) return 0;

        return await commands.DriveFreeSpaceAsync(audio[0]);
    }

    /// <summary>
    /// Lists the recent recordings, newest first, each with its length, its notes and where it stands with Drive, then
    /// <paramref name="prompt"/>, and returns the one picked; null for Esc, or when there are none, which is said.
    /// </summary>
    private async Task<RecordedClass?> PickRecentAsync(string prompt)
    {
        var recordings = folders.ListRecent(RecentRecordings);
        if (recordings.Count == 0)
        {
            ui.WriteLine($"No recordings to pick from in {folders.Root}.");
            return null;
        }

        ui.WriteLine("Recent recordings, newest first:");
        for (var i = 0; i < recordings.Count; i++)
        {
            var recorded = recordings[i];
            var notes = string.Concat(ListCommand.NotesOf(recorded).Select(note => $"  {note}"));
            ui.WriteLine($"  {i + 1}  {recorded.Files.BaseName}  {Durations.Format(recorded.Length)}{notes}  {DriveState(recorded)}");
        }

        ui.WriteLine(prompt);
        while (true)
        {
            var key = await ui.ReadKeyAsync(CancellationToken.None);
            if (key is not { } pressed || IsBack(pressed)) return null;
            if (pressed.KeyChar is >= '1' and <= '9' && pressed.KeyChar - '1' < recordings.Count) return recordings[pressed.KeyChar - '1'];
        }
    }

    /// <summary>Where a recording stands with Drive, as R and L show it: RECORDING NOW, on Drive, waiting, or not uploaded.</summary>
    private string DriveState(RecordedClass recorded) =>
        recorded.BeingWritten ? "RECORDING NOW"
        : manifests.Read(recorded.Files) switch
        {
            { State: UploadState.Done } => "on Drive",
            { State: UploadState.Waiting } => "waiting",
            _ => "not uploaded"
        };

    /// <summary>A file of the recording that is on this laptop, to name it by: its first audio file, or else any of its files.</summary>
    private static string FileOf(RecordingFiles files) =>
        files.FindSegments().Concat(files.FindSegments(Track.Microphone)).FirstOrDefault()
        ?? Directory.EnumerateFiles(files.Folder, $"{files.BaseName}*").FirstOrDefault(path => RecordingFiles.TryParse(path)?.BaseName == files.BaseName)
        ?? files.Segment(1);

    /// <summary>
    /// Asks for the client file, dragged into the window or typed, and signs in with it; Enter alone signs in with the client
    /// <paramref name="kept"/>, when there is one, and otherwise goes back, as Esc does.
    /// </summary>
    private async Task<int> SignInAsync(GoogleCredentials? kept)
    {
        ui.WriteLine(kept is null
            ? "Drag the client file you downloaded from Google (client_secret_....json, in your Downloads folder) into this window, or type its path, then press Enter. Esc goes back."
            : $"Drag a client file you downloaded from Google into this window and press Enter, or press Enter alone to sign in with the client ClassScribe keeps ({kept.Client.ShortId}). Esc goes back.");
        ui.Write("> ");

        // Windows puts quotes around a dragged path that contains spaces.
        var path = (await ui.ReadLineAsync(CancellationToken.None))?.Trim().Trim('"').Trim();
        if (path is null) return 0;
        if (path.Length == 0) return kept is null ? 0 : await commands.DriveSignInAsync(null);

        return await commands.DriveSignInAsync(path);
    }

    /// <summary>The first line: the sign-in's state, as the store has it.</summary>
    private string Header(StoredCredentials stored) => stored switch
    {
        { Problem: { } problem } => $"Google Drive: not signed in. {problem}",
        { Credentials: null } => "Google Drive: not set up. Sign in once with the client file you made in your Google Cloud project; the README's \"Google Drive (set up once)\" has the steps.",
        { Credentials: { IsSignedIn: false } credentials } => $"Google Drive: not signed in (the client {credentials.Client.ShortId} is kept).",
        { Credentials: { } credentials } => $"Google Drive: signed in as {credentials.Email}{Since(credentials.SignedInUtc)}."
    };

    /// <summary>" since 17 Oct 2026", the local date of <paramref name="signedInUtc"/>; empty when it is not known.</summary>
    private string Since(DateTimeOffset? signedInUtc) =>
        signedInUtc is { } utc ? $" since {TimeZoneInfo.ConvertTime(utc, time.LocalTimeZone).ToString(DateFormat, CultureInfo.InvariantCulture)}" : "";

    /// <summary>True for the keys that go back to the menu: Esc, Q and Ctrl+C.</summary>
    private static bool IsBack(ConsoleKeyInfo key) =>
        key.Key is ConsoleKey.Escape or ConsoleKey.Q || (key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control));
}
