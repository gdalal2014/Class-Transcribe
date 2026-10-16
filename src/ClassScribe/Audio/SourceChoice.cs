using ClassScribe.Settings;
using Microsoft.Extensions.Options;

namespace ClassScribe.Audio;

/// <summary>
/// What to record: one app's sound, one output device chosen by part of its name or, with neither, whichever device the
/// class is playing through. It follows Recording:App and Recording:Device, as they are read again before each choice,
/// until menu choice 6 picks something; that pick, automatic included, then holds for the rest of the session, whatever
/// the settings say. Choosing an app drops a chosen device and the other way round, so only one is ever set; when the
/// settings name both, the app wins.
/// </summary>
public sealed class SourceChoice(IOptionsMonitor<RecordingOptions> options)
{
    private readonly Lock _gate = new();

    /// <summary>What menu choice 6 picked for the rest of the session, or null while the settings decide.</summary>
    private Source? _picked;

    /// <summary>Part of the chosen device's name, matched in any case, or null when no device is chosen.</summary>
    public string? Device
    {
        get
        {
            lock (_gate) return Current.Device;
        }
        set
        {
            lock (_gate)
            {
                var device = Normalise(value);
                _picked = device is not null ? new Source(device, App: null) : Current with { Device = null };
            }
        }
    }

    /// <summary>The chosen app's process name, such as chrome, matched in any case, or null when no app is chosen.</summary>
    public string? App
    {
        get
        {
            lock (_gate) return Current.App;
        }
        set
        {
            lock (_gate)
            {
                var app = Normalise(value);
                _picked = app is not null ? new Source(Device: null, app) : Current with { App = null };
            }
        }
    }

    /// <summary>What is recorded, in words, for the menu.</summary>
    public string Description
    {
        get
        {
            Source current;
            lock (_gate) current = Current;

            return current.App is { } app ? $"the app \"{app}\""
                : current.Device is { } device ? $"the output device \"{device}\""
                : "the device the class plays on";
        }
    }

    /// <summary>What is recorded now: the pick from menu choice 6, or else the settings as they are now. Call with the lock held.</summary>
    private Source Current => _picked ?? FromSettings(options.CurrentValue);

    /// <summary>What the settings ask to record. When both name something, the app wins.</summary>
    private static Source FromSettings(RecordingOptions settings)
    {
        var app = Normalise(settings.App);
        return new Source(app is null ? Normalise(settings.Device) : null, app);
    }

    /// <summary>Trims a name; a blank one means nothing is chosen.</summary>
    private static string? Normalise(string? name) => string.IsNullOrWhiteSpace(name) ? null : name.Trim();

    /// <summary>A device or an app to record, or neither for whichever device the class plays on.</summary>
    private sealed record Source(string? Device, string? App);
}
