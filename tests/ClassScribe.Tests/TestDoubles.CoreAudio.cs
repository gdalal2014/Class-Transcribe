using System.Runtime.Versioning;
using ClassScribe.Audio.Windows;

namespace ClassScribe.Tests;

/// <summary>The HRESULTs the fakes of Windows' audio objects answer with.</summary>
internal static class FakeResults
{
    /// <summary>S_OK.</summary>
    public const int Ok = 0;

    /// <summary>E_NOTIMPL, for the methods the device check never calls.</summary>
    public const int NotImplemented = unchecked((int)0x80004001);

    /// <summary>E_NOINTERFACE, for an object a fake device cannot make.</summary>
    public const int NoInterface = unchecked((int)0x80004002);

    /// <summary>AUDCLNT_E_SERVICE_NOT_RUNNING: Windows' audio service is not running, as while it restarts.</summary>
    public const int ServiceNotRunning = unchecked((int)0x88890010);

    /// <summary>AUDCLNT_E_DEVICE_INVALIDATED: the device has gone, as when it is unplugged during a look.</summary>
    public const int DeviceInvalidated = unchecked((int)0x88890004);
}

/// <summary>
/// Keeps account of the objects the fakes of Windows hand out and of those the code under test releases, the way
/// Windows counts references: an object must be released once for each time it was handed out. The provider is
/// given <see cref="Release"/> as its way of releasing.
/// </summary>
internal sealed class FakeComLedger
{
    private readonly Dictionary<object, int> _held = new(ReferenceEqualityComparer.Instance);
    private readonly List<string> _overReleased = [];

    /// <summary>How many objects were handed out in all.</summary>
    public int HandedOut { get; private set; }

    /// <summary>What is handed out and not yet released, by type, such as "FakeMeter x2", in name order.</summary>
    public IReadOnlyList<string> Unreleased =>
    [
        .. _held.Where(pair => pair.Value > 0)
            .GroupBy(pair => pair.Key.GetType().Name, pair => pair.Value)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => $"{group.Key} x{group.Sum()}")
    ];

    /// <summary>The releases of objects that were not held: never handed out, or released once too often.</summary>
    public IReadOnlyList<string> OverReleased => _overReleased;

    /// <summary>Hands <paramref name="item"/> out through <paramref name="ledger"/>, or simply returns it when there is no ledger.</summary>
    public static T Track<T>(FakeComLedger? ledger, T item) where T : class => ledger?.HandOut(item) ?? item;

    /// <summary>Notes that <paramref name="item"/> was handed out once more, and returns it.</summary>
    public T HandOut<T>(T item) where T : class
    {
        _held[item] = _held.GetValueOrDefault(item) + 1;
        HandedOut++;
        return item;
    }

    /// <summary>Notes that <paramref name="item"/> was released once. Null is ignored, as the real release ignores it.</summary>
    public void Release(object? item)
    {
        if (item is null) return;

        if (_held.GetValueOrDefault(item) > 0)
            _held[item]--;
        else
            _overReleased.Add(item.GetType().Name);
    }
}

/// <summary>
/// Windows' device enumerator as a test plays it. It lists the active output devices it was given, and names
/// the default one; while <see cref="Failure"/> is set, it answers every call with that error instead, as an
/// enumerator Windows no longer serves would.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class FakeDeviceEnumerator(params FakeOutputDevice[] devices) : IMMDeviceEnumeratorRaw
{
    /// <summary>The identifier of the default output device, or null when there is none.</summary>
    public string? DefaultId { get; init; }

    /// <summary>When not 0, the HRESULT every call answers with.</summary>
    public int Failure { get; set; }

    /// <summary>The account of what it hands out, or null to keep none.</summary>
    public FakeComLedger? Ledger { get; init; }

    /// <inheritdoc />
    public int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollectionRaw collection)
    {
        collection = null!;
        if (Failure != 0) return Failure;

        collection = FakeComLedger.Track(Ledger, new FakeDeviceCollection(devices, Ledger));
        return FakeResults.Ok;
    }

    /// <inheritdoc />
    public int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDeviceRaw endpoint)
    {
        endpoint = null!;
        if (Failure != 0) return Failure;
        if (devices.FirstOrDefault(device => device.Id == DefaultId) is not { } found) return CoreAudio.NotFound;

        endpoint = FakeComLedger.Track(Ledger, found);
        return FakeResults.Ok;
    }

    /// <inheritdoc />
    public int GetDevice(string id, out IMMDeviceRaw device)
    {
        device = null!;
        if (Failure != 0) return Failure;
        if (devices.FirstOrDefault(candidate => candidate.Id == id) is not { } found) return CoreAudio.NotFound;

        device = FakeComLedger.Track(Ledger, found);
        return FakeResults.Ok;
    }
}

