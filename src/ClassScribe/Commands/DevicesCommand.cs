using ClassScribe.Audio;

namespace ClassScribe.Commands;

/// <summary>
/// The devices command: the output devices Windows can play through, marked default or playing, and the apps with sound
/// open, each with the option of record and record-only that records it, then an example. It only asks Windows what there
/// is: no device is opened and nothing is recorded. ClassScribe's own processes are left out of the apps, since a
/// recording's own capture shows up as an app with sound. The devices are asked for only when the command runs.
/// </summary>
public sealed class DevicesCommand(Lazy<ILoopbackCaptureProvider> outputs, IConsoleUi ui)
{
    /// <summary>The name of ClassScribe's own processes, the window's and the recorder's.</summary>
    private const string OwnProcessName = "ClassScribe";

    /// <summary>What the line for --device auto says it records.</summary>
    private const string Automatic = "The device the class plays on";

    /// <summary>
    /// Lists the output devices, then the apps with sound, each with its option, lined up in one column, then says that
    /// nothing was opened, and gives an example. Returns the exit code, 0.
    /// </summary>
    public int Run()
    {
        var provider = outputs.Value;
        var devices = provider.GetOutputDevices().Select(device => (What: Describe(device), Option: $"--device \"{device.Name}\"")).ToList();
        var apps = AppsByName(provider.GetPlayingApps()).Select(app => (What: Describe(app), Option: $"--app {Quoted(app.Name)}")).ToList();
        var width = devices.Concat(apps).Select(row => row.What.Length).Append(Automatic.Length).Max();

        ui.WriteLine("Output devices:");
        if (devices.Count == 0) ui.WriteLine("  None is connected now.");
        foreach (var (what, option) in devices) ui.WriteLine($"  {what.PadRight(width)}  {option}");
        ui.WriteLine($"  {Automatic.PadRight(width)}  --device auto");

        ui.WriteLine("Apps with sound:");
        if (apps.Count == 0) ui.WriteLine("  None has sound open now.");
        foreach (var (what, option) in apps) ui.WriteLine($"  {what.PadRight(width)}  {option}");

        ui.WriteLine("Nothing was opened or recorded to list these.");
        ui.WriteLine("For example: ClassScribe record \"Kalam\" --device auto --output \"D:\\Classes\"");
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
    /// The apps with sound, one per name at its loudest, since a browser can have several processes with sound open, in
    /// the order of their names; ClassScribe's own processes, this one and any other ClassScribe, are left out.
    /// </summary>
    internal static IReadOnlyList<PlayingApp> AppsByName(IEnumerable<PlayingApp> apps) =>
    [
        .. apps
            .Where(app => app.ProcessId != Environment.ProcessId && !string.Equals(app.Name, OwnProcessName, StringComparison.OrdinalIgnoreCase))
            .GroupBy(app => app.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(app => app.Level).First())
            .OrderBy(app => app.Name, StringComparer.OrdinalIgnoreCase)
    ];

    /// <summary>A process name as an option takes it: in quotes when it holds a space.</summary>
    private static string Quoted(string name) => name.Contains(' ') ? $"\"{name}\"" : name;
}
