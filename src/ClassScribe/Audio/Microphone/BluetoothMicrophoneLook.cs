namespace ClassScribe.Audio.Microphones;

/// <summary>An app taking sound from a Bluetooth microphone, as one look found it.</summary>
/// <param name="App">The app's program without .exe, such as chrome, or "process N" when it could not be named.</param>
/// <param name="Microphone">The microphone's name, as Windows shows it, such as Headset (2- Pixel Buds Pro 2).</param>
public sealed record BluetoothMicrophoneUse(string App, string Microphone);

/// <summary>What one look of <see cref="BluetoothMicrophoneWatch"/> found.</summary>
/// <param name="Began">The uses that were not there at the look before: each to be warned about, once.</param>
/// <param name="Ended">The uses of the look before that are gone: each to be said once.</param>
/// <param name="Current">Every use found now.</param>
/// <param name="BuiltInMicrophone">The name of the laptop's own microphone array, when one is connected, for the advice.</param>
/// <param name="Problem">Why nothing could be read now, in words, when nothing could; the uses are then those of the look before.</param>
public sealed record BluetoothMicrophoneLook(
    IReadOnlyList<BluetoothMicrophoneUse> Began,
    IReadOnlyList<BluetoothMicrophoneUse> Ended,
    IReadOnlyList<BluetoothMicrophoneUse> Current,
    string? BuiltInMicrophone,
    string? Problem);
