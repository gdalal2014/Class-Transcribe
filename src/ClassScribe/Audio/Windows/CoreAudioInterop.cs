using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

// The tests drive the device check through fakes of the internal interfaces below, and through the
// provider's internal constructor that takes them.
[assembly: InternalsVisibleTo("ClassScribe.Tests")]

namespace ClassScribe.Audio.Windows;

/// <summary>
/// Creates Windows' device enumerator for ClassScribe's own declarations, and holds what every user of those
/// declarations needs: the numbers Windows expects, a check of the result each call returns, the release of each
/// object as soon as it has been used, so that nothing is left for the garbage collector to release later, and the
/// readers of a device's property store, which free each value as soon as it has been read.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class CoreAudio
{
    /// <summary>eRender: the devices that play sound.</summary>
    public const int Render = 0;

    /// <summary>eCapture: the devices that record, that is microphones and line inputs.</summary>
    public const int Capture = 1;

    /// <summary>DEVICE_STATE_ACTIVE: devices that are present and enabled.</summary>
    public const int Active = 1;

    /// <summary>eConsole: the role of the default device for most apps, the one Windows' Sound settings show as the default microphone.</summary>
    public const int Console = 0;

    /// <summary>eMultimedia: the role of the device that plays music and videos, the one ClassScribe calls the default output device.</summary>
    public const int Multimedia = 1;

    /// <summary>eCommunications: the role of the default device for calls, the microphone a class page uses.</summary>
    public const int Communications = 2;

    /// <summary>CLSCTX_ALL: Windows may create an activated object in any context.</summary>
    public const int AllContexts = 0x17;

    /// <summary>AudioSessionStateExpired: a session whose app has closed its sound.</summary>
    public const int SessionExpired = 2;

    /// <summary>E_NOTFOUND, HRESULT_FROM_WIN32(ERROR_NOT_FOUND): Windows' answer when it has no default output device.</summary>
    public const int NotFound = unchecked((int)0x80070490);

    /// <summary>STGM_READ: a property store opened for reading only.</summary>
    public const int ReadOnlyStore = 0;

    /// <summary>CLSCTX_INPROC_SERVER: the enumerator lives in this process.</summary>
    private const int InProcessServer = 1;

    /// <summary>CLSID_MMDeviceEnumerator.</summary>
    private static readonly Guid EnumeratorClassId = new("BCDE0395-E52F-467C-8E3D-C4579291692E");

    /// <summary>
    /// Creates Windows' device enumerator with a wrapper of its own. Windows hands out one enumerator per process,
    /// and .NET keeps one wrapper per COM object; NAudio makes its wrapper with <c>new</c> of a class of its own,
    /// which throws InvalidCastException when a wrapper of another type is already kept. A wrapper kept apart
    /// from the others cannot clash with NAudio's, whichever of the two is made first.
    /// </summary>
    public static IMMDeviceEnumeratorRaw CreateEnumerator()
    {
        var classId = EnumeratorClassId;
        var interfaceId = typeof(IMMDeviceEnumeratorRaw).GUID;
        Check(CoCreateInstance(ref classId, IntPtr.Zero, InProcessServer, ref interfaceId, out var pointer));
        try
        {
            return (IMMDeviceEnumeratorRaw)Marshal.GetUniqueObjectForIUnknown(pointer);
        }
        finally
        {
            // The wrapper holds its own reference; this one came from CoCreateInstance.
            Marshal.Release(pointer);
        }
    }

    /// <summary>
    /// Opens a device's property store for reading, with a wrapper of its own, as <see cref="CreateEnumerator"/> makes
    /// the enumerator's: NAudio wraps the same store with a class of its own when it is asked for a device's properties.
    /// The caller releases the store with <see cref="Release"/> once it has read what it needs.
    /// </summary>
    public static IPropertyStoreRaw OpenPropertyStore(IMMDeviceRaw device)
    {
        Check(device.OpenPropertyStore(ReadOnlyStore, out var pointer));
        try
        {
            return (IPropertyStoreRaw)Marshal.GetUniqueObjectForIUnknown(pointer);
        }
        finally
        {
            // The wrapper holds its own reference; this one came from OpenPropertyStore.
            Marshal.Release(pointer);
        }
    }

    /// <summary>The string property <paramref name="key"/> of <paramref name="store"/>, or null when the device has no such property, or it is not a string.</summary>
    public static string? ReadString(IPropertyStoreRaw store, PropertyKeyRaw key) => Read(store, key, static value => value.AsString());

    /// <summary>The whole-number property <paramref name="key"/> of <paramref name="store"/>, or null when the device has no such property, or it is not an unsigned 32-bit number.</summary>
    public static uint? ReadUInt(IPropertyStoreRaw store, PropertyKeyRaw key) => Read(store, key, static value => value.AsUInt32());

    /// <summary>The yes-or-no property <paramref name="key"/> of <paramref name="store"/>, or null when the device has no such property, or it is not a VARIANT_BOOL.</summary>
    public static bool? ReadBool(IPropertyStoreRaw store, PropertyKeyRaw key) => Read(store, key, static value => value.AsBool());

    /// <summary>The identifier property <paramref name="key"/> of <paramref name="store"/>, held as a class id or spelled out in a string, or null when the device has no such property, or it is neither.</summary>
    public static Guid? ReadGuid(IPropertyStoreRaw store, PropertyKeyRaw key) => Read(store, key, static value => value.AsGuid());

    /// <summary>
    /// Every string property of <paramref name="store"/> with its key, in the store's order, the properties of other
    /// kinds left out: the Bluetooth check reads them all, because a headset's bus shows up in a property that no key names.
    /// </summary>
    public static IReadOnlyList<(PropertyKeyRaw Key, string Value)> ReadStringProperties(IPropertyStoreRaw store)
    {
        Check(store.GetCount(out var count));
        List<(PropertyKeyRaw, string)> strings = [];
        for (var index = 0; index < count; index++)
        {
            Check(store.GetAt(index, out var key));
            if (ReadString(store, key) is { } text) strings.Add((key, text));
        }

        return strings;
    }

    /// <summary>Throws the exception that matches a failed result, such as a <see cref="COMException"/>; does nothing for a success.</summary>
    public static void Check(int result)
    {
        if (result < 0) Marshal.ThrowExceptionForHR(result);
    }

    /// <summary>
    /// Releases a COM object now rather than when the garbage collector gets to it. Anything else, such as a
    /// test's fake, and null, are left alone.
    /// </summary>
    public static void Release(object? comObject)
    {
        if (comObject is not null && Marshal.IsComObject(comObject)) Marshal.ReleaseComObject(comObject);
    }

    /// <summary>
    /// Reads the property <paramref name="key"/> of <paramref name="store"/> as <paramref name="read"/> reads the value, and
    /// frees whatever the value holds afterwards, whether or not it was of the kind asked for. A store that fails to answer
    /// makes this fail with its result; a property the device lacks is an empty value, which reads as null.
    /// </summary>
    private static T Read<T>(IPropertyStoreRaw store, PropertyKeyRaw key, Func<PropVariantRaw, T> read)
    {
        var value = default(PropVariantRaw);
        try
        {
            Check(store.GetValue(ref key, out value));
            return read(value);
        }
        finally
        {
            // Frees what the value holds, such as the string Windows allocated for it, and leaves an empty value as it is.
            PropVariantClear(ref value);
        }
    }

    /// <summary>Creates a COM object of the class <paramref name="classId"/> and returns its <paramref name="interfaceId"/> interface.</summary>
    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(ref Guid classId, IntPtr outer, int context, ref Guid interfaceId, out IntPtr instance);

    /// <summary>Frees whatever a PROPVARIANT holds and sets it to empty.</summary>
    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int PropVariantClear(ref PropVariantRaw value);
}

