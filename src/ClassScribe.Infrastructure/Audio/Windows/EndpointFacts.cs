using System.Runtime.Versioning;
using ClassScribe.Audio.Microphones;

namespace ClassScribe.Audio.Windows;

/// <summary>
/// What a capture endpoint's property store says about a microphone, and what the choice of microphone reads from it:
/// whether it is the laptop's own microphone array, a Bluetooth microphone, which is never chosen by itself, or an
/// input that records what the computer plays, which is never chosen at all; and whether unprocessed audio (RAW) can
/// be asked for. Reading the store opens nothing on the microphone.
/// </summary>
/// <remarks>
/// A Bluetooth microphone is known by its bus (enumerator), BTHENUM, BTHLE, BTHLEDEVICE or BTHHFENUM, or, failing
/// that, by any string property naming BTHENUM, BTHLE or Bluetooth: on the laptop ClassScribe is built on, a soundcore
/// headset's microphone sits on INTELAUDIO like the built-in array, and only a property of Intel's driver gives its
/// Bluetooth bus away. The built-in array is a microphone array by its jack subtype on a bus that is neither USB nor
/// Bluetooth. A loopback input is known by its name: Stereo Mix, What U Hear or Wave Out Mix.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class EndpointFacts
{
    /// <summary>KSNODETYPE_MICROPHONE_ARRAY: the jack subtype of a microphone array, such as a laptop's built-in one.</summary>
    public static readonly Guid MicrophoneArrayJack = new("DFF21BE5-F70F-11D0-B917-00A0C9223196");

    /// <summary>KSNODETYPE_PROCESSING_MICROPHONE_ARRAY: the jack subtype of a microphone array with processing of its own, which some drivers report for the built-in array.</summary>
    public static readonly Guid ProcessingMicrophoneArrayJack = new("DFF21BE6-F70F-11D0-B917-00A0C9223196");

    /// <summary>The buses of Bluetooth devices, as Windows names them in PKEY_Device_EnumeratorName.</summary>
    public static readonly IReadOnlyList<string> BluetoothEnumerators = ["BTHENUM", "BTHLE", "BTHLEDEVICE", "BTHHFENUM"];

    /// <summary>The words that give a Bluetooth device away in any string property of its endpoint, in any case.</summary>
    public static readonly IReadOnlyList<string> BluetoothWords = ["BTHENUM", "BTHLE", "Bluetooth"];

    /// <summary>The names, in any case, of inputs that record what the computer plays, which would record the class twice.</summary>
    public static readonly IReadOnlyList<string> LoopbackInputNames = ["Stereo Mix", "What U Hear", "Wave Out Mix"];

    /// <summary>The bus of a USB device, as Windows names it.</summary>
    private const string UsbEnumerator = "USB";

    /// <summary>
    /// The facts as read: the name and the bus (null when the store lacks them), the jack subtype, whether unprocessed
    /// audio is supported (null when the store does not say), and every string property, for the Bluetooth check.
    /// </summary>
    public EndpointFacts(string? name, string? enumerator, Guid? jackSubType, bool? rawProcessingSupported, IReadOnlyList<(PropertyKeyRaw Key, string Value)> stringProperties,
        Guid? containerId = null)
    {
        Name = name;
        Enumerator = enumerator ?? "";
        JackSubType = jackSubType;
        RawProcessingSupported = rawProcessingSupported ?? false;
        StringProperties = stringProperties;
        ContainerId = containerId;
        BluetoothEvidence = FindBluetoothEvidence();
    }

    /// <summary>The physical device the endpoint belongs to, which a webcam's camera shares with its microphone; null when the store does not say.</summary>
    public Guid? ContainerId { get; }

    /// <summary>The name Windows shows for the microphone, or null when the store has none.</summary>
    public string? Name { get; }

    /// <summary>The bus the device is on, as Windows names it: INTELAUDIO, USB, BTHENUM, BTHLE and so on; empty when the store has none.</summary>
    public string Enumerator { get; }

    /// <summary>The identifier of the jack's kind, such as KSNODETYPE_MICROPHONE_ARRAY, or null when the store has none.</summary>
    public Guid? JackSubType { get; }

    /// <summary>True when the endpoint reports that unprocessed audio (RAW) can be asked for; false when it does not say.</summary>
    public bool RawProcessingSupported { get; }

    /// <summary>Every string property of the store with its key, in the store's order.</summary>
    public IReadOnlyList<(PropertyKeyRaw Key, string Value)> StringProperties { get; }

    /// <summary>What showed the microphone to be Bluetooth: its bus, or the property that names BTHENUM, BTHLE or Bluetooth; null when nothing did.</summary>
    public string? BluetoothEvidence { get; }

    /// <summary>True for a Bluetooth microphone, which the choice of microphone never makes by itself.</summary>
    public bool IsBluetooth => BluetoothEvidence is not null;

    /// <summary>True for the laptop's own microphone array: a microphone array by its jack subtype, on a bus that is neither USB nor Bluetooth.</summary>
    public bool IsBuiltInArray =>
        JackSubType is { } jack && (jack == MicrophoneArrayJack || jack == ProcessingMicrophoneArrayJack)
        && !IsBluetooth && !Enumerator.Equals(UsbEnumerator, StringComparison.OrdinalIgnoreCase);

    /// <summary>True for an input that records what the computer plays, known by its name.</summary>
    public bool IsLoopbackInput => Name is { } name && LoopbackInputNames.Any(loopback => name.Contains(loopback, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Reads the facts from <paramref name="store"/>: the name, the bus, the jack subtype, whether unprocessed audio is
    /// supported, every string property, and the container. A store that fails to answer, as the store of an endpoint that
    /// has just gone does, makes this fail with its result, for the caller to leave the endpoint out.
    /// </summary>
    public static EndpointFacts Read(IPropertyStoreRaw store) => new(
        CoreAudio.ReadString(store, PropertyKeys.FriendlyName),
        CoreAudio.ReadString(store, PropertyKeys.EnumeratorName),
        CoreAudio.ReadGuid(store, PropertyKeys.JackSubType),
        CoreAudio.ReadBool(store, PropertyKeys.RawProcessingSupported),
        CoreAudio.ReadStringProperties(store),
        CoreAudio.ReadGuid(store, PropertyKeys.ContainerId));

    /// <summary>The microphone these facts describe, with the identifier Windows gave the endpoint and whether it is Windows' default microphone. An endpoint without a name is named by its identifier.</summary>
    public Microphone ToMicrophone(string id, bool isDefault) =>
        new(id, Name ?? id, isDefault, IsBuiltInArray, IsBluetooth, IsLoopbackInput, RawProcessingSupported, Enumerator, BluetoothEvidence, ContainerId);

    /// <summary>The bus when it is a Bluetooth one, else the first string property naming Bluetooth with its key, else null.</summary>
    private string? FindBluetoothEvidence()
    {
        if (BluetoothEnumerators.Any(bus => bus.Equals(Enumerator, StringComparison.OrdinalIgnoreCase))) return $"enumerator {Enumerator}";

        foreach (var (key, value) in StringProperties)
        {
            if (BluetoothWords.Any(word => value.Contains(word, StringComparison.OrdinalIgnoreCase))) return $"{key} = {value}";
        }

        return null;
    }
}
