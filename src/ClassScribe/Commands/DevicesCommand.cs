using ClassScribe.Audio;
using ClassScribe.Audio.Microphones;
using ClassScribe.Desktop;

namespace ClassScribe.Commands;

/// <summary>
/// The devices command: the output devices Windows can play through, marked default or playing, and the apps with sound
/// open, each with the option of record and record-only that records it; then the microphones, each with what it is
/// (built in, default, USB, raw), which apps hold it now from Windows' own lists, and the option that picks it; then
/// examples. It only asks Windows what there is: no device or microphone is opened and nothing is recorded. ClassScribe's
/// own processes are left out of the apps, since a recording's own capture shows up as an app with sound and holds the
/// microphone. The devices, the microphones and the sessions are asked for only when the command runs.
/// </summary>
/// <param name="outputs">Lists the output devices and the apps with sound, made when the command runs.</param>
/// <param name="microphones">Lists the microphones, made when the command runs.</param>
/// <param name="sessions">Reads which apps have each microphone open, made when the command runs.</param>
/// <param name="ui">The window the lists are written to.</param>
public sealed class DevicesCommand(Lazy<ILoopbackCaptureProvider> outputs, Lazy<IMicrophoneProvider> microphones, Lazy<IMicrophoneSessions> sessions, IConsoleUi ui)
{
    /// <summary>What the line for --device auto says it records.</summary>
    private const string Automatic = "The device the class plays on";

    /// <summary>What the line for --mic without a name says it records.</summary>
    private const string DefaultMicrophone = "Windows' default microphone, never a Bluetooth one";

    /// <summary>
    /// Lists the output devices, then the apps with sound, then the microphones, each with its option, lined up in one
    /// column, then says that nothing was opened, and gives examples. Returns the exit code, 0.
    /// </summary>
    public int Run()
    {
        var provider = outputs.Value;
        var devices = provider.GetOutputDevices().Select(device => (What: Describe(device), Option: $"--device \"{device.Name}\"")).ToList();
        var apps = AppsByName(provider.GetPlayingApps()).Select(app => (What: Describe(app), Option: $"--app {Quoted(app.Name)}")).ToList();
        var listings = sessions.Value.ReadAll().Listings;
        var mics = microphones.Value.GetMicrophones()
            .Select(microphone => (What: Describe(microphone, listings.FirstOrDefault(listing => listing.EndpointId == microphone.Id)), Option: MicOption(microphone)))
            .ToList();
        var width = devices.Select(row => row.What).Concat(apps.Select(row => row.What)).Concat(mics.Select(row => row.What))
            .Append(Automatic).Append(DefaultMicrophone).Max(what => what.Length);

        ui.WriteLine("Output devices:");
        if (devices.Count == 0) ui.WriteLine("  None is connected now.");
        foreach (var (what, option) in devices) ui.WriteLine(Row(what, option, width));
        ui.WriteLine(Row(Automatic, "--device auto", width));

        ui.WriteLine("Apps with sound:");
        if (apps.Count == 0) ui.WriteLine("  None has sound open now.");
        foreach (var (what, option) in apps) ui.WriteLine(Row(what, option, width));

        ui.WriteLine("Microphones:");
        if (mics.Count == 0) ui.WriteLine("  None is connected now.");
        foreach (var (what, option) in mics) ui.WriteLine(Row(what, option, width));
        ui.WriteLine(Row(DefaultMicrophone, "--mic", width));

        ui.WriteLine("Nothing was opened or recorded to list these.");
        ui.WriteLine("For example: ClassScribe record \"Kalam\" --device auto --output \"D:\\Classes\"");
        ui.WriteLine("             ClassScribe record \"Kalam\" --app chrome --mic --output \"D:\\Classes\"");
        ui.WriteLine("             ClassScribe record \"Kalam\" --in-person --output \"D:\\Classes\"");
        return 0;
    }

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
