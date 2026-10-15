using ClassScribe.Audio;
using NAudio.Wave;

namespace ClassScribe.Tests;

/// <summary>The WAV writer, including what is left on disk if the process dies.</summary>
[TestFixture]
public class WavSegmentWriterTests
{
    private TempFolder _folder = null!;

    /// <summary>Creates a temporary folder.</summary>
    [SetUp]
    public void SetUp() => _folder = new TempFolder();

    /// <summary>Deletes the temporary folder. That fails if a test left a file open, which is the point.</summary>
    [TearDown]
    public void TearDown() => _folder.Dispose();

    [Test]
    public void A_closed_file_is_a_mono_16_bit_wav_with_the_written_samples()
    {
        var path = _folder.File("a.wav");
        using (var writer = new WavSegmentWriter(path, 48000))
        {
            writer.Write(TestAudio.Shorts(1, -2, 3));
        }

        var wav = TestAudio.ReadWav(path);

        Assert.That(wav.SampleRate, Is.EqualTo(48000));
        Assert.That(wav.Channels, Is.EqualTo(1));
        Assert.That(wav.BitsPerSample, Is.EqualTo(16));
        Assert.That(wav.Samples, Is.EqualTo(new short[] { 1, -2, 3 }));
        Assert.That(new FileInfo(path).Length, Is.EqualTo(44L + 6));
    }

    [Test]
    public void The_header_is_valid_after_a_flush_while_the_file_is_still_open()
    {
        var path = _folder.File("crash.wav");
        using var writer = new WavSegmentWriter(path, 16000);
        writer.Write(TestAudio.Shorts(7, 8));
        writer.Flush();

        // This is what is left on disk if the process dies right now.
        var wav = TestAudio.ReadWav(path);

        Assert.That(wav.Samples, Is.EqualTo(new short[] { 7, 8 }));
    }

    [Test]
    public void Writing_continues_after_a_flush()
    {
        var path = _folder.File("b.wav");
        using (var writer = new WavSegmentWriter(path, 16000))
        {
            writer.Write(TestAudio.Shorts(1));
            writer.Flush();
            writer.Write(TestAudio.Shorts(2, 3));
        }

        Assert.That(TestAudio.ReadWav(path).Samples, Is.EqualTo(new short[] { 1, 2, 3 }));
    }

    [Test]
    public void Duration_follows_the_sample_rate()
    {
        using var writer = new WavSegmentWriter(_folder.File("c.wav"), 16000);
        writer.Write(new byte[16000 * 2 * 3]);

        Assert.That(writer.Duration.TotalSeconds, Is.EqualTo(3.0).Within(0.0001));
        Assert.That(writer.DataBytes, Is.EqualTo(96000L));
    }

    [Test]
    public void Its_own_header_is_recognised_together_with_the_sample_rate()
    {
        var path = _folder.File("own.wav");
        TestAudio.WriteWav(path, 44100, [1, 2, 3]);
        var header = File.ReadAllBytes(path).AsSpan(0, WavSegmentWriter.HeaderSize);

        Assert.That(WavSegmentWriter.TryReadHeader(header, out var sampleRate), Is.True);
        Assert.That(sampleRate, Is.EqualTo(44100));
    }

    [Test]
    public void Other_headers_are_not_taken_for_its_own()
    {
        var path = _folder.File("other.wav");
        using (var writer = new WaveFileWriter(path, new WaveFormat(16000, 16, 2)))
        {
            writer.Write(new byte[8], 0, 8);
        }

        var stereo = File.ReadAllBytes(path).AsSpan(0, WavSegmentWriter.HeaderSize);
        var tooShort = new byte[WavSegmentWriter.HeaderSize - 1];

        Assert.That(WavSegmentWriter.TryReadHeader(stereo, out _), Is.False);
        Assert.That(WavSegmentWriter.TryReadHeader(tooShort, out _), Is.False);
    }

    [TestCase(16, 18)]   // size of the format block
    [TestCase(20, 3)]    // sample type: floating point instead of whole numbers
    [TestCase(22, 2)]    // channels: stereo
    [TestCase(34, 8)]    // bits per sample
    public void A_plain_header_for_another_kind_of_audio_is_not_taken_for_its_own(int offset, int value)
    {
        var path = _folder.File("almost.wav");
        TestAudio.WriteWav(path, 16000, [1, 2]);
        var header = File.ReadAllBytes(path)[..WavSegmentWriter.HeaderSize];
        header[offset] = (byte)value;

        Assert.That(WavSegmentWriter.TryReadHeader(header, out _), Is.False);
    }

    [Test]
    public void An_existing_recording_is_never_overwritten()
    {
        var path = _folder.File("d.wav");
        File.WriteAllText(path, "precious");

        Assert.Throws<IOException>(() => new WavSegmentWriter(path, 16000).Dispose());
        Assert.That(File.ReadAllText(path), Is.EqualTo("precious"));
    }
}
