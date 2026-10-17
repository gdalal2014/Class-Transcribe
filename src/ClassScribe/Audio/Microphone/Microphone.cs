using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

// The folder is Audio/Microphone; the namespace is Microphones, because a namespace named Microphone would hide the
// Microphone record from every file in the Audio namespaces, where the name would find the namespace first.
namespace ClassScribe.Audio.Microphones;

/// <summary>How Windows processes the microphone's audio before handing it over (see <c>Recording:MicrophoneProcessing</c>).</summary>
public enum MicrophoneProcessing
{
    /// <summary>
    /// Unprocessed audio (RAW): Windows applies none of its sound effects, such as noise suppression, when the
    /// microphone supports it. When it does not, <see cref="Default"/> is used instead, and the log says so.
    /// </summary>
    Raw,

    /// <summary>With Windows' sound processing, as any app gets it.</summary>
    Default
}

/// <summary>A microphone Windows can record from right now, as its property store describes it. Nothing here opens it.</summary>
/// <param name="Id">Windows' identifier for the endpoint, by which the choice of microphone is kept for the whole recording.</param>
/// <param name="Name">The name Windows shows for it, such as Microphone Array (Intel® Smart Sound Technology).</param>
/// <param name="IsDefault">True for Windows' default microphone, the one its Console role names.</param>
/// <param name="IsBuiltInArray">True for the laptop's own microphone array: a jack subtype of KSNODETYPE_MICROPHONE_ARRAY on a bus that is neither USB nor Bluetooth.</param>
/// <param name="IsBluetooth">True for a Bluetooth microphone, which is never chosen by itself: using it switches earbuds to call quality, for the class too.</param>
/// <param name="IsLoopbackInput">True for an input that records what the computer plays (Stereo Mix, What U Hear, Wave Out Mix), which is never chosen: it would record the class twice.</param>
/// <param name="RawSupported">True when the endpoint reports that unprocessed audio (RAW) can be asked for.</param>
/// <param name="Enumerator">The bus the device is on, as Windows names it: INTELAUDIO, USB, BTHENUM, BTHLE and so on.</param>
/// <param name="BluetoothEvidence">What showed the microphone to be Bluetooth: its enumerator, or the string property that mentions BTHENUM, BTHLE or Bluetooth; null when nothing did.</param>
public sealed record Microphone(string Id, string Name, bool IsDefault, bool IsBuiltInArray, bool IsBluetooth, bool IsLoopbackInput,
    bool RawSupported, string Enumerator, string? BluetoothEvidence);

/// <summary>
/// Lists the microphones Windows can record from, tells when they change, and opens a capture of one. Listing reads
/// the endpoints' property stores only, and never opens a stream on any microphone.
/// </summary>
public interface IMicrophoneProvider : IDisposable
{
    /// <summary>
    /// Raised when Windows reports a change among microphones: one added, removed or changing state, or a new default.
    /// It can also be raised when nothing changed for the recording, so listeners look at <see cref="GetMicrophones"/>
    /// rather than at the event.
    /// </summary>
    event EventHandler? MicrophonesChanged;

    /// <summary>The active microphones right now, from their property stores alone: no stream is opened.</summary>
    IReadOnlyList<Microphone> GetMicrophones();

    /// <summary>Creates a capture of <paramref name="microphone"/>, asking for <paramref name="processing"/>; Raw falls back to Default when it is not available.</summary>
    IAudioCapture CreateCapture(Microphone microphone, MicrophoneProcessing processing);

    /// <summary>
    /// Whether the microphone with <paramref name="id"/> is muted in Windows: the switch in Sound settings, and the
    /// keyboard's microphone key on most laptops. Null when that cannot be read. Opens no stream.
    /// </summary>
    bool? IsMuted(string id);
}

/// <summary>Opens a capture of a microphone by its endpoint id, asking Windows for the processing wanted.</summary>
public interface IMicrophoneCaptureFactory
{
    /// <summary>
    /// Opens a capture of the microphone <paramref name="endpointId"/>, named <paramref name="name"/> in the log, with
    /// <paramref name="processing"/>; Raw falls back to Default when it is not available.
    /// </summary>
    IAudioCapture Open(string endpointId, string name, MicrophoneProcessing processing);
}

/// <summary>Whether the class app lets go of the microphone when you mute in it (see <c>Recording:ClassAppMute</c>).</summary>
public enum ClassAppMute
{
    /// <summary>It does: its capture session ends on mute and comes back on unmute, so ClassScribe follows your mute by itself.</summary>
    Visible,

