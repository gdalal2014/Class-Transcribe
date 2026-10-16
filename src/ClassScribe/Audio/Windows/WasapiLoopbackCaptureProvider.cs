using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace ClassScribe.Audio.Windows;

/// <summary>
/// Lists the Windows output devices with how loud each is playing, and the apps with sound open on
/// them; creates loopback captures of a device or of an app; and reports when Windows changes
/// anything among the devices, for example when headphones are plugged in.
/// </summary>
/// <remarks>
/// The recording looks at the devices once a second for the whole class, so the two listings use
/// ClassScribe's own declarations (<see cref="CoreAudio"/>) and release every object Windows hands them as
/// soon as it has been used. Through NAudio each look left a registry key handle and some native memory
/// waiting for the garbage collector, which can take half an hour to come, and NAudio's session classes
/// have finalizers that can end the process; no session notification is registered here. NAudio still does
/// what happens once: the device notifications, the device names and the captures.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WasapiLoopbackCaptureProvider : ILoopbackCaptureProvider, IMMNotificationClient
{
    private static readonly Guid MeterId = typeof(IAudioMeterInformationRaw).GUID;
    private static readonly Guid SessionManagerId = typeof(IAudioSessionManager2Raw).GUID;

    /// <summary>NAudio's enumerator: device notifications, device names and the devices to capture.</summary>
    private readonly MMDeviceEnumerator _enumerator = new();

    private readonly ILogger<WasapiLoopbackCaptureProvider> _logger;
    private readonly Func<IMMDeviceEnumeratorRaw> _createListingEnumerator;
    private readonly Func<string, string> _readDeviceName;
    private readonly Func<int, string?> _readProcessName;

    /// <summary>Releases an object Windows handed out: <see cref="CoreAudio.Release"/>, or a test's account of what was released.</summary>
    private readonly Action<object?> _release;

    /// <summary>The name of this process, ClassScribe for the app: the processes so named are ClassScribe's own.</summary>
    private readonly string? _ownName;

    private readonly Lock _gate = new();

    /// <summary>
    /// Held while a listing runs: one listing at a time makes and uses the listings' enumerator, and Dispose
    /// releases it only while no listing holds it.
    /// </summary>
    private readonly Lock _listingGate = new();

    /// <summary>Device names by identifier. Windows takes up to half a second to give a Bluetooth device's name, so each is read once.</summary>
    private readonly Dictionary<string, string> _names = [];

    /// <summary>
    /// The enumerator the listings use: made at the first listing, or at the next one when Windows would not make
    /// it yet, and kept until Dispose, also through listings that fail. Windows keeps a single enumerator per
    /// process and holds on to it itself, so releasing it and making it again would only give the same object
    /// back. A look that fails, after Windows' audio service restarted, say, can only wait for Windows' own
    /// enumerator to answer again.
    /// </summary>
    private IMMDeviceEnumeratorRaw? _listingEnumerator;

    private GCHandle _keepAlive;
    private bool _registered;
    private volatile bool _disposed;

    /// <summary>
    /// Registers for device notifications from Windows. When Windows refuses, the provider works without
    /// them: the recording's regular device check notices every change too, only a moment later.
    /// </summary>
    public WasapiLoopbackCaptureProvider(ILogger<WasapiLoopbackCaptureProvider> logger)
        : this(logger, CoreAudio.CreateEnumerator)
    {
    }

    /// <summary>
    /// Creates the provider with the listings' enumerator made by <paramref name="createListingEnumerator"/> at the
    /// first listing, and at each following listing until making it succeeds. Device and process names come from
    /// <paramref name="readDeviceName"/> and <paramref name="readProcessName"/>, and objects are released with
    /// <paramref name="release"/>, when given; otherwise Windows answers and <see cref="CoreAudio.Release"/>
    /// releases. Tests pass fakes, and a release that keeps account.
    /// </summary>
    internal WasapiLoopbackCaptureProvider(
        ILogger<WasapiLoopbackCaptureProvider> logger,
        Func<IMMDeviceEnumeratorRaw> createListingEnumerator,
        Func<string, string>? readDeviceName = null,
        Func<int, string?>? readProcessName = null,
        Action<object?>? release = null)
    {
        _logger = logger;
        _createListingEnumerator = createListingEnumerator;
        _readDeviceName = readDeviceName ?? ReadDeviceName;
        _readProcessName = readProcessName ?? ReadProcessName;
        _release = release ?? CoreAudio.Release;
        _ownName = _readProcessName(Environment.ProcessId);

        // Windows keeps a plain pointer to this object for its notifications and does nothing to keep
        // it alive. Were the object ever collected while registered, the next notification would take
        // the process down, so it roots itself until Dispose. (NAudio 3 does this itself, 2.2.1 does not.)
        _keepAlive = GCHandle.Alloc(this);
        try
        {
            // NAudio 2.2.1 turns a failed registration into a COMException; the number it returns is always 0.
            _enumerator.RegisterEndpointNotificationCallback(this);
            _registered = true;
        }
        catch (COMException ex)
        {
            // Deliberately not rethrown: notifications only make a change of device noticed sooner, and
            // the regular device check notices it without them. Nothing is registered, so nothing needs rooting.
            _keepAlive.Free();
            _logger.LogWarning(ex, "Windows did not accept the registration for device notifications (0x{Code:X8}). A change of output device is still noticed by the regular check", ex.HResult);
        }
        catch
        {
            _keepAlive.Free();
            throw;
        }
    }

    /// <inheritdoc />
    public event EventHandler? DevicesChanged;

    /// <inheritdoc />
    public IReadOnlyList<OutputDevice> GetOutputDevices() => RunListing(ListOutputDevices, "output devices");

    /// <inheritdoc />
    /// <remarks>ClassScribe's own processes, this one and any other ClassScribe, are left out: a recording's own capture shows up as a session.</remarks>
    public IReadOnlyList<PlayingApp> GetPlayingApps() => RunListing(ListPlayingApps, "apps with sound");

    /// <inheritdoc />
    public IAudioCapture CreateCapture(CaptureTarget target)
    {
        if (target.ProcessId is { } processId)
        {
            var client = ProcessLoopback.Activate(processId);
            try
            {
                return new WasapiLoopbackAudioCapture(client, target.Id, target.Name, _logger);
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        MMDevice device;
        try
        {
            device = _enumerator.GetDevice(target.Id);
        }
        catch (COMException ex)
        {
            throw new InvalidOperationException("The output device to record is no longer available.", ex);
        }

        try
        {
            return new WasapiLoopbackAudioCapture(device, NameOf(device.ID), _logger);
        }
        catch
        {
            device.Dispose();
            throw;
        }
    }

    /// <summary>Called by Windows on one of its own threads, so it only raises the event and returns.</summary>
    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (flow == DataFlow.Render && role == Role.Multimedia)
            DevicesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Called by Windows when a device is enabled, disabled, plugged or unplugged. Only raises the event.</summary>
    public void OnDeviceStateChanged(string deviceId, DeviceState newState) => DevicesChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>Called by Windows when a device appears. Only raises the event.</summary>
    public void OnDeviceAdded(string pwstrDeviceId) => DevicesChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>Called by Windows when a device is gone for good. Forgets its name and raises the event.</summary>
    public void OnDeviceRemoved(string deviceId)
    {
        lock (_gate) _names.Remove(deviceId);
        DevicesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Not used.</summary>
    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

    /// <summary>
    /// Stops listening for device notifications and releases both enumerators. This runs as the app exits, so a
    /// failure to unregister is logged rather than thrown, and the enumerators are released whatever happens.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var released = !_registered;
        try
        {
            if (_registered) _enumerator.UnregisterEndpointNotificationCallback(this);
            released = true;
        }
        catch (COMException ex)
        {
            // Deliberately not rethrown: the app is on its way out, and an exception here would cost its
            // exit code and its last messages. Windows may still hold its pointer, though, so this object
            // stays rooted: a notification that still comes must find it alive.
            _logger.LogDebug(ex, "Windows did not take back the registration for device notifications (0x{Code:X8})", ex.HResult);
        }
        finally
        {
            _enumerator.Dispose();
            if (released && _keepAlive.IsAllocated) _keepAlive.Free();
            ReleaseListingEnumerator();
        }
    }

    /// <summary>
    /// Runs one listing on the listings' enumerator, made first when there is none, and returns what it found;
    /// nothing once the provider is disposed. A listing that fails with a <see cref="COMException"/> is noted at
    /// Debug and rethrown, and the enumerator is kept for the next one. While Windows' audio service is down every
    /// look fails, once a second, and the recording warns about that itself. <paramref name="what"/> names the
    /// listing in the log.
    /// </summary>
    private IReadOnlyList<T> RunListing<T>(Func<IMMDeviceEnumeratorRaw, IReadOnlyList<T>> listing, string what)
    {
        try
        {
            lock (_listingGate)
            {
                if (_disposed) return [];

                try
                {
                    return listing(_listingEnumerator ??= _createListingEnumerator());
                }
                catch (COMException ex)
                {
                    _logger.LogDebug(ex, "Could not look at the {What} (0x{Code:X8})", what, ex.HResult);
                    throw;
                }
            }
        }
        finally
        {
            // Dispose does not wait for a listing that Windows holds up: such a listing releases the enumerator itself.
            if (_disposed) ReleaseListingEnumerator();
        }
    }

    /// <summary>
    /// Releases the listings' enumerator, unless a listing is using it right now: that listing releases it as it
    /// ends, so that disposing never waits for Windows.
    /// </summary>
    private void ReleaseListingEnumerator()
    {
        if (!_listingGate.TryEnter()) return;
        try
        {
            _release(_listingEnumerator);
            _listingEnumerator = null;
        }
        finally
        {
            _listingGate.Exit();
        }
    }

    /// <summary>The active output devices, each with its name, whether it is the default, and how loud it is playing.</summary>
    private List<OutputDevice> ListOutputDevices(IMMDeviceEnumeratorRaw enumerator)
    {
        var defaultId = DefaultDeviceId(enumerator);
        var devices = new List<OutputDevice>();
        ForEachActiveDevice(enumerator, (id, device) => devices.Add(new OutputDevice(id, NameOf(id), id == defaultId, ReadLevel(device))));
        return devices;
    }

    /// <summary>
    /// The apps with sound open on an active output device. An app with sound open on several devices, or several
    /// times on one, is listed once, at its loudest.
    /// </summary>
    private List<PlayingApp> ListPlayingApps(IMMDeviceEnumeratorRaw enumerator)
    {
        var apps = new Dictionary<int, PlayingApp>();

        // A browser has many sessions from a few processes, so each name is looked up once per listing; not for
        // longer, because Windows gives the number of a process that has ended to the next one.
        var processNames = new Dictionary<int, string?>();
        ForEachActiveDevice(enumerator, (id, device) =>
        {
            var deviceName = NameOf(id);
            ForEachSession(device, session => AddApp(session, deviceName, apps, processNames));
        });

        return [.. apps.Values];
    }

    /// <summary>Adds the app behind one audio session to <paramref name="apps"/>, unless the session is to be left out.</summary>
    private void AddApp(object sessionObject, string deviceName, Dictionary<int, PlayingApp> apps, Dictionary<int, string?> processNames)
    {
        var session = (IAudioSessionControl2Raw)sessionObject;
        var result = session.GetState(out var state);
        if (result < 0)
        {
            _logger.LogDebug("Left out an audio session whose state Windows did not give (0x{Code:X8})", result);
            return;
        }

        if (state == CoreAudio.SessionExpired) return;

        result = session.GetProcessId(out var id);
        if (result < 0)
        {
            _logger.LogDebug("Left out an audio session whose process Windows did not give (0x{Code:X8})", result);
            return;
        }

        // 0 is Windows' own sounds. This process is ClassScribe: a capture it runs shows up as a session too.
        var processId = (int)id;
        if (processId <= 0 || processId == Environment.ProcessId) return;

        if (!processNames.TryGetValue(processId, out var name)) processNames[processId] = name = _readProcessName(processId);
        if (name is null || string.Equals(name, _ownName, StringComparison.OrdinalIgnoreCase)) return;

        var level = ReadLevel(sessionObject);
        if (!apps.TryGetValue(processId, out var known) || level > known.Level)
            apps[processId] = new PlayingApp(processId, name, deviceName, level);
    }

    /// <summary>Identifier of the default output device, or null when there is none.</summary>
    private string? DefaultDeviceId(IMMDeviceEnumeratorRaw enumerator)
    {
        var result = enumerator.GetDefaultAudioEndpoint(CoreAudio.Render, CoreAudio.Multimedia, out var device);
        if (result == CoreAudio.NotFound)
        {
            // Windows answers "not found" when no output device is connected. That is an answer, not a failure.
            _logger.LogDebug("Windows reports no default output device");
            return null;
        }

        CoreAudio.Check(result);
        try
        {
            CoreAudio.Check(device.GetId(out var id));
            return id;
        }
        finally
        {
            _release(device);
        }
    }

    /// <summary>
    /// Calls <paramref name="visit"/> with the identifier and the object of each active output device. Each device
    /// is released after its visit, and the listing at the end.
    /// </summary>
    private void ForEachActiveDevice(IMMDeviceEnumeratorRaw enumerator, Action<string, IMMDeviceRaw> visit)
    {
        CoreAudio.Check(enumerator.EnumAudioEndpoints(CoreAudio.Render, CoreAudio.Active, out var devices));
        try
        {
            CoreAudio.Check(devices.GetCount(out var count));
            for (var i = 0; i < count; i++)
            {
                CoreAudio.Check(devices.Item(i, out var device));
                try
                {
                    CoreAudio.Check(device.GetId(out var id));
                    visit(id, device);
                }
                finally
                {
                    _release(device);
                }
            }
        }
        finally
        {
            _release(devices);
        }
    }

    /// <summary>
    /// Calls <paramref name="visit"/> with each audio session open on the device. Each session is released after its
    /// visit, and the session listing and the session manager at the end.
    /// </summary>
    private void ForEachSession(IMMDeviceRaw device, Action<object> visit)
    {
        var managerId = SessionManagerId;
        CoreAudio.Check(device.Activate(ref managerId, CoreAudio.AllContexts, IntPtr.Zero, out var manager));
        try
        {
            CoreAudio.Check(((IAudioSessionManager2Raw)manager).GetSessionEnumerator(out var sessions));
            try
            {
                CoreAudio.Check(sessions.GetCount(out var count));
                for (var i = 0; i < count; i++)
                {
                    CoreAudio.Check(sessions.GetSession(i, out var session));
                    try
                    {
                        visit(session);
                    }
                    finally
                    {
                        _release(session);
                    }
                }
            }
            finally
            {
                _release(sessions);
            }
        }
        finally
        {
            _release(manager);
        }
    }

    /// <summary>The device's name, read from Windows the first time and remembered.</summary>
    private string NameOf(string deviceId)
    {
        lock (_gate)
        {
            if (_names.TryGetValue(deviceId, out var known)) return known;
        }

        var name = _readDeviceName(deviceId);
        lock (_gate) _names[deviceId] = name;
        return name;
    }

    /// <summary>Reads the device's name from Windows, through NAudio. It happens once per device.</summary>
    private string ReadDeviceName(string deviceId)
    {
        using var device = _enumerator.GetDevice(deviceId);
        return device.FriendlyName;
    }

    /// <summary>The name of the process, such as chrome, or null when it has already ended.</summary>
    private static string? ReadProcessName(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // Deliberately not rethrown: a process that ended between the listing and this look-up has no sound
            // to record. The look-up says so with ArgumentException, or with InvalidOperationException when the
            // process ends between finding it and reading its name.
            return null;
        }
    }

    /// <summary>How loud the device is playing right now, as its level meter reports it.</summary>
    private float ReadLevel(IMMDeviceRaw device)
    {
        try
        {
            var meterId = MeterId;
            CoreAudio.Check(device.Activate(ref meterId, CoreAudio.AllContexts, IntPtr.Zero, out var meter));
            try
            {
                return PeakOf((IAudioMeterInformationRaw)meter);
            }
            finally
            {
                _release(meter);
            }
        }
        catch (COMException ex)
        {
            // Deliberately not rethrown: a device that went away between the listing and this read, or
            // one without a meter, is simply not heard playing; the listing must not fail for it.
            _logger.LogDebug(ex, "Could not read the level of an output device");
            return 0;
        }
    }

    /// <summary>How loud the app's sound is right now, as the level meter of its session reports it.</summary>
    private float ReadLevel(object session)
    {
        try
        {
            return PeakOf((IAudioMeterInformationRaw)session);
        }
        catch (COMException ex)
        {
            // Deliberately not rethrown: a session that closed between the listing and this read is simply not heard playing.
            _logger.LogDebug(ex, "Could not read the level of an app's sound");
            return 0;
        }
    }

    /// <summary>The peak the meter reports right now, from 0 to 1.</summary>
    private static float PeakOf(IAudioMeterInformationRaw meter)
    {
        CoreAudio.Check(meter.GetPeakValue(out var peak));
        return peak;
    }
}
