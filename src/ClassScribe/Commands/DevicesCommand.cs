using ClassScribe.Audio;
using ClassScribe.Audio.Microphones;
using ClassScribe.Desktop;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Commands;

/// <summary>
/// The devices command: the output devices Windows can play through, marked default or playing, and the apps with sound
/// open, each with the option of record and record-only that records it; then the microphones, each with what it is
/// (built in, default, USB, raw), which apps hold it now from Windows' own lists, and the option that picks it; then
/// examples. It only asks Windows what there is: no device or microphone is opened and nothing is recorded. ClassScribe's
/// own processes are left out of the apps, since a recording's own capture shows up as an app with sound and holds the
/// microphone. The devices, the microphones and the sessions are asked for only when the command runs. A list Windows
/// will not give says so in its place, with the reason, and the other lists are given in full.
/// </summary>
/// <param name="outputs">Lists the output devices and the apps with sound, made when the command runs.</param>
/// <param name="microphones">Lists the microphones, made when the command runs.</param>
/// <param name="sessions">Reads which apps have each microphone open, made when the command runs.</param>
/// <param name="ui">The window the lists are written to.</param>
/// <param name="logger">The log, which has a list that could not be read at Debug, with where the failure came from.</param>
public sealed class DevicesCommand(Lazy<ILoopbackCaptureProvider> outputs, Lazy<IMicrophoneProvider> microphones, Lazy<IMicrophoneSessions> sessions, IConsoleUi ui,
    ILogger<DevicesCommand> logger)
{
    /// <summary>What the line for --device auto says it records.</summary>
    private const string Automatic = "The device the class plays on";

    /// <summary>What the line for --mic without a name says it records.</summary>
    private const string DefaultMicrophone = "Windows' default microphone, never a Bluetooth one";

    /// <summary>
    /// Lists the output devices, then the apps with sound, then the microphones, each with its option, lined up in one
    /// column, then says that nothing was opened, and gives examples. A list Windows will not give says so, with the
    /// reason, in its place; when only the apps that hold each microphone cannot be read, the microphones are listed with
    /// their own notes and a line says what is missing. What a list could read only in part is said below what it is
    /// about, with whose it was and which call failed: an app's audio session below the apps, a microphone's sessions,
    /// mute or name below that microphone. When anything could not be read, a line before the end says so, so that one
    /// look at the end of the list tells. Returns the exit code, 0: the command has listed what it could.
    /// </summary>
    public int Run()
    {
        var (deviceList, devicesFailed) = Read("the output devices", () => outputs.Value.GetOutputDevices());
        var (appList, appsFailed) = Read("the apps with sound", () => outputs.Value.GetPlayingAppsListing());
        var (sessionList, sessionsFailed) = Read("which apps hold each microphone", () => sessions.Value.ReadAll());
        var (microphoneList, microphonesFailed) = Read("the microphones", () => microphones.Value.GetMicrophones());
        var listings = sessionList?.Listings ?? [];
        var holdersMissing = sessionsFailed ?? sessionList?.Problem;

        var devices = (deviceList ?? []).Select(device => (What: Describe(device), Option: $"--device \"{device.Name}\"")).ToList();
        var apps = AppsByName(appList?.Apps ?? []).Select(app => (What: Describe(app), Option: $"--app {Quoted(app.Name)}")).ToList();
        var unreadApps = appList?.Unread ?? [];
        var mics = (microphoneList ?? []).Select(microphone =>
        {
            var listing = listings.FirstOrDefault(candidate => candidate.EndpointId == microphone.Id);
            return (What: Describe(microphone, listing), Option: MicOption(microphone), Problem: listing?.Problem);
        }).ToList();
        var width = devices.Select(row => row.What).Concat(apps.Select(row => row.What)).Concat(mics.Select(row => row.What))
            .Append(Automatic).Append(DefaultMicrophone).Max(what => what.Length);

        ui.WriteLine("Output devices:");
        if (devicesFailed is not null) ui.WriteLine(Unread(devicesFailed));
        else if (devices.Count == 0) ui.WriteLine("  None is connected now.");
        foreach (var (what, option) in devices) ui.WriteLine(Row(what, option, width));
        ui.WriteLine(Row(Automatic, "--device auto", width));

        ui.WriteLine("Apps with sound:");
        if (appsFailed is not null) ui.WriteLine(Unread(appsFailed));
        else if (apps.Count == 0 && unreadApps.Count == 0) ui.WriteLine("  None has sound open now.");
        foreach (var (what, option) in apps) ui.WriteLine(Row(what, option, width));
        foreach (var session in unreadApps) ui.WriteLine(Unread(session.Describe()));

        ui.WriteLine("Microphones:");
        if (microphonesFailed is not null) ui.WriteLine(Unread(microphonesFailed));
        else if (mics.Count == 0) ui.WriteLine("  None is connected now.");
        foreach (var (what, option, problem) in mics)
        {
            ui.WriteLine(Row(what, option, width));
            if (problem is not null) ui.WriteLine("  " + Unread(problem));
        }

        if (mics.Count > 0 && holdersMissing is not null) ui.WriteLine($"  Which apps hold each microphone could not be read now: {holdersMissing}");
        ui.WriteLine(Row(DefaultMicrophone, "--mic", width));

        var anythingUnread = devicesFailed is not null || appsFailed is not null || unreadApps.Count > 0 || microphonesFailed is not null ||
            (mics.Count > 0 && holdersMissing is not null) || mics.Any(row => row.Problem is not null);
        if (anythingUnread) ui.WriteLine("Not everything could be read from Windows just now: the lines above say what, and why.");
        ui.WriteLine("Nothing was opened or recorded to list these.");
        ui.WriteLine("For example: ClassScribe record \"Kalam\" --device auto --output \"D:\\Classes\"");
        ui.WriteLine("             ClassScribe record \"Kalam\" --app chrome --mic --output \"D:\\Classes\"");
        ui.WriteLine("             ClassScribe record \"Kalam\" --in-person --output \"D:\\Classes\"");
        return 0;
    }

    /// <summary>
    /// One of Windows' lists, read by <paramref name="read"/>: what it gave and no failure, or, when Windows would not give
    /// it, nothing and the failure in words, which is noted at Debug with where it came from. <paramref name="what"/> names
    /// the list in the log.
    /// </summary>
    private (T? List, string? Failure) Read<T>(string what, Func<T> read) where T : class
    {
        try
        {
            return (read(), null);
        }
        catch (Exception ex) when (AudioFailures.IsFailedResult(ex) || ex is InvalidOperationException)
        {
            // Deliberately not rethrown: the command only lists what there is, and one list Windows will not give, as with
            // the Zoom app in a meeting on 18 October, must not keep the others from being listed. The list says that it
            // could not be read, and why; the failure, with where it came from, is in the log at Debug.
            logger.LogDebug(ex, "Could not read {What} for the list (0x{Code:X8})", what, ex.HResult);
            return (null, AudioFailures.Describe(ex));
        }
    }

    /// <summary>The line that takes the place of a list Windows would not give, with <paramref name="failure"/>, the reason in words.</summary>
    private static string Unread(string failure) => $"  Could not be read now: {failure}";

    /// <summary>A device as the lists show it: its name, and whether it is the default one or playing now.</summary>
    internal static string Describe(OutputDevice device)
    {
        var notes = string.Join(", ", new[] { device.IsDefault ? "default" : null, device.Level >= CaptureChooser.SoundLevel ? "playing" : null }.OfType<string>());
        return notes.Length > 0 ? $"{device.Name}  ({notes})" : device.Name;
    }

    /// <summary>An app as the lists show it: its name, the device it plays through, and whether it is playing now.</summary>
    internal static string Describe(PlayingApp app) =>
        app.Level >= CaptureChooser.SoundLevel ? $"{app.Name}  (app on {app.DeviceName}, playing)" : $"{app.Name}  (app on {app.DeviceName})";

    /// <summary>
    /// A microphone as the list shows it: its name, then its notes: built in for the laptop's own array, default for
    /// Windows' default microphone, USB for one on USB, raw when it offers audio without Windows' sound processing, for a
    /// Bluetooth one that it is never chosen unless named, since using it switches earbuds to call quality, and for an
    /// input that records what the computer plays that it is never recorded; then, from <paramref name="listing"/> when
    /// Windows' list of sessions has it, whether Windows mutes it and which apps hold it now, the ones taking sound as
    /// listening. Windows' own sounds and ClassScribe's own processes are left out.
    /// </summary>
    internal static string Describe(Microphone microphone, MicrophoneSessionListing? listing)
    {
        var notes = string.Join(", ", new[]
        {
            microphone.IsBuiltInArray ? "built in" : null,
            microphone.IsDefault ? "default" : null,
            microphone.Enumerator.Equals("USB", StringComparison.OrdinalIgnoreCase) ? "USB" : null,
            microphone.RawSupported ? "raw" : null,
            microphone.IsBluetooth ? "Bluetooth: never chosen unless named; call-quality sound" : null,
            microphone.IsLoopbackInput ? "records what the computer plays; never recorded" : null
        }.OfType<string>());

        var clauses = new List<string>();
        if (notes.Length > 0) clauses.Add(notes);
        if (listing is { MutedInWindows: true }) clauses.Add("muted in Windows");
        if (listing is not null) clauses.AddRange(HeldBy(listing));
        return clauses.Count > 0 ? $"{microphone.Name}  ({string.Join("; ", clauses)})" : microphone.Name;
    }

    /// <summary>
    /// The apps with sound, one per name at its loudest, since a browser can have several processes with sound open, in
    /// the order of their names; ClassScribe's own processes, this one and any other ClassScribe, are left out.
    /// </summary>
    internal static IReadOnlyList<PlayingApp> AppsByName(IEnumerable<PlayingApp> apps) =>
    [
        .. apps
            .Where(app => !IsOwn(app.ProcessId, app.Name))
            .GroupBy(app => app.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(app => app.Level).First())
            .OrderBy(app => app.Name, StringComparer.OrdinalIgnoreCase)
    ];

    /// <summary>
    /// The apps that hold the microphone <paramref name="listing"/> describes, one clause per app in the order of their
    /// names: "chrome is listening" for an app taking sound from it, "vibe holds it without listening" for one that has
    /// it open and takes none. Sessions that have expired, Windows' own sounds, sessions of no process and ClassScribe's
    /// own are left out; a process that could not be named is named by its number.
    /// </summary>
    private static IEnumerable<string> HeldBy(MicrophoneSessionListing listing) =>
        listing.Sessions
            .Where(session => !session.SystemSounds && session.ProcessId > 0 && session.State != CaptureSessionState.Expired && !IsOwn(session.ProcessId, session.ImageName))
            .GroupBy(session => session.ImageName ?? $"process {session.ProcessId}", StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Any(session => session.State == CaptureSessionState.Active) ? $"{group.Key} is listening" : $"{group.Key} holds it without listening");

    /// <summary>
    /// True for ClassScribe's own processes: this one, and any process of ClassScribe's program, by the name
    /// <see cref="OwnProcesses"/> knows it by. Static because <see cref="MenuCommand"/> calls <see cref="AppsByName"/>
    /// without a command; the registered <see cref="IOwnProcesses"/> answers the same question for the recorder.
    /// </summary>
    private static bool IsOwn(int processId, string? processName) =>
        processId == Environment.ProcessId || string.Equals(processName, OwnProcesses.ProgramName, StringComparison.OrdinalIgnoreCase);

    /// <summary>The option that picks <paramref name="microphone"/>: its whole name with --mic (--in-person takes it the same way); none for an input that is never recorded.</summary>
    private static string? MicOption(Microphone microphone) => microphone.IsLoopbackInput ? null : $"--mic \"{microphone.Name}\"";

    /// <summary>One line of a list: what it is, padded to <paramref name="width"/>, then its option, when it has one.</summary>
    private static string Row(string what, string? option, int width) => option is null ? $"  {what}" : $"  {what.PadRight(width)}  {option}";

    /// <summary>A process name as an option takes it: in quotes when it holds a space.</summary>
    private static string Quoted(string name) => name.Contains(' ') ? $"\"{name}\"" : name;
}
