using ClassScribe.Audio;

namespace ClassScribe.Tests;

/// <summary>The header of ClassScribe's WAV files, read from bytes (architecture 3.15 a: WavHeader in Core).</summary>
[TestFixture]
public class WavHeaderTests
{
    /// <summary>A header the WAV writer wrote is read with its sample rate, and the audio starts right after it.</summary>
    [Test]
    public void A_header_the_writer_wrote_is_read_with_its_sample_rate()
    {
        using var folder = new TempFolder();
        var path = Path.Combine(folder.Path, "segment.wav");
        using (var writer = new WavSegmentWriter(path, 48000))
        {
            writer.Write(new byte[960]);
        }

        var bytes = File.ReadAllBytes(path);

        Assert.That(WavHeader.TryRead(bytes, out var rate), Is.True);
        Assert.That(rate, Is.EqualTo(48000));
        Assert.That(bytes, Has.Length.EqualTo(WavHeader.Size + 960));
        Assert.That(WavHeader.Size, Is.EqualTo(44));
    }

    /// <summary>
    /// What is not a header of ClassScribe's own is refused: too few bytes, a file whose sizes say more chunks follow the
    /// audio, as other tools write, and a sample rate of nothing.
    /// </summary>
    [Test]
    public void What_is_not_a_header_of_ClassScribes_own_is_refused()
    {
        using var folder = new TempFolder();
        var path = Path.Combine(folder.Path, "segment.wav");
        using (var writer = new WavSegmentWriter(path, 16000))
        {
            writer.Write(new byte[320]);
        }

        var header = File.ReadAllBytes(path)[..WavHeader.Size];
        var otherTools = (byte[])header.Clone();
        otherTools[4]++;
        var noRate = (byte[])header.Clone();
        noRate.AsSpan(24, 4).Clear();

        Assert.That(WavHeader.TryRead(header[..^1], out _), Is.False);
        Assert.That(WavHeader.TryRead(otherTools, out _), Is.False);
        Assert.That(WavHeader.TryRead(noRate, out _), Is.False);
        Assert.That(WavHeader.TryRead(header, out var rate) && rate == 16000, Is.True);
    }
}