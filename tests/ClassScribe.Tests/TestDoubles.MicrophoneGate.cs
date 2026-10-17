using System.Runtime.Versioning;
using ClassScribe.Audio.Microphones;
using ClassScribe.Audio.Windows;
using ClassScribe.Desktop;

namespace ClassScribe.Tests;

/// <summary>
/// This laptop's microphones and the processes that open them, as the mic-design and MicWatch listings recorded them:
/// the built-in array (Windows' default for most apps), the OBSBOT (a USB microphone), Chrome's browser process and the
/// chrome.exe utility process that holds its capture sessions, a transcription app, ClassScribe's own process, and
/// Windows' system sounds.
/// </summary>
internal static class Laptop
{
    /// <summary>The built-in microphone array's endpoint id.</summary>
    public const string ArrayId = "{0.0.1.00000000}.{6f1c3a1e-3b4e-4c4d-9c2a-array}";

    /// <summary>The built-in microphone array's name.</summary>
    public const string ArrayName = "Microphone Array (Intel® Smart Sound Technology for Digital Microphones)";

    /// <summary>The OBSBOT's endpoint id.</summary>
    public const string ObsbotId = "{0.0.1.00000000}.{8e2d5b7c-1f6a-4a9b-b3c4-obsbot}";

    /// <summary>The OBSBOT's name.</summary>
    public const string ObsbotName = "Microphone (OBSBOT Meet Flip Microphone)";

    /// <summary>Chrome's browser process, which the loopback records and which holds no capture session.</summary>
    public const int ChromeBrowser = 10532;

    /// <summary>Chrome's audio service, a chrome.exe utility process, which holds Chrome's capture sessions.</summary>
    public const int ChromeUtility = 24800;

    /// <summary>A chromedriver process, whose name contains chrome.</summary>
    public const int ChromeDriver = 5555;

    /// <summary>A transcription app (vibe.exe), which held the microphone for two hours beside Chrome on 11 October.</summary>
    public const int Vibe = 4242;

    /// <summary>ClassScribe's own process: the recorder, whose capture holds the microphone for the whole class.</summary>
    public const int OwnProcess = 777;

    /// <summary>Another process of ClassScribe's program, such as a window beside the recorder.</summary>
    public const int OtherClassScribe = 778;

    /// <summary>The names of the processes above, as <see cref="IProcessNames"/> gives them.</summary>
    public static Dictionary<int, ProcessImage> Names => new()
    {
        [ChromeBrowser] = new("chrome", @"C:\Program Files\Google\Chrome\Application\chrome.exe"),
        [ChromeUtility] = new("chrome", @"C:\Program Files\Google\Chrome\Application\chrome.exe"),
        [ChromeDriver] = new("chromedriver", @"C:\Tools\chromedriver.exe"),
        [Vibe] = new("vibe", @"C:\Users\gdala\AppData\Local\Programs\vibe\vibe.exe"),
        [OwnProcess] = new("ClassScribe", @"C:\Users\gdala\AppData\Local\Programs\ClassScribe\ClassScribe.exe"),
        [OtherClassScribe] = new("ClassScribe", @"C:\Users\gdala\AppData\Local\Programs\ClassScribe\ClassScribe.exe")
    };

    /// <summary>A session of <paramref name="processId"/> on the microphone <paramref name="endpointId"/>, named as <see cref="Names"/> names the process.</summary>
    public static CaptureSession Session(string endpointId, int processId, CaptureSessionState state = CaptureSessionState.Active, bool? muted = false) =>
        new($"{endpointId}|{processId}|{state}", endpointId, processId, Names.GetValueOrDefault(processId)?.Name, SystemSounds: false, state, muted);

    /// <summary>Windows' system sounds session on the microphone <paramref name="endpointId"/>: no process, and flagged as Windows' own.</summary>
    public static CaptureSession SystemSounds(string endpointId) =>
        new($"{endpointId}|system", endpointId, 0, null, SystemSounds: true, CaptureSessionState.Active, null);

