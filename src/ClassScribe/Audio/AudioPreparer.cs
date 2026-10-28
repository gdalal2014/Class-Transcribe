using System.Buffers.Binary;
using Microsoft.Extensions.Logging;
using NAudio.Dsp;
using NAudio.Wave;

namespace ClassScribe.Audio;

/// <summary>
/// Resamples recorded segments to 16 kHz mono and joins them, and mixes your microphone's track into the class's
/// where there is one. That is the format speech models work in, and it makes a two-hour class a 230 MB upload.
/// </summary>
public sealed class AudioPreparer(ILogger<AudioPreparer> logger) : IAudioPreparer
{
    /// <summary>Sample rate of the upload file.</summary>
    public const int TargetSampleRate = 16000;

    /// <summary>The WAVE_FORMAT_EXTENSIBLE sub-format of whole-number samples.</summary>
    private static readonly Guid PcmSubFormat = new("00000001-0000-0010-8000-00aa00389b71");

    /// <summary>The WAVE_FORMAT_EXTENSIBLE sub-format of floating-point samples.</summary>
    private static readonly Guid FloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");

    /// <summary>
    /// The level, in 16-bit sample values of 32,767, that a microphone's track has to rise above once to be mixed into
    /// the upload. Below it the track is taken to have heard nothing: the microphone was off, muted or never opened.
    /// </summary>
    public const int MicrophoneSoundThreshold = 100;

    /// <summary>The logger, for the reader of a track's segments, which reports the ones it skips.</summary>
    private ILogger<AudioPreparer> Logger => logger;

    /// <summary>
    /// Opens the sound of a lesson's video (teach-video 6.9): the tracks mixed as the upload is, the class's and your
    /// microphone's summed from their common start without gain, or the main track alone when the microphone's never rose
    /// above <see cref="MicrophoneSoundThreshold"/> (<see cref="MixedSound.MicrophoneLeftOut"/>), but mono at
    /// <paramref name="sampleRate"/> rather than 16 kHz, read as it is asked for, so that nothing of it is written to disk.
    /// Damaged segments are skipped with a warning, as for the upload.
    /// </summary>
    /// <param name="tracks">The segments of each track, in the order they are joined.</param>
    /// <param name="sampleRate">The rate of the samples read, such as 48,000 for the video's AAC.</param>
    /// <param name="cancellationToken">Checked after every second of the microphone's sound check.</param>
    public MixedSound OpenMix(UploadTracks tracks, int sampleRate, CancellationToken cancellationToken)
    {
        IReadOnlySet<string> unreadable = new HashSet<string>();
        var leftOut = tracks.Microphone.Count > 0 && !HeardAnything(tracks.Microphone, out unreadable, cancellationToken);
        var main = new TrackSamples(this, tracks.Main, sampleRate);
        // As for the upload, the microphone's files the sound check found unreadable are passed over without a second word.
        var microphone = tracks.Microphone.Count > 0 && !leftOut ? new TrackSamples(this, tracks.Microphone, sampleRate, unreadable) : null;
        return new MixedSound(main, microphone, sampleRate, leftOut);
    }

    /// <inheritdoc />
    public void Prepare(UploadTracks tracks, string outputPath, CancellationToken cancellationToken)
    {
        if (tracks.Microphone.Count > 0 && TryMix(tracks, outputPath, cancellationToken)) return;
        Prepare(tracks.Main, outputPath, cancellationToken);
    }