    /// <summary>
    /// It keeps the microphone open and only stops sending your voice, as Chrome pages, Zoom and Teams do. Windows cannot
    /// see your mute, so your voice is recorded only after you press V, and only while the app holds the microphone.
    /// </summary>
    Invisible,

    /// <summary>Not measured yet: treated as <see cref="Invisible"/>, and ClassScribe reports when it sees the app let go of the microphone.</summary>
    Unknown
}

/// <summary>
/// Which app's microphone use the microphone track follows, with the class: a packet from the microphone is written
/// only while that app has the recorded microphone open and taking sound, and Windows is not muting it. These three
/// kinds and no others. Written down, each kind is named by "kind": named, any or none.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(Named), "named")]
[JsonDerivedType(typeof(Any), "any")]
[JsonDerivedType(typeof(None), "none")]
public abstract record FollowRule
{
    /// <summary>The word that means <see cref="Any"/> in the settings and on the command line.</summary>
    public const string AnyWord = "auto";

    /// <summary>The word that means <see cref="None"/> in the settings and on the command line.</summary>
    public const string NoneWord = "none";

    /// <summary>Only the kinds declared here derive from it.</summary>
    private FollowRule()
    {
    }

    /// <summary>
    /// The rule <paramref name="text"/> spells, as a setting or an option spells it: <see cref="AnyWord"/>, <see cref="NoneWord"/>,
    /// or part of an app's name, in any case and with a trailing .exe dropped. False for an empty value, which spells no rule.
    /// </summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out FollowRule? rule)
    {
        var word = (text ?? "").Trim();
        if (word.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) word = word[..^4].TrimEnd();

        rule = word.Length == 0 ? null
            : word.Equals(AnyWord, StringComparison.OrdinalIgnoreCase) ? new Any()
            : word.Equals(NoneWord, StringComparison.OrdinalIgnoreCase) ? new None()
            : new Named(word);
        return rule is not null;
    }

    /// <summary>
    /// The rule as Recording:MicrophoneFollows and --mic-follows spell it: the name part, <see cref="AnyWord"/> or
    /// <see cref="NoneWord"/>, which <see cref="TryParse"/> reads back. Left out of the run file, which keeps the kind.
    /// </summary>
    [JsonIgnore]
    public string Word => this switch
    {
        Named named => named.NamePart,
        Any => AnyWord,
        _ => NoneWord
    };

    /// <summary>
    /// The app whose process name contains <paramref name="NamePart"/>, in any case, or is it: chrome, or the app the class is
    /// recorded from. Chrome's capture runs in a chrome.exe utility process of its own, so the match is by process name,
    /// never by the process the class is recorded from.
    /// </summary>
    /// <param name="NamePart">Part of the app's process name, such as chrome, as <c>--app</c> takes it.</param>
    public sealed record Named(string NamePart) : FollowRule
    {
        /// <summary>True when <paramref name="processName"/>, without .exe, is or contains the name part, in any case: the rule <c>--app</c> matches apps by.</summary>
        public bool Matches(string processName) => processName.Contains(NamePart, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Any app other than ClassScribe that has the recorded microphone open: <c>auto</c>.</summary>
    public sealed record Any : FollowRule;

    /// <summary>No app: the microphone is recorded whenever V is on, as in person: <c>none</c>.</summary>
    public sealed record None : FollowRule;
}

/// <summary>
/// Why the microphone track is or is not being written now, as the gate that follows the class app sees it. The window's
/// title and status line show it; the recorder's state carries it to every window.
/// </summary>
public enum MicrophoneGateState
{
    /// <summary>The followed app is listening, Windows is not muting the microphone, and it is on: your voice is recorded.</summary>
    Recording,

    /// <summary>The followed app holds the microphone but is not taking sound from it: nothing is written.</summary>
    NotListening,

    /// <summary>The microphone is muted in Windows, which silences the class app too: nothing is written.</summary>
    MutedInWindows,

    /// <summary>The followed app has not opened the recorded microphone: waiting for it.</summary>
    NotOpened,

    /// <summary>The followed app listens on another microphone than the one recorded.</summary>
    OtherMicrophone,

    /// <summary>The app's sessions cannot be read: nothing is written until a look succeeds.</summary>
    Unreadable,

    /// <summary>Switched off with V, or not yet switched on with it where the app's mute cannot be seen: nothing is written.</summary>
    Off,

    /// <summary>No app is followed, in person or with <c>Recording:MicrophoneFollows</c> none: the microphone is recorded whenever it is on.</summary>
    Unfollowed
}