/// <summary>The keys of the properties ClassScribe reads from a device's property store, as Microsoft documents them.</summary>
internal static class PropertyKeys
{
    /// <summary>The property set of a device's own properties (PKEY_Device_*).</summary>
    private static readonly Guid Device = new("A45C254E-DF1C-4EFD-8020-67D146A850E0");

    /// <summary>The property set of an audio endpoint's properties (PKEY_AudioEndpoint_*).</summary>
    private static readonly Guid Endpoint = new("1DA5D803-D492-4EDD-8C23-E0C0FFEE7F0E");

    /// <summary>PKEY_Device_FriendlyName, a string: the name Windows shows, such as Microphone Array (Intel® Smart Sound Technology).</summary>
    public static readonly PropertyKeyRaw FriendlyName = new(Device, 14);

    /// <summary>PKEY_Device_EnumeratorName, a string: the bus the device is on, such as INTELAUDIO, USB, BTHENUM or BTHLE.</summary>
    public static readonly PropertyKeyRaw EnumeratorName = new(Device, 24);

    /// <summary>PKEY_AudioEndpoint_FormFactor, a whole number: what the endpoint is (see <see cref="EndpointFormFactor"/>).</summary>
    public static readonly PropertyKeyRaw FormFactor = new(Endpoint, 0);

