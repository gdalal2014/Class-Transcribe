using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace ClassScribe.Audio.Windows;

/// <summary>
/// Creates loopback captures of the Windows default output device and reports when Windows
/// switches to another one, for example when headphones are plugged in.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WasapiLoopbackCaptureProvider : ILoopbackCaptureProvider, IMMNotificationClient
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly ILogger<WasapiLoopbackCaptureProvider> _logger;
    private GCHandle _keepAlive;
    private bool _disposed;

    /// <summary>Registers for device notifications from Windows.</summary>
    public WasapiLoopbackCaptureProvider(ILogger<WasapiLoopbackCaptureProvider> logger)
    {
        _logger = logger;

        // Windows keeps a plain pointer to this object for its notifications and does nothing to keep
        // it alive. Were the object ever collected while registered, the next notification would take
        // the process down, so it roots itself until Dispose. (NAudio 3 does this itself, 2.2.1 does not.)
        _keepAlive = GCHandle.Alloc(this);
        try
        {
            var result = _enumerator.RegisterEndpointNotificationCallback(this);
            if (result < 0)
                _logger.LogWarning("Windows did not accept the registration for device notifications (0x{Code:X8}). A switch of output device is still followed, a few seconds later than usual", result);
        }
        catch
        {
            _keepAlive.Free();
            throw;
        }
    }

    /// <inheritdoc />
    public event EventHandler? DefaultDeviceChanged;

    /// <inheritdoc />
    public string? GetDefaultDeviceId()
    {
        if (_disposed) return null;

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

    /// <inheritdoc />
    public IAudioCapture CreateForDefaultDevice()
    {
        MMDevice device;
        try
        {
            device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }
        catch (COMException ex)
        {
            throw new InvalidOperationException("Windows reports no audio output device to record.", ex);
        }

        try
        {
            return new WasapiLoopbackAudioCapture(device, _logger);
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
            DefaultDeviceChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Not used.</summary>
    public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }

    /// <summary>Not used.</summary>
    public void OnDeviceAdded(string pwstrDeviceId) { }

    /// <summary>Not used.</summary>
    public void OnDeviceRemoved(string deviceId) { }

    /// <summary>Not used.</summary>
    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

    /// <summary>Stops listening for device notifications.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _enumerator.UnregisterEndpointNotificationCallback(this);
            _enumerator.Dispose();
        }
        finally
        {
            if (_keepAlive.IsAllocated) _keepAlive.Free();
        }
    }
}
