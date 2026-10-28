using ClassScribe.Runs;

namespace ClassScribe.Audio;

/// <summary>
/// What the computer plays, as a recording session's source: the output devices and the apps that Windows lists, with the
/// chooser picking what to record from them, as the run's class source names it, and the provider opening the captures.
/// It looks once a second, and passes on the provider's notifications about the output devices.
/// </summary>
public sealed class LoopbackSource(ILoopbackCaptureProvider provider, CaptureChooser chooser) : ICaptureSource
{
    /// <summary>How often a session looks at the output devices by itself (see <see cref="CheckInterval"/>).</summary>
    private static readonly TimeSpan OnceASecond = TimeSpan.FromSeconds(1);

    /// <summary>Raised when Windows reports a change among the output devices (see <see cref="ILoopbackCaptureProvider.DevicesChanged"/>).</summary>
    public event EventHandler? Changed
    {
        add => provider.DevicesChanged += value;
        remove => provider.DevicesChanged -= value;
    }

    /// <summary>For a device, found or named: an output device, and system audio. For an app: the app, and its sound, by its name.</summary>
    public SourceWording Wording => chooser.Source is ClassSource.App app
        ? new($"the app \"{app.ProcessName}\"", $"the sound of \"{app.ProcessName}\"")
        : new("an output device", "system audio");

    /// <summary>
    /// Once a second. Windows announces some changes at once, through <see cref="Changed"/>; the look catches the rest, such
    /// as a browser sent to another device in the per-app sound settings, which no announcement covers.
    /// </summary>
    public TimeSpan CheckInterval => OnceASecond;

    /// <inheritdoc />
    public string WaitingFor => chooser.WaitingFor;

    /// <inheritdoc />
    public float? RecordedLevel => chooser.RecordedLevel;

    /// <inheritdoc />
    public CaptureTarget? Choose(string? recordedId, TimeSpan? sinceRecordedSound) => chooser.Choose(provider, recordedId, sinceRecordedSound);

    /// <inheritdoc />
    public IAudioCapture Open(CaptureTarget target) => provider.CreateCapture(target);

    /// <summary>
    /// For an app: it now plays from another process, which is followed. For a device named in the class source: that
    /// device is recorded again instead. For the device the class plays on: the sound is playing on another device.
    /// </summary>
    public string DescribeMove(CaptureTarget chosen, string from) => chooser.Source switch
    {
        ClassSource.App => $"\"{chosen.Name}\" now plays from another process; following it",
        ClassSource.Device => $"Recording \"{chosen.Name}\", the chosen output device, instead of \"{from}\"",
        _ => $"Sound is playing on \"{chosen.Name}\"; recording it instead of \"{from}\""
    };

    /// <summary>Nothing of its own to release: the provider, its notifications and the captures it opens belong to others.</summary>
    public void Dispose()
    {
    }
}
