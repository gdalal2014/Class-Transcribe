using ClassScribe.Settings;
using Microsoft.Extensions.Options;

namespace ClassScribe.Audio;

/// <summary>
/// What to record: one app's sound, one output device chosen by part of its name or, with neither,
/// whichever device the class is playing through. It starts from Recording:App and Recording:Device;
/// the menu can change it for the rest of the run. Choosing an app drops a chosen device and the
/// other way round, so only one is ever set.
/// </summary>
public sealed class SourceChoice
{
    private readonly Lock _gate = new();
    private string? _device;
    private string? _app;

    /// <summary>Starts from the settings. When both name something, the app wins.</summary>
    public SourceChoice(IOptions<RecordingOptions> options)
    {
        _app = Normalise(options.Value.App);
        _device = _app is null ? Normalise(options.Value.Device) : null;
    }

    /// <summary>Part of the chosen device's name, matched in any case, or null when no device is chosen.</summary>
    public string? Device
    {
        get { lock (_gate) return _device; }
        set
        {
            lock (_gate)
            {
                _device = Normalise(value);
                if (_device is not null) _app = null;
            }
        }
    }

    /// <summary>The chosen app's process name, such as chrome, matched in any case, or null when no app is chosen.</summary>
    public string? App
    {
        get { lock (_gate) return _app; }
        set
        {
            lock (_gate)
            {
                _app = Normalise(value);
                if (_app is not null) _device = null;
            }
        }
    }

    /// <summary>What is recorded, in words, for the menu.</summary>
    public string Description => App is { } app ? $"the app \"{app}\""
        : Device is { } device ? $"the output device \"{device}\""
        : "the device the class plays on";

    /// <summary>Trims a name; a blank one means nothing is chosen.</summary>
    private static string? Normalise(string? name) => string.IsNullOrWhiteSpace(name) ? null : name.Trim();
}