    /// <inheritdoc />
    public void Prepare(IReadOnlyList<string> wavPaths, string outputPath, CancellationToken cancellationToken)
    {
        var created = false;
        try
        {
            using var writer = new WavSegmentWriter(outputPath, TargetSampleRate);
            created = true;
            using var track = new TrackSamples(this, wavPaths, TargetSampleRate);
            var samples = new float[TargetSampleRate];
            var bytes = new byte[samples.Length * 2];
            var audible = false;

            while (true)
            {
                // Once per buffer, a second of audio: often enough for Ctrl+C to stop even a long file at once.
                cancellationToken.ThrowIfCancellationRequested();
                var read = track.Read(samples);
                if (read == 0) break;
                audible |= WritePcm(writer, samples.AsSpan(0, read), bytes);
            }

            var what = wavPaths.Count == 1 ? Path.GetFileName(wavPaths[0]) : $"the {wavPaths.Count} files of the recording";
            if (writer.DataBytes == 0)
            {
                // Two different dead ends: a caller may still upload a file this app merely cannot read,
                // but there is no point in uploading nothing.
                throw track.Unreadable > 0 && track.Unreadable == wavPaths.Count
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
            Cancelled(outputPath, created);
            throw;
        }
        catch (Exception ex)
        {
            Failed(ex, outputPath, created);
            throw;
        }
    }

    /// <summary>
    /// Mixes the class and your microphone into the upload file and returns true. Returns false, having written nothing,
    /// when the microphone's track never rose above <see cref="MicrophoneSoundThreshold"/>: the main track is then
    /// prepared on its own, exactly as it is when the recording has no microphone track at all.
    /// </summary>
    private bool TryMix(UploadTracks tracks, string outputPath, CancellationToken cancellationToken)
    {
        var created = false;
        try
        {
            if (!HeardAnything(tracks.Microphone, out var unreadable, cancellationToken))
            {
                logger.LogInformation("Your microphone stayed silent; only the class is sent.");
                return false;
            }

            using var writer = new WavSegmentWriter(outputPath, TargetSampleRate);
            created = true;
            using var main = new TrackSamples(this, tracks.Main, TargetSampleRate);
            // The sound check has already reported the microphone files it could not read; they are passed over here
            // without a word, so that one damaged file is named once in the log, not twice.
            using var microphone = new TrackSamples(this, tracks.Microphone, TargetSampleRate, reported: unreadable);
            var samples = new float[TargetSampleRate];
            var voice = new float[TargetSampleRate];
            var bytes = new byte[samples.Length * 2];

            while (true)
            {
                // Once per buffer, a second of audio, as when a track is prepared alone.
                cancellationToken.ThrowIfCancellationRequested();
                var fromMain = main.Read(samples);
                var fromMicrophone = microphone.Read(voice);
                var count = Math.Max(fromMain, fromMicrophone);
                if (count == 0) break;

                // Both tracks start at the recording's start and stay on its clock, so they are summed sample for sample
                // from the first, and the one that ends first is padded with silence until the other ends. No gain is
                // applied; the sum is clamped where the two together go past full scale.
                Array.Clear(samples, fromMain, count - fromMain);
                Array.Clear(voice, fromMicrophone, count - fromMicrophone);
                for (var i = 0; i < count; i++) samples[i] += voice[i];
                WritePcm(writer, samples.AsSpan(0, count), bytes);
            }

            logger.LogInformation("Prepared {Duration} of audio for upload: the class and your microphone mixed into one track", Durations.Format(writer.Duration));
            return true;
        }
        catch (OperationCanceledException)
        {
            Cancelled(outputPath, created);
            throw;
        }
        catch (Exception ex)
        {
            Failed(ex, outputPath, created);
            throw;
        }
    }

    /// <summary>
    /// True when any sample in the microphone's files rises above <see cref="MicrophoneSoundThreshold"/>. The files are
    /// read as they are, at their own rate, and only until the first such sample: a track that was heard costs a moment,
    /// and one that stayed silent one pass without resampling. The files found unreadable on the way, each reported
    /// with a warning, come back in <paramref name="unreadable"/>, so that the mix can pass over them in silence.
    /// </summary>
    private bool HeardAnything(IReadOnlyList<string> microphone, out IReadOnlySet<string> unreadable, CancellationToken cancellationToken)
    {
        // NAudio reads a 16-bit sample as its value over 32,768, so for the recorder's own files the threshold is exact.
        const float threshold = MicrophoneSoundThreshold / 32768f;
        using var track = new TrackSamples(this, microphone, sampleRate: null);
        unreadable = track.Skipped;
        var samples = new float[TargetSampleRate];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = track.Read(samples);
            if (read == 0) return false;
            if (samples.AsSpan(0, read).IndexOfAnyExceptInRange(-threshold, threshold) >= 0) return true;
        }
    }

