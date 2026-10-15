using ClassScribe.Audio;
using Microsoft.Extensions.Logging.Abstractions;
using NAudio.Wave;

namespace ClassScribe.Tests;

/// <summary>Resampling and joining segments into the upload file.</summary>
[TestFixture]
public class AudioPreparerTests
{
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

    /// <summary>Adds bytes behind a finished WAV file without touching its header, as a crash between two saves leaves it.</summary>
    private static void AppendRaw(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write);
        stream.Write(bytes);
    }

    [Test]
    public void Segments_with_different_sample_rates_become_one_16_khz_file()
    {
        var first = _folder.File("recording-001.wav");
        var second = _folder.File("recording-002.wav");
        TestAudio.WriteWav(first, 48000, TestAudio.Tone(48000, seconds: 1, frequency: 1000, amplitude: 0.5));
        TestAudio.WriteWav(second, 44100, TestAudio.Tone(44100, seconds: 1, frequency: 1000, amplitude: 0.5));
        var output = _folder.File("upload.wav");

        _preparer.Prepare([first, second], output);

        var wav = TestAudio.ReadWav(output);
        Assert.That(wav.SampleRate, Is.EqualTo(16000));
        Assert.That(wav.Channels, Is.EqualTo(1));
        Assert.That(wav.BitsPerSample, Is.EqualTo(16));
        Assert.That(wav.Samples.Length / 16000.0, Is.EqualTo(2.0).Within(0.05), "duration in seconds");
        Assert.That(wav.Samples.Max() / 32767.0, Is.EqualTo(0.5).Within(0.05), "level is kept");
        Assert.That(ZeroCrossings(wav.Samples), Is.EqualTo(4000).Within(100), "a 1 kHz tone stays a 1 kHz tone");
    }

    [Test]
    public void A_16_khz_mono_file_passes_through_unchanged()
    {
        var input = _folder.File("in.wav");
        short[] samples = [100, -200, 300, -400];
        TestAudio.WriteWav(input, 16000, samples);
        var output = _folder.File("out.wav");

        _preparer.Prepare([input], output);

        Assert.That(TestAudio.ReadWav(output).Samples, Is.EqualTo(samples));
    }

    [Test]
    public void Audio_written_after_the_last_header_update_is_recovered()
    {
        var input = _folder.File("recording-001.wav");
        TestAudio.WriteWav(input, 16000, [100, -200]);            // the header says two samples
        AppendRaw(input, TestAudio.Shorts(300, -400, 500));       // the crash came before the next header update
        var output = _folder.File("upload.wav");

        _preparer.Prepare([input], output);

        Assert.That(TestAudio.ReadWav(output).Samples, Is.EqualTo(new short[] { 100, -200, 300, -400, 500 }));
    }

    [Test]
    public void A_sample_cut_in_half_by_a_crash_is_dropped()
    {
        var input = _folder.File("recording-001.wav");
        TestAudio.WriteWav(input, 16000, [100, -200]);
        AppendRaw(input, [0x7F]);
        var output = _folder.File("upload.wav");

        _preparer.Prepare([input], output);

        Assert.That(TestAudio.ReadWav(output).Samples, Is.EqualTo(new short[] { 100, -200 }));
    }

    [Test]
    public void A_segment_that_is_still_being_recorded_can_be_read()
    {
        var input = _folder.File("recording-001.wav");
        using var recorder = new WavSegmentWriter(input, 16000);
        recorder.Write(TestAudio.Shorts(100, -200, 300));
        recorder.Flush();
        var output = _folder.File("upload.wav");

        _preparer.Prepare([input], output);

        Assert.That(TestAudio.ReadWav(output).Samples, Is.EqualTo(new short[] { 100, -200, 300 }));
    }

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

        _preparer.Prepare([first, empty, damaged, headerOnly, last], output);

        Assert.That(TestAudio.ReadWav(output).Samples, Is.EqualTo(new short[] { 100, -200, 300 }));
    }

    [Test]
    public void Files_without_any_sound_are_reported_as_having_no_audio()
    {
        var empty = _folder.File("recording-001.wav");
        var headerOnly = _folder.File("recording-002.wav");
        File.WriteAllBytes(empty, []);
        TestAudio.WriteWav(headerOnly, 48000, []);

        Assert.Throws<NoAudioException>(() => _preparer.Prepare([empty, headerOnly], _folder.File("upload.wav")));
    }

    [Test]
    public void A_file_of_zero_bytes_counts_as_empty_not_as_damaged()
    {
        var empty = _folder.File("recording-001.wav");
        File.WriteAllBytes(empty, []);

        Assert.Throws<NoAudioException>(() => _preparer.Prepare([empty], _folder.File("upload.wav")));
    }

    [Test]
    public void A_failed_preparation_leaves_no_unfinished_upload_file_behind()
    {
        var input = _folder.File("surround.wav");
        using (var writer = new WaveFileWriter(input, new WaveFormat(16000, 16, 6)))
        {
            writer.Write(new byte[12], 0, 12);
        }

        var output = _folder.File("upload.wav");
        Assert.Throws<NotSupportedException>(() => _preparer.Prepare([input], output));

        Assert.That(File.Exists(output), Is.False);
    }

    [Test]
    public void A_file_that_already_sits_where_the_upload_file_should_go_is_not_touched()
    {
        var input = _folder.File("recording-001.wav");
        TestAudio.WriteWav(input, 16000, [100, -200]);
        var output = _folder.File("upload.wav");
        File.WriteAllText(output, "precious");

        Assert.Throws<IOException>(() => _preparer.Prepare([input], output));

        Assert.That(File.ReadAllText(output), Is.EqualTo("precious"));
    }

    [Test]
    public void Files_that_are_no_wav_files_are_reported_as_unreadable()
    {
        var mislabelled = _folder.File("lecture.wav");
        File.WriteAllText(mislabelled, "ID3 and then an MP3 stream, say: a file some other tool may well understand.");

        Assert.Throws<FormatException>(() => _preparer.Prepare([mislabelled], _folder.File("upload.wav")));
    }

    [Test]
    public void A_stereo_file_is_mixed_down_to_mono()
    {
        var input = _folder.File("stereo.wav");
        using (var writer = new WaveFileWriter(input, new WaveFormat(16000, 16, 2)))
        {
            writer.Write(TestAudio.Shorts(16384, 0, -16384, 0), 0, 8);   // left carries the sound, right is silent
        }

        var output = _folder.File("mono.wav");
        _preparer.Prepare([input], output);

        Assert.That(TestAudio.ReadWav(output).Samples, Is.EqualTo(new short[] { 8192, -8192 }));
    }

    [Test]
    public void A_file_with_more_than_two_channels_is_refused()
    {
        var input = _folder.File("surround.wav");
        using (var writer = new WaveFileWriter(input, new WaveFormat(16000, 16, 6)))
        {
            writer.Write(new byte[12], 0, 12);
        }

        Assert.Throws<NotSupportedException>(() => _preparer.Prepare([input], _folder.File("out.wav")));
    }
}