    /// <summary>A listing of the microphone <paramref name="endpointId"/> with <paramref name="sessions"/>, read completely, not muted, at 80 percent.</summary>
    public static MicrophoneSessionListing Listing(string endpointId, params CaptureSession[] sessions) =>
        new(endpointId, endpointId == ArrayId ? ArrayName : ObsbotName, endpointId == ArrayId ? MicrophoneRoles.Console | MicrophoneRoles.Multimedia : MicrophoneRoles.Communications,
            MutedInWindows: false, VolumePercent: 80, sessions, Complete: true, Problem: null);
}

/// <summary>
/// The sessions reader as a test plays it: it answers with the listings the test set, counts the reads, and can fail
/// in the way a reader never should, to show what the watch does then.
/// </summary>
internal sealed class FakeMicrophoneSessions : IMicrophoneSessions
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, MicrophoneSessionListing> _listings = [];

    /// <summary>How many times one microphone was read, by endpoint id.</summary>
    public Dictionary<string, int> Reads { get; } = [];

    /// <summary>How many times every microphone was read.</summary>
    public int ReadAlls { get; private set; }

    /// <summary>When set, every read throws it: a failure the reader does not expect, which must end the watch and nothing else.</summary>
    public Exception? Failure { get; set; }

    /// <summary>When set, reading every microphone answers with no listings and this reason, as the reader does while Windows' audio service restarts.</summary>
    public string? ListingProblem { get; set; }

    /// <summary>Sets what the microphone <paramref name="listing"/> describes reads as, from now on.</summary>
    public void Set(MicrophoneSessionListing listing)
    {
        lock (_gate) _listings[listing.EndpointId] = listing;
    }

    /// <summary>Sets the sessions on the microphone <paramref name="endpointId"/>, keeping the rest of its listing.</summary>
    public void SetSessions(string endpointId, params CaptureSession[] sessions)
    {
        lock (_gate) _listings[endpointId] = (_listings.GetValueOrDefault(endpointId) ?? Laptop.Listing(endpointId)) with { Sessions = sessions };
    }

    /// <summary>Removes the microphone <paramref name="endpointId"/>, as unplugging it would.</summary>
    public void Remove(string endpointId)
    {
        lock (_gate) _listings.Remove(endpointId);
    }

    /// <inheritdoc />
    public MicrophoneSessionListing Read(string endpointId)
    {
        lock (_gate)
        {
            if (Failure is { } failure) throw failure;
            Reads[endpointId] = Reads.GetValueOrDefault(endpointId) + 1;
            return _listings.GetValueOrDefault(endpointId)
                ?? new MicrophoneSessionListing(endpointId, endpointId, MicrophoneRoles.None, null, null, [], Complete: false, Problem: "the microphone is not connected");
        }
    }

    /// <inheritdoc />
    public MicrophoneListings ReadAll()
    {
        lock (_gate)
        {
            if (Failure is { } failure) throw failure;
            ReadAlls++;
            return ListingProblem is { } problem ? new MicrophoneListings([], problem) : new MicrophoneListings([.. _listings.Values], null);
        }
    }
}

/// <summary>Windows' microphone usage record as a test plays it: the entries the test set, or a record that cannot be read.</summary>
internal sealed class FakeMicrophoneUsageRecord : IMicrophoneUsageRecord
{
    private readonly Lock _gate = new();

    /// <summary>The apps' entries.</summary>
    public List<UsageEntry> Entries { get; } = [];

    /// <summary>When set, the record reads as unreadable, with this reason.</summary>
    public string? Problem { get; set; }

    /// <summary>How many times the record was read.</summary>
    public int Reads { get; private set; }

