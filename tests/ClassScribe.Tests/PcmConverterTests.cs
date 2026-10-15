using ClassScribe.Audio;

namespace ClassScribe.Tests;

/// <summary>Conversion of device audio to mono 16-bit PCM.</summary>
[TestFixture]
public class PcmConverterTests
{
    /// <summary>Runs the converter and returns the samples it produced.</summary>
    private static short[] Convert(byte[] source, AudioFormat format)
    {
        var destination = new byte[source.Length];
        var written = PcmConverter.ToMono16(source, format, destination);
        return Enumerable.Range(0, written / 2).Select(i => BitConverter.ToInt16(destination, i * 2)).ToArray();
    }

    [Test]
    public void Float_stereo_is_averaged_to_mono()
    {
        var source = TestAudio.Floats(0.25f, 0.25f, 1f, -1f, -0.5f, 0f);

        var mono = Convert(source, new AudioFormat(48000, 2, 32, true));

        Assert.That(mono, Is.EqualTo(new short[] { 8192, 0, -8192 }));
    }

    [Test]
    public void Out_of_range_and_invalid_floats_are_clamped()
    {
        var source = TestAudio.Floats(2f, -2f, float.NaN);

        var mono = Convert(source, new AudioFormat(48000, 1, 32, true));

        Assert.That(mono, Is.EqualTo(new short[] { 32767, -32767, 0 }));
    }

    [Test]
    public void Sixteen_bit_mono_keeps_its_samples()
    {
        var source = TestAudio.Shorts(12345, -12345, 0);

        var mono = Convert(source, new AudioFormat(44100, 1, 16, false));

        Assert.That(mono, Is.EqualTo(new short[] { 12345, -12345, 0 }));
    }

    [Test]
    public void Twenty_four_and_thirty_two_bit_integers_are_scaled()
    {
        byte[] quarter24 = [0x00, 0x00, 0x20];        // 0x200000 = a quarter of full scale
        byte[] minusQuarter24 = [0x00, 0x00, 0xE0];   // -0x200000
        byte[] quarter32 = [0x00, 0x00, 0x00, 0x20];  // 0x20000000 = a quarter of full scale

        Assert.That(Convert([.. quarter24, .. minusQuarter24], new AudioFormat(48000, 1, 24, false)), Is.EqualTo(new short[] { 8192, -8192 }));
        Assert.That(Convert(quarter32, new AudioFormat(48000, 1, 32, false)), Is.EqualTo(new short[] { 8192 }));
    }

    [Test]
    public void Surround_uses_the_front_left_and_right_channels()
    {
        var source = TestAudio.Floats(0.5f, 0f, 1f, 1f, 1f, 1f);

        var mono = Convert(source, new AudioFormat(48000, 6, 32, true));

        Assert.That(mono, Is.EqualTo(new short[] { 8192 }));
    }

    [Test]
    public void A_trailing_partial_frame_is_ignored()
    {
        byte[] source = [.. TestAudio.Floats(0.25f, 0.25f), 1, 2, 3];

        var mono = Convert(source, new AudioFormat(48000, 2, 32, true));

        Assert.That(mono, Is.EqualTo(new short[] { 8192 }));
    }

    [Test]
    public void An_unsupported_format_is_refused()
    {
        var eightBit = new AudioFormat(8000, 1, 8, false);

        Assert.That(PcmConverter.IsSupported(eightBit), Is.False);
        Assert.Throws<NotSupportedException>(() => PcmConverter.ToMono16(new byte[4], eightBit, new byte[8]));
    }

    [Test]
    public void Peak_is_the_largest_absolute_sample()
    {
        var mono = TestAudio.Shorts(10, -300, 200);

        Assert.That(PcmConverter.Peak(mono), Is.EqualTo(300));
        Assert.That(PcmConverter.Peak([]), Is.EqualTo(0));
    }
}
