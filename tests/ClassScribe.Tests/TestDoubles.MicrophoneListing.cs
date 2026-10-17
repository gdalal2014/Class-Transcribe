using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ClassScribe.Audio.Windows;

namespace ClassScribe.Tests;

/// <summary>
/// Windows' device enumerator as a test plays it for the microphone listing: it lists the capture endpoints it was
/// given whose state the listing asks for, names the default one for the Console role, finds an endpoint by its
/// identifier, and keeps account of what it was asked. While <see cref="Failure"/> is set, every call answers with that
/// error instead, as an enumerator Windows no longer serves would.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class FakeMicrophoneEnumerator(params FakeCaptureEndpoint[] endpoints) : IMMDeviceEnumeratorRaw
{
    /// <summary>The identifier of Windows' default microphone for the Console role, or null when there is none.</summary>
    public string? DefaultId { get; init; }

    /// <summary>When not 0, the HRESULT every call answers with.</summary>
    public int Failure { get; set; }

    /// <summary>The account of what it hands out, or null to keep none.</summary>
    public FakeComLedger? Ledger { get; init; }

    /// <summary>The listings asked for, each as the direction and the state mask, in order.</summary>
    public List<(int DataFlow, int StateMask)> Listings { get; } = [];

    /// <summary>The defaults asked for, each as the direction and the role, in order.</summary>
    public List<(int DataFlow, int Role)> DefaultsAsked { get; } = [];

    /// <inheritdoc />
    public int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollectionRaw collection)
    {
        collection = null!;
        if (Failure != 0) return Failure;

        Listings.Add((dataFlow, stateMask));
        var listed = dataFlow == CoreAudio.Capture ? endpoints.Where(endpoint => (endpoint.State & stateMask) != 0).ToList() : [];
        collection = FakeComLedger.Track(Ledger, new FakeEndpointCollection(listed, Ledger));
        return FakeResults.Ok;
    }

    /// <inheritdoc />
    public int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDeviceRaw endpoint)
    {
        endpoint = null!;
        if (Failure != 0) return Failure;

        DefaultsAsked.Add((dataFlow, role));
        if (dataFlow != CoreAudio.Capture || role != CoreAudio.Console) return CoreAudio.NotFound;
        if (endpoints.FirstOrDefault(candidate => candidate.Id == DefaultId) is not { } found) return CoreAudio.NotFound;

        endpoint = FakeComLedger.Track(Ledger, found);
        return FakeResults.Ok;
    }

    /// <inheritdoc />
    public int GetDevice(string id, out IMMDeviceRaw device)
    {
        device = null!;
        if (Failure != 0) return Failure;
        if (endpoints.FirstOrDefault(candidate => candidate.Id == id) is not { } found) return CoreAudio.NotFound;

        device = FakeComLedger.Track(Ledger, found);
        return FakeResults.Ok;
    }
}

/// <summary>The capture endpoints one listing found, handed out through <paramref name="ledger"/>.</summary>
[SupportedOSPlatform("windows")]
internal sealed class FakeEndpointCollection(IReadOnlyList<FakeCaptureEndpoint> endpoints, FakeComLedger? ledger) : IMMDeviceCollectionRaw
{
    /// <inheritdoc />
    public int GetCount(out int count)
    {
        count = endpoints.Count;
        return FakeResults.Ok;
    }

    /// <inheritdoc />
    public int Item(int index, out IMMDeviceRaw device)
    {
        device = FakeComLedger.Track(ledger, endpoints[index]);
        return FakeResults.Ok;
    }
}

/// <summary>
/// A capture endpoint as a test plays it: its identifier and state, the property store that describes it, and the
/// endpoint volume that says whether it is muted in Windows. It keeps account of every interface asked of it, so that a
/// test can check that nothing but the endpoint volume was ever asked for, and in particular never an audio client.
/// </summary>
/// <param name="id">The identifier Windows would give the endpoint.</param>
/// <param name="name">The name Windows shows for it, PKEY_Device_FriendlyName; null for an endpoint without one.</param>
/// <param name="enumerator">The bus it is on, PKEY_Device_EnumeratorName; null for an endpoint without one.</param>
/// <param name="jackSubType">Its jack subtype, PKEY_AudioEndpoint_JackSubType, spelled out as Windows spells it; null for none.</param>
/// <param name="rawSupported">Whether it reports RawProcessingSupported; null for an endpoint without the property.</param>
[SupportedOSPlatform("windows")]
internal sealed class FakeCaptureEndpoint(string id, string? name, string? enumerator = "INTELAUDIO", Guid? jackSubType = null, bool? rawSupported = null) : IMMDeviceRaw
{
    /// <summary>DEVICE_STATE_DISABLED: present but switched off in Sound settings, as Stereo Mix is on this laptop.</summary>
    public const int Disabled = 2;