    /// <summary>PKEY_AudioEndpoint_JackSubType, a string spelling the identifier of the jack's kind, such as KSNODETYPE_MICROPHONE_ARRAY.</summary>
    public static readonly PropertyKeyRaw JackSubType = new(Endpoint, 8);

    /// <summary>System.Devices.AudioDevice.RawProcessingSupported, a yes or no: whether unprocessed audio (RAW) can be asked for.</summary>
    public static readonly PropertyKeyRaw RawProcessingSupported = new(new Guid("8943B373-388C-4395-B557-BC6DBAFFAFDB"), 2);
}

/// <summary>What an audio endpoint is, as PKEY_AudioEndpoint_FormFactor says it (Windows' EndpointFormFactor).</summary>
internal enum EndpointFormFactor : uint
{
    /// <summary>A device on the network.</summary>
    RemoteNetworkDevice = 0,

    /// <summary>Speakers, heard in the room.</summary>
    Speakers = 1,

    /// <summary>A line-level output, such as to an amplifier, heard in the room.</summary>
    LineLevel = 2,

    /// <summary>Headphones.</summary>
    Headphones = 3,

    /// <summary>A microphone.</summary>
    Microphone = 4,

    /// <summary>A headset: headphones with a microphone.</summary>
    Headset = 5,

    /// <summary>A handset.</summary>
    Handset = 6,

    /// <summary>A digital pass-through of an unknown kind.</summary>
    UnknownDigitalPassthrough = 7,

    /// <summary>An S/PDIF output, usually to an amplifier heard in the room.</summary>
    Spdif = 8,

    /// <summary>A display's own audio over HDMI or DisplayPort, heard in the room.</summary>
    DigitalAudioDisplayDevice = 9,

    /// <summary>Not known.</summary>
    UnknownFormFactor = 10
}

/// <summary>PROPERTYKEY: names one property of a device by the identifier of its property set and a number within it.</summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly struct PropertyKeyRaw(Guid formatId, int propertyId) : IEquatable<PropertyKeyRaw>
{
    /// <summary>The property set the property belongs to.</summary>
    public readonly Guid FormatId = formatId;

    /// <summary>The property's number within its set.</summary>
    public readonly int PropertyId = propertyId;

    /// <inheritdoc />
    public bool Equals(PropertyKeyRaw other) => FormatId == other.FormatId && PropertyId == other.PropertyId;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is PropertyKeyRaw other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(FormatId, PropertyId);

    /// <summary>The key as the registry and the documentation write it, such as {a45c254e-df1c-4efd-8020-67d146a850e0},14.</summary>
    public override string ToString() => $"{FormatId:B},{PropertyId}";
}

