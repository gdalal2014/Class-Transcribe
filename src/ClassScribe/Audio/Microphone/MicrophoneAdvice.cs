using System.Globalization;
using System.Security;
using ClassScribe.Desktop;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Audio.Microphones;

/// <summary>One line of advice at the start of a recording with the microphone.</summary>
/// <param name="Text">The line.</param>
/// <param name="Warning">True for a warning about something to change, which the run also shows as a Notice; false for a line that says what is recorded and how.</param>
public sealed record MicrophoneAdviceLine(string Text, bool Warning);

/// <summary>How a recording with the microphone starts, for the advice about it.</summary>
/// <param name="Mode">The class and the microphone, or the microphone alone in person. The class alone gets no advice.</param>
/// <param name="Microphone">The microphone chosen; null while there is none to record yet (none connected, or the named one missing), when the lines about one come from <see cref="MicrophoneAdvice.ForMicrophone"/> once it is chosen.</param>
/// <param name="Used">How Windows processes its audio: RAW, or Default.</param>
/// <param name="RawRefusal">Why RAW was not available when it was asked for and Default is used instead, such as 0x88890027; null otherwise.</param>
/// <param name="Why">Why that microphone was chosen, as the chooser said it; null to leave it out.</param>
/// <param name="WaitingFor">What the recording waits for while there is no microphone, as the chooser says it, such as the microphone "OBSBOT"; null for a microphone, plainly.</param>
/// <param name="ClassDeviceName">The device the class plays on at the start, with the class; null when not known.</param>
/// <param name="ClassHeardInTheRoom">True when that device is one the room hears, so that the microphone records the class a second time.</param>
/// <param name="Follows">Which app's microphone use the microphone track follows, with the class; null or None when V alone switches it.</param>
/// <param name="AppMute">Whether the followed app lets go of the microphone when you mute in it.</param>
/// <param name="Off">
/// True when the track starts switched off, as it does while the followed app's mute cannot be seen: <paramref name="Microphone"/>
/// is then the one the recording would record once V switches it on, nothing is opened yet, and <paramref name="Used"/> is
/// what will be asked for.
/// </param>
public sealed record MicrophoneStart(RecordingMode Mode, Microphone? Microphone, MicrophoneProcessing Used, string? RawRefusal = null, string? Why = null,
    string? WaitingFor = null, string? ClassDeviceName = null, bool ClassHeardInTheRoom = false, FollowRule? Follows = null, ClassAppMute AppMute = ClassAppMute.Unknown,
    bool Off = false);

/// <summary>
/// The advice about the microphone, for a run: the lines to log before the first audio (privacy and the class app's mute,
/// which microphone and how, Lenovo Vantage's noise cancelling, Windows' ducking, a Bluetooth microphone, the class playing
/// where the microphone hears it, and what to do in person), the lines about another microphone whenever the recording
/// moves to one, the echo check again whenever the class moves, and Vantage's mode read again while the laptop's own array
/// is recorded, because Vantage's scene profile can change it mid-class. One per run: it remembers which microphone is
/// recorded and the mode it last read.
/// </summary>
public sealed class MicrophoneAdvice(IRegistryValues registry, ILogger<MicrophoneAdvice> logger)
{
    /// <summary>The key under HKEY_CURRENT_USER where Lenovo Vantage keeps its microphone settings.</summary>
    public const string VantageKey = @"Software\Lenovo\SmartEngine\modules\F4006";

    /// <summary>Vantage's microphone noise-cancelling mode, a string: "0" is off, and the labels of the other values are inferred.</summary>
    public const string VantageValue = "noise_micro_mode";

    /// <summary>The key under HKEY_CURRENT_USER where Windows keeps the ducking preference.</summary>
    public const string DuckingKey = @"Software\Microsoft\Multimedia\Audio";

    /// <summary>What Windows does to other sounds while a microphone is open, a number: 3 is Do nothing; missing means Windows' default, lower them by 80%.</summary>
    public const string DuckingValue = "UserDuckingPreference";

    /// <summary>How often Vantage's mode is worth reading again while recording the array.</summary>
    public static readonly TimeSpan VantageInterval = TimeSpan.FromSeconds(30);