    /// <summary>DEVICE_STATE_NOTPRESENT: not connected right now, as a USB microphone that is unplugged.</summary>
    public const int NotPresent = 4;

    /// <summary>DEVICE_STATE_UNPLUGGED: known but with nothing in its jack, as Bluetooth earbuds in their case.</summary>
    public const int Unplugged = 8;

    /// <summary>The identifier Windows would give the endpoint.</summary>
    public string Id { get; } = id;

    /// <summary>The endpoint's state: active unless set otherwise.</summary>
    public int State { get; init; } = CoreAudio.Active;

    /// <summary>The property store that describes the endpoint, holding what the constructor was given and whatever <see cref="With"/> adds.</summary>
    public FakeEndpointStore Store { get; } = FakeEndpointStore.Describing(name, enumerator, jackSubType, rawSupported);

    /// <summary>Whether the endpoint is muted in Windows, as its endpoint volume answers.</summary>
    public bool Muted { get; set; }

    /// <summary>When not 0, the HRESULT asking for its endpoint volume answers with.</summary>
    public int VolumeFailure { get; init; }

    /// <summary>When not 0, the HRESULT its endpoint volume's GetMute answers with.</summary>
    public int MuteFailure { get; init; }

    /// <summary>The account of the objects it hands out, or null to keep none.</summary>
    public FakeComLedger? Ledger { get; set; }

    /// <summary>Every interface asked of <see cref="Activate"/>, in order, whether or not it could be made.</summary>
    public List<Guid> Activated { get; } = [];

    /// <summary>Gives the endpoint one more string property, as a device's driver adds properties of its own, and returns the endpoint.</summary>
    public FakeCaptureEndpoint With(PropertyKeyRaw key, string value)
    {
        Store.Add(key, value);
        return this;
    }

    /// <summary>
    /// Opens a fake endpoint's property store the way the provider is given to open one: the store of that endpoint,
    /// handed out through its ledger so that the provider is held to releasing it.
    /// </summary>
    public static IPropertyStoreRaw OpenStore(IMMDeviceRaw device)
    {
        var endpoint = (FakeCaptureEndpoint)device;
        return FakeComLedger.Track(endpoint.Ledger, endpoint.Store);
    }

    /// <inheritdoc />
    public int Activate(ref Guid interfaceId, int context, IntPtr activationParams, out object instance)
    {
        instance = null!;
        Activated.Add(interfaceId);
        if (interfaceId != typeof(IAudioEndpointVolumeRaw).GUID) return FakeResults.NoInterface;
        if (VolumeFailure != 0) return VolumeFailure;

        instance = FakeComLedger.Track(Ledger, new FakeEndpointVolume(Muted, MuteFailure));
        return FakeResults.Ok;
    }

    /// <summary>Not implemented: the provider opens the store through the function it is given, which <see cref="OpenStore"/> plays.</summary>
    public int OpenPropertyStore(int access, out IntPtr properties)
    {
        properties = IntPtr.Zero;
        return FakeResults.NotImplemented;
    }

    /// <inheritdoc />
    public int GetId(out string value)
    {
        value = Id;
        return FakeResults.Ok;
    }

    /// <inheritdoc />
    public int GetState(out int state)
    {
        state = State;
        return FakeResults.Ok;
    }
}

/// <summary>
/// A property store as a test plays it for a capture endpoint, holding the values it was given, each handed out as
/// Windows hands it out: a string in task memory the reader must free. A property it lacks answers an empty variant,
/// as Windows does; while <see cref="Failure"/> is set, every call answers that result instead, as the store of an
/// endpoint that has just gone does.
/// </summary>
internal sealed class FakeEndpointStore : IPropertyStoreRaw
{
    private readonly List<(PropertyKeyRaw Key, Func<PropVariantRaw> Value)> _properties = [];

    /// <summary>When not 0, the HRESULT every call answers with.</summary>
    public int Failure { get; set; }

    /// <summary>
    /// A store holding the properties the listing reads, in the order Windows keeps them: the name, the enumerator, the
    /// jack subtype (spelled out, as Windows keeps it) and whether unprocessed audio is supported, each left out when null.
    /// </summary>
    public static FakeEndpointStore Describing(string? name, string? enumerator, Guid? jackSubType, bool? rawSupported)
    {
        var store = new FakeEndpointStore();
        if (name is not null) store.Add(PropertyKeys.FriendlyName, name);
        if (enumerator is not null) store.Add(PropertyKeys.EnumeratorName, enumerator);
        if (jackSubType is { } jack) store.Add(PropertyKeys.JackSubType, jack.ToString("B").ToUpperInvariant());
        if (rawSupported is { } raw) store.Add(PropertyKeys.RawProcessingSupported, raw);
        return store;
    }

