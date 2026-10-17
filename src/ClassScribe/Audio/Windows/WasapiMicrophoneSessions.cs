using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ClassScribe.Audio.Microphones;
using ClassScribe.Desktop;
using Microsoft.Extensions.Logging;

namespace ClassScribe.Audio.Windows;

/// <summary>
/// Reads, from Windows' own lists, which apps have a microphone open and whether the microphone is muted in Windows: the
/// microphone watch's reader, and the microphone check's. It never asks Windows for an audio client, the object through
/// which a program opens a microphone, so it cannot open one; and every object Windows hands over is released in a
/// finally block as soon as it has been read, so the process's handles stay level over a class.
/// </summary>
/// <remarks>
/// The method order of the interfaces it calls (<see cref="IAudioEndpointVolumeRaw"/> and <see cref="ISimpleAudioVolumeRaw"/>,
/// declared in CoreAudioInterop.cs with the other Core Audio declarations, and <see cref="IAudioSessionControl2WholeRaw"/>,
/// declared below) was checked against NAudio 2.2.1's declarations and mingw-w64's
/// endpointvolume.idl, audioclient.idl and audiopolicy.idl, as MicWatch's were. Order matters because COM calls a method by
/// its position: a slip would turn a read into a write. The setters are declared only to keep that order, and nothing calls them.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WasapiMicrophoneSessions : IMicrophoneSessions, IDisposable
{
    private static readonly Guid EndpointVolumeId = typeof(IAudioEndpointVolumeRaw).GUID;
    private static readonly Guid SessionManagerId = typeof(IAudioSessionManager2Raw).GUID;

    /// <summary>Windows' roles for a default microphone, with the flag each sets.</summary>
    private static readonly (int Role, MicrophoneRoles Flag)[] Roles =
    [
        (CoreAudio.Console, MicrophoneRoles.Console),
        (CoreAudio.Multimedia, MicrophoneRoles.Multimedia),
        (CoreAudio.Communications, MicrophoneRoles.Communications)
    ];

    private readonly IProcessNames _names;
    private readonly ILogger<WasapiMicrophoneSessions> _logger;
    private readonly Func<IMMDeviceEnumeratorRaw> _createEnumerator;
    private readonly Func<IMMDeviceRaw, string?> _readName;
    private readonly Action<object?> _release;

    /// <summary>Held by one read at a time: the enumerator is made, used and released under it.</summary>
    private readonly Lock _gate = new();

    /// <summary>The enumerator the reads use: made at the first read, kept between reads, and made again after a read that failed.</summary>
    private IMMDeviceEnumeratorRaw? _enumerator;

    private bool _disposed;

    /// <summary>Makes the reader on Windows' own objects.</summary>
    public WasapiMicrophoneSessions(IProcessNames names, ILogger<WasapiMicrophoneSessions> logger)
        : this(names, logger, CoreAudio.CreateEnumerator)
    {
    }

    /// <summary>
    /// Makes the reader with the enumerator made by <paramref name="createEnumerator"/>, names read by
    /// <paramref name="readName"/> and objects released with <paramref name="release"/>, when given; otherwise Windows
    /// answers and <see cref="CoreAudio.Release"/> releases. Tests pass fakes, and a release that keeps account.
    /// </summary>
    internal WasapiMicrophoneSessions(IProcessNames names, ILogger<WasapiMicrophoneSessions> logger, Func<IMMDeviceEnumeratorRaw> createEnumerator,
        Func<IMMDeviceRaw, string?>? readName = null, Action<object?>? release = null)
    {
        _names = names;
        _logger = logger;
        _createEnumerator = createEnumerator;
        _readName = readName ?? ReadFriendlyName;
        _release = release ?? CoreAudio.Release;
    }

    /// <inheritdoc />
    public MicrophoneSessionListing Read(string endpointId)
    {
        lock (_gate)
        {
            if (_disposed) return Unread(endpointId, "the reader has been disposed");

            try
            {
                var enumerator = _enumerator ??= _createEnumerator();
                var result = enumerator.GetDevice(endpointId, out var device);
                if (result == CoreAudio.NotFound) return Unread(endpointId, "the microphone is not connected");

                CoreAudio.Check(result);
                try
                {
                    return ReadEndpoint(device, endpointId, ReadDefaults(enumerator), []);
                }
                finally
                {
                    _release(device);
                }
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                // Deliberately not rethrown: the watch looks four times a second and must keep looking. A look that fails is a
                // listing that says why, which the gate reads as nothing known, and the next look tries again with a new
                // enumerator, because a restart of Windows' audio service leaves the old one stale. Noted at Debug: four
                // warnings a second would drown the log, and the gate reports the sessions as unreadable itself.
                _logger.LogDebug(ex, "Could not read the sessions on the microphone {EndpointId} ({Reason})", endpointId, Describe(ex));
                DropEnumerator();
                return Unread(endpointId, Describe(ex));
            }
        }
    }

    /// <inheritdoc />
    public MicrophoneListings ReadAll()
    {
        lock (_gate)
        {
            if (_disposed) return new MicrophoneListings([], "the reader has been disposed");

            IMMDeviceCollectionRaw? collection = null;
            try
            {
                var enumerator = _enumerator ??= _createEnumerator();
                var defaults = ReadDefaults(enumerator);
                var processNames = new Dictionary<int, string?>();
                CoreAudio.Check(enumerator.EnumAudioEndpoints(CoreAudio.Capture, CoreAudio.Active, out collection));
                CoreAudio.Check(collection.GetCount(out var count));
                var listings = new List<MicrophoneSessionListing>(count);
                for (var index = 0; index < count; index++)
                {
                    CoreAudio.Check(collection.Item(index, out var device));
                    try
                    {
                        CoreAudio.Check(device.GetId(out var id));
                        listings.Add(ReadEndpoint(device, id, defaults, processNames));
                    }
                    finally
                    {
                        _release(device);
                    }
                }

                return new MicrophoneListings(listings, null);
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                // Deliberately not rethrown: as in Read, the look must go on; no listings, with the reason, say that nothing
                // could be read, which the microphone check reports as one ERROR line rather than as every microphone gone.
                _logger.LogDebug(ex, "Could not list the microphones and their sessions ({Reason})", Describe(ex));
                DropEnumerator();
                return new MicrophoneListings([], Describe(ex));
            }
            finally
            {
                _release(collection);
            }
        }
    }

    /// <summary>Releases the enumerator. A read that Windows holds up releases it itself when it ends.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            DropEnumerator();
        }
    }

    /// <summary>Releases the enumerator, so that the next read makes a new one. Call with the lock held.</summary>
    private void DropEnumerator()
    {
        _release(_enumerator);
        _enumerator = null;
    }

    /// <summary>A listing that says nothing could be read about the microphone, and why.</summary>
    private static MicrophoneSessionListing Unread(string endpointId, string problem) =>
        new(endpointId, endpointId, MicrophoneRoles.None, null, null, [], Complete: false, problem);

    /// <summary>The identifiers of Windows' default microphones, with the roles each is default for. A role with no default is left out.</summary>
    private Dictionary<string, MicrophoneRoles> ReadDefaults(IMMDeviceEnumeratorRaw enumerator)
    {
        var defaults = new Dictionary<string, MicrophoneRoles>();
        foreach (var (role, flag) in Roles)
        {
            var result = enumerator.GetDefaultAudioEndpoint(CoreAudio.Capture, role, out var device);
            if (result == CoreAudio.NotFound) continue;

            CoreAudio.Check(result);
            try
            {
                CoreAudio.Check(device.GetId(out var id));
                defaults[id] = defaults.GetValueOrDefault(id) | flag;
            }
            finally
            {
                _release(device);
            }
        }

        return defaults;
    }

    /// <summary>Reads one microphone: its name, the roles it is default for, its mute and volume, and its sessions.</summary>
    private MicrophoneSessionListing ReadEndpoint(IMMDeviceRaw device, string id, Dictionary<string, MicrophoneRoles> defaults, Dictionary<int, string?> processNames)
    {
        var problems = new List<string>();
        var name = ReadName(device, problems) ?? id;
        var (muted, volume) = ReadVolume(device, problems);
        var sessions = new List<CaptureSession>();
        var complete = ReadSessions(device, id, sessions, processNames, problems);
        return new MicrophoneSessionListing(id, name, defaults.GetValueOrDefault(id), muted, volume, sessions, complete,
            problems.Count == 0 ? null : string.Join("; ", problems));
    }

    /// <summary>The name Windows shows for the microphone, or null, with the reason noted, when it cannot be read.</summary>
    private string? ReadName(IMMDeviceRaw device, List<string> problems)
    {
        try
        {
            return _readName(device);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            // Deliberately not rethrown: the identifier stands in for the name, and the reason is in the listing.
            problems.Add("name not readable: " + Describe(ex));
            return null;
        }
    }

    /// <summary>Reads the name from the microphone's property store, opened for reading and released at once.</summary>
    private static string? ReadFriendlyName(IMMDeviceRaw device)
    {
        var store = CoreAudio.OpenPropertyStore(device);
        try
        {
            return CoreAudio.ReadString(store, PropertyKeys.FriendlyName);
        }
        finally
        {
            CoreAudio.Release(store);
        }
    }

    /// <summary>The microphone's mute and volume in Windows. Only the two getters are called.</summary>
    private (bool? Muted, int? VolumePercent) ReadVolume(IMMDeviceRaw device, List<string> problems)
    {
        object? activated = null;
        try
        {
            var interfaceId = EndpointVolumeId;
            CoreAudio.Check(device.Activate(ref interfaceId, CoreAudio.AllContexts, IntPtr.Zero, out activated));
            var volume = (IAudioEndpointVolumeRaw)activated;

            bool? muted = null;
            var result = volume.GetMute(out var mutedValue);
            if (result >= 0) muted = mutedValue != 0;
            else problems.Add($"endpoint mute not readable (0x{result:X8})");

            int? percent = null;
            result = volume.GetMasterVolumeLevelScalar(out var level);
            if (result >= 0) percent = (int)Math.Round(level * 100, MidpointRounding.AwayFromZero);
            else problems.Add($"endpoint volume not readable (0x{result:X8})");

            return (muted, percent);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            // Deliberately not rethrown: the mute reads as unknown, which the gate takes as not muted, and the sessions,
            // which are the safety, are still read; the reason is in the listing.
            problems.Add("endpoint volume not available: " + Describe(ex));
            return (null, null);
        }
        finally
        {
            _release(activated);
        }
    }

    /// <summary>
    /// Adds every session on the microphone to <paramref name="sessions"/>. Returns false when any could not be read, so
    /// that an app missing from the list is not taken for one that has let go of the microphone.
    /// </summary>
    private bool ReadSessions(IMMDeviceRaw device, string endpointId, List<CaptureSession> sessions, Dictionary<int, string?> processNames, List<string> problems)
    {
        object? activated = null;
        IAudioSessionEnumeratorRaw? list = null;
        try
        {
            var interfaceId = SessionManagerId;
            CoreAudio.Check(device.Activate(ref interfaceId, CoreAudio.AllContexts, IntPtr.Zero, out activated));
            CoreAudio.Check(((IAudioSessionManager2Raw)activated).GetSessionEnumerator(out list));
            CoreAudio.Check(list.GetCount(out var count));
            var complete = true;
            for (var index = 0; index < count; index++)
            {
                object? session = null;
                try
                {
                    CoreAudio.Check(list.GetSession(index, out session));
                    sessions.Add(ReadSession(session, endpointId, processNames));
                }
                catch (Exception ex) when (ex is COMException or InvalidCastException)
                {
                    // Deliberately not rethrown: a session being torn down during the look must not cost the others; the
                    // listing says it is incomplete, so the gate takes nothing for granted from it.
                    problems.Add($"session {index + 1} of {count} not readable: {Describe(ex)}");
                    complete = false;
                }
                finally
                {
                    _release(session);
                }
            }

            return complete;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            // Deliberately not rethrown: the microphone's mute is still worth having; the listing says the sessions could not be read.
            problems.Add("sessions not readable: " + Describe(ex));
            return false;
        }
        finally
        {
            _release(list);
            _release(activated);
        }
    }

    /// <summary>
    /// Reads one session: its instance identifier, state and process, whether it is Windows' system sounds, and through
    /// ISimpleAudioVolume, asked of the same object, its own mute. The process is named once per read, however many
    /// sessions it has. The caller releases the object.
    /// </summary>
    private CaptureSession ReadSession(object session, string endpointId, Dictionary<int, string?> processNames)
    {
        var control = (IAudioSessionControl2WholeRaw)session;
        CoreAudio.Check(control.GetSessionInstanceIdentifier(out var key));
        CoreAudio.Check(control.GetState(out var state));

        // A success above zero (AUDCLNT_S_NO_SINGLE_PROCESS) with a process id of 0 is a session that spans processes.
        CoreAudio.Check(control.GetProcessId(out var processId));
        var systemSounds = control.IsSystemSoundsSession() == 0;

        bool? muted = null;
        if (session is ISimpleAudioVolumeRaw simple && simple.GetMute(out var mutedValue) >= 0) muted = mutedValue != 0;

        var id = unchecked((int)processId);
        string? name = null;
        if (id > 0 && !processNames.TryGetValue(id, out name)) processNames[id] = name = _names.Describe(id)?.Name;

        return new CaptureSession(key, endpointId, id, name, systemSounds, (CaptureSessionState)state, muted);
    }

    /// <summary>A failure in words for a listing: Windows' words and the result code.</summary>
    private static string Describe(Exception exception) => $"{exception.Message.Trim()} (0x{exception.HResult:X8})";
}

