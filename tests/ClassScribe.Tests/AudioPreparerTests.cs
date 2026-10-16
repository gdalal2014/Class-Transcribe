using System.Buffers.Binary;
using System.Text;
using ClassScribe.Audio;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NAudio.Wave;

namespace ClassScribe.Tests;

/// <summary>Resampling and joining segments into the upload file.</summary>
[TestFixture]
public class AudioPreparerTests
{
    /// <summary>The RMS level of a sine tone at amplitude 0.5.</summary>
    private static readonly double HalfScaleToneLevel = 0.5 / Math.Sqrt(2);

    private TempFolder _folder = null!;
    private AudioPreparer _preparer = null!;

    /// <summary>Creates a temporary folder and the preparer under test.</summary>
    [SetUp]
    public void SetUp()
    {
        _folder = new TempFolder();
        _preparer = new AudioPreparer(NullLogger<AudioPreparer>.Instance);
    }

    /// <summary>Deletes the temporary folder. That fails if a test left a file open, which is the point.</summary>
    [TearDown]
    public void TearDown() => _folder.Dispose();

    /// <summary>Counts sign changes, a simple measure of the pitch of a tone.</summary>
    private static int ZeroCrossings(short[] samples) =>
        Enumerable.Range(1, samples.Length - 1).Count(i => samples[i - 1] < 0 != samples[i] < 0);

    /// <summary>The RMS level of 16-bit samples, where 1 is full scale.</summary>
    private static double Rms(short[] samples) => Math.Sqrt(samples.Average(sample => sample / 32767.0 * (sample / 32767.0)));