    /// <summary>Gives the store a string property.</summary>
    public void Add(PropertyKeyRaw key, string value) => _properties.Add((key, () =>
        new PropVariantRaw { Type = PropVariantRaw.StringType, Value = Marshal.StringToCoTaskMemUni(value) }));

    /// <summary>Gives the store a whole-number property.</summary>
    public void Add(PropertyKeyRaw key, uint value) => _properties.Add((key, () => new PropVariantRaw { Type = PropVariantRaw.UInt32Type, Value = (IntPtr)(long)value }));

    /// <summary>Gives the store a yes-or-no property.</summary>
    public void Add(PropertyKeyRaw key, bool value) => _properties.Add((key, () => new PropVariantRaw { Type = PropVariantRaw.BoolType, Value = (IntPtr)(value ? 0xFFFF : 0) }));

    /// <inheritdoc />
    public int GetCount(out int count)
    {
        count = Failure == 0 ? _properties.Count : 0;
        return Failure == 0 ? FakeResults.Ok : Failure;
    }

    /// <inheritdoc />
    public int GetAt(int index, out PropertyKeyRaw key)
    {
        key = Failure == 0 ? _properties[index].Key : default;
        return Failure == 0 ? FakeResults.Ok : Failure;
    }

    /// <inheritdoc />
    public int GetValue(ref PropertyKeyRaw key, out PropVariantRaw value)
    {
        value = default;
        if (Failure != 0) return Failure;

        var wanted = key;
        var found = _properties.FirstOrDefault(property => property.Key.Equals(wanted));
        if (found.Value is not null) value = found.Value();
        return FakeResults.Ok;
    }

    /// <inheritdoc />
    public int SetValue(ref PropertyKeyRaw key, ref PropVariantRaw value) => throw new AssertionException("The listing set a property.");

    /// <inheritdoc />
    public int Commit() => throw new AssertionException("The listing committed a property.");
}

/// <summary>
/// An endpoint's volume and mute as a test plays it: GetMute answers <paramref name="muted"/>, or fails with
/// <paramref name="failure"/> when that is not 0. Every setter fails the test, because the provider only ever reads.
/// </summary>
internal sealed class FakeEndpointVolume(bool muted, int failure = 0) : IAudioEndpointVolumeRaw
{
    /// <inheritdoc />
    public int RegisterControlChangeNotify(IntPtr notify) => throw new AssertionException("The provider registered for volume notifications.");

    /// <inheritdoc />
    public int UnregisterControlChangeNotify(IntPtr notify) => throw new AssertionException("The provider unregistered volume notifications.");

    /// <inheritdoc />
    public int GetChannelCount(out uint count)
    {
        count = 0;
        return FakeResults.NotImplemented;
    }

    /// <inheritdoc />
    public int SetMasterVolumeLevel(float levelDecibels, IntPtr eventContext) => throw new AssertionException("The provider set the volume.");

    /// <inheritdoc />
    public int SetMasterVolumeLevelScalar(float level, IntPtr eventContext) => throw new AssertionException("The provider set the volume.");

    /// <inheritdoc />
    public int GetMasterVolumeLevel(out float levelDecibels)
    {
        levelDecibels = 0;
        return FakeResults.NotImplemented;
    }

    /// <inheritdoc />
    public int GetMasterVolumeLevelScalar(out float level)
    {
        level = 0;
        return FakeResults.NotImplemented;
    }

    /// <inheritdoc />
    public int SetChannelVolumeLevel(uint channel, float levelDecibels, IntPtr eventContext) => throw new AssertionException("The provider set a channel's volume.");

    /// <inheritdoc />
    public int SetChannelVolumeLevelScalar(uint channel, float level, IntPtr eventContext) => throw new AssertionException("The provider set a channel's volume.");

    /// <inheritdoc />
    public int GetChannelVolumeLevel(uint channel, out float levelDecibels)
    {
        levelDecibels = 0;
        return FakeResults.NotImplemented;
    }

    /// <inheritdoc />
    public int GetChannelVolumeLevelScalar(uint channel, out float level)
    {
        level = 0;
        return FakeResults.NotImplemented;
    }

    /// <inheritdoc />
    public int SetMute(int mute, IntPtr eventContext) => throw new AssertionException("The provider set the mute.");

    /// <inheritdoc />
    public int GetMute(out int value)
    {
        value = failure == 0 && muted ? 1 : 0;
        return failure == 0 ? FakeResults.Ok : failure;
    }
}

