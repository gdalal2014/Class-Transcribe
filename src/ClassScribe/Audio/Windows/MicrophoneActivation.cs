using System.Runtime.Versioning;
using ClassScribe.Audio.Microphones;
using ClassScribe.Desktop;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace ClassScribe.Audio.Windows;

/// <summary>
/// Opens a capture of a microphone by its endpoint id, asking Windows for unprocessed audio (RAW) when that is wanted:
/// the audio client is activated, its properties are set through ClassScribe's own IAudioClient2 declaration before the
/// stream is opened, and the object is handed to NAudio's <see cref="AudioClient"/> for the stream itself. Windows'
/// sound processing (Default) is used instead when the endpoint does not report that it supports RAW, when it offers no
/// IAudioClient2, when Windows refuses the properties, and at the next attempt after a stream failed to open with RAW
/// set. Once the stream has opened, before any audio, a line in the log says which microphone is recorded and how, with
/// the reason when it is not RAW, so that every attached window shows it; a reopening that would only repeat the last
/// line says it at Debug, and nothing is said about a stream that did not open.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MicrophoneActivation : IMicrophoneCaptureFactory
{
    /// <summary>
    /// Failures of opening the stream that have nothing to do with RAW, so they do not give it up: the microphone held
    /// exclusively by another program, blocked by Windows' privacy settings, gone, or Windows' audio service down.
    /// </summary>
    private static readonly int[] FailuresUnrelatedToRaw =
    [
        unchecked((int)0x8889000A),   // AUDCLNT_E_DEVICE_IN_USE
        unchecked((int)0x80070005),   // E_ACCESSDENIED: Settings > Privacy & security > Microphone
        unchecked((int)0x88890004),   // AUDCLNT_E_DEVICE_INVALIDATED
        unchecked((int)0x88890010)    // AUDCLNT_E_SERVICE_NOT_RUNNING
    ];

    private readonly IAudioClientActivator _activator;
    private readonly ILogger<MicrophoneActivation> _logger;
    private readonly IPacketSource? _packets;
    private readonly Lock _gate = new();

    /// <summary>The endpoints whose stream failed to open with RAW set, each with the failure's code: they open in Default from then on.</summary>
    private readonly Dictionary<string, int> _rawFailedToOpen = [];

    /// <summary>The last line said about how a microphone is recorded, so that a reopening does not repeat it above Debug.</summary>
    private string? _lastLine;

    /// <summary>Creates the factory over Windows.</summary>
    public MicrophoneActivation(ILogger<MicrophoneActivation> logger)
        : this(new WindowsAudioClientActivator(), logger)
    {
    }

    /// <summary>
    /// Creates the factory over <paramref name="activator"/>, which a test can play, with the captures it opens reading
    /// their packets from <paramref name="packets"/> instead of Windows when a test that starts them gives it.
    /// </summary>
    internal MicrophoneActivation(IAudioClientActivator activator, ILogger<MicrophoneActivation> logger, IPacketSource? packets = null)
    {
        _activator = activator;
        _logger = logger;
        _packets = packets;
    }

    /// <inheritdoc />
    public IAudioCapture Open(string endpointId, string name, MicrophoneProcessing processing)
    {
        ActivatedClient activated;
        try
        {
            activated = _activator.Activate(endpointId);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not open the microphone \"{Name}\" (0x{Code:X8})", name, ex.HResult);
            throw;
        }

        var raw = activated.Client;
        AudioClient client;
        HowRecorded how;
        try
        {
            how = processing == MicrophoneProcessing.Raw
                ? AskForRaw(raw, endpointId, name, activated.RawSupported)
                : new HowRecorded(MicrophoneProcessing.Default, LogLevel.Information, $"Microphone: \"{name}\", with Windows' sound processing");

            client = new AudioClient((IAudioClient)raw);
        }
        catch
        {
            // Nothing owns the activated object yet, so it is released here; from the AudioClient on, the capture owns it.
            CoreAudio.Release(raw);
            throw;
        }

        return WasapiAudioCapture.ForMicrophone(client, endpointId, name, how.Processing, _logger, failure => StreamOpened(endpointId, name, how, failure), _packets);
    }

    /// <summary>
    /// Asks <paramref name="raw"/>, the activated audio client of <paramref name="endpointId"/>, for unprocessed audio, and
    /// says which processing the stream will have, with the line to say once it has opened: RAW when it was taken;
    /// Default, with the reason, when the endpoint does not offer RAW, when a stream failed to open with it before, when
    /// the object has no IAudioClient2, or when Windows refuses the properties.
    /// </summary>
    private HowRecorded AskForRaw(object raw, string endpointId, string name, bool rawSupported)
    {
        int? failedBefore;
        lock (_gate) failedBefore = _rawFailedToOpen.TryGetValue(endpointId, out var code) ? code : null;

        if (failedBefore is { } failure)
            return HowRecorded.WithWindowsProcessing(name, $"RAW was set, but the microphone could not be opened with it: 0x{failure:X8}");

        if (!rawSupported)
            return HowRecorded.WithWindowsProcessing(name, "RAW is not offered by this microphone");

        if (raw is not IAudioClient2Native client2)
            return HowRecorded.WithWindowsProcessing(name, "RAW was not available: Windows offers no IAudioClient2 for this microphone");

        var result = AudioClient2.SetClientProperties(client2, AudioClientPropertiesNative.Raw);
        if (result >= 0)
            return new HowRecorded(MicrophoneProcessing.Raw, LogLevel.Information, $"Microphone: \"{name}\", without Windows' sound processing (RAW)");

        // The two answers Windows documents are expected of an endpoint that will not give RAW; any other is a surprise worth seeing.
        var level = result is AudioClient2.RawModeUnsupported or AudioClient2.InvalidArgument ? LogLevel.Information : LogLevel.Warning;
        return HowRecorded.WithWindowsProcessing(name, $"RAW was not available: 0x{result:X8}", level);
    }

    /// <summary>
    /// Told how opening the stream of <paramref name="endpointId"/> went. Once it is open, the line saying how the
    /// microphone is recorded is said: at its own level when it differs from the last line said, at Debug when a reopening
    /// (V on again, the class app opening the microphone again, a retry) would only repeat it. A stream that did not open
    /// is not said to be recorded; when it had RAW set, the endpoint is remembered so that the next attempt opens with
    /// Windows' sound processing.
    /// </summary>
    private void StreamOpened(string endpointId, string name, HowRecorded how, Exception? failure)
    {
        if (failure is not null)
        {
            if (how.Processing == MicrophoneProcessing.Raw) RememberRawFailure(endpointId, name, failure);
            return;
        }

        bool repeated;
        lock (_gate)
        {
            repeated = how.Line == _lastLine;
            _lastLine = how.Line;
        }

        _logger.Log(repeated ? LogLevel.Debug : how.Level, "{Line}", how.Line);
    }

    /// <summary>
    /// Notes that the stream of <paramref name="endpointId"/> failed to open with RAW set, so that the next attempt opens
    /// it with Windows' sound processing; a failure that has nothing to do with RAW is left to the next attempt as it is.
    /// </summary>
    private void RememberRawFailure(string endpointId, string name, Exception failure)
    {
        if (FailuresUnrelatedToRaw.Contains(failure.HResult))
        {
            _logger.LogDebug("The microphone \"{Name}\" could not be opened (0x{Code:X8}), for a reason unrelated to RAW; the next attempt asks for RAW again", name, failure.HResult);
            return;
        }

        lock (_gate) _rawFailedToOpen[endpointId] = failure.HResult;
        _logger.LogWarning("The microphone \"{Name}\" could not be opened without Windows' sound processing (0x{Code:X8}); the next attempt opens it with Windows' sound processing", name, failure.HResult);
    }

    /// <summary>
    /// How a microphone is to be recorded: the processing its stream was asked for, and the line that says so, with
    /// its level, for once the stream has opened.
    /// </summary>
    /// <param name="Processing">The processing the stream was asked for: RAW when the client's properties were set, Default otherwise.</param>
    /// <param name="Level">The line's level: Information, or Warning when Windows refused RAW with an answer it does not document.</param>
    /// <param name="Line">The line naming the microphone and how it is recorded, with the reason when that is not RAW.</param>
    private sealed record HowRecorded(MicrophoneProcessing Processing, LogLevel Level, string Line)
    {
        /// <summary>Windows' sound processing for <paramref name="name"/>, with <paramref name="reason"/> in the line, said at <paramref name="level"/>.</summary>
        public static HowRecorded WithWindowsProcessing(string name, string reason, LogLevel level = LogLevel.Information) =>
            new(MicrophoneProcessing.Default, level, $"Microphone: \"{name}\", with Windows' sound processing ({reason})");
    }
}

