using System.Buffers.Binary;
using Microsoft.Extensions.Logging;
using NAudio.Dsp;
using NAudio.Wave;

namespace ClassScribe.Audio;

/// <summary>Builds the file that gets uploaded.</summary>
public interface IAudioPreparer
{
    /// <summary>
    /// Joins the WAV files into one 16 kHz mono 16-bit WAV at <paramref name="outputPath"/>. A preparation
    /// that fails or is cancelled leaves no file behind.
    /// </summary>
    /// <param name="wavPaths">The WAV files, in the order they are joined.</param>
    /// <param name="outputPath">Where the upload file goes. Nothing may be there yet.</param>
    /// <param name="cancellationToken">Checked after every second of audio, so Ctrl+C stops even a long file at once.</param>
    /// <exception cref="NoAudioException">The files could be read, but they hold no samples, or nothing but digital silence.</exception>
    /// <exception cref="FormatException">None of the files is a WAV file this app can read.</exception>
    /// <exception cref="IOException">A file is already at <paramref name="outputPath"/>. It is left as it is.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    void Prepare(IReadOnlyList<string> wavPaths, string outputPath, CancellationToken cancellationToken);
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

    /// <summary>The WAVE_FORMAT_EXTENSIBLE sub-format of whole-number samples.</summary>
    private static readonly Guid PcmSubFormat = new("00000001-0000-0010-8000-00aa00389b71");

    /// <summary>The WAVE_FORMAT_EXTENSIBLE sub-format of floating-point samples.</summary>
    private static readonly Guid FloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");