    /// <summary>What a cancel leaves: no unfinished upload file, when one was started, and a line in the log.</summary>
    private void Cancelled(string outputPath, bool created)
    {
        if (created) DeleteUnfinished(outputPath);
        logger.LogInformation("Stopped preparing the upload file: cancelled");
    }

    /// <summary>
    /// What a failure leaves: a warning, not an error, because the caller decides what the failure means (a single WAV
    /// file this app cannot reduce is still uploaded as it is), and no unfinished upload file, when one was started.
    /// </summary>
    private void Failed(Exception ex, string outputPath, bool created)
    {
        logger.LogWarning(ex, "Could not prepare the upload file {Path}", outputPath);
        if (created) DeleteUnfinished(outputPath);
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
    /// Writes samples to the upload file as 16-bit values, clamped at full scale, through <paramref name="bytes"/>, which
    /// holds at least two bytes per sample. Returns true when any value written is not zero.
    /// </summary>
    private static bool WritePcm(WavSegmentWriter writer, ReadOnlySpan<float> samples, byte[] bytes)
    {
        var audible = false;
        for (var i = 0; i < samples.Length; i++)
        {
            var value = (short)MathF.Round(Math.Clamp(samples[i], -1f, 1f) * short.MaxValue);
            audible |= value != 0;
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2, 2), value);
        }

