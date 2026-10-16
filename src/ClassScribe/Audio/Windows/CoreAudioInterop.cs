using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

// The tests drive the device check through fakes of the internal interfaces below, and through the
// provider's internal constructor that takes them.
[assembly: InternalsVisibleTo("ClassScribe.Tests")]

namespace ClassScribe.Audio.Windows;

/// <summary>
/// Creates Windows' device enumerator for ClassScribe's own declarations, and holds what every user of those
/// declarations needs: the numbers Windows expects, a check of the result each call returns, and the release of
/// each object as soon as it has been used, so that nothing is left for the garbage collector to release later.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class CoreAudio
{
    /// <summary>eRender: the devices that play sound.</summary>
    public const int Render = 0;

    /// <summary>DEVICE_STATE_ACTIVE: devices that are present and enabled.</summary>
    public const int Active = 1;

    /// <summary>eMultimedia: the role of the device that plays music and videos, the one ClassScribe calls the default.</summary>
    public const int Multimedia = 1;

    /// <summary>CLSCTX_ALL: Windows may create an activated object in any context.</summary>
    public const int AllContexts = 0x17;

    /// <summary>AudioSessionStateExpired: a session whose app has closed its sound.</summary>
    public const int SessionExpired = 2;

    /// <summary>E_NOTFOUND, HRESULT_FROM_WIN32(ERROR_NOT_FOUND): Windows' answer when it has no default output device.</summary>
    public const int NotFound = unchecked((int)0x80070490);

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

    /// <summary>Creates a COM object of the class <paramref name="classId"/> and returns its <paramref name="interfaceId"/> interface.</summary>
    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(ref Guid classId, IntPtr outer, int context, ref Guid interfaceId, out IntPtr instance);
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

    /// <summary>Opens the device's property store. Declared to keep Windows' order; the device check does not call it.</summary>
    [PreserveSig] int OpenPropertyStore(int access, out IntPtr properties);

    /// <summary>The identifier Windows gave the device.</summary>
    [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);

    /// <summary>Whether the device is active, disabled, not present or unplugged.</summary>
    [PreserveSig] int GetState(out int state);
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
