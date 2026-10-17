using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ClassScribe.Audio.Windows;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace ClassScribe.Tests;

/// <summary>
/// Windows' audio client as a test plays it, behind NAudio's public declaration, so that NAudio's <see cref="AudioClient"/>
/// wraps it as it wraps the real one and the capture under test never touches a device. Its mix format is given; it
/// records how the stream was opened, started and stopped, and can refuse to open. A capture made around it is never
/// disposed, because disposing hands the stand-in to COM to release.
/// </summary>
[SupportedOSPlatform("windows")]
internal class FakeAudioClient(WaveFormat mixFormat) : IAudioClient
{
    /// <summary>The HRESULT opening the stream answers with; 0 opens it.</summary>
    public int InitializeResult { get; init; }

    /// <summary>How the stream was asked to share the device, once it was opened.</summary>
    public AudioClientShareMode? ShareMode { get; private set; }

    /// <summary>The stream flags the stream was opened with.</summary>
    public AudioClientStreamFlags? StreamFlags { get; private set; }

    /// <summary>The buffer length asked for, in 100-nanosecond units.</summary>
    public long BufferDuration { get; private set; }

    /// <summary>The period asked for, in 100-nanosecond units.</summary>
    public long Periodicity { get; private set; }

    /// <summary>The format the stream was opened with.</summary>
    public WaveFormat? StreamFormat { get; private set; }

    /// <summary>The event handle given for packets, or null when none was.</summary>
    public IntPtr? EventHandle { get; private set; }

    /// <summary>How many times the stream was asked to open.</summary>
    public int InitializeCalls { get; private set; }

    /// <summary>How many times the stream was started.</summary>
    public int StartCalls { get; private set; }

    /// <summary>How many times the stream was stopped.</summary>
    public int StopCalls { get; private set; }

    /// <summary>The mix format the client reports.</summary>
    public WaveFormat MixFormat { get; } = mixFormat;

    /// <inheritdoc />
    public int Initialize(AudioClientShareMode shareMode, AudioClientStreamFlags streamFlags, long hnsBufferDuration, long hnsPeriodicity, WaveFormat pFormat, ref Guid audioSessionGuid)
    {
        InitializeCalls++;
        ShareMode = shareMode;
        StreamFlags = streamFlags;
        BufferDuration = hnsBufferDuration;
        Periodicity = hnsPeriodicity;
        StreamFormat = pFormat;
        return InitializeResult;
    }

    /// <inheritdoc />
    public int GetBufferSize(out uint bufferSize)
    {
        bufferSize = (uint)MixFormat.SampleRate;
        return FakeResults.Ok;
    }

    /// <inheritdoc />
    public long GetStreamLatency() => 0;

    /// <inheritdoc />
    public int GetCurrentPadding(out int currentPadding)
    {
        currentPadding = 0;
        return FakeResults.Ok;
    }

    /// <inheritdoc />
    public int IsFormatSupported(AudioClientShareMode shareMode, WaveFormat pFormat, IntPtr closestMatchFormat) => FakeResults.Ok;

    /// <summary>Hands the mix format over in task memory, as Windows does, for NAudio to read and free.</summary>
    public int GetMixFormat(out IntPtr deviceFormatPointer)
    {
        deviceFormatPointer = Marshal.AllocCoTaskMem(Marshal.SizeOf(MixFormat));
        Marshal.StructureToPtr(MixFormat, deviceFormatPointer, fDeleteOld: false);
        return FakeResults.Ok;
    }

    /// <inheritdoc />
    public int GetDevicePeriod(out long defaultDevicePeriod, out long minimumDevicePeriod)
    {
        defaultDevicePeriod = 100_000;
        minimumDevicePeriod = 30_000;
        return FakeResults.Ok;
    }

    /// <inheritdoc />
    public int Start()
    {
        StartCalls++;
        return FakeResults.Ok;
    }

    /// <inheritdoc />
    public int Stop()
    {
        StopCalls++;
        return FakeResults.Ok;
    }

    /// <inheritdoc />
    public int Reset() => FakeResults.Ok;

    /// <inheritdoc />
    public int SetEventHandle(IntPtr eventHandle)
    {
        EventHandle = eventHandle;
        return FakeResults.Ok;
    }

    /// <summary>Never answered: a test gives the capture its packets directly, so the capture must not ask NAudio for Windows' capture client.</summary>
    public int GetService(Guid interfaceId, out object interfacePointer) =>
        throw new AssertionException("The capture asked NAudio for a service of the audio client; a test gives it its packets directly.");
}

