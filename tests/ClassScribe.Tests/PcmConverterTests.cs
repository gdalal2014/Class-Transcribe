using ClassScribe.Audio;

namespace ClassScribe.Tests;

/// <summary>Conversion of device audio to mono 16-bit PCM.</summary>
[TestFixture]
public class PcmConverterTests
{
    /// <summary>A sample at 0.5 that reaches the mix at -3 dB, as the centre channel and each surround pair do: 0.5 × 0.7071 of full scale.</summary>
    private const short HalfAtMinus3Db = 11585;

    /// <summary>Runs the converter and returns the samples it produced.</summary>
    private static short[] Convert(byte[] source, AudioFormat format)
    {
        var destination = new byte[source.Length];
        var written = PcmConverter.ToMono16(source, format, destination);
        return Enumerable.Range(0, written / 2).Select(i => BitConverter.ToInt16(destination, i * 2)).ToArray();
    }

    /// <summary>One frame of 32-bit float audio with 0.5 in the given channel and silence in the others.</summary>
    private static byte[] OnlyIn(int channel, int channels)
    {
        var frame = new float[channels];
        frame[channel] = 0.5f;
        return TestAudio.Floats(frame);
    }

    /// <summary>Stereo float audio becomes the average of its two channels.</summary>
    [Test]
    public void Float_stereo_is_averaged_to_mono()
    {
        var source = TestAudio.Floats(0.25f, 0.25f, 1f, -1f, -0.5f, 0f);

        var mono = Convert(source, new AudioFormat(48000, 2, 32, true));

        Assert.That(mono, Is.EqualTo(new short[] { 8192, 0, -8192 }));
    }

    /// <summary>Float samples beyond full scale are clamped, and invalid ones become silence.</summary>
    [Test]
    public void Out_of_range_and_invalid_floats_are_clamped()
    {
        var source = TestAudio.Floats(2f, -2f, float.NaN);

        var mono = Convert(source, new AudioFormat(48000, 1, 32, true));

        Assert.That(mono, Is.EqualTo(new short[] { 32767, -32767, 0 }));
    }

    /// <summary>Mono 16-bit audio passes through unchanged.</summary>
    [Test]
    public void Sixteen_bit_mono_keeps_its_samples()
    {
        var source = TestAudio.Shorts(12345, -12345, 0);

        var mono = Convert(source, new AudioFormat(44100, 1, 16, false));

        Assert.That(mono, Is.EqualTo(new short[] { 12345, -12345, 0 }));
    }

    /// <summary>24-bit and 32-bit integer samples are scaled to 16 bits.</summary>
    [Test]
    public void Twenty_four_and_thirty_two_bit_integers_are_scaled()
    {
        byte[] quarter24 = [0x00, 0x00, 0x20];        // 0x200000 = a quarter of full scale
        byte[] minusQuarter24 = [0x00, 0x00, 0xE0];   // -0x200000
        byte[] quarter32 = [0x00, 0x00, 0x00, 0x20];  // 0x20000000 = a quarter of full scale

        Assert.That(Convert([.. quarter24, .. minusQuarter24], new AudioFormat(48000, 1, 24, false)), Is.EqualTo(new short[] { 8192, -8192 }));
        Assert.That(Convert(quarter32, new AudioFormat(48000, 1, 32, false)), Is.EqualTo(new short[] { 8192 }));
    }

    /// <summary>
    /// Speech that a player sends to the centre channel of a 5.1 or 7.1 device (Firefox does with a mono
    /// track) is kept, at -3 dB, rather than lost with the channels beyond front left and right.
    /// </summary>
    [TestCase(6)]
    [TestCase(8)]
    public void The_centre_channel_of_a_surround_device_is_kept(int channels)
    {
        var mono = Convert(OnlyIn(2, channels), new AudioFormat(48000, channels, 32, true));

        Assert.That(mono, Is.EqualTo(new short[] { HalfAtMinus3Db }));
    }