/// <summary>
/// IAudioSessionControl2 as far as IsSystemSoundsSession: the same interface as <see cref="IAudioSessionControl2Raw"/>,
/// which stops at GetProcessId, declared whole for the microphone's sessions, which need the instance identifier and the
/// system sounds flag too. IAudioSessionControl's nine methods come first, because .NET does not lay out an inherited COM
/// interface's methods; only the state, the instance identifier, the process id and the system sounds flag are called.
/// </summary>
[ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionControl2WholeRaw
{
    /// <summary>Whether the session is inactive (0), active (1) or expired (2).</summary>
    [PreserveSig] int GetState(out int state);

    /// <summary>Declared to keep Windows' order; not called.</summary>
    [PreserveSig] int GetDisplayName(out IntPtr name);

    /// <summary>Declared to keep Windows' order; never called.</summary>
    [PreserveSig] int SetDisplayName(IntPtr name, IntPtr eventContext);

    /// <summary>Declared to keep Windows' order; not called.</summary>
    [PreserveSig] int GetIconPath(out IntPtr path);

    /// <summary>Declared to keep Windows' order; never called.</summary>
    [PreserveSig] int SetIconPath(IntPtr path, IntPtr eventContext);

    /// <summary>Declared to keep Windows' order; not called.</summary>
    [PreserveSig] int GetGroupingParam(out Guid grouping);

    /// <summary>Declared to keep Windows' order; never called.</summary>
    [PreserveSig] int SetGroupingParam(IntPtr grouping, IntPtr eventContext);

    /// <summary>Declared to keep Windows' order; never called, so no session notification is ever registered.</summary>
    [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);

    /// <summary>Declared to keep Windows' order; never called.</summary>
    [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);

    /// <summary>Declared to keep Windows' order; not called.</summary>
    [PreserveSig] int GetSessionIdentifier(out IntPtr id);

    /// <summary>An identifier unique to this session, by which it is followed from one look to the next.</summary>
    [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);

    /// <summary>The process that has the session; a success code above zero (AUDCLNT_S_NO_SINGLE_PROCESS) means the session spans several processes.</summary>
    [PreserveSig] int GetProcessId(out uint processId);

    /// <summary>S_OK (0) for Windows' system sounds session, S_FALSE (1) for any other.</summary>
    [PreserveSig] int IsSystemSoundsSession();
}
