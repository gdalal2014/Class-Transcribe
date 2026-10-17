using ClassScribe.Audio.Microphones;
using ClassScribe.Commands;
using ClassScribe.Settings;
using Microsoft.Extensions.Options;

namespace ClassScribe.Audio;

/// <summary>
/// What to record. The class: one app's sound, one output device chosen by part of its name or, with neither, whichever
/// device the class is playing through. It follows Recording:App and Recording:Device, as they are read again before each
/// choice, until menu choice 6 picks something; that pick, automatic included, then holds for the rest of the session,
/// whatever the settings say. Choosing an app drops a chosen device and the other way round, so only one is ever set; when
/// the settings name both, the app wins. The microphone: none, with the class, or alone in person, which microphone, and
/// which app its track follows. That follows Recording:Mode, Recording:Microphone and Recording:MicrophoneFollows until
/// menu choice 7 picks something, which then holds in the same way. A run's request takes it all as a source plan
/// (<see cref="Current"/>).
/// </summary>
public sealed class SourceChoice(IOptionsMonitor<RecordingOptions> options)
{
    private readonly Lock _gate = new();

    /// <summary>What menu choice 6 picked for the rest of the session, or null while the settings decide.</summary>
    private ClassSource? _picked;

    /// <summary>What menu choice 7 picked for the rest of the session, or null while the settings decide.</summary>
    private MenuMicrophone? _microphone;

    /// <summary>
    /// What is chosen now, as a run's source plan: the class alone, from the app or the device the choice names, or from
    /// the device the class plays on when it names neither; the class and the microphone; or the microphone alone, in
    /// person. A microphone left to the settings is null in the plan, and so is the app it follows: the run reads
    /// Recording:Microphone and Recording:MicrophoneFollows itself, or follows the app it records; in person no app is
    /// followed.
    /// </summary>
    public SourcePlan Current
    {
        get
        {
            lock (_gate)
            {
                var settings = options.CurrentValue;
                return ModeNow(settings) switch
                {
                    RecordingMode.Microphone => new SourcePlan(RecordingMode.Microphone, Class: null, new MicrophoneChoice(_microphone?.NamePart)),
                    RecordingMode.ClassAndMicrophone => new SourcePlan(RecordingMode.ClassAndMicrophone, Class, new MicrophoneChoice(_microphone?.NamePart, _microphone?.Follows)),
                    _ => new SourcePlan(RecordingMode.Class, Class, Microphone: null)
                };
            }
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

    /// <summary>What a recording holds now: the class alone, the class and your microphone, or your microphone alone in person; the pick from menu choice 7, else Recording:Mode.</summary>
    public RecordingMode Mode
    {
        get
        {
            lock (_gate) return ModeNow(options.CurrentValue);
        }
    }

    /// <summary>
    /// The app whose microphone use the microphone track follows now, with the class: the pick from menu choice 7, else the
    /// app the class is recorded from, else Recording:MicrophoneFollows as the settings read it. Null when that setting
    /// names nothing, which the settings check refuses.
    /// </summary>
    public FollowRule? Follows
    {
        get
        {
            lock (_gate) return FollowsNow(options.CurrentValue);
        }
    }

    /// <summary>
    /// Menu choice 7: records <paramref name="mode"/> for the rest of the session, whatever the settings say, with the
    /// microphone whose name contains <paramref name="namePart"/>, or null to leave the microphone to Recording:Microphone
    /// and Windows' default, and with the microphone following <paramref name="follows"/>, or null to leave that to the app
    /// recorded and Recording:MicrophoneFollows.
    /// </summary>
    public void PickMicrophone(RecordingMode mode, string? namePart, FollowRule? follows)
    {
        lock (_gate) _microphone = new MenuMicrophone(mode, Normalise(namePart), follows);
    }

    /// <summary>
    /// What is recorded, in words, for the menu: the class, from what; with the microphone, which one, by the pick or by
    /// Recording:Microphone, or Windows' default, and the app it follows; in person, the microphone alone.
    /// </summary>
    public string Description
    {
        get
        {
            lock (_gate)
            {
                var settings = options.CurrentValue;
                var microphone = (_microphone?.NamePart ?? Normalise(settings.Microphone)) is { } name ? $"your microphone \"{name}\"" : "Windows' default microphone";
                return ModeNow(settings) switch
                {
                    RecordingMode.Microphone => $"{microphone}, in person",
                    RecordingMode.ClassAndMicrophone => $"{DescribeClass(Class)}, and {microphone}{FollowWords(FollowsNow(settings))}",
                    _ => DescribeClass(Class)
                };
            }
        }
    }

    /// <summary>Where the class is recorded from now: the pick from menu choice 6, or else the settings as they are now. Call with the lock held.</summary>
    private ClassSource Class => _picked ?? FromSettings(options.CurrentValue);

    /// <summary>What a recording holds now: the pick from menu choice 7, or else <paramref name="settings"/>. Call with the lock held.</summary>
    private RecordingMode ModeNow(RecordingOptions settings) => _microphone?.Mode ?? settings.Mode;

    /// <summary>The app the microphone follows now (see <see cref="Follows"/>). Call with the lock held.</summary>
    private FollowRule? FollowsNow(RecordingOptions settings) =>
        _microphone?.Follows ?? (Class is ClassSource.App app ? new FollowRule.Named(app.ProcessName)
            : FollowRule.TryParse(settings.MicrophoneFollows, out var rule) ? rule
            : null);

    /// <summary>The class's source, in words.</summary>
    private static string DescribeClass(ClassSource source) => source switch
    {
        ClassSource.App app => $"the app \"{app.ProcessName}\"",
        ClassSource.Device device => $"the output device \"{device.NamePart}\"",
        _ => "the device the class plays on"
    };

    /// <summary>The app the microphone follows, in words after the microphone: the app named, any app, or V alone; nothing when no rule is set.</summary>
    private static string FollowWords(FollowRule? follows) => follows switch
    {
        FollowRule.Named named => $" (following {named.NamePart})",
        FollowRule.Any => " (following any app that holds the microphone)",
        FollowRule.None => " (whenever V is on)",
        _ => ""
    };

    /// <summary>What the settings ask to record. When both name something, the app wins; when neither does, the device the class plays on.</summary>
    private static ClassSource FromSettings(RecordingOptions settings) =>
        Normalise(settings.App) is { } app ? new ClassSource.App(app)
        : Normalise(settings.Device) is { } device ? new ClassSource.Device(device)
        : new ClassSource.Automatic();

    /// <summary>Trims a name; a blank one means nothing is chosen.</summary>
    private static string? Normalise(string? name) => string.IsNullOrWhiteSpace(name) ? null : name.Trim();

    /// <summary>What menu choice 7 picked: the mode, the microphone by part of its name, or null for the settings, and the app it follows, or null for the settings.</summary>
    private sealed record MenuMicrophone(RecordingMode Mode, string? NamePart, FollowRule? Follows);
}