    /// <summary>Adds bytes behind a finished WAV file without touching its header, as a crash between two saves leaves it.</summary>
    private static void AppendRaw(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write);
        stream.Write(bytes);
    }

    /// <summary>Writes a stereo 16-bit file of three frames with NAudio's writer, as another tool would. Mixed to mono, it reads 2000, -2000, 1000.</summary>
    private static void WriteForeignStereo(string path) =>
        ForeignWav.Write(path, new WaveFormat(16000, 16, 2), TestAudio.Shorts(1000, 3000, -1000, -3000, 500, 1500));

    /// <summary>A µ-law file: a WAV file NAudio can read, but cannot turn into samples.</summary>
    private static void WriteMuLaw(string path) => ForeignWav.Write(path, WaveFormat.CreateMuLawFormat(8000, 1), [0x7F, 0xFF, 0x7F, 0xFF]);

    /// <summary>Segments at different sample rates are joined into one 16 kHz mono 16-bit file that keeps the level and the pitch.</summary>
    [Test]
    public void Segments_with_different_sample_rates_become_one_16_khz_file()
    {
        var first = _folder.File("recording-001.wav");
        var second = _folder.File("recording-002.wav");
        TestAudio.WriteWav(first, 48000, TestAudio.Tone(48000, seconds: 1, frequency: 1000, amplitude: 0.5));
        TestAudio.WriteWav(second, 44100, TestAudio.Tone(44100, seconds: 1, frequency: 1000, amplitude: 0.5));
        var output = _folder.File("upload.wav");

        _preparer.Prepare([first, second], output, CancellationToken.None);

        var wav = TestAudio.ReadWav(output);
        Assert.That(wav.SampleRate, Is.EqualTo(16000));
        Assert.That(wav.Channels, Is.EqualTo(1));
        Assert.That(wav.BitsPerSample, Is.EqualTo(16));
        Assert.That(wav.Samples.Length / 16000.0, Is.EqualTo(2.0).Within(0.05), "duration in seconds");
        Assert.That(wav.Samples.Max() / 32767.0, Is.EqualTo(0.5).Within(0.05), "level is kept");
        Assert.That(ZeroCrossings(wav.Samples), Is.EqualTo(4000).Within(100), "a 1 kHz tone stays a 1 kHz tone");
    }

    /// <summary>A file that is already 16 kHz mono comes out sample for sample as it went in.</summary>
    [Test]
    public void A_16_khz_mono_file_passes_through_unchanged()
    {
        var input = _folder.File("in.wav");
        short[] samples = [100, -200, 300, -400];
        TestAudio.WriteWav(input, 16000, samples);
        var output = _folder.File("out.wav");

        _preparer.Prepare([input], output, CancellationToken.None);

        Assert.That(TestAudio.ReadWav(output).Samples, Is.EqualTo(samples));
    }

    /// <summary>A segment of the recorder's own is read to its end, past a header that a crash left behind the audio.</summary>
    [Test]
    public void Audio_written_after_the_last_header_update_is_recovered()
    {
        var input = _folder.File("recording-001.wav");
        TestAudio.WriteWav(input, 16000, [100, -200]);            // the header says two samples
        AppendRaw(input, TestAudio.Shorts(300, -400, 500));       // the crash came before the next header update
        var output = _folder.File("upload.wav");

        _preparer.Prepare([input], output, CancellationToken.None);

        Assert.That(TestAudio.ReadWav(output).Samples, Is.EqualTo(new short[] { 100, -200, 300, -400, 500 }));
    }

    /// <summary>Half a sample at the end of a crashed segment is dropped rather than read as noise.</summary>
    [Test]
    public void A_sample_cut_in_half_by_a_crash_is_dropped()
    {
        var input = _folder.File("recording-001.wav");
        TestAudio.WriteWav(input, 16000, [100, -200]);
        AppendRaw(input, [0x7F]);
        var output = _folder.File("upload.wav");

        _preparer.Prepare([input], output, CancellationToken.None);

        Assert.That(TestAudio.ReadWav(output).Samples, Is.EqualTo(new short[] { 100, -200 }));
    }

    /// <summary>A segment the recorder still holds open can be read.</summary>
    [Test]
    public void A_segment_that_is_still_being_recorded_can_be_read()
    {
        var input = _folder.File("recording-001.wav");
        using var recorder = new WavSegmentWriter(input, 16000);
        recorder.Write(TestAudio.Shorts(100, -200, 300));
        recorder.Flush();
        var output = _folder.File("upload.wav");

        _preparer.Prepare([input], output, CancellationToken.None);

        Assert.That(TestAudio.ReadWav(output).Samples, Is.EqualTo(new short[] { 100, -200, 300 }));
    }

    /// <summary>Empty and damaged segments are skipped, and the audio of the others is kept.</summary>
    [Test]
    public void Empty_and_damaged_segments_are_skipped()
    {
        var first = _folder.File("recording-001.wav");
        var empty = _folder.File("recording-002.wav");
        var damaged = _folder.File("recording-003.wav");
        var headerOnly = _folder.File("recording-004.wav");
        var last = _folder.File("recording-005.wav");
        TestAudio.WriteWav(first, 16000, [100, -200]);
        File.WriteAllBytes(empty, []);
        File.WriteAllText(damaged, "This is not audio at all, although the name says so.");
        TestAudio.WriteWav(headerOnly, 16000, []);
        TestAudio.WriteWav(last, 16000, [300]);
        var output = _folder.File("upload.wav");

        _preparer.Prepare([first, empty, damaged, headerOnly, last], output, CancellationToken.None);

        Assert.That(TestAudio.ReadWav(output).Samples, Is.EqualTo(new short[] { 100, -200, 300 }));
    }

    /// <summary>Files that hold no samples at all are reported as having no audio, and leave no upload file.</summary>
    [Test]
    public void Files_without_any_samples_are_reported_as_having_no_audio()
    {
        var empty = _folder.File("recording-001.wav");
        var headerOnly = _folder.File("recording-002.wav");
        File.WriteAllBytes(empty, []);
        TestAudio.WriteWav(headerOnly, 48000, []);
        var output = _folder.File("upload.wav");

        Assert.Throws<NoAudioException>(() => _preparer.Prepare([empty, headerOnly], output, CancellationToken.None));
        Assert.That(File.Exists(output), Is.False);
    }

    /// <summary>A file of zero bytes is empty rather than damaged.</summary>
    [Test]
    public void A_file_of_zero_bytes_counts_as_empty_not_as_damaged()
    {
        var empty = _folder.File("recording-001.wav");
        File.WriteAllBytes(empty, []);

        Assert.Throws<NoAudioException>(() => _preparer.Prepare([empty], _folder.File("upload.wav"), CancellationToken.None));
    }

    /// <summary>A recording of nothing but zero samples, as Windows delivers for a muted stream, is digital silence and is not uploaded.</summary>
    [Test]
    public void A_recording_of_digital_silence_is_refused()
    {
        var input = _folder.File("recording-001.wav");
        TestAudio.WriteWav(input, 48000, new short[48000]);
        var output = _folder.File("upload.wav");

        var error = Assert.Throws<NoAudioException>(() => _preparer.Prepare([input], output, CancellationToken.None));

        Assert.That(error!.Message, Does.Contain("digital silence"));
        Assert.That(File.Exists(output), Is.False);
    }

    /// <summary>Only exact zeros count as silence: a very quiet recording is still prepared.</summary>
    [Test]
    public void A_very_quiet_recording_is_not_taken_for_silence()
    {
        var input = _folder.File("recording-001.wav");
        TestAudio.WriteWav(input, 16000, [1, -1, 0, 1]);
        var output = _folder.File("upload.wav");

        _preparer.Prepare([input], output, CancellationToken.None);

        Assert.That(TestAudio.ReadWav(output).Samples, Is.EqualTo(new short[] { 1, -1, 0, 1 }));
    }

    /// <summary>A preparation that fails part-way leaves no unfinished upload file behind.</summary>
    [Test]
    public void A_failed_preparation_leaves_no_unfinished_upload_file_behind()
    {
        var input = _folder.File("phone.wav");
        WriteMuLaw(input);
        var output = _folder.File("upload.wav");

        Assert.Throws<ArgumentException>(() => _preparer.Prepare([input], output, CancellationToken.None));

        Assert.That(File.Exists(output), Is.False);
    }

    /// <summary>A file that cannot be reduced is a warning, not an error: the caller still has the original to upload.</summary>
    [Test]
    public void A_file_that_cannot_be_reduced_is_logged_as_a_warning_not_an_error()
    {
        var input = _folder.File("phone.wav");
        WriteMuLaw(input);
        var log = new LevelLogger();

        Assert.Throws<ArgumentException>(() => new AudioPreparer(log).Prepare([input], _folder.File("upload.wav"), CancellationToken.None));

        Assert.That(log.At(LogLevel.Error), Is.Empty);
        Assert.That(log.At(LogLevel.Warning), Has.Some.Contains("upload.wav"));
    }

    /// <summary>A file already where the upload file should go is never overwritten.</summary>
    [Test]
    public void A_file_that_already_sits_where_the_upload_file_should_go_is_not_touched()
    {
        var input = _folder.File("recording-001.wav");
        TestAudio.WriteWav(input, 16000, [100, -200]);
        var output = _folder.File("upload.wav");
        File.WriteAllText(output, "precious");

        Assert.Throws<IOException>(() => _preparer.Prepare([input], output, CancellationToken.None));

        Assert.That(File.ReadAllText(output), Is.EqualTo("precious"));
    }

    /// <summary>A file named .wav that holds something else is reported as unreadable, so the caller can upload it as it is.</summary>
    [Test]
    public void Files_that_are_no_wav_files_are_reported_as_unreadable()
    {
        var mislabelled = _folder.File("lecture.wav");
        File.WriteAllText(mislabelled, "ID3 and then an MP3 stream, say: a file some other tool may well understand.");

        Assert.Throws<FormatException>(() => _preparer.Prepare([mislabelled], _folder.File("upload.wav"), CancellationToken.None));
    }

    /// <summary>Stereo is mixed down to mono by averaging the two channels.</summary>
    [Test]
    public void A_stereo_file_is_mixed_down_to_mono()
    {
        var input = _folder.File("stereo.wav");
        using (var writer = new WaveFileWriter(input, new WaveFormat(16000, 16, 2)))
        {
            writer.Write(TestAudio.Shorts(16384, 0, -16384, 0), 0, 8);   // left carries the sound, right is silent
        }

        var output = _folder.File("mono.wav");
        _preparer.Prepare([input], output, CancellationToken.None);

        Assert.That(TestAudio.ReadWav(output).Samples, Is.EqualTo(new short[] { 8192, -8192 }));
    }

    /// <summary>More than two channels are mixed down to mono as well, by averaging all of them.</summary>
    [Test]
    public void A_file_with_more_than_two_channels_is_mixed_down_to_mono()
    {
        var input = _folder.File("surround.wav");
        ForeignWav.Write(input, new WaveFormat(16000, 16, 6), TestAudio.Shorts(6000, 0, 0, 0, 0, 0, -6000, 0, 0, 0, 0, 0));
        var output = _folder.File("out.wav");

        _preparer.Prepare([input], output, CancellationToken.None);

        Assert.That(TestAudio.ReadWav(output).Samples, Is.EqualTo(new short[] { 1000, -1000 }));
    }

    /// <summary>A 24-bit WAV with the WAVE_FORMAT_EXTENSIBLE header ffmpeg writes is reduced like any other, not uploaded at full size.</summary>
    [Test]
    public void An_extensible_24_bit_wav_as_ffmpeg_writes_it_is_reduced()
    {
        var input = _folder.File("export.wav");
        var tone = TestAudio.Tone(48000, seconds: 1, frequency: 1000, amplitude: 0.5).Select(sample => sample << 8);
        ForeignWav.Write(input, new WaveFormatExtensible(48000, 24, 2), ForeignWav.Int24(ForeignWav.OnEveryChannel(tone, 2)));
        var output = _folder.File("upload.wav");

        _preparer.Prepare([input], output, CancellationToken.None);

        var wav = TestAudio.ReadWav(output);
        Assert.That(wav.Samples.Length / 16000.0, Is.EqualTo(1.0).Within(0.02), "duration in seconds");
        Assert.That(wav.Samples.Max() / 32767.0, Is.EqualTo(0.5).Within(0.02), "level is kept");
        Assert.That(ZeroCrossings(wav.Samples), Is.EqualTo(2000).Within(50), "a 1 kHz tone stays a 1 kHz tone");
    }

    /// <summary>A floating-point WAV with the WAVE_FORMAT_EXTENSIBLE header is reduced as well.</summary>
    [Test]
    public void An_extensible_float_wav_is_reduced()
    {
        var input = _folder.File("export.wav");
        var tone = TestAudio.Tone(48000, seconds: 1, frequency: 1000, amplitude: 0.5).Select(sample => sample / 32768f);
        ForeignWav.Write(input, new WaveFormatExtensible(48000, 32, 2), TestAudio.Floats([.. ForeignWav.OnEveryChannel(tone, 2)]));
        var output = _folder.File("upload.wav");

        _preparer.Prepare([input], output, CancellationToken.None);

        var wav = TestAudio.ReadWav(output);
        Assert.That(wav.Samples.Length / 16000.0, Is.EqualTo(1.0).Within(0.02), "duration in seconds");
        Assert.That(wav.Samples.Max() / 32767.0, Is.EqualTo(0.5).Within(0.02), "level is kept");
    }

    /// <summary>
    /// The resampler keeps the band where sibilants carry their energy: a 6 kHz tone loses less than 1 dB on the way
    /// to 16 kHz, from every rate a device may mix at.
    /// </summary>
    [TestCase(44100)]
    [TestCase(48000)]
    [TestCase(96000)]
    [TestCase(192000)]
    public void A_6_khz_tone_keeps_its_level(int sampleRate)
    {
        var input = _folder.File("recording-001.wav");
        TestAudio.WriteWav(input, sampleRate, TestAudio.Tone(sampleRate, seconds: 1, frequency: 6000, amplitude: 0.5));
        var output = _folder.File("upload.wav");

        _preparer.Prepare([input], output, CancellationToken.None);

        var level = Rms(TestAudio.ReadWav(output).Samples[1600..14400]) / HalfScaleToneLevel;
        Assert.That(20 * Math.Log10(level), Is.EqualTo(0).Within(1), "change in level, in dB");
    }

    /// <summary>
    /// Sound above 8 kHz, which 16 kHz cannot hold, is filtered out rather than folded back into the band as a false
    /// tone, from every rate a device may mix at.
    /// </summary>
    [TestCase(44100)]
    [TestCase(48000)]
    [TestCase(96000)]
    [TestCase(192000)]
    public void A_tone_above_8_khz_does_not_fold_back_into_the_upload(int sampleRate)
    {
        var input = _folder.File("recording-001.wav");
        TestAudio.WriteWav(input, sampleRate, TestAudio.Tone(sampleRate, seconds: 1, frequency: 10000, amplitude: 0.5));
        var output = _folder.File("upload.wav");

        _preparer.Prepare([input], output, CancellationToken.None);

        var level = Rms(TestAudio.ReadWav(output).Samples[1600..14400]) / HalfScaleToneLevel;
        Assert.That(20 * Math.Log10(level), Is.LessThan(-30), "level of what folds back, in dB");
    }

    /// <summary>One sample that is not a number, or is infinite, in a float file costs at most a moment of audio, never the rest of the file.</summary>
    [Test]
    public void One_damaged_sample_in_a_float_file_does_not_silence_the_rest()
    {
        var input = _folder.File("glitch.wav");
        var samples = TestAudio.Tone(48000, seconds: 2, frequency: 1000, amplitude: 0.5).Select(sample => sample / 32768f).ToArray();
        samples[24000] = float.NaN;                 // at 0.5 s
        samples[48000] = float.PositiveInfinity;    // at 1.0 s
        ForeignWav.Write(input, WaveFormat.CreateIeeeFloatWaveFormat(48000, 1), TestAudio.Floats(samples));
        var output = _folder.File("upload.wav");

        _preparer.Prepare([input], output, CancellationToken.None);

        var wav = TestAudio.ReadWav(output).Samples;
        Assert.That(Rms(wav[9600..14400]), Is.EqualTo(HalfScaleToneLevel).Within(0.02), "level between the two damaged samples");
        Assert.That(Rms(wav[17600..30400]), Is.EqualTo(HalfScaleToneLevel).Within(0.02), "level after both");
    }

    /// <summary>
    /// A WAV file another recorder never finished, whose header still says it holds no audio, is read to its end
    /// rather than reported as empty, and the short header is reported.
    /// </summary>
    [Test]
    public void A_wav_file_whose_header_was_never_finished_is_read_to_its_end()
    {
        var input = _folder.File("rescued.wav");
        WriteForeignStereo(input);
        ForeignWav.LeaveUnfinished(input, dataBytes: 0);
        var output = _folder.File("upload.wav");
        var log = new LevelLogger();

        new AudioPreparer(log).Prepare([input], output, CancellationToken.None);

        Assert.That(TestAudio.ReadWav(output).Samples, Is.EqualTo(new short[] { 2000, -2000, 1000 }));
        Assert.That(log.At(LogLevel.Warning), Has.Some.Contains("rescued.wav"));
    }

    /// <summary>A WAV file whose recorder stopped updating the header part-way is read to its end, not cut short at the old length.</summary>
    [Test]
    public void A_wav_file_whose_header_stopped_being_updated_is_read_to_its_end()
    {
        var input = _folder.File("rescued.wav");
        WriteForeignStereo(input);
        ForeignWav.LeaveUnfinished(input, dataBytes: 4);   // the header covers the first frame only
        var output = _folder.File("upload.wav");

        _preparer.Prepare([input], output, CancellationToken.None);

        Assert.That(TestAudio.ReadWav(output).Samples, Is.EqualTo(new short[] { 2000, -2000, 1000 }));
    }

    /// <summary>A chunk of tags after the audio is not read as audio: a finished header is trusted when another chunk follows it.</summary>
    [Test]
    public void A_chunk_after_the_audio_is_not_read_as_audio()
    {
        var input = _folder.File("tagged.wav");
        WriteForeignStereo(input);
        ForeignWav.AppendChunk(input, "LIST", Encoding.ASCII.GetBytes("INFOISFT\u0004\0\0\0Tool"));
        var output = _folder.File("upload.wav");

        _preparer.Prepare([input], output, CancellationToken.None);

        Assert.That(TestAudio.ReadWav(output).Samples, Is.EqualTo(new short[] { 2000, -2000, 1000 }));
    }

    /// <summary>A WAV file that really is empty stays empty: the tags that follow its empty data chunk are not taken for audio.</summary>
    [Test]
    public void An_empty_wav_file_with_tags_is_still_empty()
    {
        var input = _folder.File("empty.wav");
        ForeignWav.Write(input, new WaveFormat(16000, 16, 2), []);
        ForeignWav.AppendChunk(input, "LIST", Encoding.ASCII.GetBytes("INFOISFT\u0004\0\0\0Tool"));

        Assert.Throws<NoAudioException>(() => _preparer.Prepare([input], _folder.File("upload.wav"), CancellationToken.None));
    }

    /// <summary>A cancelled preparation stops with a cancellation and leaves no unfinished upload file.</summary>
    [Test]
    public void A_cancelled_preparation_leaves_no_unfinished_upload_file()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var input = _folder.File("recording-001.wav");
        TestAudio.WriteWav(input, 16000, [100, -200]);
        var output = _folder.File("upload.wav");

        Assert.Throws<OperationCanceledException>(() => _preparer.Prepare([input], output, cancellation.Token));

        Assert.That(File.Exists(output), Is.False);
    }

    /// <summary>A cancel that comes while a file is being read stops the preparation within that file, so Ctrl+C on a long file takes effect at once.</summary>
    [Test]
    public void A_cancel_while_a_file_is_read_stops_the_preparation_within_it()
    {
        using var cancellation = new CancellationTokenSource();
        var input = _folder.File("rescued.wav");
        WriteForeignStereo(input);
        ForeignWav.LeaveUnfinished(input, dataBytes: 0);   // reported as the file is opened, before any of its audio is read
        var preparer = new AudioPreparer(new LevelLogger(onWarning: cancellation.Cancel));
        var output = _folder.File("upload.wav");

        Assert.Throws<OperationCanceledException>(() => preparer.Prepare([input], output, cancellation.Token));

        Assert.That(File.Exists(output), Is.False);
    }

    /// <summary>
    /// When an unfinished upload file cannot be removed, because another program has opened it, that is only a
    /// warning naming the file: the error that stopped the preparation is still the one reported.
    /// </summary>
    [Test]
    public void An_unfinished_upload_file_that_cannot_be_removed_does_not_hide_why_the_preparation_stopped()
    {
        var input = _folder.File("recording-001.wav");
        TestAudio.WriteWav(input, 16000, new short[16000]);   // digital silence: refused once the upload file has been made
        var output = _folder.File("upload.wav");
        FileStream? holder = null;
        // Another program opens the upload file, without letting it be deleted, as the failure is reported.
        var log = new LevelLogger(onWarning: () => holder ??= new FileStream(output, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));

        try
        {
            Assert.Throws<NoAudioException>(() => new AudioPreparer(log).Prepare([input], output, CancellationToken.None));

            Assert.That(log.At(LogLevel.Warning), Has.Some.EqualTo($"Could not remove the unfinished file {output}"));
            Assert.That(File.Exists(output), Is.True, "still held open, so still there");
        }
        finally
        {
            holder?.Dispose();
        }
    }

    /// <summary>
    /// The length prepared is logged in hours, minutes and whole seconds, with as many hours as there are, as every
    /// length in the app is shown (Durations.Format).
    /// </summary>
    [Test]
    public void The_length_prepared_is_logged_in_hours_minutes_and_seconds()
    {
        var input = _folder.File("recording-001.wav");
        TestAudio.WriteWav(input, 16000, TestAudio.Tone(16000, seconds: 2.5, frequency: 440, amplitude: 0.5));
        var log = new LevelLogger();

        new AudioPreparer(log).Prepare([input], _folder.File("upload.wav"), CancellationToken.None);

        Assert.That(log.At(LogLevel.Information), Has.Some.EqualTo("Prepared 00:00:02 of audio for upload"));
    }

    /// <summary>A logger that keeps each message with its level, and can act on a warning the moment it is logged.</summary>
    private sealed class LevelLogger(Action? onWarning = null) : ILogger<AudioPreparer>
    {
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        /// <summary>The messages logged at <paramref name="level"/>, in order.</summary>
        public IReadOnlyList<string> At(LogLevel level) => [.. _entries.Where(entry => entry.Level == level).Select(entry => entry.Message)];

        /// <inheritdoc />
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        /// <inheritdoc />
        public bool IsEnabled(LogLevel logLevel) => true;

        /// <inheritdoc />
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            _entries.Add((logLevel, formatter(state, exception)));
            if (logLevel == LogLevel.Warning) onWarning?.Invoke();
        }
    }
}