/// <summary>The devices one listing found, handed out through <paramref name="ledger"/>.</summary>
[SupportedOSPlatform("windows")]
internal sealed class FakeDeviceCollection(IReadOnlyList<FakeOutputDevice> devices, FakeComLedger? ledger) : IMMDeviceCollectionRaw
{
    /// <inheritdoc />
    public int GetCount(out int count)
    {
        count = devices.Count;
        return FakeResults.Ok;
    }

    /// <inheritdoc />
    public int Item(int index, out IMMDeviceRaw device)
    {
        device = FakeComLedger.Track(ledger, devices[index]);
        return FakeResults.Ok;
    }
}

/// <summary>
/// An output device as a test plays it: its identifier, how loud its level meter reads, and the audio sessions
/// apps have open on it.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class FakeOutputDevice(string id, float level = 0, params FakeAudioSession[] sessions) : IMMDeviceRaw
{
    /// <summary>The identifier Windows would give the device.</summary>
    public string Id { get; } = id;

    /// <summary>When not 0, the HRESULT its level meter answers with, as the meter of a device that has just gone would.</summary>
    public int MeterFailure { get; init; }

    /// <summary>When not 0, the HRESULT asking for its session manager answers with.</summary>
    public int SessionManagerFailure { get; init; }

    /// <summary>The account of the meters and session managers it hands out, or null to keep none.</summary>
    public FakeComLedger? Ledger { get; init; }

    /// <inheritdoc />
    public int Activate(ref Guid interfaceId, int context, IntPtr activationParams, out object instance)
    {
        instance = null!;
        if (interfaceId == typeof(IAudioMeterInformationRaw).GUID)
        {
            instance = FakeComLedger.Track(Ledger, new FakeMeter(level, MeterFailure));
            return FakeResults.Ok;
        }

        if (interfaceId == typeof(IAudioSessionManager2Raw).GUID)
        {
            if (SessionManagerFailure != 0) return SessionManagerFailure;

            instance = FakeComLedger.Track(Ledger, new FakeSessionManager(sessions, Ledger));
            return FakeResults.Ok;
        }

        return FakeResults.NoInterface;
    }

    /// <inheritdoc />
    public int OpenPropertyStore(int access, out IntPtr properties)
    {
        properties = IntPtr.Zero;
        return FakeResults.NotImplemented;
    }

    /// <inheritdoc />
    public int GetId(out string id)
    {
        id = Id;
        return FakeResults.Ok;
    }

    /// <inheritdoc />
    public int GetState(out int state)
    {
        state = CoreAudio.Active;
        return FakeResults.Ok;
    }
}

/// <summary>A level meter that reads <paramref name="level"/>, or fails with <paramref name="failure"/> when that is not 0.</summary>
internal sealed class FakeMeter(float level, int failure = 0) : IAudioMeterInformationRaw
{
    /// <inheritdoc />
    public int GetPeakValue(out float peak)
    {
        peak = failure == 0 ? level : 0;
        return failure == 0 ? FakeResults.Ok : failure;
    }
}

/// <summary>A device's session manager, which lists the sessions it was given, handing them out through <paramref name="ledger"/>.</summary>
internal sealed class FakeSessionManager(IReadOnlyList<FakeAudioSession> sessions, FakeComLedger? ledger) : IAudioSessionManager2Raw
{
    /// <inheritdoc />
    public int GetAudioSessionControl(IntPtr sessionGuid, int streamFlags, out IntPtr sessionControl)
    {
        sessionControl = IntPtr.Zero;
        return FakeResults.NotImplemented;
    }