    /// <inheritdoc />
    public void Prepare(IReadOnlyList<string> wavPaths, string outputPath, CancellationToken cancellationToken)
    {
        var created = false;
        try
        {
            using var writer = new WavSegmentWriter(outputPath, TargetSampleRate);
            created = true;
            var samples = new float[TargetSampleRate];
            var bytes = new byte[samples.Length * 2];
            var unreadable = 0;
            var audible = false;

            foreach (var path in wavPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    audible |= Append(path, writer, samples, bytes, cancellationToken);
                }
                catch (Exception ex) when (ex is FormatException or EndOfStreamException or InvalidDataException)
                {
                    // Deliberately not rethrown: one damaged file must not cost the transcript of the others.
                    unreadable++;
                    logger.LogWarning(ex, "Skipping {File}: it is not a readable WAV file", Path.GetFileName(path));
                }
            }

            var what = wavPaths.Count == 1 ? Path.GetFileName(wavPaths[0]) : $"the {wavPaths.Count} files of the recording";
            if (writer.DataBytes == 0)
            {
                // Two different dead ends: a caller may still upload a file this app merely cannot read,
                // but there is no point in uploading nothing.
                throw unreadable > 0 && unreadable == wavPaths.Count
                    ? new FormatException("None of the files is a WAV file this app can read.")
                    : new NoAudioException($"No audio samples could be read from {what}, so there is nothing to transcribe.");
            }

            // Only exact zeros count: they are what Windows delivers while nothing plays or the sound is muted,
            // and uploading them would be paid for. A class that is merely quiet still goes up.
            if (!audible)
                throw new NoAudioException($"Every sample in {what} is zero: it is digital silence, so there is nothing to transcribe.");

            logger.LogInformation("Prepared {Duration} of audio for upload", Durations.Format(writer.Duration));
        }
        catch (OperationCanceledException)
        {
            if (created) DeleteUnfinished(outputPath);
            logger.LogInformation("Stopped preparing the upload file: cancelled");
            throw;
        }
        catch (Exception ex)
        {
            // A warning, not an error: the caller decides what the failure means. A single WAV file this app
            // cannot reduce is still uploaded as it is.
            logger.LogWarning(ex, "Could not prepare the upload file {Path}", outputPath);
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

    /// <summary>
    /// Converts one WAV file to 16 kHz mono 16-bit and appends it to the upload file. Returns true when any
    /// sample it wrote is not zero.
    /// </summary>
    private bool Append(string path, WavSegmentWriter writer, float[] samples, byte[] bytes, CancellationToken cancellationToken)
    {
        // Shared for writing as well, so a segment that a recorder still holds open can be read.
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (file.Length == 0) return false;   // nothing was ever written to it: empty, not damaged

        using var wave = OpenWave(file, path);
        var source = ToMono16Khz(wave);

        var audible = false;
        while (true)
        {
            // Once per buffer, a second of audio: often enough for Ctrl+C to stop even a long file at once.
            cancellationToken.ThrowIfCancellationRequested();
            var read = source.Read(samples, 0, samples.Length);
            if (read == 0) return audible;

            for (var i = 0; i < read; i++)
            {
                var value = (short)MathF.Round(Math.Clamp(samples[i], -1f, 1f) * short.MaxValue);
                audible |= value != 0;
                BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2, 2), value);
            }

            writer.Write(bytes.AsSpan(0, read * 2));
        }
    }

    /// <summary>
    /// Opens the audio in a WAV file. A file with the recorder's own plain header is read to its very end,
    /// whatever length the header states: after a crash or a power cut the header can be a few seconds behind
    /// the audio. Any other WAV file is parsed by NAudio and read as far as its header says, unless the header
    /// was never finished (see <see cref="LooksUnfinished"/>): then it, too, is read to its end, with a warning.
    /// </summary>
    private WaveStream OpenWave(FileStream file, string path)
    {
        Span<byte> header = stackalloc byte[WavSegmentWriter.HeaderSize];
        var read = file.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        if (read == header.Length && WavSegmentWriter.TryReadHeader(header, out var sampleRate))
            return new WaveData(file, WavSegmentWriter.HeaderSize, file.Length, new WaveFormat(sampleRate, 16, 1));

        file.Position = 0;
        WaveFormat format;
        long start, declared;
        using (var reader = new WaveFileReader(file))
        {
            format = Plain(reader.WaveFormat);
            start = file.Position;   // the reader leaves the file where the audio starts
            declared = reader.Length;
        }

        if (format.Channels < 1 || format.SampleRate < 1 || format.BlockAlign < 1)
            throw new FormatException($"The format block of {Path.GetFileName(path)} is damaged.");

        var end = start + declared;
        if (LooksUnfinished(file, end + declared % 2, format))
        {
            var bytesPerSecond = (double)format.SampleRate * format.BlockAlign;
            logger.LogWarning(
                "The header of {File} says it holds {Declared} of audio, but the file goes on to {Actual}, as a recorder that stopped without finishing its file leaves it. All of it is read",
                Path.GetFileName(path),
                Durations.Format(TimeSpan.FromSeconds(declared / bytesPerSecond)),
                Durations.Format(TimeSpan.FromSeconds((file.Length - start) / bytesPerSecond)));
            end = file.Length;
        }

        return new WaveData(file, start, Math.Min(end, file.Length), format);
    }

    /// <summary>
    /// True when a WAV file goes on past the end of the audio its header gives, by at least a frame, and what
    /// follows is not another chunk, such as tags. That is what a recorder leaves when it stops without finishing
    /// its file, whether it never filled in the sizes or stopped updating them; ffmpeg, too, reads a data size of
    /// zero as "to the end of the file".
    /// </summary>
    private static bool LooksUnfinished(FileStream file, long declaredEnd, WaveFormat format)
    {
        var rest = file.Length - declaredEnd;
        if (rest < format.BlockAlign) return false;
        if (rest < 8) return true;   // too short to be a chunk

        Span<byte> chunk = stackalloc byte[8];
        file.Position = declaredEnd;
        file.ReadExactly(chunk);
        var named = chunk[..4].IndexOfAnyExceptInRange((byte)0x20, (byte)0x7E) < 0;
        var fits = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]) <= rest - 8;
        return !(named && fits);
    }

    /// <summary>
    /// The plain format a WAVE_FORMAT_EXTENSIBLE header stands for, when it holds whole-number or floating-point
    /// samples. ffmpeg writes such headers for 24-bit and float audio, for rates above 48 kHz and for more than
    /// two channels, and NAudio only converts the plain form. Any other format is returned as it is.
    /// </summary>
    private static WaveFormat Plain(WaveFormat format)
    {
        if (format.Encoding != WaveFormatEncoding.Extensible || format is not WaveFormatExtraData { ExtraSize: >= 22 } extensible)
            return format;

        // After the valid bits (two bytes) and the speaker positions (four bytes) comes the sub-format, a GUID.
        var subFormat = new Guid(extensible.ExtraData.AsSpan(6, 16));
        var encoding = subFormat == PcmSubFormat ? WaveFormatEncoding.Pcm
            : subFormat == FloatSubFormat ? WaveFormatEncoding.IeeeFloat
            : WaveFormatEncoding.Extensible;
        return encoding == WaveFormatEncoding.Extensible
            ? format
            : WaveFormat.CreateCustomFormat(encoding, format.SampleRate, format.Channels, format.AverageBytesPerSecond, format.BlockAlign, format.BitsPerSample);
    }

    /// <summary>
    /// Wraps a WAV stream so it yields mono samples at the target rate: damaged samples made safe, all channels
    /// mixed together, and the rate converted with a windowed-sinc filter.
    /// </summary>
    private static ISampleProvider ToMono16Khz(WaveStream wave)
    {
        ISampleProvider source = new FiniteSamples(wave.ToSampleProvider());
        if (source.WaveFormat.Channels > 1) source = new MonoDownmix(source);

        return source.WaveFormat.SampleRate == TargetSampleRate
            ? source
            : new SincResampler(source, TargetSampleRate);
    }
}

