using System.Buffers.Binary;
using Microsoft.Extensions.Logging;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace ClassScribe.Audio;

/// <summary>Builds the file that gets uploaded.</summary>
public interface IAudioPreparer
{
    /// <summary>Joins the WAV files into one 16 kHz mono 16-bit WAV at <paramref name="outputPath"/>.</summary>
    /// <exception cref="NoAudioException">The files could be read, but there is no sound in them.</exception>
    /// <exception cref="FormatException">None of the files is a WAV file this app can read.</exception>
    void Prepare(IReadOnlyList<string> wavPaths, string outputPath);
}

/// <summary>The audio files were read without trouble, but they hold nothing to transcribe.</summary>
public sealed class NoAudioException(string message) : Exception(message);

/// <summary>
/// Resamples recorded segments to 16 kHz mono and joins them. That is the format speech models
/// work in, and it makes a two-hour class a 230 MB upload.
/// </summary>
public sealed class AudioPreparer(ILogger<AudioPreparer> logger) : IAudioPreparer
{
    /// <summary>Sample rate of the upload file.</summary>
    public const int TargetSampleRate = 16000;

    /// <inheritdoc />
    public void Prepare(IReadOnlyList<string> wavPaths, string outputPath)
    {
        var created = false;
        try
        {
            using var writer = new WavSegmentWriter(outputPath, TargetSampleRate);
            created = true;
            var samples = new float[TargetSampleRate];
            var bytes = new byte[samples.Length * 2];
            var unreadable = 0;

            foreach (var path in wavPaths)
            {
                try
                {
                    Append(path, writer, samples, bytes);
                }
                catch (Exception ex) when (ex is FormatException or EndOfStreamException or InvalidDataException)
                {
                    // Deliberately not rethrown: one damaged file must not cost the transcript of the others.
                    unreadable++;
                    logger.LogWarning(ex, "Skipping {File}: it is not a readable WAV file", Path.GetFileName(path));
                }
            }

            if (writer.DataBytes == 0)
            {
                // Two different dead ends: a caller may still upload a file this app merely cannot read,
                // but there is no point in uploading silence.
                throw unreadable > 0 && unreadable == wavPaths.Count
                    ? new FormatException("None of the files is a WAV file this app can read.")
                    : new NoAudioException("There is no audio in the recording, so there is nothing to transcribe.");
            }

            logger.LogInformation("Prepared {Duration} of audio for upload", writer.Duration.ToString(@"hh\:mm\:ss"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not prepare the upload file {Path}", outputPath);
            if (created) DeleteUnfinished(outputPath);
            throw;
        }
    }

    /// <summary>
    /// Removes an upload file that was started and not finished. If that fails too, it is only logged,
    /// so that the error that stopped the preparation remains the one that is reported.
    /// </summary>
    private void DeleteUnfinished(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not remove the unfinished file {Path}", path);
        }
    }

    /// <summary>Converts one WAV file to 16 kHz mono 16-bit and appends it to the upload file.</summary>
    private static void Append(string path, WavSegmentWriter writer, float[] samples, byte[] bytes)
    {
        // Shared for writing as well, so a segment that a recorder still holds open can be read.
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (file.Length == 0) return;   // nothing was ever written to it: empty, not damaged

        using var wave = OpenWave(file);
        var source = ToMono16Khz(wave, path);

        int read;
        while ((read = source.Read(samples, 0, samples.Length)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                var value = (short)MathF.Round(Math.Clamp(samples[i], -1f, 1f) * short.MaxValue);
                BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2, 2), value);
            }

            writer.Write(bytes.AsSpan(0, read * 2));
        }
    }

    /// <summary>
    /// Opens the audio in a WAV file. A file with the recorder's own plain header is read to its very
    /// end, whatever length the header states: after a crash or a power cut the header can be a few
    /// seconds behind the audio. Any other WAV file is left to NAudio's reader.
    /// </summary>
    private static WaveStream OpenWave(FileStream file)
    {
        Span<byte> header = stackalloc byte[WavSegmentWriter.HeaderSize];
        var read = file.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        if (read == header.Length && WavSegmentWriter.TryReadHeader(header, out var sampleRate))
            return new RawSourceWaveStream(file, new WaveFormat(sampleRate, 16, 1));

        file.Position = 0;
        return new WaveFileReader(file);
    }

    /// <summary>Wraps a WAV stream so it yields mono samples at the target rate.</summary>
    private static ISampleProvider ToMono16Khz(WaveStream wave, string path)
    {
        var source = wave.ToSampleProvider();
        source = source.WaveFormat.Channels switch
        {
            1 => source,
            2 => source.ToMono(),
            var channels => throw new NotSupportedException($"{path} has {channels} channels; only mono and stereo WAV files can be prepared.")
        };

        return source.WaveFormat.SampleRate == TargetSampleRate
            ? source
            : new WdlResamplingSampleProvider(source, TargetSampleRate);
    }
}
