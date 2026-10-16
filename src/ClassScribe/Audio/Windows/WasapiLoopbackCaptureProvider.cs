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
[SupportedOSPlatform("windows")]
public sealed class WasapiLoopbackCaptureProvider : ILoopbackCaptureProvider, IMMNotificationClient
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly ILogger<WasapiLoopbackCaptureProvider> _logger;
    private readonly Lock _gate = new();

    /// <summary>Device names by identifier. Windows takes up to half a second to give a Bluetooth device's name, so each is read once.</summary>
    private readonly Dictionary<string, string> _names = [];

    private GCHandle _keepAlive;
    private bool _registered;
    private bool _disposed;

    /// <summary>
    /// Registers for device notifications from Windows. When Windows refuses, the provider works without
    /// them: the recording's regular device check notices every change too, only a moment later.
    /// </summary>
    public WasapiLoopbackCaptureProvider(ILogger<WasapiLoopbackCaptureProvider> logger)
    {
        _logger = logger;

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
    public IReadOnlyList<OutputDevice> GetOutputDevices()
    {
        if (_disposed) return [];

        var defaultId = GetDefaultDeviceId();
        var devices = new List<OutputDevice>();
        foreach (var device in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (device)
            {
                devices.Add(new OutputDevice(device.ID, NameOf(device), device.ID == defaultId, ReadLevel(device)));
            }
        }

        return devices;
    }

    /// <inheritdoc />
    public IReadOnlyList<PlayingApp> GetPlayingApps()
    {
        if (_disposed) return [];

        // An app with sound open on several devices, or several times on one, is listed once, at its loudest.
        var apps = new Dictionary<int, PlayingApp>();
        foreach (var device in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (device)
            {
                var deviceName = NameOf(device);
                var manager = device.AudioSessionManager;   // has Dispose without IDisposable, so no using
                try
                {
                    var sessions = manager.Sessions;
                    for (var i = 0; i < sessions.Count; i++)
                    {
                        using var session = sessions[i];
                        if (session.State == AudioSessionState.AudioSessionStateExpired) continue;

                        var processId = (int)session.GetProcessID;
                        if (processId <= 0 || ProcessNameOf(processId) is not { } name) continue;   // 0 is Windows' own sounds

                        var level = ReadLevel(session);
                        if (!apps.TryGetValue(processId, out var known) || level > known.Level)
                            apps[processId] = new PlayingApp(processId, name, deviceName, level);
                    }
                }
                finally
                {
                    manager.Dispose();
                }
            }
        }

        return [.. apps.Values];
    }

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
            return new WasapiLoopbackAudioCapture(device, NameOf(device), _logger);
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
    /// Stops listening for device notifications. This runs as the app exits, so a failure to unregister is
    /// logged rather than thrown, and the enumerator is released whatever happens.
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
        }
    }

    /// <summary>Identifier of the default output device, or null when there is none.</summary>
    private string? GetDefaultDeviceId()
    {
        try
        {
            using var device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return device.ID;
        }
        catch (COMException ex)
        {
            // Windows answers "not found" when no output device is connected. That is an answer, not a failure.
            _logger.LogDebug(ex, "Windows reports no default output device");
            return null;
        }
    }

    /// <summary>The device's name, read from Windows the first time and remembered.</summary>
    private string NameOf(MMDevice device)
    {
        lock (_gate)
        {
            if (_names.TryGetValue(device.ID, out var known)) return known;
        }

        var name = device.FriendlyName;
        lock (_gate) _names[device.ID] = name;
        return name;
    }

    /// <summary>The name of the process, such as chrome, or null when it has already ended.</summary>
    private static string? ProcessNameOf(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch (ArgumentException)
        {
            // Deliberately not rethrown: a process that ended between the listing and this look-up has no sound to record.
            return null;
        }
    }

    /// <summary>How loud the device is playing right now, as its level meter reports it.</summary>
    private float ReadLevel(MMDevice device)
    {
        try
        {
            return device.AudioMeterInformation.MasterPeakValue;
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
    private float ReadLevel(AudioSessionControl session)
    {
        try
        {
            return session.AudioMeterInformation.MasterPeakValue;
        }
        catch (COMException ex)
        {
            // Deliberately not rethrown: a session that closed between the listing and this read is simply not heard playing.
            _logger.LogDebug(ex, "Could not read the level of an app's sound");
            return 0;
        }
    }
}
