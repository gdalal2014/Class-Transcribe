using ClassScribe.Commands;
using ClassScribe.Settings;
using Microsoft.Extensions.Options;

namespace ClassScribe.Audio;

/// <summary>
/// What to record: one app's sound, one output device chosen by part of its name or, with neither, whichever device the
/// class is playing through. It follows Recording:App and Recording:Device, as they are read again before each choice,
/// until menu choice 6 picks something; that pick, automatic included, then holds for the rest of the session, whatever
/// the settings say. Choosing an app drops a chosen device and the other way round, so only one is ever set; when the
/// settings name both, the app wins. A run's request takes it as a source plan (<see cref="Current"/>).
/// </summary>
public sealed class SourceChoice(IOptionsMonitor<RecordingOptions> options)
{
    private readonly Lock _gate = new();

    /// <summary>What menu choice 6 picked for the rest of the session, or null while the settings decide.</summary>
    private ClassSource? _picked;

    /// <summary>
    /// What is chosen now, as a run's source plan: the class alone, without a microphone, from the app or the device the
    /// choice names, or from the device the class plays on when it names neither.
    /// </summary>
    public SourcePlan Current
    {
        get
        {
            lock (_gate) return new SourcePlan(RecordingMode.Class, Class, Microphone: null);
        }
    }

    /// <summary>
    /// Part of the chosen device's name, matched in any case, or null when no device is chosen. Setting it picks that
    /// device; setting null drops a chosen device for the device the class plays on, and keeps a chosen app. Kept beside
    /// <see cref="Current"/> for the menu, which picks a device or an app by name.
    /// </summary>
    public string? Device
    {
        get
        {
            lock (_gate) return (Class as ClassSource.Device)?.NamePart;
        }
        set
        {
            lock (_gate)
            {
                var device = Normalise(value);
                _picked = device is not null ? new ClassSource.Device(device)
                    : Class is ClassSource.Device ? new ClassSource.Automatic()
                    : Class;
            }
        }
    }

    /// <summary>
    /// The chosen app's process name, such as chrome, matched in any case, or null when no app is chosen. Setting it picks
    /// that app; setting null drops a chosen app for the device the class plays on, and keeps a chosen device.
    /// </summary>
    public string? App
    {
        get
        {
            lock (_gate) return (Class as ClassSource.App)?.ProcessName;
        }
        set
        {
            lock (_gate)
            {
                var app = Normalise(value);
                _picked = app is not null ? new ClassSource.App(app)
                    : Class is ClassSource.App ? new ClassSource.Automatic()
                    : Class;
            }
        }
    }

    /// <summary>What is recorded, in words, for the menu.</summary>
    public string Description
    {
        get
        {
            ClassSource current;
            lock (_gate) current = Class;

            return current switch
            {
                ClassSource.App app => $"the app \"{app.ProcessName}\"",
                ClassSource.Device device => $"the output device \"{device.NamePart}\"",
                _ => "the device the class plays on"
            };
        }
    }

    /// <summary>Where the class is recorded from now: the pick from menu choice 6, or else the settings as they are now. Call with the lock held.</summary>
    private ClassSource Class => _picked ?? FromSettings(options.CurrentValue);

    /// <summary>What the settings ask to record. When both name something, the app wins; when neither does, the device the class plays on.</summary>
    private static ClassSource FromSettings(RecordingOptions settings) =>
        Normalise(settings.App) is { } app ? new ClassSource.App(app)
        : Normalise(settings.Device) is { } device ? new ClassSource.Device(device)
        : new ClassSource.Automatic();

    /// <summary>Trims a name; a blank one means nothing is chosen.</summary>
    private static string? Normalise(string? name) => string.IsNullOrWhiteSpace(name) ? null : name.Trim();
}