    /// <summary>The low-frequency channel of a surround device carries no speech, so it is left out of the mix.</summary>
    [TestCase(6)]
    [TestCase(8)]
    public void The_low_frequency_channel_is_left_out(int channels)
    {
        var mono = Convert(OnlyIn(3, channels), new AudioFormat(48000, channels, 32, true));

        Assert.That(mono, Is.EqualTo(new short[] { 0 }));
    }

    /// <summary>The surround channels are mixed in like a pair at -3 dB, so sound that plays only there is not lost.</summary>
    [Test]
    public void The_surround_channels_are_mixed_in_lower()
    {
        var source = TestAudio.Floats(0f, 0f, 0f, 0f, 0.5f, 0.5f);

        var mono = Convert(source, new AudioFormat(48000, 6, 32, true));

        Assert.That(mono, Is.EqualTo(new short[] { HalfAtMinus3Db }));
    }

    /// <summary>Stereo sound on a surround device keeps the level it has on a stereo one.</summary>
    [Test]
    public void Stereo_on_a_surround_device_keeps_its_level()
    {
        var source = TestAudio.Floats(0.25f, 0.25f, 0f, 0f, 0f, 0f, 0f, 0f);

        var mono = Convert(source, new AudioFormat(48000, 8, 32, true));

        Assert.That(mono, Is.EqualTo(new short[] { 8192 }));
    }

    /// <summary>
    /// A format that says which speaker each channel feeds is mixed by that: the third channel is kept as the
    /// centre of a 3.0 layout, and left out as the low-frequency channel of a 2.1 one.
    /// </summary>
    [Test]
    public void The_channel_mask_says_which_channel_is_which()
    {
        const int frontLeftRightAndCentre = 0x7;
        const int frontLeftRightAndLowFrequency = 0xB;

        var threePointZero = Convert(OnlyIn(2, 3), new AudioFormat(48000, 3, 32, true, frontLeftRightAndCentre));
        var twoPointOne = Convert(OnlyIn(2, 3), new AudioFormat(48000, 3, 32, true, frontLeftRightAndLowFrequency));

        Assert.That(threePointZero, Is.EqualTo(new short[] { HalfAtMinus3Db }));
        Assert.That(twoPointOne, Is.EqualTo(new short[] { 0 }));
    }

    /// <summary>With a channel count whose layout is unknown, only the first two channels, front left and right, are mixed, as before.</summary>
    [Test]
    public void An_unknown_layout_mixes_only_front_left_and_right()
    {
        var source = TestAudio.Floats(0.5f, 0f, 1f, 1f);

        var mono = Convert(source, new AudioFormat(48000, 4, 32, true));

        Assert.That(mono, Is.EqualTo(new short[] { 8192 }));
    }

    /// <summary>A trailing partial frame is ignored rather than read past its end.</summary>
    [Test]
    public void A_trailing_partial_frame_is_ignored()
    {
        byte[] source = [.. TestAudio.Floats(0.25f, 0.25f), 1, 2, 3];

        var mono = Convert(source, new AudioFormat(48000, 2, 32, true));

        Assert.That(mono, Is.EqualTo(new short[] { 8192 }));
    }

    /// <summary>A format the converter cannot read is reported as unsupported and refused.</summary>
    [Test]
    public void An_unsupported_format_is_refused()
    {
        var eightBit = new AudioFormat(8000, 1, 8, false);

        Assert.That(PcmConverter.IsSupported(eightBit), Is.False);
        Assert.Throws<NotSupportedException>(() => PcmConverter.ToMono16(new byte[4], eightBit, new byte[8]));
    }

    /// <summary>Peak finds the largest absolute sample, and zero in an empty buffer.</summary>
    [Test]
    public void Peak_is_the_largest_absolute_sample()
    {
        var mono = TestAudio.Shorts(10, -300, 200);

        Assert.That(PcmConverter.Peak(mono), Is.EqualTo(300));
        Assert.That(PcmConverter.Peak([]), Is.EqualTo(0));
    }
}
