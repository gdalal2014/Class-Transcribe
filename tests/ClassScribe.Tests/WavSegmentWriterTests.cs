using System.Runtime.Versioning;
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

    /// <summary>A closed file is a mono 16-bit WAV holding exactly the samples written, behind a 44-byte header.</summary>
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

    /// <summary>After a flush the file on disk is a valid WAV even while it is still open, which is what a crash leaves.</summary>
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

    /// <summary>Writing goes on after a flush, and the samples on both sides of it end up in order.</summary>
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

    /// <summary>
    /// Each write goes to Windows at once rather than into a buffer of the writer's own, so another reader sees the
    /// audio before any flush, and a kill, a shutdown or a sign-out cannot take it with the process.
    /// </summary>
    [Test]
    public void Each_write_reaches_windows_at_once()
    {
        var path = _folder.File("live.wav");
        using var writer = new WavSegmentWriter(path, 48000);
        var packet = TestAudio.Shorts([.. Enumerable.Range(1, 480).Select(sample => (short)sample)]);   // 10 ms at 48 kHz

        writer.Write(packet);

        using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var onDisk = new byte[reader.Length];
        reader.ReadExactly(onDisk);
        Assert.That(onDisk[WavSegmentWriter.HeaderSize..], Is.EqualTo(packet));
        Assert.That(writer.DataBytes, Is.EqualTo(packet.Length));
    }

    /// <summary>
    /// A flush that fails, here because another program has locked the header, leaves the writer as it was: the
    /// next audio still goes after the audio already written, not over the header, and the next flush brings the
    /// header up to date.
    /// </summary>
    [Test, Platform("Win")]
    [SupportedOSPlatform("windows")]
    public void A_failed_flush_leaves_the_audio_where_it_was()
    {
        var path = _folder.File("locked.wav");
        using (var writer = new WavSegmentWriter(path, 16000))
        {
            writer.Write(TestAudio.Shorts(1, 2));
            using (var otherProgram = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                otherProgram.Lock(0, WavSegmentWriter.HeaderSize);
                Assert.That(writer.Flush, Throws.InstanceOf<IOException>());
                otherProgram.Unlock(0, WavSegmentWriter.HeaderSize);
            }

            writer.Write(TestAudio.Shorts(3));
        }

        Assert.That(File.ReadAllBytes(path)[WavSegmentWriter.HeaderSize..], Is.EqualTo(TestAudio.Shorts(1, 2, 3)));
        Assert.That(TestAudio.ReadWav(path).Samples, Is.EqualTo(new short[] { 1, 2, 3 }));
    }

    /// <summary>The duration and the byte count follow from the samples written and the sample rate.</summary>
    [Test]
    public void Duration_follows_the_sample_rate()
    {
        using var writer = new WavSegmentWriter(_folder.File("c.wav"), 16000);
        writer.Write(new byte[16000 * 2 * 3]);

        Assert.That(writer.Duration.TotalSeconds, Is.EqualTo(3.0).Within(0.0001));
        Assert.That(writer.DataBytes, Is.EqualTo(96000L));
    }

    /// <summary>A header this writer produced is recognised, and its sample rate is read from it.</summary>
    [Test]
    public void Its_own_header_is_recognised_together_with_the_sample_rate()
    {
        var path = _folder.File("own.wav");
        TestAudio.WriteWav(path, 44100, [1, 2, 3]);
        var header = File.ReadAllBytes(path).AsSpan(0, WavSegmentWriter.HeaderSize);

        Assert.That(WavSegmentWriter.TryReadHeader(header, out var sampleRate), Is.True);
        Assert.That(sampleRate, Is.EqualTo(44100));
    }

    /// <summary>A header another tool wrote, or one that is too short, is not taken for its own.</summary>
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

    /// <summary>A plain header that differs from its own in one field, and so describes other audio, is not taken for its own.</summary>
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

    /// <summary>A file already at the path is never overwritten: creating the writer fails and the file is left as it was.</summary>
    [Test]
    public void An_existing_recording_is_never_overwritten()
    {
        var path = _folder.File("d.wav");
        File.WriteAllText(path, "precious");

        Assert.Throws<IOException>(() => new WavSegmentWriter(path, 16000).Dispose());
        Assert.That(File.ReadAllText(path), Is.EqualTo("precious"));
    }
}