/// <summary>
/// The audio between two positions in a WAV file, read in whole frames. Unlike NAudio's reader it does not
/// go by the sizes in the header: the caller decides where the audio ends.
/// </summary>
internal sealed class WaveData : WaveStream
{
    private readonly Stream _file;
    private readonly long _start;

    /// <summary>Reads the frames between <paramref name="start"/> and <paramref name="end"/> of <paramref name="file"/>, which stays open after this is disposed.</summary>
    public WaveData(Stream file, long start, long end, WaveFormat format)
    {
        _file = file;
        _start = start;
        WaveFormat = format;
        Length = Math.Max(0, end - start) / format.BlockAlign * format.BlockAlign;
        file.Position = start;
    }

    /// <inheritdoc />
    public override WaveFormat WaveFormat { get; }

    /// <inheritdoc />
    public override long Length { get; }

    /// <inheritdoc />
    public override long Position
    {
        get => _file.Position - _start;
        set => _file.Position = _start + Math.Clamp(value - value % WaveFormat.BlockAlign, 0, Length);
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
    {
        var wanted = (int)Math.Min(count, Length - Position);
        return wanted <= 0 ? 0 : _file.ReadAtLeast(buffer.AsSpan(offset, wanted), wanted, throwOnEndOfStream: false);
    }
}

/// <summary>
/// Turns samples that are not a number into silence and infinite ones into full scale. One such sample in a
/// damaged float file would otherwise pass into the filters that follow, and from there into everything after it.
/// </summary>
internal sealed class FiniteSamples(ISampleProvider source) : ISampleProvider
{
    /// <inheritdoc />
    public WaveFormat WaveFormat => source.WaveFormat;

    /// <inheritdoc />
    public int Read(float[] buffer, int offset, int count)
    {
        var read = source.Read(buffer, offset, count);
        foreach (ref var sample in buffer.AsSpan(offset, read))
        {
            if (!float.IsFinite(sample)) sample = float.IsNaN(sample) ? 0f : MathF.Sign(sample);
        }

        return read;
    }
}

/// <summary>Mixes any number of channels down to mono by averaging them, so the center channel of a surround file, which carries the voice, is kept.</summary>
internal sealed class MonoDownmix : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _channels;
    private float[] _frames = [];

