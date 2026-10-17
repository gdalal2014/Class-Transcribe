using System.Globalization;
using System.Runtime.InteropServices;
using ClassScribe.Audio;
using ClassScribe.Audio.Microphones;
using ClassScribe.Desktop;

namespace ClassScribe.Tests;

/// <summary>
/// This laptop's capture endpoints, as the microphone design's listing found them: the built-in array (Windows' default
/// for every role), the Pixel Buds (Bluetooth by their BTHLE enumerator), the soundcore headset (Bluetooth by a BTHENUM
/// property, although its enumerator is INTELAUDIO like the array's), the OBSBOT webcam microphone (USB) and Stereo Mix,
/// which records what the computer plays. Stereo Mix is disabled on the laptop, so a provider never lists it; a test
/// connects it to see that it is never chosen all the same.
/// </summary>
public static class LaptopMicrophones
{
    /// <summary>The built-in array, Windows' default microphone, which supports RAW.</summary>
    public static readonly Microphone Array = new("{0.0.1.00000000}.{06cd3d4c-878f-4abb-a294-362457ddd50a}",
        "Microphone Array (Intel® Smart Sound Technology for Digital Microphones)", IsDefault: true, IsBuiltInArray: true, IsBluetooth: false,
        IsLoopbackInput: false, RawSupported: true, Enumerator: "INTELAUDIO", BluetoothEvidence: null);

    /// <summary>The Pixel Buds' headset microphone, Bluetooth by its enumerator.</summary>
    public static readonly Microphone PixelBuds = new("{0.0.1.00000000}.{b1c2d3e4-0000-4000-8000-000000000001}", "Headset (2- Pixel Buds Pro 2)",
        IsDefault: false, IsBuiltInArray: false, IsBluetooth: true, IsLoopbackInput: false, RawSupported: false, Enumerator: "BTHLE",
        BluetoothEvidence: "enumerator BTHLE");

    /// <summary>The soundcore headset's microphone: Bluetooth by a BTHENUM property, on the INTELAUDIO bus like the array.</summary>
    public static readonly Microphone Soundcore = new("{0.0.1.00000000}.{b1c2d3e4-0000-4000-8000-000000000002}", "Headset (soundcore Liberty 5 Pro)",
        IsDefault: false, IsBuiltInArray: false, IsBluetooth: true, IsLoopbackInput: false, RawSupported: false, Enumerator: "INTELAUDIO",
        BluetoothEvidence: "{b3f8fa53-0004-438e-9003-51a46e139bfc},39 = {1}.BTHENUM\\{0000111E-0000-1000-8000-00805F9B34FB}_HCIBYPASS_VID&000102B0_PID&0000");

    /// <summary>The OBSBOT webcam's microphone, on USB.</summary>
    public static readonly Microphone Obsbot = new("{0.0.1.00000000}.{b1c2d3e4-0000-4000-8000-000000000003}", "OBSBOT Meet Flip Microphone (OBSBOT Meet Flip Audio)",
        IsDefault: false, IsBuiltInArray: false, IsBluetooth: false, IsLoopbackInput: false, RawSupported: true, Enumerator: "USB", BluetoothEvidence: null);

    /// <summary>Stereo Mix, which records what the computer plays and so would record the class twice.</summary>
    public static readonly Microphone StereoMix = new("{0.0.1.00000000}.{b1c2d3e4-0000-4000-8000-000000000004}", "Stereo Mix (Realtek(R) Audio)",
        IsDefault: false, IsBuiltInArray: false, IsBluetooth: false, IsLoopbackInput: true, RawSupported: false, Enumerator: "INTELAUDIO", BluetoothEvidence: null);

    /// <summary>The failure Windows reports when another program holds a microphone exclusively (AUDCLNT_E_DEVICE_IN_USE).</summary>
    public static COMException InUse() => new("The audio endpoint device is in use by another process.", unchecked((int)0x8889000A));

    /// <summary>The failure Windows reports when its privacy settings keep desktop apps from the microphone.</summary>
    public static UnauthorizedAccessException AccessDenied() => new("Access to the microphone is blocked in Settings > Privacy & security > Microphone.");
}

/// <summary>
/// The microphones a test connects, with which one is Windows' default and which are muted, and the fake captures handed
/// out for them. Only connected microphones are listed, as a real provider lists only active endpoints; a disabled or
/// unplugged one is simply not connected. Listing never opens anything, here as in the real provider.
/// </summary>
public sealed class FakeMicrophoneProvider : IMicrophoneProvider
{
    private readonly Lock _gate = new();
    private readonly List<Microphone> _connected = [];
    private readonly Dictionary<string, bool?> _muted = [];
    private readonly Queue<IAudioCapture> _prepared = [];
    private readonly List<FakeAudioCapture> _created = [];
    private readonly List<(string Id, MicrophoneProcessing Processing)> _opened = [];
    private int _listings;

    /// <summary>The format the captures hand over unless a prepared capture says otherwise: the array's 48 kHz, 2 channels, 32-bit float.</summary>
    public static readonly AudioFormat ArrayFormat = new(48000, 2, 32, true);

    /// <summary>Captures handed out so far.</summary>
    public IReadOnlyList<FakeAudioCapture> Created
    {
        get { lock (_gate) return [.. _created]; }
    }

    /// <summary>Which microphone each capture was opened for, with the processing asked for, in order.</summary>
    public IReadOnlyList<(string Id, MicrophoneProcessing Processing)> Opened
    {
        get { lock (_gate) return [.. _opened]; }
    }

    /// <summary>How often the microphones were listed.</summary>
    public int Listings => Volatile.Read(ref _listings);

