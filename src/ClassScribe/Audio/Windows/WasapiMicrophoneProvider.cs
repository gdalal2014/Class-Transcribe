using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ClassScribe.Audio.Microphones;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace ClassScribe.Audio.Windows;

/// <summary>
/// Lists the microphones Windows can record from, with what the choice of microphone needs to know about each; tells
/// when Windows reports a change among them; reads whether one is muted in Windows; and opens a capture of one through
/// the capture factory. Listing reads the endpoints' property stores, and the mute comes from the endpoint volume:
/// neither opens a stream on any microphone, so Windows' microphone icon never comes on for a look.
/// </summary>
/// <remarks>
/// The looks use ClassScribe's own declarations (<see cref="CoreAudio"/>) and release every object Windows hands them as
/// soon as it has been used, as the device check does, because a recording looks at the microphones every 10 seconds
/// and on every change for the whole class. NAudio's enumerator does what happens once: the device notifications,
/// which this provider receives for every device and passes on for the capture ones.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WasapiMicrophoneProvider : IMicrophoneProvider, IMMNotificationClient
{
    private static readonly Guid EndpointVolumeId = typeof(IAudioEndpointVolumeRaw).GUID;

    /// <summary>NAudio's enumerator: device notifications only.</summary>
    private readonly MMDeviceEnumerator _enumerator = new();

    private readonly IMicrophoneCaptureFactory _captures;
    private readonly ILogger<WasapiMicrophoneProvider> _logger;
    private readonly Func<IMMDeviceEnumeratorRaw> _createListingEnumerator;

    /// <summary>Opens a device's property store for reading: <see cref="CoreAudio.OpenPropertyStore"/>, or a test's stores.</summary>
    private readonly Func<IMMDeviceRaw, IPropertyStoreRaw> _openStore;

    /// <summary>Releases an object Windows handed out: <see cref="CoreAudio.Release"/>, or a test's account of what was released.</summary>
    private readonly Action<object?> _release;

    /// <summary>
    /// Held while a look runs: one look at a time makes and uses the looks' enumerator, and Dispose releases it only
    /// while no look holds it.
    /// </summary>
    private readonly Lock _lookGate = new();

    /// <summary>
    /// The enumerator the looks use: made at the first look, or at the next one when Windows would not make it yet, and
    /// kept until Dispose, also through looks that fail. Windows keeps a single enumerator per process and holds on to
    /// it itself, so releasing it and making it again would only give the same object back.
    /// </summary>
    private IMMDeviceEnumeratorRaw? _listingEnumerator;

    private GCHandle _keepAlive;
    private bool _registered;
    private volatile bool _disposed;

    /// <summary>
    /// Registers for device notifications from Windows. When Windows refuses, the provider works without them: the
    /// recording's regular look at the microphones notices every change too, only later.
    /// </summary>
    public WasapiMicrophoneProvider(IMicrophoneCaptureFactory captures, ILogger<WasapiMicrophoneProvider> logger)
        : this(captures, logger, CoreAudio.CreateEnumerator)
    {
    }

    /// <summary>
    /// Creates the provider with the looks' enumerator made by <paramref name="createListingEnumerator"/> at the first
    /// look, and at each following look until making it succeeds. Property stores are opened with
    /// <paramref name="openStore"/> and objects released with <paramref name="release"/>, when given; otherwise Windows
    /// answers and <see cref="CoreAudio"/> opens and releases. Tests pass fakes, and a release that keeps account.
    /// </summary>
    internal WasapiMicrophoneProvider(
        IMicrophoneCaptureFactory captures,
        ILogger<WasapiMicrophoneProvider> logger,
        Func<IMMDeviceEnumeratorRaw> createListingEnumerator,
        Func<IMMDeviceRaw, IPropertyStoreRaw>? openStore = null,
        Action<object?>? release = null)
    {
        _captures = captures;
        _logger = logger;
        _createListingEnumerator = createListingEnumerator;
        _openStore = openStore ?? CoreAudio.OpenPropertyStore;
        _release = release ?? CoreAudio.Release;

        // Windows keeps a plain pointer to this object for its notifications and does nothing to keep it alive. Were
        // the object ever collected while registered, the next notification would take the process down, so it roots
        // itself until Dispose, as the device check's provider does.
        _keepAlive = GCHandle.Alloc(this);
        try
        {
            // NAudio 2.2.1 turns a failed registration into a COMException; the number it returns is always 0.
            _enumerator.RegisterEndpointNotificationCallback(this);
            _registered = true;
        }
        catch (COMException ex)
        {
            // Deliberately not rethrown: notifications only make a change among microphones noticed sooner, and the
            // regular look notices it without them, within 10 seconds. Nothing is registered, so nothing needs rooting.
            _keepAlive.Free();
            _logger.LogWarning(ex, "Windows did not accept the registration for microphone notifications (0x{Code:X8}). A change among microphones is still noticed by the regular look", ex.HResult);
        }
        catch
        {
            _keepAlive.Free();
            throw;
        }
    }

    /// <inheritdoc />
    public event EventHandler? MicrophonesChanged;

    /// <inheritdoc />
    /// <remarks>
    /// A look that fails with a <see cref="COMException"/>, as every look does while Windows' audio service restarts, is
    /// noted at Debug and rethrown, for the caller to look again; the enumerator is kept for the next look. A microphone
    /// whose properties cannot be read is left out of that look alone.
    /// </remarks>
    public IReadOnlyList<Microphone> GetMicrophones()
    {
        try
        {
            return Look(ListMicrophones, whenDisposed: []);
        }
        catch (COMException ex)
        {
            _logger.LogDebug(ex, "Could not look at the microphones (0x{Code:X8})", ex.HResult);
            throw;
        }
    }

    /// <inheritdoc />
    public IAudioCapture CreateCapture(Microphone microphone, MicrophoneProcessing processing) => _captures.Open(microphone.Id, microphone.Name, processing);

    /// <inheritdoc />
    public bool? IsMuted(string id)
    {
        try
        {
            return Look(enumerator => ReadMute(enumerator, id), whenDisposed: null);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            // Deliberately not rethrown: the answer asked for is whether the microphone is muted, and when Windows will
            // not say, as while its audio service restarts or when the microphone's endpoint volume cannot be made, the
            // answer is that it is not known, which the caller words as such; it never stands for "not muted". An
            // endpoint that offers no endpoint volume answers E_NOINTERFACE, which Check throws as a COMException like
            // any failed result; an object that will not be cast to the endpoint volume is an InvalidCastException,
            // which is why that is caught beside it.
            _logger.LogDebug(ex, "Could not read whether the microphone {Id} is muted (0x{Code:X8})", id, ex.HResult);
            return null;
        }
    }

    /// <summary>Called by Windows on one of its own threads when a role's default device changes, so it only raises the event and returns.</summary>
    /// <remarks>Only a new default microphone for the Console role is a change among microphones: the role the listing reports as the default.</remarks>
    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (flow == DataFlow.Capture && role == Role.Console)
            MicrophonesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Called by Windows when a device is enabled, disabled, plugged or unplugged. Only raises the event.</summary>
    /// <remarks>Windows does not say whether the device records or plays, so the event is raised for either; listeners look at the listing.</remarks>
    public void OnDeviceStateChanged(string deviceId, DeviceState newState) => MicrophonesChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>Called by Windows when a device appears. Only raises the event.</summary>
    public void OnDeviceAdded(string pwstrDeviceId) => MicrophonesChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>Called by Windows when a device is gone for good. Only raises the event.</summary>
    public void OnDeviceRemoved(string deviceId) => MicrophonesChanged?.Invoke(this, EventArgs.Empty);

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
            // Deliberately not rethrown: the app is on its way out, and an exception here would cost its exit code
            // and its last messages. Windows may still hold its pointer, though, so this object stays rooted: a
            // notification that still comes must find it alive.
            _logger.LogDebug(ex, "Windows did not take back the registration for microphone notifications (0x{Code:X8})", ex.HResult);
        }
        finally
        {
            _enumerator.Dispose();
            if (released && _keepAlive.IsAllocated) _keepAlive.Free();
            ReleaseListingEnumerator();
        }
    }

    /// <summary>
    /// Runs one look on the looks' enumerator, made first when there is none, and returns what it found, or
    /// <paramref name="whenDisposed"/> once the provider is disposed. A look that fails leaves the enumerator for the
    /// next one; the caller logs and decides.
    /// </summary>
    private T Look<T>(Func<IMMDeviceEnumeratorRaw, T> look, T whenDisposed)
    {
        try
        {
            lock (_lookGate)
            {
                if (_disposed) return whenDisposed;

                return look(_listingEnumerator ??= _createListingEnumerator());
            }
        }
        finally
        {
            // Dispose does not wait for a look that Windows holds up: such a look releases the enumerator itself.
            if (_disposed) ReleaseListingEnumerator();
        }
    }

    /// <summary>
    /// Releases the looks' enumerator, unless a look is using it right now: that look releases it as it ends, so that
    /// disposing never waits for Windows.
    /// </summary>
    private void ReleaseListingEnumerator()
    {
        if (!_lookGate.TryEnter()) return;
        try
        {
            _release(_listingEnumerator);
            _listingEnumerator = null;
        }
        finally
        {
            _lookGate.Exit();
        }
    }

    /// <summary>The active microphones, each from its property store, the Console role's default marked.</summary>
    private List<Microphone> ListMicrophones(IMMDeviceEnumeratorRaw enumerator)
    {
        var defaultId = DefaultMicrophoneId(enumerator);
        var microphones = new List<Microphone>();
        ForEachActiveMicrophone(enumerator, (id, device) =>
        {
            if (ReadFacts(device) is { } facts) microphones.Add(facts.ToMicrophone(id, id == defaultId));
        });

        return microphones;
    }

    /// <summary>
    /// Whether the microphone <paramref name="id"/> is muted in Windows, from its endpoint volume, which is only read;
    /// null when Windows no longer lists it, as after it is unplugged.
    /// </summary>
    private bool? ReadMute(IMMDeviceEnumeratorRaw enumerator, string id)
    {
        var result = enumerator.GetDevice(id, out var device);
        if (result == CoreAudio.NotFound)
        {
            _logger.LogDebug("Windows no longer lists the microphone {Id}, so whether it is muted is not known", id);
            return null;
        }

        CoreAudio.Check(result);
        try
        {
            var volumeId = EndpointVolumeId;
            CoreAudio.Check(device.Activate(ref volumeId, CoreAudio.AllContexts, IntPtr.Zero, out var volume));
            try
            {
                CoreAudio.Check(((IAudioEndpointVolumeRaw)volume).GetMute(out var muted));
                return muted != 0;
            }
            finally
            {
                _release(volume);
            }
        }
        finally
        {
            _release(device);
        }
    }

    /// <summary>The facts of one microphone from its property store, released once read; null when the store cannot be read.</summary>
    private EndpointFacts? ReadFacts(IMMDeviceRaw device)
    {
        try
        {
            var store = _openStore(device);
            try
            {
                return EndpointFacts.Read(store);
            }
            finally
            {
                _release(store);
            }
        }
        catch (COMException ex)
        {
            // Deliberately not rethrown: a microphone whose properties Windows will not give went away between the
            // listing and this read, as one unplugged during the look, and has nothing to record; the others are still
            // listed, and the next look, on Windows' notice of the change or within 10 seconds, sees what is there.
            _logger.LogDebug(ex, "Left out a microphone whose properties Windows did not give (0x{Code:X8})", ex.HResult);
            return null;
        }
    }

    /// <summary>Identifier of Windows' default microphone for the Console role, or null when there is none.</summary>
    private string? DefaultMicrophoneId(IMMDeviceEnumeratorRaw enumerator)
    {
        var result = enumerator.GetDefaultAudioEndpoint(CoreAudio.Capture, CoreAudio.Console, out var device);
        if (result == CoreAudio.NotFound)
        {
            // Windows answers "not found" when no microphone is connected. That is an answer, not a failure.
            _logger.LogDebug("Windows reports no default microphone");
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
    /// Calls <paramref name="visit"/> with the identifier and the object of each active capture endpoint. Each endpoint
    /// is released after its visit, and the listing at the end.
    /// </summary>
    private void ForEachActiveMicrophone(IMMDeviceEnumeratorRaw enumerator, Action<string, IMMDeviceRaw> visit)
    {
        CoreAudio.Check(enumerator.EnumAudioEndpoints(CoreAudio.Capture, CoreAudio.Active, out var devices));
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
}