    /// <summary>A desktop app's entry: chrome.exe at its path, open now when <paramref name="inUse"/>, closed otherwise.</summary>
    public static UsageEntry Desktop(string exe, bool inUse) =>
        new($@"NonPackaged\C:#Program Files#{exe}", exe, $@"C:\Program Files\{exe}", StartFileTime: 133_000_000_000_000_000, StopFileTime: inUse ? 0 : 133_000_001_000_000_000);

    /// <summary>A Store app's entry by its package family name, open now when <paramref name="inUse"/>.</summary>
    public static UsageEntry Packaged(string packageFamilyName, bool inUse) =>
        new(packageFamilyName, packageFamilyName, null, StartFileTime: 133_000_000_000_000_000, StopFileTime: inUse ? 0 : 133_000_001_000_000_000);

    /// <summary>A desktop app's entry without any recorded use: no start and no stop time, as most of the entries on this laptop are.</summary>
    public static UsageEntry Unused(string exe) =>
        new($@"NonPackaged\C:#Program Files#{exe}", exe, $@"C:\Program Files\{exe}", StartFileTime: null, StopFileTime: null);

    /// <inheritdoc />
    public UsageRecordListing Read()
    {
        lock (_gate)
        {
            Reads++;
            return Problem is { } problem ? new UsageRecordListing(null, null, [], problem) : new UsageRecordListing("Allow", "Allow", [.. Entries], null);
        }
    }
}

/// <summary>Process names as a test sets them; a process the test did not name has ended.</summary>
internal sealed class FakeProcessNames : IProcessNames
{
    private readonly Lock _gate = new();

    /// <summary>The processes that run, by id.</summary>
    public Dictionary<int, ProcessImage> Names { get; } = Laptop.Names;

    /// <summary>How many times each process was looked up.</summary>
    public Dictionary<int, int> Lookups { get; } = [];

    /// <inheritdoc />
    public ProcessImage? Describe(int processId)
    {
        lock (_gate)
        {
            Lookups[processId] = Lookups.GetValueOrDefault(processId) + 1;
            return Names.GetValueOrDefault(processId);
        }
    }
}