        writer.Write(bytes.AsSpan(0, samples.Length * 2));
        return audible;
    }

    /// <summary>
    /// The audio of one track as one stream of samples: its segments opened one after the other as they are read, each
    /// converted to mono at <paramref name="sampleRate"/> (16 kHz for the upload, 48 kHz for a lesson's video), or left at
    /// its own rate and channels for the sound check when that is null, with empty files passed over and damaged ones
    /// skipped with a warning, so that one damaged part costs the transcript of none of the others. A segment in
    /// <paramref name="reported"/> was found unreadable by an earlier pass over the same track and is skipped without
    /// opening it or warning again.
    /// </summary>
    internal sealed class TrackSamples(AudioPreparer preparer, IReadOnlyList<string> segments, int? sampleRate, IReadOnlySet<string>? reported = null) : IDisposable
    {
        private readonly HashSet<string> _skipped = [];
        private int _next;
        private FileStream? _file;
        private ISampleProvider? _source;

        /// <summary>How many segments were skipped because they are not WAV files this app can read.</summary>
        public int Unreadable { get; private set; }

        /// <summary>The segments skipped so far because they are not WAV files this app can read; it grows as the track is read.</summary>
        public IReadOnlySet<string> Skipped => _skipped;

        /// <summary>
        /// Reads samples into <paramref name="buffer"/>, filling it across segment boundaries, and returns how many: fewer
        /// than the buffer holds only at the end of the track, and none after it.
        /// </summary>
        public int Read(float[] buffer)
        {
            var total = 0;
            while (total < buffer.Length && (_source is not null || OpenNext()))
            {
                var read = _source!.Read(buffer, total, buffer.Length - total);
                if (read == 0) Close();
                else total += read;
            }

            return total;
        }

        /// <inheritdoc />
        public void Dispose() => Close();

        /// <summary>Opens the next segment that can be read, and returns false when none is left.</summary>
        private bool OpenNext()
        {
            while (_next < segments.Count)
            {
                var path = segments[_next++];
                if (reported?.Contains(path) == true)
                {
                    Skip(path);   // already found unreadable, and reported, by the earlier pass
                    continue;
                }

                // Shared for writing as well, so a segment that a recorder still holds open can be read.
                var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var opened = false;
                try
                {
                    if (file.Length == 0) continue;   // nothing was ever written to it: empty, not damaged

                    var wave = preparer.OpenWave(file, path);
                    _source = sampleRate is { } rate ? ToMono(wave, rate) : new FiniteSamples(wave.ToSampleProvider());
                    _file = file;
                    opened = true;
                    return true;
                }
                catch (Exception ex) when (ex is FormatException or EndOfStreamException or InvalidDataException)
                {
                    // Deliberately not rethrown: one damaged file must not cost the transcript of the others.
                    Skip(path);
                    preparer.Logger.LogWarning(ex, "Skipping {File}: it is not a readable WAV file", Path.GetFileName(path));
                }
                finally
                {
                    if (!opened) file.Dispose();
                }
            }

            return false;
        }

        /// <summary>Counts <paramref name="path"/> among the segments skipped as unreadable.</summary>
        private void Skip(string path)
        {
            Unreadable++;
            _skipped.Add(path);
        }

        /// <summary>Closes the open segment, when one is.</summary>
        private void Close()
        {
            _file?.Dispose();
            _file = null;
            _source = null;
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
        Span<byte> header = stackalloc byte[WavHeader.Size];
        var read = file.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        if (read == header.Length && WavHeader.TryRead(header, out var sampleRate))
            return new WaveData(file, WavHeader.Size, file.Length, new WaveFormat(sampleRate, 16, 1));

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
    internal static WaveFormat Plain(WaveFormat format)
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
    /// Wraps a WAV stream so it yields mono samples at <paramref name="sampleRate"/>: damaged samples made safe, all channels
    /// mixed together, and the rate converted with a windowed-sinc filter when it differs.
    /// </summary>
    private static ISampleProvider ToMono(WaveStream wave, int sampleRate)
    {
        ISampleProvider source = new FiniteSamples(wave.ToSampleProvider());
        if (source.WaveFormat.Channels > 1) source = new MonoDownmix(source);

        return source.WaveFormat.SampleRate == sampleRate
            ? source
            : new SincResampler(source, sampleRate);
    }
}

/// <summary>
/// The sound of a lesson's video as it is read (see <see cref="AudioPreparer.OpenMix"/>): mono samples at
/// <see cref="SampleRate"/>, the main track and your microphone's summed from their common start without gain, the one that
/// ends first padded with silence until the other ends, each sample between -1 and 1 (the sum clamped at full scale).
/// </summary>
public sealed class MixedSound : IDisposable
{
    /// <summary>The main track's samples.</summary>
    private readonly AudioPreparer.TrackSamples _main;

    /// <summary>Your microphone's samples; null when the recording has none, or they stayed silent.</summary>
    private readonly AudioPreparer.TrackSamples? _microphone;

    /// <summary>The microphone's samples of a read, summed into the main track's.</summary>
    private float[] _voice = [];

    /// <summary>The mix of <paramref name="main"/> and <paramref name="microphone"/>, at <paramref name="sampleRate"/>.</summary>
    internal MixedSound(AudioPreparer.TrackSamples main, AudioPreparer.TrackSamples? microphone, int sampleRate, bool microphoneLeftOut)
    {
        _main = main;
        _microphone = microphone;
        SampleRate = sampleRate;
        MicrophoneLeftOut = microphoneLeftOut;
    }

    /// <summary>The samples a second.</summary>
    public int SampleRate { get; }

    /// <summary>True when the recording has your microphone's track but it never rose above the threshold, so only the main track is heard.</summary>
    public bool MicrophoneLeftOut { get; }

    /// <summary>Reads the next samples into <paramref name="buffer"/> and returns how many: fewer only at the end, and none after it.</summary>
    public int Read(float[] buffer)
    {
        var fromMain = _main.Read(buffer);
        if (_microphone is null) return fromMain;

        if (_voice.Length != buffer.Length) _voice = new float[buffer.Length];
        var fromMicrophone = _microphone.Read(_voice);
        var count = Math.Max(fromMain, fromMicrophone);
        Array.Clear(buffer, fromMain, count - fromMain);
        for (var i = 0; i < count; i++) buffer[i] = Math.Clamp(buffer[i] + (i < fromMicrophone ? _voice[i] : 0f), -1f, 1f);
        return count;
    }

    /// <summary>Closes the tracks' files.</summary>
    public void Dispose()
    {
        _main.Dispose();
        _microphone?.Dispose();
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
