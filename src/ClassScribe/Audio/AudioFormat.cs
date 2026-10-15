namespace ClassScribe.Audio;

/// <summary>Describes the raw audio a capture device delivers.</summary>
public sealed record AudioFormat(int SampleRate, int Channels, int BitsPerSample, bool IsFloat)
{
    /// <summary>Bytes in one frame, which holds one sample for every channel.</summary>
    public int BytesPerFrame => Channels * (BitsPerSample / 8);
}