/// <summary>WAV files as other tools leave them: written with NAudio's writer, then left unfinished or given extra chunks.</summary>
public static class ForeignWav
{
    /// <summary>Writes audio with NAudio's writer, whose header (an 18-byte format chunk) is not the recorder's own.</summary>
    public static void Write(string path, WaveFormat format, byte[] audio)
    {
        using var writer = new WaveFileWriter(path, format);
        writer.Write(audio, 0, audio.Length);
    }

    /// <summary>
    /// Rewrites the sizes in the header as a recorder that stopped without finishing its file leaves them: the
    /// data chunk claims <paramref name="dataBytes"/> bytes and the RIFF size agrees, while all the audio stays in the file.
    /// </summary>
    public static void LeaveUnfinished(string path, uint dataBytes)
    {
        var bytes = File.ReadAllBytes(path);
        var data = FindChunk(bytes, "data");
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(data + 4), dataBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)data + dataBytes);
        File.WriteAllBytes(path, bytes);
    }

    /// <summary>Adds a chunk behind the audio and updates the RIFF size, as tools do that store tags after the audio.</summary>
    public static void AppendChunk(string path, string id, byte[] payload)
    {
        var size = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)payload.Length);
        byte[] bytes = [.. File.ReadAllBytes(path), .. Encoding.ASCII.GetBytes(id), .. size, .. payload, .. payload.Length % 2 == 1 ? new byte[1] : []];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)(bytes.Length - 8));
        File.WriteAllBytes(path, bytes);
    }

    /// <summary>Encodes samples as 24-bit little-endian PCM, each value the top 24 bits of a 32-bit sample.</summary>
    public static byte[] Int24(IEnumerable<int> samples)
    {
        var values = samples.ToArray();
        var bytes = new byte[values.Length * 3];
        for (var i = 0; i < values.Length; i++)
        {
            bytes[i * 3] = (byte)values[i];
            bytes[i * 3 + 1] = (byte)(values[i] >> 8);
            bytes[i * 3 + 2] = (byte)(values[i] >> 16);
        }

        return bytes;
    }

    /// <summary>Repeats each sample once per channel, for files in which every channel carries the same sound.</summary>
    public static IEnumerable<T> OnEveryChannel<T>(IEnumerable<T> samples, int channels) =>
        samples.SelectMany(sample => Enumerable.Repeat(sample, channels));

    /// <summary>Finds where a chunk's identifier starts in a WAV file.</summary>
    private static int FindChunk(byte[] file, string id)
    {
        for (var position = 12; position + 8 <= file.Length;)
        {
            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(position + 4));
            if (Encoding.ASCII.GetString(file, position, 4) == id) return position;
            position += 8 + size + size % 2;
        }

        throw new InvalidDataException($"The file has no {id} chunk.");
    }
}