/// <summary>
/// PROPVARIANT, laid out as Windows lays it out: the type in the first two bytes, six reserved bytes, then the value's
/// space, two pointer-sized words, so 16 bytes in a 32-bit process and 24 in a 64-bit one. A number, a yes or no, and a
/// pointer to a string or an identifier all sit at the start of the first word. Whatever a value holds is freed with
/// PropVariantClear once it has been read (see <see cref="CoreAudio"/>).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PropVariantRaw
{
    /// <summary>VT_EMPTY: no value, as for a property the device lacks.</summary>
    public const ushort EmptyType = 0;

    /// <summary>VT_BOOL: a 16-bit VARIANT_BOOL, -1 for yes and 0 for no.</summary>
    public const ushort BoolType = 11;

    /// <summary>VT_UNKNOWN: a pointer to a COM object, which holds a reference the clear gives back.</summary>
    public const ushort UnknownType = 13;

    /// <summary>VT_UI4: an unsigned 32-bit number.</summary>
    public const ushort UInt32Type = 19;

    /// <summary>VT_LPWSTR: a pointer to a string Windows allocated for the value.</summary>
    public const ushort StringType = 31;

    /// <summary>VT_CLSID: a pointer to a GUID Windows allocated for the value.</summary>
    public const ushort ClassIdType = 72;

    /// <summary>The type of the value, such as <see cref="StringType"/>.</summary>
    public ushort Type;

    /// <summary>Reserved by Windows.</summary>
    public ushort Reserved1;

    /// <summary>Reserved by Windows.</summary>
    public ushort Reserved2;

    /// <summary>Reserved by Windows.</summary>
    public ushort Reserved3;

    /// <summary>The value's first word: the number or the yes or no in its first bytes, or the pointer to a string, an identifier or an object.</summary>
    public IntPtr Value;

    /// <summary>The value's second word, used by kinds of value ClassScribe does not read, such as arrays.</summary>
    public IntPtr Value2;

    /// <summary>The string the value holds, or null when it holds none.</summary>
    public readonly string? AsString() => Type == StringType ? Marshal.PtrToStringUni(Value) : null;

    /// <summary>The unsigned 32-bit number the value holds (its first four bytes), or null when it holds none.</summary>
    public readonly uint? AsUInt32() => Type == UInt32Type ? unchecked((uint)(long)Value) : null;

    /// <summary>The yes or no the value holds (its first two bytes, nonzero for yes), or null when it holds none.</summary>
    public readonly bool? AsBool() => Type == BoolType ? unchecked((short)(long)Value) != 0 : null;

    /// <summary>The identifier the value holds, as a class id or spelled out in a string, or null when it holds none.</summary>
    public readonly Guid? AsGuid() => Type switch
    {
        ClassIdType when Value != IntPtr.Zero => Marshal.PtrToStructure<Guid>(Value),
        StringType when Guid.TryParse(Marshal.PtrToStringUni(Value), out var guid) => guid,
        _ => null
    };
}

