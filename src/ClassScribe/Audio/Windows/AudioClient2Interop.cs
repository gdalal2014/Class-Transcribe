using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ClassScribe.Audio.Windows;

/// <summary>
/// IAudioClient2, declared by ClassScribe itself. NAudio 2.2.1 declares it as inheriting IAudioClient, and built-in COM
/// interop ignores interface inheritance, so NAudio's <c>SetClientProperties</c> sits in slot 4, where Windows keeps
/// <c>IAudioClient::GetBufferSize</c> (measured: 0x88890001 through NAudio, S_OK through this declaration). Here
/// IAudioClient's twelve methods come first, in vtable order, so that the three of IAudioClient2 land in their own
/// slots. Only <see cref="SetClientProperties"/> is called, and only before the stream is opened; the stream itself is
/// opened and read through NAudio's <c>AudioClient</c>, which wraps the same object.
/// </summary>
[ComImport, Guid("726778CD-F60A-4EDA-82DE-E47610CD78AA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioClient2Native
{
    /// <summary>IAudioClient's first method (slot 3). Declared to keep Windows' order; the stream is opened through NAudio.</summary>
    [PreserveSig] int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);

    /// <summary>Slot 4, the one NAudio's declaration reaches when asked for SetClientProperties. Declared to keep Windows' order; not called.</summary>
    [PreserveSig] int GetBufferSize(out uint frames);

    /// <summary>Slot 5. Declared to keep Windows' order; not called.</summary>
    [PreserveSig] int GetStreamLatency(out long latency);

    /// <summary>Slot 6. Declared to keep Windows' order; not called.</summary>
    [PreserveSig] int GetCurrentPadding(out uint frames);

    /// <summary>Slot 7. Declared to keep Windows' order; not called.</summary>
    [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);

    /// <summary>Slot 8. Declared to keep Windows' order; the mix format is read through NAudio.</summary>
    [PreserveSig] int GetMixFormat(out IntPtr format);

    /// <summary>Slot 9. Declared to keep Windows' order; not called.</summary>
    [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);

    /// <summary>Slot 10. Declared to keep Windows' order; the stream is started through NAudio.</summary>
    [PreserveSig] int Start();

    /// <summary>Slot 11. Declared to keep Windows' order; the stream is stopped through NAudio.</summary>
    [PreserveSig] int Stop();

    /// <summary>Slot 12. Declared to keep Windows' order; not called.</summary>
    [PreserveSig] int Reset();

    /// <summary>Slot 13. Declared to keep Windows' order; not called.</summary>
    [PreserveSig] int SetEventHandle(IntPtr handle);

    /// <summary>Slot 14, IAudioClient's last method. Declared to keep Windows' order; the capture client is reached through NAudio.</summary>
    [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);

    /// <summary>Slot 15, IAudioClient2's first method. Declared to keep Windows' order; ClassScribe never offloads a stream.</summary>
    [PreserveSig] int IsOffloadCapable(int category, out int offloadCapable);

    /// <summary>
    /// Slot 16: sets the stream's category and options, such as RAW, from an <see cref="AudioClientPropertiesNative"/> at
    /// <paramref name="properties"/>. Called before the stream is opened. AUDCLNT_E_RAW_MODE_UNSUPPORTED (0x88890027) and
    /// E_INVALIDARG mean that the endpoint will not give unprocessed audio.
    /// </summary>
    [PreserveSig] int SetClientProperties(IntPtr properties);

    /// <summary>Slot 17. Declared to keep Windows' order; the capture asks for a fixed buffer length.</summary>
    [PreserveSig] int GetBufferSizeLimits(IntPtr format, int eventDriven, out long minimum, out long maximum);
}

/// <summary>
/// AudioClientProperties as Windows lays it out: 16 bytes, the size first, so that Windows knows the options are there
/// (the structure grew by them in Windows 8.1).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct AudioClientPropertiesNative
{
    /// <summary>The size of the structure in bytes: 16.</summary>
    public uint Size;

    /// <summary>Whether the stream is offloaded to the audio hardware: never, so 0.</summary>
    public int IsOffload;

    /// <summary>The stream's category (AUDIO_STREAM_CATEGORY), such as <see cref="AudioClient2.CategoryOther"/>.</summary>
    public int Category;

    /// <summary>The stream's options (AUDCLNT_STREAMOPTIONS), such as <see cref="AudioClient2.StreamOptionsRaw"/>.</summary>
    public int Options;

    /// <summary>The properties that ask for unprocessed audio: not offloaded, the Other category and the RAW option.</summary>
    public static AudioClientPropertiesNative Raw => new()
    {
        Size = (uint)Marshal.SizeOf<AudioClientPropertiesNative>(),
        IsOffload = 0,
        Category = AudioClient2.CategoryOther,
        Options = AudioClient2.StreamOptionsRaw
    };
}

/// <summary>The numbers IAudioClient2 is called with, and the one call ClassScribe makes through it.</summary>
internal static class AudioClient2
{
    /// <summary>IID_IAudioClient, the interface an endpoint's audio client is activated as; IAudioClient2 is asked of the same object.</summary>
    public static readonly Guid AudioClientId = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");

    /// <summary>AudioCategory_Other: a stream of no particular kind, which Windows does not treat as a call.</summary>
    public const int CategoryOther = 0;

    /// <summary>AUDCLNT_STREAMOPTIONS_RAW: the stream wants the audio as the endpoint captures it, without Windows' sound processing.</summary>
    public const int StreamOptionsRaw = 0x1;

    /// <summary>AUDCLNT_E_RAW_MODE_UNSUPPORTED: Windows' answer when the endpoint cannot give unprocessed audio.</summary>
    public const int RawModeUnsupported = unchecked((int)0x88890027);

    /// <summary>E_INVALIDARG, which Windows also answers for the RAW option on an endpoint that will not take it.</summary>
    public const int InvalidArgument = unchecked((int)0x80070057);

    /// <summary>
    /// Sets <paramref name="properties"/> on <paramref name="client"/>, laid out in unmanaged memory for the call and freed
    /// after, and returns Windows' result: 0 when they were taken.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static int SetClientProperties(IAudioClient2Native client, in AudioClientPropertiesNative properties)
    {
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<AudioClientPropertiesNative>());
        try
        {
            Marshal.StructureToPtr(properties, pointer, fDeleteOld: false);
            return client.SetClientProperties(pointer);
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }
}