/// <summary>ClassScribe's own processes as a test marks them: by id, and any process named ClassScribe.</summary>
internal sealed class FakeOwnProcesses(params int[] ownIds) : IOwnProcesses
{
    /// <inheritdoc />
    public bool IsOwn(int processId, string? processName) =>
        ownIds.Contains(processId) || string.Equals(processName, OwnProcesses.ProgramName, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Looks as a test scripts them for the gate, each stamped where the test says, on the clock the packets are stamped with.</summary>
internal static class Looks
{
    /// <summary>The app the scripted looks follow.</summary>
    public const string Chrome = "chrome";

    /// <summary>The app is taking sound and Windows is not muting: the gate is open.</summary>
    public static WatchLook Open(TimeSpan at, bool? usageInUse = null) =>
        new(at.Ticks, AppListening.Yes, MutedInWindows: false, [Chrome], null, null, Holds: true, UsageInUse: usageInUse, RecordedMicrophone: Laptop.ArrayName);

    /// <summary>The app holds the microphone but is not taking sound.</summary>
    public static WatchLook NotListening(TimeSpan at, bool? processAlive = true) =>
        new(at.Ticks, AppListening.No, MutedInWindows: false, [], null, null, Holds: true, ProcessAlive: processAlive, RecordedMicrophone: Laptop.ArrayName);

    /// <summary>The app has no session on the microphone.</summary>
    public static WatchLook NotOpened(TimeSpan at, bool? processAlive = null) =>
        new(at.Ticks, AppListening.No, MutedInWindows: false, [], null, null, Holds: false, ProcessAlive: processAlive, RecordedMicrophone: Laptop.ArrayName);

    /// <summary>The app is taking sound, but the microphone is muted in Windows.</summary>
    public static WatchLook MutedInWindows(TimeSpan at) =>
        new(at.Ticks, AppListening.Yes, MutedInWindows: true, [Chrome], null, null, Holds: true, RecordedMicrophone: Laptop.ArrayName);

    /// <summary>The app is taking sound from another microphone, and has no session on the recorded one.</summary>
    public static WatchLook OtherMicrophone(TimeSpan at, string other = Laptop.ObsbotName) =>
        new(at.Ticks, AppListening.No, MutedInWindows: false, [], other, null, Holds: false, RecordedMicrophone: Laptop.ArrayName);

    /// <summary>The sessions could not be read.</summary>
    public static WatchLook Unknown(TimeSpan at, string problem = "sessions not readable: The audio service is not running (0x88890010)") =>
        new(at.Ticks, AppListening.Unknown, MutedInWindows: null, [], null, problem, RecordedMicrophone: Laptop.ArrayName);
}

/// <summary>Feeds a gate scripted looks, moving the clock to each look's stamp first, as the looks of a real watch arrive as time passes.</summary>
internal sealed class ScriptedLooks(MicrophoneGate gate, ManualTimeProvider time)
{
    /// <summary>Moves the clock to the look's stamp, when it is behind, and records the look.</summary>
    public WatchLook Feed(WatchLook look)
    {
        var at = TimeSpan.FromTicks(look.Timestamp100ns);
        var elapsed = TimeSpan.FromTicks(time.GetTimestamp());
        if (at > elapsed) time.Advance(at - elapsed);
        gate.Record(look);
        return look;
    }

    /// <summary>Feeds one open look per interval from <paramref name="from"/> to <paramref name="to"/>, both included.</summary>
    public void OpenFrom(TimeSpan from, TimeSpan to)
    {
        for (var at = from; at <= to; at += ClassMicrophoneWatch.Interval) Feed(Looks.Open(at));
    }
}

/// <summary>
/// Windows' device enumerator as a test plays it for the microphones: it lists the active capture endpoints it was given
/// and names the defaults per role; while <see cref="Failure"/> is set, every call answers with that error instead.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class FakeCaptureEnumerator(params FakeMicrophoneEndpoint[] endpoints) : IMMDeviceEnumeratorRaw
{
    /// <summary>The endpoint ids that are Windows' default, by role (0 console, 1 multimedia, 2 communications).</summary>
    public Dictionary<int, string> Defaults { get; } = [];

    /// <summary>When not 0, the HRESULT every call answers with.</summary>
    public int Failure { get; set; }

    /// <summary>The account of what it hands out, or null to keep none.</summary>
    public FakeComLedger? Ledger { get; init; }

    /// <summary>The data flows asked for in listings and defaults: the reader must ask for capture (1) only.</summary>
    public List<int> DataFlowsAsked { get; } = [];

    /// <inheritdoc />
    public int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollectionRaw collection)
    {
        DataFlowsAsked.Add(dataFlow);
        collection = null!;
        if (Failure != 0) return Failure;

        collection = FakeComLedger.Track(Ledger, new FakeCaptureCollection(endpoints, Ledger));
        return FakeResults.Ok;
    }

    /// <inheritdoc />
    public int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDeviceRaw endpoint)
    {
        DataFlowsAsked.Add(dataFlow);
        endpoint = null!;
        if (Failure != 0) return Failure;
        if (!Defaults.TryGetValue(role, out var id) || endpoints.FirstOrDefault(candidate => candidate.Id == id) is not { } found) return CoreAudio.NotFound;

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
internal sealed class FakeCaptureCollection(IReadOnlyList<FakeMicrophoneEndpoint> endpoints, FakeComLedger? ledger) : IMMDeviceCollectionRaw
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
/// A microphone as a test plays it: its id and name, its mute and volume in Windows, and the sessions apps have open on
/// it. It keeps every interface id asked of it, so a test can check that no audio client was ever asked for.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class FakeMicrophoneEndpoint(string id, string name, params FakeCaptureSessionControl[] sessions) : IMMDeviceRaw
{
    /// <summary>IID_IAudioClient: the object through which a program opens a microphone, which the reader must never ask for.</summary>
    public static readonly Guid AudioClientId = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");

    /// <summary>The identifier Windows would give the microphone.</summary>
    public string Id { get; } = id;

    /// <summary>The name Windows would show for it.</summary>
    public string Name { get; } = name;

    /// <summary>Whether it is muted in Windows.</summary>
    public bool Muted { get; init; }

    /// <summary>Its input volume, from 0 to 1.</summary>
    public float Volume { get; init; } = 0.8f;

    /// <summary>When not 0, the HRESULT asking for its endpoint volume answers with.</summary>
    public int VolumeFailure { get; init; }

    /// <summary>When not 0, the HRESULT asking for its session manager answers with.</summary>
    public int SessionManagerFailure { get; init; }

    /// <summary>The account of the objects it hands out, or null to keep none.</summary>
    public FakeComLedger? Ledger { get; init; }

    /// <summary>Every interface id asked of <see cref="Activate"/>, in order.</summary>
    public List<Guid> Activated { get; } = [];

    /// <inheritdoc />
    public int Activate(ref Guid interfaceId, int context, IntPtr activationParams, out object instance)
    {
        Activated.Add(interfaceId);
        instance = null!;
        if (interfaceId == typeof(IAudioEndpointVolumeRaw).GUID)
        {
            if (VolumeFailure != 0) return VolumeFailure;

            instance = FakeComLedger.Track(Ledger, new FakeMicrophoneVolume(Muted, Volume));
            return FakeResults.Ok;
        }

        if (interfaceId == typeof(IAudioSessionManager2Raw).GUID)
        {
            if (SessionManagerFailure != 0) return SessionManagerFailure;

            instance = FakeComLedger.Track(Ledger, new FakeCaptureSessionManager(sessions, Ledger));
            return FakeResults.Ok;
        }

        if (interfaceId == AudioClientId) throw new AssertionException("The sessions reader asked for an audio client, which could open the microphone.");
        return FakeResults.NoInterface;
    }

    /// <inheritdoc />
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
        state = CoreAudio.Active;
        return FakeResults.Ok;
    }
}

/// <summary>A microphone's volume and mute in Windows, as a test plays them. Only the two getters answer; a setter called is a test failure.</summary>
internal sealed class FakeMicrophoneVolume(bool muted, float volume) : IAudioEndpointVolumeRaw
{
    /// <inheritdoc />
    public int RegisterControlChangeNotify(IntPtr notify) => throw new AssertionException("The sessions reader registered for volume notifications.");