    private const string ClassPrivacy = "Your microphone is recorded too, and uploaded to ElevenLabs with the class.";
    private const string RoomPrivacy = "Your microphone records the room, and the recording is uploaded to ElevenLabs. Ask before recording others.";
    private const string VSwitches = "V switches it off and on; muting yourself in the class app does not.";
    private const string InPersonLine = "Plug in and keep the lid open; the screen stays on while on battery. For a break press P; V switches the microphone off but keeps the clock running, and that silence is uploaded.";
    private const string VantageHarm = "it can cut out a lecturer across the room.";
    private const string VantageFix = "Set Vantage > Device Settings > Sound > Input to Off.";
    private const string DuckingFix = "Set Sound > More sound settings > Communications to Do nothing.";
    private const string VantageWhat = "Lenovo Vantage's microphone mode";
    private const string DuckingWhat = "Windows' ducking setting";

    /// <summary>The likely labels of Vantage's modes, inferred from Lenovo's guide; the number is always shown beside them.</summary>
    private static readonly Dictionary<string, string> VantageLabels = new()
    {
        ["1"] = "Multiple Voices",
        ["2"] = "Normal",
        ["3"] = "Voice Recognition",
        ["4"] = "Only My Voice"
    };

    /// <summary>The values whose read failed, each warned about once.</summary>
    private readonly HashSet<string> _unreadable = [];

    /// <summary>True while the laptop's array is recorded, so that Vantage's mode is read again.</summary>
    private bool _watchVantage;

    /// <summary>True once Vantage's mode has been read for the microphone recorded now, so that a change can be told from a first read.</summary>
    private bool _vantageKnown;

    /// <summary>Vantage's mode as last read; null when missing, off is "0".</summary>
    private string? _vantageMode;

    /// <summary>
    /// The lines to log before the first audio, in order: what is recorded and how, then the warnings. Empty for a
    /// recording of the class alone. With no microphone yet, the microphone line says what is waited for, and the lines
    /// about a microphone come from <see cref="ForMicrophone"/> once one is chosen. A track that starts switched off
    /// still names the microphone V will open, and is warned about it at once. A start with the laptop's array also
    /// begins the watch on Vantage's mode.
    /// </summary>
    public IReadOnlyList<MicrophoneAdviceLine> AtStart(MicrophoneStart start)
    {
        if (start.Mode == RecordingMode.Class) return [];
        var withClass = start.Mode == RecordingMode.ClassAndMicrophone;
        var microphone = start.Microphone;
        List<MicrophoneAdviceLine> lines = [];

        if (withClass)
        {
            lines.Add(new(ClassPrivacy, Warning: false));
            lines.Add(new(CaseLine(start.Follows, start.AppMute), Warning: false));
        }
        else
        {
            lines.Add(new(RoomPrivacy, Warning: false));
        }

        lines.Add(new(MicrophoneLine(start), Warning: false));

        if (microphone is not null && WatchVantage(microphone) is { } vantage) lines.Add(new(vantage, Warning: true));
        if (withClass && DuckingLine() is { } ducking) lines.Add(new(ducking, Warning: true));
        if (microphone is not null && BluetoothLine(microphone, withClass) is { } bluetooth) lines.Add(new(bluetooth, Warning: true));
        if (withClass && start.ClassHeardInTheRoom && ForClassDevice(start.ClassDeviceName ?? "", heardInTheRoom: true) is { } echo) lines.Add(new(echo, Warning: true));
        if (!withClass) lines.Add(new(InPersonLine, Warning: false));
        return lines;
    }

    /// <summary>
    /// The lines about <paramref name="microphone"/>, recorded from now on: for the run to log and show whenever the
    /// recording moves to another microphone (one standing in, the chosen one back, or the one the class app uses), and
    /// when the first one is chosen after a start without. The warning about Lenovo Vantage's noise cancelling when it is
    /// the laptop's array, which the watch of <see cref="VantageNow"/> then follows (and leaves alone for any other
    /// microphone), and the warning when it is Bluetooth. Empty when neither applies; every line is a warning.
    /// </summary>
    public IReadOnlyList<MicrophoneAdviceLine> ForMicrophone(Microphone microphone, bool withClass)
    {
        List<MicrophoneAdviceLine> lines = [];
        if (WatchVantage(microphone) is { } vantage) lines.Add(new(vantage, Warning: true));
        if (BluetoothLine(microphone, withClass) is { } bluetooth) lines.Add(new(bluetooth, Warning: true));
        return lines;
    }