/// <summary>
/// An audio client that also offers IAudioClient2, as every client does on Windows 8 and later, through ClassScribe's own
/// declaration. It records the properties set on it and answers with the result it was given. The IAudioClient methods
/// of that declaration are never called, because the capture reaches them through NAudio: here they fail the test.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class FakeAudioClient2(WaveFormat mixFormat) : FakeAudioClient(mixFormat), IAudioClient2Native
{
    /// <summary>The HRESULT setting the client's properties answers with; 0 accepts them.</summary>
    public int ClientPropertiesResult { get; init; }

    /// <summary>The properties set on the client, or null while none were.</summary>
    public AudioClientPropertiesNative? ClientProperties { get; private set; }

    /// <summary>How many times the client's properties were set.</summary>
    public int SetClientPropertiesCalls { get; private set; }

    /// <summary>Not through this declaration: the capture opens the stream through NAudio.</summary>
    public int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid) => throw ThroughTheWrongDeclaration();

    /// <summary>Not through this declaration.</summary>
    public int GetStreamLatency(out long latency) => throw ThroughTheWrongDeclaration();

    /// <summary>Not through this declaration.</summary>
    public int GetCurrentPadding(out uint frames) => throw ThroughTheWrongDeclaration();

    /// <summary>Not through this declaration.</summary>
    public int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch) => throw ThroughTheWrongDeclaration();

    /// <summary>Not through this declaration.</summary>
    public int GetService(ref Guid iid, out object service) => throw ThroughTheWrongDeclaration();

    /// <summary>Not called: ClassScribe never offloads a stream.</summary>
    public int IsOffloadCapable(int category, out int offloadCapable) => throw ThroughTheWrongDeclaration();

    /// <summary>Records the properties the pointer holds and answers with <see cref="ClientPropertiesResult"/>.</summary>
    public int SetClientProperties(IntPtr properties)
    {
        SetClientPropertiesCalls++;
        ClientProperties = Marshal.PtrToStructure<AudioClientPropertiesNative>(properties);
        return ClientPropertiesResult;
    }

    /// <summary>Not called: the capture asks for a fixed buffer length.</summary>
    public int GetBufferSizeLimits(IntPtr format, int eventDriven, out long minimum, out long maximum) => throw ThroughTheWrongDeclaration();

    /// <summary>The failure for a call that must go through NAudio's declaration, not ClassScribe's.</summary>
    private static AssertionException ThroughTheWrongDeclaration() =>
        new("An IAudioClient method was called through ClassScribe's IAudioClient2 declaration; those go through NAudio.");
}

/// <summary>
/// The packets a capture thread reads, as a test scripts them: each with its audio, its frame count, the stamp Windows
/// would give its first frame and Windows' flags, handed out in order from unmanaged memory the way Windows' capture
/// client hands them out. It notes the thread that reads, counts the frames given back, and can fail as a device that
/// went away would.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class FakePacketSource : IPacketSource, IDisposable
{
    private readonly Lock _gate = new();
    private readonly Queue<(byte[] Audio, int Frames, long Qpc, AudioClientBufferFlags Flags)> _packets = new();
    private IntPtr _buffer;
    private int _capacity;

    /// <summary>The name of the thread that first asked for a packet, or null while none has.</summary>
    public string? ThreadName { get; private set; }

    /// <summary>How many frames the capture gave back to Windows.</summary>
    public int FramesReleased { get; private set; }

    /// <summary>When set, every ask for a packet throws it, as a capture client of a device that went away would.</summary>
    public Exception? Failure { get; set; }

    /// <summary>Queues a packet of <paramref name="audio"/>, <paramref name="frames"/> long, stamped <paramref name="qpc100ns"/> and flagged <paramref name="flags"/>.</summary>
    public void Add(byte[] audio, int frames, long qpc100ns, AudioClientBufferFlags flags = AudioClientBufferFlags.None)
    {
        lock (_gate) _packets.Enqueue((audio, frames, qpc100ns, flags));
    }

    /// <inheritdoc />
    public int NextPacketFrames()
    {
        lock (_gate)
        {
            ThreadName ??= Thread.CurrentThread.Name;
            if (Failure is { } failure) throw failure;
            return _packets.TryPeek(out var next) ? next.Frames : 0;
        }
    }

    /// <inheritdoc />
    public IntPtr GetBuffer(out int frames, out AudioClientBufferFlags flags, out long qpc100ns)
    {
        lock (_gate)
        {
            var (audio, packetFrames, qpc, packetFlags) = _packets.Dequeue();
            if (_capacity < audio.Length)
            {
                if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
                _buffer = Marshal.AllocHGlobal(audio.Length);
                _capacity = audio.Length;
            }

            Marshal.Copy(audio, 0, _buffer, audio.Length);
            frames = packetFrames;
            flags = packetFlags;
            qpc100ns = qpc;
            return _buffer;
        }
    }

    /// <inheritdoc />
    public void ReleaseBuffer(int frames)
    {
        lock (_gate) FramesReleased += frames;
    }

    /// <summary>Frees the unmanaged memory the packets were handed out in.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
            _buffer = IntPtr.Zero;
            _capacity = 0;
        }
    }
}

/// <summary>
/// Activates a microphone's audio client as a test plays it: <paramref name="activate"/> answers each endpoint with a
/// fake client and whether the endpoint offers unprocessed audio, or throws as Windows would for an endpoint that is gone.
/// </summary>
internal sealed class FakeAudioClientActivator(Func<string, ActivatedClient> activate) : IAudioClientActivator
{
    /// <summary>The endpoints activated, in order.</summary>
    public List<string> Activated { get; } = [];

    /// <inheritdoc />
    public ActivatedClient Activate(string endpointId)
    {
        Activated.Add(endpointId);
        return activate(endpointId);
    }
}
