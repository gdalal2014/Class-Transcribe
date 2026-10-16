namespace ClassScribe.Audio;

/// <summary>Describes the raw audio a capture device delivers.</summary>
/// <param name="SampleRate">Frames per second.</param>
/// <param name="Channels">Samples in each frame, one for every channel.</param>
/// <param name="BitsPerSample">Size of one sample.</param>
/// <param name="IsFloat">True for floating-point samples, false for integers.</param>
/// <param name="ChannelMask">
/// Which speaker each channel feeds, as Windows' channel mask (WAVEFORMATEXTENSIBLE's dwChannelMask):
/// the channels come in the order of the mask's bits, lowest first. Zero when the format does not say.
/// </param>
public sealed record AudioFormat(int SampleRate, int Channels, int BitsPerSample, bool IsFloat, int ChannelMask = 0)
{
    /// <summary>Bytes in one frame, which holds one sample for every channel.</summary>
    public int BytesPerFrame => Channels * (BitsPerSample / 8);
}