    /// <summary>When set, every capture asked for fails with this, as a microphone blocked by Windows' privacy settings or held exclusively elsewhere would.</summary>
    public Exception? CaptureFailure { get; set; }

    /// <summary>When set, listing the microphones fails with this, as a stale enumerator would.</summary>
    public Exception? ListingFailure { get; set; }

    /// <summary>True once the provider was disposed.</summary>
    public bool Disposed { get; private set; }

    /// <summary>True while anyone listens for <see cref="MicrophonesChanged"/>.</summary>
    public bool HasListeners => MicrophonesChanged is not null;

    /// <inheritdoc />
    public event EventHandler? MicrophonesChanged;

    /// <summary>Connects <paramref name="microphone"/>, replacing one with the same id, without telling anyone. Returns this, so that a test connects several in one line.</summary>
    public FakeMicrophoneProvider Connect(Microphone microphone)
    {
        lock (_gate)
        {
            _connected.RemoveAll(existing => existing.Id == microphone.Id);
            _connected.Add(microphone);
        }

        return this;
    }

    /// <summary>Connects every microphone given, in that order.</summary>
    public FakeMicrophoneProvider Connect(params Microphone[] microphones)
    {
        foreach (var microphone in microphones) Connect(microphone);
        return this;
    }

    /// <summary>Disconnects the microphone with <paramref name="id"/>, as unplugging it would, without telling anyone.</summary>
    public void Disconnect(string id)
    {
        lock (_gate) _connected.RemoveAll(existing => existing.Id == id);
    }

    /// <summary>Makes the microphone with <paramref name="id"/> Windows' default, and no other.</summary>
    public void MakeDefault(string id)
    {
        lock (_gate)
        {
            for (var index = 0; index < _connected.Count; index++)
                _connected[index] = _connected[index] with { IsDefault = _connected[index].Id == id };
        }
    }

    /// <summary>Sets what Windows says about the mute of the microphone with <paramref name="id"/>: muted, not muted, or null when it cannot say.</summary>
    public void SetMuted(string id, bool? muted)
    {
        lock (_gate) _muted[id] = muted;
    }

    /// <summary>Tells the listeners that something changed, as Windows' notification would.</summary>
    public void RaiseChanged() => MicrophonesChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>Queues <paramref name="capture"/> as the next one handed out, whatever microphone is asked for.</summary>
    public FakeAudioCapture Prepare(FakeAudioCapture capture)
    {
        Prepare((IAudioCapture)capture);
        return capture;
    }

    /// <summary>Queues any capture as the next one handed out, such as a Windows capture over a fake audio client, which reports its own processing.</summary>
    public void Prepare(IAudioCapture capture)
    {
        lock (_gate) _prepared.Enqueue(capture);
    }

    /// <inheritdoc />
    public IReadOnlyList<Microphone> GetMicrophones()
    {
        Interlocked.Increment(ref _listings);
        if (ListingFailure is { } failure) throw failure;
        lock (_gate) return [.. _connected];
    }

    /// <inheritdoc />
    public IAudioCapture CreateCapture(Microphone microphone, MicrophoneProcessing processing)
    {
        lock (_gate)
        {
            _opened.Add((microphone.Id, processing));
            if (CaptureFailure is { } failure) throw failure;
            var capture = _prepared.Count > 0
                ? _prepared.Dequeue()
                : new FakeAudioCapture(ArrayFormat, microphone.Name, microphone.Id) { DeliversContinuously = true };
            if (capture is FakeAudioCapture fake) _created.Add(fake);
            return capture;
        }
    }

    /// <inheritdoc />
    public bool? IsMuted(string id)
    {
        lock (_gate) return _muted.GetValueOrDefault(id);
    }

    /// <summary>Notes that it was disposed.</summary>
    public void Dispose() => Disposed = true;
}

/// <summary>
/// Registry values a test sets: Lenovo Vantage's microphone mode and Windows' ducking preference, under keys a test names
/// as the real ones are named. A value not set reads as null, as a missing key or value does; a failure set here is what
/// every read throws, as a registry that cannot be read would.
/// </summary>
public sealed class FakeRegistryValues : IRegistryValues
{
    private readonly Dictionary<(string Key, string Name), object> _values = new();
    private int _reads;

    /// <summary>How many values were read, failed reads included.</summary>
    public int Reads => _reads;

    /// <summary>When set, every read throws this.</summary>
    public Exception? Failure { get; set; }

    /// <summary>Sets a string value (REG_SZ).</summary>
    public FakeRegistryValues Set(string key, string name, string value)
    {
        _values[(Normal(key), name)] = value;
        return this;
    }

    /// <summary>Sets a number value (REG_DWORD).</summary>
    public FakeRegistryValues Set(string key, string name, int value)
    {
        _values[(Normal(key), name)] = value;
        return this;
    }

    /// <summary>Removes a value, as deleting it from the registry would.</summary>
    public void Remove(string key, string name) => _values.Remove((Normal(key), name));

    /// <inheritdoc />
    public string? ReadString(string currentUserKey, string name) =>
        Read(currentUserKey, name) is { } value ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;

    /// <inheritdoc />
    public int? ReadDword(string currentUserKey, string name) => Read(currentUserKey, name) as int?;

    /// <summary>The value set for the key and name, or null; the failure when one is set.</summary>
    private object? Read(string key, string name)
    {
        _reads++;
        if (Failure is { } failure) throw failure;
        return _values.GetValueOrDefault((Normal(key), name));
    }

    /// <summary>Registry key paths compare without regard to case, as Windows compares them.</summary>
    private static string Normal(string key) => key.ToUpperInvariant();
}