    /// <summary>Mixes <paramref name="source"/> down to mono.</summary>
    public MonoDownmix(ISampleProvider source)
    {
        _source = source;
        _channels = source.WaveFormat.Channels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
    }

    /// <inheritdoc />
    public WaveFormat WaveFormat { get; }

    /// <inheritdoc />
    public int Read(float[] buffer, int offset, int count)
    {
        var wanted = count * _channels;
        if (_frames.Length < wanted) _frames = new float[wanted];

        var frames = _source.Read(_frames, 0, wanted) / _channels;
        for (var frame = 0; frame < frames; frame++)
        {
            var sum = 0f;
            for (var channel = 0; channel < _channels; channel++) sum += _frames[frame * _channels + channel];
            buffer[offset + frame] = sum / _channels;
        }

        return frames;
    }
}

/// <summary>
/// Converts the sample rate with the windowed-sinc filter of NAudio's WDL resampler. NAudio's own
/// WdlResamplingSampleProvider sets that resampler to linear interpolation behind a gentle low-pass, which on
/// the way down to 16 kHz takes 4 dB off at 5 kHz and 8 dB at 6 kHz, where s, sh and th carry their friction,
/// and lets sound above 8 kHz fold back into the band only 18 to 25 dB down. The sinc filter is flat at 6 kHz and
/// 1.6 dB down at 7 kHz, and folds sound at 9 kHz back 25 dB down, at 10 kHz 42 dB down and at 12 kHz 46 dB
/// down. Its length grows with the input rate (see <see cref="FilterLength"/>), so that this holds from 44.1 kHz
/// up to 192 kHz and beyond. From 48 kHz it runs about as fast as NAudio's provider; from 192 kHz the longer
/// filter makes the whole preparation about two and a half times as slow as from 48 kHz.
/// </summary>
internal sealed class SincResampler : ISampleProvider
{
    /// <summary>The filter length, in input samples, that gives the response described above from 48 kHz.</summary>
    private const int TapsAt48Khz = 64;

    private readonly ISampleProvider _source;
    private readonly WdlResampler _resampler = new();
    private readonly int _channels;

    /// <summary>Resamples <paramref name="source"/> to <paramref name="sampleRate"/>.</summary>
    public SincResampler(ISampleProvider source, int sampleRate)
    {
        _source = source;
        _channels = source.WaveFormat.Channels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, _channels);

        // No interpolation and no IIR filters: a windowed sinc, 32 times oversampled.
        _resampler.SetMode(false, 0, true, FilterLength(source.WaveFormat.SampleRate), 32);
        _resampler.SetFeedMode(false);   // driven by the output: each read asks for the input it needs
        _resampler.SetRates(source.WaveFormat.SampleRate, sampleRate);
    }

    /// <inheritdoc />
    public WaveFormat WaveFormat { get; }

    /// <inheritdoc />
    public int Read(float[] buffer, int offset, int count)
    {
        var frames = count / _channels;
        var needed = _resampler.ResamplePrepare(frames, _channels, out var input, out var inputOffset);
        var available = _source.Read(input, inputOffset, needed * _channels) / _channels;
        return _resampler.ResampleOut(buffer, offset, available, frames, _channels) * _channels;
    }

    /// <summary>
    /// The filter length, in samples of the input: as long in time as 64 taps at 48 kHz, rounded up to an even
    /// length, and never shorter than 64 taps. WDL moves only the cutoff with the rate, so a filter of fixed
    /// length would span less time, and so cut less steeply, the faster the input: from 192 kHz, 64 taps take
    /// 3 dB off at 6 kHz and let 10 kHz fold back only 12 dB down.
    /// </summary>
    private static int FilterLength(int inputRate) =>
        Math.Max(TapsAt48Khz, 2 * (int)Math.Ceiling(TapsAt48Khz / 2.0 * inputRate / 48000));
}