    /// <summary>
    /// The echo warning for the device the class plays on now, named <paramref name="deviceName"/>, when the room hears
    /// it; null otherwise. Checked again whenever the class moves to another device.
    /// </summary>
    public string? ForClassDevice(string deviceName, bool heardInTheRoom)
    {
        if (!heardInTheRoom) return null;
        var device = deviceName.Length == 0 ? "a device the room hears" : $"\"{deviceName}\"";
        return $"The class plays on {device}, so the microphone records it a second time (an echo). Use headphones.";
    }

    /// <summary>
    /// Reads Vantage's mode again, while the laptop's array is recorded, and says what changed since it was last read:
    /// switched on, changed to another mode, or now off. Null while it stays as it was, when another microphone is
    /// recorded, before the start has been advised, and when the registry cannot be read now, which leaves the mode last
    /// read as it was. A mode first read now, after a start whose read failed, is reported as it is found.
    /// </summary>
    public string? VantageNow()
    {
        if (!_watchVantage || !TryReadVantage(out var mode)) return null;
        var (was, known) = (_vantageMode, _vantageKnown);
        _vantageMode = mode;
        _vantageKnown = true;
        if (!known) return IsOn(mode) ? OnLine(mode!) : null;

        var wasOn = IsOn(was);
        var isOn = IsOn(mode);
        if (!wasOn && !isOn) return null;
        if (wasOn && !isOn) return "Lenovo Vantage's microphone noise cancelling is now off.";
        if (!wasOn) return $"Lenovo Vantage's microphone noise cancelling has switched on {InParentheses(mode!)}; {VantageHarm} {VantageFix}";
        if (mode != was) return $"Lenovo Vantage's microphone noise cancelling has changed to {ModeText(mode!)}; {VantageHarm} {VantageFix}";
        return null;
    }

    /// <summary>
    /// How the class app's mute is followed, for the line after the privacy line: that ClassScribe follows it, that it
    /// cannot, or, until the app has been measured, that it does not know and so cannot follow it yet.
    /// </summary>
    private static string CaseLine(FollowRule? follows, ClassAppMute appMute)
    {
        if (follows is null or FollowRule.None) return VSwitches;
        var (app, appAtStart) = follows is FollowRule.Named named ? (named.NamePart, named.NamePart) : ("the class app", "The class app");
        const string windowsMute = "Muting the microphone in Windows (the microphone key) stops it too.";
        return appMute switch
        {
            ClassAppMute.Visible => $"Your voice is recorded while {app} is listening: when you mute in {app}, ClassScribe stops too. V switches it off by hand.",
            ClassAppMute.Invisible => $"{appAtStart} keeps the microphone open while you are muted, so ClassScribe cannot see your mute. " +
                $"Your voice is recorded only after you press V, and only while {app} holds the microphone. {windowsMute}",
            _ => $"ClassScribe has not been told whether {app} lets go of the microphone when you mute (most class apps and pages keep it open), so it cannot follow your mute yet: " +
                $"your voice is recorded only after you press V, and only while {app} holds the microphone. {windowsMute} " +
                "Run the microphone check (menu choice 8) during a class, or set Recording:ClassAppMute."
        };
    }

    /// <summary>
    /// Which microphone, why, and how Windows processes its audio; or what is waited for while there is none. A track
    /// that starts switched off names the microphone V will open and what will be asked for, since nothing is waited for.
    /// </summary>
    private static string MicrophoneLine(MicrophoneStart start)
    {
        var waitingFor = start.WaitingFor ?? "a microphone";
        if (start.Microphone is not { } microphone)
            return start.Off ? $"Microphone: switched off until you press V; none to record yet (waiting for {waitingFor})" : $"Microphone: waiting for {waitingFor}";

        var why = start.Why is null ? "" : $" ({start.Why})";
        if (start.Off)
        {
            var asked = start.Used == MicrophoneProcessing.Raw ? "RAW will be asked for" : "Windows' sound processing will be kept";
            return $"Microphone: \"{microphone.Name}\"{why}, switched off until you press V; {asked}";
        }

        var processing = start.Used == MicrophoneProcessing.Raw ? "without Windows' sound processing (RAW)"
            : start.RawRefusal is null ? "with Windows' sound processing"
            : $"with Windows' sound processing (RAW was not available: {start.RawRefusal})";
        return $"Microphone: \"{microphone.Name}\"{why}, {processing}";
    }