/// <summary>IMMDeviceEnumerator: lists Windows' audio devices. Only the methods the device check uses are declared, in Windows' order.</summary>
[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumeratorRaw
{
    /// <summary>Lists the devices that play (<paramref name="dataFlow"/> 0) or record (1), in the states of <paramref name="stateMask"/>.</summary>
    [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollectionRaw devices);

    /// <summary>The default device for a direction and a role; <see cref="CoreAudio.NotFound"/> when there is none.</summary>
    [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDeviceRaw endpoint);

    /// <summary>The device with the identifier Windows gave it.</summary>
    [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDeviceRaw device);
}

/// <summary>IMMDeviceCollection: the devices a listing found, as they were when it was made.</summary>
[ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceCollectionRaw
{
    /// <summary>How many devices the listing holds.</summary>
    [PreserveSig] int GetCount(out int count);

    /// <summary>The device at <paramref name="index"/>, from 0.</summary>
    [PreserveSig] int Item(int index, out IMMDeviceRaw device);
}

/// <summary>IMMDevice: one audio device.</summary>
[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceRaw
{
    /// <summary>Creates an object of the device's, such as its level meter or its session manager, by the interface's identifier.</summary>
    [PreserveSig] int Activate(ref Guid interfaceId, int context, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);

    /// <summary>
    /// Opens the device's property store, as a pointer that <see cref="CoreAudio.OpenPropertyStore"/> wraps apart from any
    /// other wrapper of the same store. The device check does not call it; the microphone listing reads the store.
    /// </summary>
    [PreserveSig] int OpenPropertyStore(int access, out IntPtr properties);

    /// <summary>The identifier Windows gave the device.</summary>
    [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);

    /// <summary>Whether the device is active, disabled, not present or unplugged.</summary>
    [PreserveSig] int GetState(out int state);
}

/// <summary>
/// IMMEndpoint: whether an endpoint plays or records. Its one method, read only by the test guard of Windows' activation of
/// a microphone (see <see cref="Desktop.DeviceGuards"/>), so that a test can never activate a capture endpoint's audio client.
/// </summary>
[ComImport, Guid("1BE09788-6894-4089-8586-9A2A6C265AC5"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMEndpointRaw
{
    /// <summary>The endpoint's direction: <see cref="CoreAudio.Render"/> for one that plays, <see cref="CoreAudio.Capture"/> for one that records.</summary>
    [PreserveSig] int GetDataFlow(out int dataFlow);
}

/// <summary>IPropertyStore: a device's properties, read one by one or in order. Only reads are made; the two writes are declared to keep Windows' order.</summary>
[ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPropertyStoreRaw
{
    /// <summary>How many properties the store holds.</summary>
    [PreserveSig] int GetCount(out int count);

    /// <summary>The key of the property at <paramref name="index"/>, from 0.</summary>
    [PreserveSig] int GetAt(int index, out PropertyKeyRaw key);

    /// <summary>The value of the property <paramref name="key"/>: empty when the device lacks it. The caller frees what the value holds.</summary>
    [PreserveSig] int GetValue(ref PropertyKeyRaw key, out PropVariantRaw value);

    /// <summary>Declared to keep Windows' order; never called, so no property is ever written.</summary>
    [PreserveSig] int SetValue(ref PropertyKeyRaw key, ref PropVariantRaw value);

    /// <summary>Declared to keep Windows' order; never called.</summary>
    [PreserveSig] int Commit();
}

/// <summary>IAudioMeterInformation: the level meter of a device or of an app's sound.</summary>
[ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioMeterInformationRaw
{
    /// <summary>How loud the sound is right now, from 0 (silence) to 1.</summary>
    [PreserveSig] int GetPeakValue(out float peak);
}

/// <summary>IAudioSessionManager2: the audio sessions of one device, one or more per app with sound open on it.</summary>
[ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionManager2Raw
{
    /// <summary>IAudioSessionManager's first method. Declared to keep Windows' order; not called.</summary>
    [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid, int streamFlags, out IntPtr sessionControl);

    /// <summary>IAudioSessionManager's second method. Declared to keep Windows' order; not called.</summary>
    [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid, int streamFlags, out IntPtr audioVolume);

    /// <summary>Lists the device's sessions as they are now. No notification is registered, so none can arrive later.</summary>
    [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumeratorRaw sessions);
}

/// <summary>IAudioSessionEnumerator: the sessions a listing found.</summary>
[ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionEnumeratorRaw
{
    /// <summary>How many sessions the listing holds.</summary>
    [PreserveSig] int GetCount(out int count);

    /// <summary>The session at <paramref name="index"/>, from 0, as its IAudioSessionControl.</summary>
    [PreserveSig] int GetSession(int index, [MarshalAs(UnmanagedType.IUnknown)] out object session);
}

/// <summary>
/// IAudioSessionControl2: one app's sound on a device. IAudioSessionControl's nine methods come first, because
/// .NET does not lay out an inherited COM interface's methods; only the state and the process id are called.
/// </summary>
[ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionControl2Raw
{
    /// <summary>Whether the session is active, inactive or expired.</summary>
    [PreserveSig] int GetState(out int state);

    /// <summary>Declared to keep Windows' order; not called.</summary>
    [PreserveSig] int GetDisplayName(out IntPtr name);

    /// <summary>Declared to keep Windows' order; not called.</summary>
    [PreserveSig] int SetDisplayName(IntPtr name, IntPtr eventContext);

    /// <summary>Declared to keep Windows' order; not called.</summary>
    [PreserveSig] int GetIconPath(out IntPtr path);

    /// <summary>Declared to keep Windows' order; not called.</summary>
    [PreserveSig] int SetIconPath(IntPtr path, IntPtr eventContext);

    /// <summary>Declared to keep Windows' order; not called.</summary>
    [PreserveSig] int GetGroupingParam(out Guid grouping);

    /// <summary>Declared to keep Windows' order; not called.</summary>
    [PreserveSig] int SetGroupingParam(IntPtr grouping, IntPtr eventContext);

    /// <summary>Declared to keep Windows' order; never called, so no session notification is ever registered.</summary>
    [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);

    /// <summary>Declared to keep Windows' order; not called.</summary>
    [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);

    /// <summary>Declared to keep Windows' order; not called.</summary>
    [PreserveSig] int GetSessionIdentifier(out IntPtr id);

    /// <summary>Declared to keep Windows' order; not called.</summary>
    [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr id);

    /// <summary>The process that has the sound open; 0 for Windows' own sounds.</summary>
    [PreserveSig] int GetProcessId(out uint processId);
}

/// <summary>
/// IAudioEndpointVolume: a device's own volume and mute, the ones in Windows' Sound settings and behind a laptop's
/// microphone-mute key. Every method is declared, in Windows' order (endpointvolume.idl), because COM calls a method by
/// its position; only GetMute is called, by the microphone listing and the sessions reader, so nothing is ever set and
/// no notification is ever registered.
/// </summary>
[ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioEndpointVolumeRaw
{
    /// <summary>Declared to keep Windows' order; never called, so no notification is registered.</summary>
    [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);

    /// <summary>Declared to keep Windows' order; never called.</summary>
    [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);

    /// <summary>Declared to keep Windows' order; not called.</summary>
    [PreserveSig] int GetChannelCount(out uint count);

    /// <summary>Declared to keep Windows' order; never called.</summary>
    [PreserveSig] int SetMasterVolumeLevel(float levelDecibels, IntPtr eventContext);

    /// <summary>Declared to keep Windows' order; never called.</summary>
    [PreserveSig] int SetMasterVolumeLevelScalar(float level, IntPtr eventContext);

    /// <summary>Declared to keep Windows' order; not called.</summary>
    [PreserveSig] int GetMasterVolumeLevel(out float levelDecibels);

    /// <summary>Declared to keep Windows' order; not called. The device's volume from 0 to 1, the input volume in Windows' Sound settings.</summary>
    [PreserveSig] int GetMasterVolumeLevelScalar(out float level);

    /// <summary>Declared to keep Windows' order; never called.</summary>
    [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDecibels, IntPtr eventContext);

    /// <summary>Declared to keep Windows' order; never called.</summary>
    [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, IntPtr eventContext);

    /// <summary>Declared to keep Windows' order; not called.</summary>
    [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDecibels);

    /// <summary>Declared to keep Windows' order; not called.</summary>
    [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);

    /// <summary>Declared to keep Windows' order; never called, so the mute is never set.</summary>
    [PreserveSig] int SetMute(int mute, IntPtr eventContext);

    /// <summary>Whether the device is muted in Windows (nonzero) or not (0).</summary>
    [PreserveSig] int GetMute(out int muted);
}

/// <summary>ISimpleAudioVolume: one session's own volume and mute, asked of the session object. The setters are declared only to keep Windows' order and are never called.</summary>
[ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISimpleAudioVolumeRaw
{
    /// <summary>Declared to keep Windows' order; never called.</summary>
    [PreserveSig] int SetMasterVolume(float level, IntPtr eventContext);

    /// <summary>The session's own volume from 0 to 1.</summary>
    [PreserveSig] int GetMasterVolume(out float level);

    /// <summary>Declared to keep Windows' order; never called.</summary>
    [PreserveSig] int SetMute(int mute, IntPtr eventContext);

    /// <summary>Whether the session is muted (nonzero) or not (0).</summary>
    [PreserveSig] int GetMute(out int muted);
}