    /// <inheritdoc />
    public int UnregisterControlChangeNotify(IntPtr notify) => FakeResults.NotImplemented;

    /// <inheritdoc />
    public int GetChannelCount(out uint count)
    {
        count = 0;
        return FakeResults.NotImplemented;
    }

    /// <inheritdoc />
    public int SetMasterVolumeLevel(float levelDecibels, IntPtr eventContext) => throw new AssertionException("The sessions reader set the volume.");

    /// <inheritdoc />
    public int SetMasterVolumeLevelScalar(float level, IntPtr eventContext) => throw new AssertionException("The sessions reader set the volume.");

    /// <inheritdoc />
    public int GetMasterVolumeLevel(out float levelDecibels)
    {
        levelDecibels = 0;
        return FakeResults.NotImplemented;
    }

    /// <inheritdoc />
    public int GetMasterVolumeLevelScalar(out float level)
    {
        level = volume;
        return FakeResults.Ok;
    }

    /// <inheritdoc />
    public int SetChannelVolumeLevel(uint channel, float levelDecibels, IntPtr eventContext) => throw new AssertionException("The sessions reader set the volume.");

    /// <inheritdoc />
    public int SetChannelVolumeLevelScalar(uint channel, float level, IntPtr eventContext) => throw new AssertionException("The sessions reader set the volume.");

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
    public int SetMute(int mute, IntPtr eventContext) => throw new AssertionException("The sessions reader set the mute.");