    /// <summary>The warning about a Bluetooth microphone, using it switches the earbuds to call quality; null for any other.</summary>
    private static string? BluetoothLine(Microphone microphone, bool withClass) => microphone.IsBluetooth
        ? $"The microphone \"{microphone.Name}\" is Bluetooth: using it switches the earbuds to call quality{(withClass ? ", for the class too" : "")}. The laptop's microphone array avoids that."
        : null;

    /// <summary>
    /// Points the watch on Vantage's mode at <paramref name="microphone"/>, recorded from now on: on for the laptop's array,
    /// whose mode is read now, off for any other, which Vantage's setting does not touch. The warning when it is the array
    /// and noise cancelling is on; null otherwise, and when the mode cannot be read.
    /// </summary>
    private string? WatchVantage(Microphone microphone)
    {
        _watchVantage = microphone.IsBuiltInArray;
        _vantageKnown = false;
        _vantageMode = null;
        if (!_watchVantage || !TryReadVantage(out var mode)) return null;
        _vantageMode = mode;
        _vantageKnown = true;
        return IsOn(mode) ? OnLine(mode!) : null;
    }

    /// <summary>The warning that noise cancelling is on, in <paramref name="mode"/>, as found at the start or at a first read.</summary>
    private static string OnLine(string mode) => $"Lenovo Vantage's microphone noise cancelling is on {InParentheses(mode)}; {VantageHarm} {VantageFix}";

    /// <summary>The warning about Windows' ducking, unless it is set to Do nothing or cannot be read.</summary>
    private string? DuckingLine()
    {
        if (!TryRead(() => registry.ReadDword(DuckingKey, DuckingValue), DuckingWhat, out var preference)) return null;
        return preference switch
        {
            3 => null,
            0 => $"Windows mutes other sounds while a microphone is open, which silences the class, in the recording too. {DuckingFix}",
            2 => $"Windows lowers other sounds by 50% while a microphone is open, which can make the class quieter, in the recording too. {DuckingFix}",
            _ => $"Windows lowers other sounds by 80% while a microphone is open, which can make the class quieter, in the recording too. {DuckingFix}"
        };
    }

    /// <summary>Reads Vantage's mode as the registry holds it now, trimmed, null when missing (which counts as off). False when it cannot be read.</summary>
    private bool TryReadVantage(out string? mode)
    {
        var read = TryRead(() => registry.ReadString(VantageKey, VantageValue), VantageWhat, out var text);
        mode = read ? text?.Trim() : null;
        return read;
    }

    /// <summary>True for a mode that means noise cancelling is on: anything but "0" and missing.</summary>
    private static bool IsOn(string? mode) => mode is not null && mode != "0";

    /// <summary>The mode in parentheses, with its likely label: (mode 1, probably "Multiple Voices"), or (mode 10).</summary>
    private static string InParentheses(string mode) =>
        VantageLabels.TryGetValue(mode, out var label) ? $"(mode {mode}, probably \"{label}\")" : $"(mode {mode})";

    /// <summary>The mode as a phrase, with its likely label: mode 4 (probably "Only My Voice"), or mode 10.</summary>
    private static string ModeText(string mode) =>
        VantageLabels.TryGetValue(mode, out var label) ? $"mode {mode} (probably \"{label}\")" : $"mode {mode}";

    /// <summary>
    /// Reads a registry value for the advice. False when the registry cannot be read, which is warned about once for
    /// each value and leaves that advice out.
    /// </summary>
    private bool TryRead<T>(Func<T?> read, string what, out T? value)
    {
        try
        {
            value = read();
            return true;
        }
        catch (Exception error) when (error is SecurityException or UnauthorizedAccessException or IOException)
        {
            // Deliberately not rethrown: this advice warns about a setting, and a registry value that cannot be read must
            // not keep a class from being recorded. The failure is logged once for each value, and that advice is left out.
            if (_unreadable.Add(what))
                logger.LogWarning("{What} could not be read from the registry, so it is not checked: {Reason}", what, error.Message);
            value = default;
            return false;
        }
    }
}