/// <summary>An endpoint's audio client, activated and not yet initialised, and whether the endpoint offers unprocessed audio.</summary>
/// <param name="Client">The audio client, as Windows hands it out (an IAudioClient, usually an IAudioClient2 too) or as a test plays it.</param>
/// <param name="RawSupported">True when the endpoint's property store says that unprocessed audio (RAW) can be asked for.</param>
internal sealed record ActivatedClient(object Client, bool RawSupported);

/// <summary>Activates a microphone's audio client, without opening a stream: Windows, or a test's stand-in.</summary>
internal interface IAudioClientActivator
{
    /// <summary>
    /// Activates the audio client of the endpoint <paramref name="endpointId"/> and reads whether it offers unprocessed
    /// audio. The caller owns the client. Fails as Windows does for an endpoint that is gone.
    /// </summary>
    ActivatedClient Activate(string endpointId);
}

/// <summary>
/// Windows' activation of an endpoint's audio client, through ClassScribe's own declarations: the endpoint is found by
/// its id, RawProcessingSupported is read from its property store, and IAudioClient is activated on it. Every object
/// but the client is released before this returns; nothing is initialised, so no stream is opened. In a test run a capture
/// endpoint, a microphone, is refused before anything is activated (see <see cref="DeviceGuards"/>); an output device, which
/// the tests of this activator stand in for a microphone, is not.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsAudioClientActivator : IAudioClientActivator
{
    /// <inheritdoc />
    public ActivatedClient Activate(string endpointId)
    {
        var enumerator = CoreAudio.CreateEnumerator();
        try
        {
            CoreAudio.Check(enumerator.GetDevice(endpointId, out var device));
            try
            {
                // Read only while the test guard is on, so that a class never makes this call.
                if (DeviceGuards.RefusingTheRealDevices) RefuseCaptureInTests(DataFlowOf(device));

                var store = CoreAudio.OpenPropertyStore(device);
                bool rawSupported;
                try
                {
                    rawSupported = CoreAudio.ReadBool(store, PropertyKeys.RawProcessingSupported) == true;
                }
                finally
                {
                    CoreAudio.Release(store);
                }

                var interfaceId = AudioClient2.AudioClientId;
                CoreAudio.Check(device.Activate(ref interfaceId, CoreAudio.AllContexts, IntPtr.Zero, out var client));
                return new ActivatedClient(client, rawSupported);
            }
            finally
            {
                CoreAudio.Release(device);
            }
        }
        finally
        {
            CoreAudio.Release(enumerator);
        }
    }

    /// <summary>
    /// Refuses, while the test guard is on (see <see cref="DeviceGuards"/>), an endpoint whose <paramref name="dataFlow"/> is
    /// <see cref="CoreAudio.Capture"/>: a microphone's audio client is never activated in a test, which uses
    /// FakeAudioClientActivator instead. An output device passes.
    /// </summary>
    internal static void RefuseCaptureInTests(int dataFlow)
    {
        if (dataFlow == CoreAudio.Capture) DeviceGuards.RefuseInTests("Windows' activation of a microphone's audio client", "FakeAudioClientActivator");
    }

    /// <summary>
    /// Whether <paramref name="device"/> plays (<see cref="CoreAudio.Render"/>) or records (<see cref="CoreAudio.Capture"/>), as
    /// its IMMEndpoint says; reading it opens nothing. The interface is another view of the same object, which the caller
    /// releases.
    /// </summary>
    internal static int DataFlowOf(IMMDeviceRaw device)
    {
        CoreAudio.Check(((IMMEndpointRaw)device).GetDataFlow(out var dataFlow));
        return dataFlow;
    }
}