    /// <inheritdoc />
    public int GetMute(out int value)
    {
        value = muted ? 1 : 0;
        return FakeResults.Ok;
    }
}

/// <summary>A microphone's session manager, which lists the sessions it was given; asking it for a session of the caller's own, which could make one, is a test failure.</summary>
internal sealed class FakeCaptureSessionManager(IReadOnlyList<FakeCaptureSessionControl> sessions, FakeComLedger? ledger) : IAudioSessionManager2Raw
{
    /// <inheritdoc />
    public int GetAudioSessionControl(IntPtr sessionGuid, int streamFlags, out IntPtr sessionControl) =>
        throw new AssertionException("The sessions reader asked for a session of its own, which would make one.");

    /// <inheritdoc />
    public int GetSimpleAudioVolume(IntPtr sessionGuid, int streamFlags, out IntPtr audioVolume) =>
        throw new AssertionException("The sessions reader asked for a session volume of its own, which would make a session.");

    /// <inheritdoc />
    public int GetSessionEnumerator(out IAudioSessionEnumeratorRaw list)
    {
        list = FakeComLedger.Track(ledger, new FakeCaptureSessionList(sessions, ledger));
        return FakeResults.Ok;
    }
}

/// <summary>The sessions one listing found, handed out through <paramref name="ledger"/>.</summary>
internal sealed class FakeCaptureSessionList(IReadOnlyList<FakeCaptureSessionControl> sessions, FakeComLedger? ledger) : IAudioSessionEnumeratorRaw
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
/// One app's session on a microphone, as a test plays it: its instance identifier, the process that has it, its state,
/// whether it is Windows' system sounds session, and its own mute and volume, asked of the same object as with Windows.
/// </summary>
internal sealed class FakeCaptureSessionControl(string key, int processId, int state = 1, bool systemSounds = false, bool muted = false)
    : IAudioSessionControl2WholeRaw, ISimpleAudioVolumeRaw
{
    /// <summary>When not 0, the HRESULT asking for its state answers with, as a session being torn down during the look may.</summary>
    public int StateFailure { get; init; }

    /// <inheritdoc />
    public int GetState(out int value)
    {
        value = StateFailure == 0 ? state : 0;
        return StateFailure == 0 ? FakeResults.Ok : StateFailure;
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
    public int RegisterAudioSessionNotification(IntPtr client) => throw new AssertionException("The sessions reader registered a session notification.");

    /// <inheritdoc />
    public int UnregisterAudioSessionNotification(IntPtr client) => FakeResults.NotImplemented;

    /// <inheritdoc />
    public int GetSessionIdentifier(out IntPtr id)
    {
        id = IntPtr.Zero;
        return FakeResults.NotImplemented;
    }

    /// <inheritdoc />
    public int GetSessionInstanceIdentifier(out string id)
    {
        id = key;
        return FakeResults.Ok;
    }

    /// <inheritdoc />
    public int GetProcessId(out uint value)
    {
        value = (uint)processId;

        // AUDCLNT_S_NO_SINGLE_PROCESS, a success above zero, for a session that spans processes, as Windows' sounds do.
        return processId == 0 ? 0x0889000D : FakeResults.Ok;
    }

    /// <inheritdoc />
    public int IsSystemSoundsSession() => systemSounds ? 0 : 1;

    /// <inheritdoc />
    public int SetMasterVolume(float level, IntPtr eventContext) => throw new AssertionException("The sessions reader set a session's volume.");

    /// <inheritdoc />
    public int GetMasterVolume(out float level)
    {
        level = 1;
        return FakeResults.Ok;
    }

    /// <inheritdoc />
    public int SetMute(int mute, IntPtr eventContext) => throw new AssertionException("The sessions reader set a session's mute.");

    /// <inheritdoc />
    public int GetMute(out int value)
    {
        value = muted ? 1 : 0;
        return FakeResults.Ok;
    }
}