/// <summary>
/// The capture endpoints of the laptop ClassScribe is built on, as the mic-design listing recorded them: the built-in
/// array (Windows' default), Pixel Buds on the BTHLE bus, a soundcore headset whose bus is INTELAUDIO like the array's
/// but whose properties name BTHENUM and Bluetooth, an OBSBOT on USB, and Realtek's Stereo Mix. Each is made afresh,
/// active, so that a test can set its state or its mute.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class LaptopEndpoints
{
    /// <summary>The array's identifier.</summary>
    public const string ArrayId = "{0.0.1.00000000}.{array}";

    /// <summary>The Pixel Buds' identifier.</summary>
    public const string PixelBudsId = "{0.0.1.00000000}.{pixel-buds}";

    /// <summary>The soundcore headset's identifier.</summary>
    public const string SoundcoreId = "{0.0.1.00000000}.{soundcore}";

    /// <summary>The OBSBOT's identifier.</summary>
    public const string ObsbotId = "{0.0.1.00000000}.{obsbot}";

    /// <summary>Stereo Mix's identifier.</summary>
    public const string StereoMixId = "{0.0.1.00000000}.{stereo-mix}";

    /// <summary>The array's name, as Windows shows it.</summary>
    public const string ArrayName = "Microphone Array (Intel® Smart Sound Technology for Digital Microphones)";

    /// <summary>KSNODETYPE_MICROPHONE: the jack subtype of a plain microphone.</summary>
    public static readonly Guid MicrophoneJack = new("DFF21BE1-F70F-11D0-B917-00A0C9223196");

    /// <summary>KSNODETYPE_HEADSET_MICROPHONE: the jack subtype of a headset's microphone.</summary>
    public static readonly Guid HeadsetMicrophoneJack = new("DFF21DE5-F70F-11D0-B917-00A0C9223196");

    /// <summary>The property set of Intel's Smart Sound Technology driver, under which the soundcore's Bluetooth properties sit.</summary>
    private static readonly Guid IntelDriverProperties = new("B3F8FA53-0004-438E-9003-51A46E139BFC");

    /// <summary>The property of Intel's driver that names the bus the headset really sits on, BTHENUM, behind the INTELAUDIO enumerator.</summary>
    public static readonly PropertyKeyRaw IntelBusKey = new(IntelDriverProperties, 39);

    /// <summary>The property of Intel's driver that describes the headset's driver, naming Bluetooth.</summary>
    public static readonly PropertyKeyRaw IntelDriverKey = new(IntelDriverProperties, 6);

    /// <summary>The value of <see cref="IntelBusKey"/> on the soundcore headset.</summary>
    public const string SoundcoreBus = @"{1}.BTHENUM\{0000111E-0000-1000-8000-00805F9B34FB}_HCIBYPASS_VID&000102B0_PID&0000\7&2736B729&0&E8EC4A5B0E12_C00000000";

    /// <summary>The value of <see cref="IntelDriverKey"/> on the soundcore headset.</summary>
    public const string SoundcoreDriver = "Intelr Smart Sound Technology for Bluetoothr Audio";

    /// <summary>The built-in microphone array: INTELAUDIO, a microphone-array jack, unprocessed audio supported.</summary>
    public static FakeCaptureEndpoint Array() => new(ArrayId, ArrayName, "INTELAUDIO", EndpointFacts.MicrophoneArrayJack, rawSupported: true);

    /// <summary>The Pixel Buds' headset microphone: on the BTHLE bus.</summary>
    public static FakeCaptureEndpoint PixelBuds() => new(PixelBudsId, "Headset (2- Pixel Buds Pro 2)", "BTHLE", HeadsetMicrophoneJack, rawSupported: false);

    /// <summary>The soundcore headset's microphone: INTELAUDIO like the array, with Intel's driver properties naming Bluetooth and BTHENUM.</summary>
    public static FakeCaptureEndpoint Soundcore() =>
        new FakeCaptureEndpoint(SoundcoreId, "Headset (soundcore Liberty 5 Pro)", "INTELAUDIO", HeadsetMicrophoneJack, rawSupported: false)
            .With(IntelDriverKey, SoundcoreDriver)
            .With(IntelBusKey, SoundcoreBus);

    /// <summary>The OBSBOT microphone: on USB.</summary>
    public static FakeCaptureEndpoint Obsbot() => new(ObsbotId, "OBSBOT Meet Flip Microphone (OBSBOT Meet Flip Audio)", "USB", MicrophoneJack, rawSupported: true);

    /// <summary>Realtek's Stereo Mix, which records what the computer plays.</summary>
    public static FakeCaptureEndpoint StereoMix() => new(StereoMixId, "Stereo Mix (Realtek(R) Audio)", "INTELAUDIO", jackSubType: null, rawSupported: false);

    /// <summary>All five, in the order above, handing out what they make through <paramref name="ledger"/> when one is given.</summary>
    public static FakeCaptureEndpoint[] All(FakeComLedger? ledger = null)
    {
        FakeCaptureEndpoint[] endpoints = [Array(), PixelBuds(), Soundcore(), Obsbot(), StereoMix()];
        foreach (var endpoint in endpoints) endpoint.Ledger = ledger;
        return endpoints;
    }
}