    /// <inheritdoc />
    public int GetSimpleAudioVolume(IntPtr sessionGuid, int streamFlags, out IntPtr audioVolume)
    {
        audioVolume = IntPtr.Zero;
        return FakeResults.NotImplemented;
    }

    /// <inheritdoc />
    public int GetSessionEnumerator(out IAudioSessionEnumeratorRaw list)
    {
        list = FakeComLedger.Track(ledger, new FakeSessionList(sessions, ledger));
        return FakeResults.Ok;
    }
}

/// <summary>The sessions one listing found, handed out through <paramref name="ledger"/>.</summary>
internal sealed class FakeSessionList(IReadOnlyList<FakeAudioSession> sessions, FakeComLedger? ledger) : IAudioSessionEnumeratorRaw
{
    /// <inheritdoc />
    public int GetCount(out int count)
    {
        count = sessions.Count;
        return FakeResults.Ok;
    }

    /// <inheritdoc />
    public int GetSession(int index, out object session)
    {
        session = FakeComLedger.Track(ledger, sessions[index]);
        return FakeResults.Ok;
    }
}

/// <summary>
/// One app's sound on a device, as a test plays it: the process that has it open, how loud its meter reads, and
/// its state (0 inactive, 1 active, 2 expired). Its meter is the session itself, as with Windows' sessions.
/// </summary>
internal sealed class FakeAudioSession(int processId, float level, int state = 1) : IAudioSessionControl2Raw, IAudioMeterInformationRaw
{
    /// <summary>The state of a session whose app has closed its sound.</summary>
    public const int Expired = 2;

    /// <summary>When not 0, the HRESULT asking for its state answers with, as a session being torn down during the look may.</summary>
    public int StateFailure { get; init; }

    /// <summary>When not 0, the HRESULT asking for its process answers with.</summary>
    public int ProcessIdFailure { get; init; }

    /// <inheritdoc />
    public int GetState(out int value)
    {
        value = StateFailure == 0 ? state : 0;
        return StateFailure == 0 ? FakeResults.Ok : StateFailure;
    }

    /// <inheritdoc />
    public int GetProcessId(out uint value)
    {
        value = ProcessIdFailure == 0 ? (uint)processId : 0;
        return ProcessIdFailure == 0 ? FakeResults.Ok : ProcessIdFailure;
    }

    /// <inheritdoc />
    public int GetPeakValue(out float peak)
    {
        peak = level;
        return FakeResults.Ok;
    }

    /// <inheritdoc />
    public int GetDisplayName(out IntPtr name)
    {
        name = IntPtr.Zero;
        return FakeResults.NotImplemented;
    }

    /// <inheritdoc />
    public int SetDisplayName(IntPtr name, IntPtr eventContext) => FakeResults.NotImplemented;

    /// <inheritdoc />
    public int GetIconPath(out IntPtr path)
    {
        path = IntPtr.Zero;
        return FakeResults.NotImplemented;
    }

    /// <inheritdoc />
    public int SetIconPath(IntPtr path, IntPtr eventContext) => FakeResults.NotImplemented;

    /// <inheritdoc />
    public int GetGroupingParam(out Guid grouping)
    {
        grouping = Guid.Empty;
        return FakeResults.NotImplemented;
    }

    /// <inheritdoc />
    public int SetGroupingParam(IntPtr grouping, IntPtr eventContext) => FakeResults.NotImplemented;

    /// <inheritdoc />
    public int RegisterAudioSessionNotification(IntPtr client) => throw new AssertionException("The device check registered a session notification.");

    /// <inheritdoc />
    public int UnregisterAudioSessionNotification(IntPtr client) => FakeResults.NotImplemented;

    /// <inheritdoc />
    public int GetSessionIdentifier(out IntPtr id)
    {
        id = IntPtr.Zero;
        return FakeResults.NotImplemented;
    }

    /// <inheritdoc />
    public int GetSessionInstanceIdentifier(out IntPtr id)
    {
        id = IntPtr.Zero;
        return FakeResults.NotImplemented;
    }
}
